using System;
using System.IO;
using Microsoft.Win32;

namespace WindowsNotch.Services
{
    /// <summary>Start mit Windows über den Registry-Schlüssel des aktuellen Benutzers (keine Adminrechte nötig).</summary>
    public static class AutoStartService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "WindowsNotch";

        public static bool IsEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }

        public static void Set(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                                ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (enabled)
                    key.SetValue(ValueName, "\"" + GetExePath() + "\"");
                else
                    key.DeleteValue(ValueName, false);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private static string GetExePath()
        {
            string? exe = Environment.ProcessPath;
            // Bei "dotnet run" ist der Prozess dotnet.exe, dann die eigene EXE verwenden
            if (exe == null || Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                exe = Path.Combine(AppContext.BaseDirectory, "WindowsNotch.exe");
            return exe;
        }
    }
}
