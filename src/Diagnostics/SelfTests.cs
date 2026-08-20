using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
            DeviceProfile workerRoundTripProfile = WorkerProfile(
                "fixture.한글-profile", "test.worker");
            workerRoundTripProfile.ProviderOptions["한글-option"] = "값";
            string workerRequestId = Guid.NewGuid().ToString("N");
            ProviderWorkerRequest workerRoundTripRequest;
            string workerRequest = ProviderWorkerProtocol.SerializeRequest(
                workerRequestId, workerRoundTripProfile);
            Check(failures, ProviderWorkerProtocol.TryDeserializeRequest(
                    workerRequest, out workerRoundTripRequest) &&
                    workerRoundTripRequest.RequestId == workerRequestId &&
                    workerRoundTripRequest.Profile.Id == workerRoundTripProfile.Id &&
                    Convert.ToString(workerRoundTripRequest.Profile.ProviderOptions[
                        "한글-option"]) == "값",
                "provider worker profile snapshot round trip");
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
            string refreshMutexName =
                BluetoothGattBatteryReader.BuildDeviceRefreshMutexName(bluetoothPath);
            Check(failures,
                string.Equals(refreshMutexName,
                    BluetoothGattBatteryReader.BuildDeviceRefreshMutexName(
                        bluetoothPath.ToLowerInvariant()), StringComparison.Ordinal) &&
                refreshMutexName.StartsWith(
                    @"Local\PeripheralBatteryDashboard.GattRefresh.",
                    StringComparison.Ordinal) &&
                refreshMutexName.IndexOf("BTHLEDevice", StringComparison.OrdinalIgnoreCase) < 0,
                "Bluetooth refresh mutex is cross-process, stable, and path-redacted");
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
            DeviceProfile unknownProbeProfile = WorkerProfile(
                "presence.unknown-probe", "test.worker");
            Check(failures,
                !DeviceMonitorService.CanProbeUnknownHid(unknownProbeProfile,
                    DevicePresenceState.Unknown, true, true, false) &&
                DeviceMonitorService.CanProbeUnknownHid(unknownProbeProfile,
                    DevicePresenceState.Unknown, true, true, true) &&
                DeviceMonitorService.CanProbeUnknownHid(unknownProbeProfile,
                    DevicePresenceState.Unknown, true, false, false),
                "exact HID worker probes only after presence becomes inconclusive");
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
            CheckMonitorIsolation(failures);
            CheckProviderWorkerIsolation(failures, baseDirectory);

            return failures;
        }

        private static void CheckProviderWorkerIsolation(List<string> failures,
            string baseDirectory)
        {
            ProviderWorkerClient client = new ProviderWorkerClient(baseDirectory,
                "--provider-worker-fixture");
            DeviceProfile successProfile = WorkerProfile("fixture.한글-success",
                "test.worker");
            BatteryReading success = client.ReadAsync(successProfile,
                    CancellationToken.None).GetAwaiter().GetResult();
            Check(failures, success.Connection == DeviceConnectionState.Connected &&
                    success.Percent == 61 && success.ProfileId == successProfile.Id &&
                    success.DisplayName == successProfile.DisplayName,
                "provider worker returns a validated battery reading");

            DeviceProfile malformedProfile = WorkerProfile("fixture.malformed",
                "test.worker");
            BatteryReading malformed = client.ReadAsync(malformedProfile,
                    CancellationToken.None).GetAwaiter().GetResult();
            Check(failures, malformed.Connection == DeviceConnectionState.Error &&
                    malformed.ErrorCode == "provider-worker-output-invalid",
                "provider worker rejects malformed output");

            DeviceProfile hungProfile = WorkerProfile("fixture.same-device",
                "test.worker");
            hungProfile.ProviderOptions["FixtureBehavior"] = "hang";
            bool cancelled = false;
            Stopwatch duration = Stopwatch.StartNew();
            using (CancellationTokenSource timeout = new CancellationTokenSource())
            {
                timeout.CancelAfter(300);
                try
                {
                    client.ReadAsync(hungProfile, timeout.Token)
                        .GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
            }
            duration.Stop();
            int killedProcessId = ProviderWorkerClient.LastStartedWorkerProcessId;
            bool childExited = SpinWait.SpinUntil(
                () => ProviderWorkerClient.ActiveWorkerCount == 0, 2000);
            Check(failures, cancelled && childExited &&
                    duration.ElapsedMilliseconds < 2500 &&
                    IsProcessExited(killedProcessId),
                "provider worker hard timeout terminates a hung child");

            hungProfile.ProviderOptions["FixtureBehavior"] = "success";
            BatteryReading retry = client.ReadAsync(hungProfile,
                    CancellationToken.None).GetAwaiter().GetResult();
            Check(failures, retry.Connection == DeviceConnectionState.Connected &&
                    retry.Percent == 61 && retry.ProfileId == hungProfile.Id &&
                    ProviderWorkerClient.ActiveWorkerCount == 0,
                "provider worker retries the same device after terminating a hang");

            DeviceProfile exitProfile = WorkerProfile("fixture.exit",
                "test.worker");
            BatteryReading nonzeroExit = client.ReadAsync(exitProfile,
                    CancellationToken.None).GetAwaiter().GetResult();
            Check(failures, nonzeroExit.Connection == DeviceConnectionState.Error &&
                    nonzeroExit.ErrorCode == "provider-worker-exit",
                "provider worker rejects a nonzero child exit");

            DeviceProfile floodProfile = WorkerProfile("fixture.flood",
                "test.worker");
            BatteryReading flood = client.ReadAsync(floodProfile,
                    CancellationToken.None).GetAwaiter().GetResult();
            Check(failures, flood.Connection == DeviceConnectionState.Error &&
                    flood.ErrorCode == "provider-worker-output-too-large" &&
                    ProviderWorkerClient.ActiveWorkerCount == 0,
                "provider worker bounds and rejects excessive output");
        }

        private static bool IsProcessExited(int processId)
        {
            if (processId <= 0)
                return false;
            try
            {
                using (Process process = Process.GetProcessById(processId))
                    return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        private static void CheckMonitorIsolation(List<string> failures)
        {
            DeviceProfile hungProfile = MonitorProfile("monitor.hung", "test.monitor.hung", 0);
            DeviceProfile sameDeviceProfile = MonitorProfile(
                "monitor.same-device", "test.monitor.same-device", 0);
            DeviceProfile secondHungProfile = MonitorProfile(
                "monitor.second-hung", "test.monitor.second-hung", 2);
            DeviceProfile fastProfile = MonitorProfile("monitor.fast", "test.monitor.fast", 1);
            DeviceProfile staleProfile = MonitorProfile(
                "monitor.stale-cache", "test.monitor.stale-cache", 3);

            NeverCompletingProvider hung = new NeverCompletingProvider(
                hungProfile.ProviderId, hungProfile);
            SynchronousBlockingProvider secondHung = new SynchronousBlockingProvider(
                secondHungProfile.ProviderId, secondHungProfile);
            CountingProvider sameDevice = new CountingProvider(
                sameDeviceProfile.ProviderId, sameDeviceProfile);
            CountingProvider fast = new CountingProvider(
                fastProfile.ProviderId, fastProfile);
            StaleCacheProvider stale = new StaleCacheProvider(
                staleProfile.ProviderId, staleProfile);
            ProviderRegistry registry = new ProviderRegistry();
            registry.Register(hung);
            registry.Register(secondHung);
            registry.Register(sameDevice);
            registry.Register(fast);
            registry.Register(stale);

            AppSettings settings = new AppSettings { PollSeconds = 1 };
            DeviceMonitorService monitor = new DeviceMonitorService(
                new[]
                {
                    hungProfile,
                    sameDeviceProfile,
                    secondHungProfile,
                    fastProfile,
                    staleProfile
                },
                registry,
                new BatteryReadContext(new HidDeviceEnumerator()),
                settings,
                20,
                100,
                0,
                100);
            int deliveredEvents = 0;
            int timeoutEvents = 0;
            int hungConnectedEvents = 0;
            ManualResetEventSlim blockingSubscriberStarted =
                new ManualResetEventSlim(false);
            ManualResetEventSlim releaseBlockingSubscriber =
                new ManualResetEventSlim(false);
            monitor.ReadingUpdated += (sender, args) =>
            {
                blockingSubscriberStarted.Set();
                releaseBlockingSubscriber.Wait(4000);
            };
            monitor.ReadingUpdated += (sender, args) =>
            {
                throw new InvalidOperationException("self-test subscriber failure");
            };
            monitor.ReadingUpdated += (sender, args) =>
            {
                Interlocked.Increment(ref deliveredEvents);
                if (args != null && args.Reading != null &&
                    string.Equals(args.Reading.ProfileId, hungProfile.Id,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(args.Reading.ErrorCode, "provider-watchdog-timeout",
                        StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref timeoutEvents);
                }
                if (args != null && args.Reading != null &&
                    string.Equals(args.Reading.ProfileId, hungProfile.Id,
                        StringComparison.OrdinalIgnoreCase) &&
                    args.Reading.Connection == DeviceConnectionState.Connected)
                {
                    Interlocked.Increment(ref hungConnectedEvents);
                }
            };

            Stopwatch lifetime = Stopwatch.StartNew();
            monitor.Start();
            bool watchdogObserved = SpinWait.SpinUntil(
                () => monitor.Health.WatchdogTimeoutCount >= 2 &&
                      Volatile.Read(ref timeoutEvents) == 1, 1500);
            bool fastContinued = SpinWait.SpinUntil(() => fast.CallCount >= 2, 3000);
            DeviceMonitorHealth health = monitor.Health;
            BatteryReading staleSnapshot = monitor.Snapshot.FirstOrDefault(reading =>
                string.Equals(reading.ProfileId, staleProfile.Id,
                    StringComparison.OrdinalIgnoreCase));

            Check(failures, watchdogObserved,
                "monitor watchdog reports a non-cooperative provider once");
            Check(failures, fastContinued,
                "monitor keeps polling other devices after one provider hangs");
            Check(failures, staleSnapshot != null && staleSnapshot.Percent == 66 &&
                    staleSnapshot.IsStale &&
                    staleSnapshot.Connection == DeviceConnectionState.Error,
                "monitor preserves a provider-supplied stale cache percentage");
            Check(failures, hung.CallCount == 1 && hung.MaxActive == 1,
                "monitor keeps at most one raw attempt for a hung device");
            Check(failures, secondHung.CallCount == 1 && secondHung.MaxActive == 1,
                "monitor fills both responsive slots before watchdog recovery");
            Check(failures, sameDevice.CallCount == 0,
                "monitor retains the physical I/O key while a timed-out call remains alive");
            Check(failures, health.WatchdogTimeoutCount >= 2 &&
                    health.TimedOutNativeCallCount == 2 &&
                    health.ActiveReadCount >= health.TimedOutNativeCallCount,
                "monitor health exposes quarantined native calls");
            Check(failures, health.SubscriberErrorCount >= 1 &&
                    Volatile.Read(ref deliveredEvents) >= 2 &&
                    blockingSubscriberStarted.IsSet &&
                    health.LastHeartbeatUtc > DateTime.MinValue,
                "monitor isolates blocked and throwing subscribers while keeping its heartbeat");

            DeviceProfile bluetoothKeys = new DeviceProfile
            {
                Id = "monitor.bluetooth-keys",
                Match = new DeviceMatch
                {
                    Transport = "bluetooth-gatt",
                    VendorId = "0x045E",
                    ProductIds = new List<string> { "0x0B13" },
                    BluetoothServiceId = "bt-bas-0123456789abcdef01234567"
                }
            };
            IList<string> ioKeys = DeviceMonitorService.BuildIoKeys(bluetoothKeys);
            Check(failures,
                ioKeys.Contains("bt:bt-bas-0123456789abcdef01234567",
                    StringComparer.OrdinalIgnoreCase) &&
                ioKeys.Contains("bt:045E:0B13",
                    StringComparer.OrdinalIgnoreCase),
                "Bluetooth I/O ownership retains both local-service and hardware keys");

            releaseBlockingSubscriber.Set();
            monitor.RefreshAll();
            monitor.RefreshAll();
            Thread.Sleep(200);
            Check(failures, hung.CallCount == 1 && hung.MaxActive == 1 &&
                    secondHung.CallCount == 1 && secondHung.MaxActive == 1,
                "manual refresh coalesces without overlapping a hung request");

            hung.Release();
            secondHung.Release();
            bool pendingRefreshRan = SpinWait.SpinUntil(() =>
                hung.CallCount >= 2 && secondHung.CallCount >= 2 &&
                Volatile.Read(ref hungConnectedEvents) >= 1, 1500);
            Check(failures, pendingRefreshRan && hung.CallCount == 2 &&
                    secondHung.CallCount == 2 && hung.MaxActive == 1 &&
                    secondHung.MaxActive == 1,
                "late timed-out completion is discarded and one pending refresh runs");
            Check(failures, Volatile.Read(ref hungConnectedEvents) == 1,
                "late timed-out success is not published before the pending retry");

            int beforeDisposeEvents = Volatile.Read(ref deliveredEvents);
            Stopwatch dispose = Stopwatch.StartNew();
            monitor.Dispose();
            dispose.Stop();
            Thread.Sleep(100);
            lifetime.Stop();
            Check(failures, dispose.ElapsedMilliseconds < 1000,
                "monitor disposal is bounded while native work is outstanding");
            Check(failures, Volatile.Read(ref deliveredEvents) == beforeDisposeEvents,
                "late provider completion does not publish after monitor disposal");
            Check(failures, lifetime.ElapsedMilliseconds < 5000,
                "monitor isolation self-test remains bounded");

            DeviceProfile diagnosticHungProfile = MonitorProfile(
                "diagnostics.hung", "test.diagnostics.hung", 0);
            DeviceProfile diagnosticFastProfile = MonitorProfile(
                "diagnostics.fast", "test.diagnostics.fast", 1);
            SynchronousBlockingProvider diagnosticHung = new SynchronousBlockingProvider(
                diagnosticHungProfile.ProviderId, diagnosticHungProfile);
            CountingProvider diagnosticFast = new CountingProvider(
                diagnosticFastProfile.ProviderId, diagnosticFastProfile);
            ProviderRegistry diagnosticRegistry = new ProviderRegistry();
            diagnosticRegistry.Register(diagnosticHung);
            diagnosticRegistry.Register(diagnosticFast);
            DiagnosticsService diagnosticService = new DiagnosticsService(
                new[] { diagnosticHungProfile, diagnosticFastProfile },
                diagnosticRegistry,
                new BatteryReadContext(new HidDeviceEnumerator()),
                100,
                0);
            Stopwatch diagnosticDuration = Stopwatch.StartNew();
            IList<BatteryReading> diagnosticReadings = diagnosticService
                .ReadOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            diagnosticDuration.Stop();
            Check(failures, diagnosticReadings.Count == 2 &&
                    diagnosticReadings[0].ErrorCode == "provider-watchdog-timeout" &&
                    diagnosticReadings[1].Connection == DeviceConnectionState.Connected &&
                    diagnosticFast.CallCount == 1,
                "one-shot diagnostics skips a hung provider and continues");
            Check(failures, diagnosticDuration.ElapsedMilliseconds < 1500,
                "one-shot diagnostics watchdog is bounded");

            ManualResetEventSlim enumerationRelease = new ManualResetEventSlim(false);
            DiagnosticsService textService = new DiagnosticsService(
                new[] { diagnosticFastProfile },
                diagnosticRegistry,
                new BatteryReadContext(new HidDeviceEnumerator()),
                100,
                0,
                () =>
                {
                    enumerationRelease.Wait();
                    return new HidEnumerationResult(
                        new List<HidDeviceDescriptor>(), new List<string>());
                });
            Stopwatch textDuration = Stopwatch.StartNew();
            string diagnosticText = textService.BuildText(new[]
            {
                ProviderSupport.Connected(diagnosticFastProfile, 50,
                    BatteryLevelBand.High, DeviceChargeState.Discharging,
                    "배터리 충분", "test", false)
            });
            textDuration.Stop();
            Check(failures,
                diagnosticText.Contains("enumeration timeout") &&
                textDuration.ElapsedMilliseconds < 1500,
                "diagnostics HID metadata enumeration watchdog is bounded");
            enumerationRelease.Set();
            enumerationRelease.Dispose();
            diagnosticHung.Release();
        }

        private static DeviceProfile MonitorProfile(string id, string providerId,
            int xinputSlot)
        {
            return new DeviceProfile
            {
                Id = id,
                DisplayName = id,
                Category = "gamepad",
                ProviderId = providerId,
                PollSeconds = 1,
                TimeoutMilliseconds = 250,
                Match = new DeviceMatch
                {
                    Transport = "xinput",
                    XInputUserIndex = xinputSlot
                }
            };
        }

        private static DeviceProfile WorkerProfile(string id, string providerId)
        {
            return new DeviceProfile
            {
                Id = id,
                DisplayName = id,
                Category = "keyboard",
                ProviderId = providerId,
                PollSeconds = 10,
                TimeoutMilliseconds = 250,
                Match = new DeviceMatch
                {
                    Transport = "hid",
                    VendorId = "1234",
                    ProductIds = new List<string> { "5678" },
                    InterfaceNumber = 1,
                    UsagePage = "0xFF00",
                    Usage = "0x0001"
                }
            };
        }

        private sealed class CountingProvider : IBatteryProvider
        {
            private readonly DeviceProfile _profile;
            private int _callCount;

            public string ProviderId { get; private set; }
            public int CallCount { get { return Volatile.Read(ref _callCount); } }

            public CountingProvider(string providerId, DeviceProfile profile)
            {
                ProviderId = providerId;
                _profile = profile;
            }

            public Task<BatteryReading> ReadAsync(DeviceProfile profile,
                BatteryReadContext context, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _callCount);
                return Task.FromResult(new BatteryReading
                {
                    ProfileId = _profile.Id,
                    DisplayName = _profile.DisplayName,
                    Category = _profile.Category,
                    Percent = 80,
                    Band = BatteryReading.BandFromPercent(80),
                    Connection = DeviceConnectionState.Connected,
                    Charge = DeviceChargeState.Discharging,
                    StatusText = "연결됨",
                    Presence = DevicePresenceState.Present
                });
            }
        }

        private sealed class NeverCompletingProvider : IBatteryProvider
        {
            private readonly DeviceProfile _profile;
            private readonly TaskCompletionSource<BatteryReading> _completion =
                new TaskCompletionSource<BatteryReading>();
            private int _callCount;
            private int _active;
            private int _maxActive;

            public string ProviderId { get; private set; }
            public int CallCount { get { return Volatile.Read(ref _callCount); } }
            public int MaxActive { get { return Volatile.Read(ref _maxActive); } }

            public NeverCompletingProvider(string providerId, DeviceProfile profile)
            {
                ProviderId = providerId;
                _profile = profile;
            }

            public async Task<BatteryReading> ReadAsync(DeviceProfile profile,
                BatteryReadContext context, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _callCount);
                int active = Interlocked.Increment(ref _active);
                UpdateMaximum(ref _maxActive, active);
                CancellationTokenRegistration registration =
                    cancellationToken.Register(() =>
                    {
                        throw new InvalidOperationException(
                            "self-test cancellation callback failure");
                    });
                try
                {
                    return await _completion.Task.ConfigureAwait(false);
                }
                finally
                {
                    registration.Dispose();
                    Interlocked.Decrement(ref _active);
                }
            }

            public void Release()
            {
                _completion.TrySetResult(new BatteryReading
                {
                    ProfileId = _profile.Id,
                    DisplayName = _profile.DisplayName,
                    Category = _profile.Category,
                    Percent = 75,
                    Band = BatteryReading.BandFromPercent(75),
                    Connection = DeviceConnectionState.Connected,
                    Presence = DevicePresenceState.Present
                });
            }

            private static void UpdateMaximum(ref int target, int candidate)
            {
                int current;
                do
                {
                    current = Volatile.Read(ref target);
                    if (candidate <= current)
                        return;
                }
                while (Interlocked.CompareExchange(ref target, candidate, current) != current);
            }
        }

        private sealed class StaleCacheProvider : IBatteryProvider
        {
            private readonly DeviceProfile _profile;

            public string ProviderId { get; private set; }

            public StaleCacheProvider(string providerId, DeviceProfile profile)
            {
                ProviderId = providerId;
                _profile = profile;
            }

            public Task<BatteryReading> ReadAsync(DeviceProfile profile,
                BatteryReadContext context, CancellationToken cancellationToken)
            {
                return Task.FromResult(new BatteryReading
                {
                    ProfileId = _profile.Id,
                    DisplayName = _profile.DisplayName,
                    Category = _profile.Category,
                    Percent = 66,
                    Band = BatteryReading.BandFromPercent(66),
                    Connection = DeviceConnectionState.Error,
                    StatusText = "새 값 조회 실패",
                    IsStale = true,
                    Presence = DevicePresenceState.Present
                });
            }
        }

        private sealed class SynchronousBlockingProvider : IBatteryProvider
        {
            private readonly DeviceProfile _profile;
            private readonly ManualResetEventSlim _release =
                new ManualResetEventSlim(false);
            private int _callCount;
            private int _active;
            private int _maxActive;

            public string ProviderId { get; private set; }
            public int CallCount { get { return Volatile.Read(ref _callCount); } }
            public int MaxActive { get { return Volatile.Read(ref _maxActive); } }

            public SynchronousBlockingProvider(string providerId,
                DeviceProfile profile)
            {
                ProviderId = providerId;
                _profile = profile;
            }

            public Task<BatteryReading> ReadAsync(DeviceProfile profile,
                BatteryReadContext context, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _callCount);
                int active = Interlocked.Increment(ref _active);
                UpdateMaximum(ref _maxActive, active);
                CancellationTokenRegistration registration =
                    cancellationToken.Register(() => _release.Wait());
                try
                {
                    _release.Wait();
                    return Task.FromResult(new BatteryReading
                    {
                        ProfileId = _profile.Id,
                        DisplayName = _profile.DisplayName,
                        Category = _profile.Category,
                        Percent = 70,
                        Band = BatteryReading.BandFromPercent(70),
                        Connection = DeviceConnectionState.Connected,
                        Presence = DevicePresenceState.Present
                    });
                }
                finally
                {
                    registration.Dispose();
                    Interlocked.Decrement(ref _active);
                }
            }

            public void Release()
            {
                _release.Set();
            }

            private static void UpdateMaximum(ref int target, int candidate)
            {
                int current;
                do
                {
                    current = Volatile.Read(ref target);
                    if (candidate <= current)
                        return;
                }
                while (Interlocked.CompareExchange(ref target, candidate, current) != current);
            }
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

                BatteryReading errorWithLastValue = TestReading(DeviceConnectionState.Error, 73);
                errorWithLastValue.IsStale = true;
                errorWithLastValue.StatusText = "조회 오류";
                object errorVisual = createVisual.Invoke(null,
                    new object[] { profile, errorWithLastValue });
                string errorKey = VisualProperty(errorVisual, "RenderKey");
                string errorToolTip = Convert.ToString(buildDeviceToolTip.Invoke(null,
                    new object[] { profile, errorWithLastValue }));
                Check(failures, VisualProperty(errorVisual, "Text") == "73" &&
                    errorKey.StartsWith("error|73|stale:True", StringComparison.Ordinal) &&
                    VisualColorArgb(errorVisual) ==
                        System.Drawing.Color.FromArgb(255, 255, 154, 169).ToArgb() &&
                    VisualBackgroundArgb(errorVisual) ==
                        System.Drawing.Color.FromArgb(255, 157, 37, 60).ToArgb() &&
                    errorToolTip.Contains("조회 오류 · 마지막 73%"),
                    "tray error keeps last percent on red background");

                object normalSamePercent = createVisual.Invoke(null,
                    new object[] { profile, TestReading(DeviceConnectionState.Connected, 73) });
                Check(failures, errorKey != VisualProperty(normalSamePercent, "RenderKey"),
                    "tray normal and error visuals use different render keys");

                BatteryReading errorWithoutValue = TestReading(DeviceConnectionState.Error, null);
                object errorWithoutValueVisual = createVisual.Invoke(null,
                    new object[] { profile, errorWithoutValue });
                Check(failures, VisualProperty(errorWithoutValueVisual, "Text") == "—" &&
                    VisualBackgroundArgb(errorWithoutValueVisual) ==
                        System.Drawing.Color.FromArgb(255, 157, 37, 60).ToArgb() &&
                    errorKey != VisualProperty(errorWithoutValueVisual, "RenderKey"),
                    "tray error without last percent uses red dash");

                object combinedErrorVisual = createCombinedVisual.Invoke(null,
                    new object[] { errorVisual });
                Check(failures, VisualProperty(combinedErrorVisual, "Text") == "73" &&
                    VisualProperty(combinedErrorVisual, "DeviceShape") == "combined" &&
                    VisualBackgroundArgb(combinedErrorVisual) ==
                        System.Drawing.Color.FromArgb(255, 157, 37, 60).ToArgb(),
                    "combined tray preserves red stale-error visual");

                object combinedErrorWithoutValueVisual = createCombinedVisual.Invoke(null,
                    new object[] { errorWithoutValueVisual });
                Check(failures,
                    VisualProperty(combinedErrorWithoutValueVisual, "Text") == "—" &&
                    VisualProperty(combinedErrorWithoutValueVisual, "DeviceShape") == "combined" &&
                    VisualBackgroundArgb(combinedErrorWithoutValueVisual) ==
                        System.Drawing.Color.FromArgb(255, 157, 37, 60).ToArgb(),
                    "combined tray preserves red error visual without cached value");

                BatteryReading offlineReading = TestReading(DeviceConnectionState.Disconnected, 73);
                object offlineVisual = createVisual.Invoke(null, new object[] { profile, offlineReading });
                string offlineKey = VisualProperty(offlineVisual, "RenderKey");
                Check(failures, VisualProperty(offlineVisual, "Text") == "—" &&
                    offlineKey.StartsWith("offline|", StringComparison.Ordinal) &&
                    offlineKey != unknownKey &&
                    VisualBackgroundArgb(offlineVisual) ==
                        System.Drawing.Color.FromArgb(255, 17, 27, 46).ToArgb(),
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

        private static int VisualBackgroundArgb(object visual)
        {
            if (visual == null)
                return 0;
            PropertyInfo property = visual.GetType().GetProperty("Background",
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
