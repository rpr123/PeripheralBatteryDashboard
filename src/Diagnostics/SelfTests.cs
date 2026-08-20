using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using PeripheralBatteryDashboard.Core;
using PeripheralBatteryDashboard.Hardware;
using PeripheralBatteryDashboard.Providers;
using PeripheralBatteryDashboard.UI;

namespace PeripheralBatteryDashboard.Diagnostics
{
    public static class SelfTests
    {
        public static IList<string> Run(string baseDirectory)
        {
            List<string> failures = new List<string>();
            Check(failures, HexValue.TryParseUInt16("0x227E") == 0x227E, "hex parser");
            string startupTestPath = @"C:\Program Files\배터리 & 도구\PeripheralBatteryDashboard.exe";
            Check(failures, StartupRegistration.BuildCommandLine(startupTestPath) ==
                "\"" + startupTestPath + "\" --startup", "startup command quoting");
            AppSettings defaultSettings = new AppSettings();
            Check(failures, defaultSettings.TrayIconMode == AppSettings.TrayIconModePerDevice,
                "default tray mode");
            AppSettings migratedSettings = new JavaScriptSerializer()
                .Deserialize<AppSettings>("{\"PollSeconds\":30}");
            Check(failures, migratedSettings != null && migratedSettings.StartWithWindows &&
                migratedSettings.TrayIconMode == AppSettings.TrayIconModePerDevice,
                "settings migration defaults");
            Check(failures, AppSettings.NormalizeTrayIconMode("COMBINED") ==
                AppSettings.TrayIconModeCombined, "combined tray mode normalization");
            Check(failures, AppSettings.NormalizeTrayIconMode("invalid") ==
                AppSettings.TrayIconModePerDevice, "invalid tray mode fallback");
            Check(failures, AppSettings.NormalizeTrayIconMode(null) ==
                AppSettings.TrayIconModePerDevice, "missing tray mode fallback");
            CheckTrayIconPureHelpers(failures);

            byte[] aula = new byte[32];
            aula[0] = 0x20;
            aula[1] = 0x01;
            aula[3] = 100;
            int aulaSum = 0;
            for (int i = 0; i < 31; i++) aulaSum = (aulaSum + aula[i]) & 0xFF;
            aula[31] = (byte)aulaSum;
            Check(failures, ProviderSupport.AulaChecksumIsValid(aula), "AULA checksum");

            byte[] vxe = new byte[17];
            vxe[0] = 0x08;
            vxe[1] = 0x04;
            vxe[6] = 95;
            int partial = 0;
            for (int i = 0; i < 16; i++) partial = (partial + vxe[i]) & 0xFF;
            vxe[16] = (byte)((0x55 - partial) & 0xFF);
            Check(failures, ProviderSupport.SumChecksumEquals(vxe, 17, 0x55), "VXE checksum");
            Check(failures, ProviderSupport.IsValidBatteryPercent(0), "battery percent lower bound");
            Check(failures, ProviderSupport.IsValidBatteryPercent(100), "battery percent upper bound");
            Check(failures, !ProviderSupport.IsValidBatteryPercent(101), "battery percent rejects overflow");

            Check(failures, BluetoothGattBatteryReader.InteropLayoutIsValid,
                "Bluetooth GATT interop layout");
            Check(failures, BluetoothGattBatteryReader.PathMatchesHardware(
                @"\\?\BTHLEDevice#{0000180f-0000-1000-8000-00805f9b34fb}_Dev_VID&02045E_PID&0B13",
                0x045E, new List<ushort> { 0x0B13 }), "Xbox GATT path match");
            Check(failures, BluetoothGattBatteryReader.PathMatchesHardware(
                @"\\?\BTHLEDevice#{0000180f-0000-1000-8000-00805f9b34fb}_Dev_VID&01045E_PID&0B13",
                0x045E, new List<ushort> { 0x0B13 }), "Bluetooth VID source path match");
            Check(failures, !BluetoothGattBatteryReader.PathMatchesHardware(
                @"\\?\BTHLEDevice#{0000180f-0000-1000-8000-00805f9b34fb}_Dev_VID&02045E_PID&FFFF",
                0x045E, new List<ushort> { 0x0B13 }), "Xbox GATT path rejects other PID");

            ProfileStore store = new ProfileStore(baseDirectory);
            IList<DeviceProfile> profiles = store.LoadProfiles();
            Check(failures, profiles.Count >= 4, "profile count");
            Check(failures, profiles.Any(p => p.Id == "steelseries.arctis-nova-7-gen2"), "Nova profile");
            Check(failures, profiles.Any(p => p.Id == "aula.f108-pro"), "AULA profile");
            Check(failures, profiles.Any(p => p.Id == "vxe.r1-se-plus"), "VXE profile");
            DeviceProfile xbox = profiles.FirstOrDefault(p => p.Id == "microsoft.xbox-wireless-controller");
            Check(failures, xbox != null && xbox.Match.ParsedVendorId == 0x045E &&
                xbox.Match.ParsedProductIds.Contains(0x0B13), "Xbox Bluetooth hardware profile");

            ProviderRegistry registry = new ProviderRegistry();
            registry.Register(new SteelSeriesNova7Provider());
            bool duplicateRejected = false;
            try { registry.Register(new SteelSeriesNova7Provider()); }
            catch (InvalidOperationException) { duplicateRejected = true; }
            Check(failures, duplicateRejected, "duplicate provider rejection");

            return failures;
        }

