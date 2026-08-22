using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;

using PeripheralBatteryDashboard.Core;
using PeripheralBatteryDashboard.Diagnostics;
using PeripheralBatteryDashboard.Hardware;
using PeripheralBatteryDashboard.Providers;
using PeripheralBatteryDashboard.UI;

[assembly: AssemblyTitle("Peripheral Battery Dashboard")]
[assembly: AssemblyProduct("Peripheral Battery Dashboard")]
[assembly: AssemblyDescription("Windows 주변기기 배터리 상태 대시보드")]
[assembly: AssemblyCompany("rpr123")]
[assembly: AssemblyCopyright("Copyright © 2026 rpr123")]
[assembly: AssemblyVersion("1.1.4.0")]
[assembly: AssemblyFileVersion("1.1.4.0")]

namespace PeripheralBatteryDashboard
{
    internal static class Program
    {
        private const string SingleInstanceMutexName = "Local\\PeripheralBatteryDashboard.Gui.v1";
        private const string ActivateEventName = "Local\\PeripheralBatteryDashboard.Gui.Activate.v1";

        [STAThread]
        public static int Main(string[] args)
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string mode = args != null && args.Length > 0 ? args[0] : string.Empty;
            bool startupLaunch = args != null && Array.Exists(args,
                value => string.Equals(value, "--startup", StringComparison.OrdinalIgnoreCase));

            try
            {
                if (string.Equals(mode, "--self-test", StringComparison.OrdinalIgnoreCase))
                    return RunSelfTests(baseDirectory);
                if (string.Equals(mode, "--inventory", StringComparison.OrdinalIgnoreCase))
                    return RunInventory(baseDirectory);
                if (string.Equals(mode, "--snapshot", StringComparison.OrdinalIgnoreCase))
                    return RunConsoleDiagnostics(baseDirectory, true);
                if (string.Equals(mode, "--diagnostics", StringComparison.OrdinalIgnoreCase))
                    return RunConsoleDiagnostics(baseDirectory, false);
                if (string.Equals(mode, "--provider-worker", StringComparison.OrdinalIgnoreCase))
                    return RunProviderWorker(baseDirectory, args, false);
                if (string.Equals(mode, "--provider-worker-fixture", StringComparison.OrdinalIgnoreCase))
                    return RunProviderWorker(baseDirectory, args, true);
                if (string.Equals(mode, "--version", StringComparison.OrdinalIgnoreCase))
                    return PrintVersion();
                if (string.Equals(mode, "--help", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mode, "-h", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mode, "/?", StringComparison.OrdinalIgnoreCase))
                    return PrintHelp();
                if (!string.IsNullOrEmpty(mode) && mode.StartsWith("--",
                    StringComparison.Ordinal) &&
                    !string.Equals(mode, "--startup", StringComparison.OrdinalIgnoreCase))
                {
                    PrepareConsoleEncoding();
                    Console.Error.WriteLine("알 수 없는 옵션: " + mode);
                    Console.Error.WriteLine("지원 옵션은 --help로 확인하세요.");
                    return 64;
                }

                bool createdNew;
                using (Mutex mutex = new Mutex(true, SingleInstanceMutexName, out createdNew))
                {
                    if (!createdNew)
                    {
                        if (startupLaunch)
                            return 0;
                        if (SignalRunningInstance())
                            return 0;
                        MessageBox.Show("주변기기 배터리 대시보드가 이미 실행 중입니다.\n트레이 아이콘을 더블 클릭해 주세요.",
                            "이미 실행 중", MessageBoxButton.OK, MessageBoxImage.Information);
                        return 3;
                    }

                    using (EventWaitHandle activateEvent = new EventWaitHandle(false,
                        EventResetMode.AutoReset, ActivateEventName))
                    {
                        return RunGui(baseDirectory, activateEvent, startupLaunch);
                    }
                }
            }
            catch (Exception ex)
            {
                if (IsConsoleMode(mode))
                {
                    PrepareConsoleEncoding();
                    Console.Error.WriteLine("실행 오류: " + ex);
                }
                else if (!startupLaunch)
                {
                    MessageBox.Show("앱을 시작하지 못했습니다.\n\n" + ex.Message,
                        "주변기기 배터리 대시보드", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return 2;
            }
        }

        private static int RunGui(string baseDirectory, EventWaitHandle activateEvent,
            bool startupLaunch)
        {
            ProfileStore profileStore;
            ProviderRegistry registry;
            AppSettingsStore settingsStore;
            AppSettings settings;
            IList<DeviceProfile> profiles;
            BatteryReadContext context;
            DiagnosticsService diagnostics;
            CreateServices(baseDirectory, out profileStore, out registry, out settingsStore,
                out settings, out profiles, out context, out diagnostics);

            string guiExecutablePath = Path.Combine(baseDirectory, "PeripheralBatteryDashboard.exe");
            string startupRegistrationError;
            bool startupRegistrationSucceeded = StartupRegistration.TrySetEnabled(
                settings.StartWithWindows, guiExecutablePath,
                out startupRegistrationError);
            if (startupLaunch && !settings.StartWithWindows)
                return 0;

            BatteryHistoryStore batteryHistory = new BatteryHistoryStore();
            DeviceMonitorService monitor = new DeviceMonitorService(profiles, registry,
                context, settings, new ProviderWorkerClient(baseDirectory), batteryHistory);
            Application application = new Application();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            MainWindow window = new MainWindow(profiles, monitor, profileStore, settings, settingsStore, diagnostics);
            if (!startupRegistrationSucceeded && !startupLaunch)
                window.ReportStartupRegistrationError(startupRegistrationError);
            TrayService tray = null;
            bool exitStarted = false;

            Action exitAction = delegate
            {
                Action exitOnUi = delegate
                {
                    if (exitStarted)
                        return;
                    exitStarted = true;
                    if (tray != null)
                        tray.AllowWindowClose = true;
                    if (window.IsVisible)
                        window.Close();
                    application.Shutdown();
                };

                if (window.Dispatcher.CheckAccess())
                    exitOnUi();
                else if (!window.Dispatcher.HasShutdownStarted)
                    window.Dispatcher.BeginInvoke(exitOnUi);
            };

            tray = new TrayService(window, monitor, settings, exitAction);
            RegisteredWaitHandle activationRegistration = ThreadPool.RegisterWaitForSingleObject(
                activateEvent,
                delegate
                {
                    if (!window.Dispatcher.HasShutdownStarted)
                        window.Dispatcher.BeginInvoke(new Action(window.ShowFromTray));
                },
                null,
                Timeout.Infinite,
                false);
            window.Closed += delegate
            {
                if (!exitStarted)
                {
                    exitStarted = true;
                    application.Shutdown();
                }
            };

            try
            {
                monitor.Start();
                if (!startupLaunch)
                    window.Show();
                return application.Run();
            }
            finally
            {
                activationRegistration.Unregister(null);
                if (tray != null)
                    tray.Dispose();
                monitor.Dispose();
                batteryHistory.Dispose();
            }
        }

        private static bool SignalRunningInstance()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using (EventWaitHandle activateEvent = EventWaitHandle.OpenExisting(ActivateEventName))
                    {
                        return activateEvent.Set();
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }
            return false;
        }

