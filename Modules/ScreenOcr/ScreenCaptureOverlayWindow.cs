using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using WindowsNotch.Core;
using WindowsNotch.Services;
using Forms = System.Windows.Forms;
using MediaColor = System.Windows.Media.Color;
using MediaPoint = System.Windows.Point;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfImage = System.Windows.Controls.Image;

namespace WindowsNotch.Modules.ScreenOcr
{
    public sealed record ScreenRegionSelection(Bitmap Bitmap, Forms.Screen Screen);

    /// <summary>
    /// Multi-Monitor- und Per-Monitor-DPI-fähiges Overlay zur freien Auswahl eines Bildschirmausschnitts.
    /// </summary>
    public static class ScreenRegionSelector
    {
        public static Task<ScreenRegionSelection?> SelectRegionAsync()
        {
            var tcs = new TaskCompletionSource<ScreenRegionSelection?>();
            var screens = Forms.Screen.AllScreens;
            var cursorScreen = Forms.Screen.FromPoint(Forms.Cursor.Position);
            var windows = new List<ScreenCaptureOverlayWindow>(screens.Length);
            bool finished = false;

            void Finish(ScreenRegionSelection? selection)
            {
                if (finished) return;
                finished = true;

                foreach (var w in windows)
                {
                    try { w.Close(); } catch { }
                }

                tcs.TrySetResult(selection);
            }

            foreach (var screen in screens)
            {
                var screenshot = ScreenOcrService.CaptureScreen(screen);
                bool isCursorMonitor = string.Equals(screen.DeviceName, cursorScreen.DeviceName, StringComparison.OrdinalIgnoreCase);
                var overlay = new ScreenCaptureOverlayWindow(screen, screenshot, isCursorMonitor, Finish);
                windows.Add(overlay);
            }

            foreach (var w in windows)
                w.Show();

            // Das Fenster unter dem Mauszeiger aktivieren, damit Esc sofort greift
            var activeWin = windows.Find(w => w.IsCursorMonitor) ?? (windows.Count > 0 ? windows[0] : null);
            activeWin?.Activate();
            activeWin?.Focus();

            return tcs.Task;
        }
    }

    internal sealed class ScreenCaptureOverlayWindow : Window
    {
        private readonly Forms.Screen _screen;
        private readonly Bitmap _screenshot;
        private readonly Action<ScreenRegionSelection?> _onFinish;

        private readonly Canvas _canvas;
        private readonly Path _dimPath;
        private readonly Border _selectionFrame;
        private readonly Border _sizeBadge;
        private readonly TextBlock _sizeText;
        private readonly Border _hintPill;

        private bool _isDragging;
        private MediaPoint _startPoint;
        private Rect _selectionDip = Rect.Empty;

        public bool IsCursorMonitor { get; }

        public ScreenCaptureOverlayWindow(
            Forms.Screen screen,
            Bitmap screenshot,
            bool isCursorMonitor,
            Action<ScreenRegionSelection?> onFinish)
        {
            _screen = screen;
            _screenshot = screenshot;
            IsCursorMonitor = isCursorMonitor;
            _onFinish = onFinish;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = WpfBrushes.Black;
            Cursor = Cursors.Cross;

            var root = new Grid();

            var bgImage = new WpfImage
            {
                Source = ScreenOcrService.ToBitmapSource(_screenshot),
                Stretch = Stretch.Fill,
                SnapsToDevicePixels = true
            };
            root.Children.Add(bgImage);

            _dimPath = new Path
            {
                Fill = new SolidColorBrush(MediaColor.FromArgb(0x8C, 0x00, 0x00, 0x00)),
                IsHitTestVisible = false
            };
            root.Children.Add(_dimPath);

            _canvas = new Canvas { IsHitTestVisible = false };

            _selectionFrame = new Border
            {
                BorderBrush = NotchDesign.AccentPrimaryBrush,
                BorderThickness = new Thickness(1.5),
                Visibility = Visibility.Collapsed
            };
            _canvas.Children.Add(_selectionFrame);

            _sizeText = new TextBlock
            {
                Foreground = WpfBrushes.White,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold
            };
            _sizeBadge = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromArgb(0xE6, 0x0A, 0x0A, 0x0E)),
                BorderBrush = new SolidColorBrush(MediaColor.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 3, 8, 3),
                Visibility = Visibility.Collapsed,
                Child = _sizeText
            };
            _canvas.Children.Add(_sizeBadge);

            root.Children.Add(_canvas);

