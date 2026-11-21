using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using WindowsNotch.Modules.Bluetooth;
using WindowsNotch.Modules.Clipboard;
using WindowsNotch.Modules.Downloads;
using WindowsNotch.Modules.FileTray;
using WindowsNotch.Modules.Finance;
using WindowsNotch.Modules.Generic;
using WindowsNotch.Modules.Media;
using WindowsNotch.Modules.Notifications;
using WindowsNotch.Modules.PhoneTransfer;
using WindowsNotch.Modules.QuickActions;
using WindowsNotch.Modules.ScreenCapture;
using WindowsNotch.Modules.ScreenOcr;
using WindowsNotch.Modules.Weather;
using WindowsNotch.Modules.Widgets;

namespace WindowsNotch.Core
{
    /// <summary>
    /// Zentraler Host und Registry für alle Notch-Module.
    /// Verwaltet den Lebenszyklus (Start/Stop), aggregiert Prioritäten für Live-Aktivitäten
    /// und isoliert Fehler via App.LogError.
    /// </summary>
    public sealed class ModuleHost : IDisposable
    {
        private readonly List<INotchModule> _allModules = new();
        private readonly List<INotchModule> _activeModules = new();
        private readonly object _lock = new();

        public event Action? ActiveModulesChanged;
        public event Action? StateChanged;
        public event Action<INotchModule>? CollapseRequested;

        public IReadOnlyList<INotchModule> AllModules
        {
            get { lock (_lock) return _allModules.ToList(); }
        }

        public IReadOnlyList<INotchModule> ActiveModules
        {
            get { lock (_lock) return _activeModules.ToList(); }
        }

        /// <summary>
        /// Liefert das aktive Modul mit der derzeit höchsten Priorität für die Kapsel-Vorschau.
        /// </summary>
        public INotchModule? PrimaryModule
        {
            get
            {
                lock (_lock)
                {
                    return _activeModules
                        .OrderByDescending(m => SafeGetPriority(m))
                        .FirstOrDefault();
                }
            }
        }

        public ModuleHost()
        {
            RegisterDefaultModules();
        }

        private void RegisterDefaultModules()
        {
            // Reihenfolge der Icons in der Notchify-Toolbar: Dashboard zuerst, dann Ablage, Verlauf, Mitteilungen, Medien, Aktionen, Tools
            RegisterModule(new WidgetsModule());
            RegisterModule(new FileTrayModule());
            RegisterModule(new ClipboardModule());
            RegisterModule(new MediaModule());
            RegisterModule(new NotificationsModule());
            RegisterModule(new QuickActionsModule());
            RegisterModule(new ScreenOcrModule());
            RegisterModule(new ScreenCaptureModule());
            RegisterModule(new BluetoothModule());
            RegisterModule(new PhoneTransferModule());
            RegisterModule(new DownloadsModule());
            RegisterModule(new FinanceModule());
            RegisterModule(new WeatherModule());

            // Für alle weiteren Katalog-Einträge einen generischen Platzhalter bereitstellen
            foreach (var info in ModuleCatalog.All)
            {
                if (!_allModules.Any(m => m.Id == info.Id))
                {
                    RegisterModule(new GenericNotchModule(info.Id, info.Label));
                }
            }
        }

        public void RegisterModule(INotchModule module)
        {
            lock (_lock)
            {
                if (_allModules.Any(m => m.Id == module.Id))
                    return;

                _allModules.Add(module);
                module.StateChanged += OnModuleStateChanged;
                module.CollapseRequested += OnModuleCollapseRequested;
            }
        }

        /// <summary>
        /// Aktiviert oder stoppt Module anhand der aktuellen Konfiguration in AppSettings.Modules.
        /// </summary>
        public void UpdateActiveModules(Dictionary<string, bool> moduleSettings)
        {
            bool changed = false;

            lock (_lock)
            {
                foreach (var module in _allModules)
                {
                    bool shouldBeActive = moduleSettings.TryGetValue(module.Id, out bool on) && on;

                    if (shouldBeActive && !module.IsActive)
                    {
                        SafeStart(module);
                        if (!_activeModules.Contains(module))
                        {
                            _activeModules.Add(module);
                        }
                        changed = true;
                    }
                    else if (!shouldBeActive && module.IsActive)
                    {
                        SafeStop(module);
                        _activeModules.Remove(module);
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                ActiveModulesChanged?.Invoke();
            }
        }

        /// <summary>
        /// Erzeugt sicher eine Live-View eines Moduls für den Ruhezustand mit Fehlerisolation.
        /// </summary>
        public FrameworkElement? SafeCreateLiveView(INotchModule module)
        {
            try
            {
                return module.CreateLiveView();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return null;
            }
        }

        /// <summary>
        /// Erzeugt sicher eine Preview-View eines Moduls mit Fehlerisolation.
        /// </summary>
        public FrameworkElement? SafeCreatePreviewView(INotchModule module)
        {
            try
            {
                return module.CreatePreviewView();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return null;
            }
        }

        /// <summary>
        /// Erzeugt sicher eine Expanded-View eines Moduls mit Fehlerisolation.
        /// </summary>
        public FrameworkElement? SafeCreateExpandedView(INotchModule module)
        {
            try
            {
                return module.CreateExpandedView();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return null;
            }
        }

        private static void SafeStart(INotchModule module)
        {
            try
            {
                module.IsActive = true;
                module.Start();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private static void SafeStop(INotchModule module)
        {
            try
            {
                module.IsActive = false;
                module.Stop();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private static int SafeGetPriority(INotchModule module)
        {
            try
            {
                return module.Priority;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return 0;
            }
        }

        private void OnModuleStateChanged(object? sender, EventArgs e)
        {
            StateChanged?.Invoke();
        }

        private void OnModuleCollapseRequested(object? sender, EventArgs e)
        {
            if (sender is INotchModule module)
                CollapseRequested?.Invoke(module);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var module in _allModules)
                {
                    module.StateChanged -= OnModuleStateChanged;
                    module.CollapseRequested -= OnModuleCollapseRequested;
                    SafeStop(module);
                    try
                    {
                        module.Dispose();
                    }
                    catch (Exception ex)
                    {
                        App.LogError(ex);
                    }
                }
                _allModules.Clear();
                _activeModules.Clear();
            }
        }
    }
}
