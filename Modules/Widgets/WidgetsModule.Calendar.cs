using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WindowsNotch.Core;
using WindowsNotch.Services;
using WindowsNotch.Services.Database;

namespace WindowsNotch.Modules.Widgets
{
    public partial class WidgetsModule
    {
        // ---------- Kachel 3: Kalender / Aufgaben / Notizen ----------

        private Border BuildBottomLeftTile()
        {
            var container = NotchDesign.CreateCard(16, new Thickness(12, 9, 12, 7));
            if (!NotchDesign.IsLight)
            {
                container.Background = new SolidColorBrush(NotchDesign.IsGlass
                    ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
                    : Color.FromRgb(0x16, 0x16, 0x1A));
            }
            container.ToolTip = L.T("widgets.cal_tile_tooltip");

            container.MouseWheel += (_, e) =>
            {
                if (_jiggleController?.IsJiggleMode == true) return;
                if (_bottomLeftTab == 2 && _noteTextBox?.IsMouseOver == true) return;
                SwitchBottomLeftTab((_bottomLeftTab + (e.Delta > 0 ? 2 : 1)) % 3);
                e.Handled = true;
            };

            container.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (_jiggleController?.IsJiggleMode == true) return;
                if (e.OriginalSource is DependencyObject d && IsInteractiveControl(d))
                {
                    _blSwipeStart = null;
                    return;
                }
                _blSwipeStart = e.GetPosition(container);
            };

            container.PreviewMouseLeftButtonUp += (_, e) =>
            {
                if (_jiggleController?.IsJiggleMode == true || !_blSwipeStart.HasValue) return;
                var end = e.GetPosition(container);
                double dx = end.X - _blSwipeStart.Value.X;
                double dy = end.Y - _blSwipeStart.Value.Y;
                _blSwipeStart = null;

                if (Math.Abs(dx) >= 28 && Math.Abs(dx) > Math.Abs(dy) * 1.4)
                {
                    SwitchBottomLeftTab((_bottomLeftTab + (dx < 0 ? 1 : 2)) % 3);
                    e.Handled = true;
                    return;
                }

                // Klick ins Leere (wie beim Carousel) wechselt zur nächsten Ansicht
                if (Math.Abs(dx) < 8 && Math.Abs(dy) < 8 &&
                    !(e.OriginalSource is DependencyObject d && IsInteractiveControl(d)))
                {
                    SwitchBottomLeftTab((_bottomLeftTab + 1) % 3);
                    e.Handled = true;
                }
            };

            var dock = new DockPanel();

            // 3 Carousel-Punkte unten (Kalender | Aufgaben | Notizen)
            _blDotsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            };
            string[] tabNames = { "Kalender", "Aufgaben", "Notizen" };
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                var dotVisual = new Border
                {
                    Width = 5,
                    Height = 5,
                    CornerRadius = new CornerRadius(2.5),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var hitArea = new Border
                {
                    Width = 14,
                    Height = 14,
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    ToolTip = tabNames[i],
                    Child = dotVisual
                };
                hitArea.MouseLeftButtonUp += (_, e) =>
                {
                    if (_jiggleController?.IsJiggleMode == true) return;
                    e.Handled = true;
                    SwitchBottomLeftTab(idx);
                };
                _blDotsPanel.Children.Add(hitArea);
            }
            DockPanel.SetDock(_blDotsPanel, Dock.Bottom);
            dock.Children.Add(_blDotsPanel);

            _bottomLeftHost = new Grid();
            _blCalendarView = BuildCalendarContent();
            _blTodoView = BuildTodoContent();
            _blNoteView = BuildNoteContent();

            _bottomLeftHost.Children.Add(_blCalendarView);
            _bottomLeftHost.Children.Add(_blTodoView);
            _bottomLeftHost.Children.Add(_blNoteView);
            dock.Children.Add(_bottomLeftHost);

