using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WindowsNotch.Core;
using WindowsNotch.Services;
using WindowsNotch.Services.Database;

namespace WindowsNotch.Modules.FileTray
{
    /// <summary>
    /// File Tray Modul: Drag & Drop Datei-Ablage mit Thumbnails, SQLite-Persistenz und Herausziehen in andere Apps.
    /// </summary>
    public class FileTrayModule : NotchModuleBase
    {
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico"
        };

        private readonly List<FileTrayRecord> _items = new();
        private readonly object _lock = new();
        private Panel? _listPanel;
        private Border? _dropContainer;

        public override string Id => "filetray";
        public override string Title => "File Tray";
        public override NotchIconKind IconKind => NotchIconKind.FileTray;
        public override int Priority => 12;
        public override double? PreferredPreviewWidth => 180;
        public override (double Width, double Height)? PreferredExpandedSize => (584, 270);

        public override void Start()
        {
            ReloadFromDatabase();
        }

        private void ReloadFromDatabase()
        {
            try
            {
                var loaded = NotchDatabase.Instance.GetFileTrayItems();
                lock (_lock)
                {
                    _items.Clear();
                    _items.AddRange(loaded);
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        /// <summary>Fügt Dateien oder Ordner per Pfad hinzu und aktualisiert die Ansicht.</summary>
        public void AddFiles(IEnumerable<string> paths)
        {
            bool addedAny = false;
            foreach (string rawPath in paths)
            {
                if (string.IsNullOrWhiteSpace(rawPath)) continue;
                try
                {
                    bool isDir = Directory.Exists(rawPath);
                    bool isFile = !isDir && File.Exists(rawPath);
                    if (!isDir && !isFile) continue;

                    long size = 0;
                    string name;
                    if (isDir)
                    {
                        var dirInfo = new DirectoryInfo(rawPath);
                        name = dirInfo.Name;
                    }
                    else
                    {
                        var fileInfo = new FileInfo(rawPath);
                        name = fileInfo.Name;
                        size = fileInfo.Length;
                    }

                    var record = new FileTrayRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        FilePath = rawPath,
                        FileName = name,
                        FileSize = size,
                        IsDirectory = isDir,
                        AddedAt = DateTime.Now,
                        IsPinned = false
                    };

                    NotchDatabase.Instance.UpsertFileTrayItem(record);
                    addedAny = true;
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                }
            }

            if (addedAny)
            {
                ReloadFromDatabase();
                RefreshUiOnDispatcher();
                RaiseStateChanged();
            }
        }

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

            var icon = NotchDesign.CreateVectorIcon(NotchIconKind.FileTray, 13, NotchDesign.AccentPrimaryBrush);
            icon.Margin = new Thickness(0, 0, 7, 0);

            var text = new TextBlock
            {
                Text = count == 0 ? L.T("module.filetray") : (count == 1 ? L.T("filetray.count_one") : L.F("filetray.count_many", count)),
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
            // Direkt auf dem Notch-Hintergrund ohne Kopfzeile oder Karten-Box (wie Desktop-Ablage)
            _listPanel = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                MaxWidth = 524,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4)
            };
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = Brushes.Transparent,
                Content = _listPanel
            };

