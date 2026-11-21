using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT;
using WindowsNotch.Services;
using Forms = System.Windows.Forms;

namespace WindowsNotch.Modules.ScreenCapture
{
    /// <summary>
    /// Nimmt einen ausgewählten Monitor über Windows Graphics Capture + WASAPI Audio als MP4 auf.
    /// </summary>
    public sealed class ScreenCaptureService : IDisposable
    {
        private readonly object _frameLock = new();
        private IBuffer? _latestVideoBuffer;
        private int _copyingFrame;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _captureSession;
        private GraphicsCaptureItem? _captureItem;
        private IDirect3DDevice? _d3dDevice;
        private WasapiAudioRecorder? _audioRecorder;
        private CancellationTokenSource? _recordCts;
        private Task? _transcodeTask;
        private Stopwatch? _stopwatch;

        private volatile bool _isRecording;
        private volatile bool _stopping;
        private int _fps = 30;
        private long _videoFrameIndex;
        private long _audioSampleIndex;

        public static bool IsAnyRecording { get; private set; }
        public event Action? StateChanged;

        public bool IsRecording => _isRecording;
        public TimeSpan Elapsed => _isRecording && _stopwatch != null ? _stopwatch.Elapsed : TimeSpan.Zero;
        public string? CurrentFilePath { get; private set; }
        public string? LastError { get; private set; }

