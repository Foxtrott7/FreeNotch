using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WindowsNotch.Core;

namespace WindowsNotch.UI
{
    public partial class NotchWindow
    {
        private bool IsBottomEdge => S.Position is NotchPosition.BottomLeft or NotchPosition.BottomCenter or NotchPosition.BottomRight;
        private bool IsLeftEdge => S.Position == NotchPosition.LeftCenter;
        private bool IsRightEdge => S.Position == NotchPosition.RightCenter;
        private bool IsSideEdge => IsLeftEdge || IsRightEdge;

        private (double x, double y) EarPadding
        {
            get
            {
                if (S.Style != NotchStyle.Docked) return (0, 0);
                return IsSideEdge ? (12.0, 24.0) : (24.0, 0.0);
            }
        }

        private (double w, double h) SizeFor(NotchState state)
        {
            var (earX, earY) = EarPadding;

            if (state == NotchState.Expanded)
            {
                if (_isOnboardingOpen)
                    return (OnboardingWidth + earX, OnboardingHeight + earY);

                if (_isSettingsOpen)
                    return (SettingsWidth + earX, SettingsHeight + earY);

                double tourExtraH = _isTourActive ? 68 : 0;
                double tourMinW = _isTourActive ? 470 : 0;

                if (_selectedModule?.PreferredExpandedSize is var (mw, mh))
                    return (Math.Max(tourMinW, mw) + earX, mh + tourExtraH + earY);

                return (Math.Max(460, S.ExpandedWidth) + earX, Math.Max(280, S.ExpandedHeight) + tourExtraH + earY);
            }

            if (state == NotchState.Preview)
            {
                var eventModule = GetEventPrimary();
                double previewW = S.CollapsedWidth + 40;

                if (PreviewHost.Content is NotchLayoutView layoutView)
                {
                    previewW = layoutView.Width + 56;
                }
                else if (eventModule?.PreferredPreviewWidth.HasValue == true && eventModule.PreferredPreviewWidth.Value > 0)
                {
                    previewW = eventModule.PreferredPreviewWidth.Value;
                }
                else if (PreviewHost.Content is FrameworkElement fe)
                {
                    fe.Measure(new Size(double.PositiveInfinity, S.CollapsedHeight + 20));
                    if (fe.DesiredSize.Width > 0)
                        previewW = fe.DesiredSize.Width + 40;
                }

                previewW = Math.Clamp(previewW, S.CollapsedWidth + 40, Math.Max(S.CollapsedWidth + 40, S.ExpandedWidth - 20));
                double previewH = Math.Max(S.CollapsedHeight + 12, 44);
                return (previewW + earX, previewH + earY);
            }

            if (state == NotchState.Collapsed)
            {
                double? contentW = _collapsedLiveModule?.PreferredLiveWidth is > 0
                    ? _collapsedLiveModule.PreferredLiveWidth
                    : _idleView != null ? _idleView.Width + 48 : null;
                if (contentW is double liveW)
                {
                    double w = Math.Clamp(liveW, S.CollapsedWidth, Math.Max(S.CollapsedWidth, S.ExpandedWidth - 20));
                    return (w + earX, S.CollapsedHeight + earY);
                }

                if (IsSideEdge)
                {
                    double sideW = Math.Max(S.CollapsedHeight, 32) + earX;
                    double sideH = Math.Clamp(S.CollapsedWidth, 96, 260) + earY;
                    return (sideW, sideH);
                }
            }

            return (S.CollapsedWidth + earX, S.CollapsedHeight + earY);
        }

        private void UpdateShellGeometry()
        {
            double w = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : NotchBorder.Width;
            double h = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : NotchBorder.Height;
            if (w <= 2 || h <= 2) return;

            Geometry geo = S.Style == NotchStyle.Floating
                ? BuildFloatingGeometry(w, h)
                : BuildDockedEarGeometry(w, h);
            geo.Freeze();

            NotchShellPath.Data = geo;
            NotchContentHost.Clip = geo;
        }

        private Geometry BuildFloatingGeometry(double w, double h)
        {
            double r = _state switch
            {
                NotchState.Collapsed => Math.Min(16, h / 2.0),
                NotchState.Preview => Math.Min(18, h / 2.0),
                _ => Math.Clamp(S.CornerRadius, 14, Math.Min(w, h) / 2.0)
            };
            return new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        }

