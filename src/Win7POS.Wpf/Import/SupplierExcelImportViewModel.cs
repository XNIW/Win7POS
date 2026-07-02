using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Win7POS.Core.Import;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Import
{
    public sealed class SupplierExcelImportViewModel : INotifyPropertyChanged
    {
        private readonly SupplierExcelImportWorkflowService _service;
        private readonly FileLogger _logger = new FileLogger("SupplierExcelImportViewModel");
        private int _stepIndex;
        private string _selectedPath = string.Empty;
        private string _status = string.Empty;
        private string _markupPercent = "30";
        private int _roundTo = 100;
        private bool _applyOnlyEmptyRetailPrice = true;
        private bool _isBusy;
        private SupplierImportAnalysis _analysis;

        public SupplierExcelImportViewModel(SupplierExcelImportWorkflowService service = null)
        {
            _service = service ?? new SupplierExcelImportWorkflowService();
            ColumnKeyOptions = new ObservableCollection<string>(new[] { string.Empty }.Concat(AndroidImportKeys.AllKeys));
            BrowseCommand = new RelayCommand(Browse, () => !IsBusy && StepIndex == 0);
            AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !IsBusy && File.Exists(SelectedPath));
            BackCommand = new RelayCommand(Back, () => !IsBusy && StepIndex > 0);
            NextCommand = new AsyncRelayCommand(NextAsync, () => !IsBusy && StepIndex == 1 && CanProceedToStep3);
            ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => !IsBusy && StepIndex == 2 && CanApply);
            ApplyMarkupCommand = new RelayCommand(ApplyMarkup, () => !IsBusy && StepIndex == 2 && EditableRows.Count > 0);
            CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false), () => !IsBusy);
            Status = "Scegli un file Excel fornitore.";
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action<bool> RequestClose;

        public ObservableCollection<string> ColumnKeyOptions { get; }
        public ObservableCollection<int> RoundToOptions { get; } = new ObservableCollection<int>(new[] { 10, 50, 100 });
        public ObservableCollection<SupplierExcelColumn> Columns { get; } = new ObservableCollection<SupplierExcelColumn>();
        public ObservableCollection<SupplierImportEditableRow> EditableRows { get; } = new ObservableCollection<SupplierImportEditableRow>();
        public ObservableCollection<SupplierImportWarning> Warnings { get; } = new ObservableCollection<SupplierImportWarning>();
        public ObservableCollection<SupplierImportError> Errors { get; } = new ObservableCollection<SupplierImportError>();

        public ICommand BrowseCommand { get; }
        public ICommand AnalyzeCommand { get; }
        public ICommand BackCommand { get; }
        public ICommand NextCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand ApplyMarkupCommand { get; }
        public ICommand CancelCommand { get; }

        public int StepIndex
        {
            get { return _stepIndex; }
            set
            {
                if (_stepIndex == value) return;
                _stepIndex = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsStep1));
                OnPropertyChanged(nameof(IsStep2));
                OnPropertyChanged(nameof(IsStep3));
                RaiseCanExecuteChanged();
            }
        }

        public bool IsStep1 { get { return StepIndex == 0; } }
        public bool IsStep2 { get { return StepIndex == 1; } }
        public bool IsStep3 { get { return StepIndex == 2; } }

        public string SelectedPath
        {
            get { return _selectedPath; }
            set
            {
                _selectedPath = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedFileName));
                RaiseCanExecuteChanged();
            }
        }

        public string SelectedFileName
        {
            get { return string.IsNullOrWhiteSpace(SelectedPath) ? "" : Path.GetFileName(SelectedPath); }
        }

        public string Status
        {
            get { return _status; }
            set { _status = value ?? string.Empty; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get { return _isBusy; }
            set
            {
                if (_isBusy == value) return;
                _isBusy = value;
                OnPropertyChanged();
                RaiseCanExecuteChanged();
            }
        }

        public SupplierImportAnalysis Analysis
        {
            get { return _analysis; }
            private set
            {
                _analysis = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NewProductsCount));
                OnPropertyChanged(nameof(UpdatedProductsCount));
                OnPropertyChanged(nameof(WarningsCount));
                OnPropertyChanged(nameof(ErrorsCount));
                OnPropertyChanged(nameof(SelectedSheetName));
                OnPropertyChanged(nameof(HeaderSummary));
                OnPropertyChanged(nameof(RowSummary));
                OnPropertyChanged(nameof(IssueSummary));
                OnPropertyChanged(nameof(MissingNewRetailPriceCount));
                OnPropertyChanged(nameof(MissingBarcodeCount));
                OnPropertyChanged(nameof(MissingNewIdentityCount));
                OnPropertyChanged(nameof(InvalidNumberCount));
                OnPropertyChanged(nameof(SkippedRowsCount));
                OnPropertyChanged(nameof(CanProceedToStep3));
                OnPropertyChanged(nameof(CanApply));
                OnPropertyChanged(nameof(HasRetailPriceWarning));
                OnPropertyChanged(nameof(HasBarcodeWarning));
                OnPropertyChanged(nameof(HasIdentityWarning));
                OnPropertyChanged(nameof(HasInvalidNumberWarning));
                RaiseCanExecuteChanged();
            }
        }

        public int NewProductsCount { get { return Analysis == null ? 0 : Analysis.NewProducts.Count; } }
        public int UpdatedProductsCount { get { return Analysis == null ? 0 : Analysis.UpdatedProducts.Count; } }
        public int WarningsCount { get { return Analysis == null ? 0 : Analysis.Warnings.Count; } }
        public int ErrorsCount { get { return Analysis == null ? 0 : Analysis.Errors.Count; } }
        public string SelectedSheetName { get { return Analysis == null ? string.Empty : Analysis.SheetName; } }
        public string HeaderSummary
        {
            get
            {
                if (Analysis == null) return string.Empty;
                return Analysis.HasHeader
                    ? "Header rilevato alla riga " + Analysis.HeaderRowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "Header generato: file senza intestazione rilevata";
            }
        }
        public string RowSummary
        {
            get
            {
                if (Analysis == null) return string.Empty;
                return "Righe dati: " + Analysis.SourceRowCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " | Metadata saltati: " + Analysis.SkippedMetadataRows.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " | Righe totale filtrate: " + Analysis.DroppedSummaryRows.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        public string IssueSummary
        {
            get
            {
                if (Analysis == null) return string.Empty;
                return "Warning: " + WarningsCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " | Errori: " + ErrorsCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        public int MissingNewRetailPriceCount
        {
            get { return EditableRows.Count(row => row != null && !row.IsSkipped && !row.Exists && string.IsNullOrWhiteSpace(row.RetailPrice)); }
        }
        public int MissingBarcodeCount
        {
            get { return EditableRows.Count(row => row != null && !row.IsSkipped && string.IsNullOrWhiteSpace(row.Barcode)); }
        }
        public int MissingNewIdentityCount
        {
            get
            {
                return EditableRows.Count(row =>
                    row != null &&
                    !row.IsSkipped &&
                    !row.Exists &&
                    string.IsNullOrWhiteSpace(row.ProductName) &&
                    string.IsNullOrWhiteSpace(row.ItemNumber));
            }
        }
        public int SkippedRowsCount
        {
            get { return EditableRows.Count(row => row != null && row.IsSkipped); }
        }
        public int InvalidNumberCount
        {
            get
            {
                return EditableRows.Count(row =>
                    row != null &&
                    !row.IsSkipped &&
                    (IsInvalidNonNegativeNumber(row.PurchasePrice) ||
                        IsInvalidNonNegativeNumber(row.RetailPrice) ||
                        IsInvalidNonNegativeNumber(row.Quantity)));
            }
        }
        public bool CanProceedToStep3
        {
            get
            {
                return Analysis != null &&
                    Analysis.Columns.Any(c =>
                        c != null &&
                        c.IsEnabled &&
                        string.Equals(c.CanonicalKey, AndroidImportKeys.Barcode, StringComparison.Ordinal) &&
                        !string.Equals(c.HeaderSource, "generated", StringComparison.OrdinalIgnoreCase));
            }
        }
        public bool CanApply
        {
            get
            {
                return Analysis != null &&
                    Analysis.CanApply &&
                    EditableRows.Any(row => row != null && !row.IsSkipped) &&
                    MissingBarcodeCount == 0 &&
                    MissingNewIdentityCount == 0 &&
                    MissingNewRetailPriceCount == 0 &&
                    InvalidNumberCount == 0;
            }
        }
        public bool HasRetailPriceWarning
        {
            get
            {
                return Analysis != null &&
                    EditableRows.Any(row => row != null && !row.IsSkipped && row.RetailPriceMissingButPurchasePresent);
            }
        }
        public bool HasBarcodeWarning
        {
            get { return MissingBarcodeCount > 0; }
        }
        public bool HasIdentityWarning
        {
            get { return MissingNewIdentityCount > 0; }
        }
        public bool HasInvalidNumberWarning
        {
            get { return InvalidNumberCount > 0; }
        }

        public string MarkupPercent
        {
            get { return _markupPercent; }
            set { _markupPercent = value ?? string.Empty; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public int RoundTo
        {
            get { return _roundTo; }
            set { _roundTo = value <= 0 ? 1 : value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public bool ApplyOnlyEmptyRetailPrice
        {
            get { return _applyOnlyEmptyRetailPrice; }
            set { _applyOnlyEmptyRetailPrice = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        private void Browse()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Scegli Excel fornitore",
                Filter = "Excel (*.xls;*.xlsx)|*.xls;*.xlsx",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dlg.ShowDialog() == true)
            {
                SelectedPath = dlg.FileName;
                ClearAnalysis();
                Status = "File selezionato: " + SelectedFileName;
            }
        }

        private async Task AnalyzeAsync()
        {
            if (!File.Exists(SelectedPath))
            {
                Status = "Scegli prima un file .xls o .xlsx.";
                return;
            }

            IsBusy = true;
            Status = "Analisi Excel fornitore in corso...";
            try
            {
                var overrides = Columns.Count == 0
                    ? null
                    : Columns.ToDictionary(c => c.ColumnIndex, c => c.IsEnabled ? (c.CanonicalKey ?? string.Empty) : string.Empty);
                var result = await _service.AnalyzeAsync(SelectedPath, overrides).ConfigureAwait(true);
                ApplyAnalysis(result);
                StepIndex = 1;
                Status = "Analisi completata. Foglio: " + (SelectedSheetName.Length == 0 ? "n/d" : SelectedSheetName) + ". Verifica colonne, warning ed errori.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Supplier Excel analyze failed");
                Status = "Errore analisi: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ApplyAsync()
        {
            if (Analysis == null || !Analysis.CanApply)
            {
                Status = "Correggi gli errori prima di applicare.";
                return;
            }
            var rowsToApply = EditableRows.Where(row => row != null && !row.IsSkipped).ToList();
            if (rowsToApply.Count == 0)
            {
                Status = "Nessuna riga valida da applicare: correggi una riga o rimuovi Skip.";
                return;
            }
            if (rowsToApply.Any(row => string.IsNullOrWhiteSpace(row.Barcode)))
            {
                Status = "Righe senza barcode: correggi barcode o seleziona Skip prima di applicare.";
                return;
            }
            if (rowsToApply.Any(row => !row.Exists && string.IsNullOrWhiteSpace(row.ProductName) && string.IsNullOrWhiteSpace(row.ItemNumber)))
            {
                Status = "Nuovi prodotti senza productName o itemNumber: compila un'identita o seleziona Skip.";
                return;
            }
            if (rowsToApply.Any(row => !row.Exists && string.IsNullOrWhiteSpace(row.RetailPrice)))
            {
                Status = "Nuovi prodotti senza retailPrice: compila il prezzo vendita prima di applicare.";
                return;
            }
            if (rowsToApply.Any(row =>
                IsInvalidNonNegativeNumber(row.PurchasePrice) ||
                IsInvalidNonNegativeNumber(row.RetailPrice) ||
                IsInvalidNonNegativeNumber(row.Quantity)))
            {
                Status = "Prezzi o quantita non validi: correggi i valori numerici prima di applicare.";
                return;
            }
            var skippedRows = EditableRows.Count(row => row != null && row.IsSkipped);

            IsBusy = true;
            Status = "Applicazione import fornitore in corso...";
            try
            {
                var apply = await _service.ApplyAsync(rowsToApply, false, Analysis.Warnings.Count, skippedRows).ConfigureAwait(true);
                Status = apply.Summary;
                ModernMessageDialog.Show(Application.Current?.MainWindow, "Import Excel fornitore", apply.Summary);
                RequestClose?.Invoke(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Supplier Excel apply failed");
                Status = ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void Back()
        {
            if (StepIndex > 0) StepIndex -= 1;
        }

        private async Task NextAsync()
        {
            if (StepIndex != 1) return;
            await AnalyzeAsync().ConfigureAwait(true);
            if (Analysis != null && CanProceedToStep3) StepIndex = 2;
            else Status = "Mappa una colonna barcode reale prima di proseguire.";
        }

        private void ApplyMarkup()
        {
            double markup;
            if (!double.TryParse((MarkupPercent ?? string.Empty).Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out markup))
            {
                Status = "Markup percent non valido.";
                return;
            }

            var changed = SupplierRetailPriceHelper.ApplyMarkupToRetailPriceRows(
                EditableRows.Where(row => row != null && !row.IsSkipped),
                markup,
                RoundTo,
                ApplyOnlyEmptyRetailPrice);
            RefreshEditableRows();
            Status = "Prezzo vendita calcolato per " + changed.ToString(System.Globalization.CultureInfo.InvariantCulture) + " righe.";
        }

        private void ClearAnalysis()
        {
            Analysis = null;
            Columns.Clear();
            EditableRows.Clear();
            Warnings.Clear();
            Errors.Clear();
            StepIndex = 0;
        }

        private void ApplyAnalysis(SupplierImportAnalysis analysis)
        {
            Analysis = analysis;
            Columns.Clear();
            EditableRows.Clear();
            Warnings.Clear();
            Errors.Clear();

            foreach (var col in analysis.Columns)
            {
                col.PropertyChanged += Column_PropertyChanged;
                Columns.Add(col);
            }
            foreach (var row in analysis.EditableRows)
            {
                row.PropertyChanged += EditableRow_PropertyChanged;
                EditableRows.Add(row);
            }
            foreach (var warning in analysis.Warnings)
                Warnings.Add(warning);
            foreach (var error in analysis.Errors)
                Errors.Add(error);
            OnPropertyChanged(nameof(SelectedSheetName));
            OnPropertyChanged(nameof(HeaderSummary));
            OnPropertyChanged(nameof(RowSummary));
            OnPropertyChanged(nameof(IssueSummary));
            OnPropertyChanged(nameof(MissingNewRetailPriceCount));
            OnPropertyChanged(nameof(MissingBarcodeCount));
            OnPropertyChanged(nameof(MissingNewIdentityCount));
            OnPropertyChanged(nameof(InvalidNumberCount));
            OnPropertyChanged(nameof(SkippedRowsCount));
            OnPropertyChanged(nameof(CanProceedToStep3));
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(HasBarcodeWarning));
            OnPropertyChanged(nameof(HasIdentityWarning));
            OnPropertyChanged(nameof(HasInvalidNumberWarning));
            OnPropertyChanged(nameof(HasRetailPriceWarning));
            RaiseCanExecuteChanged();
        }

        private void Column_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null ||
                e.PropertyName == nameof(SupplierExcelColumn.CanonicalKey) ||
                e.PropertyName == nameof(SupplierExcelColumn.IsEnabled))
            {
                OnPropertyChanged(nameof(CanProceedToStep3));
                RaiseCanExecuteChanged();
            }
        }

        private void EditableRow_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null ||
                e.PropertyName == nameof(SupplierImportEditableRow.RetailPrice) ||
                e.PropertyName == nameof(SupplierImportEditableRow.Barcode) ||
                e.PropertyName == nameof(SupplierImportEditableRow.ItemNumber) ||
                e.PropertyName == nameof(SupplierImportEditableRow.ProductName) ||
                e.PropertyName == nameof(SupplierImportEditableRow.PurchasePrice) ||
                e.PropertyName == nameof(SupplierImportEditableRow.Quantity) ||
                e.PropertyName == nameof(SupplierImportEditableRow.IsSkipped))
            {
                OnPropertyChanged(nameof(MissingNewRetailPriceCount));
                OnPropertyChanged(nameof(MissingBarcodeCount));
                OnPropertyChanged(nameof(MissingNewIdentityCount));
                OnPropertyChanged(nameof(InvalidNumberCount));
                OnPropertyChanged(nameof(SkippedRowsCount));
                OnPropertyChanged(nameof(CanApply));
                OnPropertyChanged(nameof(HasBarcodeWarning));
                OnPropertyChanged(nameof(HasIdentityWarning));
                OnPropertyChanged(nameof(HasInvalidNumberWarning));
                OnPropertyChanged(nameof(HasRetailPriceWarning));
                RaiseCanExecuteChanged();
            }
        }

        private void RefreshEditableRows()
        {
            var rows = EditableRows.ToList();
            EditableRows.Clear();
            foreach (var row in rows)
                EditableRows.Add(row);
            OnPropertyChanged(nameof(MissingNewRetailPriceCount));
            OnPropertyChanged(nameof(MissingBarcodeCount));
            OnPropertyChanged(nameof(MissingNewIdentityCount));
            OnPropertyChanged(nameof(InvalidNumberCount));
            OnPropertyChanged(nameof(SkippedRowsCount));
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(HasBarcodeWarning));
            OnPropertyChanged(nameof(HasIdentityWarning));
            OnPropertyChanged(nameof(HasInvalidNumberWarning));
            OnPropertyChanged(nameof(HasRetailPriceWarning));
            RaiseCanExecuteChanged();
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private static bool IsInvalidNonNegativeNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var number = SupplierImportAnalyzer.ParseNumber(value);
            return !number.HasValue || number.Value < 0;
        }

        private void RaiseCanExecuteChanged()
        {
            (BrowseCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AnalyzeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (BackCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (NextCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ApplyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ApplyMarkupCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private sealed class RelayCommand : ICommand
        {
            private readonly Action _execute;
            private readonly Func<bool> _canExecute;

            public RelayCommand(Action execute, Func<bool> canExecute = null)
            {
                _execute = execute;
                _canExecute = canExecute;
            }

            public bool CanExecute(object parameter)
            {
                return _canExecute == null || _canExecute();
            }

            public void Execute(object parameter)
            {
                _execute();
            }

            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged()
            {
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private sealed class AsyncRelayCommand : ICommand
        {
            private readonly Func<Task> _execute;
            private readonly Func<bool> _canExecute;

            public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute = null)
            {
                _execute = execute;
                _canExecute = canExecute;
            }

            public bool CanExecute(object parameter)
            {
                return _canExecute == null || _canExecute();
            }

            public async void Execute(object parameter)
            {
                await _execute().ConfigureAwait(true);
            }

            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged()
            {
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
