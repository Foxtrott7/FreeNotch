using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WindowsNotch.Services;

namespace WindowsNotch.Core
{
    public enum NotchIconKind
    {
        Dashboard,
        FileTray,
        Clipboard,
        Play,
        Pause,
        Previous,
        Next,
        Music,
        Lock,
        Pulse,
        Settings,
        SettingsGear,
        Folder,
        Bell,
        VolumeOn,
        VolumeOff,
        Clock,
        Timer,
        Reset,
        Plus,
        Minus,
        Check,
        Close,
        Bolt,
        Sparkle,
        Monitor,
        Layers,
        Shield,
        Record,
        Bluetooth,
        Discord,
        Phone,
        Download,
        Waveform,
        Scan,
        Trash,
        ChevronLeft,
        ChevronRight,
        Calendar,
        Sun,
        Cloud,
        CloudSun,
        CloudRain,
        CloudSnow,
        CloudLightning,
        CloudFog,
        CheckSquare,
        Note,
        Cpu,
        Memory,
        Gpu,
        Pin,
        Search,
        Copy,
        Link,
        Image,
        File,
        ExternalLink,
        QrCode,
        Translate,
        Save,
        Headphones,
        Moon,
        Wifi,
        Airplane,
        Hotspot,
        Battery,
        Microphone,
        Pet,
        TrendingUp,
        Star
    }

    /// <summary>
    /// Zentrales Design-System im Notchify-Stil (OLED-Schwarz, konkave Ohren, ruhige Karten & eigene Vektor-Icons).
    /// </summary>
    public static class NotchDesign
    {
        // Feste CornerRadius-Skala (DIP)
        public const double RadiusShell = 22;
        public const double RadiusCard = 16;
        public const double RadiusInner = 12;
        public const double RadiusControl = 10;
        public const double RadiusSmall = 6;

        // Zentrale Motion-Profile (Apple-Stil: ruhig-snappy, über AnimationSpeedScale anpassbar)
        private const int BaseSpringOpenDurationMs = 450;
        private const int BaseTabSwitchDurationMs = 430;
        private const int BaseCollapseDurationMs = 410;
        private const int BaseDrawerOpenDurationMs = 410;
        private const int BaseMicroTransitionMs = 300;

        public static double AnimationSpeedScale =>
            Math.Clamp(SettingsService.Current.AnimationSpeedScale, 0.55, 2.0);

        public static int ScaleDuration(int baseMs) =>
            Math.Max(80, (int)Math.Round(baseMs * AnimationSpeedScale));

        public static int SpringOpenDurationMs => ScaleDuration(BaseSpringOpenDurationMs);
        public static int TabSwitchDurationMs => ScaleDuration(BaseTabSwitchDurationMs);
        public static int CollapseDurationMs => ScaleDuration(BaseCollapseDurationMs);
        public static int DrawerOpenDurationMs => ScaleDuration(BaseDrawerOpenDurationMs);
        public static int DrawerCloseDurationMs => CollapseDurationMs;
        public static int MicroTransitionMs => ScaleDuration(BaseMicroTransitionMs);
        public const int ButtonHoverDurationMs = 190;
        public const int ButtonPressDurationMs = 115;
        public const int ButtonReleaseDurationMs = 210;
        public const int ButtonReleaseSpringMs = 260;
        public const int BentoHoverEnterMs = 320;
        public const int BentoHoverLeaveMs = 390;
        public const int BentoPressReleaseMs = 340;
        public const double SpringOpenAmplitude = 0.50;
        public const double TabSwitchAmplitude = 0.46;

        public static readonly FontFamily UiTextFont = new("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily UiDisplayFont = new("Segoe UI Variable Display, Segoe UI");

        /// <summary>Berücksichtigt sowohl die App-Einstellung als auch Windows „Animationseffekte“ (prefers-reduced-motion).</summary>
        public static bool EffectiveAnimationsEnabled =>
            SettingsService.Current.AnimationsEnabled && SystemParameters.ClientAreaAnimation;

        public static IEasingFunction CreateSpringEase(double amplitude = SpringOpenAmplitude) =>
            new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = amplitude };

        public static IEasingFunction CreateEaseOut() =>
            new CubicEase { EasingMode = EasingMode.EaseOut };

        public static IEasingFunction CreateEaseInOut() =>
            new CubicEase { EasingMode = EasingMode.EaseInOut };

