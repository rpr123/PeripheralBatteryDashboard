using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using PeripheralBatteryDashboard.Core;
using PeripheralBatteryDashboard.Diagnostics;

namespace PeripheralBatteryDashboard.UI
{
    public sealed class MainWindow : Window
    {
        private readonly IList<DeviceProfile> _profiles;
        private readonly DeviceMonitorService _monitor;
        private readonly ProfileStore _profileStore;
        private readonly AppSettings _settings;
        private readonly AppSettingsStore _settingsStore;
        private readonly DiagnosticsService _diagnostics;
        private readonly Dictionary<string, DeviceCardView> _cards;
        private readonly string _guiExecutablePath;
        private TextBlock _summaryText;
        private TextBlock _lastUpdatedText;
        private TextBlock _footerText;
        private UniformGrid _cardsPanel;
        private Border _emptyStateHost;
        private TextBlock _emptyStateText;
        private CheckBox _startupCheckBox;
        private bool _monitorStarted;
        private bool _syncingStartupCheckBox;

        public event EventHandler SettingsChanged;
        public event EventHandler ProfilesImported;

        public MainWindow(
            IList<DeviceProfile> profiles,
            DeviceMonitorService monitor,
            ProfileStore profileStore,
            AppSettings settings,
            AppSettingsStore settingsStore,
            DiagnosticsService diagnostics)
        {
            if (profiles == null) throw new ArgumentNullException("profiles");
            if (monitor == null) throw new ArgumentNullException("monitor");
            if (profileStore == null) throw new ArgumentNullException("profileStore");
            if (settings == null) throw new ArgumentNullException("settings");
            if (settingsStore == null) throw new ArgumentNullException("settingsStore");
            if (diagnostics == null) throw new ArgumentNullException("diagnostics");

            _profiles = profiles;
            _monitor = monitor;
            _profileStore = profileStore;
            _settings = settings;
            _settingsStore = settingsStore;
            _diagnostics = diagnostics;
            _cards = new Dictionary<string, DeviceCardView>(StringComparer.OrdinalIgnoreCase);
            _guiExecutablePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "PeripheralBatteryDashboard.exe");

            Title = "Peripheral Battery Dashboard";
            Width = 1040;
            Height = 760;
            MinWidth = 820;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = UiFactory.WindowBackground;
            Foreground = UiFactory.PrimaryText;
            FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            Content = BuildContent();
            _monitor.ReadingUpdated += MonitorOnReadingUpdated;

            Loaded += delegate
            {
                if (!_monitorStarted)
                {
                    _monitorStarted = true;
                    _monitor.Start();
                }
                ApplySnapshot();
            };
        }

        public bool MinimizeToTrayOnClose
        {
            get { return _settings.MinimizeToTrayOnClose; }
        }

        public AppSettings CurrentSettings
        {
            get { return _settings; }
        }

