using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace WindowsNotch.Modules.Downloads
{
    public sealed partial class DownloadsService
    {
        private sealed record DbSnapshotMeta(
            long Length,
            DateTime LastWriteUtc,
            long SidecarLength,
            DateTime SidecarLastWriteUtc);

        private sealed record HistoryRow(string CurPath, string TgtPath, long Total, long Received);

        private DateTime _lastHistoryLookupUtc = DateTime.MinValue;
        private int _historyLookupRunning;
        private bool _loggedBrowserLookupError;
        private readonly Dictionary<string, DbSnapshotMeta> _dbMetaCache = new(StringComparer.OrdinalIgnoreCase);

        private bool HasItemsNeedingFallbackLocked() =>
            _active.Values.Any(a => a.TotalBytes == null);

        private void TriggerBackgroundHistoryLookup()
        {
            if (_disposed || WindowsNotch.Services.SettingsService.Current.IsWorkEdition) return;
            var now = DateTime.UtcNow;
            if ((now - _lastHistoryLookupUtc).TotalSeconds < 2.0)
                return;
            if (Interlocked.CompareExchange(ref _historyLookupRunning, 1, 0) != 0)
                return;

            _lastHistoryLookupUtc = now;
            _ = Task.Run(() =>
            {
                try
                {
                    RunBackgroundHistoryLookup();
                }
                finally
                {
                    Interlocked.Exchange(ref _historyLookupRunning, 0);
                }
            });
        }

        private void RunBackgroundHistoryLookup()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(localAppData)) return;

            var chromiumRows = ReadChangedChromiumHistoryRows(localAppData, roamingAppData);
            var firefoxRows = ReadFirefoxActiveRows(roamingAppData);
            if (chromiumRows.Count == 0 && firefoxRows.Count == 0)
                return;

            bool updated = false;
            bool becameDeterminate = false;
            lock (_lock)
            {
                bool hadProgress = _active.Values.Any(a => a.TotalBytes is > 0);
                foreach (var (curPath, tgtPath, total, received) in chromiumRows)
                {
                    ActiveDownloadItem? item = null;
                    if (!string.IsNullOrEmpty(curPath) && !_active.TryGetValue(curPath, out item))
                    {
                        var curName = Path.GetFileName(curPath);
                        item = _active.Values.FirstOrDefault(a =>
                            string.Equals(Path.GetFileName(a.TempPath), curName, StringComparison.OrdinalIgnoreCase));
                    }

                    if (item == null && !string.IsNullOrEmpty(tgtPath))
                    {
                        var tgtName = Path.GetFileName(tgtPath);
                        item = _active.Values.FirstOrDefault(a =>
                            string.Equals(a.DisplayName, tgtName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(CleanTempDisplayName(Path.GetFileName(a.TempPath)), tgtName, StringComparison.OrdinalIgnoreCase));
                    }

                    if (item == null && _active.Count == 1 && chromiumRows.Count == 1)
                        item = _active.Values.First();

                    if (item == null) continue;

                    if (total > 0 && item.TotalBytes != total)
                    {
                        item.TotalBytes = total;
                        updated = true;
                    }
                    if (received > 0)
                    {
                        item.BrowserReceivedBytes = received;
                        if (item.DownloadedBytes == 0 || (total > 0 && item.DownloadedBytes >= total && received < total))
                            item.DownloadedBytes = received;
                        else
                            item.DownloadedBytes = Math.Max(item.DownloadedBytes, received);
                        updated = true;
                    }

                    var targetName = Path.GetFileName(tgtPath);
                    if (!string.IsNullOrWhiteSpace(targetName) && item.DisplayName != targetName)
                    {
                        item.DisplayName = targetName;
                        updated = true;
                    }
                }

                foreach (var (destPath, fileSize) in firefoxRows)
                {
                    string destName = Path.GetFileName(destPath);
                    if (!_active.TryGetValue(destPath + ".part", out var item))
                    {
                        item = _active.Values.FirstOrDefault(a =>
                            string.Equals(a.DisplayName, destName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Path.GetFileName(a.TempPath), destName + ".part", StringComparison.OrdinalIgnoreCase));
                    }

                    if (item != null)
                    {
                        if (item.TotalBytes != fileSize)
                        {
                            item.TotalBytes = fileSize;
                            updated = true;
                        }
                        item.DisplayName = destName;
                    }
                }

                becameDeterminate = !hadProgress && _active.Values.Any(a => a.TotalBytes is > 0);
            }

            if (becameDeterminate)
                StateChanged?.Invoke();
            else if (updated)
                ProgressUpdated?.Invoke();
        }

        private List<HistoryRow> ReadChangedChromiumHistoryRows(string localAppData, string roamingAppData)
        {
            var results = new List<HistoryRow>();
            var userDataRoots = new[]
            {
                Path.Combine(localAppData, "Microsoft", "Edge", "User Data"),
                Path.Combine(localAppData, "Google", "Chrome", "User Data"),
                Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"),
                Path.Combine(localAppData, "Vivaldi", "User Data"),
                Path.Combine(roamingAppData, "Opera Software", "Opera Stable"),
                Path.Combine(roamingAppData, "Opera Software", "Opera GX Stable")
            };

            var historyFiles = new List<string>();
            foreach (var root in userDataRoots)
            {
                if (!Directory.Exists(root)) continue;
                var direct = Path.Combine(root, "History");
                if (File.Exists(direct)) historyFiles.Add(direct);
                try
                {
                    foreach (var profDir in Directory.EnumerateDirectories(root))
                    {
                        var profHist = Path.Combine(profDir, "History");
                        if (File.Exists(profHist)) historyFiles.Add(profHist);
                    }
                }
                catch { }
            }

            foreach (var dbPath in historyFiles)
            {
                // Unveränderte 50-MB-DBs nicht erneut kopieren
                var meta = GetDbMeta(dbPath);
                if (meta == null) continue;
                if (_dbMetaCache.TryGetValue(dbPath, out var prev) && prev == meta)
                    continue;

                var tempDir = Path.Combine(Path.GetTempPath(), $"wn_hist_{Guid.NewGuid():N}");
                var tempDb = Path.Combine(tempDir, "History");
                try
                {
                    Directory.CreateDirectory(tempDir);
                    CopySharedFile(dbPath, tempDb);
                    bool hasSidecar = false;
                    if (File.Exists(dbPath + "-wal"))
                    {
                        CopySharedFileSafe(dbPath + "-wal", tempDb + "-wal");
                        hasSidecar = true;
                    }
                    if (File.Exists(dbPath + "-journal"))
                    {
                        CopySharedFileSafe(dbPath + "-journal", tempDb + "-journal");
                        hasSidecar = true;
                    }

                    string mode = hasSidecar ? "ReadWrite" : "ReadOnly";
                    using var conn = new SqliteConnection($"Data Source={tempDb};Mode={mode};Pooling=False");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    long cutoffWebKitMicros = (DateTime.UtcNow.AddHours(-12) - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks / 10;
                    cmd.CommandText = "SELECT current_path, target_path, total_bytes, received_bytes FROM downloads WHERE state = 0 AND start_time >= @cutoff ORDER BY start_time DESC LIMIT 10";
                    cmd.Parameters.AddWithValue("@cutoff", cutoffWebKitMicros);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        results.Add(new HistoryRow(
                            reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                            reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                            reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                            reader.IsDBNull(3) ? 0 : reader.GetInt64(3)));
                    }

                    _dbMetaCache[dbPath] = meta;
                }
                catch (Exception ex)
                {
                    if (!_loggedBrowserLookupError)
                    {
                        _loggedBrowserLookupError = true;
                        App.LogError(ex);
                    }
                }
                finally
                {
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }

            return results;
        }

        private List<(string DestPath, long FileSize)> ReadFirefoxActiveRows(string roamingAppData)
        {
            var results = new List<(string, long)>();
            if (string.IsNullOrEmpty(roamingAppData)) return results;

            var profilesRoot = Path.Combine(roamingAppData, "Mozilla", "Firefox", "Profiles");
            if (!Directory.Exists(profilesRoot)) return results;

            IEnumerable<string> profiles;
            try { profiles = Directory.EnumerateDirectories(profilesRoot); }
            catch { return results; }

            foreach (var profDir in profiles)
            {
                var placesPath = Path.Combine(profDir, "places.sqlite");
                if (!File.Exists(placesPath)) continue;

                var meta = GetDbMeta(placesPath);
                if (meta == null) continue;
                if (_dbMetaCache.TryGetValue(placesPath, out var prev) && prev == meta)
                    continue;

                var tempDir = Path.Combine(Path.GetTempPath(), $"wn_ff_{Guid.NewGuid():N}");
                var tempDb = Path.Combine(tempDir, "places.sqlite");
                try
                {
                    Directory.CreateDirectory(tempDir);
                    CopySharedFile(placesPath, tempDb);
                    CopySharedFileSafe(placesPath + "-wal", tempDb + "-wal");
                    CopySharedFileSafe(placesPath + "-journal", tempDb + "-journal");

                    using var conn = new SqliteConnection($"Data Source={tempDb};Mode=ReadWrite;Pooling=False");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        SELECT a.place_id, attr.name, a.content
                        FROM moz_annos a
                        JOIN moz_anno_attributes attr ON a.anno_attribute_id = attr.id
                        WHERE attr.name IN ('downloads/destinationFileURI', 'downloads/metaData')
                        ORDER BY a.id DESC
                        LIMIT 200";
                    using var reader = cmd.ExecuteReader();

                    var byPlace = new Dictionary<long, (string? Uri, string? Meta)>();
                    while (reader.Read())
                    {
                        long pid = reader.GetInt64(0);
                        string name = reader.GetString(1);
                        string content = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                        byPlace.TryGetValue(pid, out var cur);
                        if (name == "downloads/destinationFileURI")
                            cur.Uri ??= content;
                        else
                            cur.Meta ??= content;
                        byPlace[pid] = cur;
                    }

                    foreach (var (_, (uriStr, metaStr)) in byPlace)
                    {
                        if (string.IsNullOrEmpty(uriStr) || string.IsNullOrEmpty(metaStr)) continue;
                        if (!Uri.TryCreate(uriStr, UriKind.Absolute, out var uri) || !uri.IsFile) continue;

                        try
                        {
                            using var doc = JsonDocument.Parse(metaStr);
                            if (doc.RootElement.TryGetProperty("state", out var st) && st.TryGetInt32(out var s) && s == 0 &&
                                doc.RootElement.TryGetProperty("fileSize", out var fs) && fs.TryGetInt64(out var sz) && sz > 0)
                            {
                                results.Add((uri.LocalPath, sz));
                            }
                        }
                        catch { }
                    }

                    _dbMetaCache[placesPath] = meta;
                }
                catch { }
                finally
                {
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }

            return results;
        }

        private static DbSnapshotMeta? GetDbMeta(string dbPath)
        {
            try
            {
                var fi = new FileInfo(dbPath);
                if (!fi.Exists) return null;
                var walPath = dbPath + "-wal";
                var jrnPath = dbPath + "-journal";
                bool hasWal = File.Exists(walPath);
                bool hasJrn = !hasWal && File.Exists(jrnPath);

                long sideLen = hasWal
                    ? SafeLength(new FileInfo(walPath))
                    : (hasJrn ? SafeLength(new FileInfo(jrnPath)) : -1);
                DateTime sideTime = hasWal
                    ? File.GetLastWriteTimeUtc(walPath)
                    : (hasJrn ? File.GetLastWriteTimeUtc(jrnPath) : DateTime.MinValue);

                return new DbSnapshotMeta(SafeLength(fi), fi.LastWriteTimeUtc, sideLen, sideTime);
            }
            catch
            {
                return null;
            }
        }

        private static void CopySharedFile(string srcPath, string dstPath)
        {
            using var src = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = new FileStream(dstPath, FileMode.Create, FileAccess.Write, FileShare.None);
            src.CopyTo(dst);
        }

        private static void CopySharedFileSafe(string srcPath, string dstPath)
        {
            try
            {
                if (File.Exists(srcPath))
                    CopySharedFile(srcPath, dstPath);
            }
            catch { }
        }
    }
}
