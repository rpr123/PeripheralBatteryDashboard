using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using PeripheralBatteryDashboard.Core;
using PeripheralBatteryDashboard.Hardware;

namespace PeripheralBatteryDashboard.Diagnostics
{
    public sealed class DiagnosticsService
    {
        private readonly IList<DeviceProfile> _profiles;
        private readonly ProviderRegistry _registry;
        private readonly BatteryReadContext _context;
        private readonly int _minimumWatchdogMilliseconds;
        private readonly int _timeoutMultiplier;
        private readonly Func<HidEnumerationResult> _hidMetadataEnumerator;
        private readonly object _hidEnumerationLock = new object();
        private Task<HidEnumerationResult> _hidEnumerationTask;

        public DiagnosticsService(IList<DeviceProfile> profiles, ProviderRegistry registry, BatteryReadContext context)
            : this(profiles, registry, context, 5000, 8)
        {
        }

        internal DiagnosticsService(IList<DeviceProfile> profiles,
            ProviderRegistry registry, BatteryReadContext context,
            int minimumWatchdogMilliseconds, int timeoutMultiplier)
            : this(profiles, registry, context, minimumWatchdogMilliseconds,
                timeoutMultiplier, null)
        {
        }

        internal DiagnosticsService(IList<DeviceProfile> profiles,
            ProviderRegistry registry, BatteryReadContext context,
            int minimumWatchdogMilliseconds, int timeoutMultiplier,
            Func<HidEnumerationResult> hidMetadataEnumerator)
        {
            _profiles = profiles;
            _registry = registry;
            _context = context;
            _minimumWatchdogMilliseconds = Math.Max(50,
                minimumWatchdogMilliseconds);
            _timeoutMultiplier = Math.Max(0, timeoutMultiplier);
            _hidMetadataEnumerator = hidMetadataEnumerator ??
                (() => _context.HidDevices.EnumerateMetadata());
        }

        public async Task<IList<BatteryReading>> ReadOnceAsync(CancellationToken token)
        {
            List<BatteryReading> readings = new List<BatteryReading>();
            foreach (DeviceProfile profile in _profiles)
            {
                token.ThrowIfCancellationRequested();
                IBatteryProvider provider;
                if ((DeviceMonitorService.IsHidProfile(profile) ||
                     ProviderSafetyPolicy.RequiresExactHidSelector(profile.ProviderId)) &&
                    !DeviceMonitorService.HasExactHidSelector(profile))
                {
                    AddObserved(readings, BatteryReading.Unavailable(profile,
                        DeviceConnectionState.Unsupported,
                        "정확한 HID 선택자 필요",
                        "VID/PID, Usage Page/Usage와 MI 번호 또는 MI 없음의 명시가 필요합니다.",
                        "broad-hid-selector-blocked"));
                    continue;
                }
                if (!_registry.TryGet(profile.ProviderId, out provider))
                {
                    AddObserved(readings, BatteryReading.Unavailable(profile,
                        DeviceConnectionState.Unsupported, "지원 모듈 없음",
                        profile.ProviderId, "provider-not-found"));
                    continue;
                }

                try
                {
                    BatteryReading reading = await ReadProviderWithWatchdogAsync(
                        profile, provider, token).ConfigureAwait(false);
                    AddObserved(readings, reading);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AddObserved(readings, BatteryReading.Unavailable(profile,
                        DeviceConnectionState.Error, "조회 오류", ex.Message,
                        "exception"));
                }
            }
            return readings;
        }

        private static void AddObserved(ICollection<BatteryReading> readings,
            BatteryReading reading)
        {
            NormalizeAttemptTimestamps(reading, DateTime.UtcNow);
            readings.Add(reading);
        }

        private static void NormalizeAttemptTimestamps(BatteryReading reading,
            DateTime observedUtc)
        {
            if (reading == null)
                return;
            reading.LastAttemptAtUtc = observedUtc;
            bool hasBatteryValue = reading.Percent.HasValue ||
                reading.Band != BatteryLevelBand.Unknown;
            if (reading.Connection == DeviceConnectionState.Connected &&
                !reading.IsStale && hasBatteryValue)
                reading.LastSuccessfulAtUtc = observedUtc;
        }