        private static void CheckTrayIconPureHelpers(List<string> failures)
        {
            try
            {
                Type trayServiceType = typeof(TrayService);
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
                MethodInfo createVisual = trayServiceType.GetMethod("CreateDeviceVisual", flags);
                MethodInfo resolveDeviceShape = trayServiceType.GetMethod("ResolveDeviceShape", flags);
                MethodInfo createCombinedVisual = trayServiceType.GetMethod("CreateCombinedVisual", flags);
                MethodInfo truncateToolTip = trayServiceType.GetMethod("TruncateToolTip", flags);
                MethodInfo buildDeviceToolTip = trayServiceType.GetMethod("BuildDeviceToolTip", flags);
                Check(failures, createVisual != null, "tray visual helper exists");
                Check(failures, resolveDeviceShape != null, "tray device-shape helper exists");
                Check(failures, createCombinedVisual != null, "combined tray visual helper exists");
                Check(failures, truncateToolTip != null, "tray tooltip helper exists");
                Check(failures, buildDeviceToolTip != null, "device tooltip helper exists");
                if (createVisual == null || resolveDeviceShape == null ||
                    createCombinedVisual == null || truncateToolTip == null ||
                    buildDeviceToolTip == null)
                    return;

                DeviceProfile profile = new DeviceProfile
                {
                    Id = "self-test.device",
                    DisplayName = "Self-test device",
                    LowBatteryPercent = 20
                };

                BatteryReading hundredReading = TestReading(DeviceConnectionState.Connected, 150);
                object hundredVisual = createVisual.Invoke(null, new object[] { profile, hundredReading });
                string hundredText = VisualProperty(hundredVisual, "Text");
                string hundredKey = VisualProperty(hundredVisual, "RenderKey");
                Check(failures, hundredText == "100", "tray percent clamps to 100");

                object repeatedVisual = createVisual.Invoke(null, new object[] { profile, hundredReading });
                string repeatedKey = VisualProperty(repeatedVisual, "RenderKey");
                Check(failures, !string.IsNullOrEmpty(hundredKey) && hundredKey == repeatedKey,
                    "tray render key is stable");

                BatteryReading changedReading = TestReading(DeviceConnectionState.Connected, 99);
                object changedVisual = createVisual.Invoke(null, new object[] { profile, changedReading });
                Check(failures, hundredKey != VisualProperty(changedVisual, "RenderKey"),
                    "tray render key changes with percent");

                profile.Icon = "mouse";
                profile.Category = "headset";
                Check(failures, Convert.ToString(resolveDeviceShape.Invoke(null,
                    new object[] { profile })) == "mouse", "tray profile icon selects shape");
                object mouseVisual = createVisual.Invoke(null,
                    new object[] { profile, changedReading });
                Check(failures, VisualProperty(mouseVisual, "DeviceShape") == "mouse",
                    "tray mouse visual shape");

                profile.Icon = "custom-icon";
                profile.Category = "keyboard";
                Check(failures, Convert.ToString(resolveDeviceShape.Invoke(null,
                    new object[] { profile })) == "keyboard", "tray category shape fallback");
                object keyboardVisual = createVisual.Invoke(null,
                    new object[] { profile, changedReading });
                Check(failures, VisualProperty(keyboardVisual, "DeviceShape") == "keyboard" &&
                    VisualProperty(mouseVisual, "RenderKey") !=
                        VisualProperty(keyboardVisual, "RenderKey"),
                    "tray shape participates in render key");

                profile.Icon = "unrecognized";
                profile.Category = "other";
                Check(failures, Convert.ToString(resolveDeviceShape.Invoke(null,
                    new object[] { profile })) == "device", "tray unknown shape fallback");
                object combinedVisual = createCombinedVisual.Invoke(null,
                    new object[] { mouseVisual });
                Check(failures, VisualProperty(combinedVisual, "DeviceShape") == "combined" &&
                    VisualProperty(combinedVisual, "RenderKey").StartsWith(
                        "combined|", StringComparison.Ordinal),
                    "combined tray uses generic shape");

                profile.Icon = null;
                profile.Category = null;

                BatteryReading unknownReading = TestReading(DeviceConnectionState.Unknown, null);
                object unknownVisual = createVisual.Invoke(null, new object[] { profile, unknownReading });
                string unknownKey = VisualProperty(unknownVisual, "RenderKey");
                Check(failures, VisualProperty(unknownVisual, "Text") == "?" &&
                    unknownKey.StartsWith("unknown|", StringComparison.Ordinal),
                    "tray unknown visual");

                BatteryReading connectedUnknownReading = TestReading(DeviceConnectionState.Connected, null);
                object connectedUnknownVisual = createVisual.Invoke(null,
                    new object[] { profile, connectedUnknownReading });
                Check(failures, VisualProperty(connectedUnknownVisual, "Text") == "?" &&
                    VisualColorArgb(connectedUnknownVisual) ==
                        System.Drawing.Color.FromArgb(255, 120, 137, 160).ToArgb(),
                    "tray connected unknown uses neutral color");

                object thresholdVisual = createVisual.Invoke(null,
                    new object[] { profile, TestReading(DeviceConnectionState.Connected, 20) });
                object aboveThresholdVisual = createVisual.Invoke(null,
                    new object[] { profile, TestReading(DeviceConnectionState.Connected, 21) });
                Check(failures, VisualColorArgb(thresholdVisual) ==
                    System.Drawing.Color.FromArgb(255, 245, 183, 66).ToArgb() &&
                    VisualColorArgb(aboveThresholdVisual) ==
                    System.Drawing.Color.FromArgb(255, 55, 206, 194).ToArgb(),
                    "tray exact percent honors profile low threshold");

                BatteryReading offlineReading = TestReading(DeviceConnectionState.Disconnected, 73);
                object offlineVisual = createVisual.Invoke(null, new object[] { profile, offlineReading });
                string offlineKey = VisualProperty(offlineVisual, "RenderKey");
                Check(failures, VisualProperty(offlineVisual, "Text") == "—" &&
                    offlineKey.StartsWith("offline|", StringComparison.Ordinal) &&
                    offlineKey != unknownKey,
                    "tray offline visual");

                string exactly63 = new string('가', 63);
                string overLimit = new string('나', 70);
                string exactResult = Convert.ToString(truncateToolTip.Invoke(null,
                    new object[] { exactly63 }));
                string truncatedResult = Convert.ToString(truncateToolTip.Invoke(null,
                    new object[] { overLimit }));
                Check(failures, exactResult == exactly63, "tray tooltip keeps 63 characters");
                Check(failures, truncatedResult.Length == 63 &&
                    truncatedResult.EndsWith("…", StringComparison.Ordinal),
                    "tray tooltip truncates to 63 characters");

                string surrogateBoundary = new string('가', 61) + "😀끝";
                string surrogateResult = Convert.ToString(truncateToolTip.Invoke(null,
                    new object[] { surrogateBoundary }));
                Check(failures, surrogateResult.Length <= 63 &&
                    !HasUnpairedSurrogate(surrogateResult),
                    "tray tooltip truncation preserves surrogate pairs");

                profile.DisplayName = new string('긴', 58) + "😀장치";
                BatteryReading chargingReading = TestReading(DeviceConnectionState.Connected, 95);
                chargingReading.Charge = DeviceChargeState.Charging;
                string deviceToolTip = Convert.ToString(buildDeviceToolTip.Invoke(null,
                    new object[] { profile, chargingReading }));
                Check(failures, deviceToolTip.Length <= 63 &&
                    deviceToolTip.EndsWith("충전 중 · 95%", StringComparison.Ordinal),
                    "tray tooltip preserves status suffix");
                Check(failures, !HasUnpairedSurrogate(deviceToolTip),
                    "tray tooltip preserves surrogate pairs");
            }
            catch (Exception ex)
            {
                failures.Add("tray icon pure helpers: " + ex.GetType().Name + " · " + ex.Message);
            }
        }

