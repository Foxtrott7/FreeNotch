using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using WindowsNotch.Services;

namespace WindowsNotch.Services
{
    public enum ScheduledPowerAction
    {
        Shutdown,
        Restart,
        LogOff,
        Sleep
    }

    /// <summary>
    /// Zentraler Countdown-Dienst für zeitgesteuerte Power-Aktionen (Shutdown, Restart, LogOff, Sleep).
    /// Läuft pro Prozess einmalig, unabhängig von geöffneten Notch-Fenstern/Monitoren.
    /// </summary>
    public sealed class PowerScheduleService
    {
        public static PowerScheduleService Instance { get; } = new();

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

        public bool IsActive { get; private set; }
        public DateTime? TargetLocalTime { get; private set; }
        public ScheduledPowerAction Action { get; private set; } = ScheduledPowerAction.Shutdown;

        public event EventHandler? Changed;

        private PowerScheduleService()
        {
            _timer.Tick += OnTick;
        }

        public TimeSpan Remaining
        {
            get
            {
                if (!IsActive || TargetLocalTime is not DateTime target)
                    return TimeSpan.Zero;
                var diff = target - DateTime.Now;
                return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
            }
        }

        public void ScheduleAtTime(int hour, int minute, ScheduledPowerAction action)
        {
            var now = DateTime.Now;
            var target = new DateTime(now.Year, now.Month, now.Day, Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59), 0, DateTimeKind.Local);
            if (target <= now.AddSeconds(5))
                target = target.AddDays(1);

            Action = action;
            TargetLocalTime = target;
            IsActive = true;
            _timer.Start();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void ScheduleIn(TimeSpan delay, ScheduledPowerAction action)
        {
            var target = DateTime.Now + delay;
            Action = action;
            TargetLocalTime = target;
            IsActive = true;
            _timer.Start();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Cancel()
        {
            if (!IsActive) return;
            _timer.Stop();
            IsActive = false;
            TargetLocalTime = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (!IsActive || TargetLocalTime is not DateTime target)
            {
                _timer.Stop();
                return;
            }

            if (DateTime.Now >= target)
            {
                var action = Action;
                _timer.Stop();
                IsActive = false;
                TargetLocalTime = null;
                Changed?.Invoke(this, EventArgs.Empty);
                Execute(action);
                return;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        public static void Execute(ScheduledPowerAction action)
        {
            switch (action)
            {
                case ScheduledPowerAction.Shutdown:
                    RunPowerProcess("shutdown.exe", "/s /t 0");
                    break;
                case ScheduledPowerAction.Restart:
                    RunPowerProcess("shutdown.exe", "/r /t 0");
                    break;
                case ScheduledPowerAction.LogOff:
                    RunPowerProcess("shutdown.exe", "/l");
                    break;
                case ScheduledPowerAction.Sleep:
                    TriggerSystemSleep();
                    break;
            }
        }

        public static void TriggerSystemSleep()
        {
            try
            {
                if (!SetSuspendState(false, false, false))
                    RunPowerProcess("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                RunPowerProcess("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
            }
        }

        public static void RunShutdownCommand(string arguments)
            => RunPowerProcess("shutdown.exe", arguments);

        private static void RunPowerProcess(string fileName, string arguments)
        {
            try
            {
                Process.Start(new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }
    }
}
