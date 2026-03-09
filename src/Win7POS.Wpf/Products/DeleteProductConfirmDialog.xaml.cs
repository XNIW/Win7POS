using System.Windows;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Products
{
    public partial class DeleteProductConfirmDialog : Window
    {
        public DeleteProductConfirmDialog(string barcode, string name)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyAdaptiveDialogSizing(this, minWidth: 360, minHeight: 180, maxWidthPercent: 0.92, maxHeightPercent: 0.92, allowResize: false);
            BarcodeText.Text = barcode ?? "";
            NameText.Text = name ?? "";
        }

        public static bool ShowDialog(Window owner, string barcode, string name)
        {
            var dlg = new DeleteProductConfirmDialog(barcode ?? "", name ?? "")
            {
                Owner = owner
            };
            WindowSizingHelper.CapMaxHeightToOwner(dlg);
            return dlg.ShowDialog() == true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Elimina_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
