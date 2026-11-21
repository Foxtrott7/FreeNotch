using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WindowsNotch.Core;
using WindowsNotch.Services;
using WindowsNotch.Services.Database;

namespace WindowsNotch.Modules.Widgets
{
    public partial class WidgetsModule
    {
        // ---------- Kachel 4: Erweitertes 7-fach Carousel ----------

        private Border BuildCarouselTile()
        {
            var card = NotchDesign.CreateCard(16, new Thickness(14, 11, 14, 9));
            if (!NotchDesign.IsLight)
            {
                card.Background = new SolidColorBrush(NotchDesign.IsGlass
                    ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
                    : Color.FromRgb(0x16, 0x16, 0x1A));
            }
            _carCard = card;
            card.Cursor = Cursors.Hand;
            card.SizeChanged += (_, e) =>
            {
                if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
                var clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 16, 16);
                clip.Freeze();
                card.Clip = clip;
            };
            card.ToolTip = L.T("widgets.carousel_tooltip");

            card.MouseWheel += (_, e) =>
            {
                if (_jiggleController?.IsJiggleMode == true) return;
                _carouselIndex = (_carouselIndex + (e.Delta > 0 ? CarouselSlideCount - 1 : 1)) % CarouselSlideCount;
                UpdateCarouselVisual();
                e.Handled = true;
            };
            card.MouseLeftButtonUp += (_, _) =>
            {
                if (_jiggleController?.IsJiggleMode == true) return;

                if (_carTimerControls is { Visibility: Visibility.Visible, IsMouseOver: true }) return;
                if (_carSwControls is { Visibility: Visibility.Visible, IsMouseOver: true }) return;
                _carouselIndex = (_carouselIndex + 1) % CarouselSlideCount;
                UpdateCarouselVisual();
                foreach (var updater in _tileUpdaters) updater();
            };

            var root = new Grid();