            SwitchBottomLeftTab(_bottomLeftTab);
            container.Child = dock;
            return container;
        }

        private bool IsInteractiveControl(DependencyObject? d)
        {
            while (d != null)
            {
                if (d == _calHeaderText ||
                    d is TextBox or System.Windows.Controls.Primitives.ButtonBase ||
                    (d is Border b && b.Parent == _blDotsPanel))
                    return true;
                d = VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        private void SwitchBottomLeftTab(int tabIndex)
        {
            _bottomLeftTab = tabIndex;
            if (_blCalendarView != null) _blCalendarView.Visibility = tabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_blTodoView != null) _blTodoView.Visibility = tabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (_blNoteView != null) _blNoteView.Visibility = tabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

            if (_blDotsPanel != null)
            {
                for (int i = 0; i < _blDotsPanel.Children.Count; i++)
                {
                    if (_blDotsPanel.Children[i] is Border hit && hit.Child is Border dot)
                    {
                        bool active = i == tabIndex;
                        dot.Width = active ? 14 : 5;
                        dot.Background = active ? NotchDesign.TextPrimaryBrush : NotchDesign.TextMutedBrush;
                        dot.Opacity = active ? 0.95 : 0.45;
                    }
                }
            }

            if (tabIndex == 1) ReloadTodos();
        }

        private FrameworkElement BuildCalendarContent()
        {
            var dock = new DockPanel();

            // Kopfzeile: Monat & Jahr links (Klick = Heute), < > rechts für Wochenwechsel
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };

            var navPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            var prevBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.ChevronLeft, 10, NotchDesign.TextSecondaryBrush, 2.0),
                () => { _calendarWeekAnchor = _calendarWeekAnchor.AddDays(-7); RebuildCalendarDays(); },
                background: Brushes.Transparent, hoverBackground: NotchDesign.ControlHoverBrush,
                cornerRadius: 7, padding: new Thickness(0));
            prevBtn.Width = 20;
            prevBtn.Height = 20;

            var nextBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.ChevronRight, 10, NotchDesign.TextSecondaryBrush, 2.0),
                () => { _calendarWeekAnchor = _calendarWeekAnchor.AddDays(7); RebuildCalendarDays(); },
                background: Brushes.Transparent, hoverBackground: NotchDesign.ControlHoverBrush,
                cornerRadius: 7, padding: new Thickness(0));
            nextBtn.Width = 20;
            nextBtn.Height = 20;

            navPanel.Children.Add(prevBtn);
            navPanel.Children.Add(nextBtn);
            DockPanel.SetDock(navPanel, Dock.Right);
            header.Children.Add(navPanel);

            _calHeaderText = new TextBlock
            {
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = L.T("widgets.cal_back_today")
            };
            _calHeaderText.MouseLeftButtonUp += (_, e) =>
            {
                if (_jiggleController?.IsJiggleMode == true) return;
                e.Handled = true;
                _calendarWeekAnchor = DateTime.Today;
                RebuildCalendarDays();
            };
            header.Children.Add(_calHeaderText);

            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);

            // Großes, klares Tages-Datum + Wochentag/KW
            var heroRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };
            _calDayBigText = new TextBlock
            {
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -2, 8, 0)
            };
            Typography.SetNumeralAlignment(_calDayBigText, FontNumeralAlignment.Tabular);

            var heroSubStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _calDaySubText = new TextBlock
            {
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            };
            _calWeekText = new TextBlock
            {
                Foreground = NotchDesign.TextMutedBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10.5
            };
            heroSubStack.Children.Add(_calDaySubText);
            heroSubStack.Children.Add(_calWeekText);

            heroRow.Children.Add(_calDayBigText);
            heroRow.Children.Add(heroSubStack);
            DockPanel.SetDock(heroRow, Dock.Top);
            dock.Children.Add(heroRow);

            // 7-Tage-Wochenstreifen (Mo..So + Datum)
            _calDaysGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
            for (int c = 0; c < 7; c++)
                _calDaysGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _calDaysGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _calDaysGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });

            dock.Children.Add(_calDaysGrid);
            return dock;
        }

        private FrameworkElement BuildTodoContent()
        {
            var dock = new DockPanel();

            var title = new TextBlock
            {
                Text = L.T("widgets.todos_title"),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(2, 0, 0, 6),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(title, Dock.Top);
            dock.Children.Add(title);

            var inputRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var inputCard = NotchDesign.CreateCard(10, new Thickness(8, 3, 8, 3));
            _todoInputBox = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = NotchDesign.TextPrimaryBrush,
                CaretBrush = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = L.T("widgets.todos_input_tooltip")
            };
            _todoInputBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    AddTodoFromInput();
                    e.Handled = true;
                }
            };
            inputCard.Child = _todoInputBox;
            Grid.SetColumn(inputCard, 0);
            inputRow.Children.Add(inputCard);

            var addBtn = NotchDesign.CreateRoundedButton(
                NotchDesign.CreateVectorIcon(NotchIconKind.Plus, 11, Brushes.White, 2.2),
                AddTodoFromInput,
                background: NotchDesign.AccentPrimaryBrush,
                hoverBackground: NotchDesign.AccentHoverBrush,
                cornerRadius: 10,
                padding: new Thickness(0),
                tooltip: L.T("widgets.todos_add_tooltip"));
            addBtn.Width = 26;
            addBtn.Height = 26;
            Grid.SetColumn(addBtn, 2);
            inputRow.Children.Add(addBtn);

            DockPanel.SetDock(inputRow, Dock.Top);
            dock.Children.Add(inputRow);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            NotchDesign.ApplySlimScrollBarStyle(scroll);

            _todoListPanel = new StackPanel();
            scroll.Content = _todoListPanel;
            dock.Children.Add(scroll);

            ReloadTodos();
            return dock;
        }

        private void AddTodoFromInput()
        {
            if (_todoInputBox == null) return;
            string text = _todoInputBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            NotchDatabase.Instance.AddTodo(text);
            _todoInputBox.Text = string.Empty;
            ReloadTodos();
        }

        private void ReloadTodos()
        {
            if (_todoListPanel == null) return;
            _todoListPanel.Children.Clear();

            var items = NotchDatabase.Instance.GetTodos();
            if (items.Count == 0)
            {
                _todoListPanel.Children.Add(new TextBlock
                {
                    Text = L.T("widgets.todos_empty"),
                    Foreground = NotchDesign.TextMutedBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11,
                    Margin = new Thickness(4, 14, 4, 0),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                return;
            }

            foreach (var item in items)
            {
                var row = new Border
                {
                    Background = NotchDesign.CardBackgroundBrush,
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(8, 5, 6, 5),
                    Margin = new Thickness(0, 0, 0, 4)
                };

                var dp = new DockPanel();

                var delBtn = NotchDesign.CreateRoundedButton(
                    NotchDesign.CreateVectorIcon(NotchIconKind.Close, 9.5, NotchDesign.TextMutedBrush, 1.9),
                    () =>
                    {
                        NotchDatabase.Instance.DeleteTodo(item.Id);
                        ReloadTodos();
                    },
                    background: Brushes.Transparent,
                    hoverBackground: NotchDesign.ControlHoverBrush,
                    cornerRadius: 6,
                    padding: new Thickness(0),
                    tooltip: L.T("widgets.todos_remove_tooltip"));
                delBtn.Width = 18;
                delBtn.Height = 18;
                DockPanel.SetDock(delBtn, Dock.Right);
                dp.Children.Add(delBtn);

                var checkBox = new Border
                {
                    Width = 15,
                    Height = 15,
                    CornerRadius = new CornerRadius(4.5),
                    Background = item.IsDone ? new SolidColorBrush(NotchDesign.AccentGreen) : NotchDesign.ControlBackgroundBrush,
                    BorderBrush = item.IsDone ? Brushes.Transparent : NotchDesign.CardBorderBrush,
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 8, 0),
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = item.IsDone ? NotchDesign.CreateVectorIcon(NotchIconKind.Check, 9.5, Brushes.White, 2.3) : null
                };
                checkBox.MouseLeftButtonUp += (_, e) =>
                {
                    if (_jiggleController?.IsJiggleMode == true) return;
                    e.Handled = true;
                    NotchDatabase.Instance.SetTodoDone(item.Id, !item.IsDone);
                    ReloadTodos();
                };
                DockPanel.SetDock(checkBox, Dock.Left);
                dp.Children.Add(checkBox);

                var tb = new TextBlock
                {
                    Text = item.Title,
                    Foreground = item.IsDone ? NotchDesign.TextMutedBrush : NotchDesign.TextPrimaryBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11.5,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand
                };
                if (item.IsDone) tb.TextDecorations = TextDecorations.Strikethrough;
                tb.MouseLeftButtonUp += (_, e) =>
                {
                    if (_jiggleController?.IsJiggleMode == true) return;
                    e.Handled = true;
                    NotchDatabase.Instance.SetTodoDone(item.Id, !item.IsDone);
                    ReloadTodos();
                };
                dp.Children.Add(tb);

                row.Child = dp;
                _todoListPanel.Children.Add(row);
            }

            if (items.Any(i => i.IsDone))
            {
                var clearBtn = NotchDesign.CreateRoundedButton(
                    L.T("widgets.todos_clear_done"),
                    () =>
                    {
                        NotchDatabase.Instance.ClearCompletedTodos();
                        ReloadTodos();
                    },
                    background: Brushes.Transparent,
                    hoverBackground: NotchDesign.ControlHoverBrush,
                    foreground: NotchDesign.TextMutedBrush,
                    cornerRadius: 8,
                    padding: new Thickness(8, 3, 8, 3));
                clearBtn.FontSize = 10.5;
                clearBtn.HorizontalAlignment = HorizontalAlignment.Center;
                clearBtn.Margin = new Thickness(0, 2, 0, 0);
                _todoListPanel.Children.Add(clearBtn);
            }
        }

        private FrameworkElement BuildNoteContent()
        {
            var dock = new DockPanel();

            var header = new DockPanel { Margin = new Thickness(2, 0, 0, 6) };
            var title = new TextBlock
            {
                Text = L.T("widgets.notes_title"),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(title, Dock.Left);
            header.Children.Add(title);

            _noteStatusText = new TextBlock
            {
                Text = L.T("widgets.notes_saved_local"),
                Foreground = NotchDesign.TextMutedBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            header.Children.Add(_noteStatusText);

            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);

            var card = NotchDesign.CreateCard(12, new Thickness(10, 8, 10, 8));
            _noteTextBox = new TextBox
            {
                Text = NotchDatabase.Instance.GetNote(),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = NotchDesign.TextPrimaryBrush,
                CaretBrush = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _noteTextBox.TextChanged += (_, _) =>
            {
                _pendingNoteText = _noteTextBox.Text;
                if (_noteStatusText != null) _noteStatusText.Text = L.T("widgets.notes_saving");
                _noteSaveTimer ??= CreateNoteSaveTimer();
                _noteSaveTimer.Stop();
                _noteSaveTimer.Start();
            };
            _noteTextBox.LostFocus += (_, _) => FlushPendingNote();

            card.Child = _noteTextBox;
            dock.Children.Add(card);
            return dock;
        }

        private DispatcherTimer CreateNoteSaveTimer()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                FlushPendingNote();
            };
            return t;
        }

        private void FlushPendingNote()
        {
            if (_noteTextBox == null) return;
            _noteSaveTimer?.Stop();
            NotchDatabase.Instance.SaveNote(_noteTextBox.Text);
            if (_noteStatusText != null) _noteStatusText.Text = L.T("widgets.notes_saved");
        }

        private void RebuildCalendarDays()
        {
            if (_calHeaderText == null || _calDaysGrid == null) return;

            var today = DateTime.Today;
            int offsetFromMonday = ((int)_calendarWeekAnchor.DayOfWeek + 6) % 7;
            var weekStart = _calendarWeekAnchor.Date.AddDays(-offsetFromMonday);
            var weekMid = weekStart.AddDays(3); // Donnerstag bestimmt Monat/KW nach ISO 8601
            var culture = ActiveCulture;

            string monthTitle = weekMid.ToString("MMMM yyyy", culture);
            _calHeaderText.Text = char.ToUpper(monthTitle[0], culture) + monthTitle[1..];

            int isoWeek = ISOWeek.GetWeekOfYear(weekMid);
            bool isCurrentWeek = weekStart <= today && today < weekStart.AddDays(7);
            var heroDate = isCurrentWeek ? today : weekStart;

            if (_calDayBigText != null)
                _calDayBigText.Text = heroDate.Day.ToString(culture);
            if (_calDaySubText != null)
            {
                string dayName = heroDate.ToString("dddd", culture);
                _calDaySubText.Text = char.ToUpper(dayName[0], culture) + dayName[1..];
            }
            if (_calWeekText != null)
                _calWeekText.Text = isCurrentWeek ? L.F("widgets.cal_week_today", isoWeek) : L.F("widgets.cal_week", isoWeek);

            _calDaysGrid.Children.Clear();
            string[] headers = L.T("widgets.cal_days").Split(',');

            for (int c = 0; c < 7; c++)
            {
                var dayDate = weekStart.AddDays(c);
                bool isToday = dayDate == today;
                bool sameMonth = dayDate.Month == weekMid.Month;

                var hTb = new TextBlock
                {
                    Text = c < headers.Length ? headers[c] : string.Empty,
                    Foreground = isToday ? NotchDesign.AccentPrimaryBrush : NotchDesign.TextMutedBrush,
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 10,
                    FontWeight = isToday ? FontWeights.Bold : FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 2)
                };
                Grid.SetRow(hTb, 0);
                Grid.SetColumn(hTb, c);
                _calDaysGrid.Children.Add(hTb);

                var dayTb = new TextBlock
                {
                    Text = dayDate.Day.ToString(culture),
                    Foreground = isToday ? Brushes.White : (sameMonth ? NotchDesign.TextPrimaryBrush : NotchDesign.TextMutedBrush),
                    FontFamily = NotchDesign.UiTextFont,
                    FontSize = 11.5,
                    FontWeight = isToday ? FontWeights.Bold : FontWeights.Medium,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = isToday || sameMonth ? 1.0 : 0.6
                };
                Typography.SetNumeralAlignment(dayTb, FontNumeralAlignment.Tabular);

                UIElement cellEl = isToday
                    ? new Border
                    {
                        Width = 22,
                        Height = 22,
                        CornerRadius = new CornerRadius(11),
                        Background = NotchDesign.AccentPrimaryBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = dayTb
                    }
                    : dayTb;

                Grid.SetRow(cellEl, 1);
                Grid.SetColumn(cellEl, c);
                _calDaysGrid.Children.Add(cellEl);
            }
        }

    }
}
