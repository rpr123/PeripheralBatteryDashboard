using System;
using System.Collections.Generic;

namespace PeripheralBatteryDashboard.Core
{
    public enum BatteryValueFreshness
    {
        None,
        Fresh,
        RecentStale,
        ExpiredStale
    }

    public enum BatterySeverity
    {
        Unknown,
        Normal,
        Low,
        Critical
    }

    public enum DeviceAvailability
    {
        Checking,
        Available,
        Attention,
        Inaccessible,
        Disconnected,
        Unsupported
    }

    public enum LowBatteryAlertAction
    {
        None,
        Notify,
        Clear
    }

    /// <summary>
    /// UI-independent interpretation of one device reading. UI surfaces should render
    /// this state instead of independently inferring freshness, risk, or availability.
    /// </summary>
    public sealed class DevicePresentationState
    {
        public DeviceProfile Profile { get; internal set; }
        public BatteryReading Reading { get; internal set; }
        public bool IsPresent { get; internal set; }
        public bool IsPending { get; internal set; }
        public int? DisplayPercent { get; internal set; }
        public BatteryLevelBand DisplayBand { get; internal set; }
        public int? LastKnownPercent { get; internal set; }
        public BatteryLevelBand LastKnownBand { get; internal set; }
        public BatteryValueFreshness Freshness { get; internal set; }
        public BatterySeverity Severity { get; internal set; }
        public DeviceAvailability Availability { get; internal set; }
        public string AvailabilityText { get; internal set; }
        public string FreshnessText { get; internal set; }
        public DateTime? LastAttemptAtUtc { get; internal set; }
        public DateTime? LastSuccessfulAtUtc { get; internal set; }
        public double BarRatio { get; internal set; }
        public bool HasDisplayValue { get; internal set; }
        public bool HasWarning { get; internal set; }
        public bool CanNotifyLowBattery { get; internal set; }
        public bool IsRecoveredForNotification { get; internal set; }

        public DevicePresentationState()
        {
            DisplayBand = BatteryLevelBand.Unknown;
            LastKnownBand = BatteryLevelBand.Unknown;
            Freshness = BatteryValueFreshness.None;
            Severity = BatterySeverity.Unknown;
            Availability = DeviceAvailability.Checking;
            AvailabilityText = "확인 중";
            FreshnessText = "배터리 값 없음";
        }
    }

    public static class DevicePresentationResolver
    {
        public static readonly TimeSpan MaximumStaleAge = TimeSpan.FromHours(24);

