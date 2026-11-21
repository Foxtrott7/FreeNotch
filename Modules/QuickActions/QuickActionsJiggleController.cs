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
using WindowsNotch.Services;

namespace WindowsNotch.Modules.QuickActions
{
    /// <summary>
    /// Einheitlicher Drag-, Jiggle- und Overlay-Controller für das Control Center:
    /// - Gemeinsamer Overlay-Canvas über allem (kein Clipping, sauberes Schweben obendrüber)
    /// - Freie Grid-Slots (inkl. leerer Slots in angebrochenen Reihen)
    /// - Einzeln ziehbare System-Leisten (oben/unten/Entfernen)
    /// - Nahtloser Shop -> Raster Übergang mit Schwellen-Widerstand und automatischer neuer Reihe
    /// </summary>
    internal sealed class QuickActionsJiggleController
    {
        private const int LongPressMs = 450;
        private const double MoveThreshold = 6.0;
        private const double DragFollowRate = 20.0;
        private const double SwapCooldownMs = 190.0;

        private sealed class TileEntry
        {
            public string Id { get; set; } = string.Empty;
            public required Border Wrapper { get; init; }
            public UIElement? InnerControl { get; set; }
            public required ScaleTransform DragScale { get; init; }
            public required RotateTransform JiggleRotate { get; init; }
            public required TranslateTransform JiggleTranslate { get; init; }
            public required TranslateTransform SwapTranslate { get; init; }
        }

        private sealed class BarEntry
        {
            public required string Id { get; init; }
            public required Border Wrapper { get; init; }
            public required UIElement InnerControl { get; init; }
            public required bool IsTop { get; init; }
            public required ScaleTransform Scale { get; init; }
            public required RotateTransform JiggleRotate { get; init; }
        }

        private enum DragKind { None, ActiveTile, ActiveBar, StoreTile, StoreBar }

        private readonly Grid _overlayRoot;
        private readonly StackPanel _contentStack;
        private readonly Canvas _dragOverlayCanvas;
        private readonly Grid _tilesGrid;
        private readonly StackPanel _topBarsHost;
        private readonly StackPanel _bottomBarsHost;
        private readonly List<TileEntry> _tiles = new();
        private readonly List<BarEntry> _bars = new();

        private readonly Action<IReadOnlyList<string>> _onTileSlotsChanged;
        private readonly Action<IReadOnlyList<string>, IReadOnlyList<string>> _onBarsChanged;
        private readonly Action<string, bool> _onRemoveItem;
        private readonly Action<string, bool, int, bool, int> _onCommitStoreDrop;
        private readonly Action<bool> _onExtraRowChanged;
        private readonly Action _onEnterJiggle;
        private readonly Action _onExitJiggle;

        private readonly DispatcherTimer _holdTimer;
        private readonly Stopwatch _frameClock = new();
        private Window? _hostWindow;
        private bool _isAttached;
        private bool _isRenderingHooked;
        private TimeSpan _lastFrameTime;
        private TimeSpan _lastSwapTime;

        private bool _isJiggleMode;
        private TileEntry? _pendingTile;
        private BarEntry? _pendingBar;
        private Point _pressPointInOverlay;

        private DragKind _dragKind = DragKind.None;
        private Border? _floatingHost;
        private ScaleTransform? _floatingScale;
        private Point _floatingOriginInOverlay;
        private Point _dragStartPointerInOverlay;
        private Point _targetPointerInOverlay;
        private double _floatingCurrentX;
        private double _floatingCurrentY;
        private double _dragDampingBlend = 1.0;
        private double _dragMaxDistance;
        private bool _dragStartedFromExistingJiggle;
        private bool _swappedDuringDrag;
        private bool _isOverRemoveZone;

        // Active tile drag
        private TileEntry? _dragTile;
        private int _dragSlot = -1;

        // Active bar drag
        private BarEntry? _dragBar;
        private bool _barTargetTop;
        private int _barTargetInsertIndex;
        private Border? _barInsertIndicator;

        // Store drag
        private Border? _storeSourceBorder;
        private UIElement? _storeSourceChild;
        private Brush? _storeSourceBg;
        private Thickness _storeSourcePadding;
        private CornerRadius _storeSourceRadius;
        private string _storeItemId = string.Empty;
        private bool _storePassedThreshold;
        private bool _tempExtraRowAdded;
        private int _storeHoverSlot = -1;

        public bool IsJiggleMode => _isJiggleMode;

        public QuickActionsJiggleController(
            Grid overlayRoot,
            StackPanel contentStack,
            Canvas dragOverlayCanvas,
            Grid tilesGrid,
            StackPanel topBarsHost,
            StackPanel bottomBarsHost,
            IReadOnlyList<(string Id, Border Wrapper, UIElement? InnerControl)> tiles,
            IReadOnlyList<(string Id, Border Wrapper, UIElement InnerControl, bool IsTop)> bars,
            Action<IReadOnlyList<string>> onTileSlotsChanged,
            Action<IReadOnlyList<string>, IReadOnlyList<string>> onBarsChanged,
            Action<string, bool> onRemoveItem,
            Action<string, bool, int, bool, int> onCommitStoreDrop,
            Action<bool> onExtraRowChanged,
            Action onEnterJiggle,
            Action onExitJiggle)
        {
            _overlayRoot = overlayRoot;
            _contentStack = contentStack;
            _dragOverlayCanvas = dragOverlayCanvas;
            _tilesGrid = tilesGrid;
            _topBarsHost = topBarsHost;
            _bottomBarsHost = bottomBarsHost;
            _onTileSlotsChanged = onTileSlotsChanged;
            _onBarsChanged = onBarsChanged;
            _onRemoveItem = onRemoveItem;
            _onCommitStoreDrop = onCommitStoreDrop;
            _onExtraRowChanged = onExtraRowChanged;
            _onEnterJiggle = onEnterJiggle;
            _onExitJiggle = onExitJiggle;

            _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LongPressMs) };
            _holdTimer.Tick += HoldTimer_Tick;

