using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.FileProperties;
using WindowsNotch.Core;
using WindowsNotch.Services;
using Forms = System.Windows.Forms;

namespace WindowsNotch.Modules.ScreenCapture
{
    public partial class ScreenCaptureModule
    {
        private Border CreateThumbnailElement(string filePath, double width, double height, double radius)
        {
            var container = new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(radius),
                Background = NotchDesign.GetFrozenBrush(Color.FromRgb(0x18, 0x1A, 0x20)),
                ClipToBounds = true
            };

            if (_thumbCache.TryGetValue(filePath, out var bmp))
            {
                container.Child = new Image
                {
                    Source = bmp,
                    Stretch = Stretch.UniformToFill
                };
            }
            else
            {
                if (!_pendingThumbContainers.TryGetValue(filePath, out var list))
                {
                    list = new List<WeakReference<Border>>();
                    _pendingThumbContainers[filePath] = list;
                }
                list.Add(new WeakReference<Border>(container));

                container.Child = NotchDesign.CreateVectorIcon(NotchIconKind.Play, Math.Min(18, height * 0.36), NotchDesign.TextMutedBrush, 1.8);
                RequestThumbnailAsync(filePath);
            }

            return container;
        }

        private void RequestThumbnailAsync(string filePath)
        {
            if (_thumbCache.ContainsKey(filePath)) return;
            if (_thumbFailures.TryGetValue(filePath, out int fails) && fails >= 2) return;
            if (!_thumbLoading.Add(filePath)) return;

            _ = Task.Run(async () =>
            {
                ImageSource? result = null;
                try
                {
                    if (!File.Exists(filePath)) return;
                    var info = new FileInfo(filePath);
                    if (info.Length == 0) return;

                    MemoryStream? ms = null;
                    try
                    {
                        var file = await StorageFile.GetFileFromPathAsync(filePath);
                        try
                        {
                            var clip = await MediaClip.CreateFromFileAsync(file);
                            var comp = new MediaComposition();
                            comp.Clips.Add(clip);
                            var pos = clip.OriginalDuration > TimeSpan.FromMilliseconds(200)
                                ? TimeSpan.FromMilliseconds(200)
                                : TimeSpan.Zero;
                            using var frameStream = await comp.GetThumbnailAsync(pos, 320, 180, VideoFramePrecision.NearestKeyFrame);
                            if (frameStream != null && frameStream.Size > 0)
                            {
                                using var s = frameStream.AsStreamForRead();
                                ms = new MemoryStream();
                                await s.CopyToAsync(ms);
                            }
                        }
                        catch { }

                        if (ms == null || ms.Length == 0)
                        {
                            try
                            {
                                using var thumb = await file.GetThumbnailAsync(ThumbnailMode.VideosView, 320);
                                if (thumb != null && thumb.Size > 0)
                                {
                                    using var s = thumb.AsStreamForRead();
                                    ms = new MemoryStream();
                                    await s.CopyToAsync(ms);
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }

                    if (ms != null && ms.Length > 0)
                    {
                        ms.Position = 0;
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.StreamSource = ms;
                        bi.EndInit();
                        bi.Freeze();
                        result = bi;
                    }

                    if (result == null)
                        result = await CaptureFrameWithMediaPlayerAsync(filePath);
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                }
                finally
                {
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        _thumbLoading.Remove(filePath);
                        if (result != null)
                        {
                            _thumbFailures.Remove(filePath);
                            _thumbCache[filePath] = result;
                            if (_pendingThumbContainers.TryGetValue(filePath, out var containers))
                            {
                                _pendingThumbContainers.Remove(filePath);
                                foreach (var wr in containers)
                                {
                                    if (wr.TryGetTarget(out var border))
                                    {
                                        border.Child = new Image
                                        {
                                            Source = result,
                                            Stretch = Stretch.UniformToFill
                                        };
                                    }
                                }
                            }
                        }
                        else
                        {
                            _thumbFailures[filePath] = (_thumbFailures.TryGetValue(filePath, out int c) ? c : 0) + 1;
                        }
                    });
                }
            });
        }

        private static Task<ImageSource?> CaptureFrameWithMediaPlayerAsync(string filePath)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return Task.FromResult<ImageSource?>(null);

            var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher.BeginInvoke(() =>
            {
                MediaPlayer? player = null;
                DispatcherTimer? timeout = null;
                bool completed = false;

                void Finish(ImageSource? img)
                {
                    if (completed) return;
                    completed = true;
                    timeout?.Stop();
                    if (player != null)
                    {
                        try { player.Close(); } catch { }
                    }
                    tcs.TrySetResult(img);
                }

                try
                {
                    player = new MediaPlayer
                    {
                        ScrubbingEnabled = true,
                        IsMuted = true
                    };

                    timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                    timeout.Tick += (_, _) => Finish(null);
                    timeout.Start();

                    player.MediaFailed += (_, _) => Finish(null);
                    player.MediaOpened += async (_, _) =>
                    {
                        try
                        {
                            if (player == null || completed) return;
                            int vw = player.NaturalVideoWidth;
                            int vh = player.NaturalVideoHeight;
                            if (vw <= 0 || vh <= 0)
                            {
                                Finish(null);
                                return;
                            }

                            var targetPos = player.NaturalDuration.HasTimeSpan && player.NaturalDuration.TimeSpan > TimeSpan.FromMilliseconds(200)
                                ? TimeSpan.FromMilliseconds(200)
                                : TimeSpan.Zero;
                            player.Position = targetPos;
                            player.Play();
                            await Task.Delay(180);
                            if (completed) return;
                            player.Pause();

                            const int outW = 320;
                            int outH = Math.Max(180, (int)Math.Round(outW * (double)vh / vw));
                            var dv = new DrawingVisual();
                            using (var dc = dv.RenderOpen())
                            {
                                dc.DrawVideo(player, new Rect(0, 0, outW, outH));
                            }
                            var rtb = new RenderTargetBitmap(outW, outH, 96, 96, PixelFormats.Pbgra32);
                            rtb.Render(dv);
                            rtb.Freeze();
                            Finish(rtb);
                        }
                        catch
                        {
                            Finish(null);
                        }
                    };

                    player.Open(new Uri(filePath, UriKind.Absolute));
                }
                catch
                {
                    Finish(null);
                }
            });

            return tcs.Task;
        }

