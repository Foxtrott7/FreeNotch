using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WindowsNotch.Core;
using WindowsNotch.Services;
using WindowsNotch.Services.Database;

namespace WindowsNotch.Modules.Widgets
{
    /// <summary>
    /// Dashboard & Widgets im Notchify 2×2-Bento-Stil:
    /// Live-Lyrics (Gradient-Tile), Live-Medien-Player, Monatskalender/Aufgaben/Notizen
    /// sowie erweitertes 7-fach Carousel (Uhr, Wetter, Fokus-Timer, Stoppuhr, CPU, RAM, Geschlossene Notch)
    /// mit modularem iPhone-Jiggle-Mode Drag & Drop.
    /// </summary>
    public partial class WidgetsModule : NotchModuleBase
    {
        private const int CarouselSlideCount = 6;
        private static CultureInfo ActiveCulture =>
            string.Equals(LocalizationService.CurrentLanguage, "en", StringComparison.OrdinalIgnoreCase)
                ? CultureInfo.GetCultureInfo("en-US")
                : CultureInfo.GetCultureInfo("de-DE");

        public override string Id => "widgets";
        public override string Title => L.T("widgets.title");
        public override NotchIconKind IconKind => NotchIconKind.Dashboard;

        public override int Priority
        {
            get
            {
                if (_timerFinished) return 140;
                if (_timerRunning) return 110;
                if (_stopwatchRunning) return 105;
                return 10;
            }
        }

        public override double? PreferredPreviewWidth => 280;
        public override (double Width, double Height)? PreferredExpandedSize
            => (_isLyricsPinned || _isPetPinned)
                ? (480, 314)
                : (540, 340 + (_showStoreDrawer ? EstimateStoreDrawerHeight() : 0));
        public override bool KeepExpanded => _isLyricsPinned || _isPetPinned;

        public override double? PreferredLiveWidth
        {
            get
            {
                if (_timerFinished) return 210;
                if (_timerRunning || _stopwatchRunning) return 188;

                return null;
            }
        }

        private DispatcherTimer? _tickTimer;
        private bool _isFastRenderAttached;
        private readonly Stopwatch _fastRenderSw = new();
        private BentoJiggleController? _jiggleController;
        private bool _showStoreDrawer;
        private Border? _storeDrawerHost;
        private Border? _storeDrawer;
        private int _customCarouselPageIndex;
        private Action? _refreshCustomCarousel;
        private readonly List<Action> _tileUpdaters = new();
        private readonly List<Action> _fastTickUpdaters = new();

        // Systemmetriken (zentral in SystemMetricsService)
        private static int _cpuPercent => SystemMetricsService.Instance.CpuPercent;
        private static int _ramPercent => SystemMetricsService.Instance.RamPercent;
        private static double _ramUsedGb => SystemMetricsService.Instance.RamUsedGb;
        private static double _ramTotalGb => SystemMetricsService.Instance.RamTotalGb;

        // Timer-Zustand
        private TimeSpan _timerDuration = TimeSpan.FromMinutes(5);
        private TimeSpan _timerRemaining = TimeSpan.FromMinutes(5);
        private DateTime _timerLastTickUtc;
        private bool _timerRunning;
        private bool _timerFinished;

        // Stoppuhr-Zustand
        private readonly Stopwatch _stopwatch = new();
        private bool _stopwatchRunning;

        // Kalender-, Aufgaben-, Notizen- & Carousel-Zustand
        private DateTime _calendarWeekAnchor = DateTime.Today;
        private int _carouselIndex;
        private int _bottomLeftTab; // 0 = Kalender, 1 = Aufgaben, 2 = Notizen
        private Point? _blSwipeStart;
        private DispatcherTimer? _noteSaveTimer;
        private string _pendingNoteText = string.Empty;

