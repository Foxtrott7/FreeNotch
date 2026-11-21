using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.UI
{
    /// <summary>
    /// Kompaktes OLED-Steuerband am unteren Rand der Notch für die Live-Tab-Tour (ohne Textblöcke).
    /// </summary>
    public sealed class OnboardingTourBar : UserControl
    {
        private readonly TextBlock _titleText = new();
        private readonly TextBlock _hintText = new();
        private readonly TextBlock _stepText = new();
        private readonly Border _progressFill = new();
        private readonly Border _progressTrack = new();
        private readonly Button _pauseBtn;
        private bool _isPaused;

        public event Action? PrevClicked;
        public event Action? NextClicked;
        public event Action<bool>? PauseToggled;
        public event Action? FinishClicked;

        public OnboardingTourBar()
        {
            var outer = new Border
            {
                CornerRadius = new CornerRadius(NotchDesign.RadiusInner),
                Background = NotchDesign.CardBackgroundBrush,
                Padding = new Thickness(12, 8, 10, 8)
            };

            var stack = new StackPanel();
            var row = new DockPanel { LastChildFill = true };

            var controls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };

            _pauseBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Pause, 12, NotchDesign.TextPrimaryBrush, 2.0),
                TogglePause,
                background: NotchDesign.NestedCardBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                cornerRadius: 13,
                padding: new Thickness(0),
                tooltip: L.T("onboarding.tour_pause"));
            _pauseBtn.Width = 26;
            _pauseBtn.Height = 26;
            controls.Children.Add(_pauseBtn);

            var prevBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.ChevronLeft, 12, NotchDesign.TextPrimaryBrush, 2.0),
                () => PrevClicked?.Invoke(),
                background: NotchDesign.NestedCardBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                cornerRadius: 13,
                padding: new Thickness(0),
                tooltip: L.T("onboarding.tour_back"));
            prevBtn.Width = 26;
            prevBtn.Height = 26;
            prevBtn.Margin = new Thickness(5, 0, 0, 0);
            controls.Children.Add(prevBtn);

            var nextBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.ChevronRight, 12, NotchDesign.TextPrimaryBrush, 2.0),
                () => NextClicked?.Invoke(),
                background: NotchDesign.NestedCardBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                cornerRadius: 13,
                padding: new Thickness(0),
                tooltip: L.T("onboarding.tour_next"));
            nextBtn.Width = 26;
            nextBtn.Height = 26;
            nextBtn.Margin = new Thickness(5, 0, 8, 0);
            controls.Children.Add(nextBtn);

            var finishBtn = NotchDesign.CreateRoundedButton(
                L.T("onboarding.tour_done"),
                () => NotchDesign.RunAfterPressFeedback(() => FinishClicked?.Invoke()),
                background: NotchDesign.AccentPrimaryBrush,
                hoverBackground: NotchDesign.AccentHoverBrush,
                foreground: Brushes.White,
                cornerRadius: NotchDesign.RadiusSmall,
                padding: new Thickness(12, 4, 12, 4));
            controls.Children.Add(finishBtn);

            DockPanel.SetDock(controls, Dock.Right);
            row.Children.Add(controls);

            var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var infoLine = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            _titleText.Foreground = NotchDesign.TextPrimaryBrush;
            _titleText.FontFamily = NotchDesign.UiDisplayFont;
            _titleText.FontWeight = FontWeights.SemiBold;
            _titleText.FontSize = 13;
            infoLine.Children.Add(_titleText);

            _stepText.Foreground = NotchDesign.TextSecondaryBrush;
            _stepText.FontFamily = NotchDesign.UiTextFont;
            _stepText.FontWeight = FontWeights.SemiBold;
            _stepText.FontSize = 11.5;
            _stepText.Margin = new Thickness(10, 0, 0, 0);
            _stepText.VerticalAlignment = VerticalAlignment.Center;
            infoLine.Children.Add(_stepText);
            infoStack.Children.Add(infoLine);

            _hintText.Foreground = NotchDesign.TextSecondaryBrush;
            _hintText.FontFamily = NotchDesign.UiTextFont;
            _hintText.FontSize = 11;
            _hintText.TextTrimming = TextTrimming.CharacterEllipsis;
            _hintText.Margin = new Thickness(0, 1, 0, 0);
            infoStack.Children.Add(_hintText);

            row.Children.Add(infoStack);
            stack.Children.Add(row);

            _progressTrack.Height = 2.5;
            _progressTrack.Margin = new Thickness(0, 7, 0, 0);
            _progressTrack.CornerRadius = new CornerRadius(1.5);
            _progressTrack.Background = NotchDesign.NestedCardBackgroundBrush;
            _progressTrack.ClipToBounds = true;

            _progressFill.Height = 2.5;
            _progressFill.HorizontalAlignment = HorizontalAlignment.Left;
            _progressFill.CornerRadius = new CornerRadius(1.5);
            _progressFill.Background = NotchDesign.CreateAccentGradientBrush(horizontal: true);
            _progressFill.Width = 0;
            _progressTrack.Child = _progressFill;
            stack.Children.Add(_progressTrack);

            outer.Child = stack;
            Content = outer;
        }

        public void ShowModule(INotchModule module, int index, int total, TimeSpan duration, bool paused)
        {
            _isPaused = paused;
            UpdatePauseIcon();
            _titleText.Text = module.Title;
            _hintText.Text = ExampleFor(module.Id);
            _stepText.Text = $"{index + 1} / {total}";
            StartProgressAnimation(duration, paused);
        }

        private static string ExampleFor(string id) => id switch
        {
            "widgets" => L.T("onboarding.tip_widgets"),
            "filetray" => L.T("onboarding.tip_filetray"),
            "clipboard" => L.T("onboarding.tip_clipboard"),
            "media" => L.T("onboarding.tip_media"),
            "notifications" => L.T("onboarding.tip_notifications"),
            "quickactions" => L.T("onboarding.tip_quickactions"),
            "screenocr" => L.T("onboarding.tip_screenocr"),
            "screencapture" => L.T("onboarding.tip_screencapture"),
            "bluetooth" => L.T("onboarding.tip_bluetooth"),
            "filetransfer" => L.T("onboarding.tip_filetransfer"),
            "downloads" => L.T("onboarding.tip_downloads"),
            _ => L.T("onboarding.tip_default")
        };

        public void SetPaused(bool paused, TimeSpan duration)
        {
            _isPaused = paused;
            UpdatePauseIcon();
            if (paused)
                _progressFill.BeginAnimation(WidthProperty, null);
            else
                StartProgressAnimation(duration, paused: false);
        }

        private void TogglePause()
        {
            _isPaused = !_isPaused;
            UpdatePauseIcon();
            PauseToggled?.Invoke(_isPaused);
        }

        private void UpdatePauseIcon()
        {
            _pauseBtn.Content = NotchDesign.CreateVectorIcon(
                _isPaused ? NotchIconKind.Play : NotchIconKind.Pause,
                12,
                NotchDesign.TextPrimaryBrush,
                2.0);
        }

        private void StartProgressAnimation(TimeSpan duration, bool paused)
        {
            _progressFill.BeginAnimation(WidthProperty, null);
            double targetW = Math.Max(180, _progressTrack.ActualWidth > 10 ? _progressTrack.ActualWidth : 360);
            _progressFill.Width = 0;
            if (paused) return;

            var anim = new DoubleAnimation(0, targetW, duration);
            _progressFill.BeginAnimation(WidthProperty, anim);
        }
    }
}
