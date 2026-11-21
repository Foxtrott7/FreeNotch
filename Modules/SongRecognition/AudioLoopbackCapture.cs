using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsNotch.Modules.SongRecognition
{
    /// <summary>
    /// Minimaler WASAPI-Systemaudio-Loopback-Sampler für Live-Pegel und kurze Audio-Snippets.
    /// </summary>
    internal static class AudioLoopbackCapture
    {
        private const uint AudclntStreamFlagsLoopback = 0x00020000;
        private const int ClsctxAll = 23;

        public static Task<(byte[] WavBytes, double PeakRms)> CaptureAsync(
            TimeSpan duration,
            Action<double>? onLevel,
            CancellationToken ct)
        {
            return Task.Run(() => CaptureInternal(duration, onLevel, ct), ct);
        }

        private static (byte[] WavBytes, double PeakRms) CaptureInternal(
            TimeSpan duration,
            Action<double>? onLevel,
            CancellationToken ct)
        {
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            IAudioClient? audioClient = null;
            IAudioCaptureClient? captureClient = null;
            IntPtr pwfx = IntPtr.Zero;

            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out device));
                var iidAudioClient = typeof(IAudioClient).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iidAudioClient, ClsctxAll, IntPtr.Zero, out var obj));
                audioClient = (IAudioClient)obj;

                Marshal.ThrowExceptionForHR(audioClient.GetMixFormat(out pwfx));
                var wfx = Marshal.PtrToStructure<WaveFormatEx>(pwfx);

                long hnsDuration = 10_000_000; // 1s Buffer
                Marshal.ThrowExceptionForHR(audioClient.Initialize(
                    0 /* AUDCLNT_SHAREMODE_SHARED */,
                    AudclntStreamFlagsLoopback,
                    hnsDuration,
                    0,
                    pwfx,
                    IntPtr.Zero));

                var iidCapture = typeof(IAudioCaptureClient).GUID;
                Marshal.ThrowExceptionForHR(audioClient.GetService(ref iidCapture, out var capObj));
                captureClient = (IAudioCaptureClient)capObj;

                Marshal.ThrowExceptionForHR(audioClient.Start());

                using var pcmStream = new MemoryStream();
                var startUtc = DateTime.UtcNow;
                double peakRms = 0;

                while (DateTime.UtcNow - startUtc < duration && !ct.IsCancellationRequested)
                {
                    Thread.Sleep(45);
                    double frameRms = 0;
                    int rmsCount = 0;

                    while (!ct.IsCancellationRequested)
                    {
                        int hr = captureClient.GetNextPacketSize(out int packetFrames);
                        if (hr < 0 || packetFrames == 0) break;

                        hr = captureClient.GetBuffer(out IntPtr dataPtr, out int numFrames, out int flags, out _, out _);
                        if (hr < 0) break;

                        try
                        {
                            int bytes = numFrames * wfx.BlockAlign;
                            if (bytes > 0)
                            {
                                bool silent = (flags & 0x2) != 0;
                                var buf = new byte[bytes];
                                if (!silent && dataPtr != IntPtr.Zero)
                                {
                                    Marshal.Copy(dataPtr, buf, 0, bytes);
                                    frameRms += ComputeRms(buf, wfx.BitsPerSample, wfx.FormatTag);
                                    rmsCount++;
                                }
                                pcmStream.Write(buf, 0, bytes);
                            }
                        }
                        finally
                        {
                            captureClient.ReleaseBuffer(numFrames);
                        }
                    }

                    double level = rmsCount > 0 ? Math.Clamp((frameRms / rmsCount) * 3.5, 0.0, 1.0) : 0.0;
                    if (level > peakRms) peakRms = level;
                    onLevel?.Invoke(level);
                }

                audioClient.Stop();
                return (WrapWav(pcmStream.ToArray(), wfx), peakRms);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return (Array.Empty<byte>(), 0.0);
            }
            finally
            {
                if (pwfx != IntPtr.Zero) Marshal.FreeCoTaskMem(pwfx);
                if (captureClient != null) Marshal.ReleaseComObject(captureClient);
                if (audioClient != null) Marshal.ReleaseComObject(audioClient);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }

        private static double ComputeRms(byte[] buf, ushort bitsPerSample, ushort formatTag)
        {
            double sum = 0;
            int samples = 0;

            if (bitsPerSample == 32 && (formatTag == 3 || formatTag == 0xFFFE))
            {
                for (int i = 0; i + 3 < buf.Length; i += 4)
                {
                    float s = BitConverter.ToSingle(buf, i);
                    sum += s * s;
                    samples++;
                }
            }
            else if (bitsPerSample == 16)
            {
                for (int i = 0; i + 1 < buf.Length; i += 2)
                {
                    double s = BitConverter.ToInt16(buf, i) / 32768.0;
                    sum += s * s;
                    samples++;
                }
            }

            return samples > 0 ? Math.Sqrt(sum / samples) : 0;
        }

        private static byte[] WrapWav(byte[] pcm, WaveFormatEx wfx)
        {
            if (pcm.Length == 0) return Array.Empty<byte>();
            using var ms = new MemoryStream(44 + pcm.Length);
            using var bw = new BinaryWriter(ms);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + pcm.Length);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((ushort)(wfx.BitsPerSample == 32 ? 3 : 1));
            bw.Write(wfx.Channels);
            bw.Write(wfx.SamplesPerSec);
            bw.Write(wfx.AvgBytesPerSec);
            bw.Write(wfx.BlockAlign);
            bw.Write(wfx.BitsPerSample);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(pcm.Length);
            bw.Write(pcm);
            return ms.ToArray();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormatEx
        {
            public ushort FormatTag;
            public ushort Channels;
            public uint SamplesPerSec;
            public uint AvgBytesPerSec;
            public ushort BlockAlign;
            public ushort BitsPerSample;
            public ushort Size;
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr format, IntPtr audioSessionGuid);
            int GetBufferSize(out uint numBufferFrames);
            int GetStreamLatency(out long phnsLatency);
            int GetCurrentPadding(out uint numPaddingFrames);
            int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
            int GetMixFormat(out IntPtr deviceFormatPointer);
            int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
            int Start();
            int Stop();
            int Reset();
            int SetEventHandle(IntPtr eventHandle);
            int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            int GetBuffer(out IntPtr data, out int numFramesToRead, out int flags, out ulong devicePosition, out ulong qpcPosition);
            int ReleaseBuffer(int numFramesRead);
            int GetNextPacketSize(out int numFramesInNextPacket);
        }
    }
}
