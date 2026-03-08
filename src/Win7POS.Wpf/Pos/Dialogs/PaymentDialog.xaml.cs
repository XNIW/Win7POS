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

        public PaymentDialog(long totalDueMinor, PaymentReceiptDraft draft = null, Func<string, string, Task<string>> generateFiscalPdf = null, Func<string, string, Task> printFiscalToThermal = null)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyAdaptiveDialogSizing(this, minWidth: 1100, minHeight: 650, maxWidthPercent: 0.98, maxHeightPercent: 0.98, allowResize: true);
            ViewModel = new PaymentViewModel(totalDueMinor, draft, generateFiscalPdf, printFiscalToThermal);
            ViewModel.RequestClose += OnRequestClose;
            DataContext = ViewModel;
        }

        // SII Web placeholder: area fiscale temporaneamente disattivata in XAML; Navigate commentato
        // private const string SiiLoginUrl = "https://clave.w.sii.cl/...";

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (Owner != null)
            {
                Width = Math.Max(1100, Owner.ActualWidth - 24);
                Height = Math.Max(650, Owner.ActualHeight - 24);
                Left = Owner.Left + 12;
                Top = Owner.Top + 12;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (CashBox != null)
                {
                    CashBox.Focus();
                    Keyboard.Focus(CashBox);
                    CashBox.SelectAll();
                }
            }), DispatcherPriority.Input);

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

        private void BoletaNumberButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            if (BoletaNumberDialog.ShowDialog(Owner ?? Application.Current?.MainWindow, ViewModel.NextBoletaNumber, out var result))
                ViewModel.NextBoletaNumber = result;
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
