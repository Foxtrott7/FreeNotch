using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.UI
{
    public partial class NotchWindow
    {
        private Popup? _powerPopup;

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        private void Power_Click(object sender, RoutedEventArgs e)
        {
            if (_powerPopup?.IsOpen == true)
            {
                _powerPopup.IsOpen = false;
                return;
            }

            ShowPowerMenu();
        }

        private void ShowPowerMenu()
        {
            const double menuWidth = 208;
            var stack = new StackPanel();
            var redBrush = NotchDesign.GetFrozenBrush(NotchDesign.AccentRed);
            var normalBorder = NotchDesign.GetFrozenBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));

            var popup = new Popup
            {
                AllowsTransparency = true,
                StaysOpen = false,
                PlacementTarget = PowerBtn,
                Placement = PlacementMode.Bottom,
                VerticalOffset = 6,
                HorizontalOffset = Math.Round((PowerBtn.ActualWidth > 0 ? PowerBtn.ActualWidth : 30) - menuWidth)
            };

            popup.Closed += (_, _) =>
            {
                PowerBtn.Tag = null;
                if (ReferenceEquals(_powerPopup, popup))
                    _powerPopup = null;
            };

            void CloseMenu() => popup.IsOpen = false;

            stack.Children.Add(CreatePowerMenuRow(
                L.T("notch.power.quit"),
                NotchIconKind.Close,
                NotchDesign.TextSecondaryBrush,
                NotchDesign.TextPrimaryBrush,
                () =>
                {
                    CloseMenu();
                    Application.Current?.Shutdown();
                }));

            stack.Children.Add(CreatePowerMenuRow(
                L.T("notch.power.sleep"),
                NotchIconKind.Moon,
                NotchDesign.TextSecondaryBrush,
                NotchDesign.TextPrimaryBrush,
                () =>
                {
                    CloseMenu();
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
                () =>
                {
                    CloseMenu();
                    RunShutdownCommand("/r /t 0");
                }));

            stack.Children.Add(CreateConfirmablePowerRow(
                L.T("notch.power.shutdown"),
                L.T("notch.power.shutdown_confirm"),
                NotchIconKind.Power,
                redBrush,
                redBrush,
                redBrush,
                () =>
                {
                    CloseMenu();
                    RunShutdownCommand("/s /t 0");
                }));

            popup.Child = new Border
            {
                Width = menuWidth,
                Margin = new Thickness(16), // Platz für den DropShadow innerhalb des Popup-Fensters
                Background = NotchDesign.NotchBackgroundBrush,
                BorderBrush = normalBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(NotchDesign.RadiusInner),
                Padding = new Thickness(5),
                SnapsToDevicePixels = true,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 18,
                    ShadowDepth = 4,
                    Opacity = 0.65
                },
                Child = stack
            };
            // Margin ausgleichen, damit die sichtbare Menü-Kante sauber unter PowerBtn abschließt
            popup.VerticalOffset = -10;
            popup.HorizontalOffset = Math.Round((PowerBtn.ActualWidth > 0 ? PowerBtn.ActualWidth : 30) - menuWidth - 16);

            _powerPopup = popup;
            PowerBtn.Tag = "Selected";
            popup.IsOpen = true;
        }

        private static Border CreatePowerMenuRow(
            string label,
            NotchIconKind icon,
            Brush iconBrush,
            Brush textBrush,
            Action onClick)
        {
            var row = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(9, 6, 10, 6),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconEl = NotchDesign.CreateVectorIcon(icon, 13.5, iconBrush);
            iconEl.Margin = new Thickness(0, 0, 9, 0);
            iconEl.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(iconEl, 0);

            var textEl = new TextBlock
            {
                Text = label,
                Foreground = textBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(textEl, 1);

            grid.Children.Add(iconEl);
            grid.Children.Add(textEl);
            row.Child = grid;

            row.MouseEnter += (_, _) => row.Background = NotchDesign.ControlHoverBrush;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                onClick();
            };

            return row;
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
                Background = NotchDesign.GetFrozenBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(9, 4, 5, 4),
                Margin = new Thickness(0, 1, 0, 1),
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
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 6, 0)
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
            cancelBtn.Margin = new Thickness(0, 0, 4, 0);
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
                Width = 24,
                Height = 22,
                CornerRadius = new CornerRadius(6),
                Background = backgroundBrush,
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                Child = NotchDesign.CreateVectorIcon(icon, 11.5, iconBrush, 2.0)
            };
            pill.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                onClick();
            };
            return pill;
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
