using System.Windows;
using System;
using System.Windows.Threading;
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
            if (!restoreReviewOnly)
            {
                _ = vm.InitializeBackupSettingsAsync();
                var resultTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                resultTimer.Tick += async (_, __) =>
                {
                    resultTimer.Stop();
                    try { await vm.RefreshBackupResultAsync(); }
                    finally { if (IsVisible) resultTimer.Start(); }
                };
                Closed += (_, __) => resultTimer.Stop();
                resultTimer.Start();
            }
            if (restoreReviewOnly)
            {
                BackupAutomationPanel.Visibility = Visibility.Collapsed;
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
