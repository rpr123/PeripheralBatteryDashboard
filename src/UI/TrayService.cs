using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;

using PeripheralBatteryDashboard.Core;

namespace PeripheralBatteryDashboard.UI
{
    /// <summary>
    /// Owns the notification-area icons and the small amount of window-lifetime
    /// behaviour associated with them. The monitor intentionally remains unaware
    /// of WinForms/WPF so that it can also be used by the console diagnostics app.
    /// </summary>
    public sealed class TrayService : IDisposable
    {
        private const string PerDeviceMode = "per-device";
        private const string CombinedMode = "combined";
        private static readonly Color DefaultIconBackground = Color.FromArgb(255, 17, 27, 46);
        private static readonly Color NormalBatteryAccent = Color.FromArgb(255, 55, 206, 194);
        private static readonly Color WarningAccent = Color.FromArgb(255, 245, 183, 66);
        private static readonly Color CriticalBatteryAccent = Color.FromArgb(255, 251, 96, 119);
        private static readonly Color NeutralAccent = Color.FromArgb(255, 120, 137, 160);
        private static readonly string[] DigitGlyphs =
        {
            "111101101101111",
            "010110010010111",
            "111001111100111",
            "111001111001111",
            "101101111001001",
            "111100111001111",
            "111100111101111",
            "111001001001001",
            "111101111101111",
            "111101111001111"
        };

        private readonly MainWindow _window;
        private readonly DeviceMonitorService _monitor;
        private readonly AppSettings _settings;
        private readonly Action _exitAction;
        private readonly Dictionary<string, DeviceProfile> _profiles;
        private readonly List<DeviceProfile> _orderedProfiles;
        private readonly Dictionary<string, BatteryReading> _readings;
        private readonly HashSet<string> _lowBatteryNotifications;
        private readonly object _stateLock = new object();

        private Forms.ContextMenuStrip _menu;
        private Forms.ToolStripMenuItem _openItem;
        private Forms.ToolStripMenuItem _refreshItem;
        private Forms.ToolStripMenuItem _exitItem;
        private Font _openItemFont;
        private TrayIconSlot _combinedSlot;
        private Dictionary<string, TrayIconSlot> _deviceSlots;
        private string _activeMode;
        private bool _closeHintShown;
        private bool _disposed;

        public bool AllowWindowClose { get; set; }

        public TrayService(MainWindow window, DeviceMonitorService monitor,
            AppSettings settings, Action exitAction)
        {
            if (window == null) throw new ArgumentNullException("window");
            if (monitor == null) throw new ArgumentNullException("monitor");
            if (settings == null) throw new ArgumentNullException("settings");
            if (exitAction == null) throw new ArgumentNullException("exitAction");

            _window = window;
            _monitor = monitor;
            _settings = settings;
            _exitAction = exitAction;
            _profiles = new Dictionary<string, DeviceProfile>(StringComparer.OrdinalIgnoreCase);
            _orderedProfiles = new List<DeviceProfile>();
            _readings = new Dictionary<string, BatteryReading>(StringComparer.OrdinalIgnoreCase);
            _lowBatteryNotifications = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _deviceSlots = new Dictionary<string, TrayIconSlot>(StringComparer.OrdinalIgnoreCase);

            foreach (DeviceProfile profile in monitor.Profiles)
            {
                if (profile == null || string.IsNullOrWhiteSpace(profile.Id))
                    continue;
                _profiles[profile.Id] = profile;
                _orderedProfiles.Add(profile);
            }
            _orderedProfiles.Sort(CompareProfiles);

            foreach (BatteryReading reading in monitor.Snapshot)
            {
                if (reading != null && !string.IsNullOrWhiteSpace(reading.ProfileId))
                    _readings[reading.ProfileId] = reading;
            }

            BuildSharedMenu();
            ApplyTrayMode();
            _monitor.ReadingUpdated += MonitorOnReadingUpdated;
            _window.Closing += WindowOnClosing;
            _window.SettingsChanged += WindowOnSettingsChanged;
            _window.PresentationChanged += WindowOnPresentationChanged;
        }

        public void ShowWindow()
        {
            RunOnUiThread(delegate { _window.ShowFromTray(); });
        }

        private void BuildSharedMenu()
        {
            _menu = new Forms.ContextMenuStrip();

            _openItem = new Forms.ToolStripMenuItem("열기");
            _openItemFont = new Font(_openItem.Font, System.Drawing.FontStyle.Bold);
            _openItem.Font = _openItemFont;
            _openItem.Click += OpenItemOnClick;

            _refreshItem = new Forms.ToolStripMenuItem("지금 새로고침");
            _refreshItem.Click += RefreshItemOnClick;

            _exitItem = new Forms.ToolStripMenuItem("종료");
            _exitItem.Click += ExitItemOnClick;

            _menu.Items.Add(_openItem);
            _menu.Items.Add(_refreshItem);
            _menu.Items.Add(new Forms.ToolStripSeparator());
            _menu.Items.Add(_exitItem);
        }

        private void OpenItemOnClick(object sender, EventArgs e)
        {
            ShowWindow();
        }

        private void RefreshItemOnClick(object sender, EventArgs e)
        {
            RunOnUiThread(delegate { _window.RequestRefresh(); });
        }

        private void ExitItemOnClick(object sender, EventArgs e)
        {
            _exitAction();
        }

        private void NotifyIconOnMouseClick(object sender, Forms.MouseEventArgs e)
        {
            if (e.Button == Forms.MouseButtons.Left)
                ShowWindow();
        }

        private void WindowOnSettingsChanged(object sender, EventArgs e)
        {
            RunOnUiThread(ApplyTrayMode);
        }

        private void WindowOnPresentationChanged(object sender, EventArgs e)
        {
            RunOnUiThread(delegate
            {
                if (string.Equals(_activeMode, PerDeviceMode, StringComparison.Ordinal))
                    UpdateAllDeviceSlots();
                else
                    UpdateCombinedSlot();
            });
        }

