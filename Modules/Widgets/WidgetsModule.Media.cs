using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.Widgets
{
    public partial class WidgetsModule
    {
        // ---------- Kachel 2: Live-Medien-Player (Spotify-Layout + Cover-Mikro-Gradient) ----------

        private Border BuildMediaPlayerTile()
        {
            _mpCard = new Border
            {
                Background = NotchDesign.CardBackgroundBrush,
                BorderBrush = NotchDesign.CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(13, 9, 13, 8)
            };

            var dock = new DockPanel();

            // Oben: Album-Cover + Titel/Interpret + Live-Audio-Balken
            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _mpCoverBorder = new Border
            {
                Width = 48,
                Height = 48,
                CornerRadius = new CornerRadius(11),
                Background = NotchDesign.NestedCardBackgroundBrush,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(_mpCoverBorder, BitmapScalingMode.HighQuality);
            _mpCoverFallbackIcon = NotchDesign.CreateVectorIcon(NotchIconKind.Music, 18, NotchDesign.TextSecondaryBrush);
            _mpCoverBorder.Child = _mpCoverFallbackIcon;
            Grid.SetColumn(_mpCoverBorder, 0);
            topGrid.Children.Add(_mpCoverBorder);

            var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _mpTitleText = new TextBlock
            {
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _mpArtistText = new TextBlock
            {
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            infoStack.Children.Add(_mpTitleText);
            infoStack.Children.Add(_mpArtistText);
            Grid.SetColumn(infoStack, 1);
            topGrid.Children.Add(infoStack);

            _mpBarsHost = new Border
            {
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6, 2, 0, 0)
            };
            Grid.SetColumn(_mpBarsHost, 2);
            topGrid.Children.Add(_mpBarsHost);

            DockPanel.SetDock(topGrid, Dock.Top);
            dock.Children.Add(topGrid);

            // Unten (Spotify-Layout): Fortschrittsbalken + Zeitangaben
            var progressPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };

            _mpProgressGrid = new Grid
            {
                Tag = "InteractiveScrubber",
                Height = 10,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            _mpProgressGrid.Children.Add(new Border
            {
                Height = 3.5,
                CornerRadius = new CornerRadius(1.75),
                Background = NotchDesign.TrackBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            _mpProgressFill = new Border
            {
                Height = 3.5,
                Width = 0,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(1.75),
                Background = NotchDesign.CreateAccentGradientBrush(horizontal: true),
                VerticalAlignment = VerticalAlignment.Center
            };
            _mpProgressThumb = new Border
            {
                Width = 9,
                Height = 9,
                CornerRadius = new CornerRadius(4.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = NotchDesign.CreateAccentGradientBrush(horizontal: true),
                VerticalAlignment = VerticalAlignment.Center
            };
            _mpProgressGrid.Children.Add(_mpProgressFill);
            _mpProgressGrid.Children.Add(_mpProgressThumb);
            _mpProgressGrid.SizeChanged += (_, _) => UpdateMediaProgressVisual();
            AttachMediaScrubberEvents(_mpProgressGrid);
            progressPanel.Children.Add(_mpProgressGrid);

            var timeRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 1, 0, 0) };
            _mpElapsedText = new TextBlock
            {
                Text = "00:00",
                Foreground = NotchDesign.TextMutedBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 9.5
            };
            Typography.SetNumeralAlignment(_mpElapsedText, FontNumeralAlignment.Tabular);
            DockPanel.SetDock(_mpElapsedText, Dock.Left);
            timeRow.Children.Add(_mpElapsedText);

            _mpDurationText = new TextBlock
            {
                Text = "00:00",
                Foreground = NotchDesign.TextMutedBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 9.5
            };
            Typography.SetNumeralAlignment(_mpDurationText, FontNumeralAlignment.Tabular);
            DockPanel.SetDock(_mpDurationText, Dock.Right);
            timeRow.Children.Add(_mpDurationText);

            progressPanel.Children.Add(timeRow);
            DockPanel.SetDock(progressPanel, Dock.Bottom);
            dock.Children.Add(progressPanel);

            // Mitte (über dem Fortschrittsbalken): Kompaktere Spotify-Transport-Tasten passend zum größeren Cover
            var controlsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 1)
            };

            var prevBtn = CreateTileMediaButton(NotchIconKind.Previous, 23, 23, 10, () =>
            {
                NotchDesign.AnimatePressPulse(_mpCoverBorder);
                MediaPlayerService.Instance.SkipPrevious();
            }, isPrimary: false);
            _mpPlayBtn = CreateTileMediaButton(NotchIconKind.Play, 26, 26, 10.5, () =>
            {
                NotchDesign.AnimatePressPulse(_mpCoverBorder);
                MediaPlayerService.Instance.TogglePlayPause();
            }, isPrimary: true);
            var nextBtn = CreateTileMediaButton(NotchIconKind.Next, 23, 23, 10, () =>
            {
                NotchDesign.AnimatePressPulse(_mpCoverBorder);
                MediaPlayerService.Instance.SkipNext();
            }, isPrimary: false);
            nextBtn.Margin = new Thickness(0);

            controlsRow.Children.Add(prevBtn);
            controlsRow.Children.Add(_mpPlayBtn);
            controlsRow.Children.Add(nextBtn);
            dock.Children.Add(controlsRow);

            _mpCard.Child = dock;
            UpdateMediaTile();
            return _mpCard;
        }

        private void AttachMediaScrubberEvents(Grid progressGrid)
        {
            progressGrid.MouseLeftButtonDown += (_, e) =>
            {
                if (_jiggleController?.IsJiggleMode == true) return;
                var mp = MediaPlayerService.Instance;
                if (mp.Duration <= TimeSpan.Zero || progressGrid.ActualWidth <= 0) return;

                _mpIsScrubbing = true;
                _mpScrubRatio = Math.Clamp(e.GetPosition(progressGrid).X / progressGrid.ActualWidth, 0, 1);
                progressGrid.CaptureMouse();
                UpdateMediaProgressVisual();
                e.Handled = true;
            };

            progressGrid.MouseMove += (_, e) =>
            {
                if (!_mpIsScrubbing) return;
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    FinishMediaScrub(progressGrid, e.GetPosition(progressGrid).X, commitSeek: true);
                    return;
                }

                double w = Math.Max(1.0, progressGrid.ActualWidth);
                _mpScrubRatio = Math.Clamp(e.GetPosition(progressGrid).X / w, 0, 1);
                UpdateMediaProgressVisual();

                var mp = MediaPlayerService.Instance;
                if (mp.Duration > TimeSpan.Zero && (DateTime.UtcNow - _mpLastLiveSeekUtc).TotalMilliseconds >= 120)
                {
                    _mpLastLiveSeekUtc = DateTime.UtcNow;
                    mp.SeekTo(TimeSpan.FromSeconds(mp.Duration.TotalSeconds * _mpScrubRatio));
                }
            };

            progressGrid.MouseLeftButtonUp += (_, e) =>
            {
                if (!_mpIsScrubbing) return;
                FinishMediaScrub(progressGrid, e.GetPosition(progressGrid).X, commitSeek: true);
                e.Handled = true;
            };

            progressGrid.LostMouseCapture += (_, _) =>
            {
                if (!_mpIsScrubbing) return;
                _mpIsScrubbing = false;
                UpdateMediaProgressVisual();
            };
        }

        private void FinishMediaScrub(Grid progressGrid, double pointerX, bool commitSeek)
        {
            _mpIsScrubbing = false;
            if (progressGrid.IsMouseCaptured)
                progressGrid.ReleaseMouseCapture();

            if (commitSeek)
            {
                var mp = MediaPlayerService.Instance;
                double w = Math.Max(1.0, progressGrid.ActualWidth);
                _mpScrubRatio = Math.Clamp(pointerX / w, 0, 1);
                if (mp.Duration > TimeSpan.Zero)
                {
                    NotchDesign.AnimatePressPulse(_mpCoverBorder);
                    mp.SeekTo(TimeSpan.FromSeconds(mp.Duration.TotalSeconds * _mpScrubRatio));
                }
            }

            UpdateMediaProgressVisual();
        }

        private Button CreateTileMediaButton(NotchIconKind kind, double width, double height, double iconSize, Action onClick, bool isPrimary)
        {
            var primaryBg = NotchDesign.IsLight ? NotchDesign.AccentPrimaryBrush : Brushes.White;
            var primaryHover = NotchDesign.IsLight
                ? NotchDesign.AccentHoverBrush
                : new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE7));
            var primaryFg = NotchDesign.IsLight ? Brushes.White : Brushes.Black;

            var secondaryHover = NotchDesign.IsLight
                ? NotchDesign.ControlHoverBrush
                : new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));

            var icon = NotchDesign.CreateVectorIcon(
                kind,
                iconSize,
                isPrimary ? primaryFg : NotchDesign.TextPrimaryBrush,
                isPrimary ? 2.3 : 1.95);

            var btn = NotchDesign.CreateRoundedButton(
                icon,
                () =>
                {
                    if (_jiggleController?.IsJiggleMode == true) return;
                    onClick();
                },
                background: isPrimary ? primaryBg : Brushes.Transparent,
                hoverBackground: isPrimary ? primaryHover : secondaryHover,
                foreground: isPrimary ? primaryFg : NotchDesign.TextPrimaryBrush,
                cornerRadius: height / 2.0,
                padding: new Thickness(0));
            btn.Width = width;
            btn.Height = height;
            btn.Margin = new Thickness(0, 0, 10, 0);
            return btn;
        }

        private void UpdateMediaTile()
        {
            if (_mpTitleText == null || _mpArtistText == null || _mpCoverBorder == null ||
                _mpPlayBtn == null || _mpBarsHost == null)
                return;

            var mp = MediaPlayerService.Instance;
            _mpTitleText.Text = mp.TrackTitle;
            _mpArtistText.Text = mp.Artist;

            if (!ReferenceEquals(_mpLastCover, mp.Cover))
            {
                _mpLastCover = mp.Cover;
                _mpCoverAccent = NotchDesign.ExtractDominantColor(mp.Cover);
                ApplyMediaCoverTheme();

                if (mp.Cover != null)
                {
                    _mpCoverBorder.Background = new ImageBrush(mp.Cover) { Stretch = Stretch.UniformToFill };
                    if (_mpCoverFallbackIcon != null) _mpCoverFallbackIcon.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _mpCoverBorder.Background = NotchDesign.NestedCardBackgroundBrush;
                    if (_mpCoverFallbackIcon != null) _mpCoverFallbackIcon.Visibility = Visibility.Visible;
                }
            }

            if (_mpLastPlayingVisual != mp.IsPlaying)
            {
                _mpLastPlayingVisual = mp.IsPlaying;
                var primaryFg = NotchDesign.IsLight ? Brushes.White : Brushes.Black;
                _mpPlayBtn.Content = NotchDesign.CreateVectorIcon(
                    mp.IsPlaying ? NotchIconKind.Pause : NotchIconKind.Play,
                    11.5,
                    primaryFg,
                    2.3);

                UpdateMediaAudioBars(mp.IsPlaying);
            }

            UpdateMediaProgressVisual();
        }

        private void ApplyMediaCoverTheme()
        {
            Color barColor = GetReadableCoverBarColor(_mpCoverAccent);

            if (_mpCard != null)
            {
                if (_mpCoverAccent is Color cover)
                {
                    if (NotchDesign.IsLight)
                    {
                        var start = NotchDesign.BlendColor(Colors.White, cover, 0.11);
                        var end = NotchDesign.BlendColor(Color.FromRgb(0xF5, 0xF5, 0xF7), cover, 0.04);
                        if (NotchDesign.IsGlass)
                        {
                            start = Color.FromArgb(0xCC, start.R, start.G, start.B);
                            end = Color.FromArgb(0xC2, end.R, end.G, end.B);
                        }
                        _mpCard.Background = new LinearGradientBrush(start, end, new Point(0, 0), new Point(1, 1));
                    }
                    else
                    {
                        var start = NotchDesign.BlendColor(Color.FromRgb(0x13, 0x13, 0x18), cover, 0.24);
                        var mid = NotchDesign.BlendColor(Color.FromRgb(0x11, 0x11, 0x15), cover, 0.14);
                        var end = NotchDesign.BlendColor(Color.FromRgb(0x0D, 0x0D, 0x10), cover, 0.07);
                        if (NotchDesign.IsGlass)
                        {
                            start = Color.FromArgb(0xC8, start.R, start.G, start.B);
                            mid = Color.FromArgb(0xC2, mid.R, mid.G, mid.B);
                            end = Color.FromArgb(0xBC, end.R, end.G, end.B);
                        }
                        _mpCard.Background = new LinearGradientBrush(
                            new GradientStopCollection
                            {
                                new(start, 0.0),
                                new(mid, 0.55),
                                new(end, 1.0)
                            },
                            new Point(0, 0),
                            new Point(1, 1));
                    }

                    _mpCard.BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, barColor.R, barColor.G, barColor.B));
                }
                else
                {
                    _mpCard.Background = NotchDesign.CardBackgroundBrush;
                    _mpCard.BorderBrush = NotchDesign.CardBorderBrush;
                }
            }

            if (_mpProgressFill != null && _mpProgressThumb != null)
            {
                if (_mpCoverAccent.HasValue)
                {
                    var barStart = NotchDesign.BlendColor(barColor, Colors.Black, 0.14);
                    var barEnd = NotchDesign.BlendColor(barColor, Colors.White, 0.24);
                    var barBrush = new LinearGradientBrush(barStart, barEnd, new Point(0, 0), new Point(1, 0));
                    _mpProgressFill.Background = barBrush;
                    _mpProgressThumb.Background = new SolidColorBrush(barEnd);
                }
                else
                {
                    _mpProgressFill.Background = NotchDesign.CreateAccentGradientBrush(horizontal: true);
                    _mpProgressThumb.Background = NotchDesign.CreateAccentGradientBrush(horizontal: true);
                }
            }

            UpdateMediaAudioBars(MediaPlayerService.Instance.IsPlaying);
        }

        private void UpdateMediaAudioBars(bool isPlaying)
        {
            if (_mpBarsHost == null) return;
            Color accent = _mpCoverAccent.HasValue ? GetReadableCoverBarColor(_mpCoverAccent) : NotchDesign.AccentPrimary;
            _mpBarsHost.Child = isPlaying
                ? NotchDesign.CreateAudioBars(accent, 11)
                : new Border
                {
                    Width = 6,
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = NotchDesign.TrackBrush
                };
        }

        private static Color GetReadableCoverBarColor(Color? coverColor)
        {
            if (coverColor is not Color c)
                return NotchDesign.AccentPrimary;

            double luma = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            if (NotchDesign.IsLight)
                return luma > 0.65 ? NotchDesign.BlendColor(c, Colors.Black, 0.32) : c;

            return luma < 0.36 ? NotchDesign.BlendColor(c, Colors.White, 0.38) : c;
        }

        private void UpdateMediaProgressVisual()
        {
            if (_mpProgressGrid == null || _mpProgressFill == null || _mpProgressThumb == null ||
                _mpElapsedText == null || _mpDurationText == null)
                return;

            var mp = MediaPlayerService.Instance;
            var dur = mp.Duration;

            double ratio = _mpIsScrubbing
                ? _mpScrubRatio
                : (dur > TimeSpan.Zero ? Math.Clamp(mp.GetInterpolatedPosition().TotalSeconds / dur.TotalSeconds, 0, 1) : 0);

            var pos = _mpIsScrubbing && dur > TimeSpan.Zero
                ? TimeSpan.FromSeconds(dur.TotalSeconds * ratio)
                : mp.GetInterpolatedPosition();

            double trackW = _mpProgressGrid.ActualWidth > 0 ? _mpProgressGrid.ActualWidth : 210;
            double fillW = trackW * ratio;
            _mpProgressFill.Width = fillW;
            _mpProgressThumb.Margin = new Thickness(Math.Clamp(fillW - 4.5, 0, Math.Max(0, trackW - 9)), 0, 0, 0);

            _mpElapsedText.Text = dur > TimeSpan.Zero ? FormatDuration(pos) : "--:--";
            _mpDurationText.Text = dur > TimeSpan.Zero ? FormatDuration(dur) : "--:--";
        }
    }
}
