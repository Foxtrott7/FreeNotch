using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsNotch.Core;
using WindowsNotch.Services;
using WindowsNotch.Services.Database;

namespace WindowsNotch.Modules.Clipboard
{
    public partial class ClipboardModule
    {
        public override FrameworkElement CreatePreviewView()
        {
            int count;
            lock (_lock) count = _items.Count;

            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var icon = NotchDesign.CreateVectorIcon(NotchIconKind.Clipboard, 13, NotchDesign.AccentPrimaryBrush);
            icon.Margin = new Thickness(0, 0, 7, 0);

            var text = new TextBlock
            {
                Text = count == 0 ? L.T("module.clipboard") : L.F("clipboard.count_many", count),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };

            stack.Children.Add(icon);
            stack.Children.Add(text);
            return stack;
        }

        public override FrameworkElement CreateExpandedView()
        {
            var root = new Grid { Margin = new Thickness(4, 2, 4, 0) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 1. Einheitlicher Modul-Header (wie Downloads / ScreenCapture)
            _expandBtn = NotchDesign.CreateIconActionButton(
                _isSplitExpanded ? NotchIconKind.Previous : NotchIconKind.Next,
                _isSplitExpanded ? L.T("clipboard.compact_tooltip") : L.T("clipboard.expand_tooltip"),
                ToggleSplitExpanded);
            _expandBtn.Margin = new Thickness(0, 0, 6, 0);

            _clearBtn = NotchDesign.CreateIconActionButton(
                NotchIconKind.Trash,
                L.T("clipboard.clear_unpinned"),
                ClearUnpinned);

            var header = NotchDesign.CreateModuleHeader(
                NotchIconKind.Clipboard,
                L.T("module.clipboard"),
                new Thickness(0, 0, 0, 10),
                iconBoxSize: 32,
                iconSize: 20,
                strokeThickness: 2.15,
                _expandBtn,
                _clearBtn);

            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // 2. Suchfeld
            var searchBorder = new Border
            {
                Background = NotchDesign.IsLight
                    ? NotchDesign.NestedCardBackgroundBrush
                    : NotchDesign.GetFrozenBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var searchGrid = new Grid();
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var searchIcon = NotchDesign.CreateVectorIcon(NotchIconKind.Search, 12, NotchDesign.TextSecondaryBrush);
            searchIcon.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(searchIcon, 0);
            searchGrid.Children.Add(searchIcon);

            _searchBox = new TextBox
            {
                Text = _searchQuery,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = NotchDesign.TextPrimaryBrush,
                CaretBrush = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _searchBox.TextChanged += (_, _) =>
            {
                _searchQuery = _searchBox.Text ?? string.Empty;
                RebuildList();
            };
            Grid.SetColumn(_searchBox, 1);
            searchGrid.Children.Add(_searchBox);
            searchBorder.Child = searchGrid;

            Grid.SetRow(searchBorder, 1);
            root.Children.Add(searchBorder);

            // 3. Verlaufsliste (+ optionale Detailansicht rechts)
            var contentGrid = new Grid();
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _detailCol = new ColumnDefinition
            {
                Width = _isSplitExpanded ? new GridLength(1.15, GridUnitType.Star) : new GridLength(0)
            };
            contentGrid.ColumnDefinitions.Add(_detailCol);

            _listPanel = new StackPanel();
            _listScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = Brushes.Transparent,
                Content = _listPanel
            };
            NotchDesign.ApplySlimScrollBarStyle(_listScroll);
            _listScroll.Loaded += (_, _) => AdjustHeightToFullCards();

            Grid.SetColumn(_listScroll, 0);
            contentGrid.Children.Add(_listScroll);

            _detailHost = new Border
            {
                Background = NotchDesign.IsLight
                    ? NotchDesign.NestedCardBackgroundBrush
                    : NotchDesign.GetFrozenBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12),
                Margin = new Thickness(10, 0, 0, 0),
                Visibility = _isSplitExpanded ? Visibility.Visible : Visibility.Collapsed
            };
            Grid.SetColumn(_detailHost, 1);
            contentGrid.Children.Add(_detailHost);

            Grid.SetRow(contentGrid, 2);
            root.Children.Add(contentGrid);

            RebuildList();
            return root;
        }

        private void ToggleSplitExpanded()
        {
            _isSplitExpanded = !_isSplitExpanded;
            if (_expandBtn != null)
            {
                _expandBtn.Content = NotchDesign.CreateVectorIcon(
                    _isSplitExpanded ? NotchIconKind.Previous : NotchIconKind.Next,
                    12,
                    NotchDesign.TextPrimaryBrush,
                    2.0);
                _expandBtn.ToolTip = _isSplitExpanded ? "Kompakte Ansicht" : "Erweitern (Detailansicht)";
            }

            if (_detailCol != null)
                _detailCol.Width = _isSplitExpanded ? new GridLength(1.15, GridUnitType.Star) : new GridLength(0);

            if (_detailHost != null)
                _detailHost.Visibility = _isSplitExpanded ? Visibility.Visible : Visibility.Collapsed;

            _dynamicExpandedHeight = null;
            UpdateDetailView();
            RaiseStateChanged();
            Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(AdjustHeightToFullCards));
        }