        private static int RunSelfTests(string baseDirectory)
        {
            PrepareConsoleEncoding();
            IList<string> failures = SelfTests.Run(baseDirectory);
            if (failures.Count == 0)
            {
                Console.WriteLine("SELF-TEST OK");
                return 0;
            }

            Console.Error.WriteLine("SELF-TEST FAILED (" + failures.Count + ")");
            foreach (string failure in failures)
                Console.Error.WriteLine("- " + failure);
            return 1;
        }

        private static int PrintVersion()
        {
            PrepareConsoleEncoding();
            Console.WriteLine(typeof(Program).Assembly.GetName().Version.ToString());
            return 0;
        }

        private static int PrintHelp()
        {
            PrepareConsoleEncoding();
            Console.WriteLine("Peripheral Battery Dashboard");
            Console.WriteLine("  --self-test    장치 I/O 없는 자체 테스트");
            Console.WriteLine("  --inventory    메타데이터 전용 HID + 표준 Bluetooth 배터리 서비스 인벤토리(JSON)");
            Console.WriteLine("  --snapshot     실제 공급자 1회 조회(JSON; 장치 I/O 가능)");
            Console.WriteLine("  --diagnostics  실제 공급자 진단(장치 I/O 가능)");
            Console.WriteLine("  --version      앱 버전");
            Console.WriteLine("  --help         이 도움말");
            return 0;
        }

