using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.Finance
{
    public sealed class FinanceQuote
    {
        public string Symbol { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Currency { get; set; } = "USD";
        public double Price { get; set; }
        public double PreviousClose { get; set; }
        public bool IsCrypto { get; set; }
        public bool IsLiveStream { get; set; }
        public bool IsStale { get; set; }
        public DateTime LastUpdatedUtc { get; set; }
        public Dictionary<string, List<double>> Sparklines { get; set; } = new(StringComparer.Ordinal);

        public double ChangePercent =>
            PreviousClose > 0 ? ((Price - PreviousClose) / PreviousClose) * 100.0 : 0.0;

        /// <summary>
        /// Berechnet die prozentuale Kursänderung passend zum gewählten Zeitraum (1D, 1W, 1M, 1Y, 5Y, ALL, LIVE).
        /// </summary>
        public double GetChangePercent(string range)
        {
            if (string.Equals(range, "1D", StringComparison.OrdinalIgnoreCase) && PreviousClose > 0 && Price > 0)
                return ChangePercent;

            lock (this)
            {
                if (Sparklines.TryGetValue(range, out var sp) && sp.Count >= 2 && sp[0] > 0)
                {
                    double latest = Price > 0 ? Price : sp[^1];
                    return ((latest - sp[0]) / sp[0]) * 100.0;
                }
            }

            return string.Equals(range, "LIVE", StringComparison.OrdinalIgnoreCase) ? 0.0 : ChangePercent;
        }
    }

    public sealed record FinanceSearchResult(string Symbol, string Name, string QuoteType, string Exchange);

    /// <summary>
    /// Zero-Config Live-Datenservice für Aktien (Yahoo Finance Polling + Cache)
    /// und Krypto (Coinbase/Binance WebSocket mit automatischem Reconnect).
    /// </summary>
    public sealed class FinanceService
    {
        private static readonly Lazy<FinanceService> _instance = new(() => new FinanceService());
        public static FinanceService Instance => _instance.Value;

        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        private static readonly string CachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsNotch",
            "finance_cache.json");

        private readonly ConcurrentDictionary<string, FinanceQuote> _quotes = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _lastChartFetchUtc = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _lastLiveTickUtc = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _trackingLock = new();
        private readonly object _cacheSaveLock = new();

        private int _trackingRefs;
        private CancellationTokenSource? _trackingCts;
        private CancellationTokenSource? _wsCts;
        private string _wsSubscriptionKey = string.Empty;
        private DateTime _lastCacheSaveUtc = DateTime.MinValue;

        public event Action<string>? QuoteUpdated;
        public event Action? FavoritesChanged;
        public event Action? RangeChanged;

        private FinanceService()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            LoadCacheFromDisk();
        }

        public static bool IsCryptoSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return false;
            string s = symbol.Trim().ToUpperInvariant();
            return s.EndsWith("-USD", StringComparison.Ordinal) ||
                   s.EndsWith("-EUR", StringComparison.Ordinal) ||
                   s.EndsWith("-GBP", StringComparison.Ordinal) ||
                   s.EndsWith("-USDT", StringComparison.Ordinal);
        }

        public IReadOnlyList<string> GetFavorites()
        {
            var favs = SettingsService.Current.FinanceFavorites;
            return favs is { Count: > 0 } ? favs : Array.Empty<string>();
        }

        public string? GetActiveSymbol()
        {
            var favs = GetFavorites();
            if (favs.Count == 0) return null;
            int idx = ((SettingsService.Current.FinanceActiveIndex % favs.Count) + favs.Count) % favs.Count;
            return favs[idx];
        }

        public FinanceQuote? GetCachedQuote(string? symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return null;
            return _quotes.TryGetValue(symbol.Trim(), out var q) ? q : null;
        }

        public bool IsFavorite(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return false;
            return GetFavorites().Any(f => string.Equals(f, symbol.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public void CycleFavorite(int delta = 1)
        {
            var favs = SettingsService.Current.FinanceFavorites;
            if (favs == null || favs.Count <= 1) return;

            SettingsService.UpdateQuiet(s =>
            {
                s.FinanceActiveIndex = ((s.FinanceActiveIndex + delta) % favs.Count + favs.Count) % favs.Count;
            });

            FavoritesChanged?.Invoke();
            string? active = GetActiveSymbol();
            if (!string.IsNullOrEmpty(active))
                _ = EnsureSymbolAsync(active, SettingsService.Current.FinanceRange, force: false);
        }

        public bool ToggleFavorite(string symbol, string? displayName = null)
        {
            string norm = (symbol ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(norm)) return false;

            var favs = SettingsService.Current.FinanceFavorites ??= new List<string>();
            int existingIdx = favs.FindIndex(f => string.Equals(f, norm, StringComparison.OrdinalIgnoreCase));
            bool added;
            if (existingIdx >= 0)
            {
                favs.RemoveAt(existingIdx);
                added = false;
                if (favs.Count == 0)
                    SettingsService.Current.FinanceActiveIndex = 0;
                else if (SettingsService.Current.FinanceActiveIndex >= favs.Count)
                    SettingsService.Current.FinanceActiveIndex = favs.Count - 1;
            }
            else
            {
                favs.Add(norm);
                SettingsService.Current.FinanceActiveIndex = favs.Count - 1;
                added = true;
                if (!string.IsNullOrWhiteSpace(displayName) && !_quotes.ContainsKey(norm))
                {
                    _quotes[norm] = new FinanceQuote
                    {
                        Symbol = norm,
                        Name = displayName.Trim(),
                        IsCrypto = IsCryptoSymbol(norm)
                    };
                }
            }

            SettingsService.UpdateQuiet(_ => { });
            FavoritesChanged?.Invoke();
            RestartWebSocketIfNeeded();

            if (added)
                _ = EnsureSymbolAsync(norm, SettingsService.Current.FinanceRange, force: true);

            return added;
        }

        public static readonly string[] SupportedRanges = ["LIVE", "1D", "1W", "1M", "1Y", "5Y", "ALL"];

        public static bool IsSupportedRange(string? range) =>
            range is "LIVE" or "1D" or "1W" or "1M" or "1Y" or "5Y" or "ALL";

        public static string GetNextRange(string? current)
        {
            int idx = Array.IndexOf(SupportedRanges, current);
            return SupportedRanges[(idx < 0 ? 0 : idx + 1) % SupportedRanges.Length];
        }

        public void CycleRange() => SetRange(GetNextRange(SettingsService.Current.FinanceRange));

        public void SetRange(string range)
        {
            if (!IsSupportedRange(range)) return;
            if (SettingsService.Current.FinanceRange == range) return;

            SettingsService.UpdateQuiet(s => s.FinanceRange = range);
            RangeChanged?.Invoke();

            string? active = GetActiveSymbol();
            if (!string.IsNullOrEmpty(active))
                _ = EnsureSymbolAsync(active, range, force: false);
        }

        public void StartTracking()
        {
            lock (_trackingLock)
            {
                _trackingRefs++;
                if (_trackingRefs > 1) return;

                _trackingCts = new CancellationTokenSource();
                var ct = _trackingCts.Token;
                _ = RunPollingLoopAsync(ct);
                RestartWebSocketIfNeeded();
            }
        }

        public void StopTracking()
        {
            lock (_trackingLock)
            {
                if (_trackingRefs <= 0) return;
                _trackingRefs--;
                if (_trackingRefs > 0) return;

                _trackingCts?.Cancel();
                _trackingCts?.Dispose();
                _trackingCts = null;

                _wsCts?.Cancel();
                _wsCts?.Dispose();
                _wsCts = null;
                _wsSubscriptionKey = string.Empty;
            }
        }

        private async Task RunPollingLoopAsync(CancellationToken ct)
        {
            try
            {
                string? active = GetActiveSymbol();
                if (!string.IsNullOrEmpty(active))
                    await EnsureSymbolAsync(active, SettingsService.Current.FinanceRange, force: true, ct);

                while (!ct.IsCancellationRequested)
                {
                    int delaySec = SettingsService.Current.FinanceRange == "LIVE" ? 6 : 20;
                    await Task.Delay(TimeSpan.FromSeconds(delaySec), ct);
                    if (ct.IsCancellationRequested) break;

                    string? current = GetActiveSymbol();
                    if (!string.IsNullOrEmpty(current))
                    {
                        await EnsureSymbolAsync(current, SettingsService.Current.FinanceRange, force: true, ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Reguläres Beenden
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        public async Task EnsureSymbolAsync(string symbol, string range, bool force = false, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return;
            string norm = symbol.Trim().ToUpperInvariant();
            bool isLiveMode = string.Equals(range, "LIVE", StringComparison.Ordinal);
            string normRange = range is "1W" or "1M" or "1Y" or "5Y" or "ALL" ? range : "1D";
            string fetchKey = $"{norm}|{normRange}";

            int minAgeSec = isLiveMode ? 5 : 15;
            if (!force &&
                _lastChartFetchUtc.TryGetValue(fetchKey, out var lastFetch) &&
                DateTime.UtcNow - lastFetch < TimeSpan.FromSeconds(minAgeSec) &&
                _quotes.TryGetValue(norm, out var existing) &&
                existing.Sparklines.ContainsKey(normRange))
            {
                QuoteUpdated?.Invoke(norm);
                return;
            }

            try
            {
                var (yahooRange, yahooInterval, includePrePost, windowSeconds) = normRange switch
                {
                    "1W" => ("1mo", "30m", false, 7L * 86400L),
                    "1M" => ("1mo", "1d", false, 0L),
                    "1Y" => ("1y", "1d", false, 0L),
                    "5Y" => ("5y", "1wk", false, 0L),
                    "ALL" => ("max", "1mo", false, 0L),
                    _ => ("5d", "5m", true, 86400L) // 1D: volle letzte 24h (inkl. Pre/Post-Market & Krypto 24/7)
                };

                string prePostParam = includePrePost ? "true" : "false";
                string url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(norm)}?range={yahooRange}&interval={yahooInterval}&includePrePost={prePostParam}";
                using var resp = await Http.GetAsync(url, ct);
                resp.EnsureSuccessStatusCode();

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (!doc.RootElement.TryGetProperty("chart", out var chart) ||
                    !chart.TryGetProperty("result", out var results) ||
                    results.ValueKind != JsonValueKind.Array ||
                    results.GetArrayLength() == 0)
                {
                    return;
                }

                var result = results[0];
                if (!result.TryGetProperty("meta", out var meta)) return;

                double price = TryGetDouble(meta, "regularMarketPrice");
                double prevClose = TryGetDouble(meta, "previousClose");
                if (prevClose <= 0)
                    prevClose = TryGetDouble(meta, "regularMarketPreviousClose");
                if (prevClose <= 0 && normRange == "1D" && yahooRange == "1d")
                    prevClose = TryGetDouble(meta, "chartPreviousClose");

                string currency = meta.TryGetProperty("currency", out var curEl) && curEl.ValueKind == JsonValueKind.String
                    ? curEl.GetString() ?? "USD"
                    : "USD";

                string name = string.Empty;
                if (meta.TryGetProperty("shortName", out var snEl) && snEl.ValueKind == JsonValueKind.String)
                    name = snEl.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name) && meta.TryGetProperty("longName", out var lnEl) && lnEl.ValueKind == JsonValueKind.String)
                    name = lnEl.GetString() ?? string.Empty;

                string instType = meta.TryGetProperty("instrumentType", out var itEl) && itEl.ValueKind == JsonValueKind.String
                    ? itEl.GetString() ?? string.Empty
                    : string.Empty;
                bool isCrypto = IsCryptoSymbol(norm) || string.Equals(instType, "CRYPTOCURRENCY", StringComparison.OrdinalIgnoreCase);

                var spark = new List<double>();
                var timedPoints = new List<(long Ts, double Close)>();
                if (result.TryGetProperty("indicators", out var ind) &&
                    ind.TryGetProperty("quote", out var quotesArr) &&
                    quotesArr.ValueKind == JsonValueKind.Array &&
                    quotesArr.GetArrayLength() > 0 &&
                    quotesArr[0].TryGetProperty("close", out var closeArr) &&
                    closeArr.ValueKind == JsonValueKind.Array)
                {
                    bool hasTimestamps = result.TryGetProperty("timestamp", out var tsArr) &&
                                         tsArr.ValueKind == JsonValueKind.Array;
                    int idx = 0;
                    using var tsEnum = hasTimestamps ? tsArr.EnumerateArray() : default;
                    foreach (var pt in closeArr.EnumerateArray())
                    {
                        long ts = 0;
                        if (hasTimestamps && tsEnum.MoveNext() && tsEnum.Current.ValueKind == JsonValueKind.Number)
                            tsEnum.Current.TryGetInt64(out ts);

                        if (pt.ValueKind == JsonValueKind.Number && pt.TryGetDouble(out double v) && !double.IsNaN(v))
                        {
                            spark.Add(v);
                            if (ts > 0) timedPoints.Add((ts, v));
                        }
                        idx++;
                    }
                }

                if (windowSeconds > 0 && timedPoints.Count >= 2)
                {
                    long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    long cutoff = nowUnix - windowSeconds;
                    var windowed = timedPoints.Where(p => p.Ts >= cutoff).Select(p => p.Close).ToList();
                    if (windowed.Count < 2)
                    {
                        // Wochenende / Feiertag: letzte verfügbare 24h-/7T-Handelsspanne relativ zum letzten Punkt
                        long latestCutoff = timedPoints[^1].Ts - windowSeconds;
                        windowed = timedPoints.Where(p => p.Ts >= latestCutoff).Select(p => p.Close).ToList();
                    }
                    if (windowed.Count >= 2)
                        spark = windowed;
                }

                var quote = _quotes.GetOrAdd(norm, _ => new FinanceQuote { Symbol = norm });
                lock (quote)
                {
                    if (!string.IsNullOrWhiteSpace(name)) quote.Name = name;
                    if (!string.IsNullOrWhiteSpace(currency)) quote.Currency = currency;
                    quote.IsCrypto = isCrypto;
                    // Wenn ein WebSocket-Stream aktiv ist, diesen aktuelleren Preis nicht mit älterem REST-Wert überschreiben
                    if (!quote.IsLiveStream || quote.Price <= 0)
                        quote.Price = price;
                    if (prevClose > 0)
                        quote.PreviousClose = prevClose;
                    if (spark.Count > 1)
                        quote.Sparklines[normRange] = spark;
                    if (isLiveMode && price > 0)
                        AppendLiveTick(quote, price, minIntervalMs: 0);
                    quote.IsStale = false;
                    quote.LastUpdatedUtc = DateTime.UtcNow;
                }

                _lastChartFetchUtc[fetchKey] = DateTime.UtcNow;
                SaveCacheToDiskThrottled();
                QuoteUpdated?.Invoke(norm);
            }
            catch (OperationCanceledException)
            {
                // Ignorieren
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                if (_quotes.TryGetValue(norm, out var cached))
                {
                    cached.IsStale = true;
                    QuoteUpdated?.Invoke(norm);
                }
            }
        }

        public async Task<IReadOnlyList<FinanceSearchResult>> SearchAsync(string query, CancellationToken ct = default)
        {
            string q = (query ?? string.Empty).Trim();
            if (q.Length < 1) return Array.Empty<FinanceSearchResult>();

            string url = $"https://query2.finance.yahoo.com/v1/finance/search?q={Uri.EscapeDataString(q)}&quotesCount=8&newsCount=0&enableFuzzyQuery=false";
            using var resp = await Http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var list = new List<FinanceSearchResult>();
            if (doc.RootElement.TryGetProperty("quotes", out var quotes) && quotes.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in quotes.EnumerateArray())
                {
                    string sym = item.TryGetProperty("symbol", out var sEl) ? sEl.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(sym)) continue;

                    string name = item.TryGetProperty("shortname", out var nEl) ? nEl.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(name) && item.TryGetProperty("longname", out var lEl))
                        name = lEl.GetString() ?? string.Empty;

                    string type = item.TryGetProperty("quoteType", out var tEl) ? tEl.GetString() ?? string.Empty : string.Empty;
                    string exch = item.TryGetProperty("exchDisp", out var eEl) ? eEl.GetString() ?? string.Empty : string.Empty;

                    if (type is not ("EQUITY" or "CRYPTOCURRENCY" or "ETF" or "INDEX" or ""))
                        continue;

                    list.Add(new FinanceSearchResult(sym.ToUpperInvariant(), string.IsNullOrWhiteSpace(name) ? sym : name, type, exch));
                }
            }
            return list;
        }

        private void RestartWebSocketIfNeeded()
        {
            lock (_trackingLock)
            {
                if (_trackingRefs <= 0) return;

                var cryptoSymbols = GetFavorites()
                    .Where(IsCryptoSymbol)
                    .Select(s => s.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToList();

                string nextKey = string.Join(",", cryptoSymbols);
                if (nextKey == _wsSubscriptionKey && _wsCts != null && !_wsCts.IsCancellationRequested)
                    return;

                _wsCts?.Cancel();
                _wsCts?.Dispose();
                _wsCts = null;
                _wsSubscriptionKey = nextKey;

                if (cryptoSymbols.Count == 0) return;

                _wsCts = new CancellationTokenSource();
                var ct = _wsCts.Token;
                _ = RunCryptoWebSocketLoopAsync(cryptoSymbols, ct);
            }
        }

        private async Task RunCryptoWebSocketLoopAsync(List<string> symbols, CancellationToken ct)
        {
            int backoffSec = 2;
            bool preferCoinbase = true;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (preferCoinbase)
                        await ConnectCoinbaseAsync(symbols, ct);
                    else
                        await ConnectBinanceAsync(symbols, ct);

                    backoffSec = 2;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                    preferCoinbase = !preferCoinbase; // Beim nächsten Versuch alternativen Stream nutzen
                }

                if (ct.IsCancellationRequested) break;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(backoffSec), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                backoffSec = Math.Min(30, backoffSec * 2);
            }
        }

        private async Task ConnectCoinbaseAsync(List<string> symbols, CancellationToken ct)
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri("wss://ws-feed.exchange.coinbase.com"), ct);

            var payload = JsonSerializer.Serialize(new
            {
                type = "subscribe",
                product_ids = symbols,
                channels = new[] { "ticker" }
            });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

            var buffer = new byte[8192];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                string? msg = await ReceiveTextMessageAsync(ws, buffer, ct);
                if (msg == null) break;

                using var doc = JsonDocument.Parse(msg);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var tEl) || tEl.GetString() != "ticker") continue;

                string? productId = root.TryGetProperty("product_id", out var pEl) ? pEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(productId)) continue;

                double price = ParseInvariantDouble(root, "price");
                double open24h = ParseInvariantDouble(root, "open_24h");
                if (price <= 0) continue;

                UpdateCryptoPrice(productId, price, open24h);
            }
        }

        private async Task ConnectBinanceAsync(List<string> symbols, CancellationToken ct)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var streams = new List<string>();
            foreach (var sym in symbols)
            {
                string? pair = MapYahooToBinanceStream(sym);
                if (pair == null) continue;
                map[pair] = sym;
                streams.Add($"{pair}@miniTicker");
            }
            if (streams.Count == 0) return;

            string uri = $"wss://stream.binance.com:9443/stream?streams={string.Join("/", streams)}";
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(uri), ct);

            var buffer = new byte[8192];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                string? msg = await ReceiveTextMessageAsync(ws, buffer, ct);
                if (msg == null) break;

                using var doc = JsonDocument.Parse(msg);
                if (!doc.RootElement.TryGetProperty("data", out var data)) continue;

                string? s = data.TryGetProperty("s", out var sEl) ? sEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(s) || !map.TryGetValue(s, out var yahooSym)) continue;

                double close = ParseInvariantDouble(data, "c");
                double open = ParseInvariantDouble(data, "o");
                if (close <= 0) continue;

                UpdateCryptoPrice(yahooSym, close, open);
            }
        }

        private static string? MapYahooToBinanceStream(string yahooSymbol)
        {
            string s = yahooSymbol.Trim().ToLowerInvariant();
            if (s.EndsWith("-usd", StringComparison.Ordinal))
                return s[..^4] + "usdt";
            if (s.EndsWith("-eur", StringComparison.Ordinal))
                return s[..^4] + "eur";
            return null;
        }

        private void UpdateCryptoPrice(string symbol, double price, double open24h)
        {
            string norm = symbol.Trim().ToUpperInvariant();
            var quote = _quotes.GetOrAdd(norm, _ => new FinanceQuote
            {
                Symbol = norm,
                Name = norm,
                Currency = norm.EndsWith("-EUR", StringComparison.Ordinal) ? "EUR" : "USD",
                IsCrypto = true
            });

            lock (quote)
            {
                quote.Price = price;
                if (open24h > 0)
                    quote.PreviousClose = open24h;
                quote.IsCrypto = true;
                quote.IsLiveStream = true;
                quote.IsStale = false;
                quote.LastUpdatedUtc = DateTime.UtcNow;
                AppendLiveTick(quote, price, minIntervalMs: 600);
            }

            QuoteUpdated?.Invoke(norm);
        }

        private void AppendLiveTick(FinanceQuote quote, double price, int minIntervalMs)
        {
            if (price <= 0) return;
            var now = DateTime.UtcNow;
            if (!quote.Sparklines.TryGetValue("LIVE", out var live))
            {
                live = new List<double> { price, price };
                quote.Sparklines["LIVE"] = live;
                _lastLiveTickUtc[quote.Symbol] = now;
                return;
            }

            if (minIntervalMs > 0 &&
                _lastLiveTickUtc.TryGetValue(quote.Symbol, out var last) &&
                (now - last).TotalMilliseconds < minIntervalMs)
            {
                // Letzten Punkt flüssig aktualisieren, ohne die Zeitachse zu stauchen
                if (live.Count > 0) live[^1] = price;
                return;
            }

            _lastLiveTickUtc[quote.Symbol] = now;
            live.Add(price);
            if (live.Count > 60)
                live.RemoveAt(0);
        }

        private static async Task<string?> ReceiveTextMessageAsync(ClientWebSocket ws, byte[] buffer, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) break;
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static double TryGetDouble(JsonElement el, string prop)
        {
            if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number && val.TryGetDouble(out double d))
                return d;
            return 0;
        }

        private static double ParseInvariantDouble(JsonElement el, string prop)
        {
            if (!el.TryGetProperty(prop, out var val)) return 0;
            if (val.ValueKind == JsonValueKind.Number && val.TryGetDouble(out double d)) return d;
            if (val.ValueKind == JsonValueKind.String &&
                double.TryParse(val.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                return parsed;
            return 0;
        }

        public static string FormatPrice(double price, string? currency)
        {
            if (price <= 0) return "—";
            string cur = (currency ?? "USD").Trim().ToUpperInvariant();
            int decimals = price >= 1000 ? 2 : (price >= 1 ? 2 : 4);
            string num = price.ToString($"N{decimals}", CultureInfo.CurrentUICulture);

            return cur switch
            {
                "USD" => $"${num}",
                "EUR" => $"€{num}",
                "GBP" => $"£{num}",
                "JPY" => $"¥{price.ToString("N0", CultureInfo.CurrentUICulture)}",
                "CHF" => $"CHF {num}",
                _ => $"{num} {cur}"
            };
        }

        public static string FormatChangePercent(double changePct)
        {
            string sign = changePct >= 0 ? "+" : string.Empty;
            return $"{sign}{changePct.ToString("F2", CultureInfo.CurrentUICulture)}%";
        }

        private void LoadCacheFromDisk()
        {
            try
            {
                if (!File.Exists(CachePath)) return;
                string json = File.ReadAllText(CachePath);
                var items = JsonSerializer.Deserialize<List<FinanceQuote>>(json);
                if (items == null) return;

                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item.Symbol)) continue;
                    item.IsLiveStream = false;
                    item.IsStale = true;
                    item.Sparklines ??= new Dictionary<string, List<double>>(StringComparer.Ordinal);
                    item.Sparklines.Remove("LIVE");
                    _quotes[item.Symbol] = item;
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private void SaveCacheToDiskThrottled()
        {
            lock (_cacheSaveLock)
            {
                if (DateTime.UtcNow - _lastCacheSaveUtc < TimeSpan.FromSeconds(15))
                    return;
                _lastCacheSaveUtc = DateTime.UtcNow;
            }

            try
            {
                string? dir = Path.GetDirectoryName(CachePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var snapshot = _quotes.Values.Take(24).ToList();
                string json = JsonSerializer.Serialize(snapshot);
                File.WriteAllText(CachePath, json);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }
    }
}
