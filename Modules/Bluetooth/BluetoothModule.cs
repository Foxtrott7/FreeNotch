using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.Bluetooth
{
    /// <summary>
    /// Bluetooth- und Audio-Ausgabe-Modul: kompakte Notchify-Pill (wie im Vorbild)
    /// mit Akku-Ring und aufklappbarem Audio-/Geräte-Switcher.
    /// </summary>
    public sealed class BluetoothModule : NotchModuleBase
    {
        private readonly BluetoothAudioService _service = new();
        private DispatcherTimer? _pollTimer;
        private Border? _expandedHost;
        private bool _showDetails;

        public override string Id => "bluetooth";
        public override string Title => L.T("module.bluetooth");
        public override NotchIconKind IconKind => NotchIconKind.Bluetooth;
        public override bool ShowInTabBar => false;
        public override int Priority => 15;
        public override (double Width, double Height)? PreferredExpandedSize
            => _showDetails ? (490, 360) : (390, 118);

        public override void Start()
        {
            _service.Updated -= OnServiceUpdated;
            _service.Updated += OnServiceUpdated;
            _ = _service.RefreshAsync();

            _pollTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            _pollTimer.Tick -= OnPollTick;
            _pollTimer.Tick += OnPollTick;
            _pollTimer.Start();
        }

        public override void Stop()
        {
            _pollTimer?.Stop();
            _service.Updated -= OnServiceUpdated;
        }

        public override void Dispose()
        {
            Stop();
            _pollTimer = null;
        }

        private void OnPollTick(object? sender, EventArgs e)
        {
            if (_expandedHost?.IsLoaded == true)
                _ = _service.RefreshAsync();
        }

        private void OnServiceUpdated()
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_expandedHost != null && _expandedHost.IsLoaded)
                    _expandedHost.Child = BuildExpandedContent();
                RaiseStateChanged();
            });
        }

        public override FrameworkElement CreatePreviewView()
        {
            var connectedBt = _service.BluetoothDevices.FirstOrDefault(b => b.IsConnected);
            var activeAudio = _service.AudioOutputs.FirstOrDefault(a => a.IsDefault);

            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var icon = NotchDesign.CreateVectorIcon(
                NotchIconKind.Bluetooth,
                13,
                connectedBt != null ? new SolidColorBrush(NotchDesign.AccentGreen) : NotchDesign.AccentPrimaryBrush,
                1.8);
            icon.Margin = new Thickness(0, 0, 7, 0);
            stack.Children.Add(icon);

            string label = connectedBt != null
                ? (connectedBt.BatteryPercent.HasValue
                    ? $"{ShortenName(connectedBt.Name, 18)} · {connectedBt.BatteryPercent}%"
                    : ShortenName(connectedBt.Name, 22))
                : activeAudio != null
                    ? ShortenName(activeAudio.Name, 22)
                    : L.T("module.bluetooth");

            stack.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });

            return stack;
        }

        public override FrameworkElement CreateExpandedView()
        {
            _expandedHost = new Border();
            _expandedHost.Child = BuildExpandedContent();
            _expandedHost.Loaded += (_, _) => _ = _service.RefreshAsync();
            return _expandedHost;
        }

        private FrameworkElement BuildExpandedContent()
        {
            var root = new DockPanel();

            // Kompakte Haupt-Pill (wie Screenshot 2)
            var heroRow = BuildHeroPillRow();
            DockPanel.SetDock(heroRow, Dock.Top);
            root.Children.Add(heroRow);

            if (!_showDetails)
                return root;

            // Aufgeklappter Bereich: Lautstärke + Audio-Ausgabegeräte + Bluetooth-Geräte
            var detailsStack = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };

            var volBar = BuildVolumeBar();
            DockPanel.SetDock(volBar, Dock.Top);
            detailsStack.Children.Add(volBar);

            var columns = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var audioListCard = BuildAudioOutputsColumn();
            Grid.SetColumn(audioListCard, 0);
            columns.Children.Add(audioListCard);

            var btListCard = BuildBluetoothDevicesColumn();
            Grid.SetColumn(btListCard, 2);
            columns.Children.Add(btListCard);

            detailsStack.Children.Add(columns);
            root.Children.Add(detailsStack);
            return root;
        }

        private Grid BuildHeroPillRow()
        {
            var connectedBt = _service.BluetoothDevices.FirstOrDefault(b => b.IsConnected);
            var activeAudio = _service.AudioOutputs.FirstOrDefault(a => a.IsDefault);

            int? heroBattery = connectedBt?.BatteryPercent
                ?? (activeAudio != null ? _service.FindBatteryForAudioDevice(activeAudio.Name) : null);

            string statusText;
            Color statusColor;
            string deviceTitle;

            if (connectedBt != null)
            {
                statusText = L.T("bt.connected");
                statusColor = NotchDesign.AccentGreen;
                deviceTitle = connectedBt.Name;
            }
            else if (activeAudio != null)
            {
                statusText = L.T("bt.active_output");
                statusColor = NotchDesign.TextSecondaryBrush is SolidColorBrush sb ? sb.Color : Colors.Gray;
                deviceTitle = activeAudio.Name;
            }
            else
            {
                statusText = L.T("bt.disconnected");
                statusColor = NotchDesign.TextSecondaryBrush is SolidColorBrush sb ? sb.Color : Colors.Gray;
                deviceTitle = L.T("bt.no_device");
            }

            var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Links: Dunkles rundes Bluetooth-Badge mit Signal-Strichen (wie im Screenshot)
            var btBadge = new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(22),
                Background = new SolidColorBrush(NotchDesign.IsLight
                    ? Color.FromArgb(0x12, 0x00, 0x00, 0x00)
                    : Color.FromRgb(0x12, 0x12, 0x16)),
                BorderBrush = new SolidColorBrush(NotchDesign.IsLight
                    ? Color.FromArgb(0x18, 0x00, 0x00, 0x00)
                    : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 12, 0),
                Child = CreateBluetoothSignalIcon(
                    22,
                    connectedBt != null ? NotchDesign.TextSecondaryBrush : NotchDesign.TextMutedBrush)
            };
            Grid.SetColumn(btBadge, 0);
            grid.Children.Add(btBadge);

            // Mitte: Status + Gerätename
            var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            infoStack.Children.Add(new TextBlock
            {
                Text = statusText,
                Foreground = new SolidColorBrush(statusColor),
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 1)
            });
            infoStack.Children.Add(new TextBlock
            {
                Text = deviceTitle,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 15.5,
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(infoStack, 1);
            grid.Children.Add(infoStack);

            // Rechts: Großer Akku-Ring + runder Geräte/Home-Button
            var rightStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };

            if (heroBattery.HasValue)
            {
                var ring = CreateBatteryRing(heroBattery.Value, 44, isHero: true);
                ring.Margin = new Thickness(0, 0, 10, 0);
                ring.VerticalAlignment = VerticalAlignment.Center;
                rightStack.Children.Add(ring);
            }

            var toggleBtn = NotchDesign.CreateRoundedButton(
                CreateHomeDrawerIcon(14, _showDetails ? NotchDesign.AccentPrimaryBrush : NotchDesign.TextPrimaryBrush),
                ToggleDetails,
                background: _showDetails ? NotchDesign.CreateSoftAccentTileBrush(0x30) : NotchDesign.NestedCardBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                foreground: NotchDesign.TextPrimaryBrush,
                cornerRadius: 16,
                padding: new Thickness(8, 8, 8, 8),
                tooltip: _showDetails ? L.T("bt.compact_view") : L.T("bt.show_details"));
            toggleBtn.Width = 32;
            toggleBtn.Height = 32;
            toggleBtn.VerticalAlignment = VerticalAlignment.Center;
            rightStack.Children.Add(toggleBtn);

            Grid.SetColumn(rightStack, 2);
            grid.Children.Add(rightStack);

            return grid;
        }

        private void ToggleDetails()
        {
            _showDetails = !_showDetails;
            if (_expandedHost != null)
                _expandedHost.Child = BuildExpandedContent();
            RaiseStateChanged();
        }

        private Border BuildVolumeBar()
        {
            var card = NotchDesign.CreateCard(12, new Thickness(10, 7, 10, 7));
            var volRow = new DockPanel();

            var muteBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(
                    _service.IsMuted ? NotchIconKind.VolumeOff : NotchIconKind.VolumeOn,
                    13,
                    _service.IsMuted ? new SolidColorBrush(NotchDesign.AccentRed) : NotchDesign.TextPrimaryBrush,
                    1.8),
                () => _service.ToggleMute(),
                background: NotchDesign.NestedCardBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                foreground: NotchDesign.TextPrimaryBrush,
                cornerRadius: 8,
                padding: new Thickness(6, 5, 6, 5),
                tooltip: _service.IsMuted ? L.T("bt.unmute") : L.T("bt.mute"));
            DockPanel.SetDock(muteBtn, Dock.Left);
            volRow.Children.Add(muteBtn);

            var rightActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var pctLabel = new TextBlock
            {
                Text = _service.IsMuted ? L.T("bt.muted") : $"{(int)Math.Round(_service.MasterVolume * 100)}%",
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                Width = 38,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            rightActions.Children.Add(pctLabel);

            var refreshBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Reset, 11, NotchDesign.TextSecondaryBrush, 1.8),
                () => _ = _service.RefreshAsync(),
                background: NotchDesign.NestedCardBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                foreground: NotchDesign.TextSecondaryBrush,
                cornerRadius: 7,
                padding: new Thickness(5, 4, 5, 4),
                tooltip: L.T("common.refresh"));
            refreshBtn.Margin = new Thickness(0, 0, 4, 0);
            rightActions.Children.Add(refreshBtn);

            var winBtBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.ExternalLink, 11, NotchDesign.TextSecondaryBrush, 1.8),
                OpenWindowsBluetoothSettings,
                background: NotchDesign.NestedCardBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                foreground: NotchDesign.TextSecondaryBrush,
                cornerRadius: 7,
                padding: new Thickness(5, 4, 5, 4),
                tooltip: L.T("bt.win_settings"));
            rightActions.Children.Add(winBtBtn);

            DockPanel.SetDock(rightActions, Dock.Right);
            volRow.Children.Add(rightActions);

            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 1,
                Value = _service.MasterVolume,
                IsMoveToPointEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 8, 0),
                Foreground = NotchDesign.AccentPrimaryBrush
            };
            slider.ValueChanged += (_, e) =>
            {
                _service.SetVolume(e.NewValue);
                pctLabel.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
            };
            volRow.Children.Add(slider);

            card.Child = volRow;
            return card;
        }

        private Border BuildAudioOutputsColumn()
        {
            var card = NotchDesign.CreateCard(14, new Thickness(10, 9, 10, 9));
            var dock = new DockPanel();

            var title = new TextBlock
            {
                Text = L.T("bt.audio_output"),
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 0, 0, 6)
            };
            DockPanel.SetDock(title, Dock.Top);
            dock.Children.Add(title);

            var listStack = new StackPanel();
            if (_service.AudioOutputs.Count == 0)
            {
                listStack.Children.Add(CreateEmptyHint(L.T("bt.no_outputs")));
            }
            else
            {
                foreach (var dev in _service.AudioOutputs)
                    listStack.Children.Add(BuildAudioOutputRow(dev));
            }

            dock.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = listStack
            });

            card.Child = dock;
            return card;
        }

        private Border BuildAudioOutputRow(AudioOutputDeviceInfo dev)
        {
            var row = new Border
            {
                Background = dev.IsDefault ? NotchDesign.CreateSoftAccentTileBrush(0x28) : NotchDesign.NestedCardBackgroundBrush,
                BorderBrush = dev.IsDefault
                    ? new SolidColorBrush(Color.FromArgb(0x88, NotchDesign.AccentPrimary.R, NotchDesign.AccentPrimary.G, NotchDesign.AccentPrimary.B))
                    : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 7, 9, 7),
                Margin = new Thickness(0, 0, 0, 5),
                Cursor = Cursors.Hand
            };

            var dp = new DockPanel { LastChildFill = true };
            if (dev.IsDefault)
            {
                var check = NotchDesign.CreateVectorIcon(NotchIconKind.Check, 12, new SolidColorBrush(NotchDesign.AccentGreen), 2.1);
                check.Margin = new Thickness(6, 0, 0, 0);
                check.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(check, Dock.Right);
                dp.Children.Add(check);
            }

            dp.Children.Add(new TextBlock
            {
                Text = dev.Name,
                Foreground = dev.IsDefault ? NotchDesign.TextPrimaryBrush : NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = dev.IsDefault ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });

            row.Child = dp;
            row.MouseEnter += (_, _) => { if (!dev.IsDefault) row.Background = NotchDesign.ControlHoverBrush; };
            row.MouseLeave += (_, _) => { if (!dev.IsDefault) row.Background = NotchDesign.NestedCardBackgroundBrush; };
            row.MouseLeftButtonUp += (_, _) => { if (!dev.IsDefault) _service.SwitchAudioOutput(dev.Id); };
            return row;
        }

        private Border BuildBluetoothDevicesColumn()
        {
            var card = NotchDesign.CreateCard(14, new Thickness(10, 9, 10, 9));
            var dock = new DockPanel();

            var title = new TextBlock
            {
                Text = L.T("bt.devices"),
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 0, 0, 6)
            };
            DockPanel.SetDock(title, Dock.Top);
            dock.Children.Add(title);

            var listStack = new StackPanel();
            if (_service.BluetoothDevices.Count == 0)
            {
                listStack.Children.Add(CreateEmptyHint(_service.IsRefreshing
                    ? L.T("bt.searching")
                    : L.T("bt.none_paired")));
            }
            else
            {
                foreach (var bt in _service.BluetoothDevices)
                    listStack.Children.Add(BuildBluetoothDeviceRow(bt));
            }

            dock.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = listStack
            });

            card.Child = dock;
            return card;
        }

        private static Border BuildBluetoothDeviceRow(BluetoothDeviceItem bt)
        {
            var row = new Border
            {
                Background = NotchDesign.NestedCardBackgroundBrush,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 7, 9, 7),
                Margin = new Thickness(0, 0, 0, 5)
            };

            var dp = new DockPanel { LastChildFill = true };

            if (bt.BatteryPercent.HasValue)
            {
                var ring = CreateBatteryRing(bt.BatteryPercent.Value, 28, isHero: false);
                ring.Margin = new Thickness(6, 0, 0, 0);
                ring.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(ring, Dock.Right);
                dp.Children.Add(ring);
            }

            var statusDot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = bt.IsConnected
                    ? new SolidColorBrush(NotchDesign.AccentGreen)
                    : new SolidColorBrush(Color.FromArgb(0x55, 0x8E, 0x8E, 0x93)),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(statusDot, Dock.Left);
            dp.Children.Add(statusDot);

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock
            {
                Text = bt.Name,
                Foreground = bt.IsConnected ? NotchDesign.TextPrimaryBrush : NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = bt.IsConnected ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            textStack.Children.Add(new TextBlock
            {
                Text = bt.IsConnected
                    ? (bt.BatteryPercent.HasValue ? L.F("bt.connected_battery", bt.BatteryPercent.Value) : L.T("bt.connected"))
                    : L.T("bt.paired"),
                Foreground = bt.IsConnected ? new SolidColorBrush(NotchDesign.AccentGreen) : NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10
            });
            dp.Children.Add(textStack);

            row.Child = dp;
            return row;
        }

        private static Grid CreateBatteryRing(int percent, double size, bool isHero)
        {
            percent = Math.Clamp(percent, 0, 100);
            var ringColor = percent > 40
                ? (isHero ? Color.FromRgb(0xB8, 0xE6, 0x35) : NotchDesign.AccentGreen)
                : percent >= 20
                    ? NotchDesign.AccentOrange
                    : NotchDesign.AccentRed;

            var grid = new Grid
            {
                Width = size,
                Height = size,
                ToolTip = L.F("bt.battery_tooltip", percent)
            };

            double stroke = isHero ? 3.8 : 2.3;
            double radius = (size - stroke) / 2.0;
            double center = size / 2.0;

            var track = new Ellipse
            {
                Width = size - stroke,
                Height = size - stroke,
                Stroke = new SolidColorBrush(Color.FromArgb(0x2A, ringColor.R, ringColor.G, ringColor.B)),
                StrokeThickness = stroke
            };
            grid.Children.Add(track);

            if (percent > 0)
            {
                double angle = percent >= 100 ? 359.9 : (percent / 100.0) * 360.0;
                double rad = (angle - 90.0) * Math.PI / 180.0;
                double startX = center;
                double startY = center - radius;
                double endX = center + radius * Math.Cos(rad);
                double endY = center + radius * Math.Sin(rad);
                bool isLargeArc = angle > 180.0;

                var fig = new PathFigure { StartPoint = new Point(startX, startY), IsClosed = false };
                fig.Segments.Add(new ArcSegment(
                    new Point(endX, endY),
                    new Size(radius, radius),
                    0,
                    isLargeArc,
                    SweepDirection.Clockwise,
                    true));

                var geo = new PathGeometry();
                geo.Figures.Add(fig);
                geo.Freeze();

                grid.Children.Add(new Path
                {
                    Data = geo,
                    Stroke = new SolidColorBrush(ringColor),
                    StrokeThickness = stroke,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                });
            }

            grid.Children.Add(new TextBlock
            {
                Text = $"{percent}",
                Foreground = isHero ? new SolidColorBrush(ringColor) : NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = isHero ? 14 : 8.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            return grid;
        }

        private static Viewbox CreateBluetoothSignalIcon(double size, Brush brush) => new()
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = new Path
            {
                Width = 24,
                Height = 24,
                Data = Geometry.Parse("M 7,7 L 17,17 L 12,21 L 12,3 L 17,7 L 7,17 M 3,12 L 5,12 M 19,12 L 21,12"),
                Stroke = brush,
                StrokeThickness = 1.9,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            }
        };

        private static Viewbox CreateHomeDrawerIcon(double size, Brush brush) => new()
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = new Path
            {
                Width = 24,
                Height = 24,
                Data = Geometry.Parse("M 4,11 L 12,4 L 20,11 L 20,19 A 1.5,1.5 0 0 1 18.5,20.5 L 5.5,20.5 A 1.5,1.5 0 0 1 4,19 Z"),
                Stroke = brush,
                StrokeThickness = 2.0,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            }
        };

        private static TextBlock CreateEmptyHint(string text) => new()
        {
            Text = text,
            Foreground = NotchDesign.TextSecondaryBrush,
            FontFamily = NotchDesign.UiTextFont,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 12, 4, 4)
        };

        private static string ShortenName(string name, int maxLen)
            => name.Length <= maxLen ? name : name[..(maxLen - 1)] + "…";

        private static void OpenWindowsBluetoothSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }
    }
}
