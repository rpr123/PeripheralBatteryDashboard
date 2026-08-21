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

    public sealed class DeviceMonitorHealth
    {
        public DateTime LastHeartbeatUtc { get; internal set; }
        public int LoopErrorCount { get; internal set; }
        public int SubscriberErrorCount { get; internal set; }
        public int WatchdogTimeoutCount { get; internal set; }
        public int ActiveReadCount { get; internal set; }
        public int TimedOutNativeCallCount { get; internal set; }
        public bool PresenceScanInFlight { get; internal set; }
        public bool PresenceScanTimedOut { get; internal set; }
        public string LastErrorCode { get; internal set; }

        public DeviceMonitorHealth()
        {
            LastErrorCode = string.Empty;
        }
    }

    internal sealed class DeviceRuntime
    {
        public DeviceProfile Profile;
        public BatteryReading LastReading;
        public LastSuccessfulValueSnapshot LastSuccessfulValue;
        public DevicePresenceState Presence;
        public DateTime NextPollUtc;
        public DateTime AttemptStartedUtc;
        public DateTime AttemptDeadlineUtc;
        public DateTime LastCompletedUtc;
        public int FailureCount;
        public long AttemptId;
        public bool ReadInFlight;
        public bool WatchdogReported;
        public bool ResponsiveLeaseHeld;
        public bool RefreshPending;
        public CancellationTokenSource AttemptCancellation;
        public List<string> ActiveIoKeys = new List<string>();
    }

    internal sealed class LastSuccessfulValueSnapshot
    {
        public int? Percent { get; }
        public BatteryLevelBand Band { get; }
        public bool IsApproximate { get; }
        public DeviceChargeState Charge { get; }
        public DateTime SuccessfulAtUtc { get; }

        public LastSuccessfulValueSnapshot(int? percent, BatteryLevelBand band,
            bool isApproximate, DeviceChargeState charge, DateTime successfulAtUtc)
        {
            Percent = percent;
            Band = band;
            IsApproximate = isApproximate;
            Charge = charge;
            SuccessfulAtUtc = successfulAtUtc;
        }
    }

    internal sealed class ReadingSubscriberRuntime
    {
        public readonly Queue<BatteryReading> Pending = new Queue<BatteryReading>();
        public bool WorkerRunning;
    }

    public sealed class DeviceMonitorService : IDisposable
    {
        private const int DefaultLoopDelayMilliseconds = 500;
        private const int DefaultMinimumWatchdogMilliseconds = 5000;
        private const int DefaultTimeoutMultiplier = 8;
        private const int DefaultPresenceWatchdogMilliseconds = 10000;
        private const int MaxResponsiveReads = 2;
        private const int MaxPendingReadingsPerSubscriber = 64;

        private readonly ProviderRegistry _providers;
        private readonly BatteryReadContext _context;
        private readonly AppSettings _settings;
        private readonly IProviderReadExecutor _providerReadExecutor;
        private readonly List<DeviceRuntime> _devices;
        private readonly HashSet<string> _activeIoKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Delegate, ReadingSubscriberRuntime>
            _subscriberRuntimes =
                new Dictionary<Delegate, ReadingSubscriberRuntime>();
        private readonly Dictionary<CancellationTokenSource, Task>
            _cancellationRequests =
                new Dictionary<CancellationTokenSource, Task>();
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly object _loopStartLock = new object();
        private readonly int _loopDelayMilliseconds;
        private readonly int _minimumWatchdogMilliseconds;
        private readonly int _timeoutMultiplier;
        private readonly int _presenceWatchdogMilliseconds;

        private DateTime _nextHidPresenceScanUtc = DateTime.MinValue;
        private DateTime _lastHeartbeatUtc = DateTime.MinValue;
        private DateTime _presenceScanStartedUtc = DateTime.MinValue;
        private DateTime _presenceScanDeadlineUtc = DateTime.MinValue;
        private Task _loopTask;
        private bool _presenceScanInFlight;
        private bool _presenceScanTimedOut;
        private bool _disposed;
        private long _nextAttemptId;
        private long _presenceAttemptId;
        private int _responsiveInFlight;
        private int _loopErrorCount;
        private int _subscriberErrorCount;
        private int _watchdogTimeoutCount;
        private string _lastErrorCode = string.Empty;

        public event EventHandler<BatteryReadingEventArgs> ReadingUpdated;

        public DeviceMonitorService(IEnumerable<DeviceProfile> profiles,
            ProviderRegistry providers, BatteryReadContext context, AppSettings settings)
            : this(profiles, providers, context, settings, null,
                DefaultLoopDelayMilliseconds,
                DefaultMinimumWatchdogMilliseconds,
                DefaultTimeoutMultiplier,
                DefaultPresenceWatchdogMilliseconds)
        {
        }

        public DeviceMonitorService(IEnumerable<DeviceProfile> profiles,
            ProviderRegistry providers, BatteryReadContext context, AppSettings settings,
            IProviderReadExecutor providerReadExecutor)
            : this(profiles, providers, context, settings, providerReadExecutor,
                DefaultLoopDelayMilliseconds,
                DefaultMinimumWatchdogMilliseconds,
                DefaultTimeoutMultiplier,
                DefaultPresenceWatchdogMilliseconds)
        {
        }

        internal DeviceMonitorService(IEnumerable<DeviceProfile> profiles,
            ProviderRegistry providers, BatteryReadContext context, AppSettings settings,
            int loopDelayMilliseconds, int minimumWatchdogMilliseconds,
            int timeoutMultiplier, int presenceWatchdogMilliseconds)
            : this(profiles, providers, context, settings, null,
                loopDelayMilliseconds, minimumWatchdogMilliseconds,
                timeoutMultiplier, presenceWatchdogMilliseconds)
        {
        }

        internal DeviceMonitorService(IEnumerable<DeviceProfile> profiles,
            ProviderRegistry providers, BatteryReadContext context, AppSettings settings,
            IProviderReadExecutor providerReadExecutor,
            int loopDelayMilliseconds, int minimumWatchdogMilliseconds,
            int timeoutMultiplier, int presenceWatchdogMilliseconds)
        {
            _providers = providers;
            _context = context;
            _settings = settings;
            _providerReadExecutor = providerReadExecutor;
            _loopDelayMilliseconds = Math.Max(10, loopDelayMilliseconds);
            _minimumWatchdogMilliseconds = Math.Max(50, minimumWatchdogMilliseconds);
            _timeoutMultiplier = Math.Max(0, timeoutMultiplier);
            _presenceWatchdogMilliseconds = Math.Max(50,
                presenceWatchdogMilliseconds);
            _devices = (profiles ?? Enumerable.Empty<DeviceProfile>()).Select(p =>
                new DeviceRuntime
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
                        Presence = DevicePresenceState.Unknown,
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

        public DeviceMonitorHealth Health
        {
            get
            {
                lock (_devices)
                {
                    return new DeviceMonitorHealth
                    {
                        LastHeartbeatUtc = _lastHeartbeatUtc,
                        LoopErrorCount = _loopErrorCount,
                        SubscriberErrorCount = _subscriberErrorCount,
                        WatchdogTimeoutCount = _watchdogTimeoutCount,
                        ActiveReadCount = _devices.Count(d => d.ReadInFlight),
                        TimedOutNativeCallCount = _devices.Count(d =>
                            d.ReadInFlight && d.WatchdogReported),
                        PresenceScanInFlight = _presenceScanInFlight,
                        PresenceScanTimedOut = _presenceScanTimedOut,
                        LastErrorCode = _lastErrorCode
                    };
                }
            }
        }

        public void Start()
        {
            EnsureLoopStarted();
        }

        public void RefreshAll()
        {
            List<CancellationTokenSource> cancellations =
                new List<CancellationTokenSource>();
            lock (_devices)
            {
                if (_disposed)
                    return;
                _nextHidPresenceScanUtc = DateTime.MinValue;
                foreach (DeviceRuntime device in _devices)
                {
                    if (device.ReadInFlight)
                    {
                        device.RefreshPending = true;
                        if (device.AttemptCancellation != null)
                            cancellations.Add(device.AttemptCancellation);
                    }
                    else
                    {
                        device.NextPollUtc = DateTime.UtcNow;
                    }
                }
            }
            CancelAttempts(cancellations);
            EnsureLoopStarted();
        }

        public void Refresh(string profileId)
        {
            CancellationTokenSource cancellation = null;
            lock (_devices)
            {
                if (_disposed)
                    return;
                DeviceRuntime device = _devices.FirstOrDefault(d =>
                    string.Equals(d.Profile.Id, profileId,
                        StringComparison.OrdinalIgnoreCase));
                if (device != null)
                {
                    if (device.ReadInFlight)
                    {
                        device.RefreshPending = true;
                        cancellation = device.AttemptCancellation;
                    }
                    else
                    {
                        device.NextPollUtc = DateTime.UtcNow;
                    }
                    if (IsHidProfile(device.Profile))
                        _nextHidPresenceScanUtc = DateTime.MinValue;
                }
            }
            CancelAttempt(cancellation);
            EnsureLoopStarted();
        }

        private void EnsureLoopStarted()
        {
            lock (_loopStartLock)
            {
                if (_disposed)
                    return;
                if (_loopTask != null && !_loopTask.IsCompleted)
                    return;
                _loopTask = Task.Run(() => RunLoopAsync(_shutdown.Token));
            }
        }

        private async Task RunLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    DateTime now = DateTime.UtcNow;
                    lock (_devices)
                        _lastHeartbeatUtc = now;
                    TickPresenceScan(now);
                    TickReadWatchdogs(now);
                    StartDueReads(now);
                }
                catch (Exception ex)
                {
                    RecordLoopError("monitor-loop-exception", ex);
                }

                try
                {
                    await Task.Delay(_loopDelayMilliseconds, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void TickPresenceScan(DateTime now)
        {
            long attemptId;
            lock (_devices)
            {
                if (_disposed)
                    return;
                if (_presenceScanInFlight)
                {
                    if (!_presenceScanTimedOut && now >= _presenceScanDeadlineUtc)
                    {
                        _presenceScanTimedOut = true;
                        _lastErrorCode = "presence-scan-watchdog-timeout";
                        _watchdogTimeoutCount++;
                    }
                    return;
                }
                if (now < _nextHidPresenceScanUtc ||
                    !_devices.Any(d => IsHidProfile(d.Profile)))
                    return;

                _nextHidPresenceScanUtc = now.AddSeconds(PresenceIntervalSeconds());
                _presenceScanInFlight = true;
                _presenceScanTimedOut = false;
                _presenceScanStartedUtc = now;
                _presenceScanDeadlineUtc = now.AddMilliseconds(
                    _presenceWatchdogMilliseconds);
                attemptId = ++_presenceAttemptId;
            }

            try
            {
                Task<Hardware.HidEnumerationResult> task = Task.Factory.StartNew(
                    () => _context.HidDevices.EnumerateMetadata(),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);
                task.ContinueWith(completed => CompletePresenceScan(attemptId, completed),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                lock (_devices)
                {
                    if (_presenceAttemptId == attemptId)
                        _presenceScanInFlight = false;
                }
                RecordLoopError("presence-scan-start-failed", ex);
            }
        }

        private void CompletePresenceScan(long attemptId,
            Task<Hardware.HidEnumerationResult> task)
        {
            try
            {
                lock (_devices)
                {
                    if (attemptId != _presenceAttemptId)
                        return;
                    _presenceScanInFlight = false;
                    _presenceScanTimedOut = false;
                    // No newer presence scan can start while this one is in flight.
                    // A result that arrives after the watchdog is therefore still
                    // current and lets a slow (but finite) cold-start scan recover.
                    if (_disposed || task.Status != TaskStatus.RanToCompletion ||
                        task.Result == null)
                    {
                        if (task.IsFaulted)
                        {
                            _loopErrorCount++;
                            _lastErrorCode = "presence-scan-failed";
                            var ignored = task.Exception;
                        }
                        return;
                    }

                    DateTime now = DateTime.UtcNow;
                    foreach (DeviceRuntime runtime in _devices.Where(d =>
                        IsHidProfile(d.Profile)))
                    {
                        DevicePresenceState next = ResolveHidPresence(runtime.Profile,
                            task.Result, runtime.Presence);
                        if (next == runtime.Presence)
                            continue;

                        runtime.Presence = next;
                        if (next == DevicePresenceState.Present)
                        {
                            if (!runtime.ReadInFlight)
                                runtime.NextPollUtc = now;
                            continue;
                        }
                        if (next != DevicePresenceState.Absent)
                            continue;

                        runtime.FailureCount = 0;
                        BatteryReading absent = BatteryReading.Unavailable(runtime.Profile,
                            DeviceConnectionState.Disconnected,
                            "현재 장치 없음",
                            "이 PC에서 정확히 일치하는 HID 컬렉션이 감지되지 않았습니다.",
                            "hardware-not-present");
                        runtime.LastSuccessfulValue = ApplyReadingHistory(
                            runtime.LastSuccessfulValue, absent,
                            DevicePresenceState.Absent, now);
                        runtime.LastReading = absent;
                        EnqueueReadingUpdated(absent);
                    }
                }
            }
            catch (Exception ex)
            {
                RecordLoopError("presence-scan-completion-failed", ex);
            }
        }

        private void TickReadWatchdogs(DateTime now)
        {
            List<CancellationTokenSource> cancellations =
                new List<CancellationTokenSource>();
            lock (_devices)
            {
                foreach (DeviceRuntime runtime in _devices)
                {
                    if (!runtime.ReadInFlight || runtime.WatchdogReported ||
                        now < runtime.AttemptDeadlineUtc)
                        continue;

                    runtime.WatchdogReported = true;
                    if (runtime.ResponsiveLeaseHeld)
                    {
                        runtime.ResponsiveLeaseHeld = false;
                        _responsiveInFlight = Math.Max(0, _responsiveInFlight - 1);
                    }
                    _watchdogTimeoutCount++;
                    _lastErrorCode = "provider-watchdog-timeout";
                    if (runtime.AttemptCancellation != null)
                        cancellations.Add(runtime.AttemptCancellation);

                    BatteryReading timeout = BatteryReading.Unavailable(runtime.Profile,
                        DeviceConnectionState.Error,
                        "조회 시간 초과",
                        "장치 또는 Windows 드라이버 응답이 지연되고 있습니다. 다른 장치 조회는 계속됩니다.",
                        "provider-watchdog-timeout");
                    EnqueueReadingUpdated(StoreReadingLocked(runtime, timeout, now));
                }
            }
            CancelAttempts(cancellations);
        }

        private void StartDueReads(DateTime now)
        {
            List<DeviceRuntime> launches = new List<DeviceRuntime>();
            lock (_devices)
            {
                if (_disposed)
                    return;
                foreach (DeviceRuntime runtime in _devices)
                {
                    if (_responsiveInFlight >= MaxResponsiveReads)
                        break;
                    bool workerProbeAfterInconclusivePresence =
                        CanProbeUnknownHid(runtime.Profile, runtime.Presence,
                            _providerReadExecutor != null, _presenceScanInFlight,
                            _presenceScanTimedOut);
                    if (runtime.ReadInFlight || runtime.NextPollUtc > now ||
                        (IsHidProfile(runtime.Profile) &&
                         runtime.Presence != DevicePresenceState.Present &&
                         !workerProbeAfterInconclusivePresence))
                        continue;

                    List<string> ioKeys = BuildIoKeys(runtime.Profile);
                    if (ioKeys.Any(key => _activeIoKeys.Contains(key)))
                        continue;
                    foreach (string key in ioKeys)
                        _activeIoKeys.Add(key);

                    int watchdogMilliseconds = WatchdogMilliseconds(runtime.Profile);
                    runtime.ReadInFlight = true;
                    runtime.WatchdogReported = false;
                    runtime.ResponsiveLeaseHeld = true;
                    runtime.AttemptId = ++_nextAttemptId;
                    runtime.AttemptStartedUtc = now;
                    runtime.AttemptDeadlineUtc = now.AddMilliseconds(
                        watchdogMilliseconds);
                    runtime.NextPollUtc = now.AddMinutes(10);
                    runtime.ActiveIoKeys = ioKeys;
                    runtime.AttemptCancellation = new CancellationTokenSource();
                    _responsiveInFlight++;
                    launches.Add(runtime);
                }
            }

            foreach (DeviceRuntime runtime in launches)
                LaunchReadAttempt(runtime);
        }

        private void LaunchReadAttempt(DeviceRuntime runtime)
        {
            long attemptId = 0;
            Task<BatteryReading> task = null;
            CancellationTokenSource abandonedCancellation = null;
            Task abandonedCancellationRequest = null;
            lock (_devices)
            {
                if (_disposed || !runtime.ReadInFlight)
                {
                    if (runtime.ResponsiveLeaseHeld)
                    {
                        runtime.ResponsiveLeaseHeld = false;
                        _responsiveInFlight = Math.Max(0, _responsiveInFlight - 1);
                    }
                    foreach (string key in runtime.ActiveIoKeys)
                        _activeIoKeys.Remove(key);
                    runtime.ActiveIoKeys.Clear();
                    runtime.ReadInFlight = false;
                    abandonedCancellation = runtime.AttemptCancellation;
                    if (abandonedCancellation != null)
                        _cancellationRequests.TryGetValue(abandonedCancellation,
                            out abandonedCancellationRequest);
                    runtime.AttemptCancellation = null;
                }
                else
                {
                    attemptId = runtime.AttemptId;
                    CancellationToken token = runtime.AttemptCancellation.Token;
                    try
                    {
                        task = Task.Factory.StartNew(
                            () => ReadProviderAsync(runtime.Profile, token),
                            CancellationToken.None,
                            TaskCreationOptions.DenyChildAttach,
                            TaskScheduler.Default).Unwrap();
                    }
                    catch (Exception ex)
                    {
                        TaskCompletionSource<BatteryReading> failed =
                            new TaskCompletionSource<BatteryReading>();
                        failed.SetException(ex);
                        task = failed.Task;
                    }
                }
            }

            if (abandonedCancellation != null)
            {
                DisposeCancellationWhenSafe(abandonedCancellation,
                    abandonedCancellationRequest);
                return;
            }
            if (task == null)
                return;
            task.ContinueWith(completed =>
                    CompleteReadAttempt(runtime, attemptId, completed),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task<BatteryReading> ReadProviderAsync(DeviceProfile profile,
            CancellationToken token)
        {
            IBatteryProvider provider;
            if ((IsHidProfile(profile) ||
                 ProviderSafetyPolicy.RequiresExactHidSelector(profile.ProviderId)) &&
                !HasExactHidSelector(profile))
            {
                return BatteryReading.Unavailable(profile,
                    DeviceConnectionState.Unsupported,
                    "정확한 HID 선택자 필요",
                    "VID/PID, Usage Page/Usage와 MI 번호 또는 MI 없음의 명시가 필요합니다.",
                    "broad-hid-selector-blocked");
            }
            if (!_providers.TryGet(profile.ProviderId, out provider))
            {
                return BatteryReading.Unavailable(profile,
                    DeviceConnectionState.Unsupported,
                    "지원 모듈 없음",
                    "Provider: " + profile.ProviderId,
                    "provider-not-found");
            }

            try
            {
                bool isolateInWorker = _providerReadExecutor != null &&
                    (IsHidProfile(profile) ||
                     ProviderSafetyPolicy.RequiresExactHidSelector(profile.ProviderId));
                BatteryReading reading = isolateInWorker
                    ? await _providerReadExecutor.ReadAsync(profile, token)
                        .ConfigureAwait(false)
                    : await provider.ReadAsync(profile, _context, token)
                        .ConfigureAwait(false);
                return reading ?? BatteryReading.Unavailable(profile,
                    DeviceConnectionState.Error,
                    "조회 오류",
                    "공급자가 상태를 반환하지 않았습니다.",
                    "provider-null-reading");
            }
            catch (OperationCanceledException)
            {
                return BatteryReading.Unavailable(profile,
                    DeviceConnectionState.Sleeping,
                    "조회 취소됨",
                    "새 요청 또는 종료로 진행 중인 조회를 취소했습니다.",
                    "provider-cancelled");
            }
            catch (Exception ex)
            {
                return BatteryReading.Unavailable(profile,
                    DeviceConnectionState.Error,
                    "조회 오류",
                    ex.Message,
                    "provider-exception");
            }
        }

        private void CompleteReadAttempt(DeviceRuntime runtime, long attemptId,
            Task<BatteryReading> task)
        {
            CancellationTokenSource cancellation = null;
            Task cancellationRequest = null;
            try
            {
                lock (_devices)
                {
                    if (!runtime.ReadInFlight || runtime.AttemptId != attemptId)
                    {
                        if (task.IsFaulted)
                        {
                            var ignored = task.Exception;
                        }
                        return;
                    }

                    bool timedOut = runtime.WatchdogReported;
                    if (runtime.ResponsiveLeaseHeld)
                    {
                        runtime.ResponsiveLeaseHeld = false;
                        _responsiveInFlight = Math.Max(0, _responsiveInFlight - 1);
                    }
                    foreach (string key in runtime.ActiveIoKeys)
                        _activeIoKeys.Remove(key);
                    runtime.ActiveIoKeys.Clear();
                    runtime.ReadInFlight = false;
                    DateTime completedUtc = DateTime.UtcNow;
                    runtime.LastCompletedUtc = completedUtc;
                    cancellation = runtime.AttemptCancellation;
                    if (cancellation != null)
                        _cancellationRequests.TryGetValue(cancellation,
                            out cancellationRequest);
                    runtime.AttemptCancellation = null;

                    if (!_disposed && !timedOut)
                    {
                        BatteryReading reading = ReadingFromCompletedTask(runtime.Profile,
                            task);
                        EnqueueReadingUpdated(StoreReadingLocked(runtime, reading,
                            completedUtc));
                    }
                    else if (task.IsFaulted)
                    {
                        var ignored = task.Exception;
                    }

                    if (!_disposed && runtime.RefreshPending)
                    {
                        runtime.RefreshPending = false;
                        runtime.NextPollUtc = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception ex)
            {
                RecordLoopError("provider-completion-failed", ex);
            }
            finally
            {
                if (cancellation != null)
                    DisposeCancellationWhenSafe(cancellation, cancellationRequest);
            }
        }

        private static BatteryReading ReadingFromCompletedTask(DeviceProfile profile,
            Task<BatteryReading> task)
        {
            if (task.Status == TaskStatus.RanToCompletion)
            {
                return task.Result ?? BatteryReading.Unavailable(profile,
                    DeviceConnectionState.Error,
                    "조회 오류",
                    "공급자가 상태를 반환하지 않았습니다.",
                    "provider-null-reading");
            }
            if (task.IsCanceled)
            {
                return BatteryReading.Unavailable(profile,
                    DeviceConnectionState.Sleeping,
                    "조회 취소됨",
                    "진행 중인 조회가 취소되었습니다.",
                    "provider-cancelled");
            }
            string detail = task.Exception == null
                ? "알 수 없는 공급자 오류"
                : task.Exception.GetBaseException().Message;
            var ignored = task.Exception;
            return BatteryReading.Unavailable(profile,
                DeviceConnectionState.Error,
                "조회 오류",
                detail,
                "provider-task-fault");
        }

        private BatteryReading StoreReadingLocked(DeviceRuntime runtime,
            BatteryReading reading, DateTime observedUtc)
        {
            DevicePresenceState presence;
            if (IsHidProfile(runtime.Profile))
            {
                presence = runtime.Presence;
                if (presence == DevicePresenceState.Unknown &&
                    reading.Connection == DeviceConnectionState.Connected)
                {
                    presence = DevicePresenceState.Present;
                    runtime.Presence = presence;
                }
            }
            else
            {
                presence = ResolveNonHidPresence(reading, runtime.Presence);
                runtime.Presence = presence;
            }
            runtime.LastSuccessfulValue = ApplyReadingHistory(
                runtime.LastSuccessfulValue, reading, presence, observedUtc);

            bool connected = reading.Connection == DeviceConnectionState.Connected;

            if (connected)
            {
                runtime.FailureCount = 0;
            }
            else
            {
                runtime.FailureCount++;
            }

            runtime.LastReading = reading;
            int normalSeconds = _settings.PollSeconds > 0
                ? _settings.PollSeconds
                : runtime.Profile.EffectivePollSeconds;
            int nextSeconds = connected || presence != DevicePresenceState.Present
                ? normalSeconds
                : Math.Min(300, normalSeconds *
                    (int)Math.Pow(2, Math.Min(runtime.FailureCount, 4)));
            runtime.NextPollUtc = DateTime.UtcNow.AddSeconds(nextSeconds);
            return reading;
        }

        internal static LastSuccessfulValueSnapshot ApplyReadingHistory(
            LastSuccessfulValueSnapshot priorSnapshot,
            BatteryReading reading,
            DevicePresenceState presence,
            DateTime observedUtc)
        {
            if (reading == null)
                return priorSnapshot;

            reading.Presence = presence;
            reading.LastAttemptAtUtc = observedUtc;

            bool connected = reading.Connection == DeviceConnectionState.Connected;
            bool hasUsableValue = HasUsableBatteryValue(reading);
            bool hasAnyValue = reading.Percent.HasValue ||
                reading.Band != BatteryLevelBand.Unknown;
            bool freshSuccess = presence == DevicePresenceState.Present &&
                connected && !reading.IsStale && hasUsableValue;
            if (freshSuccess)
            {
                priorSnapshot = new LastSuccessfulValueSnapshot(
                    reading.Percent,
                    reading.Band,
                    reading.IsApproximate,
                    reading.Charge,
                    observedUtc);
                reading.LastSuccessfulAtUtc = observedUtc;
                reading.IsStale = false;
            }
            else if (presence == DevicePresenceState.Absent)
            {
                priorSnapshot = null;
                ClearBatteryValue(reading);
                reading.LastSuccessfulAtUtc = null;
            }
            else if (presence == DevicePresenceState.Present &&
                CanReuseLastSuccessfulValue(reading.Connection) &&
                priorSnapshot != null)
            {
                ApplyLastSuccessfulValue(reading, priorSnapshot);
            }
            else
            {
                if (reading.IsStale || hasAnyValue)
                {
                    ClearBatteryValue(reading);
                    AppendDetail(reading,
                        "확인된 앱 내 성공 값이 없어 캐시 잔량을 표시하지 않습니다.");
                }
                reading.LastSuccessfulAtUtc = null;
            }

            return priorSnapshot;
        }

        private static bool HasUsableBatteryValue(BatteryReading reading)
        {
            if (reading == null)
                return false;
            if (reading.Percent.HasValue)
                return reading.Percent.Value >= 0 && reading.Percent.Value <= 100;
            return reading.Band != BatteryLevelBand.Unknown;
        }

        private static bool CanReuseLastSuccessfulValue(
            DeviceConnectionState connection)
        {
            return connection == DeviceConnectionState.Connected ||
                connection == DeviceConnectionState.Error ||
                connection == DeviceConnectionState.Sleeping ||
                connection == DeviceConnectionState.Busy ||
                connection == DeviceConnectionState.Disconnected;
        }

        private static void ApplyLastSuccessfulValue(BatteryReading reading,
            LastSuccessfulValueSnapshot snapshot)
        {
            reading.Percent = snapshot.Percent;
            reading.Band = snapshot.Band;
            reading.IsApproximate = snapshot.IsApproximate;
            reading.Charge = snapshot.Charge;
            reading.IsStale = true;
            reading.LastSuccessfulAtUtc = snapshot.SuccessfulAtUtc;
            string detail = snapshot.Percent.HasValue
                ? "마지막 성공 값 " + snapshot.Percent.Value + "%"
                : "마지막 성공 잔량 단계";
            AppendDetail(reading, detail);
        }

        private static void ClearBatteryValue(BatteryReading reading)
        {
            reading.Percent = null;
            reading.Band = BatteryLevelBand.Unknown;
            reading.IsApproximate = false;
            reading.Charge = DeviceChargeState.Unknown;
            reading.IsStale = false;
        }

        private static void AppendDetail(BatteryReading reading, string suffix)
        {
            if (reading == null || string.IsNullOrWhiteSpace(suffix))
                return;
            string detail = reading.DetailText ?? string.Empty;
            if (detail.IndexOf(suffix, StringComparison.Ordinal) >= 0)
                return;
            reading.DetailText = detail +
                (detail.Length == 0 ? string.Empty : " · ") + suffix;
        }

        private int WatchdogMilliseconds(DeviceProfile profile)
        {
            int scaled = _timeoutMultiplier == 0
                ? 0
                : profile.EffectiveTimeoutMilliseconds * _timeoutMultiplier;
            return Math.Max(_minimumWatchdogMilliseconds, scaled);
        }

        private int PresenceIntervalSeconds()
        {
            return Math.Max(10, _settings.PollSeconds > 0
                ? _settings.PollSeconds
                : 30);
        }

        internal static List<string> BuildIoKeys(DeviceProfile profile)
        {
            List<string> keys = new List<string>();
            if (profile == null || profile.Match == null)
                return keys;

            DeviceMatch match = profile.Match;
            string transport = match.Transport ?? string.Empty;
            ushort? vendorId = match.ParsedVendorId;
            List<ushort> productIds = match.ParsedProductIds;
            if (IsHidProfile(profile) && vendorId.HasValue)
            {
                foreach (ushort productId in productIds.Distinct())
                    keys.Add("hid:" + vendorId.Value.ToString("X4") + ":" +
                        productId.ToString("X4"));
            }
            else if (string.Equals(transport, "bluetooth-gatt",
                StringComparison.OrdinalIgnoreCase) ||
                string.Equals(transport, "xinput",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (match.HasValidBluetoothServiceId)
                    keys.Add("bt:" + match.BluetoothServiceId.ToLowerInvariant());
                if (vendorId.HasValue)
                {
                    foreach (ushort productId in productIds.Distinct())
                        keys.Add("bt:" + vendorId.Value.ToString("X4") + ":" +
                            productId.ToString("X4"));
                }
                if (match.XInputUserIndex.HasValue)
                    keys.Add("xinput:" + match.XInputUserIndex.Value);
            }

            if (keys.Count == 0)
                keys.Add("profile:" + (profile.Id ?? string.Empty).ToLowerInvariant());
            return keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

        internal static bool CanProbeUnknownHid(DeviceProfile profile,
            DevicePresenceState presence, bool workerAvailable,
            bool presenceScanInFlight, bool presenceScanTimedOut)
        {
            return workerAvailable && presence == DevicePresenceState.Unknown &&
                HasExactHidSelector(profile) &&
                (!presenceScanInFlight || presenceScanTimedOut);
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

        private void EnqueueReadingUpdated(BatteryReading reading)
        {
            if (reading == null)
                return;
            List<KeyValuePair<Delegate, ReadingSubscriberRuntime>> starts =
                new List<KeyValuePair<Delegate, ReadingSubscriberRuntime>>();
            lock (_devices)
            {
                if (_disposed)
                    return;
                EventHandler<BatteryReadingEventArgs> handler = ReadingUpdated;
                if (handler == null)
                    return;
                foreach (Delegate subscriber in handler.GetInvocationList())
                {
                    ReadingSubscriberRuntime runtime;
                    if (!_subscriberRuntimes.TryGetValue(subscriber, out runtime))
                    {
                        runtime = new ReadingSubscriberRuntime();
                        _subscriberRuntimes.Add(subscriber, runtime);
                    }
                    runtime.Pending.Enqueue(reading);
                    while (runtime.Pending.Count > MaxPendingReadingsPerSubscriber)
                        runtime.Pending.Dequeue();
                    if (runtime.WorkerRunning)
                        continue;
                    runtime.WorkerRunning = true;
                    starts.Add(new KeyValuePair<Delegate, ReadingSubscriberRuntime>(
                        subscriber, runtime));
                }
            }

            foreach (KeyValuePair<Delegate, ReadingSubscriberRuntime> start in starts)
            {
                try
                {
                    _ = Task.Factory.StartNew(
                        () => DrainSubscriber(start.Key, start.Value),
                        CancellationToken.None,
                        TaskCreationOptions.DenyChildAttach,
                        TaskScheduler.Default);
                }
                catch
                {
                    lock (_devices)
                    {
                        start.Value.Pending.Clear();
                        start.Value.WorkerRunning = false;
                        _loopErrorCount++;
                        _lastErrorCode = "subscriber-worker-start-failed";
                    }
                }
            }
        }

        private void DrainSubscriber(Delegate subscriber,
            ReadingSubscriberRuntime runtime)
        {
            try
            {
                while (true)
                {
                    BatteryReading reading;
                    lock (_devices)
                    {
                        EventHandler<BatteryReadingEventArgs> current = ReadingUpdated;
                        bool subscribed = current != null &&
                            current.GetInvocationList().Contains(subscriber);
                        if (_disposed || !subscribed)
                        {
                            runtime.Pending.Clear();
                            runtime.WorkerRunning = false;
                            _subscriberRuntimes.Remove(subscriber);
                            return;
                        }
                        if (runtime.Pending.Count == 0)
                        {
                            runtime.WorkerRunning = false;
                            return;
                        }
                        reading = runtime.Pending.Dequeue();
                    }

                    try
                    {
                        ((EventHandler<BatteryReadingEventArgs>)subscriber)(this,
                            new BatteryReadingEventArgs(reading));
                    }
                    catch
                    {
                        lock (_devices)
                        {
                            _subscriberErrorCount++;
                            _lastErrorCode = "reading-subscriber-exception";
                        }
                    }
                }
            }
            catch
            {
                lock (_devices)
                {
                    runtime.Pending.Clear();
                    runtime.WorkerRunning = false;
                    _loopErrorCount++;
                    _lastErrorCode = "subscriber-worker-failed";
                }
            }
        }

        private void RecordLoopError(string code, Exception ex)
        {
            lock (_devices)
            {
                _loopErrorCount++;
                _lastErrorCode = code;
            }
            var ignored = ex;
        }

        private void CancelAttempts(
            IEnumerable<CancellationTokenSource> cancellations)
        {
            foreach (CancellationTokenSource cancellation in cancellations)
                CancelAttempt(cancellation);
        }

        private void CancelAttempt(CancellationTokenSource cancellation)
        {
            if (cancellation == null)
                return;
            lock (_devices)
            {
                if (_cancellationRequests.ContainsKey(cancellation))
                    return;
                try
                {
                    Task request = Task.Factory.StartNew(() =>
                    {
                        try { cancellation.Cancel(); }
                        catch { }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);
                    _cancellationRequests.Add(cancellation, request);
                    _ = request.ContinueWith(completed =>
                        {
                            lock (_devices)
                                _cancellationRequests.Remove(cancellation);
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                catch
                {
                    _cancellationRequests.Remove(cancellation);
                }
            }
        }

        private static void DisposeCancellationWhenSafe(
            CancellationTokenSource cancellation, Task cancellationRequest)
        {
            if (cancellation == null)
                return;
            if (cancellationRequest == null || cancellationRequest.IsCompleted)
            {
                cancellation.Dispose();
                return;
            }
            _ = cancellationRequest.ContinueWith(completed =>
                {
                    try { cancellation.Dispose(); }
                    catch { }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public void Dispose()
        {
            List<CancellationTokenSource> cancellations =
                new List<CancellationTokenSource>();
            lock (_devices)
            {
                if (_disposed)
                    return;
                _disposed = true;
                foreach (ReadingSubscriberRuntime subscriber in
                    _subscriberRuntimes.Values)
                    subscriber.Pending.Clear();
                foreach (DeviceRuntime runtime in _devices)
                {
                    if (runtime.AttemptCancellation != null)
                        cancellations.Add(runtime.AttemptCancellation);
                }
            }

            _shutdown.Cancel();
            CancelAttempts(cancellations);
            try
            {
                Task loop;
                lock (_loopStartLock)
                    loop = _loopTask;
                if (loop != null)
                    loop.Wait(2000);
            }
            catch { }
            _shutdown.Dispose();
        }
    }
}
