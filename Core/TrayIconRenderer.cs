using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace WindowsNotch.Core
{
    /// <summary>
    /// Rendert ein kontrastangepasstes Vektor-Tray-Icon im Notchify-Design für helle und dunkle Taskleisten.
    /// </summary>
    internal static class TrayIconRenderer
    {
        private const string PersonalizeKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        public static Icon CreateTrayIcon(System.Windows.Media.Color accentColor, bool muted = false)
        {
            try
            {
                return RenderIcon(accentColor, muted, IsLightTaskbar());
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return (Icon)SystemIcons.Application.Clone();
            }
        }

        private static Icon RenderIcon(System.Windows.Media.Color accentColor, bool muted, bool lightTaskbar)
        {
            int size = Math.Max(32, Forms.SystemInformation.SmallIconSize.Width);
            float s = size / 32f;

            using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);

                var frameRect = new RectangleF(3f * s, 5f * s, 26f * s, 22f * s);
                using var framePath = CreateRoundedRectPath(frameRect, 5.5f * s);

                // Dezente Glasfläche passend zum Taskleisten-Kontrast
                Color glassTop = lightTaskbar
                    ? Color.FromArgb(30, 18, 18, 24)
                    : Color.FromArgb(44, 255, 255, 255);
                Color glassBottom = lightTaskbar
                    ? Color.FromArgb(14, 18, 18, 24)
                    : Color.FromArgb(16, 255, 255, 255);
                using var glassBrush = new LinearGradientBrush(frameRect, glassTop, glassBottom, LinearGradientMode.Vertical);
                g.FillPath(glassBrush, framePath);

                // Oben angedockte OLED-Notch mit feinem Kontur-Saum für Erkennbarkeit bei 16x16
                var notchRect = new RectangleF(8f * s, 5.25f * s, 16f * s, 10.75f * s);
                using var notchPath = CreateNotchPath(notchRect, 4.4f * s);
                using var notchBrush = new SolidBrush(Color.FromArgb(0xFF, 0x09, 0x09, 0x0B));
                g.FillPath(notchBrush, notchPath);

                Color rimColor = lightTaskbar
                    ? Color.FromArgb(85, 255, 255, 255)
                    : Color.FromArgb(145, 232, 232, 240);
                using var rimPen = new Pen(rimColor, 1.1f * s);
                g.DrawPath(rimPen, notchPath);

                // Display-Rahmen zuletzt zeichnen, damit die Notch oben bündig andockt
                Color frameColor = lightTaskbar
                    ? Color.FromArgb(238, 24, 24, 28)
                    : Color.FromArgb(242, 240, 240, 246);
                using var framePen = new Pen(frameColor, 2f * s);
                g.DrawPath(framePen, framePath);

                // Zentrierter Akzent- oder Stumm-Statuspunkt in der Notch (ausgerichtet auf 16x16-Raster)
                Color dotColor = muted
                    ? Color.FromArgb(0xFF, 0x71, 0x71, 0x7A)
                    : Color.FromArgb(0xFF, accentColor.R, accentColor.G, accentColor.B);
                float dotDiameter = 6f * s;
                float dotX = (size - dotDiameter) * 0.5f;
                float dotY = 8f * s;
                using var dotBrush = new SolidBrush(dotColor);
                g.FillEllipse(dotBrush, dotX, dotY, dotDiameter, dotDiameter);
            }

            IntPtr hIcon = bmp.GetHicon();
            try
            {
                using var temp = Icon.FromHandle(hIcon);
                return (Icon)temp.Clone();
            }
            finally
            {
                if (hIcon != IntPtr.Zero)
                    NativeMethods.DestroyIcon(hIcon);
            }
        }

        private static bool IsLightTaskbar()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
                return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return false;
            }
        }

        private static GraphicsPath CreateRoundedRectPath(RectangleF rect, float radius)
        {
            float d = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
            var path = new GraphicsPath();
            path.AddArc(rect.Left, rect.Top, d, d, 180f, 90f);
            path.AddArc(rect.Right - d, rect.Top, d, d, 270f, 90f);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0f, 90f);
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        private static GraphicsPath CreateNotchPath(RectangleF rect, float bottomRadius)
        {
            float r = Math.Min(bottomRadius, Math.Min(rect.Width * 0.5f, rect.Height - 1f));
            float d = r * 2f;
            var path = new GraphicsPath();
            path.AddLine(rect.Left, rect.Top, rect.Right, rect.Top);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0f, 90f);
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }
    }
}
