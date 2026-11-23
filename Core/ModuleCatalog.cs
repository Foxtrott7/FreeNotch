using System.Collections.Generic;
using WindowsNotch.Services;

namespace WindowsNotch.Core
{
    public record ModuleInfo(string Id, string Label, bool IsGaming, bool InWorkProfile)
    {
        public string LocalizedLabel => L.T($"module.{Id}");
    }

    /// <summary>Zentraler Katalog aller konfigurierbaren Module inkl. Profil-Zuordnung (Work / Gaming / Complete).</summary>
    public static class ModuleCatalog
    {
        public static readonly IReadOnlyList<ModuleInfo> All = new List<ModuleInfo>
        {
            new("notifications",   "Windows-Benachrichtigungen", false, true),
            new("media",           "Musik und Mediensteuerung",  false, true),
            new("lyrics",          "Synchronisierte Songtexte",  false, false),
            new("clipboard",       "Zwischenablage",             false, false),
            new("filetray",        "File Tray",                  false, true),
            new("widgets",         "Widgets",                    false, true),
            new("calendar",        "Kalender, Aufgaben, Notizen",false, true),
            new("timer",           "Timer und Pomodoro",         false, true),
            new("shortcuts",       "App Shortcuts",              false, true),
            new("downloads",       "Downloads",                  false, false),
            new("filetransfer",    "Smartphone-Dateitransfer",   false, false),
            new("bluetooth",       "Bluetooth und Geräte",       false, true),
            new("discord",         "Discord",                    false, false),
            new("controlcenter",   "Control Center",             false, true),
            new("quickactions",    "Quick Actions",              false, true),
            new("screencapture",   "Bildschirmaufnahme",         false, true),
            new("screenocr",       "Bildschirmsuche, OCR, QR",   false, true),
            new("finance",         "Finanzen",                   false, true),
            new("weather",         "Wetter",                     false, true),
            // "pet" (Mondkätzchen) vorerst pausiert; Modules/Pet & NotchIconKind.Pet bleiben für später erhalten.
            new("privacy",         "Datenschutzindikatoren",     false, true),
            new("gaming",          "Gaming-Erkennung und Sessions", true, false),
            new("performance",     "Performance-Monitoring (FPS, GPU, CPU)", true, false),
            new("gaminghud",       "Gaming-HUD im Spiel",        true, false),
        };

        public static bool DefaultEnabled(ProfileType profile, ModuleInfo module)
            => profile == ProfileType.Work ? module.InWorkProfile : true;

        public static bool DefaultEnabled(ProfileType profile, string id)
        {
            foreach (var m in All)
                if (m.Id == id) return DefaultEnabled(profile, m);
            return true;
        }
    }
}