        private Geometry BuildDockedEarGeometry(double w, double h)
        {
            if (IsBottomEdge) return BuildBottomDockedGeometry(w, h);
            if (IsLeftEdge) return BuildLeftDockedGeometry(w, h);
            if (IsRightEdge) return BuildRightDockedGeometry(w, h);
            return BuildTopDockedGeometry(w, h);
        }

        private double GetDockedCornerRadius(double maxRadius)
        {
            double target = _state switch
            {
                NotchState.Collapsed => Math.Min(16.0, S.CornerRadius),
                NotchState.Preview => Math.Min(20.0, S.CornerRadius),
                _ => Math.Max(22.0, S.CornerRadius)
            };
            return Math.Clamp(target, 6.0, Math.Max(6.0, maxRadius));
        }

        private Geometry BuildTopDockedGeometry(double w, double h)
        {
            double earR = Math.Min(12.0, Math.Min(h * 0.36, w * 0.14));
            double innerW = Math.Max(10, w - 2.0 * earR);
            double botR = GetDockedCornerRadius(Math.Min(innerW / 2.0, h - earR));

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
                ctx.ArcTo(new Point(earR, earR), new Size(earR, earR), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(earR, h - botR), true, false);
                ctx.ArcTo(new Point(earR + botR, h), new Size(botR, botR), 0, false, SweepDirection.Counterclockwise, true, false);
                ctx.LineTo(new Point(w - earR - botR, h), true, false);
                ctx.ArcTo(new Point(w - earR, h - botR), new Size(botR, botR), 0, false, SweepDirection.Counterclockwise, true, false);
                ctx.LineTo(new Point(w - earR, earR), true, false);
                ctx.ArcTo(new Point(w, 0), new Size(earR, earR), 0, false, SweepDirection.Clockwise, true, false);
            }
            return geo;
        }

