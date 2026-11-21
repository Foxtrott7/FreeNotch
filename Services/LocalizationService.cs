using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace WindowsNotch.Services
{
    public sealed record LanguageOption(string Code, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// Zentraler Lokalisierungsdienst (Deutsch &amp; Englisch) mit Embedded-JSON-Ressourcen und WPF-Live-Binding.
    /// </summary>
    public sealed class LocalizationService : INotifyPropertyChanged
    {
        public static LocalizationService Instance { get; } = new();

        public static IReadOnlyList<LanguageOption> SupportedLanguages { get; } = new LanguageOption[]
        {
            new("de", "Deutsch"),
            new("en", "English"),
        };

        private static readonly HashSet<string> SupportedCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "de", "en"
        };

        private static readonly Dictionary<string, Dictionary<string, string>> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> _current = new(StringComparer.Ordinal);
        private static Dictionary<string, string> _fallbackEn = new(StringComparer.Ordinal);
        private static Dictionary<string, string> _fallbackDe = new(StringComparer.Ordinal);

        public static string CurrentLanguage { get; private set; } = "de";
        public static event Action? LanguageChanged;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string this[string key] => Get(key);

        static LocalizationService()
        {
            _fallbackDe = LoadDictionary("de");
            _fallbackEn = LoadDictionary("en");
            _current = _fallbackDe;
        }

        public static string NormalizeCode(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "de";
            string trimmed = code.Trim().ToLowerInvariant();
            if (trimmed.Contains('-')) trimmed = trimmed.Split('-')[0];
            return SupportedCodes.Contains(trimmed) ? trimmed : "de";
        }

        public static void SetLanguage(string? code)
        {
            string normalized = NormalizeCode(code);
            if (string.Equals(CurrentLanguage, normalized, StringComparison.OrdinalIgnoreCase) && _current.Count > 0)
                return;

            CurrentLanguage = normalized;
            _current = LoadDictionary(normalized);

            try
            {
                var culture = CultureInfo.GetCultureInfo(normalized);
                CultureInfo.CurrentUICulture = culture;
            }
            catch { /* unbekannte Kultur ignorieren */ }

            Instance.NotifyChanged();
            LanguageChanged?.Invoke();
        }

        public static string Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            if (_current.TryGetValue(key, out string? val) && !string.IsNullOrEmpty(val)) return val;
            if (_fallbackEn.TryGetValue(key, out val) && !string.IsNullOrEmpty(val)) return val;
            if (_fallbackDe.TryGetValue(key, out val) && !string.IsNullOrEmpty(val)) return val;
            return key;
        }

        public static string Format(string key, params object[] args)
        {
            string pattern = Get(key);
            try { return string.Format(CultureInfo.CurrentUICulture, pattern, args); }
            catch { return pattern; }
        }

        private void NotifyChanged()
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));

        private static Dictionary<string, string> LoadDictionary(string code)
        {
            if (Cache.TryGetValue(code, out var cached)) return cached;

            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string resName = $"WindowsNotch.Resources.Lang.{code}.json";
                using Stream? stream = asm.GetManifestResourceStream(resName);
                if (stream != null)
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
                    if (parsed != null) dict = new Dictionary<string, string>(parsed, StringComparer.Ordinal);
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }

            Cache[code] = dict;
            return dict;
        }
    }

    /// <summary>Kompakter Kurzhelfer für C#-Code: L.T("key") und L.F("key", arg).</summary>
    public static class L
    {
        public static string T(string key) => LocalizationService.Get(key);
        public static string F(string key, params object[] args) => LocalizationService.Format(key, args);
    }
}
