using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Win7POS.Wpf.Pos.Dialogs;

namespace Win7POS.Wpf.Pos
{
    public partial class PaymentView
    {
        // SII Web: area fiscale temporaneamente disattivata (placeholder in XAML); Navigate non usato
        // private const string SiiLoginUrl = "https://clave.w.sii.cl/...";

        public PaymentView()
        {
            InitializeComponent();
        }

        private PaymentViewModel ViewModel => DataContext as PaymentViewModel;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (CashBox != null)
                {
                    CashBox.Focus();
                    Keyboard.Focus(CashBox);
                    CashBox.SelectAll();
                }
            }), DispatcherPriority.Input);
        }

        private void CashBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                ViewModel.ActiveField = PaymentActiveField.Cash;
        }

        private void CashBox_LostFocus(object sender, RoutedEventArgs e) { }

        private void CardBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                ViewModel.ActiveField = PaymentActiveField.Card;
        }

        private void CardBox_LostFocus(object sender, RoutedEventArgs e) { }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            var vm = ViewModel;
            if (vm == null) return;

            if (e.Key == Key.Enter)
            {
                if (vm.TryConfirm())
                    e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                vm.Cancel();
                e.Handled = true;
            }
        }

        private void BoletaNumberButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = ViewModel;
            if (vm == null) return;
            var owner = Window.GetWindow(this) ?? Application.Current?.MainWindow;
            if (BoletaNumberDialog.ShowDialog(owner, vm.NextBoletaNumber, out var result))
                vm.NextBoletaNumber = result;
        }
    }
}
