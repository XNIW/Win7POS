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
using Win7POS.Wpf.Localization;

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
        private string _lastAnalyzeFingerprint = string.Empty;

        // Preview items (kept as object to avoid hard coupling if core types change)
        public ObservableCollection<object> DiffItems { get; } = new ObservableCollection<object>();

        public string SelectedPath
        {
            get => _selectedPath ?? string.Empty;
            set
            {
                var next = value ?? string.Empty;
                if (string.Equals(_selectedPath ?? string.Empty, next, StringComparison.Ordinal))
                    return;
                _selectedPath = value ?? string.Empty;
                InvalidateAnalyzeResult(PosLocalization.T("import.analyzeFirst"));
                OnPropertyChanged();
                OnPropertyChanged(nameof(Kind));
                OnPropertyChanged(nameof(IsCsv));
                OnPropertyChanged(nameof(IsXlsx));
                OnPropertyChanged(nameof(IsXls));
                OnPropertyChanged(nameof(CanApplyImport));
                RaiseCanExecuteChanged();
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
            set
            {
                _isBusy = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanApplyImport));
                RaiseCanExecuteChanged();
            }
        }

        public bool InsertNew
        {
            get => _insertNew;
            set
            {
                if (_insertNew == value) return;
                _insertNew = value;
                InvalidateAnalyzeResult(PosLocalization.T("import.analyzeFirst"));
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanApplyImport));
            }
        }

        public bool UpdatePrice
        {
            get => _updatePrice;
            set
            {
                if (_updatePrice == value) return;
                _updatePrice = value;
                InvalidateAnalyzeResult(PosLocalization.T("import.analyzeFirst"));
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanApplyImport));
            }
        }

        public bool UpdateName
        {
            get => _updateName;
            set
            {
                if (_updateName == value) return;
                _updateName = value;
                InvalidateAnalyzeResult(PosLocalization.T("import.analyzeFirst"));
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanApplyImport));
            }
        }

        public bool DryRun
        {
            get => _dryRun;
            set
            {
                _dryRun = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ApplyModeText));
                RaiseCanExecuteChanged();
            }
        }

        public string DbPath => AppPaths.DbPath;

        public string ApplyModeText => PosLocalization.T(DryRun ? "import.modeDryRun" : "import.modeApply");
        public bool CanApplyImport => !IsBusy && HasCurrentAnalyzeResult();

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
            ApplyCommand = new AsyncRelayCommand(ApplyAsync, _ => CanApplyImport);
            Summary = PosLocalization.T("import.initialSummary");
            Status = "";
        }

        private void Browse()
        {
            var dlg = new OpenFileDialog
            {
                Title = PosLocalization.T("import.selectFileTitle"),
                Filter = PosLocalization.T("import.dataFileFilter"),
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
                Status = PosLocalization.T("import.selectExistingFile");
                return;
            }

            IsBusy = true;
            Status = PosLocalization.T("import.analyzing");
            Summary = "";
            DiffItems.Clear();
            ClearAnalyzeCache();

            try
            {
                if (Kind != ImportKind.Csv && Kind != ImportKind.Xls && Kind != ImportKind.Xlsx)
                {
                    Status = PosLocalization.T("import.unsupportedExtension");
                    return;
                }

                var result = await _service.AnalyzeAsync(path).ConfigureAwait(true);
                _lastParsedRows = result.RowsModel;
                _lastDiffResult = result.DiffModel;
                _lastDedicatedSuppliers = result.DedicatedSuppliers;
                _lastDedicatedCategories = result.DedicatedCategories;
                _lastPriceHistoryRows = result.PriceHistoryRows;
                _lastAnalyzeFingerprint = BuildCurrentAnalyzeFingerprint();

                Summary = result.Summary;
                DiffItems.Clear();
                foreach (var item in result.Items)
                    DiffItems.Add(item);
                Status = PosLocalization.F(
                    "import.analysisSummary",
                    result.NewCount,
                    result.UpdateCount,
                    result.UnchangedCount,
                    result.ErrorCount);
                OnPropertyChanged(nameof(CanApplyImport));
                RaiseCanExecuteChanged();
            }
            catch (Exception ex)
            {
                Status = PosLocalization.F("common.errorWithMessage", ex.Message);
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
                Status = PosLocalization.T("import.operationInProgress");
                return;
            }

            if (Kind != ImportKind.Csv && Kind != ImportKind.Xls && Kind != ImportKind.Xlsx)
            {
                Status = PosLocalization.T("import.selectSupportedFile");
                return;
            }

            await ApplyUnifiedAsync().ConfigureAwait(true);
        }

        private async Task ApplyUnifiedAsync()
        {
            if (_lastDiffResult == null || _lastParsedRows == null)
            {
                Status = PosLocalization.T("import.analyzeFirst");
                return;
            }
            if (!HasCurrentAnalyzeResult())
            {
                InvalidateAnalyzeResult(PosLocalization.T("import.analyzeFirst"));
                Status = PosLocalization.T("import.analyzeFirst");
                return;
            }

            if (!DryRun)
            {
                if (!ApplyConfirmDialog.ShowConfirm(
                    Application.Current?.MainWindow,
                    PosLocalization.T("import.confirmApplyTitle"),
                    PosLocalization.T("import.confirmApplyMessage")))
                {
                    Status = PosLocalization.T("import.applyCancelled");
                    return;
                }
            }

            IsBusy = true;
            Status = PosLocalization.T(DryRun ? "import.applyDryRun" : "import.applyInProgress");

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
                    ? PosLocalization.T(DryRun ? "import.applyDryRunOk" : "import.applyOk")
                    : PosLocalization.T("import.applyCompletedWithErrors");
                if (result.Success && !DryRun)
                    Win7POS.Wpf.Infrastructure.CatalogEvents.RaiseCatalogChanged(null);
                OnPropertyChanged(nameof(CanApplyImport));
            }
            catch (Exception ex)
            {
                Status = PosLocalization.F("import.applyError", ex.Message);
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

        private void InvalidateAnalyzeResult(string status)
        {
            if (_lastDiffResult == null && _lastParsedRows == null && DiffItems.Count == 0)
                return;

            ClearAnalyzeCache();
            DiffItems.Clear();
            Status = status ?? string.Empty;
            Summary = PosLocalization.T("import.initialSummary");
            OnPropertyChanged(nameof(CanApplyImport));
            RaiseCanExecuteChanged();
        }

        private void ClearAnalyzeCache()
        {
            _lastDiffResult = null;
            _lastParsedRows = null;
            _lastDedicatedSuppliers = null;
            _lastDedicatedCategories = null;
            _lastPriceHistoryRows = null;
            _lastAnalyzeFingerprint = string.Empty;
            OnPropertyChanged(nameof(CanApplyImport));
            RaiseCanExecuteChanged();
        }

        private bool HasCurrentAnalyzeResult()
        {
            if (_lastDiffResult == null || _lastParsedRows == null || string.IsNullOrWhiteSpace(_lastAnalyzeFingerprint))
                return false;
            return string.Equals(_lastAnalyzeFingerprint, BuildCurrentAnalyzeFingerprint(), StringComparison.Ordinal);
        }

        private string BuildCurrentAnalyzeFingerprint()
        {
            var path = (SelectedPath ?? string.Empty).Trim();
            var fullPath = string.Empty;
            long length = -1;
            long lastWriteTicks = -1;
            try
            {
                if (path.Length > 0 && File.Exists(path))
                {
                    var info = new FileInfo(path);
                    fullPath = info.FullName;
                    length = info.Length;
                    lastWriteTicks = info.LastWriteTimeUtc.Ticks;
                }
                else
                {
                    fullPath = Path.GetFullPath(path);
                }
            }
            catch
            {
                fullPath = path;
            }

            return string.Join("|", new[]
            {
                Kind.ToString(),
                fullPath,
                length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                lastWriteTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                InsertNew.ToString(),
                UpdatePrice.ToString(),
                UpdateName.ToString()
            });
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
