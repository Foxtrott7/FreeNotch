using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WindowsNotch.Core
{
    /// <summary>
    /// Gemeinsame Animations-, Glättungs- und Reset-Hilfsfunktionen für die Jiggle-Controller
    /// (Top-Tabs, Bento-Dashboard und Control Center), ohne deren jeweilige Parameter zu verändern.
    /// </summary>
    internal static class JiggleMotionHelper
    {
        private static readonly double[] BentoTileAngles = { 0.36, -0.34, -0.36, 0.34 };
        private static readonly int[] BentoTileDurations = { 200, 214, 206, 194 };
        private static readonly int[] BentoTileDelays = { 0, 48, 28, 72 };

        public static double ComputeFollowAlpha(double followRate, double dtSeconds)
            => 1.0 - Math.Exp(-followRate * dtSeconds);

        public static void StartJiggle(
            RotateTransform jiggleRotate,
            TranslateTransform jiggleTranslate,
            double angle,
            int rotDurationMs,
            int rotDelayMs,
            double yOffset,
            int yDurationMs,
            int yDelayMs,
            int targetFps)
        {
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

            var rotAnim = new DoubleAnimation(-angle, angle, TimeSpan.FromMilliseconds(rotDurationMs))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(rotDelayMs),
                EasingFunction = ease
            };
            Timeline.SetDesiredFrameRate(rotAnim, targetFps);
            jiggleRotate.BeginAnimation(RotateTransform.AngleProperty, rotAnim);

            var yAnim = new DoubleAnimation(-yOffset, yOffset, TimeSpan.FromMilliseconds(yDurationMs))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(yDelayMs),
                EasingFunction = ease
            };
            Timeline.SetDesiredFrameRate(yAnim, targetFps);
            jiggleTranslate.BeginAnimation(TranslateTransform.YProperty, yAnim);
        }

        public static void StartCardTileJiggle(
            RotateTransform jiggleRotate,
            TranslateTransform jiggleTranslate,
            ScaleTransform? dragScale,
            int index,
            bool animationsEnabled,
            int targetFps,
            int extraDelayMs = 0,
            double? disabledScale = null)
        {
            if (!animationsEnabled)
            {
                if (dragScale != null && disabledScale.HasValue)
                {
                    dragScale.ScaleX = disabledScale.Value;
                    dragScale.ScaleY = disabledScale.Value;
                }
                return;
            }

            int delay = BentoTileDelays[index % BentoTileDelays.Length];
            StartJiggle(
                jiggleRotate,
                jiggleTranslate,
                angle: BentoTileAngles[index % BentoTileAngles.Length],
                rotDurationMs: BentoTileDurations[index % BentoTileDurations.Length],
                rotDelayMs: delay + extraDelayMs,
                yOffset: 0.20,
                yDurationMs: 225 + (index % 4) * 9,
                yDelayMs: delay / 2 + extraDelayMs,
                targetFps: targetFps);
        }

        public static void PauseJiggle(
            RotateTransform jiggleRotate,
            TranslateTransform jiggleTranslate,
            bool animationsEnabled,
            int targetFps,
            int resetDurationMs = 90)
        {
            if (animationsEnabled)
            {
                var ease = NotchDesign.CreateEaseOut();
                var dur = TimeSpan.FromMilliseconds(resetDurationMs);
                var rotReset = new DoubleAnimation(0, dur) { EasingFunction = ease };
                var yReset = new DoubleAnimation(0, dur) { EasingFunction = ease };
                Timeline.SetDesiredFrameRate(rotReset, targetFps);
                Timeline.SetDesiredFrameRate(yReset, targetFps);
                jiggleRotate.BeginAnimation(RotateTransform.AngleProperty, rotReset);
                jiggleTranslate.BeginAnimation(TranslateTransform.YProperty, yReset);
            }
            else
            {
                jiggleRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                jiggleRotate.Angle = 0;
                jiggleTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                jiggleTranslate.Y = 0;
            }
        }

        public static void StopJiggle(
            RotateTransform jiggleRotate,
            TranslateTransform? jiggleTranslate,
            ScaleTransform? dragScale,
            bool playCommitPulse,
            bool animationsEnabled,
            int targetFps,
            bool resetScaleIfNoPulse = true)
        {
            jiggleRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            jiggleRotate.Angle = 0;
            if (jiggleTranslate != null)
            {
                jiggleTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                jiggleTranslate.Y = 0;
            }

            if (dragScale == null) return;

            if (playCommitPulse && animationsEnabled)
            {
                NotchDesign.PlaySubtleCommitPulse(dragScale, targetFps);
            }
            else if (resetScaleIfNoPulse)
            {
                dragScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                dragScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                dragScale.ScaleX = 1.0;
                dragScale.ScaleY = 1.0;
            }
        }

        public static bool IsInsideVisualTree(DependencyObject? source, Func<DependencyObject, bool> isMatch)
        {
            while (source != null)
            {
                if (isMatch(source))
                    return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }
    }
}
