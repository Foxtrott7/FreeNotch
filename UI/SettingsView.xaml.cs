using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WindowsNotch.Core;
using WindowsNotch.Services;
using Forms = System.Windows.Forms;

namespace WindowsNotch.UI
{
    public class MonitorRow
    {
        public string DeviceName { get; set; } = "";
        public string Title { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public bool IsGamingMonitor { get; set; }
    }

    public class ModuleRow
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public bool Enabled { get; set; }
    }

    public partial class SettingsView : UserControl
    {
        public event Action? CloseRequested;
        private bool _isClosing;
        private bool _appliedOrSaved;

        private readonly AppSettings _s;
        private readonly ObservableCollection<MonitorRow> _monitors = new();
        private readonly ObservableCollection<ModuleRow> _modules = new();
        private readonly ObservableCollection<ModuleRow> _liveActivities = new();
        private readonly ObservableCollection<ModuleRow> _notificationApps = new();
        private readonly ObservableCollection<string> _excluded = new();
        private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private bool _loading = true;
        private int _activePageIndex;
        private int _activeRailIndex;
        private bool _editingHoverLayout;

        public SettingsView()
        {
            InitializeComponent();

            _s = SettingsService.Clone();
            DataContext = _s;
            WeatherCitiesBox.Text = AppSettings.FormatWeatherCities(_s.WeatherCities, _s.WeatherCity);

            LanguageBox.ItemsSource = LocalizationService.SupportedLanguages;
            LanguageBox.SelectedValue = LocalizationService.NormalizeCode(_s.Language);

            RefreshComboBoxes();
            UpdatePositionPickerVisuals();

            var screens = Forms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var sc = screens[i];
                _s.Monitors.TryGetValue(sc.DeviceName, out var ms);
                _monitors.Add(new MonitorRow
                {
                    DeviceName = sc.DeviceName,
                    Title = BuildMonitorTitle(i, sc),
                    Enabled = ms?.Enabled ?? true,
                    IsGamingMonitor = ms?.IsGamingMonitor ?? false
                });
            }
            MonitorList.ItemsSource = _monitors;

            foreach (var m in ModuleCatalog.All)
            {
                _s.Modules.TryGetValue(m.Id, out bool on);
                _modules.Add(new ModuleRow
                {
                    Id = m.Id,
                    Title = m.LocalizedLabel + (m.IsGaming ? L.T("settings.modules.gaming_suffix") : ""),
                    Enabled = on
                });
            }
            ModuleList.ItemsSource = _modules;

            foreach (var a in _s.FullscreenExcludedApps) _excluded.Add(a);
            ExcludedList.ItemsSource = _excluded;

            var liveIds = new[] { "media", "notifications", "widgets", "downloads" };
            foreach (var id in liveIds)
            {
                _liveActivities.Add(new ModuleRow
                {
                    Id = id,
                    Title = L.T($"settings.layout.live_{id}"),
                    Enabled = _s.LiveActivityModules.TryGetValue(id, out bool on)
                        ? on
                        : AppSettings.DefaultLiveActivity(id)
                });
            }
            LiveActivityList.ItemsSource = _liveActivities;

            NotchDesign.ApplySlimScrollBarStyle(PagesScroll);
            BuildNavRail();
            BuildLayoutModeSelector();
            BuildLayoutRows();
            UpdateLayoutPreview();

            _previewTimer.Tick += (_, _) =>
            {
                if (Page6.Visibility == Visibility.Visible && LayoutPreviewHost.Content is NotchLayoutView view)
                {
                    if (view.Refresh())
                        UpdateLayoutPreview();
                }
            };
            Loaded += (_, _) =>
            {
                _isClosing = false;
                AttachAllButtonHaptics(this);
                SystemMetricsService.Instance.ForceGpu = true;
                LocalizationService.LanguageChanged += OnLanguageChanged;
                _previewTimer.Start();
            };
            Unloaded += (_, _) =>
            {
                _previewTimer.Stop();
                LocalizationService.LanguageChanged -= OnLanguageChanged;
                SystemMetricsService.Instance.ForceGpu = false;
                if (!_appliedOrSaved)
                    LocalizationService.SetLanguage(SettingsService.Current.Language);
            };

