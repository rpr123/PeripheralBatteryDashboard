using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using PeripheralBatteryDashboard.Core;
using PeripheralBatteryDashboard.Diagnostics;
using PeripheralBatteryDashboard.Hardware;
using PeripheralBatteryDashboard.UI;
using Forms = System.Windows.Forms;

internal static class TrayRuntimeSmoke
{
    private static string _runtimePath;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args == null || args.Length != 1)
                throw new InvalidOperationException("Usage: TrayRuntimeSmoke.exe <runtime-dll-path>");

            _runtimePath = Path.GetFullPath(args[0]);
            if (!File.Exists(_runtimePath))
                throw new FileNotFoundException("Runtime assembly not found.", _runtimePath);

            AppDomain.CurrentDomain.AssemblyResolve += ResolveRuntimeAssembly;
            SmokeRunner.Run(_runtimePath);
            Console.WriteLine("RESULT: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("RESULT: FAIL");
            Console.Error.WriteLine(Unwrap(ex));
            return 1;
        }
    }

    private static Assembly ResolveRuntimeAssembly(object sender, ResolveEventArgs args)
    {
        AssemblyName requested = new AssemblyName(args.Name);
        if (string.Equals(requested.Name, "PeripheralBatteryDashboard.Runtime",
            StringComparison.OrdinalIgnoreCase))
            return Assembly.LoadFrom(_runtimePath);
        return null;
    }

    private static Exception Unwrap(Exception exception)
    {
        TargetInvocationException invocation = exception as TargetInvocationException;
        return invocation != null && invocation.InnerException != null
            ? Unwrap(invocation.InnerException)
            : exception;
    }
}

internal static class SmokeRunner
{
    private const int ToggleCycles = 50;
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PeripheralBatteryDashboard";

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Run(string runtimePath)
    {
        Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA,
            "test thread is not STA");

        string testDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string iconSheetPath = Path.Combine(testDirectory, "tray-icons.png");
        byte[] settingsBefore;
        bool settingsExistedBefore;
        string registryBefore;

        List<DeviceProfile> profiles = BuildProfiles();
        AppSettings settings = new AppSettings
        {
            NotificationsEnabled = false,
            MinimizeToTrayOnClose = true,
            StartMinimized = true,
            StartWithWindows = false,
            TrayIconMode = AppSettings.TrayIconModePerDevice
        };
        ProviderRegistry registry = new ProviderRegistry();
        BatteryReadContext context = new BatteryReadContext(new HidDeviceEnumerator());
        DeviceMonitorService monitor = new DeviceMonitorService(profiles, registry, context, settings);
        SeedPresentReadings(monitor);
        ProfileStore profileStore = new ProfileStore(Path.Combine(testDirectory, "empty-profile-root"));
        AppSettingsStore settingsStore = new AppSettingsStore();
        DiagnosticsService diagnostics = new DiagnosticsService(profiles, registry, context);

        settingsExistedBefore = File.Exists(settingsStore.SettingsPath);
        settingsBefore = settingsExistedBefore ? File.ReadAllBytes(settingsStore.SettingsPath) : null;
        registryBefore = ReadRunValue();