        private void ApplyTrayMode()
        {
            if (_disposed)
                return;

            string desiredMode = NormalizeMode(_settings.TrayIconMode);
            if (string.Equals(desiredMode, _activeMode, StringComparison.Ordinal))
            {
                if (string.Equals(desiredMode, PerDeviceMode, StringComparison.Ordinal))
                    UpdateAllDeviceSlots();
                else
                    UpdateCombinedSlot();
                return;
            }

            if (string.Equals(desiredMode, PerDeviceMode, StringComparison.Ordinal))
            {
                Dictionary<string, TrayIconSlot> replacement =
                    new Dictionary<string, TrayIconSlot>(StringComparer.OrdinalIgnoreCase);
                TrayIconSlot fallbackReplacement = null;
                try
                {
                    foreach (DeviceProfile profile in _orderedProfiles)
                    {
                        TrayIconSlot slot = CreateSlot(profile.Id);
                        replacement[profile.Id] = slot;
                        UpdateDeviceSlot(slot, profile, GetReading(profile.Id));
                    }
                    fallbackReplacement = CreateSlot(string.Empty);
                    UpdateCombinedSlot(fallbackReplacement);
                    fallbackReplacement.NotifyIcon.Visible = !replacement.Values.Any(slot =>
                        slot.NotifyIcon.Visible);
                }
                catch
                {
                    DisposeSlot(fallbackReplacement);
                    DisposeSlots(replacement);
                    throw;
                }

                DisposeCombinedSlot();
                DisposeSlots(_deviceSlots);
                _deviceSlots = replacement;
                _combinedSlot = fallbackReplacement;
                _activeMode = PerDeviceMode;
                return;
            }

            TrayIconSlot combinedReplacement = CreateSlot(string.Empty);
            try
            {
                UpdateCombinedSlot(combinedReplacement);
                combinedReplacement.NotifyIcon.Visible = true;
            }
            catch
            {
                DisposeSlot(combinedReplacement);
                throw;
            }

            DisposeCombinedSlot();
            DisposeSlots(_deviceSlots);
            _deviceSlots = new Dictionary<string, TrayIconSlot>(StringComparer.OrdinalIgnoreCase);
            _combinedSlot = combinedReplacement;
            _activeMode = CombinedMode;
        }

        private static string NormalizeMode(string mode)
        {
            return string.Equals(mode, CombinedMode, StringComparison.OrdinalIgnoreCase)
                ? CombinedMode
                : PerDeviceMode;
        }

        private TrayIconSlot CreateSlot(string profileId)
        {
            Forms.NotifyIcon notifyIcon = new Forms.NotifyIcon();
            notifyIcon.ContextMenuStrip = _menu;
            notifyIcon.MouseClick += NotifyIconOnMouseClick;
            return new TrayIconSlot(profileId, notifyIcon);
        }

        private void WindowOnClosing(object sender, CancelEventArgs e)
        {
            if (_disposed || AllowWindowClose || !_settings.MinimizeToTrayOnClose)
                return;

            e.Cancel = true;
            _window.Hide();
            if (!_closeHintShown)
            {
                _closeHintShown = true;
                ShowBalloon("트레이에서 실행 중",
                    "배터리 모니터링을 계속합니다. 트레이 아이콘을 클릭하면 다시 열립니다.",
                    Forms.ToolTipIcon.Info,
                    false,
                    null);
            }
        }

        private void MonitorOnReadingUpdated(object sender, BatteryReadingEventArgs e)
        {
            BatteryReading reading = e.Reading;
            if (reading == null || _disposed)
                return;

            bool notifyLow = false;
            bool critical = false;
            DeviceProfile profile;
            DevicePresentationState state;
            DateTime nowUtc = DateTime.UtcNow;

            lock (_stateLock)
            {
                _readings[reading.ProfileId] = reading;
                _profiles.TryGetValue(reading.ProfileId, out profile);
                state = DevicePresentationResolver.Resolve(profile, reading, nowUtc);
                bool alreadyLatched = _lowBatteryNotifications.Contains(reading.ProfileId);
                LowBatteryAlertAction action = DevicePresentationResolver
                    .EvaluateLowBatteryAlert(state, alreadyLatched);
                if (action == LowBatteryAlertAction.Clear)
                    _lowBatteryNotifications.Remove(reading.ProfileId);
                else if (action == LowBatteryAlertAction.Notify &&
                    _settings.NotificationsEnabled)
                {
                    _lowBatteryNotifications.Add(reading.ProfileId);
                    notifyLow = true;
                    critical = state.Severity == BatterySeverity.Critical;
                }
            }

            RunOnUiThread(delegate
            {
                if (string.Equals(_activeMode, PerDeviceMode, StringComparison.Ordinal))
                {
                    UpdateDeviceSlot(reading.ProfileId);
                    UpdatePerDeviceFallbackVisibility();
                }
                else
                    UpdateCombinedSlot();

                if (notifyLow)
                {
                    string value = state.DisplayPercent.HasValue
                        ? state.DisplayPercent.Value + "%"
                        : state.DisplayBand != BatteryLevelBand.Unknown
                            ? BandLabel(state.DisplayBand)
                            : state.AvailabilityText;
                    ShowBalloon(critical ? "배터리 교체가 필요합니다" : "배터리가 부족합니다",
                        reading.DisplayName + " · " + value,
                        Forms.ToolTipIcon.Warning,
                        true,
                        reading.ProfileId);
                }
            });
        }

        private void UpdateAllDeviceSlots()
        {
            DateTime nowUtc = DateTime.UtcNow;
            foreach (DeviceProfile profile in _orderedProfiles)
            {
                TrayIconSlot slot;
                if (_deviceSlots.TryGetValue(profile.Id, out slot))
                    UpdateDeviceSlot(slot, profile, GetReading(profile.Id), nowUtc);
            }
            UpdatePerDeviceFallbackVisibility();
        }

        private void UpdateDeviceSlot(string profileId)
        {
            TrayIconSlot slot;
            DeviceProfile profile;
            if (!_deviceSlots.TryGetValue(profileId, out slot) ||
                !_profiles.TryGetValue(profileId, out profile))
                return;

            UpdateDeviceSlot(slot, profile, GetReading(profileId), DateTime.UtcNow);
        }

