using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WindowsNotch.Core;

namespace WindowsNotch.Modules.Generic
{
    /// <summary>
    /// Platzhalter-Modul im Notchify-Design für Module aus der Roadmap,
    /// die in den folgenden Ausbaustufen (Phase 2.5+) implementiert werden.
    /// </summary>
    public class GenericNotchModule : NotchModuleBase
    {
        public override string Id { get; }
        public override string Title { get; }
        public override NotchIconKind IconKind => MapIcon(Id);
        public override bool ShowInTabBar => false;
        public override int Priority => 1;
        public override double? PreferredPreviewWidth => 220;
        public override (double Width, double Height)? PreferredExpandedSize => (480, 300);

        public GenericNotchModule(string id, string title)
        {
            Id = id;
            Title = title;
        }

        private static NotchIconKind MapIcon(string id) => id switch
        {
            "filetray" => NotchIconKind.FileTray,
            "clipboard" => NotchIconKind.Clipboard,
            "screencapture" => NotchIconKind.Record,
            "bluetooth" => NotchIconKind.Bluetooth,
            "discord" => NotchIconKind.Discord,
            "filetransfer" => NotchIconKind.Phone,
            "downloads" => NotchIconKind.Download,
            "lyrics" => NotchIconKind.Music,
            "screenocr" => NotchIconKind.Scan,
            "calendar" => NotchIconKind.Calendar,
            "timer" => NotchIconKind.Timer,
            "shortcuts" => NotchIconKind.Bolt,
            "controlcenter" => NotchIconKind.Settings,
            "privacy" => NotchIconKind.Shield,
            "gaming" or "performance" or "gaminghud" => NotchIconKind.Pulse,
            _ => NotchIconKind.Sparkle
        };

        public override FrameworkElement CreatePreviewView()
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var icon = NotchDesign.CreateVectorIcon(IconKind, 13, NotchDesign.AccentPrimaryBrush);
            icon.Margin = new Thickness(0, 0, 7, 0);

            var text = new TextBlock
            {
                Text = Title,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };

            stack.Children.Add(icon);
            stack.Children.Add(text);
            return stack;
        }

        public override FrameworkElement CreateExpandedView()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Kopfzeile im Notchify-Stil (großes Icon-Quadrat + Titel + Untertitel)
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var iconBox = new Border
            {
                Width = 42,
                Height = 42,
                CornerRadius = new CornerRadius(12),
                Background = NotchDesign.NestedCardBackgroundBrush,
                Margin = new Thickness(0, 0, 12, 0),
                Child = NotchDesign.CreateVectorIcon(IconKind, 20, NotchDesign.TextPrimaryBrush)
            };
            header.Children.Add(iconBox);

            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Text = Title,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 16,
                FontWeight = FontWeights.Bold
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = "Nächster Ausbauschritt laut Roadmap",
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                Margin = new Thickness(0, 2, 0, 0)
            });
            header.Children.Add(titleStack);

            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // Zentrale ruhige Karte
            var card = NotchDesign.CreateCard(16, new Thickness(18, 16, 18, 16));
            var center = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var bigIcon = NotchDesign.CreateVectorIcon(IconKind, 28, NotchDesign.TextMutedBrush);
            bigIcon.Opacity = 0.55;
            bigIcon.Margin = new Thickness(0, 0, 0, 10);
            center.Children.Add(bigIcon);
            center.Children.Add(new TextBlock
            {
                Text = $"Modul '{Title}' ist vorbereitet und folgt in Phase 2.5 / 3.",
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            card.Child = center;

            Grid.SetRow(card, 1);
            root.Children.Add(card);

            return root;
        }
    }
}