            foreach (var (id, wrapper, inner) in tiles)
                _tiles.Add(CreateTileEntry(id, wrapper, inner));

            foreach (var (id, wrapper, inner, isTop) in bars)
            {
                var sc = new ScaleTransform(1, 1);
                var rot = new RotateTransform(0);
                var tg = new TransformGroup();
                tg.Children.Add(sc);
                tg.Children.Add(rot);
                wrapper.RenderTransformOrigin = new Point(0.5, 0.5);
                wrapper.RenderTransform = tg;

                _bars.Add(new BarEntry
                {
                    Id = id,
                    Wrapper = wrapper,
                    InnerControl = inner,
                    IsTop = isTop,
                    Scale = sc,
                    JiggleRotate = rot
                });
            }

            Attach();
        }

        private static TileEntry CreateTileEntry(string id, Border wrapper, UIElement? inner)
        {
            var sc = new ScaleTransform(1, 1);
            var rot = new RotateTransform(0);
            var jtr = new TranslateTransform(0, 0);
            var str = new TranslateTransform(0, 0);
            var tg = new TransformGroup();
            tg.Children.Add(sc);
            tg.Children.Add(rot);
            tg.Children.Add(jtr);
            tg.Children.Add(str);

            wrapper.RenderTransformOrigin = new Point(0.5, 0.5);
            wrapper.RenderTransform = tg;

            return new TileEntry
            {
                Id = id,
                Wrapper = wrapper,
                InnerControl = inner,
                DragScale = sc,
                JiggleRotate = rot,
                JiggleTranslate = jtr,
                SwapTranslate = str
            };
        }

        private void Attach()
        {
            if (_isAttached) return;
            _isAttached = true;

            foreach (var entry in _tiles)
            {
                if (entry.InnerControl != null)
                    entry.Wrapper.PreviewMouseLeftButtonDown += Tile_PreviewMouseLeftButtonDown;
            }

            foreach (var bar in _bars)
                bar.Wrapper.PreviewMouseLeftButtonDown += Bar_PreviewMouseLeftButtonDown;

            _overlayRoot.PreviewMouseMove += Overlay_PreviewMouseMove;
            _overlayRoot.PreviewMouseLeftButtonUp += Overlay_PreviewMouseLeftButtonUp;
            _overlayRoot.LostMouseCapture += Overlay_LostMouseCapture;
            _overlayRoot.Loaded += Root_Loaded;
            HookHostWindow();
        }

        private void Root_Loaded(object sender, RoutedEventArgs e) => HookHostWindow();

        private void HookHostWindow()
        {
            var win = Window.GetWindow(_overlayRoot);
            if (win == null || ReferenceEquals(_hostWindow, win)) return;
            UnhookHostWindow();
            _hostWindow = win;
            _hostWindow.PreviewMouseLeftButtonDown += HostWindow_PreviewMouseLeftButtonDown;
        }

        private void UnhookHostWindow()
        {
            if (_hostWindow == null) return;
            _hostWindow.PreviewMouseLeftButtonDown -= HostWindow_PreviewMouseLeftButtonDown;
            _hostWindow = null;
        }

        public void Detach()
        {
            if (!_isAttached) return;
            _isAttached = false;

            _holdTimer.Stop();
            _pendingTile = null;
            _pendingBar = null;
            AbortActiveDrag();
            StopRenderingLoop();
            UnhookHostWindow();

            foreach (var entry in _tiles)
                entry.Wrapper.PreviewMouseLeftButtonDown -= Tile_PreviewMouseLeftButtonDown;
            foreach (var bar in _bars)
                bar.Wrapper.PreviewMouseLeftButtonDown -= Bar_PreviewMouseLeftButtonDown;

            _overlayRoot.PreviewMouseMove -= Overlay_PreviewMouseMove;
            _overlayRoot.PreviewMouseLeftButtonUp -= Overlay_PreviewMouseLeftButtonUp;
            _overlayRoot.LostMouseCapture -= Overlay_LostMouseCapture;
            _overlayRoot.Loaded -= Root_Loaded;
        }

        public void SyncJiggleState(bool active)
        {
            _isJiggleMode = active;
            SetInnerControlsHitTestable(!active);
            UpdateEmptySlotVisuals(active);
            if (active)
            {
                HookHostWindow();
                StartJiggleAnimations();
            }
            else
            {
                StopJiggleAnimations(playCommitPulse: false);
            }
        }

        public void EnterJiggleMode()
        {
            if (_isJiggleMode) return;
            _isJiggleMode = true;
            SetInnerControlsHitTestable(false);
            UpdateEmptySlotVisuals(true);
            HookHostWindow();
            StartJiggleAnimations();
            _onEnterJiggle();
        }

        public bool ExitJiggleMode()
        {
            if (!_isJiggleMode) return false;
            _holdTimer.Stop();
            AbortActiveDrag();
            _isJiggleMode = false;
            SetInnerControlsHitTestable(true);
            UpdateEmptySlotVisuals(false);
            StopJiggleAnimations(playCommitPulse: true);
            _onExitJiggle();
            return true;
        }

        public bool HandleEscape() => ExitJiggleMode();

        private void SetInnerControlsHitTestable(bool enabled)
        {
            foreach (var entry in _tiles)
            {
                if (entry.InnerControl != null)
                    entry.InnerControl.IsHitTestVisible = enabled;
            }
            foreach (var bar in _bars)
                bar.InnerControl.IsHitTestVisible = enabled;
        }

        private void UpdateEmptySlotVisuals(bool visible, int highlightSlot = -1)
        {
            for (int i = 0; i < _tiles.Count; i++)
            {
                var entry = _tiles[i];
                bool isEmptySlot = string.IsNullOrEmpty(entry.Id) || (ReferenceEquals(entry, _dragTile) && _dragKind == DragKind.ActiveTile);
                if (!isEmptySlot)
                {
                    if (visible && i == highlightSlot)
                    {
                        entry.Wrapper.Background = Brushes.Transparent;
                        entry.Wrapper.BorderBrush = NotchDesign.CreateSoftAccentTileBrush(0x70);
                        entry.Wrapper.BorderThickness = new Thickness(1);
                    }
                    else
                    {
                        entry.Wrapper.Background = Brushes.Transparent;
                        entry.Wrapper.BorderBrush = Brushes.Transparent;
                        entry.Wrapper.BorderThickness = new Thickness(0);
                    }
                    continue;
                }

                if (!visible)
                {
                    entry.Wrapper.Background = Brushes.Transparent;
                    entry.Wrapper.BorderBrush = Brushes.Transparent;
                    entry.Wrapper.BorderThickness = new Thickness(0);
                }
                else if (i == highlightSlot)
                {
                    entry.Wrapper.Background = NotchDesign.CreateSoftAccentTileBrush(0x28);
                    entry.Wrapper.BorderBrush = NotchDesign.CreateSoftAccentTileBrush(0x70);
                    entry.Wrapper.BorderThickness = new Thickness(1);
                }
                else
                {
                    entry.Wrapper.Background = new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF));
                    entry.Wrapper.BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
                    entry.Wrapper.BorderThickness = new Thickness(1);
                }
            }
        }

        private void HostWindow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isJiggleMode || _dragKind != DragKind.None) return;

            if (e.OriginalSource is DependencyObject src)
            {
                if (IsInsideAnyActiveItem(src) || IsInsideStore(src))
                    return;
            }
            ExitJiggleMode();
        }

        private bool IsInsideAnyActiveItem(DependencyObject src)
        {
            foreach (var t in _tiles)
            {
                if (t.InnerControl != null && IsDescendantOf(src, t.Wrapper)) return true;
            }
            foreach (var b in _bars)
            {
                if (IsDescendantOf(src, b.Wrapper)) return true;
            }
            return false;
        }

        private static bool IsInsideStore(DependencyObject src)
        {
            var cur = src;
            while (cur != null)
            {
                if (cur is FrameworkElement fe && Equals(fe.Tag, "QuickActionsStoreDrawer"))
                    return true;
                cur = VisualTreeHelper.GetParent(cur);
            }
            return false;
        }

        private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
        {
            while (node != null)
            {
                if (ReferenceEquals(node, ancestor)) return true;
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        private void Tile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border wrapper) return;
            var entry = _tiles.FirstOrDefault(t => ReferenceEquals(t.Wrapper, wrapper));
            if (entry == null || entry.InnerControl == null) return;

            var pt = e.GetPosition(_overlayRoot);
            _pendingBar = null;

            if (_isJiggleMode)
            {
                e.Handled = true;
                BeginActiveTileDrag(entry, pt, fromExistingJiggle: true);
                return;
            }

            _pendingTile = entry;
            _pressPointInOverlay = pt;
            _holdTimer.Stop();
            _holdTimer.Start();
        }

        private void Bar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border wrapper) return;
            var bar = _bars.FirstOrDefault(b => ReferenceEquals(b.Wrapper, wrapper));
            if (bar == null) return;

            var pt = e.GetPosition(_overlayRoot);
            _pendingTile = null;

            if (_isJiggleMode)
            {
                e.Handled = true;
                BeginActiveBarDrag(bar, pt, fromExistingJiggle: true);
                return;
            }

            if (IsInteractiveSliderArea(e.OriginalSource as DependencyObject))
                return;

            _pendingBar = bar;
            _pressPointInOverlay = pt;
            _holdTimer.Stop();
            _holdTimer.Start();
        }

        private static bool IsInteractiveSliderArea(DependencyObject? src)
        {
            var cur = src;
            while (cur != null)
            {
                if (cur is FrameworkElement fe && Equals(fe.Tag, "VolumeSliderHitArea"))
                    return true;
                cur = VisualTreeHelper.GetParent(cur);
            }
            return false;
        }

        private void HoldTimer_Tick(object? sender, EventArgs e)
        {
            _holdTimer.Stop();
            if (Mouse.LeftButton != MouseButtonState.Pressed)
            {
                _pendingTile = null;
                _pendingBar = null;
                return;
            }

            if (_pendingTile != null)
            {
                var target = _pendingTile;
                _pendingTile = null;
                EnterJiggleMode();
                BeginActiveTileDrag(target, _pressPointInOverlay, fromExistingJiggle: false);
            }
            else if (_pendingBar != null)
            {
                var target = _pendingBar;
                _pendingBar = null;
                EnterJiggleMode();
                BeginActiveBarDrag(target, _pressPointInOverlay, fromExistingJiggle: false);
            }
        }

        public void BeginStoreItemDrag(Border sourceBorder, string id, bool isBar, MouseButtonEventArgs e)
        {
            if (_dragKind != DragKind.None) return;
            var pt = e.GetPosition(_overlayRoot);

            _dragKind = isBar ? DragKind.StoreBar : DragKind.StoreTile;
            _storeSourceBorder = sourceBorder;
            _storeSourceChild = sourceBorder.Child;
            _storeSourceBg = sourceBorder.Background;
            _storeSourcePadding = sourceBorder.Padding;
            _storeSourceRadius = sourceBorder.CornerRadius;
            _storeItemId = id;
            _storePassedThreshold = false;
            _tempExtraRowAdded = false;
            _storeHoverSlot = -1;
            _dragMaxDistance = 0;

            var origin = sourceBorder.TranslatePoint(new Point(0, 0), _overlayRoot);
            double w = sourceBorder.ActualWidth;
            double h = sourceBorder.ActualHeight;

            // Quelle als dezenter Platzhalter im Shop belassen
            sourceBorder.Child = null;
            sourceBorder.Width = w;
            sourceBorder.Height = h;
            sourceBorder.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));

            CreateFloatingHost(_storeSourceChild, w, h, origin, _storeSourceBg, _storeSourceRadius, _storeSourcePadding);
            _dragStartPointerInOverlay = pt;
            _targetPointerInOverlay = pt;

            UpdateEmptySlotVisuals(true);
            Mouse.Capture(_overlayRoot, CaptureMode.SubTree);
            _frameClock.Restart();
            _lastFrameTime = TimeSpan.Zero;
            StartRenderingLoop();
        }

        private void BeginActiveTileDrag(TileEntry entry, Point pointerInOverlay, bool fromExistingJiggle)
        {
            int slot = _tiles.IndexOf(entry);
            if (slot < 0 || entry.InnerControl == null) return;

            _dragKind = DragKind.ActiveTile;
            _dragTile = entry;
            _dragSlot = slot;
            _dragStartPointerInOverlay = pointerInOverlay;
            _targetPointerInOverlay = pointerInOverlay;
            _dragMaxDistance = 0;
            _dragStartedFromExistingJiggle = fromExistingJiggle;
            _swappedDuringDrag = false;
            _isOverRemoveZone = false;
            _lastSwapTime = TimeSpan.FromMilliseconds(-SwapCooldownMs);

            PauseTileJiggle(entry);
            var origin = entry.Wrapper.TranslatePoint(new Point(0, 0), _overlayRoot);
            double w = entry.Wrapper.ActualWidth;
            double h = entry.Wrapper.ActualHeight;

            var inner = entry.InnerControl;
            entry.Wrapper.Child = null;
            entry.Wrapper.Width = w;
            entry.Wrapper.Height = h;

            CreateFloatingHost(inner, w, h, origin, Brushes.Transparent, new CornerRadius(NotchDesign.RadiusControl), new Thickness(0));
            AnimateFloatingScale(1.04, 160);
            UpdateEmptySlotVisuals(true);

            Mouse.Capture(_overlayRoot, CaptureMode.SubTree);
            _frameClock.Restart();
            _lastFrameTime = TimeSpan.Zero;
            StartRenderingLoop();
        }

        private void BeginActiveBarDrag(BarEntry bar, Point pointerInOverlay, bool fromExistingJiggle)
        {
            _dragKind = DragKind.ActiveBar;
            _dragBar = bar;
            _dragStartPointerInOverlay = pointerInOverlay;
            _targetPointerInOverlay = pointerInOverlay;
            _dragMaxDistance = 0;
            _dragStartedFromExistingJiggle = fromExistingJiggle;
            _isOverRemoveZone = false;
            _barTargetTop = bar.IsTop;
            _barTargetInsertIndex = 0;

            var origin = bar.Wrapper.TranslatePoint(new Point(0, 0), _overlayRoot);
            double w = bar.Wrapper.ActualWidth;
            double h = bar.Wrapper.ActualHeight;

            bar.Wrapper.Child = null;
            bar.Wrapper.Width = w;
            bar.Wrapper.Height = h;
            bar.Wrapper.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
            bar.Wrapper.CornerRadius = new CornerRadius(NotchDesign.RadiusInner);

            CreateFloatingHost(bar.InnerControl, w, h, origin, Brushes.Transparent, new CornerRadius(NotchDesign.RadiusInner), new Thickness(0));
            AnimateFloatingScale(1.02, 160);

            Mouse.Capture(_overlayRoot, CaptureMode.SubTree);
            _frameClock.Restart();
            _lastFrameTime = TimeSpan.Zero;
            StartRenderingLoop();
        }

        private void CreateFloatingHost(
            UIElement? child,
            double width,
            double height,
            Point origin,
            Brush? bg,
            CornerRadius radius,
            Thickness padding)
        {
            _floatingScale = new ScaleTransform(1, 1);
            _floatingHost = new Border
            {
                Width = width,
                Height = height,
                Background = bg ?? Brushes.Transparent,
                CornerRadius = radius,
                Padding = padding,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _floatingScale,
                Opacity = 0.96,
                Child = child
            };
            _floatingOriginInOverlay = origin;
            _floatingCurrentX = origin.X;
            _floatingCurrentY = origin.Y;
            _dragDampingBlend = (_dragKind == DragKind.StoreTile || _dragKind == DragKind.StoreBar) ? 0.46 : 1.0;
            Canvas.SetLeft(_floatingHost, _floatingCurrentX);
            Canvas.SetTop(_floatingHost, _floatingCurrentY);
            _dragOverlayCanvas.Children.Add(_floatingHost);
        }

        private void AnimateFloatingScale(double target, int ms)
        {
            if (_floatingScale == null) return;
            if (!NotchDesign.EffectiveAnimationsEnabled)
            {
                _floatingScale.ScaleX = target;
                _floatingScale.ScaleY = target;
                return;
            }
            var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(ms)) { EasingFunction = NotchDesign.CreateEaseOut() };
            Timeline.SetDesiredFrameRate(anim, SettingsService.Current.TargetFrameRate);
            _floatingScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            _floatingScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        private void Overlay_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            var pt = e.GetPosition(_overlayRoot);

            if (_holdTimer.IsEnabled && (_pendingTile != null || _pendingBar != null))
            {
                if ((pt - _pressPointInOverlay).Length > MoveThreshold)
                {
                    _holdTimer.Stop();
                    _pendingTile = null;
                    _pendingBar = null;
                }
                return;
            }

            if (_dragKind != DragKind.None)
            {
                _targetPointerInOverlay = pt;
                _dragMaxDistance = Math.Max(_dragMaxDistance, (pt - _dragStartPointerInOverlay).Length);
            }
        }

        private void Overlay_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _holdTimer.Stop();
            _pendingTile = null;
            _pendingBar = null;

            if (_dragKind == DragKind.None) return;
            e.Handled = true;
            CompleteActiveDrag();
        }

        private void Overlay_LostMouseCapture(object sender, MouseEventArgs e)
        {
            _holdTimer.Stop();
            _pendingTile = null;
            _pendingBar = null;

            if (_dragKind != DragKind.None && !ReferenceEquals(Mouse.Captured, _overlayRoot))
                CompleteActiveDrag();
        }

        private void StartRenderingLoop()
        {
            if (_isRenderingHooked) return;
            _isRenderingHooked = true;
            CompositionTarget.Rendering += OnCompositionRendering;
        }

        private void StopRenderingLoop()
        {
            if (!_isRenderingHooked) return;
            _isRenderingHooked = false;
            CompositionTarget.Rendering -= OnCompositionRendering;
        }

        private double GetShopTopInOverlay()
        {
            var shop = _contentStack.Children.OfType<FrameworkElement>()
                .FirstOrDefault(c => Equals(c.Tag, "QuickActionsStoreDrawer"));
            if (shop != null && shop.IsLoaded)
                return shop.TranslatePoint(new Point(0, 0), _overlayRoot).Y;
            return _tilesGrid.TranslatePoint(new Point(0, _tilesGrid.ActualHeight), _overlayRoot).Y + 24;
        }

        private void OnCompositionRendering(object? sender, EventArgs e)
        {
            if (_dragKind == DragKind.None || _floatingHost == null) return;

            var now = _frameClock.Elapsed;
            double dt = (now - _lastFrameTime).TotalSeconds;
            _lastFrameTime = now;
            if (dt <= 0.0005) return;
            dt = Math.Min(dt, 0.05);

            double shopTop = GetShopTopInOverlay();
            double rawDx = _targetPointerInOverlay.X - _dragStartPointerInOverlay.X;
            double rawDy = _targetPointerInOverlay.Y - _dragStartPointerInOverlay.Y;
            double effectiveDx = rawDx;
            double effectiveDy = rawDy;

            if (_dragKind == DragKind.ActiveTile || _dragKind == DragKind.ActiveBar)
            {
                bool canRemove = _dragKind == DragKind.ActiveBar || _tiles.Count(t => !string.IsNullOrEmpty(t.Id)) > 1;
                double removeY = shopTop + 8;
                bool overRemove = canRemove && _targetPointerInOverlay.Y > removeY;
                if (overRemove != _isOverRemoveZone)
                {
                    _isOverRemoveZone = overRemove;
                    _floatingHost.Opacity = overRemove ? 0.66 : 0.96;
                    AnimateFloatingScale(overRemove ? 0.88 : 1.04, 140);
                }
                if (overRemove)
                {
                    // Stetige Dämpfung ab der Shop-Grenze ohne Rücksprung
                    double thresholdDy = removeY - _dragStartPointerInOverlay.Y;
                    effectiveDy = thresholdDy + (rawDy - thresholdDy) * 0.46;
                }
            }
            else if (_dragKind == DragKind.StoreTile || _dragKind == DragKind.StoreBar)
            {
                bool passed = _targetPointerInOverlay.Y < shopTop - 6;
                if (passed != _storePassedThreshold)
                {
                    _storePassedThreshold = passed;
                    if (passed)
                    {
                        _floatingHost.Background = NotchDesign.CreateSoftAccentTileBrush(0x28);
                        _floatingHost.BorderBrush = NotchDesign.CreateSoftAccentTileBrush(0x70);
                        AnimateFloatingScale(1.04, 150);
                        if (_dragKind == DragKind.StoreTile)
                            EnsureEmptySlotForStoreDrag();
                    }
                    else
                    {
                        _floatingHost.Background = _storeSourceBg ?? NotchDesign.CardBackgroundBrush;
                        _floatingHost.BorderBrush = Brushes.Transparent;
                        AnimateFloatingScale(1.0, 150);
                        _storeHoverSlot = -1;
                        UpdateEmptySlotVisuals(true);
                    }
                }
                double targetBlend = passed ? 1.0 : 0.46;
                _dragDampingBlend += (targetBlend - _dragDampingBlend) * JiggleMotionHelper.ComputeFollowAlpha(14.0, dt);
                effectiveDx = rawDx * _dragDampingBlend;
                effectiveDy = rawDy * _dragDampingBlend;
            }

            double desiredX = _floatingOriginInOverlay.X + effectiveDx;
            double desiredY = _floatingOriginInOverlay.Y + effectiveDy;
            double alpha = JiggleMotionHelper.ComputeFollowAlpha(DragFollowRate, dt);
            _floatingCurrentX += (desiredX - _floatingCurrentX) * alpha;
            _floatingCurrentY += (desiredY - _floatingCurrentY) * alpha;
            Canvas.SetLeft(_floatingHost, _floatingCurrentX);
            Canvas.SetTop(_floatingHost, _floatingCurrentY);

            if (_dragKind == DragKind.ActiveTile && !_isOverRemoveZone)
            {
                if ((now - _lastSwapTime).TotalMilliseconds >= SwapCooldownMs)
                {
                    int targetSlot = FindTargetSlot(_targetPointerInOverlay, _dragSlot);
                    if (targetSlot >= 0 && targetSlot != _dragSlot)
                    {
                        _lastSwapTime = now;
                        SwapActiveSlots(_dragSlot, targetSlot);
                    }
                }
            }
            else if (_dragKind == DragKind.ActiveBar && !_isOverRemoveZone)
            {
                UpdateBarDropPreview(_targetPointerInOverlay);
            }
            else if (_dragKind == DragKind.StoreTile && _storePassedThreshold)
            {
                int slot = FindAnyGridSlot(_targetPointerInOverlay);
                if (slot != _storeHoverSlot)
                {
                    _storeHoverSlot = slot;
                    UpdateEmptySlotVisuals(true, _storeHoverSlot);
                }
            }
            else if (_dragKind == DragKind.StoreBar && _storePassedThreshold)
            {
                UpdateBarDropPreview(_targetPointerInOverlay);
            }
        }

        private void EnsureEmptySlotForStoreDrag()
        {
            if (_tiles.Any(t => string.IsNullOrEmpty(t.Id))) return;
            if (_tempExtraRowAdded) return;
            _tempExtraRowAdded = true;

            int newRow = _tilesGrid.RowDefinitions.Count > 0 ? (_tilesGrid.RowDefinitions.Count + 1) / 2 : 0;
            if (newRow > 0)
                _tilesGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            _tilesGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });

            int gridRow = newRow * 2;
            for (int c = 0; c < 3; c++)
            {
                var wrapper = new Border
                {
                    Background = Brushes.Transparent,
                    CornerRadius = new CornerRadius(NotchDesign.RadiusControl),
                    MinHeight = 44
                };
                Grid.SetRow(wrapper, gridRow);
                Grid.SetColumn(wrapper, c * 2);
                _tilesGrid.Children.Add(wrapper);
                _tiles.Add(CreateTileEntry(string.Empty, wrapper, null));
            }

            UpdateEmptySlotVisuals(true);
            _onExtraRowChanged(true);
        }

        private Rect GetSlotRectInOverlay(int slot)
        {
            if (slot < 0 || slot >= _tiles.Count) return Rect.Empty;
            var w = _tiles[slot].Wrapper;
            var tl = w.TranslatePoint(new Point(-_tiles[slot].SwapTranslate.X, -_tiles[slot].SwapTranslate.Y), _overlayRoot);
            return new Rect(tl.X, tl.Y, w.ActualWidth, w.ActualHeight);
        }

        private int FindTargetSlot(Point pointerInOverlay, int currentSlot)
        {
            var curRect = GetSlotRectInOverlay(currentSlot);
            Point curCenter = new(curRect.X + curRect.Width / 2.0, curRect.Y + curRect.Height / 2.0);
            double distCur = (pointerInOverlay - curCenter).Length;

            for (int i = 0; i < _tiles.Count; i++)
            {
                if (i == currentSlot) continue;
                Rect r = GetSlotRectInOverlay(i);
                double padX = r.Width * 0.14;
                double padY = r.Height * 0.14;
                var inner = new Rect(r.X + padX, r.Y + padY, Math.Max(1, r.Width - 2 * padX), Math.Max(1, r.Height - 2 * padY));
                Point targetCenter = new(r.X + r.Width / 2.0, r.Y + r.Height / 2.0);
                double distTarget = (pointerInOverlay - targetCenter).Length;

                if (inner.Contains(pointerInOverlay) || (r.Contains(pointerInOverlay) && distTarget < distCur * 0.80))
                    return i;
            }
            return currentSlot;
        }

        private int FindAnyGridSlot(Point pointerInOverlay)
        {
            int best = -1;
            double bestDist = double.MaxValue;
            for (int i = 0; i < _tiles.Count; i++)
            {
                Rect r = GetSlotRectInOverlay(i);
                if (r.Contains(pointerInOverlay))
                    return i;
                Point c = new(r.X + r.Width / 2.0, r.Y + r.Height / 2.0);
                double d = (pointerInOverlay - c).Length;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            return best;
        }

        private void SwapActiveSlots(int fromSlot, int toSlot)
        {
            var dragged = _tiles[fromSlot];
            var neighbor = _tiles[toSlot];
            _swappedDuringDrag = true;

            Rect fromRect = GetSlotRectInOverlay(fromSlot);
            Rect toRect = GetSlotRectInOverlay(toSlot);
            Vector delta = toRect.TopLeft - fromRect.TopLeft;

            _tiles[toSlot] = dragged;
            _tiles[fromSlot] = neighbor;

            Grid.SetRow(dragged.Wrapper, (toSlot / 3) * 2);
            Grid.SetColumn(dragged.Wrapper, (toSlot % 3) * 2);
            Grid.SetRow(neighbor.Wrapper, (fromSlot / 3) * 2);
            Grid.SetColumn(neighbor.Wrapper, (fromSlot % 3) * 2);

            if (!string.IsNullOrEmpty(neighbor.Id))
            {
                double curX = neighbor.SwapTranslate.X;
                double curY = neighbor.SwapTranslate.Y;
                neighbor.SwapTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                neighbor.SwapTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                double startX = curX + delta.X;
                double startY = curY + delta.Y;
                neighbor.SwapTranslate.X = startX;
                neighbor.SwapTranslate.Y = startY;

                if (NotchDesign.EffectiveAnimationsEnabled)
                {
                    var ease = NotchDesign.CreateEaseInOut();
                    var animX = new DoubleAnimation(startX, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
                    var animY = new DoubleAnimation(startY, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
                    int fps = SettingsService.Current.TargetFrameRate;
                    Timeline.SetDesiredFrameRate(animX, fps);
                    Timeline.SetDesiredFrameRate(animY, fps);
                    neighbor.SwapTranslate.BeginAnimation(TranslateTransform.XProperty, animX);
                    neighbor.SwapTranslate.BeginAnimation(TranslateTransform.YProperty, animY);
                }
                else
                {
                    neighbor.SwapTranslate.X = 0;
                    neighbor.SwapTranslate.Y = 0;
                }
            }

            _dragSlot = toSlot;
            UpdateEmptySlotVisuals(true);
        }

        private void UpdateBarDropPreview(Point pointerInOverlay)
        {
            var gridTl = _tilesGrid.TranslatePoint(new Point(0, 0), _overlayRoot);
            double gridMidY = gridTl.Y + _tilesGrid.ActualHeight / 2.0;
            bool toTop = pointerInOverlay.Y < gridMidY;

            var host = toTop ? _topBarsHost : _bottomBarsHost;
            int idx = 0;
            foreach (var bar in _bars.Where(b => b.IsTop == toTop && !ReferenceEquals(b, _dragBar)))
            {
                var tl = bar.Wrapper.TranslatePoint(new Point(0, 0), _overlayRoot);
                if (pointerInOverlay.Y > tl.Y + bar.Wrapper.ActualHeight / 2.0)
                    idx++;
            }

            _barTargetTop = toTop;
            _barTargetInsertIndex = idx;

            if (_barInsertIndicator == null)
            {
                _barInsertIndicator = new Border
                {
                    Height = 3,
                    CornerRadius = new CornerRadius(1.5),
                    Background = NotchDesign.AccentPrimaryBrush,
                    Margin = new Thickness(4, 3, 4, 3)
                };
            }

            if (_barInsertIndicator.Parent is Panel oldParent && !ReferenceEquals(oldParent, host))
                oldParent.Children.Remove(_barInsertIndicator);

            int clamped = Math.Clamp(idx, 0, host.Children.Count);
            if (!host.Children.Contains(_barInsertIndicator))
                host.Children.Insert(clamped, _barInsertIndicator);
            else if (host.Children.IndexOf(_barInsertIndicator) != clamped)
            {
                host.Children.Remove(_barInsertIndicator);
                host.Children.Insert(Math.Clamp(clamped, 0, host.Children.Count), _barInsertIndicator);
            }
        }

        private void RemoveBarInsertIndicator()
        {
            if (_barInsertIndicator?.Parent is Panel p)
                p.Children.Remove(_barInsertIndicator);
        }

        private void CompleteActiveDrag()
        {
            StopRenderingLoop();
            if (ReferenceEquals(Mouse.Captured, _overlayRoot))
                Mouse.Capture(null);

            RemoveBarInsertIndicator();
            var kind = _dragKind;
            _dragKind = DragKind.None;

            if (kind == DragKind.ActiveTile && _dragTile != null)
            {
                var entry = _dragTile;
                int slot = _dragSlot;
                bool remove = _isOverRemoveZone;
                bool shouldExitByTap = _dragStartedFromExistingJiggle &&
                                       _dragMaxDistance < MoveThreshold &&
                                       !_swappedDuringDrag &&
                                       !remove;
                _dragTile = null;
                _dragSlot = -1;
                _isOverRemoveZone = false;

                if (remove && _tiles.Count(t => !string.IsNullOrEmpty(t.Id)) > 1)
                {
                    CleanupFloatingHost();
                    _onRemoveItem(entry.Id, false);
                    return;
                }

                var targetRect = GetSlotRectInOverlay(slot);
                var inner = _floatingHost?.Child;
                AnimateFloatingToAndFinish(targetRect.TopLeft, () =>
                {
                    CleanupFloatingHost();
                    entry.Wrapper.Width = double.NaN;
                    entry.Wrapper.Height = double.NaN;
                    entry.Wrapper.Child = inner;
                    UpdateEmptySlotVisuals(_isJiggleMode);
                    if (_isJiggleMode)
                        StartTileJiggle(entry, slot, extraDelayMs: 90);
                    if (shouldExitByTap)
                        ExitJiggleMode();
                });

                _onTileSlotsChanged(_tiles.Select(t => t.Id).ToList());
            }
            else if (kind == DragKind.ActiveBar && _dragBar != null)
            {
                var bar = _dragBar;
                bool remove = _isOverRemoveZone;
                bool toTop = _barTargetTop;
                int insertIdx = _barTargetInsertIndex;
                bool shouldExitByTap = _dragStartedFromExistingJiggle && _dragMaxDistance < MoveThreshold && !remove;
                _dragBar = null;
                _isOverRemoveZone = false;

                if (remove)
                {
                    CleanupFloatingHost();
                    _onRemoveItem(bar.Id, true);
                    return;
                }

                var topList = _bars.Where(b => b.IsTop && !ReferenceEquals(b, bar)).Select(b => b.Id).ToList();
                var bottomList = _bars.Where(b => !b.IsTop && !ReferenceEquals(b, bar)).Select(b => b.Id).ToList();
                var targetList = toTop ? topList : bottomList;
                targetList.Insert(Math.Clamp(insertIdx, 0, targetList.Count), bar.Id);

                CleanupFloatingHost();
                if (shouldExitByTap)
                {
                    bar.Wrapper.Width = double.NaN;
                    bar.Wrapper.Height = double.NaN;
                    bar.Wrapper.Background = Brushes.Transparent;
                    bar.Wrapper.Child = bar.InnerControl;
                    ExitJiggleMode();
                    return;
                }

                _onBarsChanged(topList, bottomList);
            }
            else if ((kind == DragKind.StoreTile || kind == DragKind.StoreBar) && _storeSourceBorder != null)
            {
                string id = _storeItemId;
                bool isBar = kind == DragKind.StoreBar;
                bool passed = _storePassedThreshold;
                int targetSlot = _storeHoverSlot;
                bool toTop = _barTargetTop;
                int insertIdx = _barTargetInsertIndex;

                var srcBorder = _storeSourceBorder;
                var srcChild = _storeSourceChild;
                var srcBg = _storeSourceBg;

                _storeSourceBorder = null;
                _storeSourceChild = null;
                _storeHoverSlot = -1;

                if (!passed || (!isBar && targetSlot < 0))
                {
                    if (_tempExtraRowAdded)
                    {
                        _tempExtraRowAdded = false;
                        _onExtraRowChanged(false);
                    }
                    var origin = srcBorder.TranslatePoint(new Point(0, 0), _overlayRoot);
                    AnimateFloatingToAndFinish(origin, () =>
                    {
                        CleanupFloatingHost();
                        srcBorder.Width = double.NaN;
                        srcBorder.Height = double.NaN;
                        srcBorder.Background = srcBg;
                        srcBorder.Child = srcChild;
                        UpdateEmptySlotVisuals(_isJiggleMode);
                    });
                    return;
                }

                if (isBar)
                {
                    CleanupFloatingHost();
                    _onCommitStoreDrop(id, true, -1, toTop, insertIdx);
                }
                else
                {
                    var destRect = GetSlotRectInOverlay(targetSlot);
                    AnimateFloatingToAndFinish(destRect.TopLeft, () =>
                    {
                        CleanupFloatingHost();
                        _onCommitStoreDrop(id, false, targetSlot, false, 0);
                    });
                }
            }
        }

        private void AnimateFloatingToAndFinish(Point dest, Action onDone)
        {
            if (_floatingHost == null || !NotchDesign.EffectiveAnimationsEnabled)
            {
                onDone();
                return;
            }

            var dur = TimeSpan.FromMilliseconds(220);
            var ease = NotchDesign.CreateEaseOut();
            int fps = SettingsService.Current.TargetFrameRate;

            var ax = new DoubleAnimation(_floatingCurrentX, dest.X, dur) { EasingFunction = ease };
            var ay = new DoubleAnimation(_floatingCurrentY, dest.Y, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(ax, fps);
            Timeline.SetDesiredFrameRate(ay, fps);

            if (_floatingScale != null)
            {
                var asc = new DoubleAnimation(1.0, dur) { EasingFunction = ease };
                Timeline.SetDesiredFrameRate(asc, fps);
                _floatingScale.BeginAnimation(ScaleTransform.ScaleXProperty, asc);
                _floatingScale.BeginAnimation(ScaleTransform.ScaleYProperty, asc);
            }

            ax.Completed += (_, _) => onDone();
            _floatingHost.BeginAnimation(Canvas.LeftProperty, ax);
            _floatingHost.BeginAnimation(Canvas.TopProperty, ay);
        }

        private void CleanupFloatingHost()
        {
            if (_floatingHost != null)
            {
                _floatingHost.BeginAnimation(Canvas.LeftProperty, null);
                _floatingHost.BeginAnimation(Canvas.TopProperty, null);
                _floatingHost.Child = null;
                _dragOverlayCanvas.Children.Remove(_floatingHost);
                _floatingHost = null;
                _floatingScale = null;
            }
        }

        private void AbortActiveDrag()
        {
            StopRenderingLoop();
            RemoveBarInsertIndicator();
            if (ReferenceEquals(Mouse.Captured, _overlayRoot))
                Mouse.Capture(null);

            if (_dragKind == DragKind.ActiveTile && _dragTile != null)
            {
                _dragTile.Wrapper.Width = double.NaN;
                _dragTile.Wrapper.Height = double.NaN;
                if (_floatingHost?.Child != null)
                    _dragTile.Wrapper.Child = _floatingHost.Child;
            }
            else if (_dragKind == DragKind.ActiveBar && _dragBar != null)
            {
                _dragBar.Wrapper.Width = double.NaN;
                _dragBar.Wrapper.Height = double.NaN;
                _dragBar.Wrapper.Background = Brushes.Transparent;
                if (_floatingHost?.Child != null)
                    _dragBar.Wrapper.Child = _floatingHost.Child;
            }
            else if ((_dragKind == DragKind.StoreTile || _dragKind == DragKind.StoreBar) && _storeSourceBorder != null)
            {
                _storeSourceBorder.Width = double.NaN;
                _storeSourceBorder.Height = double.NaN;
                _storeSourceBorder.Background = _storeSourceBg;
                if (_storeSourceChild != null)
                    _storeSourceBorder.Child = _storeSourceChild;
            }

            CleanupFloatingHost();
            _dragKind = DragKind.None;
            _dragTile = null;
            _dragBar = null;
            _storeSourceBorder = null;
            _storeSourceChild = null;
        }

        private void StartJiggleAnimations()
        {
            for (int i = 0; i < _tiles.Count; i++)
            {
                if (_tiles[i].InnerControl != null)
                    StartTileJiggle(_tiles[i], i);
            }

            if (NotchDesign.EffectiveAnimationsEnabled)
            {
                int fps = SettingsService.Current.TargetFrameRate;
                foreach (var bar in _bars)
                {
                    var barRot = new DoubleAnimation(-0.20, 0.20, TimeSpan.FromMilliseconds(210))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };
                    Timeline.SetDesiredFrameRate(barRot, fps);
                    bar.JiggleRotate.BeginAnimation(RotateTransform.AngleProperty, barRot);
                }
            }
        }

        private static void StartTileJiggle(TileEntry tile, int index, int extraDelayMs = 0)
            => JiggleMotionHelper.StartCardTileJiggle(
                tile.JiggleRotate,
                tile.JiggleTranslate,
                tile.DragScale,
                index,
                NotchDesign.EffectiveAnimationsEnabled,
                SettingsService.Current.TargetFrameRate,
                extraDelayMs);

        private static void PauseTileJiggle(TileEntry entry)
            => JiggleMotionHelper.PauseJiggle(
                entry.JiggleRotate,
                entry.JiggleTranslate,
                NotchDesign.EffectiveAnimationsEnabled,
                SettingsService.Current.TargetFrameRate,
                resetDurationMs: 120);

        private void StopJiggleAnimations(bool playCommitPulse)
        {
            bool anim = NotchDesign.EffectiveAnimationsEnabled;
            int fps = SettingsService.Current.TargetFrameRate;

            foreach (var tile in _tiles)
            {
                JiggleMotionHelper.StopJiggle(
                    tile.JiggleRotate,
                    tile.JiggleTranslate,
                    tile.InnerControl != null ? tile.DragScale : null,
                    playCommitPulse,
                    anim,
                    fps,
                    resetScaleIfNoPulse: false);
            }

            foreach (var bar in _bars)
            {
                JiggleMotionHelper.StopJiggle(
                    bar.JiggleRotate,
                    null,
                    bar.Scale,
                    playCommitPulse,
                    anim,
                    fps,
                    resetScaleIfNoPulse: false);
            }
        }
    }
}
