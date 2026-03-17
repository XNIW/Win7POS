using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Import
{
    public partial class ImportDataDialog : DialogShellWindow
    {
        public ImportDataDialog()
        {
            InitializeComponent();
            DataContext = new ImportViewModel();
        }

        public static void ShowDialog(Window owner)
        {
            var dlg = new ImportDataDialog
            {
                Owner = DialogOwnerHelper.GetSafeOwner(owner)
            };
            WindowSizingHelper.CapMaxHeightToOwner(dlg);
            dlg.ShowDialog();
        }

        private void Annulla_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
