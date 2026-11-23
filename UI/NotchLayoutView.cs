using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WindowsNotch.Core;
using WindowsNotch.Modules.Pet;
using WindowsNotch.Services;

namespace WindowsNotch.UI
{
    /// <summary>
    /// Rendert ein Notch-Layout (Platz je Information) als Kapsel mit Links/Mitte/Rechts.
    /// Mitte bleibt echt zentriert (Spalten * / Auto / *). Siehe docs/NOTCH-LAYOUT-SPEC.md.
    /// </summary>
    public sealed class NotchLayoutView : Grid
    {
        private static CultureInfo ActiveCulture =>
            string.Equals(LocalizationService.CurrentLanguage, "en", StringComparison.OrdinalIgnoreCase)
                ? CultureInfo.GetCultureInfo("en-US")
                : CultureInfo.GetCultureInfo("de-DE");

        private readonly IReadOnlyDictionary<string, NotchSlot> _layout;
        private readonly string _signature;
        private readonly List<Action> _updates = new();
        private readonly StackPanel _left = NewSlot(HorizontalAlignment.Left);
        private readonly StackPanel _center = NewSlot(HorizontalAlignment.Center);
        private readonly StackPanel _right = NewSlot(HorizontalAlignment.Right);

        /// <summary>Liefert null, wenn nichts anzuzeigen ist (leeres Layout bzw. keine verfügbare Information).</summary>
        public static NotchLayoutView? Create(IReadOnlyDictionary<string, NotchSlot> layout)
        {
            var view = new NotchLayoutView(layout);
            return view._signature.Length == 0 ? null : view;
        }

