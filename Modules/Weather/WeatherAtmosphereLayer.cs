using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsNotch.Core;

namespace WindowsNotch.Modules.Weather
{
    /// <summary>
    /// Hochauflösende, Apple-Weather-inspirierte animierte Wetter-Atmosphäre.
    /// Nutzt Stopwatch-Präzision (kein Frame-Ruckeln), vorberechnete nahtlose Wolken-Texturen
    /// und direktes DrawingContext-Rendering für flüssige 60–240 Hz Partikel.
    /// </summary>
    public sealed class WeatherAtmosphereLayer : Grid
    {
        private enum AtmosphereKind
        {
            None,
            ClearDay,
            ClearNight,
            PartlyCloudyDay,
            Overcast,
            LightRain,
            ModerateRain,
            HeavyRain,
            Thunderstorm,
            LightSnow,
            HeavySnow
        }

        private enum CloudStyle
        {
            PartlyCloudyDay,
            Overcast,
            Rain,
            Storm
        }

        private sealed class RainDrop
        {
            public double Nx;
            public double Ny;
            public double Speed;
            public double WindX;
            public double Length;
            public int Tier; // 0 = fern, 1 = mittel, 2 = nah
        }

        private sealed class SnowFlake
        {
            public double Nx;
            public double Ny;
            public double Vy;
            public double Radius;
            public double Phase;
            public double PhaseSpeed;
            public double SwayAmp;
            public byte Alpha;
        }

        private sealed class BokehFlare
        {
            public double OffsetNorm;
            public double Radius;
            public double Phase;
            public double BaseAlpha;
        }

        private sealed class StarPoint
        {
            public double Nx;
            public double Ny;
            public double Radius;
            public double Phase;
            public double PhaseSpeed;
            public double BaseAlpha;
        }

        private sealed class RenderSurface : FrameworkElement
        {
            public Action<DrawingContext, double, double>? DrawCallback;

            protected override void OnRender(DrawingContext dc)
            {
                base.OnRender(dc);
                if (ActualWidth > 1 && ActualHeight > 1)
                    DrawCallback?.Invoke(dc, ActualWidth, ActualHeight);
            }
        }

        private static readonly ConcurrentDictionary<CloudStyle, BitmapSource> CloudCache = new();
        private static readonly Pen[] RainTierPens = BuildRainPens();
        private static readonly Brush[] SnowAlphaBrushes = BuildAlphaPalette(0xF2, 0xF7, 0xFF);
        private static readonly Brush[] StarAlphaBrushes = BuildAlphaPalette(0xE2, 0xEE, 0xFF);
        private static readonly Brush SoftFlareBrush = BuildSoftFlareBrush();

        private readonly Border _bgLayer = new() { IsHitTestVisible = false };
        private readonly Border _lightningLayer = new() { IsHitTestVisible = false, Background = Brushes.White, Opacity = 0 };
        private readonly RenderSurface _surface = new() { IsHitTestVisible = false };
        private readonly bool _compact;

        private readonly List<RainDrop> _rain = new();
        private readonly List<SnowFlake> _snow = new();
        private readonly List<BokehFlare> _flares = new();
        private readonly List<StarPoint> _stars = new();

        private AtmosphereKind _kind = AtmosphereKind.None;
        private BitmapSource? _cloudTex;
        private ImageBrush? _cloudBrush1;
        private ImageBrush? _cloudBrush2;
        private double _cloudScroll1;
        private double _cloudScroll2;
        private double _cloudSpeed = 0.015;
        private double _cloudHeightRatio = 0.62;
        private double _cloudOpacity = 0.9;

        private Brush? _sunHaloBrush;
        private Brush? _sunCoreBrush;
        private Brush? _sunWarmGlowBrush;

        private bool _isHooked;
        private long _lastTimestamp;
        private double _elapsed;

