using System;
using System.Windows;
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
