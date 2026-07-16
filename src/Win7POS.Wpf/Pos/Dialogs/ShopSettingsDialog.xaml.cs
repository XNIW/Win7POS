using System;
using System.ComponentModel;
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
            vm.OwnerWindow = this;
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

        protected override void OnClosing(CancelEventArgs e)
        {
            if ((DataContext as ShopSettingsViewModel)?.CanClose == false)
            {
                e.Cancel = true;
                return;
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (DataContext is ShopSettingsViewModel viewModel)
            {
                viewModel.Dispose();
            }

            DataContext = null;
            base.OnClosed(e);
        }
    }
}
