using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.UI
{
    public partial class NotchWindow
    {
        private bool _isPowerMenuOpen;
        private bool _isQuitting;

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        private void Power_Click(object sender, RoutedEventArgs e)
        {
            _isOnboardingOpen = false;
            if (_state == NotchState.Expanded && _isPowerMenuOpen)
            {
                ClosePowerMenu();
                return;
            }

            _isSettingsOpen = false;
            UpdateSettingsButtonState();
            _isPowerMenuOpen = true;

            if (_state != NotchState.Expanded)
            {
                SetState(NotchState.Expanded);
                Activate();
                return;
            }

            ShowPowerMenuInHost();
            AnimateExpandedSize();
            AnimatePowerHostTransition();
            Activate();
        }

        private void ClosePowerMenu()
        {
            if (!_isPowerMenuOpen) return;
            _isPowerMenuOpen = false;
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

        private void ShowPowerMenuInHost()
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

            ExpandedHost.Content = CreatePowerMenuView();
            UpdatePowerButtonState();
            UpdateKeepExpandedCloseButton();
        }

        private void UpdatePowerButtonState()
        {
            PowerBtn.Tag = _isPowerMenuOpen ? "Selected" : null;
            PowerIconPath.Stroke = _isPowerMenuOpen ? NotchDesign.TextPrimaryBrush : NotchDesign.TabIconInactiveBrush;
        }

        private void AnimatePowerHostTransition()
        {
            if (!NotchDesign.EffectiveAnimationsEnabled) return;

            var scale = new ScaleTransform(0.95, 0.95);
            var translate = new TranslateTransform(0, -4);
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(translate);

            ExpandedHost.RenderTransformOrigin = new Point(0.5, 0.25);
            ExpandedHost.RenderTransform = group;
            ExpandedHost.BeginAnimation(OpacityProperty, null);
            ExpandedHost.Opacity = 0.0;

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (!_isPowerMenuOpen) return;
                var ease = NotchDesign.CreateSpringEase(0.42);
                var dur = TimeSpan.FromMilliseconds(NotchDesign.TabSwitchDurationMs);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateAnim(1.0, dur, ease));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateAnim(1.0, dur, ease));
                translate.BeginAnimation(TranslateTransform.YProperty, CreateAnim(0.0, dur, ease));
                ExpandedHost.BeginAnimation(OpacityProperty,
                    CreateAnim(1.0, dur, NotchDesign.CreateEaseInOut()));
            });
        }

        private UIElement CreatePowerMenuView()
        {
            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };

            var redBrush = NotchDesign.GetFrozenBrush(NotchDesign.AccentRed);

            stack.Children.Add(CreatePowerMenuRow(
                L.T("notch.power.quit"),
                NotchIconKind.Close,
                NotchDesign.TextSecondaryBrush,
                NotchDesign.TextPrimaryBrush,
                ShutdownGracefully));

            stack.Children.Add(CreatePowerMenuRow(
                L.T("notch.power.sleep"),
                NotchIconKind.Moon,
                NotchDesign.TextSecondaryBrush,
                NotchDesign.TextPrimaryBrush,
                () =>
                {
                    SetState(NotchState.Collapsed);
                    TriggerSystemSleep();
                }));

            stack.Children.Add(CreateConfirmablePowerRow(
                L.T("notch.power.restart"),
                L.T("notch.power.restart_confirm"),
                NotchIconKind.Reset,
                NotchDesign.TextSecondaryBrush,
                NotchDesign.TextPrimaryBrush,
                NotchDesign.AccentPrimaryBrush,
                () => RunShutdownCommand("/r /t 0")));

            stack.Children.Add(CreateConfirmablePowerRow(
                L.T("notch.power.shutdown"),
                L.T("notch.power.shutdown_confirm"),
                NotchIconKind.Power,
                redBrush,
                redBrush,
                redBrush,
                () => RunShutdownCommand("/s /t 0")));

            return stack;
        }

        private static Border CreatePowerMenuRow(
            string label,
            NotchIconKind icon,
            Brush iconBrush,
            Brush textBrush,
            Action onClick)
        {
            var idleBg = NotchDesign.CardBackgroundBrush;
            var hoverBg = NotchDesign.ControlHoverBrush;

            var scale = new ScaleTransform(1.0, 1.0);
            var row = new Border
            {
                Background = idleBg,
                CornerRadius = new CornerRadius(NotchDesign.RadiusControl),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconEl = NotchDesign.CreateVectorIcon(icon, 14, iconBrush);
            iconEl.Margin = new Thickness(0, 0, 10, 0);
            iconEl.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(iconEl, 0);

            var textEl = new TextBlock
            {
                Text = label,
                Foreground = textBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12.5,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(textEl, 1);

            grid.Children.Add(iconEl);
            grid.Children.Add(textEl);
            row.Child = grid;

            row.MouseEnter += (_, _) => row.Background = hoverBg;
            row.MouseLeave += (_, _) =>
            {
                row.Background = idleBg;
                AnimateRowScale(scale, 1.0, NotchDesign.ButtonReleaseDurationMs);
            };
            row.MouseLeftButtonDown += (_, _) =>
                AnimateRowScale(scale, 0.96, NotchDesign.ButtonPressDurationMs);
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                AnimateRowScale(scale, 1.0, NotchDesign.ButtonReleaseDurationMs);
                onClick();
            };

            return row;
        }

        private static void AnimateRowScale(ScaleTransform scale, double target, int durationMs)
        {
            if (!NotchDesign.EffectiveAnimationsEnabled)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = target;
                scale.ScaleY = target;
                return;
            }
            var anim = CreateAnim(target, TimeSpan.FromMilliseconds(durationMs), NotchDesign.CreateEaseOut());
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        private static UIElement CreateConfirmablePowerRow(
            string label,
            string confirmLabel,
            NotchIconKind icon,
            Brush iconBrush,
            Brush textBrush,
            Brush confirmAccentBrush,
            Action onConfirm)
        {
            var host = new Grid();
            Border? normalRow = null;
            Border? confirmRow = null;

            normalRow = CreatePowerMenuRow(label, icon, iconBrush, textBrush, () =>
            {
                if (normalRow != null) normalRow.Visibility = Visibility.Collapsed;
                if (confirmRow != null) confirmRow.Visibility = Visibility.Visible;
            });

            confirmRow = new Border
            {
                Background = NotchDesign.ControlHoverBrush,
                CornerRadius = new CornerRadius(NotchDesign.RadiusControl),
                Padding = new Thickness(12, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 6),
                Visibility = Visibility.Collapsed
            };

            var confirmGrid = new Grid();
            confirmGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            confirmGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            confirmGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var confirmText = new TextBlock
            {
                Text = confirmLabel,
                Foreground = textBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(confirmText, 0);

            var cancelBtn = CreateInlineActionPill(
                NotchIconKind.Close,
                NotchDesign.TextSecondaryBrush,
                NotchDesign.ControlHoverBrush,
                L.T("notch.power.confirm_cancel"),
                () =>
                {
                    confirmRow.Visibility = Visibility.Collapsed;
                    normalRow.Visibility = Visibility.Visible;
                });
            cancelBtn.Margin = new Thickness(0, 0, 5, 0);
            Grid.SetColumn(cancelBtn, 1);

            var yesBtn = CreateInlineActionPill(
                NotchIconKind.Check,
                Brushes.White,
                confirmAccentBrush,
                L.T("notch.power.confirm_yes"),
                onConfirm);
            Grid.SetColumn(yesBtn, 2);

            confirmGrid.Children.Add(confirmText);
            confirmGrid.Children.Add(cancelBtn);
            confirmGrid.Children.Add(yesBtn);
            confirmRow.Child = confirmGrid;

            host.Children.Add(normalRow);
            host.Children.Add(confirmRow);
            return host;
        }

        private static Border CreateInlineActionPill(
            NotchIconKind icon,
            Brush iconBrush,
            Brush backgroundBrush,
            string tooltip,
            Action onClick)
        {
            var pill = new Border
            {
                Width = 26,
                Height = 24,
                CornerRadius = new CornerRadius(7),
                Background = backgroundBrush,
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                Child = NotchDesign.CreateVectorIcon(icon, 12, iconBrush, 2.0)
            };
            pill.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                onClick();
            };
            return pill;
        }

        private void ShutdownGracefully()
        {
            if (_isQuitting) return;
            _isQuitting = true;

            if (!NotchDesign.EffectiveAnimationsEnabled)
            {
                Application.Current?.Shutdown();
                return;
            }

            SetState(NotchState.Collapsed);
            var fadeDur = TimeSpan.FromMilliseconds(NotchDesign.CollapseDurationMs);
            NotchBorder.BeginAnimation(OpacityProperty, CreateAnim(0.0, fadeDur, NotchDesign.CreateEaseInOut()));

            var quitTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(NotchDesign.CollapseDurationMs + 30)
            };
            quitTimer.Tick += (_, _) =>
            {
                quitTimer.Stop();
                Application.Current?.Shutdown();
            };
            quitTimer.Start();
        }

        private static void TriggerSystemSleep()
        {
            try
            {
                if (!SetSuspendState(false, false, false))
                    RunPowerProcess("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                RunPowerProcess("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
            }
        }

        private static void RunShutdownCommand(string arguments)
            => RunPowerProcess("shutdown.exe", arguments);

        private static void RunPowerProcess(string fileName, string arguments)
        {
            try
            {
                Process.Start(new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }
    }
}
