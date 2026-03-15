using System.Windows;
using Win7POS.Wpf.Chrome;

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
                Owner = owner ?? Application.Current?.MainWindow
            };
            Win7POS.Wpf.Infrastructure.WindowSizingHelper.CapMaxHeightToOwner(dlg);
            dlg.ShowDialog();
        }
    }
}
