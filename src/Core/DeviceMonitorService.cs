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
                    device.NextPollUtc = DateTime.UtcNow;
            }
        }

        private async Task RunLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                List<DeviceRuntime> due;
                lock (_devices)
                {
                    DateTime now = DateTime.UtcNow;
                    due = _devices.Where(d => d.NextPollUtc <= now).ToList();
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
                    if (!_providers.TryGet(runtime.Profile.ProviderId, out provider))
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

                    bool success = reading.Connection == DeviceConnectionState.Connected;
                    if (success)
                    {
                        runtime.FailureCount = 0;
                    }
                    else
                    {
                        runtime.FailureCount++;
                        if (runtime.LastReading != null && runtime.LastReading.Percent.HasValue)
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
                    int nextSeconds = success
                        ? normalSeconds
                        : Math.Min(300, normalSeconds * (int)Math.Pow(2, Math.Min(runtime.FailureCount, 4)));
                    lock (_devices)
                        runtime.NextPollUtc = DateTime.UtcNow.AddSeconds(nextSeconds);

                    EventHandler<BatteryReadingEventArgs> handler = ReadingUpdated;
                    if (handler != null)
                        handler(this, new BatteryReadingEventArgs(reading));
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