            _carWatermarkHost = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, -8, 0),
                Opacity = 0.22,
                IsHitTestVisible = false
            };
            root.Children.Add(_carWatermarkHost);

            var dock = new DockPanel();

            var topStack = new StackPanel();
            var line1 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _carTopIconHost = new Border { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            _carLabelTop = new TextBlock
            {
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium
            };
            line1.Children.Add(_carTopIconHost);
            line1.Children.Add(_carLabelTop);

            _carSubTop = new TextBlock
            {
                Foreground = NotchDesign.TextMutedBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 0)
            };

            topStack.Children.Add(line1);
            topStack.Children.Add(_carSubTop);
            DockPanel.SetDock(topStack, Dock.Top);
            dock.Children.Add(topStack);

            // 7 Carousel-Punkte unten
            _carDotsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };
            for (int i = 0; i < CarouselSlideCount; i++)
            {
                int idx = i;
                var dotVisual = new Border
                {
                    Width = 5,
                    Height = 5,
                    CornerRadius = new CornerRadius(2.5),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var hitArea = new Border
                {
                    Width = 12,
                    Height = 14,
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Child = dotVisual
                };
                hitArea.MouseLeftButtonUp += (_, e) =>
                {
                    if (_jiggleController?.IsJiggleMode == true) return;
                    e.Handled = true;
                    _carouselIndex = idx;
                    UpdateCarouselVisual();
                    foreach (var updater in _tileUpdaters) updater();
                };
                _carDotsPanel.Children.Add(hitArea);
            }
            DockPanel.SetDock(_carDotsPanel, Dock.Bottom);
            dock.Children.Add(_carDotsPanel);

            // Unterer Inhaltsbereich: Großwert links + kontextuelle Steuerelemente rechts (Timer / Stoppuhr / Modus)
            var bottomContent = new DockPanel { VerticalAlignment = VerticalAlignment.Bottom, LastChildFill = false };

            _carMainVal = new TextBlock
            {
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 30,
                FontWeight = FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Typography.SetNumeralAlignment(_carMainVal, FontNumeralAlignment.Tabular);
            DockPanel.SetDock(_carMainVal, Dock.Left);
            bottomContent.Children.Add(_carMainVal);

            // Inline Fokus-Timer-Steuerung (Slide 2)
            _carTimerControls = BuildCarouselTimerControls();
            _carTimerControls.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(_carTimerControls, Dock.Right);
            bottomContent.Children.Add(_carTimerControls);

            // Inline Stoppuhr-Steuerung (Slide 3)
            _carSwControls = BuildCarouselStopwatchControls();
            _carSwControls.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(_carSwControls, Dock.Right);
            bottomContent.Children.Add(_carSwControls);

            dock.Children.Add(bottomContent);

            root.Children.Add(dock);
            card.Child = root;
            return card;
        }

        private StackPanel BuildCarouselTimerControls()
        {
            var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };

            var minusBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Minus, 9.5, NotchDesign.TextPrimaryBrush, 2.1),
                () => AdjustTimer(-1),
                background: NotchDesign.ControlBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                cornerRadius: 9, padding: new Thickness(0), tooltip: "-1 Min.");
            minusBtn.Width = 22;
            minusBtn.Height = 22;
            minusBtn.Margin = new Thickness(0, 0, 4, 0);

            _carTimerToggleBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Play, 9.5, Brushes.White, 2.2),
                ToggleTimer,
                background: NotchDesign.AccentPrimaryBrush,
                hoverBackground: NotchDesign.AccentHoverBrush,
                cornerRadius: 11, padding: new Thickness(0), tooltip: L.T("widgets.start_pause"));
            _carTimerToggleBtn.Width = 26;
            _carTimerToggleBtn.Height = 22;
            _carTimerToggleBtn.Margin = new Thickness(0, 0, 4, 0);

            var plusBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Plus, 9.5, NotchDesign.TextPrimaryBrush, 2.1),
                () => AdjustTimer(1),
                background: NotchDesign.ControlBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                cornerRadius: 9, padding: new Thickness(0), tooltip: L.T("widgets.timer_plus_min_short"));
            plusBtn.Width = 22;
            plusBtn.Height = 22;
            plusBtn.Margin = new Thickness(0, 0, 4, 0);

            var resetBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Reset, 9.5, NotchDesign.TextSecondaryBrush, 2.0),
                ResetTimer,
                background: Brushes.Transparent,
                hoverBackground: NotchDesign.ControlHoverBrush,
                cornerRadius: 9, padding: new Thickness(0), tooltip: L.T("widgets.reset"));
            resetBtn.Width = 21;
            resetBtn.Height = 22;

            stack.Children.Add(minusBtn);
            stack.Children.Add(_carTimerToggleBtn);
            stack.Children.Add(plusBtn);
            stack.Children.Add(resetBtn);
            return stack;
        }

        private StackPanel BuildCarouselStopwatchControls()
        {
            var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };

            _carSwToggleBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Play, 9.5, Brushes.White, 2.2),
                ToggleStopwatch,
                background: NotchDesign.AccentPrimaryBrush,
                hoverBackground: NotchDesign.AccentHoverBrush,
                cornerRadius: 11, padding: new Thickness(0), tooltip: L.T("widgets.start_pause"));
            _carSwToggleBtn.Width = 28;
            _carSwToggleBtn.Height = 22;
            _carSwToggleBtn.Margin = new Thickness(0, 0, 4, 0);

            var resetBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Reset, 9.5, NotchDesign.TextSecondaryBrush, 2.0),
                ResetStopwatch,
                background: NotchDesign.ControlBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                cornerRadius: 9, padding: new Thickness(0), tooltip: L.T("widgets.reset"));
            resetBtn.Width = 22;
            resetBtn.Height = 22;

            stack.Children.Add(_carSwToggleBtn);
            stack.Children.Add(resetBtn);
            return stack;
        }

        private void UpdateCarouselVisual()
        {
            if (_carLabelTop == null || _carSubTop == null || _carMainVal == null ||
                _carTopIconHost == null || _carWatermarkHost == null || _carDotsPanel == null)
                return;

            var now = DateTime.Now;
            NotchIconKind kind;
            string topLabel;
            string subLabel;
            string mainVal;

            double mainFontSize = 30;
            string? tooltip = L.T("widgets.carousel_tooltip");
            var culture = ActiveCulture;

            switch (_carouselIndex)
            {
                case 0:
                    kind = NotchIconKind.Clock;
                    topLabel = now.ToString("dddd", culture);
                    topLabel = char.ToUpper(topLabel[0], culture) + topLabel[1..];
                    subLabel = now.ToString("dd. MMMM", culture);
                    mainVal = now.ToString("HH:mm", culture);
                    break;
                case 1:
                    var ws = WeatherService.Instance;
                    if (!SettingsService.Current.OptInWeather)
                    {
                        kind = NotchIconKind.CloudSun;
                        topLabel = L.T("widgets.weather_optin_label");
                        subLabel = L.T("widgets.weather_enable_short");
                        mainVal = L.T("widgets.weather_off");
                        mainFontSize = 24;
                    }
                    else if (ws.Current is { } snap)
                    {
                        kind = snap.IconKind;
                        topLabel = snap.CityName;
                        subLabel = $"{snap.ConditionText} · {Math.Round(snap.TodayMinC):0}°/{Math.Round(snap.TodayMaxC):0}°";
                        mainVal = $"{Math.Round(snap.TemperatureC):0}°";
                        if (snap.ForecastDays.Count > 1)
                        {
                            var lines = snap.ForecastDays.Select(d =>
                                $"{d.Date.ToString("ddd", culture)}: {Math.Round(d.MinC):0}°..{Math.Round(d.MaxC):0}° {WeatherService.DescribeWmoCode(d.WeatherCode)}");
                            tooltip = string.Join("\n", lines);
                        }
                    }
                    else if (ws.IsLoading)
                    {
                        kind = NotchIconKind.CloudSun;
                        topLabel = L.T("widgets.weather_label");
                        subLabel = L.T("widgets.weather_loading_dots");
                        mainVal = "...";
                    }
                    else
                    {
                        kind = NotchIconKind.CloudSun;
                        topLabel = L.T("widgets.weather_label");
                        subLabel = ws.LastError ?? L.T("widgets.weather_no_data");
                        mainVal = "--";
                    }
                    break;
                case 2:
                    kind = NotchIconKind.Timer;
                    topLabel = L.T("widgets.timer_title");
                    subLabel = _timerFinished
                        ? L.T("widgets.carousel_timer_expired")
                        : _timerRunning
                            ? L.T("widgets.timer_running")
                            : L.F("widgets.carousel_timer_set", (int)_timerDuration.TotalMinutes);
                    mainVal = _timerFinished ? L.T("widgets.carousel_timer_done_val") : FormatDuration(_timerRemaining);
                    mainFontSize = _timerFinished ? 20 : 24;
                    break;
                case 3:
                    kind = NotchIconKind.Clock;
                    topLabel = L.T("widgets.stopwatch_title");
                    subLabel = _stopwatchRunning
                        ? L.T("widgets.carousel_sw_running")
                        : (_stopwatch.Elapsed > TimeSpan.Zero ? L.T("widgets.stopwatch_paused") : L.T("widgets.carousel_sw_ready"));
                    mainVal = FormatDuration(_stopwatch.Elapsed);
                    mainFontSize = 24;
                    break;
                case 4:
                    kind = NotchIconKind.Cpu;
                    topLabel = L.T("widgets.carousel_cpu");
                    subLabel = L.T("widgets.carousel_cpu_sub");
                    mainVal = $"{_cpuPercent}%";
                    break;
                default:
                    kind = NotchIconKind.Pulse;
                    topLabel = L.T("widgets.carousel_ram");
                    subLabel = _ramTotalGb > 0 ? $"{_ramUsedGb:0.0} / {_ramTotalGb:0.0} GB" : "RAM";
                    mainVal = $"{_ramPercent}%";
                    break;
            }

            if (_carCard != null) _carCard.ToolTip = tooltip;
            _carLabelTop.Text = topLabel;
            _carSubTop.Text = subLabel;
            _carMainVal.Text = mainVal;
            _carMainVal.FontSize = mainFontSize;

            bool isTimerSlide = _carouselIndex == 2;
            bool isSwSlide = _carouselIndex == 3;

            _carMainVal.Visibility = Visibility.Visible;
            if (_carTimerControls != null) _carTimerControls.Visibility = isTimerSlide ? Visibility.Visible : Visibility.Collapsed;
            if (_carSwControls != null) _carSwControls.Visibility = isSwSlide ? Visibility.Visible : Visibility.Collapsed;

            if (_carTimerToggleBtn != null)
            {
                if (_timerFinished)
                {
                    _carTimerToggleBtn.Tag = null;
                    _carTimerToggleBtn.Content = new TextBlock
                    {
                        Text = "OK",
                        Foreground = Brushes.White,
                        FontSize = 9,
                        FontWeight = FontWeights.Bold
                    };
                }
                else if (_carTimerToggleBtn.Tag as bool? != _timerRunning)
                {
                    _carTimerToggleBtn.Tag = _timerRunning;
                    _carTimerToggleBtn.Content = NotchDesign.CreateVectorIcon(
                        _timerRunning ? NotchIconKind.Pause : NotchIconKind.Play,
                        9.5,
                        Brushes.White,
                        2.2);
                }
            }

            if (_carSwToggleBtn != null && _carSwToggleBtn.Tag as bool? != _stopwatchRunning)
            {
                _carSwToggleBtn.Tag = _stopwatchRunning;
                _carSwToggleBtn.Content = NotchDesign.CreateVectorIcon(
                    _stopwatchRunning ? NotchIconKind.Pause : NotchIconKind.Play,
                    9.5,
                    Brushes.White,
                    2.2);
            }

            Brush topIconBrush = (_carouselIndex == 2 && _timerFinished)
                ? new SolidColorBrush(NotchDesign.AccentRed)
                : ((_carouselIndex == 2 && _timerRunning) || (_carouselIndex == 3 && _stopwatchRunning))
                    ? NotchDesign.AccentPrimaryBrush
                    : NotchDesign.TextSecondaryBrush;

            _carTopIconHost.Child = NotchDesign.CreateVectorIcon(kind, 11.5, topIconBrush, 1.85);
            _carWatermarkHost.Child = NotchDesign.CreateVectorIcon(kind, 64, NotchDesign.TextMutedBrush, 1.45);

            for (int i = 0; i < _carDotsPanel.Children.Count; i++)
            {
                if (_carDotsPanel.Children[i] is Border hitArea && hitArea.Child is Border dot)
                {
                    bool active = i == _carouselIndex;
                    dot.Width = active ? 6 : 4.5;
                    dot.Height = active ? 6 : 4.5;
                    dot.CornerRadius = new CornerRadius(active ? 3 : 2.25);
                    dot.Opacity = active ? 1.0 : 0.55;
                    dot.Background = active ? NotchDesign.AccentPrimaryBrush : NotchDesign.TextMutedBrush;
                }
            }
        }

        private void UpdateExpandedWidgets()
        {
            UpdateLyricsTile();
            UpdateMediaTile();
            UpdateCarouselVisual();
            foreach (var updater in _tileUpdaters) updater();
        }

        // ---------- Timer- & Stoppuhr-Aktionen ----------

        private void AdjustTimer(int deltaMinutes)
        {
            if (_timerRunning) return;
            _timerFinished = false;
            var next = _timerDuration + TimeSpan.FromMinutes(deltaMinutes);
            if (next < TimeSpan.FromMinutes(1)) next = TimeSpan.FromMinutes(1);
            if (next > TimeSpan.FromMinutes(180)) next = TimeSpan.FromMinutes(180);
            _timerDuration = next;
            _timerRemaining = next;
            UpdateExpandedWidgets();
            RaiseStateChanged();
        }

        private void ToggleTimer()
        {
            if (_timerFinished)
            {
                ResetTimer();
                return;
            }

            if (_timerRunning)
            {
                UpdateTimerState();
                _timerRunning = false;
            }
            else
            {
                if (_timerRemaining <= TimeSpan.Zero)
                    _timerRemaining = _timerDuration;
                _timerLastTickUtc = DateTime.UtcNow;
                _timerRunning = true;
            }

            UpdateExpandedWidgets();
            RaiseStateChanged();
        }

        private void ResetTimer()
        {
            _timerRunning = false;
            _timerFinished = false;
            _timerRemaining = _timerDuration;
            UpdateExpandedWidgets();
            RaiseStateChanged();
        }

        private void ToggleStopwatch()
        {
            if (_stopwatchRunning)
            {
                _stopwatch.Stop();
                _stopwatchRunning = false;
            }
            else
            {
                _stopwatch.Start();
                _stopwatchRunning = true;
            }

            UpdateExpandedWidgets();
            RaiseStateChanged();
        }

        private void ResetStopwatch()
        {
            _stopwatch.Reset();
            _stopwatchRunning = false;
            UpdateExpandedWidgets();
            RaiseStateChanged();
        }

    }
}