        MainWindow window = null;
        TrayService tray = null;
        ResourceCounts beforeTray = default(ResourceCounts);
        ResourceCounts steadyPerDevice = default(ResourceCounts);
        ResourceCounts afterCycles = default(ResourceCounts);
        ResourceCounts afterDispose = default(ResourceCounts);
        try
        {
            window = new MainWindow(profiles, monitor, profileStore, settings,
                settingsStore, diagnostics);
            Assert(!window.IsVisible, "MainWindow became visible during construction");
            Assert(GetPrivateField(monitor, "_loopTask") == null,
                "DeviceMonitorService unexpectedly started");
            foreach (DeviceProfile profile in profiles)
                window.ApplyReading(CreateReading(profile, DevicePresenceState.Present));
            AssertMainWindowCardVisibility(window, true, profiles.Count,
                "present hardware cards");
            foreach (DeviceProfile profile in profiles)
                window.ApplyReading(CreateReading(profile, DevicePresenceState.Absent));
            AssertMainWindowCardVisibility(window, false, profiles.Count,
                "absent hardware cards");
            foreach (DeviceProfile profile in profiles)
                window.ApplyReading(CreateReading(profile, DevicePresenceState.Present));

            ForceCleanup();
            beforeTray = ResourceCounts.Capture();

            tray = new TrayService(window, monitor, settings, delegate { });
            AssertPerDeviceState(tray, profiles.Count, "initial per-device mode");
            AssertNotificationLatchSemantics(tray, settings, profiles[2]);

            BatteryReading stale70 = CreateErrorReading(profiles[2], 70, true);
            PushReading(tray, stale70);
            window.ApplyReading(stale70);
            AssertDeviceStaleState(tray, profiles[2].Id, "70", "Normal",
                "stale error keeps last percent without a red background");
            AssertMainWindowStaleState(window, profiles[2].Id, "70%", false,
                "dashboard stale 70 percent");

            BatteryReading stale8 = CreateErrorReading(profiles[2], 8, true);
            PushReading(tray, stale8);
            window.ApplyReading(stale8);
            AssertDeviceStaleState(tray, profiles[2].Id, "8", "Critical",
                "stale critical keeps battery danger separate from availability");
            AssertMainWindowStaleState(window, profiles[2].Id, "8%", true,
                "dashboard stale 8 percent");

            foreach (DeviceConnectionState connection in new[]
            {
                DeviceConnectionState.Sleeping,
                DeviceConnectionState.Busy,
                DeviceConnectionState.Error
            })
            {
                BatteryReading variant = CreateErrorReading(profiles[2], 70, true);
                variant.Connection = connection;
                PushReading(tray, variant);
                window.ApplyReading(variant);
                AssertDeviceStaleState(tray, profiles[2].Id, "70", "Normal",
                    "stale " + connection + " tray presentation");
                AssertMainWindowStaleState(window, profiles[2].Id, "70%", false,
                    "stale " + connection + " dashboard presentation");
            }

            BatteryReading expired8 = CreateErrorReading(profiles[2], 8, true);
            expired8.LastSuccessfulAtUtc = DateTime.UtcNow.AddHours(-25);
            PushReading(tray, expired8);
            window.ApplyReading(expired8);
            AssertDeviceExpiredState(tray, profiles[2].Id,
                "expired stale value is not shown as the primary tray number");
            AssertMainWindowExpiredState(window, profiles[2].Id,
                "expired stale value is not shown as the primary card number");

            PushReading(tray, CreateErrorReading(profiles[2], null, false));
            AssertDeviceUnavailableState(tray, profiles[2].Id,
                "error without a successful value");
            PushReading(tray, CreateReading(profiles[2], DevicePresenceState.Present));

            foreach (DeviceProfile profile in profiles)
                PushReading(tray, CreateReading(profile, DevicePresenceState.Absent));
            AssertNoPresentDeviceState(tray, profiles.Count,
                "all synthetic hardware absent");
            foreach (DeviceProfile profile in profiles)
                PushReading(tray, CreateReading(profile, DevicePresenceState.Present));
            AssertPerDeviceState(tray, profiles.Count,
                "synthetic hardware restored");

            for (int index = 0; index < profiles.Count; index++)
                PushReading(tray, CreateErrorReading(profiles[index], 70 - index, true));

            settings.TrayIconMode = AppSettings.TrayIconModeCombined;
            InvokeApplyTrayMode(tray);
            AssertCombinedState(tray, "first combined switch");
            AssertCombinedStaleState(tray, "first combined stale switch");

            for (int index = 0; index < profiles.Count; index++)
            {
                BatteryReading expired = CreateErrorReading(profiles[index],
                    70 - index, true);
                expired.LastSuccessfulAtUtc = DateTime.UtcNow.AddHours(-25);
                PushReading(tray, expired);
            }
            AssertCombinedExpiredState(tray,
                "combined expired values preserve detail only");

            foreach (DeviceProfile profile in profiles)
                PushReading(tray, CreateErrorReading(profile, null, false));
            AssertCombinedUnavailableState(tray,
                "combined error without successful values");

            foreach (DeviceProfile profile in profiles)
                PushReading(tray, CreateReading(profile, DevicePresenceState.Present));

            settings.TrayIconMode = AppSettings.TrayIconModePerDevice;
            InvokeApplyTrayMode(tray);
            AssertPerDeviceState(tray, profiles.Count, "first per-device switch-back");

            ForceCleanup();
            steadyPerDevice = ResourceCounts.Capture();

            for (int cycle = 1; cycle <= ToggleCycles; cycle++)
            {
                settings.TrayIconMode = AppSettings.TrayIconModeCombined;
                InvokeApplyTrayMode(tray);
                AssertCombinedState(tray, "cycle " + cycle + " combined");

                settings.TrayIconMode = AppSettings.TrayIconModePerDevice;
                InvokeApplyTrayMode(tray);
                AssertPerDeviceState(tray, profiles.Count,
                    "cycle " + cycle + " per-device");
            }

            ForceCleanup();
            afterCycles = ResourceCounts.Capture();
            Assert(afterCycles.Gdi - steadyPerDevice.Gdi <= 4,
                "GDI handles grew across mode cycles: " +
                (afterCycles.Gdi - steadyPerDevice.Gdi));
            Assert(afterCycles.User - steadyPerDevice.User <= 4,
                "USER handles grew across mode cycles: " +
                (afterCycles.User - steadyPerDevice.User));

            CreateIconQaSheet(typeof(TrayService), iconSheetPath);
            Assert(File.Exists(iconSheetPath) && new FileInfo(iconSheetPath).Length > 0,
                "tray icon QA sheet was not created");

            tray.Dispose();
            AssertZeroSlotState(tray, "after Dispose");
            tray = null;

            RunEmptyProfileTraySmoke(testDirectory);

            ForceCleanup();
            afterDispose = ResourceCounts.Capture();

            Assert(GetPrivateField(monitor, "_loopTask") == null,
                "DeviceMonitorService started during tray test");
            Assert(!window.IsVisible, "MainWindow became visible during tray test");

            AssertSettingsUnchanged(settingsStore.SettingsPath,
                settingsExistedBefore, settingsBefore);
            Assert(string.Equals(registryBefore, ReadRunValue(), StringComparison.Ordinal),
                "HKCU Run value changed during tray test");

            Console.WriteLine("Runtime: " + runtimePath);
            Console.WriteLine("Synthetic profiles: " + profiles.Count);
            Console.WriteLine("Mode switches: 1 initial round-trip + " +
                ToggleCycles + " repeated round-trips");
            Console.WriteLine("Per-device slots: 4 visible when present, 0 when absent");
            Console.WriteLine("No-device fallback slots: 1 visible");
            Console.WriteLine("Combined slots: 1 visible");
            Console.WriteLine("Disposed slots: 0");
            Console.WriteLine("Monitor started: false");
            Console.WriteLine("Settings file changed: false");
            Console.WriteLine("HKCU Run value changed: false");
            Console.WriteLine("Handles before TrayService: " + beforeTray);
            Console.WriteLine("Handles steady per-device: " + steadyPerDevice);
            Console.WriteLine("Handles after cycles: " + afterCycles +
                " (delta steady: " + afterCycles.DeltaFrom(steadyPerDevice) + ")");
            Console.WriteLine("Handles after Dispose: " + afterDispose +
                " (delta pre-tray: " + afterDispose.DeltaFrom(beforeTray) + ")");
            Console.WriteLine("Icon QA sheet: " + iconSheetPath);
        }
        finally
        {
            if (tray != null)
                tray.Dispose();
            if (window != null)
            {
                try { window.Close(); }
                catch { }
            }
            monitor.Dispose();
        }
    }

    private static void RunEmptyProfileTraySmoke(string testDirectory)
    {
        List<DeviceProfile> profiles = new List<DeviceProfile>();
        AppSettings settings = new AppSettings
        {
            NotificationsEnabled = false,
            StartWithWindows = false,
            TrayIconMode = AppSettings.TrayIconModePerDevice
        };
        ProviderRegistry registry = new ProviderRegistry();
        BatteryReadContext context = new BatteryReadContext(new HidDeviceEnumerator());
        DeviceMonitorService monitor = new DeviceMonitorService(profiles, registry,
            context, settings);
        MainWindow window = null;
        TrayService tray = null;
        try
        {
            ProfileStore profileStore = new ProfileStore(
                Path.Combine(testDirectory, "empty-profile-root"));
            window = new MainWindow(profiles, monitor, profileStore, settings,
                new AppSettingsStore(), new DiagnosticsService(profiles, registry, context));
            tray = new TrayService(window, monitor, settings, delegate { });
            AssertNoPresentDeviceState(tray, 0, "empty public profile set");
            Assert(GetPrivateField(monitor, "_loopTask") == null,
                "empty-profile monitor unexpectedly started");
        }
        finally
        {
            if (tray != null)
                tray.Dispose();
            if (window != null)
            {
                try { window.Close(); }
                catch { }
            }
            monitor.Dispose();
        }
    }

    private static List<DeviceProfile> BuildProfiles()
    {
        string[] names =
        {
            "Fixture Headset",
            "Fixture Keyboard",
            "Fixture Mouse",
            "Fixture Gamepad"
        };
        string[] categories = { "headset", "keyboard", "mouse", "gamepad" };
        List<DeviceProfile> profiles = new List<DeviceProfile>();
        for (int index = 0; index < names.Length; index++)
        {
            profiles.Add(new DeviceProfile
            {
                Id = "smoke-device-" + (index + 1),
                DisplayName = names[index],
                Category = categories[index],
                ProviderId = "smoke-provider-never-read",
                Enabled = true,
                DisplayOrder = index + 1,
                LowBatteryPercent = 20
            });
        }
        return profiles;
    }

    private static void SeedPresentReadings(DeviceMonitorService monitor)
    {
        IEnumerable runtimes = GetPrivateField(monitor, "_devices") as IEnumerable;
        Assert(runtimes != null, "monitor runtimes are unavailable");
        foreach (object runtime in runtimes)
        {
            DeviceProfile profile = (DeviceProfile)GetFieldValue(runtime, "Profile");
            SetFieldValue(runtime, "Presence", DevicePresenceState.Present);
            SetFieldValue(runtime, "LastReading",
                CreateReading(profile, DevicePresenceState.Present));
        }
    }

    private static BatteryReading CreateReading(DeviceProfile profile,
        DevicePresenceState presence)
    {
        DateTime nowUtc = DateTime.UtcNow;
        return new BatteryReading
        {
            ProfileId = profile.Id,
            DisplayName = profile.DisplayName,
            Category = profile.Category,
            Presence = presence,
            Connection = presence == DevicePresenceState.Present
                ? DeviceConnectionState.Connected
                : DeviceConnectionState.Disconnected,
            Percent = presence == DevicePresenceState.Present ? (int?)78 : null,
            Band = presence == DevicePresenceState.Present
                ? BatteryLevelBand.High
                : BatteryLevelBand.Unknown,
            StatusText = presence == DevicePresenceState.Present ? "연결됨" : "현재 장치 없음",
            SampledAtUtc = nowUtc,
            LastAttemptAtUtc = nowUtc,
            LastSuccessfulAtUtc = presence == DevicePresenceState.Present
                ? (DateTime?)nowUtc
                : null
        };
    }

    private static BatteryReading CreateErrorReading(DeviceProfile profile, int? percent,
        bool stale)
    {
        BatteryReading reading = CreateReading(profile, DevicePresenceState.Present);
        reading.Connection = DeviceConnectionState.Error;
        reading.Percent = percent;
        reading.Band = BatteryReading.BandFromPercent(percent);
        reading.IsStale = stale;
        reading.StatusText = "조회 오류";
        reading.DetailText = "synthetic error";
        reading.LastAttemptAtUtc = DateTime.UtcNow;
        reading.LastSuccessfulAtUtc = stale && percent.HasValue
            ? (DateTime?)DateTime.UtcNow.AddMinutes(-5)
            : null;
        return reading;
    }

    private static void PushReading(TrayService tray, BatteryReading reading)
    {
        MethodInfo method = typeof(TrayService).GetMethod("MonitorOnReadingUpdated",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(method != null, "TrayService reading handler was not found");
        method.Invoke(tray, new object[] { null, new BatteryReadingEventArgs(reading) });
        Forms.Application.DoEvents();
    }

    private static void AssertMainWindowCardVisibility(MainWindow window,
        bool expectedVisible, int expectedCount, string phase)
    {
        IDictionary cards = GetPrivateField(window, "_cards") as IDictionary;
        Assert(cards != null && cards.Count == expectedCount,
            phase + ": unexpected card count");
        foreach (DictionaryEntry entry in cards)
        {
            PropertyInfo rootProperty = entry.Value.GetType().GetProperty("Root",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert(rootProperty != null, phase + ": card Root was not found");
            System.Windows.UIElement root = rootProperty.GetValue(entry.Value, null)
                as System.Windows.UIElement;
            Assert(root != null, phase + ": card Root is unavailable");
            bool isVisible = root.Visibility == System.Windows.Visibility.Visible;
            Assert(isVisible == expectedVisible,
                phase + ": unexpected visibility for " + entry.Key);
        }
    }

    private static void InvokeApplyTrayMode(TrayService tray)
    {
        MethodInfo method = typeof(TrayService).GetMethod("ApplyTrayMode",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(method != null, "ApplyTrayMode was not found");
        method.Invoke(tray, null);
        Forms.Application.DoEvents();
    }

    private static void AssertPerDeviceState(TrayService tray, int expectedCount,
        string phase)
    {
        IDictionary slots = GetDeviceSlots(tray);
        Assert(slots.Count == expectedCount,
            phase + ": expected " + expectedCount + " device slots, found " + slots.Count);
        foreach (DictionaryEntry entry in slots)
        {
            Forms.NotifyIcon icon = GetSlotNotifyIcon(entry.Value);
            Assert(icon != null, phase + ": slot has no NotifyIcon");
            Assert(icon.Visible, phase + ": NotifyIcon is not visible for " + entry.Key);
        }
        object fallbackSlot = GetPrivateField(tray, "_combinedSlot");
        Assert(fallbackSlot != null,
            phase + ": no-device fallback slot is missing");
        Assert(!GetSlotNotifyIcon(fallbackSlot).Visible,
            phase + ": no-device fallback should be hidden");
        Assert(string.Equals((string)GetPrivateField(tray, "_activeMode"),
            AppSettings.TrayIconModePerDevice, StringComparison.Ordinal),
            phase + ": active mode is not per-device");
    }

    private static void AssertNoPresentDeviceState(TrayService tray, int expectedCount,
        string phase)
    {
        IDictionary slots = GetDeviceSlots(tray);
        Assert(slots.Count == expectedCount,
            phase + ": expected " + expectedCount + " retained slots, found " + slots.Count);
        foreach (DictionaryEntry entry in slots)
            Assert(!GetSlotNotifyIcon(entry.Value).Visible,
                phase + ": absent device icon is visible for " + entry.Key);

        object fallbackSlot = GetPrivateField(tray, "_combinedSlot");
        Assert(fallbackSlot != null && GetSlotNotifyIcon(fallbackSlot).Visible,
            phase + ": generic app fallback icon is not visible");
    }

    private static void AssertCombinedState(TrayService tray, string phase)
    {
        IDictionary slots = GetDeviceSlots(tray);
        Assert(slots.Count == 0, phase + ": device slots were not cleared");
        object combinedSlot = GetPrivateField(tray, "_combinedSlot");
        Assert(combinedSlot != null, phase + ": combined slot is null");
        Forms.NotifyIcon icon = GetSlotNotifyIcon(combinedSlot);
        Assert(icon != null && icon.Visible, phase + ": combined NotifyIcon is not visible");
        Assert(string.Equals((string)GetPrivateField(tray, "_activeMode"),
            AppSettings.TrayIconModeCombined, StringComparison.Ordinal),
            phase + ": active mode is not combined");
    }

    private static void AssertDeviceStaleState(TrayService tray, string profileId,
        string expectedText, string expectedSeverity, string phase)
    {
        IDictionary slots = GetDeviceSlots(tray);
        Assert(slots.Contains(profileId), phase + ": device slot was not found");
        AssertAttentionSlot(slots[profileId], expectedText,
            "resolved|RecentStale|" + expectedSeverity + "|", phase);
        int expectedAccent = string.Equals(expectedSeverity, "Critical",
            StringComparison.Ordinal)
            ? Color.FromArgb(255, 153, 67, 88).ToArgb()
            : Color.FromArgb(255, 39, 131, 132).ToArgb();
        AssertDeviceAccent(slots[profileId], expectedAccent, phase);
        Assert(GetSlotNotifyIcon(slots[profileId]).Text.Contains("마지막 확인"),
            phase + ": tooltip does not expose stale age");
    }

    private static void AssertDeviceExpiredState(TrayService tray, string profileId,
        string phase)
    {
        IDictionary slots = GetDeviceSlots(tray);
        Assert(slots.Contains(profileId), phase + ": device slot was not found");
        AssertAttentionSlot(slots[profileId], "—",
            "resolved|ExpiredStale|Critical|", phase);
        AssertDeviceAccent(slots[profileId],
            Color.FromArgb(255, 120, 137, 160).ToArgb(), phase);
        string tooltip = GetSlotNotifyIcon(slots[profileId]).Text;
        Assert(tooltip.Contains("마지막 값 만료") && tooltip.Contains("8%"),
            phase + ": expired tooltip does not preserve the last value");
    }

    private static void AssertDeviceUnavailableState(TrayService tray, string profileId,
        string phase)
    {
        IDictionary slots = GetDeviceSlots(tray);
        Assert(slots.Contains(profileId), phase + ": device slot was not found");
        AssertAttentionSlot(slots[profileId], "—", "resolved|None|Unknown|", phase);
        AssertDeviceAccent(slots[profileId],
            Color.FromArgb(255, 120, 137, 160).ToArgb(), phase);
        Assert(!GetSlotNotifyIcon(slots[profileId]).Text.Contains("마지막"),
            phase + ": no-history tooltip incorrectly implies a last value");
    }

    private static void AssertCombinedStaleState(TrayService tray, string phase)
    {
        object slot = GetPrivateField(tray, "_combinedSlot");
        Assert(slot != null, phase + ": combined slot was not found");
        AssertAttentionSlot(slot, "67", "combined|67|", phase);
        string tooltip = GetSlotNotifyIcon(slot).Text;
        Assert(tooltip.Contains("마지막 67%") && tooltip.Contains("상태 주의 4"),
            phase + ": combined tooltip does not describe the stale representative");
    }

    private static void AssertCombinedUnavailableState(TrayService tray, string phase)
    {
        object slot = GetPrivateField(tray, "_combinedSlot");
        Assert(slot != null, phase + ": combined slot was not found");
        AssertAttentionSlot(slot, "—", "combined|—|", phase);
        string tooltip = GetSlotNotifyIcon(slot).Text;
        Assert(tooltip.Contains("상태 확인 필요"),
            phase + ": combined tooltip does not describe unavailable devices");
        Assert(!tooltip.Contains("응답 대기"),
            phase + ": combined unavailable state was mislabeled as pending");
    }

    private static void AssertCombinedExpiredState(TrayService tray, string phase)
    {
        object slot = GetPrivateField(tray, "_combinedSlot");
        Assert(slot != null, phase + ": combined slot was not found");
        AssertAttentionSlot(slot, "—", "combined|—|", phase);
        string tooltip = GetSlotNotifyIcon(slot).Text;
        Assert(tooltip.Contains("마지막 67%") && tooltip.Contains("성공 "),
            phase + ": combined expired detail lost value or success time: " + tooltip);
    }

    private static void AssertAttentionSlot(object slot, string expectedText,
        string expectedRenderPrefix, string phase)
    {
        Forms.NotifyIcon notifyIcon = GetSlotNotifyIcon(slot);
        Assert(notifyIcon != null && notifyIcon.Visible,
            phase + ": attention NotifyIcon is not visible");
        string renderKey = Convert.ToString(GetSlotProperty(slot, "RenderKey"));
        bool textMatches = renderKey.StartsWith("combined|", StringComparison.Ordinal)
            ? renderKey.StartsWith("combined|" + expectedText + "|",
                StringComparison.Ordinal)
            : renderKey.Contains("|" + expectedText + "|badge:");
        Assert(renderKey.StartsWith(expectedRenderPrefix, StringComparison.Ordinal) &&
               textMatches &&
               renderKey.Contains("attention:True"),
            phase + ": unexpected attention render key: " + renderKey);
        int backgroundArgb = Color.FromArgb(255, 17, 27, 46).ToArgb();
        string expectedBackground = renderKey.StartsWith("combined|",
            StringComparison.Ordinal)
            ? "|" + backgroundArgb + "|"
            : "background:" + backgroundArgb;
        Assert(renderKey.Contains(expectedBackground),
            phase + ": attention state changed the tray background");
        Icon icon = GetSlotProperty(slot, "CurrentIcon") as Icon;
        Assert(icon != null, phase + ": current attention icon is missing");
        using (Bitmap bitmap = icon.ToBitmap())
        {
            AssertDarkDeviceBackground(bitmap);
            AssertAttentionBadge(bitmap);
        }
    }

    private static void AssertDeviceAccent(object slot, int expectedArgb, string phase)
    {
        string renderKey = Convert.ToString(GetSlotProperty(slot, "RenderKey"));
        Assert(renderKey.Contains("|badge:True|" + expectedArgb + "|background:"),
            phase + ": unexpected device accent in render key: " + renderKey);
    }

    private static void AssertMainWindowStaleState(MainWindow window, string profileId,
        string expectedValue, bool critical, string phase)
    {
        object card = GetMainWindowCard(window, profileId, phase);
        System.Windows.Controls.TextBlock value =
            (System.Windows.Controls.TextBlock)GetPrivateField(card, "_valueText");
        System.Windows.Controls.TextBlock status =
            (System.Windows.Controls.TextBlock)GetPrivateField(card, "_statusText");
        System.Windows.Controls.TextBlock sample =
            (System.Windows.Controls.TextBlock)GetPrivateField(card, "_sampleText");
        System.Windows.Shapes.Ellipse dot =
            (System.Windows.Shapes.Ellipse)GetPrivateField(card, "_stateDot");
        System.Windows.Controls.Border bar =
            (System.Windows.Controls.Border)GetPrivateField(card, "_barFill");

        Assert(string.Equals(value.Text, expectedValue, StringComparison.Ordinal),
            phase + ": unexpected primary value " + value.Text);
        Assert(value.Opacity < 0.7 && bar.Opacity < 0.7,
            phase + ": stale battery value/bar is not dimmed");
        Assert(string.Equals(status.Text, "최근 응답 없음", StringComparison.Ordinal) ||
               string.Equals(status.Text, "장치에 접근할 수 없음", StringComparison.Ordinal),
            phase + ": availability text is not factual: " + status.Text);
        Assert(sample.Text.StartsWith("마지막 확인 ", StringComparison.Ordinal),
            phase + ": last-success age is not shown");
        AssertBrushColor(dot.Fill, 245, 183, 66,
            phase + ": availability indicator is not amber");
        if (critical)
        {
            AssertBrushColor(value.Foreground, 251, 96, 119,
                phase + ": critical last value lost its danger color");
            AssertBrushColor(bar.Background, 251, 96, 119,
                phase + ": critical bar does not share battery severity");
        }
        else
        {
            AssertBrushColor(value.Foreground, 64, 210, 141,
                phase + ": normal last value was colored as an error");
            AssertBrushColor(bar.Background, 64, 210, 141,
                phase + ": normal bar was colored as an error");
        }
    }

    private static void AssertNotificationLatchSemantics(TrayService tray,
        AppSettings settings, DeviceProfile profile)
    {
        HashSet<string> latched = GetPrivateField(tray,
            "_lowBatteryNotifications") as HashSet<string>;
        Assert(latched != null, "notification latch set is unavailable");
        bool previousSetting = settings.NotificationsEnabled;
        settings.NotificationsEnabled = true;
        try
        {
            latched.Remove(profile.Id);
            PushReading(tray, CreateErrorReading(profile, 8, true));
            Assert(!latched.Contains(profile.Id),
                "a stale critical value created a low-battery notification latch");

            latched.Add(profile.Id);
            PushReading(tray, CreateErrorReading(profile, 70, true));
            Assert(latched.Contains(profile.Id),
                "a stale value was treated as notification recovery");

            BatteryReading recovered = CreateReading(profile,
                DevicePresenceState.Present);
            recovered.Percent = 26;
            recovered.Band = BatteryReading.BandFromPercent(26);
            PushReading(tray, recovered);
            Assert(!latched.Contains(profile.Id),
                "a fresh recovered value did not clear the notification latch");
        }
        finally
        {
            settings.NotificationsEnabled = previousSetting;
            latched.Remove(profile.Id);
        }
    }

    private static void AssertMainWindowExpiredState(MainWindow window,
        string profileId, string phase)
    {
        object card = GetMainWindowCard(window, profileId, phase);
        System.Windows.Controls.TextBlock value =
            (System.Windows.Controls.TextBlock)GetPrivateField(card, "_valueText");
        System.Windows.Controls.TextBlock detail =
            (System.Windows.Controls.TextBlock)GetPrivateField(card, "_detailText");
        System.Windows.Controls.TextBlock sample =
            (System.Windows.Controls.TextBlock)GetPrivateField(card, "_sampleText");
        Assert(string.Equals(value.Text, "—", StringComparison.Ordinal),
            phase + ": expired value is still primary");
        Assert(detail.Text.Contains("마지막 값 8%") && detail.Text.Contains("성공 시각"),
            phase + ": expired detail did not preserve value and success time");
        Assert(sample.Text.Contains("24시간 초과"),
            phase + ": expired cutoff is not explained");
    }

    private static object GetMainWindowCard(MainWindow window, string profileId,
        string phase)
    {
        IDictionary cards = GetPrivateField(window, "_cards") as IDictionary;
        Assert(cards != null && cards.Contains(profileId),
            phase + ": dashboard card was not found");
        return cards[profileId];
    }

    private static void AssertBrushColor(System.Windows.Media.Brush brush,
        byte red, byte green, byte blue, string message)
    {
        System.Windows.Media.SolidColorBrush solid =
            brush as System.Windows.Media.SolidColorBrush;
        Assert(solid != null && solid.Color.R == red && solid.Color.G == green &&
            solid.Color.B == blue, message);
    }

    private static void AssertZeroSlotState(TrayService tray, string phase)
    {
        Assert(GetDeviceSlots(tray).Count == 0, phase + ": device slots remain");
        Assert(GetPrivateField(tray, "_combinedSlot") == null,
            phase + ": combined slot remains");
    }

    private static IDictionary GetDeviceSlots(TrayService tray)
    {
        IDictionary result = GetPrivateField(tray, "_deviceSlots") as IDictionary;
        Assert(result != null, "_deviceSlots is unavailable");
        return result;
    }

    private static Forms.NotifyIcon GetSlotNotifyIcon(object slot)
    {
        PropertyInfo property = slot.GetType().GetProperty("NotifyIcon",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert(property != null, "TrayIconSlot.NotifyIcon was not found");
        return property.GetValue(slot, null) as Forms.NotifyIcon;
    }

    private static object GetSlotProperty(object slot, string name)
    {
        PropertyInfo property = slot.GetType().GetProperty(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert(property != null, "TrayIconSlot property was not found: " + name);
        return property.GetValue(slot, null);
    }

    private static object GetPrivateField(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert(field != null, "private field was not found: " + name);
        return field.GetValue(target);
    }

    private static object GetFieldValue(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert(field != null, "field was not found: " + name);
        return field.GetValue(target);
    }

    private static void SetFieldValue(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert(field != null, "field was not found: " + name);
        field.SetValue(target, value);
    }

    private static void CreateIconQaSheet(Type trayType, string outputPath)
    {
        MethodInfo create = trayType.GetMethod("CreateStatusIcon",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo createWithAttention = trayType.GetMethod("CreateStatusIconWithAttention",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert(create != null, "CreateStatusIcon was not found");
        Assert(createWithAttention != null, "CreateStatusIconWithAttention was not found");

        string[] texts = { "100", "95", "64", "78", "8" };
        string[] shapes = { "headset", "keyboard", "mouse", "gamepad", "mouse" };
        string[] labels =
        {
            "Headset 100%",
            "Keyboard 95% charge",
            "Mouse 64%",
            "Gamepad 78%",
            "Mouse stale 8% · warning"
        };
        Color[] accents =
        {
            Color.FromArgb(255, 55, 206, 194),
            Color.FromArgb(255, 55, 206, 194),
            Color.FromArgb(255, 55, 206, 194),
            Color.FromArgb(255, 55, 206, 194),
            Color.FromArgb(255, 153, 67, 88)
        };
        bool[] charging = { false, true, false, false, false };
        bool[] attention = { false, false, false, false, true };

        AssertDistinctDeviceShapes(create,
            new[] { "headset", "keyboard", "mouse", "gamepad" });

        const int width = 1145;
        const int height = 280;
        using (Bitmap sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(sheet))
        using (Font titleFont = new Font("Segoe UI", 15.0f, FontStyle.Bold))
        using (Font labelFont = new Font("Segoe UI", 11.0f, FontStyle.Regular))
        using (SolidBrush background = new SolidBrush(Color.FromArgb(255, 9, 16, 29)))
        using (SolidBrush foreground = new SolidBrush(Color.FromArgb(255, 238, 244, 255)))
        using (Pen frame = new Pen(Color.FromArgb(255, 47, 65, 91), 1.0f))
        {
            graphics.FillRectangle(background, 0, 0, width, height);
            graphics.DrawString("Device-shaped tray icon QA (5x preview + native 32px)",
                titleFont, foreground, 20, 14);

            for (int index = 0; index < texts.Length; index++)
            {
                Icon icon = (Icon)createWithAttention.Invoke(null,
                    new object[]
                    {
                        texts[index], accents[index], charging[index], shapes[index],
                        attention[index]
                    });
                try
                {
                    using (Bitmap native = icon.ToBitmap())
                    {
                        AssertDarkDeviceBackground(native);
                        if (attention[index])
                            AssertAttentionBadge(native);
                        int left = 20 + index * 225;
                        graphics.DrawRectangle(frame, left, 54, 205, 205);
                        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                        graphics.PixelOffsetMode = PixelOffsetMode.Half;
                        graphics.DrawImage(native,
                            new Rectangle(left + 22, 67, 160, 160),
                            0, 0, 32, 32, GraphicsUnit.Pixel);
                        graphics.DrawImage(native,
                            new Rectangle(left + 16, 220, 32, 32));
                        graphics.DrawString(labels[index], labelFont, foreground,
                            left + 58, 227);
                    }
                }
                finally
                {
                    icon.Dispose();
                }
            }
            sheet.Save(outputPath, ImageFormat.Png);
        }
    }

    private static void AssertDarkDeviceBackground(Bitmap bitmap)
    {
        int darkPixels = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.A > 200 && Math.Abs(pixel.R - 17) <= 3 &&
                    Math.Abs(pixel.G - 27) <= 3 && Math.Abs(pixel.B - 46) <= 3)
                    darkPixels++;
            }
        }
        Assert(darkPixels >= 40,
            "tray icon does not retain the neutral dark device background");
    }

    private static void AssertAttentionBadge(Bitmap bitmap)
    {
        int amberPixels = 0;
        for (int y = 0; y <= Math.Min(11, bitmap.Height - 1); y++)
        {
            for (int x = Math.Min(21, bitmap.Width - 1); x < bitmap.Width; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.A > 180 && pixel.R > 190 && pixel.G > 125 &&
                    pixel.B < 110)
                    amberPixels++;
            }
        }
        Assert(amberPixels >= 8,
            "tray icon does not contain a visible amber attention badge");
    }

    private static void AssertDistinctDeviceShapes(MethodInfo create, string[] shapes)
    {
        List<byte[]> renders = new List<byte[]>();
        Color accent = Color.FromArgb(255, 55, 206, 194);
        for (int index = 0; index < shapes.Length; index++)
        {
            Icon icon = (Icon)create.Invoke(null,
                new object[] { "88", accent, false, shapes[index] });
            try
            {
                using (Bitmap bitmap = icon.ToBitmap())
                {
                    byte[] pixels = new byte[bitmap.Width * bitmap.Height * 4];
                    int offset = 0;
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        for (int x = 0; x < bitmap.Width; x++)
                        {
                            Color pixel = bitmap.GetPixel(x, y);
                            pixels[offset++] = pixel.A;
                            pixels[offset++] = pixel.R;
                            pixels[offset++] = pixel.G;
                            pixels[offset++] = pixel.B;
                        }
                    }
                    renders.Add(pixels);
                }
            }
            finally
            {
                icon.Dispose();
            }
        }

        for (int left = 0; left < renders.Count; left++)
        {
            for (int right = left + 1; right < renders.Count; right++)
            {
                Assert(!ByteArraysEqual(renders[left], renders[right]),
                    "device shapes rendered identically: " + shapes[left] +
                    " and " + shapes[right]);
            }
        }
    }

    private static string ReadRunValue()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
        {
            object value = key == null ? null : key.GetValue(RunValueName, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value == null ? null : Convert.ToString(value);
        }
    }

    private static void AssertSettingsUnchanged(string path, bool existedBefore,
        byte[] before)
    {
        bool existsAfter = File.Exists(path);
        Assert(existedBefore == existsAfter, "settings file existence changed");
        if (!existedBefore)
            return;
        byte[] after = File.ReadAllBytes(path);
        Assert(ByteArraysEqual(before, after), "settings file content changed");
    }

    private static bool ByteArraysEqual(byte[] left, byte[] right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left == null || right == null || left.Length != right.Length)
            return false;
        for (int index = 0; index < left.Length; index++)
        {
            if (left[index] != right[index])
                return false;
        }
        return true;
    }

    private static void ForceCleanup()
    {
        Forms.Application.DoEvents();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Forms.Application.DoEvents();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private struct ResourceCounts
    {
        public int Gdi;
        public int User;

        public static ResourceCounts Capture()
        {
            using (Process process = Process.GetCurrentProcess())
            {
                return new ResourceCounts
                {
                    Gdi = GetGuiResources(process.Handle, 0),
                    User = GetGuiResources(process.Handle, 1)
                };
            }
        }

        public string DeltaFrom(ResourceCounts baseline)
        {
            return "GDI " + Signed(Gdi - baseline.Gdi) +
                   ", USER " + Signed(User - baseline.User);
        }

        public override string ToString()
        {
            return "GDI " + Gdi + ", USER " + User;
        }

        private static string Signed(int value)
        {
            return value >= 0 ? "+" + value : value.ToString();
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr process, int flags);
}
