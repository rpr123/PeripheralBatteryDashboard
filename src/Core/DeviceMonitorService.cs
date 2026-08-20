using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PeripheralBatteryDashboard.Core
{
    public sealed class BatteryReadingEventArgs : EventArgs
    {
        public BatteryReading Reading { get; private set; }

        public BatteryReadingEventArgs(BatteryReading reading)
        {
            Reading = reading;
        }
    }

    internal sealed class DeviceRuntime
    {
        public DeviceProfile Profile;
        public BatteryReading LastReading;
        public DevicePresenceState Presence;
        public DateTime NextPollUtc;
        public int FailureCount;
        public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
    }

    public sealed class DeviceMonitorService : IDisposable
    {
        private readonly ProviderRegistry _providers;
        private readonly BatteryReadContext _context;
        private readonly AppSettings _settings;
        private readonly List<DeviceRuntime> _devices;
        private readonly SemaphoreSlim _globalConcurrency = new SemaphoreSlim(2, 2);
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private DateTime _nextHidPresenceScanUtc = DateTime.MinValue;
        private Task _loopTask;
        private bool _disposed;

        public event EventHandler<BatteryReadingEventArgs> ReadingUpdated;

        public DeviceMonitorService(IEnumerable<DeviceProfile> profiles, ProviderRegistry providers,
            BatteryReadContext context, AppSettings settings)
        {
            _providers = providers;
            _context = context;
            _settings = settings;
            _devices = profiles.Select(p => new DeviceRuntime
            {
                Profile = p,
                Presence = DevicePresenceState.Unknown,
                NextPollUtc = DateTime.UtcNow,
                FailureCount = 0,
                LastReading = new BatteryReading
                {
                    ProfileId = p.Id,
                    DisplayName = p.DisplayName,
                    Category = p.Category,
                    StatusText = "확인 중",
                    DetailText = "첫 상태를 조회하고 있습니다."
                }
            }).ToList();
        }

        public IList<DeviceProfile> Profiles
        {
            get { return _devices.Select(d => d.Profile).ToList(); }
        }

        public IList<BatteryReading> Snapshot
        {
            get
            {
                lock (_devices)
                    return _devices.Select(d => d.LastReading).ToList();
            }
        }

        public void Start()
        {
            if (_loopTask != null)
                return;
            _loopTask = Task.Run(() => RunLoopAsync(_shutdown.Token));
        }

        public void RefreshAll()
        {
            lock (_devices)
            {
                _nextHidPresenceScanUtc = DateTime.MinValue;
                foreach (DeviceRuntime device in _devices)
                    device.NextPollUtc = DateTime.UtcNow;
            }
        }

        public void Refresh(string profileId)
        {
            lock (_devices)
            {
                DeviceRuntime device = _devices.FirstOrDefault(d => string.Equals(d.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
                if (device != null)
                {
                    device.NextPollUtc = DateTime.UtcNow;
                    if (IsHidProfile(device.Profile))
                        _nextHidPresenceScanUtc = DateTime.MinValue;
                }
            }
        }

        private async Task RunLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                IList<BatteryReading> presenceUpdates = RefreshHidPresenceIfDue();
                foreach (BatteryReading update in presenceUpdates)
                    RaiseReadingUpdated(update);

                List<DeviceRuntime> due;
                lock (_devices)
                {
                    DateTime now = DateTime.UtcNow;
                    due = _devices.Where(d => d.NextPollUtc <= now &&
                        (!IsHidProfile(d.Profile) ||
                         d.Presence == DevicePresenceState.Present)).ToList();
                    foreach (DeviceRuntime device in due)
                        device.NextPollUtc = now.AddMinutes(10);
                }

                if (due.Count > 0)
                {
                    Task[] tasks = due.Select(d => ReadOneAsync(d, token)).ToArray();
                    try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }

                try { await Task.Delay(500, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task ReadOneAsync(DeviceRuntime runtime, CancellationToken token)
        {
            if (!await runtime.Gate.WaitAsync(0).ConfigureAwait(false))
                return;
            try
            {
                await _globalConcurrency.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    IBatteryProvider provider;
                    BatteryReading reading;
                    if ((IsHidProfile(runtime.Profile) ||
                         ProviderSafetyPolicy.RequiresExactHidSelector(
                             runtime.Profile.ProviderId)) &&
                        !HasExactHidSelector(runtime.Profile))
                    {
                        reading = BatteryReading.Unavailable(runtime.Profile,
                            DeviceConnectionState.Unsupported,
                            "정확한 HID 선택자 필요",
                            "VID/PID, Usage Page/Usage와 MI 번호 또는 MI 없음의 명시가 필요합니다.",
                            "broad-hid-selector-blocked");
                    }
                    else if (!_providers.TryGet(runtime.Profile.ProviderId, out provider))
                    {
                        reading = BatteryReading.Unavailable(runtime.Profile,
                            DeviceConnectionState.Unsupported,
                            "지원 모듈 없음",
                            "Provider: " + runtime.Profile.ProviderId,
                            "provider-not-found");
                    }
                    else
                    {
                        using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            timeout.CancelAfter(Math.Max(5000, runtime.Profile.EffectiveTimeoutMilliseconds * 8));
                            try
                            {
                                reading = await provider.ReadAsync(runtime.Profile, _context, timeout.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                if (token.IsCancellationRequested)
                                    throw;
                                reading = BatteryReading.Unavailable(runtime.Profile,
                                    DeviceConnectionState.Sleeping,
                                    "조회 시간 초과",
                                    "장치가 절전 중일 수 있습니다.",
                                    "provider-timeout");
                            }
                            catch (Exception ex)
                            {
                                reading = BatteryReading.Unavailable(runtime.Profile,
                                    DeviceConnectionState.Error,
                                    "조회 오류",
                                    ex.Message,
                                    "provider-exception");
                            }
                        }
                    }

                    if (reading == null)
                    {
                        reading = BatteryReading.Unavailable(runtime.Profile,
                            DeviceConnectionState.Error,
                            "조회 오류",
                            "공급자가 상태를 반환하지 않았습니다.",
                            "provider-null-reading");
                    }

                    DevicePresenceState presence;
                    lock (_devices)
                    {
                        if (IsHidProfile(runtime.Profile))
                        {
                            presence = runtime.Presence;
                        }
                        else
                        {
                            presence = ResolveNonHidPresence(reading,
                                runtime.Presence);
                            runtime.Presence = presence;
                        }
                    }
                    reading.Presence = presence;

                    bool success = reading.Connection == DeviceConnectionState.Connected;
                    if (success)
                    {
                        runtime.FailureCount = 0;
                    }
                    else
                    {
                        runtime.FailureCount++;
                        if (presence == DevicePresenceState.Present &&
                            runtime.LastReading != null && runtime.LastReading.Percent.HasValue)
                        {
                            reading.Percent = runtime.LastReading.Percent;
                            reading.Band = runtime.LastReading.Band;
                            reading.IsStale = true;
                            reading.DetailText = reading.DetailText + " · 마지막 값 " + runtime.LastReading.Percent.Value + "%";
                        }
                    }

                    lock (_devices)
                        runtime.LastReading = reading;

                    int normalSeconds = _settings.PollSeconds > 0 ? _settings.PollSeconds : runtime.Profile.EffectivePollSeconds;
                    int nextSeconds = success || presence != DevicePresenceState.Present
                        ? normalSeconds
                        : Math.Min(300, normalSeconds * (int)Math.Pow(2, Math.Min(runtime.FailureCount, 4)));
                    lock (_devices)
                        runtime.NextPollUtc = DateTime.UtcNow.AddSeconds(nextSeconds);

                    RaiseReadingUpdated(reading);
                }
                finally
                {
                    _globalConcurrency.Release();
                }
            }
            finally
            {
                runtime.Gate.Release();
            }
        }

        private IList<BatteryReading> RefreshHidPresenceIfDue()
        {
            DateTime now = DateTime.UtcNow;
            List<DeviceRuntime> hidDevices;
            int intervalSeconds;
            lock (_devices)
            {
                if (now < _nextHidPresenceScanUtc)
                    return new List<BatteryReading>();

                hidDevices = _devices.Where(d => IsHidProfile(d.Profile)).ToList();
                intervalSeconds = Math.Max(10, _settings.PollSeconds > 0
                    ? _settings.PollSeconds
                    : 30);
                _nextHidPresenceScanUtc = now.AddSeconds(intervalSeconds);
            }

            if (hidDevices.Count == 0)
                return new List<BatteryReading>();

            Hardware.HidEnumerationResult scan;
            try
            {
                scan = _context.HidDevices.EnumerateMetadata();
            }
            catch
            {
                return new List<BatteryReading>();
            }

            List<BatteryReading> updates = new List<BatteryReading>();
            lock (_devices)
            {
                foreach (DeviceRuntime runtime in hidDevices)
                {
                    DevicePresenceState next = ResolveHidPresence(runtime.Profile,
                        scan, runtime.Presence);
                    if (next == runtime.Presence)
                        continue;

                    runtime.Presence = next;
                    if (next == DevicePresenceState.Present)
                    {
                        runtime.NextPollUtc = now;
                        continue;
                    }

                    if (next == DevicePresenceState.Absent)
                    {
                        runtime.FailureCount = 0;
                        BatteryReading absent = BatteryReading.Unavailable(runtime.Profile,
                            DeviceConnectionState.Disconnected,
                            "현재 장치 없음",
                            "이 PC에서 정확히 일치하는 HID 컬렉션이 감지되지 않았습니다.",
                            "hardware-not-present");
                        absent.Presence = DevicePresenceState.Absent;
                        runtime.LastReading = absent;
                        updates.Add(absent);
                    }
                }
            }
            return updates;
        }

        internal static bool IsHidProfile(DeviceProfile profile)
        {
            if (profile == null || profile.Match == null)
                return false;
            string transport = profile.Match.Transport;
            return string.IsNullOrWhiteSpace(transport) ||
                   string.Equals(transport, "hid", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool HasExactHidSelector(DeviceProfile profile)
        {
            return IsHidProfile(profile) && profile.Match.ParsedVendorId.HasValue &&
                   profile.Match.ParsedProductIds.Count > 0 &&
                   (profile.Match.InterfaceNumber.HasValue ||
                    profile.Match.RequireNoInterfaceNumber) &&
                   profile.Match.ParsedUsagePage.HasValue &&
                   profile.Match.ParsedUsage.HasValue;
        }

        internal static DevicePresenceState ResolveHidPresence(DeviceProfile profile,
            Hardware.HidEnumerationResult scan, DevicePresenceState previous)
        {
            if (profile == null || scan == null)
                return previous;
            if (scan.Devices.Any(descriptor => descriptor.Matches(profile)))
                return DevicePresenceState.Present;

            bool sameVidPid = scan.Devices.Any(descriptor =>
                profile.Match != null && profile.Match.ParsedVendorId.HasValue &&
                descriptor.VendorId == profile.Match.ParsedVendorId.Value &&
                profile.Match.ParsedProductIds.Contains(descriptor.ProductId));
            if (sameVidPid)
                return previous;

            bool globalEnumerationFailure = scan.WarningCodes.Any(code =>
                string.Equals(code, "hid-device-set-open-failed",
                    StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(code, "hid-interface-enumeration-failed",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "hid-interface-detail-size-failed",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "hid-interface-detail-failed",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "hid-interface-path-empty",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "hid-descriptor-read-failed",
                    StringComparison.OrdinalIgnoreCase));
            return globalEnumerationFailure
                ? previous
                : DevicePresenceState.Absent;
        }

        internal static DevicePresenceState InferNonHidPresence(
            DeviceConnectionState connection, DevicePresenceState previous)
        {
            if (connection == DeviceConnectionState.Connected)
                return DevicePresenceState.Present;
            if (connection == DeviceConnectionState.Disconnected)
                return DevicePresenceState.Absent;
            if (connection == DeviceConnectionState.Sleeping ||
                connection == DeviceConnectionState.Busy ||
                connection == DeviceConnectionState.Error)
            {
                return previous == DevicePresenceState.Present
                    ? DevicePresenceState.Present
                    : DevicePresenceState.Unknown;
            }
            return previous;
        }

        internal static DevicePresenceState ResolveNonHidPresence(
            BatteryReading reading, DevicePresenceState previous)
        {
            if (reading != null && reading.Presence != DevicePresenceState.Unknown)
                return reading.Presence;
            return reading == null
                ? previous
                : InferNonHidPresence(reading.Connection, previous);
        }

        private void RaiseReadingUpdated(BatteryReading reading)
        {
            EventHandler<BatteryReadingEventArgs> handler = ReadingUpdated;
            if (handler != null)
                handler(this, new BatteryReadingEventArgs(reading));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _shutdown.Cancel();
            try { if (_loopTask != null) _loopTask.Wait(2000); }
            catch { }
            _shutdown.Dispose();
            _globalConcurrency.Dispose();
            foreach (DeviceRuntime device in _devices)
                device.Gate.Dispose();
        }
    }
}
