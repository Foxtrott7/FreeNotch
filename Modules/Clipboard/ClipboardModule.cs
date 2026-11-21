using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsNotch.Core;
using WindowsNotch.Services;
using WindowsNotch.Services.Database;

namespace WindowsNotch.Modules.Clipboard
{
    /// <summary>
    /// Zwischenablage-Verlauf mit Win32-Listener, SQLite-Persistenz, Suche, Filtern, Pinning und optionalen Favicons.
    /// </summary>
    public partial class ClipboardModule : NotchModuleBase
    {
        private static readonly HttpClient FaviconHttp = new() { Timeout = TimeSpan.FromSeconds(3) };

        private readonly List<ClipboardRecord> _items = new();
        private readonly object _lock = new();

        private HwndSource? _hwndSource;
        private DateTime _ignoreUpdatesUntilUtc = DateTime.MinValue;
        private bool _isSplitExpanded;
        private string _searchQuery = string.Empty;
        private ClipboardRecord? _selectedItem;

        private StackPanel? _listPanel;
        private ScrollViewer? _listScroll;
        private Button? _clearBtn;
        private Button? _expandBtn;
        private TextBox? _searchBox;
        private ColumnDefinition? _detailCol;
        private Border? _detailHost;
        private double? _dynamicExpandedHeight;

        public override string Id => "clipboard";
        public override string Title => L.T("module.clipboard");
        public override NotchIconKind IconKind => NotchIconKind.Clipboard;
        public override int Priority => 10;
        public override double? PreferredPreviewWidth => 190;
        public override (double Width, double Height)? PreferredExpandedSize =>
            _isSplitExpanded ? (680, _dynamicExpandedHeight ?? 445) : (410, _dynamicExpandedHeight ?? 435);

        public override void Start()
        {
            if (!SettingsService.Current.IsWorkEdition)
                ReloadFromDatabase();
            Application.Current?.Dispatcher.BeginInvoke(StartClipboardListener);
        }

        public override void Stop()
        {
            Application.Current?.Dispatcher.BeginInvoke(StopClipboardListener);
        }

        private void StartClipboardListener()
        {
            if (_hwndSource != null) return;
            try
            {
                var parameters = new HwndSourceParameters("WindowsNotchClipboardMonitor")
                {
                    Width = 0,
                    Height = 0,
                    PositionX = 0,
                    PositionY = 0,
                    WindowStyle = 0,
                    ParentWindow = new IntPtr(-3) // HWND_MESSAGE
                };
                _hwndSource = new HwndSource(parameters);
                _hwndSource.AddHook(WndProc);
                NativeMethods.AddClipboardFormatListener(_hwndSource.Handle);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private void StopClipboardListener()
        {
            if (_hwndSource == null) return;
            try
            {
                NativeMethods.RemoveClipboardFormatListener(_hwndSource.Handle);
                _hwndSource.RemoveHook(WndProc);
                _hwndSource.Dispose();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
            finally
            {
                _hwndSource = null;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
            {
                if (DateTime.UtcNow < _ignoreUpdatesUntilUtc)
                    return IntPtr.Zero;

                _ = CaptureClipboardWithRetryAsync();
            }
            return IntPtr.Zero;
        }

        private async Task CaptureClipboardWithRetryAsync()
        {
            await Task.Delay(50);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    bool captured = false;
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        captured = TryCaptureCurrentClipboard();
                    });
                    if (captured) break;
                }
                catch
                {
                    // Zwischenablage kurzzeitig durch andere App gesperrt
                }
                await Task.Delay(50);
            }
        }

        private bool TryCaptureCurrentClipboard()
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    string raw = System.Windows.Clipboard.GetText();
                    if (string.IsNullOrWhiteSpace(raw)) return true;

                    string trimmed = raw.Trim();
                    bool isLink = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
                                  (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                                   uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));

                    var kind = isLink ? ClipboardItemKind.Link : ClipboardItemKind.Text;
                    string payload = isLink ? trimmed : raw;
                    string hash = ComputeHash($"{kind}:{payload}");

                    string preview = CollapseWhitespace(trimmed);
                    if (preview.Length > 140) preview = preview[..140] + "…";

