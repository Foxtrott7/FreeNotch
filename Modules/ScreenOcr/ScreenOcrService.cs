using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using ZXing;
using ZXing.Common;
using WindowsNotch.Services;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace WindowsNotch.Modules.ScreenOcr
{
    public sealed record ScreenAnalysisResult(
        string Text,
        string LanguageTag,
        string? QrCodeText);

    /// <summary>
    /// Stellt Bildschirm-OCR (Windows.Media.Ocr), QR-Code-Erkennung (ZXing.Net)
    /// sowie Bild-, Such- und Übersetzungsaktionen bereit.
    /// </summary>
    public static class ScreenOcrService
    {
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        /// <summary>Erfasst einen einzelnen Monitor in physischen Pixeln.</summary>
        public static Bitmap CaptureScreen(System.Windows.Forms.Screen screen)
        {
            var b = screen.Bounds;
            var bmp = new Bitmap(b.Width, b.Height, DrawingPixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }

        /// <summary>Konvertiert eine GDI-Bitmap in eine eingefrorene WPF-BitmapSource.</summary>
        public static BitmapSource ToBitmapSource(Bitmap bitmap)
        {
            IntPtr hBitmap = bitmap.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }

        /// <summary>Führt parallel OCR und QR-Code-Erkennung auf dem Ausschnitt aus.</summary>
        public static async Task<ScreenAnalysisResult> AnalyzeAsync(Bitmap bitmap)
        {
            // Bitmap für Hintergrund-Threads klonen, damit es nicht während UI-Zugriffen gesperrt wird
            using var ocrCopy = new Bitmap(bitmap);
            using var qrCopy = new Bitmap(bitmap);

            var ocrTask = RecognizeTextAsync(ocrCopy);
            var qrTask = Task.Run(() => DecodeQr(qrCopy));

            await Task.WhenAll(ocrTask, qrTask).ConfigureAwait(false);
            var (text, lang) = await ocrTask.ConfigureAwait(false);
            var qr = await qrTask.ConfigureAwait(false);

            return new ScreenAnalysisResult(text, lang, qr);
        }

        private static async Task<(string Text, string LanguageTag)> RecognizeTextAsync(Bitmap bitmap)
        {
            try
            {
                var engine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (engine == null)
                {
                    Language? fallback = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
                    if (fallback == null)
                        return (string.Empty, string.Empty);
                    engine = OcrEngine.TryCreateFromLanguage(fallback);
                }

                if (engine == null)
                    return (string.Empty, string.Empty);

                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                byte[] bytes = ms.ToArray();

                using var ras = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                }

                ras.Seek(0);
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
                using SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied);

                OcrResult result = await engine.RecognizeAsync(softwareBitmap);
                string text = string.Join(
                    Environment.NewLine,
                    result.Lines.Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)));

                return (text.Trim(), engine.RecognizerLanguage.DisplayName);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return (string.Empty, string.Empty);
            }
        }

        private static string? DecodeQr(Bitmap bitmap)
        {
            try
            {
                // Bei sehr kleinen Ausschnitten leicht hochskalieren, damit dichte QR-Codes sauber dekodiert werden
                Bitmap source = bitmap;
                Bitmap? upscaled = null;
                if (bitmap.Width < 160 || bitmap.Height < 160)
                {
                    upscaled = new Bitmap(bitmap.Width * 2, bitmap.Height * 2, DrawingPixelFormat.Format32bppArgb);
                    using var g = Graphics.FromImage(upscaled);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.DrawImage(bitmap, 0, 0, upscaled.Width, upscaled.Height);
                    source = upscaled;
                }

                try
                {
                    var rect = new Rectangle(0, 0, source.Width, source.Height);
                    BitmapData data = source.LockBits(rect, ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
                    byte[] buffer;
                    try
                    {
                        int byteCount = Math.Abs(data.Stride) * source.Height;
                        buffer = new byte[byteCount];
                        Marshal.Copy(data.Scan0, buffer, 0, byteCount);
                    }
                    finally
                    {
                        source.UnlockBits(data);
                    }

                    var luminance = new RGBLuminanceSource(
                        buffer,
                        source.Width,
                        source.Height,
                        RGBLuminanceSource.BitmapFormat.BGRA32);

                    var reader = new BarcodeReaderGeneric
                    {
                        AutoRotate = true,
                        Options = new DecodingOptions
                        {
                            TryHarder = true,
                            PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }
                        }
                    };

                    var result = reader.Decode(luminance);
                    return string.IsNullOrWhiteSpace(result?.Text) ? null : result.Text.Trim();
                }
                finally
                {
                    upscaled?.Dispose();
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return null;
            }
        }

        public static void CopyImageToClipboard(Bitmap bitmap)
        {
            var src = ToBitmapSource(bitmap);
            System.Windows.Clipboard.SetImage(src);
        }

        public static bool SaveImageWithDialog(Bitmap bitmap)
        {
            var dlg = new SaveFileDialog
            {
                Title = L.T("screenocr.save_dialog_title"),
                Filter = L.T("screenocr.save_dialog_filter"),
                DefaultExt = ".png",
                FileName = $"WindowsNotch_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            };

            if (dlg.ShowDialog() != true)
                return false;

            var format = dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                         dlg.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                ? ImageFormat.Jpeg
                : ImageFormat.Png;

            bitmap.Save(dlg.FileName, format);
            return true;
        }

        public static void OpenGoogleSearch(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string url = "https://www.google.com/search?q=" + Uri.EscapeDataString(text.Trim());
            OpenUrl(url);
        }

        public static void OpenTranslate(string text, string targetLang = "de")
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string url = $"https://translate.google.com/?sl=auto&tl={Uri.EscapeDataString(targetLang)}&text={Uri.EscapeDataString(text.Trim())}&op=translate";
            OpenUrl(url);
        }

        public static void OpenGoogleImageSearch(Bitmap bitmap)
        {
            CopyImageToClipboard(bitmap);
            OpenUrl("https://lens.google.com/");
        }

        public static bool IsNavigableUri(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp ||
                    uri.Scheme == Uri.UriSchemeHttps ||
                    uri.Scheme == Uri.UriSchemeMailto ||
                    string.Equals(uri.Scheme, "tel", StringComparison.OrdinalIgnoreCase));
        }

        public static void OpenUrl(string url)
        {
            if (!IsNavigableUri(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo(url.Trim()) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }
    }
}
