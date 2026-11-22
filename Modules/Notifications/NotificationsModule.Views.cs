using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Windows.UI.Notifications.Management;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.Notifications
{
    public partial class NotificationsModule
    {
        // ---------- Notch Views ----------

        public override FrameworkElement? CreateLiveView()
        {
            if (_bannerItem == null) return null;

            var grid = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = CreateAppBadge(_bannerItem, 22);
            badge.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(badge, 0);
            grid.Children.Add(badge);

            string summary = string.IsNullOrWhiteSpace(_bannerItem.Body)
                ? _bannerItem.Title
                : $"{_bannerItem.Title}: {_bannerItem.Body}";

            var textBlock = new TextBlock
            {
                Text = summary,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 205
            };
            Grid.SetColumn(textBlock, 1);
            grid.Children.Add(textBlock);

            var dot = new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(3.5),
                Background = NotchDesign.AccentPrimaryBrush,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(dot, 2);
            grid.Children.Add(dot);

            return grid;
        }

        public override FrameworkElement CreatePreviewView()
        {
            if (_accessStatus != UserNotificationListenerAccessStatus.Allowed)
            {
                return CreateSimplePreview(NotchIconKind.Bell, _listenerUnavailable
                    ? L.T("notifications.unavailable")
                    : L.T("notifications.allow_access"));
            }

            if (_history.Count == 0)
                return CreateSimplePreview(NotchIconKind.Bell, L.T("notifications.none_new"));

            var latest = _history[0];
            var grid = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = CreateAppBadge(latest, 24);
            badge.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(badge, 0);
            grid.Children.Add(badge);

            var textStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 185
            };
            textStack.Children.Add(new TextBlock
            {
                Text = latest.Title,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            string sub = string.IsNullOrWhiteSpace(latest.Body) ? latest.AppName : latest.Body;
            textStack.Children.Add(new TextBlock
            {
                Text = sub,
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(textStack, 1);
            grid.Children.Add(textStack);

            if (_history.Count > 1)
            {
                var countPill = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x38, 0x60, 0xA5, 0xFA)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = $"+{_history.Count - 1}",
                        Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xC5, 0xFD)),
                        FontFamily = UiTextFont,
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold
                    }
                };
                Grid.SetColumn(countPill, 2);
                grid.Children.Add(countPill);
            }

            return grid;
        }

        public override FrameworkElement CreateExpandedView()
        {
            // Beim Öffnen einmalig aktualisieren, falls Events bei unpackaged App ausblieben
            if (_accessStatus == UserNotificationListenerAccessStatus.Allowed)
                _ = RefreshNotificationsAsync(suppressBanner: false);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Kopfzeile im Notchify-Stil (Icon + Titel + Zähler-Pill + kompakte runde Aktionen)
            var header = new DockPanel { LastChildFill = false, Margin = new Thickness(2, 2, 2, 12) };

            var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var bellIcon = NotchDesign.CreateVectorIcon(NotchIconKind.Bell, 16, NotchDesign.AccentPrimaryBrush, 2.15);
            bellIcon.Margin = new Thickness(0, 0, 8, 0);
            titleStack.Children.Add(bellIcon);

            titleStack.Children.Add(new TextBlock
            {
                Text = L.T("module.notifications"),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 15.5,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });

            _expandedCountBadge = new TextBlock
            {
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _expandedCountPill = new Border
            {
                Background = NotchDesign.CreateSoftAccentTileBrush(0x32),
                CornerRadius = new CornerRadius(11),
                MinWidth = 22,
                Height = 22,
                Padding = new Thickness(7, 0, 7, 0),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = _expandedCountBadge
            };
            titleStack.Children.Add(_expandedCountPill);
            DockPanel.SetDock(titleStack, Dock.Left);
            header.Children.Add(titleStack);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            _expandedSoundBtn = NotchDesign.CreateRoundedButton(
                BuildSoundButtonContent(SettingsService.Current.NotificationSoundEnabled),
                ToggleNotificationSound,
                background: Brushes.Transparent,
                hoverBackground: NotchDesign.TabActiveBackgroundBrush,
                cornerRadius: 14,
                padding: new Thickness(0),
                tooltip: L.T("notifications.sound_toggle"));
            _expandedSoundBtn.Width = 28;
            _expandedSoundBtn.Height = 28;
            _expandedSoundBtn.Margin = new Thickness(0, 0, 4, 0);
            actions.Children.Add(_expandedSoundBtn);

            var dndBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Moon, 14.5, NotchDesign.TextPrimaryBrush, 2.15),
                OpenWindowsBannerSettings,
                background: Brushes.Transparent,
                hoverBackground: NotchDesign.TabActiveBackgroundBrush,
                cornerRadius: 14,
                padding: new Thickness(0),
                tooltip: L.T("notifications.dnd_tooltip"));
            dndBtn.Width = 28;
            dndBtn.Height = 28;
            dndBtn.Margin = new Thickness(0, 0, 4, 0);
            actions.Children.Add(dndBtn);

            var clearBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Trash, 14.5, NotchDesign.TextPrimaryBrush, 2.15),
                ClearHistory,
                background: Brushes.Transparent,
                hoverBackground: NotchDesign.TabActiveBackgroundBrush,
                foreground: NotchDesign.TextPrimaryBrush,
                cornerRadius: 14,
                padding: new Thickness(0),
                tooltip: L.T("notifications.clear_history"));
            clearBtn.Width = 28;
            clearBtn.Height = 28;
            actions.Children.Add(clearBtn);

            DockPanel.SetDock(actions, Dock.Right);
            header.Children.Add(actions);

            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // Scrollbarer Inhalt
            _expandedContentPanel = new StackPanel();
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _expandedContentPanel
            };
            NotchDesign.ApplySlimScrollBarStyle(scroll);
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            RefreshExpandedContent();
            return new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(4, 2, 4, 2),
                Child = root
            };
        }

        private static UIElement BuildSoundButtonContent(bool enabled)
        {
            return NotchDesign.CreateVectorIcon(
                enabled ? NotchIconKind.VolumeOn : NotchIconKind.VolumeOff,
                14.5,
                enabled ? NotchDesign.TextPrimaryBrush : NotchDesign.TextMutedBrush,
                2.15);
        }

        private void RefreshExpandedContent()
        {
            if (_expandedContentPanel == null) return;

            if (_expandedCountBadge != null && _expandedCountPill != null)
            {
                _expandedCountBadge.Text = _history.Count > 0 ? $"{_history.Count}" : string.Empty;
                _expandedCountPill.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            if (_expandedSoundBtn != null)
                _expandedSoundBtn.Content = BuildSoundButtonContent(SettingsService.Current.NotificationSoundEnabled);

            _expandedContentPanel.Children.Clear();

            if (_accessStatus != UserNotificationListenerAccessStatus.Allowed || _listenerUnavailable)
            {
                _expandedContentPanel.Children.Add(BuildPermissionCard());
                return;
            }

            if (_history.Count == 0)
            {
                _expandedContentPanel.Children.Add(new TextBlock
                {
                    Text = L.T("notifications.empty_history"),
                    Foreground = NotchDesign.TextMutedBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 12,
                    Margin = new Thickness(0, 14, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                return;
            }

            foreach (var entry in _history)
            {
                _expandedContentPanel.Children.Add(BuildNotificationCard(entry));
            }
        }

        private Border BuildPermissionCard()
        {
            var card = NotchDesign.CreateCard(16, new Thickness(15, 13, 15, 13));

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = _listenerUnavailable
                    ? L.T("notifications.listener_unavailable")
                    : L.T("notifications.access_required"),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold
            });

            string desc = _listenerUnavailable
                ? L.F("notifications.listener_error", _statusError ?? L.T("notifications.unknown_error"))
                : L.T("notifications.access_hint");
            stack.Children.Add(new TextBlock
            {
                Text = desc,
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 10)
            });

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
            var reqBtn = CreateChipButton(L.T("notifications.request_perm"), out _, RequestPermissionAsync, accent: true);
            reqBtn.Margin = new Thickness(0, 0, 8, 0);
            btnRow.Children.Add(reqBtn);

            var sysBtn = CreateChipButton(L.T("notifications.win_settings"), out _, OpenWindowsNotificationSettings);
            btnRow.Children.Add(sysBtn);

            stack.Children.Add(btnRow);
            card.Child = stack;
            return card;
        }

        private Border BuildNotificationCard(NotificationEntry entry)
        {
            Brush hoverBg = NotchDesign.IsLight
                ? NotchDesign.ControlHoverBrush
                : new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));

            var card = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8, 8, 8, 8),
                Margin = new Thickness(0, 0, 0, 4)
            };
            card.MouseEnter += (_, _) => card.Background = hoverBg;
            card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var badge = CreateAppBadge(entry, 38);
            badge.CornerRadius = new CornerRadius(11);
            badge.VerticalAlignment = VerticalAlignment.Top;
            badge.Margin = new Thickness(0, 1, 11, 0);
            Grid.SetColumn(badge, 0);
            grid.Children.Add(badge);

            var content = new StackPanel();
            var metaRow = new DockPanel { LastChildFill = false };

            var appLabel = new TextBlock
            {
                Text = entry.AppName,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(appLabel, Dock.Left);
            metaRow.Children.Add(appLabel);

            var rightMeta = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            rightMeta.Children.Add(new TextBlock
            {
                Text = FormatRelativeTime(entry.Timestamp),
                Foreground = NotchDesign.TextMutedBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10.5,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            var muteBtn = CreateIconAction(NotchIconKind.VolumeOff, L.T("notifications.mute_app"), () => ToggleAppBlocked(entry.FilterKey));
            muteBtn.Margin = new Thickness(0, 0, 4, 0);
            rightMeta.Children.Add(muteBtn);

            rightMeta.Children.Add(CreateIconAction(NotchIconKind.Close, L.T("notifications.remove_entry"), () => RemoveHistoryItem(entry)));

            DockPanel.SetDock(rightMeta, Dock.Right);
            metaRow.Children.Add(rightMeta);
            content.Children.Add(metaRow);

            content.Children.Add(new TextBlock
            {
                Text = entry.Title,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            });

            if (!string.IsNullOrWhiteSpace(entry.Body))
            {
                content.Children.Add(new TextBlock
                {
                    Text = entry.Body,
                    Foreground = NotchDesign.TextSecondaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 36,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 1, 0, 0)
                });
            }

            Grid.SetColumn(content, 1);
            grid.Children.Add(content);
            card.Child = grid;
            return card;
        }

        // ---------- UI-Helper ----------

        private static FrameworkElement CreateSimplePreview(NotchIconKind kind, string text)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var icon = NotchDesign.CreateVectorIcon(kind, 13, NotchDesign.AccentPrimaryBrush);
            icon.Margin = new Thickness(0, 0, 8, 0);
            stack.Children.Add(icon);
            stack.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            return stack;
        }

        private static Border CreateAppBadge(NotificationEntry entry, double size)
        {
            var badge = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(Math.Max(8, size * 0.3)),
                Background = NotchDesign.TabActiveBackgroundBrush,
                BorderThickness = new Thickness(0),
                ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (entry.AppIcon != null)
            {
                badge.Child = new Image
                {
                    Source = entry.AppIcon,
                    Width = size,
                    Height = size,
                    Stretch = Stretch.UniformToFill,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            else if (!string.IsNullOrWhiteSpace(entry.AppName))
            {
                string initial = entry.AppName.Substring(0, 1).ToUpperInvariant();
                badge.Child = new TextBlock
                {
                    Text = initial,
                    Foreground = NotchDesign.TextPrimaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = Math.Max(11, size * 0.48),
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            else
            {
                badge.Child = NotchDesign.CreateVectorIcon(NotchIconKind.Bell, Math.Max(12, size * 0.54), NotchDesign.TextPrimaryBrush, 2.15);
            }

            return badge;
        }

        private static Button CreateChipButton(string label, out TextBlock textBlock, Action onClick, bool accent = false)
        {
            textBlock = new TextBlock
            {
                Text = label,
                Foreground = accent ? NotchDesign.AccentPrimaryBrush : NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10.5,
                FontWeight = FontWeights.Medium
            };

            var bg = accent
                ? NotchDesign.CreateSoftAccentTileBrush(0x28)
                : NotchDesign.TabActiveBackgroundBrush;
            var hover = accent
                ? NotchDesign.CreateSoftAccentTileBrush(0x40)
                : NotchDesign.ControlHoverBrush;

            return NotchDesign.CreateRoundedButton(
                textBlock,
                onClick,
                background: bg,
                hoverBackground: hover,
                foreground: accent ? Brushes.White : NotchDesign.TextPrimaryBrush,
                cornerRadius: 14,
                padding: new Thickness(11, 0, 11, 0));
        }

        private static Button CreateIconAction(NotchIconKind kind, string tooltip, Action onClick)
        {
            var btn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(kind, 12.5, NotchDesign.TextPrimaryBrush, 2.15),
                onClick,
                background: Brushes.Transparent,
                hoverBackground: NotchDesign.TabActiveBackgroundBrush,
                cornerRadius: 12,
                padding: new Thickness(0),
                tooltip: tooltip);
            btn.Width = 24;
            btn.Height = 24;
            return btn;
        }

        private static string FormatRelativeTime(DateTimeOffset ts)
        {
            var diff = DateTimeOffset.Now - ts;
            if (diff.TotalSeconds < 45) return L.T("common.just_now");
            if (diff.TotalMinutes < 60) return L.F("common.minutes_ago", (int)diff.TotalMinutes);
            if (diff.TotalHours < 24) return ts.LocalDateTime.ToString("HH:mm");
            return ts.LocalDateTime.ToString("dd.MM. HH:mm");
        }

        private static void OpenWindowsNotificationSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:privacy-notifications") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private static void OpenWindowsBannerSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }
    }
}