        private void UpdateDetailView()
        {
            if (!_isSplitExpanded || _detailHost == null) return;

            var item = _selectedItem;
            if (item == null)
            {
                _detailHost.Child = new TextBlock
                {
                    Text = L.T("clipboard.select_item"),
                    Foreground = NotchDesign.TextSecondaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                return;
            }

            var panel = new Grid();
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var topBar = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
            var meta = new TextBlock
            {
                Text = $"{(item.Kind == ClipboardItemKind.Image ? L.T("clipboard.kind_image") : item.Kind == ClipboardItemKind.Link ? L.T("clipboard.kind_link") : L.T("clipboard.kind_text"))} • {FormatRelativeTime(item.CreatedAt)}",
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(meta, Dock.Left);
            topBar.Children.Add(meta);

            var copyBtn = NotchDesign.CreateRoundedButton(
                new TextBlock
                {
                    Text = L.T("clipboard.copy"),
                    Foreground = Brushes.White,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold
                },
                () => CopyItemToClipboard(item, meta),
                background: NotchDesign.AccentPrimaryBrush,
                hoverBackground: NotchDesign.AccentHoverBrush,
                cornerRadius: 8,
                padding: new Thickness(10, 4, 10, 4));
            DockPanel.SetDock(copyBtn, Dock.Right);
            topBar.Children.Add(copyBtn);

            Grid.SetRow(topBar, 0);
            panel.Children.Add(topBar);

            if (item.Kind == ClipboardItemKind.Image && !string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
            {
                var img = new Image { Stretch = Stretch.Uniform };
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(item.ImagePath);
                    bmp.EndInit();
                    img.Source = bmp;
                }
                catch { }
                var imgFrame = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    ClipToBounds = true,
                    Child = img
                };
                imgFrame.SizeChanged += (_, _) =>
                {
                    if (imgFrame.ActualWidth > 0 && imgFrame.ActualHeight > 0)
                        imgFrame.Clip = new RectangleGeometry(new Rect(0, 0, imgFrame.ActualWidth, imgFrame.ActualHeight), 12, 12);
                };
                Grid.SetRow(imgFrame, 1);
                panel.Children.Add(imgFrame);
            }
            else
            {
                var box = new TextBox
                {
                    Text = item.TextContent ?? item.PreviewText,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = NotchDesign.TextPrimaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 12
                };
                Grid.SetRow(box, 1);
                panel.Children.Add(box);
            }

            _detailHost.Child = panel;
        }

        private void RebuildList()
        {
            if (_listPanel == null) return;
            _listPanel.Children.Clear();

            List<ClipboardRecord> snapshot;
            lock (_lock) snapshot = _items.ToList();

            if (_clearBtn != null)
                _clearBtn.Visibility = snapshot.Any(x => !x.IsPinned) ? Visibility.Visible : Visibility.Collapsed;

            IEnumerable<ClipboardRecord> filtered = snapshot
                .OrderByDescending(x => x.IsPinned)
                .ThenByDescending(x => x.CreatedAt);

            if (!string.IsNullOrWhiteSpace(_searchQuery))
            {
                string q = _searchQuery.Trim();
                filtered = filtered.Where(x =>
                    (x.TextContent?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    x.PreviewText.Contains(q, StringComparison.OrdinalIgnoreCase));
            }

            var visible = filtered.ToList();
            if (_selectedItem == null || visible.All(v => v.Id != _selectedItem.Id))
                _selectedItem = visible.FirstOrDefault();
            UpdateDetailView();
            if (visible.Count == 0)
            {
                var empty = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 46, 0, 46)
                };
                empty.Children.Add(new Border
                {
                    Width = 44,
                    Height = 44,
                    CornerRadius = new CornerRadius(14),
                    Background = NotchDesign.CreateSoftAccentTileBrush(0x22),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = NotchDesign.CreateVectorIcon(NotchIconKind.Clipboard, 20, NotchDesign.AccentPrimaryBrush)
                });
                empty.Children.Add(new TextBlock
                {
                    Text = snapshot.Count == 0 ? L.T("clipboard.empty_title") : L.T("clipboard.empty_filtered"),
                    Foreground = NotchDesign.TextPrimaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 13,
                    FontWeight = FontWeights.Medium,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 12, 0, 4)
                });
                empty.Children.Add(new TextBlock
                {
                    Text = L.T("clipboard.empty_hint"),
                    Foreground = NotchDesign.TextSecondaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11.5,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                _listPanel.Children.Add(empty);
                if (_dynamicExpandedHeight != null)
                {
                    _dynamicExpandedHeight = null;
                    RaiseStateChanged();
                }
                return;
            }

            foreach (var item in visible)
            {
                _listPanel.Children.Add(BuildClipboardCard(item));
            }

            Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(AdjustHeightToFullCards));
        }

