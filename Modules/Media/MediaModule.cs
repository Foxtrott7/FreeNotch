using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.Media
{
    /// <summary>
    /// Medien-Modul mit WinRT GSMTC-Anbindung (Live-Anzeige, Vorschau und Player-Steuerung).
    /// </summary>
    public class MediaModule : NotchModuleBase
    {
        public override string Id => "media";
        public override string Title => L.T("module.media");
        public override NotchIconKind IconKind => NotchIconKind.Music;
        public override bool ShowInTabBar => false;
        public override int Priority => MediaPlayerService.Instance.IsPlaying ? 100 : 20;
        public override double? PreferredPreviewWidth => 270;
        public override double? PreferredLiveWidth =>
            MediaPlayerService.Instance.IsPlaying ? 228 : null;
        public override (double Width, double Height)? PreferredExpandedSize => (470, 205);

        // Aktuell angezeigte Elemente der geöffneten Ansicht (für Live-Updates)
        private TextBlock? _expTitle, _expArtist;
        private Button? _expPlayBtn;
        private FrameworkElement? _expCoverIcon;
        private Border? _expCover;
        private Grid? _expProgressGrid;
        private Border? _expProgressFill, _expProgressThumb;
        private DispatcherTimer? _progressTimer;
        private bool _isScrubbing;
        private double _scrubRatio;
        private DateTime _lastLiveSeekUtc;

        public override void Start()
        {
            var mp = MediaPlayerService.Instance;
            mp.StateChanged -= OnMediaStateChanged;
            mp.StateChanged += OnMediaStateChanged;
            mp.EnsureStarted();
        }

        public override void Stop()
        {
            MediaPlayerService.Instance.StateChanged -= OnMediaStateChanged;
            StopProgressTimer();
        }

        private void OnMediaStateChanged()
        {
            var disp = Application.Current?.Dispatcher;
            if (disp == null) return;

            disp.BeginInvoke(() =>
            {
                UpdateExpanded();
                RaiseStateChanged();
            });
        }

        private void UpdateExpanded()
        {
            var mp = MediaPlayerService.Instance;
            if (_expTitle != null) _expTitle.Text = mp.TrackTitle;
            if (_expArtist != null) _expArtist.Text = mp.Artist;
            if (_expPlayBtn != null)
            {
                _expPlayBtn.Content = NotchDesign.CreateVectorIcon(
                    mp.IsPlaying ? NotchIconKind.Pause : NotchIconKind.Play,
                    14,
                    NotchDesign.IsLight ? Brushes.White : Brushes.Black,
                    2.4);
            }
            if (_expCover != null)
            {
                if (mp.Cover != null)
                {
                    _expCover.Background = new ImageBrush(mp.Cover) { Stretch = Stretch.UniformToFill };
                    if (_expCoverIcon != null) _expCoverIcon.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _expCover.Background = NotchDesign.CardBackgroundBrush;
                    if (_expCoverIcon != null) _expCoverIcon.Visibility = Visibility.Visible;
                }
            }

            UpdateProgressVisual();
        }

        private void UpdateProgressVisual()
        {
            if (_expProgressGrid == null || _expProgressFill == null || _expProgressThumb == null)
                return;

            var mp = MediaPlayerService.Instance;
            double trackW = _expProgressGrid.ActualWidth > 0 ? _expProgressGrid.ActualWidth : 280;
            double ratio = _isScrubbing
                ? _scrubRatio
                : (mp.Duration > TimeSpan.Zero
                    ? Math.Clamp(mp.GetInterpolatedPosition().TotalSeconds / mp.Duration.TotalSeconds, 0, 1)
                    : 0);

            double fillW = trackW * ratio;
            _expProgressFill.Width = fillW;
            _expProgressThumb.Margin = new Thickness(Math.Clamp(fillW - 5, 0, Math.Max(0, trackW - 10)), 0, 0, 0);
        }

        private void StartProgressTimer()
        {
            if (_progressTimer != null) return;
            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _progressTimer.Tick += (_, _) => UpdateProgressVisual();
            _progressTimer.Start();
        }

        private void StopProgressTimer()
        {
            _progressTimer?.Stop();
            _progressTimer = null;
        }

        public override FrameworkElement? CreateLiveView()
        {
            var mp = MediaPlayerService.Instance;
            if (!mp.IsPlaying) return null;

            var grid = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var coverBorder = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(6),
                Background = mp.Cover != null
                    ? new ImageBrush(mp.Cover) { Stretch = Stretch.UniformToFill }
                    : NotchDesign.ControlBackgroundBrush,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (mp.Cover == null)
            {
                coverBorder.Child = NotchDesign.CreateVectorIcon(NotchIconKind.Music, 11, NotchDesign.TextSecondaryBrush);
            }
            Grid.SetColumn(coverBorder, 0);
            grid.Children.Add(coverBorder);

            var titleBlock = new TextBlock
            {
                Text = mp.HasKnownTrack ? mp.TrackTitle : L.T("media.none"),
                Foreground = mp.HasKnownTrack ? NotchDesign.TextPrimaryBrush : NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 160
            };
            Grid.SetColumn(titleBlock, 1);
            grid.Children.Add(titleBlock);

            UIElement rightIndicator = mp.IsPlaying
                ? NotchDesign.CreateAudioBars(NotchDesign.AccentPrimary, 12)
                : NotchDesign.CreateVectorIcon(NotchIconKind.Play, 10, NotchDesign.TextSecondaryBrush, 2.0);
            if (rightIndicator is FrameworkElement fe)
            {
                fe.Margin = new Thickness(8, 0, 0, 0);
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            Grid.SetColumn(rightIndicator, 2);
            grid.Children.Add(rightIndicator);

            return grid;
        }

        public override FrameworkElement CreatePreviewView()
        {
            var mp = MediaPlayerService.Instance;
            var grid = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconBorder = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(7),
                Background = NotchDesign.ControlBackgroundBrush,
                Margin = new Thickness(0, 0, 8, 0)
            };
            if (mp.Cover != null)
            {
                iconBorder.Background = new ImageBrush(mp.Cover) { Stretch = Stretch.UniformToFill };
            }
            else
            {
                iconBorder.Child = NotchDesign.CreateVectorIcon(NotchIconKind.Music, 13, NotchDesign.TextSecondaryBrush);
            }
            Grid.SetColumn(iconBorder, 0);
            grid.Children.Add(iconBorder);

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var titleBlock = new TextBlock
            {
                Text = mp.TrackTitle,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 170
            };
            var artistBlock = new TextBlock
            {
                Text = mp.Artist,
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 170
            };
            textStack.Children.Add(titleBlock);
            textStack.Children.Add(artistBlock);
            Grid.SetColumn(textStack, 1);
            grid.Children.Add(textStack);

            UIElement indicator = mp.IsPlaying
                ? NotchDesign.CreateAudioBars(NotchDesign.AccentPrimary, 12)
                : new Border
                {
                    Width = 6,
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = NotchDesign.TrackBrush
                };
            if (indicator is FrameworkElement indFe)
            {
                indFe.Margin = new Thickness(8, 0, 0, 0);
                indFe.VerticalAlignment = VerticalAlignment.Center;
            }
            Grid.SetColumn(indicator, 2);
            grid.Children.Add(indicator);

            return grid;
        }

        public override FrameworkElement CreateExpandedView()
        {
            var mp = MediaPlayerService.Instance;
            var outer = new Border
            {
                Background = NotchDesign.CreateSoftAccentCardBrush(),
                BorderBrush = NotchDesign.CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(16, 14, 16, 14)
            };

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var coverBorder = new Border
            {
                Width = 86,
                Height = 86,
                CornerRadius = new CornerRadius(14),
                Background = NotchDesign.NestedCardBackgroundBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            var coverIcon = NotchDesign.CreateVectorIcon(NotchIconKind.Music, 28, NotchDesign.TextSecondaryBrush);
            coverBorder.Child = coverIcon;
            _expCover = coverBorder;
            _expCoverIcon = coverIcon;
            Grid.SetColumn(coverBorder, 0);
            root.Children.Add(coverBorder);

            var contentPanel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };

            var titleText = new TextBlock
            {
                Text = mp.TrackTitle,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var artistText = new TextBlock
            {
                Text = mp.Artist,
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _expTitle = titleText;
            _expArtist = artistText;
            contentPanel.Children.Add(titleText);
            contentPanel.Children.Add(artistText);

            var progressGrid = new Grid
            {
                Margin = new Thickness(0, 11, 0, 10),
                Height = 12,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            var progressBg = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = NotchDesign.TrackBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            var progressFill = new Border
            {
                Height = 4,
                Width = 0,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(2),
                Background = NotchDesign.CreateAccentGradientBrush(horizontal: true),
                VerticalAlignment = VerticalAlignment.Center
            };
            var thumb = new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(NotchDesign.AccentGradientEnd),
                VerticalAlignment = VerticalAlignment.Center
            };
            progressGrid.Children.Add(progressBg);
            progressGrid.Children.Add(progressFill);
            progressGrid.Children.Add(thumb);
            progressGrid.SizeChanged += (_, _) => UpdateProgressVisual();
            progressGrid.MouseLeftButtonDown += (_, e) =>
            {
                if (mp.Duration <= TimeSpan.Zero || progressGrid.ActualWidth <= 0) return;
                _isScrubbing = true;
                _scrubRatio = Math.Clamp(e.GetPosition(progressGrid).X / progressGrid.ActualWidth, 0, 1);
                progressGrid.CaptureMouse();
                UpdateProgressVisual();
                e.Handled = true;
            };
            progressGrid.MouseMove += (_, e) =>
            {
                if (!_isScrubbing) return;
                double w = Math.Max(1.0, progressGrid.ActualWidth);
                _scrubRatio = Math.Clamp(e.GetPosition(progressGrid).X / w, 0, 1);
                UpdateProgressVisual();

                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    _isScrubbing = false;
                    if (progressGrid.IsMouseCaptured) progressGrid.ReleaseMouseCapture();
                    if (mp.Duration > TimeSpan.Zero)
                    {
                        NotchDesign.AnimatePressPulse(_expCover);
                        mp.SeekTo(TimeSpan.FromSeconds(mp.Duration.TotalSeconds * _scrubRatio));
                    }
                    return;
                }

                if (mp.Duration > TimeSpan.Zero && (DateTime.UtcNow - _lastLiveSeekUtc).TotalMilliseconds >= 120)
                {
                    _lastLiveSeekUtc = DateTime.UtcNow;
                    mp.SeekTo(TimeSpan.FromSeconds(mp.Duration.TotalSeconds * _scrubRatio));
                }
            };
            progressGrid.MouseLeftButtonUp += (_, e) =>
            {
                if (!_isScrubbing) return;
                _isScrubbing = false;
                if (progressGrid.IsMouseCaptured) progressGrid.ReleaseMouseCapture();
                if (mp.Duration > TimeSpan.Zero && progressGrid.ActualWidth > 0)
                {
                    _scrubRatio = Math.Clamp(e.GetPosition(progressGrid).X / progressGrid.ActualWidth, 0, 1);
                    NotchDesign.AnimatePressPulse(_expCover);
                    mp.SeekTo(TimeSpan.FromSeconds(mp.Duration.TotalSeconds * _scrubRatio));
                }
                UpdateProgressVisual();
                e.Handled = true;
            };
            progressGrid.LostMouseCapture += (_, _) =>
            {
                if (!_isScrubbing) return;
                _isScrubbing = false;
                UpdateProgressVisual();
            };
            _expProgressGrid = progressGrid;
            _expProgressFill = progressFill;
            _expProgressThumb = thumb;
            contentPanel.Children.Add(progressGrid);

            var controlsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var prevBtn = CreateMediaButton(NotchIconKind.Previous, 34, 32, 13, () =>
            {
                NotchDesign.AnimatePressPulse(_expCover);
                mp.SkipPrevious();
            });
            var playBtn = CreateMediaButton(mp.IsPlaying ? NotchIconKind.Pause : NotchIconKind.Play, 44, 32, 14, () =>
            {
                NotchDesign.AnimatePressPulse(_expCover);
                mp.TogglePlayPause();
            }, isPrimary: true);
            var nextBtn = CreateMediaButton(NotchIconKind.Next, 34, 32, 13, () =>
            {
                NotchDesign.AnimatePressPulse(_expCover);
                mp.SkipNext();
            });

            _expPlayBtn = playBtn;

            controlsRow.Children.Add(prevBtn);
            controlsRow.Children.Add(playBtn);
            controlsRow.Children.Add(nextBtn);

            UpdateExpanded();

            contentPanel.Children.Add(controlsRow);

            Grid.SetColumn(contentPanel, 1);
            root.Children.Add(contentPanel);

            outer.Child = root;
            outer.Loaded += (_, _) => StartProgressTimer();
            outer.Unloaded += (_, _) => StopProgressTimer();
            return outer;
        }

        private static Button CreateMediaButton(NotchIconKind kind, double width, double height, double iconSize, Action onClick, bool isPrimary = false)
        {
            var primaryBg = NotchDesign.IsLight ? NotchDesign.AccentPrimaryBrush : Brushes.White;
            var primaryHover = NotchDesign.IsLight
                ? NotchDesign.AccentHoverBrush
                : new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE7));
            var primaryFg = NotchDesign.IsLight ? Brushes.White : Brushes.Black;

            var icon = NotchDesign.CreateVectorIcon(
                kind,
                iconSize,
                isPrimary ? primaryFg : NotchDesign.TextPrimaryBrush,
                isPrimary ? 2.4 : 1.9);

            var bg = isPrimary ? primaryBg : Brushes.Transparent;
            var hover = isPrimary ? primaryHover : NotchDesign.ControlHoverBrush;

            var btn = NotchDesign.CreateRoundedButton(
                icon,
                onClick,
                background: bg,
                hoverBackground: hover,
                foreground: isPrimary ? primaryFg : NotchDesign.TextPrimaryBrush,
                cornerRadius: 16,
                padding: new Thickness(0));
            btn.Width = width;
            btn.Height = height;
            btn.Margin = new Thickness(0, 0, 6, 0);
            return btn;
        }
    }
}
