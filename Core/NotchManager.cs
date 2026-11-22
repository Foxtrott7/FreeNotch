using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using WindowsNotch.Services;
using WindowsNotch.UI;
using Forms = System.Windows.Forms;

namespace WindowsNotch.Core
{
    /// <summary>
    /// Zentrale Steuerung: erzeugt je Monitor eine Notch, verwaltet Tray-Symbol,
    /// globale Tastenkombinationen, Vollbildregeln und Einstellungen.
    /// </summary>
    public sealed class NotchManager : IDisposable
    {
        private const int HotkeyToggle = 0x4E01;
        private const int HotkeyHide = 0x4E02;

        private readonly List<NotchWindow> _windows = new();
        private readonly ModuleHost _moduleHost = new();
        private DispatcherTimer? _timer;
        private HwndSource? _messageWindow;
        private Forms.NotifyIcon? _tray;
        private System.Drawing.Icon? _trayIcon;
        private Forms.ToolStripMenuItem? _toggleItem;
        private Forms.ToolStripMenuItem? _hideItem;
        private Forms.ToolStripMenuItem? _settingsItem;
        private Forms.ToolStripMenuItem? _exitItem;
        private bool _manualHidden;
        private bool _captureSuppressed;

        public ModuleHost ModuleHost => _moduleHost;