        public WeatherAtmosphereLayer(bool compact = false)
        {
            _compact = compact;
            IsHitTestVisible = false;
            ClipToBounds = true;

            _surface.DrawCallback = DrawFrame;
            Children.Add(_bgLayer);
            Children.Add(_surface);
            Children.Add(_lightningLayer);

            Loaded += (_, _) => SyncRenderHook();
            Unloaded += (_, _) => UnhookRender();
            IsVisibleChanged += (_, _) => SyncRenderHook();
        }

        public void SetWeather(int? weatherCode, bool isDay, double precipitationMm = 0, int cloudCoverPercent = 0)
        {
            var next = weatherCode.HasValue
                ? MapKind(weatherCode.Value, isDay, precipitationMm, cloudCoverPercent)
                : AtmosphereKind.None;

            if (next == _kind) return;

            _kind = next;
            RebuildScene();
            SyncRenderHook();
            _surface.InvalidateVisual();
        }

        private static AtmosphereKind MapKind(int code, bool isDay, double precipMm, int cloudCover)
        {
            // Gewitter
            if (code is 95 or 96 or 99)
                return AtmosphereKind.Thunderstorm;

            // Regen / Schauer / Niesel (echte Differenzierung anhand Niederschlagsmenge + WMO-Code)
            if (code is (>= 51 and <= 67) or (>= 80 and <= 82))
            {
                if (precipMm >= 4.5 || code is 65 or 67 or 82)
                    return AtmosphereKind.HeavyRain;
                if (precipMm >= 1.2 || code is 55 or 57 or 63 or 81)
                    return AtmosphereKind.ModerateRain;
                return AtmosphereKind.LightRain;
            }

            // Schnee
            if (code is (>= 71 and <= 77) or 85 or 86)
                return (precipMm >= 1.5 || code is 73 or 75 or 86) ? AtmosphereKind.HeavySnow : AtmosphereKind.LightSnow;

            // Klar / Sonnig
            if (code is 0 or 1)
                return isDay ? AtmosphereKind.ClearDay : AtmosphereKind.ClearNight;

            // Bewölkt vs. Teilweise bewölkt (mit CloudCover-Unterstützung)
            if (code == 2 || (code == 3 && cloudCover is > 0 and < 65))
                return isDay ? AtmosphereKind.PartlyCloudyDay : AtmosphereKind.Overcast;

            return AtmosphereKind.Overcast;
        }

