using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WindowsNotch.Core;

namespace WindowsNotch.Modules.Pet
{
    /// <summary>
    /// Erweiterte Ansicht für das Notch-Maskottchen:
    /// weiße Header-Buttons (Außen-Notch Ein/Aus &amp; Auto-Reset), Füttern, Ball-Minispiel und Stimmungen.
    /// </summary>
    public sealed class PetExpandedView : UserControl
    {
        private readonly PetModule _module;
        private readonly TextBlock _subtitle;
        private readonly Button _idleToggleBtn;
        private readonly Button _sleepBtn;
        private readonly List<(Button Btn, PetMood? Mood)> _moodButtons = new();

        public PetExpandedView(PetModule module)
        {
            _module = module;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Standard-Notchify-Header (34x34 Icon-Host + Titel/Untertitel + weiße 28x28 Action-Buttons)
            var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconHost = new Grid { Width = 34, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
            iconHost.Children.Add(NotchDesign.CreateVectorIcon(NotchIconKind.Pet, 21, NotchDesign.AccentPrimaryBrush, 1.9));
            header.Children.Add(iconHost);

            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Text = "Notch-Maskottchen",
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold
            });
            _subtitle = new TextBlock
            {
                Foreground = NotchDesign.TextMutedBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5
            };
            titleStack.Children.Add(_subtitle);
            Grid.SetColumn(titleStack, 1);
            header.Children.Add(titleStack);

            var rightActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            _idleToggleBtn = CreateWhiteHeaderButton(
                NotchIconKind.Monitor,
                "Fuchs oben in der Notch ein-/ausschalten",
                () => _module.SetShowInIdleNotch(!_module.ShowInIdleNotch));
            _idleToggleBtn.Margin = new Thickness(0, 0, 6, 0);
            rightActions.Children.Add(_idleToggleBtn);

            var resetBtn = CreateWhiteHeaderButton(
                NotchIconKind.Reset,
                "Automatische Stimmung wiederherstellen",
                () => _module.SetMood(PetMood.Cozy, autoMood: true));
            rightActions.Children.Add(resetBtn);

            Grid.SetColumn(rightActions, 2);
            header.Children.Add(rightActions);
            root.Children.Add(header);

            // Interaktive Fuchs-Bühne
            var petCard = new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = NotchDesign.CardBackgroundBrush,
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var cardGrid = new Grid();
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            cardGrid.Children.Add(new PetVisualControl(_module, compact: false));

            var hint = new TextBlock
            {
                Text = "Streicheln, anklicken, füttern oder Ball werfen",
                Foreground = NotchDesign.TextMutedBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            };
            Grid.SetRow(hint, 1);
            cardGrid.Children.Add(hint);

            petCard.Child = cardGrid;
            Grid.SetRow(petCard, 1);
            root.Children.Add(petCard);

            // Aktionen & Stimmungen
            var bottomBar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
            AddActionPill(bottomBar, "Füttern", () => _module.RequestFeed(), accent: true);
            AddActionPill(bottomBar, "Ball werfen", () => _module.RequestPlayBall(), accent: true);
            _sleepBtn = AddActionPill(bottomBar, "Schlafen", ToggleSleep, accent: false);
            AddMoodPill(bottomBar, "Auto", null);
            AddMoodPill(bottomBar, "Entspannt", PetMood.Cozy);

            Grid.SetRow(bottomBar, 2);
            root.Children.Add(bottomBar);

            Content = root;

            Loaded += (_, _) =>
            {
                _module.PetStateChanged += OnPetStateChanged;
                RefreshUi();
            };
            Unloaded += (_, _) => _module.PetStateChanged -= OnPetStateChanged;

            RefreshUi();
        }

        private static Button CreateWhiteHeaderButton(NotchIconKind kind, string tooltip, Action onClick)
        {
            var btn = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Background = NotchDesign.TabActiveBackgroundBrush,
                BorderThickness = new Thickness(0),
                ToolTip = tooltip,
                Cursor = System.Windows.Input.Cursors.Hand,
                Content = NotchDesign.CreateVectorIcon(kind, 13, Brushes.White, 1.85)
            };
            btn.Resources.Add(typeof(Border), new Style(typeof(Border))
            {
                Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(14)) }
            });
            btn.Click += (_, _) => onClick();
            return btn;
        }

        private Button AddActionPill(Panel parent, string label, Action onClick, bool accent)
        {
            var btn = new Button
            {
                Content = label,
                Margin = new Thickness(3, 0, 3, 0),
                Padding = new Thickness(10, 5, 10, 5),
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = accent ? NotchDesign.AccentPrimaryBrush : NotchDesign.TextPrimaryBrush,
                Background = accent ? NotchDesign.CreateSoftAccentTileBrush(0x2A) : NotchDesign.CardBackgroundBrush,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.Resources.Add(typeof(Border), new Style(typeof(Border))
            {
                Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(12)) }
            });
            btn.Click += (_, _) => onClick();
            parent.Children.Add(btn);
            return btn;
        }

        private void AddMoodPill(Panel parent, string label, PetMood? mood)
        {
            var btn = new Button
            {
                Content = label,
                Margin = new Thickness(3, 0, 3, 0),
                Padding = new Thickness(10, 5, 10, 5),
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.Resources.Add(typeof(Border), new Style(typeof(Border))
            {
                Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(12)) }
            });
            btn.Click += (_, _) =>
            {
                if (mood is null)
                    _module.SetMood(PetMood.Cozy, autoMood: true);
                else
                    _module.SetMood(mood.Value, autoMood: false);
            };
            _moodButtons.Add((btn, mood));
            parent.Children.Add(btn);
        }

        private void ToggleSleep()
        {
            if (_module.CurrentMood == PetMood.Sleepy)
                _module.SetMood(PetMood.Cozy, autoMood: true);
            else
                _module.SetMood(PetMood.Sleepy, autoMood: false);
        }

        private void OnPetStateChanged(object? sender, EventArgs e)
            => Dispatcher.BeginInvoke(RefreshUi);

        private void RefreshUi()
        {
            string notchState = _module.ShowInIdleNotch ? "Notch an" : "Notch aus";
            _subtitle.Text = $"{_module.GetMoodLabel()} · {notchState} · Satt {_module.HungerPercent}% · {_module.CatchCount}× Ball";

            _idleToggleBtn.Opacity = _module.ShowInIdleNotch ? 1.0 : 0.45;
            _sleepBtn.Content = _module.CurrentMood == PetMood.Sleepy ? "Aufwecken" : "Schlafen";

            foreach (var (btn, mood) in _moodButtons)
            {
                bool active = mood is null ? _module.AutoMood : (!_module.AutoMood && _module.CurrentMood == mood.Value);
                btn.Background = active
                    ? NotchDesign.CreateSoftAccentTileBrush(0x32)
                    : NotchDesign.CardBackgroundBrush;
                btn.Foreground = active
                    ? NotchDesign.AccentPrimaryBrush
                    : NotchDesign.TextMutedBrush;
            }
        }
    }
}