        private void AdjustHeightToFullCards()
        {
            if (_listPanel == null || _listScroll == null || !_listScroll.IsLoaded) return;

            double baseTotalH = _isSplitExpanded ? 445.0 : 435.0;
            double currentTotalH = _dynamicExpandedHeight ?? baseTotalH;
            double currentViewportH = _listScroll.ViewportHeight;
            if (currentViewportH <= 20) return;

            double chromeH = currentTotalH - currentViewportH;
            double baseViewportH = Math.Max(120.0, baseTotalH - chromeH);

            double y = 0;
            double targetViewportH = baseViewportH;
            foreach (var child in _listPanel.Children)
            {
                if (child is not Border card) continue;
                double h = card.ActualHeight;
                if (h <= 0) continue;

                double step = h + card.Margin.Top + card.Margin.Bottom;
                if (y < baseViewportH && y + h > baseViewportH)
                {
                    double visiblePart = baseViewportH - y;
                    double ratio = visiblePart / h;
                    targetViewportH = ratio >= 0.5 ? (y + step) : y;
                    break;
                }
                y += step;
            }

            double newTotalH = Math.Clamp(Math.Ceiling(chromeH + targetViewportH), 300.0, 540.0);
            if (Math.Abs((_dynamicExpandedHeight ?? baseTotalH) - newTotalH) > 1.5)
            {
                _dynamicExpandedHeight = newTotalH;
                RaiseStateChanged();
            }
        }

