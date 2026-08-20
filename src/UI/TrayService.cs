using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
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

            if (string.Equals(desiredMode, PerDeviceMode, StringComparison.Ordinal) &&
                _orderedProfiles.Count > 0)
            {
                Dictionary<string, TrayIconSlot> replacement =
                    new Dictionary<string, TrayIconSlot>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (DeviceProfile profile in _orderedProfiles)
                    {
                        TrayIconSlot slot = CreateSlot(profile.Id);
                        replacement[profile.Id] = slot;
                        UpdateDeviceSlot(slot, profile, GetReading(profile.Id));
                    }
                    foreach (TrayIconSlot slot in replacement.Values)
                        slot.NotifyIcon.Visible = true;
                }
                catch
                {
                    DisposeSlots(replacement);
                    throw;
                }

                DisposeCombinedSlot();
                DisposeSlots(_deviceSlots);
                _deviceSlots = replacement;
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

            lock (_stateLock)
            {
                _readings[reading.ProfileId] = reading;
                _profiles.TryGetValue(reading.ProfileId, out profile);

                bool isUsableReading = reading.Connection == DeviceConnectionState.Connected && !reading.IsStale;
                int threshold = profile == null ? 20 : Math.Max(1, Math.Min(99, profile.LowBatteryPercent));
                bool isLow = isUsableReading && reading.Charge != DeviceChargeState.Charging &&
                    ((reading.Percent.HasValue && reading.Percent.Value <= threshold) ||
                     (!reading.Percent.HasValue &&
                      (reading.Band == BatteryLevelBand.Low || reading.Band == BatteryLevelBand.Critical)));

                bool recovered = isUsableReading &&
                    ((reading.Percent.HasValue && reading.Percent.Value > threshold + 5) ||
                     (!reading.Percent.HasValue &&
                      (reading.Band == BatteryLevelBand.Medium || reading.Band == BatteryLevelBand.High ||
                       reading.Band == BatteryLevelBand.Full)));

                if (recovered)
                    _lowBatteryNotifications.Remove(reading.ProfileId);
                else if (isLow && _settings.NotificationsEnabled &&
                         !_lowBatteryNotifications.Contains(reading.ProfileId))
                {
                    _lowBatteryNotifications.Add(reading.ProfileId);
                    notifyLow = true;
                    critical = reading.Band == BatteryLevelBand.Critical ||
                               (reading.Percent.HasValue && reading.Percent.Value <= 10);
                }
            }

            RunOnUiThread(delegate
            {
                if (string.Equals(_activeMode, PerDeviceMode, StringComparison.Ordinal))
                    UpdateDeviceSlot(reading.ProfileId);
                else
                    UpdateCombinedSlot();

                if (notifyLow)
                {
                    string value = reading.Percent.HasValue
                        ? reading.Percent.Value + "%"
                        : reading.StatusText;
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
            foreach (DeviceProfile profile in _orderedProfiles)
                UpdateDeviceSlot(profile.Id);
        }

        private void UpdateDeviceSlot(string profileId)
        {
            TrayIconSlot slot;
            DeviceProfile profile;
            if (!_deviceSlots.TryGetValue(profileId, out slot) ||
                !_profiles.TryGetValue(profileId, out profile))
                return;

            UpdateDeviceSlot(slot, profile, GetReading(profileId));
        }

        private void UpdateDeviceSlot(TrayIconSlot slot, DeviceProfile profile, BatteryReading reading)
        {
            TrayVisual visual = CreateDeviceVisual(profile, reading);
            ApplyVisual(slot, visual);
            slot.NotifyIcon.Text = TruncateToolTip(BuildDeviceToolTip(profile, reading));
        }

        private void UpdateCombinedSlot()
        {
            if (_combinedSlot != null)
                UpdateCombinedSlot(_combinedSlot);
        }

        private void UpdateCombinedSlot(TrayIconSlot slot)
        {
            DeviceProfile representativeProfile;
            BatteryReading representative;
            bool anyUnknown;
            SelectCombinedReading(out representativeProfile, out representative, out anyUnknown);

            TrayVisual visual;
            string tooltip;
            if (representative != null)
            {
                visual = CreateCombinedVisual(CreateDeviceVisual(representativeProfile, representative));
                tooltip = representative.Percent.HasValue
                    ? "주변기기 배터리 · 최저 " + ClampPercent(representative.Percent.Value) + "%"
                    : "주변기기 배터리 · " + SafeStatus(representative.StatusText, "잔량 단계 확인됨");
            }
            else if (anyUnknown)
            {
                visual = CreateSimpleVisual("?", Color.FromArgb(255, 120, 137, 160), false,
                    "combined-unknown", "combined");
                tooltip = "주변기기 배터리 · 확인 중";
            }
            else
            {
                visual = CreateSimpleVisual("—", Color.FromArgb(255, 102, 116, 139), false,
                    "combined-offline", "combined");
                tooltip = "주변기기 배터리 · 연결된 장치 없음";
            }

            ApplyVisual(slot, visual);
            slot.NotifyIcon.Text = TruncateToolTip(tooltip);
        }

        private void SelectCombinedReading(out DeviceProfile representativeProfile,
            out BatteryReading representative, out bool anyUnknown)
        {
            representativeProfile = null;
            representative = null;
            anyUnknown = false;

            lock (_stateLock)
            {
                foreach (DeviceProfile profile in _orderedProfiles)
                {
                    BatteryReading candidate;
                    if (!_readings.TryGetValue(profile.Id, out candidate) || candidate == null)
                    {
                        anyUnknown = true;
                        continue;
                    }

                    if (candidate.Connection == DeviceConnectionState.Unknown)
                        anyUnknown = true;
                    if (candidate.Connection != DeviceConnectionState.Connected || candidate.IsStale)
                        continue;

                    if (representative == null || IsMoreUrgent(candidate, representative))
                    {
                        representative = candidate;
                        representativeProfile = profile;
                    }
                }
            }
        }

        private static bool IsMoreUrgent(BatteryReading candidate, BatteryReading current)
        {
            BatteryLevelBand candidateBand = EffectiveBand(candidate);
            BatteryLevelBand currentBand = EffectiveBand(current);
            int urgencyDifference = Urgency(candidateBand) - Urgency(currentBand);
            if (urgencyDifference != 0)
                return urgencyDifference > 0;

            if (candidate.Percent.HasValue && current.Percent.HasValue)
                return candidate.Percent.Value < current.Percent.Value;
            return candidate.Percent.HasValue && !current.Percent.HasValue;
        }

        private static BatteryLevelBand EffectiveBand(BatteryReading reading)
        {
            if (reading == null)
                return BatteryLevelBand.Unknown;
            return reading.Band != BatteryLevelBand.Unknown
                ? reading.Band
                : BatteryReading.BandFromPercent(reading.Percent);
        }

        private static int Urgency(BatteryLevelBand band)
        {
            switch (band)
            {
                case BatteryLevelBand.Critical: return 5;
                case BatteryLevelBand.Low: return 4;
                case BatteryLevelBand.Medium: return 3;
                case BatteryLevelBand.High: return 2;
                case BatteryLevelBand.Full: return 1;
                default: return 0;
            }
        }

        private static TrayVisual CreateDeviceVisual(DeviceProfile profile, BatteryReading reading)
        {
            string deviceShape = ResolveDeviceShape(profile);
            if (reading == null || reading.Connection == DeviceConnectionState.Unknown)
                return CreateSimpleVisual("?", Color.FromArgb(255, 120, 137, 160), false,
                    "unknown", deviceShape);

            if (reading.Connection != DeviceConnectionState.Connected || reading.IsStale)
            {
                string offlineKey = "offline|" + reading.Connection + "|" + reading.IsStale;
                return CreateSimpleVisual("—", Color.FromArgb(255, 102, 116, 139), false,
                    offlineKey, deviceShape);
            }

            int threshold = profile == null ? 20 : Math.Max(1, Math.Min(99, profile.LowBatteryPercent));
            int? percent = reading.Percent.HasValue
                ? (int?)ClampPercent(reading.Percent.Value)
                : null;
            BatteryLevelBand band = EffectiveBand(reading);
            bool critical = percent.HasValue
                ? percent.Value <= 10
                : band == BatteryLevelBand.Critical;
            bool low = !critical && (percent.HasValue
                ? percent.Value <= threshold
                : band == BatteryLevelBand.Low);
            bool charging = reading.Charge == DeviceChargeState.Charging;
            bool levelUnknown = !percent.HasValue && band == BatteryLevelBand.Unknown;
            Color accent = levelUnknown
                ? Color.FromArgb(255, 120, 137, 160)
                : critical
                    ? Color.FromArgb(255, 251, 96, 119)
                    : low
                        ? Color.FromArgb(255, 245, 183, 66)
                        : Color.FromArgb(255, 55, 206, 194);
            string text = percent.HasValue ? percent.Value.ToString() : "?";
            string key = "connected|" + text + "|" + band + "|" + charging + "|" +
                         critical + "|" + low + "|" + levelUnknown;
            return CreateSimpleVisual(text, accent, charging, key, deviceShape);
        }

        private static TrayVisual CreateSimpleVisual(string text, Color accent,
            bool charging, string renderKey, string deviceShape)
        {
            string normalizedShape = NormalizeDeviceShape(deviceShape);
            return new TrayVisual
            {
                Text = text,
                Accent = accent,
                Charging = charging,
                DeviceShape = normalizedShape,
                RenderKey = renderKey + "|" + accent.ToArgb() + "|" + charging +
                            "|shape:" + normalizedShape
            };
        }

        private static TrayVisual CreateCombinedVisual(TrayVisual source)
        {
            return new TrayVisual
            {
                Text = source == null ? "?" : source.Text,
                Accent = source == null ? Color.FromArgb(255, 120, 137, 160) : source.Accent,
                Charging = source != null && source.Charging,
                DeviceShape = "combined",
                RenderKey = "combined|" + (source == null ? "?" : source.Text) + "|" +
                            (source == null ? 0 : source.Accent.ToArgb()) + "|" +
                            (source != null && source.Charging)
            };
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
            string name = profile == null || string.IsNullOrWhiteSpace(profile.DisplayName)
                ? "주변기기"
                : profile.DisplayName.Trim();
            if (reading == null || reading.Connection == DeviceConnectionState.Unknown)
                return ComposeToolTip(name, "확인 중");

            if (reading.Connection != DeviceConnectionState.Connected || reading.IsStale)
            {
                string state;
                switch (reading.Connection)
                {
                    case DeviceConnectionState.Sleeping: state = "절전 또는 연결 안 됨"; break;
                    case DeviceConnectionState.Busy: state = "장치 사용 중"; break;
                    case DeviceConnectionState.Unsupported: state = "지원되지 않음"; break;
                    case DeviceConnectionState.Error: state = "조회 오류"; break;
                    default: state = "연결 안 됨"; break;
                }
                if (reading.IsStale && reading.Percent.HasValue)
                    state += " · 마지막 " + ClampPercent(reading.Percent.Value) + "%";
                return ComposeToolTip(name, state);
            }

            string value;
            if (reading.Percent.HasValue)
                value = (reading.IsApproximate ? "약 " : string.Empty) +
                        ClampPercent(reading.Percent.Value) + "%";
            else
                value = SafeStatus(reading.StatusText, "잔량 정보 없음");

            if (reading.Charge == DeviceChargeState.Charging)
                return ComposeToolTip(name, "충전 중 · " + value);
            if (reading.Charge == DeviceChargeState.Full)
                return ComposeToolTip(name, "완충 · " + value);
            return ComposeToolTip(name, value);
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

            Icon replacement = CreateStatusIcon(visual.Text, visual.Accent, visual.Charging,
                visual.DeviceShape);
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
            using (Bitmap bitmap = new Bitmap(32, 32,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                graphics.Clear(Color.Transparent);

                string normalizedShape = NormalizeDeviceShape(deviceShape);
                Color backgroundColor = Color.FromArgb(255, 17, 27, 46);
                DrawDeviceSilhouette(graphics, normalizedShape, backgroundColor, accent);

                using (SolidBrush foreground = new SolidBrush(Color.FromArgb(255, 238, 244, 255)))
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
            public bool Charging { get; set; }
            public string DeviceShape { get; set; }
            public string RenderKey { get; set; }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr handle);
    }
}