        private void UpdateDeviceSlot(TrayIconSlot slot, DeviceProfile profile, BatteryReading reading)
        {
            UpdateDeviceSlot(slot, profile, reading, DateTime.UtcNow);
        }

        private void UpdateDeviceSlot(TrayIconSlot slot, DeviceProfile profile,
            BatteryReading reading, DateTime nowUtc)
        {
            DevicePresentationState state = DevicePresentationResolver.Resolve(
                profile, reading, nowUtc);
            TrayVisual visual = CreateResolvedDeviceVisual(profile, state);
            ApplyVisual(slot, visual);
            slot.NotifyIcon.Text = TruncateToolTip(BuildResolvedDeviceToolTip(
                profile, state, nowUtc));
            slot.NotifyIcon.Visible = state.IsPresent;
        }

        private void UpdatePerDeviceFallbackVisibility()
        {
            if (_combinedSlot == null ||
                !string.Equals(_activeMode, PerDeviceMode, StringComparison.Ordinal))
                return;

            bool anyVisible = _deviceSlots.Values.Any(slot => slot.NotifyIcon.Visible);
            if (!anyVisible)
                UpdateCombinedSlot(_combinedSlot);
            _combinedSlot.NotifyIcon.Visible = !anyVisible;
        }

        private void UpdateCombinedSlot()
        {
            if (_combinedSlot != null)
                UpdateCombinedSlot(_combinedSlot);
        }

        private void UpdateCombinedSlot(TrayIconSlot slot)
        {
            DateTime nowUtc = DateTime.UtcNow;
            List<DevicePresentationState> states = new List<DevicePresentationState>();
            bool anyUnknown = false;
            lock (_stateLock)
            {
                foreach (DeviceProfile profile in _orderedProfiles)
                {
                    BatteryReading reading;
                    if (!_readings.TryGetValue(profile.Id, out reading) || reading == null)
                        anyUnknown = true;
                    DevicePresentationState state = DevicePresentationResolver.Resolve(
                        profile, reading, nowUtc);
                    if (state.IsPending)
                        anyUnknown = true;
                    states.Add(state);
                }
            }

            DevicePresentationState representative =
                DevicePresentationResolver.SelectCombined(states);
            DevicePresentationState expiredRepresentative =
                DevicePresentationResolver.SelectExpired(states);
            bool anyPresent = states.Any(state => state.IsPresent);
            int attentionCount = states.Count(ShouldShowAttentionBadge);
            int expiredCount = states.Count(state => state.IsPresent &&
                state.Freshness == BatteryValueFreshness.ExpiredStale);
            bool showAttentionBadge = attentionCount > 0;

            TrayVisual visual;
            string tooltip;
            if (representative != null)
            {
                TrayVisual source = CreateResolvedDeviceVisual(
                    representative.Profile, representative);
                visual = CreateCombinedVisualWithAttention(source, showAttentionBadge);
                if (representative.Freshness == BatteryValueFreshness.Fresh)
                    tooltip = "주변기기 배터리 · 최저 " +
                        PresentationValue(representative);
                else
                    tooltip = "주변기기 배터리 · 마지막 " +
                        PresentationValue(representative) + " · " +
                        DevicePresentationResolver.FormatAge(
                            representative.LastSuccessfulAtUtc, nowUtc);
                if (attentionCount > 0)
                    tooltip += " · 상태 주의 " + attentionCount;
            }
            else if (anyPresent)
            {
                visual = CreateCombinedVisualWithAttention(
                    CreateSimpleVisual("—", NeutralAccent, false,
                        "combined-present-unavailable", "combined"),
                    showAttentionBadge);
                if (expiredRepresentative != null)
                    tooltip = "주변기기 배터리 · 마지막 " +
                        LastKnownValue(expiredRepresentative) + " · 성공 " +
                        FormatSuccessfulTime(expiredRepresentative.LastSuccessfulAtUtc);
                else if (expiredCount > 0)
                    tooltip = "주변기기 배터리 · 이전 값 만료 " + expiredCount;
                else if (attentionCount > 0)
                    tooltip = "주변기기 배터리 · 상태 확인 필요 " + attentionCount;
                else
                    tooltip = "주변기기 배터리 · 감지됨 · 응답 대기";
            }
            else if (anyUnknown)
            {
                visual = CreateSimpleVisual("?", NeutralAccent, false,
                    "combined-unknown", "combined");
                tooltip = "주변기기 배터리 · 확인 중";
            }
            else
            {
                visual = CreateSimpleVisual("—", NeutralAccent, false,
                    "combined-offline", "combined");
                tooltip = "주변기기 배터리 · 연결된 장치 없음";
            }

            ApplyVisual(slot, visual);
            slot.NotifyIcon.Text = TruncateToolTip(tooltip);
        }

        private static TrayVisual CreateDeviceVisual(DeviceProfile profile, BatteryReading reading)
        {
            DevicePresentationState state = DevicePresentationResolver.Resolve(
                profile, reading, DateTime.UtcNow);
            return CreateResolvedDeviceVisual(profile, state);
        }

        private static TrayVisual CreateResolvedDeviceVisual(DeviceProfile profile,
            DevicePresentationState state)
        {
            string deviceShape = ResolveDeviceShape(profile);
            if (state == null)
                return CreateSimpleVisual("?", NeutralAccent, false,
                    "missing-state", deviceShape);

            bool attentionBadge = ShouldShowAttentionBadge(state);
            bool charging = state.Freshness == BatteryValueFreshness.Fresh &&
                state.Reading != null &&
                state.Reading.Charge == DeviceChargeState.Charging;
            string text;
            Color accent;
            if (state.HasDisplayValue)
            {
                text = state.DisplayPercent.HasValue
                    ? state.DisplayPercent.Value.ToString()
                    : "?";
                accent = BatterySeverityColor(state.Severity);
                if (state.Freshness == BatteryValueFreshness.RecentStale)
                    accent = BlendWithBackground(accent, 0.58);
            }
            else
            {
                text = state.IsPending ? "?" : "—";
                accent = NeutralAccent;
            }

            string key = "resolved|" + state.Freshness + "|" + state.Severity + "|" +
                state.Availability + "|" + text + "|badge:" + attentionBadge;
            return CreateTrayVisual(text, accent, charging, key, deviceShape,
                attentionBadge);
        }