        public static DevicePresentationState Resolve(DeviceProfile profile,
            BatteryReading reading, DateTime nowUtc)
        {
            DateTime normalizedNow = NormalizeUtc(nowUtc);
            DevicePresentationState state = new DevicePresentationState
            {
                Profile = profile,
                Reading = reading
            };
            if (reading == null)
                return state;

            state.IsPresent = reading.Presence == DevicePresenceState.Present;
            state.IsPending = reading.Presence == DevicePresenceState.Unknown ||
                reading.Connection == DeviceConnectionState.Unknown;
            state.LastAttemptAtUtc = NormalizeUtc(reading.LastAttemptAtUtc);
            state.LastSuccessfulAtUtc = NormalizeUtc(reading.LastSuccessfulAtUtc);

            int? percent = NormalizePercent(reading.Percent);
            BatteryLevelBand band = EffectiveBand(profile, percent, reading.Band);
            bool hasKnownValue = percent.HasValue || band != BatteryLevelBand.Unknown;

            bool fresh = state.IsPresent &&
                reading.Connection == DeviceConnectionState.Connected &&
                !reading.IsStale && hasKnownValue;
            if (fresh)
            {
                DateTime? legacySample = NormalizeUtc(reading.SampledAtUtc);
                if (!state.LastSuccessfulAtUtc.HasValue)
                    state.LastSuccessfulAtUtc = legacySample;
                if (!state.LastAttemptAtUtc.HasValue)
                    state.LastAttemptAtUtc = legacySample;
                state.Freshness = BatteryValueFreshness.Fresh;
            }
            else if (state.IsPresent && reading.IsStale && hasKnownValue &&
                     state.LastSuccessfulAtUtc.HasValue)
            {
                TimeSpan age = normalizedNow - state.LastSuccessfulAtUtc.Value;
                state.Freshness = age <= MaximumStaleAge
                    ? BatteryValueFreshness.RecentStale
                    : BatteryValueFreshness.ExpiredStale;
            }

            if (state.Freshness == BatteryValueFreshness.Fresh ||
                state.Freshness == BatteryValueFreshness.RecentStale)
            {
                state.DisplayPercent = percent;
                state.DisplayBand = band;
                state.HasDisplayValue = hasKnownValue;
                state.BarRatio = ResolveBarRatio(percent, band);
            }
            if (state.Freshness != BatteryValueFreshness.None && hasKnownValue)
            {
                state.LastKnownPercent = percent;
                state.LastKnownBand = band;
                state.Severity = ResolveSeverity(profile, percent, band);
            }

            state.Availability = ResolveAvailability(reading);
            state.AvailabilityText = AvailabilityLabel(state.Availability);
            state.FreshnessText = FreshnessLabel(state.Freshness,
                state.LastSuccessfulAtUtc, normalizedNow);
            state.HasWarning = state.Availability == DeviceAvailability.Attention ||
                state.Availability == DeviceAvailability.Inaccessible ||
                state.Availability == DeviceAvailability.Unsupported;
            state.CanNotifyLowBattery = state.IsPresent &&
                state.Freshness == BatteryValueFreshness.Fresh &&
                state.Availability == DeviceAvailability.Available &&
                reading.Charge != DeviceChargeState.Charging &&
                (state.Severity == BatterySeverity.Low ||
                 state.Severity == BatterySeverity.Critical);
            state.IsRecoveredForNotification = state.IsPresent &&
                state.Freshness == BatteryValueFreshness.Fresh &&
                state.Availability == DeviceAvailability.Available &&
                IsRecovered(profile, percent, band);
            return state;
        }

        public static LowBatteryAlertAction EvaluateLowBatteryAlert(
            DevicePresentationState state, bool alreadyLatched)
        {
            if (state == null || state.Freshness != BatteryValueFreshness.Fresh)
                return LowBatteryAlertAction.None;
            if (!alreadyLatched && state.CanNotifyLowBattery)
                return LowBatteryAlertAction.Notify;
            if (alreadyLatched && state.IsRecoveredForNotification)
                return LowBatteryAlertAction.Clear;
            return LowBatteryAlertAction.None;
        }

        public static DevicePresentationState SelectCombined(
            IEnumerable<DevicePresentationState> states)
        {
            List<DevicePresentationState> fresh = new List<DevicePresentationState>();
            List<DevicePresentationState> recentStale =
                new List<DevicePresentationState>();
            if (states != null)
            {
                foreach (DevicePresentationState state in states)
                {
                    if (state == null || !state.IsPresent || !state.HasDisplayValue)
                        continue;
                    if (state.Freshness == BatteryValueFreshness.Fresh)
                        fresh.Add(state);
                    else if (state.Freshness == BatteryValueFreshness.RecentStale)
                        recentStale.Add(state);
                }
            }

            List<DevicePresentationState> candidates = fresh.Count > 0
                ? fresh
                : recentStale;
            if (candidates.Count == 0)
                return null;
            candidates.Sort(CompareCombinedCandidates);
            return candidates[0];
        }

        public static DevicePresentationState SelectExpired(
            IEnumerable<DevicePresentationState> states)
        {
            List<DevicePresentationState> expired = new List<DevicePresentationState>();
            if (states != null)
            {
                foreach (DevicePresentationState state in states)
                {
                    if (state == null || !state.IsPresent ||
                        state.Freshness != BatteryValueFreshness.ExpiredStale ||
                        (!state.LastKnownPercent.HasValue &&
                         state.LastKnownBand == BatteryLevelBand.Unknown))
                        continue;
                    expired.Add(state);
                }
            }
            if (expired.Count == 0)
                return null;
            expired.Sort(CompareCombinedCandidates);
            return expired[0];
        }