        private Geometry BuildBottomDockedGeometry(double w, double h)
        {
            double earR = Math.Min(12.0, Math.Min(h * 0.36, w * 0.14));
            double innerW = Math.Max(10, w - 2.0 * earR);
            double topR = GetDockedCornerRadius(Math.Min(innerW / 2.0, h - earR));

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(0, h), isFilled: true, isClosed: true);
                ctx.ArcTo(new Point(earR, h - earR), new Size(earR, earR), 0, false, SweepDirection.Counterclockwise, true, false);
                ctx.LineTo(new Point(earR, topR), true, false);
                ctx.ArcTo(new Point(earR + topR, 0), new Size(topR, topR), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(w - earR - topR, 0), true, false);
                ctx.ArcTo(new Point(w - earR, topR), new Size(topR, topR), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(w - earR, h - earR), true, false);
                ctx.ArcTo(new Point(w, h), new Size(earR, earR), 0, false, SweepDirection.Counterclockwise, true, false);
            }
            return geo;
        }

        private Geometry BuildLeftDockedGeometry(double w, double h)
        {
            double earR = Math.Min(12.0, Math.Min(w * 0.36, h * 0.14));
            double innerH = Math.Max(10, h - 2.0 * earR);
            double rightR = GetDockedCornerRadius(Math.Min(innerH / 2.0, w - earR));

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
                ctx.ArcTo(new Point(earR, earR), new Size(earR, earR), 0, false, SweepDirection.Counterclockwise, true, false);
                ctx.LineTo(new Point(w - rightR, earR), true, false);
                ctx.ArcTo(new Point(w, earR + rightR), new Size(rightR, rightR), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(w, h - earR - rightR), true, false);
                ctx.ArcTo(new Point(w - rightR, h - earR), new Size(rightR, rightR), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(earR, h - earR), true, false);
                ctx.ArcTo(new Point(0, h), new Size(earR, earR), 0, false, SweepDirection.Counterclockwise, true, false);
            }
            return geo;
        }

        private Geometry BuildRightDockedGeometry(double w, double h)
        {
            double earR = Math.Min(12.0, Math.Min(w * 0.36, h * 0.14));
            double innerH = Math.Max(10, h - 2.0 * earR);
            double leftR = GetDockedCornerRadius(Math.Min(innerH / 2.0, w - earR));

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(w, 0), isFilled: true, isClosed: true);
                ctx.ArcTo(new Point(w - earR, earR), new Size(earR, earR), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(leftR, earR), true, false);
                ctx.ArcTo(new Point(0, earR + leftR), new Size(leftR, leftR), 0, false, SweepDirection.Counterclockwise, true, false);
                ctx.LineTo(new Point(0, h - earR - leftR), true, false);
                ctx.ArcTo(new Point(leftR, h - earR), new Size(leftR, leftR), 0, false, SweepDirection.Counterclockwise, true, false);
                ctx.LineTo(new Point(w - earR, h - earR), true, false);
                ctx.ArcTo(new Point(w, h), new Size(earR, earR), 0, false, SweepDirection.Clockwise, true, false);
            }
            return geo;
        }

        private void ApplyDockStyle()
        {
            bool floating = S.Style == NotchStyle.Floating;
            if (floating)
            {
                NotchBorder.Margin = S.Position switch
                {
                    NotchPosition.BottomLeft or NotchPosition.BottomCenter or NotchPosition.BottomRight => new Thickness(0, 0, 0, 6),
                    NotchPosition.LeftCenter => new Thickness(6, 0, 0, 0),
                    NotchPosition.RightCenter => new Thickness(0, 0, 6, 0),
                    _ => new Thickness(0, 6, 0, 0)
                };
                ExpandedPanel.Margin = new Thickness(0, 2, 0, 2);
            }
            else
            {
                NotchBorder.Margin = new Thickness(0);
                ExpandedPanel.Margin = IsSideEdge
                    ? new Thickness(IsLeftEdge ? 0 : 4, 12, IsRightEdge ? 0 : 4, 12)
                    : new Thickness(0);
            }

            UpdatePrivacyIndicatorPosition();
            UpdateShellGeometry();
        }

        private void UpdatePrivacyIndicatorPosition()
        {
            PrivacyIndicatorPanel.Margin = IsSideEdge
                ? new Thickness(0, 14, 6, 0)
                : S.Style == NotchStyle.Floating
                    ? new Thickness(0, 7, 10, 0)
                    : new Thickness(0, 7, 18, 0);
        }

        private (double x, double y) GetSuppressSlideOffset()
        {
            const double shadowBuffer = 22.0; // NotchShadow BlurRadius=12 + ShadowDepth=2 + Puffer
            var (w, h) = SizeFor(NotchState.Collapsed);
            var m = NotchBorder.Margin;
            return S.Position switch
            {
                NotchPosition.BottomLeft or NotchPosition.BottomCenter or NotchPosition.BottomRight
                    => (0, h + m.Bottom + shadowBuffer),
                NotchPosition.LeftCenter => (-(w + m.Left + shadowBuffer), 0),
                NotchPosition.RightCenter => (w + m.Right + shadowBuffer, 0),
                _ => (0, -(h + m.Top + shadowBuffer))
            };
        }

        private (double x, double y) GetCollapsedTranslateOffset()
            => S.Position switch
            {
                NotchPosition.BottomLeft or NotchPosition.BottomCenter or NotchPosition.BottomRight => (0, 4),
                NotchPosition.LeftCenter => (-4, 0),
                NotchPosition.RightCenter => (4, 0),
                _ => (0, -4)
            };

        private static int GetEffectiveFrameRate(bool activeTransition)
        {
            int target = S.TargetFrameRate;
            if (S.AdaptiveFrameRate && !activeTransition)
                return Math.Min(target, 60);
            return target;
        }

        private static DoubleAnimation CreateAnim(double to, TimeSpan duration, IEasingFunction? ease = null, int delayMs = 0, bool activeTransition = true)
        {
            var anim = new DoubleAnimation(to, duration)
            {
                EasingFunction = ease
            };
            if (delayMs > 0)
                anim.BeginTime = TimeSpan.FromMilliseconds(delayMs);

            Timeline.SetDesiredFrameRate(anim, GetEffectiveFrameRate(activeTransition));
            return anim;
        }

        private void ApplyThemeResources()
        {
            NotchShellPath.Fill = NotchDesign.NotchBackgroundBrush;
            NotchShellPath.Stroke = NotchDesign.NotchBorderBrush;
            NotchShellPath.StrokeThickness = NotchDesign.IsLight ? 1.0 : 0.0;

            Resources["TabBarBg"] = NotchDesign.TabBarBackgroundBrush;
            Resources["TabActiveBg"] = NotchDesign.TabActiveBackgroundBrush;
            Resources["TabHoverBg"] = NotchDesign.ControlHoverBrush;
            Resources["TabForeground"] = NotchDesign.TabIconInactiveBrush;
            Resources["TabActiveForeground"] = NotchDesign.TextPrimaryBrush;
            Resources["AccentBrush"] = NotchDesign.AccentPrimaryBrush;
            Resources["DividerBrush"] = NotchDesign.DividerBrush;
        }
    }
}
