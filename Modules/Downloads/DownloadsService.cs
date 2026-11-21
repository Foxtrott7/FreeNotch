using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Timers;
using Microsoft.Win32;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.Downloads
{
    public sealed class ActiveDownloadItem
    {
        public required string TempPath { get; set; }
        public required string DisplayName { get; set; }
        public long DownloadedBytes { get; set; }
        public long BrowserReceivedBytes { get; set; }
        public long? TotalBytes { get; set; }
        public double BytesPerSecond { get; set; }
        public DateTime StartedUtc { get; init; }
        public DateTime LastSampleUtc { get; set; }
        public long LastSampleBytes { get; set; }

        public double? ProgressRatio =>
            TotalBytes is > 0 ? Math.Clamp((double)DownloadedBytes / TotalBytes.Value, 0.0, 1.0) : null;

        public TimeSpan? EstimatedRemaining =>
            TotalBytes is > 0 && BytesPerSecond > 1024 && TotalBytes.Value > DownloadedBytes
                ? TimeSpan.FromSeconds((TotalBytes.Value - DownloadedBytes) / BytesPerSecond)
                : null;

        public ActiveDownloadItem Clone() => new()
        {
            TempPath = TempPath,
            DisplayName = DisplayName,
            DownloadedBytes = DownloadedBytes,
            BrowserReceivedBytes = BrowserReceivedBytes,
            TotalBytes = TotalBytes,
            BytesPerSecond = BytesPerSecond,
            StartedUtc = StartedUtc,
            LastSampleUtc = LastSampleUtc,
            LastSampleBytes = LastSampleBytes
        };
    }

    public sealed class CompletedDownloadItem
    {
        public required string FullPath { get; init; }
        public required string FileName { get; init; }
        public long SizeBytes { get; init; }
        public DateTime CompletedUtc { get; init; }
    }

    /// <summary>
    /// Überwacht den Windows-Downloads-Ordner auf aktive Browser-Downloads
    /// (inkl. Geschwindigkeit, Restzeit &amp; asynchronem Browser-DB-Lookup) sowie kürzlich abgeschlossene Dateien.
    /// </summary>
    public sealed partial class DownloadsService : IDisposable
    {
        private static readonly HashSet<string> TempExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".crdownload", ".part", ".partial", ".download", ".opdownload", ".tmp"
        };

        private const int MaxCompletedItems = 25;
        private static readonly TimeSpan RecentCompletionDuration = TimeSpan.FromSeconds(6);

        private readonly object _lock = new();
        private readonly Dictionary<string, ActiveDownloadItem> _active = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<CompletedDownloadItem> _completed = new();
        private readonly Timer _pollTimer;
        private FileSystemWatcher? _watcher;
        private bool _disposed;

        public string DownloadsFolderPath { get; }
        public string? RecentCompletedName { get; private set; }
        public DateTime RecentCompletedUntilUtc { get; private set; }

        /// <summary>Strukturelle Änderungen (Download gestartet/abgeschlossen/entfernt).</summary>
        public event Action? StateChanged;

        /// <summary>Laufende Fortschritts- &amp; Speed-Updates ohne Strukturwechsel.</summary>
        public event Action? ProgressUpdated;

        public bool HasActiveDownloads
        {
            get { lock (_lock) return _active.Count > 0; }
        }

        public bool HasActiveDownloadWithProgress
        {
            get { lock (_lock) return _active.Values.Any(a => a.TotalBytes is > 0); }
        }

        public bool HasRecentCompletion
        {
            get
            {
                lock (_lock)
                    return !string.IsNullOrEmpty(RecentCompletedName) && DateTime.UtcNow < RecentCompletedUntilUtc;
            }
        }

        public DownloadsService()
        {
            DownloadsFolderPath = ResolveDownloadsFolder();
            _pollTimer = new Timer(400) { AutoReset = true };
            _pollTimer.Elapsed += (_, _) => PollActiveDownloads();
        }

        public void Start()
        {
            if (_disposed) return;

            RefreshAll();

            if (_watcher != null || !Directory.Exists(DownloadsFolderPath))
                return;

            try
            {
                // Nur Dateinamen-Änderungen beobachten (Created/Renamed/Deleted), keine Chunk-Write-Events fluten
                _watcher = new FileSystemWatcher(DownloadsFolderPath)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };

                _watcher.Created += OnFileCreated;
                _watcher.Renamed += OnFileRenamed;
                _watcher.Deleted += OnFileDeleted;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        public void Stop()
        {
            _pollTimer.Stop();

            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        public IReadOnlyList<ActiveDownloadItem> GetActiveDownloads()
        {
            lock (_lock)
            {
                return _active.Values
                    .OrderByDescending(a => a.StartedUtc)
                    .Select(a => a.Clone())
                    .ToList();
            }
        }

        public IReadOnlyList<CompletedDownloadItem> GetCompletedDownloads()
        {
            lock (_lock)
                return _completed.ToList();
        }

        /// <summary>Liest aktive und zuletzt abgeschlossene Downloads neu vom Datenträger.</summary>
        public void RefreshAll()
        {
            if (!Directory.Exists(DownloadsFolderPath)) return;

            try
            {
                var dir = new DirectoryInfo(DownloadsFolderPath);
                var files = dir.EnumerateFiles()
                    .Where(f => !IsIgnoredFile(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();

                bool needHistoryFallback;
                lock (_lock)
                {
                    var seenTemp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in files)
                    {
                        if (IsTempDownloadFile(file.Name))
                        {
                            seenTemp.Add(file.FullName);
                            UpsertActiveLocked(file);
                        }
                    }

                    foreach (var gone in _active.Keys.Where(k => !seenTemp.Contains(k)).ToList())
                        _active.Remove(gone);

                    _completed.Clear();
                    foreach (var file in files.Where(f => !IsTempDownloadFile(f.Name)).Take(MaxCompletedItems))
                    {
                        if (HasCompanionTempFile(file.FullName)) continue;
                        _completed.Add(new CompletedDownloadItem
                        {
                            FullPath = file.FullName,
                            FileName = file.Name,
                            SizeBytes = SafeLength(file),
                            CompletedUtc = file.LastWriteTimeUtc
                        });
                    }

                    SyncPollTimerLocked();
                    needHistoryFallback = HasItemsNeedingFallbackLocked();
                }

                if (needHistoryFallback)
                    TriggerBackgroundHistoryLookup();

                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        /// <summary>Leert nur die angezeigte Liste in der Notch (löscht keine Dateien auf der Festplatte).</summary>
        public void ClearCompletedList()
        {
            lock (_lock)
            {
                _completed.Clear();
                RecentCompletedName = null;
                SyncPollTimerLocked();
            }
            StateChanged?.Invoke();
        }

        /// <summary>Bricht einen aktiven Download ab, indem die temporäre Download-Datei gelöscht wird.</summary>
        public void CancelDownload(string tempPath)
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }

            lock (_lock)
            {
                _active.Remove(tempPath);
                SyncPollTimerLocked();
            }
            StateChanged?.Invoke();
        }

        public static bool IsTempDownloadFile(string fileName)
        {
            var ext = Path.GetExtension(fileName);
            return !string.IsNullOrEmpty(ext) && TempExtensions.Contains(ext);
        }

        public static string CleanTempDisplayName(string fileName)
        {
            var ext = Path.GetExtension(fileName);
            if (!string.IsNullOrEmpty(ext) && TempExtensions.Contains(ext))
            {
                var stripped = Path.GetFileNameWithoutExtension(fileName);
                if (!string.IsNullOrWhiteSpace(stripped))
                {
                    if (stripped.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase))
                        return L.T("downloads.browser_download");
                    return stripped;
                }
            }
            return fileName;
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return $"{kb.ToString("0.#", CultureInfo.CurrentCulture)} KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return $"{mb.ToString("0.0", CultureInfo.CurrentCulture)} MB";
            double gb = mb / 1024.0;
            return $"{gb.ToString("0.00", CultureInfo.CurrentCulture)} GB";
        }

        public static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0) return "0 KB/s";
            return $"{FormatBytes((long)Math.Round(bytesPerSecond))}/s";
        }

        public static string FormatEta(TimeSpan? eta)
        {
            if (eta == null) return string.Empty;
            var t = eta.Value;
            if (t.TotalSeconds < 60) return L.F("downloads.eta_sec", Math.Max(1, (int)Math.Ceiling(t.TotalSeconds)));
            if (t.TotalMinutes < 60) return L.F("downloads.eta_min", (int)t.TotalMinutes, t.Seconds);
            return L.F("downloads.eta_hr", (int)t.TotalHours, t.Minutes);
        }

        public static void OpenFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        public static void RevealInExplorer(string path)
        {
            try
            {
                if (File.Exists(path))
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                else if (Directory.Exists(Path.GetDirectoryName(path)))
                    Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path)!) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        public void OpenDownloadsFolder()
        {
            try
            {
                if (Directory.Exists(DownloadsFolderPath))
                    Process.Start(new ProcessStartInfo(DownloadsFolderPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            try
            {
                if (!File.Exists(e.FullPath)) return;
                var info = new FileInfo(e.FullPath);
                if (IsIgnoredFile(info)) return;

                bool needHistoryFallback;
                lock (_lock)
                {
                    if (IsTempDownloadFile(info.Name))
                        UpsertActiveLocked(info);
                    else
                        UpsertCompletedLocked(info, markRecent: true);

                    SyncPollTimerLocked();
                    needHistoryFallback = HasItemsNeedingFallbackLocked();
                }

                if (needHistoryFallback)
                    TriggerBackgroundHistoryLookup();

                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            try
            {
                bool changed = false;
                bool needHistoryFallback;

                lock (_lock)
                {
                    _active.TryGetValue(e.OldFullPath, out var oldItem);
                    if (oldItem != null)
                    {
                        _active.Remove(e.OldFullPath);
                        changed = true;
                    }

                    if (File.Exists(e.FullPath))
                    {
                        var info = new FileInfo(e.FullPath);
                        if (!IsIgnoredFile(info))
                        {
                            if (IsTempDownloadFile(info.Name))
                            {
                                if (oldItem != null)
                                {
                                    // Metadaten (TotalBytes, BrowserReceivedBytes, Speed, Startzeit) bei Umbenennung behalten
                                    long diskLen = SafeLength(info);
                                    string cleaned = CleanTempDisplayName(info.Name);
                                    oldItem.TempPath = info.FullName;
                                    if (!string.IsNullOrWhiteSpace(cleaned) && cleaned != "Browser-Download")
                                        oldItem.DisplayName = cleaned;
                                    oldItem.DownloadedBytes = Math.Max(diskLen, oldItem.DownloadedBytes);
                                    _active[info.FullName] = oldItem;
                                }
                                else
                                {
                                    UpsertActiveLocked(info);
                                }
                            }
                            else
                            {
                                UpsertCompletedLocked(info, markRecent: true);
                            }
                            changed = true;
                        }
                    }

                    SyncPollTimerLocked();
                    needHistoryFallback = HasItemsNeedingFallbackLocked();
                }

                if (needHistoryFallback)
                    TriggerBackgroundHistoryLookup();

                if (changed)
                    StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private void OnFileDeleted(object sender, FileSystemEventArgs e)
        {
            bool changed;
            lock (_lock)
            {
                changed = _active.Remove(e.FullPath);
                changed |= _completed.RemoveAll(c => string.Equals(c.FullPath, e.FullPath, StringComparison.OrdinalIgnoreCase)) > 0;
                SyncPollTimerLocked();
            }

            if (changed)
                StateChanged?.Invoke();
        }

        private void PollActiveDownloads()
        {
            if (_disposed) return;

            bool structuralChanged = false;
            bool progressChanged = false;
            bool needHistoryFallback;
            var now = DateTime.UtcNow;

            lock (_lock)
            {
                if (!string.IsNullOrEmpty(RecentCompletedName) && now >= RecentCompletedUntilUtc)
                {
                    RecentCompletedName = null;
                    structuralChanged = true;
                }

                foreach (var key in _active.Keys.ToList())
                {
                    var item = _active[key];
                    if (!File.Exists(item.TempPath))
                    {
                        _active.Remove(key);
                        structuralChanged = true;
                        continue;
                    }

                    long diskLen = SafeLength(new FileInfo(item.TempPath));
                    long len = (item.TotalBytes is > 0 && diskLen >= item.TotalBytes.Value &&
                                item.BrowserReceivedBytes > 0 && item.BrowserReceivedBytes < item.TotalBytes.Value)
                        ? item.BrowserReceivedBytes
                        : Math.Max(diskLen, item.BrowserReceivedBytes);

                    double dt = (now - item.LastSampleUtc).TotalSeconds;
                    if (dt >= 0.35)
                    {
                        double instantSpeed = Math.Max(0, (len - item.LastSampleBytes) / dt);
                        item.BytesPerSecond = item.BytesPerSecond <= 0
                            ? instantSpeed
                            : item.BytesPerSecond * 0.6 + instantSpeed * 0.4;
                        item.LastSampleBytes = len;
                        item.LastSampleUtc = now;
                    }

                    item.DownloadedBytes = len;
                    progressChanged = true;
                }

                needHistoryFallback = HasItemsNeedingFallbackLocked();
                SyncPollTimerLocked();
            }

            if (needHistoryFallback)
                TriggerBackgroundHistoryLookup();

            if (structuralChanged)
                StateChanged?.Invoke();
            else if (progressChanged)
                ProgressUpdated?.Invoke();
        }

        private void UpsertActiveLocked(FileInfo file)
        {
            var now = DateTime.UtcNow;
            long len = SafeLength(file);

            if (_active.TryGetValue(file.FullName, out var existing))
            {
                existing.DownloadedBytes = Math.Max(len, existing.BrowserReceivedBytes);
                if (existing.TotalBytes == null)
                    existing.DisplayName = CleanTempDisplayName(file.Name);
                return;
            }

            _active[file.FullName] = new ActiveDownloadItem
            {
                TempPath = file.FullName,
                DisplayName = CleanTempDisplayName(file.Name),
                DownloadedBytes = len,
                StartedUtc = now,
                LastSampleUtc = now,
                LastSampleBytes = len
            };
        }

        private void UpsertCompletedLocked(FileInfo file, bool markRecent)
        {
            long len = SafeLength(file);
            if (len == 0 && HasCompanionTempFile(file.FullName))
                return;

            _completed.RemoveAll(c => string.Equals(c.FullPath, file.FullName, StringComparison.OrdinalIgnoreCase));
            _completed.Insert(0, new CompletedDownloadItem
            {
                FullPath = file.FullName,
                FileName = file.Name,
                SizeBytes = len,
                CompletedUtc = file.LastWriteTimeUtc
            });

            if (_completed.Count > MaxCompletedItems)
                _completed.RemoveRange(MaxCompletedItems, _completed.Count - MaxCompletedItems);

            if (markRecent && len > 0)
            {
                RecentCompletedName = file.Name;
                RecentCompletedUntilUtc = DateTime.UtcNow.Add(RecentCompletionDuration);
            }
        }

        private void SyncPollTimerLocked()
        {
            bool shouldRun = _active.Count > 0 ||
                             (!string.IsNullOrEmpty(RecentCompletedName) && DateTime.UtcNow < RecentCompletedUntilUtc);
            if (shouldRun && !_pollTimer.Enabled)
                _pollTimer.Start();
            else if (!shouldRun && _pollTimer.Enabled)
                _pollTimer.Stop();
        }

        private static bool HasCompanionTempFile(string fullPath)
        {
            foreach (var ext in TempExtensions)
            {
                if (File.Exists(fullPath + ext))
                    return true;
            }
            return false;
        }

        private static bool IsIgnoredFile(FileInfo file)
        {
            if (file.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return true;
            return (file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }

        private static long SafeLength(FileInfo file)
        {
            try
            {
                using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return fs.Length;
            }
            catch
            {
                try
                {
                    file.Refresh();
                    return file.Exists ? file.Length : 0;
                }
                catch
                {
                    return 0;
                }
            }
        }

        private static string ResolveDownloadsFolder()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
                if (key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string raw && !string.IsNullOrWhiteSpace(raw))
                {
                    var expanded = Environment.ExpandEnvironmentVariables(raw);
                    if (Directory.Exists(expanded))
                        return expanded;
                }
            }
            catch
            {
                // Fallback auf Standardpfad
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _pollTimer.Dispose();
        }
    }
}
