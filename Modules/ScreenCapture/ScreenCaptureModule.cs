using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.FileProperties;
using WindowsNotch.Core;
using WindowsNotch.Services;
using Forms = System.Windows.Forms;

namespace WindowsNotch.Modules.ScreenCapture
{
    /// <summary>
    /// Bildschirmaufnahme-Modul im Notchify-Design (Phase 3.6):
    /// Video- &amp; Audio-Aufnahme (Windows Graphics Capture + WASAPI) mit Aufnahmen/Einstellungen-Tabs &amp; Vorschau.
    /// </summary>
    public partial class ScreenCaptureModule : NotchModuleBase
    {
        private enum CaptureSubTab { Recordings, Settings }

        private readonly ScreenCaptureService _service = new();
        private readonly ScreenCaptureOptions _options = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly Dictionary<string, ImageSource> _thumbCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _thumbLoading = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<WeakReference<Border>>> _pendingThumbContainers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _thumbFailures = new(StringComparer.OrdinalIgnoreCase);

        private Border? _expandedHost;
        private Border? _liveHost;
        private TextBlock? _liveTimerText;
        private TextBlock? _headerRecText;
        private MediaElement? _previewPlayer;
        private bool _isPlayingPreview;
        private CaptureSubTab _activeTab = CaptureSubTab.Recordings;
        private bool _animateNextSubView;
        private string? _selectedFilePath;
        private bool _busy;

        public override string Id => "screencapture";
        public override string Title => L.T("module.screencapture");
        public override NotchIconKind IconKind => NotchIconKind.Record;
        public override int Priority => _service.IsRecording ? 180 : 8;
        public override double? PreferredLiveWidth => _service.IsRecording ? 118 : null;
        public override double? PreferredPreviewWidth => _service.IsRecording ? 190 : 180;
        public override (double Width, double Height)? PreferredExpandedSize => (640, 412);

        public override void Start()
        {
            _service.StateChanged -= OnServiceStateChanged;
            _service.StateChanged += OnServiceStateChanged;
            _timer.Tick -= OnTimerTick;
            _timer.Tick += OnTimerTick;
        }

        public override void Stop()
        {
            _timer.Stop();
            StopPreviewPlayback();
            _service.StateChanged -= OnServiceStateChanged;
            _timer.Tick -= OnTimerTick;
        }

        private void StopPreviewPlayback()
        {
            _isPlayingPreview = false;
            try
            {
                _previewPlayer?.Stop();
                if (_previewPlayer != null) _previewPlayer.Source = null;
            }
            catch { }
            _previewPlayer = null;
        }

        public override void Dispose()
        {
            Stop();
            _service.Dispose();
            base.Dispose();
        }

        private void OnServiceStateChanged()
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_service.IsRecording)
                    _timer.Start();
                else
                    _timer.Stop();

