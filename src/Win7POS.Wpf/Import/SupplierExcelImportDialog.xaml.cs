using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Import
{
    public partial class SupplierExcelImportDialog : DialogShellWindow
    {
        private readonly SupplierExcelImportViewModel _viewModel;

        public SupplierExcelImportDialog()
        {
            InitializeComponent();
            _viewModel = new SupplierExcelImportViewModel(
                fileDialogService: new SupplierExcelFileDialogService(() => this));
            _viewModel.RequestClose += OnRequestClose;
            DataContext = _viewModel;
        }

        public static bool ShowDialog(Window owner)
        {
            var dlg = new SupplierExcelImportDialog
            {
                Owner = DialogOwnerHelper.GetSafeOwner(owner)
            };
            WindowSizingHelper.CapMaxHeightToOwner(dlg);
            return dlg.ShowDialog() == true;
        }

        private void OnRequestClose(bool success)
        {
            DialogResult = success;
            Close();
        }
    }
}