        private static TrayVisual CreateSimpleVisual(string text, Color accent,
            bool charging, string renderKey, string deviceShape)
        {
            return CreateSimpleVisualWithBackground(text, accent, charging, renderKey,
                deviceShape, DefaultIconBackground);
        }

        private static TrayVisual CreateSimpleVisualWithBackground(string text, Color accent,
            bool charging, string renderKey, string deviceShape, Color background)
        {
            return CreateTrayVisual(text, accent, charging, renderKey, deviceShape, false);
        }

        private static TrayVisual CreateTrayVisual(string text, Color accent,
            bool charging, string renderKey, string deviceShape, bool attentionBadge)
        {
            string normalizedShape = NormalizeDeviceShape(deviceShape);
            return new TrayVisual
            {
                Text = text,
                Accent = accent,
                ValueColor = IsAsciiDigits(text) ? accent : NeutralAccent,
                Background = DefaultIconBackground,
                Charging = charging,
                AttentionBadge = attentionBadge,
                DeviceShape = normalizedShape,
                RenderKey = renderKey + "|" + accent.ToArgb() + "|background:" +
                            DefaultIconBackground.ToArgb() + "|value:" +
                            (IsAsciiDigits(text) ? accent : NeutralAccent).ToArgb() +
                            "|" + charging + "|shape:" + normalizedShape +
                            "|attention:" + attentionBadge
            };
        }

        private static TrayVisual CreateCombinedVisual(TrayVisual source)
        {
            return CreateCombinedVisualWithAttention(source, false);
        }

        private static TrayVisual CreateCombinedVisualWithAttention(TrayVisual source,
            bool attentionBadge)
        {
            return new TrayVisual
            {
                Text = source == null ? "?" : source.Text,
                Accent = source == null ? NeutralAccent : source.Accent,
                ValueColor = source == null ? NeutralAccent : source.ValueColor,
                Background = DefaultIconBackground,
                Charging = source != null && source.Charging,
                AttentionBadge = attentionBadge,
                DeviceShape = "combined",
                RenderKey = "combined|" + (source == null ? "?" : source.Text) + "|" +
                    (source == null ? 0 : source.Accent.ToArgb()) + "|" +
                    DefaultIconBackground.ToArgb() + "|" +
                    (source == null ? NeutralAccent.ToArgb() :
                        source.ValueColor.ToArgb()) + "|" +
                    (source != null && source.Charging) + "|attention:" + attentionBadge
            };
        }

        private static bool ShouldShowAttentionBadge(DevicePresentationState state)
        {
            return state != null && state.IsPresent &&
                state.Freshness != BatteryValueFreshness.Fresh &&
                (state.Availability == DeviceAvailability.Attention ||
                 state.Availability == DeviceAvailability.Inaccessible);
        }

        private static Color BatterySeverityColor(BatterySeverity severity)
        {
            switch (severity)
            {
                case BatterySeverity.Critical: return CriticalBatteryAccent;
                case BatterySeverity.Low: return WarningAccent;
                case BatterySeverity.Normal: return NormalBatteryAccent;
                default: return NeutralAccent;
            }
        }

        private static Color BlendWithBackground(Color foreground, double opacity)
        {
            double normalized = Math.Max(0.0, Math.Min(1.0, opacity));
            return Color.FromArgb(255,
                (int)Math.Round(DefaultIconBackground.R +
                    (foreground.R - DefaultIconBackground.R) * normalized),
                (int)Math.Round(DefaultIconBackground.G +
                    (foreground.G - DefaultIconBackground.G) * normalized),
                (int)Math.Round(DefaultIconBackground.B +
                    (foreground.B - DefaultIconBackground.B) * normalized));
        }

        private static string ResolveDeviceShape(DeviceProfile profile)
        {
            if (profile == null)
                return "device";
            string fromIcon = NormalizeDeviceShape(profile.Icon);
            if (!string.Equals(fromIcon, "device", StringComparison.Ordinal))
                return fromIcon;
            return NormalizeDeviceShape(profile.Category);
        }

        private static string NormalizeDeviceShape(string value)
        {
            string key = string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToLowerInvariant();
            switch (key)
            {
                case "headset":
                case "headphone":
                case "headphones":
                    return "headset";
                case "keyboard":
                    return "keyboard";
                case "mouse":
                    return "mouse";
                case "gamepad":
                case "controller":
                case "xbox":
                    return "gamepad";
                case "combined":
                    return "combined";
                default:
                    return "device";
            }
        }

        private static string BuildDeviceToolTip(DeviceProfile profile, BatteryReading reading)
        {
            DevicePresentationState state = DevicePresentationResolver.Resolve(
                profile, reading, DateTime.UtcNow);
            return BuildResolvedDeviceToolTip(profile, state, DateTime.UtcNow);
        }

        private static string BuildResolvedDeviceToolTip(DeviceProfile profile,
            DevicePresentationState state, DateTime nowUtc)
        {
            string name = profile == null || string.IsNullOrWhiteSpace(profile.DisplayName)
                ? "주변기기"
                : profile.DisplayName.Trim();
            if (state == null || state.IsPending)
                return ComposeToolTip(name, "확인 중");

            if (state.Freshness == BatteryValueFreshness.RecentStale)
            {
                string suffix = state.AvailabilityText + " · " + state.FreshnessText +
                    " · " + PresentationValue(state);
                return ComposeToolTip(name, suffix);
            }

            if (state.Freshness == BatteryValueFreshness.ExpiredStale)
            {
                string suffix = state.AvailabilityText + " · 마지막 값 만료 · " +
                    LastKnownValue(state) + " · 성공 " +
                    FormatSuccessfulTime(state.LastSuccessfulAtUtc);
                return ComposeToolTip(name, suffix);
            }

            if (state.Freshness != BatteryValueFreshness.Fresh)
            {
                string suffix = state.AvailabilityText;
                if (state.Availability == DeviceAvailability.Available)
                    suffix += " · 잔량 정보 없음";
                return ComposeToolTip(name, suffix);
            }

            string value = PresentationValue(state);
            BatteryReading reading = state.Reading;
            if (reading != null && reading.Charge == DeviceChargeState.Charging)
                return ComposeToolTip(name, "충전 중 · " + value);
            if (reading != null && reading.Charge == DeviceChargeState.Full)
                return ComposeToolTip(name, "완충 · " + value);
            return ComposeToolTip(name, value);
        }