        public void Start()
        {
            SettingsService.Load();
            LocalizationService.SetLanguage(SettingsService.Current.Language);

            if (SettingsService.Current.AutoStart != AutoStartService.IsEnabled())
                AutoStartService.Set(SettingsService.Current.AutoStart);

            _moduleHost.UpdateActiveModules(SettingsService.Current.Modules);

            SystemMetricsService.Instance.NeedsGpu = () =>
                SettingsService.Current.CollapsedLayout.ContainsKey(NotchInfo.Gpu) ||
                SettingsService.Current.HoverLayout.ContainsKey(NotchInfo.Gpu);
            SystemMetricsService.Instance.Start();

            // Unsichtbares Nachrichtenfenster für Hotkeys
            var p = new HwndSourceParameters("WindowsNotchMessages")
            {
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE
                Width = 0,
                Height = 0
            };
            _messageWindow = new HwndSource(p);
            _messageWindow.AddHook(WndProc);
            RegisterHotkeys();

            CreateTray();
            RebuildWindows();

            if (SettingsService.IsFirstRun)
                Application.Current.Dispatcher.BeginInvoke(OpenOnboarding);

            SettingsService.Changed += OnSettingsChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            // Vollbildprüfung: einmal pro Sekunde, sehr geringe Last
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        // ---------- Fenster ----------

        private static List<Forms.Screen> ResolveTargetScreens(AppSettings s)
        {
            var all = Forms.Screen.AllScreens;
            var primary = Forms.Screen.PrimaryScreen ?? all[0];

            IEnumerable<Forms.Screen> targets = s.ShowOnAllMonitors
                ? all.Where(sc => !s.Monitors.TryGetValue(sc.DeviceName, out var m) || m.Enabled)
                : new[] { primary };

            var list = targets.ToList();
            if (list.Count == 0) list.Add(primary);
            return list;
        }

        private void RebuildWindows()
        {
            foreach (var w in _windows) w.Close();
            _windows.Clear();

            foreach (var screen in ResolveTargetScreens(SettingsService.Current))
            {
                var w = new NotchWindow(screen, _moduleHost);
                _windows.Add(w);
                w.Show();
            }

            UpdateVisibility();
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
            => Application.Current.Dispatcher.Invoke(RebuildWindows);

        private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
            => Application.Current?.Dispatcher.BeginInvoke(RefreshTrayIcon);

        private void OnSettingsChanged()
        {
            LocalizationService.SetLanguage(SettingsService.Current.Language);
            RegisterHotkeys();
            AutoStartService.Set(SettingsService.Current.AutoStart);
            _moduleHost.UpdateActiveModules(SettingsService.Current.Modules);
            RefreshTrayIcon();

            var targetDevices = ResolveTargetScreens(SettingsService.Current).Select(sc => sc.DeviceName).ToList();
            bool sameWindows = _windows.Count == targetDevices.Count &&
                               _windows.Select(w => w.DeviceName).SequenceEqual(targetDevices);

            if (sameWindows)
            {
                foreach (var w in _windows) w.RefreshFromSettings();
                UpdateVisibility();
                return;
            }

            bool wasSettingsOpen = _windows.Any(w => w.IsSettingsOpen);
            RebuildWindows();
            if (wasSettingsOpen && _windows.Count > 0)
                _windows[0].OpenSettings();
        }

        // ---------- Sichtbarkeit und Vollbild ----------

        private void Tick()
        {
            UpdateVisibility();
            foreach (var w in _windows) w.KeepOnTop();
        }

        private void UpdateVisibility()
        {
            var info = _manualHidden ? FullscreenInfo.None : FullscreenDetector.Detect();
            bool rule = ShouldHide(info);

            foreach (var w in _windows)
            {
                bool onAffectedMonitor = rule &&
                    string.Equals(w.DeviceName, info.MonitorDevice, StringComparison.OrdinalIgnoreCase);
                w.SetSuppressed(_manualHidden || _captureSuppressed || onAffectedMonitor, instant: _captureSuppressed);
            }
        }

        /// <summary>Blendet alle Notch-Fenster während einer Bildschirm-Auswahl aus und öffnet danach optional ein Modul.</summary>
        public void SetCaptureSuppressed(bool suppressed, Forms.Screen? openOnScreen = null, string? openModuleId = null)
        {
            _captureSuppressed = suppressed;
            UpdateVisibility();

            if (!suppressed && !string.IsNullOrEmpty(openModuleId))
            {
                var target = openOnScreen != null
                    ? _windows.FirstOrDefault(x => !x.IsSuppressed &&
                        string.Equals(x.DeviceName, openOnScreen.DeviceName, StringComparison.OrdinalIgnoreCase))
                    : null;
                target ??= _windows.FirstOrDefault(x => !x.IsSuppressed);
                target?.OpenModule(openModuleId);
            }
        }

        private static bool ShouldHide(FullscreenInfo info)
        {
            if (info.Kind == FullscreenKind.None) return false;

            var s = SettingsService.Current;
            string name = info.ProcessName;
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

            if (s.FullscreenExcludedApps.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)))
                return false;

            return info.Kind switch
            {
                FullscreenKind.ExclusiveGame => s.HideOnExclusiveGame,
                FullscreenKind.Borderless => s.HideOnBorderless,
                FullscreenKind.Video => s.HideOnVideo,
                FullscreenKind.Presentation => s.HideOnPresentation,
                _ => false
            };
        }

        // ---------- Aktionen ----------

        public void Toggle()
        {
            string device = Forms.Screen.FromPoint(Forms.Cursor.Position).DeviceName;
            var w = _windows.FirstOrDefault(x => !x.IsSuppressed &&
                        string.Equals(x.DeviceName, device, StringComparison.OrdinalIgnoreCase))
                    ?? _windows.FirstOrDefault(x => !x.IsSuppressed);
            w?.Toggle();
        }

        public void ShowSettings()
        {
            string device = Forms.Screen.FromPoint(Forms.Cursor.Position).DeviceName;
            var w = _windows.FirstOrDefault(x => !x.IsSuppressed &&
                        string.Equals(x.DeviceName, device, StringComparison.OrdinalIgnoreCase))
                    ?? _windows.FirstOrDefault(x => !x.IsSuppressed)
                    ?? _windows.FirstOrDefault();
            w?.OpenSettings();
        }

        public void OpenOnboarding()
        {
            var w = _windows.FirstOrDefault(x => !x.IsSuppressed) ?? _windows.FirstOrDefault();
            w?.OpenOnboarding();
        }

        // ---------- Tray ----------

