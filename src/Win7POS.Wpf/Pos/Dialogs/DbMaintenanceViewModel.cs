using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Win7POS.Core;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class DbMaintenanceViewModel : INotifyPropertyChanged
    {
        private readonly Pos.PosWorkflowService _service;

        private string _outputLog = string.Empty;
        private bool _isBusy;

        public DbMaintenanceViewModel(Pos.PosWorkflowService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            BackupNowCommand = new AsyncRelayCommand(BackupNowAsync, _ => !IsBusy);
            RestoreBackupCommand = new AsyncRelayCommand(RestoreBackupAsync, _ => !IsBusy);
            IntegrityCheckCommand = new AsyncRelayCommand(IntegrityCheckAsync, _ => !IsBusy);
            VacuumCommand = new AsyncRelayCommand(VacuumAsync, _ => !IsBusy);
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
        public ICommand VacuumCommand { get; }
        public ICommand OpenFolderCommand { get; }

        public event PropertyChangedEventHandler PropertyChanged;

        private async Task BackupNowAsync()
        {
            IsBusy = true;
            try
            {
                var path = await _service.BackupDbAsync().ConfigureAwait(true);
                Append("Backup creato: " + path);
            }
            catch (Exception ex)
            {
                Append("Backup fallito: " + ex.Message);
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
                Title = "Seleziona backup DB",
                Filter = "Database (*.db)|*.db|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
                InitialDirectory = BackupsDirectory
            };
            if (dlg.ShowDialog() != true) return;

            IsBusy = true;
            try
            {
                await _service.RestoreDbAsync(dlg.FileName).ConfigureAwait(true);
                Append("Ripristino completato da: " + dlg.FileName);
                MessageBox.Show("Ripristino completato. Riavvia l'app.", "Gestione DB", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Append("Ripristino fallito: " + ex.Message);
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
                Append("Integrity check:" + Environment.NewLine + text);
            }
            catch (Exception ex)
            {
                Append("Integrity check fallito: " + ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task VacuumAsync()
        {
            IsBusy = true;
            try
            {
                await _service.VacuumAsync().ConfigureAwait(true);
                Append("VACUUM completato.");
            }
            catch (Exception ex)
            {
                Append("VACUUM fallito: " + ex.Message);
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
                Append("Open folder fallito: " + ex.Message);
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
            (VacuumCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
            public async void Execute(object parameter) => await _executeAsync().ConfigureAwait(true);
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
