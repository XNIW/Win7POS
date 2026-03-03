using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class PaymentDialog : Window
    {
        public PaymentViewModel ViewModel { get; }

        public PaymentDialog(int totalDueMinor, PaymentReceiptDraft draft = null, Func<string, string, Task<string>> generateFiscalPdf = null)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyDialogSizing(this, widthPercent: 0.85, heightPercent: 0.8, minWidth: 900, minHeight: 550);
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
                if (SiiQrImage == null) return;
                if (!File.Exists(path))
                {
                    SiiQrImage.Visibility = Visibility.Collapsed;
                    return;
                }

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();

                SiiQrImage.Source = bmp;
                SiiQrImage.Visibility = Visibility.Visible;
            }
            catch
            {
                if (SiiQrImage != null)
                    SiiQrImage.Visibility = Visibility.Collapsed;
            }
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