        private Border BuildSettingsView()
        {
            var card = NotchDesign.CreateCard(14, new Thickness(16, 14, 16, 14));
            card.Background = NotchDesign.CardBackgroundBrush;

            var stack = new StackPanel();
            bool locked = _service.IsRecording;

            var screens = Forms.Screen.AllScreens;
            stack.Children.Add(CreateSettingSectionLabel(L.T("screencapture.sec_display")));
            var screenRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            for (int i = 0; i < screens.Length; i++)
            {
                int idx = i;
                var sc = screens[i];
                screenRow.Children.Add(CreateOptionPill(
                    L.F("screencapture.monitor_option", i + 1, sc.Bounds.Width, sc.Bounds.Height),
                    _options.MonitorIndex == idx,
                    locked,
                    () =>
                    {
                        _options.MonitorIndex = idx;
                        RefreshViews();
                    }));
            }
            stack.Children.Add(screenRow);

            var qFpsGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            qFpsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            qFpsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            qFpsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var qStack = new StackPanel();
            qStack.Children.Add(CreateSettingSectionLabel(L.T("screencapture.sec_quality")));
            var qRow = new WrapPanel();
            foreach (var (q, text) in new[]
            {
                (CaptureQuality.Low480p, "480p"),
                (CaptureQuality.Medium720p, "720p"),
                (CaptureQuality.High1080p, "1080p"),
                (CaptureQuality.Original, L.T("screencapture.quality_native"))
            })
            {
                var captured = q;
                qRow.Children.Add(CreateOptionPill(text, _options.Quality == captured, locked, () =>
                {
                    _options.Quality = captured;
                    RefreshViews();
                }));
            }
            qStack.Children.Add(qRow);
            Grid.SetColumn(qStack, 0);
            qFpsGrid.Children.Add(qStack);

            var fStack = new StackPanel();
            fStack.Children.Add(CreateSettingSectionLabel(L.T("screencapture.sec_fps")));
            var fRow = new WrapPanel();
            foreach (int fps in new[] { 24, 30, 60 })
            {
                int f = fps;
                fRow.Children.Add(CreateOptionPill(L.F("screencapture.fps_option", f), _options.Fps == f, locked, () =>
                {
                    _options.Fps = f;
                    RefreshViews();
                }));
            }
            fStack.Children.Add(fRow);
            Grid.SetColumn(fStack, 2);
            qFpsGrid.Children.Add(fStack);

            stack.Children.Add(qFpsGrid);

            stack.Children.Add(CreateSettingSectionLabel(L.T("screencapture.sec_audio_cursor")));
            var togglesGrid = new Grid();
            togglesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            togglesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            togglesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            togglesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            togglesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var sysBtn = CreateToggleTile(NotchIconKind.VolumeOn, L.T("screencapture.sys_audio"), _options.IncludeSystemAudio, locked, () =>
            {
                _options.IncludeSystemAudio = !_options.IncludeSystemAudio;
                RefreshViews();
            });
            Grid.SetColumn(sysBtn, 0);
            togglesGrid.Children.Add(sysBtn);

            var micBtn = CreateToggleTile(NotchIconKind.Microphone, L.T("screencapture.microphone"), _options.IncludeMicrophone, locked, () =>
            {
                _options.IncludeMicrophone = !_options.IncludeMicrophone;
                RefreshViews();
            });
            Grid.SetColumn(micBtn, 2);
            togglesGrid.Children.Add(micBtn);

            var curBtn = CreateToggleTile(NotchIconKind.Monitor, L.T("screencapture.cursor"), _options.ShowCursor, locked, () =>
            {
                _options.ShowCursor = !_options.ShowCursor;
                RefreshViews();
            });
            Grid.SetColumn(curBtn, 4);
            togglesGrid.Children.Add(curBtn);

            stack.Children.Add(togglesGrid);
            card.Child = stack;
            return card;
        }