                    var record = new ClipboardRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Kind = kind,
                        TextContent = payload,
                        PreviewText = preview,
                        CreatedAt = DateTime.Now,
                        ContentHash = hash
                    };

                    if (SettingsService.Current.IsWorkEdition)
                        AddOrBumpVolatileItem(record);
                    else
                    {
                        NotchDatabase.Instance.AddOrBumpClipboardItem(record);
                        ReloadFromDatabase();
                    }
                    RefreshUiOnDispatcher();
                    RaiseStateChanged();

                    if (isLink && uri != null && SettingsService.Current.OptInFavicons)
                    {
                        _ = FetchFaviconAsync(uri.Host);
                    }
                    return true;
                }

                if (System.Windows.Clipboard.ContainsImage() && !SettingsService.Current.IsWorkEdition)
                {
                    var bmp = System.Windows.Clipboard.GetImage();
                    if (bmp == null) return true;

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bmp));
                    using var ms = new MemoryStream();
                    encoder.Save(ms);
                    byte[] bytes = ms.ToArray();

                    string hash = ComputeHash(bytes);
                    string id = Guid.NewGuid().ToString("N");
                    string imgPath = Path.Combine(NotchDatabase.Instance.ClipboardImagesDirectory, $"{id}.png");

                    NotchDatabase.Instance.EnsureInitialized();
                    File.WriteAllBytes(imgPath, bytes);

                    var record = new ClipboardRecord
                    {
                        Id = id,
                        Kind = ClipboardItemKind.Image,
                        ImagePath = imgPath,
                        PreviewText = L.F("clipboard.image_preview", bmp.PixelWidth, bmp.PixelHeight),
                        CreatedAt = DateTime.Now,
                        ContentHash = hash
                    };

                    NotchDatabase.Instance.AddOrBumpClipboardItem(record);
                    ReloadFromDatabase();
                    RefreshUiOnDispatcher();
                    RaiseStateChanged();
                    return true;
                }
            }
            catch
            {
                return false;
            }
            return true;
        }

        private async Task FetchFaviconAsync(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return;
            try
            {
                string cacheFile = GetFaviconCachePath(host);
                if (File.Exists(cacheFile)) return;

                string url = $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(host)}&sz=32";
                byte[] data = await FaviconHttp.GetByteArrayAsync(url);
                if (data.Length > 0)
                {
                    await File.WriteAllBytesAsync(cacheFile, data);
                    RefreshUiOnDispatcher();
                }
            }
            catch
            {
                // Favicon ist rein optional
            }
        }

        private static string GetFaviconCachePath(string host)
        {
            string safeHost = string.Concat(host.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'));
            if (string.IsNullOrWhiteSpace(safeHost) || safeHost is "." or "..")
                safeHost = "site";
            return Path.Combine(NotchDatabase.Instance.FaviconCacheDirectory, $"{safeHost}.png");
        }

        private void AddOrBumpVolatileItem(ClipboardRecord record)
        {
            lock (_lock)
            {
                int idx = _items.FindIndex(x => string.Equals(x.ContentHash, record.ContentHash, StringComparison.Ordinal));
                if (idx >= 0)
                {
                    record.IsPinned = _items[idx].IsPinned;
                    _items.RemoveAt(idx);
                }
                int insertAt = record.IsPinned ? 0 : _items.TakeWhile(x => x.IsPinned).Count();
                _items.Insert(insertAt, record);
                while (_items.Count > 50)
                    _items.RemoveAt(_items.Count - 1);
            }
        }

        private void ReloadFromDatabase()
        {
            try
            {
                var loaded = NotchDatabase.Instance.GetClipboardItems();
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

        private void ClearUnpinned()
        {
            try
            {
                if (SettingsService.Current.IsWorkEdition)
                {
                    lock (_lock) { _items.RemoveAll(x => !x.IsPinned); }
                }
                else
                {
                    NotchDatabase.Instance.ClearClipboard(includePinned: false);
                    ReloadFromDatabase();
                }
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

        private void CopyItemToClipboard(ClipboardRecord item, TextBlock metaText)
        {
            try
            {
                _ignoreUpdatesUntilUtc = DateTime.UtcNow.AddMilliseconds(600);
                if (item.Kind == ClipboardItemKind.Image && !string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(item.ImagePath, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    System.Windows.Clipboard.SetImage(bmp);
                }
                else if (!string.IsNullOrEmpty(item.TextContent))
                {
                    System.Windows.Clipboard.SetText(item.TextContent);
                }

                string oldText = metaText.Text;
                var oldBrush = metaText.Foreground;
                metaText.Text = L.T("clipboard.copied_feedback");
                metaText.Foreground = new SolidColorBrush(NotchDesign.AccentGreen);

                _ = Task.Delay(1200).ContinueWith(_ =>
                {
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        metaText.Text = oldText;
                        metaText.Foreground = oldBrush;
                    });
                });
            }
            catch (Exception ex)
            {
                _ignoreUpdatesUntilUtc = DateTime.MinValue;
                App.LogError(ex);
            }
        }

        private void TogglePin(ClipboardRecord item)
        {
            try
            {
                if (SettingsService.Current.IsWorkEdition)
                {
                    lock (_lock)
                    {
                        item.IsPinned = !item.IsPinned;
                        _items.Sort((a, b) => b.IsPinned != a.IsPinned
                            ? b.IsPinned.CompareTo(a.IsPinned)
                            : b.CreatedAt.CompareTo(a.CreatedAt));
                    }
                }
                else
                {
                    NotchDatabase.Instance.SetClipboardPinned(item.Id, !item.IsPinned);
                    ReloadFromDatabase();
                }
                RebuildList();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private void DeleteItem(ClipboardRecord item)
        {
            try
            {
                if (SettingsService.Current.IsWorkEdition)
                {
                    lock (_lock) { _items.RemoveAll(x => x.Id == item.Id); }
                }
                else
                {
                    NotchDatabase.Instance.DeleteClipboardItem(item.Id);
                    ReloadFromDatabase();
                }
                RebuildList();
                RaiseStateChanged();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private static void OpenLink(string url)
        {
            try
            {
                if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return;

                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
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

        private static string CollapseWhitespace(string input)
        {
            var sb = new StringBuilder(input.Length);
            bool prevSpace = false;
            foreach (char c in input)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!prevSpace)
                    {
                        sb.Append(' ');
                        prevSpace = true;
                    }
                }
                else
                {
                    sb.Append(c);
                    prevSpace = false;
                }
            }
            return sb.ToString().Trim();
        }

        private static string ComputeHash(string text)
            => ComputeHash(Encoding.UTF8.GetBytes(text));

        private static string ComputeHash(byte[] data)
        {
            byte[] hash = SHA256.HashData(data);
            return Convert.ToHexString(hash);
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
