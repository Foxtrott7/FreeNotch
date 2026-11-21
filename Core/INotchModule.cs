using System;
using System.Windows;

namespace WindowsNotch.Core
{
    /// <summary>
    /// Schnittstelle für alle Module der Windows Notch.
    /// Jedes Modul liefert eine kompakte Kapselansicht für den Hover-Zustand
    /// sowie eine erweiterte Ansicht für den geöffneten Zustand.
    /// </summary>
    public interface INotchModule : IDisposable
    {
        /// <summary>Eindeutige ID des Moduls (muss zu ModuleCatalog passen, z. B. 'media').</summary>
        string Id { get; }

        /// <summary>Anzeigename des Moduls in der Tab-Leiste (Tooltip).</summary>
        string Title { get; }

        /// <summary>Icon-Typ des Moduls für die kompakte Icon-Toolbar.</summary>
        NotchIconKind IconKind => NotchIconKind.Sparkle;

        /// <summary>Gibt an, ob das Modul als eigener Tab in der oberen Icon-Leiste angezeigt wird.</summary>
        bool ShowInTabBar => true;

        /// <summary>Gibt an, ob das Modul aktiv ist.</summary>
        bool IsActive { get; set; }

        /// <summary>Verhindert automatisches Einklappen bei Fokusverlust/Hover-Verlassen (z. B. angepinnte Vollbild-Lyrics).</summary>
        bool KeepExpanded => false;

        /// <summary>
        /// Priorität für Live-Aktivitäten (z. B. 0 = Normal, 50 = Bereit, 100 = Medien aktiv, 200 = Alarm).
        /// Das Modul mit der höchsten Priorität wird primär in der Kapselvorschau angezeigt.
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// Optionale bevorzugte Breite der Kapsel im Hover-Zustand in DIP.
        /// Null bedeutet automatische Größenberechnung anhand des Elements.
        /// </summary>
        double? PreferredPreviewWidth => null;

        /// <summary>
        /// Optionale bevorzugte Breite der Kapsel im Ruhezustand (Collapsed) bei aktiver Live-Aktivität in DIP.
        /// </summary>
        double? PreferredLiveWidth => null;

        /// <summary>
        /// Optionale bevorzugte Größe der geöffneten Notch für dieses Modul in DIP (Breite, Höhe).
        /// </summary>
        (double Width, double Height)? PreferredExpandedSize => null;

        /// <summary>Erzeugt eine kompakte Live-Ansicht für den Ruhezustand (z. B. laufende Musik bei hoher Priorität), oder null.</summary>
        FrameworkElement? CreateLiveView() => null;

        /// <summary>Erzeugt eine neue visuelle Instanz für die Kapselvorschau im Hover-Zustand.</summary>
        FrameworkElement CreatePreviewView();

        /// <summary>Erzeugt eine neue visuelle Instanz für die geöffnete Hauptansicht der Notch.</summary>
        FrameworkElement CreateExpandedView();

        /// <summary>Verarbeitet optional Escape in der geöffneten Ansicht (true = abgefangen, Notch bleibt offen).</summary>
        bool HandleEscape() => false;

        /// <summary>Startet Hintergrunddienste, Listener oder Timer des Moduls.</summary>
        void Start();

        /// <summary>Stoppt Hintergrunddienste des Moduls und gibt temporäre Ressourcen frei.</summary>
        void Stop();

        /// <summary>Wird ausgelöst, wenn sich Status, Daten oder Priorität des Moduls geändert haben.</summary>
        event EventHandler? StateChanged;

        /// <summary>Fordert ein normales Einklappen der Notch durch das aktive Modul an.</summary>
        event EventHandler? CollapseRequested { add { } remove { } }
    }
}
