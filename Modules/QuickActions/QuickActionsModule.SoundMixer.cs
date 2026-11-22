using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.QuickActions
{
    public partial class QuickActionsModule
    {
        private DispatcherTimer? _mixerRefreshTimer;
        private bool _isDraggingMixerSlider;
        private StackPanel? _mixerListStack;

        private Border BuildSoundMixerSubView()
        {
            var root = new DockPanel();

            // Master-Lautstärke oben als ruhige Ankerzeile
            var masterCard = BuildVolumeCard();
            masterCard.Margin = new Thickness(0, 0, 0, 8);
            DockPanel.SetDock(masterCard, Dock.Top);
            root.Children.Add(masterCard);

            _mixerListStack = new StackPanel();
            PopulateMixerSessions(_mixerListStack);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _mixerListStack
            };
            NotchDesign.ApplySlimScrollBarStyle(scroll);
            root.Children.Add(scroll);

            StartMixerRefreshTimer();

            var refreshBtn = NotchDesign.CreateIconActionButton(
                NotchIconKind.Reset,
                L.T("mixer.refresh"),
                () =>
                {
                    if (_mixerListStack != null)
                        PopulateMixerSessions(_mixerListStack);
                },
                size: 28);

            return BuildSubViewShell(L.T("qa.tile.volmixer"), NotchIconKind.Layers, root, refreshBtn);
        }

        private void StartMixerRefreshTimer()
        {
            StopMixerRefreshTimer();
            _mixerRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
            _mixerRefreshTimer.Tick += (_, _) =>
            {
                if (_activeSubView != QuickActionsSubView.SoundMixer || _expandedHost?.IsLoaded != true)
                {
                    StopMixerRefreshTimer();
                    return;
                }
                if (_isDraggingMixerSlider || _mixerListStack == null) return;
                PopulateMixerSessions(_mixerListStack);
            };
            _mixerRefreshTimer.Start();
        }

        private void StopMixerRefreshTimer()
        {
            _mixerRefreshTimer?.Stop();
            _mixerRefreshTimer = null;
            _isDraggingMixerSlider = false;
        }

        private void PopulateMixerSessions(StackPanel stack)
        {
            stack.Children.Clear();
            List<AudioAppSession> sessions = AudioSessionMixerService.GetSessions();

            if (sessions.Count == 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = L.T("mixer.empty"),
                    Foreground = NotchDesign.TextSecondaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 32, 0, 0)
                });
                return;
            }

            foreach (var session in sessions)
                stack.Children.Add(BuildMixerSessionRow(session));
        }

        private Border BuildMixerSessionRow(AudioAppSession session)
        {
            var card = NotchDesign.CreateCard(NotchDesign.RadiusControl, new Thickness(10, 7, 10, 7));
            card.Background = NotchDesign.CardBackgroundBrush;
            card.Margin = new Thickness(0, 0, 0, 6);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

            bool muted = session.IsMuted;
            float currentVol = session.Volume;

            var muteIcon = NotchDesign.CreateVectorIcon(
                muted ? NotchIconKind.VolumeOff : NotchIconKind.VolumeOn,
                14,
                muted ? NotchDesign.TextMutedBrush : NotchDesign.AccentPrimaryBrush,
                2.0);

            var pctText = new TextBlock
            {
                Text = muted ? L.T("mixer.muted_short") : $"{Math.Round(currentVol * 100)}%",
                Foreground = muted ? NotchDesign.TextMutedBrush : NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            pctText.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;

            var slider = BuildAppVolumeSlider(
                muted ? 0.0 : currentVol,
                ratio =>
                {
                    currentVol = (float)ratio;
                    if (muted && ratio > 0.01)
                    {
                        muted = false;
                        AudioSessionMixerService.SetSessionMute(session.ProcessId, session.IsSystemSounds, false);
                    }
                    AudioSessionMixerService.SetSessionVolume(session.ProcessId, session.IsSystemSounds, currentVol);
                    pctText.Text = muted ? L.T("mixer.muted_short") : $"{Math.Round(currentVol * 100)}%";
                    pctText.Foreground = muted ? NotchDesign.TextMutedBrush : NotchDesign.TextSecondaryBrush;
                });

            var muteBtn = NotchDesign.CreateRoundedButton(
                muteIcon,
                () =>
                {
                    muted = !muted;
                    AudioSessionMixerService.SetSessionMute(session.ProcessId, session.IsSystemSounds, muted);
                    if (_mixerListStack != null)
                        PopulateMixerSessions(_mixerListStack);
                },
                background: Brushes.Transparent,
                hoverBackground: NotchDesign.TabActiveBackgroundBrush,
                cornerRadius: 8,
                padding: new Thickness(5),
                tooltip: L.T("qa.mute_toggle"));
            Grid.SetColumn(muteBtn, 0);
            row.Children.Add(muteBtn);

            var nameBlock = new TextBlock
            {
                Text = session.Name,
                Foreground = session.IsActive ? NotchDesign.TextPrimaryBrush : NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                FontWeight = session.IsActive ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(nameBlock, 2);
            row.Children.Add(nameBlock);

            Grid.SetColumn(slider, 4);
            row.Children.Add(slider);

            Grid.SetColumn(pctText, 6);
            row.Children.Add(pctText);

            card.Child = row;
            return card;
        }

        private Border BuildAppVolumeSlider(double initialRatio, Action<double> onChanged)
        {
            const double thumbSize = 12;
            var hitArea = new Border
            {
                Height = 22,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };

            var canvas = new Grid { VerticalAlignment = VerticalAlignment.Center };
            var track = new Border
            {
                Height = 5,
                CornerRadius = new CornerRadius(2.5),
                Background = NotchDesign.TrackBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            var fill = new Border
            {
                Height = 5,
                CornerRadius = new CornerRadius(2.5),
                Background = NotchDesign.AccentPrimaryBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            var thumb = new Border
            {
                Width = thumbSize,
                Height = thumbSize,
                CornerRadius = new CornerRadius(thumbSize / 2),
                Background = NotchDesign.TextPrimaryBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };

            canvas.Children.Add(track);
            canvas.Children.Add(fill);
            canvas.Children.Add(thumb);
            hitArea.Child = canvas;

            double currentRatio = Math.Clamp(initialRatio, 0.0, 1.0);
            void UpdateVisual(double ratio)
            {
                double w = hitArea.ActualWidth;
                if (w <= thumbSize) return;
                double clamped = Math.Clamp(ratio, 0.0, 1.0);
                fill.Width = Math.Max(thumbSize / 2, clamped * w);
                double left = Math.Clamp(clamped * (w - thumbSize), 0, w - thumbSize);
                thumb.Margin = new Thickness(left, 0, 0, 0);
            }

            hitArea.Loaded += (_, _) => UpdateVisual(currentRatio);
            hitArea.SizeChanged += (_, _) => UpdateVisual(currentRatio);

            void ApplyPointer(MouseEventArgs e)
            {
                double w = hitArea.ActualWidth;
                if (w <= 1) return;
                currentRatio = Math.Clamp(e.GetPosition(hitArea).X / w, 0.0, 1.0);
                onChanged(currentRatio);
                UpdateVisual(currentRatio);
            }

            hitArea.MouseLeftButtonDown += (_, e) =>
            {
                _isDraggingMixerSlider = true;
                hitArea.CaptureMouse();
                ApplyPointer(e);
            };
            hitArea.MouseMove += (_, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed && hitArea.IsMouseCaptured)
                    ApplyPointer(e);
            };
            hitArea.MouseLeftButtonUp += (_, _) =>
            {
                if (hitArea.IsMouseCaptured) hitArea.ReleaseMouseCapture();
                _isDraggingMixerSlider = false;
            };
            hitArea.MouseWheel += (_, e) =>
            {
                double step = e.Delta > 0 ? 0.05 : -0.05;
                currentRatio = Math.Clamp(currentRatio + step, 0.0, 1.0);
                onChanged(currentRatio);
                UpdateVisual(currentRatio);
                e.Handled = true;
            };

            return hitArea;
        }
    }
}