        private async Task<BatteryReading> ReadProviderWithWatchdogAsync(
            DeviceProfile profile, IBatteryProvider provider, CancellationToken token)
        {
            CancellationTokenSource attempt =
                CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<BatteryReading> providerTask;
            try
            {
                providerTask = Task.Factory.StartNew(
                    () => provider.ReadAsync(profile, _context, attempt.Token),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default).Unwrap();
            }
            catch
            {
                attempt.Dispose();
                throw;
            }

            int scaled = _timeoutMultiplier == 0
                ? 0
                : profile.EffectiveTimeoutMilliseconds * _timeoutMultiplier;
            int watchdogMilliseconds = Math.Max(_minimumWatchdogMilliseconds, scaled);
            Task delay = Task.Delay(watchdogMilliseconds, token);
            Task completed = await Task.WhenAny(providerTask, delay).ConfigureAwait(false);
            if (completed == providerTask)
            {
                try
                {
                    BatteryReading reading = await providerTask.ConfigureAwait(false);
                    return reading ?? BatteryReading.Unavailable(profile,
                        DeviceConnectionState.Error,
                        "조회 오류",
                        "공급자가 상태를 반환하지 않았습니다.",
                        "provider-null-reading");
                }
                finally
                {
                    attempt.Dispose();
                }
            }

            Task cancelRequest = Task.Factory.StartNew(() =>
                {
                    try { attempt.Cancel(); }
                    catch { }
                },
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
            Task providerObservation = providerTask.ContinueWith(late =>
                {
                    if (late.IsFaulted)
                    {
                        var ignored = late.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _ = Task.WhenAll(cancelRequest, providerObservation).ContinueWith(completed =>
                {
                    try { attempt.Dispose(); }
                    catch { }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            token.ThrowIfCancellationRequested();
            return BatteryReading.Unavailable(profile,
                DeviceConnectionState.Error,
                "조회 시간 초과",
                "장치 또는 Windows 드라이버 호출이 반환되지 않아 이 진단 항목을 건너뛰었습니다.",
                "provider-watchdog-timeout");
        }

        public string BuildText(IList<BatteryReading> readings)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Peripheral Battery Dashboard diagnostics");
            text.AppendLine("Generated: " + DateTimeOffset.Now.ToString("u"));
            text.AppendLine("Runtime: " + Environment.Version);
            text.AppendLine("OS: " + Environment.OSVersion);
            text.AppendLine("Providers: " + string.Join(", ", _registry.ProviderIds.ToArray()));
            if (_registry.PluginWarnings.Count > 0)
            {
                text.AppendLine("Plugin warnings:");
                foreach (string warning in _registry.PluginWarnings)
                    text.AppendLine("- " + warning);
            }
            text.AppendLine();
            text.AppendLine("Battery readings");
            foreach (BatteryReading reading in readings)
            {
                text.Append("- ").Append(reading.DisplayName)
                    .Append(" | ").Append(reading.Connection)
                    .Append(" | ").Append(reading.Percent.HasValue ? reading.Percent.Value + "%" : reading.StatusText)
                    .Append(reading.IsStale ? " (stale)" : string.Empty)
                    .Append(" | ").Append(reading.Charge)
                    .Append(" | ").Append(reading.DetailText)
                    .Append(" | attempt=").Append(FormatNullableUtc(reading.LastAttemptAtUtc))
                    .Append(" | success=").Append(FormatNullableUtc(reading.LastSuccessfulAtUtc))
                    .AppendLine();
            }

            text.AppendLine();
            text.AppendLine("Configured profiles");
            foreach (DeviceProfile profile in _profiles)
            {
                string ids;
                if (string.Equals(profile.Match.Transport, "xinput",
                    StringComparison.OrdinalIgnoreCase))
                    ids = "XInput";
                else if (string.Equals(profile.Match.Transport, "bluetooth-gatt",
                    StringComparison.OrdinalIgnoreCase) &&
                    profile.Match.HasValidBluetoothServiceId)
                    ids = "Bluetooth GATT local service ID";
                else
                    ids = profile.Match.VendorId + ":" +
                        string.Join(",", profile.Match.ProductIds.ToArray());
                text.AppendLine("- " + profile.Id + " | " + profile.ProviderId + " | " + ids);
            }

            text.AppendLine();
            text.AppendLine("HID collections (paths and serials omitted)");
            try
            {
                string enumerationStatus;
                HidEnumerationResult scan = EnumerateHidMetadataWithWatchdog(
                    out enumerationStatus);
                if (scan == null)
                {
                    text.AppendLine("- " + enumerationStatus);
                }
                else
                {
                    foreach (HidDeviceDescriptor device in scan.Devices)
                    {
                        text.Append("- ").Append(device.SafeIdentity)
                            .Append(" | ").Append(device.ProductName)
                            .Append(" | IN=").Append(device.InputReportLength)
                            .Append(" OUT=").Append(device.OutputReportLength)
                            .Append(" FEATURE=").Append(device.FeatureReportLength)
                            .AppendLine();
                    }
                    foreach (string warning in scan.WarningCodes)
                        text.AppendLine("- enumeration warning: " + warning);
                }
            }
            catch (Exception ex)
            {
                text.AppendLine("- enumeration error: " + ex.Message);
            }
            return text.ToString();
        }

        private static string FormatNullableUtc(DateTime? value)
        {
            return value.HasValue ? value.Value.ToUniversalTime().ToString("o") : "unknown";
        }

        private HidEnumerationResult EnumerateHidMetadataWithWatchdog(
            out string status)
        {
            Task<HidEnumerationResult> task;
            lock (_hidEnumerationLock)
            {
                if (_hidEnumerationTask == null || _hidEnumerationTask.IsCompleted)
                {
                    _hidEnumerationTask = Task.Factory.StartNew(
                        _hidMetadataEnumerator,
                        CancellationToken.None,
                        TaskCreationOptions.DenyChildAttach,
                        TaskScheduler.Default);
                }
                task = _hidEnumerationTask;
            }

            try
            {
                if (!task.Wait(_minimumWatchdogMilliseconds))
                {
                    status = "enumeration timeout: Windows HID metadata call is still pending";
                    return null;
                }
                status = string.Empty;
                return task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                status = "enumeration error: " + ex.GetBaseException().Message;
                return null;
            }
            finally
            {
                if (task.IsCompleted)
                {
                    lock (_hidEnumerationLock)
                    {
                        if (ReferenceEquals(_hidEnumerationTask, task))
                            _hidEnumerationTask = null;
                    }
                }
            }
        }

        public string ToJson(IList<BatteryReading> readings)
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            return json.Serialize(readings.Select(r => new
            {
                id = r.ProfileId,
                name = r.DisplayName,
                connection = r.Connection.ToString(),
                percent = r.Percent,
                approximate = r.IsApproximate,
                band = r.Band.ToString(),
                charge = r.Charge.ToString(),
                status = r.StatusText,
                detail = r.DetailText,
                sampledAtUtc = r.SampledAtUtc.ToString("o"),
                lastAttemptAtUtc = r.LastAttemptAtUtc.HasValue
                    ? r.LastAttemptAtUtc.Value.ToString("o")
                    : null,
                lastSuccessfulAtUtc = r.LastSuccessfulAtUtc.HasValue
                    ? r.LastSuccessfulAtUtc.Value.ToString("o")
                    : null,
                stale = r.IsStale,
                error = r.ErrorCode
            }).ToArray());
        }

        public string Export(string path, IList<BatteryReading> readings)
        {
            File.WriteAllText(path, BuildText(readings));
            return path;
        }
    }
}
