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
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            FocusQtyBoxSelectAll();
        }

        private void Digit_Click(object sender, RoutedEventArgs e)
        {
            var digit = (sender as Button)?.Content?.ToString();
            if (string.IsNullOrWhiteSpace(digit)) return;

            ReplaceSelectionOrAppend(digit);
        }

        private void DoubleZero_Click(object sender, RoutedEventArgs e)
        {
            ReplaceSelectionOrAppend("00");
        }

        private void Backspace_Click(object sender, RoutedEventArgs e)
        {
            RemoveSelectionOrBackspace();
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

        private void FocusQtyBoxSelectAll()
        {
            if (QtyBox == null)
                return;

            QtyBox.Focus();
            Keyboard.Focus(QtyBox);
            QtyBox.SelectAll();
        }

        private void ReplaceSelectionOrAppend(string textToInsert)
        {
            if (QtyBox == null || string.IsNullOrEmpty(textToInsert))
                return;

            var text = QtyBox.Text ?? string.Empty;
            var selectionStart = Math.Max(0, Math.Min(QtyBox.SelectionStart, text.Length));
            var selectionLength = Math.Max(0, Math.Min(QtyBox.SelectionLength, text.Length - selectionStart));

            if (selectionLength > 0)
            {
                text = text.Remove(selectionStart, selectionLength).Insert(selectionStart, textToInsert);
                QtyBox.Text = text;
                RestoreQtyBoxFocus(selectionStart + textToInsert.Length);
                return;
            }

            if (text == "0")
                text = string.Empty;

            text += textToInsert;
            QtyBox.Text = text;
            RestoreQtyBoxFocus(text.Length);
        }

        private void RemoveSelectionOrBackspace()
        {
            if (QtyBox == null)
                return;

            var text = QtyBox.Text ?? string.Empty;
            var selectionStart = Math.Max(0, Math.Min(QtyBox.SelectionStart, text.Length));
            var selectionLength = Math.Max(0, Math.Min(QtyBox.SelectionLength, text.Length - selectionStart));

            if (selectionLength > 0)
            {
                text = text.Remove(selectionStart, selectionLength);
                if (string.IsNullOrWhiteSpace(text))
                    text = "0";

                QtyBox.Text = text;
                RestoreQtyBoxFocus(Math.Min(selectionStart, text.Length));
                return;
            }

            if (text.Length > 0)
                text = text.Substring(0, text.Length - 1);

            if (string.IsNullOrWhiteSpace(text))
                text = "0";

            QtyBox.Text = text;
            RestoreQtyBoxFocus(text.Length);
        }

        private void RestoreQtyBoxFocus(int caretIndex)
        {
            if (QtyBox == null)
                return;

            var clampedCaretIndex = Math.Max(0, Math.Min(caretIndex, (QtyBox.Text ?? string.Empty).Length));
            QtyBox.Focus();
            Keyboard.Focus(QtyBox);
            QtyBox.SelectionStart = clampedCaretIndex;
            QtyBox.SelectionLength = 0;
            QtyBox.CaretIndex = clampedCaretIndex;
        }
    }
}