        private void RebuildScene()
        {
            _rain.Clear();
            _snow.Clear();
            _flares.Clear();
            _stars.Clear();
            _cloudTex = null;
            _cloudBrush1 = null;
            _cloudBrush2 = null;
            _sunHaloBrush = null;
            _sunCoreBrush = null;
            _sunWarmGlowBrush = null;
            _lightningLayer.Opacity = 0;

            if (_kind == AtmosphereKind.None)
            {
                _bgLayer.Background = Brushes.Transparent;
                return;
            }

            _bgLayer.Background = BuildSkyBrush(_kind);
            var rng = new Random(1337 + (int)_kind * 31);

            switch (_kind)
            {
                case AtmosphereKind.PartlyCloudyDay:
                    _cloudTex = GetOrCreateCloudTexture(CloudStyle.PartlyCloudyDay);
                    _cloudSpeed = 0.012;
                    _cloudHeightRatio = _compact ? 0.72 : 0.66;
                    _cloudOpacity = 0.92;
                    _sunWarmGlowBrush = BuildSunWarmGlow();
                    break;

                case AtmosphereKind.Overcast:
                    _cloudTex = GetOrCreateCloudTexture(CloudStyle.Overcast);
                    _cloudSpeed = 0.016;
                    _cloudHeightRatio = _compact ? 0.78 : 0.74;
                    _cloudOpacity = 0.90;
                    break;

                case AtmosphereKind.LightRain:
                case AtmosphereKind.ModerateRain:
                case AtmosphereKind.HeavyRain:
                case AtmosphereKind.Thunderstorm:
                {
                    bool storm = _kind is AtmosphereKind.HeavyRain or AtmosphereKind.Thunderstorm;
                    _cloudTex = GetOrCreateCloudTexture(storm ? CloudStyle.Storm : CloudStyle.Rain);
                    _cloudSpeed = storm ? 0.032 : 0.020;
                    _cloudHeightRatio = _compact ? 0.66 : 0.60;
                    _cloudOpacity = storm ? 0.92 : 0.78;

                    int count = _kind switch
                    {
                        AtmosphereKind.LightRain => _compact ? 14 : 22,
                        AtmosphereKind.ModerateRain => _compact ? 26 : 42,
                        AtmosphereKind.HeavyRain => _compact ? 42 : 68,
                        _ => _compact ? 46 : 74
                    };

                    for (int i = 0; i < count; i++)
                    {
                        int tier = i % 3; // 0 = fern, 1 = mittel, 2 = nah
                        double baseSpeed = _kind switch
                        {
                            AtmosphereKind.LightRain => 0.72,
                            AtmosphereKind.ModerateRain => 1.15,
                            AtmosphereKind.HeavyRain => 1.65,
                            _ => 1.78
                        };
                        double speedMult = tier == 0 ? 0.78 : tier == 1 ? 1.0 : 1.24;
                        double windBase = _kind switch
                        {
                            AtmosphereKind.LightRain => -0.04,
                            AtmosphereKind.ModerateRain => -0.08,
                            AtmosphereKind.HeavyRain => -0.18,
                            _ => -0.22
                        };
                        double lenBase = _kind switch
                        {
                            AtmosphereKind.LightRain => 15,
                            AtmosphereKind.ModerateRain => 22,
                            _ => 29
                        };

                        _rain.Add(new RainDrop
                        {
                            Nx = rng.NextDouble(),
                            Ny = rng.NextDouble(),
                            Tier = tier,
                            Speed = baseSpeed * speedMult * (0.90 + rng.NextDouble() * 0.22),
                            WindX = windBase * speedMult,
                            Length = lenBase * (0.82 + tier * 0.22 + rng.NextDouble() * 0.20)
                        });
                    }
                    break;
                }

                case AtmosphereKind.LightSnow:
                case AtmosphereKind.HeavySnow:
                {
                    bool heavy = _kind == AtmosphereKind.HeavySnow;
                    _cloudTex = GetOrCreateCloudTexture(CloudStyle.Overcast);
                    _cloudSpeed = 0.014;
                    _cloudHeightRatio = 0.56;
                    _cloudOpacity = 0.55;

                    int count = heavy ? (_compact ? 28 : 48) : (_compact ? 15 : 26);
                    for (int i = 0; i < count; i++)
                    {
                        double depth = rng.NextDouble();
                        _snow.Add(new SnowFlake
                        {
                            Nx = rng.NextDouble(),
                            Ny = rng.NextDouble(),
                            Vy = (heavy ? 0.14 : 0.09) * (0.7 + depth * 0.65),
                            Radius = (heavy ? 1.6 : 1.3) + depth * 1.9,
                            Phase = rng.NextDouble() * Math.PI * 2,
                            PhaseSpeed = 1.0 + rng.NextDouble() * 1.2,
                            SwayAmp = 0.016 + rng.NextDouble() * 0.020,
                            Alpha = (byte)(0x55 + depth * 0x55)
                        });
                    }
                    break;
                }

                case AtmosphereKind.ClearDay:
                {
                    // Apple Weather Bild 2: strahlende Sonne + zarter Spektral-Halo + schräge Lens-Flares
                    _sunHaloBrush = BuildSpectralHaloBrush();
                    _sunCoreBrush = BuildSunCoreBrush();

                    double[] offsets = { 0.22, 0.36, 0.52, 0.68, 0.84 };
                    double[] radii = _compact
                        ? new[] { 16.0, 9.0, 20.0, 12.0, 7.0 }
                        : new[] { 24.0, 13.0, 30.0, 17.0, 10.0 };
                    for (int i = 0; i < offsets.Length; i++)
                    {
                        _flares.Add(new BokehFlare
                        {
                            OffsetNorm = offsets[i],
                            Radius = radii[i],
                            Phase = i * 1.3,
                            BaseAlpha = 0.055 + (i % 2) * 0.03
                        });
                    }
                    break;
                }

                case AtmosphereKind.ClearNight:
                {
                    int stars = _compact ? 14 : 24;
                    for (int i = 0; i < stars; i++)
                    {
                        _stars.Add(new StarPoint
                        {
                            Nx = 0.05 + rng.NextDouble() * 0.90,
                            Ny = 0.05 + rng.NextDouble() * 0.72,
                            Radius = 0.8 + rng.NextDouble() * 1.1,
                            Phase = rng.NextDouble() * Math.PI * 2,
                            PhaseSpeed = 0.8 + rng.NextDouble() * 1.2,
                            BaseAlpha = 0.25 + rng.NextDouble() * 0.55
                        });
                    }
                    break;
                }
            }

            if (_cloudTex != null)
            {
                _cloudBrush1 = new ImageBrush(_cloudTex)
                {
                    TileMode = TileMode.Tile,
                    ViewportUnits = BrushMappingMode.Absolute,
                    Stretch = Stretch.Fill,
                    Opacity = _cloudOpacity
                };
                _cloudBrush2 = new ImageBrush(_cloudTex)
                {
                    TileMode = TileMode.Tile,
                    ViewportUnits = BrushMappingMode.Absolute,
                    Stretch = Stretch.Fill,
                    Opacity = _cloudOpacity * 0.45
                };
            }
        }