        private static TextBlock CreateSettingSectionLabel(string text) => new()
        {
            Text = text,
            Foreground = NotchDesign.TextSecondaryBrush,
            FontFamily = NotchDesign.UiTextFont,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        };

        private static Button CreateOptionPill(string text, bool isActive, bool disabled, Action onClick)
        {
            var tb = new TextBlock
            {
                Text = text,
                Foreground = isActive ? NotchDesign.AccentPrimaryBrush : NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var btn = NotchDesign.CreateRoundedButton(
                tb,
                disabled ? () => { } : onClick,
                background: isActive ? NotchDesign.CreateSoftAccentTileBrush(0x28) : NotchDesign.NestedCardBackgroundBrush,
                hoverBackground: disabled
                    ? NotchDesign.NestedCardBackgroundBrush
                    : (isActive ? NotchDesign.CreateSoftAccentTileBrush(0x3C) : NotchDesign.ControlHoverBrush),
                cornerRadius: 10,
                padding: new Thickness(11, 6, 11, 6));
            btn.Margin = new Thickness(0, 0, 6, 0);
            btn.Opacity = disabled ? 0.55 : 1.0;
            return btn;
        }

        private static Button CreateToggleTile(NotchIconKind iconKind, string label, bool isActive, bool disabled, Action onClick)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var icon = NotchDesign.CreateVectorIcon(
                iconKind,
                13,
                isActive ? NotchDesign.AccentPrimaryBrush : NotchDesign.TextMutedBrush,
                1.9);
            icon.Margin = new Thickness(0, 0, 7, 0);
            row.Children.Add(icon);

            row.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = isActive ? NotchDesign.TextPrimaryBrush : NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            });

            var btn = NotchDesign.CreateRoundedButton(
                row,
                disabled ? () => { } : onClick,
                background: isActive ? NotchDesign.CreateSoftAccentTileBrush(0x24) : NotchDesign.NestedCardBackgroundBrush,
                hoverBackground: disabled
                    ? NotchDesign.NestedCardBackgroundBrush
                    : (isActive ? NotchDesign.CreateSoftAccentTileBrush(0x38) : NotchDesign.ControlHoverBrush),
                cornerRadius: 10,
                padding: new Thickness(10, 8, 10, 8));
            btn.Opacity = disabled ? 0.55 : 1.0;
            return btn;
        }
    }
}