            UseCustomAccentBox.IsChecked = _s.UseCustomAccent;
            CustomStartBox.Text = _s.CustomAccentStartHex;
            CustomEndBox.Text = _s.CustomAccentEndHex;
            BuildAccentSwatches();
            UpdateAccentPreview();
            ApplyWindowTheme();
            UpdatePositionPickerVisuals();

            var allNotifApps = _s.NotificationKnownApps
                .Union(_s.NotificationBlockedApps, StringComparer.OrdinalIgnoreCase)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var appKey in allNotifApps)
            {
                bool isBlocked = _s.NotificationBlockedApps.Any(b => string.Equals(b, appKey, StringComparison.OrdinalIgnoreCase));
                _notificationApps.Add(new ModuleRow { Id = appKey, Title = appKey, Enabled = !isBlocked });
            }
            NotificationAppFilterList.ItemsSource = _notificationApps;
            NotificationFilterEmptyHint.Visibility = _notificationApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateNotificationSoundLabel();

            _loading = false;
        }

        private static readonly (int PageIndex, string LabelKey, NotchIconKind Icon)[] NavItems =
        {
            (0, "settings.nav.general", NotchIconKind.Sparkle),
            (1, "settings.nav.appearance", NotchIconKind.Layers),
            (6, "settings.nav.layout", NotchIconKind.Clock),
            (2, "settings.nav.behavior", NotchIconKind.Bolt),
            (3, "settings.nav.fullscreen", NotchIconKind.Monitor),
            (4, "settings.nav.monitors", NotchIconKind.Dashboard),
            (5, "settings.nav.modules", NotchIconKind.CheckSquare),
            (8, "settings.nav.notifications", NotchIconKind.Bell),
            (7, "settings.nav.privacy", NotchIconKind.Shield),
        };

        private void BuildNavRail()
        {
            if (NavRail == null) return;
            NavRail.Children.Clear();

            var (_, _, _, accent) = ResolveSelectedAccentColors();
            var inactiveBrush = new SolidColorBrush(IsLightThemeSelected()
                ? Color.FromRgb(0x63, 0x63, 0x6E)
                : Color.FromRgb(0x8E, 0x8E, 0x93));
            var activeBrush = new SolidColorBrush(accent);

            for (int i = 0; i < NavItems.Length; i++)
            {
                var item = NavItems[i];
                bool selected = item.PageIndex == _activePageIndex;
                var btn = new Button
                {
                    Style = (Style)FindResource("RailButton"),
                    ToolTip = L.T(item.LabelKey),
                    Content = NotchDesign.CreateVectorIcon(
                        item.Icon,
                        16,
                        selected ? activeBrush : inactiveBrush,
                        selected ? 2.15 : 1.85)
                };

                int pageIdx = item.PageIndex;
                int railIdx = i;
                NotchDesign.AttachInteractiveScale(btn);
                btn.Click += (_, _) => SelectPage(pageIdx, railIdx);
                NavRail.Children.Add(btn);
            }
        }

        private static void AttachAllButtonHaptics(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Button btn)
                    NotchDesign.AttachInteractiveScale(btn);
                AttachAllButtonHaptics(child);
            }
        }

        private void OpenNotchLayout_Click(object sender, RoutedEventArgs e)
        {
            SelectPage(6, 2);
        }

        private void ResetCurrentLayout_Click(object sender, RoutedEventArgs e)
        {
            var defaults = _editingHoverLayout ? NotchInfo.DefaultHover() : NotchInfo.DefaultCollapsed();
            if (_editingHoverLayout)
                _s.HoverLayout = defaults;
            else
                _s.CollapsedLayout = defaults;
            BuildLayoutRows();
            UpdateLayoutPreview();
        }

        private void SelectPage(int pageIdx, int railIdx)
        {
            if (Page0 == null) return;
            _activePageIndex = pageIdx;
            _activeRailIndex = railIdx;

            var pages = new FrameworkElement[] { Page0, Page1, Page2, Page3, Page4, Page5, Page6, Page7, Page8 };
            for (int i = 0; i < pages.Length; i++)
                pages[i].Visibility = (i == pageIdx) ? Visibility.Visible : Visibility.Collapsed;

            PagesScroll?.ScrollToTop();
            NotchDesign.AnimateSubViewEnter(pages[pageIdx], 6);

            double targetY = railIdx * 40.0;
            if (NotchDesign.EffectiveAnimationsEnabled && NavIndicatorShift != null)
            {
                var anim = new DoubleAnimation(targetY, TimeSpan.FromMilliseconds(NotchDesign.MicroTransitionMs))
                {
                    EasingFunction = NotchDesign.CreateSpringEase(0.45)
                };
                Timeline.SetDesiredFrameRate(anim, Math.Clamp(_s.TargetFrameRate, 60, 240));
                NavIndicatorShift.BeginAnimation(TranslateTransform.YProperty, anim);
            }
            else if (NavIndicatorShift != null)
            {
                NavIndicatorShift.BeginAnimation(TranslateTransform.YProperty, null);
                NavIndicatorShift.Y = targetY;
            }

            BuildNavRail();
            if (pageIdx == 6)
                UpdateLayoutPreview();
        }

        private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || LanguageBox.SelectedValue is not string code) return;
            _s.Language = LocalizationService.NormalizeCode(code);
            LocalizationService.SetLanguage(_s.Language);
        }

        private void OnLanguageChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(OnLanguageChanged);
                return;
            }
            bool prevLoading = _loading;
            _loading = true;
            try
            {
                RefreshComboBoxes();
                var screens = Forms.Screen.AllScreens;
                for (int i = 0; i < _monitors.Count && i < screens.Length; i++)
                    _monitors[i].Title = BuildMonitorTitle(i, screens[i]);
                MonitorList.ItemsSource = null;
                MonitorList.ItemsSource = _monitors;

                foreach (var row in _modules)
                {
                    var def = ModuleCatalog.All.FirstOrDefault(m => m.Id == row.Id);
                    if (def != null)
                        row.Title = def.LocalizedLabel + (def.IsGaming ? L.T("settings.modules.gaming_suffix") : "");
                }
                ModuleList.ItemsSource = null;
                ModuleList.ItemsSource = _modules;

                foreach (var row in _liveActivities)
                    row.Title = L.T($"settings.layout.live_{row.Id}");
                LiveActivityList.ItemsSource = null;
                LiveActivityList.ItemsSource = _liveActivities;

                BuildNavRail();
                BuildLayoutModeSelector();
                BuildLayoutRows();
                UpdatePositionPickerVisuals();
                UpdateNotificationSoundLabel();
            }
            finally { _loading = prevLoading; }
        }

        private void RefreshComboBoxes()
        {
            int profileIdx = ProfileBox.SelectedIndex >= 0 ? ProfileBox.SelectedIndex : (int)_s.Profile;
            ProfileBox.ItemsSource = new[]
            {
                L.T("settings.general.profile_work"),
                L.T("settings.general.profile_gaming"),
                L.T("settings.general.profile_complete")
            };
            ProfileBox.SelectedIndex = profileIdx;

            int modeIdx = ModeBox.SelectedIndex >= 0 ? ModeBox.SelectedIndex : (int)_s.Mode;
            ModeBox.ItemsSource = new[]
            {
                L.T("settings.behavior.mode_hover_click"),
                L.T("settings.behavior.mode_hover"),
                L.T("settings.behavior.mode_click")
            };
            ModeBox.SelectedIndex = modeIdx;

            int idleVisIdx = IdleVisibilityBox.SelectedIndex >= 0 ? IdleVisibilityBox.SelectedIndex : (int)_s.IdleVisibility;
            IdleVisibilityBox.ItemsSource = new[]
            {
                L.T("settings.behavior.idle_always_visible"),
                L.T("settings.behavior.idle_peek_when_empty"),
                L.T("settings.behavior.idle_always_peek")
            };
            IdleVisibilityBox.SelectedIndex = idleVisIdx;

            int styleIdx = StyleBox.SelectedIndex >= 0 ? StyleBox.SelectedIndex : (int)_s.Style;
            StyleBox.ItemsSource = new[]
            {
                L.T("settings.appearance.style_docked"),
                L.T("settings.appearance.style_floating")
            };
            StyleBox.SelectedIndex = styleIdx;

            int fpsIdx = FrameRateBox.SelectedIndex >= 0
                ? FrameRateBox.SelectedIndex
                : _s.TargetFrameRate switch { <= 60 => 0, <= 120 => 1, <= 144 => 2, _ => 3 };
            FrameRateBox.ItemsSource = new[]
            {
                L.T("settings.appearance.fps_60"),
                L.T("settings.appearance.fps_120"),
                L.T("settings.appearance.fps_144"),
                L.T("settings.appearance.fps_240")
            };
            FrameRateBox.SelectedIndex = fpsIdx;

            int speedIdx = AnimationSpeedBox.SelectedIndex >= 0
                ? AnimationSpeedBox.SelectedIndex
                : _s.AnimationSpeedScale switch { < 0.85 => 0, < 1.15 => 1, < 1.48 => 2, _ => 3 };
            AnimationSpeedBox.ItemsSource = new[]
            {
                L.T("settings.appearance.speed_fast"),
                L.T("settings.appearance.speed_balanced"),
                L.T("settings.appearance.speed_calm"),
                L.T("settings.appearance.speed_very_calm")
            };
            AnimationSpeedBox.SelectedIndex = speedIdx;
        }

        private static string BuildMonitorTitle(int index, Forms.Screen sc)
            => L.F("settings.monitors.monitor_row", index + 1, sc.Bounds.Width, sc.Bounds.Height)
               + (sc.Primary ? L.T("settings.monitors.primary_suffix") : "");

        private void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || ProfileBox.SelectedIndex < 0) return;

            var profile = (ProfileType)ProfileBox.SelectedIndex;
            foreach (var row in _modules)
                row.Enabled = ModuleCatalog.DefaultEnabled(profile, row.Id);

            // Liste neu binden, damit die Checkboxen aktualisiert werden
            ModuleList.ItemsSource = null;
            ModuleList.ItemsSource = _modules;
        }

        private void AddExcluded_Click(object sender, RoutedEventArgs e)
        {
            string name = ExcludedInput.Text.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (name.Length == 0) return;
            if (!_excluded.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
                _excluded.Add(name);
            ExcludedInput.Clear();
        }

        private void RemoveExcluded_Click(object sender, RoutedEventArgs e)
        {
            if (ExcludedList.SelectedItem is string s) _excluded.Remove(s);
        }

        private void RestartOnboarding_Click(object sender, RoutedEventArgs e)
        {
            NotchDesign.RunAfterPressFeedback(() =>
            {
                CloseRequested?.Invoke();
                (Application.Current as App)?.Manager?.OpenOnboarding();
            });
        }

        private void ExportSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = L.T("settings.general.export_dialog_title"),
                Filter = L.T("settings.general.json_filter"),
                FileName = "windows-notch-settings.json"
            };
            if (dlg.ShowDialog() == true)
                SettingsService.ExportToFile(dlg.FileName);
        }

        private void ImportSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L.T("settings.general.import_dialog_title"),
                Filter = L.T("settings.general.json_filter")
            };
            if (dlg.ShowDialog() == true && SettingsService.ImportFromFile(dlg.FileName))
            {
                _appliedOrSaved = true;
                CloseRequested?.Invoke();
            }
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            ApplySettingsFromUi();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;
            _isClosing = true;
            ApplySettingsFromUi();
            NotchDesign.RunAfterPressFeedback(() => CloseRequested?.Invoke());
        }

        private void ApplySettingsFromUi()
        {
            _appliedOrSaved = true;
            if (LanguageBox.SelectedValue is string langCode)
                _s.Language = LocalizationService.NormalizeCode(langCode);
            _s.Profile = (ProfileType)Math.Max(0, ProfileBox.SelectedIndex);
            _s.Mode = (InteractionMode)Math.Max(0, ModeBox.SelectedIndex);
            _s.IdleVisibility = (IdleVisibilityMode)Math.Max(0, IdleVisibilityBox.SelectedIndex);
            _s.Style = (NotchStyle)Math.Max(0, StyleBox.SelectedIndex);
            _s.Theme = NotchTheme.Dark;
            _s.TargetFrameRate = FrameRateBox.SelectedIndex switch
            {
                0 => 60,
                1 => 120,
                2 => 144,
                3 => 240,
                _ => 120
            };
            _s.AnimationSpeedScale = AnimationSpeedBox.SelectedIndex switch
            {
                0 => 0.70,
                1 => 1.00,
                2 => 1.30,
                3 => 1.65,
                _ => 1.00
            };
            _s.Modules = _modules.ToDictionary(m => m.Id, m => m.Enabled);
            _s.LiveActivityModules = _liveActivities.ToDictionary(m => m.Id, m => m.Enabled);
            _s.CollapsedLayout = NotchInfo.Sanitize(_s.CollapsedLayout);
            _s.HoverLayout = NotchInfo.Sanitize(_s.HoverLayout);
            _s.Monitors = _monitors.ToDictionary(
                m => m.DeviceName,
                m => new MonitorSetting { Enabled = m.Enabled, IsGamingMonitor = m.IsGamingMonitor });
            _s.FullscreenExcludedApps = _excluded.ToList();
            _s.NotificationBlockedApps = _notificationApps
                .Where(a => !a.Enabled)
                .Select(a => a.Id)
                .ToList();
            _s.UseCustomAccent = UseCustomAccentBox.IsChecked == true;
            _s.CustomAccentStartHex = NotchDesign.NormalizeHex(CustomStartBox.Text, "#2822A8");
            _s.CustomAccentEndHex = NotchDesign.NormalizeHex(CustomEndBox.Text, "#9333EA");
            _s.SetWeatherCities(AppSettings.ParseWeatherCities(WeatherCitiesBox.Text));

            SettingsService.Update(SettingsService.Clone(_s));
            WeatherService.Instance.InvalidateAndRefresh();
            LyricsService.Instance.InvalidateAndRefresh();
        }

        private void PositionSpot_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag && Enum.TryParse<NotchPosition>(tag, out var pos))
            {
                _s.Position = pos;
                UpdatePositionPickerVisuals();
            }
        }

        private void UpdatePositionPickerVisuals()
        {
            PositionNameText.Text = _s.Position switch
            {
                NotchPosition.TopLeft => L.T("settings.appearance.pos_tl"),
                NotchPosition.TopCenter => L.T("settings.appearance.pos_tc_default"),
                NotchPosition.TopRight => L.T("settings.appearance.pos_tr"),
                NotchPosition.LeftCenter => L.T("settings.appearance.pos_lc"),
                NotchPosition.RightCenter => L.T("settings.appearance.pos_rc"),
                NotchPosition.BottomLeft => L.T("settings.appearance.pos_bl"),
                NotchPosition.BottomCenter => L.T("settings.appearance.pos_bc"),
                NotchPosition.BottomRight => L.T("settings.appearance.pos_br"),
                _ => L.T("settings.appearance.pos_tc")
            };

            var (_, _, _, accent) = ResolveSelectedAccentColors();
            bool isLight = IsLightThemeSelected();
            var activeBrush = new SolidColorBrush(accent);
            var inactiveBrush = new SolidColorBrush(isLight
                ? Color.FromRgb(0xD2, 0xD2, 0xDC)
                : Color.FromRgb(0x2C, 0x2C, 0x38));
            var selectedBorder = isLight ? new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18)) : Brushes.White;
            var inactiveBorder = new SolidColorBrush(isLight
                ? Color.FromArgb(0x28, 0x00, 0x00, 0x00)
                : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

            foreach (var child in PositionPickerGrid.Children)
            {
                if (child is not Button btn || btn.Tag is not string tag || !Enum.TryParse<NotchPosition>(tag, out var pos))
                    continue;

                bool selected = pos == _s.Position;

                var template = new ControlTemplate(typeof(Button));
                var factory = new FrameworkElementFactory(typeof(Border));
                factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
                factory.SetValue(Border.BackgroundProperty, selected ? activeBrush : inactiveBrush);
                factory.SetValue(Border.BorderBrushProperty, selected ? selectedBorder : inactiveBorder);
                factory.SetValue(Border.BorderThicknessProperty, new Thickness(selected ? 1.5 : 1));
                template.VisualTree = factory;
                btn.Template = template;
            }
        }

        private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsNotch");
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;
            _isClosing = true;
            NotchDesign.RunAfterPressFeedback(() => CloseRequested?.Invoke());
        }

        private void UpdateNotificationSoundLabel()
        {
            NotificationSoundPathText.Text = string.IsNullOrWhiteSpace(_s.NotificationSoundPath)
                ? L.T("settings.notifications.default_sound")
                : Path.GetFileName(_s.NotificationSoundPath);
        }

        private void PickNotificationSound_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L.T("settings.notifications.sound_dialog_title"),
                Filter = L.T("settings.notifications.sound_filter"),
                CheckFileExists = true
            };
            if (dlg.ShowDialog() == true)
            {
                _s.NotificationSoundPath = dlg.FileName;
                UpdateNotificationSoundLabel();
            }
        }

        private void ResetNotificationSound_Click(object sender, RoutedEventArgs e)
        {
            _s.NotificationSoundPath = string.Empty;
            UpdateNotificationSoundLabel();
        }

        private void OpenWindowsDnd_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private void PagesScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta == 0) return;

            var target = FindScrollableTarget(e.OriginalSource as DependencyObject, e.Delta);
            if (target == null) return;

            double step = CalculateWheelScrollStep(e.Delta, target.ViewportHeight, SystemParameters.WheelScrollLines);
            if (Math.Abs(step) < 0.01) return;

            target.ScrollToVerticalOffset(Math.Clamp(target.VerticalOffset + step, 0.0, target.ScrollableHeight));
            e.Handled = true;
        }

        private ScrollViewer? FindScrollableTarget(DependencyObject? source, int delta)
        {
            var cur = source;
            while (cur != null && !ReferenceEquals(cur, PagesScroll))
            {
                if (cur is ScrollViewer inner && inner.ScrollableHeight > 0.5)
                {
                    bool canScroll = delta < 0
                        ? inner.VerticalOffset < inner.ScrollableHeight - 0.5
                        : inner.VerticalOffset > 0.5;
                    if (canScroll)
                        return inner;
                }

                cur = cur is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(cur)
                    : LogicalTreeHelper.GetParent(cur);
            }

            return PagesScroll.ScrollableHeight > 0.5 ? PagesScroll : null;
        }

        public static double CalculateWheelScrollStep(int delta, double viewportHeight, int wheelScrollLines)
        {
            if (delta == 0) return 0.0;

            double pixelsPerNotch = wheelScrollLines < 0
                ? Math.Max(120.0, viewportHeight * 0.65)
                : Math.Clamp(wheelScrollLines, 1, 6) * 12.0;

            return Math.Clamp((-delta / 120.0) * pixelsPerNotch, -96.0, 96.0);
        }
    }
}
