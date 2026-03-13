using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class ShopSettingsDialog : DialogShellWindow
    {
        public ShopSettingsDialog(ShopSettingsViewModel vm)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyAdaptiveDialogSizing(this, minWidth: 520, minHeight: 420, maxWidthPercent: 0.92, maxHeightPercent: 0.92, allowResize: true);
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
