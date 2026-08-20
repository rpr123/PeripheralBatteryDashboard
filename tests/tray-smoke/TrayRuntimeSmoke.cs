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

            ForceCleanup();
            beforeTray = ResourceCounts.Capture();

            tray = new TrayService(window, monitor, settings, delegate { });
            AssertPerDeviceState(tray, profiles.Count, "initial per-device mode");

            settings.TrayIconMode = AppSettings.TrayIconModeCombined;
            InvokeApplyTrayMode(tray);
            AssertCombinedState(tray, "first combined switch");

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
            Console.WriteLine("Initial per-device slots: 4 visible");
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

    private static List<DeviceProfile> BuildProfiles()
    {
        string[] names =
        {
            "Arctis Nova 7 Gen 2",
            "AULA F108 Pro",
            "VXE R1 SE+",
            "Xbox Wireless Controller"
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
        Assert(GetPrivateField(tray, "_combinedSlot") == null,
            phase + ": combined slot should be null");
        Assert(string.Equals((string)GetPrivateField(tray, "_activeMode"),
            AppSettings.TrayIconModePerDevice, StringComparison.Ordinal),
            phase + ": active mode is not per-device");
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

    private static object GetPrivateField(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(field != null, "private field was not found: " + name);
        return field.GetValue(target);
    }

    private static void CreateIconQaSheet(Type trayType, string outputPath)
    {
        MethodInfo create = trayType.GetMethod("CreateStatusIcon",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert(create != null, "CreateStatusIcon was not found");

        string[] texts = { "100", "95", "64", "78" };
        string[] shapes = { "headset", "keyboard", "mouse", "gamepad" };
        string[] labels =
        {
            "Headset 100%",
            "Keyboard 95% charge",
            "Mouse 64%",
            "Gamepad 78%"
        };
        Color[] accents =
        {
            Color.FromArgb(255, 55, 206, 194),
            Color.FromArgb(255, 55, 206, 194),
            Color.FromArgb(255, 55, 206, 194),
            Color.FromArgb(255, 55, 206, 194)
        };
        bool[] charging = { false, true, false, false };

        AssertDistinctDeviceShapes(create, shapes);

        const int width = 920;
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
                Icon icon = (Icon)create.Invoke(null,
                    new object[]
                    {
                        texts[index], accents[index], charging[index], shapes[index]
                    });
                try
                {
                    using (Bitmap native = icon.ToBitmap())
                    {
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
