using System.Collections.Generic;
using WindowsNotch.Services;

namespace WindowsNotch.Core
{
    /// <summary>Platz einer Information in der Notch-Kapsel.</summary>
    public enum NotchSlot { Off, Left, Center, Right }

    public sealed record NotchInfoDef(string Id, string Label, NotchIconKind Icon)
    {
        public string LocalizedLabel => L.T($"info.{Id}");
    }

    /// <summary>Registry aller frei platzierbaren Informationen für Ruhezustand und Hover (siehe docs/NOTCH-LAYOUT-SPEC.md).</summary>
    public static class NotchInfo
    {
        public const string Clock = "clock";
        public const string Date = "date";
        public const string Weather = "weather";
        public const string Cpu = "cpu";
        public const string Ram = "ram";
        public const string Gpu = "gpu";
        public const string Media = "media";

        /// <summary>Reihenfolge = Reihenfolge innerhalb eines Platzes.</summary>
        public static readonly IReadOnlyList<NotchInfoDef> All = new NotchInfoDef[]
        {
            new(Clock, "Uhrzeit", NotchIconKind.Clock),
            new(Date, "Datum", NotchIconKind.Calendar),
            new(Weather, "Wetter", NotchIconKind.CloudSun),
            new(Cpu, "CPU", NotchIconKind.Cpu),
            new(Ram, "RAM", NotchIconKind.Memory),
            new(Gpu, "GPU", NotchIconKind.Gpu),
            new(Media, "Medientitel", NotchIconKind.Music),
        };

        public static Dictionary<string, NotchSlot> DefaultCollapsed() => new() { [Clock] = NotchSlot.Center };

        public static Dictionary<string, NotchSlot> DefaultHover() => new()
        {
            [Clock] = NotchSlot.Center,
            [Weather] = NotchSlot.Center,
            [Cpu] = NotchSlot.Center,
        };

        /// <summary>Entfernt unbekannte Ids und Off-Einträge.</summary>
        public static Dictionary<string, NotchSlot> Sanitize(Dictionary<string, NotchSlot>? layout)
        {
            var result = new Dictionary<string, NotchSlot>();
            if (layout == null) return result;
            foreach (var def in All)
                if (layout.TryGetValue(def.Id, out var slot) && slot != NotchSlot.Off)
                    result[def.Id] = slot;
            return result;
        }
    }
}
