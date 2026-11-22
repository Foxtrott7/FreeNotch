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
    /// Erkennt laufende Musik über Systemaudio-Loopback-Fingerprinting (Shazam-Discovery)
    /// mit Fallback auf aktive Windows-Medien-Sessions (GSMTC) – strikt Opt-in.
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
                TimeSpan.FromSeconds(3.6),
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

            // 1. Primär: Shazam-Audio-Fingerprint (funktioniert auch bei Browser/YouTube/Radio ohne GSMTC)
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

            // 2. Fallback: Aktive Windows-Medien-Session (GSMTC)
            var mp = MediaPlayerService.Instance;
            if (mp.HasKnownTrack && !string.IsNullOrWhiteSpace(mp.TrackTitle))
            {
                TriggerLyricsIfEnabled(mp.TrackTitle, mp.Artist, mp.AlbumTitle);
                string query = Uri.EscapeDataString(string.IsNullOrWhiteSpace(mp.Artist) ? mp.TrackTitle : $"{mp.Artist} {mp.TrackTitle}");
                return new SongRecognitionResult(
                    true,
                    mp.TrackTitle,
                    mp.Artist,
                    mp.AlbumTitle,
                    L.T("songrec.found_and_lyrics"),
                    string.Empty,
                    $"https://open.spotify.com/search/{query}",
                    $"https://music.apple.com/search?term={query}",
                    string.Empty,
                    "Windows Media");
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
