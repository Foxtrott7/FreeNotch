using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.Pet
{
    /// <summary>
    /// Minimalistisches OLED-Notch-Augenpaar (Vektor-UI).
    /// Compact-Modus passt nahtlos in die geschlossene Notch; Groß-Modus skaliert für Bento-Kachel und Vollbild.
    /// </summary>
    public sealed class NotchEyesControl : UserControl
    {
        private readonly bool _compact;
        private readonly DispatcherTimer _animTimer;
        private readonly TranslateTransform _faceOffset = new();
        private readonly TranslateTransform _pupilOffset = new();
        private readonly ScaleTransform _blinkScale = new(1.0, 1.0);

        private readonly Grid _openEyesLayer;
        private readonly Path _happyEyesPath;
        private readonly Path _sleepEyesPath;
        private readonly Path _hungryLidsPath;
        private readonly Border _leftEyeOuter;
        private readonly Border _rightEyeOuter;
        private readonly Ellipse _leftSpark;
        private readonly Ellipse _rightSpark;
        private readonly TextBlock _zzzText;
        private readonly TextBlock _vibingNote;

        private double _targetLookX;
        private double _targetLookY;
        private double _lookX;
        private double _lookY;
        private double _blinkTimerSec = 3.2;
        private double _blinkPhase; // > 0 während Blinzeln
        private double _bobPhase;
        private DateTime _lastTickUtc = DateTime.UtcNow;

        public NotchEyesControl(bool compact)
        {
            _compact = compact;
            IsHitTestVisible = !compact;

            double baseW = compact ? 38 : 132;
            double baseH = compact ? 18 : 56;
            if (compact)
            {
                Width = baseW;
                Height = baseH;
            }

            var canvas = new Canvas
            {
                Width = baseW,
                Height = baseH,
                RenderTransform = _faceOffset
            };

            // 1) Normale / neugierige / vibende Pill-Augen
            _openEyesLayer = new Grid
            {
                Width = baseW,
                Height = baseH,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _blinkScale
            };

            double eyeW = compact ? 8.5 : 30;
            double eyeH = compact ? 12.0 : 40;
            double gap = compact ? 9.0 : 28;
            double leftX = (baseW - (eyeW * 2 + gap)) / 2.0;
            double rightX = leftX + eyeW + gap;
            double eyeY = (baseH - eyeH) / 2.0;

            _leftEyeOuter = CreateEyePill(eyeW, eyeH);
            _rightEyeOuter = CreateEyePill(eyeW, eyeH);

            var leftHost = new Grid { Width = eyeW, Height = eyeH };
            leftHost.Children.Add(_leftEyeOuter);
            _leftSpark = CreateEyeSpark(compact);
            leftHost.Children.Add(_leftSpark);
            Canvas.SetLeft(leftHost, leftX);
            Canvas.SetTop(leftHost, eyeY);

            var rightHost = new Grid { Width = eyeW, Height = eyeH };
            rightHost.Children.Add(_rightEyeOuter);
            _rightSpark = CreateEyeSpark(compact);
            rightHost.Children.Add(_rightSpark);
            Canvas.SetLeft(rightHost, rightX);
            Canvas.SetTop(rightHost, eyeY);

            var eyesCanvas = new Canvas { Width = baseW, Height = baseH, RenderTransform = _pupilOffset };
            eyesCanvas.Children.Add(leftHost);
            eyesCanvas.Children.Add(rightHost);
            _openEyesLayer.Children.Add(eyesCanvas);

            // Müde/hungrige Oberlider (schwarz über dem oberen Teil der Pill-Augen)
            double lidH = eyeH * 0.42;
            _hungryLidsPath = new Path
            {
                Fill = NotchDesign.NotchBackgroundBrush,
                Data = Geometry.Parse(
                    $"M {F(leftX - 1)},{F(eyeY - 1)} L {F(leftX + eyeW + 1)},{F(eyeY - 1)} L {F(leftX + eyeW + 1)},{F(eyeY + lidH)} Q {F(leftX + eyeW * 0.5)},{F(eyeY + lidH * 0.65)} {F(leftX - 1)},{F(eyeY + lidH * 0.9)} Z " +
                    $"M {F(rightX - 1)},{F(eyeY - 1)} L {F(rightX + eyeW + 1)},{F(eyeY - 1)} L {F(rightX + eyeW + 1)},{F(eyeY + lidH * 0.9)} Q {F(rightX + eyeW * 0.5)},{F(eyeY + lidH * 0.65)} {F(rightX - 1)},{F(eyeY + lidH)} Z"),
                Visibility = Visibility.Collapsed
            };
            eyesCanvas.Children.Add(_hungryLidsPath);
            canvas.Children.Add(_openEyesLayer);

            // 2) Fröhliche Bogen-Augen (^ ^)
            double strokeT = compact ? 2.2 : 5.5;
            double arcTopY = eyeY + eyeH * 0.66;
            double arcPeakY = eyeY + eyeH * 0.14;
            _happyEyesPath = new Path
            {
                Stroke = NotchDesign.TextPrimaryBrush,
                StrokeThickness = strokeT,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse(
                    $"M {F(leftX)},{F(arcTopY)} Q {F(leftX + eyeW * 0.5)},{F(arcPeakY)} {F(leftX + eyeW)},{F(arcTopY)} " +
                    $"M {F(rightX)},{F(arcTopY)} Q {F(rightX + eyeW * 0.5)},{F(arcPeakY)} {F(rightX + eyeW)},{F(arcTopY)}"),
                Visibility = Visibility.Collapsed
            };
            canvas.Children.Add(_happyEyesPath);

            // 3) Schlafende Bogen-Augen (u u)
            double sleepTopY = eyeY + eyeH * 0.44;
            double sleepDipY = eyeY + eyeH * 0.78;
            _sleepEyesPath = new Path
            {
                Stroke = NotchDesign.TextSecondaryBrush,
                StrokeThickness = strokeT,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse(
                    $"M {F(leftX)},{F(sleepTopY)} Q {F(leftX + eyeW * 0.5)},{F(sleepDipY)} {F(leftX + eyeW)},{F(sleepTopY)} " +
                    $"M {F(rightX)},{F(sleepTopY)} Q {F(rightX + eyeW * 0.5)},{F(sleepDipY)} {F(rightX + eyeW)},{F(sleepTopY)}"),
                Visibility = Visibility.Collapsed
            };
            canvas.Children.Add(_sleepEyesPath);

            // Kleines zZ beim Schlafen
            _zzzText = new TextBlock
            {
                Text = "zZ",
                Foreground = NotchDesign.TextMutedBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = compact ? 7.5 : 13,
                FontWeight = FontWeights.SemiBold,
                Visibility = Visibility.Collapsed
            };
            Canvas.SetLeft(_zzzText, rightX + eyeW + (compact ? 1 : 5));
            Canvas.SetTop(_zzzText, compact ? -1 : 0);
            canvas.Children.Add(_zzzText);

            // Kleiner Akzent-Punkt / Musik-Indikator bei Vibing
            _vibingNote = new TextBlock
            {
                Text = "♪",
                Foreground = NotchDesign.AccentPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = compact ? 8.5 : 15,
                FontWeight = FontWeights.Bold,
                Visibility = Visibility.Collapsed
            };
            Canvas.SetLeft(_vibingNote, rightX + eyeW + (compact ? 1 : 6));
            Canvas.SetTop(_vibingNote, compact ? 0 : 2);
            canvas.Children.Add(_vibingNote);

            if (compact)
            {
                Content = canvas;
            }
            else
            {
                Content = new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    MaxWidth = 152,
                    MaxHeight = 68,
                    Child = canvas
                };
            }

            _animTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33) // ~30 fps ruhig & sparsam
            };
            _animTimer.Tick += (_, _) => OnAnimTick();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private static Border CreateEyePill(double w, double h) => new()
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(w * 0.5),
            Background = NotchDesign.TextPrimaryBrush
        };

        private static Ellipse CreateEyeSpark(bool compact)
        {
            double size = compact ? 2.4 : 7.5;
            return new Ellipse
            {
                Width = size,
                Height = size,
                Fill = NotchDesign.NotchBackgroundBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = compact ? new Thickness(0, 2.2, 1.8, 0) : new Thickness(0, 6.5, 5.5, 0),
                Opacity = 0.85
            };
        }

        private static string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            PetService.Instance.StateChanged += OnPetStateChanged;
            SettingsService.Changed += OnThemeChanged;
            ApplyExpression();
            _lastTickUtc = DateTime.UtcNow;
            if (NotchDesign.EffectiveAnimationsEnabled)
                _animTimer.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _animTimer.Stop();
            PetService.Instance.StateChanged -= OnPetStateChanged;
            SettingsService.Changed -= OnThemeChanged;
        }

        private void OnPetStateChanged(object? sender, EventArgs e) => ApplyExpression();
        private void OnThemeChanged() => ApplyExpression();

        private void ApplyExpression()
        {
            var expr = PetService.Instance.CurrentExpression;
            var primary = NotchDesign.TextPrimaryBrush;
            var accent = NotchDesign.AccentPrimaryBrush;

            _openEyesLayer.Visibility = expr is PetExpression.Happy or PetExpression.Sleepy
                ? Visibility.Collapsed
                : Visibility.Visible;

            _happyEyesPath.Visibility = expr == PetExpression.Happy ? Visibility.Visible : Visibility.Collapsed;
            _happyEyesPath.Stroke = accent;

            _sleepEyesPath.Visibility = expr == PetExpression.Sleepy ? Visibility.Visible : Visibility.Collapsed;
            _zzzText.Visibility = expr == PetExpression.Sleepy ? Visibility.Visible : Visibility.Collapsed;

            _hungryLidsPath.Visibility = expr == PetExpression.Hungry ? Visibility.Visible : Visibility.Collapsed;
            _vibingNote.Visibility = expr == PetExpression.Vibing ? Visibility.Visible : Visibility.Collapsed;
            _vibingNote.Foreground = accent;

            var eyeFill = expr == PetExpression.Vibing ? accent : primary;
            _leftEyeOuter.Background = eyeFill;
            _rightEyeOuter.Background = eyeFill;

            if (!NotchDesign.EffectiveAnimationsEnabled)
            {
                _faceOffset.X = 0;
                _faceOffset.Y = 0;
                _pupilOffset.X = 0;
                _pupilOffset.Y = 0;
                _blinkScale.ScaleY = 1.0;
            }
        }

        private void OnAnimTick()
        {
            if (!NotchDesign.EffectiveAnimationsEnabled)
            {
                _animTimer.Stop();
                return;
            }
            if (!IsVisible) return;

            var now = DateTime.UtcNow;
            double dt = Math.Clamp((now - _lastTickUtc).TotalSeconds, 0.01, 0.1);
            _lastTickUtc = now;

            var expr = PetService.Instance.CurrentExpression;

            // 1) Sanftes Maus-Blick-Tracking (nur wenn wach)
            if (expr != PetExpression.Sleepy && expr != PetExpression.Happy)
            {
                UpdateCursorTarget();
            }
            else
            {
                _targetLookX = 0;
                _targetLookY = 0;
            }

            double smooth = 1.0 - Math.Exp(-8.5 * dt);
            _lookX += (_targetLookX - _lookX) * smooth;
            _lookY += (_targetLookY - _lookY) * smooth;

            double maxLookX = _compact ? 2.4 : 7.5;
            double maxLookY = _compact ? 1.4 : 4.2;
            _pupilOffset.X = _lookX * maxLookX;
            _pupilOffset.Y = _lookY * maxLookY;

            // 2) Natürliches Blinzeln
            if (expr != PetExpression.Sleepy && expr != PetExpression.Happy)
            {
                if (_blinkPhase > 0)
                {
                    _blinkPhase -= dt / 0.16;
                    if (_blinkPhase <= 0)
                    {
                        _blinkPhase = 0;
                        _blinkScale.ScaleY = 1.0;
                    }
                    else
                    {
                        // Dreieckskurve: 1 -> 0.12 -> 1
                        double t = 1.0 - Math.Abs(_blinkPhase * 2.0 - 1.0);
                        _blinkScale.ScaleY = 1.0 - 0.88 * t;
                    }
                }
                else
                {
                    _blinkTimerSec -= dt;
                    if (_blinkTimerSec <= 0)
                    {
                        _blinkPhase = 1.0;
                        _blinkTimerSec = 3.1 + (Environment.TickCount64 % 2700) / 1000.0;
                    }
                    _blinkScale.ScaleY = 1.0;
                }
            }
            else
            {
                _blinkScale.ScaleY = 1.0;
            }

            // 3) Ruhiges Atmen / Vibing / Schlaf-Puls
            _bobPhase += dt * (expr == PetExpression.Vibing ? 5.2 : 1.8);
            if (expr == PetExpression.Vibing)
            {
                _faceOffset.Y = Math.Sin(_bobPhase) * (_compact ? 1.1 : 3.0);
                _faceOffset.X = Math.Cos(_bobPhase * 0.5) * (_compact ? 0.6 : 1.6);
            }
            else if (expr == PetExpression.Sleepy)
            {
                _faceOffset.Y = Math.Sin(_bobPhase) * (_compact ? 0.45 : 1.4);
                _faceOffset.X = 0;
                _zzzText.Opacity = 0.45 + 0.45 * (0.5 + 0.5 * Math.Sin(_bobPhase * 1.3));
            }
            else
            {
                _faceOffset.Y = Math.Sin(_bobPhase * 0.8) * (_compact ? 0.25 : 0.8);
                _faceOffset.X = 0;
            }
        }

        private void UpdateCursorTarget()
        {
            if (PresentationSource.FromVisual(this) == null) return;
            if (!NativeMethods.GetCursorPos(out var pt)) return;

            try
            {
                var centerScreen = PointToScreen(new Point(ActualWidth * 0.5, ActualHeight * 0.5));
                double dx = pt.X - centerScreen.X;
                double dy = pt.Y - centerScreen.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 12)
                {
                    _targetLookX = 0;
                    _targetLookY = 0;
                    return;
                }

                // Weiche Sättigung, sodass nahe und ferne Mauspositionen angenehm verfolgt werden
                double norm = Math.Min(1.0, dist / 260.0);
                _targetLookX = (dx / dist) * norm;
                _targetLookY = (dy / dist) * norm;
            }
            catch
            {
                _targetLookX = 0;
                _targetLookY = 0;
            }
        }
    }
}