        private static string PresentationValue(DevicePresentationState state)
        {
            if (state == null)
                return "잔량 정보 없음";
            if (state.DisplayPercent.HasValue)
            {
                bool approximate = state.Reading != null && state.Reading.IsApproximate;
                return (approximate ? "약 " : string.Empty) +
                    state.DisplayPercent.Value + "%";
            }
            return state.DisplayBand != BatteryLevelBand.Unknown
                ? BandLabel(state.DisplayBand)
                : "잔량 정보 없음";
        }

        private static string LastKnownValue(DevicePresentationState state)
        {
            if (state == null)
                return "값 없음";
            if (state.LastKnownPercent.HasValue)
                return state.LastKnownPercent.Value + "%";
            return state.LastKnownBand != BatteryLevelBand.Unknown
                ? BandLabel(state.LastKnownBand)
                : "값 없음";
        }

        private static string FormatSuccessfulTime(DateTime? successfulAtUtc)
        {
            return successfulAtUtc.HasValue
                ? successfulAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "시각 알 수 없음";
        }

        private static string BandLabel(BatteryLevelBand band)
        {
            switch (band)
            {
                case BatteryLevelBand.Critical: return "교체 필요";
                case BatteryLevelBand.Low: return "부족";
                case BatteryLevelBand.Medium: return "보통";
                case BatteryLevelBand.High: return "충분";
                case BatteryLevelBand.Full: return "가득 참";
                default: return "잔량 정보 없음";
            }
        }

        private static string ComposeToolTip(string name, string suffix)
        {
            const int maximumLength = 63;
            const string separator = " · ";
            string safeName = CleanToolTipPart(name, "주변기기");
            string safeSuffix = CleanToolTipPart(suffix, "상태 정보 없음");

            if (safeName.Length + separator.Length + safeSuffix.Length <= maximumLength)
                return safeName + separator + safeSuffix;

            int nameLength = maximumLength - separator.Length - safeSuffix.Length;
            if (nameLength < 2)
                return ClipWithEllipsis(safeSuffix, maximumLength);

            return ClipWithEllipsis(safeName, nameLength) + separator + safeSuffix;
        }

        private static string CleanToolTipPart(string value, string fallback)
        {
            string result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            return result.Replace('\r', ' ').Replace('\n', ' ');
        }

        private static string SafeStatus(string status, string fallback)
        {
            return string.IsNullOrWhiteSpace(status) ? fallback : status.Trim();
        }

        private static string TruncateToolTip(string text)
        {
            string value = CleanToolTipPart(text, "주변기기 배터리 대시보드");
            return ClipWithEllipsis(value, 63);
        }

        private static string ClipWithEllipsis(string value, int maximumLength)
        {
            if (string.IsNullOrEmpty(value) || maximumLength <= 0)
                return string.Empty;
            if (value.Length <= maximumLength)
                return value;
            if (maximumLength == 1)
                return "…";

            int contentLength = maximumLength - 1;
            if (char.IsHighSurrogate(value[contentLength - 1]) &&
                contentLength < value.Length && char.IsLowSurrogate(value[contentLength]))
                contentLength--;
            return value.Substring(0, contentLength) + "…";
        }

        private static int ClampPercent(int value)
        {
            return Math.Max(0, Math.Min(100, value));
        }

        private BatteryReading GetReading(string profileId)
        {
            lock (_stateLock)
            {
                BatteryReading reading;
                return _readings.TryGetValue(profileId, out reading) ? reading : null;
            }
        }

        private void ApplyVisual(TrayIconSlot slot, TrayVisual visual)
        {
            if (slot == null || visual == null ||
                string.Equals(slot.RenderKey, visual.RenderKey, StringComparison.Ordinal))
                return;

            Icon replacement = CreateStatusIconWithPresentation(visual.Text,
                visual.Accent, visual.ValueColor, visual.Charging,
                visual.DeviceShape, visual.AttentionBadge);
            try
            {
                slot.NotifyIcon.Icon = replacement;
            }
            catch
            {
                replacement.Dispose();
                throw;
            }

            Icon previous = slot.CurrentIcon;
            slot.CurrentIcon = replacement;
            slot.RenderKey = visual.RenderKey;
            if (previous != null)
                previous.Dispose();
        }

        private void ShowBalloon(string title, string text, Forms.ToolTipIcon icon,
            bool respectNotificationSetting, string profileId)
        {
            if (_disposed || (respectNotificationSetting && !_settings.NotificationsEnabled))
                return;

            Forms.NotifyIcon anchor = FindBalloonAnchor(profileId);
            if (anchor == null)
                return;

            anchor.BalloonTipTitle = title;
            anchor.BalloonTipText = text;
            anchor.BalloonTipIcon = icon;
            anchor.ShowBalloonTip(5000);
        }

        private Forms.NotifyIcon FindBalloonAnchor(string profileId)
        {
            if (!string.IsNullOrWhiteSpace(profileId))
            {
                TrayIconSlot deviceSlot;
                if (_deviceSlots.TryGetValue(profileId, out deviceSlot) && deviceSlot.NotifyIcon.Visible)
                    return deviceSlot.NotifyIcon;
            }

            if (_combinedSlot != null && _combinedSlot.NotifyIcon.Visible)
                return _combinedSlot.NotifyIcon;

            foreach (DeviceProfile profile in _orderedProfiles)
            {
                TrayIconSlot fallback;
                if (_deviceSlots.TryGetValue(profile.Id, out fallback) && fallback.NotifyIcon.Visible)
                    return fallback.NotifyIcon;
            }
            return null;
        }