        private NotchLayoutView(IReadOnlyDictionary<string, NotchSlot> layout)
        {
            _layout = layout;
            _signature = VisibleSignature(layout);
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            SetColumn(_center, 1);
            SetColumn(_right, 2);
            Children.Add(_left);
            Children.Add(_center);
            Children.Add(_right);

            foreach (var id in _signature.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var stack = layout[id] switch { NotchSlot.Left => _left, NotchSlot.Right => _right, _ => _center };
                if (stack.Children.Count > 0)
                    stack.Children.Add(new Border
                    {
                        Width = 1,
                        Height = 11,
                        Background = NotchDesign.DividerBrush,
                        Margin = new Thickness(9, 0, 9, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                var (element, update) = BuildItem(id);
                stack.Children.Add(element);
                _updates.Add(update);
            }

            UpdateWidth();
        }

        /// <summary>Aktualisiert Texte. true = Aufbau oder Breite hat sich geändert → Besitzer baut neu auf.</summary>
        public bool Refresh()
        {
            // Wetter lädt sonst nur, wenn das Dashboard-Modul aktiv ist
            if (_layout.ContainsKey(NotchInfo.Weather) && SettingsService.Current.OptInWeather)
                _ = WeatherService.Instance.RefreshIfNeededAsync();

            if (VisibleSignature(_layout) != _signature) return true;
            foreach (var update in _updates) update();
            double old = Width;
            UpdateWidth();
            return Math.Abs(Width - old) > 2;
        }

        private void UpdateWidth()
        {
            double l = WidthOf(_left), c = WidthOf(_center), r = WidthOf(_right);
            double side = Math.Max(l, r);
            Width = side > 0 ? 2 * side + c + (c > 0 ? 24 : 12) : c;
        }

        private static double WidthOf(StackPanel panel)
        {
            panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return panel.DesiredSize.Width;
        }

        private static StackPanel NewSlot(HorizontalAlignment align) => new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center
        };

        private static bool IsAvailable(string id) => id switch
        {
            NotchInfo.Weather => SettingsService.Current.OptInWeather && WeatherService.Instance.Current != null,
            NotchInfo.Media => MediaPlayerService.Instance.HasKnownTrack,
            _ => true
        };

        /// <summary>Ids der aktuell darstellbaren Informationen in fester Reihenfolge ("clock|cpu|").</summary>
        private static string VisibleSignature(IReadOnlyDictionary<string, NotchSlot> layout)
            => string.Concat(NotchInfo.All
                .Where(d => layout.TryGetValue(d.Id, out var s) && s != NotchSlot.Off && IsAvailable(d.Id))
                .Select(d => d.Id + "|"));

        // ---------- Einzelne Informationen ----------

        private static (FrameworkElement, Action) BuildItem(string id)
        {
            switch (id)
            {
                case NotchInfo.Clock:
                    return Text(() => DateTime.Now.ToString("HH:mm", ActiveCulture), 13.5, display: true);

                case NotchInfo.Date:
                    return Text(() =>
                    {
                        var now = DateTime.Now;
                        var f = ActiveCulture.DateTimeFormat;
                        return $"{f.GetAbbreviatedDayName(now.DayOfWeek).TrimEnd('.')} {now.Day}. {f.GetAbbreviatedMonthName(now.Month).TrimEnd('.')}";
                    }, 12.5);

                case NotchInfo.Weather:
                    return Weather();

                case NotchInfo.Cpu:
                    return Metric("CPU", () => SystemMetricsService.Instance.CpuPercent);
                case NotchInfo.Ram:
                    return Metric("RAM", () => SystemMetricsService.Instance.RamPercent);
                case NotchInfo.Gpu:
                    return Metric("GPU", () => SystemMetricsService.Instance.GpuPercent);

                case NotchInfo.PetEyes:
                    return (new NotchEyesControl(compact: true), () => { });

                default:
                    return Media();
            }
        }

        private static TextBlock NewText(double size, bool muted = false, bool display = false)
        {
            var tb = new TextBlock
            {
                Foreground = muted ? NotchDesign.TextMutedBrush : NotchDesign.TextPrimaryBrush,
                FontFamily = display ? NotchDesign.UiDisplayFont : NotchDesign.UiTextFont,
                FontSize = size,
                FontWeight = muted ? FontWeights.Medium : FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
            return tb;
        }

        private static (FrameworkElement, Action) Text(Func<string> read, double size, bool display = false)
        {
            var tb = NewText(size, display: display);
            tb.Text = read();
            return (tb, () => tb.Text = read());
        }

        private static (FrameworkElement, Action) Metric(string label, Func<int> read)
        {
            var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var name = NewText(11, muted: true);
            name.Text = label;
            name.Margin = new Thickness(0, 0, 5, 0);
            var value = NewText(12.5);

            string Format() => read() is var v && v >= 0 ? $"{v}%" : "–";
            value.Text = Format();
            stack.Children.Add(name);
            stack.Children.Add(value);
            return (stack, () => value.Text = Format());
        }

        private static (FrameworkElement, Action) Weather()
        {
            var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var iconHost = new ContentControl { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            var temp = NewText(12.5);
            NotchIconKind? shown = null;

            void Update()
            {
                var snap = WeatherService.Instance.Current;
                if (snap == null) return;
                temp.Text = $"{Math.Round(snap.TemperatureC):0}°";
                if (shown != snap.IconKind)
                {
                    shown = snap.IconKind;
                    iconHost.Content = NotchDesign.CreateVectorIcon(snap.IconKind, 13, NotchDesign.TextSecondaryBrush, 1.8);
                }
            }

            Update();
            stack.Children.Add(iconHost);
            stack.Children.Add(temp);
            return (stack, Update);
        }

        private static (FrameworkElement, Action) Media()
        {
            var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var icon = NotchDesign.CreateVectorIcon(NotchIconKind.Music, 13, NotchDesign.AccentPrimaryBrush, 1.9);
            icon.Margin = new Thickness(0, 0, 6, 0);
            var title = NewText(12.5);
            title.MaxWidth = 150;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.Text = MediaPlayerService.Instance.TrackTitle;
            stack.Children.Add(icon);
            stack.Children.Add(title);
            return (stack, () => title.Text = MediaPlayerService.Instance.TrackTitle);
        }
    }
}