        public static string RecordingsFolder
        {
            get
            {
                string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                string dir = Path.Combine(videos, "WindowsNotch");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static List<RecordingEntry> GetRecentRecordings(int maxCount = 12)
        {
            try
            {
                string dir = RecordingsFolder;
                return new DirectoryInfo(dir)
                    .GetFiles("*.mp4")
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .Take(maxCount)
                    .Select(f => new RecordingEntry(f.FullName, f.Name, f.CreationTime, f.Length))
                    .ToList();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return new List<RecordingEntry>();
            }
        }

        public async Task<bool> StartAsync(ScreenCaptureOptions options)
        {
            if (_isRecording) return false;
            LastError = null;

            try
            {
                if (!GraphicsCaptureSession.IsSupported())
                {
                    LastError = L.T("screencapture.err_wgc_unsupported");
                    StateChanged?.Invoke();
                    return false;
                }

                var screens = Forms.Screen.AllScreens;
                if (screens.Length == 0)
                {
                    LastError = L.T("screencapture.err_no_screen");
                    StateChanged?.Invoke();
                    return false;
                }

                int idx = Math.Clamp(options.MonitorIndex, 0, screens.Length - 1);
                var screen = screens[idx];
                IntPtr hMonitor = GetMonitorHandle(screen);
                if (hMonitor == IntPtr.Zero)
                {
                    LastError = L.T("screencapture.err_no_monitor");
                    StateChanged?.Invoke();
                    return false;
                }

                _d3dDevice = CreateDirect3DDevice();
                _captureItem = CreateCaptureItemForMonitor(hMonitor);

                int srcW = Math.Max(2, _captureItem.Size.Width & ~1);
                int srcH = Math.Max(2, _captureItem.Size.Height & ~1);
                _fps = Math.Clamp(options.Fps, 15, 60);

                _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _d3dDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    3,
                    _captureItem.Size);

                _framePool.FrameArrived += OnFrameArrived;

                _captureSession = _framePool.CreateCaptureSession(_captureItem);
                _captureSession.IsCursorCaptureEnabled = options.ShowCursor;

                _audioRecorder = new WasapiAudioRecorder(options.IncludeSystemAudio, options.IncludeMicrophone);
                _audioRecorder.Start();

                string fileName = $"Aufnahme_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
                string filePath = Path.Combine(RecordingsFolder, fileName);
                CurrentFilePath = filePath;

                // Zieldatei vorab anlegen für StorageFile.GetFileFromPathAsync
                await File.WriteAllBytesAsync(filePath, Array.Empty<byte>());
                var storageFile = await StorageFile.GetFileFromPathAsync(filePath);
                var outStream = await storageFile.OpenAsync(FileAccessMode.ReadWrite);

                var videoProps = VideoEncodingProperties.CreateUncompressed(
                    MediaEncodingSubtypes.Bgra8,
                    (uint)srcW,
                    (uint)srcH);
                videoProps.FrameRate.Numerator = (uint)_fps;
                videoProps.FrameRate.Denominator = 1;

                var mss = new MediaStreamSource(new VideoStreamDescriptor(videoProps))
                {
                    IsLive = true,
                    BufferTime = TimeSpan.Zero
                };

                if (_audioRecorder.HasAudio)
                {
                    var audioProps = AudioEncodingProperties.CreatePcm(48000, 2, 16);
                    mss.AddStreamDescriptor(new AudioStreamDescriptor(audioProps));
                }

                _videoFrameIndex = 0;
                _audioSampleIndex = 0;
                _stopping = false;
                _recordCts = new CancellationTokenSource();

                mss.SampleRequested += OnSampleRequested;

                var profile = BuildEncodingProfile(srcW, srcH, _fps, options.Quality, _audioRecorder.HasAudio);
                var transcoder = new MediaTranscoder();
                var prep = await transcoder.PrepareMediaStreamSourceTranscodeAsync(mss, outStream, profile);

                if (!prep.CanTranscode)
                {
                    outStream.Dispose();
                    throw new InvalidOperationException(L.F("screencapture.err_transcode", prep.FailureReason));
                }

                _captureSession.StartCapture();
                _stopwatch = Stopwatch.StartNew();
                _isRecording = true;
                IsAnyRecording = true;
                StateChanged?.Invoke();

                _transcodeTask = Task.Run(async () =>
                {
                    try
                    {
                        await prep.TranscodeAsync().AsTask(_recordCts.Token);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        App.LogError(ex);
                        LastError = ex.Message;
                    }
                    finally
                    {
                        outStream.Dispose();
                        if (_isRecording && !_stopping)
                        {
                            CleanupResources();
                            _isRecording = false;
                            IsAnyRecording = false;
                            StateChanged?.Invoke();
                        }
                    }
                });

                return true;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                LastError = ex.Message;
                CleanupResources();
                _isRecording = false;
                IsAnyRecording = false;
                StateChanged?.Invoke();
                return false;
            }
        }

        public async Task StopAsync()
        {
            if (!_isRecording) return;

            _stopping = true;
            _stopwatch?.Stop();

            try
            {
                if (_transcodeTask != null)
                {
                    var completed = await Task.WhenAny(_transcodeTask, Task.Delay(4000));
                    if (completed != _transcodeTask)
                        _recordCts?.Cancel();
                    await _transcodeTask;
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
            finally
            {
                CleanupResources();
                _isRecording = false;
                IsAnyRecording = false;
                StateChanged?.Invoke();
            }
        }

        private async void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            using var frame = sender.TryGetNextFrame();
            if (frame == null || _stopping) return;

            if (Interlocked.Exchange(ref _copyingFrame, 1) == 1)
                return;

            try
            {
                using var bmp = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);
                int width = bmp.PixelWidth;
                int height = bmp.PixelHeight;
                int stride = width * 4;
                int byteCount = stride * height;

                // MediaStreamSource erwartet Bgra8 (RGB32) standardmäßig bottom-up; SoftwareBitmap liefert top-down.
                var raw = new byte[byteCount];
                bmp.CopyToBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(raw));

                var flipped = new byte[byteCount];
                for (int y = 0; y < height; y++)
                {
                    System.Buffer.BlockCopy(raw, y * stride, flipped, (height - 1 - y) * stride, stride);
                }

                var buffer = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(flipped);

                lock (_frameLock)
                {
                    _latestVideoBuffer = buffer;
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _copyingFrame, 0);
            }
        }

