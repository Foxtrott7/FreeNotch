using System;
using System.IO;
using System.Media;

namespace WindowsNotch.Modules.Notifications
{
    /// <summary>
    /// Erzeugt und spielt einen hellen, weichen Klick-Ton ("Glass Tap") im Stil moderner OS-Overlays
    /// oder optional eine benutzerdefinierte .wav-Datei.
    /// </summary>
    internal static class NotchSound
    {
        private static readonly byte[] ClickWav = GenerateBrightClickWav();

        public static void PlayNotificationClick(string? customWavPath = null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(customWavPath) && File.Exists(customWavPath))
                {
                    using var customPlayer = new SoundPlayer(customWavPath);
                    customPlayer.Play();
                    return;
                }

                using var ms = new MemoryStream(ClickWav, writable: false);
                using var player = new SoundPlayer(ms);
                player.Play();
            }
            catch
            {
                // Audio-Fehler niemals nach außen tragen
            }
        }

        private static byte[] GenerateBrightClickWav()
        {
            const int sampleRate = 44100;
            const double durationSec = 0.044; // 44 ms: kurz, hell, geschmeidig
            int sampleCount = (int)(sampleRate * durationSec);
            int dataSize = sampleCount * 2; // 16-bit Mono

            using var ms = new MemoryStream(44 + dataSize);
            using var bw = new BinaryWriter(ms);

            // RIFF/WAV Header
            bw.Write("RIFF".ToCharArray());
            bw.Write(36 + dataSize);
            bw.Write("WAVE".ToCharArray());
            bw.Write("fmt ".ToCharArray());
            bw.Write(16);
            bw.Write((short)1); // PCM
            bw.Write((short)1); // Mono
            bw.Write(sampleRate);
            bw.Write(sampleRate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write("data".ToCharArray());
            bw.Write(dataSize);

            double phaseMain = 0;
            double phaseOvertone = 0;

            for (int i = 0; i < sampleCount; i++)
            {
                double t = (double)i / sampleRate;
                double norm = (double)i / sampleCount;

                // Seidenweicher Attack (3 ms) + glattes Ausklingen ohne harte Kante
                double attack = t < 0.003 ? Math.Sin((t / 0.003) * (Math.PI / 2.0)) : 1.0;
                double decay = Math.Exp(-norm * 7.5) * (1.0 - Math.Pow(norm, 3));
                double env = attack * decay;

                // Heller, luftiger Tap (1920 Hz -> 1480 Hz) + zarte Quinte (1.5x)
                double freq = 1480.0 + 440.0 * Math.Exp(-norm * 12.0);
                phaseMain += 2.0 * Math.PI * freq / sampleRate;
                phaseOvertone += 2.0 * Math.PI * (freq * 1.5) / sampleRate;

                double wave = 0.82 * Math.Sin(phaseMain) + 0.18 * Math.Sin(phaseOvertone);
                short sample = (short)Math.Clamp(wave * env * 0.28 * short.MaxValue, short.MinValue, short.MaxValue);
                bw.Write(sample);
            }

            return ms.ToArray();
        }
    }
}
