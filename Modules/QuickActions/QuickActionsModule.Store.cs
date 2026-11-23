using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.QuickActions
{
    public partial class QuickActionsModule
    {
        internal sealed record QuickTileDef(string Id, NotchIconKind Icon, Action? Execute)
        {
            public string Label => L.T($"qa.tile.{Id}");
            public string Tooltip => L.T($"qa.tile.{Id}_tooltip");
        }

        internal sealed record QuickBarDef(string Id, NotchIconKind Icon)
        {
            public string Label => L.T($"qa.bar.{Id}");
            public string Tooltip => L.T($"qa.bar.{Id}_tooltip");
        }

        private static readonly QuickTileDef[] TileCatalog =
        {
            new("lock", NotchIconKind.Lock, () => OpenUri("rundll32.exe", "user32.dll,LockWorkStation")),
            new("project", NotchIconKind.Monitor, () => OpenUri("DisplaySwitch.exe")),
            new("taskmgr", NotchIconKind.Pulse, () => OpenUri("taskmgr.exe")),
            new("explorer", NotchIconKind.Folder, () => OpenUri("explorer.exe")),
            new("audio", NotchIconKind.Headphones, null),
            new("snip", NotchIconKind.Scan, () => OpenUri("ms-screenclip:")),
            new("terminal", NotchIconKind.Bolt, OpenTerminal),
            new("settings", NotchIconKind.SettingsGear, () => OpenUri("ms-settings:")),
            new("sleep", NotchIconKind.Moon, TriggerStandby),
            new("notepad", NotchIconKind.Note, () => OpenUri("notepad.exe")),
            new("wifi", NotchIconKind.Wifi, null),
            new("bluetooth", NotchIconKind.Bluetooth, null),
            new("nightlight", NotchIconKind.Sun, null),
            new("calc", NotchIconKind.Dashboard, () => OpenUri("calc.exe")),
            new("volmixer", NotchIconKind.Layers, () => OpenUri("ms-settings:apps-volume")),
            new("recycle", NotchIconKind.Trash, () => OpenUri("explorer.exe", "shell:RecycleBinFolder"))
        };

        private static readonly QuickBarDef[] BarCatalog =
        {
            new("volume", NotchIconKind.VolumeOn)
        };

        private double EstimateStoreDrawerHeight()
        {
            var activeTiles = new HashSet<string>(GetActiveTileIds());
            var activeBars = new HashSet<string>(GetActiveBarIds());
            int availBars = BarCatalog.Count(b => !activeBars.Contains(b.Id));
            int availTiles = TileCatalog.Count(t => !activeTiles.Contains(t.Id));

            if (availBars == 0 && availTiles == 0)
                return 60;

            double h = 30; // Top-Margin (10) + Trennlinie (1) + Gap (10) + Bottom-Padding (9)
            if (availBars > 0)
                h += availBars * 42 + (availBars - 1) * 6 + (availTiles > 0 ? 8 : 0);
            if (availTiles > 0)
            {
                int tileRows = (availTiles + 2) / 3;
                h += tileRows * 44 + (tileRows - 1) * 6;
            }
            return h;
        }

        private Border BuildStoreDrawer()
        {
            // Direkt auf OLED-Schwarz ohne Text-Header, doppelte Box oder Scroll-Abschnitt
            var drawer = new Border
            {
                Tag = "QuickActionsStoreDrawer",
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 10, 0, 6),
                Padding = new Thickness(0)
            };

            var root = new StackPanel();
            root.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(2, 0, 2, 10)
            });

            var activeTiles = new HashSet<string>(GetActiveTileIds());
            var activeBars = new HashSet<string>(GetActiveBarIds());
            var availableBars = BarCatalog.Where(b => !activeBars.Contains(b.Id)).ToList();
            var availableTiles = TileCatalog.Where(t => !activeTiles.Contains(t.Id)).ToList();

            if (availableBars.Count == 0 && availableTiles.Count == 0)
            {
                root.Children.Add(new TextBlock
                {
                    Text = L.T("qa.all_active_hint"),
                    Foreground = NotchDesign.TextSecondaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 10, 0, 10)
                });
                drawer.Child = root;
                return drawer;
            }

            if (availableBars.Count > 0)
            {
                var barsStack = new StackPanel { Margin = new Thickness(0, 0, 0, availableTiles.Count > 0 ? 8 : 0) };
                for (int i = 0; i < availableBars.Count; i++)
                {
                    var bar = availableBars[i];
                    var preview = CreateStoreBarPreview(bar);
                    if (i > 0) preview.Margin = new Thickness(0, 6, 0, 0);
                    barsStack.Children.Add(preview);
                }
                root.Children.Add(barsStack);
            }

            if (availableTiles.Count > 0)
            {
                int rows = (availableTiles.Count + 2) / 3;
                var grid = new Grid();
                for (int r = 0; r < rows; r++)
                {
                    if (r > 0) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
                    grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
                }
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                for (int i = 0; i < availableTiles.Count; i++)
                {
                    var def = availableTiles[i];
                    var tile = CreateStoreTilePreview(def);
                    Grid.SetRow(tile, (i / 3) * 2);
                    Grid.SetColumn(tile, (i % 3) * 2);
                    grid.Children.Add(tile);
                }
                root.Children.Add(grid);
            }

            drawer.Child = root;
            return drawer;
        }

        private static Border CreateStoreBarPreview(QuickBarDef bar)
        {
            var row = new DockPanel { LastChildFill = true };
            var icon = NotchDesign.CreateVectorIcon(bar.Icon, 13.5, NotchDesign.AccentPrimaryBrush, 1.9);
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);

            var badge = new TextBlock
            {
                Text = L.T("qa.bar_badge"),
                Foreground = NotchDesign.TextMutedBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(badge, Dock.Right);
            row.Children.Add(badge);

            row.Children.Add(new TextBlock
            {
                Text = bar.Label,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            return new Border
            {
                Tag = ("store_bar", bar.Id),
                Background = NotchDesign.CardBackgroundBrush,
                CornerRadius = new CornerRadius(NotchDesign.RadiusInner),
                Padding = new Thickness(12, 10, 12, 10),
                Height = 42,
                Cursor = Cursors.SizeAll,
                ToolTip = bar.Tooltip,
                Child = row
            };
        }

        private static Border CreateStoreTilePreview(QuickTileDef def)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(NotchDesign.CreateVectorIcon(def.Icon, 13.5, NotchDesign.TextPrimaryBrush, 1.9));
            stack.Children.Add(new TextBlock
            {
                Text = def.Label,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            return new Border
            {
                Tag = ("store_tile", def.Id),
                Background = NotchDesign.CardBackgroundBrush,
                CornerRadius = new CornerRadius(NotchDesign.RadiusControl),
                Padding = new Thickness(8, 0, 8, 0),
                Height = 44,
                Cursor = Cursors.SizeAll,
                ToolTip = def.Tooltip,
                Child = stack
            };
        }

        private void AttachStoreDragHandlers(Border storeDrawer)
        {
            if (_jiggleController == null) return;
            foreach (var border in FindVisualChildren<Border>(storeDrawer))
            {
                if (border.Tag is ValueTuple<string, string> meta &&
                    (meta.Item1 == "store_tile" || meta.Item1 == "store_bar"))
                {
                    bool isBar = meta.Item1 == "store_bar";
                    string id = meta.Item2;
                    border.MouseLeftButtonDown += (_, e) =>
                    {
                        if (_jiggleController == null) return;
                        e.Handled = true;
                        _jiggleController.BeginStoreItemDrag(border, id, isBar, e);
                    };
                }
            }
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                    yield return match;
                foreach (var nested in FindVisualChildren<T>(child))
                    yield return nested;
            }
        }

        private void CommitStoreDrop(string id, bool isBar, int targetSlot, bool toTop, int insertIdx)
        {
            if (isBar)
            {
                var (top, bottom) = GetTopAndBottomBarIds();
                top.Remove(id);
                bottom.Remove(id);
                var target = toTop ? top : bottom;
                int idx = Math.Clamp(insertIdx, 0, target.Count);
                target.Insert(idx, id);
                SaveBarPositions(top, bottom);
            }
            else
            {
                var slots = GetTileSlots();
                for (int i = 0; i < slots.Count; i++)
                {
                    if (slots[i] == id)
                        slots[i] = string.Empty;
                }

                if (targetSlot < 0)
                {
                    int firstEmpty = slots.FindIndex(string.IsNullOrEmpty);
                    targetSlot = firstEmpty >= 0 ? firstEmpty : slots.Count;
                }

                while (slots.Count <= targetSlot)
                    slots.Add(string.Empty);

                if (string.IsNullOrEmpty(slots[targetSlot]))
                {
                    slots[targetSlot] = id;
                }
                else
                {
                    int emptyIdx = FindNearestEmptySlot(slots, targetSlot);
                    if (emptyIdx < 0)
                    {
                        slots.Add(string.Empty);
                        emptyIdx = slots.Count - 1;
                    }
                    if (emptyIdx > targetSlot)
                    {
                        for (int i = emptyIdx; i > targetSlot; i--)
                            slots[i] = slots[i - 1];
                    }
                    else
                    {
                        for (int i = emptyIdx; i < targetSlot; i++)
                            slots[i] = slots[i + 1];
                    }
                    slots[targetSlot] = id;
                }

                SettingsService.UpdateQuiet(s => s.QuickActionTileOrder = AppSettings.NormalizeQuickTileSlots(slots));
            }

            if (_showStoreDrawer) _suppressNextStoreEnterAnim = true;
            RebuildExpandedUi();
            RaiseStateChanged();
        }

        private static int FindNearestEmptySlot(List<string> slots, int target)
        {
            int best = -1;
            int bestDist = int.MaxValue;
            for (int i = 0; i < slots.Count; i++)
            {
                if (!string.IsNullOrEmpty(slots[i])) continue;
                int d = Math.Abs(i - target);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            return best;
        }

        private void RemoveActiveItem(string id, bool isBar)
        {
            if (isBar)
            {
                var (top, bottom) = GetTopAndBottomBarIds();
                bool removed = top.Remove(id) | bottom.Remove(id);
                if (removed)
                    SaveBarPositions(top, bottom);
            }
            else
            {
                var slots = GetTileSlots();
                if (slots.Count(s => !string.IsNullOrEmpty(s)) <= 1) return;
                bool changed = false;
                for (int i = 0; i < slots.Count; i++)
                {
                    if (slots[i] == id)
                    {
                        slots[i] = string.Empty;
                        changed = true;
                    }
                }
                if (changed)
                    SettingsService.UpdateQuiet(s => s.QuickActionTileOrder = AppSettings.NormalizeQuickTileSlots(slots));
            }

            if (_showStoreDrawer) _suppressNextStoreEnterAnim = true;
            RebuildExpandedUi();
            RaiseStateChanged();
        }
    }
}