        private void CreateTray()
        {
            var menu = new Forms.ContextMenuStrip();
            _toggleItem = new Forms.ToolStripMenuItem(L.T("tray.toggle"), null, (_, _) => Dispatch(Toggle));
            _hideItem = new Forms.ToolStripMenuItem(L.T("tray.hide")) { CheckOnClick = true };
            _hideItem.CheckedChanged += (_, _) => Dispatch(() =>
            {
                _manualHidden = _hideItem!.Checked;
                UpdateVisibility();
                RefreshTrayIcon();
            });
            _settingsItem = new Forms.ToolStripMenuItem(L.T("tray.settings"), null, (_, _) => Dispatch(ShowSettings));
            _exitItem = new Forms.ToolStripMenuItem(L.T("tray.exit"), null, (_, _) => Dispatch(() => Application.Current.Shutdown()));

            menu.Items.Add(_toggleItem);
            menu.Items.Add(_hideItem);
            menu.Items.Add(_settingsItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(_exitItem);

            LocalizationService.LanguageChanged += OnLanguageChanged;

            _tray = new Forms.NotifyIcon
            {
                Text = "FreeNotch",
                ContextMenuStrip = menu
            };
            RefreshTrayIcon();
            _tray.Visible = true;
            _tray.MouseClick += (_, args) =>
            {
                if (args.Button == Forms.MouseButtons.Left) Dispatch(Toggle);
            };
        }

        private void OnLanguageChanged() => Dispatch(() =>
        {
            if (_toggleItem != null) _toggleItem.Text = L.T("tray.toggle");
            if (_hideItem != null) _hideItem.Text = L.T("tray.hide");
            if (_settingsItem != null) _settingsItem.Text = L.T("tray.settings");
            if (_exitItem != null) _exitItem.Text = L.T("tray.exit");
        });

        private void RefreshTrayIcon()
        {
            if (_tray == null) return;
            System.Drawing.Icon? next = null;
            try
            {
                next = TrayIconRenderer.CreateTrayIcon(NotchDesign.AccentPrimary, _manualHidden);
                var old = _trayIcon;
                _tray.Icon = next;
                _trayIcon = next;
                next = null;
                old?.Dispose();
            }
            catch (Exception ex)
            {
                next?.Dispose();
                App.LogError(ex);
            }
        }

        private static void Dispatch(Action action) => Application.Current.Dispatcher.Invoke(action);

        // ---------- Hotkeys ----------

        private void RegisterHotkeys()
        {
            if (_messageWindow == null) return;
            IntPtr h = _messageWindow.Handle;
            NativeMethods.UnregisterHotKey(h, HotkeyToggle);
            NativeMethods.UnregisterHotKey(h, HotkeyHide);

            if (!SettingsService.Current.HotkeyEnabled) return;

            uint mods = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT;
            NativeMethods.RegisterHotKey(h, HotkeyToggle, mods, (uint)KeyInterop.VirtualKeyFromKey(Key.N));
            NativeMethods.RegisterHotKey(h, HotkeyHide, mods, (uint)KeyInterop.VirtualKeyFromKey(Key.H));
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HotkeyToggle)
                {
                    Toggle();
                    handled = true;
                }
                else if (id == HotkeyHide && _hideItem != null)
                {
                    _hideItem.Checked = !_hideItem.Checked;
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        // ---------- Aufräumen ----------

        public void Dispose()
        {
            SettingsService.Changed -= OnSettingsChanged;
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _timer?.Stop();

            if (_messageWindow != null)
            {
                NativeMethods.UnregisterHotKey(_messageWindow.Handle, HotkeyToggle);
                NativeMethods.UnregisterHotKey(_messageWindow.Handle, HotkeyHide);
                _messageWindow.RemoveHook(WndProc);
                _messageWindow.Dispose();
            }

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            _trayIcon?.Dispose();
            _trayIcon = null;

            foreach (var w in _windows) w.Close();
            _windows.Clear();
            _moduleHost.Dispose();
        }
    }
}
