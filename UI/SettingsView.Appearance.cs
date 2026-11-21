using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.UI
{
    public partial class SettingsView
    {
        private Dictionary<string, NotchSlot> ActiveLayout => _editingHoverLayout ? _s.HoverLayout : _s.CollapsedLayout;

        private void BuildLayoutModeSelector()
        {
            if (LayoutModeHost == null) return;
            LayoutModeHost.Children.Clear();

            var (_, _, _, accent) = ResolveSelectedAccentColors();
            var textPrimary = NotchDesign.GetFrozenBrush(IsLightThemeSelected() ? Color.FromRgb(0x14, 0x14, 0x18) : Colors.White);

            foreach (var (isHover, label) in new[]
            {
                (false, L.T("settings.layout.mode_idle")),
                (true, L.T("settings.layout.mode_hover"))
            })
            {
                bool active = _editingHoverLayout == isHover;
                var accentBrush = NotchDesign.GetFrozenBrush(accent);
                var pill = new Border
                {
                    CornerRadius = new CornerRadius(NotchDesign.RadiusControl),
                    Padding = new Thickness(14, 7, 14, 7),
                    Margin = new Thickness(0, 0, 8, 0),
                    Cursor = Cursors.Hand,
                    BorderThickness = new Thickness(1),
                    Background = active ? accentBrush : (Brush)Resources["ControlBg"],
                    BorderBrush = active ? accentBrush : (Brush)Resources["ControlBorder"],
                    Child = new TextBlock
                    {
                        Text = label,
                        Foreground = active ? Brushes.White : textPrimary,
                        FontSize = 12.5,
                        FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium
                    }
                };

                bool targetHover = isHover;
                pill.MouseLeftButtonUp += (_, _) =>
                {
                    if (_editingHoverLayout == targetHover) return;
                    _editingHoverLayout = targetHover;
                    BuildLayoutModeSelector();
                    BuildLayoutRows();
                    UpdateLayoutPreview();
                };

                LayoutModeHost.Children.Add(pill);
            }

            if (LayoutHintText != null)
            {
                LayoutHintText.Text = _editingHoverLayout
                    ? L.T("settings.layout.hint_hover")
                    : L.T("settings.layout.hint_idle");
            }
        }

        private void BuildLayoutRows()
        {
            if (LayoutRowsHost == null) return;
            LayoutRowsHost.Children.Clear();

            var (_, _, _, accent) = ResolveSelectedAccentColors();
            bool isLight = IsLightThemeSelected();
            var textPrimary = NotchDesign.GetFrozenBrush(isLight ? Color.FromRgb(0x14, 0x14, 0x18) : Colors.White);
            var textSecondary = NotchDesign.GetFrozenBrush(isLight ? Color.FromRgb(0x63, 0x63, 0x6E) : Color.FromRgb(0x8E, 0x8E, 0x93));
            var accentBrush = NotchDesign.GetFrozenBrush(accent);
            var layout = ActiveLayout;

            var slots = new (NotchSlot Slot, string Label)[]
            {
                (NotchSlot.Off, L.T("settings.layout.slot_off")),
                (NotchSlot.Left, L.T("settings.layout.slot_left")),
                (NotchSlot.Center, L.T("settings.layout.slot_center")),
                (NotchSlot.Right, L.T("settings.layout.slot_right"))
            };

            for (int i = 0; i < NotchInfo.All.Count; i++)
            {
                var def = NotchInfo.All[i];
                var current = layout.TryGetValue(def.Id, out var s) ? s : NotchSlot.Off;

                var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                left.Children.Add(NotchDesign.CreateVectorIcon(
                    def.Icon,
                    14,
                    current != NotchSlot.Off ? accentBrush : textSecondary,
                    1.9));

                var labelStack = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                labelStack.Children.Add(new TextBlock
                {
                    Text = def.LocalizedLabel,
                    Foreground = textPrimary,
                    FontSize = 12.5,
                    FontWeight = current != NotchSlot.Off ? FontWeights.SemiBold : FontWeights.Normal
                });
                if (def.Id == NotchInfo.Weather && !_s.OptInWeather)
                {
                    var optInRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
                    optInRow.Children.Add(new TextBlock
                    {
                        Text = L.T("settings.layout.optin_required"),
                        Foreground = textSecondary,
                        FontSize = 10.5,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    var enableLink = new TextBlock
                    {
                        Text = "· " + L.T("settings.layout.enable_weather_now"),
                        Foreground = accentBrush,
                        FontSize = 10.5,
                        FontWeight = FontWeights.SemiBold,
                        Cursor = Cursors.Hand,
                        Margin = new Thickness(5, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    enableLink.MouseLeftButtonUp += (_, _) =>
                    {
                        _s.OptInWeather = true;
                        if (OptInWeatherBox != null) OptInWeatherBox.IsChecked = true;
                        _ = WeatherService.Instance.RefreshIfNeededAsync();
                        BuildLayoutRows();
                        UpdateLayoutPreview();
                    };
                    optInRow.Children.Add(enableLink);
                    labelStack.Children.Add(optInRow);
                }
                left.Children.Add(labelStack);
                Grid.SetColumn(left, 0);
                row.Children.Add(left);

                var segWrap = new Border
                {
                    Background = (Brush)Resources["ControlBg"],
                    BorderBrush = (Brush)Resources["ControlBorder"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(NotchDesign.RadiusControl),
                    Padding = new Thickness(2),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var segPanel = new StackPanel { Orientation = Orientation.Horizontal };

                foreach (var (slot, slotLabel) in slots)
                {
                    bool selected = current == slot;
                    var seg = new Border
                    {
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(9, 4, 9, 4),
                        Cursor = Cursors.Hand,
                        Background = selected ? accentBrush : Brushes.Transparent,
                        Child = new TextBlock
                        {
                            Text = slotLabel,
                            FontSize = 11.5,
                            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Medium,
                            Foreground = selected ? Brushes.White : textSecondary
                        }
                    };

                    string infoId = def.Id;
                    NotchSlot targetSlot = slot;
                    seg.MouseLeftButtonUp += (_, _) =>
                    {
                        if (targetSlot == NotchSlot.Off)
                            layout.Remove(infoId);
                        else
                            layout[infoId] = targetSlot;

                        BuildLayoutRows();
                        UpdateLayoutPreview();
                    };

                    segPanel.Children.Add(seg);
                }

                segWrap.Child = segPanel;
                Grid.SetColumn(segWrap, 1);
                row.Children.Add(segWrap);
                LayoutRowsHost.Children.Add(row);

                if (i < NotchInfo.All.Count - 1)
                {
                    LayoutRowsHost.Children.Add(new Border
                    {
                        Height = 1,
                        Background = (Brush)Resources["DividerBg"],
                        Margin = new Thickness(0, 3, 0, 3)
                    });
                }
            }
        }

        private void UpdateLayoutPreview()
        {
            if (LayoutPreviewPill == null || LayoutPreviewHost == null || LayoutPreviewEmpty == null) return;

            var view = NotchLayoutView.Create(ActiveLayout);
            LayoutPreviewHost.Content = view;
            LayoutPreviewEmpty.Visibility = view == null ? Visibility.Visible : Visibility.Collapsed;

            bool isLight = IsLightThemeSelected();
            LayoutPreviewPill.Background = NotchDesign.GetFrozenBrush(isLight
                ? Color.FromRgb(0xF5, 0xF5, 0xF7)
                : Color.FromRgb(0x00, 0x00, 0x00));

            double targetH = _editingHoverLayout ? 40 : 32;
            double minW = _editingHoverLayout ? 212 : 172;
            double targetW = view != null
                ? Math.Clamp(view.Width + (_editingHoverLayout ? 56 : 48), minW, 460)
                : minW;
            LayoutPreviewPill.CornerRadius = new CornerRadius(targetH / 2.0);

            UpdateLayoutZoneSummary();

            if (NotchDesign.EffectiveAnimationsEnabled)
            {
                var dur = TimeSpan.FromMilliseconds(NotchDesign.MicroTransitionMs);
                var ease = NotchDesign.CreateEaseOut();
                var wAnim = new DoubleAnimation(targetW, dur) { EasingFunction = ease };
                var hAnim = new DoubleAnimation(targetH, dur) { EasingFunction = ease };
                Timeline.SetDesiredFrameRate(wAnim, Math.Clamp(_s.TargetFrameRate, 60, 240));
                Timeline.SetDesiredFrameRate(hAnim, Math.Clamp(_s.TargetFrameRate, 60, 240));
                LayoutPreviewPill.BeginAnimation(WidthProperty, wAnim);
                LayoutPreviewPill.BeginAnimation(HeightProperty, hAnim);
            }
            else
            {
                LayoutPreviewPill.BeginAnimation(WidthProperty, null);
                LayoutPreviewPill.BeginAnimation(HeightProperty, null);
                LayoutPreviewPill.Width = targetW;
                LayoutPreviewPill.Height = targetH;
            }
        }

        private void UpdateLayoutZoneSummary()
        {
            if (LayoutZoneSummaryGrid == null) return;
            LayoutZoneSummaryGrid.Children.Clear();

            var layout = ActiveLayout;
            bool isLight = IsLightThemeSelected();
            var textPrimary = NotchDesign.GetFrozenBrush(isLight ? Color.FromRgb(0x14, 0x14, 0x18) : Colors.White);
            var textSecondary = NotchDesign.GetFrozenBrush(isLight ? Color.FromRgb(0x63, 0x63, 0x6E) : Color.FromRgb(0x8E, 0x8E, 0x93));

            var zones = new (NotchSlot Slot, string Title, int Col)[]
            {
                (NotchSlot.Left, L.T("settings.layout.slot_left"), 0),
                (NotchSlot.Center, L.T("settings.layout.slot_center"), 2),
                (NotchSlot.Right, L.T("settings.layout.slot_right"), 4)
            };

            foreach (var (slot, title, col) in zones)
            {
                var labels = NotchInfo.All
                    .Where(d => layout.TryGetValue(d.Id, out var s) && s == slot)
                    .Select(d => d.LocalizedLabel)
                    .ToList();

                var box = new Border
                {
                    Background = (Brush)Resources["ControlBg"],
                    BorderBrush = (Brush)Resources["ControlBorder"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(NotchDesign.RadiusControl),
                    Padding = new Thickness(10, 6, 10, 6)
                };

                var stack = new StackPanel();
                stack.Children.Add(new TextBlock
                {
                    Text = title,
                    Foreground = textSecondary,
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold
                });
                stack.Children.Add(new TextBlock
                {
                    Text = labels.Count > 0 ? string.Join(" · ", labels) : L.T("settings.layout.zone_none"),
                    Foreground = labels.Count > 0 ? textPrimary : textSecondary,
                    FontSize = 11.5,
                    FontWeight = labels.Count > 0 ? FontWeights.SemiBold : FontWeights.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 1, 0, 0)
                });
                box.Child = stack;
                Grid.SetColumn(box, col);
                LayoutZoneSummaryGrid.Children.Add(box);
            }
        }

        private void BuildAccentSwatches()
        {
            AccentSwatchPanel.Children.Clear();
            bool isLight = IsLightThemeSelected();
            var selectedBorder = isLight ? NotchDesign.GetFrozenBrush(Color.FromRgb(0x14, 0x14, 0x18)) : Brushes.White;
            var unselectedBorder = NotchDesign.GetFrozenBrush(isLight
                ? Color.FromArgb(0x24, 0x00, 0x00, 0x00)
                : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

            foreach (var preset in NotchDesign.AccentPresets)
            {
                bool selected = !_s.UseCustomAccent && string.Equals(_s.AccentThemeId, preset.Id, StringComparison.OrdinalIgnoreCase);
                var swatch = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(13),
                    Margin = new Thickness(0, 0, 8, 8),
                    Cursor = Cursors.Hand,
                    ToolTip = preset.Name,
                    Background = new LinearGradientBrush(preset.Start, preset.End, new Point(0, 0), new Point(1, 1)),
                    BorderThickness = new Thickness(selected ? 2.5 : 1),
                    BorderBrush = selected ? selectedBorder : unselectedBorder
                };
                string id = preset.Id;
                swatch.MouseLeftButtonUp += (_, _) =>
                {
                    _s.AccentThemeId = id;
                    _s.UseCustomAccent = false;
                    UseCustomAccentBox.IsChecked = false;
                    BuildAccentSwatches();
                    UpdateAccentPreview();
                    ApplyWindowTheme();
                    UpdatePositionPickerVisuals();
                };
                AccentSwatchPanel.Children.Add(swatch);
            }
        }

        private void CustomAccent_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _s.UseCustomAccent = UseCustomAccentBox.IsChecked == true;
            BuildAccentSwatches();
            UpdateAccentPreview();
            ApplyWindowTheme();
            UpdatePositionPickerVisuals();
        }

        private void CustomColorHex_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            UpdateAccentPreview();
            ApplyWindowTheme();
            UpdatePositionPickerVisuals();
        }

        private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || ThemeBox.SelectedIndex < 0) return;
            _s.Theme = (NotchTheme)ThemeBox.SelectedIndex;
            ApplyWindowTheme();
            BuildAccentSwatches();
            UpdatePositionPickerVisuals();
        }

        private bool IsLightThemeSelected()
        {
            var theme = ThemeBox.SelectedIndex >= 0 ? (NotchTheme)ThemeBox.SelectedIndex : _s.Theme;
            return theme is NotchTheme.Light or NotchTheme.LightGlass;
        }

        private (Color Start, Color Mid, Color End, Color Primary) ResolveSelectedAccentColors()
        {
            bool custom = UseCustomAccentBox.IsChecked == true;
            if (custom)
            {
                if (!NotchDesign.TryParseHexColor(CustomStartBox.Text, out var start))
                    start = Color.FromRgb(0x28, 0x22, 0xA8);
                if (!NotchDesign.TryParseHexColor(CustomEndBox.Text, out var end))
                    end = Color.FromRgb(0x93, 0x33, 0xEA);
                var mid = NotchDesign.BlendColor(start, end, 0.5);
                var primary = NotchDesign.BlendColor(start, end, 0.65);
                return (start, mid, end, primary);
            }

            var preset = NotchDesign.AccentPresets.FirstOrDefault(p =>
                string.Equals(p.Id, _s.AccentThemeId, StringComparison.OrdinalIgnoreCase)) ?? NotchDesign.AccentPresets[1];
            return (preset.Start, preset.Mid, preset.End, preset.Primary);
        }

        private void ApplyWindowTheme()
        {
            var theme = ThemeBox.SelectedIndex >= 0 ? (NotchTheme)ThemeBox.SelectedIndex : _s.Theme;
            var (start, mid, end, accent) = ResolveSelectedAccentColors();
            bool isLight = theme is NotchTheme.Light or NotchTheme.LightGlass;

            // Helle Themes bekommen eine leicht abgedunkelte Hover-Variante, dunkle eine aufgehellte
            var accentHover = isLight
                ? NotchDesign.BlendColor(accent, Colors.Black, 0.14)
                : NotchDesign.BlendColor(accent, Colors.White, 0.16);
            double luminance = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255.0;
            var accentFg = luminance > 0.62 ? Color.FromRgb(0x14, 0x14, 0x18) : Colors.White;

            switch (theme)
            {
                case NotchTheme.DarkGlass:
                    SetBrush("WinBg", Color.FromRgb(0x0B, 0x0B, 0x10));
                    SetBrush("SidebarBg", Color.FromRgb(0x11, 0x11, 0x18));
                    SetBrush("SidebarBorder", Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
                    SetBrush("CardBg", Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
                    SetBrush("CardBorder", Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
                    SetBrush("ControlBg", Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
                    SetBrush("ControlHoverBg", Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
                    SetBrush("ControlBorder", Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
                    SetBrush("TextPrimary", Colors.White);
                    SetBrush("TextSecondary", Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
                    SetBrush("NavHoverBg", Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
                    SetBrush("NavActiveBg", Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
                    SetBrush("SwitchTrackBg", Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
                    SetBrush("SwitchTrackHoverBg", Color.FromArgb(0x42, 0xFF, 0xFF, 0xFF));
                    SetBrush("SwitchKnobBg", Color.FromRgb(0xE4, 0xE4, 0xE7));
                    SetBrush("DividerBg", Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
                    SetBrush("PopupBg", Color.FromRgb(0x1C, 0x1C, 0x24));
                    SetBrush("PopupBorder", Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
                    SetBrush("SliderTrackBg", Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
                    SetBrush("MonitorPreviewBg", Color.FromRgb(0x0E, 0x0E, 0x14));
                    SetBrush("MonitorPreviewBorder", Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
                    break;

                case NotchTheme.Light:
                    SetBrush("WinBg", Color.FromRgb(0xF5, 0xF5, 0xF7));
                    SetBrush("SidebarBg", Color.FromRgb(0xEC, 0xEC, 0xF0));
                    SetBrush("SidebarBorder", Color.FromArgb(0x14, 0x00, 0x00, 0x00));
                    SetBrush("CardBg", Colors.White);
                    SetBrush("CardBorder", Color.FromArgb(0x12, 0x00, 0x00, 0x00));
                    SetBrush("ControlBg", Color.FromRgb(0xE8, 0xE8, 0xEE));
                    SetBrush("ControlHoverBg", Color.FromRgb(0xDC, 0xDC, 0xE4));
                    SetBrush("ControlBorder", Color.FromArgb(0x1C, 0x00, 0x00, 0x00));
                    SetBrush("TextPrimary", Color.FromRgb(0x14, 0x14, 0x18));
                    SetBrush("TextSecondary", Color.FromRgb(0x63, 0x63, 0x6E));
                    SetBrush("NavHoverBg", Color.FromArgb(0x0E, 0x00, 0x00, 0x00));
                    SetBrush("NavActiveBg", Color.FromArgb(0x16, 0x00, 0x00, 0x00));
                    SetBrush("SwitchTrackBg", Color.FromRgb(0xC8, 0xC8, 0xD2));
                    SetBrush("SwitchTrackHoverBg", Color.FromRgb(0xB8, 0xB8, 0xC4));
                    SetBrush("SwitchKnobBg", Colors.White);
                    SetBrush("DividerBg", Color.FromArgb(0x14, 0x00, 0x00, 0x00));
                    SetBrush("PopupBg", Colors.White);
                    SetBrush("PopupBorder", Color.FromArgb(0x1E, 0x00, 0x00, 0x00));
                    SetBrush("SliderTrackBg", Color.FromRgb(0xD4, 0xD4, 0xDE));
                    SetBrush("MonitorPreviewBg", Color.FromRgb(0xE6, 0xE6, 0xEC));
                    SetBrush("MonitorPreviewBorder", Color.FromArgb(0x22, 0x00, 0x00, 0x00));
                    break;

                case NotchTheme.LightGlass:
                    SetBrush("WinBg", Color.FromRgb(0xEE, 0xEE, 0xF4));
                    SetBrush("SidebarBg", Color.FromRgb(0xE4, 0xE4, 0xEC));
                    SetBrush("SidebarBorder", Color.FromArgb(0x16, 0x00, 0x00, 0x00));
                    SetBrush("CardBg", Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF));
                    SetBrush("CardBorder", Color.FromArgb(0x16, 0x00, 0x00, 0x00));
                    SetBrush("ControlBg", Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
                    SetBrush("ControlHoverBg", Colors.White);
                    SetBrush("ControlBorder", Color.FromArgb(0x1E, 0x00, 0x00, 0x00));
                    SetBrush("TextPrimary", Color.FromRgb(0x14, 0x14, 0x18));
                    SetBrush("TextSecondary", Color.FromRgb(0x52, 0x52, 0x5E));
                    SetBrush("NavHoverBg", Color.FromArgb(0x10, 0x00, 0x00, 0x00));
                    SetBrush("NavActiveBg", Color.FromArgb(0x18, 0x00, 0x00, 0x00));
                    SetBrush("SwitchTrackBg", Color.FromRgb(0xC2, 0xC2, 0xCE));
                    SetBrush("SwitchTrackHoverBg", Color.FromRgb(0xB2, 0xB2, 0xC0));
                    SetBrush("SwitchKnobBg", Colors.White);
                    SetBrush("DividerBg", Color.FromArgb(0x16, 0x00, 0x00, 0x00));
                    SetBrush("PopupBg", Color.FromRgb(0xF6, 0xF6, 0xFA));
                    SetBrush("PopupBorder", Color.FromArgb(0x22, 0x00, 0x00, 0x00));
                    SetBrush("SliderTrackBg", Color.FromRgb(0xCC, 0xCC, 0xD8));
                    SetBrush("MonitorPreviewBg", Color.FromRgb(0xDF, 0xDF, 0xE8));
                    SetBrush("MonitorPreviewBorder", Color.FromArgb(0x24, 0x00, 0x00, 0x00));
                    break;

                default: // Dark (Notchify OLED)
                    SetBrush("WinBg", Color.FromRgb(0x00, 0x00, 0x00));
                    SetBrush("SidebarBg", Color.FromRgb(0x09, 0x09, 0x0B));
                    SetBrush("SidebarBorder", Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
                    SetBrush("CardBg", Color.FromRgb(0x11, 0x11, 0x14));
                    SetBrush("CardBorder", Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF));
                    SetBrush("ControlBg", Color.FromRgb(0x18, 0x18, 0x1D));
                    SetBrush("ControlHoverBg", Color.FromRgb(0x24, 0x24, 0x2C));
                    SetBrush("ControlBorder", Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
                    SetBrush("TextPrimary", Colors.White);
                    SetBrush("TextSecondary", Color.FromRgb(0x8E, 0x8E, 0x93));
                    SetBrush("NavHoverBg", Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
                    SetBrush("NavActiveBg", Color.FromRgb(0x1E, 0x1E, 0x24));
                    SetBrush("SwitchTrackBg", Color.FromRgb(0x2C, 0x2C, 0x36));
                    SetBrush("SwitchTrackHoverBg", Color.FromRgb(0x3A, 0x3A, 0x48));
                    SetBrush("SwitchKnobBg", Color.FromRgb(0xE4, 0xE4, 0xE7));
                    SetBrush("DividerBg", Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
                    SetBrush("PopupBg", Color.FromRgb(0x18, 0x18, 0x1D));
                    SetBrush("PopupBorder", Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
                    SetBrush("SliderTrackBg", Color.FromRgb(0x2C, 0x2C, 0x36));
                    SetBrush("MonitorPreviewBg", Color.FromRgb(0x0A, 0x0A, 0x0D));
                    SetBrush("MonitorPreviewBorder", Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
                    break;
            }

            SetBrush("Accent", accent);
            SetBrush("AccentHover", accentHover);
            SetBrush("AccentForeground", accentFg);
            var grad = new LinearGradientBrush(
                new GradientStopCollection { new(start, 0.0), new(mid, 0.55), new(end, 1.0) },
                new Point(0, 0),
                new Point(1, 0));
            grad.Freeze();
            Resources["AccentGradient"] = grad;

            BuildNavRail();
            BuildLayoutModeSelector();
            BuildLayoutRows();
            UpdateLayoutPreview();
        }

        private void SetBrush(string key, Color color) => Resources[key] = NotchDesign.GetFrozenBrush(color);

        private void UpdateAccentPreview()
        {
            bool custom = UseCustomAccentBox.IsChecked == true;
            CustomAccentEditorPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;

            var (start, mid, end, _) = ResolveSelectedAccentColors();

            CustomStartDot.Background = NotchDesign.GetFrozenBrush(start);
            CustomEndDot.Background = NotchDesign.GetFrozenBrush(end);
            var previewGrad = new LinearGradientBrush(
                new GradientStopCollection { new(start, 0.0), new(mid, 0.55), new(end, 1.0) },
                new Point(0, 0),
                new Point(1, 1));
            previewGrad.Freeze();
            GradientPreviewBar.Background = previewGrad;
        }
    }
}
