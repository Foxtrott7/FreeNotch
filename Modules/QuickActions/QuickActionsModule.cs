using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Windows.Devices.Radios;
using WindowsNotch.Core;
using WindowsNotch.Modules.Bluetooth;
using WindowsNotch.Modules.SongRecognition;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.QuickActions
{
    /// <summary>
    /// Control Center im ruhigen Notchify-Desktop-Stil:
    /// Direkte Aktionen (Bluetooth, Projizieren, Task-Manager, Explorer),
    /// Song-Erkennung sowie Lautstärke & allgemeine Audio-Ausgabe.
    /// </summary>
    public partial class QuickActionsModule : NotchModuleBase
    {
        private enum QuickActionsSubView
        {
            None,
            SongRecognition,
            Calculator,
            SoundMixer
        }

        private readonly BluetoothAudioService _audioService = new();
        private CancellationTokenSource? _recogCts;
        private Border? _expandedHost;
        private QuickActionsJiggleController? _jiggleController;
        private QuickActionsSubView _activeSubView = QuickActionsSubView.None;
        private bool _isSubViewPinned;
        private bool _suppressNextSubViewEnterAnim;
        private bool _isListening;
        private bool _isDraggingVolume;
        private bool _showAudioDrawer;
        private bool _showStoreDrawer;
        private bool _suppressNextStoreEnterAnim;
        private bool _animateDrawerOnce;
        private bool _extraStoreRowActive;
        private bool _bluetoothOn;
        private bool _wifiOn;
        private bool _nightLightOn;

        public override string Id => "quickactions";
        public override string Title => "Control Center";
        public override NotchIconKind IconKind => NotchIconKind.Bolt;
        public override int Priority => 5;
        public override bool KeepExpanded => _activeSubView != QuickActionsSubView.None && _isSubViewPinned;
        public override double? PreferredPreviewWidth => 200;
        public override (double Width, double Height)? PreferredExpandedSize
        {
            get
            {
                if (_activeSubView == QuickActionsSubView.SongRecognition) return (450, 290);
                if (_activeSubView == QuickActionsSubView.Calculator) return (316, 352);
                if (_activeSubView == QuickActionsSubView.SoundMixer) return (460, 344);

                int slotCount = GetTileSlots().Count + (_extraStoreRowActive ? 3 : 0);
                int rows = Math.Max(1, (slotCount + 2) / 3);
                int topBars = GetTopBarIds().Count;
                int bottomBars = GetBottomBarIds().Count;
                int barCount = topBars + bottomBars;
                double barGap = (topBars > 0 ? 8 : 0) + (bottomBars > 0 ? 8 : 0);
                double h = 76 + rows * 44 + (rows - 1) * 6 + barCount * 48 + barGap;
                if (_showAudioDrawer) h += 116;
                if (_showStoreDrawer) h += EstimateStoreDrawerHeight();
                return (450, h);
            }
        }

        public override void Start()
        {
            _audioService.Updated -= OnAudioUpdated;
            _audioService.Updated += OnAudioUpdated;
            _ = _audioService.RefreshAsync();
            _ = RefreshSystemToggleStatesAsync();
        }

        public override void Stop()
        {
            _jiggleController?.Detach();
            _audioService.Updated -= OnAudioUpdated;
            _recogCts?.Cancel();
            StopMixerRefreshTimer();
            _isSubViewPinned = false;
            _activeSubView = QuickActionsSubView.None;
        }

        public void OnCollapsed()
        {
            if (_activeSubView != QuickActionsSubView.None && !_isSubViewPinned)
                CloseSubView();
        }

        public void OnTabDeselected()
        {
            if (_activeSubView != QuickActionsSubView.None && !_isSubViewPinned)
                CloseSubView();
        }

        public override void Dispose()
        {
            Stop();
            _recogCts?.Dispose();
            base.Dispose();
        }

        private void OnAudioUpdated()
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_expandedHost != null && _expandedHost.IsLoaded &&
                    _activeSubView == QuickActionsSubView.None &&
                    !_isListening && !_isDraggingVolume && _jiggleController?.IsJiggleMode != true)
                {
                    _expandedHost.Child = BuildExpandedContent();
                }
            });
        }

        public override FrameworkElement CreatePreviewView()
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var icon = NotchDesign.CreateVectorIcon(NotchIconKind.Bolt, 13, NotchDesign.AccentPrimaryBrush, 2.0);
            icon.Margin = new Thickness(0, 0, 7, 0);

            var text = new TextBlock
            {
                Text = "Control Center",
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };

            stack.Children.Add(icon);
            stack.Children.Add(text);
            return stack;
        }

        public override FrameworkElement CreateExpandedView()
        {
            _expandedHost = new Border { Background = Brushes.Transparent };
            _expandedHost.Child = BuildExpandedContent();
            _expandedHost.Loaded += (_, _) =>
            {
                _ = _audioService.RefreshAsync();
                _ = RefreshSystemToggleStatesAsync();
            };
            return _expandedHost;
        }

        public void SetTourDemoJiggle(bool active)
        {
            if (active) _jiggleController?.EnterJiggleMode();
            else _jiggleController?.ExitJiggleMode();
        }

        public override bool HandleEscape()
        {
            if (_activeSubView != QuickActionsSubView.None)
            {
                CloseSubView();
                return true;
            }

            if (_jiggleController?.HandleEscape() == true)
                return true;

            if (_showStoreDrawer || _showAudioDrawer)
            {
                _showStoreDrawer = false;
                _showAudioDrawer = false;
                RebuildExpandedUi();
                RaiseStateChanged();
                return true;
            }
            return false;
        }

        private void OpenSubView(QuickActionsSubView view)
        {
            if (_activeSubView == view) return;
            _jiggleController?.ExitJiggleMode();
            _showStoreDrawer = false;
            _showAudioDrawer = false;
            StopMixerRefreshTimer();
            _isSubViewPinned = false;
            _activeSubView = view;

            if (view == QuickActionsSubView.SongRecognition && SettingsService.Current.OptInMusicRecognition)
            {
                _lastSongResult = null;
                _ = StartSongRecognitionInSubViewAsync(skipInitialRebuild: true);
            }

            RebuildExpandedUi();
            RaiseStateChanged();
        }

        private void CloseSubView()
        {
            if (_activeSubView == QuickActionsSubView.None) return;
            _recogCts?.Cancel();
            _isListening = false;
            _isSubViewPinned = false;
            _lastSongResult = null;
            StopMixerRefreshTimer();
            _activeSubView = QuickActionsSubView.None;
            RebuildExpandedUi();
            RaiseStateChanged();
        }

        private Border BuildSubViewShell(
            string title,
            NotchIconKind icon,
            FrameworkElement body,
            FrameworkElement? headerRight = null)
        {
            var root = new DockPanel { Margin = new Thickness(4, 2, 4, 2) };

            var header = new DockPanel { LastChildFill = false, Margin = new Thickness(2, 0, 2, 10) };
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var iconEl = NotchDesign.CreateVectorIcon(icon, 15, NotchDesign.AccentPrimaryBrush, 2.0);
            iconEl.Margin = new Thickness(0, 0, 8, 0);
            iconEl.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(iconEl);
            left.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 15.5,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });
            DockPanel.SetDock(left, Dock.Left);
            header.Children.Add(left);

            var closeBtn = NotchDesign.CreateIconActionButton(
                NotchIconKind.Close,
                L.T("qa.subview.back"),
                CloseSubView,
                size: 28);
            DockPanel.SetDock(closeBtn, Dock.Right);
            header.Children.Add(closeBtn);

            var pinBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(
                    NotchIconKind.Pin,
                    14,
                    _isSubViewPinned ? Brushes.White : NotchDesign.TextPrimaryBrush,
                    _isSubViewPinned ? 2.1 : 1.85),
                () => { },
                background: _isSubViewPinned ? NotchDesign.AccentPrimaryBrush : NotchDesign.CardBackgroundBrush,
                hoverBackground: _isSubViewPinned ? NotchDesign.AccentHoverBrush : NotchDesign.NestedCardBackgroundBrush,
                cornerRadius: 14,
                padding: new Thickness(0),
                tooltip: _isSubViewPinned ? L.T("qa.subview.unpin") : L.T("qa.subview.pin"));
            pinBtn.Width = 28;
            pinBtn.Height = 28;
            pinBtn.VerticalAlignment = VerticalAlignment.Center;
            pinBtn.Margin = new Thickness(0, 0, 6, 0);
            pinBtn.Opacity = _isSubViewPinned ? 1.0 : 0.0;
            pinBtn.IsHitTestVisible = _isSubViewPinned;
            DockPanel.SetDock(pinBtn, Dock.Right);
            header.Children.Add(pinBtn);

            if (headerRight != null)
            {
                headerRight.Margin = new Thickness(0, 0, 6, 0);
                DockPanel.SetDock(headerRight, Dock.Right);
                header.Children.Add(headerRight);
            }

            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(body);

            var container = new Border
            {
                Background = Brushes.Transparent,
                Child = root
            };

            void UpdatePinVisibility(bool hovered)
            {
                bool show = _isSubViewPinned || hovered;
                pinBtn.IsHitTestVisible = show;
                double target = show ? 1.0 : 0.0;
                if (!NotchDesign.EffectiveAnimationsEnabled)
                {
                    pinBtn.BeginAnimation(UIElement.OpacityProperty, null);
                    pinBtn.Opacity = target;
                    return;
                }
                pinBtn.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(
                    target,
                    TimeSpan.FromMilliseconds(160))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
            }

            container.MouseEnter += (_, _) => UpdatePinVisibility(true);
            container.MouseLeave += (_, _) => UpdatePinVisibility(false);

            pinBtn.Click += (_, _) =>
            {
                if (_isSubViewPinned)
                {
                    _isSubViewPinned = false;
                    CloseSubView();
                    RaiseCollapseRequested();
                    return;
                }

                _isSubViewPinned = true;
                _suppressNextSubViewEnterAnim = true;
                RebuildExpandedUi();
                RaiseStateChanged();
            };

            if (_suppressNextSubViewEnterAnim)
                _suppressNextSubViewEnterAnim = false;
            else
                NotchDesign.AnimateSubViewEnter(container, slideY: 8, durationMs: NotchDesign.MicroTransitionMs);
            return container;
        }

        private void RebuildExpandedUi()
        {
            _extraStoreRowActive = false;
            if (_expandedHost != null)
                _expandedHost.Child = BuildExpandedContent();
        }

        private List<string> GetTileSlots()
        {
            var raw = SettingsService.Current.QuickActionTileOrder ?? AppSettings.DefaultQuickActionTileOrder.ToList();
            var valid = new HashSet<string>(TileCatalog.Select(t => t.Id));
            var filtered = raw.Select(id => !string.IsNullOrEmpty(id) && valid.Contains(id) ? id : string.Empty).ToList();
            return AppSettings.NormalizeQuickTileSlots(filtered);
        }

        private List<string> GetActiveTileIds()
            => GetTileSlots().Where(id => !string.IsNullOrEmpty(id)).ToList();

        private (List<string> Top, List<string> Bottom) GetTopAndBottomBarIds()
        {
            var valid = new HashSet<string>(BarCatalog.Select(b => b.Id));
            var s = SettingsService.Current;

            if (s.QuickActionTopBars == null && s.QuickActionBottomBars == null)
            {
                var legacy = (s.QuickActionBarOrder ?? AppSettings.DefaultQuickActionBarOrder.ToList())
                    .Where(id => valid.Contains(id))
                    .Distinct()
                    .ToList();
                return s.QuickActionsVolumeTop
                    ? (legacy, new List<string>())
                    : (new List<string>(), legacy);
            }

            var top = (s.QuickActionTopBars ?? new List<string>()).Where(id => valid.Contains(id)).Distinct().ToList();
            var bottom = (s.QuickActionBottomBars ?? new List<string>()).Where(id => valid.Contains(id) && !top.Contains(id)).Distinct().ToList();
            return (top, bottom);
        }

        private List<string> GetTopBarIds() => GetTopAndBottomBarIds().Top;
        private List<string> GetBottomBarIds() => GetTopAndBottomBarIds().Bottom;

        private List<string> GetActiveBarIds()
        {
            var (top, bottom) = GetTopAndBottomBarIds();
            var all = new List<string>(top.Count + bottom.Count);
            all.AddRange(top);
            all.AddRange(bottom);
            return all;
        }

        private void SaveBarPositions(List<string> top, List<string> bottom)
        {
            var combined = new List<string>(top.Count + bottom.Count);
            combined.AddRange(top);
            combined.AddRange(bottom);
            SettingsService.UpdateQuiet(s =>
            {
                s.QuickActionTopBars = top.ToList();
                s.QuickActionBottomBars = bottom.ToList();
                s.QuickActionBarOrder = combined;
                s.QuickActionsVolumeTop = top.Count > 0 && bottom.Count == 0;
            });
        }

        private FrameworkElement BuildExpandedContent()
        {
            if (_activeSubView == QuickActionsSubView.SongRecognition)
                return BuildSongRecognitionSubView();
            if (_activeSubView == QuickActionsSubView.Calculator)
                return BuildCalculatorSubView();
            if (_activeSubView == QuickActionsSubView.SoundMixer)
                return BuildSoundMixerSubView();

            var overlayRoot = new Grid();
            var contentStack = new StackPanel { Margin = new Thickness(4, 2, 4, 2) };
            var dragOverlayCanvas = new Canvas { IsHitTestVisible = false, ClipToBounds = false };
            Panel.SetZIndex(dragOverlayCanvas, 500);

            overlayRoot.Children.Add(contentStack);
            overlayRoot.Children.Add(dragOverlayCanvas);

            var (topBars, bottomBars) = GetTopAndBottomBarIds();
            var topBarsHost = BuildBarsHost(topBars, isTop: true);
            var (tilesGrid, jiggleTiles) = BuildQuickTilesGrid();
            var bottomBarsHost = BuildBarsHost(bottomBars, isTop: false);

            topBarsHost.Margin = new Thickness(0, 0, 0, topBars.Count > 0 ? 8 : 0);
            contentStack.Children.Add(topBarsHost);
            contentStack.Children.Add(tilesGrid);
            bottomBarsHost.Margin = new Thickness(0, bottomBars.Count > 0 ? 8 : 0, 0, 0);
            contentStack.Children.Add(bottomBarsHost);

            if (_showAudioDrawer)
            {
                var drawer = BuildAudioOutputDrawer();
                if (_animateDrawerOnce)
                {
                    _animateDrawerOnce = false;
                    NotchDesign.AnimateSubViewEnter(drawer, slideY: 6);
                }
                contentStack.Children.Add(drawer);
            }

            Border? storeDrawer = null;
            if (_showStoreDrawer)
            {
                bool suppressAnim = _suppressNextStoreEnterAnim;
                _suppressNextStoreEnterAnim = false;
                storeDrawer = BuildStoreDrawer();
                if (!suppressAnim)
                    NotchDesign.AnimateSubViewEnter(storeDrawer, slideY: 8, durationMs: NotchDesign.DrawerOpenDurationMs);
                contentStack.Children.Add(storeDrawer);
            }
            else
            {
                _suppressNextStoreEnterAnim = false;
            }

            bool wasJiggle = _jiggleController?.IsJiggleMode == true;
            _jiggleController?.Detach();
            _jiggleController = new QuickActionsJiggleController(
                overlayRoot,
                contentStack,
                dragOverlayCanvas,
                tilesGrid,
                topBarsHost,
                bottomBarsHost,
                jiggleTiles,
                BuildBarEntries(topBarsHost, bottomBarsHost),
                newSlots => SettingsService.UpdateQuiet(s => s.QuickActionTileOrder = AppSettings.NormalizeQuickTileSlots(newSlots.ToList())),
                (newTop, newBottom) =>
                {
                    SaveBarPositions(newTop.ToList(), newBottom.ToList());
                    if (_showStoreDrawer) _suppressNextStoreEnterAnim = true;
                    RebuildExpandedUi();
                    RaiseStateChanged();
                },
                (removeId, isBar) => RemoveActiveItem(removeId, isBar),
                (addId, isBar, targetSlot, toTop, insertIdx) => CommitStoreDrop(addId, isBar, targetSlot, toTop, insertIdx),
                extraActive =>
                {
                    if (_extraStoreRowActive == extraActive) return;
                    _extraStoreRowActive = extraActive;
                    RaiseStateChanged();
                },
                onEnterJiggle: () =>
                {
                    if (_showStoreDrawer) return;
                    _showStoreDrawer = true;
                    storeDrawer = BuildStoreDrawer();
                    NotchDesign.AnimateSubViewEnter(storeDrawer, slideY: 8, durationMs: NotchDesign.DrawerOpenDurationMs);
                    contentStack.Children.Add(storeDrawer);
                    AttachStoreDragHandlers(storeDrawer);
                    RaiseStateChanged();
                },
                onExitJiggle: () =>
                {
                    if (!_showStoreDrawer) return;
                    _showStoreDrawer = false;
                    _extraStoreRowActive = false;
                    var closingDrawer = storeDrawer;
                    storeDrawer = null;
                    if (closingDrawer != null)
                    {
                        NotchDesign.AnimateDrawerExit(closingDrawer, () =>
                        {
                            if (!_showStoreDrawer)
                                contentStack.Children.Remove(closingDrawer);
                        });
                    }
                    RaiseStateChanged();
                });

            if (storeDrawer != null)
                AttachStoreDragHandlers(storeDrawer);

            if (wasJiggle)
                _jiggleController.SyncJiggleState(true);

            return overlayRoot;
        }

        private StackPanel BuildBarsHost(List<string> barIds, bool isTop)
        {
            var host = new StackPanel { Tag = isTop ? "TopBarsHost" : "BottomBarsHost" };
            for (int i = 0; i < barIds.Count; i++)
            {
                string id = barIds[i];
                FrameworkElement? barCard = id switch
                {
                    "volume" => BuildVolumeCard(),
                    _ => null
                };
                if (barCard == null) continue;

                var wrapper = new Border
                {
                    Background = Brushes.Transparent,
                    Tag = id,
                    Margin = i > 0 ? new Thickness(0, 6, 0, 0) : new Thickness(0),
                    Child = barCard
                };
                host.Children.Add(wrapper);
            }
            return host;
        }

        private static List<(string Id, Border Wrapper, UIElement InnerControl, bool IsTop)> BuildBarEntries(
            StackPanel topBarsHost,
            StackPanel bottomBarsHost)
        {
            var list = new List<(string, Border, UIElement, bool)>();
            foreach (var child in topBarsHost.Children.OfType<Border>())
            {
                if (child.Tag is string id && child.Child != null)
                    list.Add((id, child, child.Child, true));
            }
            foreach (var child in bottomBarsHost.Children.OfType<Border>())
            {
                if (child.Tag is string id && child.Child != null)
                    list.Add((id, child, child.Child, false));
            }
            return list;
        }

        private (Grid Grid, List<(string Id, Border Wrapper, UIElement? InnerControl)> Tiles) BuildQuickTilesGrid()
        {
            var slots = GetTileSlots();
            int rows = Math.Max(1, (slots.Count + 2) / 3);

            var grid = new Grid();
            for (int r = 0; r < rows; r++)
            {
                if (r > 0) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            }
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var jiggleList = new List<(string Id, Border Wrapper, UIElement? InnerControl)>(slots.Count);

            for (int i = 0; i < slots.Count; i++)
            {
                string id = slots[i];
                Button? btn = string.IsNullOrEmpty(id) ? null : CreateTileById(id);

                // Separater äußerer Wrapper verhindert RenderTransform-Kollision mit NotchDesign.AttachInteractiveScale
                var wrapper = new Border
                {
                    Background = Brushes.Transparent,
                    CornerRadius = new CornerRadius(NotchDesign.RadiusControl),
                    MinHeight = 44,
                    Child = btn
                };

                int row = (i / 3) * 2;
                int col = (i % 3) * 2;
                Grid.SetRow(wrapper, row);
                Grid.SetColumn(wrapper, col);
                grid.Children.Add(wrapper);
                jiggleList.Add((btn != null ? id : string.Empty, wrapper, btn));
            }

            return (grid, jiggleList);
        }

        private Button? CreateTileById(string id)
        {
            if (id == "song") return CreateSongRecognitionButton();
            if (id == "audio")
            {
                var activeAudio = _audioService.AudioOutputs.FirstOrDefault(a => a.IsDefault);
                int? battery = activeAudio != null
                    ? _audioService.FindBatteryForAudioDevice(activeAudio.Name)
                    : null;
                string activeName = activeAudio?.Name ?? L.T("qa.audio_default");
                string audioTooltip = battery is int pct
                    ? L.F("qa.audio_switch_bat", activeName, pct)
                    : L.F("qa.audio_switch", activeName);

                return CreateQuickTile(
                    NotchIconKind.Headphones,
                    L.T("qa.tile.audio"),
                    _showAudioDrawer,
                    audioTooltip,
                    ToggleAudioDrawer);
            }

            if (id == "wifi")
                return CreateQuickTile(NotchIconKind.Wifi, L.T("qa.tile.wifi"), _wifiOn, L.T("qa.tile.wifi_tooltip"), () => _ = ToggleRadioAsync(RadioKind.WiFi, "ms-settings:network-wifi"));
            if (id == "bluetooth")
                return CreateQuickTile(NotchIconKind.Bluetooth, L.T("qa.tile.bluetooth"), _bluetoothOn, L.T("qa.tile.bluetooth_tooltip"), () => _ = ToggleRadioAsync(RadioKind.Bluetooth, "ms-settings:bluetooth"));
            if (id == "nightlight")
                return CreateQuickTile(NotchIconKind.Sun, L.T("qa.tile.nightlight"), _nightLightOn, L.T("qa.tile.nightlight_tooltip"), ToggleNightLight);
            if (id == "calc")
                return CreateQuickTile(NotchIconKind.Dashboard, L.T("qa.tile.calc"), false, L.T("qa.tile.calc_tooltip"), () => OpenSubView(QuickActionsSubView.Calculator));
            if (id == "volmixer")
                return CreateQuickTile(NotchIconKind.Layers, L.T("qa.tile.volmixer"), false, L.T("qa.tile.volmixer_tooltip"), () => OpenSubView(QuickActionsSubView.SoundMixer));

            var def = TileCatalog.FirstOrDefault(t => t.Id == id);
            if (def == null || def.Execute == null) return null;
            return CreateQuickTile(def.Icon, def.Label, false, def.Tooltip, def.Execute);
        }

        private Button CreateSongRecognitionButton()
        {
            string tooltip = !SettingsService.Current.OptInMusicRecognition
                ? L.T("qa.song_rec_enable")
                : L.T("qa.song_rec_tooltip");

            return CreateQuickTile(
                NotchIconKind.Waveform,
                L.T("qa.song_rec"),
                _isListening,
                tooltip,
                () => OpenSubView(QuickActionsSubView.SongRecognition));
        }

        private Border BuildVolumeCard()
        {
            var card = NotchDesign.CreateCard(NotchDesign.RadiusInner, new Thickness(12, 8, 12, 8));
            card.Background = NotchDesign.CardBackgroundBrush;

            // Eine ruhige Zeile: Mute-Icon + horizontaler Slider (ohne Prozentzahl)
            var volGrid = new Grid();
            volGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            volGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            volGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var muteIcon = NotchDesign.CreateVectorIcon(
                _audioService.IsMuted ? NotchIconKind.VolumeOff : NotchIconKind.VolumeOn,
                14,
                _audioService.IsMuted ? NotchDesign.TextMutedBrush : NotchDesign.AccentPrimaryBrush,
                2.0);
            var muteBtn = NotchDesign.CreateRoundedButton(
                muteIcon,
                () => _audioService.ToggleMute(),
                background: Brushes.Transparent,
                hoverBackground: NotchDesign.TabActiveBackgroundBrush,
                cornerRadius: 8,
                padding: new Thickness(5),
                tooltip: L.T("qa.mute_toggle"));
            muteBtn.Margin = new Thickness(-4, 0, 0, 0);
            Grid.SetColumn(muteBtn, 0);
            volGrid.Children.Add(muteBtn);

            var slider = BuildHorizontalVolumeSlider();
            Grid.SetColumn(slider, 2);
            volGrid.Children.Add(slider);

            card.Child = volGrid;
            return card;
        }

        private Border BuildHorizontalVolumeSlider()
        {
            const double thumbSize = 14;
            var hitArea = new Border
            {
                Tag = "VolumeSliderHitArea",
                Height = 24,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };

            var canvas = new Grid { VerticalAlignment = VerticalAlignment.Center };
            var track = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = NotchDesign.TrackBrush,
                VerticalAlignment = VerticalAlignment.Center
            };

            var fill = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = NotchDesign.AccentPrimaryBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };

            var thumb = new Border
            {
                Width = thumbSize,
                Height = thumbSize,
                CornerRadius = new CornerRadius(thumbSize / 2),
                Background = NotchDesign.TextPrimaryBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };

            canvas.Children.Add(track);
            canvas.Children.Add(fill);
            canvas.Children.Add(thumb);
            hitArea.Child = canvas;

            void UpdateVisual(double ratio)
            {
                double w = hitArea.ActualWidth;
                if (w <= thumbSize) return;
                double clamped = Math.Clamp(ratio, 0.0, 1.0);
                fill.Width = Math.Max(thumbSize / 2, clamped * w);
                double left = Math.Clamp(clamped * (w - thumbSize), 0, w - thumbSize);
                thumb.Margin = new Thickness(left, 0, 0, 0);
            }

            hitArea.Loaded += (_, _) => UpdateVisual(_audioService.IsMuted ? 0.0 : _audioService.MasterVolume);
            hitArea.SizeChanged += (_, _) => UpdateVisual(_audioService.IsMuted ? 0.0 : _audioService.MasterVolume);

            void ApplyPointer(MouseEventArgs e)
            {
                double w = hitArea.ActualWidth;
                if (w <= 1) return;
                double ratio = Math.Clamp(e.GetPosition(hitArea).X / w, 0.0, 1.0);
                _audioService.SetVolume(ratio);
                UpdateVisual(ratio);
            }

            hitArea.MouseLeftButtonDown += (_, e) =>
            {
                _isDraggingVolume = true;
                hitArea.CaptureMouse();
                ApplyPointer(e);
            };
            hitArea.MouseMove += (_, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed && hitArea.IsMouseCaptured)
                    ApplyPointer(e);
            };
            hitArea.MouseLeftButtonUp += (_, _) =>
            {
                if (hitArea.IsMouseCaptured) hitArea.ReleaseMouseCapture();
                _isDraggingVolume = false;
            };
            hitArea.MouseWheel += (_, e) =>
            {
                double step = e.Delta > 0 ? 0.04 : -0.04;
                double next = Math.Clamp(_audioService.MasterVolume + step, 0.0, 1.0);
                _audioService.SetVolume(next);
                UpdateVisual(next);
                e.Handled = true;
            };

            return hitArea;
        }

        private Border BuildAudioOutputDrawer()
        {
            var card = NotchDesign.CreateCard(14, new Thickness(8, 6, 8, 6));
            card.Background = NotchDesign.CardBackgroundBrush;
            card.Margin = new Thickness(0, 8, 0, 0);

            var stack = new StackPanel();
            var scroll = new ScrollViewer
            {
                MaxHeight = 96,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack
            };
            NotchDesign.ApplySlimScrollBarStyle(scroll);

            foreach (var output in _audioService.AudioOutputs.Take(8))
            {
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var dot = new Border
                {
                    Width = 6,
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = output.IsDefault ? NotchDesign.AccentPrimaryBrush : NotchDesign.TrackBrush,
                    Margin = new Thickness(2, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);

                if (_audioService.FindBatteryForAudioDevice(output.Name) is int pct)
                {
                    var bat = new TextBlock
                    {
                        Text = $"{pct}%",
                        Foreground = NotchDesign.TextSecondaryBrush,
                        FontFamily = NotchDesign.UiTextFont,
                        FontSize = 10.5,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    DockPanel.SetDock(bat, Dock.Right);
                    row.Children.Add(bat);
                }

                row.Children.Add(new TextBlock
                {
                    Text = output.Name,
                    Foreground = output.IsDefault ? NotchDesign.TextPrimaryBrush : NotchDesign.TextSecondaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11.5,
                    FontWeight = output.IsDefault ? FontWeights.SemiBold : FontWeights.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var rowBtn = NotchDesign.CreateRoundedButton(
                    row,
                    () => _audioService.SwitchAudioOutput(output.Id),
                    background: output.IsDefault ? NotchDesign.CreateSoftAccentTileBrush(0x1C) : Brushes.Transparent,
                    hoverBackground: NotchDesign.TabActiveBackgroundBrush,
                    cornerRadius: 8,
                    padding: new Thickness(7, 5, 7, 5));
                rowBtn.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                stack.Children.Add(rowBtn);
            }

            card.Child = scroll;
            return card;
        }

        private void ToggleAudioDrawer()
        {
            _showAudioDrawer = !_showAudioDrawer;
            _animateDrawerOnce = _showAudioDrawer;
            if (_expandedHost != null)
                _expandedHost.Child = BuildExpandedContent();
            RaiseStateChanged();
        }

        private static Button CreateQuickTile(NotchIconKind kind, string label, bool isActive, string tooltip, Action onClick)
        {
            var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = NotchDesign.CreateVectorIcon(
                kind,
                14.5,
                isActive ? NotchDesign.AccentPrimaryBrush : NotchDesign.TextPrimaryBrush,
                1.95);
            icon.Margin = new Thickness(0, 0, 8, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var text = new TextBlock
            {
                Text = label,
                Foreground = isActive ? NotchDesign.AccentPrimaryBrush : NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var btn = NotchDesign.CreateRoundedButton(
                grid,
                onClick,
                background: isActive ? NotchDesign.CreateSoftAccentTileBrush(0x26) : NotchDesign.CardBackgroundBrush,
                hoverBackground: isActive ? NotchDesign.CreateSoftAccentTileBrush(0x38) : NotchDesign.NestedCardBackgroundBrush,
                cornerRadius: NotchDesign.RadiusInner,
                padding: new Thickness(10, 11, 10, 11),
                tooltip: tooltip);
            btn.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            return btn;
        }

        private static void OpenUri(string target, string? args = null)
        {
            try
            {
                var psi = new ProcessStartInfo(target) { UseShellExecute = true };
                if (!string.IsNullOrEmpty(args))
                    psi.Arguments = args;
                Process.Start(psi);
            }
            catch { }
        }

        private static void OpenTerminal()
        {
            try
            {
                Process.Start(new ProcessStartInfo("wt.exe") { UseShellExecute = true });
            }
            catch
            {
                OpenUri("powershell.exe");
            }
        }

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        private static void TriggerStandby()
        {
            try
            {
                if (!SetSuspendState(false, false, false))
                    OpenUri("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
            }
            catch
            {
                OpenUri("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
            }
        }

        private async Task RefreshSystemToggleStatesAsync()
        {
            bool nightLight = IsNightLightActive();
            bool bt = _bluetoothOn;
            bool wifi = _wifiOn;

            try
            {
                var access = await Radio.RequestAccessAsync();
                if (access == RadioAccessStatus.Allowed)
                {
                    var radios = await Radio.GetRadiosAsync();
                    var btRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                    var wifiRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
                    if (btRadio != null) bt = btRadio.State == RadioState.On;
                    if (wifiRadio != null) wifi = wifiRadio.State == RadioState.On;
                }
            }
            catch { }

            if (nightLight == _nightLightOn && bt == _bluetoothOn && wifi == _wifiOn)
                return;

            _nightLightOn = nightLight;
            _bluetoothOn = bt;
            _wifiOn = wifi;

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_expandedHost != null && _expandedHost.IsLoaded &&
                    _activeSubView == QuickActionsSubView.None &&
                    !_isListening && !_isDraggingVolume && _jiggleController?.IsJiggleMode != true)
                {
                    RebuildExpandedUi();
                }
            });
        }

        private async Task ToggleRadioAsync(RadioKind kind, string fallbackUri)
        {
            try
            {
                var access = await Radio.RequestAccessAsync();
                if (access == RadioAccessStatus.Allowed)
                {
                    var radios = await Radio.GetRadiosAsync();
                    var radio = radios.FirstOrDefault(r => r.Kind == kind);
                    if (radio != null)
                    {
                        var next = radio.State == RadioState.On ? RadioState.Off : RadioState.On;
                        var result = await radio.SetStateAsync(next);
                        if (result == RadioAccessStatus.Allowed)
                        {
                            if (kind == RadioKind.Bluetooth) _bluetoothOn = next == RadioState.On;
                            if (kind == RadioKind.WiFi) _wifiOn = next == RadioState.On;
                            Application.Current?.Dispatcher.BeginInvoke(RebuildExpandedUi);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }

            OpenUri(fallbackUri);
        }

        private const string NightLightRegPath =
            @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

        private static bool IsNightLightActive()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(NightLightRegPath);
                if (key?.GetValue("Data") is byte[] data && data.Length > 24)
                    return data[18] == 0x15 && data[23] == 0x10 && data[24] == 0x00;
            }
            catch { }
            return false;
        }

        private void ToggleNightLight()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(NightLightRegPath, writable: true);
                if (key?.GetValue("Data") is byte[] data && data.Length >= 41)
                {
                    bool currentlyOn = data.Length > 24 && data[18] == 0x15 && data[23] == 0x10 && data[24] == 0x00;
                    byte[] next;
                    if (currentlyOn)
                    {
                        next = new byte[data.Length - 2];
                        Array.Copy(data, 0, next, 0, 23);
                        Array.Copy(data, 25, next, 23, data.Length - 25);
                        next[18] = 0x13;
                    }
                    else
                    {
                        next = new byte[data.Length + 2];
                        Array.Copy(data, 0, next, 0, 23);
                        next[23] = 0x10;
                        next[24] = 0x00;
                        Array.Copy(data, 23, next, 25, data.Length - 23);
                        next[18] = 0x15;
                    }

                    // Zeitstempel-Bytes (10..14) leicht erhöhen, damit Windows Shell die Änderung übernimmt
                    for (int i = 10; i < 15 && i < next.Length; i++)
                    {
                        if (next[i] < 0xFF)
                        {
                            next[i]++;
                            break;
                        }
                        next[i] = 0;
                    }

                    key.SetValue("Data", next, RegistryValueKind.Binary);
                    _nightLightOn = !currentlyOn;
                    RebuildExpandedUi();
                    return;
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }

            OpenUri("ms-settings:nightlight");
        }
    }
}