            _dropContainer = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                AllowDrop = true,
                Child = scroll
            };

            _dropContainer.DragEnter += DropContainer_DragEnter;
            _dropContainer.DragOver += DropContainer_DragOver;
            _dropContainer.DragLeave += DropContainer_DragLeave;
            _dropContainer.Drop += DropContainer_Drop;
            _dropContainer.MouseLeftButtonUp += (_, e) =>
            {
                if (!IsInsideFileTileOrButton(e.OriginalSource as DependencyObject))
                    OpenFilePicker();
            };

            var bgMenu = new ContextMenu();
            var addMenu = new MenuItem { Header = L.T("filetray.menu_add") };
            addMenu.Click += (_, _) => OpenFilePicker();
            var clearMenu = new MenuItem { Header = L.T("filetray.menu_clear_unpinned") };
            clearMenu.Click += (_, _) => ClearUnpinned();
            bgMenu.Items.Add(addMenu);
            bgMenu.Items.Add(clearMenu);
            _dropContainer.ContextMenu = bgMenu;

            RebuildList();
            return _dropContainer;
        }

        private void DropContainer_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                if (_dropContainer != null)
                {
                    _dropContainer.BorderBrush = NotchDesign.AccentPrimaryBrush;
                    _dropContainer.BorderThickness = new Thickness(1.5);
                }
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void DropContainer_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void DropContainer_DragLeave(object sender, DragEventArgs e)
        {
            if (_dropContainer != null)
            {
                _dropContainer.BorderBrush = Brushes.Transparent;
                _dropContainer.BorderThickness = new Thickness(1);
            }
        }

        private void DropContainer_Drop(object sender, DragEventArgs e)
        {
            DropContainer_DragLeave(sender, e);
            if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                AddFiles(files);
                e.Handled = true;
            }
        }

        private void OpenFilePicker()
        {
            var dlg = new OpenFileDialog
            {
                Title = L.T("filetray.picker_title"),
                Multiselect = true
            };
            if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
            {
                AddFiles(dlg.FileNames);
            }
        }

        private void ClearUnpinned()
        {
            try
            {
                NotchDatabase.Instance.ClearFileTray(includePinned: false);
                ReloadFromDatabase();
                RebuildList();
                RaiseStateChanged();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private void RefreshUiOnDispatcher()
        {
            Application.Current?.Dispatcher.BeginInvoke(RebuildList);
        }

        private void RebuildList()
        {
            if (_listPanel == null) return;
            _listPanel.Children.Clear();

            List<FileTrayRecord> snapshot;
            lock (_lock) snapshot = _items.ToList();

            if (snapshot.Count == 0)
            {
                var empty = new StackPanel
                {
                    Width = 516,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 26, 0, 26),
                    Cursor = Cursors.Hand
                };
                empty.Children.Add(new Border
                {
                    Width = 44,
                    Height = 44,
                    CornerRadius = new CornerRadius(14),
                    Background = NotchDesign.CreateSoftAccentTileBrush(0x22),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = NotchDesign.CreateVectorIcon(NotchIconKind.FileTray, 20, NotchDesign.AccentPrimaryBrush)
                });
                empty.Children.Add(new TextBlock
                {
                    Text = L.T("filetray.empty_title"),
                    Foreground = NotchDesign.TextPrimaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 13,
                    FontWeight = FontWeights.Medium,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 10, 0, 3)
                });
                empty.Children.Add(new TextBlock
                {
                    Text = L.T("filetray.empty_hint"),
                    Foreground = NotchDesign.TextSecondaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11.5,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                _listPanel.Children.Add(empty);
                return;
            }

            foreach (var item in snapshot)
            {
                _listPanel.Children.Add(BuildFileCard(item));
            }
        }

        private Border BuildFileCard(FileTrayRecord item)
        {
            bool exists = item.IsDirectory ? Directory.Exists(item.FilePath) : File.Exists(item.FilePath);

            var card = new Border
            {
                Tag = "FileTile",
                Width = 98,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(5, 6, 5, 6),
                Margin = new Thickness(3, 4, 3, 6),
                Cursor = exists ? Cursors.Hand : Cursors.Arrow,
                Opacity = exists ? 1.0 : 0.55,
                ToolTip = item.FileName
            };

            var hoverBrush = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
            card.MouseEnter += (_, _) => card.Background = hoverBrush;
            card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;

            var tile = new StackPanel();

            var thumbBox = CreateThumbnailElement(item, exists);
            thumbBox.HorizontalAlignment = HorizontalAlignment.Center;
            thumbBox.Margin = new Thickness(0, 0, 0, 6);
            tile.Children.Add(thumbBox);

            tile.Children.Add(new TextBlock
            {
                Text = item.FileName,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxWidth = 86
            });

            tile.Children.Add(new TextBlock
            {
                Text = exists ? item.AddedAt.ToString("yy/MM/dd HH:mm") : L.T("filetray.missing"),
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10,
                Opacity = 0.72,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            });

            tile.Children.Add(new TextBlock
            {
                Text = exists ? (item.IsDirectory ? L.T("filetray.folder") : FormatSize(item.FileSize)) : "—",
                Foreground = NotchDesign.TextMutedBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10,
                Opacity = 0.68,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0)
            });

            card.Child = tile;

            // Verwaltung per Rechtsklick-Kontextmenü (keine störenden Pin-/X-Buttons)
            var menu = new ContextMenu();
            var pinMenu = new MenuItem { Header = item.IsPinned ? L.T("filetray.unpin") : L.T("filetray.pin") };
            pinMenu.Click += (_, _) => TogglePin(item);
            menu.Items.Add(pinMenu);

            if (exists)
            {
                var revealMenu = new MenuItem { Header = L.T("filetray.reveal") };
                revealMenu.Click += (_, _) => RevealInExplorer(item);
                menu.Items.Add(revealMenu);
            }

            var removeMenu = new MenuItem { Header = L.T("filetray.remove") };
            removeMenu.Click += (_, _) => RemoveItem(item);
            menu.Items.Add(removeMenu);
            card.ContextMenu = menu;

            // Herausziehen in andere Apps ermöglichen + Doppelklick öffnet im Explorer
            if (exists)
            {
                Point? dragStart = null;
                card.PreviewMouseLeftButtonDown += (_, e) =>
                {
                    if (e.ClickCount == 2)
                    {
                        RevealInExplorer(item);
                        dragStart = null;
                        return;
                    }
                    dragStart = e.GetPosition(card);
                };
                card.PreviewMouseMove += (_, e) =>
                {
                    if (dragStart == null || e.LeftButton != MouseButtonState.Pressed)
                        return;

                    Point current = e.GetPosition(card);
                    if (Math.Abs(current.X - dragStart.Value.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                        Math.Abs(current.Y - dragStart.Value.Y) >= SystemParameters.MinimumVerticalDragDistance)
                    {
                        dragStart = null;
                        try
                        {
                            var data = new DataObject(DataFormats.FileDrop, new[] { item.FilePath });
                            DragDrop.DoDragDrop(card, data, DragDropEffects.Copy | DragDropEffects.Move);
                        }
                        catch (Exception ex)
                        {
                            App.LogError(ex);
                        }
                    }
                };
            }

            return card;
        }

        private static bool IsInsideFileTileOrButton(DependencyObject? dep)
        {
            while (dep != null)
            {
                if (dep is Button) return true;
                if (dep is Border b && Equals(b.Tag, "FileTile")) return true;
                dep = VisualTreeHelper.GetParent(dep);
            }
            return false;
        }

        private static bool IsInsideButton(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is Button) return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private static Border CreateThumbnailElement(FileTrayRecord item, bool exists)
        {
            var box = new Border
            {
                Width = 64,
                Height = 46,
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                ClipToBounds = true
            };

            if (!exists)
            {
                box.Child = NotchDesign.CreateVectorIcon(NotchIconKind.File, 34, NotchDesign.TextSecondaryBrush);
                return box;
            }

            if (item.IsDirectory)
            {
                box.Child = NotchDesign.CreateVectorIcon(NotchIconKind.Folder, 38, NotchDesign.AccentPrimaryBrush);
                return box;
            }

            string ext = Path.GetExtension(item.FilePath);
            if (ImageExtensions.Contains(ext))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(item.FilePath, UriKind.Absolute);
                    bmp.DecodePixelWidth = 128;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();

                    var img = new Image
                    {
                        Source = bmp,
                        Stretch = Stretch.Uniform
                    };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    box.Child = img;
                    return box;
                }
                catch
                {
                    // Fallback auf Shell-Icon
                }
            }

            try
            {
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(item.FilePath);
                if (sysIcon != null)
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(
                        sysIcon.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(48, 48));
                    source.Freeze();

                    var img = new Image
                    {
                        Source = source,
                        Width = 42,
                        Height = 42,
                        Stretch = Stretch.Uniform
                    };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    box.Child = img;
                    return box;
                }
            }
            catch
            {
                // Fallback auf Vektor-Icon
            }

            box.Child = NotchDesign.CreateVectorIcon(NotchIconKind.File, 34, NotchDesign.TextSecondaryBrush);
            return box;
        }

        private void TogglePin(FileTrayRecord item)
        {
            try
            {
                NotchDatabase.Instance.SetFileTrayPinned(item.Id, !item.IsPinned);
                ReloadFromDatabase();
                RebuildList();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private void RemoveItem(FileTrayRecord item)
        {
            try
            {
                NotchDatabase.Instance.DeleteFileTrayItem(item.Id);
                ReloadFromDatabase();
                RebuildList();
                RaiseStateChanged();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private static void RevealInExplorer(FileTrayRecord item)
        {
            try
            {
                if (item.IsDirectory)
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.FilePath}\"") { UseShellExecute = true });
                }
                else
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FilePath}\"") { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return $"{kb:0.#} KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return $"{mb:0.#} MB";
            double gb = mb / 1024.0;
            return $"{gb:0.##} GB";
        }

        private static string FormatRelativeTime(DateTime dt)
        {
            var diff = DateTime.Now - dt;
            if (diff.TotalSeconds < 60) return L.T("common.just_now");
            if (diff.TotalMinutes < 60) return L.F("common.minutes_ago", (int)diff.TotalMinutes);
            if (diff.TotalHours < 24 && dt.Date == DateTime.Today) return dt.ToString("HH:mm");
            return dt.ToString("dd.MM.");
        }
    }
}
