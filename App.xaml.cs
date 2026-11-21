using System;
using System.IO;
using System.Threading;
using System.Windows;
using WindowsNotch.Core;

namespace WindowsNotch
{
    public partial class App : Application
    {
        private Mutex? _singleInstance;

        public NotchManager? Manager { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _singleInstance = new Mutex(true, "WindowsNotch.SingleInstance", out bool isNew);
            if (!isNew)
            {
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (_, args) =>
            {
                LogError(args.Exception);
                args.Handled = true; // Ein Fehler soll nicht die ganze App beenden
            };

            base.OnStartup(e);

            Manager = new NotchManager();
            Manager.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Manager?.Dispose();
            _singleInstance?.Dispose();
            base.OnExit(e);
        }

        public static void LogError(Exception ex)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsNotch");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { /* Logging darf nie selbst crashen */ }
        }
    }
}
