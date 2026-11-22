using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WindowsNotch.Core;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.QuickActions
{
    public partial class QuickActionsModule
    {
        private string _calcEntry = "0";
        private double? _calcAccumulator;
        private string? _calcPendingOp;
        private string _calcExpression = string.Empty;
        private bool _calcResetOnNextDigit;
        private TextBlock? _calcMainDisplay;
        private TextBlock? _calcExprDisplay;

        private Border BuildCalculatorSubView()
        {
            var root = new DockPanel();

            // Display direkt auf OLED-Schwarz
            var displayBox = new StackPanel
            {
                Margin = new Thickness(6, 0, 6, 10),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            _calcExprDisplay = new TextBlock
            {
                Text = _calcExpression,
                Foreground = NotchDesign.TextSecondaryBrush,
                FontFamily = NotchDesign.UiTextFont,
                FontSize = 11.5,
                TextAlignment = TextAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Height = 16
            };
            displayBox.Children.Add(_calcExprDisplay);

            _calcMainDisplay = new TextBlock
            {
                Text = FormatDisplay(_calcEntry),
                Foreground = NotchDesign.TextPrimaryBrush,
                FontFamily = NotchDesign.UiDisplayFont,
                FontSize = 28,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _calcMainDisplay.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
            displayBox.Children.Add(_calcMainDisplay);

            DockPanel.SetDock(displayBox, Dock.Top);
            root.Children.Add(displayBox);

            // 4x5 Tastenfeld
            var grid = new Grid();
            for (int r = 0; r < 5; r++)
            {
                if (r > 0) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            }
            for (int c = 0; c < 4; c++)
            {
                if (c > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            string[,] layout =
            {
                { "C", "⌫", "%", "÷" },
                { "7", "8", "9", "×" },
                { "4", "5", "6", "−" },
                { "1", "2", "3", "+" },
                { "±", "0", ",", "=" }
            };

            for (int r = 0; r < 5; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    string key = layout[r, c];
                    bool isEquals = key == "=";
                    bool isOp = key is "C" or "⌫" or "%" or "÷" or "×" or "−" or "+";

                    Brush bg = isEquals
                        ? NotchDesign.AccentPrimaryBrush
                        : isOp
                            ? NotchDesign.NestedCardBackgroundBrush
                            : NotchDesign.CardBackgroundBrush;
                    Brush hover = isEquals
                        ? NotchDesign.AccentHoverBrush
                        : isOp
                            ? NotchDesign.TabActiveBackgroundBrush
                            : NotchDesign.NestedCardBackgroundBrush;
                    Brush fg = isEquals
                        ? Brushes.White
                        : key is "C" or "⌫" or "%"
                            ? NotchDesign.TextSecondaryBrush
                            : isOp
                                ? NotchDesign.AccentPrimaryBrush
                                : NotchDesign.TextPrimaryBrush;

                    var label = new TextBlock
                    {
                        Text = key,
                        Foreground = fg,
                        FontFamily = NotchDesign.UiDisplayFont,
                        FontSize = isOp || isEquals ? 15 : 14.5,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    var btn = NotchDesign.CreateRoundedButton(
                        label,
                        () => HandleCalculatorInput(key),
                        background: bg,
                        hoverBackground: hover,
                        cornerRadius: NotchDesign.RadiusControl,
                        padding: new Thickness(0, 8, 0, 8));
                    btn.HorizontalContentAlignment = HorizontalAlignment.Center;
                    btn.VerticalContentAlignment = VerticalAlignment.Center;

                    Grid.SetRow(btn, r * 2);
                    Grid.SetColumn(btn, c * 2);
                    grid.Children.Add(btn);
                }
            }

            root.Children.Add(grid);

            var shell = BuildSubViewShell(L.T("qa.tile.calc"), NotchIconKind.Dashboard, root);
            shell.Focusable = true;
            shell.Loaded += (_, _) => Keyboard.Focus(shell);
            shell.PreviewKeyDown += OnCalculatorKeyDown;
            return shell;
        }

        private void OnCalculatorKeyDown(object sender, KeyEventArgs e)
        {
            string? mapped = e.Key switch
            {
                Key.D0 or Key.NumPad0 => "0",
                Key.D1 or Key.NumPad1 => "1",
                Key.D2 or Key.NumPad2 => "2",
                Key.D3 or Key.NumPad3 => "3",
                Key.D4 or Key.NumPad4 => "4",
                Key.D5 or Key.NumPad5 => "5",
                Key.D6 or Key.NumPad6 => "6",
                Key.D7 or Key.NumPad7 => "7",
                Key.D8 or Key.NumPad8 => "8",
                Key.D9 or Key.NumPad9 => "9",
                Key.OemComma or Key.OemPeriod or Key.Decimal => ",",
                Key.Add or Key.OemPlus when (Keyboard.Modifiers & ModifierKeys.Shift) == 0 => "+",
                Key.Subtract or Key.OemMinus => "−",
                Key.Multiply => "×",
                Key.Divide or Key.OemQuestion => "÷",
                Key.Enter => "=",
                Key.Back => "⌫",
                Key.Delete => "C",
                _ => null
            };

            if (e.Key == Key.D5 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                mapped = "%";
            else if (e.Key == Key.D8 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                mapped = "×";
            else if (e.Key == Key.OemPlus && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                mapped = "+";

            if (mapped != null)
            {
                HandleCalculatorInput(mapped);
                e.Handled = true;
            }
        }

        private void HandleCalculatorInput(string key)
        {
            if (key.Length == 1 && char.IsDigit(key[0]))
            {
                if (_calcResetOnNextDigit || _calcEntry == "0" || _calcEntry == "Error")
                {
                    _calcEntry = key;
                    _calcResetOnNextDigit = false;
                }
                else if (_calcEntry.Length < 14)
                {
                    _calcEntry += key;
                }
            }
            else if (key == ",")
            {
                if (_calcResetOnNextDigit || _calcEntry == "Error")
                {
                    _calcEntry = "0.";
                    _calcResetOnNextDigit = false;
                }
                else if (!_calcEntry.Contains('.'))
                {
                    _calcEntry += ".";
                }
            }
            else if (key == "C")
            {
                _calcEntry = "0";
                _calcAccumulator = null;
                _calcPendingOp = null;
                _calcExpression = string.Empty;
                _calcResetOnNextDigit = false;
            }
            else if (key == "⌫")
            {
                if (_calcResetOnNextDigit || _calcEntry == "Error")
                {
                    _calcEntry = "0";
                    _calcResetOnNextDigit = false;
                }
                else if (_calcEntry.Length > 1)
                {
                    _calcEntry = _calcEntry[..^1];
                    if (_calcEntry == "-") _calcEntry = "0";
                }
                else
                {
                    _calcEntry = "0";
                }
            }
            else if (key == "±")
            {
                if (double.TryParse(_calcEntry, NumberStyles.Float, CultureInfo.InvariantCulture, out double val) && val != 0)
                    _calcEntry = (-val).ToString(CultureInfo.InvariantCulture);
            }
            else if (key == "%")
            {
                if (double.TryParse(_calcEntry, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                {
                    double result = _calcAccumulator.HasValue ? _calcAccumulator.Value * (val / 100.0) : val / 100.0;
                    _calcEntry = FormatCalcNumber(result);
                    _calcResetOnNextDigit = true;
                }
            }
            else if (key is "+" or "−" or "×" or "÷")
            {
                if (double.TryParse(_calcEntry, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                {
                    if (_calcAccumulator.HasValue && _calcPendingOp != null && !_calcResetOnNextDigit)
                    {
                        if (!TryEvaluate(_calcAccumulator.Value, val, _calcPendingOp, out double res))
                        {
                            SetCalcError();
                            return;
                        }
                        _calcAccumulator = res;
                        _calcEntry = FormatCalcNumber(res);
                    }
                    else
                    {
                        _calcAccumulator = val;
                    }
                    _calcPendingOp = key;
                    _calcExpression = $"{FormatDisplay(FormatCalcNumber(_calcAccumulator.Value))} {key}";
                    _calcResetOnNextDigit = true;
                }
            }
            else if (key == "=")
            {
                if (_calcAccumulator.HasValue && _calcPendingOp != null &&
                    double.TryParse(_calcEntry, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                {
                    string left = FormatDisplay(FormatCalcNumber(_calcAccumulator.Value));
                    string right = FormatDisplay(_calcEntry);
                    if (!TryEvaluate(_calcAccumulator.Value, val, _calcPendingOp, out double res))
                    {
                        SetCalcError();
                        return;
                    }
                    _calcExpression = $"{left} {_calcPendingOp} {right} =";
                    _calcEntry = FormatCalcNumber(res);
                    _calcAccumulator = null;
                    _calcPendingOp = null;
                    _calcResetOnNextDigit = true;
                }
            }

            if (_calcMainDisplay != null)
                _calcMainDisplay.Text = FormatDisplay(_calcEntry);
            if (_calcExprDisplay != null)
                _calcExprDisplay.Text = _calcExpression;
        }

        private void SetCalcError()
        {
            _calcEntry = "Error";
            _calcAccumulator = null;
            _calcPendingOp = null;
            _calcExpression = string.Empty;
            _calcResetOnNextDigit = true;
            if (_calcMainDisplay != null) _calcMainDisplay.Text = L.T("calc.error");
            if (_calcExprDisplay != null) _calcExprDisplay.Text = string.Empty;
        }

        private static bool TryEvaluate(double left, double right, string op, out double result)
        {
            result = 0;
            switch (op)
            {
                case "+": result = left + right; return !double.IsInfinity(result) && !double.IsNaN(result);
                case "−": result = left - right; return !double.IsInfinity(result) && !double.IsNaN(result);
                case "×": result = left * right; return !double.IsInfinity(result) && !double.IsNaN(result);
                case "÷":
                    if (Math.Abs(right) < 1e-15) return false;
                    result = left / right;
                    return !double.IsInfinity(result) && !double.IsNaN(result);
                default: return false;
            }
        }

        private static string FormatCalcNumber(double val)
        {
            if (Math.Abs(val) < 1e-12) return "0";
            return val.ToString("G12", CultureInfo.InvariantCulture);
        }

        private static string FormatDisplay(string raw)
        {
            if (raw == "Error") return L.T("calc.error");
            string sep = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            return raw.Replace(".", sep);
        }
    }
}