        private Border BuildClipboardCard(ClipboardRecord item)
        {
            bool isSelected = _isSplitExpanded && _selectedItem?.Id == item.Id;
            bool isCode = item.Kind == ClipboardItemKind.Text && LooksLikeCode(item.TextContent);

            Brush baseBg = item.IsPinned || isSelected
                ? NotchDesign.CreateSoftAccentTileBrush((byte)(isSelected ? 0x24 : 0x16))
                : NotchDesign.CardBackgroundBrush;
            Brush hoverBg = item.IsPinned || isSelected
                ? NotchDesign.CreateSoftAccentTileBrush(0x2C)
                : NotchDesign.NestedCardBackgroundBrush;

            var card = new Border
            {
                Background = baseBg,
                BorderBrush = isSelected ? NotchDesign.CreateSoftAccentTileBrush(0x55) : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(NotchDesign.RadiusInner),
                Padding = new Thickness(10, 9, 10, 9),
                Margin = new Thickness(0, 0, 0, 5),
                Cursor = Cursors.Hand
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var thumb = CreateItemBadge(item, isCode);
            thumb.Margin = new Thickness(0, 1, 10, 0);
            thumb.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(thumb, 0);
            row.Children.Add(thumb);

            var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textCol.Children.Add(new TextBlock
            {
                Text = item.PreviewText,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = isCode ? new FontFamily("Cascadia Mono, Consolas, Courier New") : NotchDesign.UiTextFont,
                FontSize = isCode ? 11.5 : 12.2,
                FontWeight = FontWeights.Medium,
                LineHeight = 16.5,
                MaxHeight = 33,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap
            });

            var metaRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 0)
            };

            string kindLabel = item.Kind switch
            {
                ClipboardItemKind.Link => "Link",
                ClipboardItemKind.Image => "Bild",
                _ => "Text"
            };
            Brush pillBg = NotchDesign.TrackBrush;
            Brush pillFg = NotchDesign.TextSecondaryBrush;

            metaRow.Children.Add(new Border
            {
                Background = pillBg,
                CornerRadius = new CornerRadius(NotchDesign.RadiusSmall),
                Padding = new Thickness(6, 1.5, 6, 1.5),
                Margin = new Thickness(0, 0, 6, 0),
                Child = new TextBlock
                {
                    Text = kindLabel,
                    Foreground = pillFg,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold
                }
            });

            string metaDetail = item.Kind switch
            {
                ClipboardItemKind.Link when Uri.TryCreate(item.TextContent, UriKind.Absolute, out var u) => u.Host.Replace("www.", ""),
                ClipboardItemKind.Image => "PNG",
                _ => $"{item.TextContent?.Length ?? 0} Zeichen"
            };
            var metaText = new TextBlock
            {
                Text = $"{metaDetail} · {FormatRelativeTime(item.CreatedAt)}",
                Foreground = NotchDesign.TextMutedBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            metaRow.Children.Add(metaText);
            textCol.Children.Add(metaRow);
            Grid.SetColumn(textCol, 1);
            row.Children.Add(textCol);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Opacity = item.IsPinned ? 1.0 : 0.0
            };

