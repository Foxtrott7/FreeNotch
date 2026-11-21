using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.Finance
{
    public sealed class FinanceModule : NotchModuleBase
    {
        public override string Id => "finance";
        public override string Title => L.T("module.finance");
        public override NotchIconKind IconKind => NotchIconKind.TrendingUp;
        public override (double Width, double Height)? PreferredExpandedSize => (460, 290);

        private bool _isSearchOpen;
        private CancellationTokenSource? _searchCts;
        private DispatcherTimer? _searchDebounce;
        private EventHandler? _liveRenderHandler;

        public override FrameworkElement CreatePreviewView()
        {
            var svc = FinanceService.Instance;
            string? sym = svc.GetActiveSymbol();
            var quote = svc.GetCachedQuote(sym);

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(NotchDesign.CreateVectorIcon(NotchIconKind.TrendingUp, 13, NotchDesign.AccentPrimaryBrush, 2.0));

            row.Children.Add(new TextBlock
            {
                Text = sym ?? Title,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(7, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            if (quote is { Price: > 0 })
            {
                var priceText = new TextBlock
                {
                    Text = FinanceService.FormatPrice(quote.Price, quote.Currency),
                    Foreground = NotchDesign.TextPrimaryBrush,
                    FontFamily = NotchDesign.UiDisplayFont,
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Typography.SetNumeralAlignment(priceText, FontNumeralAlignment.Tabular);
                row.Children.Add(priceText);

                double pct = quote.ChangePercent;
                var pctText = new TextBlock
                {
                    Text = FinanceService.FormatChangePercent(pct),
                    Foreground = new SolidColorBrush(pct >= 0 ? NotchDesign.AccentGreen : NotchDesign.AccentRed),
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Typography.SetNumeralAlignment(pctText, FontNumeralAlignment.Tabular);
                row.Children.Add(pctText);
            }

            return row;
        }

        public override bool HandleEscape()
        {
            if (!_isSearchOpen) return false;
            _isSearchOpen = false;
            RaiseStateChanged();
            return true;
        }

        public override FrameworkElement CreateExpandedView()
        {
            _isSearchOpen = false;

            // Direkt auf OLED-Schwarz ohne Modul-Überschrift (analog FileTray / QuickActions)
            var root = new Grid { Background = Brushes.Transparent };

            Action? updateCard = null;
            Action? openSearch = null;
            Action? closeSearch = null;

            var cardView = BuildSingleFocusCard(
                () => openSearch?.Invoke(),
                out updateCard);

            var searchView = BuildSearchView(() => closeSearch?.Invoke());

            void ShowActiveMode(bool animate)
            {
                root.Children.Clear();
                UIElement target = _isSearchOpen ? searchView : cardView;
                root.Children.Add(target);
                if (animate)
                    NotchDesign.AnimateSubViewEnter(target, slideY: 6, durationMs: NotchDesign.MicroTransitionMs);
            }

            openSearch = () =>
            {
                if (_isSearchOpen) return;
                _isSearchOpen = true;
                ShowActiveMode(animate: true);
            };

            closeSearch = () =>
            {
                if (!_isSearchOpen) return;
                _isSearchOpen = false;
                ShowActiveMode(animate: true);
                updateCard?.Invoke();
            };

            ShowActiveMode(animate: false);
            updateCard?.Invoke();

            void OnQuoteUpdated(string symbol)
            {
                root.Dispatcher.BeginInvoke(() =>
                {
                    if (!root.IsLoaded || _isSearchOpen) return;
                    if (string.Equals(symbol, FinanceService.Instance.GetActiveSymbol(), StringComparison.OrdinalIgnoreCase))
                        updateCard?.Invoke();
                });
            }

            void OnFavoritesChanged()
            {
                root.Dispatcher.BeginInvoke(() =>
                {
                    if (!root.IsLoaded) return;
                    if (!_isSearchOpen)
                        updateCard?.Invoke();
                });
            }

            root.Loaded += (_, _) =>
            {
                FinanceService.Instance.QuoteUpdated += OnQuoteUpdated;
                FinanceService.Instance.FavoritesChanged += OnFavoritesChanged;
                FinanceService.Instance.StartTracking();
                string? active = FinanceService.Instance.GetActiveSymbol();
                if (!string.IsNullOrEmpty(active))
                    _ = FinanceService.Instance.EnsureSymbolAsync(active, SettingsService.Current.FinanceRange, force: false);
                updateCard?.Invoke();
            };

            root.Unloaded += (_, _) =>
            {
                FinanceService.Instance.QuoteUpdated -= OnQuoteUpdated;
                FinanceService.Instance.FavoritesChanged -= OnFavoritesChanged;
                FinanceService.Instance.StopTracking();
                _searchCts?.Cancel();
                _searchDebounce?.Stop();
                if (_liveRenderHandler != null)
                {
                    CompositionTarget.Rendering -= _liveRenderHandler;
                    _liveRenderHandler = null;
                }
            };

            return root;
        }

        private FrameworkElement BuildSingleFocusCard(Action onOpenSearch, out Action updateCard)
        {
            var cardHost = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(NotchDesign.RadiusCard),
                Padding = new Thickness(4, 2, 4, 2),
                Cursor = Cursors.Hand,
                ToolTip = L.T("finance.cycle_tooltip")
            };
            AttachSubtleCardPress(cardHost);

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Symbol + Range + Search
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Price + %
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Sparkline

            // Zeile 0: Ticker + Name links, Zeitraum-Zyklus-Button + Such-Icon rechts
            var topGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var symbolText = new TextBlock
            {
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 15.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            var nameText = new TextBlock
            {
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                Margin = new Thickness(8, 1, 0, 0),
                MaxWidth = 135,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var liveDot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Margin = new Thickness(7, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            titleStack.Children.Add(symbolText);
            titleStack.Children.Add(nameText);
            titleStack.Children.Add(liveDot);
            Grid.SetColumn(titleStack, 0);
            topGrid.Children.Add(titleStack);

            var controlsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            Action? refreshLocal = null;
            var rangeCycleText = new TextBlock
            {
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            };
            var rangeCycleBtn = NotchDesign.CreateRoundedButton(
                rangeCycleText,
                () =>
                {
                    FinanceService.Instance.CycleRange();
                    refreshLocal?.Invoke();
                },
                cornerRadius: NotchDesign.RadiusSmall,
                padding: new Thickness(9, 3, 9, 3));
            rangeCycleBtn.ToolTip = L.T("finance.range_cycle_tooltip");
            controlsRow.Children.Add(rangeCycleBtn);

            var searchBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Search, 12, NotchDesign.TextPrimaryBrush, 1.85),
                onOpenSearch,
                background: NotchDesign.ControlBackgroundBrush,
                cornerRadius: 13,
                padding: new Thickness(0),
                tooltip: L.T("finance.search_tooltip"));
            searchBtn.Width = 26;
            searchBtn.Height = 26;
            searchBtn.Margin = new Thickness(8, 0, 0, 0);
            controlsRow.Children.Add(searchBtn);

            Grid.SetColumn(controlsRow, 1);
            topGrid.Children.Add(controlsRow);
            Grid.SetRow(topGrid, 0);
            mainGrid.Children.Add(topGrid);

            // Zeile 1: Kurs + Tagesveränderung (%)
            var priceRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 10)
            };
            var priceText = new TextBlock
            {
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 31,
                FontWeight = FontWeights.Normal
            };
            Typography.SetNumeralAlignment(priceText, FontNumeralAlignment.Tabular);

            var changeText = new TextBlock
            {
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(10, 0, 0, 5)
            };
            Typography.SetNumeralAlignment(changeText, FontNumeralAlignment.Tabular);

            priceRow.Children.Add(priceText);
            priceRow.Children.Add(changeText);
            Grid.SetRow(priceRow, 1);
            mainGrid.Children.Add(priceRow);

            // Zeile 2: Großer Sparkline-Bereich mit ausreichend Platz nach oben/unten
            var sparkHost = new Grid
            {
                MinHeight = 115,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 2, 0, 2),
                ClipToBounds = true
            };
            var emptyHint = new TextBlock
            {
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            sparkHost.Children.Add(emptyHint);
            Grid.SetRow(sparkHost, 2);
            mainGrid.Children.Add(sparkHost);

            cardHost.Child = mainGrid;

            // Klick auf freie Kartenfläche -> nächster Favorit
            Point downPt = default;
            cardHost.MouseLeftButtonDown += (_, e) => downPt = e.GetPosition(cardHost);
            cardHost.MouseLeftButtonUp += (_, e) =>
            {
                if (IsInsideElement(e.OriginalSource as DependencyObject, controlsRow)) return;
                Point upPt = e.GetPosition(cardHost);
                if (Math.Abs(upPt.X - downPt.X) > 6 || Math.Abs(upPt.Y - downPt.Y) > 6) return;
                if (FinanceService.Instance.GetFavorites().Count <= 1) return;

                FinanceService.Instance.CycleFavorite(1);
                NotchDesign.AnimateSubViewEnter(mainGrid, slideY: 4, durationMs: NotchDesign.MicroTransitionMs);
            };

            void Update()
            {
                var svc = FinanceService.Instance;
                var favs = svc.GetFavorites();
                string activeRange = SettingsService.Current.FinanceRange;

                string rangeLabelKey = activeRange switch
                {
                    "LIVE" => "finance.range_live",
                    "1W" => "finance.range_1w",
                    "1M" => "finance.range_1m",
                    "1Y" => "finance.range_1y",
                    "5Y" => "finance.range_5y",
                    "ALL" => "finance.range_all",
                    _ => "finance.range_1d"
                };
                rangeCycleText.Text = L.T(rangeLabelKey);
                rangeCycleBtn.Background = NotchDesign.ControlBackgroundBrush;
                rangeCycleBtn.Foreground = NotchDesign.TextPrimaryBrush;

                if (favs.Count == 0)
                {
                    symbolText.Text = L.T("finance.no_favorites");
                    nameText.Text = string.Empty;
                    liveDot.Visibility = Visibility.Collapsed;
                    priceText.Text = "—";
                    changeText.Text = string.Empty;
                    ClearSparkline(sparkHost);
                    emptyHint.Text = L.T("finance.add_favorite_hint");
                    emptyHint.Visibility = Visibility.Visible;
                    return;
                }

                emptyHint.Visibility = Visibility.Collapsed;
                string activeSym = svc.GetActiveSymbol() ?? favs[0];
                var quote = svc.GetCachedQuote(activeSym);
                symbolText.Text = activeSym;

                if (quote == null || quote.Price <= 0)
                {
                    nameText.Text = L.T("finance.loading");
                    liveDot.Visibility = Visibility.Collapsed;
                    priceText.Text = "—";
                    changeText.Text = string.Empty;
                    ClearSparkline(sparkHost);
                    return;
                }

                nameText.Text = string.IsNullOrWhiteSpace(quote.Name) ? string.Empty : quote.Name;
                if (quote.IsLiveStream || activeRange == "LIVE")
                {
                    liveDot.Fill = new SolidColorBrush(NotchDesign.AccentGreen);
                    liveDot.ToolTip = L.T("finance.live_ws");
                    liveDot.Visibility = Visibility.Visible;
                }
                else if (quote.IsStale)
                {
                    liveDot.Fill = new SolidColorBrush(NotchDesign.AccentOrange);
                    liveDot.ToolTip = L.T("finance.cached_offline");
                    liveDot.Visibility = Visibility.Visible;
                }
                else
                {
                    liveDot.Visibility = Visibility.Collapsed;
                }

                priceText.Text = FinanceService.FormatPrice(quote.Price, quote.Currency);
                double pct = quote.ChangePercent;
                bool isUp = pct >= 0;
                changeText.Text = FinanceService.FormatChangePercent(pct);
                changeText.Foreground = new SolidColorBrush(isUp ? NotchDesign.AccentGreen : NotchDesign.AccentRed);

                if (activeRange != "LIVE")
                {
                    IReadOnlyList<double>? points = quote.Sparklines.TryGetValue(activeRange, out var sp) ? sp : null;
                    RenderSparkline(sparkHost, points, isUp ? NotchDesign.AccentGreen : NotchDesign.AccentRed, strokeThickness: 2.0, padY: 10);
                }
            }

            // Smooth 60-240 Hz Live-Interpolation für den LIVE-Graphen
            string lastLiveSymbol = string.Empty;
            double liveRenderPrice = 0;
            double smoothMin = 0;
            double smoothMax = 0;
            var frameClock = System.Diagnostics.Stopwatch.StartNew();
            double lastFrameSec = 0;

            void RenderLiveFrame(bool instant)
            {
                if (!cardHost.IsLoaded || SettingsService.Current.FinanceRange != "LIVE") return;
                string? sym = FinanceService.Instance.GetActiveSymbol();
                var quote = FinanceService.Instance.GetCachedQuote(sym);
                if (sym == null || quote == null || quote.Price <= 0) return;

                List<double> raw;
                lock (quote)
                {
                    raw = quote.Sparklines.TryGetValue("LIVE", out var sp) && sp.Count >= 2
                        ? new List<double>(sp)
                        : new List<double> { quote.Price, quote.Price };
                }

                if (!string.Equals(lastLiveSymbol, sym, StringComparison.OrdinalIgnoreCase) || liveRenderPrice <= 0 || instant)
                {
                    lastLiveSymbol = sym;
                    liveRenderPrice = quote.Price;
                    ComputeBufferedBounds(raw, out smoothMin, out smoothMax, minPctSpan: 0.0005);
                }
                else
                {
                    double nowSec = frameClock.Elapsed.TotalSeconds;
                    double dt = Math.Clamp(nowSec - lastFrameSec, 0.001, 0.05);
                    lastFrameSec = nowSec;

                    double alpha = NotchDesign.EffectiveAnimationsEnabled ? 1.0 - Math.Exp(-dt * 9.0) : 1.0;
                    liveRenderPrice += (quote.Price - liveRenderPrice) * alpha;
                    raw[^1] = liveRenderPrice;

                    ComputeBufferedBounds(raw, out double targetMin, out double targetMax, minPctSpan: 0.0005);
                    double boundAlpha = NotchDesign.EffectiveAnimationsEnabled ? 1.0 - Math.Exp(-dt * 5.0) : 1.0;
                    smoothMin += (targetMin - smoothMin) * boundAlpha;
                    smoothMax += (targetMax - smoothMax) * boundAlpha;
                }

                raw[^1] = liveRenderPrice;
                bool isUp = quote.ChangePercent >= 0;
                RenderSparkline(
                    sparkHost,
                    raw,
                    isUp ? NotchDesign.AccentGreen : NotchDesign.AccentRed,
                    strokeThickness: 2.1,
                    padY: 12,
                    overrideMin: smoothMin,
                    overrideMax: smoothMax,
                    showLiveTip: true,
                    fillAlpha: 0x2E);
            }

            if (_liveRenderHandler != null)
                CompositionTarget.Rendering -= _liveRenderHandler;
            _liveRenderHandler = (_, _) =>
            {
                if (SettingsService.Current.FinanceRange == "LIVE" && cardHost.IsVisible)
                    RenderLiveFrame(instant: false);
            };
            CompositionTarget.Rendering += _liveRenderHandler;

            sparkHost.SizeChanged += (_, _) =>
            {
                Update();
                if (SettingsService.Current.FinanceRange == "LIVE")
                    RenderLiveFrame(instant: true);
            };
            refreshLocal = () =>
            {
                Update();
                if (SettingsService.Current.FinanceRange == "LIVE")
                    RenderLiveFrame(instant: true);
            };
            updateCard = refreshLocal;
            return cardHost;
        }

        private FrameworkElement BuildSearchView(Action onClose)
        {
            var container = new Grid();
            container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Oben: Suchleiste + Schließen-Icon
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
                Text = L.T("finance.search_placeholder"),
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

            // Ergebnis- bzw. Favoritenliste
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

            void RenderList(IReadOnlyList<FinanceSearchResult> items, string emptyText)
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

                    var leftStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    var topLine = new StackPanel { Orientation = Orientation.Horizontal };
                    topLine.Children.Add(new TextBlock
                    {
                        Text = item.Symbol,
                        Foreground = NotchDesign.TextPrimaryBrush,
                        FontFamily = NotchDesign.UiDisplayFont,
                        FontSize = 12.5,
                        FontWeight = FontWeights.SemiBold
                    });
                    if (!string.IsNullOrWhiteSpace(item.Exchange))
                    {
                        topLine.Children.Add(new TextBlock
                        {
                            Text = $" · {item.Exchange}",
                            Foreground = NotchDesign.TextSecondaryBrush,
                            FontFamily = NotchDesign.UiTextFont,
                            FontSize = 10.5,
                            VerticalAlignment = VerticalAlignment.Center
                        });
                    }
                    leftStack.Children.Add(topLine);
                    if (!string.IsNullOrWhiteSpace(item.Name) && !string.Equals(item.Name, item.Symbol, StringComparison.OrdinalIgnoreCase))
                    {
                        leftStack.Children.Add(new TextBlock
                        {
                            Text = item.Name,
                            Foreground = NotchDesign.TextSecondaryBrush,
                            FontFamily = NotchDesign.UiTextFont,
                            FontSize = 11,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        });
                    }
                    Grid.SetColumn(leftStack, 0);
                    rowGrid.Children.Add(leftStack);

                    bool isFav = FinanceService.Instance.IsFavorite(item.Symbol);
                    Button? starBtn = null;
                    starBtn = NotchDesign.CreateRoundedButton(
                        NotchDesign.CreateVectorIcon(
                            NotchIconKind.Star,
                            13,
                            isFav ? NotchDesign.AccentPrimaryBrush : NotchDesign.TextSecondaryBrush,
                            1.9),
                        () =>
                        {
                            bool nowFav = FinanceService.Instance.ToggleFavorite(item.Symbol, item.Name);
                            if (string.IsNullOrWhiteSpace(searchBox.Text))
                            {
                                RenderCurrentFavorites();
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
                        if (!FinanceService.Instance.IsFavorite(item.Symbol))
                        {
                            FinanceService.Instance.ToggleFavorite(item.Symbol, item.Name);
                        }
                        else
                        {
                            var favs = SettingsService.Current.FinanceFavorites;
                            if (favs != null)
                            {
                                int idx = favs.FindIndex(f => string.Equals(f, item.Symbol, StringComparison.OrdinalIgnoreCase));
                                if (idx >= 0)
                                {
                                    SettingsService.UpdateQuiet(s => s.FinanceActiveIndex = idx);
                                    _ = FinanceService.Instance.EnsureSymbolAsync(item.Symbol, SettingsService.Current.FinanceRange, force: false);
                                }
                            }
                        }
                        onClose();
                    };

                    resultsPanel.Children.Add(rowBorder);
                }
            }

            void RenderCurrentFavorites()
            {
                var svc = FinanceService.Instance;
                var favItems = svc.GetFavorites()
                    .Select(sym =>
                    {
                        var q = svc.GetCachedQuote(sym);
                        return new FinanceSearchResult(sym, q?.Name ?? sym, string.Empty, string.Empty);
                    })
                    .ToList();
                RenderList(favItems, L.T("finance.search_empty"));
            }

            async Task ExecuteSearchAsync(string query)
            {
                _searchCts?.Cancel();
                _searchCts?.Dispose();
                _searchCts = new CancellationTokenSource();
                var ct = _searchCts.Token;

                try
                {
                    var results = await FinanceService.Instance.SearchAsync(query, ct);
                    if (ct.IsCancellationRequested) return;
                    RenderList(results, L.T("finance.search_no_results"));
                }
                catch (OperationCanceledException)
                {
                    // Ignorieren
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                    if (!ct.IsCancellationRequested)
                        ShowStatusText(L.T("finance.search_error"));
                }
            }

            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
            _searchDebounce.Tick += (_, _) =>
            {
                _searchDebounce.Stop();
                string q = (searchBox.Text ?? string.Empty).Trim();
                if (q.Length == 0)
                {
                    RenderCurrentFavorites();
                    return;
                }
                _ = ExecuteSearchAsync(q);
            };

            searchBox.TextChanged += (_, _) =>
            {
                string q = searchBox.Text ?? string.Empty;
                placeholder.Visibility = string.IsNullOrEmpty(q) ? Visibility.Visible : Visibility.Collapsed;
                _searchDebounce.Stop();
                _searchDebounce.Start();
            };

            container.Loaded += (_, _) =>
            {
                searchBox.Text = string.Empty;
                RenderCurrentFavorites();
                searchBox.Focus();
            };

            return container;
        }

        private static void AttachSubtleCardPress(Border card)
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

            card.PreviewMouseLeftButtonDown += (_, _) =>
                AnimateScale(0.99, NotchDesign.ButtonPressDurationMs, NotchDesign.CreateEaseOut());
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

        internal static void ClearSparkline(Panel host)
        {
            for (int i = host.Children.Count - 1; i >= 0; i--)
            {
                if (host.Children[i] is Path || host.Children[i] is Ellipse)
                    host.Children.RemoveAt(i);
            }
        }

        private static void ComputeBufferedBounds(IReadOnlyList<double> points, out double min, out double max, double minPctSpan = 0.0005)
        {
            min = points.Min();
            max = points.Max();
            double mid = (min + max) * 0.5;
            double minSpan = Math.Max(Math.Abs(mid) * minPctSpan, 1e-6);
            double span = max - min;
            if (span < minSpan)
            {
                double half = minSpan * 0.5;
                min = mid - half;
                max = mid + half;
            }
            else
            {
                double margin = span * 0.08;
                min -= margin;
                max += margin;
            }
        }

        internal static void RenderSparkline(
            Panel host,
            IReadOnlyList<double>? points,
            Color strokeColor,
            double strokeThickness = 1.8,
            double padY = 8,
            double? overrideMin = null,
            double? overrideMax = null,
            bool showLiveTip = false,
            byte fillAlpha = 0x30)
        {
            ClearSparkline(host);
            if (points == null || points.Count < 2) return;

            double w = host.ActualWidth > 10 ? host.ActualWidth : host.Width;
            double h = host.ActualHeight > 10 ? host.ActualHeight : host.Height;
            if (double.IsNaN(w) || w <= 10) w = 420;
            if (double.IsNaN(h) || h <= 10) h = 110;

            double rightPad = showLiveTip ? 10 : 0;
            double plotW = Math.Max(20, w - rightPad);

            double min, max;
            if (overrideMin.HasValue && overrideMax.HasValue && overrideMax.Value > overrideMin.Value)
            {
                min = overrideMin.Value;
                max = overrideMax.Value;
            }
            else
            {
                ComputeBufferedBounds(points, out min, out max, minPctSpan: 0.001);
            }
            double range = Math.Max(max - min, 1e-9);

            double topPad = Math.Max(padY, strokeThickness + 3);
            double bottomPad = Math.Max(padY * 0.6, strokeThickness + 2);
            double drawH = Math.Max(4, h - topPad - bottomPad);

            var pts = new Point[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                double x = (double)i / (points.Count - 1) * plotW;
                double normY = Math.Clamp((points[i] - min) / range, 0.0, 1.0);
                double y = topPad + drawH - (normY * drawH);
                pts[i] = new Point(x, y);
            }

            var lineGeo = new StreamGeometry();
            var fillGeo = new StreamGeometry();

            static void AppendSmoothSegments(StreamGeometryContext ctx, Point[] p)
            {
                for (int i = 0; i < p.Length - 1; i++)
                {
                    Point p0 = i > 0 ? p[i - 1] : p[i];
                    Point p1 = p[i];
                    Point p2 = p[i + 1];
                    Point p3 = i + 2 < p.Length ? p[i + 2] : p[i + 1];

                    var c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
                    var c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
                    ctx.BezierTo(c1, c2, p2, isStroked: true, isSmoothJoin: true);
                }
            }

            using (var ctx = lineGeo.Open())
            {
                ctx.BeginFigure(pts[0], isFilled: false, isClosed: false);
                AppendSmoothSegments(ctx, pts);
            }
            lineGeo.Freeze();

            using (var ctx = fillGeo.Open())
            {
                ctx.BeginFigure(new Point(pts[0].X, h), isFilled: true, isClosed: true);
                ctx.LineTo(pts[0], isStroked: false, isSmoothJoin: false);
                for (int i = 0; i < pts.Length - 1; i++)
                {
                    Point p0 = i > 0 ? pts[i - 1] : pts[i];
                    Point p1 = pts[i];
                    Point p2 = pts[i + 1];
                    Point p3 = i + 2 < pts.Length ? pts[i + 2] : pts[i + 1];
                    var c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
                    var c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
                    ctx.BezierTo(c1, c2, p2, isStroked: false, isSmoothJoin: true);
                }
                ctx.LineTo(new Point(pts[^1].X, h), isStroked: false, isSmoothJoin: false);
            }
            fillGeo.Freeze();

            var fillPath = new Path
            {
                Data = fillGeo,
                IsHitTestVisible = false,
                Fill = new LinearGradientBrush(
                    Color.FromArgb(fillAlpha, strokeColor.R, strokeColor.G, strokeColor.B),
                    Color.FromArgb(0x00, strokeColor.R, strokeColor.G, strokeColor.B),
                    90)
            };

            var linePath = new Path
            {
                Data = lineGeo,
                IsHitTestVisible = false,
                Stroke = new SolidColorBrush(strokeColor),
                StrokeThickness = strokeThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };

            host.Children.Add(fillPath);
            host.Children.Add(linePath);

            if (showLiveTip)
            {
                Point tip = pts[^1];
                var halo = new Ellipse
                {
                    Width = 12,
                    Height = 12,
                    IsHitTestVisible = false,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(tip.X - 6, tip.Y - 6, 0, 0),
                    Fill = new SolidColorBrush(Color.FromArgb(0x44, strokeColor.R, strokeColor.G, strokeColor.B))
                };
                var dot = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    IsHitTestVisible = false,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(tip.X - 3, tip.Y - 3, 0, 0),
                    Fill = new SolidColorBrush(strokeColor)
                };
                host.Children.Add(halo);
                host.Children.Add(dot);
            }
        }
    }
}