        private void SyncRenderHook()
        {
            bool shouldAnimate = IsLoaded && IsVisible && _kind != AtmosphereKind.None && NotchDesign.EffectiveAnimationsEnabled;
            if (shouldAnimate && !_isHooked)
            {
                _lastTimestamp = Stopwatch.GetTimestamp();
                CompositionTarget.Rendering += OnRenderFrame;
                _isHooked = true;
            }
            else if (!shouldAnimate && _isHooked)
            {
                UnhookRender();
            }
        }

        private void UnhookRender()
        {
            if (!_isHooked) return;
            CompositionTarget.Rendering -= OnRenderFrame;
            _isHooked = false;
        }

        private void OnRenderFrame(object? sender, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            double dt = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
            _lastTimestamp = now;
            if (dt <= 0) return;
            if (dt > 0.05) dt = 0.05;

            _elapsed += dt;
            StepScene(dt);
            _surface.InvalidateVisual();
        }

        private void StepScene(double dt)
        {
            if (_cloudTex != null)
            {
                _cloudScroll1 = (_cloudScroll1 + _cloudSpeed * dt) % 1.0;
                _cloudScroll2 = (_cloudScroll2 + _cloudSpeed * 0.58 * dt) % 1.0;
            }

            for (int i = 0; i < _rain.Count; i++)
            {
                var r = _rain[i];
                r.Nx += r.WindX * dt;
                r.Ny += r.Speed * dt;
                if (r.Ny > 1.12)
                {
                    r.Ny = -0.14;
                    r.Nx = (r.Nx + 0.317) % 1.0;
                }
                if (r.Nx < -0.10) r.Nx += 1.20;
            }

            if (_kind == AtmosphereKind.Thunderstorm)
            {
                double cycle = _elapsed % 7.2;
                _lightningLayer.Opacity = cycle switch
                {
                    >= 0.0 and < 0.07 => 0.09,
                    >= 0.13 and < 0.20 => 0.06,
                    _ => 0.0
                };
            }

            for (int i = 0; i < _snow.Count; i++)
            {
                var f = _snow[i];
                f.Phase += f.PhaseSpeed * dt;
                f.Nx += Math.Sin(f.Phase) * f.SwayAmp * dt;
                f.Ny += f.Vy * dt;
                if (f.Ny > 1.06) f.Ny = -0.06;
                if (f.Nx < -0.05) f.Nx += 1.10;
                else if (f.Nx > 1.05) f.Nx -= 1.10;
            }
        }

