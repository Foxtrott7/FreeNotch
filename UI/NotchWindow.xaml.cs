using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WindowsNotch.Core;
using WindowsNotch.Modules.ScreenCapture;
using WindowsNotch.Services;
using Forms = System.Windows.Forms;

namespace WindowsNotch.UI
{
    public enum NotchState { Collapsed, Preview, Expanded }

    /// <summary>Eine Notch auf genau einem Monitor.</summary>
    public partial class NotchWindow : Window
    {
        private const double WindowMargin = 40; // Fenster ist so viel größer als die geöffnete Notch
        // Größe der In-Notch-Einstellungen; das Fenster wird mindestens so groß (sonst wird der Inhalt abgeschnitten)
        private const double SettingsWidth = 640;
        private const double SettingsHeight = 440;
        private const double PowerMenuWidth = 300;

        private readonly ModuleHost _moduleHost;
        private NotchState _state = NotchState.Collapsed;
        private bool _suppressed;
        private int _suppressAnimVersion;
        private readonly DispatcherTimer _hoverTimer = new();
        private readonly DispatcherTimer _layoutTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private NotchLayoutView? _idleView;
        private INotchModule? _collapsedLiveModule;
        private readonly DispatcherTimer _privacyTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
        private readonly DispatcherTimer _fileTrayAutoCloseTimer = new();
        private int _privacyMask; // Bit 1 = Aufnahme (rot), Bit 2 = Kamera (grün)
        private bool _privacyAnimActive;
        private INotchModule? _selectedModule;
        private TopTabJiggleController? _tabJiggleController;
        private bool _isSettingsOpen;
        private bool _isOnboardingOpen;
        private bool _isTourActive;
        private List<INotchModule> _tourModules = new();
        private int _tourIndex;
        private bool _tourPaused;
        private DispatcherTimer? _tourTimer;
        private DispatcherTimer? _tourDemoTimer;
        private OnboardingTourBar? _tourBar;
        private const double OnboardingWidth = 600;
        private const double OnboardingHeight = 395;
        private double _lastCollapsedW = double.NaN;
        private double _lastCollapsedH = double.NaN;
        private bool? _lastCollapsedHasLive;
        private bool? _lastCollapsedPeek;

        public Forms.Screen Screen { get; }
        public string DeviceName => Screen.DeviceName;
        public bool IsSuppressed => _suppressed;
        public bool IsSettingsOpen => _isSettingsOpen;

        public void RefreshFromSettings()
        {
            ApplySettingsInstant();
            Reposition();
        }

        public NotchWindow(Forms.Screen screen, ModuleHost moduleHost)
        {
            InitializeComponent();
            Screen = screen;
            _moduleHost = moduleHost;

            _moduleHost.ActiveModulesChanged += OnModuleHostActiveModulesChanged;
            _moduleHost.StateChanged += OnModuleHostStateChanged;
            _moduleHost.CollapseRequested += OnModuleHostCollapseRequested;
            LocalizationService.LanguageChanged += OnLanguageChanged;
            Closed += (_, _) =>
            {
                _layoutTimer.Stop();
                _privacyTimer.Stop();
                _fileTrayAutoCloseTimer.Stop();
                _tabJiggleController?.Detach();
                _tabJiggleController = null;
                _moduleHost.ActiveModulesChanged -= OnModuleHostActiveModulesChanged;
                _moduleHost.StateChanged -= OnModuleHostStateChanged;
                _moduleHost.CollapseRequested -= OnModuleHostCollapseRequested;
                LocalizationService.LanguageChanged -= OnLanguageChanged;
            };

            _hoverTimer.Tick += HoverTimer_Tick;
            _fileTrayAutoCloseTimer.Tick += FileTrayAutoCloseTimer_Tick;
            NotchBorder.AddHandler(DragDrop.DragLeaveEvent, new DragEventHandler((_, _) =>
            {
                if (_state == NotchState.Expanded && _selectedModule?.Id == "filetray" && !IsActive)
                    ScheduleFileTrayAutoClose(FileTrayAutoCloseDelayMs);
            }), handledEventsToo: true);
            NotchBorder.AddHandler(DragDrop.DropEvent, new DragEventHandler((_, _) =>
            {
                if (_state == NotchState.Expanded && _selectedModule?.Id == "filetray" && !IsActive)
                    ScheduleFileTrayAutoClose(FileTrayAutoCloseDelayMs);
            }), handledEventsToo: true);
            _privacyTimer.Tick += (_, _) => UpdatePrivacyIndicator();
            _privacyTimer.Start();
            _layoutTimer.Tick += LayoutTimer_Tick;
            _layoutTimer.Start();
            NotchBorder.SizeChanged += (_, _) => UpdateShellGeometry();

            // Handle früh erzeugen, damit Position und Größe gesetzt werden können
            new WindowInteropHelper(this).EnsureHandle();
            ApplySettingsInstant();
            Reposition();

            Loaded += (_, _) => Reposition();
        }

        private static AppSettings S => SettingsService.Current;

