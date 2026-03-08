using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class BoletaNumberDialog : Window
    {
        public int BoletaNumber { get; private set; }

        public BoletaNumberDialog(int currentNumber)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyAdaptiveDialogSizing(this, minWidth: 360, minHeight: 280, maxWidthPercent: 0.92, maxHeightPercent: 0.92, allowResize: false);
            BoletaBox.Text = currentNumber > 0 ? currentNumber.ToString() : "1";
            Loaded += (s, ev) =>
            {
                BoletaBox.Focus();
                BoletaBox.SelectAll();
            };
        }

        public static bool ShowDialog(Window owner, int currentNumber, out int result)
        {
            var dlg = new BoletaNumberDialog(currentNumber) { Owner = owner };
            var ok = dlg.ShowDialog() == true;
            result = ok ? dlg.BoletaNumber : currentNumber;
            return ok;
        }

        private void Digit_Click(object sender, RoutedEventArgs e)
        {
            var digit = (sender as Button)?.Content?.ToString();
            if (string.IsNullOrWhiteSpace(digit)) return;
            var old = BoletaBox.Text ?? string.Empty;
            if (old == "0") old = string.Empty;
            BoletaBox.Text = old + digit;
            BoletaBox.CaretIndex = BoletaBox.Text.Length;
            BoletaBox.Focus();
        }

        private void DoubleZero_Click(object sender, RoutedEventArgs e)
        {
            var old = BoletaBox.Text ?? string.Empty;
            if (old == "0") old = string.Empty;
            BoletaBox.Text = old + "00";
            BoletaBox.CaretIndex = BoletaBox.Text.Length;
            BoletaBox.Focus();
        }

        private void Backspace_Click(object sender, RoutedEventArgs e)
        {
            var text = BoletaBox.Text ?? string.Empty;
            if (text.Length > 0)
                BoletaBox.Text = text.Substring(0, text.Length - 1);
            if (string.IsNullOrWhiteSpace(BoletaBox.Text))
                BoletaBox.Text = "1";
            BoletaBox.CaretIndex = BoletaBox.Text.Length;
            BoletaBox.Focus();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(BoletaBox?.Text?.Trim(), out var num) && num > 0)
            {
                BoletaNumber = num;
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show("Inserire un numero Boleta valido (intero > 0).", "Numero Boleta", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BoletaBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { Ok_Click(sender, e); e.Handled = true; }
            if (e.Key == Key.Escape) { Cancel_Click(sender, e); e.Handled = true; }
        }
    }
}
