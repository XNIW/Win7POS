using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class ChangeQuantityDialog : DialogShellWindow
    {
        public int Quantity { get; private set; }

        public ChangeQuantityDialog(string productName, int currentQuantity)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyAdaptiveDialogSizing(this, minWidth: 360, minHeight: 320, maxWidthPercent: 0.92, maxHeightPercent: 0.92, allowResize: false);
            ProductNameText.Text = productName ?? "";
            QtyBox.Text = currentQuantity.ToString();
            Loaded += (s, ev) =>
            {
                QtyBox.Focus();
                QtyBox.SelectAll();
            };
        }

        private void Digit_Click(object sender, RoutedEventArgs e)
        {
            var digit = (sender as Button)?.Content?.ToString();
            if (string.IsNullOrWhiteSpace(digit)) return;

            var old = QtyBox.Text ?? string.Empty;
            if (old == "0")
                old = string.Empty;

            QtyBox.Text = old + digit;
            QtyBox.CaretIndex = QtyBox.Text.Length;
            QtyBox.Focus();
        }

        private void DoubleZero_Click(object sender, RoutedEventArgs e)
        {
            var old = QtyBox.Text ?? string.Empty;
            if (old == "0")
                old = string.Empty;
            QtyBox.Text = old + "00";
            QtyBox.CaretIndex = QtyBox.Text.Length;
            QtyBox.Focus();
        }

        private void Backspace_Click(object sender, RoutedEventArgs e)
        {
            var text = QtyBox.Text ?? string.Empty;
            if (text.Length > 0)
                QtyBox.Text = text.Substring(0, text.Length - 1);

            if (string.IsNullOrWhiteSpace(QtyBox.Text))
                QtyBox.Text = "0";

            QtyBox.CaretIndex = QtyBox.Text.Length;
            QtyBox.Focus();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(QtyBox?.Text?.Trim(), out var qty) && qty >= 0)
            {
                Quantity = qty;
                DialogResult = true;
                Close();
            }
            else
            {
                Win7POS.Wpf.Import.ModernMessageDialog.Show(System.Windows.Application.Current?.MainWindow, "Modifica quantità", "Inserire una quantità valida (numero ≥ 0).");
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void QtyBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Ok_Click(sender, e);
                e.Handled = true;
            }
            if (e.Key == Key.Escape)
            {
                Cancel_Click(sender, e);
                e.Handled = true;
            }
        }
    }
}