        private static int RunConsoleDiagnostics(string baseDirectory, bool jsonOnly)
        {
            PrepareConsoleEncoding();
            ProfileStore profileStore;
            ProviderRegistry registry;
            AppSettingsStore settingsStore;
            AppSettings settings;
            IList<DeviceProfile> profiles;
            BatteryReadContext context;
            DiagnosticsService diagnostics;
            CreateServices(baseDirectory, out profileStore, out registry, out settingsStore,
                out settings, out profiles, out context, out diagnostics);

            IList<BatteryReading> readings = diagnostics.ReadOnceAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
            Console.WriteLine(jsonOnly ? diagnostics.ToJson(readings) : diagnostics.BuildText(readings));

            foreach (string warning in profileStore.LoadWarnings)
                Console.Error.WriteLine("프로필 경고: " + warning);
            foreach (string warning in registry.PluginWarnings)
                Console.Error.WriteLine("플러그인 경고: " + warning);
            return 0;
        }

        private static int RunInventory(string baseDirectory)
        {
            PrepareConsoleEncoding();
            try
            {
                ProfileStore profileStore = new ProfileStore(baseDirectory);
                IList<DeviceProfile> profiles = profileStore.LoadProfiles();
                InventoryService inventory = new InventoryService(profiles, new HidDeviceEnumerator());
                InventorySnapshot snapshot = inventory.Collect();
                snapshot.ProfileWarningCount = profileStore.LoadWarnings.Count;
                if (snapshot.ProfileWarningCount > 0)
                {
                    snapshot.Complete = false;
                    snapshot.WarningCodes.Add("profile-load-warning");
                }
                Console.WriteLine(inventory.ToJson(snapshot));
                return snapshot.Complete ? 0 : 4;
            }
            catch
            {
                InventorySnapshot failed = new InventorySnapshot
                {
                    GeneratedAtUtc = DateTime.UtcNow.ToString("o"),
                    Complete = false
                };
                failed.WarningCodes.Add("inventory-failed");
                Console.WriteLine(new InventoryService(new List<DeviceProfile>(),
                    new HidDeviceEnumerator()).ToJson(failed));
                return 4;
            }
        }

