using System.Windows;
using System.Windows.Input;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class RefundDialog : Window
    {
        public RefundViewModel ViewModel { get; }

        public RefundDialog(RefundViewModel vm)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyDialogSizing(this, widthPercent: 0.8, heightPercent: 0.75, minWidth: 780, minHeight: 550);
            ViewModel = vm;
            ViewModel.RequestClose += OnRequestClose;
            DataContext = vm;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Keyboard.Focus(CashRefundBox);
            CashRefundBox.SelectAll();
        }

        private void OnRequestClose(bool ok)
        {
            DialogResult = ok;
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
    }
}
