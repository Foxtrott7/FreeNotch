using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WindowsNotch.Core;

namespace WindowsNotch.UI
{
    /// <summary>
    /// Modularer iPhone-Jiggle-Mode mit smooth gedämpftem Drag & Drop für die Top-Icon-Leiste.
    /// Beenden per Tap ohne Ziehen, Klick außerhalb der Leiste oder Esc.
    /// </summary>
    internal sealed class TopTabJiggleController
    {
        private const int LongPressMs = 450;
        private const double MoveCancelThreshold = 6.0;
        private const double DragFollowRate = 26.0;
        private const double SwapCooldownMs = 140.0;

        private sealed class TabEntry
        {
            public required string Id { get; init; }
            public required Button Button { get; init; }
            public required ScaleTransform DragScale { get; init; }
            public required RotateTransform JiggleRotate { get; init; }
            public required TranslateTransform JiggleTranslate { get; init; }
            public required TranslateTransform DragTranslate { get; init; }
        }

        private readonly Window _hostWindow;
        private readonly StackPanel _tabContainer;
        private readonly List<TabEntry> _entries = new();
        private readonly Action<IReadOnlyList<string>> _onOrderChanged;
        private readonly Func<bool> _animationsEnabled;
        private readonly Func<int> _targetFrameRate;

        private readonly DispatcherTimer _holdTimer;
        private readonly Stopwatch _frameClock = new();

        private bool _isAttached;
        private bool _isJiggleMode;
        private bool _isDragging;
        private bool _isRenderingHooked;

        private TabEntry? _pendingEntry;
        private Point _pressPointInContainer;

        private int _dragIndex = -1;
        private TabEntry? _dragEntry;
        private Point _dragAnchorInContainer;
        private Point _dragStartPointerPos;
        private Point _targetPointerPos;
        private double _dragMaxDistance;
        private bool _dragStartedFromExistingJiggle;
        private bool _swappedDuringDrag;
        private TimeSpan _lastFrameTime;
        private TimeSpan _lastSwapTime;

        public bool IsJiggleMode => _isJiggleMode;

        public TopTabJiggleController(
            Window hostWindow,
            StackPanel tabContainer,
            Action<IReadOnlyList<string>> onOrderChanged,
            Func<bool> animationsEnabled,
            Func<int> targetFrameRate)
        {
            _hostWindow = hostWindow;
            _tabContainer = tabContainer;
            _onOrderChanged = onOrderChanged;
            _animationsEnabled = animationsEnabled;
            _targetFrameRate = targetFrameRate;

            _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LongPressMs) };
            _holdTimer.Tick += HoldTimer_Tick;

            foreach (var child in _tabContainer.Children)
            {
                if (child is Button btn && btn.DataContext is INotchModule mod)
                {
                    _entries.Add(WrapButton(mod.Id, btn));
                }
            }