        private static int RunProviderWorker(string baseDirectory, string[] args,
            bool fixtureMode)
        {
            PrepareConsoleEncoding();
            try
            {
                // CREATE_NO_WINDOW has no console code page to inherit. Decode the
                // redirected request deterministically before Console.In is opened.
                Console.InputEncoding = new UTF8Encoding(false);
            }
            catch
            {
                // TryDeserializeRequest still rejects any undecodable input safely.
            }
            if (args == null || args.Length != 2)
                return 64;
            int parentProcessId;
            if (!int.TryParse(args[1], out parentProcessId) || parentProcessId <= 0)
                return 64;
            StartParentExitWatchdog(parentProcessId);

            string requestText = ReadBoundedConsoleLine(
                ProviderWorkerProtocol.MaximumMessageCharacters + 1);
            ProviderWorkerRequest request;
            if (!ProviderWorkerProtocol.TryDeserializeRequest(requestText, out request))
                return 65;
            if (!ProviderWorkerSafety.IsSafeHidWorkerProfile(request.Profile))
                return 66;
            DeviceProfile profile = request.Profile;

            if (fixtureMode)
            {
                object fixtureOption;
                string fixtureBehavior = profile.ProviderOptions != null &&
                    profile.ProviderOptions.TryGetValue("FixtureBehavior", out fixtureOption)
                    ? Convert.ToString(fixtureOption)
                    : profile.Id;
                if (string.Equals(fixtureBehavior, "hang",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fixtureBehavior, "fixture.hang",
                    StringComparison.OrdinalIgnoreCase))
                {
                    Thread.Sleep(Timeout.Infinite);
                    return 70;
                }
                if (string.Equals(fixtureBehavior, "malformed",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fixtureBehavior, "fixture.malformed",
                    StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("fixture-malformed-output");
                    return 0;
                }
                if (string.Equals(fixtureBehavior, "exit",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fixtureBehavior, "fixture.exit",
                    StringComparison.OrdinalIgnoreCase))
                    return 72;
                if (string.Equals(fixtureBehavior, "flood",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fixtureBehavior, "fixture.flood",
                    StringComparison.OrdinalIgnoreCase))
                {
                    Console.Write(new string('X',
                        ProviderWorkerProtocol.MaximumMessageCharacters + 4096));
                    return 0;
                }
                BatteryReading fixture = new BatteryReading
                {
                    ProfileId = profile.Id,
                    DisplayName = "fixture",
                    Category = "other",
                    Percent = 61,
                    Band = BatteryLevelBand.High,
                    Connection = DeviceConnectionState.Connected,
                    Charge = DeviceChargeState.Discharging,
                    StatusText = "연결됨",
                    DetailText = "worker fixture",
                    SampledAtUtc = DateTime.UtcNow
                };
                Console.WriteLine(ProviderWorkerProtocol.SerializeResponse(
                    request.RequestId, fixture));
                return 0;
            }

            ProviderRegistry registry = new ProviderRegistry();
            BuiltInProviderCatalog.RegisterInto(registry);
            IBatteryProvider provider;
            BatteryReading reading;
            if (!registry.TryGet(profile.ProviderId, out provider))
            {
                registry.LoadPlugins(Path.Combine(baseDirectory, "Plugins"));
                if (!registry.TryGet(profile.ProviderId, out provider))
                {
                    reading = BatteryReading.Unavailable(profile,
                        DeviceConnectionState.Unsupported,
                        "지원 모듈 없음",
                        "등록된 공급자를 찾지 못했습니다.",
                        "provider-not-found");
                }
                else
                {
                    reading = ReadProviderInWorker(profile, provider);
                }
            }
            else
            {
                reading = ReadProviderInWorker(profile, provider);
            }
            Console.WriteLine(ProviderWorkerProtocol.SerializeResponse(
                request.RequestId, reading));
            return 0;
        }

        private static BatteryReading ReadProviderInWorker(DeviceProfile profile,
            IBatteryProvider provider)
        {
            try
            {
                BatteryReading reading = provider.ReadAsync(profile,
                    new BatteryReadContext(new HidDeviceEnumerator()),
                    CancellationToken.None).GetAwaiter().GetResult();
                return reading ?? BatteryReading.Unavailable(profile,
                    DeviceConnectionState.Error,
                    "조회 오류",
                    "공급자가 상태를 반환하지 않았습니다.",
                    "provider-null-reading");
            }
            catch
            {
                return BatteryReading.Unavailable(profile,
                    DeviceConnectionState.Error,
                    "조회 오류",
                    "조회 보조 프로세스에서 공급자 호출이 실패했습니다.",
                    "provider-worker-exception");
            }
        }

        private static string ReadBoundedConsoleLine(int maximumCharacters)
        {
            StringBuilder value = new StringBuilder(Math.Min(maximumCharacters, 4096));
            while (value.Length <= maximumCharacters)
            {
                int next = Console.In.Read();
                if (next < 0 || next == '\n')
                    return value.ToString().TrimEnd('\r');
                value.Append((char)next);
            }
            return null;
        }

        private static void StartParentExitWatchdog(int parentProcessId)
        {
            Thread watchdog = new Thread(new ThreadStart(delegate
            {
                try
                {
                    using (Process parent = Process.GetProcessById(parentProcessId))
                    {
                        parent.WaitForExit();
                    }
                    Environment.Exit(90);
                }
                catch (ArgumentException)
                {
                    Environment.Exit(90);
                }
                catch
                {
                    // The parent still owns the normal timeout/kill path. An access
                    // failure here must not interfere with the battery read itself.
                }
            }));
            watchdog.IsBackground = true;
            watchdog.Name = "provider-parent-watchdog";
            watchdog.Start();
        }

        private static void CreateServices(string baseDirectory,
            out ProfileStore profileStore,
            out ProviderRegistry registry,
            out AppSettingsStore settingsStore,
            out AppSettings settings,
            out IList<DeviceProfile> profiles,
            out BatteryReadContext context,
            out DiagnosticsService diagnostics)
        {
            profileStore = new ProfileStore(baseDirectory);
            profiles = profileStore.LoadProfiles();

            registry = new ProviderRegistry();
            BuiltInProviderCatalog.RegisterInto(registry);
            registry.LoadPlugins(Path.Combine(baseDirectory, "Plugins"));

            settingsStore = new AppSettingsStore();
            settings = settingsStore.Load();
            context = new BatteryReadContext(new HidDeviceEnumerator());
            diagnostics = new DiagnosticsService(profiles, registry, context);
        }

        private static bool IsConsoleMode(string mode)
        {
            return string.Equals(mode, "--self-test", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "--inventory", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "--snapshot", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "--diagnostics", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "--provider-worker", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "--provider-worker-fixture", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "--version", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "--help", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "-h", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "/?", StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrEmpty(mode) && mode.StartsWith("--",
                       StringComparison.Ordinal) &&
                       !string.Equals(mode, "--startup", StringComparison.OrdinalIgnoreCase));
        }

        private static void PrepareConsoleEncoding()
        {
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch
            {
                // A winexe build has no attached console; the diagnostics build does.
            }
        }
    }
}
