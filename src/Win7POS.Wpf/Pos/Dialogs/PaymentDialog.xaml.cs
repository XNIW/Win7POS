using System.Windows;
using System.Windows.Input;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class PaymentDialog : Window
    {
        public PaymentViewModel ViewModel { get; }

        public PaymentDialog(int totalDueMinor)
        {
            InitializeComponent();
            ViewModel = new PaymentViewModel(totalDueMinor);
            ViewModel.RequestClose += OnRequestClose;
            DataContext = ViewModel;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Keyboard.Focus(CashBox);
            CashBox.SelectAll();
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (ViewModel.TryConfirm())
                    e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                ViewModel.Cancel();
                e.Handled = true;
            }
        }

        private void OnRequestClose(bool ok)
        {
            DialogResult = ok;
        }
    }
}