        private void DrawFrame(DrawingContext dc, double w, double h)
        {
            if (_kind == AtmosphereKind.None) return;

            // 1. Warmer Sonnenschein hinter teilweise bewölktem Himmel (Apple Bild 1 oben links)
            if (_sunWarmGlowBrush != null)
            {
                double gx = w * 0.16;
                double gy = h * 0.05;
                double gr = Math.Max(w, h) * 0.42;
                dc.DrawEllipse(_sunWarmGlowBrush, null, new Point(gx, gy), gr, gr * 0.85);
            }

            // 2. Klare Sonne mit Regenbogen-Halo & Bokeh-Lens-Flare (Apple Bild 2)
            if (_kind == AtmosphereKind.ClearDay && _sunHaloBrush != null && _sunCoreBrush != null)
            {
                double sx = w * 0.78;
                double sy = h * 0.08;
                double pulse = 1.0 + 0.035 * Math.Sin(_elapsed * 1.1);
                double haloR = Math.Max(w, h) * 0.56 * pulse;
                double coreR = Math.Max(w, h) * 0.19 * (1.0 + 0.02 * Math.Sin(_elapsed * 1.6));

                dc.DrawEllipse(_sunHaloBrush, null, new Point(sx, sy), haloR, haloR);
                dc.DrawEllipse(_sunCoreBrush, null, new Point(sx, sy), coreR, coreR);

                // Weiche Bokeh-Lichtreflexe diagonal von der Sonne ins Bild (ohne harte Kanten)
                double targetX = w * 0.32;
                double targetY = h * 0.90;
                for (int i = 0; i < _flares.Count; i++)
                {
                    var fl = _flares[i];
                    double wave = 0.65 + 0.35 * Math.Sin(_elapsed * 0.85 + fl.Phase);
                    double fx = sx + (targetX - sx) * fl.OffsetNorm;
                    double fy = sy + (targetY - sy) * fl.OffsetNorm;
                    dc.PushOpacity(fl.BaseAlpha * wave * 4.5);
                    dc.DrawEllipse(SoftFlareBrush, null, new Point(fx, fy), fl.Radius * 1.4, fl.Radius * 1.4);
                    dc.Pop();
                }
            }

            // 3. Funkelnde Sterne bei klarer Nacht
            for (int i = 0; i < _stars.Count; i++)
            {
                var st = _stars[i];
                double wave = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(_elapsed * st.PhaseSpeed + st.Phase));
                int bIdx = Math.Clamp((int)(st.BaseAlpha * wave * 15.0), 0, 15);
                dc.DrawEllipse(StarAlphaBrushes[bIdx], null, new Point(st.Nx * w, st.Ny * h), st.Radius, st.Radius);
            }

            // 4. Mehrschichtige nahtlose Apple-Style Wolken (Viewport-Höhe > h verhindert Y-Wrap-Around-Striche)
            if (_cloudBrush1 != null && _cloudBrush2 != null)
            {
                double cloudH = h * Math.Max(1.05, _cloudHeightRatio + 0.28);
                double tileW = Math.Max(w, cloudH * 2.0);

                double x2 = -_cloudScroll2 * tileW;
                _cloudBrush2.Viewport = new Rect(x2, 0, tileW, cloudH);
                dc.DrawRectangle(_cloudBrush2, null, new Rect(0, 0, w, h));

                double x1 = -_cloudScroll1 * tileW;
                _cloudBrush1.Viewport = new Rect(x1, 0, tileW, cloudH * 1.06);
                dc.DrawRectangle(_cloudBrush1, null, new Rect(0, 0, w, h));
            }

