using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.UI
{
    /// <summary>
    /// Teil 1 der Ersteinrichtung: reiner OLED-Startscreen direkt auf dem Notch-Hintergrund
    /// mit animierter Editions-Swipe-Pille und sofortiger Live-Vorschau an der echten Notch.
    /// </summary>
    public sealed class OnboardingView : UserControl
    {
        private readonly AppSettings _draft;
        private readonly Action _onLiveApply;
        private Border? _editionThumb;
        private double _editionThumbX;
        private bool _showHuePicker;
        private Border? _customDot;
        private Border? _hueThumb;
        private TranslateTransform? _hueThumbShift;
        private double _hueT = 0.72;

        public event Action<AppSettings>? StartTourRequested;
        public event Action? ImportedOrSkipped;

        public OnboardingView(Action onLiveApply)
        {
            _onLiveApply = onLiveApply;
            _draft = SettingsService.Clone();
            _draft.Normalize();
            BuildUi();
        }

        private void BuildUi()
        {
            var root = new StackPanel { VerticalAlignment = VerticalAlignment.Stretch };

            // Kopfzeile direkt auf OLED-Schwarz
            var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titleRow.Children.Add(NotchDesign.CreateVectorIcon(NotchIconKind.Sparkle, 15, NotchDesign.AccentPrimaryBrush, 1.9));
            titleRow.Children.Add(new TextBlock
            {
                Text = "FreeNotch",
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontWeight = FontWeights.Bold,
                FontSize = 16,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            DockPanel.SetDock(titleRow, Dock.Left);
            header.Children.Add(titleRow);

            var skipBtn = NotchDesign.CreateRoundedButton(
                L.T("onboarding.skip"),
                () => NotchDesign.RunAfterPressFeedback(Skip),
                background: Brushes.Transparent,
                hoverBackground: NotchDesign.ControlBackgroundBrush,
                foreground: NotchDesign.TextSecondaryBrush,
                padding: new Thickness(10, 4, 10, 4));
            DockPanel.SetDock(skipBtn, Dock.Right);
            header.Children.Add(skipBtn);
            root.Children.Add(header);

            // 1. Edition / Gerätetyp als animierte 3-Segment-Swipe-Pille
            root.Children.Add(BuildSectionLabel(L.T("onboarding.section_edition")));
            root.Children.Add(BuildEditionSwipeBar());

            string editionHint = _draft.Profile switch
            {
                ProfileType.Work => L.T("onboarding.desc_work"),
                ProfileType.Gaming => L.T("onboarding.desc_gaming"),
                _ => L.T("onboarding.desc_complete")
            };
            var hintRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 5, 0, 10) };
            hintRow.Children.Add(NotchDesign.CreateVectorIcon(
                _draft.Profile == ProfileType.Work ? NotchIconKind.Shield : NotchIconKind.Check,
                12,
                _draft.Profile == ProfileType.Work ? new SolidColorBrush(NotchDesign.AccentGreen) : NotchDesign.AccentPrimaryBrush,
                2.0));
            hintRow.Children.Add(new TextBlock
            {
                Text = editionHint,
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.2,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            root.Children.Add(hintRow);

            // 2. Live-Basis-Anpassung direkt auf OLED (wirkt sofort an der echten Notch)
            var midGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            midGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(186) });
            midGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            midGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Links: 8 Positionen (ohne Rahmen-Kasten, feiner Monitor-Umriss)
            var posStack = new StackPanel();
            posStack.Children.Add(BuildSectionLabel(L.T("onboarding.section_pos")));
            var monitor = new Border
            {
                Height = 104,
                Margin = new Thickness(0, 6, 0, 0),
                CornerRadius = new CornerRadius(NotchDesign.RadiusInner),
                BorderBrush = NotchDesign.ControlHoverBrush,
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent,
                Padding = new Thickness(6)
            };
            var monGrid = new Grid();
            AddPosSpot(monGrid, NotchPosition.TopLeft, HorizontalAlignment.Left, VerticalAlignment.Top, 30, 10);
            AddPosSpot(monGrid, NotchPosition.TopCenter, HorizontalAlignment.Center, VerticalAlignment.Top, 40, 10);
            AddPosSpot(monGrid, NotchPosition.TopRight, HorizontalAlignment.Right, VerticalAlignment.Top, 30, 10);
            AddPosSpot(monGrid, NotchPosition.LeftCenter, HorizontalAlignment.Left, VerticalAlignment.Center, 10, 30);
            AddPosSpot(monGrid, NotchPosition.RightCenter, HorizontalAlignment.Right, VerticalAlignment.Center, 10, 30);
            AddPosSpot(monGrid, NotchPosition.BottomLeft, HorizontalAlignment.Left, VerticalAlignment.Bottom, 30, 10);
            AddPosSpot(monGrid, NotchPosition.BottomCenter, HorizontalAlignment.Center, VerticalAlignment.Bottom, 40, 10);
            AddPosSpot(monGrid, NotchPosition.BottomRight, HorizontalAlignment.Right, VerticalAlignment.Bottom, 30, 10);
            monitor.Child = monGrid;
            posStack.Children.Add(monitor);
            Grid.SetColumn(posStack, 0);
            midGrid.Children.Add(posStack);

            // Rechts: Theme, Akzent & Tempo direkt auf OLED
            var styleStack = new StackPanel();
            styleStack.Children.Add(BuildSectionLabel(L.T("onboarding.section_style")));

            var accentRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, _showHuePicker ? 6 : 10) };
            var selBorder = Brushes.White;
            var unselBorder = NotchDesign.GetFrozenBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

            string[] curatedIds = { "mono-gray", "indigo-violet", "royal-blue", "cyan-ocean", "emerald", "amber-gold", "coral-red" };
            foreach (var presetId in curatedIds)
            {
                var preset = NotchDesign.AccentPresets.FirstOrDefault(p => p.Id == presetId);
                if (preset == null) continue;
                bool active = !_draft.UseCustomAccent && string.Equals(_draft.AccentThemeId, preset.Id, StringComparison.OrdinalIgnoreCase);
                var gradBrush = new LinearGradientBrush(preset.Start, preset.End, new Point(0, 0), new Point(1, 1));
                var dot = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Margin = new Thickness(0, 0, 7, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = preset.Name,
                    Background = gradBrush,
                    BorderThickness = new Thickness(active ? 2 : 1),
                    BorderBrush = active ? selBorder : unselBorder
                };
                dot.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    _showHuePicker = false;
                    ApplyLiveChange(s =>
                    {
                        s.AccentThemeId = preset.Id;
                        s.UseCustomAccent = false;
                    });
                };
                accentRow.Children.Add(dot);
            }

            // Eigene Farbe (Notchify-Spektrum-Dot → öffnet kompakte Inline-Farbleiste)
            var spectrumBrush = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromRgb(0xEF, 0x44, 0x44), 0.0),
                    new(Color.FromRgb(0xF5, 0x9E, 0x0B), 0.25),
                    new(Color.FromRgb(0x10, 0xB9, 0x81), 0.5),
                    new(Color.FromRgb(0x3B, 0x82, 0xF6), 0.75),
                    new(Color.FromRgb(0xEC, 0x48, 0x99), 1.0)
                },
                new Point(0, 0),
                new Point(1, 1));
            _customDot = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Cursor = Cursors.Hand,
                ToolTip = L.T("onboarding.custom_color"),
                Background = _draft.UseCustomAccent ? NotchDesign.CreateAccentGradientBrush(horizontal: true) : spectrumBrush,
                BorderThickness = new Thickness(_draft.UseCustomAccent || _showHuePicker ? 2 : 1),
                BorderBrush = _draft.UseCustomAccent || _showHuePicker ? selBorder : unselBorder
            };
            _customDot.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _showHuePicker = !_showHuePicker;
                BuildUi();
            };
            accentRow.Children.Add(_customDot);
            styleStack.Children.Add(accentRow);

            if (_showHuePicker)
                styleStack.Children.Add(BuildInlineHueBar(unselBorder));

            var speedRow = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (scale, label) in new[]
            {
                (0.75, L.T("onboarding.speed_snappy")),
                (1.0, L.T("onboarding.speed_standard")),
                (1.25, L.T("onboarding.speed_calm"))
            })
            {
                bool active = Math.Abs(_draft.AnimationSpeedScale - scale) < 0.05;
                var pill = NotchDesign.CreateRoundedButton(
                    label,
                    () => ApplyLiveChange(s => s.AnimationSpeedScale = scale),
                    background: active ? NotchDesign.AccentPrimaryBrush : NotchDesign.ControlBackgroundBrush,
                    hoverBackground: active ? NotchDesign.AccentHoverBrush : NotchDesign.ControlHoverBrush,
                    foreground: active ? Brushes.White : NotchDesign.TextPrimaryBrush,
                    cornerRadius: NotchDesign.RadiusSmall,
                    padding: new Thickness(10, 5, 10, 5));
                pill.Margin = new Thickness(0, 0, 6, 0);
                speedRow.Children.Add(pill);
            }
            styleStack.Children.Add(speedRow);

            Grid.SetColumn(styleStack, 2);
            midGrid.Children.Add(styleStack);
            root.Children.Add(midGrid);

            // 3. Optionale Online-Dienste (Privacy Opt-in)
            root.Children.Add(BuildSectionLabel(L.T("onboarding.section_online")));
            var onlineRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 12) };
            AddOnlineOptInPill(onlineRow, L.T("onboarding.optin_weather"), _draft.OptInWeather, (s, on) => s.OptInWeather = on);
            AddOnlineOptInPill(onlineRow, L.T("onboarding.optin_lyrics"), _draft.OptInLyrics, (s, on) => s.OptInLyrics = on);
            AddOnlineOptInPill(onlineRow, L.T("onboarding.optin_favicons"), _draft.OptInFavicons, (s, on) => s.OptInFavicons = on);
            AddOnlineOptInPill(onlineRow, L.T("onboarding.optin_music_rec"), _draft.OptInMusicRecognition, (s, on) => s.OptInMusicRecognition = on);
            root.Children.Add(onlineRow);

            // Fußzeile: Import links + "Live-Tour starten →" rechts
            var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 4, 0, 0) };
            var importBtn = NotchDesign.CreateRoundedButton(
                L.T("onboarding.import_btn"),
                ImportSettings,
                background: Brushes.Transparent,
                hoverBackground: NotchDesign.ControlBackgroundBrush,
                foreground: NotchDesign.TextSecondaryBrush,
                padding: new Thickness(8, 6, 8, 6));
            DockPanel.SetDock(importBtn, Dock.Left);
            footer.Children.Add(importBtn);

            var tourBtn = NotchDesign.CreateRoundedButton(
                L.T("onboarding.start_tour"),
                () => NotchDesign.RunAfterPressFeedback(() => StartTourRequested?.Invoke(_draft)),
                background: NotchDesign.AccentPrimaryBrush,
                hoverBackground: NotchDesign.AccentHoverBrush,
                foreground: Brushes.White,
                padding: new Thickness(18, 8, 18, 8));
            DockPanel.SetDock(tourBtn, Dock.Right);
            footer.Children.Add(tourBtn);
            root.Children.Add(footer);

            Content = root;
        }

        private void AddOnlineOptInPill(Panel parent, string label, bool isOn, Action<AppSettings, bool> setter)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (isOn)
            {
                content.Children.Add(new Border
                {
                    Margin = new Thickness(0, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = NotchDesign.CreateVectorIcon(NotchIconKind.Check, 11, Brushes.White, 2.2)
                });
            }
            content.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = isOn ? Brushes.White : NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = isOn ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            });

            var pill = NotchDesign.CreateRoundedButton(
                content,
                () => ApplyLiveChange(s => setter(s, !isOn)),
                background: isOn ? NotchDesign.AccentPrimaryBrush : NotchDesign.ControlBackgroundBrush,
                hoverBackground: isOn ? NotchDesign.AccentHoverBrush : NotchDesign.ControlHoverBrush,
                cornerRadius: NotchDesign.RadiusSmall,
                padding: new Thickness(10, 5, 10, 5));
            pill.Margin = new Thickness(0, 0, 6, 4);
            parent.Children.Add(pill);
        }

        private FrameworkElement BuildEditionSwipeBar()
        {
            const double totalWidth = 540;
            const double segWidth = (totalWidth - 8) / 3.0;
            int activeIdx = _draft.Profile switch
            {
                ProfileType.Work => 0,
                ProfileType.Complete => 1,
                _ => 2
            };
            double targetX = activeIdx * segWidth;

            var track = new Border
            {
                Width = totalWidth,
                Height = 36,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(4),
                CornerRadius = new CornerRadius(NotchDesign.RadiusControl),
                Background = NotchDesign.ControlBackgroundBrush
            };

            var canvas = new Grid();
            var tt = new TranslateTransform(_editionThumbX, 0);
            _editionThumb = new Border
            {
                Width = segWidth,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(NotchDesign.RadiusSmall + 2),
                Background = NotchDesign.AccentPrimaryBrush,
                RenderTransform = tt
            };
            canvas.Children.Add(_editionThumb);

            if (NotchDesign.EffectiveAnimationsEnabled && Math.Abs(_editionThumbX - targetX) > 0.5)
            {
                var anim = new DoubleAnimation(_editionThumbX, targetX, TimeSpan.FromMilliseconds(NotchDesign.MicroTransitionMs))
                {
                    EasingFunction = NotchDesign.CreateSpringEase(0.35)
                };
                tt.BeginAnimation(TranslateTransform.XProperty, anim);
            }
            else
            {
                tt.X = targetX;
            }
            _editionThumbX = targetX;

            var cols = new Grid();
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var items = new[]
            {
                (ProfileType.Work, L.T("onboarding.edition_work")),
                (ProfileType.Complete, L.T("onboarding.edition_complete")),
                (ProfileType.Gaming, L.T("onboarding.edition_gaming"))
            };
            for (int i = 0; i < items.Length; i++)
            {
                var (profile, label) = items[i];
                bool selected = _draft.Profile == profile;
                var seg = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand };
                seg.Child = new TextBlock
                {
                    Text = label,
                    Foreground = selected ? Brushes.White : NotchDesign.TextSecondaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                seg.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    SelectProfile(profile);
                };
                Grid.SetColumn(seg, i);
                cols.Children.Add(seg);
            }

            canvas.Children.Add(cols);
            track.Child = canvas;
            return track;
        }

        private void SelectProfile(ProfileType profile)
        {
            _draft.Profile = profile;
            foreach (var m in ModuleCatalog.All)
                _draft.Modules[m.Id] = ModuleCatalog.DefaultEnabled(profile, m);
            if (profile == ProfileType.Work)
                _draft.PhoneTransferOnlineDefault = false;

            BuildUi();
        }

        private void ApplyLiveChange(Action<AppSettings> mutate, bool rebuildUi = true)
        {
            mutate(_draft);
            SettingsService.UpdateQuiet(s =>
            {
                s.Theme = _draft.Theme;
                s.AccentThemeId = _draft.AccentThemeId;
                s.UseCustomAccent = _draft.UseCustomAccent;
                s.CustomAccentStartHex = _draft.CustomAccentStartHex;
                s.CustomAccentEndHex = _draft.CustomAccentEndHex;
                s.Position = _draft.Position;
                s.AnimationSpeedScale = _draft.AnimationSpeedScale;
            });
            _onLiveApply();
            if (rebuildUi)
                BuildUi();
        }

        private void AddPosSpot(
            Grid parent,
            NotchPosition pos,
            HorizontalAlignment hAlign,
            VerticalAlignment vAlign,
            double w,
            double h)
        {
            bool active = _draft.Position == pos;
            var spot = NotchDesign.CreateRoundedButton(
                string.Empty,
                () => ApplyLiveChange(s => s.Position = pos),
                background: active ? NotchDesign.AccentPrimaryBrush : NotchDesign.ControlHoverBrush,
                hoverBackground: active ? NotchDesign.AccentHoverBrush : NotchDesign.AccentPrimaryBrush,
                cornerRadius: 4,
                padding: new Thickness(0));
            spot.Width = w;
            spot.Height = h;
            spot.HorizontalAlignment = hAlign;
            spot.VerticalAlignment = vAlign;
            parent.Children.Add(spot);
        }

        private FrameworkElement BuildInlineHueBar(Brush borderBrush)
        {
            const double barWidth = 240;
            const double thumbSize = 16;

            var host = new Grid
            {
                Width = barWidth,
                Height = 18,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 10),
                Cursor = Cursors.Hand
            };

            var bar = new Border
            {
                Height = 10,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                BorderBrush = borderBrush,
                Background = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new(HueToColor(0, 0.78, 0.92), 0.000),
                        new(HueToColor(60, 0.78, 0.92), 0.167),
                        new(HueToColor(120, 0.78, 0.92), 0.333),
                        new(HueToColor(180, 0.78, 0.92), 0.500),
                        new(HueToColor(240, 0.78, 0.92), 0.667),
                        new(HueToColor(300, 0.78, 0.92), 0.833),
                        new(HueToColor(359, 0.78, 0.92), 1.000)
                    },
                    new Point(0, 0.5),
                    new Point(1, 0.5))
            };
            host.Children.Add(bar);

            _hueThumbShift = new TranslateTransform(_hueT * (barWidth - thumbSize), 0);
            _hueThumb = new Border
            {
                Width = thumbSize,
                Height = thumbSize,
                CornerRadius = new CornerRadius(thumbSize / 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(HueToColor(_hueT * 360.0, 0.78, 0.92)),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(2),
                RenderTransform = _hueThumbShift,
                IsHitTestVisible = false
            };
            host.Children.Add(_hueThumb);

            host.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                host.CaptureMouse();
                ApplyHueFromPoint(host, e.GetPosition(host).X, barWidth, thumbSize, rebuildUi: false);
            };
            host.MouseMove += (_, e) =>
            {
                if (!host.IsMouseCaptured) return;
                ApplyHueFromPoint(host, e.GetPosition(host).X, barWidth, thumbSize, rebuildUi: false);
            };
            host.MouseLeftButtonUp += (_, e) =>
            {
                if (!host.IsMouseCaptured) return;
                e.Handled = true;
                host.ReleaseMouseCapture();
                ApplyHueFromPoint(host, e.GetPosition(host).X, barWidth, thumbSize, rebuildUi: true);
            };
            return host;
        }

        private void ApplyHueFromPoint(FrameworkElement host, double x, double barWidth, double thumbSize, bool rebuildUi)
        {
            double w = host.ActualWidth > 0 ? host.ActualWidth : barWidth;
            _hueT = Math.Clamp((x - thumbSize / 2.0) / Math.Max(1, w - thumbSize), 0.0, 1.0);
            var c = HueToColor(_hueT * 360.0, 0.78, 0.92);

            if (_hueThumbShift != null)
                _hueThumbShift.X = _hueT * (w - thumbSize);
            if (_hueThumb != null)
                _hueThumb.Background = new SolidColorBrush(c);

            static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
            string startHex = ToHex((byte)(c.R * 0.70), (byte)(c.G * 0.70), (byte)(c.B * 0.70));
            string endHex = ToHex(
                (byte)Math.Min(255, c.R + (255 - c.R) * 0.16),
                (byte)Math.Min(255, c.G + (255 - c.G) * 0.16),
                (byte)Math.Min(255, c.B + (255 - c.B) * 0.16));

            ApplyLiveChange(s =>
            {
                s.UseCustomAccent = true;
                s.CustomAccentStartHex = startHex;
                s.CustomAccentEndHex = endHex;
            }, rebuildUi);

            if (!rebuildUi && _customDot != null)
                _customDot.Background = NotchDesign.CreateAccentGradientBrush(horizontal: true);
        }

        private static Color HueToColor(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;
            var (r, g, b) = h switch
            {
                < 60 => (c, x, 0.0),
                < 120 => (x, c, 0.0),
                < 180 => (0.0, c, x),
                < 240 => (0.0, x, c),
                < 300 => (x, 0.0, c),
                _ => (c, 0.0, x)
            };
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }

        private void ImportSettings()
        {
            var dlg = new OpenFileDialog
            {
                Title = L.T("settings.general.import_dialog_title"),
                Filter = L.T("settings.general.json_filter")
            };
            if (dlg.ShowDialog() == true && SettingsService.ImportFromFile(dlg.FileName))
                ImportedOrSkipped?.Invoke();
        }

        private void Skip()
        {
            var current = SettingsService.Clone();
            current.HasCompletedOnboarding = true;
            SettingsService.Update(current);
            ImportedOrSkipped?.Invoke();
        }

        private static TextBlock BuildSectionLabel(string text) => new()
        {
            Text = text,
            Foreground = NotchDesign.TextSecondaryBrush,
            FontFamily = NotchDesign.UiTextFont,
            FontWeight = FontWeights.SemiBold,
            FontSize = 10.5
        };
    }
}
