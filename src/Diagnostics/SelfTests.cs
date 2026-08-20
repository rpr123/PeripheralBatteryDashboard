using System;
using System.Collections.Generic;
using System.IO;
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
            Check(failures, typeof(HidSession).GetMethod("OpenReadOnly",
                BindingFlags.Public | BindingFlags.Static) != null,
                "read-only HID session entry point");
            CheckInventoryPureHelpers(failures);

            Check(failures, BluetoothGattBatteryReader.InteropLayoutIsValid,
                "Bluetooth GATT interop layout");
            Check(failures, BluetoothGattBatteryReader.PathMatchesHardware(
                @"\\?\BTHLEDevice#{0000180f-0000-1000-8000-00805f9b34fb}_Dev_VID&02045E_PID&0B13",
                0x045E, new List<ushort> { 0x0B13 }), "Xbox GATT path match");
            Check(failures, BluetoothGattBatteryReader.PathMatchesHardware(
                @"\\?\BTHLEDevice#{0000180f-0000-1000-8000-00805f9b34fb}_Dev_VID&01045E_PID&0B13",
                0x045E, new List<ushort> { 0x0B13 }), "Bluetooth VID source path match");
            Check(failures, BluetoothGattBatteryReader.PathMatchesHardware(
                    @"\\?\BTHLEDevice#{0000180f-0000-1000-8000-00805f9b34fb}_Dev_VID&02045E_PID&0B13",
                    0x045E, new List<ushort> { 0x0B13 }, 0x02) &&
                !BluetoothGattBatteryReader.PathMatchesHardware(
                    @"\\?\BTHLEDevice#{0000180f-0000-1000-8000-00805f9b34fb}_Dev_VID&01045E_PID&0B13",
                    0x045E, new List<ushort> { 0x0B13 }, 0x02),
                "Bluetooth vendor ID namespaces remain distinct");
            Check(failures, !BluetoothGattBatteryReader.PathMatchesHardware(
                @"\\?\BTHLEDevice#{0000180f-0000-1000-8000-00805f9b34fb}_Dev_VID&02045E_PID&FFFF",
                0x045E, new List<ushort> { 0x0B13 }), "Xbox GATT path rejects other PID");
            string bluetoothPath =
                @"\\?\BTHLEDevice#{0000180f-0000-1000-8000-00805f9b34fb}_Dev_VID&02045E_PID&0B13";
            string bluetoothLocalId =
                BluetoothGattBatteryReader.ComputeLocalServiceId(bluetoothPath);
            Check(failures, string.Equals(bluetoothLocalId,
                    BluetoothGattBatteryReader.ComputeLocalServiceId(
                        bluetoothPath.ToLowerInvariant()),
                    StringComparison.Ordinal),
                "Bluetooth local service identity is path-case stable");
            Check(failures, BluetoothGattBatteryReader.CandidateMatches(bluetoothPath,
                    "Xbox Wireless Controller", "Xbox", 0x045E,
                    new List<ushort> { 0x0B13 }, null) &&
                !BluetoothGattBatteryReader.CandidateMatches(bluetoothPath,
                    null, "Xbox", 0x045E, new List<ushort> { 0x0B13 }, null) &&
                BluetoothGattBatteryReader.CandidateMatches(bluetoothPath,
                    null, null, null, new List<ushort>(), bluetoothLocalId),
                "Bluetooth candidate matching is exact and friendly-name filtering fails closed");
            Check(failures, BluetoothGattBatteryReader.ClassifyCandidateSet(2, false) ==
                    BluetoothGattBatteryReadStatus.Ambiguous &&
                BluetoothGattBatteryReader.ClassifyCandidateSet(0, false) ==
                    BluetoothGattBatteryReadStatus.NotFound &&
                BluetoothGattBatteryReader.ClassifyCandidateSet(0, true) ==
                    BluetoothGattBatteryReadStatus.EnumerationUnavailable &&
                BluetoothGattBatteryReader.ClassifyCandidateSet(0, true, true) ==
                    BluetoothGattBatteryReadStatus.EnumerationUnavailable &&
                BluetoothGattBatteryReader.ClassifyCandidateSet(1, true) ==
                    BluetoothGattBatteryReadStatus.EnumerationUnavailable &&
                BluetoothGattBatteryReader.ClassifyCandidateSet(1, true, true) ==
                    BluetoothGattBatteryReadStatus.FoundUnavailable,
                "Bluetooth candidate ambiguity and incomplete enumeration are distinct");

            ProfileStore store = new ProfileStore(baseDirectory);
            IList<DeviceProfile> builtInProfiles = store.ReadProfileFile(
                Path.Combine(baseDirectory, "Profiles", "builtin.devices.json"));
            store.LoadProfiles();
            Check(failures, store.LoadWarnings.Count == 0, "profile load warnings");
            Check(failures, builtInProfiles.Count == 0,
                "public distribution starts with no active device profiles");
            CheckBluetoothProfileValidation(failures);

            ProviderRegistry registry = new ProviderRegistry();
            BuiltInProviderCatalog.RegisterInto(registry);
            string[] builtInProviderIds =
            {
                "builtin.steelseries.nova7",
                "builtin.aula.f108",
                "builtin.vxe.r1",
                "builtin.bluetooth.gatt-battery",
                "builtin.xbox.xinput"
            };
            Check(failures, builtInProviderIds.All(providerId =>
                    registry.ProviderIds.Contains(providerId,
                        StringComparer.OrdinalIgnoreCase)),
                "bundled provider catalog remains available without active profiles");
            Check(failures, ProviderSafetyPolicy.IsAllowedTransport("hid") &&
                    ProviderSafetyPolicy.IsAllowedTransport("bluetooth-gatt") &&
                    ProviderSafetyPolicy.IsAllowedTransport("xinput") &&
                    !ProviderSafetyPolicy.IsAllowedTransport("usb") &&
                    !ProviderSafetyPolicy.IsAllowedTransport("hid "),
                "profile transport allowlist is exact");
            Check(failures, !ProviderSafetyPolicy.IsBuiltInTransportCompatible(
                    "builtin.vxe.r1", "xinput") &&
                ProviderSafetyPolicy.IsBuiltInTransportCompatible(
                    "builtin.vxe.r1", "hid") &&
                ProviderSafetyPolicy.IsBuiltInTransportCompatible(
                    "builtin.bluetooth.gatt-battery", "bluetooth-gatt"),
                "built-in provider transport binding");
            DeviceProfile xinputFixture = new DeviceProfile
            {
                Match = new DeviceMatch
                {
                    Transport = "xinput",
                    XInputUserIndex = 0
                }
            };
            Check(failures, !XboxControllerProvider.AllowsUnboundXInput(xinputFixture),
                "XInput slot is not trusted without explicit opt-in");
            Check(failures, !XboxControllerProvider.HasExactGattIdentity(xinputFixture),
                "XInput profile has no implicit Xbox VID/PID fallback");
            Check(failures, !BluetoothGattBatteryProvider.HasExactIdentity(xinputFixture),
                "generic Bluetooth profile requires a per-PC local service identity");
            xinputFixture.ProviderOptions["AllowUnboundXInput"] = true;
            Check(failures, XboxControllerProvider.AllowsUnboundXInput(xinputFixture),
                "explicit fixed-slot XInput opt-in");
            xinputFixture.Match.VendorId = "0x2DC8";
            xinputFixture.Match.ProductIds.Add("0x301B");
            Check(failures, XboxControllerProvider.HasExactGattIdentity(xinputFixture),
                "explicit controller VID/PID enables exact GATT identity");
            Check(failures, !BluetoothGattBatteryProvider.HasExactIdentity(xinputFixture),
                "generic Bluetooth provider requires a per-PC local service identity");
            DeviceProfile localBluetoothFixture = new DeviceProfile
            {
                Match = new DeviceMatch
                {
                    Transport = "bluetooth-gatt",
                    BluetoothServiceId = BluetoothGattBatteryReader.ComputeLocalServiceId(
                        "private-service-path-fixture")
                }
            };
            Check(failures, localBluetoothFixture.Match.HasValidBluetoothServiceId &&
                    BluetoothGattBatteryProvider.HasExactIdentity(localBluetoothFixture),
                "generic Bluetooth provider accepts a valid per-PC local service ID");
            xinputFixture.Match.XInputUserIndex = null;
            Check(failures, !XboxControllerProvider.AllowsUnboundXInput(xinputFixture),
                "unbound XInput scan remains blocked");
            BatteryReading presentReading = new BatteryReading
            {
                Presence = DevicePresenceState.Present,
                Connection = DeviceConnectionState.Sleeping
            };
            Check(failures, presentReading.IsPresent,
                "present dongle remains visible while peripheral sleeps");
            presentReading.Presence = DevicePresenceState.Absent;
            Check(failures, !presentReading.IsPresent,
                "absent hardware is hidden");
            Check(failures, DeviceMonitorService.InferNonHidPresence(
                    DeviceConnectionState.Connected, DevicePresenceState.Unknown) ==
                    DevicePresenceState.Present &&
                DeviceMonitorService.InferNonHidPresence(
                    DeviceConnectionState.Disconnected, DevicePresenceState.Present) ==
                    DevicePresenceState.Absent,
                "non-HID presence follows live connection state");
            BatteryReading presentButUnreadable = new BatteryReading
            {
                Presence = DevicePresenceState.Present,
                Connection = DeviceConnectionState.Busy
            };
            Check(failures, DeviceMonitorService.ResolveNonHidPresence(
                    presentButUnreadable, DevicePresenceState.Unknown) ==
                    DevicePresenceState.Present,
                "exact Bluetooth service remains visible when its value is unreadable");
            bool duplicateRejected = false;
            try { registry.Register(new SteelSeriesNova7Provider()); }
            catch (InvalidOperationException) { duplicateRejected = true; }
            Check(failures, duplicateRejected, "duplicate provider rejection");

            return failures;
        }

        private static void CheckBluetoothProfileValidation(List<string> failures)
        {
            string validLocalId = "bt-bas-0123456789abcdef01234567";
            DeviceProfile valid = BluetoothValidationProfile(validLocalId,
                string.Empty, new List<string>());
            DeviceProfile missingLocal = BluetoothValidationProfile(string.Empty,
                "0x1234", new List<string> { "0x5678" });
            DeviceProfile malformedLocal = BluetoothValidationProfile("bt-bas-invalid",
                "0x1234", new List<string> { "0x5678" });
            DeviceProfile partialHardware = BluetoothValidationProfile(validLocalId,
                "0x1234", new List<string>());
            DeviceProfile wrongTransport = BluetoothValidationProfile(validLocalId,
                "0x1234", new List<string> { "0x5678" });
            wrongTransport.ProviderId = "fixture-provider";
            wrongTransport.Match.Transport = "hid";
            DeviceProfile validCustomGatt = BluetoothValidationProfile(string.Empty,
                "0x1234", new List<string> { "0x5678" });
            validCustomGatt.ProviderId = "fixture.bluetooth-gatt";
            DeviceProfile customGattMissingHardware = BluetoothValidationProfile(string.Empty,
                string.Empty, new List<string>());
            customGattMissingHardware.ProviderId = "fixture.bluetooth-gatt";
            DeviceProfile customGattWithBasIdentity = BluetoothValidationProfile(validLocalId,
                "0x1234", new List<string> { "0x5678" });
            customGattWithBasIdentity.ProviderId = "fixture.bluetooth-gatt";

            Check(failures,
                ProfileValidationAccepts(valid) &&
                !ProfileValidationAccepts(missingLocal) &&
                !ProfileValidationAccepts(malformedLocal) &&
                !ProfileValidationAccepts(partialHardware) &&
                !ProfileValidationAccepts(wrongTransport) &&
                ProfileValidationAccepts(validCustomGatt) &&
                !ProfileValidationAccepts(customGattMissingHardware) &&
                !ProfileValidationAccepts(customGattWithBasIdentity),
                "Bluetooth profile validation separates standard BAS and custom GATT identities");
        }

        private static DeviceProfile BluetoothValidationProfile(string localServiceId,
            string vendorId, List<string> productIds)
        {
            return new DeviceProfile
            {
                Id = "self-test.bluetooth-profile-validation",
                DisplayName = "Bluetooth validation fixture",
                ProviderId = "builtin.bluetooth.gatt-battery",
                Match = new DeviceMatch
                {
                    Transport = "bluetooth-gatt",
                    VendorId = vendorId,
                    ProductIds = productIds,
                    BluetoothServiceId = localServiceId
                }
            };
        }

        private static bool ProfileValidationAccepts(DeviceProfile profile)
        {
            MethodInfo validate = typeof(ProfileStore).GetMethod("Validate",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (validate == null)
                return false;
            try
            {
                validate.Invoke(null, new object[] { profile, "self-test" });
                return true;
            }
            catch (TargetInvocationException ex)
            {
                if (ex.InnerException is InvalidDataException)
                    return false;
                throw;
            }
        }

        private static void CheckInventoryPureHelpers(List<string> failures)
        {
            Guid fixtureContainerId = new Guid("11111111-2222-3333-4444-555555555555");
            HidDeviceDescriptor descriptor = new HidDeviceDescriptor
            {
                DevicePath = @"\\?\hid#vid_1234&pid_abcd#private-serial",
                VendorId = 0x1234,
                ProductId = 0xABCD,
                VersionNumber = 0x0102,
                InterfaceNumber = 3,
                UsagePage = 0xFF60,
                Usage = 0x0061,
                InputReportLength = 33,
                OutputReportLength = 33,
                FeatureReportLength = 0,
                CapabilitiesAvailable = true,
                ContainerId = fixtureContainerId,
                ProductName = "테스트 " + Environment.UserName +
                    " 장치 AA:BB:CC:DD:EE:FF"
            };
            DeviceProfile profile = new DeviceProfile
            {
                Id = "self-test.inventory",
                DisplayName = "Inventory fixture",
                ProviderId = "fixture-provider",
                Match = new DeviceMatch
                {
                    VendorId = "0x1234",
                    ProductIds = new List<string> { "0xABCD" },
                    InterfaceNumber = 3,
                    UsagePage = "0xFF60",
                    Usage = "0x0061"
                }
            };

            InventorySnapshot snapshot = InventoryService.BuildSnapshot(
                new[] { descriptor }, new[] { profile },
                new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc));
            string json = new InventoryService(new List<DeviceProfile>(), new HidDeviceEnumerator())
                .ToJson(snapshot);

            Check(failures, !snapshot.ProviderBatteryRequestsSent,
                "inventory sends no provider requests");
            Check(failures, snapshot.HidCollections.Count == 1 &&
                snapshot.HidCollections[0].MatchStatus == "exact-selector-match" &&
                snapshot.HidCollections[0].MatchedProfileCount == 1 &&
                snapshot.HidCollections[0].DeviceMatchStatus ==
                    "exact-profile-selector-present" &&
                !snapshot.HidCollections[0].ResearchCandidate,
                "inventory exact profile match");
            Check(failures, json.Contains("\"vendorId\":\"0x1234\"") &&
                json.Contains("\"usagePage\":\"0xFF60\"") &&
                json.Contains("\"deviceGroupIdsArePseudonymousLocalIdentifiers\":true"),
                "inventory safe HID metadata");
            Check(failures, !json.Contains("private-serial") &&
                !json.Contains("AA:BB:CC:DD:EE:FF") &&
                !json.Contains(fixtureContainerId.ToString("D")) &&
                (string.IsNullOrWhiteSpace(Environment.UserName) ||
                 json.IndexOf(Environment.UserName, StringComparison.OrdinalIgnoreCase) < 0),
                "inventory redacts paths and user name");
            Check(failures, DeviceMonitorService.HasExactHidSelector(profile),
                "exact HID selector gate");
            Check(failures, DeviceMonitorService.ResolveHidPresence(profile,
                    new HidEnumerationResult(new List<HidDeviceDescriptor> { descriptor },
                        new List<string>()), DevicePresenceState.Unknown) ==
                    DevicePresenceState.Present,
                "HID presence exact match");

            string bluetoothProfileServiceId =
                BluetoothGattBatteryReader.ComputeLocalServiceId(
                    "bluetooth-service-fixture");
            DeviceProfile bluetoothProfile = new DeviceProfile
            {
                Id = "self-test.bluetooth-gatt",
                DisplayName = "Bluetooth Battery fixture",
                ProviderId = "builtin.bluetooth.gatt-battery",
                Match = new DeviceMatch
                {
                    Transport = "bluetooth-gatt",
                    VendorId = "0x2DC8",
                    ProductIds = new List<string> { "0x301B" },
                    BluetoothServiceId = bluetoothProfileServiceId
                }
            };
            InventorySnapshot bluetoothSnapshot = InventoryService.BuildSnapshot(
                new HidDeviceDescriptor[0],
                new[]
                {
                    new BluetoothGattBatteryServiceDescriptor
                    {
                        VendorIdSource = 0x02,
                        VendorId = 0x2DC8,
                        ProductId = 0x301B,
                        LocalServiceId = bluetoothProfileServiceId,
                        FriendlyName = "8BitDo Controller"
                    }
                },
                new[] { bluetoothProfile }, DateTime.UtcNow);
            Check(failures, bluetoothSnapshot.BluetoothBatteryServices.Count == 1 &&
                    bluetoothSnapshot.BluetoothBatteryServices[0].MatchStatus ==
                        "exact-standard-battery-profile-present" &&
                    !bluetoothSnapshot.BluetoothBatteryServices[0].ResearchCandidate,
                "inventory recognizes a generic standard Bluetooth battery profile");

            BluetoothGattBatteryServiceDescriptor secondSameModelService =
                new BluetoothGattBatteryServiceDescriptor
                {
                    VendorIdSource = 0x02,
                    VendorId = 0x2DC8,
                    ProductId = 0x301B,
                    LocalServiceId = BluetoothGattBatteryReader.ComputeLocalServiceId(
                        "second-bluetooth-service-fixture"),
                    FriendlyName = "8BitDo Controller"
                };
            DeviceProfile broadXboxStyleProfile = new DeviceProfile
            {
                Id = "self-test.bluetooth-ambiguous",
                DisplayName = "Ambiguous controller fixture",
                ProviderId = "builtin.xbox.xinput",
                Match = new DeviceMatch
                {
                    Transport = "xinput",
                    VendorId = "0x2DC8",
                    ProductIds = new List<string> { "0x301B" }
                }
            };
            BluetoothGattBatteryServiceDescriptor firstSameModelService =
                new BluetoothGattBatteryServiceDescriptor
                {
                    VendorIdSource = 0x02,
                    VendorId = 0x2DC8,
                    ProductId = 0x301B,
                    LocalServiceId = bluetoothProfileServiceId,
                    FriendlyName = "8BitDo Controller"
                };
            InventorySnapshot ambiguousBluetoothSnapshot = InventoryService.BuildSnapshot(
                new HidDeviceDescriptor[0],
                new[] { firstSameModelService, secondSameModelService },
                new[] { broadXboxStyleProfile }, DateTime.UtcNow);
            Check(failures,
                ambiguousBluetoothSnapshot.BluetoothBatteryServices.Count == 2 &&
                ambiguousBluetoothSnapshot.BluetoothBatteryServices.All(item =>
                    item.MatchStatus == "ambiguous-standard-battery-profile" &&
                    item.ResearchCandidate),
                "inventory reports multi-service profile matches as ambiguous");
            BluetoothGattBatteryServiceDescriptor wrongVendorNamespaceService =
                new BluetoothGattBatteryServiceDescriptor
                {
                    VendorIdSource = 0x01,
                    VendorId = 0x2DC8,
                    ProductId = 0x301B,
                    LocalServiceId = BluetoothGattBatteryReader.ComputeLocalServiceId(
                        "wrong-vendor-namespace-service"),
                    FriendlyName = "8BitDo Controller"
                };
            Check(failures,
                !InventoryService.StandardBluetoothProfileMatches(
                    broadXboxStyleProfile, wrongVendorNamespaceService),
                "Xbox-style inventory matching requires the USB-IF vendor namespace");
            DeviceProfile customGattProfile = new DeviceProfile
            {
                Id = "self-test.custom-gatt",
                DisplayName = "Custom GATT fixture",
                ProviderId = "fixture.bluetooth-gatt",
                Match = new DeviceMatch
                {
                    Transport = "bluetooth-gatt",
                    VendorId = "0x2DC8",
                    ProductIds = new List<string> { "0x301B" }
                }
            };
            InventorySnapshot customGattInventory = InventoryService.BuildSnapshot(
                new HidDeviceDescriptor[0], new[] { firstSameModelService },
                new[] { customGattProfile }, DateTime.UtcNow);
            Check(failures,
                customGattInventory.BluetoothBatteryServices.Count == 1 &&
                customGattInventory.BluetoothBatteryServices[0].MatchStatus ==
                    "unmatched-standard-battery-service" &&
                customGattInventory.BluetoothBatteryServices[0].ResearchCandidate,
                "custom GATT providers do not masquerade as the standard BAS provider");
            string bluetoothJson = new InventoryService(new List<DeviceProfile>(),
                new HidDeviceEnumerator()).ToJson(bluetoothSnapshot);
            Check(failures, bluetoothJson.Contains(
                    "\"bluetoothStandardBatteryServices\":true") &&
                bluetoothJson.Contains("\"bluetoothBatteryServices\"") &&
                bluetoothJson.Contains("\"vendorIdSource\":\"0x02\"") &&
                bluetoothJson.Contains("\"vendorId\":\"0x2DC8\"") &&
                bluetoothJson.Contains("\"localServiceId\":\"bt-bas-") &&
                bluetoothJson.Contains(
                    "\"bluetoothServiceIdsArePseudonymousLocalIdentifiers\":true"),
                "inventory reports standard Bluetooth Battery Service metadata");

            string localBluetoothServiceId = BluetoothGattBatteryReader.ComputeLocalServiceId(
                "local-bluetooth-service-fixture");
            DeviceProfile localBluetoothProfile = new DeviceProfile
            {
                Id = "self-test.bluetooth-local",
                DisplayName = "Bluetooth local identity fixture",
                ProviderId = "builtin.bluetooth.gatt-battery",
                Match = new DeviceMatch
                {
                    Transport = "bluetooth-gatt",
                    BluetoothServiceId = localBluetoothServiceId
                }
            };
            InventorySnapshot localBluetoothSnapshot = InventoryService.BuildSnapshot(
                new HidDeviceDescriptor[0],
                new[]
                {
                    new BluetoothGattBatteryServiceDescriptor
                    {
                        LocalServiceId = localBluetoothServiceId,
                        FriendlyName = "Bluetooth device without PnP VID/PID"
                    }
                },
                new[] { localBluetoothProfile }, DateTime.UtcNow);
            Check(failures, localBluetoothSnapshot.BluetoothBatteryServices.Count == 1 &&
                    !localBluetoothSnapshot.BluetoothBatteryServices[0].HasHardwareIdentity &&
                    localBluetoothSnapshot.BluetoothBatteryServices[0].MatchedProfileCount == 1 &&
                    !localBluetoothSnapshot.BluetoothBatteryServices[0].ResearchCandidate,
                "local Bluetooth service ID supports BAS devices without VID/PID");

            DeviceProfile combinedBluetoothIdentityProfile = new DeviceProfile
            {
                Id = "self-test.bluetooth-combined-identity",
                DisplayName = "Bluetooth combined identity fixture",
                ProviderId = "builtin.bluetooth.gatt-battery",
                Match = new DeviceMatch
                {
                    Transport = "bluetooth-gatt",
                    VendorId = "0x2DC8",
                    ProductIds = new List<string> { "0x301B" },
                    BluetoothServiceId = localBluetoothServiceId
                }
            };
            BluetoothGattBatteryServiceDescriptor matchingCombinedService =
                new BluetoothGattBatteryServiceDescriptor
                {
                    VendorId = 0x2DC8,
                    ProductId = 0x301B,
                    LocalServiceId = localBluetoothServiceId,
                    FriendlyName = "Expected Controller"
                };
            BluetoothGattBatteryServiceDescriptor conflictingCombinedService =
                new BluetoothGattBatteryServiceDescriptor
                {
                    VendorId = 0x045E,
                    ProductId = 0x0B13,
                    LocalServiceId = localBluetoothServiceId,
                    FriendlyName = "Expected Controller"
                };
            combinedBluetoothIdentityProfile.ProviderOptions["BluetoothNameContains"] =
                "Expected";
            string combinedBluetoothPath =
                @"\\?\BTHLEDevice#{0000180f-0000-1000-8000-00805f9b34fb}_Dev_VID&02045E_PID&0B13";
            string combinedBluetoothLocalId =
                BluetoothGattBatteryReader.ComputeLocalServiceId(combinedBluetoothPath);
            Check(failures,
                InventoryService.StandardBluetoothProfileMatches(
                    combinedBluetoothIdentityProfile, matchingCombinedService) &&
                !InventoryService.StandardBluetoothProfileMatches(
                    combinedBluetoothIdentityProfile, conflictingCombinedService) &&
                !InventoryService.StandardBluetoothProfileMatches(
                    combinedBluetoothIdentityProfile,
                    new BluetoothGattBatteryServiceDescriptor
                    {
                        VendorId = 0x2DC8,
                        ProductId = 0x301B,
                        LocalServiceId = localBluetoothServiceId,
                        FriendlyName = "Different Controller"
                    }) &&
                BluetoothGattBatteryReader.CandidateMatches(
                    combinedBluetoothPath, null, null, 0x045E,
                    new List<ushort> { 0x0B13 }, combinedBluetoothLocalId) &&
                !BluetoothGattBatteryReader.CandidateMatches(
                    combinedBluetoothPath, null, null, 0x045E,
                    new List<ushort> { 0xFFFF }, combinedBluetoothLocalId),
                "Bluetooth local, hardware and friendly-name identities use AND semantics");

            DeviceProfile noInterfaceProfile = new DeviceProfile
            {
                Id = "self-test.no-interface",
                DisplayName = "No MI fixture",
                ProviderId = "fixture-provider",
                Match = new DeviceMatch
                {
                    VendorId = "0x1234",
                    ProductIds = new List<string> { "0xABCD" },
                    RequireNoInterfaceNumber = true,
                    UsagePage = "0xFF60",
                    Usage = "0x0061"
                }
            };
            HidDeviceDescriptor noInterfaceDescriptor = new HidDeviceDescriptor
            {
                VendorId = 0x1234,
                ProductId = 0xABCD,
                InterfaceNumber = null,
                UsagePage = 0xFF60,
                Usage = 0x0061,
                CapabilitiesAvailable = true
            };
            Check(failures, DeviceMonitorService.HasExactHidSelector(noInterfaceProfile) &&
                    noInterfaceDescriptor.Matches(noInterfaceProfile),
                "exact HID selector can require an absent MI component");
            noInterfaceDescriptor.InterfaceNumber = 0;
            Check(failures, !noInterfaceDescriptor.Matches(noInterfaceProfile),
                "no-MI selector rejects a numbered interface");

            HidDeviceDescriptor siblingCollection = new HidDeviceDescriptor
            {
                VendorId = 0x1234,
                ProductId = 0xABCD,
                VersionNumber = 0x0102,
                InterfaceNumber = 1,
                UsagePage = 0x0001,
                Usage = 0x0006,
                CapabilitiesAvailable = false,
                ContainerId = fixtureContainerId,
                ProductName = string.Empty
            };
            Check(failures, DeviceMonitorService.ResolveHidPresence(profile,
                    new HidEnumerationResult(new List<HidDeviceDescriptor> { siblingCollection },
                        new List<string> { "hid-capabilities-unavailable" }),
                    DevicePresenceState.Present) == DevicePresenceState.Present,
                "HID incomplete matching identity preserves presence");
            InventorySnapshot siblingSnapshot = InventoryService.BuildSnapshot(
                new[] { siblingCollection }, new[] { profile }, DateTime.UtcNow);
            Check(failures, siblingSnapshot.HidCollections[0].MatchStatus ==
                    "same-vid-pid-sibling" &&
                siblingSnapshot.HidCollections[0].DeviceMatchStatus ==
                    "known-vid-pid-selector-missing" &&
                siblingSnapshot.HidCollections[0].ResearchCandidate &&
                siblingSnapshot.HidCollections[0].InputReportLength == null,
                "inventory distinguishes sibling collection and unknown capabilities");

            InventorySnapshot compositeSnapshot = InventoryService.BuildSnapshot(
                new[] { descriptor, siblingCollection }, new[] { profile }, DateTime.UtcNow);
            Check(failures, compositeSnapshot.HidCollections.Count == 2 &&
                compositeSnapshot.HidCollections.All(collection =>
                    collection.DeviceMatchStatus == "exact-profile-selector-present" &&
                    !collection.ResearchCandidate &&
                    collection.DeviceGroupId == compositeSnapshot.HidCollections[0].DeviceGroupId),
                "inventory groups composite HID sibling collections");

            HidDeviceDescriptor fallbackCollectionA = new HidDeviceDescriptor
            {
                VendorId = 0x2222,
                ProductId = 0x3333,
                VersionNumber = 0x0100,
                InterfaceNumber = 0,
                UsagePage = 0x0001,
                Usage = 0x0002,
                CapabilitiesAvailable = true
            };
            HidDeviceDescriptor fallbackCollectionB = new HidDeviceDescriptor
            {
                VendorId = 0x2222,
                ProductId = 0x3333,
                VersionNumber = 0x0100,
                InterfaceNumber = 2,
                UsagePage = 0xFF00,
                Usage = 0x0001,
                CapabilitiesAvailable = true
            };
            InventorySnapshot fallbackGroups = InventoryService.BuildSnapshot(
                new[] { fallbackCollectionA, fallbackCollectionB },
                new DeviceProfile[0], DateTime.UtcNow);
            Check(failures, fallbackGroups.HidCollections.Select(collection =>
                    collection.DeviceGroupId).Distinct(StringComparer.Ordinal).Count() == 2 &&
                fallbackGroups.HidCollections.All(collection => collection.ResearchCandidate),
                "inventory does not merge unproven sibling collections without container identity");

            HidEnumerationResult partialEnumeration = new HidEnumerationResult(
                new List<HidDeviceDescriptor>(),
                new List<string> { "hid-interface-detail-failed" });
            Check(failures, !partialEnumeration.Complete,
                "inventory metadata enumeration reports partial failure");

            HidDeviceDescriptor unrelatedEarlierDevice = new HidDeviceDescriptor
            {
                VendorId = 0x0001,
                ProductId = 0x0002,
                VersionNumber = 0x0003,
                InterfaceNumber = 0,
                UsagePage = 0x0001,
                Usage = 0x0002,
                CapabilitiesAvailable = true,
                ProductName = "Earlier device"
            };
            Check(failures, DeviceMonitorService.ResolveHidPresence(profile,
                    new HidEnumerationResult(
                        new List<HidDeviceDescriptor> { unrelatedEarlierDevice },
                        new List<string> { "hid-capabilities-unavailable" }),
                    DevicePresenceState.Present) == DevicePresenceState.Absent,
                "unrelated HID warning does not keep removed hardware visible");
            Check(failures, DeviceMonitorService.ResolveHidPresence(profile,
                    new HidEnumerationResult(new List<HidDeviceDescriptor>(),
                        new List<string> { "hid-device-set-open-failed" }),
                    DevicePresenceState.Present) == DevicePresenceState.Present,
                "global HID enumeration failure preserves prior presence");
            Check(failures, DeviceMonitorService.ResolveHidPresence(profile,
                    new HidEnumerationResult(new List<HidDeviceDescriptor>(),
                        new List<string> { "hid-interface-detail-failed" }),
                    DevicePresenceState.Present) == DevicePresenceState.Present,
                "unidentified HID interface failure preserves prior presence");
            InventorySnapshot stableIdSnapshot = InventoryService.BuildSnapshot(
                new[] { unrelatedEarlierDevice, descriptor }, new[] { profile }, DateTime.UtcNow);
            Check(failures, stableIdSnapshot.HidCollections.Single(collection =>
                    collection.VendorId == "0x1234").DeviceGroupId ==
                    snapshot.HidCollections[0].DeviceGroupId,
                "inventory device group id is stable across unrelated changes");

            profile.Match.Transport = "xinput";
            InventorySnapshot xinputSnapshot = InventoryService.BuildSnapshot(
                new[] { descriptor }, new[] { profile }, DateTime.UtcNow);
            Check(failures, xinputSnapshot.HidCollections[0].MatchStatus ==
                    "unmatched-hid-collection" &&
                xinputSnapshot.HidCollections[0].ResearchCandidate,
                "inventory does not match XInput profile to HID");

            profile.Match.Transport = "hid";
            profile.Match.InterfaceNumber = null;
            profile.Match.UsagePage = string.Empty;
            profile.Match.Usage = string.Empty;
            Check(failures, !DeviceMonitorService.HasExactHidSelector(profile),
                "broad HID selector is blocked from provider I/O");
            InventorySnapshot broadSnapshot = InventoryService.BuildSnapshot(
                new[] { descriptor }, new[] { profile }, DateTime.UtcNow);
            Check(failures, broadSnapshot.HidCollections[0].MatchStatus ==
                    "broad-selector-match" &&
                broadSnapshot.HidCollections[0].DeviceMatchStatus ==
                    "broad-profile-selector-present" &&
                broadSnapshot.HidCollections[0].ResearchCandidate,
                "inventory keeps broad profile selector as research candidate");

            string extraPrivateText = InventoryService.SanitizeProductName(
                "Pad 0011.2233.4455 001122334455 from C:\\Users\\private\\device");
            Check(failures, !extraPrivateText.Contains("0011.2233.4455") &&
                !extraPrivateText.Contains("001122334455") &&
                !extraPrivateText.Contains("C:\\Users"),
                "inventory redacts additional address and embedded path forms");
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