            // 5. Flüssige Regen-Fäden mit Gradient-Verlauf (oben transparent -> unten spitz/hell)
            for (int i = 0; i < _rain.Count; i++)
            {
                var r = _rain[i];
                double x2 = r.Nx * w;
                double y2 = r.Ny * h;
                double slant = r.WindX * r.Length * 1.35;
                double x1 = x2 - slant;
                double y1 = y2 - r.Length;
                dc.DrawLine(RainTierPens[r.Tier], new Point(x1, y1), new Point(x2, y2));
            }

            // 6. Sanfte Schneeflocken
            for (int i = 0; i < _snow.Count; i++)
            {
                var f = _snow[i];
                int bIdx = Math.Clamp(f.Alpha * 15 / 255, 0, 15);
                dc.DrawEllipse(SnowAlphaBrushes[bIdx], null, new Point(f.Nx * w, f.Ny * h), f.Radius, f.Radius);
            }
        }

        private static Brush BuildSkyBrush(AtmosphereKind kind)
        {
            var (c1, c2) = kind switch
            {
                AtmosphereKind.ClearDay => (Color.FromArgb(0xD8, 0x24, 0x62, 0xB2), Color.FromArgb(0xB8, 0x16, 0x45, 0x82)),
                AtmosphereKind.ClearNight => (Color.FromArgb(0xD0, 0x0F, 0x1A, 0x3A), Color.FromArgb(0xB0, 0x08, 0x0D, 0x1E)),
                AtmosphereKind.PartlyCloudyDay => (Color.FromArgb(0xD0, 0x35, 0x6D, 0xAD), Color.FromArgb(0xB4, 0x20, 0x4D, 0x85)),
                AtmosphereKind.Overcast => (Color.FromArgb(0xD0, 0x3A, 0x46, 0x58), Color.FromArgb(0xB4, 0x22, 0x2B, 0x37)),
                AtmosphereKind.LightRain => (Color.FromArgb(0xD0, 0x2B, 0x38, 0x48), Color.FromArgb(0xB4, 0x1A, 0x22, 0x2E)),
                AtmosphereKind.ModerateRain => (Color.FromArgb(0xD8, 0x25, 0x30, 0x3E), Color.FromArgb(0xBC, 0x16, 0x1D, 0x27)),
                AtmosphereKind.HeavyRain => (Color.FromArgb(0xDE, 0x1F, 0x27, 0x34), Color.FromArgb(0xC4, 0x12, 0x17, 0x20)),
                AtmosphereKind.Thunderstorm => (Color.FromArgb(0xE2, 0x1C, 0x22, 0x2E), Color.FromArgb(0xC8, 0x0E, 0x13, 0x1C)),
                AtmosphereKind.LightSnow => (Color.FromArgb(0xCA, 0x40, 0x56, 0x72), Color.FromArgb(0xAE, 0x22, 0x30, 0x42)),
                AtmosphereKind.HeavySnow => (Color.FromArgb(0xD2, 0x48, 0x60, 0x7D), Color.FromArgb(0xB6, 0x26, 0x34, 0x47)),
                _ => (Colors.Transparent, Colors.Transparent)
            };

            var brush = new LinearGradientBrush(c1, c2, 90);
            brush.Freeze();
            return brush;
        }