            var pinBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Pin, 11.5,
                    item.IsPinned ? Brushes.White : NotchDesign.TextSecondaryBrush),
                () => TogglePin(item),
                background: item.IsPinned ? NotchDesign.AccentPrimaryBrush : Brushes.Transparent,
                hoverBackground: item.IsPinned ? NotchDesign.AccentPrimaryBrush : NotchDesign.ControlHoverBrush,
                cornerRadius: 8,
                padding: new Thickness(5),
                tooltip: item.IsPinned ? L.T("clipboard.unpin") : L.T("clipboard.pin"));
            pinBtn.Margin = new Thickness(0, 0, 2, 0);
            actions.Children.Add(pinBtn);

            if (item.Kind == ClipboardItemKind.Link && !string.IsNullOrWhiteSpace(item.TextContent))
            {
                var openBtn = NotchDesign.CreateRoundedButton(
                    NotchDesign.CreateVectorIcon(NotchIconKind.ExternalLink, 11.5, NotchDesign.TextSecondaryBrush),
                    () => OpenLink(item.TextContent!),
                    background: Brushes.Transparent,
                    cornerRadius: 8,
                    padding: new Thickness(5),
                    tooltip: L.T("clipboard.open_link"));
                openBtn.Margin = new Thickness(0, 0, 2, 0);
                actions.Children.Add(openBtn);
            }

            var delBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Close, 11.5, NotchDesign.TextSecondaryBrush),
                () => DeleteItem(item),
                background: Brushes.Transparent,
                cornerRadius: 8,
                padding: new Thickness(5),
                tooltip: L.T("clipboard.delete_item"));
            actions.Children.Add(delBtn);

            Grid.SetColumn(actions, 2);
            row.Children.Add(actions);

            card.MouseEnter += (_, _) =>
            {
                card.Background = hoverBg;
                actions.Opacity = 1.0;
            };
            card.MouseLeave += (_, _) =>
            {
                card.Background = baseBg;
                actions.Opacity = item.IsPinned ? 1.0 : 0.0;
            };

            card.Child = row;
            card.MouseLeftButtonUp += (_, e) =>
            {
                if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
                NotchDesign.AnimatePressPulse(card, 0.985);
                _selectedItem = item;
                UpdateDetailView();
                CopyItemToClipboard(item, metaText);
            };
            return card;
        }

        private static bool LooksLikeCode(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length < 8) return false;
            return text.Contains("=>", StringComparison.Ordinal) ||
                   text.Contains("</", StringComparison.Ordinal) ||
                   text.Contains("function ", StringComparison.Ordinal) ||
                   text.Contains("class ", StringComparison.Ordinal) ||
                   text.Contains("import ", StringComparison.Ordinal) ||
                   text.Contains("SELECT ", StringComparison.OrdinalIgnoreCase) ||
                   (text.Contains('{') && text.Contains('}')) ||
                   (text.Contains('\n') && text.Contains(';'));
        }

        private static Border CreateItemBadge(ClipboardRecord item, bool isCode = false)
        {
            var box = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(9),
                Background = item.Kind == ClipboardItemKind.Image ? Brushes.Transparent : NotchDesign.NestedCardBackgroundBrush,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (item.Kind == ClipboardItemKind.Image && !string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(item.ImagePath, UriKind.Absolute);
                    bmp.DecodePixelWidth = 72;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();

                    var thumb = new Image
                    {
                        Source = bmp,
                        Width = 34,
                        Height = 34,
                        Stretch = Stretch.UniformToFill,
                        Clip = new RectangleGeometry(new Rect(0, 0, 34, 34), 9, 9)
                    };
                    box.Child = thumb;
                    return box;
                }
                catch
                {
                    // Fallback auf Icon
                }
            }

            if (item.Kind == ClipboardItemKind.Link)
            {
                if (SettingsService.Current.OptInFavicons &&
                    Uri.TryCreate(item.TextContent, UriKind.Absolute, out var uri))
                {
                    string cacheFile = GetFaviconCachePath(uri.Host);
                    if (File.Exists(cacheFile))
                    {
                        try
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.UriSource = new Uri(cacheFile, UriKind.Absolute);
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.EndInit();
                            bmp.Freeze();
                            box.Child = new Image
                            {
                                Source = bmp,
                                Width = 20,
                                Height = 20,
                                Stretch = Stretch.Uniform,
                                Clip = new RectangleGeometry(new Rect(0, 0, 20, 20), 5, 5)
                            };
                            return box;
                        }
                        catch { }
                    }
                }
                box.Child = NotchDesign.CreateVectorIcon(NotchIconKind.Link, 18, NotchDesign.TextSecondaryBrush, 1.7);
                return box;
            }

            if (item.Kind == ClipboardItemKind.Image)
            {
                box.Child = NotchDesign.CreateVectorIcon(NotchIconKind.Image, 18, NotchDesign.TextSecondaryBrush, 1.7);
                return box;
            }

            box.Child = NotchDesign.CreateVectorIcon(
                NotchIconKind.File,
                16,
                NotchDesign.TextSecondaryBrush,
                1.75);
            return box;
        }
    }
}
