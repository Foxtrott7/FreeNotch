using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowsNotch.Services
{
    public enum FullscreenKind { None, ExclusiveGame, Borderless, Video, Presentation }

    /// <param name="MonitorDevice">Gerätename des betroffenen Monitors, z. B. \\.\DISPLAY1</param>
    public readonly record struct FullscreenInfo(FullscreenKind Kind, string MonitorDevice, string ProcessName)
    {
        public static readonly FullscreenInfo None = new(FullscreenKind.None, "", "");
    }

    /// <summary>
    /// Erkennt Vollbild des Vordergrundfensters:
    /// - SHQueryUserNotificationState für exklusive Vollbildspiele und Präsentationsmodus,
    /// - Fenstergeometrie (deckt den ganzen Monitor, ohne Titelleiste) für randloses Vollbild und Videos.
    /// </summary>
    public static class FullscreenDetector
    {
        private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
        private const int QUNS_PRESENTATION_MODE = 4;

        private static readonly HashSet<string> VideoApps = new(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "msedge", "firefox", "brave", "opera", "vivaldi",
            "vlc", "mpv", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "potplayer", "potplayermini64",
            "video.ui", "plex"
        };

        private static readonly HashSet<string> PresentationApps = new(StringComparer.OrdinalIgnoreCase)
        {
            "powerpnt", "impress", "soffice"
        };

        private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"
        };

        public static FullscreenInfo Detect()
        {
            try
            {
                NativeMethods.SHQueryUserNotificationState(out int state);

                IntPtr hwnd = NativeMethods.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return FullscreenInfo.None;

                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == (uint)Environment.ProcessId) return FullscreenInfo.None;

                var cls = new StringBuilder(256);
                NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
                if (ShellClasses.Contains(cls.ToString())) return FullscreenInfo.None;

                IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                var mi = new NativeMethods.MONITORINFOEX
                {
                    cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
                };
                if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref mi))
                    return FullscreenInfo.None;

                bool covers = false;
                if (NativeMethods.GetWindowRect(hwnd, out var r))
                {
                    covers = r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                             r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
                }

                int style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
                bool hasCaption = (style & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION;
                bool borderlessFull = covers && !hasCaption;

                bool exclusive = state == QUNS_RUNNING_D3D_FULL_SCREEN;
                bool presentationMode = state == QUNS_PRESENTATION_MODE;

                if (!exclusive && !presentationMode && !borderlessFull)
                    return FullscreenInfo.None; // Normalfall: kein Prozessnamen-Lookup nötig

                string proc = GetProcessName(pid);

                if (presentationMode) return new(FullscreenKind.Presentation, mi.szDevice, proc);
                if (exclusive) return new(FullscreenKind.ExclusiveGame, mi.szDevice, proc);
                if (PresentationApps.Contains(proc)) return new(FullscreenKind.Presentation, mi.szDevice, proc);
                if (VideoApps.Contains(proc)) return new(FullscreenKind.Video, mi.szDevice, proc);
                return new(FullscreenKind.Borderless, mi.szDevice, proc);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return FullscreenInfo.None;
            }
        }

        private static string GetProcessName(uint pid)
        {
            try { return Process.GetProcessById((int)pid).ProcessName; }
            catch { return ""; }
        }
    }
}
