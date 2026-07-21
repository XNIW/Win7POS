using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Win7POS.Core;
using Win7POS.Core.Security;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class DbMaintenanceViewModel : INotifyPropertyChanged
    {
        private readonly Pos.PosWorkflowService _service;
        private readonly Func<Task<bool>> _demandRestorePermission;
        private readonly Func<bool> _hasBackupPermission;
        private readonly Func<bool> _hasCatalogImportPermission;
        private readonly Func<bool> _hasMaintenancePermission;

        private string _outputLog = string.Empty;
        private bool _isBusy;

        public DbMaintenanceViewModel(
            Pos.PosWorkflowService service,
            Func<Task<bool>> demandRestorePermission,
            Func<bool> hasBackupPermission,
            Func<bool> hasCatalogImportPermission,
            Func<bool> hasMaintenancePermission)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _demandRestorePermission = demandRestorePermission ?? (() => Task.FromResult(false));
            _hasBackupPermission = hasBackupPermission ?? (() => false);
            _hasCatalogImportPermission = hasCatalogImportPermission ?? (() => false);
            _hasMaintenancePermission = hasMaintenancePermission ?? (() => false);
            BackupNowCommand = new AsyncRelayCommand(BackupNowAsync, _ => !IsBusy);
            RestoreBackupCommand = new AsyncRelayCommand(RestoreBackupAsync, _ => !IsBusy);
            IntegrityCheckCommand = new AsyncRelayCommand(IntegrityCheckAsync, _ => !IsBusy);
            CompleteRestoreReviewCommand = new AsyncRelayCommand(CompleteRestoreReviewAsync, _ => !IsBusy);
            VacuumCommand = new AsyncRelayCommand(VacuumAsync, _ => !IsBusy);
            SupplierExcelImportCommand = new RelayCommand(_ => OpenSupplierExcelImport(), _ => !IsBusy);
            OpenFolderCommand = new RelayCommand(_ => OpenFolder(), _ => !IsBusy);
        }

        public string DbPath => _service.DbPath;
        public string BackupsDirectory => AppPaths.BackupsDirectory;
        public string ExportsDirectory => AppPaths.ExportsDirectory;

        public string OutputLog
        {
            get => _outputLog;
            set { _outputLog = value ?? string.Empty; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public ICommand BackupNowCommand { get; }
        public ICommand RestoreBackupCommand { get; }
        public ICommand IntegrityCheckCommand { get; }
        public ICommand CompleteRestoreReviewCommand { get; }
        public ICommand VacuumCommand { get; }
        public ICommand SupplierExcelImportCommand { get; }
        public ICommand OpenFolderCommand { get; }

        public event PropertyChangedEventHandler PropertyChanged;
        internal Window OwnerWindow { get; set; }

        private async Task BackupNowAsync()
        {
            if (!_hasBackupPermission())
            {
                Append(PosLocalization.F(
                    "common.permissionDeniedOperation",
                    PosLocalization.T("operations.dbBackup")));
                return;
            }

            IsBusy = true;
            try
            {
                var path = await _service.BackupDbAsync().ConfigureAwait(true);
                Append(PosLocalization.F("dbMaintenance.backupCreated", path));
            }
            catch (Exception ex)
            {
                Append(PosLocalization.F("dbMaintenance.backupFailed", ex.Message));
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RestoreBackupAsync()
        {
            var dlg = new OpenFileDialog
            {
                Title = PosLocalization.T("dbMaintenance.selectBackupTitle"),
                Filter = PosLocalization.T("dbMaintenance.databaseFileFilter"),
                CheckFileExists = true,
                Multiselect = false,
                InitialDirectory = BackupsDirectory
            };
            var owner = DialogOwnerHelper.GetSafeOwner(OwnerWindow);
            if (owner == null)
                throw new InvalidOperationException(PosLocalization.T("dbMaintenance.restoreOwnerMissing"));

            owner.Activate();
            if (dlg.ShowDialog(owner) != true) return;

            if (!(await _demandRestorePermission().ConfigureAwait(true)))
            {
                Append(PosLocalization.T("dbMaintenance.restorePermissionDenied"));
                return;
            }

            IsBusy = true;
            try
            {
                var result = await _service.RestoreDbAsync(dlg.FileName).ConfigureAwait(true);
                OperatorSessionHolder.Current?.LogSecurityEvent(SecurityEventCodes.DbRestore, "backupFile=" + (Path.GetFileName(dlg.FileName) ?? ""));
                OperatorSessionHolder.Current?.LogoutForced();
                Append(PosLocalization.F("dbMaintenance.restoreCompletedFrom", Path.GetFileName(dlg.FileName) ?? "backup.db"));
                Append(PosLocalization.F("dbMaintenance.preRestoreBackup", Path.GetFileName(result.PreRestoreBackupPath) ?? "n/a"));
                Append(PosLocalization.F("dbMaintenance.integrityCheckResult", result.IntegrityCheck));
                Append(PosLocalization.T("dbMaintenance.restoreSyncReview"));
                Win7POS.Wpf.Import.ModernMessageDialog.Show(
                    OwnerWindow ?? DialogOwnerHelper.GetSafeOwner(),
                    PosLocalization.T("dbMaintenance.title"),
                    PosLocalization.T("dbMaintenance.restoreCompletedMessage"));
            }
            catch (Exception ex)
            {
                Append(PosLocalization.F("dbMaintenance.restoreFailed", ex.Message));
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task IntegrityCheckAsync()
        {
            IsBusy = true;
            try
            {
                var text = await _service.IntegrityCheckAsync().ConfigureAwait(true);
                Append(PosLocalization.F("dbMaintenance.integrityCheckBlock", text));
            }
            catch (Exception ex)
            {
                Append(PosLocalization.F("dbMaintenance.integrityCheckFailed", ex.Message));
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task CompleteRestoreReviewAsync()
        {
            if (!_hasMaintenancePermission())
            {
                Append(PosLocalization.F(
                    "common.permissionDeniedOperation",
                    PosLocalization.T("operations.dbMaintenance")));
                return;
            }

            IsBusy = true;
            try
            {
                await _service.CompleteRestoreSyncReviewAsync().ConfigureAwait(true);
                Append(PosLocalization.T("dbMaintenance.restoreReviewCompleted"));
            }
            catch (Exception ex)
            {
                Append(PosLocalization.F("dbMaintenance.restoreReviewFailed", ex.Message));
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task VacuumAsync()
        {
            if (!_hasMaintenancePermission())
            {
                Append(PosLocalization.F(
                    "common.permissionDeniedOperation",
                    PosLocalization.T("operations.dbMaintenance")));
                return;
            }

            IsBusy = true;
            try
            {
                await _service.VacuumAsync().ConfigureAwait(true);
                Append(PosLocalization.T("dbMaintenance.vacuumCompleted"));
            }
            catch (Exception ex)
            {
                Append(PosLocalization.F("dbMaintenance.vacuumFailed", ex.Message));
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void OpenFolder()
        {
            try
            {
                AppPaths.EnsureCreated();
                var path = AppPaths.DataDirectory;
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    Process.Start("explorer.exe", path);
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                Append(PosLocalization.F("dbMaintenance.openFolderFailed", ex.Message));
            }
        }

        private void OpenSupplierExcelImport()
        {
            if (!_hasCatalogImportPermission())
            {
                Append(PosLocalization.F(
                    "common.permissionDeniedOperation",
                    PosLocalization.T("products.operationImportCatalog")));
                return;
            }

            try
            {
                var applied = SupplierExcelImportDialog.ShowDialog(
                    OwnerWindow ?? DialogOwnerHelper.GetSafeOwner(),
                    _hasCatalogImportPermission);
                if (applied)
                {
                    Win7POS.Wpf.Infrastructure.CatalogEvents.RaiseCatalogChanged(null);
                    Append(PosLocalization.T("supplierExcelImport.completed"));
                }
                else
                {
                    Append(PosLocalization.T("supplierExcelImport.cancelled"));
                }
            }
            catch (Exception ex)
            {
                Append(PosLocalization.F("supplierExcelImport.failed", ex.Message));
            }
        }

        private void Append(string line)
        {
            OutputLog = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + Environment.NewLine + OutputLog;
        }

        private void RaiseCanExecuteChanged()
        {
            (BackupNowCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RestoreBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (IntegrityCheckCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (CompleteRestoreReviewCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (VacuumCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (SupplierExcelImportCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private sealed class AsyncRelayCommand : ICommand
        {
            private readonly Func<Task> _executeAsync;
            private readonly Func<object, bool> _canExecute;

            public AsyncRelayCommand(Func<Task> executeAsync, Func<object, bool> canExecute = null)
            {
                _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
                _canExecute = canExecute;
            }

            public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);

            public async void Execute(object parameter)
            {
                try
                {
                    await _executeAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Win7POS.Wpf.Infrastructure.UiErrorHandler.Handle(ex, null, "DbMaintenance AsyncRelayCommand failed");
                }
            }
            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }

        private sealed class RelayCommand : ICommand
        {
            private readonly Action<object> _execute;
            private readonly Func<object, bool> _canExecute;

            public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);
            public void Execute(object parameter) => _execute(parameter);
            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
