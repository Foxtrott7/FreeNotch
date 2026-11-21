using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.Downloads
{
    /// <summary>
    /// Downloads-Monitor: Überwacht laufende Browser-Downloads (Fortschritt, Geschwindigkeit, Restzeit)
    /// als Live Activity sowie zuletzt heruntergeladene Dateien mit Schnellzugriff im Explorer.
    /// </summary>
    public sealed class DownloadsModule : NotchModuleBase
    {
        private sealed class ActiveCardWidgets
        {
            public required TextBlock TitleText;
            public required TextBlock MetaText;
            public required Grid BarHost;
            public required Border FillBar;
            public required Border IndeterminateBar;
            public required TranslateTransform IndeterminateTransform;
            public bool IndeterminateRunning;
            public double IndeterminateTrackWidth;
            public double? LastRatio;
        }

        private readonly DownloadsService _service = new();
        private event Action? StructuralUpdated;

        private int _structuralDispatchPending;
        private int _progressDispatchPending;

        // Gecachte Live-View-Elemente, damit laufende Updates nicht flackern
        private Grid? _liveRoot;
        private TextBlock? _liveRightText;
        private Grid? _liveBarHost;
        private Border? _liveFillBar;
        private Border? _liveIndeterminateBar;
        private TranslateTransform? _liveIndeterminateTransform;
        private bool _liveIndeterminateRunning;
        private double _liveIndeterminateTrackWidth;
        private double? _liveLastRatio;

        // Gecachte Preview-View-Elemente, damit Hover stabil bleibt
        private StackPanel? _previewRoot;
        private TextBlock? _previewText;

        // Gecachte Expanded-View-Elemente für In-Place-Updates ohne Scroll/Click-Verlust
        private Border? _expandedHost;
        private readonly Dictionary<string, ActiveCardWidgets> _activeCards = new(StringComparer.OrdinalIgnoreCase);
        private string _lastStructureSignature = string.Empty;

        public override string Id => "downloads";
        public override string Title => "Downloads";
        public override NotchIconKind IconKind => NotchIconKind.Download;

        public override int Priority
        {
            get
            {
                if (_service.HasActiveDownloadWithProgress) return 160;
                if (_service.HasRecentCompletion) return 130;
                return 10;
            }
        }

        // Immer mindestens so breit wie PreferredLiveWidth, damit Hover nicht unter dem Cursor schrumpft
        public override double? PreferredPreviewWidth =>
            _service.HasActiveDownloadWithProgress ? 280 : (_service.HasRecentCompletion ? 236 : 230);

        public override double? PreferredLiveWidth =>
            _service.HasActiveDownloadWithProgress ? 268 : (_service.HasRecentCompletion ? 218 : null);

        public override (double Width, double Height)? PreferredExpandedSize => (500, 370);

        public DownloadsModule()
        {
            _service.StateChanged += ScheduleStructuralDispatch;
            _service.ProgressUpdated += ScheduleProgressDispatch;
        }

        private void ScheduleStructuralDispatch()
        {
            if (Interlocked.Exchange(ref _structuralDispatchPending, 1) != 0)
                return;

            var disp = Application.Current?.Dispatcher;
            if (disp == null)
            {
                Interlocked.Exchange(ref _structuralDispatchPending, 0);
                return;
            }

            disp.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _structuralDispatchPending, 0);
                var active = _service.GetActiveDownloads();
                var completed = _service.GetCompletedDownloads();
                UpdateLiveAndPreviewInPlace(active, completed);
                StructuralUpdated?.Invoke();
                RaiseStateChanged();
            });
        }

        private void ScheduleProgressDispatch()
        {
            if (Interlocked.Exchange(ref _progressDispatchPending, 1) != 0)
                return;

            var disp = Application.Current?.Dispatcher;
            if (disp == null)
            {
                Interlocked.Exchange(ref _progressDispatchPending, 0);
                return;
            }

            disp.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _progressDispatchPending, 0);
                var active = _service.GetActiveDownloads();
                var completed = _service.GetCompletedDownloads();
                UpdateLiveAndPreviewInPlace(active, completed);
                UpdateExpandedCardsInPlace(active);
            });
        }

        public override void Start() => _service.Start();

        public override void Stop() => _service.Stop();

        public override void Dispose()
        {
            _service.StateChanged -= ScheduleStructuralDispatch;
            _service.ProgressUpdated -= ScheduleProgressDispatch;
            _service.Dispose();
            base.Dispose();
        }

        public override FrameworkElement? CreateLiveView()
        {
            var withProgress = _service.GetActiveDownloads().FirstOrDefault(a => a.ProgressRatio != null);
            if (withProgress != null)
                return GetOrUpdateActiveLivePill(withProgress);

            _liveRoot = null;
            _liveBarHost = null;
            _liveFillBar = null;
            _liveIndeterminateRunning = false;
            _liveIndeterminateTrackWidth = 0;
            _liveLastRatio = null;

            if (_service.HasRecentCompletion && _service.RecentCompletedName is { } doneName)
                return BuildCompletedLivePill(doneName);

            return null;
        }

        public override FrameworkElement CreatePreviewView()
        {
            if (_previewRoot == null)
            {
                _previewRoot = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var icon = NotchDesign.CreateVectorIcon(NotchIconKind.Download, 14, NotchDesign.AccentPrimaryBrush, 2.15);
                icon.Margin = new Thickness(0, 0, 7, 0);

                _previewText = new TextBlock
                {
                    Foreground = NotchDesign.TextPrimaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 235
                };

                _previewRoot.Children.Add(icon);
                _previewRoot.Children.Add(_previewText);
            }

            UpdatePreviewText(_service.GetActiveDownloads(), _service.GetCompletedDownloads());
            return _previewRoot;
        }

        public override FrameworkElement CreateExpandedView()
        {
            var host = new Border { Background = Brushes.Transparent };
            _expandedHost = host;

            void SyncExpanded()
            {
                var active = _service.GetActiveDownloads();
                var completed = _service.GetCompletedDownloads();
                string sig = BuildStructureSignature(active, completed);
                if (host.Child != null && sig == _lastStructureSignature)
                {
                    UpdateExpandedCardsInPlace(active);
                    return;
                }

                _lastStructureSignature = sig;
                host.Child = BuildExpandedContent(active, completed);
            }

            _lastStructureSignature = string.Empty;
            SyncExpanded();

            host.Loaded += (_, _) => StructuralUpdated += SyncExpanded;
            host.Unloaded += (_, _) =>
            {
                StructuralUpdated -= SyncExpanded;
                if (ReferenceEquals(_expandedHost, host))
                {
                    _expandedHost = null;
                    _activeCards.Clear();
                }
            };
            return host;
        }

        private void UpdateLiveAndPreviewInPlace(
            IReadOnlyList<ActiveDownloadItem> active,
            IReadOnlyList<CompletedDownloadItem> completed)
        {
            var withProgress = active.FirstOrDefault(a => a.ProgressRatio != null);
            if (_liveRoot != null && withProgress != null)
                GetOrUpdateActiveLivePill(withProgress);

            if (_previewRoot != null)
                UpdatePreviewText(active, completed);
        }

        private void UpdatePreviewText(
            IReadOnlyList<ActiveDownloadItem> active,
            IReadOnlyList<CompletedDownloadItem> completed)
        {
            if (_previewText == null) return;

            if (active.Count > 0)
            {
                var first = active[0];
                string speed = DownloadsService.FormatSpeed(first.BytesPerSecond);
                _previewText.Text = first.ProgressRatio is { } ratio
                    ? $"{Math.Round(ratio * 100):0}% • {speed}"
                    : $"{DownloadsService.FormatBytes(first.DownloadedBytes)} • {speed}";
            }
            else
            {
                _previewText.Text = completed.Count switch
                {
                    0 => L.T("module.downloads"),
                    1 => L.T("downloads.count_one"),
                    _ => L.F("downloads.count_many", completed.Count)
                };
            }
        }

        private FrameworkElement GetOrUpdateActiveLivePill(ActiveDownloadItem item)
        {
            if (_liveRoot == null)
            {
                _liveRoot = new Grid
                {
                    Width = 236,
                    Margin = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                // Links: Icon | Mitte: Balken | Rechts: Fortschritt/Größe + Speed
                _liveRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                _liveRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                _liveRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var icon = NotchDesign.CreateVectorIcon(NotchIconKind.Download, 13, NotchDesign.AccentPrimaryBrush, 2.15);
                icon.Margin = new Thickness(0, 0, 8, 0);
                icon.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(icon, 0);

                var barTrack = new Border
                {
                    Height = 4,
                    MinWidth = 72,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    CornerRadius = new CornerRadius(2),
                    Background = NotchDesign.TrackBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    ClipToBounds = true
                };
                _liveBarHost = new Grid();
                _liveFillBar = new Border
                {
                    Width = 0,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    CornerRadius = new CornerRadius(2),
                    Background = NotchDesign.AccentPrimaryBrush
                };

                _liveIndeterminateTransform = new TranslateTransform(0, 0);
                _liveIndeterminateBar = new Border
                {
                    Width = 34,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    CornerRadius = new CornerRadius(2),
                    Background = NotchDesign.AccentPrimaryBrush,
                    RenderTransform = _liveIndeterminateTransform,
                    Visibility = Visibility.Collapsed
                };

                _liveBarHost.Children.Add(_liveFillBar);
                _liveBarHost.Children.Add(_liveIndeterminateBar);
                _liveBarHost.SizeChanged += (_, _) =>
                {
                    if (_liveBarHost == null) return;
                    if (_liveLastRatio is { } r && _liveFillBar != null)
                    {
                        ApplySmoothBarWidth(_liveFillBar, _liveBarHost.ActualWidth, r, animate: false);
                    }
                    else if (_liveIndeterminateRunning && _liveIndeterminateBar != null && _liveIndeterminateTransform != null)
                    {
                        double tw = _liveBarHost.ActualWidth;
                        if (Math.Abs(tw - _liveIndeterminateTrackWidth) > 1.0)
                        {
                            _liveIndeterminateTrackWidth = tw;
                            StartIndeterminateAnimation(_liveIndeterminateTransform, tw, _liveIndeterminateBar.Width, 1.05);
                        }
                    }
                };

                barTrack.Child = _liveBarHost;
                Grid.SetColumn(barTrack, 1);

                _liveRightText = new TextBlock
                {
                    Foreground = NotchDesign.TextPrimaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                    TextAlignment = TextAlignment.Right
                };
                Grid.SetColumn(_liveRightText, 2);

                _liveRoot.Children.Add(icon);
                _liveRoot.Children.Add(barTrack);
                _liveRoot.Children.Add(_liveRightText);
                _liveIndeterminateRunning = false;
                _liveIndeterminateTrackWidth = 0;
                _liveLastRatio = null;
            }

            if (_liveRightText != null && item.ProgressRatio is { } ratioPct)
                _liveRightText.Text = $"{Math.Round(ratioPct * 100):0}%";

            if (item.ProgressRatio is { } ratio)
            {
                _liveLastRatio = ratio;
                if (_liveFillBar != null && _liveBarHost != null)
                {
                    _liveFillBar.Visibility = Visibility.Visible;
                    ApplySmoothBarWidth(_liveFillBar, _liveBarHost.ActualWidth, ratio, animate: NotchDesign.EffectiveAnimationsEnabled);
                }

                if (_liveIndeterminateBar != null && _liveIndeterminateRunning)
                {
                    _liveIndeterminateRunning = false;
                    _liveIndeterminateTrackWidth = 0;
                    _liveIndeterminateBar.Visibility = Visibility.Collapsed;
                    _liveIndeterminateTransform?.BeginAnimation(TranslateTransform.XProperty, null);
                }
            }
            else
            {
                _liveLastRatio = null;
                if (_liveFillBar != null)
                {
                    _liveFillBar.BeginAnimation(FrameworkElement.WidthProperty, null);
                    _liveFillBar.Width = 0;
                    _liveFillBar.Visibility = Visibility.Collapsed;
                }

                if (_liveIndeterminateBar != null && _liveIndeterminateTransform != null && _liveBarHost != null && !_liveIndeterminateRunning)
                {
                    _liveIndeterminateRunning = true;
                    _liveIndeterminateBar.Visibility = Visibility.Visible;
                    double tw = _liveBarHost.ActualWidth > 0 ? _liveBarHost.ActualWidth : 90;
                    _liveIndeterminateTrackWidth = tw;
                    StartIndeterminateAnimation(_liveIndeterminateTransform, tw, _liveIndeterminateBar.Width, 1.05);
                }
            }

            return _liveRoot;
        }

        private static FrameworkElement BuildCompletedLivePill(string fileName)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0)
            };

            var green = new SolidColorBrush(NotchDesign.IsLight
                ? Color.FromRgb(0x2E, 0x8B, 0x57)
                : Color.FromRgb(0x5E, 0xC2, 0x8E));
            var icon = NotchDesign.CreateVectorIcon(NotchIconKind.Check, 12, green, 2.2);
            icon.Margin = new Thickness(0, 0, 6, 0);

            var text = new TextBlock
            {
                Text = L.F("downloads.done_toast", fileName),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 175,
                VerticalAlignment = VerticalAlignment.Center
            };

            stack.Children.Add(icon);
            stack.Children.Add(text);
            return stack;
        }

        private static string BuildStructureSignature(
            IReadOnlyList<ActiveDownloadItem> active,
            IReadOnlyList<CompletedDownloadItem> completed)
        {
            return string.Join("|",
                string.Join(",", active.Select(a => a.TempPath)),
                string.Join(",", completed.Select(c => c.FullPath)));
        }

        private FrameworkElement BuildExpandedContent(
            IReadOnlyList<ActiveDownloadItem> active,
            IReadOnlyList<CompletedDownloadItem> completed)
        {
            _activeCards.Clear();

            var root = new Grid { Margin = new Thickness(4, 2, 4, 0) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Header – sauber in einer Linie mit den Karten darunter
            var openFolderBtn = NotchDesign.CreateIconActionButton(
                NotchIconKind.Folder,
                L.T("downloads.open_folder"),
                _service.OpenDownloadsFolder);
            openFolderBtn.Margin = new Thickness(0, 0, 6, 0);

            var clearBtn = NotchDesign.CreateIconActionButton(
                NotchIconKind.Trash,
                L.T("downloads.clear_list"),
                _service.ClearCompletedList);
            clearBtn.IsEnabled = completed.Count > 0;
            clearBtn.Opacity = completed.Count > 0 ? 1.0 : 0.45;

            var header = NotchDesign.CreateModuleHeader(
                NotchIconKind.Download,
                L.T("module.downloads"),
                new Thickness(0, 0, 0, 12),
                iconBoxSize: 34,
                iconSize: 22,
                strokeThickness: 2.2,
                openFolderBtn,
                clearBtn);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // Inhalt
            if (active.Count == 0 && completed.Count == 0)
            {
                var emptyCard = BuildEmptyCard();
                Grid.SetRow(emptyCard, 1);
                root.Children.Add(emptyCard);
                return root;
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 2, 12)
            };
            NotchDesign.ApplySlimScrollBarStyle(scroll);

            var list = new StackPanel();
            foreach (var item in active)
                list.Children.Add(BuildActiveCard(item));

            foreach (var item in completed)
                list.Children.Add(BuildCompletedCard(item));

            scroll.Content = list;
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);
            return root;
        }

        private void UpdateExpandedCardsInPlace(IReadOnlyList<ActiveDownloadItem> active)
        {
            if (_expandedHost == null || !_expandedHost.IsLoaded) return;

            foreach (var item in active)
            {
                if (_activeCards.TryGetValue(item.TempPath, out var widgets))
                    ApplyActiveItemToWidgets(item, widgets, animateBar: NotchDesign.EffectiveAnimationsEnabled);
            }
        }

        private Border BuildEmptyCard()
        {
            var card = NotchDesign.CreateCard(16, new Thickness(20, 18, 20, 18));
            card.VerticalAlignment = VerticalAlignment.Stretch;
            card.Margin = new Thickness(0, 0, 0, 8);

            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            stack.Children.Add(new Border
            {
                Width = 48,
                Height = 48,
                CornerRadius = new CornerRadius(14),
                Background = NotchDesign.CreateSoftAccentTileBrush(0x22),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10),
                Child = NotchDesign.CreateVectorIcon(NotchIconKind.Download, 24, NotchDesign.AccentPrimaryBrush, 2.1)
            });

            stack.Children.Add(new TextBlock
            {
                Text = L.T("downloads.empty_title"),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 14.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            stack.Children.Add(new TextBlock
            {
                Text = L.T("downloads.empty_hint"),
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320,
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            card.Child = stack;
            return card;
        }

        private FrameworkElement BuildActiveCard(ActiveDownloadItem item)
        {
            var card = NotchDesign.CreateCard(14, new Thickness(12, 10, 12, 10));
            card.Margin = new Thickness(0, 0, 0, 8);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconWrap = new Border
            {
                Width = 34,
                Height = 34,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = NotchDesign.CreateVectorIcon(NotchIconKind.Download, 20, NotchDesign.AccentPrimaryBrush, 2.15)
            };
            Grid.SetColumn(iconWrap, 0);

            var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var titleText = new TextBlock
            {
                Text = item.DisplayName,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            center.Children.Add(titleText);

            var metaText = new TextBlock
            {
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 7)
            };
            center.Children.Add(metaText);

            var barTrack = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = NotchDesign.TrackBrush,
                ClipToBounds = true
            };
            var barHost = new Grid();
            var fillBar = new Border
            {
                Width = 0,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(2),
                Background = NotchDesign.CreateAccentGradientBrush(horizontal: true)
            };
            var indTransform = new TranslateTransform(0, 0);
            var indBar = new Border
            {
                Width = 64,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(2),
                Background = NotchDesign.CreateAccentGradientBrush(horizontal: true),
                RenderTransform = indTransform,
                Visibility = Visibility.Collapsed
            };
            barHost.Children.Add(fillBar);
            barHost.Children.Add(indBar);
            barTrack.Child = barHost;
            center.Children.Add(barTrack);
            Grid.SetColumn(center, 1);

            var widgets = new ActiveCardWidgets
            {
                TitleText = titleText,
                MetaText = metaText,
                BarHost = barHost,
                FillBar = fillBar,
                IndeterminateBar = indBar,
                IndeterminateTransform = indTransform
            };
            _activeCards[item.TempPath] = widgets;

            barHost.SizeChanged += (_, _) =>
            {
                if (widgets.LastRatio is { } r)
                {
                    ApplySmoothBarWidth(widgets.FillBar, widgets.BarHost.ActualWidth, r, animate: false);
                }
                else if (widgets.IndeterminateRunning)
                {
                    double tw = widgets.BarHost.ActualWidth;
                    if (Math.Abs(tw - widgets.IndeterminateTrackWidth) > 1.0)
                    {
                        widgets.IndeterminateTrackWidth = tw;
                        StartIndeterminateAnimation(widgets.IndeterminateTransform, tw, widgets.IndeterminateBar.Width, 1.15);
                    }
                }
            };

            ApplyActiveItemToWidgets(item, widgets, animateBar: false);

            row.Children.Add(iconWrap);
            row.Children.Add(center);
            card.Child = row;
            return card;
        }

        private static void ApplyActiveItemToWidgets(ActiveDownloadItem item, ActiveCardWidgets widgets, bool animateBar)
        {
            widgets.TitleText.Text = item.DisplayName;

            var metaParts = new List<string>
            {
                item.ProgressRatio is { } r
                    ? $"{Math.Round(r * 100):0}% ({DownloadsService.FormatBytes(item.DownloadedBytes)} / {DownloadsService.FormatBytes(item.TotalBytes ?? 0)})"
                    : DownloadsService.FormatBytes(item.DownloadedBytes),
                DownloadsService.FormatSpeed(item.BytesPerSecond)
            };
            var eta = DownloadsService.FormatEta(item.EstimatedRemaining);
            if (!string.IsNullOrEmpty(eta))
                metaParts.Add(eta);

            widgets.MetaText.Text = string.Join(" · ", metaParts);

            if (item.ProgressRatio is { } ratio)
            {
                widgets.LastRatio = ratio;
                widgets.FillBar.Visibility = Visibility.Visible;
                ApplySmoothBarWidth(widgets.FillBar, widgets.BarHost.ActualWidth, ratio, animateBar);

                if (widgets.IndeterminateRunning)
                {
                    widgets.IndeterminateRunning = false;
                    widgets.IndeterminateTrackWidth = 0;
                    widgets.IndeterminateBar.Visibility = Visibility.Collapsed;
                    widgets.IndeterminateTransform.BeginAnimation(TranslateTransform.XProperty, null);
                }
            }
            else
            {
                widgets.LastRatio = null;
                widgets.FillBar.BeginAnimation(FrameworkElement.WidthProperty, null);
                widgets.FillBar.Width = 0;
                widgets.FillBar.Visibility = Visibility.Collapsed;

                if (!widgets.IndeterminateRunning)
                {
                    widgets.IndeterminateRunning = true;
                    widgets.IndeterminateBar.Visibility = Visibility.Visible;
                    double tw = widgets.BarHost.ActualWidth > 0 ? widgets.BarHost.ActualWidth : 340;
                    widgets.IndeterminateTrackWidth = tw;
                    StartIndeterminateAnimation(widgets.IndeterminateTransform, tw, widgets.IndeterminateBar.Width, 1.15);
                }
            }
        }

        private FrameworkElement BuildCompletedCard(CompletedDownloadItem item)
        {
            // Sanftes, gedämpftes Mintgrün ohne grellen Neon-Stich
            var doneGreen = new SolidColorBrush(NotchDesign.IsLight
                ? Color.FromRgb(0x2E, 0x8B, 0x57)
                : Color.FromRgb(0x5E, 0xC2, 0x8E));

            var card = NotchDesign.CreateCard(13, new Thickness(12, 9, 12, 9));
            card.Margin = new Thickness(0, 0, 0, 6);
            card.Cursor = Cursors.Hand;
            card.ToolTip = L.T("downloads.completed_tooltip");

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconHost = new Border
            {
                Width = 34,
                Height = 34,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = NotchDesign.CreateVectorIcon(NotchIconKind.Folder, 20, doneGreen, 2.15)
            };
            Grid.SetColumn(iconHost, 0);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = item.FileName,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            info.Children.Add(new TextBlock
            {
                Text = $"{DownloadsService.FormatBytes(item.SizeBytes)} · {L.T("downloads.show_in_folder")}",
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 6)
            });

            info.Children.Add(new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = doneGreen,
                Opacity = 0.72
            });

            Grid.SetColumn(info, 1);

            row.Children.Add(iconHost);
            row.Children.Add(info);
            card.Child = row;

            Point dragStart = default;
            card.MouseEnter += (_, _) => card.Background = NotchDesign.NestedCardBackgroundBrush;
            card.MouseLeave += (_, _) => card.Background = NotchDesign.CardBackgroundBrush;
            card.PreviewMouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(card);
            card.MouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                var delta = e.GetPosition(card) - dragStart;
                if ((Math.Abs(delta.X) > 6 || Math.Abs(delta.Y) > 6) && File.Exists(item.FullPath))
                {
                    var data = new DataObject(DataFormats.FileDrop, new[] { item.FullPath });
                    DragDrop.DoDragDrop(card, data, DragDropEffects.Copy);
                }
            };
            card.MouseLeftButtonUp += (_, _) => DownloadsService.RevealInExplorer(item.FullPath);

            return card;
        }

        private static void ApplySmoothBarWidth(Border fillBar, double trackWidth, double ratio, bool animate)
        {
            if (trackWidth <= 0) return;

            double targetW = Math.Clamp(ratio, 0.0, 1.0) * trackWidth;
            if (!animate || !NotchDesign.EffectiveAnimationsEnabled)
            {
                fillBar.BeginAnimation(FrameworkElement.WidthProperty, null);
                fillBar.Width = targetW;
                return;
            }

            // Ohne fromValue animieren, damit vom aktuell animierten Wert weitergefahren wird (kein Rücksprung auf 0)
            var anim = new DoubleAnimation(targetW, TimeSpan.FromMilliseconds(NotchDesign.CollapseDurationMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fillBar.BeginAnimation(FrameworkElement.WidthProperty, anim);
        }

        private static void StartIndeterminateAnimation(TranslateTransform tx, double trackWidth, double pillWidth, double seconds)
        {
            double endX = Math.Max(40, trackWidth);
            if (!NotchDesign.EffectiveAnimationsEnabled)
            {
                tx.BeginAnimation(TranslateTransform.XProperty, null);
                tx.X = Math.Max(0, (endX - pillWidth) * 0.5);
                return;
            }

            // Ein sauberer Schwung von links nach rechts durch den Track (ohne Zurückspringen)
            var anim = new DoubleAnimation(-pillWidth, endX, TimeSpan.FromSeconds(seconds))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            tx.BeginAnimation(TranslateTransform.XProperty, anim);
        }
    }
}
