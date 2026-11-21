using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using WindowsNotch.Core;
using WindowsNotch.Modules.Weather;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.Widgets
{
    public partial class WidgetsModule
    {
        private static Border CreateBentoAppCard(Thickness padding)
        {
            var card = NotchDesign.CreateCard(16, padding);
            if (!NotchDesign.IsLight)
            {
                card.Background = new SolidColorBrush(NotchDesign.IsGlass
                    ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
                    : Color.FromRgb(0x16, 0x16, 0x1A));
            }
            card.SizeChanged += (_, e) =>
            {
                if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
                var clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 16, 16);
                clip.Freeze();
                card.Clip = clip;
            };
            return card;
        }

        // ---------- 1. Dediziertes Wetter-Modul (mehrere Städte + dynamischer Glas-/Regen-Hintergrund) ----------

        private Border BuildWeatherTile()
        {
            var card = CreateBentoAppCard(new Thickness(0));
            var root = new Grid();
            root.SizeChanged += (_, _) =>
            {
                if (root.ActualWidth > 0 && root.ActualHeight > 0)
                {
                    root.Clip = new RectangleGeometry(
                        new Rect(0, 0, root.ActualWidth, root.ActualHeight),
                        NotchDesign.RadiusCard,
                        NotchDesign.RadiusCard);
                }
            };

            var atmosphere = new WeatherAtmosphereLayer(compact: true);
            var frostOverlay = new Border
            {
                IsHitTestVisible = false,
                Background = new LinearGradientBrush(
                    Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
                    Color.FromArgb(0x04, 0xFF, 0xFF, 0xFF),
                    90)
            };

            root.Children.Add(atmosphere);
            root.Children.Add(frostOverlay);

            var content = new DockPanel { Margin = new Thickness(14, 11, 14, 10) };

            // Top: Aktiver Ort + Wetter-Icon
            var topDock = new DockPanel { LastChildFill = false };
            var cityText = new TextBlock
            {
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                MaxWidth = 155,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            var topIconHost = new Border { VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(cityText, Dock.Left);
            DockPanel.SetDock(topIconHost, Dock.Right);
            topDock.Children.Add(cityText);
            topDock.Children.Add(topIconHost);
            DockPanel.SetDock(topDock, Dock.Top);
            content.Children.Add(topDock);

            // Bottom: 3-Tage-Vorschau
            var forecastGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            for (int c = 0; c < 3; c++)
                forecastGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            DockPanel.SetDock(forecastGrid, Dock.Bottom);
            content.Children.Add(forecastGrid);

            // Center: Temperatur + Zustand + Opt-In-Button
            var midStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var tempRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
            var tempText = new TextBlock
            {
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 30,
                FontWeight = FontWeights.Normal
            };
            Typography.SetNumeralAlignment(tempText, FontNumeralAlignment.Tabular);
            var atmosphericSecondaryBrush = NotchDesign.GetFrozenBrush(NotchDesign.IsLight
                ? Color.FromArgb(0xC8, 0x14, 0x14, 0x18)
                : Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF));

            var rangeText = new TextBlock
            {
                Foreground = atmosphericSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10.5,
                Margin = new Thickness(8, 0, 0, 5)
            };
            Typography.SetNumeralAlignment(rangeText, FontNumeralAlignment.Tabular);
            tempRow.Children.Add(tempText);
            tempRow.Children.Add(rangeText);

            var condText = new TextBlock
            {
                Foreground = atmosphericSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var optInBtn = NotchDesign.CreateRoundedButton(
                L.T("widgets.weather.enable"),
                () =>
                {
                    var cfg = SettingsService.Current;
                    cfg.OptInWeather = true;
                    SettingsService.Update(cfg);
                    WeatherService.Instance.InvalidateAndRefresh();
                },
                background: NotchDesign.ControlBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                foreground: NotchDesign.TextPrimaryBrush,
                cornerRadius: NotchDesign.RadiusControl,
                padding: new Thickness(12, 5, 12, 5));
            optInBtn.HorizontalAlignment = HorizontalAlignment.Left;
            optInBtn.Margin = new Thickness(0, 8, 0, 0);

            midStack.Children.Add(tempRow);
            midStack.Children.Add(condText);
            midStack.Children.Add(optInBtn);
            content.Children.Add(midStack);

            root.Children.Add(content);
            card.Child = root;

            void UpdateWeather()
            {
                var s = SettingsService.Current;
                var cities = GetWeatherCityList();
                bool canCycle = s.OptInWeather && cities.Count > 1;
                card.Cursor = canCycle ? Cursors.Hand : Cursors.Arrow;
                card.ToolTip = canCycle ? L.T("widgets.weather.city_switch_tooltip") : null;
                string activeCity = cities.Count > 0 ? cities[s.WeatherCityIndex % cities.Count] : L.T("widgets.weather.no_city");

                if (!s.OptInWeather)
                {
                    cityText.Text = activeCity;
                    topIconHost.Child = NotchDesign.CreateVectorIcon(NotchIconKind.CloudSun, 17, NotchDesign.AccentPrimaryBrush, 1.8);
                    tempRow.Visibility = Visibility.Collapsed;
                    rangeText.Text = string.Empty;
                    condText.Text = L.T("widgets.weather.disabled");
                    optInBtn.Visibility = Visibility.Visible;
                    forecastGrid.Children.Clear();
                    atmosphere.SetWeather(null, true);
                    return;
                }

                tempRow.Visibility = Visibility.Visible;
                optInBtn.Visibility = Visibility.Collapsed;
                var w = WeatherService.Instance.Current;
                cityText.Text = w?.CityName ?? activeCity;
                if (w == null)
                {
                    topIconHost.Child = NotchDesign.CreateVectorIcon(NotchIconKind.Cloud, 18, NotchDesign.TextSecondaryBrush, 1.8);
                    tempText.Text = WeatherService.Instance.IsLoading ? "…" : "--°";
                    rangeText.Text = string.Empty;
                    condText.Text = WeatherService.Instance.LastError ?? L.T("widgets.weather.loading");
                    forecastGrid.Children.Clear();
                    atmosphere.SetWeather(null, true);
                    return;
                }

                topIconHost.Child = NotchDesign.CreateVectorIcon(w.IconKind, 18, NotchDesign.TextPrimaryBrush, 1.8);
                tempText.Text = $"{w.TemperatureC:0}°";
                rangeText.Text = $"H {w.TodayMaxC:0}° · T {w.TodayMinC:0}°";
                condText.Text = w.ConditionText;

                atmosphere.SetWeather(w.WeatherCode, w.IsDay, w.PrecipitationMm, w.CloudCoverPercent);

                forecastGrid.Children.Clear();
                for (int i = 0; i < Math.Min(3, w.ForecastDays.Count); i++)
                {
                    var d = w.ForecastDays[i];
                    var dayCol = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : i == 2 ? HorizontalAlignment.Right : HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    string dayLabel = i == 0 ? L.T("common.today") : d.Date.ToString("ddd", ActiveCulture);
                    dayCol.Children.Add(new TextBlock
                    {
                        Text = dayLabel,
                        Foreground = NotchDesign.TextMutedBrush,
                        FontFamily = NotchDesign.UiTextFont,
                        FontSize = 10,
                        Margin = new Thickness(0, 0, 4, 0)
                    });
                    dayCol.Children.Add(NotchDesign.CreateVectorIcon(WeatherService.MapWmoToIcon(d.WeatherCode, true), 11, NotchDesign.TextSecondaryBrush, 1.6));
                    var mm = new TextBlock
                    {
                        Text = $"{d.MaxC:0}°",
                        Foreground = NotchDesign.TextPrimaryBrush,
                        FontFamily = NotchDesign.UiTextFont,
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(4, 0, 0, 0)
                    };
                    Typography.SetNumeralAlignment(mm, FontNumeralAlignment.Tabular);
                    dayCol.Children.Add(mm);
                    Grid.SetColumn(dayCol, i);
                    forecastGrid.Children.Add(dayCol);
                }
            }

            Point downPt = default;
            card.MouseLeftButtonDown += (_, e) => downPt = e.GetPosition(card);
            card.MouseLeftButtonUp += (_, e) =>
            {
                if (_jiggleController?.IsJiggleMode == true) return;
                if (!SettingsService.Current.OptInWeather) return;
                Point upPt = e.GetPosition(card);
                if (Math.Abs(upPt.X - downPt.X) > 6 || Math.Abs(upPt.Y - downPt.Y) > 6) return;
                if (GetWeatherCityList().Count <= 1) return;
                CycleWeatherCity();
            };

            _tileUpdaters.Add(UpdateWeather);
            UpdateWeather();
            return card;
        }

        private static IReadOnlyList<string> GetWeatherCityList() => WeatherService.Instance.GetCities();

        private void CycleWeatherCity()
        {
            WeatherService.Instance.CycleCity(1);
            UpdateExpandedWidgets();
        }

        // ---------- 2. Kalender / Aufgaben / Notizen (Einzel-Apps) ----------

        private Border BuildStandaloneCalendarTile()
        {
            var card = CreateBentoAppCard(new Thickness(12, 10, 12, 10));
            card.Child = BuildCalendarContent();
            RebuildCalendarDays();
            return card;
        }

        private Border BuildStandaloneTodoTile()
        {
            var card = CreateBentoAppCard(new Thickness(12, 10, 12, 10));
            card.Child = BuildTodoContent();
            return card;
        }

        private Border BuildStandaloneNoteTile()
        {
            var card = CreateBentoAppCard(new Thickness(12, 10, 12, 10));
            card.Child = BuildNoteContent();
            return card;
        }

        // ---------- 3. Uhr (Einzel-App) ----------

        private Border BuildClockTile()
        {
            var card = CreateBentoAppCard(new Thickness(14, 12, 14, 12));
            var root = new Grid();
            root.SizeChanged += (_, _) =>
            {
                if (root.ActualWidth > 0 && root.ActualHeight > 0)
                {
                    root.Clip = new RectangleGeometry(
                        new Rect(0, 0, root.ActualWidth, root.ActualHeight),
                        NotchDesign.RadiusCard,
                        NotchDesign.RadiusCard);
                }
            };
            root.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, -6, 0),
                Opacity = 0.16,
                IsHitTestVisible = false,
                Child = NotchDesign.CreateVectorIcon(NotchIconKind.Clock, 56, NotchDesign.TextSecondaryBrush, 1.4)
            });

            var dock = new DockPanel();
            var topRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            topRow.Children.Add(NotchDesign.CreateVectorIcon(NotchIconKind.Clock, 13, NotchDesign.AccentPrimaryBrush, 1.9));
            topRow.Children.Add(new TextBlock
            {
                Text = L.T("widgets.clock_title"),
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(6, 0, 0, 0)
            });
            DockPanel.SetDock(topRow, Dock.Top);
            dock.Children.Add(topRow);

            var bottomStack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            var timeText = new TextBlock
            {
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 31,
                FontWeight = FontWeights.Normal
            };
            Typography.SetNumeralAlignment(timeText, FontNumeralAlignment.Tabular);
            var dateText = new TextBlock
            {
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                Margin = new Thickness(0, 2, 0, 0)
            };
            bottomStack.Children.Add(timeText);
            bottomStack.Children.Add(dateText);
            dock.Children.Add(bottomStack);

            root.Children.Add(dock);
            card.Child = root;

            void UpdateClock()
            {
                var now = DateTime.Now;
                int kw = ISOWeek.GetWeekOfYear(now);
                var culture = ActiveCulture;
                timeText.Text = now.ToString("HH:mm:ss");
                dateText.Text = L.F("widgets.clock_week", now.ToString("dddd, d. MMM", culture), kw);
            }

            _tileUpdaters.Add(UpdateClock);
            UpdateClock();
            return card;
        }

        // ---------- 4. Fokus-Timer (Einzel-App) ----------

        private static Button CreateAppRoundButton(NotchIconKind icon, Brush bg, Brush fg, string tooltip, Action onClick)
        {
            var btn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(icon, 10.5, fg, 1.9),
                onClick,
                background: bg,
                hoverBackground: NotchDesign.ControlHoverBrush,
                cornerRadius: 11,
                padding: new Thickness(0),
                tooltip: tooltip);
            btn.Width = 24;
            btn.Height = 24;
            return btn;
        }

        private Border BuildTimerTile()
        {
            var card = CreateBentoAppCard(new Thickness(14, 11, 14, 11));
            var dock = new DockPanel();

            var topDock = new DockPanel { LastChildFill = false };
            var labelRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var orangeBrush = new SolidColorBrush(NotchDesign.AccentOrange);
            orangeBrush.Freeze();
            labelRow.Children.Add(NotchDesign.CreateVectorIcon(NotchIconKind.Timer, 13, orangeBrush, 1.9));
            var statusText = new TextBlock
            {
                Text = L.T("widgets.timer_title"),
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(6, 0, 0, 0)
            };
            labelRow.Children.Add(statusText);
            DockPanel.SetDock(labelRow, Dock.Left);
            topDock.Children.Add(labelRow);

            // Schnell-Presets (5m / 15m / 25m)
            var presets = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (int mins in new[] { 5, 15, 25 })
            {
                int m = mins;
                var pBtn = NotchDesign.CreateRoundedButton(
                    new TextBlock
                    {
                        Text = $"{m}m",
                        Foreground = NotchDesign.TextSecondaryBrush,
                        FontFamily = NotchDesign.UiTextFont,
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold
                    },
                    () =>
                    {
                        if (_timerRunning) return;
                        _timerFinished = false;
                        _timerDuration = TimeSpan.FromMinutes(m);
                        _timerRemaining = _timerDuration;
                        UpdateExpandedWidgets();
                        RaiseStateChanged();
                    },
                    background: NotchDesign.ControlBackgroundBrush,
                    hoverBackground: NotchDesign.ControlHoverBrush,
                    cornerRadius: NotchDesign.RadiusSmall,
                    padding: new Thickness(6, 2, 6, 2));
                pBtn.Margin = new Thickness(3, 0, 0, 0);
                presets.Children.Add(pBtn);
            }
            DockPanel.SetDock(presets, Dock.Right);
            topDock.Children.Add(presets);
            DockPanel.SetDock(topDock, Dock.Top);
            dock.Children.Add(topDock);

            var bottomDock = new DockPanel { VerticalAlignment = VerticalAlignment.Bottom, LastChildFill = false };
            var valText = new TextBlock
            {
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 30,
                FontWeight = FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Typography.SetNumeralAlignment(valText, FontNumeralAlignment.Tabular);
            DockPanel.SetDock(valText, Dock.Left);
            bottomDock.Children.Add(valText);

            var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
            var plusBtn = CreateAppRoundButton(NotchIconKind.Plus, NotchDesign.ControlBackgroundBrush, NotchDesign.TextPrimaryBrush, L.T("widgets.timer_plus_min"), () => AdjustTimer(1));
            plusBtn.Margin = new Thickness(0, 0, 5, 0);
            var toggleBtn = CreateAppRoundButton(NotchIconKind.Play, orangeBrush, Brushes.White, L.T("widgets.start_pause"), ToggleTimer);
            toggleBtn.Margin = new Thickness(0, 0, 5, 0);
            var resetBtn = CreateAppRoundButton(NotchIconKind.Reset, NotchDesign.ControlBackgroundBrush, NotchDesign.TextSecondaryBrush, L.T("widgets.reset"), ResetTimer);
            controls.Children.Add(plusBtn);
            controls.Children.Add(toggleBtn);
            controls.Children.Add(resetBtn);
            DockPanel.SetDock(controls, Dock.Right);
            bottomDock.Children.Add(controls);

            dock.Children.Add(bottomDock);
            card.Child = dock;

            void UpdateTimerTile()
            {
                valText.Text = FormatDuration(_timerRemaining);
                statusText.Text = _timerFinished ? L.T("widgets.timer_done") : _timerRunning ? L.T("widgets.timer_running") : L.T("widgets.timer_title");
                toggleBtn.Content = NotchDesign.CreateVectorIcon(_timerRunning ? NotchIconKind.Pause : NotchIconKind.Play, 11, Brushes.White, 1.8);
            }

            _tileUpdaters.Add(UpdateTimerTile);
            UpdateTimerTile();
            return card;
        }

        // ---------- 5. Stoppuhr (Einzel-App) ----------

        private Border BuildStopwatchTile()
        {
            var card = CreateBentoAppCard(new Thickness(14, 11, 14, 11));
            var dock = new DockPanel();

            var topRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            topRow.Children.Add(NotchDesign.CreateVectorIcon(NotchIconKind.Pulse, 13, NotchDesign.AccentPrimaryBrush, 1.9));
            var statusText = new TextBlock
            {
                Text = L.T("widgets.stopwatch_title"),
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(6, 0, 0, 0)
            };
            topRow.Children.Add(statusText);
            DockPanel.SetDock(topRow, Dock.Top);
            dock.Children.Add(topRow);

            var bottomDock = new DockPanel { VerticalAlignment = VerticalAlignment.Bottom, LastChildFill = false };
            var valText = new TextBlock
            {
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 28,
                FontWeight = FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Typography.SetNumeralAlignment(valText, FontNumeralAlignment.Tabular);
            DockPanel.SetDock(valText, Dock.Left);
            bottomDock.Children.Add(valText);

            var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
            var toggleBtn = CreateAppRoundButton(NotchIconKind.Play, NotchDesign.AccentPrimaryBrush, Brushes.White, L.T("widgets.start_pause"), ToggleStopwatch);
            toggleBtn.Margin = new Thickness(0, 0, 5, 0);
            var resetBtn = CreateAppRoundButton(NotchIconKind.Reset, NotchDesign.ControlBackgroundBrush, NotchDesign.TextSecondaryBrush, L.T("widgets.reset"), ResetStopwatch);
            controls.Children.Add(toggleBtn);
            controls.Children.Add(resetBtn);
            DockPanel.SetDock(controls, Dock.Right);
            bottomDock.Children.Add(controls);

            dock.Children.Add(bottomDock);
            card.Child = dock;

            void UpdateSw()
            {
                var sw = _stopwatch.Elapsed;
                valText.Text = $"{(int)sw.TotalMinutes:00}:{sw.Seconds:00}.{sw.Milliseconds / 10:00}";
                statusText.Text = _stopwatchRunning ? L.T("widgets.stopwatch_running") : sw > TimeSpan.Zero ? L.T("widgets.stopwatch_paused") : L.T("widgets.stopwatch_title");
                toggleBtn.Content = NotchDesign.CreateVectorIcon(_stopwatchRunning ? NotchIconKind.Pause : NotchIconKind.Play, 11, Brushes.White, 1.8);
            }

            _tileUpdaters.Add(UpdateSw);
            _fastTickUpdaters.Add(UpdateSw);
            UpdateSw();
            return card;
        }

        // ---------- 6. Gebündelte Hardware-Auslastung (CPU + RAM + GPU) ----------

        private Border BuildHardwareTile()
        {
            var card = CreateBentoAppCard(new Thickness(14, 11, 14, 11));
            var dock = new DockPanel();

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            header.Children.Add(NotchDesign.CreateVectorIcon(NotchIconKind.Cpu, 13, NotchDesign.AccentPrimaryBrush, 1.9));
            header.Children.Add(new TextBlock
            {
                Text = L.T("widgets.hardware_title"),
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(6, 0, 0, 0)
            });
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);

            var rows = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var (cpuRow, cpuVal, cpuFill) = CreateMetricRow("CPU", NotchIconKind.Cpu);
            var (ramRow, ramVal, ramFill) = CreateMetricRow("RAM", NotchIconKind.Memory);
            var (gpuRow, gpuVal, gpuFill) = CreateMetricRow("GPU", NotchIconKind.Gpu);
            ramRow.Margin = new Thickness(0, 7, 0, 7);
            rows.Children.Add(cpuRow);
            rows.Children.Add(ramRow);
            rows.Children.Add(gpuRow);
            dock.Children.Add(rows);

            card.Child = dock;

            void UpdateHardware()
            {
                var m = SystemMetricsService.Instance;
                cpuVal.Text = $"{m.CpuPercent}%";
                ramVal.Text = $"{m.RamUsedGb:0.0} / {m.RamTotalGb:0} GB";
                gpuVal.Text = $"{m.GpuPercent}%";

                SetBarRatio(cpuFill, m.CpuPercent / 100.0);
                SetBarRatio(ramFill, m.RamPercent / 100.0);
                SetBarRatio(gpuFill, m.GpuPercent / 100.0);
            }

            _tileUpdaters.Add(UpdateHardware);
            UpdateHardware();
            return card;
        }

        private static (StackPanel Row, TextBlock ValueText, Border FillBar) CreateMetricRow(string label, NotchIconKind icon)
        {
            var row = new StackPanel();
            var top = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 3) };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(NotchDesign.CreateVectorIcon(icon, 11, NotchDesign.TextMutedBrush, 1.7));
            left.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(5, 0, 0, 0)
            });
            var valText = new TextBlock
            {
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10.5,
                FontWeight = FontWeights.Medium
            };
            Typography.SetNumeralAlignment(valText, FontNumeralAlignment.Tabular);
            DockPanel.SetDock(left, Dock.Left);
            DockPanel.SetDock(valText, Dock.Right);
            top.Children.Add(left);
            top.Children.Add(valText);
            row.Children.Add(top);

            var track = new Grid { Height = 5 };
            track.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(2.5)
            });
            var fill = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0,
                CornerRadius = new CornerRadius(2.5),
                Background = NotchDesign.CreateAccentGradientBrush(horizontal: true)
            };
            track.SizeChanged += (_, e) =>
            {
                if (fill.Tag is double ratio && e.NewSize.Width > 0)
                    fill.Width = Math.Clamp(ratio, 0, 1) * e.NewSize.Width;
            };
            track.Children.Add(fill);
            row.Children.Add(track);
            return (row, valText, fill);
        }

        private static void SetBarRatio(Border fill, double ratio)
        {
            double clamped = Math.Clamp(ratio, 0, 1);
            fill.Tag = clamped;
            if (fill.Parent is FrameworkElement track && track.ActualWidth > 0)
                fill.Width = clamped * track.ActualWidth;
        }

        // ---------- 7. Frei befüllbares Custom-Karussell ----------

        private Border BuildCustomCarouselTile()
        {
            var card = CreateBentoAppCard(new Thickness(0));
            var outerGrid = new Grid();

            var contentHost = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            outerGrid.Children.Add(contentHost);

            var dotsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Panel.SetZIndex(dotsPanel, 25);
            outerGrid.Children.Add(dotsPanel);

            card.Child = outerGrid;

            void RenderPage()
            {
                var pages = SettingsService.Current.BentoCarouselPages;
                bool isEdit = _showStoreDrawer || (_jiggleController?.IsJiggleMode == true);
                int totalPages = isEdit ? pages.Count + 1 : Math.Max(1, pages.Count);
                if (_customCarouselPageIndex < 0) _customCarouselPageIndex = 0;
                if (_customCarouselPageIndex >= totalPages) _customCarouselPageIndex = totalPages - 1;

                contentHost.Children.Clear();
                dotsPanel.Children.Clear();

                if (_customCarouselPageIndex < pages.Count)
                {
                    string pageId = pages[_customCarouselPageIndex];
                    var innerTile = BuildBentoTile(pageId);
                    innerTile.Background = Brushes.Transparent;
                    innerTile.BorderThickness = new Thickness(0);
                    innerTile.CornerRadius = new CornerRadius(0);
                    innerTile.Margin = new Thickness(0);
                    innerTile.Effect = null;
                    innerTile.HorizontalAlignment = HorizontalAlignment.Stretch;
                    innerTile.VerticalAlignment = VerticalAlignment.Stretch;
                    innerTile.MinWidth = 0;
                    innerTile.MinHeight = 0;
                    contentHost.Children.Add(innerTile);

                    if (isEdit)
                    {
                        int removeIdx = _customCarouselPageIndex;
                        var removeBtn = NotchDesign.CreateRoundedButton(
                            NotchDesign.CreateVectorIcon(NotchIconKind.Close, 9, NotchDesign.TextSecondaryBrush, 2.0),
                            () =>
                            {
                                var updated = SettingsService.Current.BentoCarouselPages.ToList();
                                if (removeIdx >= 0 && removeIdx < updated.Count)
                                {
                                    updated.RemoveAt(removeIdx);
                                    SettingsService.UpdateQuiet(s => s.BentoCarouselPages = updated);
                                    if (_customCarouselPageIndex > 0 && _customCarouselPageIndex >= updated.Count)
                                        _customCarouselPageIndex = updated.Count;
                                    RenderPage();
                                    RefreshStoreDrawerInPlace();
                                }
                            },
                            background: new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00)),
                            hoverBackground: new SolidColorBrush(Color.FromArgb(0x88, 0xEA, 0x43, 0x35)),
                            cornerRadius: 9,
                            padding: new Thickness(0),
                            tooltip: L.T("widgets.carousel_remove_page"));
                        removeBtn.Tag = "CarouselNavControl";
                        removeBtn.Width = 18;
                        removeBtn.Height = 18;
                        removeBtn.HorizontalAlignment = HorizontalAlignment.Right;
                        removeBtn.VerticalAlignment = VerticalAlignment.Top;
                        removeBtn.Margin = new Thickness(0, 7, 7, 0);
                        Panel.SetZIndex(removeBtn, 20);
                        contentHost.Children.Add(removeBtn);
                    }
                }
                else
                {
                    var emptyStack = new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    emptyStack.Children.Add(NotchDesign.CreateVectorIcon(NotchIconKind.Plus, 18, NotchDesign.AccentPrimaryBrush, 1.9));
                    emptyStack.Children.Add(new TextBlock
                    {
                        Text = pages.Count == 0 ? L.T("widgets.carousel_empty") : L.F("widgets.carousel_add_page", pages.Count + 1),
                        Foreground = NotchDesign.TextPrimaryBrush,
                        FontFamily = NotchDesign.UiTextFont,
                        FontSize = 11.5,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 6, 0, 2)
                    });
                    emptyStack.Children.Add(new TextBlock
                    {
                        Text = isEdit ? L.T("widgets.carousel_drop_hint") : L.T("widgets.carousel_hold_hint"),
                        Foreground = NotchDesign.TextMutedBrush,
                        FontFamily = NotchDesign.UiTextFont,
                        FontSize = 10,
                        HorizontalAlignment = HorizontalAlignment.Center
                    });
                    contentHost.Children.Add(emptyStack);
                }

                // Punkte unten (inkl. "+"-Punkt im Edit-Mode)
                var hitBg = new SolidColorBrush(Color.FromArgb(0x01, 0x00, 0x00, 0x00));
                hitBg.Freeze();
                for (int i = 0; i < totalPages; i++)
                {
                    int idx = i;
                    bool isActive = idx == _customCarouselPageIndex;
                    bool isPlusDot = isEdit && idx == pages.Count;
                    var dot = new Border
                    {
                        Width = isActive ? 12 : 5,
                        Height = 5,
                        CornerRadius = new CornerRadius(2.5),
                        Background = isActive
                            ? NotchDesign.AccentPrimaryBrush
                            : isPlusDot
                                ? new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF))
                                : new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var hit = new Border
                    {
                        Tag = "CarouselNavControl",
                        Width = isActive ? 18 : 14,
                        Height = 16,
                        Background = hitBg,
                        Cursor = Cursors.Hand,
                        ToolTip = isPlusDot ? L.T("widgets.carousel_add_dot_tooltip") : L.F("widgets.carousel_page_tooltip", idx + 1),
                        Child = dot
                    };
                    hit.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;
                    hit.MouseLeftButtonUp += (_, e) =>
                    {
                        e.Handled = true;
                        _customCarouselPageIndex = idx;
                        RenderPage();
                    };
                    dotsPanel.Children.Add(hit);
                }
            }

            card.MouseWheel += (_, e) =>
            {
                var pages = SettingsService.Current.BentoCarouselPages;
                bool isEdit = _showStoreDrawer || (_jiggleController?.IsJiggleMode == true);
                int total = isEdit ? pages.Count + 1 : Math.Max(1, pages.Count);
                if (total <= 1) return;
                _customCarouselPageIndex = (_customCarouselPageIndex + (e.Delta > 0 ? total - 1 : 1)) % total;
                RenderPage();
                e.Handled = true;
            };

            _refreshCustomCarousel = RenderPage;
            RenderPage();
            return card;
        }

        // ---------- 8. Leerer Slot ----------

        private static Border BuildEmptySlotTile()
        {
            var card = CreateBentoAppCard(new Thickness(14));
            card.Opacity = 0.55;
            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(NotchDesign.CreateVectorIcon(NotchIconKind.Plus, 18, NotchDesign.TextMutedBrush, 1.8));
            stack.Children.Add(new TextBlock
            {
                Text = L.T("widgets.empty_slot"),
                Foreground = NotchDesign.TextMutedBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 2)
            });
            stack.Children.Add(new TextBlock
            {
                Text = L.T("widgets.empty_slot_hint"),
                Foreground = NotchDesign.TextMutedBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            card.Child = stack;
            return card;
        }
    }
}