        // ---------- Fenster-Setup ----------

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            // Nicht in Alt+Tab anzeigen
            int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex | NativeMethods.WS_EX_TOOLWINDOW);
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            Reposition();
        }

        /// <summary>Setzt das Fenster in physischen Pixeln an die gewählte Position auf den eigenen Monitor.</summary>
        public void Reposition()
        {
            IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
            double scale = GetMonitorScale();
            double hostW = Math.Max(Math.Max(S.ExpandedWidth, 620), SettingsWidth + 24) + WindowMargin;
            double hostH = Math.Max(Math.Max(S.ExpandedHeight, 440), SettingsHeight + 24) + WindowMargin;
            int wPx = (int)Math.Round(hostW * scale);
            int hPx = (int)Math.Round(hostH * scale);
            var b = Screen.Bounds;

            int x = S.Position switch
            {
                NotchPosition.TopLeft or NotchPosition.LeftCenter or NotchPosition.BottomLeft => b.Left,
                NotchPosition.TopRight or NotchPosition.RightCenter or NotchPosition.BottomRight => b.Right - wPx,
                _ => b.Left + (b.Width - wPx) / 2
            };
            int y = S.Position switch
            {
                NotchPosition.BottomLeft or NotchPosition.BottomCenter or NotchPosition.BottomRight => b.Bottom - hPx,
                NotchPosition.LeftCenter or NotchPosition.RightCenter => b.Top + (b.Height - hPx) / 2,
                _ => b.Top
            };

            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, wPx, hPx,
                NativeMethods.SWP_NOACTIVATE);

            NotchBorder.HorizontalAlignment = S.Position switch
            {
                NotchPosition.TopLeft or NotchPosition.LeftCenter or NotchPosition.BottomLeft => HorizontalAlignment.Left,
                NotchPosition.TopRight or NotchPosition.RightCenter or NotchPosition.BottomRight => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Center
            };
            NotchBorder.VerticalAlignment = S.Position switch
            {
                NotchPosition.BottomLeft or NotchPosition.BottomCenter or NotchPosition.BottomRight => VerticalAlignment.Bottom,
                NotchPosition.LeftCenter or NotchPosition.RightCenter => VerticalAlignment.Center,
                _ => VerticalAlignment.Top
            };

            NotchShadow.Direction = S.Position switch
            {
                NotchPosition.BottomLeft or NotchPosition.BottomCenter or NotchPosition.BottomRight => 90,
                NotchPosition.LeftCenter => 0,
                NotchPosition.RightCenter => 180,
                _ => 270
            };

            ExpandedPanel.RenderTransformOrigin = S.Position switch
            {
                NotchPosition.BottomLeft or NotchPosition.BottomCenter or NotchPosition.BottomRight => new Point(0.5, 1),
                NotchPosition.LeftCenter => new Point(0, 0.5),
                NotchPosition.RightCenter => new Point(1, 0.5),
                _ => new Point(0.5, 0)
            };

            Width = hostW;
            Height = hostH;
        }

        private double GetMonitorScale()
        {
            var b = Screen.Bounds;
            var pt = new NativeMethods.POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 };
            IntPtr mon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (mon != IntPtr.Zero &&
                NativeMethods.GetDpiForMonitor(mon, 0, out uint dpiX, out _) == 0 && dpiX > 0)
                return dpiX / 96.0;
            return 1.0;
        }

        /// <summary>Hält das Fenster über anderen Topmost-Fenstern (z. B. Taskleiste).</summary>
        public void KeepOnTop()
        {
            if (_suppressed) return;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        private void ClearSuppressAnimations(double x = 0, double y = 0, double opacity = 1.0)
        {
            NotchSuppressTransform.BeginAnimation(TranslateTransform.XProperty, null);
            NotchSuppressTransform.BeginAnimation(TranslateTransform.YProperty, null);
            NotchSuppressTransform.X = x;
            NotchSuppressTransform.Y = y;

            NotchBorder.BeginAnimation(OpacityProperty, null);
            NotchBorder.Opacity = opacity;
        }

        /// <summary>Ausblenden durch Vollbildregeln oder manuelles Ausblenden (mit weicher Rand-Slide-Animation).</summary>
        public void SetSuppressed(bool suppressed, bool instant = false)
        {
            bool wasSuppressed = _suppressed;
            if (wasSuppressed == suppressed && !instant) return;
            _suppressed = suppressed;

            int version = ++_suppressAnimVersion;
            IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();

            if (suppressed)
            {
                _hoverTimer.Stop();
                _fileTrayAutoCloseTimer.Stop();
                NotchBorder.IsHitTestVisible = false;

                if (instant || !IsLoaded || !NotchDesign.EffectiveAnimationsEnabled)
                {
                    _state = NotchState.Collapsed;
                    _isSettingsOpen = false;
                    _isOnboardingOpen = false;
                    StopTourInternal();
                    _tabJiggleController?.ExitJiggleMode();
                    UpdateSettingsButtonState();
                    ApplySettingsInstant();
                    NotchBorder.IsHitTestVisible = false;
                    ClearSuppressAnimations();
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
                    return;
                }

                if (_state != NotchState.Collapsed)
                    SetState(NotchState.Collapsed);

                var (hideX, hideY) = GetSuppressSlideOffset();
                var hideDur = TimeSpan.FromMilliseconds(NotchDesign.CollapseDurationMs);
                var hideEase = NotchDesign.CreateEaseInOut();
                var fadeEase = new CubicEase { EasingMode = EasingMode.EaseIn };

                var animX = CreateAnim(hideX, hideDur, hideEase, activeTransition: true);
                var animY = CreateAnim(hideY, hideDur, hideEase, activeTransition: true);
                var animOp = CreateAnim(0.0, hideDur, fadeEase, activeTransition: true);
                animOp.Completed += (_, _) =>
                {
                    if (version != _suppressAnimVersion || !_suppressed) return;
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
                    ClearSuppressAnimations();
                    UpdatePrivacyIndicator();
                };

                NotchSuppressTransform.BeginAnimation(TranslateTransform.XProperty, animX);
                NotchSuppressTransform.BeginAnimation(TranslateTransform.YProperty, animY);
                NotchBorder.BeginAnimation(OpacityProperty, animOp);
            }
            else
            {
                ClearSuppressAnimations();

                if (instant || !IsLoaded || !NotchDesign.EffectiveAnimationsEnabled)
                {
                    if (wasSuppressed || _state == NotchState.Collapsed)
                        ApplySettingsInstant();
                    else
                        ClearSuppressAnimations(opacity: GetTargetNotchOpacity());
                    NotchBorder.IsHitTestVisible = true;
                    // Ohne Aktivierung anzeigen, damit kein Fokus gestohlen wird (z. B. aus einem Spiel)
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
                    Reposition();
                    return;
                }

                _state = NotchState.Collapsed;
                ApplySettingsInstant();

                var (hideX, hideY) = GetSuppressSlideOffset();
                NotchSuppressTransform.X = hideX;
                NotchSuppressTransform.Y = hideY;
                NotchBorder.Opacity = 0;
                NotchBorder.IsHitTestVisible = true;

                // Ohne Aktivierung anzeigen, damit kein Fokus gestohlen wird (z. B. aus einem Spiel)
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
                Reposition();

                // EaseOut ohne BackEase-Overshoot, damit angedockte Ohren niemals vom Monitorrand abheben
                var slideDur = TimeSpan.FromMilliseconds(NotchDesign.DrawerOpenDurationMs);
                var slideEase = NotchDesign.CreateEaseOut();
                var animX = CreateAnim(0, slideDur, slideEase, activeTransition: true);
                var animY = CreateAnim(0, slideDur, slideEase, activeTransition: true);
                animY.Completed += (_, _) =>
                {
                    if (version != _suppressAnimVersion || _suppressed) return;
                    ClearSuppressAnimations(opacity: GetTargetNotchOpacity());
                };

                NotchSuppressTransform.BeginAnimation(TranslateTransform.XProperty, animX);
                NotchSuppressTransform.BeginAnimation(TranslateTransform.YProperty, animY);

                var fadeDur = TimeSpan.FromMilliseconds(NotchDesign.MicroTransitionMs);
                var animOp = CreateAnim(GetTargetNotchOpacity(), fadeDur, NotchDesign.CreateEaseOut(), activeTransition: true);
                NotchBorder.BeginAnimation(OpacityProperty, animOp);
            }
        }

        // ---------- Zustände & Größen ----------

        public void Toggle()
        {
            if (_state == NotchState.Expanded)
            {
                SetState(NotchState.Collapsed);
            }
            else
            {
                SetState(NotchState.Expanded);
                Activate();
            }
        }

        /// <summary>Öffnet die Notch direkt auf dem gewünschten Modul-Tab.</summary>
        public void OpenModule(string moduleId)
        {
            var module = _moduleHost.ActiveModules.FirstOrDefault(m => m.Id == moduleId);
            if (module != null)
                SelectModule(module);

            if (_state != NotchState.Expanded)
            {
                SetState(NotchState.Expanded);
                Activate();
            }
        }

        /// <summary>Setzt Größe, Rundung, Theme und Inhalt ohne Animation auf den aktuellen Zustand.</summary>
        private void ApplySettingsInstant()
        {
            ApplyThemeResources();
            ApplyDockStyle();

            bool showPreview = _state == NotchState.Preview;
            if (_state == NotchState.Collapsed)
                showPreview = UpdateCollapsedLiveContent();
            else if (_state == NotchState.Preview)
                UpdatePreviewContent();
            else if (_state == NotchState.Expanded)
                BuildTabs();

            var (w, h) = SizeFor(_state);
            if (_state == NotchState.Collapsed)
            {
                _lastCollapsedW = w;
                _lastCollapsedH = h;
                _lastCollapsedHasLive = showPreview;
                _lastCollapsedPeek = IsCollapsedPeekActive;
            }
            else
            {
                _lastCollapsedW = double.NaN;
                _lastCollapsedH = double.NaN;
                _lastCollapsedHasLive = null;
                _lastCollapsedPeek = null;
            }

            NotchBorder.BeginAnimation(WidthProperty, null);
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Width = w;
            NotchBorder.Height = h;
            UpdateShellGeometry();

            SetOpacity(PreviewHost, showPreview ? 1 : 0, 0, animate: false);
            PreviewHost.IsHitTestVisible = _state != NotchState.Expanded;
            SetOpacity(ExpandedPanel, _state == NotchState.Expanded ? 1 : 0, 0, animate: false);
            ExpandedPanel.Visibility = _state == NotchState.Expanded ? Visibility.Visible : Visibility.Collapsed;
            ExpandedScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ExpandedScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ExpandedTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            ExpandedTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            ExpandedScale.ScaleX = _state == NotchState.Expanded ? 1.0 : 0.97;
            ExpandedScale.ScaleY = _state == NotchState.Expanded ? 1.0 : 0.97;
            var (tx, ty) = _state == NotchState.Expanded ? (0.0, 0.0) : GetCollapsedTranslateOffset();
            ExpandedTranslate.X = tx;
            ExpandedTranslate.Y = ty;
            ExpandedPanel.IsHitTestVisible = _state == NotchState.Expanded;

            if (!_suppressed)
            {
                ClearSuppressAnimations(opacity: GetTargetNotchOpacity());
                NotchBorder.IsHitTestVisible = true;
            }

            UpdatePrivacyIndicator();
        }

        private void SetState(NotchState target)
        {
            if (_state == target) return;
            _state = target;

            if (_state != NotchState.Expanded)
            {
                _fileTrayAutoCloseTimer.Stop();
                _tabJiggleController?.ExitJiggleMode();
                _isSettingsOpen = false;
                _isPowerMenuOpen = false;
                _isOnboardingOpen = false;
                StopTourInternal();
                UpdateSettingsButtonState();
                UpdatePowerButtonState();
                _selectedModule?.OnCollapsed();
            }

            bool hasLive = false;
            if (_state == NotchState.Collapsed)
            {
                hasLive = UpdateCollapsedLiveContent();
            }
            else if (_state == NotchState.Preview)
            {
                UpdatePreviewContent();
            }
            else if (_state == NotchState.Expanded)
            {
                BuildTabs();
            }

            UpdatePrivacyIndicator();

            if (!NotchDesign.EffectiveAnimationsEnabled)
            {
                ApplySettingsInstant();
                return;
            }

            var (w, h) = SizeFor(target);
            if (target == NotchState.Collapsed)
            {
                _lastCollapsedW = w;
                _lastCollapsedH = h;
                _lastCollapsedHasLive = hasLive;
                _lastCollapsedPeek = IsCollapsedPeekActive;
            }
            else
            {
                _lastCollapsedW = double.NaN;
                _lastCollapsedH = double.NaN;
                _lastCollapsedHasLive = null;
                _lastCollapsedPeek = null;
            }

            bool opening = target != NotchState.Collapsed;
            var duration = TimeSpan.FromMilliseconds(opening ? NotchDesign.SpringOpenDurationMs : NotchDesign.CollapseDurationMs);
            IEasingFunction ease = opening
                ? NotchDesign.CreateSpringEase(NotchDesign.SpringOpenAmplitude)
                : NotchDesign.CreateEaseInOut();

            if (target == NotchState.Expanded)
            {
                ExpandedPanel.Visibility = Visibility.Visible;
                ExpandedHost.BeginAnimation(OpacityProperty, null);
                ExpandedHost.Opacity = 1.0;
                ExpandedHost.RenderTransform = Transform.Identity;
            }

            ApplyDockStyle();
            NotchBorder.BeginAnimation(WidthProperty, CreateAnim(w, duration, ease, activeTransition: true));
            NotchBorder.BeginAnimation(HeightProperty, CreateAnim(h, duration, ease, activeTransition: true));

            bool showPreview = target == NotchState.Preview || (target == NotchState.Collapsed && hasLive);
            int previewFadeMs = showPreview ? NotchDesign.MicroTransitionMs : Math.Min(160, NotchDesign.MicroTransitionMs);
            int expandedFadeMs = opening
                ? NotchDesign.MicroTransitionMs
                : Math.Max(180, (int)Math.Round(NotchDesign.CollapseDurationMs * 0.88));
            var previewEase = opening ? NotchDesign.CreateEaseOut() : NotchDesign.CreateEaseInOut();
            var expandedEase = opening
                ? NotchDesign.CreateEaseOut()
                : NotchDesign.CreateEaseInOut();
            PreviewHost.IsHitTestVisible = target != NotchState.Expanded;
            SetOpacity(PreviewHost, showPreview ? 1 : 0, 0, animate: true, activeTransition: true, durationMs: previewFadeMs, ease: previewEase);
            NotchBorder.BeginAnimation(OpacityProperty, CreateAnim(GetTargetNotchOpacity(), TimeSpan.FromMilliseconds(previewFadeMs), previewEase, activeTransition: true));
            SetOpacity(ExpandedPanel, target == NotchState.Expanded ? 1 : 0,
                target == NotchState.Expanded ? 15 : 0, animate: true, activeTransition: true,
                durationMs: expandedFadeMs,
                ease: expandedEase,
                onCompleted: target != NotchState.Expanded ? () =>
                {
                    if (_state != NotchState.Expanded)
                        ExpandedPanel.Visibility = Visibility.Collapsed;
                } : null);

            double targetScale = target == NotchState.Expanded ? 1.0 : 0.94;
            var (targetX, targetY) = target == NotchState.Expanded ? (0.0, 0.0) : GetCollapsedTranslateOffset();
            var transformAnim = TimeSpan.FromMilliseconds(target == NotchState.Expanded ? NotchDesign.SpringOpenDurationMs : NotchDesign.CollapseDurationMs);
            ExpandedScale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateAnim(targetScale, transformAnim, ease, activeTransition: true));
            ExpandedScale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateAnim(targetScale, transformAnim, ease, activeTransition: true));
            ExpandedTranslate.BeginAnimation(TranslateTransform.XProperty, CreateAnim(targetX, transformAnim, ease, activeTransition: true));
            ExpandedTranslate.BeginAnimation(TranslateTransform.YProperty, CreateAnim(targetY, transformAnim, ease, activeTransition: true));

            ExpandedPanel.IsHitTestVisible = target == NotchState.Expanded;
        }

        private static void SetOpacity(UIElement element, double opacity, int delayMs, bool animate, bool activeTransition = true, int? durationMs = null, IEasingFunction? ease = null, Action? onCompleted = null)
        {
            if (animate)
            {
                var anim = CreateAnim(opacity, TimeSpan.FromMilliseconds(durationMs ?? NotchDesign.MicroTransitionMs), ease: ease, delayMs: delayMs, activeTransition: activeTransition);
                if (onCompleted != null)
                    anim.Completed += (_, _) => onCompleted();
                element.BeginAnimation(OpacityProperty, anim);
            }
            else
            {
                element.BeginAnimation(OpacityProperty, null);
                element.Opacity = opacity;
                onCompleted?.Invoke();
            }
        }

        // ---------- Modul-Vorschau (Hover) ----------

        private int GetEffectiveLivePriority(INotchModule m)
        {
            if (m.Id == "notifications" && m.Priority >= 100)
                return 165;
            if (m.Id == "downloads" && m.Priority >= 160)
                return 162;
            if (m.Id == "widgets" && m.Priority >= 105)
                return 160;
            if (m.Id == "media" && m.Priority >= 100)
                return 155;
            return m.Priority;
        }

        /// <summary>Modul mit aktivem Ereignis (Timer, Medien …): hat in der Hover-Kapsel Vorrang vor dem Hover-Layout.</summary>
        private INotchModule? GetEventPrimary()
        {
            var primary = _moduleHost.PrimaryModule;
            return primary != null && primary.Priority >= 100 ? primary : null;
        }

        private bool UpdateCollapsedLiveContent()
        {
            _collapsedLiveModule = null;
            _idleView = null;

            if (S.IdleVisibility == IdleVisibilityMode.AlwaysPeek)
            {
                if (PreviewHost.Content != null)
                    PreviewHost.Content = null;
                return false;
            }

            if (S.LiveActivitiesEnabled)
            {
                foreach (var module in _moduleHost.ActiveModules
                             .Where(m => S.IsLiveActivityEnabled(m.Id) && m.PreferredLiveWidth is > 0)
                             .OrderByDescending(GetEffectiveLivePriority))
                {
                    var liveView = _moduleHost.SafeCreateLiveView(module);
                    if (liveView != null)
                    {
                        _collapsedLiveModule = module;
                        if (!ReferenceEquals(PreviewHost.Content, liveView))
                            PreviewHost.Content = liveView;
                        return true;
                    }
                }
            }

            _idleView = NotchLayoutView.Create(S.CollapsedLayout);
            if (!ReferenceEquals(PreviewHost.Content, _idleView))
                PreviewHost.Content = _idleView;
            return _idleView != null;
        }

        /// <summary>Jede Sekunde: Uhrzeit/Messwerte im Layout aktualisieren; bei geänderter Breite oder Verfügbarkeit neu aufbauen.</summary>
        private void LayoutTimer_Tick(object? sender, EventArgs e)
        {
            if (_suppressed || _state == NotchState.Expanded || PreviewHost.Content is not NotchLayoutView view) return;

            try
            {
                if (!view.Refresh()) return;

                if (_state == NotchState.Collapsed)
                {
                    RefreshCollapsedLiveState();
                }
                else
                {
                    UpdatePreviewContent();
                    var (w, h) = SizeFor(NotchState.Preview);
                    var ease = NotchDesign.CreateEaseOut();
                    var duration = TimeSpan.FromMilliseconds(NotchDesign.MicroTransitionMs);
                    NotchBorder.BeginAnimation(WidthProperty, CreateAnim(w, duration, ease));
                    NotchBorder.BeginAnimation(HeightProperty, CreateAnim(h, duration, ease));
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private void RefreshCollapsedLiveState()
        {
            if (_state != NotchState.Collapsed) return;

            bool hasLive = UpdateCollapsedLiveContent();
            bool isPeek = IsCollapsedPeekActive;
            var (w, h) = SizeFor(NotchState.Collapsed);

            bool sizeChanged = double.IsNaN(_lastCollapsedW) ||
                               Math.Abs(_lastCollapsedW - w) > 0.5 ||
                               Math.Abs(_lastCollapsedH - h) > 0.5;
            bool liveChanged = _lastCollapsedHasLive != hasLive;
            bool peekChanged = _lastCollapsedPeek != isPeek;
            if (!sizeChanged && !liveChanged && !peekChanged)
                return;

            _lastCollapsedW = w;
            _lastCollapsedH = h;
            _lastCollapsedHasLive = hasLive;
            _lastCollapsedPeek = isPeek;

            if (!NotchDesign.EffectiveAnimationsEnabled)
            {
                NotchBorder.BeginAnimation(WidthProperty, null);
                NotchBorder.BeginAnimation(HeightProperty, null);
                NotchBorder.BeginAnimation(OpacityProperty, null);
                NotchBorder.Width = w;
                NotchBorder.Height = h;
                NotchBorder.Opacity = GetTargetNotchOpacity();
                SetOpacity(PreviewHost, hasLive ? 1 : 0, 0, animate: false);
                return;
            }

            var duration = TimeSpan.FromMilliseconds(NotchDesign.MicroTransitionMs);
            var ease = NotchDesign.CreateEaseOut();
            if (sizeChanged)
            {
                NotchBorder.BeginAnimation(WidthProperty, CreateAnim(w, duration, ease, activeTransition: hasLive));
                NotchBorder.BeginAnimation(HeightProperty, CreateAnim(h, duration, ease, activeTransition: hasLive));
            }
            if (liveChanged)
                SetOpacity(PreviewHost, hasLive ? 1 : 0, 0, animate: true, activeTransition: hasLive);
            if (liveChanged || peekChanged)
                NotchBorder.BeginAnimation(OpacityProperty, CreateAnim(GetTargetNotchOpacity(), duration, ease, activeTransition: hasLive));
        }

        private void UpdatePreviewContent()
        {
            _idleView = null;
            _collapsedLiveModule = null;

            var eventModule = GetEventPrimary();
            FrameworkElement? view = eventModule != null ? _moduleHost.SafeCreatePreviewView(eventModule) : null;
            var next = view ?? NotchLayoutView.Create(S.HoverLayout);
            if (!ReferenceEquals(PreviewHost.Content, next))
                PreviewHost.Content = next;
        }

        // ---------- Geöffnete Ansicht: Tabs & Modul-Host ----------

        private void BuildTabs()
        {
            _tabJiggleController?.Detach();
            _tabJiggleController = null;
            TabContainer.Children.Clear();

            var active = _moduleHost.ActiveModules;
            var tabOrder = S.TopTabOrder;
            var tabModules = active
                .Where(m => m.ShowInTabBar)
                .OrderBy(m =>
                {
                    int idx = tabOrder.IndexOf(m.Id);
                    return idx >= 0 ? idx : int.MaxValue;
                })
                .ToList();

            if (tabModules.Count == 0 && !_isSettingsOpen && !_isPowerMenuOpen)
            {
                EmptyModulesPanel.Visibility = Visibility.Visible;
                ExpandedHost.Visibility = Visibility.Collapsed;
                _selectedModule = null;
                return;
            }

            EmptyModulesPanel.Visibility = Visibility.Collapsed;
            ExpandedHost.Visibility = Visibility.Visible;

            if (tabModules.Count > 0 && (_selectedModule == null || !tabModules.Contains(_selectedModule)))
            {
                var primary = _moduleHost.PrimaryModule;
                _selectedModule = (primary != null && primary.ShowInTabBar && primary.Priority >= 100)
                    ? primary
                    : tabModules.FirstOrDefault(m => m.Id == "widgets")
                      ?? (primary != null && primary.ShowInTabBar ? primary : null)
                      ?? tabModules[0];
            }

            foreach (var module in tabModules)
            {
                bool isSel = !_isSettingsOpen && !_isPowerMenuOpen && module == _selectedModule;
                string modKey = $"module.{module.Id}";
                string modTip = L.T(modKey);
                if (modTip == modKey) modTip = module.Title;
                var tabBtn = new Button
                {
                    DataContext = module,
                    ToolTip = modTip,
                    Content = NotchDesign.CreateVectorIcon(
                        module.IconKind,
                        15,
                        isSel ? NotchDesign.TextPrimaryBrush : NotchDesign.TabIconInactiveBrush,
                        2.25),
                    Style = (Style)FindResource("TabButton"),
                    Tag = isSel ? "Selected" : null
                };

                var modRef = module;
                tabBtn.Click += (_, _) => SelectModule(modRef);
                TabContainer.Children.Add(tabBtn);
            }

            _tabJiggleController = new TopTabJiggleController(
                this,
                TabContainer,
                OnTopTabOrderChanged,
                () => NotchDesign.EffectiveAnimationsEnabled,
                () => Math.Clamp(S.TargetFrameRate, 60, 240));

            if (_isOnboardingOpen)
            {
                ShowOnboardingInHost();
            }
            else if (_isSettingsOpen)
            {
                ShowSettingsInHost();
            }
            else if (_isPowerMenuOpen)
            {
                ShowPowerMenuInHost();
            }
            else if (_selectedModule != null)
            {
                ExpandedHost.Content = _moduleHost.SafeCreateExpandedView(_selectedModule);
            }
            UpdateSettingsButtonState();
            UpdatePowerButtonState();
            UpdateKeepExpandedCloseButton();
            ScrollSelectedTabIntoView();
        }

        private void OnTopTabOrderChanged(IReadOnlyList<string> orderedIds)
        {
            SettingsService.UpdateQuiet(s =>
            {
                var current = s.TopTabOrder.ToList();
                var visibleSet = new HashSet<string>(orderedIds);
                int visIdx = 0;
                for (int i = 0; i < current.Count && visIdx < orderedIds.Count; i++)
                {
                    if (visibleSet.Contains(current[i]))
                        current[i] = orderedIds[visIdx++];
                }
                for (; visIdx < orderedIds.Count; visIdx++)
                {
                    if (!current.Contains(orderedIds[visIdx]))
                        current.Add(orderedIds[visIdx]);
                }
                s.TopTabOrder = current;
                s.Normalize();
            });
        }

        private void SelectModule(INotchModule module)
        {
            if (_tabJiggleController?.IsJiggleMode == true) return;

            int oldIndex = -1;
            int newIndex = -1;
            for (int i = 0; i < TabContainer.Children.Count; i++)
            {
                if (TabContainer.Children[i] is Button b && b.DataContext is INotchModule m)
                {
                    if (ReferenceEquals(m, _selectedModule)) oldIndex = i;
                    if (ReferenceEquals(m, module)) newIndex = i;
                }
            }
            double slideDir = (oldIndex >= 0 && newIndex >= 0 && newIndex != oldIndex)
                ? (newIndex > oldIndex ? 1.0 : -1.0)
                : 0.0;

            _isSettingsOpen = false;
            _isPowerMenuOpen = false;
            UpdateSettingsButtonState();
            UpdatePowerButtonState();
            if (!ReferenceEquals(_selectedModule, module))
                _selectedModule?.OnTabDeselected();
            _selectedModule = module;
            if (!string.Equals(module.Id, "filetray", StringComparison.Ordinal))
                _fileTrayAutoCloseTimer.Stop();
            UpdateKeepExpandedCloseButton();

            foreach (var child in TabContainer.Children)
            {
                if (child is Button btn && btn.DataContext is INotchModule m)
                {
                    bool isSel = ReferenceEquals(m, module);
                    btn.Tag = isSel ? "Selected" : null;
                    btn.Content = NotchDesign.CreateVectorIcon(
                        m.IconKind,
                        15,
                        isSel ? NotchDesign.TextPrimaryBrush : NotchDesign.TabIconInactiveBrush,
                        2.25);

                    if (isSel && _isTourActive)
                        PulseTourTabButton(btn);
                    else if (btn.RenderTransform is TransformGroup tg && tg.Children.Count > 0 && tg.Children[0] is ScaleTransform st)
                    {
                        st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                        st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                        st.ScaleX = 1.0;
                        st.ScaleY = 1.0;
                    }
                }
            }

            ScrollSelectedTabIntoView();

            if (_isTourActive)
            {
                int manualIdx = _tourModules.FindIndex(m => ReferenceEquals(m, module));
                if (manualIdx >= 0 && manualIdx != _tourIndex)
                {
                    _tourIndex = manualIdx;
                    UpdateTourBarForCurrent();
                }
            }

            ExpandedHost.Content = _moduleHost.SafeCreateExpandedView(module);

            if (_state == NotchState.Expanded)
            {
                var (w, h) = SizeFor(NotchState.Expanded);
                if (NotchDesign.EffectiveAnimationsEnabled)
                {
                    var ease = NotchDesign.CreateSpringEase(NotchDesign.TabSwitchAmplitude);
                    var dur = TimeSpan.FromMilliseconds(NotchDesign.TabSwitchDurationMs);
                    NotchBorder.BeginAnimation(WidthProperty, CreateAnim(w, dur, ease));
                    NotchBorder.BeginAnimation(HeightProperty, CreateAnim(h, dur, ease));
                }
                else
                {
                    NotchBorder.Width = w;
                    NotchBorder.Height = h;
                    UpdateShellGeometry();
                }
            }

            if (NotchDesign.EffectiveAnimationsEnabled)
            {
                var scale = new ScaleTransform(0.96, 0.96);
                var translate = new TranslateTransform(slideDir * 12.0, 3.0);
                var group = new TransformGroup();
                group.Children.Add(scale);
                group.Children.Add(translate);

                ExpandedHost.RenderTransformOrigin = new Point(0.5, 0.45);
                ExpandedHost.RenderTransform = group;
                ExpandedHost.BeginAnimation(OpacityProperty, null);
                ExpandedHost.Opacity = 0.0;

                // Animation erst nach Layout-Aufbau starten, damit Frame 1 des Tab-Wechsels nicht ruckelt
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    if (!ReferenceEquals(_selectedModule, module)) return;
                    var contentEase = NotchDesign.CreateSpringEase(0.42);
                    var contentDur = TimeSpan.FromMilliseconds(NotchDesign.TabSwitchDurationMs);
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateAnim(1.0, contentDur, contentEase));
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateAnim(1.0, contentDur, contentEase));
                    translate.BeginAnimation(TranslateTransform.XProperty, CreateAnim(0.0, contentDur, contentEase));
                    translate.BeginAnimation(TranslateTransform.YProperty, CreateAnim(0.0, contentDur, contentEase));
                    ExpandedHost.BeginAnimation(OpacityProperty,
                        CreateAnim(1.0, contentDur, NotchDesign.CreateEaseInOut()));
                });
            }
        }

        private void OnLanguageChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnLanguageChanged));
                return;
            }
            if (_state == NotchState.Expanded) BuildTabs();
            else if (_state == NotchState.Preview) UpdatePreviewContent();
        }

        private void OnModuleHostActiveModulesChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnModuleHostActiveModulesChanged));
                return;
            }

            if (_state == NotchState.Expanded)
            {
                BuildTabs();
            }
            else if (_state == NotchState.Preview)
            {
                UpdatePreviewContent();
            }
            else
            {
                RefreshCollapsedLiveState();
            }
        }

        private void OnModuleHostCollapseRequested(INotchModule module)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_state != NotchState.Collapsed && _selectedModule == module && !_isSettingsOpen)
                    NotchDesign.RunAfterPressFeedback(() => SetState(NotchState.Collapsed));
            });
        }

        private void OnModuleHostStateChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnModuleHostStateChanged));
                return;
            }

            UpdatePrivacyIndicator();

            if (_state == NotchState.Preview)
            {
                UpdatePreviewContent();
            }
            else if (_state == NotchState.Collapsed)
            {
                RefreshCollapsedLiveState();
            }
            else if (_state == NotchState.Expanded)
            {
                UpdateKeepExpandedCloseButton();
                var (w, h) = SizeFor(NotchState.Expanded);
                if (Math.Abs(NotchBorder.Width - w) > 1 || Math.Abs(NotchBorder.Height - h) > 1)
                {
                    if (NotchDesign.EffectiveAnimationsEnabled)
                    {
                        var ease = NotchDesign.CreateSpringEase(NotchDesign.TabSwitchAmplitude);
                        var dur = TimeSpan.FromMilliseconds(NotchDesign.TabSwitchDurationMs);
                        NotchBorder.BeginAnimation(WidthProperty, CreateAnim(w, dur, ease));
                        NotchBorder.BeginAnimation(HeightProperty, CreateAnim(h, dur, ease));
                    }
                    else
                    {
                        NotchBorder.Width = w;
                        NotchBorder.Height = h;
                        UpdateShellGeometry();
                    }
                }
            }
        }

        // ---------- Maus-Ereignisse ----------

        private void Notch_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_suppressed) return;
            _fileTrayAutoCloseTimer.Stop();
            if (_state == NotchState.Collapsed && S.Mode == InteractionMode.ClickOnly)
            {
                if (IsCollapsedPeekActive)
                    NotchBorder.BeginAnimation(OpacityProperty, CreateAnim(GetTargetNotchOpacity(), TimeSpan.FromMilliseconds(NotchDesign.MicroTransitionMs), NotchDesign.CreateEaseOut(), activeTransition: true));
                return;
            }
            if (_state != NotchState.Collapsed) return;

            _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, S.HoverDelayMs));
            _hoverTimer.Start();
        }

        private void HoverTimer_Tick(object? sender, EventArgs e)
        {
            if (_suppressed) return;
            _hoverTimer.Stop();
            if (!NotchBorder.IsMouseOver || _state != NotchState.Collapsed) return;

            if (S.Mode == InteractionMode.HoverOnly)
            {
                SetState(NotchState.Expanded);
                Activate();
            }
            else
            {
                SetState(NotchState.Preview);
            }
        }

        private void Notch_MouseLeave(object sender, MouseEventArgs e)
        {
            if (Mouse.Captured != null) return;
            _hoverTimer.Stop();

            if (_state == NotchState.Preview)
            {
                SetState(NotchState.Collapsed);
            }
            else if (_state == NotchState.Collapsed && S.Mode == InteractionMode.ClickOnly && IsCollapsedPeekActive)
            {
                NotchBorder.BeginAnimation(OpacityProperty, CreateAnim(GetTargetNotchOpacity(), TimeSpan.FromMilliseconds(NotchDesign.MicroTransitionMs), NotchDesign.CreateEaseOut(), activeTransition: true));
            }
            else if (_state == NotchState.Expanded && !_isSettingsOpen && !_isOnboardingOpen && !_isTourActive && _selectedModule?.KeepExpanded != true)
            {
                if (_selectedModule?.Id == "filetray" && (S.Mode == InteractionMode.HoverOnly || !IsActive))
                    ScheduleFileTrayAutoClose(FileTrayAutoCloseDelayMs);
                else if (S.Mode == InteractionMode.HoverOnly)
                    SetState(NotchState.Collapsed);
            }
        }

        private void Notch_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_suppressed) return;
            if (S.Mode == InteractionMode.HoverOnly) return;

            if (_state != NotchState.Expanded)
            {
                SetState(NotchState.Expanded);
                Activate();
            }
        }

        public void OpenSettings()
        {
            _isOnboardingOpen = false;
            _isPowerMenuOpen = false;
            UpdatePowerButtonState();
            if (_state == NotchState.Expanded && _isSettingsOpen)
            {
                CloseSettings();
                return;
            }

            _isSettingsOpen = true;
            if (_state != NotchState.Expanded)
            {
                SetState(NotchState.Expanded);
                Activate();
                return;
            }

            ShowSettingsInHost();
            AnimateExpandedSize();
            Activate();
        }

        public void OpenOnboarding()
        {
            _isSettingsOpen = false;
            _isPowerMenuOpen = false;
            UpdatePowerButtonState();
            _isOnboardingOpen = true;
            if (_state != NotchState.Expanded)
            {
                SetState(NotchState.Expanded);
                Activate();
                return;
            }

            ShowOnboardingInHost();
            AnimateExpandedSize();
            Activate();
        }

        private void CloseOnboarding()
        {
            if (!_isOnboardingOpen) return;
            _isOnboardingOpen = false;
            SetState(NotchState.Collapsed);
        }

        private void ShowOnboardingInHost()
        {
            _fileTrayAutoCloseTimer.Stop();
            EmptyModulesPanel.Visibility = Visibility.Collapsed;
            ExpandedHost.Visibility = Visibility.Visible;

            if (ExpandedHost.Content is not OnboardingView)
            {
                var view = new OnboardingView(() =>
                {
                    RefreshFromSettings();
                    if (_state == NotchState.Expanded)
                        AnimateExpandedSize();
                });
                view.ImportedOrSkipped += CloseOnboarding;
                view.StartTourRequested += StartTour;
                ExpandedHost.Content = view;
            }
            UpdateSettingsButtonState();
            UpdateKeepExpandedCloseButton();
        }

        private void StartTour(AppSettings draft)
        {
            _isOnboardingOpen = false;
            _isTourActive = true;
            _tourPaused = false;

            SettingsService.Update(draft);
            BuildTabs();

            _tourModules = TabContainer.Children
                .OfType<Button>()
                .Select(b => b.DataContext as INotchModule)
                .Where(m => m != null)
                .Cast<INotchModule>()
                .ToList();

            if (_tourModules.Count == 0)
            {
                FinishTour();
                return;
            }

            _tourBar = new OnboardingTourBar();
            _tourBar.PrevClicked += () => StepTour(-1);
            _tourBar.NextClicked += () => StepTour(1);
            _tourBar.PauseToggled += paused =>
            {
                _tourPaused = paused;
                if (paused)
                {
                    _tourTimer?.Stop();
                    _tourDemoTimer?.Stop();
                }
                else
                {
                    _tourTimer?.Start();
                }
                var cur = _tourModules.Count > 0 ? _tourModules[_tourIndex] : null;
                _tourBar.SetPaused(paused, TourStepDuration(cur));
            };
            _tourBar.FinishClicked += FinishTour;

            TourBarHost.Content = _tourBar;
            TourBarHost.Visibility = Visibility.Visible;
            NotchDesign.AnimateSubViewEnter(_tourBar, slideY: 8);

            _tourTimer ??= new DispatcherTimer();
            _tourTimer.Tick -= OnTourTick;
            _tourTimer.Tick += OnTourTick;

            ShowTourStep(0);
        }

        private static TimeSpan TourStepDuration(INotchModule? module)
            => TimeSpan.FromMilliseconds(module?.Id is "widgets" or "quickactions" ? 6000 : 4000);

        private void OnTourTick(object? sender, EventArgs e)
        {
            if (!_isTourActive || _tourPaused || _tourModules.Count == 0) return;
            if (_tourIndex + 1 >= _tourModules.Count)
            {
                FinishTour();
                return;
            }
            ShowTourStep(_tourIndex + 1);
        }

        private void StepTour(int delta)
        {
            if (!_isTourActive || _tourModules.Count == 0) return;
            int next = Math.Clamp(_tourIndex + delta, 0, _tourModules.Count - 1);
            if (next != _tourIndex)
                ShowTourStep(next);
        }

        private void ShowTourStep(int index)
        {
            if (_tourModules.Count == 0) return;
            StopTourDemoJiggle();
            _tourIndex = Math.Clamp(index, 0, _tourModules.Count - 1);
            var mod = _tourModules[_tourIndex];
            SelectModule(mod);
            UpdateTourBarForCurrent();
            ScheduleTourDemoJiggle(mod);
        }

        private void UpdateTourBarForCurrent()
        {
            if (_tourBar == null || _tourModules.Count == 0) return;
            var mod = _tourModules[_tourIndex];
            var dur = TourStepDuration(mod);
            _tourBar.ShowModule(mod, _tourIndex, _tourModules.Count, dur, _tourPaused);
            if (_tourTimer != null)
            {
                _tourTimer.Stop();
                _tourTimer.Interval = dur;
                if (!_tourPaused) _tourTimer.Start();
            }
        }

        private void ScheduleTourDemoJiggle(INotchModule mod)
        {
            if (mod.Id is not ("widgets" or "quickactions")) return;

            int phase = 0;
            _tourDemoTimer ??= new DispatcherTimer();
            _tourDemoTimer.Stop();
            _tourDemoTimer.Interval = TimeSpan.FromMilliseconds(1400);
            _tourDemoTimer.Tick -= OnTourDemoTick;
            _tourDemoTimer.Tick += OnTourDemoTick;
            _tourDemoTimer.Tag = mod;
            _tourDemoTimer.Start();

            void OnTourDemoTick(object? s, EventArgs ev)
            {
                if (!_isTourActive || _tourPaused || !ReferenceEquals(_selectedModule, mod))
                {
                    _tourDemoTimer.Stop();
                    _tourDemoTimer.Tick -= OnTourDemoTick;
                    return;
                }

                if (phase == 0)
                {
                    phase = 1;
                    SetModuleDemoJiggle(mod, true);
                    _tourDemoTimer.Interval = TimeSpan.FromMilliseconds(3200);
                }
                else
                {
                    _tourDemoTimer.Stop();
                    _tourDemoTimer.Tick -= OnTourDemoTick;
                    SetModuleDemoJiggle(mod, false);
                }
            }
        }

        private void StopTourDemoJiggle()
        {
            _tourDemoTimer?.Stop();
            if (_selectedModule != null)
                SetModuleDemoJiggle(_selectedModule, false);
        }

        private static void SetModuleDemoJiggle(INotchModule mod, bool active)
        {
            if (mod is WindowsNotch.Modules.Widgets.WidgetsModule w)
                w.SetTourDemoJiggle(active);
            else if (mod is WindowsNotch.Modules.QuickActions.QuickActionsModule q)
                q.SetTourDemoJiggle(active);
        }

        private void FinishTour()
        {
            StopTourInternal();
            SettingsService.UpdateQuiet(s => s.HasCompletedOnboarding = true);
            SetState(NotchState.Collapsed);
        }

        private void StopTourInternal()
        {
            StopTourDemoJiggle();
            _isTourActive = false;
            _tourTimer?.Stop();
            TourBarHost.Visibility = Visibility.Collapsed;
            TourBarHost.Content = null;
            _tourBar = null;
            _tourModules.Clear();
        }

        private static void PulseTourTabButton(Button btn)
        {
            if (!NotchDesign.EffectiveAnimationsEnabled) return;
            ScaleTransform scale;
            if (btn.RenderTransform is TransformGroup tg && tg.Children.Count > 0 && tg.Children[0] is ScaleTransform existing)
            {
                scale = existing;
            }
            else
            {
                scale = new ScaleTransform(1.0, 1.0);
                btn.RenderTransformOrigin = new Point(0.5, 0.5);
                btn.RenderTransform = scale;
            }

            var anim = new DoubleAnimation(1.0, 1.16, TimeSpan.FromMilliseconds(460))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        private void TabScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (TabScrollViewer.ScrollableWidth <= 0) return;
            double step = e.Delta != 0 ? -Math.Sign(e.Delta) * Math.Max(28.0, Math.Abs(e.Delta) * 0.45) : 0;
            double target = Math.Clamp(TabScrollViewer.HorizontalOffset + step, 0, TabScrollViewer.ScrollableWidth);
            TabScrollViewer.ScrollToHorizontalOffset(target);
            e.Handled = true;
        }

        private void TabScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
            => UpdateTabScrollFadeMask();

        private void TabScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateTabScrollFadeMask();
            if (e.WidthChanged)
                ScrollSelectedTabIntoView();
        }

        private void UpdateTabScrollFadeMask()
        {
            double scrollable = TabScrollViewer.ScrollableWidth;
            double viewport = TabScrollViewer.ViewportWidth;
            if (scrollable <= 1.0 || viewport <= 40.0)
            {
                if (TabScrollViewer.OpacityMask != null)
                    TabScrollViewer.OpacityMask = null;
                return;
            }

            double offset = TabScrollViewer.HorizontalOffset;
            bool fadeLeft = offset > 1.0;
            bool fadeRight = offset < scrollable - 1.0;

            if (!fadeLeft && !fadeRight)
            {
                TabScrollViewer.OpacityMask = null;
                return;
            }

            double fadeFrac = Math.Clamp(18.0 / viewport, 0.04, 0.16);
            var stops = new GradientStopCollection(4);
            stops.Add(new GradientStop(fadeLeft ? Color.FromArgb(0, 0, 0, 0) : Colors.Black, 0.0));
            if (fadeLeft)
                stops.Add(new GradientStop(Colors.Black, fadeFrac));
            if (fadeRight)
                stops.Add(new GradientStop(Colors.Black, 1.0 - fadeFrac));
            stops.Add(new GradientStop(fadeRight ? Color.FromArgb(0, 0, 0, 0) : Colors.Black, 1.0));

            TabScrollViewer.OpacityMask = new LinearGradientBrush(stops, new Point(0, 0.5), new Point(1, 0.5));
        }

        private void ScrollSelectedTabIntoView()
        {
            if (_selectedModule == null || TabContainer.Children.Count == 0) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                Button? selectedBtn = null;
                foreach (var child in TabContainer.Children)
                {
                    if (child is Button b && ReferenceEquals(b.DataContext, _selectedModule))
                    {
                        selectedBtn = b;
                        break;
                    }
                }
                if (selectedBtn == null || !selectedBtn.IsLoaded) return;
                double viewportW = TabScrollViewer.ViewportWidth;
                if (viewportW <= 0) return;

                Point pos = selectedBtn.TranslatePoint(new Point(0, 0), TabContainer);
                double left = pos.X;
                double right = left + selectedBtn.ActualWidth;
                double curOffset = TabScrollViewer.HorizontalOffset;
                const double pad = 22.0;

                if (left - pad < curOffset)
                    TabScrollViewer.ScrollToHorizontalOffset(Math.Max(0, left - pad));
                else if (right + pad > curOffset + viewportW)
                    TabScrollViewer.ScrollToHorizontalOffset(Math.Min(TabScrollViewer.ScrollableWidth, right + pad - viewportW));

                UpdateTabScrollFadeMask();
            });
        }

        private void CloseSettings()
        {
            if (!_isSettingsOpen) return;
            _isSettingsOpen = false;
            _isPowerMenuOpen = false;
            UpdateSettingsButtonState();
            UpdatePowerButtonState();

            if (_state == NotchState.Expanded)
            {
                if (_selectedModule != null)
                {
                    SelectModule(_selectedModule);
                }
                else
                {
                    BuildTabs();
                    AnimateExpandedSize();
                }
            }
        }

        private void ShowSettingsInHost()
        {
            _fileTrayAutoCloseTimer.Stop();
            EmptyModulesPanel.Visibility = Visibility.Collapsed;
            ExpandedHost.Visibility = Visibility.Visible;

            foreach (var child in TabContainer.Children)
            {
                if (child is Button btn && btn.DataContext is INotchModule m)
                {
                    btn.Tag = null;
                    btn.Content = NotchDesign.CreateVectorIcon(m.IconKind, 15, NotchDesign.TabIconInactiveBrush, 2.25);
                }
            }

            if (ExpandedHost.Content is not SettingsView)
            {
                var view = new SettingsView();
                view.CloseRequested += CloseSettings;
                ExpandedHost.Content = view;
            }
            UpdateSettingsButtonState();
            UpdateKeepExpandedCloseButton();
        }

        private void UpdateSettingsButtonState()
        {
            SettingsBtn.Tag = _isSettingsOpen ? "Selected" : null;
            SettingsIconPath.Stroke = _isSettingsOpen ? NotchDesign.TextPrimaryBrush : NotchDesign.TabIconInactiveBrush;
        }

        private void AnimateExpandedSize()
        {
            var (w, h) = SizeFor(NotchState.Expanded);
            if (NotchDesign.EffectiveAnimationsEnabled)
            {
                var ease = NotchDesign.CreateSpringEase(NotchDesign.TabSwitchAmplitude);
                var dur = TimeSpan.FromMilliseconds(NotchDesign.TabSwitchDurationMs);
                NotchBorder.BeginAnimation(WidthProperty, CreateAnim(w, dur, ease));
                NotchBorder.BeginAnimation(HeightProperty, CreateAnim(h, dur, ease));
            }
            else
            {
                NotchBorder.Width = w;
                NotchBorder.Height = h;
                UpdateShellGeometry();
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            OpenSettings();
        }

        private void UpdateKeepExpandedCloseButton()
        {
            bool hideHeader = _isOnboardingOpen || (!_isSettingsOpen && _selectedModule?.Id == "widgets" && _selectedModule.KeepExpanded);
            ExpandedHeaderGrid.Visibility = hideHeader ? Visibility.Collapsed : Visibility.Visible;
            ExpandedHost.Margin = _isOnboardingOpen
                ? new Thickness(24, 18, 24, 18)
                : hideHeader
                ? new Thickness(18, 16, 18, 16)
                : new Thickness(26, 2, 26, 18);

            CloseBtn.Visibility = (!hideHeader && !_isSettingsOpen && !_isPowerMenuOpen && _selectedModule?.KeepExpanded == true)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            NotchDesign.RunAfterPressFeedback(() => SetState(NotchState.Collapsed));
        }

        // ---------- Tastatur und Fokus ----------

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _state == NotchState.Expanded)
            {
                if (_tabJiggleController?.HandleEscape() == true)
                {
                    e.Handled = true;
                    return;
                }

                if (_isTourActive)
                {
                    FinishTour();
                    e.Handled = true;
                    return;
                }

                if (_isPowerMenuOpen)
                {
                    ClosePowerMenu();
                    e.Handled = true;
                    return;
                }

                if (_isSettingsOpen)
                {
                    CloseSettings();
                    e.Handled = true;
                    return;
                }

                if (_selectedModule?.HandleEscape() == true)
                {
                    e.Handled = true;
                    return;
                }

                SetState(NotchState.Collapsed);
                e.Handled = true;
            }
        }

        private const int FileTrayAutoCloseDelayMs = 3000;

        // Klick außerhalb: Das Fenster verliert den Fokus
        private void Window_Deactivated(object? sender, EventArgs e)
        {
            if (_state != NotchState.Expanded || !S.CloseOnFocusLoss || _isSettingsOpen || _isOnboardingOpen || _isTourActive || _selectedModule?.KeepExpanded == true)
                return;

            // Beim File Tray kurze Gnadenfrist (3 s), damit man Explorer/Desktop öffnen & Dateien reinziehen kann
            if (_selectedModule?.Id == "filetray")
            {
                ScheduleFileTrayAutoClose(FileTrayAutoCloseDelayMs);
                return;
            }

            SetState(NotchState.Collapsed);
        }

        private void ScheduleFileTrayAutoClose(int delayMs)
        {
            _fileTrayAutoCloseTimer.Stop();
            _fileTrayAutoCloseTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
            _fileTrayAutoCloseTimer.Start();
        }

        private void FileTrayAutoCloseTimer_Tick(object? sender, EventArgs e)
        {
            _fileTrayAutoCloseTimer.Stop();
            if (_state != NotchState.Expanded || _selectedModule?.Id != "filetray") return;
            if (_isSettingsOpen || _isPowerMenuOpen || _isOnboardingOpen || _isTourActive) return;
            if (IsActive && S.Mode != InteractionMode.HoverOnly) return;
            if (NotchBorder.IsMouseOver) return;

            // Während noch eine Datei mit gedrückter Maustaste gezogen wird, nicht mitten im Drag schließen
            if ((NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0)
            {
                ScheduleFileTrayAutoClose(1500);
                return;
            }

            SetState(NotchState.Collapsed);
        }

        private void Notch_DragEnter(object sender, DragEventArgs e)
        {
            _fileTrayAutoCloseTimer.Stop();
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            var fileTray = _moduleHost.ActiveModules.OfType<WindowsNotch.Modules.FileTray.FileTrayModule>().FirstOrDefault();
            if (fileTray == null)
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            e.Effects = DragDropEffects.Copy;
            if (_state != NotchState.Expanded)
            {
                SetState(NotchState.Expanded);
            }
            if (!ReferenceEquals(_selectedModule, fileTray))
            {
                SelectModule(fileTray);
            }
        }

        private void Notch_DragOver(object sender, DragEventArgs e)
        {
            _fileTrayAutoCloseTimer.Stop();
            if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                _moduleHost.ActiveModules.Any(m => m is WindowsNotch.Modules.FileTray.FileTrayModule))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }

        private void Notch_Drop(object sender, DragEventArgs e)
        {
            if (e.Handled) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                var fileTray = _moduleHost.ActiveModules.OfType<WindowsNotch.Modules.FileTray.FileTrayModule>().FirstOrDefault();
                if (fileTray != null)
                {
                    fileTray.AddFiles(files);
                    e.Handled = true;
                }
            }
        }

        private void UpdatePrivacyIndicator()
        {
            bool hidden = _suppressed || _state == NotchState.Expanded;
            bool recActive = !hidden && ScreenCaptureService.IsAnyRecording;
            bool camActive = !hidden && S.ShowCameraIndicator && PrivacyIndicatorService.IsCameraActive();
            int nextMask = (recActive ? 1 : 0) | (camActive ? 2 : 0);
            bool animate = nextMask != 0 && NotchDesign.EffectiveAnimationsEnabled;

            if (nextMask == _privacyMask && animate == _privacyAnimActive)
            {
                if (camActive)
                    CameraStatusDot.Fill = NotchDesign.GetFrozenBrush(NotchDesign.AccentGreen);
                return;
            }

            _privacyMask = nextMask;

            if (nextMask == 0)
            {
                PrivacyIndicatorPanel.BeginAnimation(OpacityProperty, null);
                PrivacyIndicatorPanel.Opacity = 1.0;
                _privacyAnimActive = false;
                RecordingStatusDot.Visibility = Visibility.Collapsed;
                CameraStatusDot.Visibility = Visibility.Collapsed;
                PrivacyIndicatorPanel.Visibility = Visibility.Collapsed;
                return;
            }

            // Rot = Bildschirmaufnahme, Grün = Kamera aktiv
            RecordingStatusDot.Fill = NotchDesign.GetFrozenBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
            RecordingStatusDot.Visibility = recActive ? Visibility.Visible : Visibility.Collapsed;

            CameraStatusDot.Fill = NotchDesign.GetFrozenBrush(NotchDesign.AccentGreen);
            CameraStatusDot.Margin = recActive ? new Thickness(4, 0, 0, 0) : new Thickness(0);
            CameraStatusDot.Visibility = camActive ? Visibility.Visible : Visibility.Collapsed;

            PrivacyIndicatorPanel.Visibility = Visibility.Visible;

            if (animate)
            {
                if (!_privacyAnimActive)
                {
                    var breath = new DoubleAnimation(1.0, 0.42, TimeSpan.FromMilliseconds(2200))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };
                    Timeline.SetDesiredFrameRate(breath, GetEffectiveFrameRate(false));
                    PrivacyIndicatorPanel.BeginAnimation(OpacityProperty, breath);
                    _privacyAnimActive = true;
                }
            }
            else
            {
                PrivacyIndicatorPanel.BeginAnimation(OpacityProperty, null);
                PrivacyIndicatorPanel.Opacity = 1.0;
                _privacyAnimActive = false;
            }
        }
    }
}
