using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.UI
{
    public partial class NotchWindow
    {
        private bool _isPowerMenuOpen;
        private bool _isPowerTimerViewOpen;
        private bool _isQuitting;
        private ScheduledPowerAction _draftScheduleAction = ScheduledPowerAction.Shutdown;
        private int _draftScheduleHour;
        private int _draftScheduleMinute;

        private double CurrentPowerMenuHeight
            => _isPowerTimerViewOpen ? 326 : 278;

        private void Power_Click(object sender, RoutedEventArgs e)
        {
            _isOnboardingOpen = false;
            if (_state == NotchState.Expanded && _isPowerMenuOpen)
            {
                ClosePowerMenu();
                return;
            }

            _isSettingsOpen = false;
            UpdateSettingsButtonState();
            _isPowerMenuOpen = true;
            _isPowerTimerViewOpen = false;
            InitDraftScheduleTime();

            if (_state != NotchState.Expanded)
            {
                SetState(NotchState.Expanded);
                Activate();
                return;
            }

            ShowPowerMenuInHost();
            AnimateExpandedSize();
            AnimatePowerHostTransition();
            Activate();
        }

        private void ClosePowerMenu()
        {
            if (!_isPowerMenuOpen) return;
            _isPowerMenuOpen = false;
            _isPowerTimerViewOpen = false;
            UpdatePowerButtonState();

            if (_state == NotchState.Expanded)
            {
                if (_selectedModule != null)
                {
                    SelectModule(_selectedModule);
                }
                else
                {
                    BuildTabs();
                    AnimateExpandedSize();
                }
            }
        }

        private void InitDraftScheduleTime()
        {
            var svc = PowerScheduleService.Instance;
            if (svc.IsActive && svc.TargetLocalTime is DateTime activeTarget)
            {
                _draftScheduleAction = svc.Action;
                _draftScheduleHour = activeTarget.Hour;
                _draftScheduleMinute = activeTarget.Minute;
                return;
            }

            var preset = DateTime.Now.AddMinutes(30);
            int roundedMin = ((preset.Minute + 4) / 5) * 5;
            if (roundedMin >= 60)
            {
                preset = preset.AddHours(1);
                roundedMin = 0;
            }
            _draftScheduleHour = preset.Hour;
            _draftScheduleMinute = roundedMin;
        }

        private void ShowPowerMenuInHost()
        {
            _fileTrayAutoCloseTimer.Stop();
            EmptyModulesPanel.Visibility = Visibility.Collapsed;
            ExpandedHost.Visibility = Visibility.Visible;

            foreach (var child in TabContainer.Children)
            {
                if (child is Button btn && btn.DataContext is INotchModule m)
                {
                    btn.Tag = null;
                    btn.Content = NotchDesign.CreateVectorIcon(m.IconKind, 15, NotchDesign.TabIconInactiveBrush, 2.25);
                }
            }

            ExpandedHost.Content = _isPowerTimerViewOpen
                ? CreatePowerScheduleView()
                : CreatePowerMenuView();
            UpdatePowerButtonState();
            UpdateKeepExpandedCloseButton();
        }

        private void SwitchPowerSubView(bool openTimerView)
        {
            _isPowerTimerViewOpen = openTimerView;
            if (openTimerView)
                InitDraftScheduleTime();

            ExpandedHost.Content = openTimerView
                ? CreatePowerScheduleView()
                : CreatePowerMenuView();
            AnimateExpandedSize();
            if (ExpandedHost.Content is UIElement el)
                NotchDesign.AnimateSubViewEnter(el);
        }

        private void UpdatePowerButtonState()
        {
            PowerBtn.Tag = _isPowerMenuOpen ? "Selected" : null;
            PowerIconPath.Stroke = _isPowerMenuOpen ? NotchDesign.TextPrimaryBrush : NotchDesign.TabIconInactiveBrush;
        }

        private void AnimatePowerHostTransition()
        {
            if (!NotchDesign.EffectiveAnimationsEnabled) return;

            var scale = new ScaleTransform(0.95, 0.95);
            var translate = new TranslateTransform(0, -4);
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(translate);

            ExpandedHost.RenderTransformOrigin = new Point(0.5, 0.25);
            ExpandedHost.RenderTransform = group;
            ExpandedHost.BeginAnimation(OpacityProperty, null);
            ExpandedHost.Opacity = 0.0;

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (!_isPowerMenuOpen) return;
                var ease = NotchDesign.CreateSpringEase(0.42);
                var dur = TimeSpan.FromMilliseconds(NotchDesign.TabSwitchDurationMs);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateAnim(1.0, dur, ease));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateAnim(1.0, dur, ease));
                translate.BeginAnimation(TranslateTransform.YProperty, CreateAnim(0.0, dur, ease));
                ExpandedHost.BeginAnimation(OpacityProperty,
                    CreateAnim(1.0, dur, NotchDesign.CreateEaseInOut()));
            });
        }

        private UIElement CreatePowerMenuView()
        {
            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };

            var redBrush = NotchDesign.GetFrozenBrush(NotchDesign.AccentRed);

            stack.Children.Add(CreatePowerMenuRow(
                L.T("notch.power.quit"),
                NotchIconKind.Close,
                NotchDesign.TextSecondaryBrush,
                NotchDesign.TextPrimaryBrush,
                ShutdownGracefully));

            stack.Children.Add(CreatePowerMenuRow(
                L.T("notch.power.sleep"),
                NotchIconKind.Moon,
                NotchDesign.TextSecondaryBrush,
                NotchDesign.TextPrimaryBrush,
                () =>
                {
                    SetState(NotchState.Collapsed);
                    PowerScheduleService.TriggerSystemSleep();
                }));

            // 3. Zeile direkt unter Ruhemodus: Zeitplan / Timer (zeigt aktiven Countdown-Badge rechts)
            var scheduleBadge = new TextBlock
            {
                Foreground = NotchDesign.AccentPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Visibility = Visibility.Collapsed
            };
            System.Windows.Documents.Typography.SetNumeralAlignment(scheduleBadge, FontNumeralAlignment.Tabular);

            var scheduleRow = CreatePowerMenuRow(
                L.T("notch.power.schedule"),
                NotchIconKind.Clock,
                PowerScheduleService.Instance.IsActive ? NotchDesign.AccentPrimaryBrush : NotchDesign.TextSecondaryBrush,
                NotchDesign.TextPrimaryBrush,
                () => SwitchPowerSubView(openTimerView: true),
                trailingBadge: scheduleBadge);

            void RefreshBadge()
            {
                var svc = PowerScheduleService.Instance;
                if (!svc.IsActive)
                {
                    scheduleBadge.Visibility = Visibility.Collapsed;
                    return;
                }
                var rem = svc.Remaining;
                scheduleBadge.Text = rem.TotalHours >= 1
                    ? $"{(int)rem.TotalHours:D2}:{rem.Minutes:D2}:{rem.Seconds:D2}"
                    : $"{rem.Minutes:D2}:{rem.Seconds:D2}";
                scheduleBadge.Visibility = Visibility.Visible;
            }

            void OnScheduleChanged(object? s, EventArgs e) => RefreshBadge();
            scheduleRow.Loaded += (_, _) =>
            {
                PowerScheduleService.Instance.Changed += OnScheduleChanged;
                RefreshBadge();
            };
            scheduleRow.Unloaded += (_, _) =>
            {
                PowerScheduleService.Instance.Changed -= OnScheduleChanged;
            };

            stack.Children.Add(scheduleRow);

            stack.Children.Add(CreateConfirmablePowerRow(
                L.T("notch.power.restart"),
                L.T("notch.power.restart_confirm"),
                NotchIconKind.Reset,
                NotchDesign.TextSecondaryBrush,
                NotchDesign.TextPrimaryBrush,
                NotchDesign.AccentPrimaryBrush,
                () => PowerScheduleService.RunShutdownCommand("/r /t 0")));

            stack.Children.Add(CreateConfirmablePowerRow(
                L.T("notch.power.shutdown"),
                L.T("notch.power.shutdown_confirm"),
                NotchIconKind.Power,
                redBrush,
                redBrush,
                redBrush,
                () => PowerScheduleService.RunShutdownCommand("/s /t 0"),
                marginBottom: 0));
            return stack;
        }

        private UIElement CreatePowerScheduleView()
        {
            var root = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };

            // Kopfzeile mit Zurück-Button & Titel
            var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var backBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.ChevronLeft, 13, NotchDesign.TextPrimaryBrush, 2.0),
                () => SwitchPowerSubView(openTimerView: false),
                tooltip: L.T("notch.power.back"));
            backBtn.Width = 28;
            backBtn.Height = 28;
            backBtn.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(backBtn, 0);
            header.Children.Add(backBtn);

            var title = new TextBlock
            {
                Text = L.T("notch.power.schedule_title"),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(title, 1);
            header.Children.Add(title);
            root.Children.Add(header);

            // Aktions-Auswahl (2x2 Grid)
            var actionGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            actionGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            actionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var actionChips = new (ScheduledPowerAction Action, string Label, NotchIconKind Icon, int Row, int Col)[]
            {
                (ScheduledPowerAction.Shutdown, L.T("notch.power.shutdown"), NotchIconKind.Power, 0, 0),
                (ScheduledPowerAction.Restart,  L.T("notch.power.restart"),  NotchIconKind.Reset, 0, 2),
                (ScheduledPowerAction.LogOff,   L.T("notch.power.logoff"),   NotchIconKind.Lock,  2, 0),
                (ScheduledPowerAction.Sleep,    L.T("notch.power.sleep"),    NotchIconKind.Moon,  2, 2),
            };

            var chipBorders = new Border[actionChips.Length];
            var chipTexts = new TextBlock[actionChips.Length];

            void UpdateChipStyles()
            {
                for (int i = 0; i < actionChips.Length; i++)
                {
                    bool selected = actionChips[i].Action == _draftScheduleAction;
                    chipBorders[i].Background = selected
                        ? NotchDesign.CreateSoftAccentCardBrush()
                        : NotchDesign.CardBackgroundBrush;
                    chipBorders[i].BorderBrush = selected
                        ? NotchDesign.AccentPrimaryBrush
                        : NotchDesign.CardBorderBrush;
                    chipTexts[i].Foreground = selected
                        ? NotchDesign.TextPrimaryBrush
                        : NotchDesign.TextSecondaryBrush;
                }
            }

            for (int i = 0; i < actionChips.Length; i++)
            {
                var item = actionChips[i];
                var scale = new ScaleTransform(1.0, 1.0);
                var border = new Border
                {
                    CornerRadius = new CornerRadius(NotchDesign.RadiusControl),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 7, 8, 7),
                    Cursor = Cursors.Hand,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = scale
                };

                var inner = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                var ic = NotchDesign.CreateVectorIcon(item.Icon, 12,
                    item.Action == ScheduledPowerAction.Shutdown ? NotchDesign.GetFrozenBrush(NotchDesign.AccentRed) : NotchDesign.TextSecondaryBrush,
                    1.9);
                ic.Margin = new Thickness(0, 0, 5, 0);
                ic.VerticalAlignment = VerticalAlignment.Center;
                var txt = new TextBlock
                {
                    Text = item.Label,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11.5,
                    FontWeight = FontWeights.Medium,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                inner.Children.Add(ic);
                inner.Children.Add(txt);
                border.Child = inner;

                border.MouseLeftButtonDown += (_, _) => AnimateRowScale(scale, 0.95, NotchDesign.ButtonPressDurationMs);
                border.MouseLeave += (_, _) => AnimateRowScale(scale, 1.0, NotchDesign.ButtonReleaseDurationMs);
                border.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    AnimateRowScale(scale, 1.0, NotchDesign.ButtonReleaseDurationMs);
                    _draftScheduleAction = item.Action;
                    UpdateChipStyles();
                };

                Grid.SetRow(border, item.Row);
                Grid.SetColumn(border, item.Col);
                actionGrid.Children.Add(border);
                chipBorders[i] = border;
                chipTexts[i] = txt;
            }
            UpdateChipStyles();
            root.Children.Add(actionGrid);

            // Uhrzeit-Stepper-Karte (HH : mm)
            var timeCard = new Border
            {
                Background = NotchDesign.CardBackgroundBrush,
                CornerRadius = new CornerRadius(NotchDesign.RadiusInner),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var timeGrid = new Grid();
            timeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            timeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            timeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var hourText = new TextBlock
            {
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0)
            };
            System.Windows.Documents.Typography.SetNumeralAlignment(hourText, FontNumeralAlignment.Tabular);

            var minuteText = new TextBlock
            {
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0)
            };
            System.Windows.Documents.Typography.SetNumeralAlignment(minuteText, FontNumeralAlignment.Tabular);

            void RefreshTimeDisplay()
            {
                hourText.Text = _draftScheduleHour.ToString("D2");
                minuteText.Text = _draftScheduleMinute.ToString("D2");
            }
            RefreshTimeDisplay();

            var hourStepper = CreateTimeStepperColumn(
                () => { _draftScheduleHour = (_draftScheduleHour + 23) % 24; RefreshTimeDisplay(); },
                hourText,
                () => { _draftScheduleHour = (_draftScheduleHour + 1) % 24; RefreshTimeDisplay(); });
            Grid.SetColumn(hourStepper, 0);

            var colon = new TextBlock
            {
                Text = ":",
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, -2, 2, 0)
            };
            Grid.SetColumn(colon, 1);

            var minStepper = CreateTimeStepperColumn(
                () => { _draftScheduleMinute = (_draftScheduleMinute + 55) % 60; RefreshTimeDisplay(); },
                minuteText,
                () => { _draftScheduleMinute = (_draftScheduleMinute + 5) % 60; RefreshTimeDisplay(); });
            Grid.SetColumn(minStepper, 2);

            timeGrid.Children.Add(hourStepper);
            timeGrid.Children.Add(colon);
            timeGrid.Children.Add(minStepper);
            timeCard.Child = timeGrid;
            root.Children.Add(timeCard);

            // Schnell-Presets (+15m / +30m / +1h)
            var presetsGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            presetsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            presetsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            presetsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            presetsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            presetsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            void ApplyPresetMinutes(int minutes)
            {
                var target = DateTime.Now.AddMinutes(minutes);
                _draftScheduleHour = target.Hour;
                _draftScheduleMinute = target.Minute;
                RefreshTimeDisplay();
            }

            var p15 = CreatePresetChip(L.T("notch.power.preset_15m"), () => ApplyPresetMinutes(15));
            var p30 = CreatePresetChip(L.T("notch.power.preset_30m"), () => ApplyPresetMinutes(30));
            var p60 = CreatePresetChip(L.T("notch.power.preset_1h"), () => ApplyPresetMinutes(60));
            Grid.SetColumn(p15, 0);
            Grid.SetColumn(p30, 2);
            Grid.SetColumn(p60, 4);
            presetsGrid.Children.Add(p15);
            presetsGrid.Children.Add(p30);
            presetsGrid.Children.Add(p60);
            root.Children.Add(presetsGrid);

            // Statuszeile + Start/Abbrechen-Button
            var statusText = new TextBlock
            {
                Foreground = NotchDesign.AccentPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6),
                Visibility = Visibility.Collapsed
            };
            System.Windows.Documents.Typography.SetNumeralAlignment(statusText, FontNumeralAlignment.Tabular);
            root.Children.Add(statusText);

            var actionButtonsGrid = new Grid();
            actionButtonsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionButtonsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var startBtn = NotchDesign.CreateRoundedButton(
                L.T("notch.power.schedule_start"),
                () =>
                {
                    PowerScheduleService.Instance.ScheduleAtTime(_draftScheduleHour, _draftScheduleMinute, _draftScheduleAction);
                    SwitchPowerSubView(openTimerView: false);
                },
                background: NotchDesign.AccentPrimaryBrush,
                hoverBackground: NotchDesign.AccentHoverBrush,
                foreground: Brushes.White);
            startBtn.Height = 32;
            Grid.SetColumn(startBtn, 0);

            var cancelBtn = NotchDesign.CreateRoundedButton(
                new TextBlock
                {
                    Text = L.T("notch.power.schedule_cancel"),
                    Foreground = NotchDesign.GetFrozenBrush(NotchDesign.AccentRed),
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold
                },
                () =>
                {
                    PowerScheduleService.Instance.Cancel();
                });
            cancelBtn.Height = 32;
            cancelBtn.Padding = new Thickness(12, 0, 12, 0);
            cancelBtn.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(cancelBtn, 1);

            actionButtonsGrid.Children.Add(startBtn);
            actionButtonsGrid.Children.Add(cancelBtn);
            root.Children.Add(actionButtonsGrid);

            void RefreshActiveTimerUi()
            {
                var svc = PowerScheduleService.Instance;
                if (svc.IsActive && svc.TargetLocalTime is DateTime target)
                {
                    var rem = svc.Remaining;
                    string remText = rem.TotalHours >= 1
                        ? $"{(int)rem.TotalHours:D2}:{rem.Minutes:D2}:{rem.Seconds:D2}"
                        : $"{rem.Minutes:D2}:{rem.Seconds:D2}";
                    string actionName = svc.Action switch
                    {
                        ScheduledPowerAction.Shutdown => L.T("notch.power.shutdown"),
                        ScheduledPowerAction.Restart => L.T("notch.power.restart"),
                        ScheduledPowerAction.LogOff => L.T("notch.power.logoff"),
                        _ => L.T("notch.power.sleep")
                    };
                    statusText.Text = L.F("notch.power.schedule_active", actionName, target.ToString("HH:mm"), remText);
                    statusText.Visibility = Visibility.Visible;
                    cancelBtn.Visibility = Visibility.Visible;
                    startBtn.Content = L.T("notch.power.schedule_update");
                }
                else
                {
                    statusText.Visibility = Visibility.Collapsed;
                    cancelBtn.Visibility = Visibility.Collapsed;
                    startBtn.Content = L.T("notch.power.schedule_start");
                }
            }

            void OnSvcChanged(object? s, EventArgs e) => RefreshActiveTimerUi();
            root.Loaded += (_, _) =>
            {
                PowerScheduleService.Instance.Changed += OnSvcChanged;
                RefreshActiveTimerUi();
            };
            root.Unloaded += (_, _) =>
            {
                PowerScheduleService.Instance.Changed -= OnSvcChanged;
            };

            return root;
        }

        private static UIElement CreateTimeStepperColumn(Action onMinus, TextBlock valueText, Action onPlus)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var minusBtn = CreateSmallStepperButton(NotchIconKind.Minus, onMinus);
            var plusBtn = CreateSmallStepperButton(NotchIconKind.Plus, onPlus);

            panel.Children.Add(minusBtn);
            panel.Children.Add(valueText);
            panel.Children.Add(plusBtn);
            return panel;
        }

        private static Border CreateSmallStepperButton(NotchIconKind icon, Action onClick)
        {
            var scale = new ScaleTransform(1.0, 1.0);
            var btn = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(7),
                Background = NotchDesign.NestedCardBackgroundBrush,
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
                Child = NotchDesign.CreateVectorIcon(icon, 11, NotchDesign.TextSecondaryBrush, 2.0)
            };
            btn.MouseEnter += (_, _) => btn.Background = NotchDesign.ControlHoverBrush;
            btn.MouseLeave += (_, _) =>
            {
                btn.Background = NotchDesign.NestedCardBackgroundBrush;
                AnimateRowScale(scale, 1.0, NotchDesign.ButtonReleaseDurationMs);
            };
            btn.MouseLeftButtonDown += (_, _) => AnimateRowScale(scale, 0.92, NotchDesign.ButtonPressDurationMs);
            btn.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                AnimateRowScale(scale, 1.0, NotchDesign.ButtonReleaseDurationMs);
                onClick();
            };
            return btn;
        }

        private static Border CreatePresetChip(string label, Action onClick)
        {
            var scale = new ScaleTransform(1.0, 1.0);
            var chip = new Border
            {
                Background = NotchDesign.CardBackgroundBrush,
                CornerRadius = new CornerRadius(NotchDesign.RadiusSmall),
                Padding = new Thickness(6, 5, 6, 5),
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
                Child = new TextBlock
                {
                    Text = label,
                    Foreground = NotchDesign.TextSecondaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11.5,
                    FontWeight = FontWeights.Medium,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };
            chip.MouseEnter += (_, _) => chip.Background = NotchDesign.ControlHoverBrush;
            chip.MouseLeave += (_, _) =>
            {
                chip.Background = NotchDesign.CardBackgroundBrush;
                AnimateRowScale(scale, 1.0, NotchDesign.ButtonReleaseDurationMs);
            };
            chip.MouseLeftButtonDown += (_, _) => AnimateRowScale(scale, 0.94, NotchDesign.ButtonPressDurationMs);
            chip.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                AnimateRowScale(scale, 1.0, NotchDesign.ButtonReleaseDurationMs);
                onClick();
            };
            return chip;
        }

        private static Border CreatePowerMenuRow(
            string label,
            NotchIconKind icon,
            Brush iconBrush,
            Brush textBrush,
            Action onClick,
            UIElement? trailingBadge = null,
            double marginBottom = 6)
        {
            var idleBg = NotchDesign.CardBackgroundBrush;
            var hoverBg = NotchDesign.ControlHoverBrush;

            var scale = new ScaleTransform(1.0, 1.0);
            var row = new Border
            {
                Background = idleBg,
                CornerRadius = new CornerRadius(NotchDesign.RadiusControl),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, marginBottom),
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconEl = NotchDesign.CreateVectorIcon(icon, 14, iconBrush);
            iconEl.Margin = new Thickness(0, 0, 10, 0);
            iconEl.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(iconEl, 0);

            var textEl = new TextBlock
            {
                Text = label,
                Foreground = textBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12.5,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(textEl, 1);

            grid.Children.Add(iconEl);
            grid.Children.Add(textEl);
            if (trailingBadge != null)
            {
                Grid.SetColumn(trailingBadge, 2);
                grid.Children.Add(trailingBadge);
            }
            row.Child = grid;

            row.MouseEnter += (_, _) => row.Background = hoverBg;
            row.MouseLeave += (_, _) =>
            {
                row.Background = idleBg;
                AnimateRowScale(scale, 1.0, NotchDesign.ButtonReleaseDurationMs);
            };
            row.MouseLeftButtonDown += (_, _) =>
                AnimateRowScale(scale, 0.96, NotchDesign.ButtonPressDurationMs);
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                AnimateRowScale(scale, 1.0, NotchDesign.ButtonReleaseDurationMs);
                onClick();
            };

            return row;
        }

        private static void AnimateRowScale(ScaleTransform scale, double target, int durationMs)
        {
            if (!NotchDesign.EffectiveAnimationsEnabled)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = target;
                scale.ScaleY = target;
                return;
            }
            var anim = CreateAnim(target, TimeSpan.FromMilliseconds(durationMs), NotchDesign.CreateEaseOut());
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        /// <summary>
        /// Power-Zeile mit Bestätigung per erneutem Klick auf denselben Button (Schritt 7).
        /// Erster Klick schaltet den Button in den Bestätigungszustand; zweiter Klick führt aus.
        /// </summary>
        private static UIElement CreateConfirmablePowerRow(
            string label,
            string confirmLabel,
            NotchIconKind icon,
            Brush iconBrush,
            Brush textBrush,
            Brush confirmAccentBrush,
            Action onConfirm,
            double marginBottom = 6)
        {
            var idleBg = NotchDesign.CardBackgroundBrush;
            var hoverBg = NotchDesign.ControlHoverBrush;
            var confirmTint = new SolidColorBrush(Color.FromArgb(34,
                ((SolidColorBrush)confirmAccentBrush).Color.R,
                ((SolidColorBrush)confirmAccentBrush).Color.G,
                ((SolidColorBrush)confirmAccentBrush).Color.B));
            confirmTint.Freeze();

            var scale = new ScaleTransform(1.0, 1.0);
            bool armed = false;
            var resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };

            var row = new Border
            {
                Background = idleBg,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(NotchDesign.RadiusControl),
                Padding = new Thickness(11, 8, 11, 8),
                Margin = new Thickness(0, 0, 0, marginBottom),
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconHost = new ContentControl
            {
                Content = NotchDesign.CreateVectorIcon(icon, 14, iconBrush),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(iconHost, 0);

            var textEl = new TextBlock
            {
                Text = label,
                Foreground = textBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12.5,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(textEl, 1);

            var hintEl = new TextBlock
            {
                Text = L.T("notch.power.click_again"),
                Foreground = confirmAccentBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Visibility = Visibility.Collapsed
            };
            Grid.SetColumn(hintEl, 2);

            grid.Children.Add(iconHost);
            grid.Children.Add(textEl);
            grid.Children.Add(hintEl);
            row.Child = grid;

            void SetArmed(bool value)
            {
                armed = value;
                if (armed)
                {
                    row.Background = confirmTint;
                    row.BorderBrush = confirmAccentBrush;
                    textEl.Text = confirmLabel;
                    textEl.Foreground = confirmAccentBrush;
                    textEl.FontWeight = FontWeights.SemiBold;
                    iconHost.Content = NotchDesign.CreateVectorIcon(NotchIconKind.Check, 14, confirmAccentBrush, 2.1);
                    hintEl.Visibility = Visibility.Visible;
                    resetTimer.Stop();
                    resetTimer.Start();
                }
                else
                {
                    resetTimer.Stop();
                    row.Background = row.IsMouseOver ? hoverBg : idleBg;
                    row.BorderBrush = Brushes.Transparent;
                    textEl.Text = label;
                    textEl.Foreground = textBrush;
                    textEl.FontWeight = FontWeights.Medium;
                    iconHost.Content = NotchDesign.CreateVectorIcon(icon, 14, iconBrush);
                    hintEl.Visibility = Visibility.Collapsed;
                }
            }

            resetTimer.Tick += (_, _) => SetArmed(false);
            row.Unloaded += (_, _) => resetTimer.Stop();

            row.MouseEnter += (_, _) =>
            {
                if (!armed) row.Background = hoverBg;
            };
            row.MouseLeave += (_, _) =>
            {
                if (!armed) row.Background = idleBg;
                AnimateRowScale(scale, 1.0, NotchDesign.ButtonReleaseDurationMs);
            };
            row.MouseLeftButtonDown += (_, _) =>
                AnimateRowScale(scale, 0.96, NotchDesign.ButtonPressDurationMs);
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                AnimateRowScale(scale, 1.0, NotchDesign.ButtonReleaseDurationMs);
                if (!armed)
                {
                    SetArmed(true);
                }
                else
                {
                    SetArmed(false);
                    onConfirm();
                }
            };

            return row;
        }

        private void ShutdownGracefully()
        {
            if (_isQuitting) return;
            _isQuitting = true;

            if (!NotchDesign.EffectiveAnimationsEnabled)
            {
                Application.Current?.Shutdown();
                return;
            }

            // Erst komplett in die kompakte Notch einklappen, danach ohne Ausblenden in den Bildschirmrand einziehen
            SetState(NotchState.Collapsed);

            var collapseTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(NotchDesign.CollapseDurationMs)
            };
            collapseTimer.Tick += (_, _) =>
            {
                collapseTimer.Stop();
                var (hideX, hideY) = GetSuppressSlideOffset();
                var slideDur = TimeSpan.FromMilliseconds(NotchDesign.ScaleDuration(220));
                var slideEase = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn };
                var animX = CreateAnim(hideX, slideDur, slideEase, activeTransition: true);
                var animY = CreateAnim(hideY, slideDur, slideEase, activeTransition: true);
                animY.Completed += (_, _) => Application.Current?.Shutdown();
                NotchSuppressTransform.BeginAnimation(TranslateTransform.XProperty, animX);
                NotchSuppressTransform.BeginAnimation(TranslateTransform.YProperty, animY);
            };
            collapseTimer.Start();
        }
    }
}
