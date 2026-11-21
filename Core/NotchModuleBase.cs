using System;
using System.Windows;

namespace WindowsNotch.Core
{
    /// <summary>
    /// Basisklasse für Notch-Module mit praktischen Standardimplementierungen.
    /// </summary>
    public abstract class NotchModuleBase : INotchModule
    {
        public abstract string Id { get; }
        public abstract string Title { get; }
        public virtual NotchIconKind IconKind => NotchIconKind.Sparkle;
        public virtual bool ShowInTabBar => true;
        public bool IsActive { get; set; }
        public virtual bool KeepExpanded => false;
        public virtual int Priority { get; protected set; } = 0;
        public virtual double? PreferredPreviewWidth => null;
        public virtual double? PreferredLiveWidth => null;
        public virtual (double Width, double Height)? PreferredExpandedSize => null;

        public virtual FrameworkElement? CreateLiveView() => null;
        public abstract FrameworkElement CreatePreviewView();
        public abstract FrameworkElement CreateExpandedView();
        public virtual bool HandleEscape() => false;

        public virtual void Start() { }
        public virtual void Stop() { }

        public event EventHandler? StateChanged;
        public event EventHandler? CollapseRequested;

        /// <summary>Löst das StateChanged-Event aus, wenn sich Daten oder die Priorität geändert haben.</summary>
        protected void RaiseStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Fordert das normale Einklappen der geöffneten Notch an.</summary>
        protected void RaiseCollapseRequested()
        {
            CollapseRequested?.Invoke(this, EventArgs.Empty);
        }

        public virtual void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
