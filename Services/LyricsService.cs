using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsNotch.Services
{
    public sealed record LyricLine(TimeSpan Time, string Text);

    public enum LyricsStatus
    {
        Disabled,
        Idle,
        Loading,
        Synced,
        Plain,
        Instrumental,
        NotFound,
        Error
    }

    public sealed class LyricsSnapshot
    {
        public string TrackKey { get; init; } = string.Empty;
        public LyricsStatus Status { get; init; }
        public IReadOnlyList<LyricLine> SyncedLines { get; init; } = Array.Empty<LyricLine>();
        public IReadOnlyList<string> PlainLines { get; init; } = Array.Empty<string>();
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Lädt synchronisierte Liedtexte über LRCLIB (ausschließlich bei aktivem Opt-in in den Einstellungen).
    /// </summary>
    public sealed class LyricsService
    {
        private static readonly Lazy<LyricsService> _instance = new(() => new LyricsService());
        public static LyricsService Instance => _instance.Value;

        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        private static readonly Regex LrcTimestampRegex = new(@"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
        private static readonly Regex SuffixNoiseRegex = new(@"\s*[\(\[](?:feat\.|ft\.|remaster|live|radio edit|official|video|audio|lyrics).*?[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly Dictionary<string, LyricsSnapshot> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> _cacheOrder = new();
        private CancellationTokenSource? _activeCts;
        private string _activeTrackKey = string.Empty;

        public LyricsSnapshot Current { get; private set; } = new() { Status = LyricsStatus.Idle };

        public event Action? LyricsUpdated;

        private LyricsService()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsNotch/1.0");
        }

        public void InvalidateAndRefresh()
        {
            _activeCts?.Cancel();
            _activeTrackKey = string.Empty;

            if (!SettingsService.Current.OptInLyrics)
            {
                Current = new LyricsSnapshot { Status = LyricsStatus.Disabled };
                LyricsUpdated?.Invoke();
                return;
            }

            var mp = MediaPlayerService.Instance;
            EnsureForTrack(mp.TrackTitle, mp.Artist, mp.AlbumTitle, mp.Duration, mp.HasKnownTrack, forceRefresh: true);
        }

        public void EnsureForTrack(
            string title,
            string artist,
            string album,
            TimeSpan duration,
            bool hasTrack,
            bool forceRefresh = false)
        {
            if (!SettingsService.Current.OptInLyrics)
            {
                if (Current.Status != LyricsStatus.Disabled)
                {
                    _activeCts?.Cancel();
                    _activeTrackKey = string.Empty;
                    Current = new LyricsSnapshot { Status = LyricsStatus.Disabled };
                    LyricsUpdated?.Invoke();
                }
                return;
            }

            if (!hasTrack || string.IsNullOrWhiteSpace(title) ||
                title == MediaPlayerService.DefaultNoTrackTitle ||
                title == "Unbekannter Titel")
            {
                _activeCts?.Cancel();
                _activeTrackKey = string.Empty;
                if (Current.Status != LyricsStatus.Idle)
                {
                    Current = new LyricsSnapshot { Status = LyricsStatus.Idle };
                    LyricsUpdated?.Invoke();
                }
                return;
            }

            string cleanArtist = CleanArtistName(artist);
            string cleanTitle = CleanTrackTitle(title);
            string key = $"{cleanArtist}||{cleanTitle}";

            if (!forceRefresh && string.Equals(_activeTrackKey, key, StringComparison.OrdinalIgnoreCase))
                return;

            _activeTrackKey = key;
            _activeCts?.Cancel();

            if (!forceRefresh && _cache.TryGetValue(key, out var cached))
            {
                Current = cached;
                LyricsUpdated?.Invoke();
                return;
            }

            var cts = new CancellationTokenSource();
            _activeCts = cts;

            Current = new LyricsSnapshot { TrackKey = key, Status = LyricsStatus.Loading };
            LyricsUpdated?.Invoke();

            _ = FetchAndApplyAsync(key, cleanTitle, cleanArtist, album, duration, cts.Token);
        }

        public (string? Prev, string Current, string? Next, string? Next2, double LineProgress, int ActiveIndex) GetSyncedWindow(
            TimeSpan position,
            TimeSpan trackDuration)
        {
            var lines = Current.SyncedLines;
            if (lines.Count == 0)
                return (null, string.Empty, null, null, 0.0, -1);

            var adjusted = position + TimeSpan.FromMilliseconds(80);
            int idx = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Time <= adjusted)
                    idx = i;
                else
                    break;
            }

            if (idx < 0)
            {
                string first = lines[0].Text;
                string? second = lines.Count > 1 ? lines[1].Text : null;
                return (null, "♪  ...", first, second, 1.0, -1);
            }

            string? prev = idx > 0 ? lines[idx - 1].Text : null;
            string curr = string.IsNullOrWhiteSpace(lines[idx].Text) ? "♪" : lines[idx].Text;
            string? next = idx + 1 < lines.Count ? lines[idx + 1].Text : null;
            string? next2 = idx + 2 < lines.Count ? lines[idx + 2].Text : null;

            TimeSpan start = lines[idx].Time;
            TimeSpan end = idx + 1 < lines.Count
                ? lines[idx + 1].Time
                : (trackDuration > start ? trackDuration : start + TimeSpan.FromSeconds(5));

            double maxEstimatedSec = Math.Clamp(curr.Length * 0.14, 3.8, 9.0);
            double spanSec = Math.Clamp((end - start).TotalSeconds, 0.4, maxEstimatedSec);
            double progress = Math.Clamp((adjusted - start).TotalSeconds / spanSec, 0.0, 1.0);

            return (prev, curr, next, next2, progress, idx);
        }

        private async Task FetchAndApplyAsync(
            string key,
            string title,
            string artist,
            string album,
            TimeSpan duration,
            CancellationToken ct)
        {
            LyricsSnapshot result;
            try
            {
                result = await QueryLrclibAsync(key, title, artist, album, duration, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                result = new LyricsSnapshot
                {
                    TrackKey = key,
                    Status = LyricsStatus.Error,
                    ErrorMessage = "Verbindungsfehler"
                };
            }

            if (ct.IsCancellationRequested || !string.Equals(_activeTrackKey, key, StringComparison.OrdinalIgnoreCase))
                return;

            if (result.Status is LyricsStatus.Synced or LyricsStatus.Plain or LyricsStatus.Instrumental or LyricsStatus.NotFound)
                PutInCache(key, result);

            Current = result;
            LyricsUpdated?.Invoke();
        }

        private async Task<LyricsSnapshot> QueryLrclibAsync(
            string key,
            string title,
            string artist,
            string album,
            TimeSpan duration,
            CancellationToken ct)
        {
            bool hasArtist = !string.IsNullOrWhiteSpace(artist) && artist != "Unbekannter Interpret";

            if (hasArtist)
            {
                string url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
                if (!string.IsNullOrWhiteSpace(album))
                    url += $"&album_name={Uri.EscapeDataString(album)}";
                if (duration.TotalSeconds is > 10 and < 3600)
                    url += $"&duration={(int)Math.Round(duration.TotalSeconds)}";

                var snap = await TryParseGetAsync(url, key, ct);
                if (snap != null) return snap;

                // Zweiter Versuch ohne Album/Dauer für breitere Trefferquote
                if (!string.IsNullOrWhiteSpace(album) || duration.TotalSeconds > 10)
                {
                    string simpleUrl = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
                    snap = await TryParseGetAsync(simpleUrl, key, ct);
                    if (snap != null) return snap;
                }
            }

            // Fallback: LRCLIB-Suche
            string searchUrl = hasArtist
                ? $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}"
                : $"https://lrclib.net/api/search?q={Uri.EscapeDataString(title)}";

            using var resp = await Http.GetAsync(searchUrl, ct);
            if (resp.IsSuccessStatusCode)
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    JsonElement? best = null;
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        if (el.TryGetProperty("syncedLyrics", out var sl) && !string.IsNullOrWhiteSpace(sl.GetString()))
                        {
                            best = el;
                            break;
                        }
                        best ??= el;
                    }

                    if (best.HasValue)
                        return ParseLyricsJson(best.Value, key);
                }
            }

            return new LyricsSnapshot { TrackKey = key, Status = LyricsStatus.NotFound };
        }

        private static async Task<LyricsSnapshot?> TryParseGetAsync(string url, string key, CancellationToken ct)
        {
            using var resp = await Http.GetAsync(url, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return ParseLyricsJson(doc.RootElement, key);
        }

        private static LyricsSnapshot ParseLyricsJson(JsonElement root, string key)
        {
            bool instrumental = root.TryGetProperty("instrumental", out var instProp) &&
                                instProp.ValueKind == JsonValueKind.True;
            if (instrumental)
                return new LyricsSnapshot { TrackKey = key, Status = LyricsStatus.Instrumental };

            string? syncedRaw = root.TryGetProperty("syncedLyrics", out var sProp) ? sProp.GetString() : null;
            if (!string.IsNullOrWhiteSpace(syncedRaw))
            {
                var parsed = ParseLrc(syncedRaw);
                if (parsed.Count > 0)
                {
                    return new LyricsSnapshot
                    {
                        TrackKey = key,
                        Status = LyricsStatus.Synced,
                        SyncedLines = parsed
                    };
                }
            }

            string? plainRaw = root.TryGetProperty("plainLyrics", out var pProp) ? pProp.GetString() : null;
            if (!string.IsNullOrWhiteSpace(plainRaw))
            {
                var lines = plainRaw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();
                if (lines.Count > 0)
                {
                    return new LyricsSnapshot
                    {
                        TrackKey = key,
                        Status = LyricsStatus.Plain,
                        PlainLines = lines
                    };
                }
            }

            return new LyricsSnapshot { TrackKey = key, Status = LyricsStatus.NotFound };
        }

        private static List<LyricLine> ParseLrc(string raw)
        {
            var result = new List<LyricLine>();
            var rawLines = raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawLine in rawLines)
            {
                var matches = LrcTimestampRegex.Matches(rawLine);
                if (matches.Count == 0) continue;

                string text = LrcTimestampRegex.Replace(rawLine, string.Empty).Trim();
                foreach (Match m in matches)
                {
                    if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mins))
                        continue;
                    if (!int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int secs))
                        continue;

                    int ms = 0;
                    if (m.Groups[3].Success)
                    {
                        string frac = m.Groups[3].Value;
                        if (int.TryParse(frac, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
                        {
                            ms = frac.Length switch
                            {
                                1 => val * 100,
                                2 => val * 10,
                                _ => val
                            };
                        }
                    }

                    var time = TimeSpan.FromMinutes(mins) + TimeSpan.FromSeconds(secs) + TimeSpan.FromMilliseconds(ms);
                    result.Add(new LyricLine(time, string.IsNullOrWhiteSpace(text) ? "♪" : text));
                }
            }

            result.Sort((a, b) => a.Time.CompareTo(b.Time));
            return result;
        }

        private void PutInCache(string key, LyricsSnapshot snapshot)
        {
            if (_cache.ContainsKey(key))
            {
                _cache[key] = snapshot;
                return;
            }

            if (_cacheOrder.Count >= 30 && _cacheOrder.First is { } oldest)
            {
                _cache.Remove(oldest.Value);
                _cacheOrder.RemoveFirst();
            }

            _cache[key] = snapshot;
            _cacheOrder.AddLast(key);
        }

        private static string CleanArtistName(string artist)
        {
            string s = (artist ?? string.Empty).Trim();
            if (s.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase))
                s = s[..^8].Trim();
            int comma = s.IndexOf(',');
            if (comma > 0)
                s = s[..comma].Trim();
            return s;
        }

        private static string CleanTrackTitle(string title)
        {
            string s = SuffixNoiseRegex.Replace(title ?? string.Empty, string.Empty).Trim();
            return string.IsNullOrWhiteSpace(s) ? (title ?? string.Empty).Trim() : s;
        }
    }
}