        public void ShowFromTray()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ShowFromTray));
                return;
            }

            if (!IsVisible)
                Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
        }

        public void RequestRefresh()
        {
            _monitor.RefreshAll();
            SetFooter("모든 장치의 새 상태를 요청했습니다.");
        }

        public void ReportStartupRegistrationError(string error)
        {
            SetFooter("Windows 자동 실행을 등록하지 못했습니다: " + error);
        }

        public void ApplyReading(BatteryReading reading)
        {
            if (reading == null)
                return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<BatteryReading>(ApplyReading), reading);
                return;
            }

            DeviceCardView card;
            if (_cards.TryGetValue(reading.ProfileId, out card))
                card.Update(reading);
            UpdateSummary();
        }

        protected override void OnClosed(EventArgs e)
        {
            _monitor.ReadingUpdated -= MonitorOnReadingUpdated;
            base.OnClosed(e);
        }

        private UIElement BuildContent()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid header = new Grid { Margin = new Thickness(30, 24, 30, 14) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel titles = new StackPanel();
            titles.Children.Add(UiFactory.Text("내 장치 배터리", 25, UiFactory.PrimaryText, FontWeights.Bold));
            TextBlock subtitle = UiFactory.Text("동글과 Bluetooth 장치의 배터리를 한곳에서 확인합니다.", 13, UiFactory.SecondaryText, FontWeights.Normal);
            subtitle.Margin = new Thickness(0, 5, 0, 0);
            titles.Children.Add(subtitle);
            header.Children.Add(titles);

            Button refresh = UiFactory.Button("↻  지금 새로고침", true);
            refresh.Margin = new Thickness(16, 2, 0, 0);
            refresh.VerticalAlignment = VerticalAlignment.Top;
            refresh.Click += delegate { RequestRefresh(); };
            Grid.SetColumn(refresh, 1);
            header.Children.Add(refresh);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            TabControl tabs = new TabControl
            {
                Margin = new Thickness(24, 0, 24, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ItemContainerStyle = UiFactory.TabItemStyle()
            };

            TabItem batteryTab = new TabItem { Header = "배터리", Content = BuildBatteryTab() };
            TabItem devicesTab = new TabItem { Header = "장치 관리", Content = BuildDevicesTab() };
            tabs.Items.Add(batteryTab);
            tabs.Items.Add(devicesTab);
            Grid.SetRow(tabs, 1);
            root.Children.Add(tabs);

            _footerText = UiFactory.Text("15–120초 간격으로 가볍게 조회하며, 연결이 끊기면 자동으로 조회 간격을 늘립니다.", 12, UiFactory.MutedText, FontWeights.Normal);
            _footerText.Margin = new Thickness(31, 10, 31, 15);
            Grid.SetRow(_footerText, 2);
            root.Children.Add(_footerText);
            return root;
        }

        private UIElement BuildBatteryTab()
        {
            Grid layout = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid summaryGrid = new Grid();
            summaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            summaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _summaryText = UiFactory.Text("장치 상태를 확인하고 있습니다.", 14, UiFactory.PrimaryText, FontWeights.SemiBold);
            _lastUpdatedText = UiFactory.Text("마지막 업데이트 —", 12, UiFactory.SecondaryText, FontWeights.Normal);
            _lastUpdatedText.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(_lastUpdatedText, 1);
            summaryGrid.Children.Add(_summaryText);
            summaryGrid.Children.Add(_lastUpdatedText);
            Border summaryCard = UiFactory.Card(summaryGrid, new Thickness(6, 0, 6, 8));
            summaryCard.Padding = new Thickness(17, 12, 17, 12);
            layout.Children.Add(summaryCard);

            _cardsPanel = new UniformGrid
            {
                Columns = _profiles.Count <= 1 ? 1 : 2,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top
            };
            foreach (DeviceProfile profile in _profiles)
            {
                DeviceCardView card = new DeviceCardView(profile);
                _cards[profile.Id] = card;
                _cardsPanel.Children.Add(card.Root);
            }

            Grid cardsHost = new Grid();
            cardsHost.Children.Add(_cardsPanel);

            _emptyStateText = UiFactory.Text(
                _profiles.Count == 0
                    ? "등록된 장치가 없습니다. 설치 에이전트가 이 PC의 장치를 조사해 등록해야 합니다."
                    : "지원 장치를 찾고 있습니다.",
                15, UiFactory.SecondaryText, FontWeights.Normal);
            _emptyStateText.HorizontalAlignment = HorizontalAlignment.Center;
            _emptyStateText.TextAlignment = TextAlignment.Center;
            _emptyStateText.TextWrapping = TextWrapping.Wrap;
            _emptyStateText.Margin = new Thickness(20, 58, 20, 58);
            _emptyStateHost = UiFactory.Card(_emptyStateText, new Thickness(6));
            _emptyStateHost.VerticalAlignment = VerticalAlignment.Top;
            cardsHost.Children.Add(_emptyStateHost);

            ScrollViewer scroller = new ScrollViewer
            {
                Content = cardsHost,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 7, 0)
            };
            Grid.SetRow(scroller, 1);
            layout.Children.Add(scroller);

            Border settings = BuildQuickSettings();
            Grid.SetRow(settings, 2);
            layout.Children.Add(settings);
            return layout;
        }

        private Border BuildQuickSettings()
        {
            StackPanel panel = new StackPanel();
            TextBlock title = UiFactory.Text("앱 설정", 13, UiFactory.PrimaryText, FontWeights.SemiBold);
            title.Margin = new Thickness(0, 0, 0, 9);
            panel.Children.Add(title);

            WrapPanel controls = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock pollLabel = UiFactory.Text("조회 주기", 12, UiFactory.SecondaryText, FontWeights.Normal);
            pollLabel.Margin = new Thickness(0, 0, 8, 0);
            controls.Children.Add(pollLabel);

            ComboBox poll = new ComboBox
            {
                Width = 92,
                Height = 31,
                Background = UiFactory.PrimaryText,
                Foreground = UiFactory.WindowBackground,
                BorderBrush = UiFactory.AccentDark,
                Margin = new Thickness(0, 0, 22, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            int[] intervals = { 15, 30, 60, 120 };
            for (int index = 0; index < intervals.Length; index++)
            {
                ComboBoxItem item = new ComboBoxItem
                {
                    Content = intervals[index] + "초",
                    Tag = intervals[index],
                    Foreground = UiFactory.PrimaryText,
                    Background = UiFactory.RaisedBackground
                };
                poll.Items.Add(item);
                if (intervals[index] == _settings.PollSeconds)
                    poll.SelectedIndex = index;
            }
            if (poll.SelectedIndex < 0)
                poll.SelectedIndex = 1;
            poll.SelectionChanged += delegate
            {
                ComboBoxItem selected = poll.SelectedItem as ComboBoxItem;
                if (selected != null)
                {
                    _settings.PollSeconds = (int)selected.Tag;
                    SaveSettings("조회 주기를 " + _settings.PollSeconds + "초로 변경했습니다.");
                    _monitor.RefreshAll();
                }
            };
            controls.Children.Add(poll);

            TextBlock trayModeLabel = UiFactory.Text("트레이 표시", 12,
                UiFactory.SecondaryText, FontWeights.Normal);
            trayModeLabel.Margin = new Thickness(0, 0, 8, 0);
            controls.Children.Add(trayModeLabel);

            ComboBox trayMode = new ComboBox
            {
                Width = 126,
                Height = 31,
                Background = UiFactory.PrimaryText,
                Foreground = UiFactory.WindowBackground,
                BorderBrush = UiFactory.AccentDark,
                Margin = new Thickness(0, 0, 22, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(trayMode, "트레이 표시 방식");
            AutomationProperties.SetHelpText(trayMode,
                "각 장치를 별도 아이콘으로 표시하거나 하나의 통합 아이콘으로 표시합니다.");
            ComboBoxItem perDeviceItem = new ComboBoxItem
            {
                Content = "기기별 아이콘",
                Tag = AppSettings.TrayIconModePerDevice,
                Foreground = UiFactory.PrimaryText,
                Background = UiFactory.RaisedBackground
            };
            ComboBoxItem combinedItem = new ComboBoxItem
            {
                Content = "통합 아이콘",
                Tag = AppSettings.TrayIconModeCombined,
                Foreground = UiFactory.PrimaryText,
                Background = UiFactory.RaisedBackground
            };
            trayMode.Items.Add(perDeviceItem);
            trayMode.Items.Add(combinedItem);
            trayMode.SelectedIndex = string.Equals(_settings.TrayIconMode,
                AppSettings.TrayIconModeCombined, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            trayMode.SelectionChanged += delegate
            {
                ComboBoxItem selected = trayMode.SelectedItem as ComboBoxItem;
                if (selected == null)
                    return;
                string selectedMode = Convert.ToString(selected.Tag);
                if (string.Equals(_settings.TrayIconMode, selectedMode,
                    StringComparison.OrdinalIgnoreCase))
                    return;
                _settings.TrayIconMode = AppSettings.NormalizeTrayIconMode(selectedMode);
                SaveSettings(_settings.TrayIconMode == AppSettings.TrayIconModePerDevice
                    ? "기기별 배터리 아이콘을 표시합니다."
                    : "통합 배터리 아이콘을 표시합니다.");
            };
            controls.Children.Add(trayMode);

            CheckBox notifications = SettingCheckBox("배터리 부족 알림", _settings.NotificationsEnabled);
            notifications.Checked += delegate { _settings.NotificationsEnabled = true; SaveSettings("배터리 알림을 켰습니다."); };
            notifications.Unchecked += delegate { _settings.NotificationsEnabled = false; SaveSettings("배터리 알림을 껐습니다."); };
            controls.Children.Add(notifications);

            CheckBox tray = SettingCheckBox("닫을 때 트레이로 최소화", _settings.MinimizeToTrayOnClose);
            tray.Checked += delegate { _settings.MinimizeToTrayOnClose = true; SaveSettings("창을 닫으면 트레이로 최소화합니다."); };
            tray.Unchecked += delegate { _settings.MinimizeToTrayOnClose = false; SaveSettings("창을 닫으면 앱을 종료합니다."); };
            controls.Children.Add(tray);

            _startupCheckBox = SettingCheckBox("Windows 로그인 시 자동 실행",
                StartupRegistration.IsEnabled(_guiExecutablePath));
            _startupCheckBox.Checked += delegate { ChangeStartupPreference(true); };
            _startupCheckBox.Unchecked += delegate { ChangeStartupPreference(false); };
            controls.Children.Add(_startupCheckBox);
            panel.Children.Add(controls);

            Border card = UiFactory.Card(panel, new Thickness(6, 8, 6, 4));
            card.Padding = new Thickness(17, 13, 17, 13);
            return card;
        }

        private CheckBox SettingCheckBox(string label, bool value)
        {
            return new CheckBox
            {
                Content = label,
                IsChecked = value,
                Foreground = UiFactory.SecondaryText,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 5, 22, 5),
                Cursor = Cursors.Hand
            };
        }

        private UIElement BuildDevicesTab()
        {
            Grid layout = new Grid { Margin = new Thickness(6, 12, 6, 4) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid tools = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            tools.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel description = new StackPanel();
            description.Children.Add(UiFactory.Text("등록된 장치 프로필", 16, UiFactory.PrimaryText, FontWeights.SemiBold));
            TextBlock hint = UiFactory.Text("같은 통신 방식을 쓰는 장치는 JSON 프로필만 추가하고, 새 방식은 Plugins 폴더에 공급자를 추가할 수 있습니다.", 12, UiFactory.SecondaryText, FontWeights.Normal);
            hint.Margin = new Thickness(0, 4, 14, 0);
            description.Children.Add(hint);
            tools.Children.Add(description);

            WrapPanel buttons = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            Button import = UiFactory.Button("프로필 가져오기", true);
            import.Margin = new Thickness(6, 0, 0, 0);
            import.Click += ImportProfile;
            buttons.Children.Add(import);
            Button openFolder = UiFactory.Button("프로필 폴더 열기", false);
            openFolder.Margin = new Thickness(8, 0, 0, 0);
            openFolder.Click += OpenProfileFolder;
            buttons.Children.Add(openFolder);
            Button diagnostics = UiFactory.Button("진단 정보 저장", false);
            diagnostics.Margin = new Thickness(8, 0, 0, 0);
            diagnostics.Click += ExportDiagnostics;
            buttons.Children.Add(diagnostics);
            Grid.SetColumn(buttons, 1);
            tools.Children.Add(buttons);
            layout.Children.Add(tools);

            StackPanel rows = new StackPanel();
            foreach (DeviceProfile profile in _profiles)
                rows.Children.Add(BuildProfileRow(profile));

            if (_profileStore.LoadWarnings.Count > 0)
            {
                TextBlock warnings = UiFactory.Text("프로필 경고 · " + string.Join("  /  ", _profileStore.LoadWarnings.ToArray()), 12, UiFactory.Warning, FontWeights.Normal);
                warnings.Margin = new Thickness(8, 9, 8, 9);
                rows.Children.Add(warnings);
            }

            TextBlock path = UiFactory.Text("사용자 프로필 위치  " + _profileStore.UserProfileDirectory, 11, UiFactory.MutedText, FontWeights.Normal);
            path.Margin = new Thickness(8, 10, 8, 12);
            rows.Children.Add(path);

            ScrollViewer scroller = new ScrollViewer
            {
                Content = rows,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 7, 0)
            };
            Grid.SetRow(scroller, 1);
            layout.Children.Add(scroller);
            return layout;
        }

        private Border BuildProfileRow(DeviceProfile profile)
        {
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock icon = UiFactory.Text(IconFor(profile), 24, UiFactory.Accent, FontWeights.Normal);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            row.Children.Add(icon);

            StackPanel info = new StackPanel { Margin = new Thickness(10, 0, 16, 0) };
            info.Children.Add(UiFactory.Text(profile.DisplayName, 14, UiFactory.PrimaryText, FontWeights.SemiBold));
            TextBlock match = UiFactory.Text(MatchText(profile), 11, UiFactory.SecondaryText, FontWeights.Normal);
            match.Margin = new Thickness(0, 4, 0, 0);
            info.Children.Add(match);
            TextBlock id = UiFactory.Text(profile.Id, 10, UiFactory.MutedText, FontWeights.Normal);
            id.Margin = new Thickness(0, 3, 0, 0);
            info.Children.Add(id);
            Grid.SetColumn(info, 1);
            row.Children.Add(info);

            StackPanel provider = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            provider.Children.Add(UiFactory.Text(profile.ProviderId.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase) ? "내장 공급자" : "플러그인", 11, UiFactory.Accent, FontWeights.SemiBold));
            TextBlock providerId = UiFactory.Text(profile.ProviderId, 10, UiFactory.MutedText, FontWeights.Normal);
            providerId.Margin = new Thickness(0, 3, 0, 0);
            providerId.TextAlignment = TextAlignment.Right;
            provider.Children.Add(providerId);
            Grid.SetColumn(provider, 2);
            row.Children.Add(provider);

            Border card = UiFactory.Card(row, new Thickness(0, 0, 0, 9));
            card.Padding = new Thickness(14, 12, 14, 12);
            return card;
        }

        private static string MatchText(DeviceProfile profile)
        {
            if (profile.Match != null && string.Equals(profile.Match.Transport, "xinput", StringComparison.OrdinalIgnoreCase))
            {
                string slot = profile.Match.XInputUserIndex.HasValue ? " · 슬롯 " + profile.Match.XInputUserIndex.Value : " · 모든 슬롯";
                return "Bluetooth / XInput" + slot;
            }

            if (profile.Match != null && string.Equals(profile.Match.Transport,
                "bluetooth-gatt", StringComparison.OrdinalIgnoreCase))
            {
                if (profile.Match.HasValidBluetoothServiceId)
                    return "Bluetooth LE · 표준 Battery Service · 이 PC의 로컬 서비스 ID";
                string bluetoothProducts = profile.Match.ProductIds == null
                    ? string.Empty
                    : string.Join(", ", profile.Match.ProductIds.ToArray());
                return "Bluetooth LE · 표준 Battery Service · VID " +
                    profile.Match.VendorId + " · PID " + bluetoothProducts;
            }

            if (profile.Match == null)
                return "연결 조건 없음";
            string products = profile.Match.ProductIds == null ? "" : string.Join(", ", profile.Match.ProductIds.ToArray());
            string interfaceText = profile.Match.RequireNoInterfaceNumber
                ? " · 인터페이스 MI 없음"
                : (profile.Match.InterfaceNumber.HasValue
                    ? " · 인터페이스 " + profile.Match.InterfaceNumber.Value
                    : "");
            return "USB HID · VID " + profile.Match.VendorId + " · PID " + products + interfaceText;
        }

        private void ImportProfile(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "장치 프로필 가져오기",
                Filter = "장치 프로필 (*.json)|*.json|모든 파일 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                string destination = _profileStore.ImportProfileFile(dialog.FileName);
                SetFooter("프로필을 가져왔습니다. 앱을 다시 시작하면 적용됩니다.");
                EventHandler handler = ProfilesImported;
                if (handler != null)
                    handler(this, EventArgs.Empty);
                MessageBox.Show(this,
                    "프로필을 가져왔습니다.\n\n저장 위치: " + destination + "\n앱을 다시 시작하면 새 장치가 표시됩니다.",
                    "프로필 가져오기",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "프로필을 가져오지 못했습니다.\n\n" + ex.Message,
                    "프로필 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenProfileFolder(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(_profileStore.UserProfileDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + _profileStore.UserProfileDirectory + "\"",
                    UseShellExecute = false
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "프로필 폴더를 열지 못했습니다.\n\n" + ex.Message,
                    "폴더 열기 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportDiagnostics(object sender, RoutedEventArgs e)
        {
            SaveFileDialog dialog = new SaveFileDialog
            {
                Title = "진단 정보 저장",
                Filter = "텍스트 파일 (*.txt)|*.txt",
                FileName = "battery-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt",
                AddExtension = true,
                DefaultExt = ".txt"
            };
            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                _diagnostics.Export(dialog.FileName, _monitor.Snapshot);
                SetFooter("진단 정보를 저장했습니다: " + dialog.FileName);
                MessageBox.Show(this, "진단 정보를 저장했습니다.", "진단 정보",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "진단 정보를 저장하지 못했습니다.\n\n" + ex.Message,
                    "저장 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void ChangeStartupPreference(bool enabled)
        {
            if (_syncingStartupCheckBox)
                return;

            bool previousPreference = _settings.StartWithWindows;
            bool previousRegistration = StartupRegistration.IsEnabled(_guiExecutablePath);
            string error;
            if (!StartupRegistration.TrySetEnabled(enabled, _guiExecutablePath, out error))
            {
                SyncStartupCheckBox(previousRegistration);
                SetFooter("자동 실행 설정을 변경하지 못했습니다: " + error);
                return;
            }

            _settings.StartWithWindows = enabled;
            if (SaveSettings(enabled
                ? "Windows 로그인 시 창 없이 트레이에서 자동 실행합니다."
                : "Windows 자동 실행을 해제했습니다."))
                return;

            _settings.StartWithWindows = previousPreference;
            string rollbackError;
            bool rollbackSucceeded = StartupRegistration.TrySetEnabled(previousRegistration,
                _guiExecutablePath, out rollbackError);
            if (rollbackSucceeded)
            {
                SyncStartupCheckBox(previousRegistration);
            }
            else
            {
                bool actualRegistration = StartupRegistration.IsEnabled(_guiExecutablePath);
                _settings.StartWithWindows = actualRegistration;
                SyncStartupCheckBox(actualRegistration);
                SetFooter("자동 실행 설정 복원에 실패했습니다: " + rollbackError);
            }
        }

        private void SyncStartupCheckBox(bool enabled)
        {
            if (_startupCheckBox == null)
                return;
            _syncingStartupCheckBox = true;
            try
            {
                _startupCheckBox.IsChecked = enabled;
            }
            finally
            {
                _syncingStartupCheckBox = false;
            }
        }

        private bool SaveSettings(string status)
        {
            try
            {
                _settingsStore.Save(_settings);
                SetFooter(status);
                EventHandler handler = SettingsChanged;
                if (handler != null)
                    handler(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                SetFooter("설정을 저장하지 못했습니다: " + ex.Message);
                return false;
            }
        }

        private void MonitorOnReadingUpdated(object sender, BatteryReadingEventArgs e)
        {
            ApplyReading(e.Reading);
        }

        private void ApplySnapshot()
        {
            foreach (BatteryReading reading in _monitor.Snapshot)
            {
                DeviceCardView card;
                if (_cards.TryGetValue(reading.ProfileId, out card))
                    card.Update(reading);
            }
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            IList<BatteryReading> readings = _monitor.Snapshot;
            IList<BatteryReading> presentReadings = readings.Where(r => r.IsPresent).ToList();
            int connected = presentReadings.Count(r => r.Connection == DeviceConnectionState.Connected);
            int low = presentReadings.Count(r => r.Connection == DeviceConnectionState.Connected &&
                (r.Band == BatteryLevelBand.Critical || r.Band == BatteryLevelBand.Low));
            int stale = presentReadings.Count(r => r.IsStale);
            int pending = readings.Count(r => r.Presence == DevicePresenceState.Unknown);
            string summary;
            if (_profiles.Count == 0)
                summary = "등록된 장치 프로필 없음";
            else if (presentReadings.Count == 0 && pending > 0)
                summary = "장치 상태를 확인하고 있습니다.";
            else if (presentReadings.Count == 0)
                summary = "현재 감지된 지원 장치 없음";
            else
            {
                summary = "연결 " + connected + " / " + presentReadings.Count;
                if (low > 0)
                    summary += "   ·   배터리 부족 " + low;
                if (stale > 0)
                    summary += "   ·   이전 값 " + stale;
            }
            _summaryText.Text = summary;
            _summaryText.Foreground = low > 0 ? UiFactory.Warning : UiFactory.PrimaryText;

            if (_cardsPanel != null)
                _cardsPanel.Columns = presentReadings.Count <= 1 ? 1 : 2;
            if (_emptyStateHost != null)
            {
                _emptyStateHost.Visibility = presentReadings.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                if (_profiles.Count == 0)
                    _emptyStateText.Text = "등록된 장치가 없습니다. 설치 에이전트가 이 PC의 장치를 조사해 등록해야 합니다.";
                else if (pending > 0)
                    _emptyStateText.Text = "지원 장치를 찾고 있습니다.";
                else
                    _emptyStateText.Text = "이 PC에서 현재 감지된 등록 장치가 없습니다.";
            }

            BatteryReading latest = presentReadings.OrderByDescending(r => r.SampledAtUtc)
                .FirstOrDefault() ?? readings.Where(r => r.Presence != DevicePresenceState.Unknown)
                .OrderByDescending(r => r.SampledAtUtc).FirstOrDefault();
            _lastUpdatedText.Text = latest == null
                ? "마지막 업데이트 —"
                : "마지막 업데이트 " + latest.SampledAtUtc.ToLocalTime().ToString("HH:mm:ss");
        }

        private void SetFooter(string message)
        {
            if (_footerText != null)
                _footerText.Text = message;
        }

        private static string IconFor(DeviceProfile profile)
        {
            string icon = (profile.Icon ?? profile.Category ?? string.Empty).ToLowerInvariant();
            switch (icon)
            {
                case "headset": return "🎧";
                case "keyboard": return "⌨";
                case "mouse": return "🖱";
                case "gamepad": return "🎮";
                default: return "◆";
            }
        }

        private sealed class DeviceCardView
        {
            private readonly DeviceProfile _profile;
            private readonly Ellipse _stateDot;
            private readonly TextBlock _statusText;
            private readonly TextBlock _valueText;
            private readonly TextBlock _levelText;
            private readonly TextBlock _chargeText;
            private readonly TextBlock _detailText;
            private readonly TextBlock _sampleText;
            private readonly Border _barFill;
            private readonly Grid _barHost;
            private double _barRatio;

            internal Border Root { get; private set; }

            internal DeviceCardView(DeviceProfile profile)
            {
                _profile = profile;
                Grid content = new Grid();
                content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                Grid header = new Grid();
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                TextBlock icon = UiFactory.Text(IconFor(profile), 21, UiFactory.Accent, FontWeights.Normal);
                icon.HorizontalAlignment = HorizontalAlignment.Left;
                header.Children.Add(icon);
                TextBlock name = UiFactory.Text(profile.DisplayName, 15, UiFactory.PrimaryText, FontWeights.SemiBold);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.TextWrapping = TextWrapping.NoWrap;
                Grid.SetColumn(name, 1);
                header.Children.Add(name);

                StackPanel statusPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                _stateDot = new Ellipse { Width = 7, Height = 7, Fill = UiFactory.Offline, Margin = new Thickness(0, 1, 6, 0) };
                _statusText = UiFactory.Text("확인 중", 11, UiFactory.SecondaryText, FontWeights.Normal);
                statusPanel.Children.Add(_stateDot);
                statusPanel.Children.Add(_statusText);
                Grid.SetColumn(statusPanel, 2);
                header.Children.Add(statusPanel);
                content.Children.Add(header);

                Grid reading = new Grid { Margin = new Thickness(0, 15, 0, 10) };
                reading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                reading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                StackPanel valuePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
                _valueText = UiFactory.Text("—", 32, UiFactory.PrimaryText, FontWeights.Bold);
                _levelText = UiFactory.Text("잔량 확인 중", 11, UiFactory.SecondaryText, FontWeights.Normal);
                _levelText.Margin = new Thickness(10, 0, 0, 5);
                valuePanel.Children.Add(_valueText);
                valuePanel.Children.Add(_levelText);
                reading.Children.Add(valuePanel);
                _chargeText = UiFactory.Text("", 11, UiFactory.SecondaryText, FontWeights.SemiBold);
                _chargeText.HorizontalAlignment = HorizontalAlignment.Right;
                _chargeText.VerticalAlignment = VerticalAlignment.Bottom;
                _chargeText.Margin = new Thickness(12, 0, 0, 5);
                Grid.SetColumn(_chargeText, 1);
                reading.Children.Add(_chargeText);
                Grid.SetRow(reading, 1);
                content.Children.Add(reading);

                _barHost = new Grid
                {
                    Height = 7,
                    Background = UiFactory.RaisedBackground,
                    ClipToBounds = true
                };
                _barFill = new Border
                {
                    Height = 7,
                    Width = 0,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = UiFactory.Offline,
                    CornerRadius = new CornerRadius(4)
                };
                _barHost.Children.Add(_barFill);
                _barHost.SizeChanged += delegate { ResizeBar(); };
                Grid.SetRow(_barHost, 2);
                content.Children.Add(_barHost);

                _detailText = UiFactory.Text("첫 상태를 조회하고 있습니다.", 11, UiFactory.SecondaryText, FontWeights.Normal);
                _detailText.Margin = new Thickness(0, 12, 0, 0);
                _detailText.MaxHeight = 34;
                _detailText.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetRow(_detailText, 3);
                content.Children.Add(_detailText);

                _sampleText = UiFactory.Text("업데이트 대기 중", 10, UiFactory.MutedText, FontWeights.Normal);
                _sampleText.Margin = new Thickness(0, 7, 0, 0);
                Grid.SetRow(_sampleText, 4);
                content.Children.Add(_sampleText);

                Root = UiFactory.Card(content, new Thickness(6));
                Root.MinHeight = 194;
                Root.Visibility = Visibility.Collapsed;
            }

            internal void Update(BatteryReading reading)
            {
                Root.Visibility = reading.IsPresent
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                Brush color = StatusColor(reading);
                _stateDot.Fill = color;
                _statusText.Text = reading.StatusText;
                _statusText.Foreground = color;

                if (reading.Percent.HasValue)
                {
                    _valueText.Text = (reading.IsApproximate ? "약 " : "") + reading.Percent.Value + "%";
                    _levelText.Text = "배터리 · " + BandLabel(reading.Band);
                    _barRatio = Math.Max(0, Math.Min(1, reading.Percent.Value / 100.0));
                }
                else if (reading.Connection == DeviceConnectionState.Connected && reading.Band != BatteryLevelBand.Unknown)
                {
                    _valueText.Text = BandLabel(reading.Band);
                    _levelText.Text = reading.IsApproximate ? "단계 정보" : "배터리 상태";
                    _barRatio = BandRatio(reading.Band);
                }
                else
                {
                    _valueText.Text = "—";
                    _levelText.Text = ConnectionLabel(reading.Connection);
                    _barRatio = 0;
                }

                _valueText.Foreground = color;
                _barFill.Background = color;
                _barFill.Opacity = reading.IsStale ? 0.55 : 1.0;
                ResizeBar();

                _chargeText.Text = ChargeLabel(reading.Charge);
                _chargeText.Foreground = reading.Charge == DeviceChargeState.Charging ? UiFactory.Accent : UiFactory.SecondaryText;
                _detailText.Text = string.IsNullOrWhiteSpace(reading.DetailText) ? reading.StatusText : reading.DetailText;
                _sampleText.Text = (reading.IsStale ? "이전 값 · " : "업데이트 · ") + reading.SampledAtUtc.ToLocalTime().ToString("HH:mm:ss");
                _sampleText.Foreground = reading.IsStale ? UiFactory.Warning : UiFactory.MutedText;
                Root.ToolTip = _profile.DisplayName + "\n" + reading.StatusText + "\n" + reading.DetailText;
            }

            private void ResizeBar()
            {
                _barFill.Width = Math.Max(0, _barHost.ActualWidth * _barRatio);
            }

            private static Brush StatusColor(BatteryReading reading)
            {
                if (reading.Connection == DeviceConnectionState.Error)
                    return UiFactory.Danger;
                if (reading.Connection != DeviceConnectionState.Connected)
                    return UiFactory.Offline;
                if (reading.Charge == DeviceChargeState.Charging)
                    return UiFactory.Accent;
                switch (reading.Band)
                {
                    case BatteryLevelBand.Critical: return UiFactory.Danger;
                    case BatteryLevelBand.Low: return UiFactory.Warning;
                    case BatteryLevelBand.Medium: return UiFactory.Warning;
                    case BatteryLevelBand.High:
                    case BatteryLevelBand.Full: return UiFactory.Success;
                    default: return UiFactory.Accent;
                }
            }

            private static string BandLabel(BatteryLevelBand band)
            {
                switch (band)
                {
                    case BatteryLevelBand.Critical: return "매우 부족";
                    case BatteryLevelBand.Low: return "부족";
                    case BatteryLevelBand.Medium: return "보통";
                    case BatteryLevelBand.High: return "충분";
                    case BatteryLevelBand.Full: return "가득 참";
                    default: return "알 수 없음";
                }
            }

            private static double BandRatio(BatteryLevelBand band)
            {
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

            private static string ConnectionLabel(DeviceConnectionState state)
            {
                switch (state)
                {
                    case DeviceConnectionState.Connected: return "연결됨";
                    case DeviceConnectionState.Sleeping: return "절전 또는 전원 꺼짐";
                    case DeviceConnectionState.Disconnected: return "연결 안 됨";
                    case DeviceConnectionState.Busy: return "장치 사용 중";
                    case DeviceConnectionState.Unsupported: return "지원 모듈 없음";
                    case DeviceConnectionState.Error: return "조회 오류";
                    default: return "확인 중";
                }
            }

            private static string ChargeLabel(DeviceChargeState state)
            {
                switch (state)
                {
                    case DeviceChargeState.Charging: return "⚡ 충전 중";
                    case DeviceChargeState.Full: return "완충";
                    case DeviceChargeState.Discharging: return "배터리 사용 중";
                    case DeviceChargeState.NotApplicable: return "충전 상태 해당 없음";
                    default: return "";
                }
            }
        }
    }
}
