using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using WindowsNotch.Core;
using WindowsNotch.Services;
using WpfClipboard = System.Windows.Clipboard;
using WpfImage = System.Windows.Controls.Image;

namespace WindowsNotch.Modules.PhoneTransfer
{
    /// <summary>
    /// Auf Telefon übertragen: Dateiübertragung (Senden & Empfangen) direkt auf OLED-Schwarz
    /// mit QR-Code, ZIP-Mehrfach-Upload im Online-Modus und Apple-Swipe-Switches.
    /// </summary>
    public class PhoneTransferModule : NotchModuleBase
    {
        private readonly PhoneTransferService _service = new();
        private readonly DispatcherTimer _statusTimer;
        private string _statusMessage = string.Empty;
        private bool _isReceiveMode;
        private bool _useOnlineMode = !SettingsService.Current.IsWorkEdition && SettingsService.Current.PhoneTransferOnlineDefault;
        private bool _isUploading;
        private bool _animateModeSwipe;
        private bool _animateTargetSwipe;
        private event Action? ViewUpdated;

        public override string Id => "filetransfer";
        public override string Title => L.T("module.filetransfer");
        public override NotchIconKind IconKind => NotchIconKind.Phone;
        public override int Priority => 9;
        public override double? PreferredPreviewWidth => 210;

        public override (double Width, double Height)? PreferredExpandedSize
            => (_isReceiveMode || _service.GetFiles().Count > 0)
                ? (510, 320)
                : (430, 245);

        public PhoneTransferModule()
        {
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer.Stop();
                _statusMessage = string.Empty;
                ViewUpdated?.Invoke();
            };

