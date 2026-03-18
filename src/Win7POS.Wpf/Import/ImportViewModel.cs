using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Win7POS.Core;
using Win7POS.Data;

namespace Win7POS.Wpf.Import
{
    public enum ImportKind { None, Csv, Xls, Xlsx }

    public sealed class ImportViewModel : INotifyPropertyChanged
    {
        private string _selectedPath;
        private string _summary;
        private string _status;
        private bool _isBusy;

        private bool _insertNew = true;
        private bool _updatePrice = true;
        private bool _updateName = false;
        private bool _dryRun = true;

        // Preview items (kept as object to avoid hard coupling if core types change)
        public ObservableCollection<object> DiffItems { get; } = new ObservableCollection<object>();

        public string SelectedPath
        {
            get => _selectedPath ?? string.Empty;
            set
            {
                _selectedPath = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Kind));
                OnPropertyChanged(nameof(IsCsv));
                OnPropertyChanged(nameof(IsXlsx));
            }
        }

        public ImportKind Kind
        {
            get
            {
                var p = (SelectedPath ?? "").Trim();
                var ext = Path.GetExtension(p).ToLowerInvariant();
                if (ext == ".csv") return ImportKind.Csv;
                if (ext == ".xls") return ImportKind.Xls;
                if (ext == ".xlsx") return ImportKind.Xlsx;
                return ImportKind.None;
            }
        }

        public bool IsCsv => Kind == ImportKind.Csv;
        public bool IsXlsx => Kind == ImportKind.Xlsx;
        public bool IsXls => Kind == ImportKind.Xls;

        public string Summary
        {
            get => _summary;
            set { _summary = value; OnPropertyChanged(); }
        }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public bool InsertNew
        {
            get => _insertNew;
            set { _insertNew = value; OnPropertyChanged(); }
        }

        public bool UpdatePrice
        {
            get => _updatePrice;
            set { _updatePrice = value; OnPropertyChanged(); }
        }

        public bool UpdateName
        {
            get => _updateName;
            set { _updateName = value; OnPropertyChanged(); }
        }

        public bool DryRun
        {
            get => _dryRun;
            set
            {
                _dryRun = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ApplyModeText));
            }
        }

        public string DbPath => AppPaths.DbPath;

        public string ApplyModeText => DryRun ? "Mode: DryRun (no DB write)" : "Mode: Apply (write to DB)";

        public ICommand BrowseCommand { get; }
        public ICommand AnalyzeCommand { get; }
        public ICommand ApplyCommand { get; }

        private readonly ImportWorkflowService _service = new ImportWorkflowService();

        // Cache last analyze result for Apply (unico per CSV e XLSX)
        private object _lastDiffResult;
        private object _lastParsedRows;
        private System.Collections.Generic.IReadOnlyList<Win7POS.Core.ImportDb.SupplierRow> _lastDedicatedSuppliers;
        private System.Collections.Generic.IReadOnlyList<Win7POS.Core.ImportDb.CategoryRow> _lastDedicatedCategories;
        private System.Collections.Generic.IReadOnlyList<Win7POS.Core.ImportDb.PriceHistoryRow> _lastPriceHistoryRows;

        public ImportViewModel()
        {
            BrowseCommand = new RelayCommand(_ => Browse(), _ => !IsBusy);
            AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, _ => !IsBusy);
            ApplyCommand = new AsyncRelayCommand(ApplyAsync, _ => !IsBusy);
            Summary = "Seleziona un file .csv, .xls o .xlsx e premi Analizza.";
            Status = "";
        }

        private void Browse()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Seleziona file da importare",
                Filter = "File dati (*.csv;*.xlsx;*.xls)|*.csv;*.xlsx;*.xls|CSV (*.csv)|*.csv|Excel (*.xlsx)|*.xlsx|Excel 97-2003 (*.xls)|*.xls|Tutti (*.*)|*.*",
                FilterIndex = 1,
                CheckFileExists = true,
                Multiselect = false
            };

            if (dlg.ShowDialog() == true)
            {
                SelectedPath = dlg.FileName;
                Status = "";
            }
        }

        private async Task AnalyzeAsync()
        {
            var path = (SelectedPath ?? "").Trim();
            if (path.Length == 0 || !File.Exists(path))
            {
                Status = "Seleziona un file esistente.";
                return;
            }

            IsBusy = true;
            Status = "Analisi in corso...";
            Summary = "";
            DiffItems.Clear();
            _lastDiffResult = null;
            _lastParsedRows = null;
            _lastDedicatedSuppliers = null;
            _lastDedicatedCategories = null;
            _lastPriceHistoryRows = null;

            try
            {
                if (Kind != ImportKind.Csv && Kind != ImportKind.Xls && Kind != ImportKind.Xlsx)
                {
                    Status = "Estensione non supportata. Usa .csv, .xls o .xlsx.";
                    return;
                }

                var result = await _service.AnalyzeAsync(path).ConfigureAwait(true);
                _lastParsedRows = result.RowsModel;
                _lastDiffResult = result.DiffModel;
                _lastDedicatedSuppliers = result.DedicatedSuppliers;
                _lastDedicatedCategories = result.DedicatedCategories;
                _lastPriceHistoryRows = result.PriceHistoryRows;

                Summary = result.Summary;
                DiffItems.Clear();
                foreach (var item in result.Items)
                    DiffItems.Add(item);
                Status = "OK N/U/NC/E: " + result.NewCount + "/" + result.UpdateCount + "/" + result.UnchangedCount + "/" + result.ErrorCount;
            }
            catch (Exception ex)
            {
                Status = "Errore: " + ex.Message;
                Summary = ex.ToString();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ApplyAsync()
        {
            if (IsBusy)
            {
                Status = "Operazione in corso...";
                return;
            }

            if (Kind != ImportKind.Csv && Kind != ImportKind.Xls && Kind != ImportKind.Xlsx)
            {
                Status = "Seleziona un file .csv, .xls o .xlsx.";
                return;
            }

            await ApplyUnifiedAsync().ConfigureAwait(true);
        }

        private async Task ApplyUnifiedAsync()
        {
            if (_lastDiffResult == null || _lastParsedRows == null)
            {
                Status = "Prima esegui Analizza.";
                return;
            }

            if (!DryRun)
            {
                if (!ApplyConfirmDialog.ShowConfirm(Application.Current?.MainWindow, "Conferma Apply", "Confermi Apply? Verranno scritti dati nel DB."))
                {
                    Status = "Apply annullato.";
                    return;
                }
            }

            IsBusy = true;
            Status = DryRun ? "DryRun: simulazione Apply..." : "Apply in corso...";

            try
            {
                var result = await _service.ApplyAsync(
                    _lastParsedRows,
                    _lastDiffResult,
                    InsertNew,
                    UpdatePrice,
                    UpdateName,
                    DryRun,
                    "",
                    _lastDedicatedSuppliers,
                    _lastDedicatedCategories,
                    _lastPriceHistoryRows).ConfigureAwait(true);

                Summary = result.Summary;
                Status = result.Success
                    ? (DryRun ? "DryRun OK (nessuna scrittura DB)." : "Apply OK.")
                    : "Apply completato con errori.";
                if (result.Success && !DryRun)
                    Win7POS.Wpf.Infrastructure.CatalogEvents.RaiseCatalogChanged(null);
            }
            catch (Exception ex)
            {
                Status = "Errore Apply: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void RaiseCanExecuteChanged()
        {
            (BrowseCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AnalyzeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ApplyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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
                    Win7POS.Wpf.Infrastructure.UiErrorHandler.Handle(ex, null, "Import AsyncRelayCommand failed");
                }
            }

            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