        private static Icon CreateStatusIcon(string text, Color accent, bool charging,
            string deviceShape)
        {
            return CreateStatusIconWithBackground(text, accent, charging, deviceShape,
                DefaultIconBackground);
        }

        private static Icon CreateStatusIconWithBackground(string text, Color accent,
            bool charging, string deviceShape, Color backgroundColor)
        {
            return CreateStatusIconWithAttention(text, accent, charging, deviceShape, false);
        }

        private static Icon CreateStatusIconWithAttention(string text, Color accent,
            bool charging, string deviceShape, bool attentionBadge)
        {
            Color valueColor = IsAsciiDigits(text) ? accent : NeutralAccent;
            return CreateStatusIconWithPresentation(text, accent, valueColor,
                charging, deviceShape, attentionBadge);
        }

        private static Icon CreateStatusIconWithPresentation(string text, Color accent,
            Color valueColor, bool charging, string deviceShape, bool attentionBadge)
        {
            Color backgroundColor = DefaultIconBackground;
            using (Bitmap bitmap = new Bitmap(32, 32,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                graphics.Clear(Color.Transparent);

                string normalizedShape = NormalizeDeviceShape(deviceShape);
                DrawDeviceSilhouette(graphics, normalizedShape, backgroundColor, accent);

                using (SolidBrush foreground = new SolidBrush(valueColor))
                using (SolidBrush textShadow = new SolidBrush(Color.FromArgb(235, 4, 10, 20)))
                {
                    string glyphText = string.IsNullOrEmpty(text) ? "?" : text;
                    if (IsAsciiDigits(glyphText))
                    {
                        DrawDigitGlyphs(graphics, glyphText, foreground, textShadow,
                            charging, normalizedShape);
                    }
                    else
                    {
                        using (Font font = new Font("Segoe UI", 13.0f,
                            System.Drawing.FontStyle.Bold, GraphicsUnit.Point))
                        using (StringFormat format = new StringFormat())
                        {
                            format.Alignment = StringAlignment.Center;
                            format.LineAlignment = StringAlignment.Center;
                            format.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                            Rectangle bounds = GetContentArea(normalizedShape, charging);
                            RectangleF shadowBounds = new RectangleF(
                                bounds.X + 1, bounds.Y + 1, bounds.Width, bounds.Height);
                            graphics.DrawString(glyphText, font, textShadow, shadowBounds, format);
                            graphics.DrawString(glyphText, font, foreground, bounds, format);
                        }
                    }
                }

                if (charging)
                {
                    PointF[] bolt =
                    {
                        new PointF(28, 1), new PointF(25, 6), new PointF(27.5f, 6),
                        new PointF(26, 10.5f), new PointF(31, 5), new PointF(28.7f, 5),
                        new PointF(30.5f, 1)
                    };
                    using (SolidBrush chargeBrush = new SolidBrush(Color.FromArgb(255, 255, 222, 92)))
                        graphics.FillPolygon(chargeBrush, bolt);
                    using (Pen chargeOutline = new Pen(backgroundColor, 1.1f))
                        graphics.DrawPolygon(chargeOutline, bolt);
                }

                if (attentionBadge)
                    DrawAttentionBadge(graphics, backgroundColor);

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    return (Icon)Icon.FromHandle(handle).Clone();
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        private static void DrawAttentionBadge(Graphics graphics, Color backgroundColor)
        {
            RectangleF badgeBounds = new RectangleF(22.5f, 0.5f, 9.0f, 9.0f);
            using (SolidBrush badge = new SolidBrush(WarningAccent))
            using (Pen outline = new Pen(backgroundColor, 1.2f))
            using (Pen mark = new Pen(Color.FromArgb(255, 52, 39, 14), 1.4f))
            using (SolidBrush markDot = new SolidBrush(Color.FromArgb(255, 52, 39, 14)))
            {
                graphics.FillEllipse(badge, badgeBounds);
                graphics.DrawEllipse(outline, badgeBounds);
                mark.StartCap = LineCap.Round;
                mark.EndCap = LineCap.Round;
                graphics.DrawLine(mark, 27, 2.6f, 27, 5.7f);
                graphics.FillEllipse(markDot, 26.25f, 7.0f, 1.5f, 1.5f);
            }
        }

        private static void DrawDeviceSilhouette(Graphics graphics, string deviceShape,
            Color background, Color accent)
        {
            string shape = NormalizeDeviceShape(deviceShape);
            using (SolidBrush fill = new SolidBrush(background))
            using (Pen outline = new Pen(accent, 2.4f))
            using (Pen detail = new Pen(Color.FromArgb(185, accent), 1.4f))
            {
                outline.LineJoin = LineJoin.Round;
                outline.StartCap = LineCap.Round;
                outline.EndCap = LineCap.Round;
                detail.LineJoin = LineJoin.Round;
                detail.StartCap = LineCap.Round;
                detail.EndCap = LineCap.Round;

                switch (shape)
                {
                    case "headset":
                        DrawHeadsetSilhouette(graphics, fill, outline, detail, background, accent);
                        break;
                    case "keyboard":
                        DrawKeyboardSilhouette(graphics, fill, outline, detail, accent);
                        break;
                    case "mouse":
                        DrawMouseSilhouette(graphics, fill, outline, detail, accent);
                        break;
                    case "gamepad":
                        DrawGamepadSilhouette(graphics, fill, outline, detail, accent);
                        break;
                    case "combined":
                        DrawCombinedSilhouette(graphics, fill, outline, accent);
                        break;
                    default:
                        DrawGenericSilhouette(graphics, fill, outline);
                        break;
                }
            }
        }

        private static void DrawHeadsetSilhouette(Graphics graphics, Brush fill,
            Pen outline, Pen detail, Color background, Color accent)
        {
            using (Pen bandFill = new Pen(background, 8.0f))
            using (Pen bandOutline = new Pen(accent, 2.4f))
            {
                bandFill.StartCap = LineCap.Round;
                bandFill.EndCap = LineCap.Round;
                bandOutline.StartCap = LineCap.Round;
                bandOutline.EndCap = LineCap.Round;
                graphics.DrawArc(bandFill, 4, 3, 24, 25, 180, 180);
                graphics.DrawArc(bandOutline, 4, 3, 24, 25, 180, 180);
            }

            using (GraphicsPath left = CreateRoundedRectangle(
                new RectangleF(1.5f, 14, 8.5f, 15.5f), 3.2f))
            using (GraphicsPath right = CreateRoundedRectangle(
                new RectangleF(22, 14, 8.5f, 15.5f), 3.2f))
            {
                graphics.FillPath(fill, left);
                graphics.FillPath(fill, right);
                graphics.DrawPath(outline, left);
                graphics.DrawPath(outline, right);
            }
            graphics.DrawLine(detail, 7.5f, 18, 7.5f, 25);
            graphics.DrawLine(detail, 24.5f, 18, 24.5f, 25);
        }

        private static void DrawKeyboardSilhouette(Graphics graphics, Brush fill,
            Pen outline, Pen detail, Color accent)
        {
            using (GraphicsPath body = CreateRoundedRectangle(
                new RectangleF(1.5f, 6.5f, 29, 20), 4.0f))
            {
                graphics.FillPath(fill, body);
                graphics.DrawPath(outline, body);
            }
            graphics.DrawLine(detail, 5, 11, 27, 11);
            graphics.DrawLine(detail, 9, 23, 23, 23);
            using (SolidBrush key = new SolidBrush(Color.FromArgb(190, accent)))
            {
                graphics.FillRectangle(key, 5, 9, 2, 2);
                graphics.FillRectangle(key, 10, 9, 2, 2);
                graphics.FillRectangle(key, 20, 9, 2, 2);
                graphics.FillRectangle(key, 25, 9, 2, 2);
            }
        }

        private static void DrawMouseSilhouette(Graphics graphics, Brush fill,
            Pen outline, Pen detail, Color accent)
        {
            using (GraphicsPath body = CreateRoundedRectangle(
                new RectangleF(5, 1, 22, 30), 11.0f))
            {
                graphics.FillPath(fill, body);
                graphics.DrawPath(outline, body);
            }
            graphics.DrawLine(detail, 16, 2.5f, 16, 9);
            using (SolidBrush wheel = new SolidBrush(Color.FromArgb(215, accent)))
            using (GraphicsPath wheelPath = CreateRoundedRectangle(
                new RectangleF(14.5f, 4, 3, 5), 1.5f))
                graphics.FillPath(wheel, wheelPath);
        }

        private static void DrawGamepadSilhouette(Graphics graphics, Brush fill,
            Pen outline, Pen detail, Color accent)
        {
            using (GraphicsPath body = new GraphicsPath())
            {
                body.StartFigure();
                body.AddBezier(10, 9, 6, 9, 4, 12, 3, 17);
                body.AddBezier(3, 17, 1, 23, 1, 27, 5, 30);
                body.AddBezier(5, 30, 7, 31, 10, 27, 13, 23);
                body.AddLine(13, 23, 19, 23);
                body.AddBezier(19, 23, 22, 27, 25, 31, 27, 30);
                body.AddBezier(27, 30, 31, 27, 31, 23, 29, 17);
                body.AddBezier(29, 17, 28, 12, 26, 9, 22, 9);
                body.AddLine(22, 9, 19, 11);
                body.AddLine(19, 11, 13, 11);
                body.CloseFigure();
                graphics.FillPath(fill, body);
                graphics.DrawPath(outline, body);
            }

            graphics.DrawLine(detail, 6, 17, 11, 17);
            graphics.DrawLine(detail, 8.5f, 14.5f, 8.5f, 19.5f);
            using (SolidBrush button = new SolidBrush(Color.FromArgb(210, accent)))
            {
                graphics.FillEllipse(button, 23, 14, 2.8f, 2.8f);
                graphics.FillEllipse(button, 26, 17, 2.8f, 2.8f);
            }
        }

        private static void DrawCombinedSilhouette(Graphics graphics, Brush fill,
            Pen outline, Color accent)
        {
            using (GraphicsPath body = CreateRoundedRectangle(
                new RectangleF(2, 2, 28, 28), 6.0f))
            {
                graphics.FillPath(fill, body);
                graphics.DrawPath(outline, body);
            }
            using (SolidBrush dot = new SolidBrush(Color.FromArgb(210, accent)))
            {
                graphics.FillRectangle(dot, 5, 5, 3, 3);
                graphics.FillRectangle(dot, 24, 5, 3, 3);
                graphics.FillRectangle(dot, 5, 24, 3, 3);
                graphics.FillRectangle(dot, 24, 24, 3, 3);
            }
        }

        private static void DrawGenericSilhouette(Graphics graphics, Brush fill, Pen outline)
        {
            PointF[] points =
            {
                new PointF(8, 2), new PointF(24, 2), new PointF(30, 8),
                new PointF(30, 24), new PointF(24, 30), new PointF(8, 30),
                new PointF(2, 24), new PointF(2, 8)
            };
            graphics.FillPolygon(fill, points);
            graphics.DrawPolygon(outline, points);
        }

        private static GraphicsPath CreateRoundedRectangle(RectangleF bounds, float radius)
        {
            float diameter = Math.Max(1.0f, radius * 2.0f);
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter,
                diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Rectangle GetContentArea(string deviceShape, bool charging)
        {
            string shape = NormalizeDeviceShape(deviceShape);
            switch (shape)
            {
                case "headset":
                    return charging ? new Rectangle(3, 9, 20, 16) : new Rectangle(4, 9, 24, 16);
                case "keyboard":
                    return charging ? new Rectangle(3, 10, 20, 15) : new Rectangle(3, 10, 26, 15);
                case "mouse":
                    return charging ? new Rectangle(6, 12, 20, 15) : new Rectangle(6, 9, 20, 16);
                case "gamepad":
                    return charging ? new Rectangle(3, 11, 20, 15) : new Rectangle(4, 11, 24, 15);
                default:
                    return charging ? new Rectangle(3, 8, 20, 17) : new Rectangle(3, 8, 26, 17);
            }
        }

        private static bool IsAsciiDigits(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 3)
                return false;
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] < '0' || text[index] > '9')
                    return false;
            }
            return true;
        }