        private static BatteryReading TestReading(DeviceConnectionState connection, int? percent)
        {
            return new BatteryReading
            {
                ProfileId = "self-test.device",
                DisplayName = "Self-test device",
                Connection = connection,
                Charge = DeviceChargeState.Discharging,
                Percent = percent,
                Band = BatteryReading.BandFromPercent(percent),
                StatusText = connection == DeviceConnectionState.Connected ? "연결됨" : "연결 안 됨"
            };
        }

        private static string VisualProperty(object visual, string propertyName)
        {
            if (visual == null)
                return string.Empty;
            PropertyInfo property = visual.GetType().GetProperty(propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? string.Empty : Convert.ToString(property.GetValue(visual, null));
        }

        private static int VisualColorArgb(object visual)
        {
            if (visual == null)
                return 0;
            PropertyInfo property = visual.GetType().GetProperty("Accent",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null)
                return 0;
            object value = property.GetValue(visual, null);
            return value is System.Drawing.Color
                ? ((System.Drawing.Color)value).ToArgb()
                : 0;
        }

        private static bool HasUnpairedSurrogate(string value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                if (char.IsHighSurrogate(value[index]))
                {
                    if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                        return true;
                    index++;
                }
                else if (char.IsLowSurrogate(value[index]))
                {
                    return true;
                }
            }
            return false;
        }

        private static void Check(List<string> failures, bool condition, string name)
        {
            if (!condition)
                failures.Add(name);
        }
    }
}
