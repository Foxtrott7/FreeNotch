using System;
using System.Windows.Threading;

namespace WindowsNotch.Modules.Pet
{
    public enum PetEyeMood
    {
        Calm,
        Curious,
        Drowsy,
        Sleeping,
        HappyWake
    }

    /// <summary>
    /// Minimaler Zustandsdienst für die Notch-Augen:
    /// reagiert ruhig auf Maus-Hover, Interaktion und Windows-Inaktivität (Drowsy -> Sleeping -> HappyWake).
    /// </summary>
    public sealed class PetService
    {
        public static PetService Instance { get; } = new();

        private const uint DrowsyThresholdMs = 25_000;
        private const uint SleepThresholdMs = 45_000;

        private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private bool _isHoveringNotch;
        private bool _wasSleepingOrDrowsy;
        private DateTime _happyUntilUtc = DateTime.MinValue;

        public PetEyeMood CurrentMood { get; private set; } = PetEyeMood.Calm;

        public event EventHandler? StateChanged;

        private PetService()
        {
            UpdateMood();
            _pollTimer.Tick += (_, _) => UpdateMood();
            _pollTimer.Start();
        }

        public void SetHoveringNotch(bool hovering)
        {
            if (_isHoveringNotch == hovering) return;
            _isHoveringNotch = hovering;
            if (hovering && CurrentMood == PetEyeMood.Sleeping)
                TriggerWakeReaction();
            else
                UpdateMood();
        }

        private void TriggerWakeReaction()
        {
            _wasSleepingOrDrowsy = false;
            _happyUntilUtc = DateTime.UtcNow.AddSeconds(1.5);
            SetMood(PetEyeMood.HappyWake);
        }

        private void UpdateMood()
        {
            uint idleMs = NativeMethods.GetIdleTimeMs();
            PetEyeMood next;

            if (_happyUntilUtc > DateTime.UtcNow)
            {
                next = PetEyeMood.HappyWake;
            }
            else if (idleMs >= SleepThresholdMs && !_isHoveringNotch)
            {
                _wasSleepingOrDrowsy = true;
                next = PetEyeMood.Sleeping;
            }
            else if (idleMs >= DrowsyThresholdMs && !_isHoveringNotch)
            {
                _wasSleepingOrDrowsy = true;
                next = PetEyeMood.Drowsy;
            }
            else if (_wasSleepingOrDrowsy)
            {
                // Sanftes Aufwachen mit kurzem Lächeln, wenn nach Müdigkeit/Schlaf wieder Aktivität kommt
                TriggerWakeReaction();
                return;
            }
            else if (_isHoveringNotch)
            {
                next = PetEyeMood.Curious;
            }
            else
            {
                next = PetEyeMood.Calm;
            }

            SetMood(next);
        }

        private void SetMood(PetEyeMood mood)
        {
            if (CurrentMood == mood) return;
            CurrentMood = mood;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
