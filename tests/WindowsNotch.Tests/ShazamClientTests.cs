using System;
using System.IO;
using WindowsNotch.Modules.SongRecognition;
using Xunit;

namespace WindowsNotch.Tests
{
    public sealed class ShazamClientTests
    {
        [Fact]
        public void BuildSignature_WritesValidShazamHeaderAndBandChunks()
        {
            // Synthetisches 4-Band-Testsignal (400 Hz, 900 Hz, 2100 Hz, 4200 Hz) über 3 Sekunden bei 16 kHz
            const int sampleRate = 16000;
            const int totalSamples = sampleRate * 3;
            var samples = new short[totalSamples];

            for (int i = 0; i < totalSamples; i++)
            {
                double t = i / (double)sampleRate;
                double sig =
                    0.30 * Math.Sin(2.0 * Math.PI * (320.0 + 90.0 * t) * t) +
                    0.25 * Math.Sin(2.0 * Math.PI * 1237.0 * t) +
                    0.20 * Math.Sin(2.0 * Math.PI * 2400.0 * t) +
                    0.15 * Math.Sin(2.0 * Math.PI * 4200.0 * t);
                samples[i] = (short)(sig * 18000.0);
            }

            byte[] sigBytes = ShazamClient.BuildSignature(samples);

            Assert.True(sigBytes.Length > 56, "Signatur muss Header + Band-Daten enthalten.");
            Assert.Equal(0xCAFE2580u, BitConverter.ToUInt32(sigBytes, 0));

            uint bodyLengthWord2 = BitConverter.ToUInt32(sigBytes, 8);
            uint bodyMagic = BitConverter.ToUInt32(sigBytes, 48);
            uint bodyLengthWord13 = BitConverter.ToUInt32(sigBytes, 52);

            Assert.Equal(0x94119C00u, BitConverter.ToUInt32(sigBytes, 12));
            Assert.Equal(3u << 27, BitConverter.ToUInt32(sigBytes, 28));
            Assert.Equal(0x007C0000u, BitConverter.ToUInt32(sigBytes, 44));
            Assert.Equal(0x40000000u, bodyMagic);
            Assert.Equal((uint)(sigBytes.Length - 48), bodyLengthWord2);
            Assert.Equal(bodyLengthWord2, bodyLengthWord13);
        }

        [Fact]
        public void ParseResponse_ExtractsTrackCoverAndHttpsStreamingLinks()
        {
            const string json = """
            {
              "track": {
                "title": "Midnight City",
                "subtitle": "M83",
                "url": "https://www.shazam.com/track/55167247/midnight-city",
                "images": { "coverart": "https://is1-ssl.mzstatic.com/cover.jpg" },
                "sections": [
                  {
                    "type": "SONG",
                    "metadata": [
                      { "title": "Album", "text": "Hurry Up, We're Dreaming" }
                    ]
                  }
                ],
                "hub": {
                  "actions": [
                    { "type": "uri", "uri": "https://music.apple.com/de/album/midnight-city/123" }
                  ],
                  "providers": [
                    {
                      "type": "SPOTIFY",
                      "actions": [
                        { "uri": "https://open.spotify.com/search/M83%20Midnight%20City" }
                      ]
                    }
                  ]
                }
              }
            }
            """;

            var match = ShazamClient.ParseResponse(json);

            Assert.NotNull(match);
            Assert.Equal("Midnight City", match!.Title);
            Assert.Equal("M83", match.Artist);
            Assert.Equal("Hurry Up, We're Dreaming", match.Album);
            Assert.Equal("https://is1-ssl.mzstatic.com/cover.jpg", match.CoverUrl);
            Assert.Equal("https://music.apple.com/de/album/midnight-city/123", match.AppleMusicUrl);
            Assert.Equal("https://open.spotify.com/search/M83%20Midnight%20City", match.SpotifyUrl);
        }
    }
}
