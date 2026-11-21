using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WindowsNotch.Core;
using Forms = System.Windows.Forms;

namespace WindowsNotch.Modules.Pet
{
    /// <summary>
    /// Ruhig animierter 4-beiniger Vektor-Fuchs für Ruhezustand und Detailansicht.
    /// Passt sich dynamisch an die Notch-Größe an, chillt/schläft von selbst
    /// und unterstützt Streicheln, Füttern sowie ein Ball-Minispiel.
    /// </summary>
    public sealed class PetVisualControl : UserControl
    {
        private enum FoxBehavior { Walking, Chilling, Sleeping, Excited, Eating, ChasingBall }

        private readonly PetModule _module;
        private readonly bool _compact;
        private readonly DispatcherTimer _timer;
        private readonly Random _rng = new();

        private readonly Canvas _stage;
        private double _stageWidth = 190;
        private double _stageHeight = 28;
        private double _foxScale = 0.44;

        private readonly Canvas _foxRoot;
        private readonly TranslateTransform _foxPos = new();
        private readonly ScaleTransform _foxFacing = new(1, 1);
        private readonly ScaleTransform _foxSizeScale = new(0.44, 0.44);
        private readonly RotateTransform _tailRot = new();
        private readonly RotateTransform _headRot = new();
        private readonly RotateTransform _legFLRot = new();
        private readonly RotateTransform _legFRRot = new();
        private readonly RotateTransform _legBLRot = new();
        private readonly RotateTransform _legBRRot = new();
        private readonly ScaleTransform _eyeScale = new(1, 1);
        private readonly TranslateTransform _pupilOffset = new();

        private readonly Ellipse _eyeWhite;
        private readonly Ellipse _pupil;
        private readonly Path _happyEyeArc;
        private readonly Path _sleepIcon;
        private readonly Path _heartIcon;
        private readonly Ellipse _treatItem;
        private readonly Ellipse _ballItem;

        private FoxBehavior _behavior = FoxBehavior.Chilling;
        private double _foxX = 48;
        private double _targetX = 48;
        private double _facing = 1;
        private double _behaviorTimerSec = 5.5;
        private double _gaitPhase;
        private double _tailPhase;
        private double _blinkTimerSec = 3.4;
        private double _blinkProgress = -1;
        private double _reactionTimerSec;
        private double _strokeAccumulator;
        private Point _lastStrokePoint;
        private System.Drawing.Point _lastGlobalCursor;
        private DateTime _lastCursorMoveUtc = DateTime.UtcNow;

        // Minispiel-Objekte (Futter & Ball)
        private bool _treatActive;
        private double _treatX;
        private double _treatY;
        private bool _ballActive;
        private double _ballX;
        private double _ballY;
        private double _ballVx;
        private double _ballVy;

