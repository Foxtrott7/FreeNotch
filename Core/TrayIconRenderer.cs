using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Forms = System.Windows.Forms;

namespace WindowsNotch.Core
{
    /// <summary>
    /// Rendert das FreeNotch-Tray-Icon als abgerundetes App-Badge mit Notch-Silhouette und „FN“-Monogramm.
    /// </summary>
    internal static class TrayIconRenderer
    {
        public static Icon CreateTrayIcon(System.Windows.Media.Color accentColor, bool muted = false)
        {
            try
            {
                return RenderIcon(accentColor, muted);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return (Icon)SystemIcons.Application.Clone();
            }
        }

        private static Icon RenderIcon(System.Windows.Media.Color accentColor, bool muted)
        {
            int size = Math.Max(32, Forms.SystemInformation.SmallIconSize.Width);
            float s = size / 32f;

            Color badgeAccent = muted
                ? Color.FromArgb(0xFF, 0x71, 0x71, 0x7A)
                : Color.FromArgb(0xFF, accentColor.R, accentColor.G, accentColor.B);
            Color fgColor = muted
                ? Color.FromArgb(0xFF, 0xD4, 0xD4, 0xD8)
                : Color.White;

            using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);

                // Abgerundetes OLED-App-Badge mit subtilem Akzent-Schimmer unten
                var badgeRect = new RectangleF(2f * s, 2f * s, 28f * s, 28f * s);
                using var badgePath = CreateRoundedRectPath(badgeRect, 7.5f * s);
                Color topBg = Color.FromArgb(0xFF, 0x0A, 0x0A, 0x0E);
                Color bottomBg = Color.FromArgb(
                    0xFF,
                    (byte)(0x0A + (badgeAccent.R - 0x0A) * 0.16f),
                    (byte)(0x0A + (badgeAccent.G - 0x0A) * 0.16f),
                    (byte)(0x0E + (badgeAccent.B - 0x0E) * 0.16f));
                using var bgBrush = new LinearGradientBrush(badgeRect, topBg, bottomBg, LinearGradientMode.Vertical);
                g.FillPath(bgBrush, badgePath);

                // Oben angedockte Notch-Silhouette in Akzentfarbe
                var notchRect = new RectangleF(10f * s, 2.2f * s, 12f * s, 3.6f * s);
                using var notchPath = CreateNotchPath(notchRect, 1.8f * s);
                using var notchBrush = new SolidBrush(badgeAccent);
                g.FillPath(notchBrush, notchPath);

                // Kontraststarker Akzent-Rahmen um das Badge
                using var borderPen = new Pen(badgeAccent, 2f * s);
                g.DrawPath(borderPen, badgePath);

                // Geometrisches „FN“-Monogramm (gestochen scharf auch bei 16×16)
                using var fgPen = new Pen(fgColor, 2.5f * s)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                };

                // „F“
                g.DrawLine(fgPen, 8.2f * s, 11.2f * s, 8.2f * s, 22.8f * s);
                g.DrawLine(fgPen, 8.2f * s, 11.2f * s, 14.2f * s, 11.2f * s);
                g.DrawLine(fgPen, 8.2f * s, 16.8f * s, 12.8f * s, 16.8f * s);

                // „N“
                g.DrawLine(fgPen, 17.4f * s, 22.8f * s, 17.4f * s, 11.2f * s);
                g.DrawLine(fgPen, 17.4f * s, 11.2f * s, 23.4f * s, 22.8f * s);
                g.DrawLine(fgPen, 23.4f * s, 22.8f * s, 23.4f * s, 11.2f * s);
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