        // UI-Referenzen: Live-Lyrics-Kachel
        private bool _isLyricsPinned;
        private Grid? _bentoGrid, _expandedRootHost;
        private Border? _lyricsTileCard, _lyricsFullscreenHost;
        private DockPanel? _lyricsDock;
        private Grid? _lyricsBodyGrid;
        private StackPanel? _lyricsTitleStack;
        private Button? _lyricsPinBtn;
        private Border? _lyricsBgCoverBorder, _lyricsTintOverlay, _lyricsHeaderCover;
        private FrameworkElement? _lyricsHeaderCoverFallback;
        private TextBlock? _lyricsHeaderTitle, _lyricsHeaderArtist;
        private TextBlock? _lyricsPrev2Text, _lyricsPrevText, _lyricsCurrText, _lyricsNextText, _lyricsNext2Text, _lyricsNext3Text, _lyricsNext4Text;
        private WrapPanel? _lyricsCurrWrap;
        private TextBlock? _lyricsFallbackText, _lyricsFallbackSubText;
        private Grid? _lyricsSyncedPanel;
        private StackPanel? _lyricsFallbackPanel;
        private ImageSource? _lyricsLastCover;
        private string _lyricsBuiltLineKey = string.Empty;
        private readonly List<LyricGlyphToken> _lyricsGlyphTokens = new();

        // UI-Referenzen: Live-Medien-Player-Kachel
        private Border? _mpCard, _mpCoverBorder, _mpBarsHost, _mpProgressFill, _mpProgressThumb;
        private FrameworkElement? _mpCoverFallbackIcon;
        private TextBlock? _mpTitleText, _mpArtistText, _mpElapsedText, _mpDurationText;
        private Grid? _mpProgressGrid;
        private Button? _mpPlayBtn;
        private bool? _mpLastPlayingVisual;
        private ImageSource? _mpLastCover;
        private Color? _mpCoverAccent;
        private bool _mpIsScrubbing;
        private double _mpScrubRatio;
        private DateTime _mpLastLiveSeekUtc;

        // UI-Referenzen: Kalender / Aufgaben / Notizen
        private TextBlock? _calHeaderText, _calDayBigText, _calDaySubText, _calWeekText;
        private Grid? _calDaysGrid;
        private Grid? _bottomLeftHost;
        private StackPanel? _blDotsPanel;
        private FrameworkElement? _blCalendarView, _blTodoView, _blNoteView;
        private StackPanel? _todoListPanel;
        private TextBox? _todoInputBox;
        private TextBox? _noteTextBox;
        private TextBlock? _noteStatusText;

        // UI-Referenzen: Erweitertes Carousel
        private TextBlock? _carLabelTop, _carSubTop, _carMainVal;
        private Border? _carTopIconHost, _carWatermarkHost;
        private StackPanel? _carDotsPanel;
        private Border? _carCard;
        private StackPanel? _carTimerControls, _carSwControls;
        private Button? _carTimerToggleBtn, _carSwToggleBtn;

        public override void Start()
        {
            WeatherService.Instance.WeatherUpdated -= OnWeatherUpdated;
            WeatherService.Instance.CitiesChanged -= OnWeatherUpdated;
            WeatherService.Instance.WeatherUpdated += OnWeatherUpdated;
            WeatherService.Instance.CitiesChanged -= OnWeatherUpdated;
            WeatherService.Instance.CitiesChanged += OnWeatherUpdated;
            _ = WeatherService.Instance.RefreshIfNeededAsync();

            MediaPlayerService.Instance.StateChanged -= OnMediaOrLyricsUpdated;
            MediaPlayerService.Instance.StateChanged += OnMediaOrLyricsUpdated;
            MediaPlayerService.Instance.EnsureStarted();

            LyricsService.Instance.LyricsUpdated -= OnMediaOrLyricsUpdated;
            LyricsService.Instance.LyricsUpdated += OnMediaOrLyricsUpdated;

            if (_tickTimer == null)
            {
                _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _tickTimer.Tick += OnTick;
            }
            _tickTimer.Start();
        }

        public override void Stop()
        {
            _tickTimer?.Stop();
            StopFastUiTimer();
            _jiggleController?.Detach();
            _jiggleController = null;
            WeatherService.Instance.WeatherUpdated -= OnWeatherUpdated;
            WeatherService.Instance.CitiesChanged -= OnWeatherUpdated;
            MediaPlayerService.Instance.StateChanged -= OnMediaOrLyricsUpdated;
            LyricsService.Instance.LyricsUpdated -= OnMediaOrLyricsUpdated;
        }

        public override bool HandleEscape()
        {
            if (_isLyricsPinned)
            {
                SetLyricsPinned(false);
                return true;
            }
            if (_isPetPinned)
            {
                SetPetPinned(false);
                return true;
            }
            return _jiggleController?.HandleEscape() ?? false;
        }

