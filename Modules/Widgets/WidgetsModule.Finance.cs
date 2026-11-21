using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WindowsNotch.Core;
using WindowsNotch.Modules.Finance;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.Widgets
{
    public partial class WidgetsModule
    {
        private Border BuildFinanceTile()
        {
            var card = CreateBentoAppCard(new Thickness(14, 11, 14, 11));
            card.Cursor = Cursors.Hand;
            card.ToolTip = L.T("finance.cycle_tooltip");

            var dock = new DockPanel();

            // Oben: Icon + Ticker links, Tages-% rechts
            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            leftRow.Children.Add(NotchDesign.CreateVectorIcon(NotchIconKind.TrendingUp, 13, NotchDesign.AccentPrimaryBrush, 1.9));
            var symbolText = new TextBlock
            {
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(6, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            leftRow.Children.Add(symbolText);
            Grid.SetColumn(leftRow, 0);
            topGrid.Children.Add(leftRow);

            var changeText = new TextBlock
            {
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Typography.SetNumeralAlignment(changeText, FontNumeralAlignment.Tabular);
            Grid.SetColumn(changeText, 1);
            topGrid.Children.Add(changeText);

            DockPanel.SetDock(topGrid, Dock.Top);
            dock.Children.Add(topGrid);

            // Unten: Mini-Sparkline
            var sparkHost = new Grid
            {
                Height = 32,
                Margin = new Thickness(0, 4, 0, 0),
                ClipToBounds = true
            };
            DockPanel.SetDock(sparkHost, Dock.Bottom);
            dock.Children.Add(sparkHost);

            // Mitte: Live-Kurs + Asset-Name
            var midStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            var priceText = new TextBlock
            {
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 23,
                FontWeight = FontWeights.Normal
            };
            Typography.SetNumeralAlignment(priceText, FontNumeralAlignment.Tabular);

            var nameText = new TextBlock
            {
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0)
            };
            midStack.Children.Add(priceText);
            midStack.Children.Add(nameText);
            dock.Children.Add(midStack);

            card.Child = dock;

            void UpdateFinance()
            {
                var svc = FinanceService.Instance;
                var favs = svc.GetFavorites();
                if (favs.Count == 0)
                {
                    symbolText.Text = L.T("finance.bento_title");
                    changeText.Text = string.Empty;
                    priceText.Text = "—";
                    nameText.Text = L.T("finance.no_favorites");
                    FinanceModule.ClearSparkline(sparkHost);
                    return;
                }

                string activeSym = svc.GetActiveSymbol() ?? favs[0];
                var quote = svc.GetCachedQuote(activeSym);
                symbolText.Text = activeSym;

                if (quote == null || quote.Price <= 0)
                {
                    changeText.Text = string.Empty;
                    priceText.Text = "—";
                    nameText.Text = L.T("finance.loading");
                    FinanceModule.ClearSparkline(sparkHost);
                    return;
                }

                priceText.Text = FinanceService.FormatPrice(quote.Price, quote.Currency);
                nameText.Text = string.IsNullOrWhiteSpace(quote.Name) ? activeSym : quote.Name;

                double pct = quote.ChangePercent;
                bool isUp = pct >= 0;
                changeText.Text = FinanceService.FormatChangePercent(pct);
                changeText.Foreground = new SolidColorBrush(isUp ? NotchDesign.AccentGreen : NotchDesign.AccentRed);

                IReadOnlyList<double>? pts = quote.Sparklines.TryGetValue("1D", out var sp) ? sp : null;
                FinanceModule.RenderSparkline(sparkHost, pts, isUp ? NotchDesign.AccentGreen : NotchDesign.AccentRed, strokeThickness: 1.6, padY: 4, fillAlpha: 0x22);
            }

            Point downPt = default;
            card.MouseLeftButtonDown += (_, e) => downPt = e.GetPosition(card);
            card.MouseLeftButtonUp += (_, e) =>
            {
                if (_jiggleController?.IsJiggleMode == true) return;
                Point upPt = e.GetPosition(card);
                if (Math.Abs(upPt.X - downPt.X) > 6 || Math.Abs(upPt.Y - downPt.Y) > 6) return;
                if (FinanceService.Instance.GetFavorites().Count <= 1) return;

                FinanceService.Instance.CycleFavorite(1);
                UpdateFinance();
            };

            void OnQuoteUpdated(string symbol)
            {
                card.Dispatcher.BeginInvoke(() =>
                {
                    if (!card.IsLoaded) return;
                    if (string.Equals(symbol, FinanceService.Instance.GetActiveSymbol(), StringComparison.OrdinalIgnoreCase))
                        UpdateFinance();
                });
            }

            void OnFavoritesChanged()
            {
                card.Dispatcher.BeginInvoke(() =>
                {
                    if (!card.IsLoaded) return;
                    UpdateFinance();
                });
            }

            sparkHost.SizeChanged += (_, _) => UpdateFinance();

            card.Loaded += (_, _) =>
            {
                FinanceService.Instance.QuoteUpdated += OnQuoteUpdated;
                FinanceService.Instance.FavoritesChanged += OnFavoritesChanged;
                FinanceService.Instance.StartTracking();
                string? active = FinanceService.Instance.GetActiveSymbol();
                if (!string.IsNullOrEmpty(active))
                    _ = FinanceService.Instance.EnsureSymbolAsync(active, "1D", force: false);
                UpdateFinance();
            };

            card.Unloaded += (_, _) =>
            {
                FinanceService.Instance.QuoteUpdated -= OnQuoteUpdated;
                FinanceService.Instance.FavoritesChanged -= OnFavoritesChanged;
                FinanceService.Instance.StopTracking();
            };

            _tileUpdaters.Add(UpdateFinance);
            UpdateFinance();
            return card;
        }
    }
}