        /// <summary>Sanfter Einblend-Übergang für Sub-Views, Drawer oder den Modul-Shop (analog zur Notch-Animation).</summary>
        public static void AnimateSubViewEnter(UIElement element, double slideY = 6, int? durationMs = null)
        {
            if (!EffectiveAnimationsEnabled) return;

            var translate = new TranslateTransform(0, slideY);
            element.RenderTransform = translate;
            element.Opacity = 0;

            var dur = TimeSpan.FromMilliseconds(durationMs ?? DrawerOpenDurationMs);
            var ease = CreateEaseInOut();
            int fps = SettingsService.Current.TargetFrameRate;
            var opAnim = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
            var trAnim = new DoubleAnimation(slideY, 0, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(opAnim, fps);
            Timeline.SetDesiredFrameRate(trAnim, fps);
            element.BeginAnimation(UIElement.OpacityProperty, opAnim);
            translate.BeginAnimation(TranslateTransform.YProperty, trAnim);
        }

        /// <summary>Ruhiges Ausblenden für Drawer / Modul-Shop (analog zur Notch-Schließ-Animation).</summary>
        public static void AnimateDrawerExit(UIElement element, Action onCompleted, double slideY = 8)
        {
            if (!EffectiveAnimationsEnabled)
            {
                onCompleted();
                return;
            }

            if (element.RenderTransform is not TranslateTransform translate)
            {
                translate = new TranslateTransform(0, 0);
                element.RenderTransform = translate;
            }

            var dur = TimeSpan.FromMilliseconds(DrawerCloseDurationMs);
            var ease = CreateEaseInOut();
            int fps = SettingsService.Current.TargetFrameRate;
            var opAnim = new DoubleAnimation(element.Opacity, 0, dur) { EasingFunction = ease };
            var trAnim = new DoubleAnimation(0, slideY, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(opAnim, fps);
            Timeline.SetDesiredFrameRate(trAnim, fps);

            opAnim.Completed += (_, _) => onCompleted();
            element.BeginAnimation(UIElement.OpacityProperty, opAnim);
            translate.BeginAnimation(TranslateTransform.YProperty, trAnim);
        }

        /// <summary>Subtiler, hochwertiger Einrast-Impuls beim Bestätigen des Jiggle-/Bearbeitungsmodus.</summary>
        public static void PlaySubtleCommitPulse(ScaleTransform scale, int? targetFps = null)
        {
            if (!EffectiveAnimationsEnabled)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = 1.0;
                scale.ScaleY = 1.0;
                return;
            }

            int fps = targetFps ?? SettingsService.Current.TargetFrameRate;
            var downEase = CreateEaseOut();
            var settleEase = CreateEaseInOut();

            var keyX = new DoubleAnimationUsingKeyFrames();
            keyX.KeyFrames.Add(new EasingDoubleKeyFrame(0.988, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(115)), downEase));
            keyX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(365)), settleEase));
            Timeline.SetDesiredFrameRate(keyX, fps);

            var keyY = keyX.Clone();
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, keyX);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, keyY);
        }

        private static readonly Dictionary<uint, SolidColorBrush> FrozenBrushCache = new();
        private static readonly object BrushCacheLock = new();
        private static Style? _slimScrollBarLightStyle;
        private static Style? _slimScrollBarDarkStyle;

        public static SolidColorBrush GetFrozenBrush(Color color)
        {
            uint argb = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
            lock (BrushCacheLock)
            {
                if (FrozenBrushCache.TryGetValue(argb, out var cached))
                    return cached;

                var brush = new SolidColorBrush(color);
                brush.Freeze();
                if (FrozenBrushCache.Count >= 128)
                    FrozenBrushCache.Clear();
                FrozenBrushCache[argb] = brush;
                return brush;
            }
        }

        public static bool IsLight => SettingsService.Current.Theme is NotchTheme.Light or NotchTheme.LightGlass;
        public static bool IsGlass => SettingsService.Current.Theme is NotchTheme.DarkGlass or NotchTheme.LightGlass;

        public static Color NotchBackgroundColor => SettingsService.Current.Theme switch
        {
            NotchTheme.DarkGlass => Color.FromArgb(0xD8, 0x06, 0x06, 0x08),
            NotchTheme.Light => Color.FromRgb(0xF5, 0xF5, 0xF7),
            NotchTheme.LightGlass => Color.FromArgb(0xDC, 0xF6, 0xF6, 0xF9),
            _ => Color.FromRgb(0x00, 0x00, 0x00)
        };

        public static Brush NotchBackgroundBrush => GetFrozenBrush(NotchBackgroundColor);

        public static Brush NotchBorderBrush => GetFrozenBrush(IsLight
            ? Color.FromArgb(0x18, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));

        public static Brush TabBarBackgroundBrush => Brushes.Transparent;

        public static Brush TabActiveBackgroundBrush => GetFrozenBrush(IsLight
            ? Color.FromArgb(0x14, 0x00, 0x00, 0x00)
            : Color.FromRgb(0x1E, 0x1E, 0x24));

        public static Brush CardBackgroundBrush => GetFrozenBrush(SettingsService.Current.Theme switch
        {
            NotchTheme.DarkGlass => Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF),
            NotchTheme.Light => Color.FromRgb(0xFF, 0xFF, 0xFF),
            NotchTheme.LightGlass => Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF),
            _ => Color.FromRgb(0x11, 0x11, 0x14)
        });

        public static Brush NestedCardBackgroundBrush => GetFrozenBrush(SettingsService.Current.Theme switch
        {
            NotchTheme.DarkGlass => Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF),
            NotchTheme.Light => Color.FromRgb(0xEC, 0xEC, 0xF0),
            NotchTheme.LightGlass => Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF),
            _ => Color.FromRgb(0x18, 0x18, 0x1D)
        });

        public static Brush CardBorderBrush => GetFrozenBrush(IsLight
            ? Color.FromArgb(0x10, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF));

        public static Brush TextPrimaryBrush => GetFrozenBrush(IsLight
            ? Color.FromRgb(0x14, 0x14, 0x18)
            : Colors.White);

        public static Brush TextSecondaryBrush => GetFrozenBrush(IsLight
            ? Color.FromRgb(0x63, 0x63, 0x6E)
            : Color.FromRgb(0x8E, 0x8E, 0x93));

        /// <summary>Hellerer Weißton für inaktive Icons in der oberen Tab-Leiste.</summary>
        public static Brush TabIconInactiveBrush => GetFrozenBrush(IsLight
            ? Color.FromRgb(0x3A, 0x3A, 0x44)
            : Color.FromRgb(0xF0, 0xF0, 0xF5));

        public static Brush TextMutedBrush => GetFrozenBrush(IsLight
            ? Color.FromArgb(0x85, 0x14, 0x14, 0x18)
            : Color.FromRgb(0x63, 0x63, 0x66));

        public static Brush ControlBackgroundBrush => GetFrozenBrush(IsLight
            ? Color.FromArgb(0x10, 0x00, 0x00, 0x00)
            : Color.FromRgb(0x1C, 0x1C, 0x22));

        public static Brush ControlHoverBrush => GetFrozenBrush(IsLight
            ? Color.FromArgb(0x1A, 0x00, 0x00, 0x00)
            : Color.FromRgb(0x28, 0x28, 0x30));

        public static Brush ControlPressedBrush => GetFrozenBrush(IsLight
            ? Color.FromArgb(0x26, 0x00, 0x00, 0x00)
            : Color.FromRgb(0x32, 0x32, 0x3C));

        public static Brush TrackBrush => GetFrozenBrush(IsLight
            ? Color.FromArgb(0x1C, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));

        public static Brush DividerBrush => GetFrozenBrush(IsLight
            ? Color.FromArgb(0x12, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

        public static Brush SoftBlueBackgroundBrush => GetFrozenBrush(IsLight
            ? Color.FromArgb(0x1C, 0x00, 0x66, 0xCC)
            : Color.FromRgb(0x16, 0x22, 0x38));

        public static Brush SoftBlueForegroundBrush => GetFrozenBrush(IsLight
            ? Color.FromRgb(0x00, 0x66, 0xCC)
            : Color.FromRgb(0x60, 0xA5, 0xFA));

        public static Color AccentBlue => IsLight ? Color.FromRgb(0x00, 0x66, 0xCC) : Color.FromRgb(0x25, 0x63, 0xEB);
        public static Color AccentPurple => IsLight ? Color.FromRgb(0x7C, 0x3A, 0xED) : Color.FromRgb(0x8B, 0x5C, 0xF6);
        public static Color AccentGreen => IsLight ? Color.FromRgb(0x24, 0x8A, 0x3D) : Color.FromRgb(0x30, 0xD1, 0x58);
        public static Color AccentOrange => IsLight ? Color.FromRgb(0xC9, 0x5B, 0x00) : Color.FromRgb(0xFF, 0x9F, 0x0A);
        public static Color AccentRed => IsLight ? Color.FromRgb(0xD7, 0x00, 0x15) : Color.FromRgb(0xEA, 0x43, 0x35);

        public sealed record AccentThemePreset(
            string Id,
            string Name,
            Color Start,
            Color Mid,
            Color End,
            Color Primary);

        /// <summary>14 kuratierte Farbthemen im Notchify-Stil.</summary>
        public static readonly System.Collections.Generic.IReadOnlyList<AccentThemePreset> AccentPresets =
            new AccentThemePreset[]
            {
                new("mono-gray",      "Neutralgrau (Ohne Farbe)", Color.FromRgb(0x38, 0x38, 0x3E), Color.FromRgb(0x4C, 0x4C, 0x54), Color.FromRgb(0x6A, 0x6A, 0x74), Color.FromRgb(0x56, 0x56, 0x5E)),
                new("magenta-violet", "Notchify Magenta", Color.FromRgb(0x9D, 0x17, 0x4D), Color.FromRgb(0xC0, 0x26, 0xD3), Color.FromRgb(0xE8, 0x79, 0xF9), Color.FromRgb(0xD9, 0x46, 0xEF)),
                new("indigo-violet",  "Indigo-Violett (Standard)", Color.FromRgb(0x28, 0x22, 0xA8), Color.FromRgb(0x6D, 0x28, 0xD9), Color.FromRgb(0x93, 0x33, 0xEA), Color.FromRgb(0x8B, 0x5C, 0xF6)),
                new("royal-blue",     "Königsblau", Color.FromRgb(0x1D, 0x4E, 0xD8), Color.FromRgb(0x25, 0x63, 0xEB), Color.FromRgb(0x38, 0xBD, 0xF8), Color.FromRgb(0x3B, 0x82, 0xF6)),
                new("cyan-ocean",     "Ozean-Cyan", Color.FromRgb(0x0E, 0x74, 0x90), Color.FromRgb(0x06, 0xB6, 0xD4), Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0x06, 0xB6, 0xD4)),
                new("emerald",        "Smaragdgrün", Color.FromRgb(0x04, 0x78, 0x57), Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0x34, 0xD3, 0x99), Color.FromRgb(0x10, 0xB9, 0x81)),
                new("lime-volt",      "Limette-Volt", Color.FromRgb(0x4D, 0x7C, 0x0F), Color.FromRgb(0x65, 0xA3, 0x0D), Color.FromRgb(0xA3, 0xE6, 0x35), Color.FromRgb(0x84, 0xCC, 0x16)),
                new("amber-gold",     "Bernstein-Gold", Color.FromRgb(0xB4, 0x53, 0x09), Color.FromRgb(0xD9, 0x77, 0x06), Color.FromRgb(0xFB, 0xBF, 0x24), Color.FromRgb(0xF5, 0x9E, 0x0B)),
                new("sunset-orange",  "Sonnenuntergang", Color.FromRgb(0xC2, 0x41, 0x0C), Color.FromRgb(0xEA, 0x58, 0x0C), Color.FromRgb(0xFB, 0x92, 0x3C), Color.FromRgb(0xF9, 0x73, 0x16)),
                new("coral-red",      "Korallenrot", Color.FromRgb(0xB9, 0x1C, 0x1C), Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0xF8, 0x71, 0x71), Color.FromRgb(0xEF, 0x44, 0x44)),
                new("rose-pink",      "Rosé-Pink", Color.FromRgb(0xBE, 0x18, 0x5D), Color.FromRgb(0xEC, 0x48, 0x99), Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0xEC, 0x48, 0x99)),
                new("slate-steel",    "Schiefergrau", Color.FromRgb(0x33, 0x41, 0x55), Color.FromRgb(0x64, 0x74, 0x8B), Color.FromRgb(0x94, 0xA3, 0xB8), Color.FromRgb(0x94, 0xA3, 0xB8)),
                new("silver-frost",   "Silber-Frost", Color.FromRgb(0x52, 0x52, 0x5B), Color.FromRgb(0xA1, 0xA1, 0xAA), Color.FromRgb(0xE4, 0xE4, 0xE7), Color.FromRgb(0xD4, 0xD4, 0xD8)),
                new("midnight-navy",  "Mitternachtsblau", Color.FromRgb(0x1E, 0x29, 0x3B), Color.FromRgb(0x33, 0x41, 0x55), Color.FromRgb(0x47, 0x55, 0x69), Color.FromRgb(0x60, 0xA5, 0xFA)),
                new("lavender-iris",  "Lavendel-Iris", Color.FromRgb(0x4C, 0x1D, 0x95), Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0xA7, 0x8B, 0xFA), Color.FromRgb(0xA7, 0x8B, 0xFA)),
            };

        public static AccentThemePreset GetActiveAccentPreset()
        {
            string id = SettingsService.Current.AccentThemeId;
            foreach (var p in AccentPresets)
                if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                    return p;
            return AccentPresets[1];
        }

        public static Color AccentGradientStart
        {
            get
            {
                var s = SettingsService.Current;
                if (s.UseCustomAccent && TryParseHexColor(s.CustomAccentStartHex, out var c))
                    return c;
                return GetActiveAccentPreset().Start;
            }
        }

        public static Color AccentGradientEnd
        {
            get
            {
                var s = SettingsService.Current;
                if (s.UseCustomAccent && TryParseHexColor(s.CustomAccentEndHex, out var c))
                    return c;
                return GetActiveAccentPreset().End;
            }
        }

        public static Color AccentGradientMid
        {
            get
            {
                var s = SettingsService.Current;
                if (s.UseCustomAccent)
                    return BlendColor(AccentGradientStart, AccentGradientEnd, 0.5);
                return GetActiveAccentPreset().Mid;
            }
        }

        public static Color AccentPrimary
        {
            get
            {
                var s = SettingsService.Current;
                if (s.UseCustomAccent)
                    return BlendColor(AccentGradientStart, AccentGradientEnd, 0.65);
                return GetActiveAccentPreset().Primary;
            }
        }

        public static Brush AccentPrimaryBrush => GetFrozenBrush(AccentPrimary);

        public static Color AccentHover =>
            BlendColor(AccentPrimary, IsLight ? Colors.Black : Colors.White, 0.14);

        public static Brush AccentHoverBrush => GetFrozenBrush(AccentHover);

        public static Brush CreateAccentSolidBrush(double opacity = 1.0)
        {
            var c = AccentPrimary;
            byte a = (byte)Math.Clamp(Math.Round(c.A * Math.Clamp(opacity, 0.0, 1.0)), 0, 255);
            return GetFrozenBrush(Color.FromArgb(a, c.R, c.G, c.B));
        }

        /// <summary>Verlaufs-Brush für gezielte Akzentflächen (Fortschrittsbalken, Editor-Vorschau, Lyrics-Fallback).</summary>
        public static LinearGradientBrush CreateAccentGradientBrush(double opacity = 1.0, bool horizontal = false)
        {
            var brush = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(AccentGradientStart, 0.0),
                    new(AccentGradientMid, 0.55),
                    new(AccentGradientEnd, 1.0)
                },
                new Point(0, 0),
                horizontal ? new Point(1, 0) : new Point(1, 1))
            {
                Opacity = opacity
            };
            brush.Freeze();
            return brush;
        }

        /// <summary>Ruhige, leicht getönte Kartenfläche ohne harten Verlauf.</summary>
        public static Brush CreateSoftAccentCardBrush()
        {
            var baseColor = SettingsService.Current.Theme switch
            {
                NotchTheme.DarkGlass => Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF),
                NotchTheme.Light => Color.FromRgb(0xFF, 0xFF, 0xFF),
                NotchTheme.LightGlass => Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF),
                _ => Color.FromRgb(0x11, 0x11, 0x14)
            };

            if (IsLight)
                return GetFrozenBrush(BlendColor(baseColor, AccentPrimary, 0.06));

            var darkTinted = BlendColor(Color.FromRgb(0x11, 0x11, 0x15), AccentPrimary, 0.11);
            if (IsGlass)
                darkTinted = Color.FromArgb(0xC4, darkTinted.R, darkTinted.G, darkTinted.B);

            return GetFrozenBrush(darkTinted);
        }

        public static Brush CreateSoftAccentTileBrush(byte alpha = 0x26)
            => GetFrozenBrush(Color.FromArgb(alpha, AccentPrimary.R, AccentPrimary.G, AccentPrimary.B));

        /// <summary>Ermittelt die dominante Hauptfarbe eines Bildes (z. B. Albumcover) für dezente Mikro-Gradienten.</summary>
        public static Color? ExtractDominantColor(ImageSource? source)
        {
            if (source is not BitmapSource bmp || bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0)
                return null;

            try
            {
                const int sampleSize = 24;
                BitmapSource scaled = (bmp.PixelWidth > sampleSize || bmp.PixelHeight > sampleSize)
                    ? new TransformedBitmap(bmp, new ScaleTransform(
                        (double)sampleSize / bmp.PixelWidth,
                        (double)sampleSize / bmp.PixelHeight))
                    : bmp;

                var bgra = scaled.Format == PixelFormats.Bgra32
                    ? scaled
                    : new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);

                int w = bgra.PixelWidth;
                int h = bgra.PixelHeight;
                int stride = w * 4;
                byte[] pixels = new byte[stride * h];
                bgra.CopyPixels(pixels, stride, 0);

                const int bucketCount = 16 * 16 * 16;
                var weights = new double[bucketCount];
                var sumR = new double[bucketCount];
                var sumG = new double[bucketCount];
                var sumB = new double[bucketCount];

                double fallbackW = 0, fallbackR = 0, fallbackG = 0, fallbackB = 0;

                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte b = pixels[i];
                    byte g = pixels[i + 1];
                    byte r = pixels[i + 2];
                    byte a = pixels[i + 3];
                    if (a < 128) continue;

                    fallbackW += 1;
                    fallbackR += r;
                    fallbackG += g;
                    fallbackB += b;

                    byte max = Math.Max(r, Math.Max(g, b));
                    byte min = Math.Min(r, Math.Min(g, b));
                    if (max < 24 || min > 235) continue;

                    double sat = (max - min) / (double)max;
                    double luma = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
                    double midBoost = 1.0 - Math.Abs(luma - 0.52) * 0.7;
                    double weight = (0.35 + sat * 4.0) * Math.Max(0.25, midBoost);

                    int idx = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                    weights[idx] += weight;
                    sumR[idx] += r * weight;
                    sumG[idx] += g * weight;
                    sumB[idx] += b * weight;
                }

                int bestIdx = -1;
                double bestW = 0;
                for (int i = 0; i < bucketCount; i++)
                {
                    if (weights[i] > bestW)
                    {
                        bestW = weights[i];
                        bestIdx = i;
                    }
                }

                if (bestIdx >= 0 && bestW > 0)
                {
                    return Color.FromRgb(
                        (byte)Math.Clamp(Math.Round(sumR[bestIdx] / bestW), 0, 255),
                        (byte)Math.Clamp(Math.Round(sumG[bestIdx] / bestW), 0, 255),
                        (byte)Math.Clamp(Math.Round(sumB[bestIdx] / bestW), 0, 255));
                }

                if (fallbackW > 0)
                {
                    return Color.FromRgb(
                        (byte)Math.Clamp(Math.Round(fallbackR / fallbackW), 0, 255),
                        (byte)Math.Clamp(Math.Round(fallbackG / fallbackW), 0, 255),
                        (byte)Math.Clamp(Math.Round(fallbackB / fallbackW), 0, 255));
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }

            return null;
        }

        public static Color BlendColor(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return Color.FromArgb(
                (byte)(a.A + (b.A - a.A) * t),
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        public static bool TryParseHexColor(string? hex, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            string s = hex.Trim();
            if (s.StartsWith('#')) s = s[1..];
            if (s.Length != 6) return false;
            if (!byte.TryParse(s[0..2], System.Globalization.NumberStyles.HexNumber, null, out byte r)) return false;
            if (!byte.TryParse(s[2..4], System.Globalization.NumberStyles.HexNumber, null, out byte g)) return false;
            if (!byte.TryParse(s[4..6], System.Globalization.NumberStyles.HexNumber, null, out byte b)) return false;
            color = Color.FromRgb(r, g, b);
            return true;
        }

        public static string NormalizeHex(string? hex, string fallback)
            => TryParseHexColor(hex, out var c) ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : fallback;

        /// <summary>Erzeugt ein präzises Vektor-Icon im Notchify-/Apple-Stil (einheitliche 24×24-Geometrie).</summary>
        public static FrameworkElement CreateVectorIcon(
            NotchIconKind kind,
            double size = 15,
            Brush? foreground = null,
            double strokeThickness = 1.85)
        {
            var brush = foreground ?? TextPrimaryBrush;
            var (data, filled) = GetIconGeometry(kind);

            var path = new Path
            {
                Data = Geometry.Parse(data),
                Stretch = Stretch.Uniform,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };

            if (filled)
            {
                path.Fill = brush;
            }
            else
            {
                path.Stroke = brush;
                path.StrokeThickness = strokeThickness;
            }

            return new Viewbox
            {
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Grid
                {
                    Width = 24,
                    Height = 24,
                    Children = { path }
                }
            };
        }

        /// <summary>Erzeugt 4 kompakte vertikale Audio-Balken für die Live-Medien-Notch.</summary>
        public static StackPanel CreateAudioBars(Color accent, double maxHeight = 12)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Height = maxHeight
            };
            double[] minScales = { 0.30, 0.45, 0.25, 0.40 };
            double[] maxScales = { 0.85, 1.00, 0.90, 0.78 };
            int[] durationsMs = { 380, 460, 340, 510 };
            int[] delaysMs = { 0, 110, 60, 170 };
            var brush = new SolidColorBrush(accent);
            brush.Freeze();

            for (int i = 0; i < minScales.Length; i++)
            {
                var scale = new ScaleTransform(1.0, minScales[i]);
                var bar = new Border
                {
                    Width = 2.2,
                    Height = maxHeight,
                    CornerRadius = new CornerRadius(1.1),
                    Background = brush,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    RenderTransformOrigin = new Point(0.5, 1.0),
                    RenderTransform = scale,
                    Margin = new Thickness(i == 0 ? 0 : 1.8, 0, 0, 0)
                };

                var anim = new System.Windows.Media.Animation.DoubleAnimation(minScales[i], maxScales[i],
                    TimeSpan.FromMilliseconds(durationsMs[i]))
                {
                    AutoReverse = true,
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(delaysMs[i]),
                    EasingFunction = new System.Windows.Media.Animation.SineEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
                    }
                };
                System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(anim, 30);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);

                stack.Children.Add(bar);
            }
            return stack;
        }

        private static (string Data, bool Filled) GetIconGeometry(NotchIconKind kind) => kind switch
        {
            // Notchify Icon #1: 2x2 Bento mit leicht gedrehtem Quadrat oben rechts
            NotchIconKind.Dashboard => ("M 4,4 L 10,4 L 10,10 L 4,10 Z M 4,14 L 10,14 L 10,20 L 4,20 Z M 14,14 L 20,14 L 20,20 L 14,20 Z M 17,2.5 L 21.5,7 L 17,11.5 L 12.5,7 Z", false),
            // Notchify Icon #2: File Tray / Ablage
            NotchIconKind.FileTray => ("M 4,8 L 6,4 L 18,4 L 20,8 L 20,18 A 2,2 0 0 1 18,20 L 6,20 A 2,2 0 0 1 4,18 Z M 4,11 L 9,11 L 10.5,13.5 L 13.5,13.5 L 15,11 L 20,11", false),
            // Notchify Icon #3: Zwischenablage / Dokument
            NotchIconKind.Clipboard => ("M 6,4 A 2,2 0 0 1 8,2 L 14,2 L 19,7 L 19,20 A 2,2 0 0 1 17,22 L 8,22 A 2,2 0 0 1 6,20 Z M 14,2 L 14,7 L 19,7 M 9.5,12 L 15.5,12 M 9.5,16 L 15.5,16", false),
            // Notchify Icon #4: Glocke (gefüllt wie in Notchify)
            NotchIconKind.Bell => ("M 18,8.5 A 6,6 0 0 0 6,8.5 C 6,14.5 3.5,16.5 3.5,16.5 L 20.5,16.5 C 20.5,16.5 18,14.5 18,8.5 Z M 10,19 A 2,2 0 0 0 14,19 Z", true),
            // Notchify Icon #5: Bildschirmaufnahme (Kreis im Kreis)
            NotchIconKind.Record => ("M 12,21 A 9,9 0 1 1 12,3 A 9,9 0 1 1 12,21 Z M 12,15.5 A 3.5,3.5 0 1 1 12,8.5 A 3.5,3.5 0 1 1 12,15.5 Z", false),
            // Notchify Icon #6: Bluetooth
            NotchIconKind.Bluetooth => ("M 7,7 L 17,17 L 12,21 L 12,3 L 17,7 L 7,17", false),
            // Notchify Icon #7: Discord / Controller-Chat
            NotchIconKind.Discord => ("M 6.5,6 C 9,4.8 15,4.8 17.5,6 L 20,16 C 18,18 15.5,18.5 14,17.5 L 13,16 M 6.5,6 L 4,16 C 6,18 8.5,18.5 10,17.5 L 11,16 M 9.5,11.5 A 1,1 0 1 1 9.5,11.4 Z M 14.5,11.5 A 1,1 0 1 1 14.5,11.4 Z", false),
            // Notchify Icon #8: Smartphone (ausgewogen breit)
            NotchIconKind.Phone => ("M 5.5,4.2 A 2.2,2.2 0 0 1 7.7,2 L 16.3,2 A 2.2,2.2 0 0 1 18.5,4.2 L 18.5,19.8 A 2.2,2.2 0 0 1 16.3,22 L 7.7,22 A 2.2,2.2 0 0 1 5.5,19.8 Z M 10.5,4.5 L 13.5,4.5 M 10.5,18.8 L 13.5,18.8", false),
            // Notchify Icon #9: Downloads
            NotchIconKind.Download => ("M 12,4 L 12,15 M 7.5,10.5 L 12,15 L 16.5,10.5 M 4,17 L 4,19 A 2,2 0 0 0 6,21 L 18,21 A 2,2 0 0 0 20,19 L 20,17", false),
            // Notchify Icon #10: Song erkennen / Waveform
            NotchIconKind.Waveform => ("M 4,10 L 4,14 M 8,7 L 8,17 M 12,3.5 L 12,20.5 M 16,7 L 16,17 M 20,10 L 20,14", false),
            // Notchify Icon #11: Bildschirm-OCR / Scan
            NotchIconKind.Scan => ("M 4,8 L 4,5.5 A 1.5,1.5 0 0 1 5.5,4 L 8,4 M 16,4 L 18.5,4 A 1.5,1.5 0 0 1 20,5.5 L 20,8 M 20,16 L 20,18.5 A 1.5,1.5 0 0 1 18.5,20 L 16,20 M 8,20 L 5.5,20 A 1.5,1.5 0 0 1 4,18.5 L 4,16 M 12,15 A 3,3 0 1 1 12,9 A 3,3 0 1 1 12,15 Z M 14.5,14.5 L 16.5,16.5", false),
            // Notchify Rechts: Zahnrad
            NotchIconKind.SettingsGear => ("M 12,15 A 3,3 0 1 1 12,9 A 3,3 0 1 1 12,15 Z M 19.4,15 A 1.65,1.65 0 0 0 19.73,16.82 L 19.79,16.88 A 2,2 0 1 1 16.96,19.71 L 16.9,19.65 A 1.65,1.65 0 0 0 15.08,19.32 A 1.65,1.65 0 0 0 14,20.83 L 14,21 A 2,2 0 1 1 10,21 L 10,20.91 A 1.65,1.65 0 0 0 8.92,19.4 A 1.65,1.65 0 0 0 7.1,19.73 L 7.04,19.79 A 2,2 0 1 1 4.21,16.96 L 4.27,16.9 A 1.65,1.65 0 0 0 4.6,15.08 A 1.65,1.65 0 0 0 3.09,14 L 3,14 A 2,2 0 1 1 3,10 L 3.09,10 A 1.65,1.65 0 0 0 4.6,8.92 A 1.65,1.65 0 0 0 4.27,7.1 L 4.21,7.04 A 2,2 0 1 1 7.04,4.21 L 7.1,4.27 A 1.65,1.65 0 0 0 8.92,4.6 L 9,4.6 A 1.65,1.65 0 0 0 10,3.09 L 10,3 A 2,2 0 1 1 14,3 L 14,3.09 A 1.65,1.65 0 0 0 15.08,4.6 A 1.65,1.65 0 0 0 16.9,4.27 L 16.96,4.21 A 2,2 0 1 1 19.79,7.04 L 19.73,7.1 A 1.65,1.65 0 0 0 19.4,8.92 L 19.4,9 A 1.65,1.65 0 0 0 20.91,10 L 21,10 A 2,2 0 1 1 21,14 L 20.91,14 A 1.65,1.65 0 0 0 19.4,15 Z", false),

            NotchIconKind.Play => ("M 8,5.5 C 8,4.7 8.9,4.2 9.6,4.6 L 19.2,10.9 C 19.9,11.4 19.9,12.6 19.2,13.1 L 9.6,19.4 C 8.9,19.8 8,19.3 8,18.5 Z", true),
            NotchIconKind.Pause => ("M 8,5.5 L 8,18.5 M 16,5.5 L 16,18.5", false),
            NotchIconKind.Previous => ("M 6,6 L 6,18 M 18,6.5 L 10.5,12 L 18,17.5", false),
            NotchIconKind.Next => ("M 18,6 L 18,18 M 6,6.5 L 13.5,12 L 6,17.5", false),
            NotchIconKind.Music => ("M 9,17.5 A 2.5,2.5 0 1 1 4,17.5 A 2.5,2.5 0 1 1 9,17.5 Z M 20,15.5 A 2.5,2.5 0 1 1 15,15.5 A 2.5,2.5 0 1 1 20,15.5 Z M 9,17.5 L 9,6 L 20,4 L 20,15.5", false),
            NotchIconKind.Lock => ("M 7,11 L 17,11 A 2,2 0 0 1 19,13 L 19,19 A 2,2 0 0 1 17,21 L 7,21 A 2,2 0 0 1 5,19 L 5,13 A 2,2 0 0 1 7,11 Z M 8.5,11 L 8.5,7.5 A 3.5,3.5 0 0 1 15.5,7.5 L 15.5,11", false),
            NotchIconKind.Pulse => ("M 3,12 L 7,12 L 10,5 L 14,19 L 17,12 L 21,12", false),
            NotchIconKind.Settings => ("M 12,15 A 3,3 0 1 1 12,9 A 3,3 0 1 1 12,15 Z M 4,7 L 20,7 M 4,17 L 20,17", false),
            NotchIconKind.Folder => ("M 3,7 A 2,2 0 0 1 5,5 L 9.5,5 L 11.5,7.5 L 19,7.5 A 2,2 0 0 1 21,9.5 L 21,17 A 2,2 0 0 1 19,19 L 5,19 A 2,2 0 0 1 3,17 Z", false),
            NotchIconKind.VolumeOn => ("M 11,5 L 6,9 L 2.5,9 L 2.5,15 L 6,15 L 11,19 Z M 15.2,8.8 A 4.8,4.8 0 0 1 15.2,15.2 M 18.2,5.8 A 9,9 0 0 1 18.2,18.2", false),
            NotchIconKind.VolumeOff => ("M 11,5 L 6,9 L 2.5,9 L 2.5,15 L 6,15 L 11,19 Z M 21.5,9.5 L 16,15 M 16,9.5 L 21.5,15", false),
            NotchIconKind.Clock => ("M 12,21 A 9,9 0 1 1 12,3 A 9,9 0 1 1 12,21 Z M 12,7 L 12,12 L 15.5,14", false),
            NotchIconKind.Timer => ("M 10,2.5 L 14,2.5 M 12,13.5 L 15,10.5 M 12,21 A 7.5,7.5 0 1 1 12,6 A 7.5,7.5 0 1 1 12,21 Z", false),
            NotchIconKind.Reset => ("M 4.5,12 A 7.5,7.5 0 1 0 6.7,6.7 L 4.5,8.8 M 4.5,4.5 L 4.5,8.8 L 8.8,8.8", false),
            NotchIconKind.Plus => ("M 12,6 L 12,18 M 6,12 L 18,12", false),
            NotchIconKind.Minus => ("M 5.5,12 L 18.5,12", false),
            NotchIconKind.Check => ("M 5,12.5 L 9.5,17 L 19,7.5", false),
            NotchIconKind.Close => ("M 6,6 L 18,18 M 18,6 L 6,18", false),
            NotchIconKind.Bolt => ("M 13,2.5 L 4.5,13.5 L 11.5,13.5 L 10.5,21.5 L 19.5,10.5 L 12.5,10.5 Z", false),
            NotchIconKind.Sparkle => ("M 12,3 L 13.8,9.2 L 20,11 L 13.8,12.8 L 12,19 L 10.2,12.8 L 4,11 L 10.2,9.2 Z", false),
            NotchIconKind.Monitor => ("M 3,5 A 2,2 0 0 1 5,3 L 19,3 A 2,2 0 0 1 21,5 L 21,15 A 2,2 0 0 1 19,17 L 5,17 A 2,2 0 0 1 3,15 Z M 8,21 L 16,21 M 12,17 L 12,21", false),
            NotchIconKind.Layers => ("M 12,3 L 21,8 L 12,13 L 3,8 Z M 3,12.5 L 12,17.5 L 21,12.5 M 3,17 L 12,22 L 21,17", false),
            NotchIconKind.Shield => ("M 12,2.5 L 19.5,5.5 L 19.5,11.5 C 19.5,16.5 16.2,20.2 12,21.5 C 7.8,20.2 4.5,16.5 4.5,11.5 L 4.5,5.5 Z", false),
            NotchIconKind.Trash => ("M 4,6 L 20,6 M 9,6 L 9,3.5 A 1,1 0 0 1 10,2.5 L 14,2.5 A 1,1 0 0 1 15,3.5 L 15,6 M 6,6 L 7.2,19.5 A 1.5,1.5 0 0 0 8.7,21 L 15.3,21 A 1.5,1.5 0 0 0 16.8,19.5 L 18,6 M 10,10.5 L 10,16.5 M 14,10.5 L 14,16.5", false),
            NotchIconKind.ChevronLeft => ("M 15,6 L 9,12 L 15,18", false),
            NotchIconKind.ChevronRight => ("M 9,6 L 15,12 L 9,18", false),
            NotchIconKind.Calendar => ("M 4,6 A 2,2 0 0 1 6,4 L 18,4 A 2,2 0 0 1 20,6 L 20,19 A 2,2 0 0 1 18,21 L 6,21 A 2,2 0 0 1 4,19 Z M 8,2.5 L 8,5.5 M 16,2.5 L 16,5.5 M 4,9.5 L 20,9.5", false),
            NotchIconKind.Sun => ("M 12,16 A 4,4 0 1 1 12,8 A 4,4 0 1 1 12,16 Z M 12,2 L 12,4 M 12,20 L 12,22 M 2,12 L 4,12 M 20,12 L 22,12 M 4.9,4.9 L 6.3,6.3 M 17.7,17.7 L 19.1,19.1 M 19.1,4.9 L 17.7,6.3 M 6.3,17.7 L 4.9,19.1", false),
            NotchIconKind.Cloud => ("M 6.5,19 L 17.5,19 A 4,4 0 0 0 18.2,11.1 A 5.5,5.5 0 0 0 7.6,9.7 A 4.5,4.5 0 0 0 6.5,19 Z", false),
            NotchIconKind.CloudSun => ("M 12,5 L 12,3 M 16.5,6.8 L 17.9,5.4 M 18.5,11 L 20.5,11 M 7.5,6.8 L 6.1,5.4 M 9,10.5 A 3,3 0 0 1 14.5,9.2 M 6,19 L 16.5,19 A 3.5,3.5 0 0 0 17,12.1 A 4.5,4.5 0 0 0 8.5,11.2 A 3.8,3.8 0 0 0 6,19 Z", false),
            NotchIconKind.CloudRain => ("M 6.5,15 L 17.5,15 A 3.8,3.8 0 0 0 18,7.5 A 5,5 0 0 0 8.3,6.3 A 4.2,4.2 0 0 0 6.5,15 Z M 8.5,18 L 7.5,21 M 12.5,18 L 11.5,21 M 16.5,18 L 15.5,21", false),
            NotchIconKind.CloudSnow => ("M 6.5,15 L 17.5,15 A 3.8,3.8 0 0 0 18,7.5 A 5,5 0 0 0 8.3,6.3 A 4.2,4.2 0 0 0 6.5,15 Z M 8.5,18.5 L 8.5,19.5 M 12,18 L 12,19 M 15.5,18.5 L 15.5,19.5 M 10,20.5 L 10,21.5 M 14,20.5 L 14,21.5", false),
            NotchIconKind.CloudLightning => ("M 6.5,15 L 17.5,15 A 3.8,3.8 0 0 0 18,7.5 A 5,5 0 0 0 8.3,6.3 A 4.2,4.2 0 0 0 6.5,15 Z M 13,14 L 10,18.5 L 13,18.5 L 11,22", false),
            NotchIconKind.CloudFog => ("M 6.5,14 L 17.5,14 A 3.5,3.5 0 0 0 18,7.1 A 4.8,4.8 0 0 0 8.5,6 A 4,4 0 0 0 6.5,14 Z M 5,17.5 L 19,17.5 M 7,20.5 L 17,20.5", false),
            NotchIconKind.CheckSquare => ("M 4,6 A 2,2 0 0 1 6,4 L 18,4 A 2,2 0 0 1 20,6 L 20,18 A 2,2 0 0 1 18,20 L 6,20 A 2,2 0 0 1 4,18 Z M 8.5,12 L 11,14.5 L 15.5,9.5", false),
            NotchIconKind.Note => ("M 5,4 A 2,2 0 0 1 7,2 L 17,2 A 2,2 0 0 1 19,4 L 19,20 A 2,2 0 0 1 17,22 L 7,22 A 2,2 0 0 1 5,20 Z M 8.5,8 L 15.5,8 M 8.5,12 L 15.5,12 M 8.5,16 L 13,16", false),
            NotchIconKind.Cpu => ("M 7,7 L 17,7 L 17,17 L 7,17 Z M 10,10 L 14,10 L 14,14 L 10,14 Z M 9,3 L 9,7 M 15,3 L 15,7 M 9,17 L 9,21 M 15,17 L 15,21 M 3,9 L 7,9 M 3,15 L 7,15 M 17,9 L 21,9 M 17,15 L 21,15", false),
            NotchIconKind.Memory => ("M 3,6 A 1,1 0 0 1 4,5 L 20,5 A 1,1 0 0 1 21,6 L 21,15 L 3,15 Z M 7,15 L 7,19 M 12,15 L 12,19 M 17,15 L 17,19 M 7,9 L 7,11 M 12,9 L 12,11 M 17,9 L 17,11", false),
            NotchIconKind.Gpu => ("M 3,6 L 21,6 A 1,1 0 0 1 22,7 L 22,16 A 1,1 0 0 1 21,17 L 3,17 A 1,1 0 0 1 2,16 L 2,7 A 1,1 0 0 1 3,6 Z M 12,14 A 2.5,2.5 0 1 1 12,9 A 2.5,2.5 0 1 1 12,14 Z M 6,20 L 18,20", false),
            NotchIconKind.Pin => ("M 7.5,4 A 1,1 0 0 1 8.5,3 L 15.5,3 A 1,1 0 0 1 16.5,4 L 16.5,5.5 A 1.5,1.5 0 0 1 15,7 L 15,11 L 17.4,14.2 A 1.2,1.2 0 0 1 16.4,16.2 L 7.6,16.2 A 1.2,1.2 0 0 1 6.6,14.2 L 9,11 L 9,7 A 1.5,1.5 0 0 1 7.5,5.5 Z M 12,16.2 L 12,21.5", false),
            NotchIconKind.Search => ("M 15.5,15.5 L 20,20 M 17,10.5 A 6.5,6.5 0 1 1 4,10.5 A 6.5,6.5 0 1 1 17,10.5 Z", false),
            NotchIconKind.Copy => ("M 9,9 L 19,9 A 2,2 0 0 1 21,11 L 21,19 A 2,2 0 0 1 19,21 L 9,21 A 2,2 0 0 1 7,19 L 7,11 A 2,2 0 0 1 9,9 Z M 15,9 L 15,5 A 2,2 0 0 0 13,3 L 5,3 A 2,2 0 0 0 3,5 L 3,15 A 2,2 0 0 0 5,17 L 7,17", false),
            NotchIconKind.Link => ("M 10,14 L 14,10 M 7.5,12.5 L 5.5,14.5 A 3.5,3.5 0 0 0 10.5,19.5 L 12.5,17.5 M 16.5,11.5 L 18.5,9.5 A 3.5,3.5 0 0 0 13.5,4.5 L 11.5,6.5", false),
            NotchIconKind.Image => ("M 4,6 A 2,2 0 0 1 6,4 L 18,4 A 2,2 0 0 1 20,6 L 20,18 A 2,2 0 0 1 18,20 L 6,20 A 2,2 0 0 1 4,18 Z M 4,16 L 9,11 L 13,15 L 16,12 L 20,16 M 9,8.5 A 1,1 0 1 1 8.99,8.5 Z", false),
            NotchIconKind.File => ("M 6,3 L 14,3 L 19,8 L 19,19 A 2,2 0 0 1 17,21 L 6,21 A 2,2 0 0 1 4,19 L 4,5 A 2,2 0 0 1 6,3 Z M 14,3 L 14,8 L 19,8", false),
            NotchIconKind.ExternalLink => ("M 14,4 L 20,4 L 20,10 M 11,13 L 20,4 M 18,14 L 18,19 A 2,2 0 0 1 16,21 L 5,21 A 2,2 0 0 1 3,19 L 3,8 A 2,2 0 0 1 5,6 L 10,6", false),
            NotchIconKind.QrCode => ("M 4,4 L 10,4 L 10,10 L 4,10 Z M 6.5,6.5 L 7.5,6.5 L 7.5,7.5 L 6.5,7.5 Z M 14,4 L 20,4 L 20,10 L 14,10 Z M 16.5,6.5 L 17.5,6.5 L 17.5,7.5 L 16.5,7.5 Z M 4,14 L 10,14 L 10,20 L 4,20 Z M 6.5,16.5 L 7.5,16.5 L 7.5,17.5 L 6.5,17.5 Z M 14,14 L 16.5,14 L 16.5,16.5 L 14,16.5 Z M 18,14 L 20,14 L 20,17 M 14,19 L 17,19 L 17,20 M 19,19 L 20,19 L 20,20", false),
            NotchIconKind.Translate => ("M 4,6 L 13,6 M 8.5,3.5 L 8.5,6 M 11.5,6 C 11,10 8.5,13 5,15 M 6.5,9.5 C 8,11.5 10,13 12,14.5 M 13,20 L 16.5,11 L 20,20 M 14.3,17 L 18.7,17", false),
            NotchIconKind.Save => ("M 5,3 L 16,3 L 20,7 L 20,19 A 2,2 0 0 1 18,21 L 6,21 A 2,2 0 0 1 4,19 L 4,5 A 2,2 0 0 1 5,3 Z M 8,3 L 8,8 L 15,8 L 15,3 M 7.5,21 L 7.5,14 L 16.5,14 L 16.5,21", false),
            NotchIconKind.Pet => ("M 5,10 L 6,4 L 10,7.5 L 14,7.5 L 18,4 L 19,10 A 7,6.5 0 0 1 12,19 A 7,6.5 0 0 1 5,10 Z M 9.5,12.2 A 0.7,0.7 0 1 1 9.5,12.1 Z M 14.5,12.2 A 0.7,0.7 0 1 1 14.5,12.1 Z", false),
            NotchIconKind.Headphones => ("M 4,14 L 4,12 A 8,8 0 0 1 20,12 L 20,14 M 4,14 A 2,2 0 0 1 6,12 L 7,12 A 1.5,1.5 0 0 1 8.5,13.5 L 8.5,18.5 A 1.5,1.5 0 0 1 7,20 L 6,20 A 2,2 0 0 1 4,18 Z M 20,14 A 2,2 0 0 0 18,12 L 17,12 A 1.5,1.5 0 0 0 15.5,13.5 L 15.5,18.5 A 1.5,1.5 0 0 0 17,20 L 18,20 A 2,2 0 0 0 20,18 Z", false),
            NotchIconKind.Moon => ("M 21,12.79 A 9,9 0 1 1 11.21,3 A 7,7 0 0 0 21,12.79 Z", false),
            NotchIconKind.Wifi => ("M 5,12.55 A 11,11 0 0 1 19,12.55 M 8.53,16.11 A 6,6 0 0 1 15.47,16.11 M 12,20 L 12.01,20", false),
            NotchIconKind.Airplane => ("M 21,16 L 21,14 L 13,9 L 13,3.5 A 1.5,1.5 0 0 0 10,3.5 L 10,9 L 2,14 L 2,16 L 10,13.5 L 10,19 L 8,20.5 L 8,22 L 11.5,21 L 15,22 L 15,20.5 L 13,19 L 13,13.5 Z", true),
            NotchIconKind.Hotspot => ("M 12,12 L 12.01,12 M 8.46,15.54 A 5,5 0 0 1 8.46,8.46 M 15.54,8.46 A 5,5 0 0 1 15.54,15.54 M 5.64,18.36 A 9,9 0 0 1 5.64,5.64 M 18.36,5.64 A 9,9 0 0 1 18.36,18.36", false),
            NotchIconKind.Battery => ("M 3,7 L 18,7 A 2,2 0 0 1 20,9 L 20,15 A 2,2 0 0 1 18,17 L 3,17 A 2,2 0 0 1 1,15 L 1,9 A 2,2 0 0 1 3,7 Z M 22,10.5 L 22,13.5", false),
            NotchIconKind.Microphone => ("M 12,2 A 3,3 0 0 1 15,5 L 15,11 A 3,3 0 0 1 9,11 L 9,5 A 3,3 0 0 1 12,2 Z M 19,10 L 19,11 A 7,7 0 0 1 5,11 L 5,10 M 12,18 L 12,22 M 8,22 L 16,22", false),
            NotchIconKind.TrendingUp => ("M 3,17 L 9,11 L 13,15 L 21,7 M 15,7 L 21,7 L 21,13", false),
            NotchIconKind.Star => ("M 12,2.5 L 14.9,8.4 L 21.4,9.3 L 16.7,13.9 L 17.8,20.4 L 12,17.3 L 6.2,20.4 L 7.3,13.9 L 2.6,9.3 L 9.1,8.4 Z", false),
            _ => ("M 12,21 A 9,9 0 1 1 12,3 A 9,9 0 1 1 12,21 Z", false)
        };

        public static Border CreateCard(double cornerRadius = RadiusCard, Thickness? padding = null) => new()
        {
            Background = CardBackgroundBrush,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(cornerRadius),
            Padding = padding ?? new Thickness(12, 10, 12, 10)
        };

        /// <summary>Einheitlicher runder Icon-Only-Action-Button für Modul-Header (28×28, ohne Text-Label).</summary>
        public static Button CreateIconActionButton(
            NotchIconKind kind,
            string tooltip,
            Action onClick,
            double size = 28,
            double iconSize = 13,
            bool isActive = false)
        {
            var btn = CreateRoundedButton(
                CreateVectorIcon(kind, iconSize, AccentPrimaryBrush, 1.85),
                onClick,
                background: isActive ? CreateSoftAccentTileBrush(0x36) : CreateSoftAccentTileBrush(0x22),
                hoverBackground: CreateSoftAccentTileBrush(0x3C),
                cornerRadius: size / 2.0,
                padding: new Thickness(0),
                tooltip: tooltip);
            btn.Width = size;
            btn.Height = size;
            return btn;
        }

        /// <summary>Einheitlicher Modul-Header (Icon + 15.5 px Bold Titel links, Action-Buttons rechts).</summary>
        public static Grid CreateModuleHeader(
            NotchIconKind iconKind,
            string title,
            Thickness margin,
            double iconBoxSize = 32,
            double iconSize = 20,
            double strokeThickness = 2.15,
            params UIElement[] rightActions)
        {
            var header = new Grid { Margin = margin };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            var headerBadge = new Border
            {
                Width = iconBoxSize,
                Height = iconBoxSize,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = CreateVectorIcon(iconKind, iconSize, AccentPrimaryBrush, strokeThickness)
            };
            titleRow.Children.Add(headerBadge);
            titleRow.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = TextPrimaryBrush,
                FontFamily = UiDisplayFont,
                FontSize = 15.5,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(titleRow, 0);
            header.Children.Add(titleRow);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            for (int i = 0; i < rightActions.Length; i++)
                actions.Children.Add(rightActions[i]);
            Grid.SetColumn(actions, 1);
            header.Children.Add(actions);

            return header;
        }

        public static Button CreateRoundedButton(
            object content,
            Action onClick,
            Brush? background = null,
            Brush? hoverBackground = null,
            Brush? foreground = null,
            double cornerRadius = RadiusControl,
            Thickness? padding = null,
            string? tooltip = null,
            Brush? borderBrush = null)
        {
            var bg = background ?? ControlBackgroundBrush;
            var hover = hoverBackground ?? ControlHoverBrush;
            var pressed = ControlPressedBrush;

            var template = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "bd";
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
            bd.SetValue(Border.PaddingProperty, padding ?? new Thickness(10, 5, 10, 5));
            if (borderBrush != null)
            {
                bd.SetValue(Border.BorderBrushProperty, borderBrush);
                bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            }

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hover, "bd"));

            var pressTrigger = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
            pressTrigger.Setters.Add(new Setter(Border.BackgroundProperty, pressed, "bd"));

            template.VisualTree = bd;
            template.Triggers.Add(hoverTrigger);
            template.Triggers.Add(pressTrigger);

            var scale = new ScaleTransform(1.0, 1.0);
            var btn = new Button
            {
                Content = content,
                Template = template,
                Background = bg,
                Focusable = false,
                Foreground = foreground ?? TextPrimaryBrush,
                FontFamily = UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale
            };

            AttachInteractiveScale(btn, scale);
            btn.Click += (_, e) =>
            {
                e.Handled = true;
                onClick();
            };
            return btn;
        }

        private static readonly ConditionalWeakTable<Button, ScaleTransform> InteractiveScaleTable = new();

        /// <summary>Verleiht jedem Button die globale Press-/Release-Haptik (Eindrücken + weiches Zurückfedern).</summary>
        public static void AttachInteractiveScale(
            Button btn,
            ScaleTransform? existingScale = null,
            double hoverScale = 1.05,
            double pressScale = 0.94)
        {
            if (InteractiveScaleTable.TryGetValue(btn, out _)) return;

            var scale = existingScale ?? btn.RenderTransform as ScaleTransform;
            if (scale == null)
            {
                scale = new ScaleTransform(1.0, 1.0);
                btn.RenderTransformOrigin = new Point(0.5, 0.5);
                btn.RenderTransform = scale;
            }
            else
            {
                btn.RenderTransformOrigin = new Point(0.5, 0.5);
            }

            InteractiveScaleTable.Add(btn, scale);

            btn.MouseEnter += (_, _) => AnimateScale(scale, hoverScale, ButtonHoverDurationMs);
            btn.PreviewMouseLeftButtonDown += (_, _) => AnimateScale(scale, pressScale, ButtonPressDurationMs);
            btn.PreviewMouseLeftButtonUp += (_, _) => AnimateScale(
                scale,
                btn.IsMouseOver ? hoverScale : 1.0,
                ButtonReleaseSpringMs,
                spring: true);
            btn.MouseLeave += (_, _) =>
            {
                if (Math.Abs(scale.ScaleX - 1.0) > 0.005)
                    AnimateScale(scale, 1.0, ButtonReleaseDurationMs);
            };
        }

        /// <summary>Führt schließende Aktionen nach kurzem Reaktionsmoment aus, damit Press-/Release-Haptik sichtbar ankommt.</summary>
        public static void RunAfterPressFeedback(Action action, int delayMs = 95)
        {
            if (!EffectiveAnimationsEnabled || delayMs <= 0)
            {
                action();
                return;
            }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                action();
            };
            timer.Start();
        }

        /// <summary>Kurzer taktiler Impuls (z. B. auf dem Albumcover bei Play/Pause, Skip oder Seek).</summary>
        public static void AnimatePressPulse(FrameworkElement? element, double pulseScale = 0.96)
        {
            if (element == null || !EffectiveAnimationsEnabled) return;

            if (element.RenderTransform is not ScaleTransform scale)
            {
                scale = new ScaleTransform(1.0, 1.0);
                element.RenderTransformOrigin = new Point(0.5, 0.5);
                element.RenderTransform = scale;
            }

            var downEase = CreateEaseOut();
            var upEase = CreateEaseOut();
            var animX = new DoubleAnimationUsingKeyFrames();
            animX.KeyFrames.Add(new EasingDoubleKeyFrame(pulseScale, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)), downEase));
            animX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(270)), upEase));

            var animY = animX.Clone();
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
        }

        private static void AnimateScale(ScaleTransform scale, double target, int durationMs, bool spring = false)
        {
            if (!EffectiveAnimationsEnabled)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = target;
                scale.ScaleY = target;
                return;
            }

            var dur = TimeSpan.FromMilliseconds(durationMs);
            var ease = spring ? CreateSpringEase(0.35) : CreateEaseOut();
            int fps = SettingsService.Current.TargetFrameRate;
            var ax = new DoubleAnimation(target, dur) { EasingFunction = ease };
            var ay = new DoubleAnimation(target, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(ax, fps);
            Timeline.SetDesiredFrameRate(ay, fps);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, ay);
        }

        /// <summary>Wendet eine schlanke, moderne Scrollbar ohne klassische Windows-Pfeile auf einen ScrollViewer an.</summary>
        public static void ApplySlimScrollBarStyle(ScrollViewer scrollViewer)
        {
            bool isLight = IsLight;
            var cached = isLight ? _slimScrollBarLightStyle : _slimScrollBarDarkStyle;
            if (cached == null)
            {
                string thumbHex = isLight ? "#45141418" : "#40FFFFFF";
                string thumbHoverHex = isLight ? "#75141418" : "#75FFFFFF";

                string xaml = $@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='ScrollBar'>
    <Setter Property='Width' Value='6'/>
    <Setter Property='MinWidth' Value='6'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Template'>
        <Setter.Value>
            <ControlTemplate TargetType='ScrollBar'>
                <Grid Background='Transparent'>
                    <Track x:Name='PART_Track' IsDirectionReversed='True'>
                        <Track.Thumb>
                            <Thumb>
                                <Thumb.Template>
                                    <ControlTemplate TargetType='Thumb'>
                                        <Border x:Name='thumbBd' CornerRadius='2.5' Background='{thumbHex}' Margin='1,2'/>
                                        <ControlTemplate.Triggers>
                                            <Trigger Property='IsMouseOver' Value='True'>
                                                <Setter TargetName='thumbBd' Property='Background' Value='{thumbHoverHex}'/>
                                            </Trigger>
                                        </ControlTemplate.Triggers>
                                    </ControlTemplate>
                                </Thumb.Template>
                            </Thumb>
                        </Track.Thumb>
                    </Track>
                </Grid>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>";

                if (System.Windows.Markup.XamlReader.Parse(xaml) is Style parsed)
                {
                    parsed.Seal();
                    cached = parsed;
                    if (isLight) _slimScrollBarLightStyle = cached;
                    else _slimScrollBarDarkStyle = cached;
                }
            }

            if (cached != null)
                scrollViewer.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = cached;
        }
    }
}