        private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            if (_stopping)
            {
                args.Request.Sample = null;
                return;
            }

            if (args.Request.StreamDescriptor is VideoStreamDescriptor)
            {
                var frameDuration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / _fps);
                var targetTime = TimeSpan.FromTicks(_videoFrameIndex * frameDuration.Ticks);

                if (_stopwatch != null)
                {
                    int waitMs = (int)(targetTime - _stopwatch.Elapsed).TotalMilliseconds;
                    if (waitMs > 1 && !_stopping)
                        Thread.Sleep(Math.Min(waitMs, 50));
                }

                if (_stopping)
                {
                    args.Request.Sample = null;
                    return;
                }

                // Auf erstes Frame warten (max 500 ms)
                int tries = 0;
                IBuffer? videoBuffer = null;
                while (videoBuffer == null && tries < 50 && !_stopping)
                {
                    lock (_frameLock)
                    {
                        videoBuffer = _latestVideoBuffer;
                    }
                    if (videoBuffer == null)
                    {
                        Thread.Sleep(10);
                        tries++;
                    }
                }

                if (videoBuffer == null || _stopping)
                {
                    args.Request.Sample = null;
                    return;
                }

                var sample = MediaStreamSample.CreateFromBuffer(videoBuffer, targetTime);
                sample.Duration = frameDuration;
                sample.KeyFrame = _videoFrameIndex % _fps == 0;
                _videoFrameIndex++;
                args.Request.Sample = sample;
            }
            else if (args.Request.StreamDescriptor is AudioStreamDescriptor && _audioRecorder != null)
            {
                const int framesPerChunk = 960; // 20 ms bei 48 kHz
                var chunkDuration = TimeSpan.FromMilliseconds(20);
                var timestamp = TimeSpan.FromTicks(_audioSampleIndex * chunkDuration.Ticks);

                if (_stopwatch != null)
                {
                    int waitMs = (int)(timestamp - _stopwatch.Elapsed).TotalMilliseconds;
                    if (waitMs > 1 && !_stopping)
                        Thread.Sleep(Math.Min(waitMs, 30));
                }

                if (_stopping)
                {
                    args.Request.Sample = null;
                    return;
                }

                byte[] pcm = _audioRecorder.ReadPcmChunk(framesPerChunk);
                var buffer = Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(pcm);
                var sample = MediaStreamSample.CreateFromBuffer(buffer, timestamp);
                sample.Duration = chunkDuration;
                _audioSampleIndex++;
                args.Request.Sample = sample;
            }
        }

        private static MediaEncodingProfile BuildEncodingProfile(
            int srcW,
            int srcH,
            int fps,
            CaptureQuality quality,
            bool hasAudio)
        {
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);

            int maxHeight = quality switch
            {
                CaptureQuality.Low480p => 480,
                CaptureQuality.Medium720p => 720,
                CaptureQuality.High1080p => 1080,
                _ => srcH
            };

            uint bitrate = quality switch
            {
                CaptureQuality.Low480p => 2_500_000,
                CaptureQuality.Medium720p => 5_000_000,
                CaptureQuality.High1080p => 9_000_000,
                _ => 14_000_000
            };

            int dstH = Math.Min(srcH, maxHeight);
            int dstW = (int)Math.Round(srcW * (dstH / (double)srcH));
            // H.264 erfordert gerade Dimensionen
            dstW = Math.Max(2, dstW & ~1);
            dstH = Math.Max(2, dstH & ~1);

            profile.Video.Width = (uint)dstW;
            profile.Video.Height = (uint)dstH;
            profile.Video.Bitrate = bitrate;
            profile.Video.FrameRate.Numerator = (uint)fps;
            profile.Video.FrameRate.Denominator = 1;

            if (!hasAudio)
                profile.Audio = null;
            else
                profile.Audio = AudioEncodingProperties.CreateAac(48000, 2, 160000);

            return profile;
        }

        private void CleanupResources()
        {
            try { _audioRecorder?.Dispose(); } catch { }
            _audioRecorder = null;

            try
            {
                if (_framePool != null)
                    _framePool.FrameArrived -= OnFrameArrived;
            }
            catch { }

            try { _captureSession?.Dispose(); } catch { }
            _captureSession = null;

            try { _framePool?.Dispose(); } catch { }
            _framePool = null;

            lock (_frameLock)
            {
                _latestVideoBuffer = null;
            }

            _captureItem = null;
            try { _d3dDevice?.Dispose(); } catch { }
            _d3dDevice = null;

            try { _recordCts?.Dispose(); } catch { }
            _recordCts = null;
            _transcodeTask = null;
        }

        public void Dispose()
        {
            _stopping = true;
            _recordCts?.Cancel();
            CleanupResources();
        }

        private static IntPtr GetMonitorHandle(Forms.Screen screen)
        {
            var pt = new POINT
            {
                X = screen.Bounds.Left + screen.Bounds.Width / 2,
                Y = screen.Bounds.Top + screen.Bounds.Height / 2
            };
            return MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
        }

        private static IDirect3DDevice CreateDirect3DDevice()
        {
            int hr = D3D11CreateDevice(
                IntPtr.Zero,
                1 /* D3D_DRIVER_TYPE_HARDWARE */,
                IntPtr.Zero,
                0x20 /* D3D11_CREATE_DEVICE_BGRA_SUPPORT */,
                IntPtr.Zero,
                0,
                7 /* D3D11_SDK_VERSION */,
                out IntPtr d3dDevice,
                out _,
                out IntPtr d3dContext);

            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);

            try
            {
                var iidMultithread = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
                if (Marshal.QueryInterface(d3dDevice, in iidMultithread, out IntPtr mtPtr) == 0)
                {
                    try
                    {
                        var mt = (ID3D10Multithread)Marshal.GetObjectForIUnknown(mtPtr);
                        mt.SetMultithreadProtected(true);
                    }
                    finally
                    {
                        Marshal.Release(mtPtr);
                    }
                }

                var iidDxgi = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, in iidDxgi, out IntPtr dxgiDevice));
                try
                {
                    Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr graphicsDevice));
                    try
                    {
                        return MarshalInspectable<IDirect3DDevice>.FromAbi(graphicsDevice);
                    }
                    finally
                    {
                        Marshal.Release(graphicsDevice);
                    }
                }
                finally
                {
                    Marshal.Release(dxgiDevice);
                }
            }
            finally
            {
                if (d3dContext != IntPtr.Zero) Marshal.Release(d3dContext);
                if (d3dDevice != IntPtr.Zero) Marshal.Release(d3dDevice);
            }
        }

        private static GraphicsCaptureItem CreateCaptureItemForMonitor(IntPtr hMonitor)
        {
            var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
            var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
            Marshal.ThrowExceptionForHR(interop.CreateForMonitor(hMonitor, in iid, out IntPtr itemPtr));
            try
            {
                return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
            }
            finally
            {
                if (itemPtr != IntPtr.Zero) Marshal.Release(itemPtr);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(
            IntPtr pAdapter,
            int driverType,
            IntPtr software,
            uint flags,
            IntPtr pFeatureLevels,
            uint featureLevels,
            uint sdkVersion,
            out IntPtr ppDevice,
            out int pFeatureLevel,
            out IntPtr ppImmediateContext);

        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        [ComImport, Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ID3D10Multithread
        {
            void Enter();
            void Leave();
            [PreserveSig]
            bool SetMultithreadProtected([MarshalAs(UnmanagedType.Bool)] bool bMTProtect);
        }

        [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            [PreserveSig]
            int CreateForWindow(IntPtr window, in Guid iid, out IntPtr result);
            [PreserveSig]
            int CreateForMonitor(IntPtr monitor, in Guid iid, out IntPtr result);
        }
    }
}
