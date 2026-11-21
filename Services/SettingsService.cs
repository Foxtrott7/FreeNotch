using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsNotch.Core;

namespace WindowsNotch.Services
{
    public static class SettingsService
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsNotch");

        private static string FilePath => Path.Combine(Dir, "settings.json");

        public static AppSettings Current { get; private set; } = new();

        public static bool IsFirstRun => !Current.HasCompletedOnboarding;

        public static event Action? Changed;

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
                    if (loaded != null) Current = loaded;
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex); // Kaputte Datei: mit Standardwerten weiterarbeiten
            }
            Current.Normalize();
        }

        /// <summary>Übernimmt neue Einstellungen, speichert sie und informiert die App.</summary>
        public static void Update(AppSettings settings)
        {
            Current = settings;
            Current.Normalize();
            Save();
            Changed?.Invoke();
        }

        /// <summary>Ändert Einstellungen im laufenden Betrieb und speichert, ohne die Fenster neu aufzubauen.</summary>
        public static void UpdateQuiet(Action<AppSettings> mutate)
        {
            mutate(Current);
            Current.Normalize();
            Save();
        }

        public static AppSettings Clone() => Clone(Current);

        public static AppSettings Clone(AppSettings source)
            => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(source, Options), Options)!;

        public static bool ExportToFile(string filePath)
        {
            try
            {
                File.WriteAllText(filePath, JsonSerializer.Serialize(Current, Options));
                return true;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return false;
            }
        }

        public static bool ImportFromFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath), Options);
                if (loaded == null) return false;
                loaded.HasCompletedOnboarding = true;
                Update(loaded);
                return true;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return false;
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options));
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }
    }
}
