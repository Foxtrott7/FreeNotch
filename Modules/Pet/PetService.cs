using System;
using System.Windows.Threading;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.Pet
{
    public enum PetExpression
    {
        Neutral,
        Curious,
        Happy,
        Sleepy,
        Hungry,
        Vibing
    }

    /// <summary>
    /// Zentraler Zustand und Lebenszyklus für das Notch-Gesicht und das Tamagotchi-Pet.
    /// Notch-Augen (NotchInfo.PetEyes) funktionieren auch unabhängig bei deaktiviertem Tamagotchi.
    /// </summary>
    public sealed class PetService
    {
        public static PetService Instance { get; } = new();

        private const uint IdleSleepThresholdMs = 45_000;

        private readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromSeconds(5) };
        private DateTime _lastSavedUtc = DateTime.UtcNow;
        private DateTime _reactionUntilUtc = DateTime.MinValue;
        private PetExpression _reactionExpression = PetExpression.Happy;
        private string? _reactionMessageKey;
        private bool _isHoveringNotch;
        private double _hungerRemainder;
        private double _energyRemainder;
        private double _moodRemainder;

        public PetExpression CurrentExpression { get; private set; } = PetExpression.Neutral;

        public event EventHandler? StateChanged;

        private PetService()
        {
            ApplyElapsedDecay();
            UpdateExpression();
            _tickTimer.Tick += (_, _) => OnTick();
            _tickTimer.Start();
        }

        private static AppSettings S => SettingsService.Current;

        public bool IsTamagotchiEnabled => S.PetTamagotchiEnabled;
        public int Level => Math.Max(1, S.PetLevel);
        public int Xp => Math.Max(0, S.PetXp);
        public int Hunger => Math.Clamp(S.PetHunger, 0, 100);
        public int Energy => Math.Clamp(S.PetEnergy, 0, 100);
        public int MoodScore => Math.Clamp(S.PetMoodScore, 0, 100);
        public bool IsSleeping => S.PetSleeping;

        public int XpNeededForNextLevel => 60 + (Level - 1) * 40;

        public double XpProgressRatio
        {
            get
            {
                int needed = XpNeededForNextLevel;
                return needed <= 0 ? 0 : Math.Clamp((double)Xp / needed, 0.0, 1.0);
            }
        }

        public void SetTamagotchiEnabled(bool enabled)
        {
            if (S.PetTamagotchiEnabled == enabled) return;
            SettingsService.UpdateQuiet(s =>
            {
                s.PetTamagotchiEnabled = enabled;
                s.PetLastUpdatedUtc = DateTime.UtcNow;
            });
            UpdateExpression();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetHoveringNotch(bool hovering)
        {
            if (_isHoveringNotch == hovering) return;
            _isHoveringNotch = hovering;
            if (UpdateExpression())
                StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Feed()
        {
            if (!S.PetTamagotchiEnabled)
                S.PetTamagotchiEnabled = true;

            ApplyElapsedDecay();
            S.PetSleeping = false;
            S.PetHunger = Math.Clamp(S.PetHunger + 24, 0, 100);
            S.PetMoodScore = Math.Clamp(S.PetMoodScore + 6, 0, 100);
            bool leveledUp = AddXp(15);

            SetTempReaction(PetExpression.Happy, TimeSpan.FromSeconds(2.8),
                leveledUp ? "pet.status.level_up" : "pet.status.fed");
            SaveNow();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Pet()
        {
            if (!S.PetTamagotchiEnabled)
            {
                SetTempReaction(PetExpression.Happy, TimeSpan.FromSeconds(2.2), null);
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            ApplyElapsedDecay();
            if (S.PetSleeping)
            {
                // Sanftes Streicheln im Schlaf gibt nur etwas Laune ohne zwingend aufzuwecken
                S.PetMoodScore = Math.Clamp(S.PetMoodScore + 8, 0, 100);
                AddXp(5);
                SetTempReaction(PetExpression.Happy, TimeSpan.FromSeconds(2.0), "pet.status.cozy");
            }
            else
            {
                S.PetMoodScore = Math.Clamp(S.PetMoodScore + 14, 0, 100);
                bool leveledUp = AddXp(10);
                SetTempReaction(PetExpression.Happy, TimeSpan.FromSeconds(2.5),
                    leveledUp ? "pet.status.level_up" : "pet.status.petted");
            }

            SaveNow();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleSleep()
        {
            if (!S.PetTamagotchiEnabled)
                S.PetTamagotchiEnabled = true;

            ApplyElapsedDecay();
            S.PetSleeping = !S.PetSleeping;
            if (!S.PetSleeping)
            {
                SetTempReaction(PetExpression.Curious, TimeSpan.FromSeconds(2.0), "pet.status.awake");
            }
            else
            {
                _reactionUntilUtc = DateTime.MinValue;
                _reactionMessageKey = "pet.status.sleeping";
                UpdateExpression();
            }

            SaveNow();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public string GetStatusText()
        {
            if (!S.PetTamagotchiEnabled)
                return GetSimpleMoodLabel();

            if (_reactionUntilUtc > DateTime.UtcNow && !string.IsNullOrEmpty(_reactionMessageKey))
                return L.T(_reactionMessageKey);

            if (S.PetSleeping) return L.T("pet.status.sleeping");
            if (S.PetEnergy <= 20) return L.T("pet.status.tired");
            if (S.PetHunger <= 25) return L.T("pet.status.hungry");
            if (S.PetMoodScore <= 25) return L.T("pet.status.bored");
            if (MediaPlayerService.Instance.IsPlaying) return L.T("pet.status.vibing");
            if (S.PetMoodScore >= 80 && S.PetHunger >= 70) return L.T("pet.status.happy");
            return L.T("pet.status.cozy");
        }

        private string GetSimpleMoodLabel() => CurrentExpression switch
        {
            PetExpression.Happy => L.T("pet.status.happy"),
            PetExpression.Sleepy => L.T("pet.status.sleeping"),
            PetExpression.Vibing => L.T("pet.status.vibing"),
            PetExpression.Curious => L.T("pet.status.curious"),
            _ => L.T("pet.status.cozy")
        };

        private bool AddXp(int amount)
        {
            S.PetXp += amount;
            bool leveledUp = false;
            while (S.PetXp >= XpNeededForNextLevel)
            {
                S.PetXp -= XpNeededForNextLevel;
                S.PetLevel = Math.Max(1, S.PetLevel + 1);
                leveledUp = true;
            }
            return leveledUp;
        }

        private void SetTempReaction(PetExpression expr, TimeSpan duration, string? messageKey)
        {
            _reactionExpression = expr;
            _reactionUntilUtc = DateTime.UtcNow + duration;
            _reactionMessageKey = messageKey;
            CurrentExpression = expr;
        }

        private void OnTick()
        {
            bool changed = ApplyElapsedDecay();
            if (UpdateExpression())
                changed = true;

            if ((DateTime.UtcNow - _lastSavedUtc).TotalMinutes >= 3)
            {
                _lastSavedUtc = DateTime.UtcNow;
                if (S.PetTamagotchiEnabled)
                    SaveNow();
            }

            if (changed)
                StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private bool ApplyElapsedDecay()
        {
            var now = DateTime.UtcNow;
            if (S.PetLastUpdatedUtc == default || S.PetLastUpdatedUtc > now)
            {
                S.PetLastUpdatedUtc = now;
                return false;
            }

            double elapsedMinutes = (now - S.PetLastUpdatedUtc).TotalMinutes;
            if (elapsedMinutes < 0.25) return false;

            S.PetLastUpdatedUtc = now;
            if (!S.PetTamagotchiEnabled) return false;

            // Nach längerer Offline-Pause sanft deckeln (max. 12 Stunden Abbau), damit das Pet nie völlig auf 0 fällt
            double cappedMinutes = Math.Min(elapsedMinutes, 12 * 60);

            int oldH = S.PetHunger, oldE = S.PetEnergy, oldM = S.PetMoodScore;

            if (S.PetSleeping)
            {
                _energyRemainder += cappedMinutes / 1.8; // ~+1 Energie alle 1.8 Min im Schlaf
                _hungerRemainder -= cappedMinutes / 16.0;
            }
            else
            {
                _hungerRemainder -= cappedMinutes / 6.0;
                _energyRemainder -= cappedMinutes / 8.0;
                _moodRemainder -= cappedMinutes / 9.0;
            }

            ApplyWholeDelta(ref _hungerRemainder, d => S.PetHunger = Math.Clamp(S.PetHunger + d, 15, 100));
            ApplyWholeDelta(ref _energyRemainder, d => S.PetEnergy = Math.Clamp(S.PetEnergy + d, 12, 100));
            ApplyWholeDelta(ref _moodRemainder, d => S.PetMoodScore = Math.Clamp(S.PetMoodScore + d, 15, 100));

            return oldH != S.PetHunger || oldE != S.PetEnergy || oldM != S.PetMoodScore;
        }

        private static void ApplyWholeDelta(ref double accumulator, Action<int> apply)
        {
            int whole = (int)Math.Truncate(accumulator);
            if (whole == 0) return;
            accumulator -= whole;
            apply(whole);
        }

        private bool UpdateExpression()
        {
            var next = ComputeExpression();
            if (next == CurrentExpression) return false;
            CurrentExpression = next;
            return true;
        }

        private PetExpression ComputeExpression()
        {
            if (_reactionUntilUtc > DateTime.UtcNow)
                return _reactionExpression;

            _reactionMessageKey = null;
            bool systemIdle = NativeMethods.GetIdleTimeMs() >= IdleSleepThresholdMs;

            if (S.PetTamagotchiEnabled)
            {
                if (S.PetSleeping) return PetExpression.Sleepy;
                if (S.PetEnergy <= 20) return PetExpression.Sleepy;
                if (S.PetHunger <= 25 || S.PetMoodScore <= 25) return PetExpression.Hungry;
                if (MediaPlayerService.Instance.IsPlaying) return PetExpression.Vibing;
                if (_isHoveringNotch) return PetExpression.Curious;
                if (systemIdle) return PetExpression.Sleepy;
                return PetExpression.Neutral;
            }

            // Reiner Notch-Augen-Modus (ohne Tamagotchi-Zwang)
            if (_isHoveringNotch) return PetExpression.Curious;
            if (MediaPlayerService.Instance.IsPlaying) return PetExpression.Vibing;
            if (systemIdle) return PetExpression.Sleepy;
            return PetExpression.Neutral;
        }

        private void SaveNow()
        {
            _lastSavedUtc = DateTime.UtcNow;
            SettingsService.UpdateQuiet(s => s.PetLastUpdatedUtc = _lastSavedUtc);
        }
    }
}
