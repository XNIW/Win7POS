using System.Windows;
using System.Windows.Input;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class RefundDialog : Window
    {
        public RefundViewModel ViewModel { get; }

        public RefundDialog(RefundViewModel vm)
        {
            InitializeComponent();
            // Dimensioni fisse come Registro vendite (980x700 da XAML, NoResize)
            ViewModel = vm;
            ViewModel.RequestClose += OnRequestClose;
            DataContext = vm;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            BarcodeScanBox.Focus();
            BarcodeScanBox.SelectAll();
        }

        private void OnRequestClose(bool ok)
        {
            DialogResult = ok;
        }

        private void CardStorno_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ViewModel.IsFullVoidEnabled)
                ViewModel.IsFullVoid = true;
        }

        private void CardReso_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ViewModel.IsPartialReturn = true;
        }

        private void OnConfirmClick(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.IsValid) return;
            if (ViewModel.IsFullVoid)
            {
                var msg = $"Confermi storno totale dello scontrino {ViewModel.SaleCodeText}?";
                if (MessageBox.Show(this, msg, "Conferma storno", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
            }
            ViewModel.TryConfirm();
        }

        private void BarcodeScanBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var text = (sender as System.Windows.Controls.TextBox)?.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                ViewModel.ApplyBarcodeScan(text);
                if (sender is System.Windows.Controls.TextBox box)
                    box.Clear();
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                // Non confermare se il focus è nello scanner: lo scanner invia Enter finale e non deve confermare
                var focused = Keyboard.FocusedElement as System.Windows.FrameworkElement;
                if (focused != null && focused.Name == "BarcodeScanBox")
                    return;
                if (ViewModel.IsValid && ViewModel.TryConfirm())
                    e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                ViewModel.Cancel();
                e.Handled = true;
            }
        }
    }
}
