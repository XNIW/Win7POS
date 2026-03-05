using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Win7POS.Wpf.Pos.Dialogs;

namespace Win7POS.Wpf.Pos
{
    public partial class PaymentView
    {
        private const string SiiLoginUrl =
            "https://clave.w.sii.cl/oauthsii-v1/?response_type=code&client_id=e0378e96-4014-4a47-b852-9d9246797f5c&redirect_uri=https://eboleta.sii.cl/emitir/&scope=user_info&state=730b12d3-0586-42cb-8d8e-57c15125a8a9";

        public PaymentView()
        {
            InitializeComponent();
        }

        private PaymentViewModel ViewModel => DataContext as PaymentViewModel;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Keyboard.Focus(CashBox);
            CashBox.SelectAll();
            LoadSiiQrCode();
            try
            {
                if (SiiWeb != null)
                    SiiWeb.Navigate(SiiLoginUrl);
            }
            catch { }
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
    }
}
