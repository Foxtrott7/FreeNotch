using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WindowsNotch.Modules.Widgets
{
    /// <summary>
    /// Modularer iPhone-Jiggle-Mode mit geschwindigkeitsabhängigem, smooth gedämpftem Drag & Drop
    /// für beliebige 2×2-Bento-Kacheln. Beenden per Klick außerhalb/Tap ohne Ziehen oder Esc.
    /// </summary>
    internal sealed class BentoJiggleController
    {
        private const double GridGap = 12.0;
        private const int LongPressMs = 450;
        private const double MoveCancelThreshold = 6.0;
        private const double StoreDragThreshold = 8.0;
        private const double DragFollowRate = 20.0;
        private const double SwapCooldownMs = 270.0;

        private sealed class TileEntry
        {
            public required string Id { get; init; }
            public required Border Container { get; init; }
            public required ScaleTransform DragScale { get; init; }
            public required RotateTransform JiggleRotate { get; init; }
            public required TranslateTransform JiggleTranslate { get; init; }
            public required TranslateTransform DragTranslate { get; init; }
        }

        private readonly Grid _rootHost;
        private readonly Grid _bentoGrid;
        private readonly TileEntry[] _slots = new TileEntry[4];
        private readonly Action<IReadOnlyList<string>> _onOrderChanged;
        private readonly Func<bool> _animationsEnabled;
        private readonly Func<int> _targetFrameRate;
        private readonly Action? _onEnterJiggle;
        private readonly Action? _onExitJiggle;
        private readonly Action<string, int>? _onCommitShopDrop;
        private readonly Action<int>? _onRemoveTileToShop;

        private readonly DispatcherTimer _holdTimer;
        private readonly Stopwatch _frameClock = new();
        private Window? _hostWindow;

        private bool _isAttached;
        private bool _isJiggleMode;
        private bool _isDragging;
        private bool _isHoveringRemoveZone;
        private bool _isRenderingHooked;

        // Shop-Drag Zustand
        private bool _isShopDragging;
        private bool _shopPassedThreshold;
        private Border? _shopSourceBorder;
        private Brush? _shopSourceOriginalBg;
        private UIElement? _shopSourceOriginalChild;
        private Border? _shopPreview;
        private ScaleTransform? _shopPreviewScale;
        private TranslateTransform? _shopPreviewTranslate;
        private string _shopDragTileId = string.Empty;
        private Point _shopDragStartInRoot;
        private Point _shopTargetInRoot;
        private double _shopDragMaxDistance;
        private int _shopHoverSlot = -1;

        private TileEntry? _pendingTile;
        private TileEntry? _pressedTile;
        private Point _pressPointInBento;

        private int _dragSlot = -1;
        private TileEntry? _dragTile;
        private Point _dragAnchorInBento;
        private Point _dragStartPointerPos;
        private Point _targetPointerPos;
        private double _dragMaxDistance;
        private bool _dragStartedFromExistingJiggle;
        private bool _swappedDuringDrag;
        private TimeSpan _lastFrameTime;
        private TimeSpan _lastSwapTime;

        public bool IsJiggleMode => _isJiggleMode;

        public BentoJiggleController(
            Grid rootHost,
            Grid bentoGrid,
            IReadOnlyList<(string Id, Border Tile)> orderedTiles,
            Action<IReadOnlyList<string>> onOrderChanged,
            Func<bool> animationsEnabled,
            Func<int> targetFrameRate,
            Action? onEnterJiggle = null,
            Action? onExitJiggle = null,
            Action<string, int>? onCommitShopDrop = null,
            Action<int>? onRemoveTileToShop = null)
        {
            _rootHost = rootHost;
            _bentoGrid = bentoGrid;
            _onOrderChanged = onOrderChanged;
            _animationsEnabled = animationsEnabled;
            _targetFrameRate = targetFrameRate;
            _onEnterJiggle = onEnterJiggle;
            _onExitJiggle = onExitJiggle;
            _onCommitShopDrop = onCommitShopDrop;
            _onRemoveTileToShop = onRemoveTileToShop;

            _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LongPressMs) };
            _holdTimer.Tick += HoldTimer_Tick;

            for (int i = 0; i < 4 && i < orderedTiles.Count; i++)
            {
                var (id, tile) = orderedTiles[i];
                var entry = WrapAndSetupTile(id, tile);
                _slots[i] = entry;
                PlaceInSlot(entry.Container, i);
                _bentoGrid.Children.Add(entry.Container);
            }

            Attach();
        }

        private static TileEntry WrapAndSetupTile(string id, Border tile)
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

            tile.MinWidth = 0;
            tile.MinHeight = 0;
            tile.HorizontalAlignment = HorizontalAlignment.Stretch;
            tile.VerticalAlignment = VerticalAlignment.Stretch;
            tile.RenderTransformOrigin = new Point(0.5, 0.5);
            tile.RenderTransform = tg;

            return new TileEntry
            {
                Id = id,
                Container = tile,
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

            foreach (var entry in _slots)
            {
                if (entry == null) continue;
                entry.Container.PreviewMouseLeftButtonDown += Tile_PreviewMouseLeftButtonDown;
                entry.Container.PreviewMouseLeftButtonUp += Tile_PreviewMouseLeftButtonUp;
                entry.Container.MouseEnter += Tile_MouseEnter;
                entry.Container.MouseLeave += Tile_MouseLeave;
            }
            _bentoGrid.PreviewMouseLeftButtonDown += Bento_PreviewMouseLeftButtonDown;
            _bentoGrid.PreviewMouseMove += Bento_PreviewMouseMove;
            _bentoGrid.PreviewMouseLeftButtonUp += Bento_PreviewMouseLeftButtonUp;
            _bentoGrid.LostMouseCapture += Bento_LostMouseCapture;
            _rootHost.PreviewMouseMove += RootHost_PreviewMouseMove;
            _rootHost.PreviewMouseLeftButtonUp += RootHost_PreviewMouseLeftButtonUp;
            _rootHost.LostMouseCapture += RootHost_LostMouseCapture;
            _rootHost.Loaded += RootHost_Loaded;
            HookHostWindow();
        }

        private void RootHost_Loaded(object sender, RoutedEventArgs e)
        {
            HookHostWindow();
        }

        private void HookHostWindow()
        {
            var win = Window.GetWindow(_rootHost);
            if (win == null || ReferenceEquals(_hostWindow, win)) return;
            UnhookHostWindow();
            _hostWindow = win;
            _hostWindow.PreviewMouseLeftButtonDown += HostWindow_PreviewMouseLeftButtonDown;
            _hostWindow.PreviewMouseLeftButtonUp += HostWindow_PreviewMouseLeftButtonUp;
        }

        private void UnhookHostWindow()
        {
            if (_hostWindow == null) return;
            _hostWindow.PreviewMouseLeftButtonDown -= HostWindow_PreviewMouseLeftButtonDown;
            _hostWindow.PreviewMouseLeftButtonUp -= HostWindow_PreviewMouseLeftButtonUp;
            _hostWindow = null;
        }

        public void CancelPendingPress()
        {
            _holdTimer.Stop();
            _pendingTile = null;
            _pressedTile = null;
        }

        public void Detach()
        {
            if (!_isAttached) return;
            _isAttached = false;

            _holdTimer.Stop();
            _pendingTile = null;
            _pressedTile = null;
            StopShopDragInternal(commit: false);
            StopDragInternal(commitOrder: false);
            StopJiggleAnimations();
            UnhookHostWindow();

            foreach (var entry in _slots)
            {
                if (entry == null) continue;
                entry.Container.PreviewMouseLeftButtonDown -= Tile_PreviewMouseLeftButtonDown;
                entry.Container.PreviewMouseLeftButtonUp -= Tile_PreviewMouseLeftButtonUp;
                entry.Container.MouseEnter -= Tile_MouseEnter;
                entry.Container.MouseLeave -= Tile_MouseLeave;
            }
            _bentoGrid.PreviewMouseLeftButtonDown -= Bento_PreviewMouseLeftButtonDown;
            _bentoGrid.PreviewMouseMove -= Bento_PreviewMouseMove;
            _bentoGrid.PreviewMouseLeftButtonUp -= Bento_PreviewMouseLeftButtonUp;
            _bentoGrid.LostMouseCapture -= Bento_LostMouseCapture;
            _rootHost.PreviewMouseMove -= RootHost_PreviewMouseMove;
            _rootHost.PreviewMouseLeftButtonUp -= RootHost_PreviewMouseLeftButtonUp;
            _rootHost.LostMouseCapture -= RootHost_LostMouseCapture;
            _rootHost.Loaded -= RootHost_Loaded;
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
            StopShopDragInternal(commit: false);
            StopDragInternal(commitOrder: true);
            _isJiggleMode = false;
            StopJiggleAnimations(playCommitPulse: true);
            _onExitJiggle?.Invoke();
        }

        public void EnterJiggleMode()
        {
            if (_isJiggleMode) return;
            _isJiggleMode = true;
            _pressedTile = null;
            foreach (var entry in _slots)
            {
                if (entry == null) continue;
                Panel.SetZIndex(entry.Container, 0);
                AnimateTileHoverScale(entry, 1.0, 120);
            }
            HookHostWindow();
            StartJiggleAnimations();
            _onEnterJiggle?.Invoke();
        }

        public void ReplaceSlotTile(int slot, string newId, Border newTile)
        {
            if (slot < 0 || slot >= _slots.Length) return;
            var old = _slots[slot];
            if (old != null)
            {
                old.Container.PreviewMouseLeftButtonDown -= Tile_PreviewMouseLeftButtonDown;
                old.Container.PreviewMouseLeftButtonUp -= Tile_PreviewMouseLeftButtonUp;
                old.Container.MouseEnter -= Tile_MouseEnter;
                old.Container.MouseLeave -= Tile_MouseLeave;
                _bentoGrid.Children.Remove(old.Container);
            }

            var entry = WrapAndSetupTile(newId, newTile);
            _slots[slot] = entry;
            PlaceInSlot(entry.Container, slot);
            _bentoGrid.Children.Add(entry.Container);

            entry.Container.PreviewMouseLeftButtonDown += Tile_PreviewMouseLeftButtonDown;
            entry.Container.PreviewMouseLeftButtonUp += Tile_PreviewMouseLeftButtonUp;
            entry.Container.MouseEnter += Tile_MouseEnter;
            entry.Container.MouseLeave += Tile_MouseLeave;

            if (_isJiggleMode)
                StartTileJiggle(entry, slot, extraDelayMs: 60);
        }

        private void Tile_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_isJiggleMode || _isDragging || sender is not Border border) return;
            var entry = FindEntry(border);
            if (entry == null) return;
            Panel.SetZIndex(entry.Container, 2);
            if (ReferenceEquals(_pressedTile, entry) && Mouse.LeftButton == MouseButtonState.Pressed)
                return;
            AnimateTileHoverScale(entry, 1.025, Core.NotchDesign.BentoHoverEnterMs);
        }

        private void Tile_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_isJiggleMode || _isDragging || sender is not Border border) return;
            var entry = FindEntry(border);
            if (entry == null) return;
            if (ReferenceEquals(_pressedTile, entry) && Mouse.LeftButton == MouseButtonState.Pressed)
                return;
            Panel.SetZIndex(entry.Container, 0);
            AnimateTileHoverScale(entry, 1.0, Core.NotchDesign.BentoHoverLeaveMs);
        }

        private void Tile_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isJiggleMode || _isDragging || sender is not Border border) return;
            var entry = FindEntry(border);
            if (entry == null) return;
            ReleasePressedTile(entry);
        }

        private void HostWindow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isJiggleMode || _isDragging || _pressedTile == null) return;
            ReleasePressedTile(_pressedTile);
        }

        private void ReleasePressedTile(TileEntry entry)
        {
            if (ReferenceEquals(_pressedTile, entry))
                _pressedTile = null;
            bool over = entry.Container.IsMouseOver;
            Panel.SetZIndex(entry.Container, over ? 2 : 0);
            AnimateTileHoverScale(entry, over ? 1.025 : 1.0, Core.NotchDesign.BentoPressReleaseMs, useSpring: true);
        }

        private TileEntry? FindEntry(Border border)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i] != null && ReferenceEquals(_slots[i].Container, border))
                    return _slots[i];
            }
            return null;
        }

        private void AnimateTileHoverScale(TileEntry entry, double target, int durationMs, bool useSpring = false)
        {
            if (!_animationsEnabled())
            {
                entry.DragScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                entry.DragScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                entry.DragScale.ScaleX = target;
                entry.DragScale.ScaleY = target;
                return;
            }

            var dur = TimeSpan.FromMilliseconds(durationMs);
            IEasingFunction ease = useSpring
                ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.30 }
                : new CubicEase { EasingMode = EasingMode.EaseOut };
            int fps = _targetFrameRate();
            var animX = new DoubleAnimation(target, dur) { EasingFunction = ease };
            var animY = new DoubleAnimation(target, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(animX, fps);
            Timeline.SetDesiredFrameRate(animY, fps);
            entry.DragScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
            entry.DragScale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
        }

        private void HostWindow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isJiggleMode) return;

            // Klick außerhalb einer Bento-Kachel und außerhalb des Shops beendet den Jiggle-Mode
            if (e.OriginalSource is DependencyObject dep && (IsInsideAnyTile(dep) || IsInsideShopDrawer(dep)))
                return;

            ExitJiggleMode();
        }

        private void Bento_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isJiggleMode) return;

            if (e.OriginalSource is DependencyObject dep && !IsInsideAnyTile(dep))
            {
                e.Handled = true;
                ExitJiggleMode();
            }
        }

        private void Tile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border tile) return;
            var entry = _slots.FirstOrDefault(s => ReferenceEquals(s?.Container, tile));
            if (entry == null) return;

            var pt = e.GetPosition(_bentoGrid);

            if (_isJiggleMode)
            {
                if (IsCarouselNavControl(e.OriginalSource as DependencyObject))
                    return;
                e.Handled = true;
                BeginDrag(entry, pt, fromExistingJiggle: true);
                return;
            }

            var source = e.OriginalSource as DependencyObject;
            if (IsTextInputOrScroll(source) || IsCarouselNavControl(source) || IsInteractiveButton(source))
            {
                CancelPendingPress();
                return;
            }

            _pressedTile = entry;
            Panel.SetZIndex(entry.Container, 2);
            AnimateTileHoverScale(entry, 0.99, 130);

            // Bei interaktiven Seek-Balken kein Long-Press-Jiggle starten, damit Ziehen jederzeit klappt
            if (IsInteractiveScrubber(source))
                return;

            _pendingTile = entry;
            _pressPointInBento = pt;
            _holdTimer.Stop();
            _holdTimer.Start();
        }

        private void HoldTimer_Tick(object? sender, EventArgs e)
        {
            _holdTimer.Stop();
            if (_pendingTile == null || Mouse.LeftButton != MouseButtonState.Pressed)
            {
                _pendingTile = null;
                return;
            }

            var tile = _pendingTile;
            _pendingTile = null;
            EnterJiggleMode();
            BeginDrag(tile, _pressPointInBento, fromExistingJiggle: false);
        }

        private void Bento_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            var pt = e.GetPosition(_bentoGrid);

            if (_holdTimer.IsEnabled)
            {
                if ((pt - _pressPointInBento).Length > MoveCancelThreshold)
                {
                    _holdTimer.Stop();
                    _pendingTile = null;
                }
                return;
            }

            if (_isDragging)
            {
                _targetPointerPos = pt;
                _dragMaxDistance = Math.Max(_dragMaxDistance, (pt - _dragStartPointerPos).Length);
            }
        }

        private void Bento_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _holdTimer.Stop();
            _pendingTile = null;

            if (_isDragging)
            {
                bool shouldExitByTap = _dragStartedFromExistingJiggle &&
                                       _dragMaxDistance < MoveCancelThreshold &&
                                       !_swappedDuringDrag;
                bool removeToShop = _isHoveringRemoveZone;
                int slotToRemove = _dragSlot;
                e.Handled = true;
                StopDragInternal(commitOrder: !removeToShop);

                if (removeToShop && slotToRemove >= 0)
                    _onRemoveTileToShop?.Invoke(slotToRemove);
                else if (shouldExitByTap)
                    ExitJiggleMode();
            }
        }

        private void Bento_LostMouseCapture(object sender, MouseEventArgs e)
        {
            _holdTimer.Stop();
            _pendingTile = null;

            if (_isDragging && !ReferenceEquals(Mouse.Captured, _bentoGrid))
                StopDragInternal(commitOrder: true);
        }

        private void BeginDrag(TileEntry entry, Point pointerInBento, bool fromExistingJiggle)
        {
            int slot = Array.IndexOf(_slots, entry);
            if (slot < 0) return;

            _isDragging = true;
            _isHoveringRemoveZone = false;
            _dragSlot = slot;
            _dragTile = entry;
            double curX = entry.DragTranslate.X;
            double curY = entry.DragTranslate.Y;
            entry.DragTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            entry.DragTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            entry.DragTranslate.X = curX;
            entry.DragTranslate.Y = curY;

            _dragAnchorInBento = new Point(
                pointerInBento.X - curX,
                pointerInBento.Y - curY);
            _dragStartPointerPos = pointerInBento;
            _targetPointerPos = pointerInBento;
            _dragMaxDistance = 0.0;
            _dragStartedFromExistingJiggle = fromExistingJiggle;
            _swappedDuringDrag = false;
            _lastSwapTime = TimeSpan.FromMilliseconds(-SwapCooldownMs);

            PauseTileJiggle(entry);

            foreach (var s in _slots)
            {
                if (s != null && !ReferenceEquals(s, entry))
                    Panel.SetZIndex(s.Container, 0);
            }
            Panel.SetZIndex(entry.Container, 150);
            entry.Container.Opacity = 0.94;

            if (_animationsEnabled())
            {
                var scaleAnim = new DoubleAnimation(1.022, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Timeline.SetDesiredFrameRate(scaleAnim, _targetFrameRate());
                entry.DragScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                entry.DragScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            }
            else
            {
                entry.DragScale.ScaleX = 1.022;
                entry.DragScale.ScaleY = 1.022;
            }

            Mouse.Capture(_bentoGrid, CaptureMode.SubTree);
            _frameClock.Restart();
            _lastFrameTime = TimeSpan.Zero;
            HookRendering();
        }

        private void StopDragInternal(bool commitOrder)
        {
            if (!_isDragging) return;
            _isDragging = false;
            UnhookRendering();

            if (ReferenceEquals(Mouse.Captured, _bentoGrid))
                Mouse.Capture(null);

            var entry = _dragTile;
            int droppedSlot = _dragSlot;
            _dragTile = null;
            _dragSlot = -1;
            _isHoveringRemoveZone = false;

            if (entry != null)
            {
                entry.Container.Opacity = 1.0;

                if (_animationsEnabled())
                {
                    var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                    double curX = entry.DragTranslate.X;
                    double curY = entry.DragTranslate.Y;
                    double curScale = entry.DragScale.ScaleX;
                    var tx = new DoubleAnimation(curX, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease };
                    var ty = new DoubleAnimation(curY, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease };
                    var sc = new DoubleAnimation(curScale, 1.0, TimeSpan.FromMilliseconds(250)) { EasingFunction = ease };
                    int fps = _targetFrameRate();
                    Timeline.SetDesiredFrameRate(tx, fps);
                    Timeline.SetDesiredFrameRate(ty, fps);
                    Timeline.SetDesiredFrameRate(sc, fps);

                    tx.Completed += (_, _) =>
                    {
                        if (!ReferenceEquals(_dragTile, entry))
                            Panel.SetZIndex(entry.Container, 0);
                    };

                    entry.DragTranslate.X = 0;
                    entry.DragTranslate.Y = 0;
                    entry.DragScale.ScaleX = 1.0;
                    entry.DragScale.ScaleY = 1.0;
                    entry.DragTranslate.BeginAnimation(TranslateTransform.XProperty, tx);
                    entry.DragTranslate.BeginAnimation(TranslateTransform.YProperty, ty);
                    entry.DragScale.BeginAnimation(ScaleTransform.ScaleXProperty, sc);
                    entry.DragScale.BeginAnimation(ScaleTransform.ScaleYProperty, sc);

                    if (_isJiggleMode && droppedSlot >= 0)
                        StartTileJiggle(entry, droppedSlot, extraDelayMs: 140);
                }
                else
                {
                    Panel.SetZIndex(entry.Container, 0);
                    entry.DragTranslate.X = 0;
                    entry.DragTranslate.Y = 0;
                    entry.DragScale.ScaleX = 1.0;
                    entry.DragScale.ScaleY = 1.0;
                }
            }

            if (commitOrder)
            {
                _onOrderChanged(_slots.Select(s => s.Id).ToList());
            }
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            var now = _frameClock.Elapsed;
            double dt = (now - _lastFrameTime).TotalSeconds;
            _lastFrameTime = now;
            if (dt <= 0.0005) return;
            dt = Math.Min(dt, 0.05);

            // Bildwiederholraten-unabhängige, ruhige exponentielle Glättung ohne Geschwindigkeits-Spikes
            double frameAlpha = Core.JiggleMotionHelper.ComputeFollowAlpha(DragFollowRate, dt);

            if (_isShopDragging && _shopPreviewTranslate != null)
            {
                if (!_shopPassedThreshold)
                    return;

                double desiredX = _shopTargetInRoot.X - _shopDragStartInRoot.X;
                double desiredY = _shopTargetInRoot.Y - _shopDragStartInRoot.Y;
                _shopPreviewTranslate.X += (desiredX - _shopPreviewTranslate.X) * frameAlpha;
                _shopPreviewTranslate.Y += (desiredY - _shopPreviewTranslate.Y) * frameAlpha;

                var ptInBento = _rootHost.TranslatePoint(_shopTargetInRoot, _bentoGrid);
                int hover = FindSlotAtPoint(ptInBento);
                if (hover != _shopHoverSlot)
                {
                    _shopHoverSlot = hover;
                    for (int i = 0; i < _slots.Length; i++)
                    {
                        if (_slots[i] != null)
                            _slots[i].Container.Opacity = (i == hover) ? 0.68 : 1.0;
                    }
                }
                return;
            }

            if (!_isDragging || _dragTile == null || _dragSlot < 0) return;

            double dragDesiredX = _targetPointerPos.X - _dragAnchorInBento.X;
            double dragDesiredY = _targetPointerPos.Y - _dragAnchorInBento.Y;

            _dragTile.DragTranslate.X += (dragDesiredX - _dragTile.DragTranslate.X) * frameAlpha;
            _dragTile.DragTranslate.Y += (dragDesiredY - _dragTile.DragTranslate.Y) * frameAlpha;

            int activeNonEmpty = _slots.Count(s => s != null && !string.IsNullOrWhiteSpace(s.Id));
            bool inRemoveZone = activeNonEmpty > 1 &&
                                !string.IsNullOrWhiteSpace(_dragTile.Id) &&
                                _targetPointerPos.Y > _bentoGrid.ActualHeight + 8;
            if (inRemoveZone != _isHoveringRemoveZone)
            {
                _isHoveringRemoveZone = inRemoveZone;
                _dragTile.Container.Opacity = inRemoveZone ? 0.65 : 0.94;
                AnimateTileHoverScale(_dragTile, inRemoveZone ? 0.90 : 1.022, 140);
            }

            if (!inRemoveZone && (now - _lastSwapTime).TotalMilliseconds >= SwapCooldownMs)
            {
                int targetSlot = FindTargetSlot(_targetPointerPos, _dragSlot);
                if (targetSlot >= 0 && targetSlot != _dragSlot)
                {
                    _lastSwapTime = now;
                    SwapSlots(_dragSlot, targetSlot);
                }
            }
        }

        private int FindSlotAtPoint(Point pt)
        {
            double w = _bentoGrid.ActualWidth;
            double h = _bentoGrid.ActualHeight;
            if (w <= 20 || h <= 20) return -1;
            if (pt.X < 0 || pt.Y < 0 || pt.X > w || pt.Y > h) return -1;

            double cellW = (w - GridGap) / 2.0;
            double cellH = (h - GridGap) / 2.0;
            for (int i = 0; i < 4; i++)
            {
                if (GetSlotRect(i, cellW, cellH).Contains(pt))
                    return i;
            }
            return -1;
        }

        private int FindTargetSlot(Point pointer, int currentSlot)
        {
            double w = _bentoGrid.ActualWidth;
            double h = _bentoGrid.ActualHeight;
            if (w <= 20 || h <= 20) return currentSlot;

            double cellW = (w - GridGap) / 2.0;
            double cellH = (h - GridGap) / 2.0;

            Rect currentRect = GetSlotRect(currentSlot, cellW, cellH);
            Point currentCenter = new(currentRect.X + cellW / 2.0, currentRect.Y + cellH / 2.0);
            double distCurrent = (pointer - currentCenter).Length;

            for (int i = 0; i < 4; i++)
            {
                if (i == currentSlot) continue;
                Rect r = GetSlotRect(i, cellW, cellH);

                // Feinfühlige Positionierung mit Hysterese gegen Rand-/Diagonal-Flackern
                double padX = cellW * 0.22;
                double padY = cellH * 0.22;
                var innerRect = new Rect(r.X + padX, r.Y + padY, Math.Max(1, r.Width - 2 * padX), Math.Max(1, r.Height - 2 * padY));
                Point targetCenter = new(r.X + cellW / 2.0, r.Y + cellH / 2.0);
                double distTarget = (pointer - targetCenter).Length;

                if (innerRect.Contains(pointer) || (r.Contains(pointer) && distTarget < distCurrent * 0.72))
                    return i;
            }

            return currentSlot;
        }

        private void SwapSlots(int fromSlot, int toSlot)
        {
            var dragged = _slots[fromSlot];
            var neighbor = _slots[toSlot];
            if (dragged == null || neighbor == null) return;

            _swappedDuringDrag = true;

            double w = _bentoGrid.ActualWidth;
            double h = _bentoGrid.ActualHeight;
            double cellW = (w - GridGap) / 2.0;
            double cellH = (h - GridGap) / 2.0;

            Rect fromRect = GetSlotRect(fromSlot, cellW, cellH);
            Rect toRect = GetSlotRect(toSlot, cellW, cellH);
            Vector delta = toRect.TopLeft - fromRect.TopLeft;

            _slots[toSlot] = dragged;
            _slots[fromSlot] = neighbor;

            PlaceInSlot(dragged.Container, toSlot);
            PlaceInSlot(neighbor.Container, fromSlot);

            // Anker der gezogenen Kachel exakt kompensieren, damit sie ohne Sprung unter dem Cursor bleibt
            _dragAnchorInBento += delta;
            dragged.DragTranslate.X -= delta.X;
            dragged.DragTranslate.Y -= delta.Y;

            // Verdrängte Nachbarkachel sanft von der aktuell animierten Position in die neue Position gleiten lassen
            double curX = neighbor.DragTranslate.X;
            double curY = neighbor.DragTranslate.Y;
            neighbor.DragTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            neighbor.DragTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            double startX = curX + delta.X;
            double startY = curY + delta.Y;
            neighbor.DragTranslate.X = startX;
            neighbor.DragTranslate.Y = startY;

            if (_animationsEnabled())
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
                var animX = new DoubleAnimation(startX, 0, TimeSpan.FromMilliseconds(270)) { EasingFunction = ease };
                var animY = new DoubleAnimation(startY, 0, TimeSpan.FromMilliseconds(270)) { EasingFunction = ease };
                int fps = _targetFrameRate();
                Timeline.SetDesiredFrameRate(animX, fps);
                Timeline.SetDesiredFrameRate(animY, fps);
                // Basiswert auf 0 setzen, damit ein späteres BeginAnimation(..., null) nie auf startX/startY zurückspringt
                neighbor.DragTranslate.X = 0;
                neighbor.DragTranslate.Y = 0;
                neighbor.DragTranslate.BeginAnimation(TranslateTransform.XProperty, animX);
                neighbor.DragTranslate.BeginAnimation(TranslateTransform.YProperty, animY);
            }
            else
            {
                neighbor.DragTranslate.X = 0;
                neighbor.DragTranslate.Y = 0;
            }

            _dragSlot = toSlot;
        }

        private void StartJiggleAnimations()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                var entry = _slots[i];
                if (entry == null) continue;
                StartTileJiggle(entry, i);
            }
        }

        private void StartTileJiggle(TileEntry entry, int index, int extraDelayMs = 0)
            => Core.JiggleMotionHelper.StartCardTileJiggle(
                entry.JiggleRotate,
                entry.JiggleTranslate,
                entry.DragScale,
                index,
                _animationsEnabled(),
                _targetFrameRate(),
                extraDelayMs,
                disabledScale: 0.985);

        private void PauseTileJiggle(TileEntry entry)
            => Core.JiggleMotionHelper.PauseJiggle(
                entry.JiggleRotate,
                entry.JiggleTranslate,
                _animationsEnabled(),
                _targetFrameRate(),
                resetDurationMs: 90);

        private void StopJiggleAnimations(bool playCommitPulse = false)
        {
            bool anim = _animationsEnabled();
            int fps = _targetFrameRate();

            foreach (var entry in _slots)
            {
                if (entry == null) continue;
                Core.JiggleMotionHelper.StopJiggle(
                    entry.JiggleRotate,
                    entry.JiggleTranslate,
                    entry.DragScale,
                    playCommitPulse,
                    anim,
                    fps);
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

        private static bool IsInsideShopDrawer(DependencyObject? source)
            => Core.JiggleMotionHelper.IsInsideVisualTree(source, node =>
                node is FrameworkElement fe && Equals(fe.Tag, "BentoStoreDrawer"));

        private static bool IsCarouselNavControl(DependencyObject? source)
            => Core.JiggleMotionHelper.IsInsideVisualTree(source, node =>
                node is FrameworkElement fe && Equals(fe.Tag, "CarouselNavControl"));

        private static bool IsInteractiveButton(DependencyObject? source)
            => Core.JiggleMotionHelper.IsInsideVisualTree(source, node =>
                node is System.Windows.Controls.Primitives.ButtonBase);

        public void BeginShopTileDrag(Border sourceBorder, string tileId, MouseButtonEventArgs e)
        {
            if (!_isJiggleMode || _isDragging || _isShopDragging) return;
            e.Handled = true;

            _isShopDragging = true;
            _shopPassedThreshold = false;
            _shopSourceBorder = sourceBorder;
            _shopSourceOriginalBg = sourceBorder.Background;
            _shopSourceOriginalChild = sourceBorder.Child;
            _shopDragTileId = tileId;
            _shopHoverSlot = -1;
            _shopDragMaxDistance = 0;
            _shopDragStartInRoot = e.GetPosition(_rootHost);
            _shopTargetInRoot = _shopDragStartInRoot;

            var posInRoot = sourceBorder.TranslatePoint(new Point(0, 0), _rootHost);
            double w = sourceBorder.ActualWidth > 10 ? sourceBorder.ActualWidth : 120;
            double h = sourceBorder.ActualHeight > 10 ? sourceBorder.ActualHeight : 40;

            var previewScale = new ScaleTransform(1.0, 1.0);
            var previewTranslate = new TranslateTransform(0, 0);
            _shopPreviewScale = previewScale;
            _shopPreviewTranslate = previewTranslate;

            var preview = new Border
            {
                Width = w,
                Height = h,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(posInRoot.X, posInRoot.Y, 0, 0),
                Background = _shopSourceOriginalBg,
                CornerRadius = sourceBorder.CornerRadius,
                Padding = sourceBorder.Padding,
                Opacity = 0.96,
                IsHitTestVisible = false,
                RenderTransform = new TransformGroup
                {
                    Children = { previewScale, previewTranslate }
                },
                RenderTransformOrigin = new Point(0.5, 0.5)
            };

            sourceBorder.Child = null;
            sourceBorder.Width = w;
            sourceBorder.Height = h;
            sourceBorder.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
            preview.Child = _shopSourceOriginalChild;

            Grid.SetRowSpan(preview, Math.Max(1, _rootHost.RowDefinitions.Count));
            Panel.SetZIndex(preview, 220);
            _shopPreview = preview;
            _rootHost.Children.Add(preview);

            Mouse.Capture(_rootHost, CaptureMode.SubTree);
            _frameClock.Restart();
            _lastFrameTime = TimeSpan.Zero;
            HookRendering();
        }

        private void RootHost_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isShopDragging) return;
            _shopTargetInRoot = e.GetPosition(_rootHost);
            _shopDragMaxDistance = Math.Max(_shopDragMaxDistance, (_shopTargetInRoot - _shopDragStartInRoot).Length);

            if (!_shopPassedThreshold && _shopDragMaxDistance >= StoreDragThreshold)
            {
                _shopPassedThreshold = true;
                if (_shopPreviewScale != null && _animationsEnabled())
                {
                    var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                    var anim = new DoubleAnimation(1.04, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
                    Timeline.SetDesiredFrameRate(anim, _targetFrameRate());
                    _shopPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                    _shopPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                }
                else if (_shopPreviewScale != null)
                {
                    _shopPreviewScale.ScaleX = 1.04;
                    _shopPreviewScale.ScaleY = 1.04;
                }
            }
        }

        private void RootHost_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isShopDragging) return;
            e.Handled = true;
            StopShopDragInternal(commit: _shopPassedThreshold);
        }

        private void RootHost_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_isShopDragging && !ReferenceEquals(Mouse.Captured, _rootHost))
                StopShopDragInternal(commit: false);
        }

        private void StopShopDragInternal(bool commit)
        {
            if (!_isShopDragging) return;
            _isShopDragging = false;
            _shopPassedThreshold = false;
            UnhookRendering();

            if (ReferenceEquals(Mouse.Captured, _rootHost))
                Mouse.Capture(null);

            if (_shopPreview != null)
            {
                _shopPreview.Child = null;
                _rootHost.Children.Remove(_shopPreview);
                _shopPreview = null;
            }
            if (_shopSourceBorder != null)
            {
                _shopSourceBorder.Background = _shopSourceOriginalBg;
                _shopSourceBorder.Child = _shopSourceOriginalChild;
                _shopSourceBorder.ClearValue(FrameworkElement.WidthProperty);
                _shopSourceBorder.ClearValue(FrameworkElement.HeightProperty);
                _shopSourceBorder = null;
            }
            _shopSourceOriginalBg = null;
            _shopSourceOriginalChild = null;
            _shopPreviewScale = null;
            _shopPreviewTranslate = null;

            int dropSlot = _shopHoverSlot;
            string tileId = _shopDragTileId;
            _shopHoverSlot = -1;
            _shopDragTileId = string.Empty;

            // Hover-Highlight auf Slots zurücksetzen
            foreach (var s in _slots)
            {
                if (s != null)
                    s.Container.Opacity = 1.0;
            }

            if (commit && dropSlot >= 0 && !string.IsNullOrEmpty(tileId))
                _onCommitShopDrop?.Invoke(tileId, dropSlot);
        }

        private bool IsInsideAnyTile(DependencyObject? source)
            => Core.JiggleMotionHelper.IsInsideVisualTree(source, node =>
            {
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (ReferenceEquals(node, _slots[i]?.Container))
                        return true;
                }
                return false;
            });

        private static Rect GetSlotRect(int slot, double cellW, double cellH)
        {
            int col = slot % 2;
            int row = slot / 2;
            double x = col == 0 ? 0 : cellW + GridGap;
            double y = row == 0 ? 0 : cellH + GridGap;
            return new Rect(x, y, cellW, cellH);
        }

        private static void PlaceInSlot(UIElement el, int slot)
        {
            Grid.SetRow(el, (slot / 2) * 2);
            Grid.SetColumn(el, (slot % 2) * 2);
        }

        private static bool IsTextInputOrScroll(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is TextBoxBase or ScrollBar or Thumb)
                    return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private static bool IsInteractiveScrubber(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is FrameworkElement fe && Equals(fe.Tag, "InteractiveScrubber"))
                    return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }
    }
}
