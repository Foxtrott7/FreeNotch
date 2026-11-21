using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowsNotch.Core;
using WindowsNotch.Services;
using Bitmap = System.Drawing.Bitmap;
using WpfClipboard = System.Windows.Clipboard;
using WpfImage = System.Windows.Controls.Image;

namespace WindowsNotch.Modules.ScreenOcr
{
    /// <summary>
    /// Modul für Bildschirm-Ausschnitte mit OCR (Windows.Media.Ocr),
    /// QR-Code-Erkennung, Google-/Bildersuche, Übersetzung und Export.
    /// </summary>
    public class ScreenOcrModule : NotchModuleBase
    {
        private Bitmap? _currentBitmap;
        private BitmapSource? _currentPreview;
        private ScreenAnalysisResult? _analysis;
        private bool _isAnalyzing;
        private bool _isCapturing;
        private string _statusMessage = string.Empty;
        private readonly DispatcherTimer _statusTimer;

        public override string Id => "screenocr";
        public override string Title => L.T("screenocr.title");
        public override NotchIconKind IconKind => NotchIconKind.Scan;
        public override int Priority => 10;
        public override double? PreferredPreviewWidth => 235;
        public override (double Width, double Height)? PreferredExpandedSize
        {
            get
            {
                if (_currentBitmap == null)
                    return (420, 220);

                bool hasQr = !string.IsNullOrWhiteSpace(_analysis?.QrCodeText);
                double chromeW = 52 + 48; // ExpandedHost-Rand + Bild-Seitenabstand
                double chromeH = 68 + 38 + (hasQr ? 92 : 64);

                double bmpW = Math.Max(1, _currentBitmap.Width);
                double bmpH = Math.Max(1, _currentBitmap.Height);
                double scale = Math.Min(1.0, Math.Min(580.0 / bmpW, 300.0 / bmpH));

                double targetW = Math.Clamp(bmpW * scale + chromeW, 500, 680);
                double targetH = Math.Clamp(bmpH * scale + chromeH, 235, 450);
                return (Math.Round(targetW), Math.Round(targetH));
            }
        }

        private event Action? ViewUpdated;

        public ScreenOcrModule()
        {
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer.Stop();
                _statusMessage = string.Empty;
                ViewUpdated?.Invoke();
            };
        }

        public override FrameworkElement CreatePreviewView()
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var icon = NotchDesign.CreateVectorIcon(NotchIconKind.Scan, 13, NotchDesign.AccentPrimaryBrush);
            icon.Margin = new Thickness(0, 0, 7, 0);

            var text = new TextBlock
            {
                Text = _currentBitmap != null ? L.T("screenocr.preview_ready") : L.T("screenocr.preview_empty"),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };

            stack.Children.Add(icon);
            stack.Children.Add(text);
            return stack;
        }

        public override FrameworkElement CreateExpandedView()
        {
            var host = new Border();

            void Rebuild()
            {
                host.Child = BuildExpandedContent();
            }

            Rebuild();
            host.Loaded += (_, _) => ViewUpdated += Rebuild;
            host.Unloaded += (_, _) => ViewUpdated -= Rebuild;
            return host;
        }

        private FrameworkElement BuildExpandedContent()
        {
            if (_currentBitmap == null || _currentPreview == null)
                return BuildEmptyState();

            string recognizedText = _analysis?.Text ?? string.Empty;
            bool hasText = !string.IsNullOrWhiteSpace(recognizedText);
            string? qrText = _analysis?.QrCodeText;
            bool hasQr = !string.IsNullOrWhiteSpace(qrText);

            var root = new Grid();

            // Hintergrund-Fläche im ruhigen Notchify-Stil ohne Akzent-Einfärbung
            var stageCard = new Border
            {
                Background = NotchDesign.CardBackgroundBrush,
                BorderBrush = NotchDesign.CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                ClipToBounds = true
            };

            var stageGrid = new Grid();

            // 1. Oben links: dezenter Status/Maße-Hinweis + "Neu wählen"-Link
            var topBar = new DockPanel
            {
                LastChildFill = false,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(14, 10, 14, 0)
            };

            string infoText = _isAnalyzing
                ? L.F("screenocr.meta_analyzing", _currentBitmap.Width, _currentBitmap.Height)
                : !string.IsNullOrWhiteSpace(_statusMessage)
                    ? _statusMessage
                    : hasText
                        ? L.F("screenocr.meta_text_found", _currentBitmap.Width, _currentBitmap.Height)
                        : $"{_currentBitmap.Width} × {_currentBitmap.Height} px";

            var infoLabel = new TextBlock
            {
                Text = infoText,
                Foreground = !string.IsNullOrWhiteSpace(_statusMessage)
                    ? new SolidColorBrush(NotchDesign.AccentGreen)
                    : NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(infoLabel, Dock.Left);
            topBar.Children.Add(infoLabel);

            var newBtnContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var newBtnIcon = NotchDesign.CreateVectorIcon(NotchIconKind.Scan, 11, NotchDesign.TextPrimaryBrush, 1.85);
            newBtnIcon.Margin = new Thickness(0, 0, 5, 0);
            newBtnContent.Children.Add(newBtnIcon);
            newBtnContent.Children.Add(new TextBlock
            {
                Text = L.T("screenocr.reselect"),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            });

            var newBtn = NotchDesign.CreateRoundedButton(
                newBtnContent,
                () => _ = StartCaptureAsync(),
                background: NotchDesign.ControlBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                foreground: NotchDesign.TextPrimaryBrush,
                cornerRadius: 10,
                padding: new Thickness(10, 4, 10, 4));
            newBtn.BorderBrush = NotchDesign.CardBorderBrush;
            newBtn.BorderThickness = new Thickness(1);
            DockPanel.SetDock(newBtn, Dock.Right);
            topBar.Children.Add(newBtn);

            stageGrid.Children.Add(topBar);

            // 2. Mitte: Großes, abgerundetes Ausschnitt-Bild ohne störende Ränder
            var previewFrame = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24, 38, 24, hasQr ? 92 : 64),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(0),
                ClipToBounds = true,
                Child = new WpfImage
                {
                    Source = _currentPreview,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true
                }
            };
            previewFrame.SizeChanged += (_, _) =>
            {
                if (previewFrame.ActualWidth > 0 && previewFrame.ActualHeight > 0)
                    previewFrame.Clip = new RectangleGeometry(new Rect(0, 0, previewFrame.ActualWidth, previewFrame.ActualHeight), 12, 12);
            };
            stageGrid.Children.Add(previewFrame);

            // 3. Optionales kompaktes QR-Pill-Banner direkt über der Action-Leiste
            if (hasQr)
            {
                bool isUri = ScreenOcrService.IsNavigableUri(qrText);
                var qrPill = new Border
                {
                    Background = NotchDesign.NestedCardBackgroundBrush,
                    BorderBrush = NotchDesign.CardBorderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(10, 4, 8, 4),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(16, 0, 16, 56),
                    MaxWidth = 480
                };

                var qrStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var qrIcon = NotchDesign.CreateVectorIcon(NotchIconKind.QrCode, 13, NotchDesign.AccentPrimaryBrush);
                qrIcon.Margin = new Thickness(0, 0, 6, 0);
                qrStack.Children.Add(qrIcon);

                qrStack.Children.Add(new TextBlock
                {
                    Text = qrText!,
                    Foreground = NotchDesign.TextPrimaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11.5,
                    FontWeight = FontWeights.Medium,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 280,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = qrText,
                    Margin = new Thickness(0, 0, 8, 0)
                });

                qrStack.Children.Add(NotchDesign.CreateRoundedButton(
                    new TextBlock
                    {
                        Text = isUri ? L.T("screenocr.qr_open") : L.T("screenocr.qr_copy"),
                        Foreground = Brushes.White,
                        FontFamily = NotchDesign.UiTextFont,
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold
                    },
                    () =>
                    {
                        if (isUri)
                            ScreenOcrService.OpenUrl(qrText!);
                        else
                        {
                            WpfClipboard.SetText(qrText!);
                            ShowStatus(L.T("screenocr.qr_copied"));
                        }
                    },
                    background: NotchDesign.AccentPrimaryBrush,
                    hoverBackground: NotchDesign.AccentHoverBrush,
                    foreground: Brushes.White,
                    cornerRadius: 8,
                    padding: new Thickness(8, 3, 8, 3)));

                qrPill.Child = qrStack;
                stageGrid.Children.Add(qrPill);
            }

            // 4. Schwebende Aktions-Pill-Leiste unten (exakt im Notchify-Stil)
            var actionBar = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x12, 0x12, 0x17)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(6, 5, 6, 5),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(8, 0, 8, 10)
            };

            var barStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Button 1: Mit Google suchen (Textsuche wenn Text erkannt, sonst Google Lens Bildersuche)
            barStack.Children.Add(CreateBarButton(
                NotchIconKind.Search,
                L.T("screenocr.search_google"),
                () =>
                {
                    if (hasText)
                        ScreenOcrService.OpenGoogleSearch(recognizedText);
                    else
                    {
                        ScreenOcrService.OpenGoogleImageSearch(_currentBitmap);
                        ShowStatus(L.T("screenocr.lens_copied"));
                    }
                },
                isHighlighted: true,
                tooltip: hasText ? L.F("screenocr.search_google_tooltip", Truncate(recognizedText, 80)) : L.T("screenocr.lens_tooltip")));

            // Button 2: Übersetzen
            barStack.Children.Add(CreateBarButton(
                NotchIconKind.Translate,
                L.T("screenocr.translate"),
                () =>
                {
                    if (!hasText)
                    {
                        ShowStatus(L.T("screenocr.no_text_translate"));
                        return;
                    }
                    ScreenOcrService.OpenTranslate(recognizedText, LocalizationService.CurrentLanguage);
                },
                isEnabled: hasText,
                tooltip: hasText ? L.T("screenocr.translate_tooltip") : L.T("screenocr.no_text")));

            // Button 3: Text kopieren (OCR)
            barStack.Children.Add(CreateBarButton(
                NotchIconKind.Scan,
                L.T("screenocr.copy_text"),
                () =>
                {
                    if (!hasText)
                    {
                        ShowStatus(L.T("screenocr.no_text_region"));
                        return;
                    }
                    WpfClipboard.SetText(recognizedText);
                    ShowStatus(L.T("screenocr.text_copied"));
                },
                isEnabled: hasText,
                tooltip: hasText ? Truncate(recognizedText, 140) : L.T("screenocr.no_text")));

            // Button 4: Bild kopieren
            barStack.Children.Add(CreateBarButton(
                NotchIconKind.Copy,
                L.T("screenocr.copy_image"),
                () =>
                {
                    ScreenOcrService.CopyImageToClipboard(_currentBitmap);
                    ShowStatus(L.T("screenocr.image_copied"));
                },
                tooltip: L.T("screenocr.copy_image_tooltip")));

            // Button 5: Speichern
            barStack.Children.Add(CreateBarButton(
                NotchIconKind.Save,
                L.T("screenocr.save"),
                () =>
                {
                    if (ScreenOcrService.SaveImageWithDialog(_currentBitmap))
                        ShowStatus(L.T("screenocr.saved"));
                },
                tooltip: L.T("screenocr.save_tooltip")));

            // Button 6: Verwerfen / Zurücksetzen (X)
            var closeBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Close, 12, NotchDesign.TextSecondaryBrush, 1.9),
                ClearCapture,
                background: new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                hoverBackground: new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
                foreground: NotchDesign.TextSecondaryBrush,
                cornerRadius: 12,
                padding: new Thickness(7, 6, 7, 6),
                tooltip: L.T("screenocr.close_tooltip"));
            closeBtn.Margin = new Thickness(4, 0, 0, 0);
            barStack.Children.Add(closeBtn);

            actionBar.Child = barStack;
            stageGrid.Children.Add(actionBar);

            stageCard.Child = stageGrid;
            root.Children.Add(stageCard);
            return root;
        }

        private FrameworkElement BuildEmptyState()
        {
            var container = new Border
            {
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var empty = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            empty.Children.Add(new Border
            {
                Width = 48,
                Height = 48,
                CornerRadius = new CornerRadius(14),
                Background = NotchDesign.CreateSoftAccentTileBrush(0x22),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = NotchDesign.CreateVectorIcon(NotchIconKind.Scan, 24, NotchDesign.AccentPrimaryBrush, 1.85)
            });

            empty.Children.Add(new TextBlock
            {
                Text = L.T("screenocr.empty_title"),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 3)
            });

            empty.Children.Add(new TextBlock
            {
                Text = L.T("screenocr.empty_hint"),
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            container.Child = empty;
            container.MouseLeftButtonUp += (_, _) => _ = StartCaptureAsync();
            return container;
        }

        private void ClearCapture()
        {
            _statusTimer.Stop();
            _statusMessage = string.Empty;
            _currentBitmap?.Dispose();
            _currentBitmap = null;
            _currentPreview = null;
            _analysis = null;
            _isAnalyzing = false;
            RaiseStateChanged();
            ViewUpdated?.Invoke();
        }

        public async Task StartCaptureAsync()
        {
            if (_isCapturing) return;
            _isCapturing = true;

            var manager = (Application.Current as App)?.Manager;
            System.Windows.Forms.Screen? targetScreen = null;

            try
            {
                manager?.SetCaptureSuppressed(true);
                await Task.Delay(140);

                var selection = await ScreenRegionSelector.SelectRegionAsync();
                if (selection != null)
                {
                    targetScreen = selection.Screen;
                    _currentBitmap?.Dispose();
                    _currentBitmap = selection.Bitmap;
                    _currentPreview = ScreenOcrService.ToBitmapSource(_currentBitmap);
                    _analysis = null;
                    _isAnalyzing = true;
                    _statusMessage = string.Empty;

                    RaiseStateChanged();
                    ViewUpdated?.Invoke();
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
            finally
            {
                manager?.SetCaptureSuppressed(false, targetScreen, targetScreen != null ? Id : null);
                _isCapturing = false;
            }

            if (_currentBitmap != null && _isAnalyzing)
            {
                var captured = _currentBitmap;
                var result = await ScreenOcrService.AnalyzeAsync(captured);

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_currentBitmap, captured))
                    {
                        _analysis = result;
                        _isAnalyzing = false;
                        RaiseStateChanged();
                        ViewUpdated?.Invoke();
                    }
                });
            }
        }

        private void ShowStatus(string message)
        {
            _statusMessage = message;
            _statusTimer.Stop();
            _statusTimer.Start();
            ViewUpdated?.Invoke();
        }

        private static string Truncate(string text, int maxLen)
        {
            string clean = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return clean.Length <= maxLen ? clean : clean[..maxLen] + "…";
        }

        private static Button CreateBarButton(
            NotchIconKind iconKind,
            string label,
            Action onClick,
            bool isHighlighted = false,
            bool isEnabled = true,
            string? tooltip = null)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = isEnabled ? 1.0 : 0.42
            };

            Brush fg = isHighlighted ? NotchDesign.AccentPrimaryBrush : NotchDesign.TextPrimaryBrush;
            var icon = NotchDesign.CreateVectorIcon(iconKind, 12.5, fg, 1.85);
            icon.Margin = new Thickness(0, 0, 5, 0);
            stack.Children.Add(icon);

            stack.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = fg,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.2,
                FontWeight = isHighlighted ? FontWeights.SemiBold : FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            });

            var btn = NotchDesign.CreateRoundedButton(
                stack,
                onClick,
                background: isHighlighted
                    ? NotchDesign.CreateSoftAccentTileBrush(0x28)
                    : Brushes.Transparent,
                hoverBackground: isHighlighted
                    ? NotchDesign.CreateSoftAccentTileBrush(0x3C)
                    : NotchDesign.ControlHoverBrush,
                foreground: fg,
                cornerRadius: 13,
                padding: new Thickness(9, 5, 9, 5),
                tooltip: tooltip);

            btn.Margin = new Thickness(0, 0, 2, 0);
            return btn;
        }

        public override void Dispose()
        {
            _statusTimer.Stop();
            _currentBitmap?.Dispose();
            _currentBitmap = null;
            base.Dispose();
        }
    }
}
