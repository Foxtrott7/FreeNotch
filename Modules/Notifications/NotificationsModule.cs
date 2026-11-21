using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.Notifications
{
    /// <summary>
    /// Benachrichtigungs-Modul mit WinRT UserNotificationListener:
    /// Berechtigungsabfrage, Live-Banner-Vorschau, App-Filter und Verlauf.
    /// </summary>
    public partial class NotificationsModule : NotchModuleBase
    {
        private const int MaxHistoryCount = 50;
        private static readonly TimeSpan BannerDuration = TimeSpan.FromSeconds(5);

        private static readonly FontFamily UiTextFont = new("Segoe UI Variable Text, Segoe UI");
        private static readonly FontFamily UiDisplayFont = new("Segoe UI Variable Display, Segoe UI");

        public override string Id => "notifications";
        public override string Title => L.T("module.notifications");
        public override NotchIconKind IconKind => NotchIconKind.Bell;
        public override int Priority => _bannerItem != null ? 150 : (_history.Count > 0 ? 25 : 15);
        public override double? PreferredPreviewWidth => 280;
        public override double? PreferredLiveWidth => _bannerItem != null ? 290 : null;
        public override (double Width, double Height)? PreferredExpandedSize => (510, 390);

        private readonly List<NotificationEntry> _history = new();
        private readonly HashSet<uint> _seenIds = new();
        private readonly HashSet<string> _knownApps = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _refreshLock = new(1, 1);

        private UserNotificationListener? _listener;
        private UserNotificationListenerAccessStatus _accessStatus = UserNotificationListenerAccessStatus.Unspecified;
        private bool _listenerUnavailable;
        private string? _statusError;
        private bool _initialLoadDone;

        private CancellationTokenSource? _cts;
        private DispatcherTimer? _bannerTimer;
        private DispatcherTimer? _pollTimer;
        private NotificationEntry? _bannerItem;

        // UI-Zustand der geöffneten Ansicht
        private StackPanel? _expandedContentPanel;
        private Border? _expandedCountPill;
        private TextBlock? _expandedCountBadge;
        private Button? _expandedSoundBtn;

        public override void Start()
        {
            _cts = new CancellationTokenSource();
            _initialLoadDone = false;
            _ = InitAsync(_cts.Token);
        }

        public override void Stop()
        {
            _cts?.Cancel();
            _cts = null;

            StopBannerTimer();
            StopPollTimer();
            _bannerItem = null;

            if (_listener != null)
            {
                try { _listener.NotificationChanged -= OnNotificationChanged; }
                catch { /* Ignorieren beim Herunterfahren */ }
                _listener = null;
            }
        }

        private async Task InitAsync(CancellationToken ct)
        {
            try
            {
                _listener = UserNotificationListener.Current;
                _accessStatus = await _listener.RequestAccessAsync();
                if (ct.IsCancellationRequested) return;

                if (_accessStatus == UserNotificationListenerAccessStatus.Allowed)
                {
                    SetupListenerUpdates();
                    await RefreshNotificationsAsync(suppressBanner: true, ct);
                }
                else
                {
                    NotifyUiChanged();
                }
            }
            catch (Exception ex)
            {
                _listenerUnavailable = true;
                _statusError = ex.Message;
                App.LogError(ex);
                NotifyUiChanged();
            }
        }

        private async void RequestPermissionAsync()
        {
            try
            {
                _listenerUnavailable = false;
                _statusError = null;
                _listener ??= UserNotificationListener.Current;

                _accessStatus = await _listener.RequestAccessAsync();
                if (_accessStatus == UserNotificationListenerAccessStatus.Allowed)
                {
                    SetupListenerUpdates();
                    await RefreshNotificationsAsync(suppressBanner: true);
                }
                else
                {
                    NotifyUiChanged();
                }
            }
            catch (Exception ex)
            {
                _listenerUnavailable = true;
                _statusError = ex.Message;
                App.LogError(ex);
                NotifyUiChanged();
            }
        }

        private void SetupListenerUpdates()
        {
            if (_listener == null) return;

            try
            {
                _listener.NotificationChanged -= OnNotificationChanged;
                _listener.NotificationChanged += OnNotificationChanged;
                StopPollTimer();
            }
            catch
            {
                // Unpackaged WPF-Apps ohne Paketidentität können NotificationChanged nicht immer abonnieren (0x80070490).
                // In diesem Fall liefert GetNotificationsAsync() trotzdem Daten über ein leichtes Fallback-Intervall.
                StartPollTimer();
            }
        }

        private void StartPollTimer()
        {
            var disp = Application.Current?.Dispatcher;
            if (disp == null) return;

            if (!disp.CheckAccess())
            {
                disp.BeginInvoke(new Action(StartPollTimer));
                return;
            }

            if (_pollTimer != null) return;
            _pollTimer = new DispatcherTimer(DispatcherPriority.Background, disp)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _pollTimer.Tick += (_, _) =>
            {
                if (_cts != null && !_cts.IsCancellationRequested)
                    _ = RefreshNotificationsAsync(suppressBanner: false, _cts.Token);
            };
            _pollTimer.Start();
        }

        private void StopPollTimer()
        {
            if (_pollTimer == null) return;
            _pollTimer.Stop();
            _pollTimer = null;
        }

        private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
        {
            if (_cts == null || _cts.IsCancellationRequested) return;
            _ = RefreshNotificationsAsync(suppressBanner: false, _cts.Token);
        }

        private async Task RefreshNotificationsAsync(bool suppressBanner, CancellationToken ct = default)
        {
            if (_listener == null || _accessStatus != UserNotificationListenerAccessStatus.Allowed)
                return;

            if (!await _refreshLock.WaitAsync(0, ct))
                return;

            try
            {
                var rawList = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                if (ct.IsCancellationRequested) return;

                var parsed = new List<NotificationEntry>(rawList.Count);
                foreach (var n in rawList)
                {
                    if (ct.IsCancellationRequested) return;
                    var entry = await ParseNotificationAsync(n);
                    if (entry != null)
                        parsed.Add(entry);
                }

                // Älteste zuerst verarbeiten, damit neueste oben landen
                parsed.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

                var disp = Application.Current?.Dispatcher;
                if (disp == null) return;

                await disp.InvokeAsync(() =>
                {
                    NotificationEntry? newestUnblocked = null;

                    bool addedNewApp = false;
                    foreach (var entry in parsed)
                    {
                        if (_knownApps.Add(entry.FilterKey))
                            addedNewApp = true;

                        if (!_seenIds.Add(entry.Id))
                            continue;

                        if (IsAppBlocked(entry))
                            continue;

                        _history.RemoveAll(h => h.Id == entry.Id);
                        _history.Insert(0, entry);

                        if (_initialLoadDone && !suppressBanner)
                            newestUnblocked = entry;
                    }

                    if (addedNewApp)
                    {
                        SettingsService.UpdateQuiet(s =>
                        {
                            foreach (var app in _knownApps)
                            {
                                if (!s.NotificationKnownApps.Contains(app, StringComparer.OrdinalIgnoreCase))
                                    s.NotificationKnownApps.Add(app);
                            }
                        });
                    }

                    if (_history.Count > MaxHistoryCount)
                        _history.RemoveRange(MaxHistoryCount, _history.Count - MaxHistoryCount);

                    _initialLoadDone = true;

                    if (newestUnblocked != null)
                        ShowBanner(newestUnblocked);
                    else
                        NotifyUiChanged();
                });
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private static async Task<NotificationEntry?> ParseNotificationAsync(UserNotification notification)
        {
            try
            {
                string appId = string.Empty;
                string appName = "System";
                ImageSource? icon = null;

                var appInfo = notification.AppInfo;
                if (appInfo != null)
                {
                    appId = appInfo.AppUserModelId ?? string.Empty;
                    var displayName = appInfo.DisplayInfo?.DisplayName;
                    appName = !string.IsNullOrWhiteSpace(displayName)
                        ? displayName
                        : (!string.IsNullOrWhiteSpace(appId) ? appId : "System");

                    icon = await LoadAppIconAsync(appInfo);
                }

                var binding = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                var texts = binding?.GetTextElements()
                    .Select(t => t.Text?.Trim())
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Cast<string>()
                    .ToList() ?? new List<string>();

                string title = texts.Count > 0 ? texts[0] : appName;
                string body = texts.Count > 1 ? string.Join(" — ", texts.Skip(1)) : string.Empty;

                bool isAgentPerm = AgentPermissionHelper.IsPermissionRequest(appName, appId, title, body, out string agentKey);

                return new NotificationEntry
                {
                    Id = notification.Id,
                    AppId = appId,
                    AppName = appName,
                    Title = title,
                    Body = body,
                    Timestamp = notification.CreationTime,
                    AppIcon = icon,
                    IsAgentPermissionRequest = isAgentPerm,
                    AgentKey = agentKey
                };
            }
            catch
            {
                return null;
            }
        }

        private static async Task<ImageSource?> LoadAppIconAsync(Windows.ApplicationModel.AppInfo appInfo)
        {
            try
            {
                var logo = appInfo.DisplayInfo?.GetLogo(new Windows.Foundation.Size(32, 32));
                if (logo == null) return null;

                using var stream = await logo.OpenReadAsync();
                if (stream.Size == 0) return null;

                var reader = new DataReader(stream);
                await reader.LoadAsync((uint)stream.Size);
                var bytes = new byte[stream.Size];
                reader.ReadBytes(bytes);

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.DecodePixelWidth = 32;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private void ShowBanner(NotificationEntry entry)
        {
            _bannerItem = entry;
            StopBannerTimer();

            if (SettingsService.Current.NotificationSoundEnabled)
                NotchSound.PlayNotificationClick(SettingsService.Current.NotificationSoundPath);

            var disp = Application.Current?.Dispatcher;
            if (disp != null)
            {
                _bannerTimer = new DispatcherTimer(DispatcherPriority.Normal, disp)
                {
                    Interval = BannerDuration
                };
                _bannerTimer.Tick += (_, _) =>
                {
                    StopBannerTimer();
                    _bannerItem = null;
                    NotifyUiChanged();
                };
                _bannerTimer.Start();
            }

            NotifyUiChanged();
        }

        private void StopBannerTimer()
        {
            if (_bannerTimer == null) return;
            _bannerTimer.Stop();
            _bannerTimer = null;
        }

        private void NotifyUiChanged()
        {
            var disp = Application.Current?.Dispatcher;
            if (disp == null) return;

            if (!disp.CheckAccess())
            {
                disp.BeginInvoke(new Action(NotifyUiChanged));
                return;
            }

            RefreshExpandedContent();
            RaiseStateChanged();
        }

        // ---------- App-Filter ----------

        private static bool IsAppBlocked(NotificationEntry entry)
        {
            var blocked = SettingsService.Current.NotificationBlockedApps;
            if (blocked.Count == 0) return false;

            return blocked.Any(b =>
                string.Equals(b, entry.FilterKey, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(entry.AppId) &&
                 string.Equals(b, entry.AppId, StringComparison.OrdinalIgnoreCase)));
        }

        private void ToggleAppBlocked(string appKey)
        {
            var updated = SettingsService.Clone();
            var blocked = updated.NotificationBlockedApps;
            int idx = blocked.FindIndex(b => string.Equals(b, appKey, StringComparison.OrdinalIgnoreCase));

            if (idx >= 0)
            {
                blocked.RemoveAt(idx);
            }
            else
            {
                blocked.Add(appKey);
                _history.RemoveAll(h => string.Equals(h.FilterKey, appKey, StringComparison.OrdinalIgnoreCase));
                if (_bannerItem != null && string.Equals(_bannerItem.FilterKey, appKey, StringComparison.OrdinalIgnoreCase))
                {
                    StopBannerTimer();
                    _bannerItem = null;
                }
            }

            SettingsService.Update(updated);
            NotifyUiChanged();
        }

        private void RemoveHistoryItem(NotificationEntry entry)
        {
            _history.RemoveAll(h => h.Id == entry.Id);
            if (_bannerItem?.Id == entry.Id)
            {
                StopBannerTimer();
                _bannerItem = null;
            }

            try
            {
                if (_accessStatus == UserNotificationListenerAccessStatus.Allowed)
                    _listener?.RemoveNotification(entry.Id);
            }
            catch { /* Ignorieren falls System-Benachrichtigung bereits entfernt wurde */ }

            NotifyUiChanged();
        }

        private void ToggleNotificationSound()
        {
            var updated = SettingsService.Clone();
            updated.NotificationSoundEnabled = !updated.NotificationSoundEnabled;
            SettingsService.Update(updated);

            if (updated.NotificationSoundEnabled)
                NotchSound.PlayNotificationClick(updated.NotificationSoundPath);

            // Header neu aufbauen, damit der Ton-Button-Text aktualisiert wird
            NotifyUiChanged();
        }

        private void PickCustomSound()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L.T("notifications.pick_sound_title"),
                Filter = L.T("notifications.wav_filter"),
                CheckFileExists = true
            };

            if (dlg.ShowDialog() == true)
            {
                var updated = SettingsService.Clone();
                updated.NotificationSoundPath = dlg.FileName;
                updated.NotificationSoundEnabled = true;
                SettingsService.Update(updated);
                NotchSound.PlayNotificationClick(updated.NotificationSoundPath);
                NotifyUiChanged();
            }
        }

        private void ResetCustomSound()
        {
            if (string.IsNullOrEmpty(SettingsService.Current.NotificationSoundPath)) return;
            var updated = SettingsService.Clone();
            updated.NotificationSoundPath = string.Empty;
            SettingsService.Update(updated);
            if (updated.NotificationSoundEnabled)
                NotchSound.PlayNotificationClick();
            NotifyUiChanged();
        }

        private void ClearHistory()
        {
            _history.Clear();
            StopBannerTimer();
            _bannerItem = null;
            NotifyUiChanged();
        }

    }
}