            _hintPill = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromArgb(0xDC, 0x0A, 0x0A, 0x0E)),
                BorderBrush = new SolidColorBrush(MediaColor.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 7, 14, 7),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 22, 0, 0),
                IsHitTestVisible = false,
                Visibility = isCursorMonitor ? Visibility.Visible : Visibility.Collapsed,
                Child = new TextBlock
                {
                    Text = L.T("screenocr.overlay_hint"),
                    Foreground = WpfBrushes.White,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 12,
                    FontWeight = FontWeights.Medium
                }
            };
            root.Children.Add(_hintPill);

            Content = root;

            SourceInitialized += OnSourceInitialized;
            Loaded += (_, _) => UpdateDimGeometry();
            SizeChanged += (_, _) => UpdateDimGeometry();
            PreviewKeyDown += OnPreviewKeyDown;
            MouseLeftButtonDown += OnMouseLeftButtonDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnMouseLeftButtonUp;
            MouseRightButtonUp += (_, _) => _onFinish(null);
            Closed += (_, _) => _screenshot.Dispose();
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            var b = _screen.Bounds;
            uint flags = 0x0040; // SWP_SHOWWINDOW
            if (!IsCursorMonitor)
                flags |= NativeMethods.SWP_NOACTIVATE;

            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, flags);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _onFinish(null);
            }
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
            _startPoint = e.GetPosition(this);
            _selectionDip = new Rect(_startPoint, new System.Windows.Size(0, 0));
            _hintPill.Visibility = Visibility.Collapsed;
            CaptureMouse();
            UpdateSelectionVisuals();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;
            var current = e.GetPosition(this);
            double x = Math.Max(0, Math.Min(_startPoint.X, current.X));
            double y = Math.Max(0, Math.Min(_startPoint.Y, current.Y));
            double maxX = Math.Min(ActualWidth, Math.Max(_startPoint.X, current.X));
            double maxY = Math.Min(ActualHeight, Math.Max(_startPoint.Y, current.Y));

            _selectionDip = new Rect(x, y, Math.Max(0, maxX - x), Math.Max(0, maxY - y));
            UpdateSelectionVisuals();
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            ReleaseMouseCapture();

            if (ActualWidth <= 0 || ActualHeight <= 0)
            {
                _onFinish(null);
                return;
            }

            double scaleX = _screenshot.Width / ActualWidth;
            double scaleY = _screenshot.Height / ActualHeight;

            int px = Math.Clamp((int)Math.Round(_selectionDip.X * scaleX), 0, _screenshot.Width - 1);
            int py = Math.Clamp((int)Math.Round(_selectionDip.Y * scaleY), 0, _screenshot.Height - 1);
            int pw = Math.Clamp((int)Math.Round(_selectionDip.Width * scaleX), 0, _screenshot.Width - px);
            int ph = Math.Clamp((int)Math.Round(_selectionDip.Height * scaleY), 0, _screenshot.Height - py);

            if (pw < 8 || ph < 8)
            {
                // Zu kleiner Klick: Auswahl zurücksetzen, damit Nutzer neu ziehen kann
                _selectionDip = Rect.Empty;
                UpdateSelectionVisuals();
                return;
            }

            var cropped = _screenshot.Clone(new System.Drawing.Rectangle(px, py, pw, ph), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            _onFinish(new ScreenRegionSelection(cropped, _screen));
        }

        private void UpdateSelectionVisuals()
        {
            UpdateDimGeometry();

            if (_selectionDip.IsEmpty || _selectionDip.Width < 2 || _selectionDip.Height < 2)
            {
                _selectionFrame.Visibility = Visibility.Collapsed;
                _sizeBadge.Visibility = Visibility.Collapsed;
                return;
            }

            Canvas.SetLeft(_selectionFrame, _selectionDip.X);
            Canvas.SetTop(_selectionFrame, _selectionDip.Y);
            _selectionFrame.Width = _selectionDip.Width;
            _selectionFrame.Height = _selectionDip.Height;
            _selectionFrame.Visibility = Visibility.Visible;

            if (ActualWidth > 0 && ActualHeight > 0)
            {
                int pw = (int)Math.Round(_selectionDip.Width * (_screenshot.Width / ActualWidth));
                int ph = (int)Math.Round(_selectionDip.Height * (_screenshot.Height / ActualHeight));
                _sizeText.Text = $"{pw} × {ph} px";
            }

            _sizeBadge.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double badgeW = _sizeBadge.DesiredSize.Width;
            double badgeH = _sizeBadge.DesiredSize.Height;

            double badgeLeft = Math.Clamp(_selectionDip.X, 8, Math.Max(8, ActualWidth - badgeW - 8));
            double badgeTop = _selectionDip.Y - badgeH - 6;
            if (badgeTop < 8)
                badgeTop = Math.Min(ActualHeight - badgeH - 8, _selectionDip.Bottom + 6);

            Canvas.SetLeft(_sizeBadge, badgeLeft);
            Canvas.SetTop(_sizeBadge, badgeTop);
            _sizeBadge.Visibility = Visibility.Visible;
        }

        private void UpdateDimGeometry()
        {
            double w = ActualWidth > 0 ? ActualWidth : _screen.Bounds.Width;
            double h = ActualHeight > 0 ? ActualHeight : _screen.Bounds.Height;
            var outer = new RectangleGeometry(new Rect(0, 0, w, h));

            if (_selectionDip.IsEmpty || _selectionDip.Width < 1 || _selectionDip.Height < 1)
            {
                _dimPath.Data = outer;
                return;
            }

            var inner = new RectangleGeometry(_selectionDip);
            _dimPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
        }
    }
}
