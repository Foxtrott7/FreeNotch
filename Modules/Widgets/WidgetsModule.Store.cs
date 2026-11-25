using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.Widgets
{
    public partial class WidgetsModule
    {
        internal sealed record BentoTileDef(string Id, NotchIconKind Icon, bool IsCarousel)
        {
            public string Label => L.T(Id == "finance" ? "finance.bento_title" : $"widgets.bento.{Id}");
            public string Tooltip => L.T(Id == "finance" ? "finance.bento_tooltip" : $"widgets.bento.{Id}_tooltip");
        }

        private static readonly BentoTileDef[] BentoCatalog =
        {
            new("lyrics", NotchIconKind.Waveform, false),
            new("media", NotchIconKind.Music, false),
            new("weather", NotchIconKind.CloudSun, false),
            new("calendar", NotchIconKind.Calendar, false),
            new("todos", NotchIconKind.CheckSquare, false),
            new("notes", NotchIconKind.Note, false),
            new("clock", NotchIconKind.Clock, false),
            new("timer", NotchIconKind.Timer, false),
            new("stopwatch", NotchIconKind.Pulse, false),
            new("hardware", NotchIconKind.Cpu, false),
            new("finance", NotchIconKind.TrendingUp, false),
            new("carousel", NotchIconKind.Layers, true)
        };

        private static HashSet<string> GetUsedBentoIds()
        {
            var used = new HashSet<string>(
                SettingsService.Current.BentoTileOrder.Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.Ordinal);
            if (used.Contains("carousel"))
            {
                foreach (var pageId in SettingsService.Current.BentoCarouselPages)
                    used.Add(pageId);
            }
            return used;
        }

        private double EstimateStoreDrawerHeight()
        {
            var active = GetUsedBentoIds();
            int availableCount = BentoCatalog.Count(t => !active.Contains(t.Id));
            if (availableCount == 0)
                return 54;

            int rows = (availableCount + 3) / 4;
            return 24 + rows * 40 + (rows - 1) * 6;
        }

        private Border BuildStoreDrawer()
        {
            var drawer = new Border
            {
                Tag = "BentoStoreDrawer",
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 10, 0, 2),
                Padding = new Thickness(0)
            };

            var root = new StackPanel();
            root.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(2, 0, 2, 10)
            });

            var active = GetUsedBentoIds();
            var available = BentoCatalog.Where(t => !active.Contains(t.Id)).ToList();

            if (available.Count == 0)
            {
                root.Children.Add(new TextBlock
                {
                    Text = L.T("widgets.all_active_hint"),
                    Foreground = NotchDesign.TextSecondaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 8)
                });
                drawer.Child = root;
                return drawer;
            }

            int rows = (available.Count + 3) / 4;
            var grid = new Grid();
            for (int r = 0; r < rows; r++)
            {
                if (r > 0) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
            }
            for (int c = 0; c < 4; c++)
            {
                if (c > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            for (int i = 0; i < available.Count; i++)
            {
                var def = available[i];
                var preview = CreateStoreTilePreview(def);
                Grid.SetRow(preview, (i / 4) * 2);
                Grid.SetColumn(preview, (i % 4) * 2);
                grid.Children.Add(preview);
            }

            root.Children.Add(grid);
            drawer.Child = root;
            return drawer;
        }

        private static Border CreateStoreTilePreview(BentoTileDef def)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(NotchDesign.CreateVectorIcon(
                def.Icon,
                13,
                def.IsCarousel ? NotchDesign.AccentPrimaryBrush : NotchDesign.TextPrimaryBrush,
                1.8));
            stack.Children.Add(new TextBlock
            {
                Text = def.Label,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(6, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });

            return new Border
            {
                Tag = ("bento_store_tile", def.Id),
                Background = NotchDesign.CardBackgroundBrush,
                CornerRadius = new CornerRadius(NotchDesign.RadiusInner),
                Padding = new Thickness(8, 6, 8, 6),
                Height = 40,
                Cursor = Cursors.SizeAll,
                ToolTip = def.Tooltip,
                Child = stack
            };
        }

        private void AttachStoreDragHandlers(Border storeDrawer)
        {
            if (_jiggleController == null) return;
            foreach (var preview in FindStorePreviews(storeDrawer))
            {
                if (preview.Tag is ValueTuple<string, string> info && info.Item1 == "bento_store_tile")
                {
                    string tileId = info.Item2;
                    preview.PreviewMouseLeftButtonDown += (_, e) =>
                    {
                        if (_jiggleController?.IsJiggleMode != true) return;
                        _jiggleController.BeginShopTileDrag(preview, tileId, e);
                    };
                }
            }
        }

        private static IEnumerable<Border> FindStorePreviews(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Border b && b.Tag is ValueTuple<string, string>)
                    yield return b;
                foreach (var nested in FindStorePreviews(child))
                    yield return nested;
            }
        }

        private void RefreshStoreDrawerInPlace()
        {
            if (!_showStoreDrawer || _storeDrawerHost == null) return;
            var drawer = BuildStoreDrawer();
            _storeDrawer = drawer;
            _storeDrawerHost.Child = drawer;
            AttachStoreDragHandlers(drawer);
            RaiseStateChanged();
        }

        private void CommitShopDrop(string tileId, int targetSlot)
        {
            if (targetSlot < 0 || targetSlot >= 4) return;

            var order = SettingsService.Current.BentoTileOrder.ToList();
            while (order.Count < 4) order.Add(string.Empty);

            int existingIdx = order.FindIndex(id => string.Equals(id, tileId, StringComparison.Ordinal));
            if (existingIdx == targetSlot) return;

            if (existingIdx >= 0)
            {
                // Falls bereits aktiv, Slots tauschen
                (order[existingIdx], order[targetSlot]) = (order[targetSlot], order[existingIdx]);
            }
            else if (order[targetSlot] == "carousel" && tileId != "carousel")
            {
                // Auf das Karussell gezogen -> als Seite im Karussell einfügen
                var pages = SettingsService.Current.BentoCarouselPages.ToList();
                if (!pages.Contains(tileId, StringComparer.Ordinal))
                {
                    if (_customCarouselPageIndex >= 0 && _customCarouselPageIndex < pages.Count)
                        pages[_customCarouselPageIndex] = tileId;
                    else
                    {
                        pages.Add(tileId);
                        _customCarouselPageIndex = pages.Count; // Automatisch auf neue leere "+"-Seite wechseln
                    }
                    SettingsService.UpdateQuiet(s => s.BentoCarouselPages = pages);
                }
                RebuildSlotVisual(targetSlot, "carousel");
                RefreshStoreDrawerInPlace();
                return;
            }
            else
            {
                order[targetSlot] = tileId;
            }

            SettingsService.UpdateQuiet(s => s.BentoTileOrder = order);
            RebuildSlotVisual(targetSlot, order[targetSlot]);
            if (existingIdx >= 0)
                RebuildSlotVisual(existingIdx, order[existingIdx]);
            RefreshStoreDrawerInPlace();
        }

        private void RemoveTileToShop(int slot)
        {
            if (slot < 0 || slot >= 4) return;
            var order = SettingsService.Current.BentoTileOrder.ToList();
            while (order.Count < 4) order.Add(string.Empty);

            if (string.IsNullOrWhiteSpace(order[slot])) return;
            int activeCount = order.Count(id => !string.IsNullOrWhiteSpace(id));
            if (activeCount <= 1) return; // Mindestens 1 Kachel bleibt aktiv

            if (string.Equals(order[slot], "lyrics", StringComparison.Ordinal) && _isLyricsPinned)
                SetLyricsPinned(false);

            order[slot] = string.Empty;
            SettingsService.UpdateQuiet(s => s.BentoTileOrder = order);
            RebuildSlotVisual(slot, string.Empty);
            RefreshStoreDrawerInPlace();
        }

        private void RebuildSlotVisual(int slot, string tileId)
        {
            if (string.Equals(tileId, "lyrics", StringComparison.Ordinal))
            {
                var mp = MediaPlayerService.Instance;
                LyricsService.Instance.EnsureForTrack(mp.TrackTitle, mp.Artist, mp.AlbumTitle, mp.Duration, mp.HasKnownTrack);
            }
            var newTile = BuildBentoTile(tileId);
            _jiggleController?.ReplaceSlotTile(slot, tileId, newTile);
            UpdateExpandedWidgets();
        }
    }
}
