using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace WindowsNotch.Services
{
    /// <summary>
    /// Zentraler WinRT-GSMTC-Dienst für Medienstatus, Cover, Timeline-Fortschritt und Transportsteuerung.
    /// Filtert flüchtige Browser-Thumbnail-Previews (z. B. YouTube-Hover) heraus und priorisiert aktive Audio-Player.
    /// </summary>
    public sealed class MediaPlayerService
    {
        private static readonly Lazy<MediaPlayerService> _instance = new(() => new MediaPlayerService());
        public static MediaPlayerService Instance => _instance.Value;

        public const string DefaultNoTrackTitle = "Keine Medienwiedergabe";
        public static string DefaultNoTrackArtist => L.T("media.no_track_hint");

        private static readonly TimeSpan BrowserInitialDebounce = TimeSpan.FromMilliseconds(800);
        private static readonly TimeSpan BrowserSwitchDebounce = TimeSpan.FromMilliseconds(2200);
        private static readonly TimeSpan BrowserShortClipDebounce = TimeSpan.FromMilliseconds(5000);

        private sealed record BrowserTrackSnapshot(
            string TrackKey,
            string Title,
            string Artist,
            string Album,
            ImageSource? Cover,
            TimeSpan Duration);
        private static readonly string[] BrowserAppMarkers =
        {
            "chrome", "msedge", "edge", "firefox", "brave", "opera",
            "vivaldi", "arc", "zen", "waterfox", "librewolf", "chromium",
            "floorp", "thorium"
        };

        private readonly object _gate = new();
        private readonly List<GlobalSystemMediaTransportControlsSession> _trackedSessions = new();
        private bool _started;
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _activeSession;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _pendingCts;
        private int _refreshVersion;

        private string _confirmedAppId = string.Empty;
        private string _confirmedTrackKey = string.Empty;
        private TimeSpan _confirmedTrackDuration = TimeSpan.Zero;
        private string _previousConfirmedTrackKey = string.Empty;
        private BrowserTrackSnapshot? _lastLongBrowserTrack;
        private string _pendingKey = string.Empty;
        private DateTimeOffset _pendingFirstSeenUtc = DateTimeOffset.MinValue;
        private bool? _pendingToggleTarget;
        private DateTimeOffset _pendingToggleExpiresUtc = DateTimeOffset.MinValue;
        private Timer? _playingWatchTimer;

        private TimeSpan _timelineStart;

        public bool IsPlaying { get; private set; }
        public bool HasSession => _activeSession != null;
        public bool HasKnownTrack =>
            IsPlaying || _activeSession != null || (!string.IsNullOrWhiteSpace(TrackTitle) && TrackTitle != DefaultNoTrackTitle);

        public string TrackTitle { get; private set; } = DefaultNoTrackTitle;
        public string Artist { get; private set; } = DefaultNoTrackArtist;
        public string AlbumTitle { get; private set; } = string.Empty;
        public ImageSource? Cover { get; private set; }

        public TimeSpan Position { get; private set; }
        public TimeSpan Duration { get; private set; }
        public DateTimeOffset TimelineUpdatedAtUtc { get; private set; }

        public event Action? StateChanged;

        private MediaPlayerService() { }

        public void EnsureStarted()
        {
            lock (_gate)
            {
                if (_started) return;
                _started = true;
                _cts = new CancellationTokenSource();
            }
            _ = InitAsync(_cts.Token);
        }

        public TimeSpan GetInterpolatedPosition()
        {
            TimeSpan pos = Position;
            if (IsPlaying && TimelineUpdatedAtUtc.Year > 2000)
            {
                var elapsed = DateTimeOffset.UtcNow - TimelineUpdatedAtUtc;
                if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(12))
                    pos += elapsed;
            }

            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
            if (Duration > TimeSpan.Zero && pos > Duration) pos = Duration;
            return pos;
        }

        public void TogglePlayPause()
        {
            var s = _activeSession;
            if (s == null) return;

            bool targetPlaying = !IsPlaying;
            _pendingToggleTarget = targetPlaying;
            _pendingToggleExpiresUtc = DateTimeOffset.UtcNow.AddMilliseconds(2500);

            if (!targetPlaying)
                Position = GetInterpolatedPosition();
            TimelineUpdatedAtUtc = DateTimeOffset.UtcNow;
            IsPlaying = targetPlaying;
            StateChanged?.Invoke();

            _ = SendToggleAsync(s, targetPlaying);
        }

        private async Task SendToggleAsync(GlobalSystemMediaTransportControlsSession session, bool targetPlaying)
        {
            try
            {
                bool ok = targetPlaying
                    ? await session.TryPlayAsync()
                    : await session.TryPauseAsync();
                if (!ok)
                    ok = await session.TryTogglePlayPauseAsync();

                if (!ok)
                {
                    _pendingToggleTarget = null;
                    _pendingToggleExpiresUtc = DateTimeOffset.MinValue;
                    await RefreshAsync();
                }
            }
            catch (Exception ex)
            {
                _pendingToggleTarget = null;
                _pendingToggleExpiresUtc = DateTimeOffset.MinValue;
                App.LogError(ex);
                await RefreshAsync();
            }
        }
        public void SkipNext() => Control(s => s.TrySkipNextAsync());
        public void SkipPrevious() => Control(s => s.TrySkipPreviousAsync());

        public void SeekTo(TimeSpan target)
        {
            if (Duration <= TimeSpan.Zero) return;
            var clamped = target < TimeSpan.Zero ? TimeSpan.Zero : (target > Duration ? Duration : target);
            long ticks = (_timelineStart + clamped).Ticks;
            Control(s => s.TryChangePlaybackPositionAsync(ticks));
        }

        private async void Control(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> action)
        {
            try
            {
                var s = _activeSession;
                if (s != null) await action(s);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private async Task InitAsync(CancellationToken ct)
        {
            try
            {
                var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (ct.IsCancellationRequested) return;
                _manager = mgr;
                _manager.CurrentSessionChanged += OnSessionsOrCurrentChanged;
                _manager.SessionsChanged += OnSessionsOrCurrentChanged;
                SyncTrackedSessions();
                _ = RefreshAsync();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private void OnSessionsOrCurrentChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args)
        {
            SyncTrackedSessions();
            _ = RefreshAsync();
        }

        private void SyncTrackedSessions()
        {
            var mgr = _manager;
            if (mgr == null) return;

            IReadOnlyList<GlobalSystemMediaTransportControlsSession> current;
            try
            {
                current = mgr.GetSessions();
            }
            catch
            {
                return;
            }

            lock (_gate)
            {
                for (int i = _trackedSessions.Count - 1; i >= 0; i--)
                {
                    var existing = _trackedSessions[i];
                    if (!ContainsSession(current, existing))
                    {
                        UnsubscribeSession(existing);
                        _trackedSessions.RemoveAt(i);
                    }
                }

                for (int i = 0; i < current.Count; i++)
                {
                    var session = current[i];
                    if (session != null && !ContainsSession(_trackedSessions, session))
                    {
                        session.MediaPropertiesChanged += OnSessionUpdated;
                        session.PlaybackInfoChanged += OnSessionUpdated;
                        session.TimelinePropertiesChanged += OnSessionUpdated;
                        _trackedSessions.Add(session);
                    }
                }
            }
        }

        private void UnsubscribeSession(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                session.MediaPropertiesChanged -= OnSessionUpdated;
                session.PlaybackInfoChanged -= OnSessionUpdated;
                session.TimelinePropertiesChanged -= OnSessionUpdated;
            }
            catch { }
        }

        private static bool ContainsSession(
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> list,
            GlobalSystemMediaTransportControlsSession target)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (Equals(list[i], target)) return true;
            }
            return false;
        }

        private void OnSessionUpdated(GlobalSystemMediaTransportControlsSession s, object a)
            => _ = RefreshAsync();

        private static bool IsBrowserSession(GlobalSystemMediaTransportControlsSession session)
        {
            string appId = session.SourceAppUserModelId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(appId)) return false;

            for (int i = 0; i < BrowserAppMarkers.Length; i++)
            {
                if (appId.Contains(BrowserAppMarkers[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsSessionPlaying(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                return session.GetPlaybackInfo()?.PlaybackStatus ==
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch
            {
                return false;
            }
        }

        private bool HasAnyPlayingSession()
        {
            try
            {
                var current = _manager?.GetCurrentSession();
                if (current != null && IsSessionPlaying(current))
                    return true;
            }
            catch { }

            List<GlobalSystemMediaTransportControlsSession> snapshot;
            lock (_gate)
            {
                snapshot = new List<GlobalSystemMediaTransportControlsSession>(_trackedSessions);
            }
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (IsSessionPlaying(snapshot[i]))
                    return true;
            }
            return false;
        }

        private void UpdatePlayingWatchTimer(bool isPlaying)
        {
            if (isPlaying)
            {
                _playingWatchTimer ??= new Timer(_ => OnPlayingWatchTick(), null, 1200, 1200);
                try { _playingWatchTimer.Change(1200, 1200); } catch { }
            }
            else if (_playingWatchTimer != null)
            {
                try { _playingWatchTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            }
        }

        private void OnPlayingWatchTick()
        {
            if (!IsPlaying)
            {
                UpdatePlayingWatchTimer(false);
                return;
            }
            if (_pendingToggleTarget != null && DateTimeOffset.UtcNow < _pendingToggleExpiresUtc)
                return;

            SyncTrackedSessions();
            if (!HasAnyPlayingSession())
                _ = RefreshAsync();
        }

        private GlobalSystemMediaTransportControlsSession? SelectCandidateSession(
            out GlobalSystemMediaTransportControlsSession? fallbackSession)
        {
            var mgr = _manager;
            fallbackSession = null;
            if (mgr == null) return null;

            GlobalSystemMediaTransportControlsSession? current = null;
            try { current = mgr.GetCurrentSession(); } catch { }

            List<GlobalSystemMediaTransportControlsSession> sessions;
            lock (_gate)
            {
                sessions = new List<GlobalSystemMediaTransportControlsSession>(_trackedSessions);
            }
            if (current != null && !ContainsSession(sessions, current))
                sessions.Insert(0, current);

            // 1. Aktiver Nicht-Browser-Audio-Player (z. B. Spotify) gewinnt immer vor Browser-Sessions
            GlobalSystemMediaTransportControlsSession? playingNonBrowser = null;
            GlobalSystemMediaTransportControlsSession? playingBrowser = null;
            GlobalSystemMediaTransportControlsSession? pausedNonBrowser = null;

            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                bool isBrowser = IsBrowserSession(s);
                bool playing = IsSessionPlaying(s);

                if (!isBrowser && playing)
                {
                    if (playingNonBrowser == null || Equals(s, current))
                        playingNonBrowser = s;
                }
                else if (isBrowser && playing)
                {
                    if (playingBrowser == null || Equals(s, current))
                        playingBrowser = s;
                }
                else if (!isBrowser)
                {
                    if (pausedNonBrowser == null || Equals(s, _activeSession) || Equals(s, current))
                        pausedNonBrowser = s;
                }
            }

            // Fallback merken (z. B. pausiertes Spotify oder bisherige bestätigte Session)
            if (_activeSession != null && ContainsSession(sessions, _activeSession))
                fallbackSession = _activeSession;
            else
                fallbackSession = pausedNonBrowser;

            if (playingNonBrowser != null) return playingNonBrowser;
            if (playingBrowser != null) return playingBrowser;

            if (_activeSession != null && ContainsSession(sessions, _activeSession))
                return _activeSession;

            return pausedNonBrowser ?? current;
        }

        private void CancelPendingConfirmation()
        {
            lock (_gate)
            {
                _pendingCts?.Cancel();
                _pendingCts?.Dispose();
                _pendingCts = null;
                _pendingKey = string.Empty;
                _pendingFirstSeenUtc = DateTimeOffset.MinValue;
            }
        }

        private bool IsBrowserCandidateConfirmed(string candidateKey, TimeSpan requiredDebounce)
        {
            TimeSpan remaining;
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (!string.Equals(_pendingKey, candidateKey, StringComparison.Ordinal))
                {
                    _pendingCts?.Cancel();
                    _pendingCts?.Dispose();
                    _pendingCts = null;
                    _pendingKey = candidateKey;
                    _pendingFirstSeenUtc = DateTimeOffset.UtcNow;
                }

                TimeSpan elapsed = DateTimeOffset.UtcNow - _pendingFirstSeenUtc;
                if (elapsed >= requiredDebounce)
                {
                    _pendingCts?.Cancel();
                    _pendingCts?.Dispose();
                    _pendingCts = null;
                    _pendingKey = string.Empty;
                    _pendingFirstSeenUtc = DateTimeOffset.MinValue;
                    return true;
                }

                if (_pendingCts != null)
                    return false;

                remaining = requiredDebounce - elapsed + TimeSpan.FromMilliseconds(30);
                cts = new CancellationTokenSource();
                _pendingCts = cts;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(remaining, cts.Token);
                    if (cts.IsCancellationRequested) return;
                    lock (_gate)
                    {
                        if (ReferenceEquals(_pendingCts, cts))
                            _pendingCts = null;
                    }
                    await RefreshAsync();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { App.LogError(ex); }
            });

            return false;
        }

        private async Task RefreshAsync()
        {
            int version = Interlocked.Increment(ref _refreshVersion);
            try
            {
                var session = SelectCandidateSession(out var fallbackSession);
                if (session == null)
                {
                    CancelPendingConfirmation();
                    await ApplyAsync(version, null, string.Empty, string.Empty, false,
                        DefaultNoTrackTitle, DefaultNoTrackArtist, string.Empty,
                        null, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.MinValue);
                    return;
                }

                bool playing = IsSessionPlaying(session);
                string appId = session.SourceAppUserModelId ?? string.Empty;
                var props = await session.TryGetMediaPropertiesAsync();
                if (version != _refreshVersion) return;

                string rawTitle = props?.Title?.Trim() ?? string.Empty;
                string rawArtist = props?.Artist?.Trim() ?? string.Empty;
                string title = string.IsNullOrWhiteSpace(rawTitle) ? "Unbekannter Titel" : rawTitle;
                string artist = string.IsNullOrWhiteSpace(rawArtist) ? "Unbekannter Interpret" : rawArtist;
                string trackKey = $"{title}||{artist}";

                if (IsBrowserSession(session))
                {
                    var timeline = session.GetTimelineProperties();
                    TimeSpan tStart = timeline?.StartTime ?? TimeSpan.Zero;
                    TimeSpan tEnd = timeline?.EndTime ?? TimeSpan.Zero;
                    TimeSpan tPos = timeline?.Position ?? TimeSpan.Zero;
                    TimeSpan duration = tEnd > tStart ? tEnd - tStart : TimeSpan.Zero;
                    TimeSpan pos = tPos > tStart ? tPos - tStart : TimeSpan.Zero;
                    if (duration > TimeSpan.Zero && pos > duration) pos = duration;
                    DateTimeOffset updatedAt = timeline?.LastUpdatedTime ?? DateTimeOffset.MinValue;
                    if (playing && updatedAt.Year <= 2000)
                        updatedAt = DateTimeOffset.UtcNow;

                    // Falls Chrome/Edge beim Fortsetzen des Hauptvideos noch auf dem alten Hover-Titel
                    // hängen bleibt, aber die Timeline-Duration wieder exakt zum vorherigen langen Video passt:
                    var prevLong = _lastLongBrowserTrack;
                    if (playing && prevLong != null
                        && !string.Equals(trackKey, prevLong.TrackKey, StringComparison.OrdinalIgnoreCase)
                        && duration >= TimeSpan.FromSeconds(25)
                        && Math.Abs((duration - prevLong.Duration).TotalSeconds) <= 2.5
                        && (_confirmedTrackDuration == TimeSpan.Zero
                            || Math.Abs((_confirmedTrackDuration - prevLong.Duration).TotalSeconds) > 5.0))
                    {
                        CancelPendingConfirmation();
                        await ApplyAsync(version, session, appId, prevLong.TrackKey, true,
                            prevLong.Title, prevLong.Artist, prevLong.Album,
                            null, tStart, pos, duration, updatedAt, prevLong.Cover);
                        return;
                    }

                    bool alreadyConfirmed = Equals(session, _activeSession)
                        && string.Equals(trackKey, _confirmedTrackKey, StringComparison.OrdinalIgnoreCase);
                    bool returningToPrevious = playing
                        && !string.IsNullOrEmpty(_previousConfirmedTrackKey)
                        && string.Equals(trackKey, _previousConfirmedTrackKey, StringComparison.OrdinalIgnoreCase);

                    if (!alreadyConfirmed && !returningToPrevious)
                    {
                        string candidateKey = $"{appId}||{trackKey}";
                        if (!playing || string.IsNullOrWhiteSpace(rawTitle))
                        {
                            // Flüchtiges/pausiertes Browser-Preview nicht neu übernehmen;
                            // falls ein Nicht-Browser-Fallback existiert, sauber dorthin zurückfallen.
                            CancelPendingConfirmation();
                            if (fallbackSession != null && !IsBrowserSession(fallbackSession) && !Equals(fallbackSession, session)
                                && (!Equals(fallbackSession, _activeSession) || IsPlaying))
                            {
                                await CommitSessionAsync(version, fallbackSession, null);
                                return;
                            }

                            // Wenn aktuell nichts mehr spielt, darf IsPlaying nicht auf true hängen bleiben.
                            if (IsPlaying && !HasAnyPlayingSession())
                            {
                                var disp = Application.Current?.Dispatcher;
                                if (disp != null)
                                {
                                    await disp.InvokeAsync(() =>
                                    {
                                        if (version != _refreshVersion || !IsPlaying) return;
                                        IsPlaying = false;
                                        TimelineUpdatedAtUtc = DateTimeOffset.UtcNow;
                                        UpdatePlayingWatchTimer(false);
                                        StateChanged?.Invoke();
                                    });
                                }
                            }
                            return;
                        }

                        TimeSpan requiredDebounce = !HasKnownTrack
                            ? BrowserInitialDebounce
                            : (duration < TimeSpan.FromSeconds(25)
                                ? BrowserShortClipDebounce
                                : BrowserSwitchDebounce);

                        if (!IsBrowserCandidateConfirmed(candidateKey, requiredDebounce))
                            return;
                    }
                }

                CancelPendingConfirmation();
                await CommitSessionAsync(version, session, props);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private async Task CommitSessionAsync(
            int version,
            GlobalSystemMediaTransportControlsSession session,
            GlobalSystemMediaTransportControlsSessionMediaProperties? props)
        {
            bool playing = IsSessionPlaying(session);
            string appId = session.SourceAppUserModelId ?? string.Empty;

            var timeline = session.GetTimelineProperties();
            TimeSpan tStart = timeline?.StartTime ?? TimeSpan.Zero;
            TimeSpan tEnd = timeline?.EndTime ?? TimeSpan.Zero;
            TimeSpan tPos = timeline?.Position ?? TimeSpan.Zero;
            TimeSpan duration = tEnd > tStart ? tEnd - tStart : TimeSpan.Zero;
            TimeSpan pos = tPos > tStart ? tPos - tStart : TimeSpan.Zero;
            if (duration > TimeSpan.Zero && pos > duration) pos = duration;
            DateTimeOffset updatedAt = timeline?.LastUpdatedTime ?? DateTimeOffset.MinValue;
            if (playing && updatedAt.Year <= 2000)
                updatedAt = DateTimeOffset.UtcNow;

            props ??= await session.TryGetMediaPropertiesAsync();
            if (version != _refreshVersion) return;

            string title = string.IsNullOrWhiteSpace(props?.Title) ? "Unbekannter Titel" : props!.Title.Trim();
            string artist = string.IsNullOrWhiteSpace(props?.Artist) ? "Unbekannter Interpret" : props!.Artist.Trim();
            string album = props?.AlbumTitle ?? string.Empty;
            string trackKey = $"{title}||{artist}";

            bool sameTrack = Equals(session, _activeSession)
                && string.Equals(trackKey, _confirmedTrackKey, StringComparison.OrdinalIgnoreCase);
            ImageSource? existingCover = sameTrack ? Cover : null;
            byte[]? thumb = existingCover == null ? await ReadThumbnailAsync(props?.Thumbnail) : null;
            if (version != _refreshVersion) return;

            await ApplyAsync(version, session, appId, trackKey, playing, title, artist, album,
                thumb, tStart, pos, duration, updatedAt, existingCover);
        }

        private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference? thumbnail)
        {
            if (thumbnail == null) return null;
            try
            {
                using var stream = await thumbnail.OpenReadAsync();
                var reader = new DataReader(stream);
                await reader.LoadAsync((uint)stream.Size);
                var bytes = new byte[stream.Size];
                reader.ReadBytes(bytes);
                return bytes;
            }
            catch
            {
                return null;
            }
        }

        private async Task ApplyAsync(
            int version,
            GlobalSystemMediaTransportControlsSession? session,
            string appId,
            string trackKey,
            bool playing,
            string title,
            string artist,
            string album,
            byte[]? thumb,
            TimeSpan timelineStart,
            TimeSpan position,
            TimeSpan duration,
            DateTimeOffset updatedAtUtc,
            ImageSource? preloadedCover = null)
        {
            var disp = Application.Current?.Dispatcher;
            if (disp == null) return;

            ImageSource? cover = preloadedCover;
            if (cover == null && thumb != null && thumb.Length > 0)
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = new MemoryStream(thumb);
                    bmp.DecodePixelWidth = 180;
                    bmp.EndInit();
                    bmp.Freeze();
                    cover = bmp;
                }
                catch { cover = null; }
            }

            await disp.InvokeAsync(() =>
            {
                if (version != _refreshVersion) return;

                if (!string.IsNullOrEmpty(_confirmedTrackKey)
                    && !string.Equals(_confirmedTrackKey, trackKey, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(trackKey))
                {
                    _previousConfirmedTrackKey = _confirmedTrackKey;
                }

                _activeSession = session;
                _confirmedAppId = appId;
                _confirmedTrackKey = trackKey;
                _confirmedTrackDuration = duration;

                if (session != null && IsBrowserSession(session) && duration >= TimeSpan.FromSeconds(25)
                    && !string.IsNullOrEmpty(trackKey))
                {
                    _lastLongBrowserTrack = new BrowserTrackSnapshot(
                        trackKey, title, artist, album, cover, duration);
                }

                if (_pendingToggleTarget is bool target)
                {
                    if (playing == target)
                    {
                        _pendingToggleTarget = null;
                        _pendingToggleExpiresUtc = DateTimeOffset.MinValue;
                    }
                    else if (DateTimeOffset.UtcNow < _pendingToggleExpiresUtc
                        && Equals(session, _activeSession)
                        && string.Equals(trackKey, _confirmedTrackKey, StringComparison.OrdinalIgnoreCase))
                    {
                        playing = target;
                        if (!playing)
                        {
                            position = Position;
                            updatedAtUtc = DateTimeOffset.UtcNow;
                        }
                    }
                    else
                    {
                        _pendingToggleTarget = null;
                        _pendingToggleExpiresUtc = DateTimeOffset.MinValue;
                    }
                }

                bool resumedFromPause = !IsPlaying && playing;
                var nowUtc = DateTimeOffset.UtcNow;
                if (playing && (updatedAtUtc.Year <= 2000
                    || updatedAtUtc > nowUtc
                    || (resumedFromPause && (nowUtc - updatedAtUtc) > TimeSpan.FromSeconds(5))))
                {
                    updatedAtUtc = nowUtc;
                }

                IsPlaying = playing;
                UpdatePlayingWatchTimer(playing);
                TrackTitle = title;
                Artist = artist;
                AlbumTitle = album;
                Cover = cover;
                _timelineStart = timelineStart;
                Position = position;
                Duration = duration;
                TimelineUpdatedAtUtc = updatedAtUtc;

                LyricsService.Instance.EnsureForTrack(title, artist, album, duration, HasKnownTrack);
                StateChanged?.Invoke();
            });
        }
    }
}
