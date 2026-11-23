using System;
using System.Threading;
using System.Threading.Tasks;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.SongRecognition
{
    public sealed record SongRecognitionResult(
        bool Success,
        string Title,
        string Artist,
        string Album,
        string StatusMessage,
        string CoverUrl = "",
        string SpotifyUrl = "",
        string AppleMusicUrl = "",
        string ShazamUrl = "",
        string Source = "");

    /// <summary>
    /// Erkennt laufende Musik ausschließlich über Systemaudio-Loopback-Fingerprinting (Shazam-Discovery) – strikt Opt-in.
    /// </summary>
    public static class SongRecognitionService
    {
        public static async Task<SongRecognitionResult> RecognizeAsync(
            Action<double>? onLevel,
            CancellationToken ct)
        {
            if (!SettingsService.Current.OptInMusicRecognition)
            {
                return new SongRecognitionResult(
                    false,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    L.T("songrec.enable_hint"));
            }

            var (samples16k, peakRms) = await AudioLoopbackCapture.CaptureAsync(
                TimeSpan.FromSeconds(5.5),
                onLevel,
                ct);

            if (ct.IsCancellationRequested)
                return new SongRecognitionResult(false, string.Empty, string.Empty, string.Empty, L.T("songrec.canceled"));

            if (peakRms < 0.004)
            {
                return new SongRecognitionResult(
                    false,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    L.T("songrec.no_audio"));
            }

            try
            {
                var shazam = await ShazamClient.RecognizeAsync(samples16k, ct);
                if (shazam != null)
                {
                    TriggerLyricsIfEnabled(shazam.Title, shazam.Artist, shazam.Album);
                    return new SongRecognitionResult(
                        true,
                        shazam.Title,
                        shazam.Artist,
                        shazam.Album,
                        L.T("songrec.found_shazam"),
                        shazam.CoverUrl,
                        shazam.SpotifyUrl,
                        shazam.AppleMusicUrl,
                        shazam.ShazamUrl,
                        "Shazam");
                }
            }
            catch (OperationCanceledException)
            {
                return new SongRecognitionResult(false, string.Empty, string.Empty, string.Empty, L.T("songrec.canceled"));
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }

            return new SongRecognitionResult(
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                L.T("songrec.no_match"));
        }

        private static void TriggerLyricsIfEnabled(string title, string artist, string album)
        {
            if (!SettingsService.Current.OptInLyrics) return;
            var mp = MediaPlayerService.Instance;
            LyricsService.Instance.EnsureForTrack(
                title,
                artist,
                album,
                mp.Duration,
                hasTrack: true,
                forceRefresh: true);
        }
    }
}
