using System;
using System.Windows;
using WindowsNotch.Core;

namespace WindowsNotch.Modules.Pet
{
    public enum PetMood
    {
        Happy,
        Curious,
        Cozy,
        Excited,
        Sleepy
    }

    /// <summary>
    /// Interaktives Notch-Maskottchen (4-beiniger Fuchs) mit eigenem Lebensrhythmus,
    /// Maus-Tracking, Streicheln, Füttern und Ball-Minispiel.
    /// </summary>
    public sealed class PetModule : NotchModuleBase
    {
        public override string Id => "pet";
        public override string Title => "Notch-Maskottchen";
        public override NotchIconKind IconKind => NotchIconKind.Pet;
        public override int Priority => 5;
        public override double? PreferredLiveWidth => null;
        public override double? PreferredPreviewWidth => 210;
        public override (double Width, double Height)? PreferredExpandedSize => (440, 320);

        public bool ShowInIdleNotch { get; private set; } = true;
        public PetMood CurrentMood { get; private set; } = PetMood.Cozy;
        public bool AutoMood { get; private set; } = true;
        public int PetCount { get; private set; }
        public int FeedCount { get; private set; }
        public int CatchCount { get; private set; }
        public int HungerPercent { get; private set; } = 85;
        public DateTime LastInteractionUtc { get; private set; } = DateTime.UtcNow;

        public event EventHandler? PetStateChanged;
        public event EventHandler? FeedRequested;
        public event EventHandler? PlayBallRequested;

        public void SetShowInIdleNotch(bool show)
        {
            if (ShowInIdleNotch == show) return;
            ShowInIdleNotch = show;
            PetStateChanged?.Invoke(this, EventArgs.Empty);
            RaiseStateChanged();
        }

        public void SetMood(PetMood mood, bool autoMood)
        {
            AutoMood = autoMood;
            CurrentMood = mood;
            LastInteractionUtc = DateTime.UtcNow;
            PetStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RegisterClickReaction()
        {
            PetCount++;
            LastInteractionUtc = DateTime.UtcNow;
            if (AutoMood)
                CurrentMood = PetMood.Excited;
            PetStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RegisterStrokeReaction()
        {
            PetCount++;
            LastInteractionUtc = DateTime.UtcNow;
            if (AutoMood)
                CurrentMood = PetMood.Happy;
            PetStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RequestFeed()
        {
            FeedCount++;
            HungerPercent = Math.Min(100, HungerPercent + 15);
            LastInteractionUtc = DateTime.UtcNow;
            if (AutoMood)
                CurrentMood = PetMood.Happy;
            FeedRequested?.Invoke(this, EventArgs.Empty);
            PetStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RequestPlayBall()
        {
            LastInteractionUtc = DateTime.UtcNow;
            if (AutoMood)
                CurrentMood = PetMood.Excited;
            PlayBallRequested?.Invoke(this, EventArgs.Empty);
            PetStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RegisterBallCatch()
        {
            CatchCount++;
            HungerPercent = Math.Max(20, HungerPercent - 3);
            PetStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void UpdateAutoMoodFromIdle(double idleSeconds)
        {
            if (!AutoMood) return;

            var next = idleSeconds switch
            {
                >= 30 => PetMood.Sleepy,
                >= 12 => PetMood.Cozy,
                _ => CurrentMood is PetMood.Excited or PetMood.Happy && (DateTime.UtcNow - LastInteractionUtc).TotalSeconds < 6
                    ? CurrentMood
                    : PetMood.Curious
            };

            if (next != CurrentMood)
            {
                CurrentMood = next;
                PetStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string GetMoodLabel() => CurrentMood switch
        {
            PetMood.Happy => "Fröhlich",
            PetMood.Curious => "Neugierig",
            PetMood.Cozy => "Entspannt",
            PetMood.Excited => "Verspielt",
            PetMood.Sleepy => "Schläft",
            _ => "Entspannt"
        };

        /// <summary>Erzeugt den Fuchs für das Overlay im eingeklappten Ruhezustand.</summary>
        public FrameworkElement CreateIdleOverlayView()
            => new PetVisualControl(this, compact: true);

        public override FrameworkElement? CreateLiveView() => null;

        public override FrameworkElement CreatePreviewView()
            => new PetVisualControl(this, compact: true);

        public override FrameworkElement CreateExpandedView()
            => new PetExpandedView(this);
    }
}
