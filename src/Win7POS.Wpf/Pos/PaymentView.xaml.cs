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
            if (ViewModel != null)
                ViewModel.RequestCashRefocus += FocusCashBoxAtEnd;

            Unloaded += OnUnloaded;

            Dispatcher.BeginInvoke(new Action(FocusCashBoxSelectAll), DispatcherPriority.Input);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Unloaded -= OnUnloaded;
            if (ViewModel != null)
                ViewModel.RequestCashRefocus -= FocusCashBoxAtEnd;
        }

        private void FocusCashBoxSelectAll()
        {
            if (CashBox == null) return;
            CashBox.Focus();
            Keyboard.Focus(CashBox);
            CashBox.SelectAll();
        }

        private void FocusCashBoxAtEnd()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (CashBox == null) return;
                CashBox.Focus();
                Keyboard.Focus(CashBox);
                CashBox.CaretIndex = CashBox.Text?.Length ?? 0;
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

        private void OnPaymentPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var vm = ViewModel;
            if (vm == null) return;

            var isPlusMainKeyboard = e.Key == Key.OemPlus && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            var isPlusNumpad = e.Key == Key.Add;

            if (isPlusMainKeyboard || isPlusNumpad)
            {
                if (vm.PayAllCardCommand != null && vm.PayAllCardCommand.CanExecute(null))
                {
                    vm.PayAllCardCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }

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
