using System.Windows;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class ShopSettingsDialog : Window
    {
        public ShopSettingsDialog(ShopSettingsViewModel vm)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyDialogSizing(this, widthPercent: 0.6, heightPercent: 0.65, minWidth: 560, minHeight: 450);
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
