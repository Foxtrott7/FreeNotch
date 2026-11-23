using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.Pet
{
    /// <summary>
    /// Ausdrucksstarke, ruhige Cozmo-/OLED-Notch-Augen ohne Arme oder Ohren.
    /// Nutzt weich interpolierte Form-Morphs (Schrägstellung, Squash/Stretch, Perspektive,
    /// schläfrige Lider, Schlafbögen und freundliches Aufwachen) auf reinem OLED-Schwarz.
    /// </summary>
    public sealed class NotchEyesControl : UserControl
    {
        private const double CanvasW = 48;
        private const double CanvasH = 20;
        private const double EyeBaseW = 11.5;
        private const double EyeBaseH = 13.5;
        private const double EyeGap = 8.5;

        private readonly DispatcherTimer _animTimer;
        private readonly Canvas _canvas;
        private readonly Path _leftEyePath;
        private readonly Path _rightEyePath;
        private readonly TextBlock _zzzText;
        private readonly RotateTransform _headTilt = new(0, CanvasW * 0.5, CanvasH * 0.5);
        private readonly TranslateTransform _headShift = new();

        private DateTime _lastTickUtc = DateTime.UtcNow;
        private double _timeSec;

        // Geglättete Animationszustände
        private double _lookX, _lookY;
        private double _targetLookX, _targetLookY;
        private double _ambientLookX, _ambientLookY;
        private double _nextAmbientLookSec = 3.5;
        private Point _lastCursorPt;
        private DateTime _lastCursorMoveUtc = DateTime.UtcNow;

        private double _blinkAmount;      // 0 = offen, 1 = geschlossen
        private double _blinkTimerSec = 3.6;
        private double _blinkPhase;
        private bool _pendingDoubleBlink;

        private double _drowsyLevel;      // 0..1
        private double _sleepLevel;       // 0..1
        private double _happyLevel;       // 0..1
        private double _curiousLevel;     // 0..1
        private double _tiltDeg;

        private readonly DropShadowEffect _eyeGlow;

        public NotchEyesControl(bool compact = true)
        {
            IsHitTestVisible = false;
            Width = CanvasW;
            Height = CanvasH;

            var tg = new TransformGroup();
            tg.Children.Add(_headTilt);
            tg.Children.Add(_headShift);

            _eyeGlow = new DropShadowEffect
            {
                ShadowDepth = 0,
                BlurRadius = 5,
                Opacity = 0.28,
                Color = NotchDesign.AccentPrimary,
                RenderingBias = RenderingBias.Performance
            };

            _canvas = new Canvas
            {
                Width = CanvasW,
                Height = CanvasH,
                RenderTransform = tg
            };

            _leftEyePath = new Path
            {
                Fill = NotchDesign.TextPrimaryBrush,
                Effect = _eyeGlow
            };
            _rightEyePath = new Path
            {
                Fill = NotchDesign.TextPrimaryBrush,
                Effect = _eyeGlow
            };

            _zzzText = new TextBlock
            {
                Text = "z",
                Foreground = NotchDesign.TextMutedBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 8,
                FontWeight = FontWeights.SemiBold,
                Opacity = 0
            };
            Canvas.SetLeft(_zzzText, CanvasW - 7);
            Canvas.SetTop(_zzzText, -1);

            _canvas.Children.Add(_leftEyePath);
            _canvas.Children.Add(_rightEyePath);
            _canvas.Children.Add(_zzzText);

            Content = _canvas;

            _animTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33) // ruhige ~30 fps
            };
            _animTimer.Tick += (_, _) => OnAnimTick();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            PetService.Instance.StateChanged += OnPetStateChanged;
            SettingsService.Changed += OnThemeChanged;
            _lastTickUtc = DateTime.UtcNow;
            UpdateThemeColors();
            RenderFrame(dt: 0.033, instant: !NotchDesign.EffectiveAnimationsEnabled);
            if (NotchDesign.EffectiveAnimationsEnabled)
                _animTimer.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _animTimer.Stop();
            PetService.Instance.StateChanged -= OnPetStateChanged;
            SettingsService.Changed -= OnThemeChanged;
        }

        private void OnPetStateChanged(object? sender, EventArgs e)
        {
            if (!NotchDesign.EffectiveAnimationsEnabled)
                RenderFrame(0.033, instant: true);
        }

        private void OnThemeChanged()
        {
            UpdateThemeColors();
            RenderFrame(0.033, instant: !NotchDesign.EffectiveAnimationsEnabled);
        }

        private void UpdateThemeColors()
        {
            _eyeGlow.Color = NotchDesign.AccentPrimary;
        }

        private void OnAnimTick()
        {
            if (!NotchDesign.EffectiveAnimationsEnabled)
            {
                _animTimer.Stop();
                RenderFrame(0.033, instant: true);
                return;
            }
            if (!IsVisible) return;

            var now = DateTime.UtcNow;
            double dt = Math.Clamp((now - _lastTickUtc).TotalSeconds, 0.01, 0.08);
            _lastTickUtc = now;
            RenderFrame(dt, instant: false);
        }

        private void RenderFrame(double dt, bool instant)
        {
            _timeSec += dt;
            var mood = PetService.Instance.CurrentMood;

            // Zielwerte für weiche Übergänge
            double tDrowsy = mood == PetEyeMood.Drowsy ? 1.0 : 0.0;
            double tSleep = mood == PetEyeMood.Sleeping ? 1.0 : 0.0;
            double tHappy = mood == PetEyeMood.HappyWake ? 1.0 : 0.0;
            double tCurious = mood == PetEyeMood.Curious ? 1.0 : 0.0;

            double moodRate = instant ? 1.0 : 1.0 - Math.Exp(-3.4 * dt); // bewusst ruhig & weich
            _drowsyLevel += (tDrowsy - _drowsyLevel) * moodRate;
            _sleepLevel += (tSleep - _sleepLevel) * moodRate;
            _happyLevel += (tHappy - _happyLevel) * (instant ? 1.0 : 1.0 - Math.Exp(-6.0 * dt));
            _curiousLevel += (tCurious - _curiousLevel) * moodRate;

            // Blickziel bestimmen (ruhiges Maus-Tracking + gelegentliches ruhiges Umherschauen)
            UpdateLookTarget(mood, dt, instant);

            double lookRate = instant ? 1.0 : 1.0 - Math.Exp(-4.8 * dt); // langsamer, entspannter Blick
            _lookX += (_targetLookX - _lookX) * lookRate;
            _lookY += (_targetLookY - _lookY) * lookRate;

            // Natürliches, ruhiges Blinzeln (inkl. seltenem Doppelblinzeln)
            UpdateBlink(mood, dt, instant);

            // Sanfte Kopfneigung je nach Blick & Neugier
            double targetTilt = _lookX * 3.6 + _curiousLevel * 2.5 * Math.Sin(_timeSec * 1.1);
            if (_sleepLevel > 0.5) targetTilt *= (1.0 - _sleepLevel);
            _tiltDeg += (targetTilt - _tiltDeg) * (instant ? 1.0 : 1.0 - Math.Exp(-4.0 * dt));
            _headTilt.Angle = _tiltDeg;

            // Ruhige Blick-/Atem-Verschiebung
            double breathY = Math.Sin(_timeSec * (1.25 - _sleepLevel * 0.45)) * (0.35 + _sleepLevel * 0.25);
            _headShift.X = _lookX * 2.4 * (1.0 - _sleepLevel);
            _headShift.Y = _lookY * 1.3 * (1.0 - _sleepLevel) + breathY;

            // Augenfarben & Glow sanft nach Stimmung modulieren
            var eyeColor = InterpolateEyeColor();
            var brush = new SolidColorBrush(eyeColor);
            brush.Freeze();
            _leftEyePath.Fill = brush;
            _rightEyePath.Fill = brush;
            _eyeGlow.Opacity = (0.28 + _happyLevel * 0.22 + _curiousLevel * 0.14) * (1.0 - _sleepLevel * 0.7);

            // Schlaf-Indikator "z" ganz sanft atmen lassen
            if (_sleepLevel > 0.25 && !instant)
            {
                double pulse = 0.5 + 0.5 * Math.Sin(_timeSec * 1.5);
                _zzzText.Opacity = (_sleepLevel - 0.25) / 0.75 * (0.25 + 0.55 * pulse);
                Canvas.SetTop(_zzzText, -1.0 - pulse * 1.4);
            }
            else
            {
                _zzzText.Opacity = _sleepLevel > 0.6 ? 0.55 : 0.0;
            }

            // Geometrien für linkes & rechtes Cozmo-Auge aufbauen
            double centerLeftX = (CanvasW - EyeGap) * 0.5 - EyeBaseW * 0.5;
            double centerRightX = (CanvasW + EyeGap) * 0.5 + EyeBaseW * 0.5;
            double centerY = CanvasH * 0.5;

            // Perspektive beim Zur-Seite-Schauen (vorderes Auge minimal größer, hinteres minimal schmaler)
            double leftScaleW = 1.0 - _lookX * 0.07;
            double rightScaleW = 1.0 + _lookX * 0.07;
            double leftScaleH = 1.0 - _lookX * 0.05 + _curiousLevel * 0.08;
            double rightScaleH = 1.0 + _lookX * 0.05 - _curiousLevel * 0.04;

            // Neugieriges leichtes Schrägstellen der Oberkante
            double leftSlant = -_curiousLevel * 1.2 + _lookX * 0.6;
            double rightSlant = _curiousLevel * 0.8 - _lookX * 0.6;

            _leftEyePath.Data = BuildExpressiveEyeGeometry(
                centerLeftX, centerY,
                EyeBaseW * leftScaleW, EyeBaseH * leftScaleH,
                leftSlant, isLeft: true);

            _rightEyePath.Data = BuildExpressiveEyeGeometry(
                centerRightX, centerY,
                EyeBaseW * rightScaleW, EyeBaseH * rightScaleH,
                rightSlant, isLeft: false);
        }

        private Geometry BuildExpressiveEyeGeometry(
            double cx, double cy,
            double w, double h,
            double slantPx,
            bool isLeft)
        {
            // Blinzel- & Müdigkeits-Squash
            double closedFactor = Math.Clamp(Math.Max(_blinkAmount, _sleepLevel), 0.0, 1.0);
            double drowsyLid = _drowsyLevel * 0.42;

            // Squash & Stretch beim Blinzeln
            double curW = w * (1.0 + closedFactor * 0.14);
            double minOpenH = 2.4;
            double openFactor = Math.Max(0.0, 1.0 - closedFactor - drowsyLid * 0.7);
            double curH = minOpenH + (h - minOpenH) * openFactor;

            // Im Schlaf oder bei HappyWake morpht das Auge in einen geschmeidigen Bogen
            double happyCurve = _happyLevel * 4.2;
            double sleepDip = _sleepLevel * 2.6;

            // Vertikale Mitte leicht versetzen bei Schlaf/Müdigkeit
            double yOffset = _drowsyLevel * 1.4 + _sleepLevel * 1.2 - _happyLevel * 0.6;
            double topY = cy + yOffset - curH * 0.5;
            double botY = cy + yOffset + curH * 0.5;
            double leftX = cx - curW * 0.5;
            double rightX = cx + curW * 0.5;

            // Bei leichtem Blinzeln/Drowsy senkt sich die Oberkante stärker ab als die Unterkante steigt
            double topInnerSlant = isLeft ? -slantPx : slantPx;
            double topLeftY = topY + (isLeft ? 0 : topInnerSlant) + drowsyLid * 2.2;
            double topRightY = topY + (isLeft ? topInnerSlant : 0) + drowsyLid * 2.2;

            // Bogenwölbung oben & unten (für HappyWake ◠ oder Schlaf ︶)
            double topCtrlY = Math.Min(topLeftY, topRightY) - happyCurve + sleepDip * 1.15;
            double botCtrlY = botY - happyCurve * 1.35 + sleepDip * 1.45;

            double r = Math.Clamp(Math.Min(curW, curH) * 0.36, 1.1, 4.4);

            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                // Geschlossener / Bogen-Modus mit geschmeidigen kubischen/quadratischen Konturen
                ctx.BeginFigure(new Point(leftX + r, topLeftY), isFilled: true, isClosed: true);

                // Oberkante (leicht gewölbt bei Happy oder leicht abgesenkt bei Sleep/Drowsy)
                ctx.QuadraticBezierTo(
                    new Point(cx, topCtrlY),
                    new Point(rightX - r, topRightY),
                    isStroked: true, isSmoothJoin: true);

                // Rechts oben abrunden
                ctx.QuadraticBezierTo(
                    new Point(rightX, topRightY),
                    new Point(rightX, Math.Min(botY - r * 0.4, topRightY + r)),
                    isStroked: true, isSmoothJoin: true);

                // Rechte Flanke
                ctx.LineTo(new Point(rightX, Math.Max(topRightY + r * 0.4, botY - r)), isStroked: true, isSmoothJoin: true);

                // Rechts unten abrunden
                ctx.QuadraticBezierTo(
                    new Point(rightX, botY),
                    new Point(rightX - r, botY),
                    isStroked: true, isSmoothJoin: true);

                // Unterkante (bei Happy stark nach oben gewölbt -> ◠, bei Sleep sanft nach unten -> ︶)
                ctx.QuadraticBezierTo(
                    new Point(cx, botCtrlY),
                    new Point(leftX + r, botY),
                    isStroked: true, isSmoothJoin: true);

                // Links unten abrunden
                ctx.QuadraticBezierTo(
                    new Point(leftX, botY),
                    new Point(leftX, Math.Max(topLeftY + r * 0.4, botY - r)),
                    isStroked: true, isSmoothJoin: true);

                // Linke Flanke
                ctx.LineTo(new Point(leftX, Math.Min(botY - r * 0.4, topLeftY + r)), isStroked: true, isSmoothJoin: true);

                // Links oben abrunden
                ctx.QuadraticBezierTo(
                    new Point(leftX, topLeftY),
                    new Point(leftX + r, topLeftY),
                    isStroked: true, isSmoothJoin: true);
            }
            geom.Freeze();
            return geom;
        }

        private Color InterpolateEyeColor()
        {
            var baseCol = Colors.White;
            var accent = NotchDesign.AccentPrimary;
            // Leichter lebendiger Akzent-Schimmer bei Curious/HappyWake, gedimmtes Weiß im Schlaf
            double accentMix = Math.Clamp(_curiousLevel * 0.25 + _happyLevel * 0.35, 0.0, 0.45);
            double dim = 1.0 - _sleepLevel * 0.42 - _drowsyLevel * 0.16;

            byte Lerp(byte a, byte b, double t) => (byte)Math.Clamp((a + (b - a) * t) * dim, 0, 255);
            return Color.FromRgb(
                Lerp(baseCol.R, accent.R, accentMix),
                Lerp(baseCol.G, accent.G, accentMix),
                Lerp(baseCol.B, accent.B, accentMix));
        }

        private void UpdateLookTarget(PetEyeMood mood, double dt, bool instant)
        {
            if (mood == PetEyeMood.Sleeping || mood == PetEyeMood.HappyWake || instant)
            {
                _targetLookX = 0;
                _targetLookY = 0;
                return;
            }

            bool cursorActive = false;
            if (PresentationSource.FromVisual(this) != null && NativeMethods.GetCursorPos(out var pt))
            {
                if (Math.Abs(pt.X - _lastCursorPt.X) > 3 || Math.Abs(pt.Y - _lastCursorPt.Y) > 3)
                {
                    _lastCursorPt = new Point(pt.X, pt.Y);
                    _lastCursorMoveUtc = DateTime.UtcNow;
                }

                // Wenn die Maus in den letzten 4s bewegt wurde oder Notch gehovert wird -> Cursor anschauen
                if (mood == PetEyeMood.Curious || (DateTime.UtcNow - _lastCursorMoveUtc).TotalSeconds < 4.0)
                {
                    try
                    {
                        var center = PointToScreen(new Point(ActualWidth * 0.5, ActualHeight * 0.5));
                        double dx = pt.X - center.X;
                        double dy = pt.Y - center.Y;
                        double dist = Math.Sqrt(dx * dx + dy * dy);
                        if (dist > 14)
                        {
                            double norm = Math.Min(1.0, dist / 320.0);
                            _targetLookX = (dx / dist) * norm;
                            _targetLookY = (dy / dist) * norm;
                            cursorActive = true;
                        }
                    }
                    catch { }
                }
            }

            if (!cursorActive)
            {
                // Ruhiges, gelegentliches Umherschauen statt starr nach vorn zu blicken
                _nextAmbientLookSec -= dt;
                if (_nextAmbientLookSec <= 0)
                {
                    long seed = Environment.TickCount64;
                    int choice = (int)(seed % 5);
                    _ambientLookX = choice switch
                    {
                        0 => -0.55,
                        1 => 0.55,
                        2 => -0.25,
                        3 => 0.25,
                        _ => 0.0
                    };
                    _ambientLookY = (choice % 2 == 0) ? 0.15 : -0.1;
                    _nextAmbientLookSec = 3.8 + (seed % 3500) / 1000.0;
                }
                _targetLookX = _ambientLookX * (1.0 - _drowsyLevel * 0.7);
                _targetLookY = _ambientLookY + _drowsyLevel * 0.25;
            }
        }

        private void UpdateBlink(PetEyeMood mood, double dt, bool instant)
        {
            if (instant || mood == PetEyeMood.Sleeping || mood == PetEyeMood.HappyWake)
            {
                _blinkAmount = 0;
                _blinkPhase = 0;
                return;
            }

            if (_blinkPhase > 0)
            {
                // Langsamerer, schwererer Lidschlag wenn müde (Drowsy)
                double blinkDur = mood == PetEyeMood.Drowsy ? 0.34 : 0.20;
                _blinkPhase -= dt / blinkDur;
                if (_blinkPhase <= 0)
                {
                    _blinkPhase = 0;
                    _blinkAmount = 0;
                    if (_pendingDoubleBlink)
                    {
                        _pendingDoubleBlink = false;
                        _blinkPhase = 1.0;
                    }
                }
                else
                {
                    double t = 1.0 - Math.Abs(_blinkPhase * 2.0 - 1.0);
                    _blinkAmount = Math.Sin(t * Math.PI * 0.5);
                }
                return;
            }

            _blinkAmount = 0;
            _blinkTimerSec -= dt;
            if (_blinkTimerSec <= 0)
            {
                _blinkPhase = 1.0;
                long tick = Environment.TickCount64;
                _pendingDoubleBlink = mood == PetEyeMood.Calm && (tick % 6 == 0);
                _blinkTimerSec = mood == PetEyeMood.Drowsy
                    ? 1.9 + (tick % 1400) / 1000.0
                    : 3.4 + (tick % 3200) / 1000.0;
            }
        }
    }
}
