using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class DbMaintenanceDialog : DialogShellWindow
    {
        public DbMaintenanceDialog(DbMaintenanceViewModel vm, bool restoreReviewOnly = false)
        {
            InitializeComponent();
            vm.OwnerWindow = this;
            WindowSizingHelper.ApplyAdaptiveDialogSizing(this, minWidth: 640, minHeight: 420, maxWidthPercent: 0.92, maxHeightPercent: 0.92, allowResize: true);
            DataContext = vm;
            if (restoreReviewOnly)
            {
                BackupNowButton.Visibility = Visibility.Collapsed;
                RestoreBackupButton.Visibility = Visibility.Collapsed;
                VacuumButton.Visibility = Visibility.Collapsed;
                SupplierImportButton.Visibility = Visibility.Collapsed;
                OpenFolderButton.Visibility = Visibility.Collapsed;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
