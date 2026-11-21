using System;

namespace WindowsNotch.Modules.ScreenCapture
{
    public enum CaptureQuality
    {
        Low480p,
        Medium720p,
        High1080p,
        Original
    }

    public sealed class ScreenCaptureOptions
    {
        public int MonitorIndex { get; set; }
        public int Fps { get; set; } = 30;
        public CaptureQuality Quality { get; set; } = CaptureQuality.High1080p;
        public bool IncludeSystemAudio { get; set; } = true;
        public bool IncludeMicrophone { get; set; } = true;
        public bool ShowCursor { get; set; } = true;
    }

    public sealed record RecordingEntry(
        string FilePath,
        string FileName,
        DateTime CreatedAt,
        long FileSizeBytes)
    {
        public string FormattedSize => FileSizeBytes switch
        {
            < 1024 => $"{FileSizeBytes} B",
            < 1024 * 1024 => $"{FileSizeBytes / 1024.0:F0} KB",
            _ => $"{FileSizeBytes / (1024.0 * 1024.0):F1} MB"
        };
    }
}
