using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace WindowsNotch.Services
{
    /// <summary>
    /// Start mit Windows: nutzt bei MSIX-Store-Paketen die offizielle WinRT StartupTask-API,
    /// ansonsten den Registry-Schlüssel des aktuellen Benutzers (keine Adminrechte nötig).
    /// </summary>
    public static class AutoStartService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "WindowsNotch";
        private const string StartupTaskId = "FreeNotchStartup";
        private const int AppModelErrorNoPackage = 15700;

        public static bool IsPackaged
        {
            get
            {
                int length = 0;
                int result = NativeMethods.GetCurrentPackageFullName(ref length, IntPtr.Zero);
                return result != AppModelErrorNoPackage;
            }
        }

        public static bool IsEnabled()
        {
            if (IsPackaged)
            {
                try
                {
                    var state = Task.Run(async () =>
                    {
                        var task = await StartupTask.GetAsync(StartupTaskId);
                        return task.State;
                    }).GetAwaiter().GetResult();
                    return state == StartupTaskState.Enabled || state == StartupTaskState.EnabledByPolicy;
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                    return false;
                }
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }

        public static void Set(bool enabled)
        {
            if (IsPackaged)
            {
                try
                {
                    Task.Run(async () =>
                    {
                        var task = await StartupTask.GetAsync(StartupTaskId);
                        if (enabled)
                        {
                            if (task.State == StartupTaskState.Disabled)
                                await task.RequestEnableAsync();
                        }
                        else if (task.State == StartupTaskState.Enabled)
                        {
                            task.Disable();
                        }
                    }).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                }
                return;
            }

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
                exe = Path.Combine(AppContext.BaseDirectory, "FreeNotch.exe");
            return exe;
        }
    }
}
