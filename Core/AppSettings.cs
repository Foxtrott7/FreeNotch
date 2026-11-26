using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowsNotch.Core
{
    public enum ProfileType { Work, Gaming, Complete }

    public enum InteractionMode { HoverAndClick, HoverOnly, ClickOnly }

    public enum IdleVisibilityMode { AlwaysVisible, PeekWhenEmpty, AlwaysPeek }

    public enum NotchTheme { Dark, DarkGlass, Light, LightGlass }

    public enum NotchStyle { Docked, Floating }

    public enum NotchPosition
    {
        TopLeft,
        TopCenter,
        TopRight,
        LeftCenter,
        RightCenter,
        BottomLeft,
        BottomCenter,
        BottomRight
    }

    public class MonitorSetting
    {
        public bool Enabled { get; set; } = true;
        public bool IsGamingMonitor { get; set; }
    }

    /// <summary>Alle Einstellungen. Wird als JSON in %AppData%\WindowsNotch\settings.json gespeichert.</summary>
    public class AppSettings
    {
        // Allgemein
        public bool HasCompletedOnboarding { get; set; }
        public string Language { get; set; } = "de";
        public ProfileType Profile { get; set; } = ProfileType.Complete;
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsWorkEdition => Profile == ProfileType.Work;
        public bool AutoStart { get; set; }
        public bool HotkeyEnabled { get; set; } = true;
        public Dictionary<string, bool> Modules { get; set; } = new();

        // Darstellung (Größen in DIP)
        public NotchTheme Theme { get; set; } = NotchTheme.Dark;
        public NotchStyle Style { get; set; } = NotchStyle.Docked;
        public NotchPosition Position { get; set; } = NotchPosition.TopCenter;
        public string AccentThemeId { get; set; } = "indigo-violet";
        public bool UseCustomAccent { get; set; }
        public string CustomAccentStartHex { get; set; } = "#2822A8";
        public string CustomAccentEndHex { get; set; } = "#9333EA";
        public double CollapsedWidth { get; set; } = 172;
        public double CollapsedHeight { get; set; } = 32;
        public double ExpandedWidth { get; set; } = 540;
        public double ExpandedHeight { get; set; } = 340;
        public double CornerRadius { get; set; } = 22;
        public bool AnimationsEnabled { get; set; } = true;
        public double AnimationSpeedScale { get; set; } = 1.0;
        public int TargetFrameRate { get; set; } = 120;
        public bool AdaptiveFrameRate { get; set; } = true;

        // Verhalten
        public InteractionMode Mode { get; set; } = InteractionMode.HoverAndClick;
        public IdleVisibilityMode IdleVisibility { get; set; } = IdleVisibilityMode.PeekWhenEmpty;
        public double HoverDelayMs { get; set; } = 150;
        public bool CloseOnFocusLoss { get; set; } = true;

        // Vollbild
        public bool HideOnExclusiveGame { get; set; } = true;
        public bool HideOnBorderless { get; set; } = true;
        public bool HideOnVideo { get; set; } = true;
        public bool HideOnPresentation { get; set; } = true;
        /// <summary>Prozessnamen (ohne .exe), bei denen die Notch trotz Vollbild sichtbar bleibt.</summary>
        public List<string> FullscreenExcludedApps { get; set; } = new();

        // Benachrichtigungen
        /// <summary>App-Namen oder App-IDs, deren Benachrichtigungen im Benachrichtigungsmodul ausgeblendet werden.</summary>
        public List<string> NotificationBlockedApps { get; set; } = new();
        /// <summary>Bereits erkannte Benachrichtigungs-Apps für den App-Filter in den Einstellungen.</summary>
        public List<string> NotificationKnownApps { get; set; } = new();
        /// <summary>Spielt bei neuen Benachrichtigungen einen dezenten Notch-Klickton ab.</summary>
        public bool NotificationSoundEnabled { get; set; } = true;
        /// <summary>Optionaler Pfad zu einer eigenen .wav-Datei für Benachrichtigungen (leer = Standard-Notch-Ton).</summary>
        public string NotificationSoundPath { get; set; } = string.Empty;

        public static readonly string[] DefaultBentoTileOrder = { "lyrics", "media", "calendar", "clock" };
        public static readonly string[] AllBentoTileIds =
        {
            "lyrics", "media", "weather", "calendar",
            "todos", "notes", "clock", "timer",
            "stopwatch", "hardware", "finance", "carousel"
        };
        public static readonly string[] DefaultFinanceFavorites = { "BTC-USD", "ETH-USD", "AAPL", "NVDA" };
        public static readonly string[] DefaultQuickActionTileOrder = { "lock", "project", "calc", "taskmgr", "explorer", "audio" };
        public static readonly string[] DefaultQuickActionBarOrder = { "volume" };

        // Live-Activities (Anzeige im Ruhezustand ohne Hover)
        public bool LiveActivitiesEnabled { get; set; } = true;
        public Dictionary<string, bool> LiveActivityModules { get; set; } = new();
        /// <summary>Platz je Information im Ruhezustand (siehe NotchInfo / docs/NOTCH-LAYOUT-SPEC.md).</summary>
        public Dictionary<string, NotchSlot> CollapsedLayout { get; set; } = NotchInfo.DefaultCollapsed();
        /// <summary>Platz je Information in der Hover-Kapsel.</summary>
        public Dictionary<string, NotchSlot> HoverLayout { get; set; } = NotchInfo.DefaultHover();
        /// <summary>Modulare Reihenfolge der 2×2-Bento-Kacheln im Dashboard (Slot 0..3).</summary>
        public List<string> BentoTileOrder { get; set; } = new(DefaultBentoTileOrder);
        /// <summary>Frei konfigurierbare Seiten-IDs innerhalb des Custom-Karussells.</summary>
        public List<string> BentoCarouselPages { get; set; } = new();
        /// <summary>Modulare Reihenfolge der Modul-Icons in der oberen Leiste.</summary>
        public List<string> TopTabOrder { get; set; } = new();
        /// <summary>Aktive Kacheln im Control Center in ihrer Reihenfolge ("" = freier Raster-Slot).</summary>
        public List<string>? QuickActionTileOrder { get; set; }
        /// <summary>Aktive System-Leisten im Control Center (z. B. volume, quickcmd).</summary>
        public List<string>? QuickActionBarOrder { get; set; }
        /// <summary>System-Leisten oberhalb des Kachel-Rasters.</summary>
        public List<string>? QuickActionTopBars { get; set; }
        /// <summary>System-Leisten unterhalb des Kachel-Rasters.</summary>
        public List<string>? QuickActionBottomBars { get; set; }
        /// <summary>Ob die System-Leisten (Lautstärke etc.) oberhalb der Kacheln liegen.</summary>
        public bool QuickActionsVolumeTop { get; set; }

        // Monitore
        public bool ShowOnAllMonitors { get; set; }
        public Dictionary<string, MonitorSetting> Monitors { get; set; } = new();

        // Privatsphäre & Online-Opt-ins (standardmäßig alle deaktiviert)
        public bool ShowCameraIndicator { get; set; } = true;
        public bool OptInWeather { get; set; }
        public string WeatherCity { get; set; } = "Berlin";
        public List<string> WeatherCities { get; set; } = new();
        public int WeatherCityIndex { get; set; }
        public double? WeatherLatitude { get; set; }
        public double? WeatherLongitude { get; set; }
        public string WeatherResolvedCity { get; set; } = string.Empty;
        public bool OptInLyrics { get; set; }
        public bool OptInFavicons { get; set; }

        // Finanzen (Zero-Config Kurse & Favoriten)
        public List<string>? FinanceFavorites { get; set; }
        public int FinanceActiveIndex { get; set; }
        public string FinanceRange { get; set; } = "1D";

        public static List<string> ParseWeatherCities(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return new List<string>();
            return input
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim())
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string FormatWeatherCities(IEnumerable<string>? cities, string? fallbackCity = null)
        {
            var list = cities?
                .Select(c => c?.Trim() ?? string.Empty)
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
            if (list.Count == 0 && !string.IsNullOrWhiteSpace(fallbackCity))
                list.Add(fallbackCity.Trim());
            return string.Join(", ", list);
        }

        public void SetWeatherCities(IEnumerable<string> cities)
        {
            var previousCity = (WeatherCity ?? string.Empty).Trim();
            WeatherCities = cities
                .Select(c => c?.Trim() ?? string.Empty)
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (WeatherCities.Count == 0)
            {
                WeatherCity = string.Empty;
                WeatherCityIndex = 0;
                WeatherLatitude = null;
                WeatherLongitude = null;
                WeatherResolvedCity = string.Empty;
                return;
            }

            int preservedIdx = string.IsNullOrEmpty(previousCity)
                ? -1
                : WeatherCities.FindIndex(c => string.Equals(c, previousCity, StringComparison.OrdinalIgnoreCase));

            WeatherCityIndex = preservedIdx >= 0 ? preservedIdx : 0;
            WeatherCity = WeatherCities[WeatherCityIndex];

            if (!string.Equals(previousCity, WeatherCity, StringComparison.OrdinalIgnoreCase))
            {
                WeatherLatitude = null;
                WeatherLongitude = null;
                WeatherResolvedCity = string.Empty;
            }
        }

        public bool IsLiveActivityEnabled(string moduleId)
        {
            if (!LiveActivitiesEnabled) return false;
            return LiveActivityModules.TryGetValue(moduleId, out bool on)
                ? on
                : DefaultLiveActivity(moduleId);
        }

        /// <summary>Standard-Modus für "Auf Telefon übertragen": true = Online-Link, false = Lokales Netzwerk.</summary>
        public bool PhoneTransferOnlineDefault { get; set; } = true;

        public static bool DefaultLiveActivity(string moduleId)
            => moduleId is "media" or "notifications" or "widgets" or "downloads";

        /// <summary>Ergänzt fehlende Werte und begrenzt unsinnige Werte.</summary>
        public void Normalize()
        {
            Modules ??= new();
            LiveActivityModules ??= new();
            Monitors ??= new();
            FullscreenExcludedApps ??= new();
            NotificationBlockedApps ??= new();
            NotificationKnownApps ??= new();
            BentoTileOrder ??= new();
            TopTabOrder ??= new();
            QuickActionTileOrder = NormalizeQuickTileSlots(QuickActionTileOrder);
            QuickActionBarOrder = QuickActionBarOrder == null
                ? new List<string>(DefaultQuickActionBarOrder)
                : QuickActionBarOrder.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            if (QuickActionTopBars != null)
                QuickActionTopBars = QuickActionTopBars.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            if (QuickActionBottomBars != null)
                QuickActionBottomBars = QuickActionBottomBars.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();

            var normalizedBento = new List<string>(4);
            var seenBento = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rawId in BentoTileOrder)
            {
                if (normalizedBento.Count >= 4) break;
                string tileId = rawId == "planner" ? "calendar" : rawId;
                if (string.IsNullOrWhiteSpace(tileId))
                {
                    normalizedBento.Add(string.Empty);
                    continue;
                }
                if (Array.IndexOf(AllBentoTileIds, tileId) >= 0 && seenBento.Add(tileId))
                    normalizedBento.Add(tileId);
            }
            if (seenBento.Count == 0)
            {
                normalizedBento.Clear();
                normalizedBento.AddRange(DefaultBentoTileOrder);
            }
            else
            {
                while (normalizedBento.Count < 4)
                    normalizedBento.Add(string.Empty);
            }
            BentoTileOrder = normalizedBento;

            BentoCarouselPages ??= new();
            BentoCarouselPages = BentoCarouselPages
                .Where(id => !string.IsNullOrWhiteSpace(id) &&
                             id != "carousel" &&
                             Array.IndexOf(AllBentoTileIds, id) >= 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            WeatherCities ??= new();
            WeatherCities = WeatherCities
                .Select(c => c?.Trim() ?? string.Empty)
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (WeatherCities.Count == 0 && !string.IsNullOrWhiteSpace(WeatherCity))
                WeatherCities.Add(WeatherCity.Trim());
            if (WeatherCities.Count > 0)
            {
                WeatherCityIndex = ((WeatherCityIndex % WeatherCities.Count) + WeatherCities.Count) % WeatherCities.Count;
                if (string.IsNullOrWhiteSpace(WeatherCity) ||
                    !WeatherCities.Contains(WeatherCity.Trim(), StringComparer.OrdinalIgnoreCase))
                    WeatherCity = WeatherCities[WeatherCityIndex];
            }
            else
            {
                WeatherCityIndex = 0;
            }

            FinanceFavorites = FinanceFavorites == null
                ? new List<string>(DefaultFinanceFavorites)
                : FinanceFavorites
                    .Select(f => (f ?? string.Empty).Trim().ToUpperInvariant())
                    .Where(f => !string.IsNullOrEmpty(f))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (FinanceFavorites.Count > 0)
                FinanceActiveIndex = ((FinanceActiveIndex % FinanceFavorites.Count) + FinanceFavorites.Count) % FinanceFavorites.Count;
            else
                FinanceActiveIndex = 0;

            if (FinanceRange is not ("LIVE" or "1D" or "1W" or "1M" or "1Y" or "5Y" or "ALL"))
                FinanceRange = "1D";

            var validModuleIds = new HashSet<string>(ModuleCatalog.All.Count);
            foreach (var m in ModuleCatalog.All)
                validModuleIds.Add(m.Id);

            var normalizedTabs = new List<string>(ModuleCatalog.All.Count);
            foreach (var tabId in TopTabOrder)
            {
                if (!string.IsNullOrWhiteSpace(tabId) &&
                    validModuleIds.Contains(tabId) &&
                    !normalizedTabs.Contains(tabId))
                {
                    normalizedTabs.Add(tabId);
                }
            }
            foreach (var m in ModuleCatalog.All)
            {
                if (!normalizedTabs.Contains(m.Id))
                    normalizedTabs.Add(m.Id);
            }
            TopTabOrder = normalizedTabs;

            foreach (var m in ModuleCatalog.All)
                if (!Modules.ContainsKey(m.Id))
                    Modules[m.Id] = ModuleCatalog.DefaultEnabled(Profile, m);

            foreach (var id in new[] { "media", "notifications", "widgets", "downloads" })
                if (!LiveActivityModules.ContainsKey(id))
                    LiveActivityModules[id] = true;

            CollapsedWidth = Math.Clamp(CollapsedWidth, 100, 400);
            CollapsedHeight = Math.Clamp(CollapsedHeight, 20, 60);
            ExpandedWidth = Math.Clamp(ExpandedWidth, 300, 1000);
            ExpandedHeight = Math.Clamp(ExpandedHeight, 120, 600);
            CornerRadius = Math.Clamp(CornerRadius, 0, 40);
            HoverDelayMs = Math.Clamp(HoverDelayMs, 0, 2000);
            if (double.IsNaN(AnimationSpeedScale) || double.IsInfinity(AnimationSpeedScale))
                AnimationSpeedScale = 1.0;
            AnimationSpeedScale = Math.Clamp(AnimationSpeedScale, 0.55, 2.0);
            CollapsedLayout = NotchInfo.Sanitize(CollapsedLayout);
            HoverLayout = NotchInfo.Sanitize(HoverLayout);
            if (!Enum.IsDefined(IdleVisibility))
                IdleVisibility = IdleVisibilityMode.PeekWhenEmpty;
            TargetFrameRate = TargetFrameRate switch
            {
                <= 60 => 60,
                <= 120 => 120,
                <= 144 => 144,
                _ => 240
            };

            Theme = NotchTheme.Dark; // Einheitlicher OLED-Dark-Standard (v1.0.2)

            if (string.IsNullOrWhiteSpace(AccentThemeId))
                AccentThemeId = "indigo-violet";
            CustomAccentStartHex = NotchDesign.NormalizeHex(CustomAccentStartHex, "#2822A8");
            CustomAccentEndHex = NotchDesign.NormalizeHex(CustomAccentEndHex, "#9333EA");
        }

        public static List<string> NormalizeQuickTileSlots(List<string>? raw)
        {
            if (raw == null)
                return new List<string>(DefaultQuickActionTileOrder);

            var seen = new HashSet<string>();
            var slots = new List<string>(raw.Count);
            foreach (var rawItem in raw)
            {
                string item = string.Equals(rawItem, "song", StringComparison.Ordinal) ? "calc" : rawItem;
                if (string.IsNullOrWhiteSpace(item))
                {
                    slots.Add(string.Empty);
                    continue;
                }
                if (seen.Add(item))
                    slots.Add(item);
            }

            // Komplett leere Reihen am Ende entfernen, aber Positionen innerhalb belegter Reihen erhalten
            int lastNonEmpty = slots.FindLastIndex(s => !string.IsNullOrEmpty(s));
            if (lastNonEmpty < 0)
                return new List<string>(DefaultQuickActionTileOrder);

            int targetLen = ((lastNonEmpty / 3) + 1) * 3;
            while (slots.Count > targetLen)
                slots.RemoveAt(slots.Count - 1);
            while (slots.Count < targetLen)
                slots.Add(string.Empty);

            return slots;
        }
    }
}
