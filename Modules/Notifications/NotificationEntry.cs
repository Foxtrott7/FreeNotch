using System;
using System.Windows.Media;

namespace WindowsNotch.Modules.Notifications
{
    /// <summary>
    /// Repräsentiert eine ausgelesene Windows-Benachrichtigung für Vorschau und Verlauf.
    /// </summary>
    public sealed class NotificationEntry
    {
        public uint Id { get; init; }
        public string AppId { get; init; } = string.Empty;
        public string AppName { get; init; } = "System";
        public string Title { get; init; } = string.Empty;
        public string Body { get; init; } = string.Empty;
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
        public ImageSource? AppIcon { get; init; }
        public bool IsAgentPermissionRequest { get; init; }
        public string AgentKey { get; init; } = string.Empty;

        /// <summary>Liefert den passenden Filter-Schlüssel (bevorzugt AppName, sonst AppId).</summary>
        public string FilterKey => !string.IsNullOrWhiteSpace(AppName) ? AppName : AppId;
    }
}