                RefreshViews();
                RaiseStateChanged();
            });
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            if (!_service.IsRecording)
            {
                _timer.Stop();
                return;
            }

            string elapsed = FormatElapsed(_service.Elapsed);
            if (_liveTimerText != null) _liveTimerText.Text = elapsed;
            if (_headerRecText != null) _headerRecText.Text = L.F("screencapture.stop_btn", elapsed);
        }

        private void RefreshViews()
        {
            if (_expandedHost != null && _expandedHost.IsLoaded)
                _expandedHost.Child = BuildExpandedContent();
            if (_liveHost != null)
                _liveHost.Child = BuildLiveContent();
        }

        public override FrameworkElement? CreateLiveView()
        {
            if (!_service.IsRecording) return null;
            _liveHost = new Border { Background = Brushes.Transparent };
            _liveHost.Child = BuildLiveContent();
            return _liveHost;
        }

        private FrameworkElement BuildLiveContent()
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            stack.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            _liveTimerText = new TextBlock
            {
                Text = FormatElapsed(_service.Elapsed),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(_liveTimerText);
            return stack;
        }

        public override FrameworkElement CreatePreviewView()
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var icon = NotchDesign.CreateVectorIcon(
                NotchIconKind.Record,
                13,
                _service.IsRecording
                    ? new SolidColorBrush(NotchDesign.AccentRed)
                    : NotchDesign.AccentPrimaryBrush,
                2.0);
            icon.Margin = new Thickness(0, 0, 7, 0);

            stack.Children.Add(icon);
            stack.Children.Add(new TextBlock
            {
                Text = _service.IsRecording ? L.F("screencapture.recording_preview", FormatElapsed(_service.Elapsed)) : L.T("module.screencapture"),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            return stack;
        }

        public override FrameworkElement CreateExpandedView()
        {
            _expandedHost = new Border { Background = Brushes.Transparent };
            _expandedHost.Child = BuildExpandedContent();
            return _expandedHost;
        }

        private FrameworkElement BuildExpandedContent()
        {
            var root = new StackPanel { Margin = new Thickness(6, 2, 6, 2) };

            root.Children.Add(BuildHeader());
            root.Children.Add(BuildSubTabBar());

            if (!string.IsNullOrWhiteSpace(_service.LastError))
            {
                root.Children.Add(new TextBlock
                {
                    Text = _service.LastError,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x69, 0x61)),
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(2, 0, 2, 8)
                });
            }

            var subView = _activeTab == CaptureSubTab.Recordings
                ? BuildRecordingsSplitView()
                : BuildSettingsView();
            if (_animateNextSubView)
            {
                _animateNextSubView = false;
                NotchDesign.AnimateSubViewEnter(subView, slideY: 5);
            }
            root.Children.Add(subView);

            return root;
        }

        private Grid BuildHeader()
        {
            var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Links: App-Badge + Titel + aktiver Status-Untertitel
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var badge = new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = new CornerRadius(11),
                Background = _service.IsRecording
                    ? new SolidColorBrush(Color.FromArgb(0x2A, 0xEA, 0x43, 0x35))
                    : NotchDesign.CreateSoftAccentTileBrush(0x1E),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var badgeGrid = new Grid();
            badgeGrid.Children.Add(NotchDesign.CreateVectorIcon(
                NotchIconKind.Monitor,
                18,
                _service.IsRecording ? NotchDesign.TextPrimaryBrush : NotchDesign.AccentPrimaryBrush,
                1.9));
            badgeGrid.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = NotchDesign.GetFrozenBrush(NotchDesign.AccentRed),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 7, 7, 0)
            });
            badge.Child = badgeGrid;
            left.Children.Add(badge);

            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Text = L.T("module.screencapture"),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 15.5,
                FontWeight = FontWeights.Bold
            });

            string qualityText = _options.Quality switch
            {
                CaptureQuality.Low480p => "480p",
                CaptureQuality.Medium720p => "720p",
                CaptureQuality.High1080p => "1080p",
                _ => L.T("screencapture.quality_native")
            };
            titleStack.Children.Add(new TextBlock
            {
                Text = L.F("screencapture.monitor_meta", _options.MonitorIndex + 1, qualityText, _options.Fps),
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                Margin = new Thickness(0, 1, 0, 0)
            });
            left.Children.Add(titleStack);
            Grid.SetColumn(left, 0);
            header.Children.Add(left);

            // Rechts: Roter Pill-Button "Aufnahme starten" + kompakter Mic/Audio-Toggle-Pill
            var right = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            bool rec = _service.IsRecording;
            var recRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            recRow.Children.Add(NotchDesign.CreateVectorIcon(NotchIconKind.Record, 14, Brushes.White, 2.0));
            _headerRecText = new TextBlock
            {
                Text = rec ? L.F("screencapture.stop_btn", FormatElapsed(_service.Elapsed)) : L.T("screencapture.start_btn"),
                Foreground = Brushes.White,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            recRow.Children.Add(_headerRecText);

            var recBtn = NotchDesign.CreateRoundedButton(
                recRow,
                () => _ = ToggleRecordingAsync(),
                background: new SolidColorBrush(rec ? Color.FromRgb(0xC9, 0x34, 0x2B) : Color.FromRgb(0xE5, 0x48, 0x3B)),
                hoverBackground: new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x4D)),
                cornerRadius: 17,
                padding: new Thickness(14, 8, 16, 8),
                tooltip: rec ? L.T("screencapture.stop_tooltip") : L.T("screencapture.start_tooltip"));
            recBtn.Margin = new Thickness(0, 0, 10, 0);
            right.Children.Add(recBtn);

            // Schnelltoggles für Mikrofon & Systemaudio
            var quickPill = new Border
            {
                CornerRadius = new CornerRadius(17),
                Background = NotchDesign.CardBackgroundBrush,
                Padding = new Thickness(4, 3, 4, 3),
                VerticalAlignment = VerticalAlignment.Center
            };
            var quickRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var micBtn = CreateQuickIconToggle(
                NotchIconKind.Microphone,
                _options.IncludeMicrophone,
                rec,
                _options.IncludeMicrophone ? L.T("screencapture.mic_on") : L.T("screencapture.mic_off"),
                () =>
                {
                    _options.IncludeMicrophone = !_options.IncludeMicrophone;
                    RefreshViews();
                });
            quickRow.Children.Add(micBtn);

            quickRow.Children.Add(new Border
            {
                Width = 1,
                Height = 16,
                Background = NotchDesign.TrackBrush,
                Margin = new Thickness(2, 0, 2, 0)
            });

            var sysBtn = CreateQuickIconToggle(
                _options.IncludeSystemAudio ? NotchIconKind.VolumeOn : NotchIconKind.VolumeOff,
                _options.IncludeSystemAudio,
                rec,
                _options.IncludeSystemAudio ? L.T("screencapture.sys_audio_on") : L.T("screencapture.sys_audio_off"),
                () =>
                {
                    _options.IncludeSystemAudio = !_options.IncludeSystemAudio;
                    RefreshViews();
                });
            quickRow.Children.Add(sysBtn);

            quickPill.Child = quickRow;
            right.Children.Add(quickPill);

            Grid.SetColumn(right, 1);
            header.Children.Add(right);
            return header;
        }

        private Border BuildSubTabBar()
        {
            var bar = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Margin = new Thickness(0, 0, 0, 12)
            };

            var tabs = new StackPanel { Orientation = Orientation.Horizontal };
            tabs.Children.Add(CreateSubTabButton(
                NotchIconKind.Play,
                L.T("screencapture.tab_recordings"),
                _activeTab == CaptureSubTab.Recordings,
                () =>
                {
                    StopPreviewPlayback();
                    _activeTab = CaptureSubTab.Recordings;
                    _animateNextSubView = true;
                    RefreshViews();
                }));

            tabs.Children.Add(CreateSubTabButton(
                NotchIconKind.SettingsGear,
                L.T("screencapture.tab_settings"),
                _activeTab == CaptureSubTab.Settings,
                () =>
                {
                    StopPreviewPlayback();
                    _activeTab = CaptureSubTab.Settings;
                    _animateNextSubView = true;
                    RefreshViews();
                }));

            bar.Child = tabs;
            return bar;
        }

        private static Border CreateSubTabButton(NotchIconKind iconKind, string label, bool isActive, Action onClick)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 8)
            };
            var color = isActive ? NotchDesign.TextPrimaryBrush : NotchDesign.TextMutedBrush;
            var icon = NotchDesign.CreateVectorIcon(iconKind, 13, color, 1.9);
            icon.Margin = new Thickness(0, 0, 7, 0);
            row.Children.Add(icon);
            row.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = color,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            });

            var container = new Border
            {
                BorderBrush = isActive ? NotchDesign.AccentPrimaryBrush : Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Margin = new Thickness(0, 0, 22, -1),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = row
            };
            container.MouseLeftButtonUp += (_, _) => onClick();
            return container;
        }

        private FrameworkElement BuildRecordingsSplitView()
        {
            var recordings = ScreenCaptureService.GetRecentRecordings(15);
            if (recordings.Count == 0)
            {
                var emptyCard = NotchDesign.CreateCard(14, new Thickness(20));
                emptyCard.Background = NotchDesign.CardBackgroundBrush;
                emptyCard.Height = 230;
                var emptyStack = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                emptyStack.Children.Add(new TextBlock
                {
                    Text = L.T("screencapture.empty_title"),
                    Foreground = NotchDesign.TextSecondaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                emptyStack.Children.Add(new TextBlock
                {
                    Text = L.T("screencapture.empty_hint"),
                    Foreground = NotchDesign.TextMutedBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11.5,
                    Margin = new Thickness(0, 4, 0, 12),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                var openFolderBtn = NotchDesign.CreateRoundedButton(
                    new TextBlock
                    {
                        Text = L.T("screencapture.open_folder"),
                        Foreground = NotchDesign.TextPrimaryBrush,
                        FontFamily = NotchDesign.UiTextFont,
                        FontSize = 11.5,
                        FontWeight = FontWeights.Medium
                    },
                    () => OpenFolder(ScreenCaptureService.RecordingsFolder),
                    background: NotchDesign.NestedCardBackgroundBrush,
                    hoverBackground: NotchDesign.ControlHoverBrush,
                    cornerRadius: 12,
                    padding: new Thickness(12, 6, 12, 6));
                openFolderBtn.HorizontalAlignment = HorizontalAlignment.Center;
                emptyStack.Children.Add(openFolderBtn);
                emptyCard.Child = emptyStack;
                return emptyCard;
            }

            var selected = recordings.FirstOrDefault(r => string.Equals(r.FilePath, _selectedFilePath, StringComparison.OrdinalIgnoreCase))
                           ?? recordings[0];
            _selectedFilePath = selected.FilePath;

            var split = new Grid { Height = 236 };
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Linke Spalte: Scrollbare Liste der Aufnahmen
            var listStack = new StackPanel();
            foreach (var rec in recordings)
            {
                bool isSel = string.Equals(rec.FilePath, selected.FilePath, StringComparison.OrdinalIgnoreCase);
                listStack.Children.Add(BuildRecordingListRow(rec, isSel));
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = listStack
            };
            NotchDesign.ApplySlimScrollBarStyle(scroll);
            Grid.SetColumn(scroll, 0);
            split.Children.Add(scroll);

            // Rechte Spalte: Vorschau-Karte der gewählten Aufnahme
            var previewCard = BuildRecordingPreviewCard(selected);
            Grid.SetColumn(previewCard, 2);
            split.Children.Add(previewCard);

            return split;
        }

        private Border BuildRecordingListRow(RecordingEntry rec, bool isSelected)
        {
            var rowCard = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = isSelected ? NotchDesign.CreateSoftAccentTileBrush(0x1E) : Brushes.Transparent,
                Padding = new Thickness(8, 7, 8, 7),
                Margin = new Thickness(0, 0, 0, 5),
                Cursor = Cursors.Hand
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var thumb = CreateThumbnailElement(rec.FilePath, 62, 38, 6);
            thumb.Margin = new Thickness(0, 0, 10, 0);
            Grid.SetColumn(thumb, 0);
            grid.Children.Add(thumb);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = System.IO.Path.GetFileNameWithoutExtension(rec.FileName),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{rec.CreatedAt:dd.MM.yyyy HH:mm} · {rec.FormattedSize}",
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(info, 1);
            grid.Children.Add(info);

            var delBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Trash, 11.5, NotchDesign.TextMutedBrush, 1.8),
                () => DeleteRecording(rec.FilePath),
                background: NotchDesign.NestedCardBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                cornerRadius: 12,
                padding: new Thickness(0),
                tooltip: L.T("screencapture.delete_rec"));
            delBtn.Width = 24;
            delBtn.Height = 24;
            delBtn.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(delBtn, 2);
            grid.Children.Add(delBtn);

            rowCard.Child = grid;
            rowCard.MouseLeftButtonUp += (_, _) =>
            {
                StopPreviewPlayback();
                _selectedFilePath = rec.FilePath;
                RefreshViews();
            };
            rowCard.MouseEnter += (_, _) =>
            {
                if (!isSelected) rowCard.Background = NotchDesign.NestedCardBackgroundBrush;
            };
            rowCard.MouseLeave += (_, _) =>
            {
                if (!isSelected) rowCard.Background = Brushes.Transparent;
            };

            return rowCard;
        }

        private Border BuildRecordingPreviewCard(RecordingEntry rec)
        {
            var card = NotchDesign.CreateCard(14, new Thickness(12));
            card.Background = NotchDesign.CardBackgroundBrush;

            var dock = new DockPanel();

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 10, 0, 0)
            };

            var playIconHost = new Border
            {
                VerticalAlignment = VerticalAlignment.Center,
                Child = NotchDesign.CreateVectorIcon(
                    _isPlayingPreview ? NotchIconKind.Pause : NotchIconKind.Play,
                    11,
                    NotchDesign.AccentPrimaryBrush,
                    1.9)
            };
            var playLabel = new TextBlock
            {
                Text = _isPlayingPreview ? L.T("screencapture.pause_btn") : L.T("screencapture.preview_btn"),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var playRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            playRow.Children.Add(playIconHost);
            playRow.Children.Add(playLabel);

            var previewStage = new Border
            {
                Width = 216,
                Height = 112,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x13, 0x18)),
                ClipToBounds = true,
                Cursor = Cursors.Hand
            };

            var thumbView = CreateThumbnailElement(rec.FilePath, 216, 112, 10);
            _previewPlayer = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Stop,
                Stretch = Stretch.Uniform,
                Visibility = Visibility.Collapsed
            };
            _previewPlayer.MediaEnded += (_, _) =>
            {
                _isPlayingPreview = false;
                _previewPlayer.Stop();
                _previewPlayer.Visibility = Visibility.Collapsed;
                thumbView.Visibility = Visibility.Visible;
                playIconHost.Child = NotchDesign.CreateVectorIcon(NotchIconKind.Play, 11, NotchDesign.AccentPrimaryBrush, 1.9);
                playLabel.Text = L.T("screencapture.preview_btn");
            };

            var stageGrid = new Grid();
            stageGrid.Children.Add(thumbView);
            stageGrid.Children.Add(_previewPlayer);
            previewStage.Child = stageGrid;

            void TogglePreview()
            {
                try
                {
                    if (_isPlayingPreview)
                    {
                        _previewPlayer.Pause();
                        _isPlayingPreview = false;
                        playIconHost.Child = NotchDesign.CreateVectorIcon(NotchIconKind.Play, 11, NotchDesign.AccentPrimaryBrush, 1.9);
                        playLabel.Text = L.T("screencapture.preview_btn");
                    }
                    else
                    {
                        if (_previewPlayer.Source == null || !string.Equals(_previewPlayer.Source.LocalPath, rec.FilePath, StringComparison.OrdinalIgnoreCase))
                            _previewPlayer.Source = new Uri(rec.FilePath, UriKind.Absolute);

                        thumbView.Visibility = Visibility.Collapsed;
                        _previewPlayer.Visibility = Visibility.Visible;
                        _previewPlayer.Play();
                        _isPlayingPreview = true;
                        playIconHost.Child = NotchDesign.CreateVectorIcon(NotchIconKind.Pause, 11, NotchDesign.AccentPrimaryBrush, 1.9);
                        playLabel.Text = L.T("screencapture.pause_btn");
                    }
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                }
            }

            previewStage.MouseLeftButtonUp += (_, _) => TogglePreview();

            var playBtn = NotchDesign.CreateRoundedButton(
                playRow,
                TogglePreview,
                background: NotchDesign.CreateSoftAccentTileBrush(0x28),
                hoverBackground: NotchDesign.CreateSoftAccentTileBrush(0x3E),
                cornerRadius: 15,
                padding: new Thickness(13, 7, 15, 7),
                tooltip: L.T("screencapture.preview_tooltip"));
            playBtn.Margin = new Thickness(0, 0, 7, 0);
            actions.Children.Add(playBtn);

            var folderBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Folder, 13, NotchDesign.TextPrimaryBrush, 1.85),
                () => RevealInExplorer(rec.FilePath),
                background: NotchDesign.NestedCardBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                cornerRadius: 15,
                padding: new Thickness(0),
                tooltip: L.T("screencapture.reveal_tooltip"));
            folderBtn.Width = 30;
            folderBtn.Height = 30;
            folderBtn.Margin = new Thickness(0, 0, 6, 0);
            actions.Children.Add(folderBtn);

            var copyBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Copy, 13, NotchDesign.TextPrimaryBrush, 1.85),
                () => CopyFileToClipboard(rec.FilePath),
                background: NotchDesign.NestedCardBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                cornerRadius: 15,
                padding: new Thickness(0),
                tooltip: L.T("screencapture.copy_tooltip"));
            copyBtn.Width = 30;
            copyBtn.Height = 30;
            copyBtn.Margin = new Thickness(0, 0, 6, 0);
            actions.Children.Add(copyBtn);

            var extBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.ExternalLink, 12.5, NotchDesign.TextSecondaryBrush, 1.85),
                () => OpenFile(rec.FilePath),
                background: NotchDesign.NestedCardBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                cornerRadius: 15,
                padding: new Thickness(0),
                tooltip: L.T("screencapture.open_player_tooltip"));
            extBtn.Width = 30;
            extBtn.Height = 30;
            actions.Children.Add(extBtn);

            DockPanel.SetDock(actions, Dock.Bottom);
            dock.Children.Add(actions);

            var metaStack = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            metaStack.Children.Add(new TextBlock
            {
                Text = System.IO.Path.GetFileNameWithoutExtension(rec.FileName),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            metaStack.Children.Add(new TextBlock
            {
                Text = $"{rec.CreatedAt:dd.MM.yyyy · HH:mm} · {rec.FormattedSize}",
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0)
            });
            DockPanel.SetDock(metaStack, Dock.Bottom);
            dock.Children.Add(metaStack);

            dock.Children.Add(previewStage);

            card.Child = dock;
            return card;
        }

        private async Task ToggleRecordingAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                if (_service.IsRecording)
                {
                    await _service.StopAsync();
                }
                else
                {
                    RaiseCollapseRequested();
                    int waitMs = NotchDesign.EffectiveAnimationsEnabled
                        ? 95 + NotchDesign.CollapseDurationMs + 60
                        : 60;
                    await Task.Delay(waitMs);
                    await _service.StartAsync(_options);
                }
            }
            finally
            {
                _busy = false;
            }
        }

        private static Button CreateQuickIconToggle(NotchIconKind kind, bool isActive, bool disabled, string tooltip, Action onClick)
        {
            var btn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(
                    kind,
                    13.5,
                    isActive ? NotchDesign.TextPrimaryBrush : NotchDesign.TextMutedBrush,
                    1.9),
                disabled ? () => { } : onClick,
                background: isActive ? NotchDesign.NestedCardBackgroundBrush : Brushes.Transparent,
                hoverBackground: disabled ? Brushes.Transparent : NotchDesign.ControlHoverBrush,
                cornerRadius: 13,
                padding: new Thickness(0),
                tooltip: tooltip);
            btn.Width = 28;
            btn.Height = 28;
            btn.Opacity = disabled ? 0.55 : 1.0;
            return btn;
        }

        private static string FormatElapsed(TimeSpan ts) =>
            ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");

        private static void OpenFile(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }

        private static void OpenFolder(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }

        private static void RevealInExplorer(string path)
        {
            try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
        }

        private static void CopyFileToClipboard(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var files = new StringCollection { path };
                System.Windows.Clipboard.SetFileDropList(files);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private void DeleteRecording(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                _thumbCache.Remove(path);
                if (string.Equals(_selectedFilePath, path, StringComparison.OrdinalIgnoreCase))
                    _selectedFilePath = null;
                RefreshViews();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }
    }
}