        private static void DrawDigitGlyphs(Graphics graphics, string text,
            Brush foreground, Brush shadow, bool charging, string deviceShape)
        {
            Rectangle area = GetContentArea(deviceShape, charging);
            int gap = text.Length >= 3 ? 1 : 2;
            int preferredWidth = text.Length == 1 ? 4 : text.Length == 2 ? 3 : 2;
            int maximumWidth = (area.Width - gap * (text.Length - 1)) /
                               Math.Max(1, 3 * text.Length);
            int cellWidth = Math.Max(1, Math.Min(preferredWidth, maximumWidth));
            int preferredHeight = text.Length == 1 ? 4 : 3;
            int cellHeight = Math.Max(1, Math.Min(preferredHeight, area.Height / 5));
            int glyphWidth = 3 * cellWidth;
            int glyphHeight = 5 * cellHeight;
            int totalWidth = glyphWidth * text.Length + gap * (text.Length - 1);
            int originX = area.X + (area.Width - totalWidth) / 2;
            int originY = area.Y + (area.Height - glyphHeight) / 2;

            DrawDigitCells(graphics, text, shadow, cellWidth, cellHeight, gap,
                originX - 1, originY - 1, 2);
            DrawDigitCells(graphics, text, foreground, cellWidth, cellHeight, gap,
                originX, originY, 0);
        }

