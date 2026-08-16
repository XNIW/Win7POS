using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class CartViewSettingsDialog : DialogShellWindow
    {
        public CartViewSettingsDialog(CartViewMode currentMode)
        {
            InitializeComponent();
            RowsOption.IsChecked = currentMode != CartViewMode.Grid;
            GridOption.IsChecked = currentMode == CartViewMode.Grid;
        }

        public CartViewMode SelectedMode { get; private set; }

        public static CartViewMode? ShowDialog(
            Window ownerWindow,
            CartViewMode currentMode)
        {
            var dialog = new CartViewSettingsDialog(currentMode)
            {
                Owner = ownerWindow ?? DialogOwnerHelper.GetSafeOwner()
            };
            return dialog.ShowDialog() == true
                ? dialog.SelectedMode
                : (CartViewMode?)null;
        }

        private void OnApplyClick(object sender, RoutedEventArgs e)
        {
            SelectedMode = GridOption.IsChecked == true
                ? CartViewMode.Grid
                : CartViewMode.Rows;
            DialogResult = true;
            Close();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