            _service.StateChanged += () =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    ViewUpdated?.Invoke();
                    RaiseStateChanged();
                });
            };
        }

        public override void Stop() => _service.Dispose();

        public override FrameworkElement CreatePreviewView()
        {
            var files = _service.GetFiles();
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var icon = NotchDesign.CreateVectorIcon(NotchIconKind.Phone, 13, NotchDesign.AccentPrimaryBrush);
            icon.Margin = new Thickness(0, 0, 7, 0);

            string label = _isReceiveMode
                ? L.T("phonetransfer.receive_qr")
                : files.Count switch
                {
                    0 => L.T("phonetransfer.short_title"),
                    1 => L.T("phonetransfer.one_file_qr"),
                    _ => L.F("phonetransfer.many_files_qr", files.Count)
                };

            stack.Children.Add(icon);
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
            var host = new Border
            {
                AllowDrop = true,
                Background = Brushes.Transparent
            };

            host.DragOver += (_, e) =>
            {
                e.Effects = (!_isReceiveMode && e.Data.GetDataPresent(DataFormats.FileDrop))
                    ? DragDropEffects.Copy
                    : DragDropEffects.None;
                e.Handled = true;
            };

            host.Drop += (_, e) =>
            {
                if (!_isReceiveMode &&
                    e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                {
                    AddAndActivateFiles(paths);
                    e.Handled = true;
                }
            };

            void Rebuild() => host.Child = BuildExpandedContent();

            Rebuild();
            host.Loaded += (_, _) => ViewUpdated += Rebuild;
            host.Unloaded += (_, _) => ViewUpdated -= Rebuild;
            return host;
        }

        private FrameworkElement BuildExpandedContent()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 1. Hauptbereich direkt auf OLED-Schwarz (ohne graue Container-Boxen)
            FrameworkElement center = _isReceiveMode
                ? BuildReceiveState()
                : _service.GetFiles().Count == 0
                    ? BuildSendEmptyState()
                    : BuildSendActiveState(_service.GetFiles());

            Grid.SetRow(center, 0);
            root.Children.Add(center);

            // 2. Untere Leiste: Modus (Senden/Empfangen) & Ziel (Online/Lokal)
            var bottomBar = BuildBottomSwitches();
            Grid.SetRow(bottomBar, 1);
            root.Children.Add(bottomBar);

            return root;
        }

        private FrameworkElement BuildSendEmptyState()
        {
            var container = new Border
            {
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };

            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            stack.Children.Add(new Border
            {
                Width = 48,
                Height = 48,
                CornerRadius = new CornerRadius(14),
                Background = NotchDesign.CreateSoftAccentTileBrush(0x22),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = NotchDesign.CreateVectorIcon(NotchIconKind.Phone, 24, NotchDesign.AccentPrimaryBrush, 1.85)
            });

            stack.Children.Add(new TextBlock
            {
                Text = L.T("phonetransfer.start_title"),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 3)
            });

            stack.Children.Add(new TextBlock
            {
                Text = L.T("phonetransfer.start_hint"),
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            container.Child = stack;
            container.MouseLeftButtonUp += (_, _) => PickFiles();
            return container;
        }

        private FrameworkElement BuildSendActiveState(IReadOnlyList<PhoneTransferFile> files)
        {
            var root = new Grid { Margin = new Thickness(2, 4, 2, 0) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(154) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Linke Seite: QR-Code direkt auf OLED-Schwarz
            var qrStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            string activeUrl = _service.ActiveQrUrl;
            if (!string.IsNullOrEmpty(activeUrl))
            {
                qrStack.Children.Add(CreateQrFrame(activeUrl, 134));

                string info = !string.IsNullOrEmpty(_statusMessage)
                    ? _statusMessage
                    : _service.IsOnlineMode
                        ? (files.Count > 1 ? L.T("phonetransfer.online_zip") : L.T("phonetransfer.online_link"))
                        : _service.DownloadCount > 0
                            ? L.F("phonetransfer.downloaded_count", _service.DownloadCount)
                            : L.T("phonetransfer.scan_wlan");

                qrStack.Children.Add(new TextBlock
                {
                    Text = info,
                    Foreground = (!string.IsNullOrEmpty(_statusMessage) || _service.DownloadCount > 0)
                        ? new SolidColorBrush(NotchDesign.AccentGreen)
                        : NotchDesign.TextSecondaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 10.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 0)
                });
            }
            else
            {
                qrStack.Children.Add(new TextBlock
                {
                    Text = _isUploading ? L.T("phonetransfer.uploading") : L.T("phonetransfer.preparing"),
                    Foreground = NotchDesign.TextSecondaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

            Grid.SetColumn(qrStack, 0);
            root.Children.Add(qrStack);

            // Rechte Seite: Dateiliste + Aktionen direkt auf OLED-Schwarz
            var rightGrid = new Grid();
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new TextBlock
            {
                Text = files.Count == 1 ? L.T("phonetransfer.selected_file") : L.F("phonetransfer.many_files", files.Count),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 0, 0, 8)
            };
            Grid.SetRow(header, 0);
            rightGrid.Children.Add(header);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            NotchDesign.ApplySlimScrollBarStyle(scroll);

            var listStack = new StackPanel();
            foreach (var f in files)
                listStack.Children.Add(BuildFileRow(f, removable: true));
            scroll.Content = listStack;
            Grid.SetRow(scroll, 1);
            rightGrid.Children.Add(scroll);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };

            if (!string.IsNullOrEmpty(activeUrl))
            {
                var copyBtn = NotchDesign.CreateRoundedButton(
                    L.T("phonetransfer.copy_link"),
                    CopyUrl,
                    background: NotchDesign.AccentPrimaryBrush,
                    hoverBackground: NotchDesign.AccentHoverBrush,
                    foreground: Brushes.White,
                    cornerRadius: 10,
                    padding: new Thickness(10, 5, 10, 5));
                copyBtn.FontSize = 11;
                copyBtn.Margin = new Thickness(0, 0, 6, 0);
                actions.Children.Add(copyBtn);
            }

            var addBtn = NotchDesign.CreateRoundedButton(
                L.T("phonetransfer.add_file"),
                PickFiles,
                background: NotchDesign.ControlBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                foreground: NotchDesign.TextPrimaryBrush,
                cornerRadius: 10,
                padding: new Thickness(10, 5, 10, 5));
            addBtn.FontSize = 11;
            addBtn.Margin = new Thickness(0, 0, 6, 0);
            actions.Children.Add(addBtn);

            var stopBtn = NotchDesign.CreateRoundedButton(
                L.T("phonetransfer.stop"),
                _service.ClearAll,
                background: NotchDesign.ControlBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                foreground: NotchDesign.TextSecondaryBrush,
                cornerRadius: 10,
                padding: new Thickness(10, 5, 10, 5));
            stopBtn.FontSize = 11;
            actions.Children.Add(stopBtn);

            Grid.SetRow(actions, 2);
            rightGrid.Children.Add(actions);

            Grid.SetColumn(rightGrid, 2);
            root.Children.Add(rightGrid);

            return root;
        }

        private FrameworkElement BuildReceiveState()
        {
            var root = new Grid { Margin = new Thickness(2, 4, 2, 0) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(154) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var qrStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            string activeUrl = _service.ActiveQrUrl;
            if (!string.IsNullOrEmpty(activeUrl))
                qrStack.Children.Add(CreateQrFrame(activeUrl, 134));

            qrStack.Children.Add(new TextBlock
            {
                Text = !string.IsNullOrEmpty(_statusMessage) ? _statusMessage : L.T("phonetransfer.scan_phone"),
                Foreground = !string.IsNullOrEmpty(_statusMessage)
                    ? new SolidColorBrush(NotchDesign.AccentGreen)
                    : NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            });

            Grid.SetColumn(qrStack, 0);
            root.Children.Add(qrStack);

            var rightGrid = new Grid();
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var received = _service.GetReceivedFiles();
            var header = new TextBlock
            {
                Text = received.Count == 0 ? L.T("phonetransfer.received_title") : L.F("phonetransfer.received_count", received.Count),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 0, 0, 8)
            };
            Grid.SetRow(header, 0);
            rightGrid.Children.Add(header);

            if (received.Count == 0)
            {
                var hint = new TextBlock
                {
                    Text = L.T("phonetransfer.receive_hint"),
                    Foreground = NotchDesign.TextSecondaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(hint, 1);
                rightGrid.Children.Add(hint);
            }
            else
            {
                var scroll = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                NotchDesign.ApplySlimScrollBarStyle(scroll);

                var listStack = new StackPanel();
                foreach (var f in received)
                    listStack.Children.Add(BuildFileRow(f, removable: false));
                scroll.Content = listStack;
                Grid.SetRow(scroll, 1);
                rightGrid.Children.Add(scroll);
            }

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            if (!string.IsNullOrEmpty(activeUrl))
            {
                var copyBtn = NotchDesign.CreateRoundedButton(
                    L.T("phonetransfer.copy_link"),
                    CopyUrl,
                    background: NotchDesign.AccentPrimaryBrush,
                    hoverBackground: NotchDesign.AccentHoverBrush,
                    foreground: Brushes.White,
                    cornerRadius: 10,
                    padding: new Thickness(10, 5, 10, 5));
                copyBtn.FontSize = 11;
                copyBtn.Margin = new Thickness(0, 0, 6, 0);
                actions.Children.Add(copyBtn);
            }

            var folderBtn = NotchDesign.CreateRoundedButton(
                L.T("phonetransfer.open_folder"),
                OpenReceiveFolder,
                background: NotchDesign.ControlBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                foreground: NotchDesign.TextPrimaryBrush,
                cornerRadius: 10,
                padding: new Thickness(10, 5, 10, 5));
            folderBtn.FontSize = 11;
            actions.Children.Add(folderBtn);

            Grid.SetRow(actions, 2);
            rightGrid.Children.Add(actions);

            Grid.SetColumn(rightGrid, 2);
            root.Children.Add(rightGrid);

            return root;
        }

        private FrameworkElement BuildBottomSwitches()
        {
            var bar = new DockPanel
            {
                LastChildFill = false,
                Margin = new Thickness(2, 12, 2, 0)
            };

            bool animateMode = _animateModeSwipe;
            _animateModeSwipe = false;

            var modeSwitch = BuildSwipeSwitch(
                L.T("phonetransfer.send"),
                L.T("phonetransfer.receive"),
                isRightActive: _isReceiveMode,
                animate: animateMode,
                width: 190,
                onSelect: SwitchTransferDirection);
            DockPanel.SetDock(modeSwitch, Dock.Left);
            bar.Children.Add(modeSwitch);

            if (!_isReceiveMode && !SettingsService.Current.IsWorkEdition)
            {
                bool animateTarget = _animateTargetSwipe;
                _animateTargetSwipe = false;

                var targetSwitch = BuildSwipeSwitch(
                    L.T("phonetransfer.online"),
                    L.T("phonetransfer.local"),
                    isRightActive: !_useOnlineMode,
                    animate: animateTarget,
                    width: 156,
                    onSelect: SwitchOnlinePreference);
                DockPanel.SetDock(targetSwitch, Dock.Right);
                bar.Children.Add(targetSwitch);
            }
            else
            {
                var wlanTag = new TextBlock
                {
                    Text = SettingsService.Current.IsWorkEdition && !_isReceiveMode ? L.T("phonetransfer.local_work") : L.T("phonetransfer.local_wlan"),
                    Foreground = NotchDesign.TextSecondaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(wlanTag, Dock.Right);
                bar.Children.Add(wlanTag);
            }

            return bar;
        }

        private static Border BuildSwipeSwitch(
            string leftLabel,
            string rightLabel,
            bool isRightActive,
            bool animate,
            double width,
            Action<bool> onSelect)
        {
            const double height = 30;
            const double pad = 3;
            double thumbWidth = (width - pad * 2) / 2.0;

            var track = new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(12),
                Background = NotchDesign.CardBackgroundBrush,
                BorderBrush = NotchDesign.CardBorderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(pad),
                Cursor = Cursors.Hand
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            double startX = animate
                ? (isRightActive ? 0 : thumbWidth)
                : (isRightActive ? thumbWidth : 0);
            double targetX = isRightActive ? thumbWidth : 0;

            var translate = new TranslateTransform(startX, 0);
            var thumb = new Border
            {
                Width = thumbWidth,
                Height = height - pad * 2 - 2,
                CornerRadius = new CornerRadius(9),
                Background = NotchDesign.CreateSoftAccentTileBrush(0x32),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = translate
            };
            Grid.SetColumnSpan(thumb, 2);
            grid.Children.Add(thumb);

            if (animate && NotchDesign.EffectiveAnimationsEnabled)
            {
                var anim = new DoubleAnimation(startX, targetX, TimeSpan.FromMilliseconds(NotchDesign.MicroTransitionMs))
                {
                    EasingFunction = NotchDesign.CreateSpringEase(0.32)
                };
                translate.BeginAnimation(TranslateTransform.XProperty, anim);
            }

            var leftText = new TextBlock
            {
                Text = leftLabel,
                Foreground = !isRightActive ? NotchDesign.AccentPrimaryBrush : NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                FontWeight = !isRightActive ? FontWeights.SemiBold : FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(leftText, 0);
            grid.Children.Add(leftText);

            var rightText = new TextBlock
            {
                Text = rightLabel,
                Foreground = isRightActive ? NotchDesign.AccentPrimaryBrush : NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                FontWeight = isRightActive ? FontWeights.SemiBold : FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(rightText, 1);
            grid.Children.Add(rightText);

            track.Child = grid;
            track.MouseLeftButtonUp += (_, e) =>
            {
                var pos = e.GetPosition(track);
                bool clickedRight = pos.X >= width / 2.0;
                onSelect(clickedRight == isRightActive ? !isRightActive : clickedRight);
            };
            return track;
        }

        private static Border CreateQrFrame(string url, double size)
        {
            var qrImg = new WpfImage
            {
                Source = PhoneTransferService.GenerateQrBitmap(url, 160),
                Stretch = Stretch.Uniform
            };
            RenderOptions.SetBitmapScalingMode(qrImg, BitmapScalingMode.NearestNeighbor);

            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(12),
                Background = Brushes.White,
                Padding = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = qrImg
            };
        }

        private Border BuildFileRow(PhoneTransferFile file, bool removable)
        {
            var row = new Border
            {
                Background = NotchDesign.NestedCardBackgroundBrush,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 5)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = file.FileName,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            info.Children.Add(new TextBlock
            {
                Text = PhoneTransferService.FormatFileSize(file.FileSize),
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10
            });
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            if (removable)
            {
                var removeBtn = new Button
                {
                    Width = 22,
                    Height = 22,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0),
                    ToolTip = L.T("common.remove"),
                    Content = NotchDesign.CreateVectorIcon(NotchIconKind.Close, 9, NotchDesign.TextPrimaryBrush)
                };
                removeBtn.Click += (_, _) => _service.RemoveFile(file.Id);
                Grid.SetColumn(removeBtn, 1);
                grid.Children.Add(removeBtn);
            }

            row.Child = grid;
            return row;
        }

        private void SwitchTransferDirection(bool receive)
        {
            if (_isReceiveMode == receive) return;
            _isReceiveMode = receive;
            _animateModeSwipe = true;

            if (receive)
            {
                _service.SetReceiveMode(true, startIfReceive: true);
            }
            else
            {
                _service.SetReceiveMode(false, startIfReceive: false);
                if (_service.GetFiles().Count > 0)
                    _ = ActivateSendTransferAsync();
                else
                    _service.StopServer();
            }

            ViewUpdated?.Invoke();
            RaiseStateChanged();
        }

        private void SwitchOnlinePreference(bool localSelected)
        {
            bool online = !localSelected && !SettingsService.Current.IsWorkEdition;
            if (_useOnlineMode == online) return;

            _useOnlineMode = online;
            _animateTargetSwipe = true;
            SettingsService.UpdateQuiet(s => s.PhoneTransferOnlineDefault = online);

            if (!_isReceiveMode && _service.GetFiles().Count > 0)
                _ = ActivateSendTransferAsync();
            else
                ViewUpdated?.Invoke();
        }

        private void PickFiles()
        {
            var dlg = new OpenFileDialog
            {
                Title = L.T("phonetransfer.picker_title"),
                Multiselect = true
            };

            if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
                AddAndActivateFiles(dlg.FileNames);
        }

        private void AddAndActivateFiles(IEnumerable<string> paths)
        {
            _service.AddFiles(paths);
            if (!_isReceiveMode && _service.GetFiles().Count > 0)
                _ = ActivateSendTransferAsync();
        }

        private async Task ActivateSendTransferAsync()
        {
            var files = _service.GetFiles();
            if (files.Count == 0 || _isReceiveMode) return;

            if (_useOnlineMode)
            {
                if (_isUploading) return;
                _isUploading = true;
                ViewUpdated?.Invoke();

                bool ok = await _service.UploadMultipleAsZipAsync(files);
                _isUploading = false;
                if (!ok)
                {
                    ShowTransientStatus(L.T("phonetransfer.upload_failed"));
                    _service.StartLocalServer();
                }
                else
                {
                    ViewUpdated?.Invoke();
                }
            }
            else
            {
                _service.StartLocalServer();
            }
        }

        private void CopyUrl()
        {
            string url = _service.ActiveQrUrl;
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                WpfClipboard.SetText(url);
                ShowTransientStatus(L.T("phonetransfer.link_copied"));
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private static void OpenReceiveFolder()
        {
            try
            {
                Directory.CreateDirectory(PhoneTransferService.ReceiveFolder);
                Process.Start(new ProcessStartInfo("explorer.exe", PhoneTransferService.ReceiveFolder)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        private void ShowTransientStatus(string message)
        {
            _statusMessage = message;
            _statusTimer.Stop();
            _statusTimer.Start();
            ViewUpdated?.Invoke();
        }
    }
}
