using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using FileTime = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace WindowsNotch.Services
{
    /// <summary>
    /// CPU-, RAM- und GPU-Auslastung für Notch-Layout und Dashboard. Sampling auf einem Hintergrund-Timer (1 s),
    /// GPU (PDH "GPU Engine") nur, solange <see cref="NeedsGpu"/> true liefert.
    /// </summary>
    public sealed class SystemMetricsService
    {
        public static SystemMetricsService Instance { get; } = new();

        public int CpuPercent { get; private set; }
        public int RamPercent { get; private set; }
        public double RamUsedGb { get; private set; }
        public double RamTotalGb { get; private set; }
        /// <summary>-1 = nicht verfügbar / noch kein Messwert.</summary>
        public int GpuPercent { get; private set; } = -1;

        /// <summary>Wird vom Besitzer gesetzt (z. B. NotchManager): true, wenn irgendein Layout GPU anzeigt.</summary>
        public Func<bool> NeedsGpu { get; set; } = () => false;

        /// <summary>Hält die GPU-Messung aktiv (z. B. für die Layout-Vorschau in den Einstellungen).</summary>
        public bool ForceGpu { get; set; }

        private Timer? _timer;
        private ulong _prevIdle, _prevKernel, _prevUser;
        private bool _hasPrevCpu;
        private readonly GpuSampler _gpu = new();

        public void Start()
        {
            if (_timer != null) return;
            Sample();
            _timer = new Timer(_ => Tick(), null, 1000, Timeout.Infinite);
        }

        private void Tick()
        {
            try { Sample(); }
            catch (Exception ex) { App.LogError(ex); }
            finally { _timer?.Change(1000, Timeout.Infinite); }
        }

        private void Sample()
        {
            if (GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
            {
                ulong idle = ToUInt64(idleFt), kernel = ToUInt64(kernelFt), user = ToUInt64(userFt);
                if (_hasPrevCpu)
                {
                    ulong total = (kernel - _prevKernel) + (user - _prevUser);
                    ulong dIdle = idle - _prevIdle;
                    if (total > 0)
                    {
                        double busy = total > dIdle ? (double)(total - dIdle) / total : 0;
                        CpuPercent = (int)Math.Clamp(Math.Round(busy * 100.0), 0, 100);
                    }
                }
                _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
                _hasPrevCpu = true;
            }

            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem) && mem.ullTotalPhys > 0)
            {
                RamPercent = (int)Math.Clamp(mem.dwMemoryLoad, 0, 100);
                RamTotalGb = mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                RamUsedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / (1024.0 * 1024.0 * 1024.0);
            }

            if (ForceGpu || NeedsGpu()) GpuPercent = _gpu.Sample() ?? -1;
        }

        private static ulong ToUInt64(FileTime ft) => ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        /// <summary>
        /// GPU-Last wie im Task-Manager: je physischer Engine die Summe über alle Prozesse, davon das Maximum.
        /// Die Wildcard-Instanzen werden alle 15 s neu geöffnet, damit neue Prozesse erfasst werden.
        /// </summary>
        private sealed class GpuSampler
        {
            private const uint PdhFmtDouble = 0x200;
            private const uint PdhMoreData = 0x800007D2;
            private const int ReopenAfter = 15;

            private IntPtr _query, _counter;
            private int _age, _samples;
            private bool _unavailable;
            private int? _last;

            public int? Sample()
            {
                if (_unavailable) return null;
                try
                {
                    if (_query == IntPtr.Zero || _age >= ReopenAfter) Reopen();
                    if (_query == IntPtr.Zero) return null;

                    PdhCollectQueryData(_query);
                    _age++;
                    if (++_samples < 2) return _last;

                    uint size = 0, count = 0;
                    if (PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref size, out count, IntPtr.Zero) != PdhMoreData || size == 0)
                        return _last;

                    IntPtr buf = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if (PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref size, out count, buf) != 0) return _last;

                        var perEngine = new Dictionary<string, double>();
                        int stride = Marshal.SizeOf<PdhItem>();
                        for (int i = 0; i < count; i++)
                        {
                            var item = Marshal.PtrToStructure<PdhItem>(buf + i * stride);
                            string name = Marshal.PtrToStringUni(item.Name) ?? "";
                            int luid = name.IndexOf("luid_", StringComparison.Ordinal);
                            int type = name.IndexOf("_engtype", StringComparison.Ordinal);
                            if (luid < 0 || type < luid) continue;
                            string key = name.Substring(luid, type - luid);
                            perEngine[key] = perEngine.GetValueOrDefault(key) + item.Value;
                        }

                        double max = 0;
                        foreach (var v in perEngine.Values) max = Math.Max(max, v);
                        _last = (int)Math.Clamp(Math.Round(max), 0, 100);
                        return _last;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                catch (Exception ex)
                {
                    _unavailable = true; // z. B. pdh.dll/Counter fehlt: nicht wiederholen
                    App.LogError(ex);
                    return null;
                }
            }

            private void Reopen()
            {
                if (_query != IntPtr.Zero) PdhCloseQuery(_query);
                _query = _counter = IntPtr.Zero;
                _age = _samples = 0;

                if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0 ||
                    PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) != 0)
                {
                    if (_query != IntPtr.Zero) PdhCloseQuery(_query);
                    _query = IntPtr.Zero;
                    _unavailable = true; // Windows ohne GPU-Engine-Zähler
                }
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct PdhItem
            {
                public IntPtr Name;
                public uint Status;
                public double Value;
            }

            [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
            private static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);

            [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
            private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);

            [DllImport("pdh.dll")]
            private static extern uint PdhCollectQueryData(IntPtr query);

            [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
            private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);

            [DllImport("pdh.dll")]
            private static extern uint PdhCloseQuery(IntPtr query);
        }
    }
}
