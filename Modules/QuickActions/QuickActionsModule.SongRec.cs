using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using WindowsNotch.Core;
using WindowsNotch.Modules.SongRecognition;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.QuickActions
{
    public partial class QuickActionsModule
    {
        private SongRecognitionResult? _lastSongResult;
        private bool _autoStartSongRec = true;

        private Border BuildSongRecognitionSubView()
        {
            if (!SettingsService.Current.OptInMusicRecognition)
            {
                _autoStartSongRec = true;
                return BuildSubViewShell(
                    L.T("qa.song_rec"),
                    NotchIconKind.Waveform,
                    BuildSongRecOptInBody());
            }

            if (!_isListening && _lastSongResult == null && _autoStartSongRec)
            {
                _autoStartSongRec = false;
                _ = StartSongRecognitionInSubViewAsync();
            }

            FrameworkElement body;
            if (_isListening)
                body = BuildSongRecListeningBody(out _);
            else if (_lastSongResult != null)
                body = BuildSongRecResultBody(_lastSongResult);
            else
                body = BuildSongRecIdleBody();

            return BuildSubViewShell(
                L.T("qa.song_rec"),
                NotchIconKind.Waveform,
                body);
        }

        private FrameworkElement BuildSongRecOptInBody()
        {
            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxWidth = 360
            };

            var badge = new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(22),
                Background = NotchDesign.CreateSoftAccentTileBrush(0x24),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10),
                Child = NotchDesign.CreateVectorIcon(NotchIconKind.Waveform, 22, NotchDesign.AccentPrimaryBrush, 2.1)
            };
            stack.Children.Add(badge);

            stack.Children.Add(new TextBlock
            {
                Text = L.T("songrec.optin_title"),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 14.5,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            });

            stack.Children.Add(new TextBlock
            {
                Text = L.T("songrec.optin_desc"),
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            });

            var enableBtn = NotchDesign.CreateRoundedButton(
                L.T("songrec.optin_enable_btn"),
                () =>
                {
                    SettingsService.UpdateQuiet(s => s.OptInMusicRecognition = true);
                    _lastSongResult = null;
                    _autoStartSongRec = false;
                    _ = StartSongRecognitionInSubViewAsync();
                },
                background: NotchDesign.AccentPrimaryBrush,
                hoverBackground: NotchDesign.AccentHoverBrush,
                foreground: Brushes.White,
                padding: new Thickness(18, 8, 18, 8));
            enableBtn.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(enableBtn);

            return stack;
        }

        private FrameworkElement BuildSongRecIdleBody()
        {
            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var startBtn = NotchDesign.CreateRoundedButton(
                L.T("songrec.start_btn"),
                () => _ = StartSongRecognitionInSubViewAsync(),
                background: NotchDesign.AccentPrimaryBrush,
                hoverBackground: NotchDesign.AccentHoverBrush,
                foreground: Brushes.White,
                padding: new Thickness(18, 9, 18, 9));
            stack.Children.Add(startBtn);
            return stack;
        }

        private Border[]? _waveBars;

        private FrameworkElement BuildSongRecListeningBody(out Border[] bars)
        {
            var root = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            // Animierte Shazam-Welle (17 symmetrische Balken + pulsierendes Zentrum)
            var wavePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Height = 84,
                Margin = new Thickness(0, 4, 0, 12)
            };

            const int barCount = 17;
            bars = new Border[barCount];
            for (int i = 0; i < barCount; i++)
            {
                double dist = Math.Abs(i - barCount / 2) / (barCount / 2.0);
                double baseH = 14 + (1.0 - dist) * 18;
                var bar = new Border
                {
                    Width = 5,
                    Height = baseH,
                    CornerRadius = new CornerRadius(2.5),
                    Background = i == barCount / 2
                        ? NotchDesign.AccentPrimaryBrush
                        : NotchDesign.CreateSoftAccentTileBrush((byte)(0x70 + (1.0 - dist) * 0x70)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2.5, 0, 2.5, 0)
                };
                bars[i] = bar;
                wavePanel.Children.Add(bar);
            }
            _waveBars = bars;
            root.Children.Add(wavePanel);

            root.Children.Add(new TextBlock
            {
                Text = L.T("songrec.listening_hint"),
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            });

            var cancelBtn = NotchDesign.CreateRoundedButton(
                L.T("songrec.cancel_btn"),
                () => _recogCts?.Cancel(),
                background: NotchDesign.CardBackgroundBrush,
                hoverBackground: NotchDesign.NestedCardBackgroundBrush,
                foreground: NotchDesign.TextSecondaryBrush,
                padding: new Thickness(16, 7, 16, 7));
            cancelBtn.HorizontalAlignment = HorizontalAlignment.Center;
            root.Children.Add(cancelBtn);

            return root;
        }

        private void UpdateWaveBars(double level)
        {
            var bars = _waveBars;
            if (bars == null || bars.Length == 0) return;

            double clamped = Math.Clamp(level, 0.04, 1.0);
            int mid = bars.Length / 2;
            double tick = Environment.TickCount64 / 110.0;

            for (int i = 0; i < bars.Length; i++)
            {
                double dist = Math.Abs(i - mid) / (double)mid;
                double envelope = 1.0 - dist * 0.55;
                double ripple = 0.72 + 0.28 * Math.Sin(tick + i * 0.65);
                double targetH = Math.Clamp(12.0 + clamped * 64.0 * envelope * ripple, 10.0, 78.0);

                if (NotchDesign.EffectiveAnimationsEnabled)
                {
                    var anim = new DoubleAnimation(targetH, TimeSpan.FromMilliseconds(95))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    bars[i].BeginAnimation(FrameworkElement.HeightProperty, anim);
                }
                else
                {
                    bars[i].Height = targetH;
                }
            }
        }

        private FrameworkElement BuildSongRecResultBody(SongRecognitionResult res)
        {
            if (!res.Success)
            {
                var errStack = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    MaxWidth = 360
                };

                errStack.Children.Add(new Border
                {
                    Width = 42,
                    Height = 42,
                    CornerRadius = new CornerRadius(21),
                    Background = NotchDesign.CardBackgroundBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 10),
                    Child = NotchDesign.CreateVectorIcon(NotchIconKind.Waveform, 20, NotchDesign.TextMutedBrush, 2.0)
                });

                errStack.Children.Add(new TextBlock
                {
                    Text = res.StatusMessage,
                    Foreground = NotchDesign.TextSecondaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 12.5,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 14)
                });

                var retryBtn = NotchDesign.CreateRoundedButton(
                    L.T("songrec.retry_btn"),
                    () => _ = StartSongRecognitionInSubViewAsync(),
                    background: NotchDesign.AccentPrimaryBrush,
                    hoverBackground: NotchDesign.AccentHoverBrush,
                    foreground: Brushes.White,
                    padding: new Thickness(16, 8, 16, 8));
                retryBtn.HorizontalAlignment = HorizontalAlignment.Center;
                errStack.Children.Add(retryBtn);
                return errStack;
            }

            var root = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            // Track-Info-Zeile (Cover + Titel/Artist/Album)
            var infoGrid = new Grid { Margin = new Thickness(2, 4, 2, 14) };
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var coverBox = new Border
            {
                Width = 72,
                Height = 72,
                CornerRadius = new CornerRadius(NotchDesign.RadiusInner),
                Background = NotchDesign.CreateSoftAccentTileBrush(0x22),
                ClipToBounds = true,
                Child = NotchDesign.CreateVectorIcon(NotchIconKind.Music, 28, NotchDesign.AccentPrimaryBrush, 1.9)
            };
            if (!string.IsNullOrWhiteSpace(res.CoverUrl) && Uri.TryCreate(res.CoverUrl, UriKind.Absolute, out var coverUri))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = coverUri;
                    bmp.DecodePixelWidth = 144;
                    bmp.EndInit();
                    coverBox.Child = new Image
                    {
                        Source = bmp,
                        Stretch = Stretch.UniformToFill
                    };
                }
                catch { }
            }
            Grid.SetColumn(coverBox, 0);
            infoGrid.Children.Add(coverBox);

            var metaStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            metaStack.Children.Add(new TextBlock
            {
                Text = res.Title,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            if (!string.IsNullOrWhiteSpace(res.Artist))
            {
                metaStack.Children.Add(new TextBlock
                {
                    Text = res.Artist,
                    Foreground = NotchDesign.TextSecondaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            string subLine = !string.IsNullOrWhiteSpace(res.Album)
                ? $"{res.Album} · {res.Source}"
                : res.Source;
            if (!string.IsNullOrWhiteSpace(subLine))
            {
                metaStack.Children.Add(new TextBlock
                {
                    Text = subLine,
                    Foreground = NotchDesign.TextMutedBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            Grid.SetColumn(metaStack, 2);
            infoGrid.Children.Add(metaStack);
            root.Children.Add(infoGrid);

            // Aktions-Zeile: Spotify + Apple Music + Erneut
            var actions = new Grid();
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var spotifyBtn = BuildStreamingLinkButton(
                "Spotify",
                Color.FromRgb(0x1D, 0xB9, 0x54),
                () => OpenExternalHttpsUrl(res.SpotifyUrl));
            Grid.SetColumn(spotifyBtn, 0);
            actions.Children.Add(spotifyBtn);

            var appleBtn = BuildStreamingLinkButton(
                "Apple Music",
                Color.FromRgb(0xFA, 0x24, 0x3C),
                () => OpenExternalHttpsUrl(res.AppleMusicUrl));
            Grid.SetColumn(appleBtn, 2);
            actions.Children.Add(appleBtn);

            var againBtn = NotchDesign.CreateIconActionButton(
                NotchIconKind.Reset,
                L.T("songrec.retry_btn"),
                () => _ = StartSongRecognitionInSubViewAsync(),
                size: 36);
            Grid.SetColumn(againBtn, 4);
            actions.Children.Add(againBtn);

            root.Children.Add(actions);
            return root;
        }

        private static Button BuildStreamingLinkButton(string label, Color brandColor, Action onClick)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(brandColor),
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });

            return NotchDesign.CreateRoundedButton(
                row,
                onClick,
                background: NotchDesign.CardBackgroundBrush,
                hoverBackground: NotchDesign.NestedCardBackgroundBrush,
                cornerRadius: NotchDesign.RadiusControl,
                padding: new Thickness(10, 9, 10, 9));
        }

        private async Task StartSongRecognitionInSubViewAsync()
        {
            if (_isListening) return;

            _isListening = true;
            _lastSongResult = null;
            _recogCts?.Dispose();
            _recogCts = new CancellationTokenSource();
            var ct = _recogCts.Token;

            if (_activeSubView == QuickActionsSubView.SongRecognition)
                RebuildExpandedUi();

            try
            {
                var result = await SongRecognitionService.RecognizeAsync(
                    level => Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        if (_isListening && _activeSubView == QuickActionsSubView.SongRecognition)
                            UpdateWaveBars(level);
                    }),
                    ct);

                _lastSongResult = result;
                if (result.Success)
                {
                    _recognizedLine = result.Title;
                    _recognizedSub = string.IsNullOrWhiteSpace(result.Artist) ? result.StatusMessage : result.Artist;
                }
                else
                {
                    _recognizedLine = L.T("qa.song_rec");
                    _recognizedSub = result.StatusMessage;
                }
            }
            catch (OperationCanceledException)
            {
                _lastSongResult = new SongRecognitionResult(false, string.Empty, string.Empty, string.Empty, L.T("songrec.canceled"));
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                _lastSongResult = new SongRecognitionResult(false, string.Empty, string.Empty, string.Empty, L.T("qa.song_failed"));
            }
            finally
            {
                _isListening = false;
                _waveBars = null;
                if (_activeSubView == QuickActionsSubView.SongRecognition)
                    RebuildExpandedUi();
            }
        }

        private static void OpenExternalHttpsUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
            if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }
    }
}
