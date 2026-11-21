using System;
using Microsoft.Win32;

namespace WindowsNotch.Services
{
    /// <summary>
    /// Prüft über den Windows CapabilityAccessManager, ob aktuell eine Kamera aktiv genutzt wird.
    /// </summary>
    internal static class PrivacyIndicatorService
    {
        private const string WebcamConsentPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

        private static bool _loggedError;

        public static bool IsCameraActive() => GetActiveCameraApp() != null;

        public static string? GetActiveCameraApp()
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var root = hive.OpenSubKey(WebcamConsentPath);
                    if (root == null) continue;

                    string? app = FindActiveSubKey(root, isNonPackaged: false);
                    if (app != null) return app;

                    using var nonPackaged = root.OpenSubKey("NonPackaged");
                    if (nonPackaged != null)
                    {
                        app = FindActiveSubKey(nonPackaged, isNonPackaged: true);
                        if (app != null) return app;
                    }
                }
                catch (Exception ex)
                {
                    // Einmalig loggen, damit der 1,5-s-Timer bei gesperrtem HKLM-Key error.log nicht flutet
                    if (!_loggedError)
                    {
                        _loggedError = true;
                        App.LogError(ex);
                    }
                }
            }

            return null;
        }

        private static string? FindActiveSubKey(RegistryKey parent, bool isNonPackaged)
        {
            foreach (string name in parent.GetSubKeyNames())
            {
                if (!isNonPackaged && string.Equals(name, "NonPackaged", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    using var sub = parent.OpenSubKey(name);
                    if (sub == null) continue;
                    if (sub.GetValue("LastUsedTimeStart") is long start && start > 0)
                    {
                        object? stopVal = sub.GetValue("LastUsedTimeStop");
                        if (stopVal is null || (stopVal is long stop && (stop == 0 || stop < start)))
                            return CleanAppName(name, isNonPackaged);
                    }
                }
                catch { }
            }
            return null;
        }

        private static string CleanAppName(string subKeyName, bool isNonPackaged)
        {
            string name = subKeyName;
            if (isNonPackaged)
            {
                int sep = name.LastIndexOfAny(new[] { '#', '\\', '/' });
                if (sep >= 0 && sep + 1 < name.Length)
                    name = name[(sep + 1)..];
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    name = name[..^4];
                return string.IsNullOrWhiteSpace(name) ? subKeyName : name;
            }

            // Store-/MSIX-Paketfamiliennamen enden auf _<13-Zeichen-Publisher-ID>
            int underscore = name.LastIndexOf('_');
            if (underscore > 0 && name.Length - underscore - 1 == 13)
                name = name[..underscore];

            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < name.Length && name.Length - dot - 1 > 2)
                name = name[(dot + 1)..];

            return string.IsNullOrWhiteSpace(name) ? subKeyName : name;
        }
    }
}
