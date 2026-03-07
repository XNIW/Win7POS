using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class ChangeQuantityDialog : Window
    {
        public int Quantity { get; private set; }

        public ChangeQuantityDialog(string productName, int currentQuantity)
        {
            InitializeComponent();
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
                MessageBox.Show("Inserire una quantità valida (numero ≥ 0).", "Modifica quantità", MessageBoxButton.OK, MessageBoxImage.Warning);
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