        private static void DrawDigitCells(Graphics graphics, string text, Brush brush,
            int cellWidth, int cellHeight, int gap, int originX, int originY, int inflate)
        {
            int glyphWidth = 3 * cellWidth;
            for (int digitIndex = 0; digitIndex < text.Length; digitIndex++)
            {
                string pattern = DigitGlyphs[text[digitIndex] - '0'];
                int digitX = originX + digitIndex * (glyphWidth + gap);
                for (int row = 0; row < 5; row++)
                {
                    for (int column = 0; column < 3; column++)
                    {
                        if (pattern[row * 3 + column] != '1')
                            continue;
                        graphics.FillRectangle(brush,
                            digitX + column * cellWidth,
                            originY + row * cellHeight,
                            cellWidth + inflate,
                            cellHeight + inflate);
                    }
                }
            }
        }

        private void RunOnUiThread(Action action)
        {
            if (_disposed || action == null || _window.Dispatcher.HasShutdownStarted)
                return;

            Action guarded = delegate
            {
                if (!_disposed)
                    action();
            };
            if (_window.Dispatcher.CheckAccess())
                guarded();
            else
                _window.Dispatcher.BeginInvoke(guarded);
        }

        private static int CompareProfiles(DeviceProfile left, DeviceProfile right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left == null)
                return 1;
            if (right == null)
                return -1;
            int order = left.DisplayOrder.CompareTo(right.DisplayOrder);
            if (order != 0)
                return order;
            int name = string.Compare(left.DisplayName, right.DisplayName,
                StringComparison.CurrentCultureIgnoreCase);
            if (name != 0)
                return name;
            return string.Compare(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);
        }

        private void DisposeCombinedSlot()
        {
            if (_combinedSlot == null)
                return;
            DisposeSlot(_combinedSlot);
            _combinedSlot = null;
        }

        private void DisposeSlots(Dictionary<string, TrayIconSlot> slots)
        {
            if (slots == null)
                return;
            foreach (TrayIconSlot slot in slots.Values)
                DisposeSlot(slot);
            slots.Clear();
        }

        private void DisposeSlot(TrayIconSlot slot)
        {
            if (slot == null)
                return;

            Forms.NotifyIcon notifyIcon = slot.NotifyIcon;
            if (notifyIcon != null)
            {
                try { notifyIcon.Visible = false; }
                catch { }
                try { notifyIcon.MouseClick -= NotifyIconOnMouseClick; }
                catch { }
                try { notifyIcon.ContextMenuStrip = null; }
                catch { }
                try { notifyIcon.Icon = null; }
                catch { }
                try { notifyIcon.Dispose(); }
                catch { }
            }
            if (slot.CurrentIcon != null)
            {
                slot.CurrentIcon.Dispose();
                slot.CurrentIcon = null;
            }
            slot.RenderKey = null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _monitor.ReadingUpdated -= MonitorOnReadingUpdated;
            _window.Closing -= WindowOnClosing;
            _window.SettingsChanged -= WindowOnSettingsChanged;
            _window.PresentationChanged -= WindowOnPresentationChanged;

            DisposeCombinedSlot();
            DisposeSlots(_deviceSlots);

            if (_openItem != null)
            {
                _openItem.Click -= OpenItemOnClick;
                _openItem = null;
            }
            if (_refreshItem != null)
            {
                _refreshItem.Click -= RefreshItemOnClick;
                _refreshItem = null;
            }
            if (_exitItem != null)
            {
                _exitItem.Click -= ExitItemOnClick;
                _exitItem = null;
            }
            if (_menu != null)
            {
                _menu.Dispose();
                _menu = null;
            }
            if (_openItemFont != null)
            {
                _openItemFont.Dispose();
                _openItemFont = null;
            }
        }

        private sealed class TrayIconSlot
        {
            public string ProfileId { get; private set; }
            public Forms.NotifyIcon NotifyIcon { get; private set; }
            public Icon CurrentIcon { get; set; }
            public string RenderKey { get; set; }

            public TrayIconSlot(string profileId, Forms.NotifyIcon notifyIcon)
            {
                ProfileId = profileId ?? string.Empty;
                NotifyIcon = notifyIcon;
            }
        }

        private sealed class TrayVisual
        {
            public string Text { get; set; }
            public Color Accent { get; set; }
            public Color ValueColor { get; set; }
            public Color Background { get; set; }
            public bool Charging { get; set; }
            public bool AttentionBadge { get; set; }
            public string DeviceShape { get; set; }
            public string RenderKey { get; set; }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr handle);
    }
}
