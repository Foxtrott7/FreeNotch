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
    /// Erzeugt Shazam-kompatible Spektral-Signaturen (16 kHz Mono FFT-Landmarks)
    /// und fragt Shazams Discovery-Endpoint ohne Benutzer-API-Key ab.
    /// </summary>
    internal static class ShazamClient
    {
        private const int SampleRate = 16000;
        private const int FftSize = 2048;
        private const int HopSize = 128;

        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        private static readonly double[] HannWindow = BuildHannWindow(FftSize);

        // Frequenzbänder in FFT-Bins (16000 Hz / 2048 = 7.8125 Hz pro Bin, skaliert auf Bin*64)
        private static readonly (int MinBin, int MaxBin)[] Bands =
        {
            (32, 67),    // ~250 – 520 Hz
            (67, 186),   // ~520 – 1450 Hz
            (186, 448),  // ~1450 – 3500 Hz
            (448, 704)   // ~3500 – 5500 Hz
        };

        public static async Task<ShazamMatch?> RecognizeAsync(short[] mono16kSamples, CancellationToken ct)
        {
            if (mono16kSamples.Length < SampleRate * 2)
                return null;

            byte[] signature = BuildSignature(mono16kSamples);
            if (signature.Length == 0)
                return null;

            string dataUri = "data:audio/vnd.shazam.sig;base64," + Convert.ToBase64String(signature);
            int sampleMs = (int)(mono16kSamples.Length * 1000.0 / SampleRate);

            string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de/DE" : "en/US";
            string url = $"https://amp.shazam.com/discovery/v5/{lang}/android/-/tag/{Guid.NewGuid():D}/{Guid.NewGuid():D}" +
                         "?sync=true&webv3=true&sampling=true&connected=&shazamapiversion=v3&sharehub=true&hubv5minorversion=v5.1&hidelb=true&video=v3";

            string ianaTimezone = TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out string? iana)
                ? iana
                : "UTC";

            var payload = new
            {
                geolocation = new { altitude = 300.0, latitude = 45.0, longitude = 2.0 },
                signature = new
                {
                    samplems = sampleMs,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    uri = dataUri
                },
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                timezone = ianaTimezone
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("User-Agent", "Dalvik/2.1.0 (Linux; U; Android 13; Pixel 7 Build/TQ3A.230805.001)");
            req.Headers.TryAddWithoutValidation("Accept-Language", lang.Replace('/', '-'));

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                App.LogError(new HttpRequestException($"Shazam discovery HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})"));
                return null;
            }

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseResponse(json);
        }

        private static ShazamMatch? ParseResponse(string json)
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
                            if (!string.IsNullOrEmpty(uri) && uri.Contains("music.apple.com", StringComparison.OrdinalIgnoreCase))
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

        private static byte[] BuildSignature(short[] samples)
        {
            int numFrames = Math.Max(0, (samples.Length - FftSize) / HopSize + 1);
            if (numFrames < 16) return Array.Empty<byte>();

            var magSpectrogram = new double[numFrames][];
            var fftBuf = new Complex[FftSize];

            for (int f = 0; f < numFrames; f++)
            {
                int offset = f * HopSize;
                for (int i = 0; i < FftSize; i++)
                    fftBuf[i] = new Complex(samples[offset + i] * HannWindow[i], 0.0);

                FftInPlace(fftBuf);

                var mags = new double[FftSize / 2];
                for (int b = 0; b < mags.Length; b++)
                {
                    double re = fftBuf[b].Real;
                    double im = fftBuf[b].Imaginary;
                    // SongRec/Shazam Energie-Skalierung: (re^2 + im^2) / 2^17
                    mags[b] = (re * re + im * im) / 131072.0;
                }
                magSpectrogram[f] = mags;
            }

            var bandPeaks = new List<(int Stripe, double Bin, double Mag)>[Bands.Length];
            for (int b = 0; b < Bands.Length; b++)
                bandPeaks[b] = new List<(int, double, double)>();

            // Lokale Maxima in Zeit (±6 Frames / ±48 ms) & Frequenz (±10 Bins / ±78 Hz) — mehrere Peaks pro Band erlaubt
            const double minEnergy = 1.0 / 64.0;
            for (int t = 12; t < numFrames - 6; t++)
            {
                for (int bandIdx = 0; bandIdx < Bands.Length; bandIdx++)
                {
                    var (minBin, maxBin) = Bands[bandIdx];
                    for (int bin = minBin; bin < maxBin; bin++)
                    {
                        double val = magSpectrogram[t][bin];
                        if (val <= minEnergy) continue;

                        bool isPeak = true;
                        for (int dt = -6; dt <= 6 && isPeak; dt++)
                        {
                            double[] row = magSpectrogram[t + dt];
                            int bStart = Math.Max(0, bin - 10);
                            int bEnd = Math.Min(row.Length - 1, bin + 10);
                            for (int b = bStart; b <= bEnd; b++)
                            {
                                if (dt == 0 && b == bin) continue;
                                if (row[b] >= val)
                                {
                                    isPeak = false;
                                    break;
                                }
                            }
                        }

                        if (!isPeak) continue;

                        double y0 = Math.Log(Math.Max(minEnergy, magSpectrogram[t][bin - 1]));
                        double y1 = Math.Log(Math.Max(minEnergy, val));
                        double y2 = Math.Log(Math.Max(minEnergy, magSpectrogram[t][bin + 1]));
                        double denom = y0 - 2.0 * y1 + y2;
                        double shift = Math.Abs(denom) > 1e-9 ? Math.Clamp(0.5 * (y0 - y2) / denom, -0.5, 0.5) : 0.0;
                        double shazamMag = y1 * 1477.3 + 6144.0;
                        if (shazamMag > 0)
                            bandPeaks[bandIdx].Add((t, bin + shift, shazamMag));
                    }
                }
            }

            using var bodyStream = new MemoryStream();
            using (var bodyWriter = new BinaryWriter(bodyStream))
            {
                for (int bandIdx = 0; bandIdx < Bands.Length; bandIdx++)
                {
                    var peaks = bandPeaks[bandIdx];
                    if (peaks.Count == 0) continue;

                    using var chunkStream = new MemoryStream();
                    using (var cw = new BinaryWriter(chunkStream))
                    {
                        int lastStripe = 0;
                        foreach (var (stripe, bin, mag) in peaks)
                        {
                            int delta = stripe - lastStripe;
                            while (delta >= 255)
                            {
                                cw.Write((byte)0xFF);
                                cw.Write(stripe);
                                delta = 0;
                            }
                            cw.Write((byte)delta);
                            ushort scaledMag = (ushort)Math.Clamp((int)Math.Round(mag), 1, 65535);
                            ushort scaledFreq = (ushort)Math.Clamp((int)Math.Round(bin * 64.0), 0, 65535);
                            cw.Write(scaledMag);
                            cw.Write(scaledFreq);
                            lastStripe = stripe;
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

            int totalSize = 48 + 8 + bandData.Length;
            byte[] result = new byte[totalSize];
            using (var ms = new MemoryStream(result))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(0xCAFE2580u);                              // Magic1
                bw.Write(0u);                                       // CRC32-Platzhalter
                bw.Write((uint)(totalSize - 48));                   // SizeMinusHeader
                bw.Write(0x94119C00u);                              // Magic2
                bw.Write(0u);
                bw.Write(0u);
                bw.Write(0u);
                bw.Write(3u << 27);                                 // 16000 Hz ID
                bw.Write(0u);
                bw.Write(0u);
                bw.Write((uint)(samples.Length + (int)(SampleRate * 0.24)));
                bw.Write((15u << 19) + 0x40000u);

                bw.Write(0x40000000u);
                bw.Write((uint)bandData.Length);
                bw.Write(bandData);
            }

            uint crc = ComputeCrc32(result, 8, result.Length - 8);
            BitConverter.TryWriteBytes(result.AsSpan(4, 4), crc);
            return result;
        }

        private static double[] BuildHannWindow(int size)
        {
            var win = new double[size];
            for (int i = 0; i < size; i++)
                win[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (size - 1)));
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
