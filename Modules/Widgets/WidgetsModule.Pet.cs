using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WindowsNotch.Core;
using WindowsNotch.Modules.Pet;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.Widgets
{
    public partial class WidgetsModule
    {
        private bool _isPetPinned;
        private bool _isPetUnpinAnimating;
        private Point? _petLastTileOffset;

        private Border? _petTileCard;
        private DockPanel? _petDock;
        private Button? _petPinBtn;
        private TextBlock? _petLevelText;
        private TextBlock? _petStatusText;
        private Border? _petXpBarSection;
        private Border? _petXpBarFill;
        private TextBlock? _petXpLabel;
        private Border? _petHungerFill, _petEnergyFill, _petMoodFill;
        private TextBlock? _petHungerPct, _petEnergyPct, _petMoodPct;
        private TextBlock? _petSleepBtnText;
        private FrameworkElement? _petEyesHost;

        private Border BuildPetTile()
        {
            var card = NotchDesign.CreateCard(16, new Thickness(0));
            _petTileCard = card;
            card.ClipToBounds = true;
            card.SizeChanged += (_, e) =>
            {
                if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
                double radius = _isPetPinned ? 22 : 16;
                var clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), radius, radius);
                clip.Freeze();
                card.Clip = clip;
            };

            var dock = new DockPanel
            {
                Margin = new Thickness(14, 10, 14, 10),
                Background = Brushes.Transparent
            };
            _petDock = dock;

            // Kopfzeile: Level-Pill + Status links, Pin-Button rechts
            var header = new Grid { VerticalAlignment = VerticalAlignment.Center };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var levelPill = new Border
            {
                Background = NotchDesign.CreateSoftAccentTileBrush(38),
                CornerRadius = new CornerRadius(NotchDesign.RadiusSmall),
                Padding = new Thickness(7, 2.5, 7, 2.5),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _petLevelText = new TextBlock
            {
                Foreground = NotchDesign.AccentPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 11,
                FontWeight = FontWeights.Bold
            };
            System.Windows.Documents.Typography.SetNumeralAlignment(_petLevelText, FontNumeralAlignment.Tabular);
            levelPill.Child = _petLevelText;
            Grid.SetColumn(levelPill, 0);
            header.Children.Add(levelPill);

            _petStatusText = new TextBlock
            {
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(_petStatusText, 1);
            header.Children.Add(_petStatusText);

            _petPinBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Pin, 14, NotchDesign.TextPrimaryBrush, 1.8),
                () => SetPetPinned(!_isPetPinned),
                background: Brushes.Transparent,
                hoverBackground: NotchDesign.ControlHoverBrush,
                foreground: NotchDesign.TextPrimaryBrush,
                cornerRadius: 18,
                padding: new Thickness(0),
                tooltip: L.T("widgets.pet.pin_tooltip"));
            _petPinBtn.Width = 28;
            _petPinBtn.Height = 28;
            _petPinBtn.VerticalAlignment = VerticalAlignment.Center;
            _petPinBtn.Margin = new Thickness(6, 0, 0, 0);
            _petPinBtn.Opacity = _isPetPinned ? 1.0 : 0.0;
            _petPinBtn.IsHitTestVisible = _isPetPinned;
            Grid.SetColumn(_petPinBtn, 2);
            header.Children.Add(_petPinBtn);

            card.MouseEnter += (_, _) => UpdatePetPinVisibility(isHovered: true);
            card.MouseLeave += (_, _) => UpdatePetPinVisibility(isHovered: false);

            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);

            // Unten: XP-Balken (nur im Vollbild), 3 Status-Balken, 3 Aktions-Buttons
            var footer = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };

            // XP-Leiste (im Vollbild sichtbar)
            _petXpBarSection = new Border
            {
                Margin = new Thickness(0, 0, 0, 10),
                Visibility = Visibility.Collapsed
            };
            var xpStack = new StackPanel();
            var xpHeader = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            xpHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            xpHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            xpHeader.Children.Add(new TextBlock
            {
                Text = L.T("widgets.pet.xp_title"),
                Foreground = NotchDesign.TextMutedBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                FontWeight = FontWeights.Medium
            });
            _petXpLabel = new TextBlock
            {
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            };
            System.Windows.Documents.Typography.SetNumeralAlignment(_petXpLabel, FontNumeralAlignment.Tabular);
            Grid.SetColumn(_petXpLabel, 1);
            xpHeader.Children.Add(_petXpLabel);
            xpStack.Children.Add(xpHeader);

            var xpTrack = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = NotchDesign.NestedCardBackgroundBrush,
                ClipToBounds = true
            };
            var xpTrackGrid = new Grid();
            _petXpBarFill = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = NotchDesign.CreateAccentGradientBrush(horizontal: true),
                Width = 0
            };
            xpTrackGrid.Children.Add(_petXpBarFill);
            xpTrackGrid.SizeChanged += (_, _) => UpdatePetXpBarWidth();
            xpTrack.Child = xpTrackGrid;
            xpStack.Children.Add(xpTrack);
            _petXpBarSection.Child = xpStack;
            footer.Children.Add(_petXpBarSection);

            // 3 Status-Balken (Hunger, Energie, Laune)
            var barsGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            barsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            barsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            barsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            barsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            barsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var (hungerCol, hFill, hPct) = CreateStatBar(L.T("widgets.pet.stat_hunger"), NotchDesign.GetFrozenBrush(NotchDesign.AccentOrange));
            var (energyCol, eFill, ePct) = CreateStatBar(L.T("widgets.pet.stat_energy"), NotchDesign.AccentPrimaryBrush);
            var (moodCol, mFill, mPct) = CreateStatBar(L.T("widgets.pet.stat_mood"), NotchDesign.GetFrozenBrush(NotchDesign.AccentGreen));
            _petHungerFill = hFill; _petHungerPct = hPct;
            _petEnergyFill = eFill; _petEnergyPct = ePct;
            _petMoodFill = mFill; _petMoodPct = mPct;

            Grid.SetColumn(hungerCol, 0);
            Grid.SetColumn(energyCol, 2);
            Grid.SetColumn(moodCol, 4);
            barsGrid.Children.Add(hungerCol);
            barsGrid.Children.Add(energyCol);
            barsGrid.Children.Add(moodCol);
            footer.Children.Add(barsGrid);

            // Aktions-Buttons (Füttern, Streicheln, Schlafen)
            var actionsGrid = new Grid();
            actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var feedBtn = CreatePetActionButton(NotchIconKind.Sparkle, L.T("widgets.pet.feed"), () => PetService.Instance.Feed());
            var petBtn = CreatePetActionButton(NotchIconKind.Pet, L.T("widgets.pet.pet"), () => PetService.Instance.Pet());
            var (sleepBtn, sleepTxt) = CreatePetActionButtonWithLabel(NotchIconKind.Moon, L.T("widgets.pet.sleep"), () => PetService.Instance.ToggleSleep());
            _petSleepBtnText = sleepTxt;

            Grid.SetColumn(feedBtn, 0);
            Grid.SetColumn(petBtn, 2);
            Grid.SetColumn(sleepBtn, 4);
            actionsGrid.Children.Add(feedBtn);
            actionsGrid.Children.Add(petBtn);
            actionsGrid.Children.Add(sleepBtn);
            footer.Children.Add(actionsGrid);

            DockPanel.SetDock(footer, Dock.Bottom);
            dock.Children.Add(footer);

            // Mitte: Notch-Gesicht (Klick streichelt direkt)
            var eyes = new NotchEyesControl(compact: false)
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = L.T("widgets.pet.eyes_tooltip")
            };
            eyes.MouseLeftButtonUp += (_, e) =>
            {
                if (_jiggleController?.IsJiggleMode == true) return;
                e.Handled = true;
                PetService.Instance.Pet();
            };
            _petEyesHost = eyes;

            var centerHost = new Grid
            {
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 2, 0, 2)
            };
            centerHost.Children.Add(eyes);
            dock.Children.Add(centerHost);

            card.Child = dock;

            void OnPetChanged(object? s, EventArgs e)
            {
                Application.Current?.Dispatcher.BeginInvoke(UpdatePetTileVisuals);
            }

            card.Loaded += (_, _) =>
            {
                PetService.Instance.StateChanged += OnPetChanged;
                UpdatePetTileVisuals();
            };
            card.Unloaded += (_, _) =>
            {
                PetService.Instance.StateChanged -= OnPetChanged;
            };

            UpdatePetTileVisuals();
            return card;
        }

        private static (StackPanel Host, Border Fill, TextBlock Pct) CreateStatBar(string label, Brush fillBrush)
        {
            var stack = new StackPanel();
            var labelRow = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = label,
                Foreground = NotchDesign.TextMutedBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10,
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var pct = new TextBlock
            {
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4, 0, 0, 0)
            };
            System.Windows.Documents.Typography.SetNumeralAlignment(pct, FontNumeralAlignment.Tabular);
            Grid.SetColumn(pct, 1);
            labelRow.Children.Add(title);
            labelRow.Children.Add(pct);
            stack.Children.Add(labelRow);

            var track = new Border
            {
                Height = 4.5,
                CornerRadius = new CornerRadius(2.25),
                Background = NotchDesign.NestedCardBackgroundBrush,
                ClipToBounds = true
            };
            var trackGrid = new Grid();
            var fill = new Border
            {
                Height = 4.5,
                CornerRadius = new CornerRadius(2.25),
                Background = fillBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0
            };
            trackGrid.Children.Add(fill);
            track.Child = trackGrid;
            stack.Children.Add(track);

            return (stack, fill, pct);
        }

        private Button CreatePetActionButton(NotchIconKind icon, string label, Action onClick)
            => CreatePetActionButtonWithLabel(icon, label, onClick).Btn;

        private (Button Btn, TextBlock Text) CreatePetActionButtonWithLabel(NotchIconKind icon, string label, Action onClick)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var ic = NotchDesign.CreateVectorIcon(icon, 11, NotchDesign.TextPrimaryBrush, 1.85);
            ic.Margin = new Thickness(0, 0, 4, 0);
            ic.VerticalAlignment = VerticalAlignment.Center;
            var txt = new TextBlock
            {
                Text = label,
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10.5,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            stack.Children.Add(ic);
            stack.Children.Add(txt);

            var btn = NotchDesign.CreateRoundedButton(
                stack,
                () =>
                {
                    if (_jiggleController?.IsJiggleMode == true) return;
                    onClick();
                },
                background: NotchDesign.NestedCardBackgroundBrush,
                hoverBackground: NotchDesign.ControlHoverBrush,
                cornerRadius: NotchDesign.RadiusControl,
                padding: new Thickness(6, 5, 6, 5));
            btn.Height = 26;
            return (btn, txt);
        }

        private void UpdatePetTileVisuals()
        {
            var svc = PetService.Instance;
            if (_petLevelText != null)
                _petLevelText.Text = L.F("widgets.pet.level", svc.Level);
            if (_petStatusText != null)
                _petStatusText.Text = svc.GetStatusText();
            if (_petSleepBtnText != null)
                _petSleepBtnText.Text = svc.IsSleeping ? L.T("widgets.pet.wake") : L.T("widgets.pet.sleep");

            if (_petXpLabel != null)
                _petXpLabel.Text = $"{svc.Xp} / {svc.XpNeededForNextLevel} XP";
            UpdatePetXpBarWidth();

            UpdateStatBar(_petHungerFill, _petHungerPct, svc.Hunger);
            UpdateStatBar(_petEnergyFill, _petEnergyPct, svc.Energy);
            UpdateStatBar(_petMoodFill, _petMoodPct, svc.MoodScore);
        }

        private void UpdatePetXpBarWidth()
        {
            if (_petXpBarFill?.Parent is not FrameworkElement track) return;
            double total = track.ActualWidth;
            if (total <= 0) return;
            _petXpBarFill.Width = Math.Max(0, total * PetService.Instance.XpProgressRatio);
        }

        private void UpdateStatBar(Border? fill, TextBlock? pct, int value)
        {
            if (pct != null)
            {
                pct.Text = $"{value}%";
                pct.Visibility = _isPetPinned ? Visibility.Visible : Visibility.Collapsed;
            }
            if (fill?.Parent is FrameworkElement track)
            {
                void Apply()
                {
                    if (track.ActualWidth > 0)
                        fill.Width = track.ActualWidth * (Math.Clamp(value, 0, 100) / 100.0);
                }
                Apply();
                if (track.ActualWidth <= 0)
                {
                    void OnSize(object s, SizeChangedEventArgs e)
                    {
                        track.SizeChanged -= OnSize;
                        Apply();
                    }
                    track.SizeChanged += OnSize;
                }
            }
        }

        private void SetPetPinned(bool pinned)
        {
            if (pinned && _isLyricsPinned)
                SetLyricsPinned(false);

            if (_bentoGrid == null || _petTileCard == null || _lyricsFullscreenHost == null)
            {
                _isPetPinned = pinned;
                return;
            }

            if (!pinned && _isPetPinned && NotchDesign.EffectiveAnimationsEnabled &&
                _lyricsFullscreenHost.Visibility == Visibility.Visible && !_isPetUnpinAnimating)
            {
                _isPetUnpinAnimating = true;
                AnimateTileUnpinOut(_petLastTileOffset, () =>
                {
                    _isPetUnpinAnimating = false;
                    CompleteSetPetPinned(false, null);
                });
                return;
            }

            Point? tileOffsetFromCenter = null;
            if (pinned && _expandedRootHost != null && _bentoGrid.Visibility == Visibility.Visible &&
                _petTileCard.ActualWidth > 10 && _expandedRootHost.ActualWidth > 10)
            {
                try
                {
                    var topLeft = _petTileCard.TransformToVisual(_expandedRootHost).Transform(new Point(0, 0));
                    double tileCenterX = topLeft.X + _petTileCard.ActualWidth * 0.5;
                    double tileCenterY = topLeft.Y + _petTileCard.ActualHeight * 0.5;
                    double hostCenterX = _expandedRootHost.ActualWidth * 0.5;
                    double hostCenterY = _expandedRootHost.ActualHeight * 0.5;
                    tileOffsetFromCenter = new Point(tileCenterX - hostCenterX, tileCenterY - hostCenterY);
                    _petLastTileOffset = tileOffsetFromCenter;
                }
                catch { }
            }

            CompleteSetPetPinned(pinned, tileOffsetFromCenter);
        }

        private void CompleteSetPetPinned(bool pinned, Point? tileOffsetFromCenter)
        {
            _isPetPinned = pinned;
            if (_bentoGrid == null || _petTileCard == null || _lyricsFullscreenHost == null)
                return;

            if (pinned)
            {
                if (_bentoGrid.Children.Contains(_petTileCard))
                    _bentoGrid.Children.Remove(_petTileCard);

                _bentoGrid.Visibility = Visibility.Collapsed;
                _lyricsFullscreenHost.Padding = new Thickness(22, 12, 22, 20);
                _lyricsFullscreenHost.Child = _petTileCard;
                _lyricsFullscreenHost.Visibility = Visibility.Visible;
            }
            else
            {
                _lyricsFullscreenHost.BeginAnimation(UIElement.OpacityProperty, null);
                _lyricsFullscreenHost.Opacity = 1.0;
                _lyricsFullscreenHost.RenderTransform = Transform.Identity;
                _lyricsFullscreenHost.Child = null;
                _lyricsFullscreenHost.Padding = new Thickness(0);
                _lyricsFullscreenHost.Visibility = Visibility.Collapsed;

                int idx = SettingsService.Current.BentoTileOrder.IndexOf("pet");
                if (idx >= 0)
                {
                    int row = idx < 2 ? 0 : 2;
                    int col = (idx % 2 == 0) ? 0 : 2;
                    Grid.SetRow(_petTileCard, row);
                    Grid.SetColumn(_petTileCard, col);
                    if (!_bentoGrid.Children.Contains(_petTileCard))
                        _bentoGrid.Children.Add(_petTileCard);
                }
                else
                {
                    _refreshCustomCarousel?.Invoke();
                }

                _bentoGrid.Visibility = Visibility.Visible;
            }

            _petTileCard.CornerRadius = new CornerRadius(pinned ? 22 : 16);
            if (_petDock != null)
                _petDock.Margin = pinned ? new Thickness(22, 16, 22, 18) : new Thickness(14, 10, 14, 10);
            if (_petXpBarSection != null)
                _petXpBarSection.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
            if (_petEyesHost is FrameworkElement eh)
            {
                eh.RenderTransformOrigin = new Point(0.5, 0.5);
                eh.RenderTransform = pinned ? new ScaleTransform(1.35, 1.35) : Transform.Identity;
            }
            if (_petPinBtn != null)
            {
                _petPinBtn.Width = pinned ? 34 : 28;
                _petPinBtn.Height = pinned ? 34 : 28;
                _petPinBtn.Background = pinned ? NotchDesign.ControlHoverBrush : Brushes.Transparent;
                _petPinBtn.ToolTip = pinned
                    ? L.T("widgets.pet.unpin_tooltip")
                    : L.T("widgets.pet.pin_tooltip");
                UpdatePetPinVisibility(_petTileCard.IsMouseOver);
            }

            UpdatePetTileVisuals();
            AnimateLyricsPinTransition(pinned, tileOffsetFromCenter);
            RaiseStateChanged();
        }

        private void UpdatePetPinVisibility(bool isHovered)
        {
            if (_petPinBtn == null) return;
            bool visible = (_isPetPinned || isHovered) && _jiggleController?.IsJiggleMode != true;
            _petPinBtn.IsHitTestVisible = visible;
            double target = visible ? 1.0 : 0.0;

            if (!NotchDesign.EffectiveAnimationsEnabled)
            {
                _petPinBtn.BeginAnimation(UIElement.OpacityProperty, null);
                _petPinBtn.Opacity = target;
                return;
            }

            _petPinBtn.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(visible ? 140 : 180))
                {
                    EasingFunction = NotchDesign.CreateEaseOut()
                });
        }
    }
}