        public void SetTourDemoJiggle(bool active)
        {
            if (active) _jiggleController?.EnterJiggleMode();
            else _jiggleController?.ExitJiggleMode();
        }

        private void OnWeatherUpdated()
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                UpdateCarouselVisual();
                foreach (var updater in _tileUpdaters)
                    updater();
                RaiseStateChanged();
            });
        }

        private void OnMediaOrLyricsUpdated()
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                UpdateLyricsTile();
                UpdateMediaTile();
                UpdateCarouselVisual();
            });
        }

        private void OnTick(object? sender, EventArgs e)
        {
            UpdateTimerState();
            _ = WeatherService.Instance.RefreshIfNeededAsync();
            UpdateExpandedWidgets();
            if (_timerRunning || _timerFinished || _stopwatchRunning) RaiseStateChanged();
        }

        private void StartFastUiTimer()
        {
            if (_isFastRenderAttached) return;
            _fastRenderSw.Restart();
            CompositionTarget.Rendering += OnFastRenderFrame;
            _isFastRenderAttached = true;
        }

        private void StopFastUiTimer()
        {
            if (!_isFastRenderAttached) return;
            CompositionTarget.Rendering -= OnFastRenderFrame;
            _isFastRenderAttached = false;
            _fastRenderSw.Stop();
        }

        private void OnFastRenderFrame(object? sender, EventArgs e)
        {
            if (_expandedRootHost is not { IsVisible: true }) return;

            double minFrameMs = 1000.0 / Math.Clamp(SettingsService.Current.TargetFrameRate, 60, 240);
            if (_fastRenderSw.Elapsed.TotalMilliseconds < minFrameMs * 0.85)
                return;
            _fastRenderSw.Restart();

            if (MediaPlayerService.Instance.IsPlaying)
            {
                UpdateLyricsTile();
                UpdateMediaProgressVisual();
            }
            if (_stopwatchRunning)
            {
                if (_carouselIndex == 3)
                    UpdateCarouselVisual();
                foreach (var u in _fastTickUpdaters)
                    u();
            }
        }

        private void UpdateTimerState()
        {
            if (!_timerRunning) return;

            var now = DateTime.UtcNow;
            var elapsed = now - _timerLastTickUtc;
            _timerLastTickUtc = now;

            _timerRemaining -= elapsed;
            if (_timerRemaining <= TimeSpan.Zero)
            {
                _timerRemaining = TimeSpan.Zero;
                _timerRunning = false;
                _timerFinished = true;
                try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
            }
        }

        // ---------- Notch Views (Collapsed & Preview) ----------

        public override FrameworkElement? CreateLiveView()
        {
            if (_timerFinished)
                return CreateLiveDotRow(L.T("widgets.live_timer_done"), null, NotchDesign.AccentRed);

            if (_timerRunning)
                return CreateLiveDotRow(FormatDuration(_timerRemaining), L.T("widgets.live_focus"), NotchDesign.AccentOrange);

            if (_stopwatchRunning)
                return CreateLiveDotRow(FormatDuration(_stopwatch.Elapsed), L.T("widgets.live_stopwatch"), NotchDesign.AccentPrimary);

            return null;
        }



        private static FrameworkElement CreateLiveDotRow(string primaryText, string? secondaryText, Color dotColor)
            => CreateLiveTextPair(primaryText, secondaryText, dotColor);

        private static FrameworkElement CreateLiveTextPair(string primaryText, string? secondaryText, Color? dotColor)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            if (dotColor.HasValue)
            {
                stack.Children.Add(new Border
                {
                    Width = 6,
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(dotColor.Value),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 7, 0)
                });
            }

            var primary = new TextBlock
            {
                Text = primaryText,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Typography.SetNumeralAlignment(primary, FontNumeralAlignment.Tabular);
            stack.Children.Add(primary);

            if (!string.IsNullOrWhiteSpace(secondaryText))
            {
                var secondary = new TextBlock
                {
                    Text = secondaryText,
                    Foreground = NotchDesign.TextMutedBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11.5,
                    FontWeight = FontWeights.Medium,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(7, 0, 0, 0)
                };
                Typography.SetNumeralAlignment(secondary, FontNumeralAlignment.Tabular);
                stack.Children.Add(secondary);
            }

            return stack;
        }

        public override FrameworkElement CreatePreviewView()
        {
            Color accent = _timerFinished ? NotchDesign.AccentRed
                : _timerRunning ? NotchDesign.AccentOrange
                : NotchDesign.AccentPrimary;

            string text = _timerFinished
                ? "Timer abgelaufen"
                : _timerRunning
                    ? $"{FormatDuration(_timerRemaining)}  ·  {DateTime.Now:HH:mm}"
                    : $"{FormatDuration(_stopwatch.Elapsed)}  ·  CPU {_cpuPercent}%";

            return CreateLiveDotRow(text, null, accent);
        }

        // ---------- 2×2 Bento Dashboard (Notchify-Stil + Jiggle-Mode) ----------

        public override FrameworkElement CreateExpandedView()
        {
            _jiggleController?.Detach();
            _jiggleController = null;
            _showStoreDrawer = false;
            _storeDrawer = null;
            _refreshCustomCarousel = null;
            _tileUpdaters.Clear();
            _fastTickUpdaters.Clear();
            _mpLastPlayingVisual = null;
            _mpLastCover = null;
            _mpCoverAccent = null;
            _mpIsScrubbing = false;
            _lyricsLastCover = null;
            _lyricsBuiltLineKey = string.Empty;
            _lyricsLastActiveIdx = int.MinValue;
            _lyricsLastTrackKey = string.Empty;
            _lyricsLastProgress = -1.0;
            _lyricsForceLayoutSync = true;
            _lyricsGlyphTokens.Clear();
            _lyricsTileCard = null;
            _petTileCard = null;

            var rootHost = new Grid();
            _expandedRootHost = rootHost;

            var contentStack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };

            var bento = new Grid { Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Top };
            _bentoGrid = bento;
            var col0 = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            var colGap = new ColumnDefinition { Width = new GridLength(12) };
            var col2 = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            bento.ColumnDefinitions.Add(col0);
            bento.ColumnDefinitions.Add(colGap);
            bento.ColumnDefinitions.Add(col2);
            var row0 = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
            var rowGap = new RowDefinition { Height = new GridLength(12) };
            var row2 = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
            bento.RowDefinitions.Add(row0);
            bento.RowDefinitions.Add(rowGap);
            bento.RowDefinitions.Add(row2);

            bento.SizeChanged += (_, e) =>
            {
                if (e.NewSize.Width <= 20 || e.NewSize.Height <= 20) return;
                double cellW = Math.Max(0, (e.NewSize.Width - 12) / 2.0);
                double cellH = Math.Max(0, (e.NewSize.Height - 12) / 2.0);
                col0.MaxWidth = cellW;
                col2.MaxWidth = cellW;
                row0.MaxHeight = cellH;
                row2.MaxHeight = cellH;
            };

            _storeDrawerHost = new Border { Visibility = Visibility.Collapsed };

            contentStack.Children.Add(bento);
            contentStack.Children.Add(_storeDrawerHost);

            _lyricsFullscreenHost = new Border { Visibility = Visibility.Collapsed };

            rootHost.Children.Add(contentStack);
            rootHost.Children.Add(_lyricsFullscreenHost);

            // Im Ruhezustand füllt bento die verfügbare Höhe von rootHost (340 - Header)
            rootHost.SizeChanged += (_, e) =>
            {
                if (!_showStoreDrawer && e.NewSize.Height > 40)
                {
                    bento.MaxHeight = e.NewSize.Height;
                    if (double.IsNaN(bento.Height))
                        bento.MinHeight = e.NewSize.Height;
                }
            };

            var order = SettingsService.Current.BentoTileOrder;
            var orderedTiles = new List<(string Id, Border Tile)>(4);
            for (int i = 0; i < 4; i++)
            {
                string tileId = i < order.Count ? order[i] : string.Empty;
                orderedTiles.Add((tileId, BuildBentoTile(tileId)));
            }

            _jiggleController = new BentoJiggleController(
                rootHost,
                bento,
                orderedTiles,
                newOrder => SettingsService.UpdateQuiet(s => s.BentoTileOrder = newOrder.ToList()),
                () => NotchDesign.EffectiveAnimationsEnabled,
                () => SettingsService.Current.TargetFrameRate,
                onEnterJiggle: () =>
                {
                    if (_showStoreDrawer || _storeDrawerHost == null) return;
                    if (bento.ActualHeight > 0)
                    {
                        bento.MinHeight = 0;
                        bento.MaxHeight = double.PositiveInfinity;
                        bento.Height = bento.ActualHeight;
                    }
                    _showStoreDrawer = true;
                    _refreshCustomCarousel?.Invoke();
                    var drawer = BuildStoreDrawer();
                    _storeDrawer = drawer;
                    _storeDrawerHost.Child = drawer;
                    _storeDrawerHost.Visibility = Visibility.Visible;
                    NotchDesign.AnimateSubViewEnter(drawer, slideY: 8, durationMs: NotchDesign.DrawerOpenDurationMs);
                    AttachStoreDragHandlers(drawer);
                    RaiseStateChanged();
                },
                onExitJiggle: () =>
                {
                    if (!_showStoreDrawer || _storeDrawerHost == null) return;
                    _showStoreDrawer = false;
                    _refreshCustomCarousel?.Invoke();
                    var closingDrawer = _storeDrawer;
                    _storeDrawer = null;
                    if (closingDrawer != null)
                    {
                        NotchDesign.AnimateDrawerExit(closingDrawer, () =>
                        {
                            if (!_showStoreDrawer && _storeDrawerHost != null)
                            {
                                _storeDrawerHost.Child = null;
                                _storeDrawerHost.Visibility = Visibility.Collapsed;
                                bento.Height = double.NaN;
                                if (rootHost.ActualHeight > 40)
                                {
                                    bento.MinHeight = rootHost.ActualHeight;
                                    bento.MaxHeight = rootHost.ActualHeight;
                                }
                            }
                        });
                    }
                    else
                    {
                        _storeDrawerHost.Child = null;
                        _storeDrawerHost.Visibility = Visibility.Collapsed;
                        bento.Height = double.NaN;
                        if (rootHost.ActualHeight > 40)
                        {
                            bento.MinHeight = rootHost.ActualHeight;
                            bento.MaxHeight = rootHost.ActualHeight;
                        }
                    }
                    RaiseStateChanged();
                },
                onCommitShopDrop: CommitShopDrop,
                onRemoveTileToShop: RemoveTileToShop);

            RebuildCalendarDays();
            UpdateExpandedWidgets();
            if (_isLyricsPinned)
            {
                if (_lyricsTileCard != null) SetLyricsPinned(true);
                else _isLyricsPinned = false;
            }
            if (_isPetPinned)
            {
                if (_petTileCard != null) SetPetPinned(true);
                else _isPetPinned = false;
            }

            var localJiggle = _jiggleController;
            rootHost.Loaded += (_, _) =>
            {
                if (!ReferenceEquals(_expandedRootHost, rootHost)) return;
                var mp = MediaPlayerService.Instance;
                LyricsService.Instance.EnsureForTrack(mp.TrackTitle, mp.Artist, mp.AlbumTitle, mp.Duration, mp.HasKnownTrack);
                UpdateLyricsTile();
                UpdateMediaTile();
                StartFastUiTimer();
            };
            rootHost.Unloaded += (_, _) =>
            {
                if (ReferenceEquals(_expandedRootHost, rootHost))
                    StopFastUiTimer();
                localJiggle.Detach();
                if (ReferenceEquals(_jiggleController, localJiggle))
                    _jiggleController = null;
            };

            return rootHost;
        }

        private Border BuildBentoTile(string tileId) => tileId switch
        {
            "lyrics" => BuildLyricsTile(),
            "media" => BuildMediaPlayerTile(),
            "weather" => BuildWeatherTile(),
            "calendar" => BuildStandaloneCalendarTile(),
            "todos" => BuildStandaloneTodoTile(),
            "notes" => BuildStandaloneNoteTile(),
            "clock" => BuildClockTile(),
            "timer" => BuildTimerTile(),
            "stopwatch" => BuildStopwatchTile(),
            "hardware" => BuildHardwareTile(),
            "finance" => BuildFinanceTile(),
            "pet" => BuildPetTile(),
            "carousel" => BuildCustomCarouselTile(),
            "" => BuildEmptySlotTile(),
            _ => BuildCustomCarouselTile()
        };

        private static string FormatDuration(TimeSpan ts)
        {
            if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
                : $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}";
        }

        public override void Dispose()
        {
            Stop();
            _tickTimer = null;
            _noteSaveTimer?.Stop();
            _noteSaveTimer = null;
            base.Dispose();
        }
    }
}
