using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowsNotch.Core;
using WindowsNotch.Services;
using WindowsNotch.Services.Database;

namespace WindowsNotch.Modules.Widgets
{
    public partial class WidgetsModule
    {
        private sealed class LyricGlyphToken
        {
            public readonly TextBlock Label;
            public readonly ScaleTransform Scale;
            public readonly int CharIndex;
            public readonly int WordStart;
            public readonly int WordEnd;
            public double LastOpacity;
            public double LastScale;

            public LyricGlyphToken(TextBlock label, ScaleTransform scale, int charIndex, int wordStart, int wordEnd, double initialOpacity)
            {
                Label = label;
                Scale = scale;
                CharIndex = charIndex;
                WordStart = wordStart;
                WordEnd = wordEnd;
                LastOpacity = initialOpacity;
                LastScale = 1.0;
            }
        }

        private const double LyricsInactiveOpacity = 0.22;
        private RowDefinition? _lyricsRowTop, _lyricsRowBottom;
        private TranslateTransform? _lyricsScrollTransform;
        private int _lyricsLastActiveIdx = int.MinValue;
        private string _lyricsLastTrackKey = string.Empty;
        private double _lyricsLastProgress = -1.0;
        private bool _lyricsForceLayoutSync = true;
        private Point? _lyricsLastTileOffset;
        private bool _isUnpinAnimating;
        private Button? _lyricsOptInBtn;

