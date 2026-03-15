using System.Windows;
using Win7POS.Wpf.Chrome;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class ShopSettingsDialog : DialogShellWindow
    {
        public ShopSettingsDialog(ShopSettingsViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            vm.RequestClose += ok =>
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.Invoke(() => CloseWithResult(ok));
                    return;
                }
                CloseWithResult(ok);
            };
        }

        private void CloseWithResult(bool ok)
        {
            try { DialogResult = ok; }
            catch { Close(); }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            CloseWithResult(false);
        }
    }
}