        public static string FormatAge(DateTime? occurredAtUtc, DateTime nowUtc)
        {
            DateTime? normalizedOccurred = NormalizeUtc(occurredAtUtc);
            if (!normalizedOccurred.HasValue)
                return "시각 알 수 없음";

            TimeSpan age = NormalizeUtc(nowUtc) - normalizedOccurred.Value;
            if (age < TimeSpan.Zero)
                age = TimeSpan.Zero;
            if (age < TimeSpan.FromMinutes(1))
                return "방금";
            if (age < TimeSpan.FromHours(1))
                return Math.Max(1, (int)age.TotalMinutes) + "분 전";
            if (age < TimeSpan.FromDays(1))
                return Math.Max(1, (int)age.TotalHours) + "시간 전";
            return Math.Max(1, (int)age.TotalDays) + "일 전";
        }

        private static int CompareCombinedCandidates(DevicePresentationState left,
            DevicePresentationState right)
        {
            int severity = SeverityRank(right.Severity).CompareTo(
                SeverityRank(left.Severity));
            if (severity != 0)
                return severity;

            int? leftPercent = left.DisplayPercent ?? left.LastKnownPercent;
            int? rightPercent = right.DisplayPercent ?? right.LastKnownPercent;
            if (leftPercent.HasValue && rightPercent.HasValue)
            {
                int percent = leftPercent.Value.CompareTo(rightPercent.Value);
                if (percent != 0)
                    return percent;
            }
            else if (leftPercent.HasValue != rightPercent.HasValue)
            {
                return leftPercent.HasValue ? -1 : 1;
            }

            DateTime leftSuccess = left.LastSuccessfulAtUtc ?? DateTime.MinValue;
            DateTime rightSuccess = right.LastSuccessfulAtUtc ?? DateTime.MinValue;
            int success = rightSuccess.CompareTo(leftSuccess);
            if (success != 0)
                return success;

            int leftOrder = left.Profile == null ? int.MaxValue : left.Profile.DisplayOrder;
            int rightOrder = right.Profile == null ? int.MaxValue : right.Profile.DisplayOrder;
            int order = leftOrder.CompareTo(rightOrder);
            if (order != 0)
                return order;

            string leftId = ProfileId(left);
            string rightId = ProfileId(right);
            return string.Compare(leftId, rightId,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string ProfileId(DevicePresentationState state)
        {
            if (state == null)
                return string.Empty;
            if (state.Profile != null && !string.IsNullOrWhiteSpace(state.Profile.Id))
                return state.Profile.Id;
            return state.Reading == null ? string.Empty : state.Reading.ProfileId ?? string.Empty;
        }

        private static int SeverityRank(BatterySeverity severity)
        {
            switch (severity)
            {
                case BatterySeverity.Critical: return 3;
                case BatterySeverity.Low: return 2;
                case BatterySeverity.Normal: return 1;
                default: return 0;
            }
        }

        private static BatterySeverity ResolveSeverity(DeviceProfile profile,
            int? percent, BatteryLevelBand band)
        {
            if (percent.HasValue)
            {
                if (percent.Value <= 10)
                    return BatterySeverity.Critical;
                return percent.Value <= LowBatteryThreshold(profile)
                    ? BatterySeverity.Low
                    : BatterySeverity.Normal;
            }

            switch (band)
            {
                case BatteryLevelBand.Critical: return BatterySeverity.Critical;
                case BatteryLevelBand.Low: return BatterySeverity.Low;
                case BatteryLevelBand.Medium:
                case BatteryLevelBand.High:
                case BatteryLevelBand.Full:
                    return BatterySeverity.Normal;
                default:
                    return BatterySeverity.Unknown;
            }
        }

        private static bool IsRecovered(DeviceProfile profile, int? percent,
            BatteryLevelBand band)
        {
            if (percent.HasValue)
            {
                int lowBoundary = Math.Max(10, LowBatteryThreshold(profile));
                int recoveryThreshold = Math.Min(99, lowBoundary + 5);
                return percent.Value > recoveryThreshold;
            }
            return band == BatteryLevelBand.Medium ||
                   band == BatteryLevelBand.High ||
                   band == BatteryLevelBand.Full;
        }

        private static int LowBatteryThreshold(DeviceProfile profile)
        {
            int threshold = profile == null ? 20 : profile.LowBatteryPercent;
            return Math.Max(1, Math.Min(99, threshold));
        }

        private static DeviceAvailability ResolveAvailability(BatteryReading reading)
        {
            if (reading == null || reading.Connection == DeviceConnectionState.Unknown)
                return DeviceAvailability.Checking;
            if (reading.Connection == DeviceConnectionState.Unsupported)
                return DeviceAvailability.Unsupported;
            if (reading.Connection == DeviceConnectionState.Busy || IsAccessError(reading))
                return DeviceAvailability.Inaccessible;
            if (reading.Connection == DeviceConnectionState.Error ||
                reading.Connection == DeviceConnectionState.Sleeping)
                return DeviceAvailability.Attention;
            if (reading.Connection == DeviceConnectionState.Disconnected)
                return DeviceAvailability.Disconnected;
            if (reading.IsStale)
                return DeviceAvailability.Attention;
            if (reading.Presence == DevicePresenceState.Absent)
                return DeviceAvailability.Disconnected;
            return reading.Connection == DeviceConnectionState.Connected
                ? DeviceAvailability.Available
                : DeviceAvailability.Attention;
        }

        private static bool IsAccessError(BatteryReading reading)
        {
            string code = reading == null ? string.Empty : reading.ErrorCode ?? string.Empty;
            return code.IndexOf("access", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   code.IndexOf("sharing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   string.Equals(code, "busy", StringComparison.OrdinalIgnoreCase);
        }

        private static string AvailabilityLabel(DeviceAvailability availability)
        {
            switch (availability)
            {
                case DeviceAvailability.Available: return "연결됨";
                case DeviceAvailability.Attention: return "최근 응답 없음";
                case DeviceAvailability.Inaccessible: return "장치에 접근할 수 없음";
                case DeviceAvailability.Disconnected: return "연결 안 됨";
                case DeviceAvailability.Unsupported: return "지원되지 않음";
                default: return "확인 중";
            }
        }

        private static string FreshnessLabel(BatteryValueFreshness freshness,
            DateTime? lastSuccessfulAtUtc, DateTime nowUtc)
        {
            switch (freshness)
            {
                case BatteryValueFreshness.Fresh:
                    return "확인 " + FormatAge(lastSuccessfulAtUtc, nowUtc);
                case BatteryValueFreshness.RecentStale:
                    return "마지막 확인 " + FormatAge(lastSuccessfulAtUtc, nowUtc);
                case BatteryValueFreshness.ExpiredStale:
                    return "마지막 확인 " + FormatAge(lastSuccessfulAtUtc, nowUtc) +
                        " · 24시간 초과";
                default:
                    return "배터리 값 없음";
            }
        }

        private static double ResolveBarRatio(int? percent, BatteryLevelBand band)
        {
            if (percent.HasValue)
                return Math.Max(0, Math.Min(1, percent.Value / 100.0));
            switch (band)
            {
                case BatteryLevelBand.Critical: return 0.06;
                case BatteryLevelBand.Low: return 0.25;
                case BatteryLevelBand.Medium: return 0.55;
                case BatteryLevelBand.High: return 0.85;
                case BatteryLevelBand.Full: return 1.0;
                default: return 0;
            }
        }

        private static BatteryLevelBand EffectiveBand(DeviceProfile profile,
            int? percent, BatteryLevelBand band)
        {
            if (!percent.HasValue)
                return band;
            if (percent.Value <= 10)
                return BatteryLevelBand.Critical;
            if (percent.Value <= LowBatteryThreshold(profile))
                return BatteryLevelBand.Low;
            if (percent.Value <= 55)
                return BatteryLevelBand.Medium;
            if (percent.Value < 95)
                return BatteryLevelBand.High;
            return BatteryLevelBand.Full;
        }

        private static int? NormalizePercent(int? percent)
        {
            return percent.HasValue
                ? (int?)Math.Max(0, Math.Min(100, percent.Value))
                : null;
        }

        private static DateTime? NormalizeUtc(DateTime? value)
        {
            if (!value.HasValue || value.Value == DateTime.MinValue)
                return null;
            return NormalizeUtc(value.Value);
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
                return value;
            if (value.Kind == DateTimeKind.Local)
                return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }
    }
}
