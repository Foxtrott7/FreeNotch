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
        string StatusMessage);

    /// <summary>
    /// Erkennt laufende Musik über Systemaudio-Loopback-Pegel + aktive Windows-Medien-Session
    /// (strikt Opt-in über OptInMusicRecognition) und übergibt erkannte Tracks an LyricsService.
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

            // Kurzes Systemaudio-Sampling mit Live-Pegel für visuelles Feedback (mind. 1.8s für sichtbares Shazam-Feedback)
            var started = DateTime.UtcNow;
            var (_, peakRms) = await AudioLoopbackCapture.CaptureAsync(
                TimeSpan.FromSeconds(2.4),
                onLevel,
                ct);

            var elapsed = DateTime.UtcNow - started;
            if (elapsed < TimeSpan.FromSeconds(1.8) && !ct.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(1.8) - elapsed, ct);

            if (ct.IsCancellationRequested)
                return new SongRecognitionResult(false, string.Empty, string.Empty, string.Empty, L.T("songrec.canceled"));

            var mp = MediaPlayerService.Instance;
            if (mp.HasKnownTrack && !string.IsNullOrWhiteSpace(mp.TrackTitle))
            {
                if (SettingsService.Current.OptInLyrics)
                {
                    LyricsService.Instance.EnsureForTrack(
                        mp.TrackTitle,
                        mp.Artist,
                        mp.AlbumTitle,
                        mp.Duration,
                        hasTrack: true,
                        forceRefresh: true);
                }

                return new SongRecognitionResult(
                    true,
                    mp.TrackTitle,
                    mp.Artist,
                    mp.AlbumTitle,
                    L.T("songrec.found_and_lyrics"));
            }

            if (peakRms < 0.005)
            {
                return new SongRecognitionResult(
                    false,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    L.T("songrec.no_audio"));
            }

            return new SongRecognitionResult(
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                L.T("songrec.no_media_source"));
        }
    }
}
