using System.Windows;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class ShopSettingsDialog : Window
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
    }
}