        private static readonly Brush LyricsDimLineBrush = CreateFrozenBrush(Color.FromArgb(0x42, 0xFF, 0xFF, 0xFF));
        private static readonly Brush LyricsFarLineBrush = CreateFrozenBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));

        private static Brush CreateFrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static LinearGradientBrush CreateLyricsBodyMask(bool pinned)
        {
            // Nur am alleräußersten Rand sanft abfangen, damit inaktive Zeilen nicht doppelt ausgefadet/fleckig wirken
            var mask = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Colors.Transparent, 0.0),
                    new(Colors.White, pinned ? 0.05 : 0.06),
                    new(Colors.White, pinned ? 0.95 : 0.94),
                    new(Colors.Transparent, 1.0)
                },
                new Point(0, 0),
                new Point(0, 1));
            mask.Freeze();
            return mask;
        }

        // ---------- Kachel 1: Live-Lyrics (Album-Blur + Spotify-Buchstaben-Welle) ----------

        private Border BuildLyricsTile()
        {
            var card = new Border
            {
                Background = NotchDesign.CreateAccentGradientBrush(),
                CornerRadius = new CornerRadius(16)
            };
            _lyricsTileCard = card;

            card.MouseLeftButtonUp += (_, e) =>
            {
                if (_jiggleController?.IsJiggleMode == true) return;
                if (!SettingsService.Current.OptInLyrics)
                {
                    e.Handled = true;
                    ((App)Application.Current).Manager?.ShowSettings();
                }
            };

            var layers = new Grid { ClipToBounds = true };

            // Ebene 1: Vorgerendertes unscharfes Album-Cover als atmosphärischer Spotify-Hintergrund
            // (ohne Live-BlurEffect/BitmapCache im Visual-Tree, damit Drag/Swap/Clip im Dashboard sauber bleibt)
            _lyricsBgCoverBorder = new Border
            {
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            RenderOptions.SetBitmapScalingMode(_lyricsBgCoverBorder, BitmapScalingMode.HighQuality);
            layers.Children.Add(_lyricsBgCoverBorder);

            // Ebene 2: Subtiles Kontrast-Scrim (bei Cover leicht & lebendig, ohne Cover voller Akzent-Verlauf)
            _lyricsTintOverlay = new Border
            {
                Background = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new(Color.FromArgb(0x2A, 0x00, 0x00, 0x00), 0.0),
                        new(Color.FromArgb(0x14, 0x00, 0x00, 0x00), 0.5),
                        new(Color.FromArgb(0x56, 0x00, 0x00, 0x00), 1.0)
                    },
                    new Point(0, 0),
                    new Point(0.2, 1)),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            layers.Children.Add(_lyricsTintOverlay);

            var dock = new DockPanel
            {
                Margin = new Thickness(14, 9, 14, 0),
                Background = Brushes.Transparent
            };
            _lyricsDock = dock;

            // Kopfzeile: Grid (1* / Auto), damit der Pin-Kreis rechts nie durch langen Text oder Hover-Scale abgeschnitten wird
            var header = new Grid { VerticalAlignment = VerticalAlignment.Center };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftHeader = new Grid { VerticalAlignment = VerticalAlignment.Center };
            leftHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            leftHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _lyricsHeaderCover = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0x32, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(_lyricsHeaderCover, BitmapScalingMode.HighQuality);
            _lyricsHeaderCoverFallback = NotchDesign.CreateVectorIcon(NotchIconKind.Waveform, 14, Brushes.White, 1.9);
            _lyricsHeaderCover.Child = _lyricsHeaderCoverFallback;
            Grid.SetColumn(_lyricsHeaderCover, 0);
            leftHeader.Children.Add(_lyricsHeaderCover);

            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _lyricsTitleStack = titleStack;
            _lyricsHeaderTitle = new TextBlock
            {
                Text = "Live-Lyrics",
                Foreground = Brushes.White,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _lyricsHeaderArtist = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromArgb(0xDC, 0xFF, 0xFF, 0xFF)),
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titleStack.Children.Add(_lyricsHeaderTitle);
            titleStack.Children.Add(_lyricsHeaderArtist);
            Grid.SetColumn(titleStack, 1);
            leftHeader.Children.Add(titleStack);

            Grid.SetColumn(leftHeader, 0);
            header.Children.Add(leftHeader);

            _lyricsPinBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Pin, 14, Brushes.White, 1.8),
                () => SetLyricsPinned(!_isLyricsPinned),
                background: Brushes.Transparent,
                hoverBackground: new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
                foreground: Brushes.White,
                cornerRadius: 18,
                padding: new Thickness(0),
                tooltip: L.T("widgets.lyrics.pin_tooltip"));
            _lyricsPinBtn.Width = 28;
            _lyricsPinBtn.Height = 28;
            _lyricsPinBtn.VerticalAlignment = VerticalAlignment.Center;
            _lyricsPinBtn.Margin = new Thickness(6, 0, 2, 0);
            _lyricsPinBtn.Opacity = _isLyricsPinned ? 1.0 : 0.0;
            _lyricsPinBtn.IsHitTestVisible = _isLyricsPinned;
            Grid.SetColumn(_lyricsPinBtn, 1);
            header.Children.Add(_lyricsPinBtn);

            card.MouseEnter += (_, _) => UpdateLyricsPinVisibility(isHovered: true);
            card.MouseLeave += (_, _) => UpdateLyricsPinVisibility(isHovered: false);

            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);

            // Textkörper: Spotify-artige Lyrics mit weichem Bottom-Fade & Wort-Welle
            var body = new Grid
            {
                Margin = new Thickness(0, 4, 0, 0),
                ClipToBounds = true,
                OpacityMask = CreateLyricsBodyMask(pinned: false)
            };
            _lyricsBodyGrid = body;

            _lyricsScrollTransform = new TranslateTransform(0, 0);
            _lyricsSyncedPanel = new Grid
            {
                VerticalAlignment = VerticalAlignment.Stretch,
                RenderTransform = _lyricsScrollTransform
            };
            _lyricsRowTop = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
            var rowMid = new RowDefinition { Height = GridLength.Auto };
            _lyricsRowBottom = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
            _lyricsSyncedPanel.RowDefinitions.Add(_lyricsRowTop);
            _lyricsSyncedPanel.RowDefinitions.Add(rowMid);
            _lyricsSyncedPanel.RowDefinitions.Add(_lyricsRowBottom);

            var topStack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            _lyricsPrev2Text = new TextBlock
            {
                Foreground = LyricsFarLineBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 21,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 28,
                LineHeight = 26.5,
                Margin = new Thickness(0, 0, 0, 5),
                Visibility = Visibility.Collapsed
            };
            _lyricsPrevText = new TextBlock
            {
                Foreground = LyricsDimLineBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 35,
                LineHeight = 17.5,
                Margin = new Thickness(0, 0, 0, 3)
            };
            topStack.Children.Add(_lyricsPrev2Text);
            topStack.Children.Add(_lyricsPrevText);
            Grid.SetRow(topStack, 0);
            _lyricsSyncedPanel.Children.Add(topStack);

            var midHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
            _lyricsCurrWrap = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                MaxHeight = 37
            };
            _lyricsCurrText = new TextBlock
            {
                Foreground = Brushes.White,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 36,
                LineHeight = 17.5,
                Visibility = Visibility.Collapsed
            };
            midHost.Children.Add(_lyricsCurrWrap);
            midHost.Children.Add(_lyricsCurrText);
            Grid.SetRow(midHost, 1);
            _lyricsSyncedPanel.Children.Add(midHost);

            var bottomStack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            _lyricsNextText = new TextBlock
            {
                Foreground = LyricsDimLineBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 35,
                LineHeight = 17.5,
                Margin = new Thickness(0, 3, 0, 0)
            };
            _lyricsNext2Text = new TextBlock
            {
                Foreground = LyricsFarLineBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 21,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 28,
                LineHeight = 26.5,
                Margin = new Thickness(0, 5, 0, 0),
                Visibility = Visibility.Collapsed
            };
            _lyricsNext3Text = new TextBlock
            {
                Foreground = LyricsFarLineBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 22.5,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 30,
                LineHeight = 28,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed
            };
            _lyricsNext4Text = new TextBlock
            {
                Foreground = LyricsFarLineBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 22.5,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 30,
                LineHeight = 28,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed
            };
            bottomStack.Children.Add(_lyricsNextText);
            bottomStack.Children.Add(_lyricsNext2Text);
            bottomStack.Children.Add(_lyricsNext3Text);
            bottomStack.Children.Add(_lyricsNext4Text);
            Grid.SetRow(bottomStack, 2);
            _lyricsSyncedPanel.Children.Add(bottomStack);

            _lyricsFallbackPanel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6),
                Visibility = Visibility.Collapsed
            };
            _lyricsFallbackText = new TextBlock
            {
                Foreground = Brushes.White,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 44
            };
            _lyricsFallbackSubText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)),
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            };
            _lyricsOptInBtn = NotchDesign.CreateRoundedButton(
                L.T("widgets.lyrics.enable"),
                () =>
                {
                    var cfg = SettingsService.Current;
                    cfg.OptInLyrics = true;
                    SettingsService.Update(cfg);
                    var mpNow = MediaPlayerService.Instance;
                    LyricsService.Instance.EnsureForTrack(mpNow.TrackTitle, mpNow.Artist, mpNow.AlbumTitle, mpNow.Duration, mpNow.HasKnownTrack);
                    UpdateLyricsTile();
                },
                background: new SolidColorBrush(Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF)),
                hoverBackground: new SolidColorBrush(Color.FromArgb(0x52, 0xFF, 0xFF, 0xFF)),
                foreground: Brushes.White,
                cornerRadius: NotchDesign.RadiusControl,
                padding: new Thickness(12, 5, 12, 5));
            _lyricsOptInBtn.HorizontalAlignment = HorizontalAlignment.Left;
            _lyricsOptInBtn.Margin = new Thickness(0, 8, 0, 0);
            _lyricsOptInBtn.Visibility = Visibility.Collapsed;

            _lyricsFallbackPanel.Children.Add(_lyricsFallbackText);
            _lyricsFallbackPanel.Children.Add(_lyricsFallbackSubText);
            _lyricsFallbackPanel.Children.Add(_lyricsOptInBtn);

            body.Children.Add(_lyricsSyncedPanel);
            body.Children.Add(_lyricsFallbackPanel);
            dock.Children.Add(body);

            card.SizeChanged += (_, e) =>
            {
                if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
                double radius = _isLyricsPinned ? 22 : 16;
                var clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), radius, radius);
                clip.Freeze();
                card.Clip = clip;
            };

            layers.Children.Add(dock);
            card.Child = layers;
            UpdateLyricsTile();
            return card;
        }

        private void SetLyricsPinned(bool pinned)
        {
            _jiggleController?.CancelPendingPress();
            if (_bentoGrid == null || _lyricsTileCard == null || _lyricsFullscreenHost == null)
            {
                _isLyricsPinned = pinned;
                return;
            }

            if (!pinned && _isLyricsPinned && NotchDesign.EffectiveAnimationsEnabled &&
                _lyricsFullscreenHost.Visibility == Visibility.Visible && !_isUnpinAnimating)
            {
                _isUnpinAnimating = true;
                AnimateLyricsUnpinOut(() =>
                {
                    _isUnpinAnimating = false;
                    CompleteSetLyricsPinned(false, null);
                });
                return;
            }

            Point? tileOffsetFromCenter = null;
            if (pinned && _expandedRootHost != null && _bentoGrid.Visibility == Visibility.Visible &&
                _lyricsTileCard.ActualWidth > 10 && _expandedRootHost.ActualWidth > 10)
            {
                try
                {
                    var topLeft = _lyricsTileCard.TransformToVisual(_expandedRootHost).Transform(new Point(0, 0));
                    double tileCenterX = topLeft.X + _lyricsTileCard.ActualWidth * 0.5;
                    double tileCenterY = topLeft.Y + _lyricsTileCard.ActualHeight * 0.5;
                    double hostCenterX = _expandedRootHost.ActualWidth * 0.5;
                    double hostCenterY = _expandedRootHost.ActualHeight * 0.5;
                    tileOffsetFromCenter = new Point(tileCenterX - hostCenterX, tileCenterY - hostCenterY);
                    _lyricsLastTileOffset = tileOffsetFromCenter;
                }
                catch { }
            }

            CompleteSetLyricsPinned(pinned, tileOffsetFromCenter);
        }

        private void CompleteSetLyricsPinned(bool pinned, Point? tileOffsetFromCenter)
        {
            _isLyricsPinned = pinned;
            if (_bentoGrid == null || _lyricsTileCard == null || _lyricsFullscreenHost == null)
                return;

            if (pinned)
            {
                if (_bentoGrid.Children.Contains(_lyricsTileCard))
                    _bentoGrid.Children.Remove(_lyricsTileCard);

                _bentoGrid.Visibility = Visibility.Collapsed;
                _lyricsFullscreenHost.Padding = new Thickness(22, 12, 22, 20);
                _lyricsFullscreenHost.Child = _lyricsTileCard;
                _lyricsFullscreenHost.Visibility = Visibility.Visible;
            }
            else
            {
                _lyricsFullscreenHost.BeginAnimation(UIElement.OpacityProperty, null);
                _lyricsFullscreenHost.Opacity = 1.0;
                _lyricsFullscreenHost.RenderTransform = Transform.Identity;
                _lyricsFullscreenHost.Child = null;
                _lyricsFullscreenHost.Padding = new Thickness(0);
                _lyricsFullscreenHost.Visibility = Visibility.Collapsed;

                int idx = SettingsService.Current.BentoTileOrder.IndexOf("lyrics");
                if (idx < 0) idx = 0;
                int row = idx < 2 ? 0 : 2;
                int col = (idx % 2 == 0) ? 0 : 2;
                Grid.SetRow(_lyricsTileCard, row);
                Grid.SetColumn(_lyricsTileCard, col);

                if (!_bentoGrid.Children.Contains(_lyricsTileCard))
                    _bentoGrid.Children.Add(_lyricsTileCard);

                _bentoGrid.Visibility = Visibility.Visible;
            }

            _lyricsTileCard.CornerRadius = new CornerRadius(pinned ? 22 : 16);
            if (_lyricsTileCard.ActualWidth > 0 && _lyricsTileCard.ActualHeight > 0)
            {
                double radius = pinned ? 22 : 16;
                var clip = new RectangleGeometry(new Rect(0, 0, _lyricsTileCard.ActualWidth, _lyricsTileCard.ActualHeight), radius, radius);
                clip.Freeze();
                _lyricsTileCard.Clip = clip;
            }

            if (_lyricsRowTop != null)
                _lyricsRowTop.Height = new GridLength(1, GridUnitType.Star);
            if (_lyricsRowBottom != null)
                _lyricsRowBottom.Height = new GridLength(1, GridUnitType.Star);
            if (_lyricsSyncedPanel != null)
                _lyricsSyncedPanel.VerticalAlignment = VerticalAlignment.Stretch;

            if (_lyricsDock != null)
                _lyricsDock.Margin = pinned ? new Thickness(22, 14, 22, 4) : new Thickness(14, 9, 14, 0);
            if (_lyricsBodyGrid != null)
            {
                _lyricsBodyGrid.Margin = pinned ? new Thickness(0, 4, 0, 0) : new Thickness(0, 2, 0, 0);
                _lyricsBodyGrid.OpacityMask = CreateLyricsBodyMask(pinned);
            }
            if (_lyricsHeaderCover != null)
            {
                _lyricsHeaderCover.Width = pinned ? 42 : 34;
                _lyricsHeaderCover.Height = pinned ? 42 : 34;
                _lyricsHeaderCover.CornerRadius = new CornerRadius(pinned ? 10 : 8);
            }
            if (_lyricsTitleStack != null)
                _lyricsTitleStack.MaxWidth = pinned ? 270 : 152;
            if (_lyricsHeaderTitle != null)
                _lyricsHeaderTitle.FontSize = pinned ? 16 : 13;
            if (_lyricsHeaderArtist != null)
                _lyricsHeaderArtist.FontSize = pinned ? 13.5 : 11;

            if (_lyricsPrev2Text != null)
            {
                _lyricsPrev2Text.FontSize = 22.5;
                _lyricsPrev2Text.LineHeight = 28;
                _lyricsPrev2Text.MaxHeight = 30;
            }
            if (_lyricsPrevText != null)
            {
                _lyricsPrevText.FontSize = pinned ? 22.5 : 14;
                _lyricsPrevText.FontWeight = FontWeights.Bold;
                _lyricsPrevText.LineHeight = pinned ? 28 : 17.5;
                _lyricsPrevText.MaxHeight = pinned ? 58 : 35;
                _lyricsPrevText.Margin = new Thickness(0, 0, 0, pinned ? 6 : 3);
            }
            if (_lyricsCurrWrap != null)
                _lyricsCurrWrap.MaxHeight = pinned ? 88 : 37;
            if (_lyricsCurrText != null)
            {
                _lyricsCurrText.FontSize = pinned ? 22.5 : 14;
                _lyricsCurrText.LineHeight = pinned ? 28 : 17.5;
                _lyricsCurrText.MaxHeight = pinned ? 88 : 36;
            }
            if (_lyricsNextText != null)
            {
                _lyricsNextText.FontSize = pinned ? 22.5 : 14;
                _lyricsNextText.FontWeight = FontWeights.Bold;
                _lyricsNextText.LineHeight = pinned ? 28 : 17.5;
                _lyricsNextText.MaxHeight = pinned ? 58 : 35;
                _lyricsNextText.Margin = new Thickness(0, pinned ? 6 : 3, 0, 0);
            }
            if (_lyricsNext2Text != null)
            {
                _lyricsNext2Text.FontSize = pinned ? 22.5 : 12.5;
                _lyricsNext2Text.FontWeight = FontWeights.Bold;
                _lyricsNext2Text.LineHeight = pinned ? 28 : 15.5;
                _lyricsNext2Text.MaxHeight = pinned ? 30 : 16;
                _lyricsNext2Text.Margin = new Thickness(0, pinned ? 6 : 2, 0, 0);
            }
            if (_lyricsFallbackText != null)
                _lyricsFallbackText.FontSize = pinned ? 20 : 13;
            if (_lyricsFallbackSubText != null)
                _lyricsFallbackSubText.FontSize = pinned ? 13.5 : 10.5;

            if (_lyricsPinBtn != null)
            {
                _lyricsPinBtn.Width = pinned ? 36 : 28;
                _lyricsPinBtn.Height = pinned ? 36 : 28;
                _lyricsPinBtn.Padding = new Thickness(0);
                _lyricsPinBtn.Background = pinned
                    ? new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF))
                    : Brushes.Transparent;
                _lyricsPinBtn.Content = NotchDesign.CreateVectorIcon(
                    NotchIconKind.Pin,
                    pinned ? 16 : 14,
                    Brushes.White,
                    pinned ? 2.15 : 1.8);
                _lyricsPinBtn.ToolTip = pinned
                    ? L.T("widgets.lyrics.unpin_tooltip")
                    : L.T("widgets.lyrics.pin_tooltip");
                UpdateLyricsPinVisibility(_lyricsTileCard?.IsMouseOver == true);
            }

            _lyricsLastProgress = -1.0;
            _lyricsBuiltLineKey = string.Empty;
            _lyricsForceLayoutSync = true;
            UpdateLyricsTile();
            AnimateLyricsPinTransition(pinned, tileOffsetFromCenter);
            RaiseStateChanged();
        }

        private static ImageSource CreateBlurredCoverBitmap(ImageSource cover)
        {
            const int size = 160;
            const int bleed = 28;
            var host = new Border
            {
                Width = size,
                Height = size,
                ClipToBounds = true,
                Background = Brushes.Black,
                Child = new Border
                {
                    Margin = new Thickness(-bleed),
                    Background = new ImageBrush(cover) { Stretch = Stretch.UniformToFill },
                    Effect = new BlurEffect
                    {
                        Radius = 24,
                        RenderingBias = RenderingBias.Quality
                    }
                }
            };
            host.Measure(new Size(size, size));
            host.Arrange(new Rect(0, 0, size, size));
            host.UpdateLayout();

            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);
            rtb.Freeze();
            return rtb;
        }

        private void UpdateLyricsPinVisibility(bool isHovered)
        {
            if (_lyricsPinBtn == null) return;
            bool visible = (_isLyricsPinned || isHovered) && _jiggleController?.IsJiggleMode != true;
            _lyricsPinBtn.IsHitTestVisible = visible;
            double target = visible ? 1.0 : 0.0;

            if (!NotchDesign.EffectiveAnimationsEnabled)
            {
                _lyricsPinBtn.BeginAnimation(UIElement.OpacityProperty, null);
                _lyricsPinBtn.Opacity = target;
                return;
            }

            _lyricsPinBtn.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(visible ? 140 : 180))
                {
                    EasingFunction = NotchDesign.CreateEaseOut()
                });
        }

        private void AnimateLyricsUnpinOut(Action onCompleted)
        {
            if (_lyricsFullscreenHost == null)
            {
                onCompleted();
                return;
            }

            double targetX = _lyricsLastTileOffset?.X ?? 0.0;
            double targetY = _lyricsLastTileOffset?.Y ?? 24.0;

            var scale = new ScaleTransform(1.0, 1.0);
            var translate = new TranslateTransform(0.0, 0.0);
            var tg = new TransformGroup();
            tg.Children.Add(scale);
            tg.Children.Add(translate);

            _lyricsFullscreenHost.RenderTransformOrigin = new Point(0.5, 0.5);
            _lyricsFullscreenHost.RenderTransform = tg;

            var dur = TimeSpan.FromMilliseconds(240);
            var ease = NotchDesign.CreateEaseInOut();

            var opAnim = new DoubleAnimation(1.0, 0.0, dur) { EasingFunction = ease };
            opAnim.Completed += (_, _) => onCompleted();

            _lyricsFullscreenHost.BeginAnimation(UIElement.OpacityProperty, opAnim);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 0.50, dur) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 0.50, dur) { EasingFunction = ease });
            translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0.0, targetX, dur) { EasingFunction = ease });
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, targetY, dur) { EasingFunction = ease });
        }

        private void AnimateLyricsPinTransition(bool pinned, Point? startOffset = null)
        {
            if (!NotchDesign.EffectiveAnimationsEnabled) return;

            // Wichtig: Niemals _lyricsTileCard.RenderTransform überschreiben, damit der TransformGroup
            // von BentoJiggleController für Jiggle- & Drag-Animationen im Dashboard erhalten bleibt!
            FrameworkElement? target = pinned ? _lyricsFullscreenHost : _bentoGrid;
            if (target == null) return;

            if (pinned)
            {
                double startX = (startOffset?.X ?? 0.0) * 0.85;
                double startY = (startOffset?.Y ?? 24.0) * 0.85;

                var scale = new ScaleTransform(0.50, 0.50);
                var translate = new TranslateTransform(startX, startY);
                var tg = new TransformGroup();
                tg.Children.Add(scale);
                tg.Children.Add(translate);

                target.RenderTransformOrigin = new Point(0.5, 0.5);
                target.RenderTransform = tg;
                target.Opacity = 0.65;

                var totalDur = TimeSpan.FromMilliseconds(560);
                var centerDur = TimeSpan.FromMilliseconds(420);

                var opacityAnim = new DoubleAnimation(0.65, 1.0, TimeSpan.FromMilliseconds(320))
                {
                    EasingFunction = NotchDesign.CreateEaseOut()
                };
                target.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

                var xAnim = new DoubleAnimation(startX, 0.0, centerDur)
                {
                    EasingFunction = NotchDesign.CreateEaseOut()
                };
                translate.BeginAnimation(TranslateTransform.XProperty, xAnim);

                // Wenn Kachel oben war (startY < 0): leichter Bogen nach unten (+8 px) und hoch zur Mitte;
                // wenn Kachel unten war (startY >= 0): direkt & sauber von unten nach oben in die Mitte
                if (startY < -8.0)
                {
                    var yAnim = new DoubleAnimationUsingKeyFrames();
                    yAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(startY, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                    yAnim.KeyFrames.Add(new SplineDoubleKeyFrame(8.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), new KeySpline(0.25, 0.1, 0.25, 1.0)));
                    yAnim.KeyFrames.Add(new SplineDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(totalDur), new KeySpline(0.16, 1.0, 0.3, 1.0)));
                    translate.BeginAnimation(TranslateTransform.YProperty, yAnim);
                }
                else
                {
                    translate.BeginAnimation(
                        TranslateTransform.YProperty,
                        new DoubleAnimation(startY, 0.0, centerDur) { EasingFunction = NotchDesign.CreateEaseOut() });
                }

                var scaleEase = NotchDesign.CreateSpringEase(0.36);
                var scaleAnim = new DoubleAnimation(0.50, 1.0, totalDur) { EasingFunction = scaleEase };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
                return;
            }

            var unpinScale = new ScaleTransform(0.96, 0.96);
            target.RenderTransformOrigin = new Point(0.5, 0.5);
            target.RenderTransform = unpinScale;
            target.Opacity = 0.55;

            var dur = TimeSpan.FromMilliseconds(260);
            var ease = NotchDesign.CreateEaseOut();
            target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.55, 1.0, dur) { EasingFunction = ease });
            unpinScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1.0, dur) { EasingFunction = ease });
            unpinScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1.0, dur) { EasingFunction = ease });
        }

        private void UpdateLyricsTile()
        {
            if (_lyricsSyncedPanel == null || _lyricsFallbackPanel == null ||
                _lyricsPrevText == null || _lyricsCurrText == null || _lyricsNextText == null || _lyricsNext2Text == null ||
                _lyricsFallbackText == null || _lyricsFallbackSubText == null ||
                _lyricsHeaderTitle == null || _lyricsHeaderArtist == null ||
                _lyricsBgCoverBorder == null || _lyricsHeaderCover == null)
                return;

            var mp = MediaPlayerService.Instance;
            var ls = LyricsService.Instance;
            var snap = ls.Current;

            if (!ReferenceEquals(_lyricsLastCover, mp.Cover))
            {
                _lyricsLastCover = mp.Cover;
                if (mp.Cover != null)
                {
                    if (_lyricsTileCard != null) _lyricsTileCard.Background = Brushes.Black;
                    var blurred = CreateBlurredCoverBitmap(mp.Cover);
                    var bgBrush = new ImageBrush(blurred) { Stretch = Stretch.UniformToFill };
                    bgBrush.Freeze();
                    _lyricsBgCoverBorder.Background = bgBrush;
                    _lyricsBgCoverBorder.Visibility = Visibility.Visible;
                    if (_lyricsTintOverlay != null) _lyricsTintOverlay.Visibility = Visibility.Visible;

                    _lyricsHeaderCover.Background = new ImageBrush(mp.Cover) { Stretch = Stretch.UniformToFill };
                    if (_lyricsHeaderCoverFallback != null) _lyricsHeaderCoverFallback.Visibility = Visibility.Collapsed;
                }
                else
                {
                    if (_lyricsTileCard != null) _lyricsTileCard.Background = NotchDesign.CreateAccentGradientBrush();
                    _lyricsBgCoverBorder.Background = null;
                    _lyricsBgCoverBorder.Visibility = Visibility.Collapsed;
                    if (_lyricsTintOverlay != null) _lyricsTintOverlay.Visibility = Visibility.Collapsed;

                    _lyricsHeaderCover.Background = new SolidColorBrush(Color.FromArgb(0x32, 0xFF, 0xFF, 0xFF));
                    if (_lyricsHeaderCoverFallback != null) _lyricsHeaderCoverFallback.Visibility = Visibility.Visible;
                }
            }

            string headerTitle = mp.HasKnownTrack ? mp.TrackTitle : L.T("widgets.bento.lyrics");
            string headerArtist = mp.HasKnownTrack ? mp.Artist : L.T("widgets.lyrics.no_song");
            if (!string.Equals(_lyricsHeaderTitle.Text, headerTitle, StringComparison.Ordinal))
                _lyricsHeaderTitle.Text = headerTitle;
            if (!string.Equals(_lyricsHeaderArtist.Text, headerArtist, StringComparison.Ordinal))
                _lyricsHeaderArtist.Text = headerArtist;

            if (!SettingsService.Current.OptInLyrics)
            {
                ResetLyricsSyncedState();
                _lyricsSyncedPanel.Visibility = Visibility.Collapsed;
                _lyricsFallbackPanel.Visibility = Visibility.Visible;
                _lyricsFallbackText.Text = L.T("widgets.lyrics.disabled");
                _lyricsFallbackSubText.Text = L.T("widgets.lyrics.disabled_hint");
                if (_lyricsOptInBtn != null) _lyricsOptInBtn.Visibility = Visibility.Visible;
                return;
            }

            if (_lyricsOptInBtn != null) _lyricsOptInBtn.Visibility = Visibility.Collapsed;

            if (!mp.HasKnownTrack || snap.Status == LyricsStatus.Idle)
            {
                ResetLyricsSyncedState();
                _lyricsSyncedPanel.Visibility = Visibility.Collapsed;
                _lyricsFallbackPanel.Visibility = Visibility.Visible;
                _lyricsFallbackText.Text = L.T("widgets.lyrics.start_play");
                _lyricsFallbackSubText.Text = L.T("widgets.lyrics.start_hint");
                return;
            }

            switch (snap.Status)
            {
                case LyricsStatus.Loading:
                    ResetLyricsSyncedState();
                    _lyricsSyncedPanel.Visibility = Visibility.Collapsed;
                    _lyricsFallbackPanel.Visibility = Visibility.Visible;
                    _lyricsFallbackText.Text = L.T("widgets.lyrics.loading");
                    _lyricsFallbackSubText.Text = mp.TrackTitle;
                    break;

                case LyricsStatus.Synced when snap.SyncedLines.Count > 0:
                    if (_lyricsSyncedPanel.Visibility != Visibility.Visible)
                        _lyricsSyncedPanel.Visibility = Visibility.Visible;
                    if (_lyricsFallbackPanel.Visibility != Visibility.Collapsed)
                        _lyricsFallbackPanel.Visibility = Visibility.Collapsed;
                    if (_lyricsCurrWrap != null && _lyricsCurrWrap.Visibility != Visibility.Visible)
                        _lyricsCurrWrap.Visibility = Visibility.Visible;
                    if (_lyricsCurrText.Visibility != Visibility.Collapsed)
                        _lyricsCurrText.Visibility = Visibility.Collapsed;

                    bool trackChanged = !string.Equals(_lyricsLastTrackKey, snap.TrackKey, StringComparison.Ordinal);
                    if (trackChanged)
                    {
                        _lyricsLastTrackKey = snap.TrackKey;
                        _lyricsLastActiveIdx = int.MinValue;
                        _lyricsLastProgress = -1.0;
                        _lyricsForceLayoutSync = true;
                        if (_lyricsScrollTransform != null)
                        {
                            _lyricsScrollTransform.BeginAnimation(TranslateTransform.YProperty, null);
                            _lyricsScrollTransform.Y = 0;
                        }
                    }

                    var (prev, curr, next, next2, progress, activeIdx) = ls.GetSyncedWindow(mp.GetInterpolatedPosition(), mp.Duration);
                    bool lineChanged = activeIdx != _lyricsLastActiveIdx;
                    if (lineChanged && _lyricsLastActiveIdx != int.MinValue)
                        AnimateLyricsLineScroll(forward: activeIdx > _lyricsLastActiveIdx);

                    if (lineChanged || _lyricsForceLayoutSync)
                    {
                        _lyricsLastActiveIdx = activeIdx;
                        _lyricsForceLayoutSync = false;

                        string? prev2 = (_isLyricsPinned && activeIdx >= 2 && activeIdx - 2 < snap.SyncedLines.Count)
                            ? snap.SyncedLines[activeIdx - 2].Text
                            : null;
                        if (_lyricsPrev2Text != null)
                        {
                            _lyricsPrev2Text.Text = prev2 ?? string.Empty;
                            _lyricsPrev2Text.Visibility = (_isLyricsPinned && !string.IsNullOrEmpty(prev2))
                                ? Visibility.Visible
                                : Visibility.Collapsed;
                        }
                        _lyricsPrevText.Text = prev ?? string.Empty;
                        _lyricsPrevText.Visibility = string.IsNullOrEmpty(prev) ? Visibility.Collapsed : Visibility.Visible;

                        if (_lyricsRowTop != null && !_lyricsRowTop.Height.IsStar)
                            _lyricsRowTop.Height = new GridLength(1, GridUnitType.Star);
                        if (_lyricsRowBottom != null && !_lyricsRowBottom.Height.IsStar)
                            _lyricsRowBottom.Height = new GridLength(1, GridUnitType.Star);

                        _lyricsNextText.Text = next ?? string.Empty;
                        _lyricsNextText.Visibility = string.IsNullOrEmpty(next) ? Visibility.Collapsed : Visibility.Visible;

                        _lyricsNext2Text.Text = next2 ?? string.Empty;
                        _lyricsNext2Text.Foreground = LyricsFarLineBrush;
                        _lyricsNext2Text.Visibility = (_isLyricsPinned && !string.IsNullOrEmpty(next2))
                            ? Visibility.Visible
                            : Visibility.Collapsed;

                        string? next3 = (_isLyricsPinned && activeIdx < 0 && activeIdx + 3 < snap.SyncedLines.Count)
                            ? snap.SyncedLines[activeIdx + 3].Text
                            : null;
                        if (_lyricsNext3Text != null)
                        {
                            _lyricsNext3Text.Text = next3 ?? string.Empty;
                            _lyricsNext3Text.Visibility = (_isLyricsPinned && !string.IsNullOrEmpty(next3))
                                ? Visibility.Visible
                                : Visibility.Collapsed;
                        }

                        if (_lyricsNext4Text != null)
                            _lyricsNext4Text.Visibility = Visibility.Collapsed;
                    }

                    if (_lyricsCurrWrap != null)
                        ApplyLyricWordWave(_lyricsCurrWrap, curr, progress, activeIdx);
                    break;

                case LyricsStatus.Plain when snap.PlainLines.Count > 0:
                    ResetLyricsSyncedState();
                    _lyricsBuiltLineKey = string.Empty;
                    _lyricsSyncedPanel.Visibility = Visibility.Visible;
                    _lyricsFallbackPanel.Visibility = Visibility.Collapsed;
                    if (_lyricsCurrWrap != null) _lyricsCurrWrap.Visibility = Visibility.Collapsed;
                    _lyricsCurrText.Visibility = Visibility.Visible;
                    if (_lyricsPrev2Text != null) _lyricsPrev2Text.Visibility = Visibility.Collapsed;
                    _lyricsPrevText.Visibility = Visibility.Collapsed;
                    _lyricsCurrText.Inlines.Clear();
                    _lyricsCurrText.Text = snap.PlainLines[0];
                    _lyricsNextText.Text = snap.PlainLines.Count > 1 ? snap.PlainLines[1] : string.Empty;
                    _lyricsNextText.Visibility = snap.PlainLines.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                    if (_lyricsNext3Text != null) _lyricsNext3Text.Visibility = Visibility.Collapsed;
                    if (_lyricsNext4Text != null) _lyricsNext4Text.Visibility = Visibility.Collapsed;
                    _lyricsNext2Text.Text = snap.PlainLines.Count > 2 ? snap.PlainLines[2] : string.Empty;
                    _lyricsNext2Text.Visibility = (_isLyricsPinned && snap.PlainLines.Count > 2)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    break;

                case LyricsStatus.Instrumental:
                    ResetLyricsSyncedState();
                    _lyricsSyncedPanel.Visibility = Visibility.Collapsed;
                    _lyricsFallbackPanel.Visibility = Visibility.Visible;
                    _lyricsFallbackText.Text = L.T("widgets.lyrics.instrumental");
                    _lyricsFallbackSubText.Text = mp.Artist;
                    break;

                case LyricsStatus.Error:
                    ResetLyricsSyncedState();
                    _lyricsSyncedPanel.Visibility = Visibility.Collapsed;
                    _lyricsFallbackPanel.Visibility = Visibility.Visible;
                    _lyricsFallbackText.Text = L.T("widgets.lyrics.unreachable");
                    _lyricsFallbackSubText.Text = snap.ErrorMessage ?? L.T("widgets.lyrics.conn_err");
                    break;

                default:
                    ResetLyricsSyncedState();
                    _lyricsSyncedPanel.Visibility = Visibility.Collapsed;
                    _lyricsFallbackPanel.Visibility = Visibility.Visible;
                    _lyricsFallbackText.Text = L.T("widgets.lyrics.none");
                    _lyricsFallbackSubText.Text = mp.TrackTitle;
                    break;
            }
        }

        private void ResetLyricsSyncedState()
        {
            _lyricsLastProgress = -1.0;
            _lyricsLastActiveIdx = int.MinValue;
            _lyricsLastTrackKey = string.Empty;
            _lyricsForceLayoutSync = true;
        }

        private void AnimateLyricsLineScroll(bool forward)
        {
            if (_lyricsScrollTransform == null) return;
            if (!NotchDesign.EffectiveAnimationsEnabled)
            {
                _lyricsScrollTransform.BeginAnimation(TranslateTransform.YProperty, null);
                _lyricsScrollTransform.Y = 0;
                return;
            }

            // Exakt eine Zeilenhöhe (LineHeight + Margin), damit die bisherige aktive Zeile nahtlos
            // von ihrer alten Position hochrollt ohne zu springen
            double offset = _isLyricsPinned ? 34.0 : 20.5;
            double from = forward ? offset : -offset;
            var anim = new DoubleAnimation(from, 0.0, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = NotchDesign.CreateEaseOut()
            };
            Timeline.SetDesiredFrameRate(anim, SettingsService.Current.TargetFrameRate);
            _lyricsScrollTransform.BeginAnimation(TranslateTransform.YProperty, anim);
        }

        private void ApplyLyricWordWave(WrapPanel wrap, string text, double progress, int activeIdx)
        {
            if (string.IsNullOrEmpty(text))
            {
                if (_lyricsGlyphTokens.Count > 0)
                {
                    wrap.Children.Clear();
                    _lyricsGlyphTokens.Clear();
                    _lyricsBuiltLineKey = string.Empty;
                    _lyricsLastProgress = -1.0;
                }
                return;
            }

            string lineKey = $"{activeIdx}:{_isLyricsPinned}:{text}";
            if (!string.Equals(_lyricsBuiltLineKey, lineKey, StringComparison.Ordinal))
            {
                _lyricsBuiltLineKey = lineKey;
                _lyricsLastProgress = -1.0;
                BuildLyricGlyphTokens(wrap, text);
            }

            double clampedProgress = Math.Clamp(progress, 0.0, 1.0);
            if (_lyricsLastProgress >= 0.0 && Math.Abs(clampedProgress - _lyricsLastProgress) < 0.0004)
                return;
            _lyricsLastProgress = clampedProgress;

            double cursor = clampedProgress * text.Length;
            bool animateWave = NotchDesign.EffectiveAnimationsEnabled;
            double maxScale = _isLyricsPinned ? 1.13 : 1.08;

            foreach (var glyph in _lyricsGlyphTokens)
            {
                // trail > 0: Buchstabe liegt bereits hinter dem Cursor (Nachlauf)
                // trail < 0: Buchstabe kommt noch
                double trail = cursor - (glyph.CharIndex + 0.35);

                // GPU-freundliches Opacity-Glow (keine Text-Neu-Rasterisierung pro Frame):
                // aktives Wort leuchtet reinweiß (1.0), bereits gesungene Buchstaben bleiben weich (0.85), ungesungene blass (0.22)
                bool inActiveWord = cursor >= glyph.WordStart - 0.3 && cursor <= glyph.WordEnd + 0.3;
                double glowT;
                if (trail >= 1.2 && !inActiveWord)
                    glowT = 0.85;
                else if (trail >= -0.4 || (inActiveWord && trail >= -0.8))
                    glowT = 1.0;
                else if (trail > -2.0)
                    glowT = Math.Clamp(1.0 - (-trail - 0.4) / 1.6, 0.0, 1.0);
                else
                    glowT = 0.0;

                double targetOpacity = LyricsInactiveOpacity + glowT * (1.0 - LyricsInactiveOpacity);
                if (Math.Abs(targetOpacity - glyph.LastOpacity) > 0.004)
                {
                    glyph.Label.Opacity = targetOpacity;
                    glyph.LastOpacity = targetOpacity;
                }

                double targetScale = 1.0;
                if (animateWave)
                {
                    double pulse = 0.0;
                    if (trail >= 0.0 && trail <= 4.8)
                    {
                        double t = 1.0 - (trail / 4.8);
                        pulse = Math.Pow(t, 1.1);
                    }
                    else if (trail > -1.1 && trail < 0.0)
                    {
                        double t = 1.0 + (trail / 1.1);
                        pulse = Math.Pow(t, 1.4);
                    }

                    if (inActiveWord)
                        pulse = Math.Max(pulse, 0.28);

                    targetScale = 1.0 + (maxScale - 1.0) * pulse;
                }

                if (Math.Abs(targetScale - glyph.LastScale) > 0.003)
                {
                    glyph.Scale.ScaleX = targetScale;
                    glyph.Scale.ScaleY = targetScale;
                    glyph.LastScale = targetScale;
                }
            }
        }

        private void BuildLyricGlyphTokens(WrapPanel wrap, string text)
        {
            wrap.Children.Clear();
            _lyricsGlyphTokens.Clear();

            double fontSize = _isLyricsPinned ? 22.5 : 14.0;
            double lineHeight = _isLyricsPinned ? 28.0 : 17.5;
            double wordGap = _isLyricsPinned ? 6.4 : 3.8;

            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;
                if (i >= text.Length) break;

                int wordStart = i;
                int wordEnd = i;
                while (wordEnd < text.Length && !char.IsWhiteSpace(text[wordEnd]))
                    wordEnd++;

                var wordPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, wordGap, _isLyricsPinned ? 1 : 0),
                    IsHitTestVisible = false
                };

                while (i < wordEnd)
                {
                    var scale = new ScaleTransform(1.0, 1.0);

                    var label = new TextBlock
                    {
                        Text = text[i].ToString(),
                        FontFamily = NotchDesign.UiDisplayFont,
                        FontSize = fontSize,
                        FontWeight = FontWeights.Bold,
                        LineHeight = lineHeight,
                        Foreground = Brushes.White,
                        Opacity = LyricsInactiveOpacity,
                        RenderTransformOrigin = new Point(0.5, 0.55),
                        RenderTransform = scale,
                        IsHitTestVisible = false
                    };

                    wordPanel.Children.Add(label);
                    _lyricsGlyphTokens.Add(new LyricGlyphToken(label, scale, i, wordStart, wordEnd, LyricsInactiveOpacity));
                    i++;
                }

                wrap.Children.Add(wordPanel);
            }
        }

    }
}