        public PetVisualControl(PetModule module, bool compact)
        {
            _module = module;
            _compact = compact;
            Background = Brushes.Transparent;
            Cursor = compact ? Cursors.Arrow : Cursors.Hand;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;

            _stage = new Canvas
            {
                ClipToBounds = true,
                Background = Brushes.Transparent
            };
            _stage.SizeChanged += (_, e) => UpdateStageMetrics(e.NewSize.Width, e.NewSize.Height);

            // Harmonisierte Fuchs-Geometrie (Basis 64x44)
            _foxRoot = new Canvas
            {
                Width = 64,
                Height = 44,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup
                {
                    Children =
                    {
                        _foxFacing,
                        _foxSizeScale,
                        _foxPos
                    }
                }
            };

            var furMain = new SolidColorBrush(Color.FromRgb(0xEA, 0x6A, 0x20));
            var furShade = new SolidColorBrush(Color.FromRgb(0xC2, 0x4E, 0x12));
            var cream = new SolidColorBrush(Color.FromRgb(0xFA, 0xF3, 0xEA));
            var darkPaw = new SolidColorBrush(Color.FromRgb(0x1E, 0x1B, 0x18));

            // Hintere Beine
            var legBL = CreateLeg(furShade, darkPaw, _legBLRot);
            Canvas.SetLeft(legBL, 17);
            Canvas.SetTop(legBL, 29);
            _foxRoot.Children.Add(legBL);

            var legFL = CreateLeg(furShade, darkPaw, _legFLRot);
            Canvas.SetLeft(legFL, 35);
            Canvas.SetTop(legFL, 29);
            _foxRoot.Children.Add(legFL);

            // Buschiger Schweif mit weißer Spitze
            var tailCanvas = new Canvas
            {
                Width = 24,
                Height = 16,
                RenderTransformOrigin = new Point(0.88, 0.55),
                RenderTransform = _tailRot
            };
            tailCanvas.Children.Add(new Path
            {
                Data = Geometry.Parse("M 21,10 C 14,14 5,11 1,4 C 6,1 14,2 21,7 Z"),
                Fill = furMain
            });
            tailCanvas.Children.Add(new Path
            {
                Data = Geometry.Parse("M 7,7.5 C 4,6.5 2,5 1,4 C 3.5,2.3 6.5,2.5 8.8,4 Z"),
                Fill = cream
            });
            Canvas.SetLeft(tailCanvas, 0);
            Canvas.SetTop(tailCanvas, 17);
            _foxRoot.Children.Add(tailCanvas);

            // Schlanker Körper + Nackenübergang
            _foxRoot.Children.Add(new Path
            {
                Data = Geometry.Parse("M 16,25 C 16,19 23,17 33,17 C 40,17 44,20 43,27 C 42,31 36,32 26,32 C 19,32 16,29 16,25 Z"),
                Fill = furMain
            });
            _foxRoot.Children.Add(new Path
            {
                Data = Geometry.Parse("M 36,19 C 41,20 43,24 41.5,28.5 C 38.5,30 35,29 34,25 Z"),
                Fill = cream
            });

            // Vordere Beine
            var legBR = CreateLeg(furMain, darkPaw, _legBRRot);
            Canvas.SetLeft(legBR, 20);
            Canvas.SetTop(legBR, 30);
            _foxRoot.Children.Add(legBR);

            var legFR = CreateLeg(furMain, darkPaw, _legFRRot);
            Canvas.SetLeft(legFR, 37);
            Canvas.SetTop(legFR, 30);
            _foxRoot.Children.Add(legFR);

            // Kopf-Gruppe (spitze Ohren, schlanke Schnauze, Backenfell)
            var headGroup = new Canvas
            {
                Width = 26,
                Height = 22,
                RenderTransformOrigin = new Point(0.28, 0.72),
                RenderTransform = _headRot
            };
            headGroup.Children.Add(new Path { Data = Geometry.Parse("M 5,8 L 7,1 L 12,6.5 Z"), Fill = furShade });
            headGroup.Children.Add(new Path { Data = Geometry.Parse("M 13,6.5 L 17,0.5 L 20,8 Z"), Fill = furMain });
            headGroup.Children.Add(new Path { Data = Geometry.Parse("M 15,5.5 L 17,1.8 L 18.6,6.5 Z"), Fill = darkPaw });

            headGroup.Children.Add(new Path
            {
                Data = Geometry.Parse("M 4,12 C 4,7 9,5 15,5 C 19.5,5 22.5,8.5 25.5,11.5 C 23,15.5 18.5,17.5 12,17.5 C 7,17.5 4,15 4,12 Z"),
                Fill = furMain
            });
            headGroup.Children.Add(new Path
            {
                Data = Geometry.Parse("M 9,13 C 14,13 20,13.2 25,11.8 C 22.5,15.8 17.5,17.5 12,17.5 C 9.5,16.5 8.5,14.8 9,13 Z"),
                Fill = cream
            });

            var nose = new Ellipse { Width = 3.0, Height = 2.4, Fill = darkPaw };
            Canvas.SetLeft(nose, 23.2);
            Canvas.SetTop(nose, 10.6);
            headGroup.Children.Add(nose);

            _eyeWhite = new Ellipse
            {
                Width = 4.6,
                Height = 4.6,
                Fill = Brushes.White,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _eyeScale
            };
            Canvas.SetLeft(_eyeWhite, 13.5);
            Canvas.SetTop(_eyeWhite, 8.6);
            headGroup.Children.Add(_eyeWhite);

            _pupil = new Ellipse
            {
                Width = 2.4,
                Height = 2.4,
                Fill = darkPaw,
                RenderTransform = _pupilOffset
            };
            Canvas.SetLeft(_pupil, 14.9);
            Canvas.SetTop(_pupil, 9.8);
            headGroup.Children.Add(_pupil);

            _happyEyeArc = new Path
            {
                Data = Geometry.Parse("M 13.2,11.2 Q 15.8,8.5 18.2,11.2"),
                Stroke = darkPaw,
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Visibility = Visibility.Collapsed
            };
            headGroup.Children.Add(_happyEyeArc);

            Canvas.SetLeft(headGroup, 35);
            Canvas.SetTop(headGroup, 5);
            _foxRoot.Children.Add(headGroup);

            _stage.Children.Add(_foxRoot);

            // Minispiel-Objekte: Leckerli & Ball
            _treatItem = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x5E, 0x57)),
                Visibility = Visibility.Collapsed
            };
            _stage.Children.Add(_treatItem);

            _ballItem = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = NotchDesign.AccentPrimaryBrush,
                Visibility = Visibility.Collapsed
            };
            _stage.Children.Add(_ballItem);

            // Herz & Schlaf-Z
            _heartIcon = new Path
            {
                Data = Geometry.Parse("M 12,20 C 12,20 4,14.5 4,9.5 A 4,4 0 0 1 12,7.5 A 4,4 0 0 1 20,9.5 C 20,14.5 12,20 12,20 Z"),
                Fill = NotchDesign.AccentPrimaryBrush,
                Width = compact ? 9 : 15,
                Height = compact ? 9 : 15,
                Stretch = Stretch.Uniform,
                Opacity = 0
            };
            _stage.Children.Add(_heartIcon);

            _sleepIcon = new Path
            {
                Data = Geometry.Parse("M 2,2 L 10,2 L 2,11 L 10,11"),
                Stroke = NotchDesign.TextMutedBrush,
                StrokeThickness = 1.8,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = compact ? 8 : 12,
                Height = compact ? 8 : 12,
                Stretch = Stretch.Uniform,
                Opacity = 0
            };
            _stage.Children.Add(_sleepIcon);

            Content = _stage;

            _lastGlobalCursor = Forms.Cursor.Position;

            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _timer.Tick += OnTick;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            MouseMove += OnPetMouseMove;
            MouseLeftButtonUp += OnPetMouseLeftButtonUp;
        }

        private void UpdateStageMetrics(double w, double h)
        {
            if (w <= 10 || h <= 10) return;
            _stageWidth = w;
            _stageHeight = h;

            if (_compact)
            {
                _foxScale = Math.Clamp((h - 4.0) / 44.0, 0.36, 0.85);
            }
            else
            {
                _foxScale = Math.Clamp((h - 18.0) / 44.0, 0.9, 1.55);
            }

            _foxSizeScale.ScaleX = _foxScale;
            _foxSizeScale.ScaleY = _foxScale;

            double halfW = 32 * _foxScale;
            _foxX = Math.Clamp(_foxX, halfW + 2, Math.Max(halfW + 2, _stageWidth - halfW - 2));
            _targetX = Math.Clamp(_targetX, halfW + 2, Math.Max(halfW + 2, _stageWidth - halfW - 2));
        }

        private static Canvas CreateLeg(Brush upperBrush, Brush pawBrush, RotateTransform rot)
        {
            var c = new Canvas
            {
                Width = 4.5,
                Height = 11.5,
                RenderTransformOrigin = new Point(0.5, 0.1),
                RenderTransform = rot
            };
            c.Children.Add(new Rectangle { Width = 3.8, Height = 9.0, RadiusX = 1.9, RadiusY = 1.9, Fill = upperBrush });
            var paw = new Ellipse { Width = 4.4, Height = 2.8, Fill = pawBrush };
            Canvas.SetTop(paw, 8.5);
            c.Children.Add(paw);
            return c;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (ActualWidth > 10 && ActualHeight > 10)
                UpdateStageMetrics(ActualWidth, ActualHeight);

            _module.PetStateChanged += OnModulePetStateChanged;
            _module.FeedRequested += OnFeedRequested;
            _module.PlayBallRequested += OnPlayBallRequested;
            ApplyMoodVisuals();
            _timer.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            _module.PetStateChanged -= OnModulePetStateChanged;
            _module.FeedRequested -= OnFeedRequested;
            _module.PlayBallRequested -= OnPlayBallRequested;
        }

        private void OnModulePetStateChanged(object? sender, EventArgs e)
            => Dispatcher.BeginInvoke(ApplyMoodVisuals);

        private void OnFeedRequested(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_compact) return;
                double margin = 28;
                _treatX = margin + _rng.NextDouble() * Math.Max(20, _stageWidth - margin * 2);
                _treatY = 8;
                _treatActive = true;
                _treatItem.Visibility = Visibility.Visible;
                _behavior = FoxBehavior.Eating;
                _targetX = _treatX;
            });
        }

        private void OnPlayBallRequested(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_compact) return;
                _ballX = _foxX < _stageWidth * 0.5 ? _stageWidth - 28 : 28;
                _ballY = 18;
                _ballVx = (_foxX < _stageWidth * 0.5 ? -1 : 1) * (95 + _rng.NextDouble() * 40);
                _ballVy = -40;
                _ballActive = true;
                _ballItem.Visibility = Visibility.Visible;
                _behavior = FoxBehavior.ChasingBall;
            });
        }

        private void OnPetMouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(this);
            if (_lastStrokePoint != default)
            {
                _strokeAccumulator += Math.Abs(pos.X - _lastStrokePoint.X) + Math.Abs(pos.Y - _lastStrokePoint.Y);
                if (_strokeAccumulator >= (_compact ? 24 : 48))
                {
                    _strokeAccumulator = 0;
                    TriggerReaction();
                    _module.RegisterStrokeReaction();
                }
            }
            _lastStrokePoint = pos;
        }

        private void OnPetMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            TriggerReaction();
            _module.RegisterClickReaction();
            if (!_compact)
                e.Handled = true;
        }

        private void TriggerReaction()
        {
            _reactionTimerSec = 1.6;
            _behavior = FoxBehavior.Excited;
            _behaviorTimerSec = 1.6;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            const double dt = 0.033;
            _tailPhase += dt * (_behavior == FoxBehavior.Excited ? 10.0 : 3.0);

            var cursor = Forms.Cursor.Position;
            if (Math.Abs(cursor.X - _lastGlobalCursor.X) > 3 || Math.Abs(cursor.Y - _lastGlobalCursor.Y) > 3)
            {
                _lastGlobalCursor = cursor;
                _lastCursorMoveUtc = DateTime.UtcNow;
            }
            _module.UpdateAutoMoodFromIdle((DateTime.UtcNow - _lastCursorMoveUtc).TotalSeconds);

            if (_module.CurrentMood == PetMood.Sleepy && _reactionTimerSec <= 0 && !_treatActive && !_ballActive)
                _behavior = FoxBehavior.Sleeping;

            UpdateMiniGameItems(dt);
            UpdateFoxBehavior(dt);

            double foxW = 64 * _foxScale;
            double foxH = 44 * _foxScale;
            double baseTop = Math.Max(0, _stageHeight - foxH - (_compact ? 1.5 : 8));

            double bobY = 0;
            if (_behavior is FoxBehavior.Walking or FoxBehavior.Eating or FoxBehavior.ChasingBall)
            {
                double gaitSpeed = _behavior == FoxBehavior.ChasingBall ? 16.0 : 10.0;
                _gaitPhase += dt * gaitSpeed;
                double swing = Math.Sin(_gaitPhase) * 22.0;
                _legFLRot.Angle = swing;
                _legBRRot.Angle = swing;
                _legFRRot.Angle = -swing;
                _legBLRot.Angle = -swing;
                bobY = -Math.Abs(Math.Sin(_gaitPhase)) * (_compact ? 0.9 : 2.6);
                _headRot.Angle = Math.Sin(_gaitPhase * 0.5) * 2.5;
            }
            else if (_behavior == FoxBehavior.Excited)
            {
                _legFLRot.Angle = -10;
                _legFRRot.Angle = -10;
                _legBLRot.Angle = 10;
                _legBRRot.Angle = 10;
                bobY = -Math.Abs(Math.Sin(_tailPhase * 0.8)) * (_compact ? 2.2 : 7.0);
                _headRot.Angle = -5;
            }
            else if (_behavior == FoxBehavior.Sleeping)
            {
                _legFLRot.Angle = 52;
                _legFRRot.Angle = 52;
                _legBLRot.Angle = -52;
                _legBRRot.Angle = -52;
                bobY = _compact ? 2.0 : 5.5;
                _headRot.Angle = 11;
            }
            else // Chilling
            {
                _legFLRot.Angle *= 0.8;
                _legFRRot.Angle *= 0.8;
                _legBLRot.Angle *= 0.8;
                _legBRRot.Angle *= 0.8;
                bobY = Math.Sin(_tailPhase * 0.45) * (_compact ? 0.3 : 0.8);
                _headRot.Angle = Math.Sin(_tailPhase * 0.3) * 2.5;
            }

            _tailRot.Angle = _behavior == FoxBehavior.Sleeping
                ? 8
                : Math.Sin(_tailPhase) * (_behavior == FoxBehavior.Excited ? 18 : 7);

            _foxFacing.ScaleX = _facing;
            _foxPos.X = _foxX - foxW * 0.5;
            _foxPos.Y = baseTop + bobY;

            if (_reactionTimerSec > 0)
            {
                _reactionTimerSec = Math.Max(0, _reactionTimerSec - dt);
                _heartIcon.Opacity = Math.Min(1, _reactionTimerSec);
                Canvas.SetLeft(_heartIcon, Math.Clamp(_foxX + (_facing * 10 * _foxScale), 4, _stageWidth - 18));
                Canvas.SetTop(_heartIcon, Math.Max(1, baseTop - 4));
            }
            else
            {
                _heartIcon.Opacity = 0;
            }

            if (_behavior == FoxBehavior.Sleeping)
            {
                _sleepIcon.Opacity = 0.6 + 0.3 * Math.Sin(_tailPhase * 0.5);
                Canvas.SetLeft(_sleepIcon, Math.Clamp(_foxX + (_facing * 12 * _foxScale), 4, _stageWidth - 16));
                Canvas.SetTop(_sleepIcon, Math.Max(1, baseTop - 3));
            }
            else
            {
                _sleepIcon.Opacity = 0;
            }

            UpdatePupilTracking(cursor);
            UpdateBlink(dt);
        }

        private void UpdateMiniGameItems(double dt)
        {
            if (_treatActive)
            {
                double floorY = _stageHeight - 14;
                _treatY = Math.Min(floorY, _treatY + 180 * dt);
                Canvas.SetLeft(_treatItem, _treatX - 4.5);
                Canvas.SetTop(_treatItem, _treatY);
            }

            if (_ballActive)
            {
                double floorY = _stageHeight - 18;
                _ballVy += 320 * dt;
                _ballX += _ballVx * dt;
                _ballY += _ballVy * dt;

                if (_ballX < 12) { _ballX = 12; _ballVx = Math.Abs(_ballVx); }
                if (_ballX > _stageWidth - 12) { _ballX = _stageWidth - 12; _ballVx = -Math.Abs(_ballVx); }
                if (_ballY >= floorY)
                {
                    _ballY = floorY;
                    _ballVy = -Math.Abs(_ballVy) * 0.72;
                    if (Math.Abs(_ballVy) < 35) _ballVy = -95;
                }

                Canvas.SetLeft(_ballItem, _ballX - 6);
                Canvas.SetTop(_ballItem, _ballY);
                _targetX = _ballX;
            }
        }

        private void UpdateFoxBehavior(double dt)
        {
            if (_behavior == FoxBehavior.Eating && _treatActive)
            {
                MoveToward(_treatX, 88 * dt, () =>
                {
                    _treatActive = false;
                    _treatItem.Visibility = Visibility.Collapsed;
                    TriggerReaction();
                });
                return;
            }

            if (_behavior == FoxBehavior.ChasingBall && _ballActive)
            {
                MoveToward(_ballX, 118 * dt, () =>
                {
                    _ballActive = false;
                    _ballItem.Visibility = Visibility.Collapsed;
                    _module.RegisterBallCatch();
                    TriggerReaction();
                });
                return;
            }

            if (_behavior == FoxBehavior.Excited && _reactionTimerSec > 0)
                return;

            _behaviorTimerSec -= dt;

            if (_behavior == FoxBehavior.Walking)
            {
                double speed = _compact ? 24.0 : 54.0;
                MoveToward(_targetX, speed * dt, () =>
                {
                    // Nach dem Laufen ruhig chillen oder ein Nickerchen machen
                    _behavior = _rng.NextDouble() < 0.3 ? FoxBehavior.Sleeping : FoxBehavior.Chilling;
                    _behaviorTimerSec = 5.5 + _rng.NextDouble() * 6.5;
                });
            }
            else if (_behaviorTimerSec <= 0)
            {
                if (_behavior == FoxBehavior.Sleeping && _module.CurrentMood == PetMood.Sleepy)
                    return;

                // In der eingeklappten Notch bevorzugt links oder rechts von der Uhrzeit bleiben
                double halfW = 32 * _foxScale + 6;
                double minX = halfW;
                double maxX = Math.Max(minX + 10, _stageWidth - halfW);

                if (_compact && _stageWidth > 140)
                {
                    bool goLeft = _foxX > _stageWidth * 0.5;
                    _targetX = goLeft
                        ? minX + _rng.NextDouble() * (_stageWidth * 0.24)
                        : maxX - _rng.NextDouble() * (_stageWidth * 0.24);
                }
                else
                {
                    _targetX = minX + _rng.NextDouble() * (maxX - minX);
                }

                _behavior = FoxBehavior.Walking;
                _behaviorTimerSec = 6.0;
            }
        }

        private void MoveToward(double targetX, double maxStep, Action onReached)
        {
            double diff = targetX - _foxX;
            if (Math.Abs(diff) <= Math.Max(4.0, maxStep))
            {
                _foxX = targetX;
                onReached();
            }
            else
            {
                _facing = diff > 0 ? 1 : -1;
                _foxX += Math.Sign(diff) * maxStep;
            }
        }

        private void UpdatePupilTracking(System.Drawing.Point cursor)
        {
            if (!IsVisible) return;
            try
            {
                var center = PointToScreen(new Point(_foxX, _stageHeight * 0.5));
                double dx = (cursor.X - center.X) * _facing;
                double dy = cursor.Y - center.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double targetX = dist > 1 ? (dx / dist) * Math.Min(1.1, dist / 60.0) : 0;
                double targetY = dist > 1 ? (dy / dist) * Math.Min(1.1, dist / 60.0) : 0;
                _pupilOffset.X += (targetX - _pupilOffset.X) * 0.25;
                _pupilOffset.Y += (targetY - _pupilOffset.Y) * 0.25;
            }
            catch { }
        }

        private void UpdateBlink(double dt)
        {
            bool closed = _behavior == FoxBehavior.Sleeping || _reactionTimerSec > 0.2;
            _eyeWhite.Visibility = closed ? Visibility.Collapsed : Visibility.Visible;
            _pupil.Visibility = closed ? Visibility.Collapsed : Visibility.Visible;
            _happyEyeArc.Visibility = closed ? Visibility.Visible : Visibility.Collapsed;

            if (closed) return;

            _blinkTimerSec -= dt;
            if (_blinkTimerSec <= 0 && _blinkProgress < 0)
            {
                _blinkProgress = 0;
                _blinkTimerSec = 2.8 + _rng.NextDouble() * 3.5;
            }

            if (_blinkProgress >= 0)
            {
                _blinkProgress += dt / 0.14;
                double sy = _blinkProgress < 0.5
                    ? 1.0 - (_blinkProgress * 1.8)
                    : (_blinkProgress - 0.5) * 1.8 + 0.1;
                _eyeScale.ScaleY = Math.Clamp(sy, 0.08, 1.0);
                if (_blinkProgress >= 1.0)
                {
                    _blinkProgress = -1;
                    _eyeScale.ScaleY = 1.0;
                }
            }
        }

        private void ApplyMoodVisuals()
        {
            if (_module.CurrentMood == PetMood.Sleepy && _reactionTimerSec <= 0)
            {
                _behavior = FoxBehavior.Sleeping;
                _happyEyeArc.Data = Geometry.Parse("M 13.2,10.5 Q 15.8,13 18.2,10.5");
            }
            else
            {
                _happyEyeArc.Data = Geometry.Parse("M 13.2,11.2 Q 15.8,8.5 18.2,11.2");
                if (_behavior == FoxBehavior.Sleeping && _module.CurrentMood != PetMood.Sleepy)
                {
                    _behavior = FoxBehavior.Chilling;
                    _behaviorTimerSec = 5.0;
                }
            }
        }
    }
}
