using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class PaymentDialog : Window
    {
        public PaymentViewModel ViewModel { get; }

        public PaymentDialog(int totalDueMinor, PaymentReceiptDraft draft = null, Func<string, string, Task<string>> generateFiscalPdf = null)
        {
            InitializeComponent();
            ViewModel = new PaymentViewModel(totalDueMinor, draft, generateFiscalPdf);
            ViewModel.RequestClose += OnRequestClose;
            DataContext = ViewModel;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Keyboard.Focus(CashBox);
            CashBox.SelectAll();
            LoadSiiQrCode();
        }

        private void LoadSiiQrCode()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "sii_qrcode.png");
                if (File.Exists(path) && SiiQrImage != null)
                {
                    SiiQrImage.Source = new BitmapImage(new Uri(path, UriKind.Absolute));
                }
            }
            catch { }
        }

        private void CashBox_GotFocus(object sender, RoutedEventArgs e)
        {
            ViewModel.ActiveField = PaymentActiveField.Cash;
        }

        private void CashBox_LostFocus(object sender, RoutedEventArgs e) { }

        private void CardBox_GotFocus(object sender, RoutedEventArgs e)
        {
            ViewModel.ActiveField = PaymentActiveField.Card;
        }

        private void CardBox_LostFocus(object sender, RoutedEventArgs e) { }

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
            void SetResult()
            {
                if (!IsLoaded)
                {
                    try { Close(); } catch { }
                    return;
                }
                try
                {
                    DialogResult = ok;
                }
                catch (InvalidOperationException)
                {
                    try { Close(); } catch { }
                }
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(DispatcherPriority.Normal, (Action)(() => SetResult()));
                return;
            }
            SetResult();
        }
    }
}
