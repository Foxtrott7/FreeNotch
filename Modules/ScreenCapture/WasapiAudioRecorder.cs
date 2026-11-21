using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace WindowsNotch.Modules.ScreenCapture
{
    /// <summary>
    /// Zeichnet Systemaudio (WASAPI Loopback) und/oder Mikrofon auf und liefert gemischtes 48 kHz Stereo 16-Bit PCM.
    /// </summary>
    internal sealed class WasapiAudioRecorder : IDisposable
    {
        private const int TargetSampleRate = 48000;
        private const int TargetChannels = 2;
        private const uint AudclntStreamFlagsLoopback = 0x00020000;
        private const ushort WaveFormatPcm = 1;
        private const ushort WaveFormatFloat = 3;
        private const ushort WaveFormatExtensible = 0xFFFE;

        private readonly ConcurrentQueue<short[]> _systemQueue = new();
        private readonly ConcurrentQueue<short[]> _micQueue = new();
        private readonly bool _useSystem;
        private readonly bool _useMic;
        private CancellationTokenSource? _cts;
        private Thread? _systemThread;
        private Thread? _micThread;

        private short[] _sysCarry = Array.Empty<short>();
        private int _sysCarryPos;
        private short[] _micCarry = Array.Empty<short>();
        private int _micCarryPos;

        public WasapiAudioRecorder(bool includeSystemAudio, bool includeMicrophone)
        {
            _useSystem = includeSystemAudio;
            _useMic = includeMicrophone;
        }

        public bool HasAudio => _useSystem || _useMic;

        public void Start()
        {
            if (!HasAudio) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            if (_useSystem)
            {
                _systemThread = new Thread(() => CaptureLoop(isLoopback: true, _systemQueue, ct))
                {
                    IsBackground = true,
                    Name = "ScreenCapture-SysAudio"
                };
                _systemThread.Start();
            }

            if (_useMic)
            {
                _micThread = new Thread(() => CaptureLoop(isLoopback: false, _micQueue, ct))
                {
                    IsBackground = true,
                    Name = "ScreenCapture-MicAudio"
                };
                _micThread.Start();
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _systemThread?.Join(500); } catch { }
            try { _micThread?.Join(500); } catch { }
        }

        /// <summary>
        /// Liest exakt <paramref name="frameCount"/> Stereo-Frames (je 2 Shorts) als 16-Bit-PCM-Bytes.
        /// </summary>
        public byte[] ReadPcmChunk(int frameCount)
        {
            int sampleCount = frameCount * TargetChannels;
            var sysBuf = _useSystem ? ReadSamples(_systemQueue, ref _sysCarry, ref _sysCarryPos, sampleCount) : null;
            var micBuf = _useMic ? ReadSamples(_micQueue, ref _micCarry, ref _micCarryPos, sampleCount) : null;

            var bytes = new byte[sampleCount * 2];
            for (int i = 0; i < sampleCount; i++)
            {
                int mixed = (sysBuf?[i] ?? 0) + (micBuf?[i] ?? 0);
                short clamped = (short)Math.Clamp(mixed, short.MinValue, short.MaxValue);
                bytes[i * 2] = (byte)(clamped & 0xFF);
                bytes[i * 2 + 1] = (byte)((clamped >> 8) & 0xFF);
            }
            return bytes;
        }

        private static short[] ReadSamples(
            ConcurrentQueue<short[]> queue,
            ref short[] carry,
            ref int carryPos,
            int needed)
        {
            var output = new short[needed];
            int written = 0;

            while (written < needed)
            {
                if (carryPos < carry.Length)
                {
                    int take = Math.Min(needed - written, carry.Length - carryPos);
                    Array.Copy(carry, carryPos, output, written, take);
                    carryPos += take;
                    written += take;
                }
                else if (queue.TryDequeue(out var next))
                {
                    carry = next;
                    carryPos = 0;
                }
                else
                {
                    break; // Stille bei Underrun
                }
            }

            return output;
        }

        private static void CaptureLoop(bool isLoopback, ConcurrentQueue<short[]> queue, CancellationToken ct)
        {
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            IAudioClient? audioClient = null;
            IAudioCaptureClient? captureClient = null;
            IntPtr pwfx = IntPtr.Zero;

            try
            {
                var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
                enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid)!)!;
                // eRender (0) für Systemaudio-Loopback, eCapture (1) für Mikrofon
                int flow = isLoopback ? 0 : 1;
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(flow, 1, out device));

                var iidAudioClient = typeof(IAudioClient).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iidAudioClient, 23, IntPtr.Zero, out var obj));
                audioClient = (IAudioClient)obj;

                Marshal.ThrowExceptionForHR(audioClient.GetMixFormat(out pwfx));
                var wfx = Marshal.PtrToStructure<WaveFormatEx>(pwfx);
                bool isFloat = IsFloatFormat(pwfx, wfx);

                long bufferDuration = 1_000_000; // 100 ms
                uint flags = isLoopback ? AudclntStreamFlagsLoopback : 0u;
                var sessionGuid = Guid.Empty;
                Marshal.ThrowExceptionForHR(audioClient.Initialize(0, flags, bufferDuration, 0, pwfx, ref sessionGuid));

                var iidCapture = typeof(IAudioCaptureClient).GUID;
                Marshal.ThrowExceptionForHR(audioClient.GetService(ref iidCapture, out var capObj));
                captureClient = (IAudioCaptureClient)capObj;

                Marshal.ThrowExceptionForHR(audioClient.Start());

                while (!ct.IsCancellationRequested)
                {
                    Thread.Sleep(8);
                    while (!ct.IsCancellationRequested)
                    {
                        int hr = captureClient.GetNextPacketSize(out int packetFrames);
                        if (hr < 0 || packetFrames <= 0) break;

                        hr = captureClient.GetBuffer(out IntPtr dataPtr, out int numFrames, out uint bufFlags, out _, out _);
                        if (hr < 0) break;

                        try
                        {
                            if (numFrames > 0)
                            {
                                bool silent = (bufFlags & 0x2) != 0; // AUDCLNT_BUFFERFLAGS_SILENT
                                var converted = ConvertTo48kStereo16(
                                    dataPtr,
                                    numFrames,
                                    (int)wfx.nSamplesPerSec,
                                    wfx.nChannels,
                                    wfx.wBitsPerSample,
                                    isFloat,
                                    silent);
                                if (converted.Length > 0 && queue.Count < 150)
                                    queue.Enqueue(converted);
                            }
                        }
                        finally
                        {
                            captureClient.ReleaseBuffer(numFrames);
                        }
                    }
                }

                audioClient.Stop();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
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

        private static bool IsFloatFormat(IntPtr pwfx, WaveFormatEx wfx)
        {
            if (wfx.wFormatTag == WaveFormatFloat) return true;
            if (wfx.wFormatTag == WaveFormatPcm) return false;
            if (wfx.wFormatTag == WaveFormatExtensible && wfx.cbSize >= 22)
            {
                // SubFormat GUID beginnt bei Offset 24 (direkt nach WAVEFORMATEXTENSIBLE.Samples + dwChannelMask)
                var subFormat = Marshal.PtrToStructure<Guid>(IntPtr.Add(pwfx, 24));
                return subFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
            }
            return wfx.wBitsPerSample == 32;
        }

        private static short[] ConvertTo48kStereo16(
            IntPtr dataPtr,
            int frames,
            int srcRate,
            int srcChannels,
            int bitsPerSample,
            bool isFloat,
            bool silent)
        {
            int dstFrames = srcRate == TargetSampleRate
                ? frames
                : Math.Max(1, (int)Math.Round(frames * (double)TargetSampleRate / srcRate));

            var result = new short[dstFrames * TargetChannels];
            if (silent || dataPtr == IntPtr.Zero || srcChannels <= 0)
                return result;

            int bytesPerSample = Math.Max(1, bitsPerSample / 8);
            int totalBytes = frames * srcChannels * bytesPerSample;
            var raw = new byte[totalBytes];
            Marshal.Copy(dataPtr, raw, 0, totalBytes);

            double ratio = (double)frames / dstFrames;
            for (int d = 0; d < dstFrames; d++)
            {
                int s = Math.Min(frames - 1, (int)(d * ratio));
                float left = ReadSampleNormalized(raw, s, 0, srcChannels, bytesPerSample, isFloat);
                float right = srcChannels > 1
                    ? ReadSampleNormalized(raw, s, 1, srcChannels, bytesPerSample, isFloat)
                    : left;

                result[d * 2] = (short)Math.Clamp(left * 32767f, short.MinValue, short.MaxValue);
                result[d * 2 + 1] = (short)Math.Clamp(right * 32767f, short.MinValue, short.MaxValue);
            }

            return result;
        }

        private static float ReadSampleNormalized(
            byte[] raw,
            int frame,
            int channel,
            int channels,
            int bytesPerSample,
            bool isFloat)
        {
            int offset = (frame * channels + channel) * bytesPerSample;
            if (offset + bytesPerSample > raw.Length) return 0f;

            if (isFloat && bytesPerSample == 4)
                return Math.Clamp(BitConverter.ToSingle(raw, offset), -1f, 1f);
            if (bytesPerSample == 2)
                return BitConverter.ToInt16(raw, offset) / 32768f;
            if (bytesPerSample == 4)
                return BitConverter.ToInt32(raw, offset) / (float)int.MaxValue;
            return 0f;
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormatEx
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePtr);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, ref Guid audioSessionGuid);
            int GetBufferSize(out uint numBufferFrames);
            int GetStreamLatency(out long phnsLatency);
            int GetCurrentPadding(out uint numPaddingFrames);
            int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);
            int GetMixFormat(out IntPtr ppDeviceFormat);
            int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
            int Start();
            int Stop();
            int Reset();
            int SetEventHandle(IntPtr eventHandle);
            int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            int GetBuffer(out IntPtr ppData, out int numFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
            int ReleaseBuffer(int numFramesRead);
            int GetNextPacketSize(out int numFramesInNextPacket);
        }
    }
}
