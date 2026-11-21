using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.Weather
{
    public sealed class WeatherModule : NotchModuleBase
    {
        public override string Id => "weather";
        public override string Title => L.T("module.weather");
        public override NotchIconKind IconKind => NotchIconKind.CloudSun;
        public override (double Width, double Height)? PreferredExpandedSize => (450, 290);

        private bool _isSearchOpen;
        private CancellationTokenSource? _searchCts;
        private DispatcherTimer? _searchDebounce;

        public override FrameworkElement CreatePreviewView()
        {
            var svc = WeatherService.Instance;
            string? city = svc.GetActiveCity();
            var snap = svc.Current;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(NotchDesign.CreateVectorIcon(
                snap?.IconKind ?? NotchIconKind.CloudSun,
                13,
                NotchDesign.AccentPrimaryBrush,
                2.0));

            row.Children.Add(new TextBlock
            {
                Text = snap?.CityName ?? city ?? Title,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(7, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            if (SettingsService.Current.OptInWeather && snap != null)
            {
                var temp = new TextBlock
                {
                    Text = $"{snap.TemperatureC:0}°",
                    Foreground = NotchDesign.TextPrimaryBrush,
                    FontFamily = NotchDesign.UiDisplayFont,
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Typography.SetNumeralAlignment(temp, FontNumeralAlignment.Tabular);
                row.Children.Add(temp);

                row.Children.Add(new TextBlock
                {
                    Text = snap.ConditionText,
                    Foreground = NotchDesign.TextSecondaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            return row;
        }

        public override FrameworkElement CreateExpandedView()
        {
            _isSearchOpen = false;
            var root = new Grid();

            FrameworkElement? searchView = null;
            Action? updateCard = null;

            var focusCard = BuildFocusCard(
                onOpenSearch: () =>
                {
                    if (searchView == null) return;
                    _isSearchOpen = true;
                    searchView.Visibility = Visibility.Visible;
                    NotchDesign.AnimateSubViewEnter(searchView, slideY: 6, durationMs: NotchDesign.MicroTransitionMs);
                },
                out updateCard);

            searchView = BuildSearchView(
                onClose: () =>
                {
                    _isSearchOpen = false;
                    if (searchView != null) searchView.Visibility = Visibility.Collapsed;
                    updateCard?.Invoke();
                    NotchDesign.AnimateSubViewEnter(focusCard, slideY: 4, durationMs: NotchDesign.MicroTransitionMs);
                });
            searchView.Visibility = Visibility.Collapsed;

            root.Children.Add(focusCard);
            root.Children.Add(searchView);

            void OnChanged()
            {
                root.Dispatcher.BeginInvoke(() =>
                {
                    if (!root.IsLoaded || _isSearchOpen) return;
                    updateCard?.Invoke();
                });
            }

            root.Loaded += (_, _) =>
            {
                WeatherService.Instance.WeatherUpdated += OnChanged;
                WeatherService.Instance.CitiesChanged += OnChanged;
                updateCard?.Invoke();
                if (SettingsService.Current.OptInWeather)
                    _ = WeatherService.Instance.RefreshIfNeededAsync();
            };

            root.Unloaded += (_, _) =>
            {
                WeatherService.Instance.WeatherUpdated -= OnChanged;
                WeatherService.Instance.CitiesChanged -= OnChanged;
                _searchDebounce?.Stop();
                _searchCts?.Cancel();
            };

            return root;
        }

        private FrameworkElement BuildFocusCard(Action onOpenSearch, out Action updateCard)
        {
            var cardHost = new Border
            {
                CornerRadius = new CornerRadius(NotchDesign.RadiusCard),
                Background = Brushes.Transparent,
                ClipToBounds = true,
                Cursor = Cursors.Hand
            };

            var layers = new Grid();
            layers.SizeChanged += (_, _) =>
            {
                if (layers.ActualWidth > 0 && layers.ActualHeight > 0)
                {
                    layers.Clip = new RectangleGeometry(
                        new Rect(0, 0, layers.ActualWidth, layers.ActualHeight),
                        NotchDesign.RadiusCard,
                        NotchDesign.RadiusCard);
                }
            };
            var atmosphere = new WeatherAtmosphereLayer(compact: false);
            var frostOverlay = new Border
            {
                IsHitTestVisible = false,
                Background = new LinearGradientBrush(
                    Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
                    Color.FromArgb(0x04, 0xFF, 0xFF, 0xFF),
                    90)
            };
            layers.Children.Add(atmosphere);
            layers.Children.Add(frostOverlay);

            var mainGrid = new Grid { Margin = new Thickness(16, 13, 16, 13) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Zeile 0: Stadt + Zustand/Zähler links, Such-Icon rechts
            var topGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            var titleIconHost = new Border
            {
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            var cityText = new TextBlock
            {
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 15.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 220
            };
            var atmosphericSecondaryBrush = NotchDesign.GetFrozenBrush(NotchDesign.IsLight
                ? Color.FromArgb(0xC8, 0x14, 0x14, 0x18)
                : Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF));

            var subMetaText = new TextBlock
            {
                Foreground = atmosphericSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                Margin = new Thickness(8, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titleStack.Children.Add(titleIconHost);
            titleStack.Children.Add(cityText);
            titleStack.Children.Add(subMetaText);
            Grid.SetColumn(titleStack, 0);
            topGrid.Children.Add(titleStack);

            var searchBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Search, 12, NotchDesign.TextPrimaryBrush, 1.85),
                onOpenSearch,
                background: NotchDesign.ControlBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                cornerRadius: 13,
                padding: new Thickness(0),
                tooltip: L.T("weather.search_tooltip"));
            searchBtn.Width = 26;
            searchBtn.Height = 26;
            Grid.SetColumn(searchBtn, 1);
            topGrid.Children.Add(searchBtn);

            Grid.SetRow(topGrid, 0);
            mainGrid.Children.Add(topGrid);

            // Zeile 1: Temperatur + Tages-Range links, Wetter-Icon rechts
            var midGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            midGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            midGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tempStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
            var tempText = new TextBlock
            {
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 36,
                FontWeight = FontWeights.Normal
            };
            Typography.SetNumeralAlignment(tempText, FontNumeralAlignment.Tabular);

            var rangeText = new TextBlock
            {
                Foreground = atmosphericSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12.5,
                Margin = new Thickness(12, 0, 0, 7)
            };
            Typography.SetNumeralAlignment(rangeText, FontNumeralAlignment.Tabular);

            tempStack.Children.Add(tempText);
            tempStack.Children.Add(rangeText);
            Grid.SetColumn(tempStack, 0);
            midGrid.Children.Add(tempStack);

            var weatherIconHost = new Border { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
            Grid.SetColumn(weatherIconHost, 1);
            midGrid.Children.Add(weatherIconHost);

            Grid.SetRow(midGrid, 1);
            mainGrid.Children.Add(midGrid);

            // Zeile 2: Entweder Opt-In-Bereich (ohne Logo auf Button) oder 3-Tage-Vorhersage am unteren Rand
            var bottomHost = new Grid();
            Grid.SetRow(bottomHost, 2);
            mainGrid.Children.Add(bottomHost);

            var optInPanel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = Visibility.Collapsed
            };
            optInPanel.Children.Add(new TextBlock
            {
                Text = L.T("weather.optin_desc"),
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 360,
                Margin = new Thickness(0, 0, 0, 12)
            });
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
                padding: new Thickness(14, 7, 14, 7));
            optInBtn.HorizontalAlignment = HorizontalAlignment.Left;
            optInPanel.Children.Add(optInBtn);

            var forecastGrid = new Grid
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 10, 0, 0)
            };
            for (int c = 0; c < 3; c++)
                forecastGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var statusText = new TextBlock
            {
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            bottomHost.Children.Add(optInPanel);
            bottomHost.Children.Add(forecastGrid);
            bottomHost.Children.Add(statusText);

            layers.Children.Add(mainGrid);
            cardHost.Child = layers;
            AttachSubtleCardPress(cardHost, searchBtn);

            // Klick auf freie Kartenfläche -> nächste Stadt (wie im Finance-Tab)
            Point downPt = default;
            cardHost.MouseLeftButtonDown += (_, e) => downPt = e.GetPosition(cardHost);
            cardHost.MouseLeftButtonUp += (_, e) =>
            {
                if (!SettingsService.Current.OptInWeather) return;
                if (IsInsideElement(e.OriginalSource as DependencyObject, searchBtn)) return;
                Point upPt = e.GetPosition(cardHost);
                if (Math.Abs(upPt.X - downPt.X) > 6 || Math.Abs(upPt.Y - downPt.Y) > 6) return;
                if (WeatherService.Instance.GetCities().Count <= 1) return;

                WeatherService.Instance.CycleCity(1);
                NotchDesign.AnimateSubViewEnter(mainGrid, slideY: 4, durationMs: NotchDesign.MicroTransitionMs);
            };

            void Update()
            {
                var s = SettingsService.Current;
                var svc = WeatherService.Instance;
                var cities = svc.GetCities();
                string? activeCity = svc.GetActiveCity();

                if (!s.OptInWeather)
                {
                    atmosphere.SetWeather(null, true);
                    frostOverlay.Visibility = Visibility.Collapsed;
                    cardHost.Cursor = Cursors.Arrow;
                    cardHost.ToolTip = null;
                    searchBtn.Visibility = Visibility.Collapsed;

                    titleIconHost.Child = NotchDesign.CreateVectorIcon(NotchIconKind.CloudSun, 15, NotchDesign.AccentPrimaryBrush, 1.9);
                    titleIconHost.Visibility = Visibility.Visible;
                    cityText.Text = Title;
                    subMetaText.Text = string.Empty;

                    midGrid.Visibility = Visibility.Collapsed;
                    forecastGrid.Visibility = Visibility.Collapsed;
                    statusText.Visibility = Visibility.Collapsed;
                    optInPanel.Visibility = Visibility.Visible;
                    return;
                }

                bool canCycle = cities.Count > 1;
                frostOverlay.Visibility = Visibility.Visible;
                cardHost.Cursor = canCycle ? Cursors.Hand : Cursors.Arrow;
                cardHost.ToolTip = canCycle ? L.T("widgets.weather.city_switch_tooltip") : null;
                searchBtn.Visibility = Visibility.Visible;
                titleIconHost.Visibility = Visibility.Collapsed;
                optInPanel.Visibility = Visibility.Collapsed;

                var snap = svc.Current;
                cityText.Text = snap?.CityName ?? activeCity ?? L.T("widgets.weather.no_city");

                if (snap == null)
                {
                    atmosphere.SetWeather(null, true);
                    subMetaText.Text = string.Empty;
                    midGrid.Visibility = Visibility.Collapsed;
                    forecastGrid.Visibility = Visibility.Collapsed;
                    statusText.Text = svc.IsLoading
                        ? L.T("widgets.weather.loading")
                        : (svc.LastError ?? L.T("weather.no_city_hint"));
                    statusText.Visibility = Visibility.Visible;
                    return;
                }

                atmosphere.SetWeather(snap.WeatherCode, snap.IsDay, snap.PrecipitationMm, snap.CloudCoverPercent);
                int activeIdx = Math.Max(0, cities.ToList().FindIndex(c => string.Equals(c, activeCity, StringComparison.OrdinalIgnoreCase)));
                subMetaText.Text = canCycle
                    ? $"{snap.ConditionText} · {activeIdx + 1}/{cities.Count}"
                    : snap.ConditionText;

                statusText.Visibility = Visibility.Collapsed;
                midGrid.Visibility = Visibility.Visible;
                forecastGrid.Visibility = Visibility.Visible;

                tempText.Text = $"{snap.TemperatureC:0}°";
                rangeText.Text = $"H {snap.TodayMaxC:0}° · T {snap.TodayMinC:0}°";
                weatherIconHost.Child = NotchDesign.CreateVectorIcon(snap.IconKind, 28, NotchDesign.TextPrimaryBrush, 1.7);

                forecastGrid.Children.Clear();
                var culture = LocalizationService.NormalizeCode(s.Language) == "de"
                    ? CultureInfo.GetCultureInfo("de-DE")
                    : CultureInfo.GetCultureInfo("en-US");

                var tileBg = new SolidColorBrush(NotchDesign.IsLight
                    ? Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0x28, 0x00, 0x00, 0x00));
                tileBg.Freeze();

                for (int i = 0; i < Math.Min(3, snap.ForecastDays.Count); i++)
                {
                    var d = snap.ForecastDays[i];
                    var dayCard = new Border
                    {
                        Background = tileBg,
                        CornerRadius = new CornerRadius(NotchDesign.RadiusInner),
                        Padding = new Thickness(10, 7, 10, 7),
                        Margin = new Thickness(i == 0 ? 0 : 4, 0, i == 2 ? 0 : 4, 0)
                    };
                    var dayStack = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    dayStack.Children.Add(new TextBlock
                    {
                        Text = i == 0 ? L.T("weather.today") : d.Date.ToString("ddd", culture),
                        Foreground = NotchDesign.TextSecondaryBrush,
                        FontFamily = NotchDesign.UiTextFont,
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 6, 0)
                    });
                    dayStack.Children.Add(new Border
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 6, 0),
                        Child = NotchDesign.CreateVectorIcon(WeatherService.MapWmoToIcon(d.WeatherCode, true), 13, NotchDesign.TextPrimaryBrush, 1.7)
                    });
                    var range = new TextBlock
                    {
                        Text = $"{d.MaxC:0}°/{d.MinC:0}°",
                        Foreground = NotchDesign.TextPrimaryBrush,
                        FontFamily = NotchDesign.UiTextFont,
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Typography.SetNumeralAlignment(range, FontNumeralAlignment.Tabular);
                    dayStack.Children.Add(range);
                    dayCard.Child = dayStack;
                    Grid.SetColumn(dayCard, i);
                    forecastGrid.Children.Add(dayCard);
                }
            }

            updateCard = Update;
            Update();
            return cardHost;
        }

        private FrameworkElement BuildSearchView(Action onClose)
        {
            var container = new Grid { Background = NotchDesign.NotchBackgroundBrush };
            container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Oben: Suchleiste + Schließen-Button (analog FinanceModule)
            var topBar = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var searchBorder = new Border
            {
                Background = NotchDesign.NestedCardBackgroundBrush,
                CornerRadius = new CornerRadius(NotchDesign.RadiusControl),
                Padding = new Thickness(10, 6, 10, 6)
            };

            var searchGrid = new Grid();
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = NotchDesign.CreateVectorIcon(NotchIconKind.Search, 13, NotchDesign.TextSecondaryBrush);
            icon.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(icon, 0);
            searchGrid.Children.Add(icon);

            var boxHost = new Grid();
            var placeholder = new TextBlock
            {
                Text = L.T("weather.search_placeholder"),
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Opacity = 0.7
            };
            var searchBox = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = NotchDesign.TextPrimaryBrush,
                CaretBrush = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12.5,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            boxHost.Children.Add(placeholder);
            boxHost.Children.Add(searchBox);
            Grid.SetColumn(boxHost, 1);
            searchGrid.Children.Add(boxHost);
            searchBorder.Child = searchGrid;
            Grid.SetColumn(searchBorder, 0);
            topBar.Children.Add(searchBorder);

            var closeBtn = NotchDesign.CreateIconActionButton(
                NotchIconKind.Close,
                L.T("finance.search_close_tooltip"),
                onClose,
                size: 28,
                iconSize: 12);
            closeBtn.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(closeBtn, 1);
            topBar.Children.Add(closeBtn);

            Grid.SetRow(topBar, 0);
            container.Children.Add(topBar);

            var resultsPanel = new StackPanel();
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = Brushes.Transparent,
                Content = resultsPanel
            };
            NotchDesign.ApplySlimScrollBarStyle(scroll);
            Grid.SetRow(scroll, 1);
            container.Children.Add(scroll);

            void ShowStatusText(string text)
            {
                resultsPanel.Children.Clear();
                resultsPanel.Children.Add(new TextBlock
                {
                    Text = text,
                    Foreground = NotchDesign.TextSecondaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 12,
                    Margin = new Thickness(4, 14, 4, 0),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

            void RenderList(IReadOnlyList<WeatherCitySearchResult> items, string emptyText)
            {
                resultsPanel.Children.Clear();
                if (items.Count == 0)
                {
                    ShowStatusText(emptyText);
                    return;
                }

                foreach (var item in items)
                {
                    var rowBorder = new Border
                    {
                        Background = Brushes.Transparent,
                        CornerRadius = new CornerRadius(NotchDesign.RadiusControl),
                        Padding = new Thickness(8, 6, 6, 6),
                        Margin = new Thickness(0, 0, 0, 2),
                        Cursor = Cursors.Hand
                    };
                    rowBorder.MouseEnter += (_, _) => rowBorder.Background = NotchDesign.ControlHoverBrush;
                    rowBorder.MouseLeave += (_, _) => rowBorder.Background = Brushes.Transparent;

                    var rowGrid = new Grid();
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var leftStack = new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    leftStack.Children.Add(new TextBlock
                    {
                        Text = item.Name,
                        Foreground = NotchDesign.TextPrimaryBrush,
                        FontFamily = NotchDesign.UiDisplayFont,
                        FontSize = 12.5,
                        FontWeight = FontWeights.SemiBold
                    });
                    if (!string.IsNullOrWhiteSpace(item.SubLabel))
                    {
                        leftStack.Children.Add(new TextBlock
                        {
                            Text = item.SubLabel,
                            Foreground = NotchDesign.TextSecondaryBrush,
                            FontFamily = NotchDesign.UiTextFont,
                            FontSize = 11,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        });
                    }
                    Grid.SetColumn(leftStack, 0);
                    rowGrid.Children.Add(leftStack);

                    bool isFav = WeatherService.Instance.IsFavoriteCity(item.Name);
                    double? lat = item.Latitude == 0 && item.Longitude == 0 ? null : item.Latitude;
                    double? lon = item.Latitude == 0 && item.Longitude == 0 ? null : item.Longitude;

                    Button? starBtn = null;
                    starBtn = NotchDesign.CreateRoundedButton(
                        NotchDesign.CreateVectorIcon(
                            NotchIconKind.Star,
                            13,
                            isFav ? NotchDesign.AccentPrimaryBrush : NotchDesign.TextSecondaryBrush,
                            1.9),
                        () =>
                        {
                            bool nowFav = WeatherService.Instance.ToggleCity(item.Name, lat, lon, item.Name);
                            if (string.IsNullOrWhiteSpace(searchBox.Text))
                            {
                                RenderSavedCities();
                                return;
                            }
                            if (starBtn != null)
                            {
                                starBtn.Content = NotchDesign.CreateVectorIcon(
                                    NotchIconKind.Star,
                                    13,
                                    nowFav ? NotchDesign.AccentPrimaryBrush : NotchDesign.TextSecondaryBrush,
                                    1.9);
                                starBtn.Background = nowFav
                                    ? NotchDesign.CreateSoftAccentTileBrush(0x28)
                                    : Brushes.Transparent;
                                starBtn.ToolTip = nowFav ? L.T("finance.star_remove") : L.T("finance.star_add");
                            }
                        },
                        background: isFav ? NotchDesign.CreateSoftAccentTileBrush(0x28) : Brushes.Transparent,
                        hoverBackground: NotchDesign.CreateSoftAccentTileBrush(0x38),
                        cornerRadius: 14,
                        padding: new Thickness(0),
                        tooltip: isFav ? L.T("finance.star_remove") : L.T("finance.star_add"));
                    starBtn.Width = 28;
                    starBtn.Height = 28;
                    Grid.SetColumn(starBtn, 1);
                    rowGrid.Children.Add(starBtn);

                    rowBorder.Child = rowGrid;
                    rowBorder.MouseLeftButtonUp += (_, e) =>
                    {
                        if (IsInsideElement(e.OriginalSource as DependencyObject, starBtn)) return;
                        if (!WeatherService.Instance.IsFavoriteCity(item.Name))
                            WeatherService.Instance.AddCity(item.Name, lat, lon, item.Name);
                        else
                            WeatherService.Instance.SetActiveCity(item.Name);
                        onClose();
                    };

                    resultsPanel.Children.Add(rowBorder);
                }
            }

            void RenderSavedCities()
            {
                var items = WeatherService.Instance.GetCities()
                    .Select(c => new WeatherCitySearchResult { Name = c })
                    .ToList();
                RenderList(items, L.T("weather.search_empty"));
            }

            async Task RunSearchAsync(string query)
            {
                _searchCts?.Cancel();
                _searchCts = new CancellationTokenSource();
                var ct = _searchCts.Token;

                if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
                {
                    RenderSavedCities();
                    return;
                }

                var hits = await WeatherService.Instance.SearchCitiesAsync(query, ct);
                if (ct.IsCancellationRequested) return;

                RenderList(hits, L.T("weather.search_no_results"));
            }

            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _searchDebounce.Tick += (_, _) =>
            {
                _searchDebounce.Stop();
                _ = RunSearchAsync(searchBox.Text);
            };
            searchBox.TextChanged += (_, _) =>
            {
                placeholder.Visibility = string.IsNullOrEmpty(searchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
                _searchDebounce.Stop();
                _searchDebounce.Start();
            };

            container.IsVisibleChanged += (_, _) =>
            {
                if (container.Visibility == Visibility.Visible)
                {
                    searchBox.Text = string.Empty;
                    RenderSavedCities();
                    container.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                    {
                        searchBox.Focus();
                        Keyboard.Focus(searchBox);
                    });
                }
            };

            return container;
        }

        private static void AttachSubtleCardPress(Border card, DependencyObject? excludedControl = null)
        {
            var scale = new ScaleTransform(1.0, 1.0);
            card.RenderTransformOrigin = new Point(0.5, 0.5);
            card.RenderTransform = scale;

            void AnimateScale(double target, int durationMs, IEasingFunction ease)
            {
                if (!NotchDesign.EffectiveAnimationsEnabled)
                {
                    scale.ScaleX = target;
                    scale.ScaleY = target;
                    return;
                }
                var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(durationMs))
                {
                    EasingFunction = ease
                };
                Timeline.SetDesiredFrameRate(anim, SettingsService.Current.TargetFrameRate);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }

            card.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (!SettingsService.Current.OptInWeather) return;
                if (excludedControl != null && IsInsideElement(e.OriginalSource as DependencyObject, excludedControl)) return;
                AnimateScale(0.99, NotchDesign.ButtonPressDurationMs, NotchDesign.CreateEaseOut());
            };
            card.PreviewMouseLeftButtonUp += (_, _) =>
                AnimateScale(1.0, NotchDesign.BentoPressReleaseMs, NotchDesign.CreateSpringEase(0.30));
            card.MouseLeave += (_, _) =>
                AnimateScale(1.0, NotchDesign.ButtonReleaseDurationMs, NotchDesign.CreateEaseOut());
        }

        private static bool IsInsideElement(DependencyObject? source, DependencyObject? target)
        {
            while (source != null)
            {
                if (ReferenceEquals(source, target)) return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }
    }
}