            Attach();
        }

        private static TabEntry WrapButton(string id, Button btn)
        {
            var dragScale = new ScaleTransform(1.0, 1.0);
            var jiggleRotate = new RotateTransform(0.0);
            var jiggleTranslate = new TranslateTransform(0.0, 0.0);
            var dragTranslate = new TranslateTransform(0.0, 0.0);

            var tg = new TransformGroup();
            tg.Children.Add(dragScale);
            tg.Children.Add(jiggleRotate);
            tg.Children.Add(jiggleTranslate);
            tg.Children.Add(dragTranslate);

            btn.RenderTransformOrigin = new Point(0.5, 0.5);
            btn.RenderTransform = tg;

            return new TabEntry
            {
                Id = id,
                Button = btn,
                DragScale = dragScale,
                JiggleRotate = jiggleRotate,
                JiggleTranslate = jiggleTranslate,
                DragTranslate = dragTranslate
            };
        }

        private void Attach()
        {
            if (_isAttached) return;
            _isAttached = true;

            foreach (var entry in _entries)
                entry.Button.PreviewMouseLeftButtonDown += TabButton_PreviewMouseLeftButtonDown;

            _tabContainer.PreviewMouseMove += TabContainer_PreviewMouseMove;
            _tabContainer.PreviewMouseLeftButtonUp += TabContainer_PreviewMouseLeftButtonUp;
            _tabContainer.LostMouseCapture += TabContainer_LostMouseCapture;
            _hostWindow.PreviewMouseLeftButtonDown += HostWindow_PreviewMouseLeftButtonDown;
        }

        public void Detach()
        {
            if (!_isAttached) return;
            _isAttached = false;

            _holdTimer.Stop();
            StopDragInternal(commitOrder: false);
            _isJiggleMode = false;
            StopJiggleAnimations();

            foreach (var entry in _entries)
                entry.Button.PreviewMouseLeftButtonDown -= TabButton_PreviewMouseLeftButtonDown;

            _tabContainer.PreviewMouseMove -= TabContainer_PreviewMouseMove;
            _tabContainer.PreviewMouseLeftButtonUp -= TabContainer_PreviewMouseLeftButtonUp;
            _tabContainer.LostMouseCapture -= TabContainer_LostMouseCapture;
            _hostWindow.PreviewMouseLeftButtonDown -= HostWindow_PreviewMouseLeftButtonDown;
            UnhookRendering();
        }

        public bool HandleEscape()
        {
            if (!_isJiggleMode) return false;
            ExitJiggleMode();
            return true;
        }

        public void ExitJiggleMode()
        {
            if (!_isJiggleMode) return;
            _holdTimer.Stop();
            StopDragInternal(commitOrder: true);
            _isJiggleMode = false;
            StopJiggleAnimations();
        }

        private void EnterJiggleMode()
        {
            if (_isJiggleMode) return;
            _isJiggleMode = true;
            StartJiggleAnimations();
        }

        private void HostWindow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isJiggleMode) return;
            if (e.OriginalSource is DependencyObject dep && IsInsideAnyTab(dep))
                return;

            ExitJiggleMode();
        }

        private void TabButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_entries.Count <= 1) return;
            if (sender is not Button btn) return;
            var entry = _entries.FirstOrDefault(x => ReferenceEquals(x.Button, btn));
            if (entry == null) return;

            var pt = e.GetPosition(_tabContainer);

            if (_isJiggleMode)
            {
                e.Handled = true;
                BeginDrag(entry, pt, fromExistingJiggle: true);
                return;
            }

            _pendingEntry = entry;
            _pressPointInContainer = pt;
            _holdTimer.Stop();
            _holdTimer.Start();
        }

        private void HoldTimer_Tick(object? sender, EventArgs e)
        {
            _holdTimer.Stop();
            if (_pendingEntry == null || Mouse.LeftButton != MouseButtonState.Pressed)
            {
                _pendingEntry = null;
                return;
            }

            var entry = _pendingEntry;
            _pendingEntry = null;
            EnterJiggleMode();
            BeginDrag(entry, _pressPointInContainer, fromExistingJiggle: false);
        }

        private void TabContainer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            var pt = e.GetPosition(_tabContainer);

            if (_holdTimer.IsEnabled)
            {
                if ((pt - _pressPointInContainer).Length > MoveCancelThreshold)
                {
                    _holdTimer.Stop();
                    _pendingEntry = null;
                }
                return;
            }

            if (_isDragging)
            {
                _targetPointerPos = pt;
                _dragMaxDistance = Math.Max(_dragMaxDistance, (pt - _dragStartPointerPos).Length);
            }
        }

        private void TabContainer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _holdTimer.Stop();
            _pendingEntry = null;

            if (_isDragging)
            {
                bool shouldExitByTap = _dragStartedFromExistingJiggle &&
                                       _dragMaxDistance < MoveCancelThreshold &&
                                       !_swappedDuringDrag;
                e.Handled = true;
                StopDragInternal(commitOrder: true);

                if (shouldExitByTap)
                    ExitJiggleMode();
            }
        }

        private void TabContainer_LostMouseCapture(object sender, MouseEventArgs e)
        {
            _holdTimer.Stop();
            _pendingEntry = null;

            if (_isDragging && !ReferenceEquals(Mouse.Captured, _tabContainer))
                StopDragInternal(commitOrder: true);
        }

        private void BeginDrag(TabEntry entry, Point pointerInContainer, bool fromExistingJiggle)
        {
            int idx = _entries.IndexOf(entry);
            if (idx < 0) return;

            _isDragging = true;
            _dragIndex = idx;
            _dragEntry = entry;
            _dragAnchorInContainer = pointerInContainer;
            _dragStartPointerPos = pointerInContainer;
            _targetPointerPos = pointerInContainer;
            _dragMaxDistance = 0.0;
            _dragStartedFromExistingJiggle = fromExistingJiggle;
            _swappedDuringDrag = false;
            _lastSwapTime = TimeSpan.FromMilliseconds(-SwapCooldownMs);

            entry.DragTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            entry.DragTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            PauseTabJiggle(entry);

            Panel.SetZIndex(entry.Button, 100);
            entry.Button.Opacity = 0.92;

            if (_animationsEnabled())
            {
                var scaleAnim = new DoubleAnimation(1.12, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Timeline.SetDesiredFrameRate(scaleAnim, _targetFrameRate());
                entry.DragScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                entry.DragScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            }
            else
            {
                entry.DragScale.ScaleX = 1.12;
                entry.DragScale.ScaleY = 1.12;
            }

            Mouse.Capture(_tabContainer, CaptureMode.SubTree);
            _frameClock.Restart();
            _lastFrameTime = TimeSpan.Zero;
            HookRendering();
        }

        private void StopDragInternal(bool commitOrder)
        {
            if (!_isDragging) return;
            _isDragging = false;
            UnhookRendering();

            if (ReferenceEquals(Mouse.Captured, _tabContainer))
                Mouse.Capture(null);

            var entry = _dragEntry;
            int droppedIdx = _dragIndex;
            _dragEntry = null;
            _dragIndex = -1;

            if (entry != null)
            {
                Panel.SetZIndex(entry.Button, 0);
                entry.Button.Opacity = 1.0;

                if (_animationsEnabled())
                {
                    var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                    var tx = new DoubleAnimation(0, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease };
                    var ty = new DoubleAnimation(0, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease };
                    var sc = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
                    int fps = _targetFrameRate();
                    Timeline.SetDesiredFrameRate(tx, fps);
                    Timeline.SetDesiredFrameRate(ty, fps);
                    Timeline.SetDesiredFrameRate(sc, fps);

                    entry.DragTranslate.BeginAnimation(TranslateTransform.XProperty, tx);
                    entry.DragTranslate.BeginAnimation(TranslateTransform.YProperty, ty);
                    entry.DragScale.BeginAnimation(ScaleTransform.ScaleXProperty, sc);
                    entry.DragScale.BeginAnimation(ScaleTransform.ScaleYProperty, sc);

                    if (_isJiggleMode && droppedIdx >= 0)
                        StartTabJiggle(entry, droppedIdx);
                }
                else
                {
                    entry.DragTranslate.X = 0;
                    entry.DragTranslate.Y = 0;
                    entry.DragScale.ScaleX = 1.0;
                    entry.DragScale.ScaleY = 1.0;
                }
            }

            if (commitOrder && _swappedDuringDrag)
            {
                _onOrderChanged(_entries.Select(e => e.Id).ToList());
            }
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_isDragging || _dragEntry == null || _dragIndex < 0) return;

            var now = _frameClock.Elapsed;
            double dt = (now - _lastFrameTime).TotalSeconds;
            _lastFrameTime = now;
            if (dt <= 0.0005) return;
            dt = Math.Min(dt, 0.05);

            double frameAlpha = WindowsNotch.Core.JiggleMotionHelper.ComputeFollowAlpha(DragFollowRate, dt);

            double desiredX = _targetPointerPos.X - _dragAnchorInContainer.X;
            double desiredY = Math.Clamp(_targetPointerPos.Y - _dragAnchorInContainer.Y, -5.0, 5.0);

            _dragEntry.DragTranslate.X += (desiredX - _dragEntry.DragTranslate.X) * frameAlpha;
            _dragEntry.DragTranslate.Y += (desiredY - _dragEntry.DragTranslate.Y) * frameAlpha;

            if ((now - _lastSwapTime).TotalMilliseconds >= SwapCooldownMs)
            {
                int targetIndex = FindTargetIndex(_targetPointerPos.X, _dragIndex);
                if (targetIndex >= 0 && targetIndex != _dragIndex)
                {
                    _lastSwapTime = now;
                    SwapTabs(_dragIndex, targetIndex);
                }
            }
        }

        private double GetSlotPitch(TabEntry entry)
        {
            double w = entry.Button.ActualWidth > 0 ? entry.Button.ActualWidth : 30.0;
            return w + entry.Button.Margin.Left + entry.Button.Margin.Right;
        }

        private int FindTargetIndex(double pointerX, int currentIndex)
        {
            if (_entries.Count <= 1) return currentIndex;
            double pitch = GetSlotPitch(_entries[currentIndex]);
            if (pitch <= 1.0) return currentIndex;

            double currentCenter = currentIndex * pitch + pitch * 0.5;
            double delta = pointerX - currentCenter;

            // Hysterese (58 % der Slot-Breite), damit benachbarte Icons nicht flackern
            if (delta > pitch * 0.58 && currentIndex < _entries.Count - 1)
                return currentIndex + 1;
            if (delta < -pitch * 0.58 && currentIndex > 0)
                return currentIndex - 1;

            return currentIndex;
        }

        private void SwapTabs(int fromIndex, int toIndex)
        {
            if (fromIndex == toIndex || fromIndex < 0 || toIndex < 0 ||
                fromIndex >= _entries.Count || toIndex >= _entries.Count)
                return;

            var dragged = _entries[fromIndex];
            var neighbor = _entries[toIndex];
            double pitch = GetSlotPitch(dragged);
            double deltaX = (toIndex - fromIndex) * pitch;

            _swappedDuringDrag = true;

            _entries[toIndex] = dragged;
            _entries[fromIndex] = neighbor;

            // Reihenfolge im StackPanel aktualisieren
            _tabContainer.Children.Remove(dragged.Button);
            _tabContainer.Children.Insert(toIndex, dragged.Button);

            // Anker der gezogenen Schaltfläche kompensieren, damit sie ohne Sprung unter dem Cursor bleibt
            _dragAnchorInContainer.X += deltaX;
            dragged.DragTranslate.X -= deltaX;

            // Verdrängtes Nachbar-Icon sanft in die neue Position gleiten lassen
            neighbor.DragTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            double startX = neighbor.DragTranslate.X + deltaX;
            neighbor.DragTranslate.X = startX;

            if (_animationsEnabled())
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var animX = new DoubleAnimation(startX, 0, TimeSpan.FromMilliseconds(185)) { EasingFunction = ease };
                Timeline.SetDesiredFrameRate(animX, _targetFrameRate());
                neighbor.DragTranslate.BeginAnimation(TranslateTransform.XProperty, animX);
            }
            else
            {
                neighbor.DragTranslate.X = 0;
            }

            _dragIndex = toIndex;
        }

        private void StartJiggleAnimations()
        {
            for (int i = 0; i < _entries.Count; i++)
                StartTabJiggle(_entries[i], i);
        }

        private void StartTabJiggle(TabEntry entry, int index)
        {
            if (!_animationsEnabled())
            {
                entry.DragScale.ScaleX = 0.96;
                entry.DragScale.ScaleY = 0.96;
                return;
            }

            double angle = (index % 2 == 0) ? 2.1 : -2.1;
            int dur = 152 + (index % 4) * 9;
            int delay = (index % 5) * 26;

            WindowsNotch.Core.JiggleMotionHelper.StartJiggle(
                entry.JiggleRotate,
                entry.JiggleTranslate,
                angle: angle,
                rotDurationMs: dur,
                rotDelayMs: delay,
                yOffset: 0.45,
                yDurationMs: 185 + (index % 3) * 11,
                yDelayMs: delay / 2,
                targetFps: _targetFrameRate());
        }

        private void PauseTabJiggle(TabEntry entry)
            => WindowsNotch.Core.JiggleMotionHelper.PauseJiggle(
                entry.JiggleRotate,
                entry.JiggleTranslate,
                _animationsEnabled(),
                _targetFrameRate(),
                resetDurationMs: 90);

        private void StopJiggleAnimations()
        {
            foreach (var entry in _entries)
            {
                WindowsNotch.Core.JiggleMotionHelper.StopJiggle(
                    entry.JiggleRotate,
                    entry.JiggleTranslate,
                    entry.DragScale,
                    playCommitPulse: false,
                    animationsEnabled: _animationsEnabled(),
                    targetFps: _targetFrameRate());
            }
        }

        private void HookRendering()
        {
            if (_isRenderingHooked) return;
            _isRenderingHooked = true;
            CompositionTarget.Rendering += OnRendering;
        }

        private void UnhookRendering()
        {
            if (!_isRenderingHooked) return;
            _isRenderingHooked = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        private bool IsInsideAnyTab(DependencyObject? source)
            => WindowsNotch.Core.JiggleMotionHelper.IsInsideVisualTree(source, node =>
            {
                foreach (var entry in _entries)
                    if (ReferenceEquals(node, entry.Button))
                        return true;
                return false;
            });
    }
}