        private static Pen[] BuildRainPens()
        {
            byte[] bottomAlphas = { 0x52, 0x80, 0xAC };
            double[] thicknesses = { 0.8, 1.05, 1.3 };
            var pens = new Pen[3];
            for (int t = 0; t < 3; t++)
            {
                var b = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 0.0),
                    EndPoint = new Point(0.5, 1.0),
                    GradientStops = new GradientStopCollection
                    {
                        new(Color.FromArgb(0x00, 0xDC, 0xEC, 0xFF), 0.0),
                        new(Color.FromArgb((byte)(bottomAlphas[t] / 2), 0xDC, 0xEC, 0xFF), 0.55),
                        new(Color.FromArgb(bottomAlphas[t], 0xEB, 0xF5, 0xFF), 1.0)
                    }
                };
                b.Freeze();
                var pen = new Pen(b, thicknesses[t])
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                pen.Freeze();
                pens[t] = pen;
            }
            return pens;
        }

        private static Brush BuildSoftFlareBrush()
        {
            var b = new RadialGradientBrush
            {
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(0x2A, 0xE6, 0xF4, 0xFF), 0.0),
                    new(Color.FromArgb(0x12, 0xD4, 0xEA, 0xFF), 0.55),
                    new(Color.FromArgb(0x00, 0xD4, 0xEA, 0xFF), 1.0)
                }
            };
            b.Freeze();
            return b;
        }

        private static Brush[] BuildAlphaPalette(byte r, byte g, byte b)
        {
            var list = new Brush[16];
            for (int i = 0; i < list.Length; i++)
            {
                byte alpha = (byte)(8 + i * 15);
                var brush = new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
                brush.Freeze();
                list[i] = brush;
            }
            return list;
        }

        private static Brush BuildSpectralHaloBrush()
        {
            var b = new RadialGradientBrush
            {
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(0x40, 0xFF, 0xFA, 0xE6), 0.00),
                    new(Color.FromArgb(0x28, 0xFF, 0xEE, 0xBD), 0.32),
                    new(Color.FromArgb(0x16, 0xFF, 0xCC, 0x80), 0.52),
                    new(Color.FromArgb(0x12, 0x9C, 0xE6, 0xD8), 0.68),
                    new(Color.FromArgb(0x10, 0x8A, 0xB8, 0xFF), 0.82),
                    new(Color.FromArgb(0x00, 0x52, 0x94, 0xE2), 1.00)
                }
            };
            b.Freeze();
            return b;
        }

        private static Brush BuildSunCoreBrush()
        {
            var b = new RadialGradientBrush
            {
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(0xEA, 0xFF, 0xFF, 0xFA), 0.00),
                    new(Color.FromArgb(0x8A, 0xFF, 0xF5, 0xD6), 0.45),
                    new(Color.FromArgb(0x00, 0xFF, 0xE4, 0x9E), 1.00)
                }
            };
            b.Freeze();
            return b;
        }

        private static Brush BuildSunWarmGlow()
        {
            var b = new RadialGradientBrush
            {
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(0x90, 0xFF, 0xF8, 0xE4), 0.0),
                    new(Color.FromArgb(0x38, 0xFF, 0xE8, 0xBA), 0.48),
                    new(Color.FromArgb(0x00, 0xFF, 0xE8, 0xBA), 1.0)
                }
            };
            b.Freeze();
            return b;
        }

        private static BitmapSource GetOrCreateCloudTexture(CloudStyle style)
            => CloudCache.GetOrAdd(style, BuildCloudTexture);

        private static BitmapSource BuildCloudTexture(CloudStyle style)
        {
            const int width = 320;
            const int height = 160;
            var pixels = new byte[width * height * 4];

            var (lit, shadow) = style switch
            {
                CloudStyle.PartlyCloudyDay => (
                    Color.FromRgb(0xFA, 0xFC, 0xFF),
                    Color.FromRgb(0x92, 0xA8, 0xC4)),
                CloudStyle.Overcast => (
                    Color.FromRgb(0xCF, 0xDA, 0xE6),
                    Color.FromRgb(0x63, 0x72, 0x86)),
                CloudStyle.Rain => (
                    Color.FromRgb(0x88, 0x99, 0xAD),
                    Color.FromRgb(0x37, 0x46, 0x58)),
                _ => (
                    Color.FromRgb(0x6D, 0x7D, 0x91),
                    Color.FromRgb(0x23, 0x2D, 0x3B))
            };

            double threshold = style == CloudStyle.PartlyCloudyDay ? 0.42 : 0.30;

            for (int y = 0; y < height; y++)
            {
                double ny = (double)y / height;
                // Vertikale Maske: oben voll da, bis ny=0.78 komplett auf 0 auslaufend (kein Wrap-Around-Rand)
                double verticalMask = ny < 0.12
                    ? 1.0
                    : Math.Clamp(1.0 - SmoothStep(0.14, 0.78, ny), 0.0, 1.0);

                for (int x = 0; x < width; x++)
                {
                    double nx = (double)x / width;

                    double n = FbmPeriodic(nx, ny);
                    double nUp = FbmPeriodic(nx, Math.Max(0, ny - 0.045));

                    double density = Math.Clamp((n - threshold) / (1.0 - threshold), 0.0, 1.0);
                    density = Math.Pow(density, style == CloudStyle.PartlyCloudyDay ? 0.85 : 0.72);
                    double alpha = Math.Clamp(density * verticalMask * 1.15, 0.0, 1.0);

                    // Volumetrische Schattierung: Oberkante hell, Unterseite wolkig-schattig
                    double light = Math.Clamp(0.55 + (n - nUp) * 3.4 - ny * 0.25, 0.0, 1.0);
                    byte r = (byte)(shadow.R + (lit.R - shadow.R) * light);
                    byte g = (byte)(shadow.G + (lit.G - shadow.G) * light);
                    byte b = (byte)(shadow.B + (lit.B - shadow.B) * light);
                    byte a = (byte)(alpha * 255);

                    // Premultiplied BGRA für WriteableBitmap (Pbgra32)
                    int idx = (y * width + x) * 4;
                    pixels[idx + 0] = (byte)(b * a / 255);
                    pixels[idx + 1] = (byte)(g * a / 255);
                    pixels[idx + 2] = (byte)(r * a / 255);
                    pixels[idx + 3] = a;
                }
            }

            var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
            bmp.Freeze();
            return bmp;
        }

        private static double FbmPeriodic(double nx, double ny)
        {
            double sum = 0;
            sum += 0.52 * ValueNoisePeriodic(nx, ny, 4, 3);
            sum += 0.28 * ValueNoisePeriodic(nx + 0.17, ny + 0.11, 8, 6);
            sum += 0.14 * ValueNoisePeriodic(nx + 0.43, ny + 0.29, 16, 12);
            sum += 0.06 * ValueNoisePeriodic(nx + 0.71, ny + 0.53, 32, 24);
            return sum;
        }

        private static double ValueNoisePeriodic(double nx, double ny, int periodX, int periodY)
        {
            double fx = ((nx % 1.0) + 1.0) % 1.0 * periodX;
            double fy = Math.Clamp(ny, 0.0, 0.9999) * periodY;

            int x0 = (int)Math.Floor(fx) % periodX;
            int y0 = (int)Math.Floor(fy) % periodY;
            int x1 = (x0 + 1) % periodX;
            int y1 = (y0 + 1) % periodY;

            double tx = Smooth(fx - Math.Floor(fx));
            double ty = Smooth(fy - Math.Floor(fy));

            double v00 = Hash01(x0, y0, periodX);
            double v10 = Hash01(x1, y0, periodX);
            double v01 = Hash01(x0, y1, periodX);
            double v11 = Hash01(x1, y1, periodX);

            double a = v00 + (v10 - v00) * tx;
            double b = v01 + (v11 - v01) * tx;
            return a + (b - a) * ty;
        }

        private static double Hash01(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFF) / 65535.0;
            }
        }

        private static double Smooth(double t) => t * t * (3.0 - 2.0 * t);

        private static double SmoothStep(double edge0, double edge1, double x)
        {
            double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }
    }
}
