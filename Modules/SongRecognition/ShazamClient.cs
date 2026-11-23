using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsNotch.Modules.SongRecognition
{
    internal sealed record ShazamMatch(
        string Title,
        string Artist,
        string Album,
        string CoverUrl,
        string ShazamUrl,
        string SpotifyUrl,
        string AppleMusicUrl);

    /// <summary>
    /// Erzeugt Shazam-kompatible Spektral-Signaturen (16 kHz Mono FFT-Landmarks nach shazamio-core/SongRec)
    /// und fragt Shazams Discovery-Endpoint ohne Benutzer-API-Key ab.
    /// </summary>
    internal static class ShazamClient
    {
        private const int SampleRate = 16000;
        private const int FrameLength = 2048;
        private const int HopLength = 128;
        private const int BinCount = FrameLength / 2 + 1; // 1025
        private const int HistoryLength = 256;
        private const int PeakLag = 45;
        private const int ReferenceLag = 48;
        private const int FirstPeakBin = 10;
        private const int LastPeakBin = 1014;
        private const double MinPeakPower = 1.0 / 64.0;

        private static readonly int[] FrequencyNeighbourDistances = { 2, 3, 6, 9 };
        private static readonly int[] TimeNeighbourDistances = { 4, 7, 14, 21, 28, 35, 42 };
        private static readonly int[] SpreadBackLags = { 1, 3, 6 };

        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        private static readonly double[] HannWindow = BuildHannWindow(FrameLength);

        private readonly record struct Peak(uint Frame, ushort Magnitude, ushort RefinedBin);

        private sealed class Frame
        {
            public readonly double[] Power = new double[BinCount];
            public readonly double[] Spread = new double[BinCount];
        }

        private sealed class History
        {
            private readonly Frame[] _frames;
            public int Count { get; private set; }

            public History()
            {
                _frames = new Frame[HistoryLength];
                for (int i = 0; i < HistoryLength; i++)
                    _frames[i] = new Frame();
            }

            public int Slot(int lag) => (Count + HistoryLength - 1 - lag) % HistoryLength;
            public Frame Ago(int lag) => _frames[Slot(lag)];

            public void Advance(double[] power, double[] spread)
            {
                Count++;
                for (int i = 0; i < SpreadBackLags.Length; i++)
                {
                    var earlier = Ago(SpreadBackLags[i]).Spread;
                    for (int b = 0; b < BinCount; b++)
                    {
                        if (spread[b] > earlier[b])
                            earlier[b] = spread[b];
                    }
                }

                var current = Ago(0);
                Array.Copy(power, current.Power, BinCount);
                Array.Copy(spread, current.Spread, BinCount);
            }
        }

        public static async Task<ShazamMatch?> RecognizeAsync(short[] mono16kSamples, CancellationToken ct)
        {
            if (mono16kSamples.Length < SampleRate * 2)
                return null;

            byte[] signature = BuildSignature(mono16kSamples);
            if (signature.Length == 0)
                return null;

            string dataUri = "data:audio/vnd.shazam.sig;base64," + Convert.ToBase64String(signature);
            int sampleMs = (int)(mono16kSamples.Length * 1000.0 / SampleRate);

            bool isGerman = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase);
            string lang = isGerman ? "de-DE" : "en-US";
            string country = isGerman ? "DE" : "GB";
            string uuid1 = Guid.NewGuid().ToString("D").ToUpperInvariant();
            string uuid2 = Guid.NewGuid().ToString("D").ToUpperInvariant();

            string url = $"https://amp.shazam.com/discovery/v5/{lang}/{country}/iphone/-/tag/{uuid1}/{uuid2}" +
                         "?sync=true&webv3=true&sampling=true&connected=&shazamapiversion=v3&sharehub=true&hubv5minorversion=v5.1&hidelb=true&video=v3";

            string ianaTimezone = TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out string? iana)
                ? iana
                : "Europe/Berlin";

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var payload = new
            {
                geolocation = new { altitude = 300.0, latitude = 45.0, longitude = 2.0 },
                signature = new
                {
                    samplems = sampleMs,
                    timestamp = nowMs,
                    uri = dataUri
                },
                timestamp = nowMs,
                timezone = ianaTimezone
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("X-Shazam-Platform", "IPHONE");
            req.Headers.TryAddWithoutValidation("X-Shazam-AppVersion", "14.1.0");
            req.Headers.TryAddWithoutValidation("Accept", "*/*");
            req.Headers.TryAddWithoutValidation("Accept-Language", lang);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 14_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148");

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                App.LogError(new HttpRequestException($"Shazam discovery HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})"));
                return null;
            }

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseResponse(json);
        }

        internal static ShazamMatch? ParseResponse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("track", out var track))
                return null;

            string title = track.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? string.Empty : string.Empty;
            string artist = track.TryGetProperty("subtitle", out var aEl) ? aEl.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(title))
                return null;

            string shazamUrl = track.TryGetProperty("url", out var uEl) ? uEl.GetString() ?? string.Empty : string.Empty;

            string coverUrl = string.Empty;
            if (track.TryGetProperty("images", out var imgs) && imgs.TryGetProperty("coverart", out var cEl))
                coverUrl = cEl.GetString() ?? string.Empty;

            string album = string.Empty;
            if (track.TryGetProperty("sections", out var secs) && secs.ValueKind == JsonValueKind.Array)
            {
                foreach (var sec in secs.EnumerateArray())
                {
                    if (sec.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var m in meta.EnumerateArray())
                        {
                            string mTitle = m.TryGetProperty("title", out var mt) ? mt.GetString() ?? string.Empty : string.Empty;
                            if (mTitle.Equals("Album", StringComparison.OrdinalIgnoreCase) &&
                                m.TryGetProperty("text", out var mText))
                            {
                                album = mText.GetString() ?? string.Empty;
                                break;
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(album)) break;
                }
            }

            string query = Uri.EscapeDataString(string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}");
            string spotifyUrl = $"https://open.spotify.com/search/{query}";
            string appleUrl = $"https://music.apple.com/search?term={query}";

            if (track.TryGetProperty("hub", out var hub))
            {
                if (hub.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var act in actions.EnumerateArray())
                    {
                        if (act.TryGetProperty("uri", out var uriEl))
                        {
                            string? uri = uriEl.GetString();
                            if (!string.IsNullOrEmpty(uri) &&
                                uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                                uri.Contains("music.apple.com", StringComparison.OrdinalIgnoreCase))
                                appleUrl = uri;
                        }
                    }
                }

                if (hub.TryGetProperty("providers", out var providers) && providers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var prov in providers.EnumerateArray())
                    {
                        string? type = prov.TryGetProperty("type", out var pt) ? pt.GetString() : null;
                        if (prov.TryGetProperty("actions", out var pActs) && pActs.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var pa in pActs.EnumerateArray())
                            {
                                string? uri = pa.TryGetProperty("uri", out var pu) ? pu.GetString() : null;
                                if (string.IsNullOrEmpty(uri)) continue;

                                if (string.Equals(type, "SPOTIFY", StringComparison.OrdinalIgnoreCase) &&
                                    uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                                    spotifyUrl = uri;
                            }
                        }
                    }
                }
            }

            return new ShazamMatch(title, artist, album, coverUrl, shazamUrl, spotifyUrl, appleUrl);
        }

        internal static byte[] BuildSignature(short[] samples)
        {
            if (samples.Length < HopLength)
                return Array.Empty<byte>();

            var ring = new short[FrameLength];
            var history = new History();
            var fftBuf = new Complex[FrameLength];
            var power = new double[BinCount];
            var spread = new double[BinCount];
            var bandPeaks = new List<Peak>[4];
            for (int i = 0; i < 4; i++)
                bandPeaks[i] = new List<Peak>();

            int fullHops = samples.Length / HopLength;
            for (int hopIdx = 0; hopIdx < fullHops; hopIdx++)
            {
                // Ringbuffer um 128 Samples weiterschieben und neuen Hop einspeisen
                Array.Copy(ring, HopLength, ring, 0, FrameLength - HopLength);
                Array.Copy(samples, hopIdx * HopLength, ring, FrameLength - HopLength, HopLength);

                for (int i = 0; i < FrameLength; i++)
                    fftBuf[i] = new Complex(ring[i] * HannWindow[i], 0.0);

                FftInPlace(fftBuf);

                for (int b = 0; b < BinCount; b++)
                {
                    double re = fftBuf[b].Real;
                    double im = fftBuf[b].Imaginary;
                    power[b] = Math.Max((re * re + im * im) / 131072.0, 1e-10);
                }

                Array.Copy(power, spread, BinCount);
                for (int b = 0; b <= BinCount - 3; b++)
                    spread[b] = Math.Max(power[b], Math.Max(power[b + 1], power[b + 2]));

                history.Advance(power, spread);

                if (history.Count > PeakLag)
                    PickPeaks(history, bandPeaks);
            }

            using var bodyStream = new MemoryStream();
            using (var bodyWriter = new BinaryWriter(bodyStream))
            {
                for (int bandIdx = 0; bandIdx < 4; bandIdx++)
                {
                    var peaks = bandPeaks[bandIdx];
                    if (peaks.Count == 0) continue;

                    using var chunkStream = new MemoryStream();
                    using (var cw = new BinaryWriter(chunkStream))
                    {
                        uint prevFrame = 0;
                        foreach (var peak in peaks)
                        {
                            uint delta = peak.Frame - prevFrame;
                            if (delta >= 255)
                            {
                                cw.Write((byte)0xFF);
                                cw.Write(peak.Frame);
                                delta = 0;
                            }
                            cw.Write((byte)delta);
                            cw.Write(peak.Magnitude);
                            cw.Write(peak.RefinedBin);
                            prevFrame = peak.Frame;
                        }
                    }

                    byte[] chunkBytes = chunkStream.ToArray();
                    bodyWriter.Write(0x60030040u + (uint)bandIdx);
                    bodyWriter.Write((uint)chunkBytes.Length);
                    bodyWriter.Write(chunkBytes);
                    while (bodyStream.Length % 4 != 0)
                        bodyWriter.Write((byte)0);
                }
            }

            byte[] bandData = bodyStream.ToArray();
            if (bandData.Length == 0) return Array.Empty<byte>();

            // Beide bodyLength-Felder im Header schließen BODY_MAGIC + Länge (8 Bytes) mit ein!
            uint bodyLength = (uint)(bandData.Length + 8);
            uint paddedSampleCount = (uint)(samples.Length + SampleRate * 24 / 100);
            int totalSize = 48 + (int)bodyLength;

            byte[] result = new byte[totalSize];
            using (var ms = new MemoryStream(result))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(0xCAFE2580u);      // Header Magic
                bw.Write(0u);               // CRC32-Platzhalter
                bw.Write(bodyLength);       // Body-Länge (inkl. 8 Bytes Body-Header)
                bw.Write(0x94119C00u);      // Second Magic
                bw.Write(0u);
                bw.Write(0u);
                bw.Write(0u);
                bw.Write(3u << 27);         // 16000 Hz ID
                bw.Write(0u);
                bw.Write(0u);
                bw.Write(paddedSampleCount);
                bw.Write(0x007C0000u);      // Fixed Value

                bw.Write(0x40000000u);      // Body Magic
                bw.Write(bodyLength);       // Body-Länge (identisch zu Wort 2)
                bw.Write(bandData);
            }

            uint crc = ComputeCrc32(result, 8, result.Length - 8);
            BitConverter.TryWriteBytes(result.AsSpan(4, 4), crc);
            return result;
        }

        private static void PickPeaks(History history, List<Peak>[] bandPeaks)
        {
            uint frameIndex = (uint)(history.Count - 1 - PeakLag);
            double[] candidate = history.Ago(PeakLag).Power;
            double[] reference = history.Ago(ReferenceLag).Spread;

            for (int bin = FirstPeakBin; bin <= LastPeakBin; bin++)
            {
                double pwr = candidate[bin];
                int below = bin - 1;
                if (pwr < MinPeakPower || pwr < reference[below])
                    continue;

                bool beatsFreq = true;
                for (int i = 0; i < FrequencyNeighbourDistances.Length; i++)
                {
                    int d = FrequencyNeighbourDistances[i];
                    if (pwr <= reference[below - d] || pwr <= reference[below + d])
                    {
                        beatsFreq = false;
                        break;
                    }
                }
                if (!beatsFreq) continue;

                bool beatsTime = true;
                for (int i = 0; i < TimeNeighbourDistances.Length; i++)
                {
                    int d = TimeNeighbourDistances[i];
                    if (pwr <= history.Ago(ReferenceLag - d).Spread[below] ||
                        pwr <= history.Ago(ReferenceLag + d).Spread[below])
                    {
                        beatsTime = false;
                        break;
                    }
                }
                if (!beatsTime) continue;

                double magnitude = LogMagnitude(pwr);
                double magBelow = LogMagnitude(candidate[bin - 1]);
                double magAbove = LogMagnitude(candidate[bin + 1]);
                double curvature = magnitude * 2.0 - magBelow - magAbove;
                if (Math.Abs(curvature) < 1e-9) continue;

                double vertexOffset = (magAbove - magBelow) * 32.0 / curvature;
                int refinedBinInt = bin * 64 + (int)vertexOffset;
                if (refinedBinInt <= 0 || refinedBinInt > ushort.MaxValue) continue;

                ushort refinedBin = (ushort)refinedBinInt;
                uint freqHz = (uint)refinedBin * 125u / 1024u;

                int bandIdx = freqHz switch
                {
                    >= 250 and <= 519 => 0,
                    >= 520 and <= 1449 => 1,
                    >= 1450 and <= 3499 => 2,
                    >= 3500 and <= 5500 => 3,
                    _ => -1
                };
                if (bandIdx < 0) continue;

                ushort magU16 = (ushort)Math.Clamp((int)magnitude, 1, ushort.MaxValue);
                bandPeaks[bandIdx].Add(new Peak(frameIndex, magU16, refinedBin));
            }
        }

        private static double LogMagnitude(double power)
            => Math.Max(Math.Log(power), 1.0 / 64.0) * 1477.3 + 6144.0;

        // Entspricht numpy.hanning(N+2)[1:-1]: sin^2(pi*(i+1)/(N+1))
        private static double[] BuildHannWindow(int size)
        {
            var win = new double[size];
            double period = size + 1;
            for (int i = 0; i < size; i++)
            {
                double s = Math.Sin(Math.PI * (i + 1) / period);
                win[i] = s * s;
            }
            return win;
        }

        private static void FftInPlace(Complex[] buffer)
        {
            int n = buffer.Length;
            int bits = 0;
            while ((1 << bits) < n) bits++;

            for (int i = 0; i < n; i++)
            {
                int rev = 0;
                for (int b = 0; b < bits; b++)
                {
                    if ((i & (1 << b)) != 0)
                        rev |= 1 << (bits - 1 - b);
                }
                if (rev > i)
                    (buffer[i], buffer[rev]) = (buffer[rev], buffer[i]);
            }

            for (int size = 2; size <= n; size <<= 1)
            {
                int half = size >> 1;
                double angleStep = -2.0 * Math.PI / size;
                for (int start = 0; start < n; start += size)
                {
                    for (int k = 0; k < half; k++)
                    {
                        double angle = angleStep * k;
                        var w = new Complex(Math.Cos(angle), Math.Sin(angle));
                        var t = w * buffer[start + k + half];
                        var u = buffer[start + k];
                        buffer[start + k] = u + t;
                        buffer[start + k + half] = u - t;
                    }
                }
            }
        }

        private static uint ComputeCrc32(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = offset; i < offset + length; i++)
            {
                crc ^= data[i];
                for (int b = 0; b < 8; b++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return ~crc;
        }
    }
}
