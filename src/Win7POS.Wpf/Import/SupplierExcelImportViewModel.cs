using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using Win7POS.Core.Import;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Import
{
    public sealed class SupplierExcelImportViewModel : INotifyPropertyChanged
    {
        private readonly SupplierExcelImportWorkflowService _service;
        private readonly ISupplierExcelFileDialogService _fileDialogService;
        private readonly ISupplierExcelCompletionDialogService _completionDialogService;
        private readonly FileLogger _logger = new FileLogger("SupplierExcelImportViewModel");
        private int _stepIndex;
        private string _selectedPath = string.Empty;
        private string _status = string.Empty;
        private string _markupPercent = "30";
        private int _roundTo = 100;
        private bool _applyOnlyEmptyRetailPrice = true;
        private bool _isBusy;
        private bool _isSyncPreviewStale;
        private string _syncSearchText = string.Empty;
        private SupplierImportAnalysis _analysis;
        private SupplierImportSyncPreview _syncPreview;

        public SupplierExcelImportViewModel(
            SupplierExcelImportWorkflowService service = null,
            ISupplierExcelFileDialogService fileDialogService = null,
            ISupplierExcelCompletionDialogService completionDialogService = null)
        {
            _service = service ?? new SupplierExcelImportWorkflowService(() => false);
            _fileDialogService = fileDialogService ?? new SupplierExcelFileDialogService();
            _completionDialogService = completionDialogService ?? new SupplierExcelCompletionDialogService();
            InitializeSyncViews();
            ColumnKeyOptions = new ObservableCollection<string>(new[] { string.Empty }.Concat(AndroidImportKeys.AllKeys));
            BrowseCommand = new RelayCommand(Browse, () => !IsBusy && StepIndex == 0);
            AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !IsBusy && File.Exists(SelectedPath));
            BackCommand = new RelayCommand(Back, () => !IsBusy && StepIndex > 0);
            NextCommand = new AsyncRelayCommand(NextAsync, () => !IsBusy && StepIndex == 1 && CanProceedToStep3);
            SyncPreviewCommand = new AsyncRelayCommand(BuildSyncPreviewAsync, () => !IsBusy && StepIndex == 2 && EditableRows.Count > 0);
            ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => !IsBusy && StepIndex == 3 && CanApply);
            ApplyMarkupCommand = new RelayCommand(ApplyMarkup, () => !IsBusy && StepIndex == 2 && EditableRows.Count > 0);
            CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false), () => !IsBusy);
            Status = PosLocalization.T("supplierExcelImport.statusChooseFile");
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action<bool> RequestClose;
        public SupplierExcelApplyUiResult LastApplyResult { get; private set; }

        public ObservableCollection<string> ColumnKeyOptions { get; }
        public ObservableCollection<int> RoundToOptions { get; } = new ObservableCollection<int>(new[] { 10, 50, 100 });
        public ObservableCollection<SupplierExcelColumn> Columns { get; } = new ObservableCollection<SupplierExcelColumn>();
        public ObservableCollection<SupplierImportEditableRow> EditableRows { get; } = new ObservableCollection<SupplierImportEditableRow>();
        public ObservableCollection<SupplierImportWarning> Warnings { get; } = new ObservableCollection<SupplierImportWarning>();
        public ObservableCollection<SupplierImportError> Errors { get; } = new ObservableCollection<SupplierImportError>();
        public ObservableCollection<SupplierImportProductRow> SyncNewProducts { get; } = new ObservableCollection<SupplierImportProductRow>();
        public ObservableCollection<SupplierImportSyncRow> SyncUpdatedProducts { get; } = new ObservableCollection<SupplierImportSyncRow>();
        public ObservableCollection<SupplierImportSyncRow> SyncNoChangeRows { get; } = new ObservableCollection<SupplierImportSyncRow>();
        public ObservableCollection<SupplierImportSyncSkippedRow> SyncSkippedRows { get; } = new ObservableCollection<SupplierImportSyncSkippedRow>();
        public ObservableCollection<SupplierImportWarning> SyncWarnings { get; } = new ObservableCollection<SupplierImportWarning>();
        public ObservableCollection<SupplierImportError> SyncErrors { get; } = new ObservableCollection<SupplierImportError>();
        public ICollectionView SyncNewProductsView { get; private set; }
        public ICollectionView SyncUpdatedProductsView { get; private set; }
        public ICollectionView SyncNoChangeRowsView { get; private set; }
        public ICollectionView SyncSkippedRowsView { get; private set; }
        public ICollectionView SyncWarningsView { get; private set; }
        public ICollectionView SyncErrorsView { get; private set; }

        public ICommand BrowseCommand { get; }
        public ICommand AnalyzeCommand { get; }
        public ICommand BackCommand { get; }
        public ICommand NextCommand { get; }
        public ICommand SyncPreviewCommand { get; }
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
                OnPropertyChanged(nameof(IsStep4));
                RaiseCanExecuteChanged();
            }
        }

        public bool IsStep1 { get { return StepIndex == 0; } }
        public bool IsStep2 { get { return StepIndex == 1; } }
        public bool IsStep3 { get { return StepIndex == 2; } }
        public bool IsStep4 { get { return StepIndex == 3; } }

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

        public SupplierImportSyncPreview SyncPreview
        {
            get { return _syncPreview; }
            private set
            {
                _syncPreview = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SyncNewProductsCount));
                OnPropertyChanged(nameof(SyncUpdatedProductsCount));
                OnPropertyChanged(nameof(SyncNoChangeRowsCount));
                OnPropertyChanged(nameof(SyncSkippedRowsCount));
                OnPropertyChanged(nameof(SyncWarningsCount));
                OnPropertyChanged(nameof(SyncErrorsCount));
                OnPropertyChanged(nameof(SyncTotalRowsCount));
                OnPropertyChanged(nameof(SyncCanApply));
                OnPropertyChanged(nameof(CanApply));
                RaiseCanExecuteChanged();
            }
        }

        public int NewProductsCount { get { return Analysis == null ? 0 : Analysis.NewProducts.Count; } }
        public int UpdatedProductsCount { get { return Analysis == null ? 0 : Analysis.UpdatedProducts.Count; } }
        public int WarningsCount { get { return Analysis == null ? 0 : Analysis.Warnings.Count; } }
        public int ErrorsCount { get { return Analysis == null ? 0 : Analysis.Errors.Count; } }
        public int SyncNewProductsCount { get { return SyncPreview == null ? 0 : SyncPreview.Summary.NewProducts; } }
        public int SyncUpdatedProductsCount { get { return SyncPreview == null ? 0 : SyncPreview.Summary.UpdatedProducts; } }
        public int SyncNoChangeRowsCount { get { return SyncPreview == null ? 0 : SyncPreview.Summary.NoChangeRows; } }
        public int SyncSkippedRowsCount { get { return SyncPreview == null ? 0 : SyncPreview.Summary.SkippedRows; } }
        public int SyncWarningsCount { get { return SyncPreview == null ? 0 : SyncPreview.Summary.WarningCount; } }
        public int SyncErrorsCount { get { return SyncPreview == null ? 0 : SyncPreview.Summary.ErrorCount; } }
        public int SyncTotalRowsCount { get { return SyncPreview == null ? 0 : SyncPreview.Summary.TotalRows; } }
        public string SyncSearchText
        {
            get { return _syncSearchText; }
            set
            {
                var next = value ?? string.Empty;
                if (_syncSearchText == next) return;
                _syncSearchText = next;
                OnPropertyChanged();
                RefreshSyncFilters();
            }
        }
        public bool SyncCanApply { get { return SyncPreview != null && SyncPreview.CanApply && !IsSyncPreviewStale; } }
        public bool IsSyncPreviewStale
        {
            get { return _isSyncPreviewStale; }
            private set
            {
                if (_isSyncPreviewStale == value) return;
                _isSyncPreviewStale = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SyncCanApply));
                OnPropertyChanged(nameof(CanApply));
                RaiseCanExecuteChanged();
            }
        }
        public string SelectedSheetName { get { return Analysis == null ? string.Empty : Analysis.SheetName; } }
        public string HeaderSummary
        {
            get
            {
                if (Analysis == null) return string.Empty;
                return Analysis.HasHeader
                    ? PosLocalization.F("supplierExcelImport.headerDetected", Analysis.HeaderRowNumber)
                    : PosLocalization.T("supplierExcelImport.headerGenerated");
            }
        }
        public string RowSummary
        {
            get
            {
                if (Analysis == null) return string.Empty;
                return PosLocalization.F(
                    "supplierExcelImport.rowSummary",
                    Analysis.SourceRowCount,
                    Analysis.SkippedMetadataRows,
                    Analysis.DroppedSummaryRows);
            }
        }
        public string IssueSummary
        {
            get
            {
                if (Analysis == null) return string.Empty;
                return PosLocalization.F("supplierExcelImport.issueSummary", WarningsCount, ErrorsCount);
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
                    string.IsNullOrWhiteSpace(row.SecondProductName) &&
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
                return StepIndex == 3 && SyncCanApply;
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
            var fileName = _fileDialogService.SelectSupplierExcelFile();
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                SelectedPath = fileName;
                ClearAnalysis();
                Status = PosLocalization.F("supplierExcelImport.statusFileSelected", SelectedFileName);
            }
        }

        private async Task AnalyzeAsync()
        {
            if (!File.Exists(SelectedPath))
            {
                Status = PosLocalization.T("supplierExcelImport.statusSelectFileFirst");
                return;
            }

            IsBusy = true;
            Status = PosLocalization.T("supplierExcelImport.statusAnalyzing");
            try
            {
                var overrides = Columns.Count == 0
                    ? null
                    : Columns.ToDictionary(c => c.ColumnIndex, c => c.IsEnabled ? (c.CanonicalKey ?? string.Empty) : string.Empty);
                var result = await _service.AnalyzeAsync(SelectedPath, overrides).ConfigureAwait(true);
                ApplyAnalysis(result);
                StepIndex = 1;
                Status = PosLocalization.F(
                    "supplierExcelImport.statusAnalysisComplete",
                    SelectedSheetName.Length == 0
                        ? PosLocalization.T("common.unavailableShort")
                        : SelectedSheetName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Supplier Excel analyze failed");
                Status = PosLocalization.F("supplierExcelImport.statusAnalyzeError", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ApplyAsync()
        {
            if (SyncPreview == null)
            {
                Status = PosLocalization.T("supplierExcelImport.recalculateBeforeApply");
                return;
            }
            if (IsSyncPreviewStale)
            {
                Status = PosLocalization.T("supplierExcelImport.syncPreviewStale");
                return;
            }
            if (!SyncPreview.CanApply)
            {
                Status = PosLocalization.T("supplierExcelImport.previewHasErrors");
                return;
            }

            IsBusy = true;
            Status = PosLocalization.T("supplierExcelImport.statusApplying");
            try
            {
                var apply = await _service.ApplyAsync(SyncPreview, false, SelectedFileName).ConfigureAwait(true);
                LastApplyResult = apply;
                Status = apply.Summary;
                _completionDialogService.ShowCompletion(PosLocalization.T("supplierExcelImport.title"), apply.Summary);
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
            else Status = PosLocalization.T("supplierExcelImport.mapBarcodeBeforeContinue");
        }

        private async Task BuildSyncPreviewAsync()
        {
            IsBusy = true;
            Status = PosLocalization.T("supplierExcelImport.statusCalculatingSync");
            try
            {
                var preview = await _service.BuildSyncPreviewAsync(EditableRows.ToList()).ConfigureAwait(true);
                ApplySyncPreview(preview);
                IsSyncPreviewStale = false;
                StepIndex = 3;
                Status = preview.CanApply
                    ? PosLocalization.T("supplierExcelImport.statusSyncReady")
                    : PosLocalization.T("supplierExcelImport.statusSyncReadyWithErrors");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Supplier Excel sync preview failed");
                Status = PosLocalization.F("supplierExcelImport.statusSyncError", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void ApplyMarkup()
        {
            double markup;
            if (!double.TryParse((MarkupPercent ?? string.Empty).Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out markup))
            {
                Status = PosLocalization.T("supplierExcelImport.invalidMarkup");
                return;
            }

            var changed = SupplierRetailPriceHelper.ApplyMarkupToRetailPriceRows(
                EditableRows.Where(row => row != null && !row.IsSkipped),
                markup,
                RoundTo,
                ApplyOnlyEmptyRetailPrice);
            InvalidateSyncPreview();
            RefreshEditableRows();
            Status = PosLocalization.F("supplierExcelImport.markupApplied", changed);
        }

        private void ClearAnalysis()
        {
            Analysis = null;
            Columns.Clear();
            EditableRows.Clear();
            Warnings.Clear();
            Errors.Clear();
            ClearSyncPreview();
            StepIndex = 0;
        }

        private void ApplyAnalysis(SupplierImportAnalysis analysis)
        {
            Analysis = analysis;
            Columns.Clear();
            EditableRows.Clear();
            Warnings.Clear();
            Errors.Clear();
            ClearSyncPreview();

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

        private void ApplySyncPreview(SupplierImportSyncPreview preview)
        {
            SyncPreview = preview;
            SyncNewProducts.Clear();
            SyncUpdatedProducts.Clear();
            SyncNoChangeRows.Clear();
            SyncSkippedRows.Clear();
            SyncWarnings.Clear();
            SyncErrors.Clear();
            if (preview == null) return;
            foreach (var row in preview.NewProducts)
                SyncNewProducts.Add(row);
            foreach (var row in preview.UpdatedProducts)
                SyncUpdatedProducts.Add(row);
            foreach (var row in preview.NoChangeRows)
                SyncNoChangeRows.Add(row);
            foreach (var row in preview.SkippedRows)
                SyncSkippedRows.Add(row);
            foreach (var warning in preview.Warnings)
                SyncWarnings.Add(warning);
            foreach (var error in preview.Errors)
                SyncErrors.Add(error);
            RefreshSyncFilters();
            OnPropertyChanged(nameof(SyncNewProductsCount));
            OnPropertyChanged(nameof(SyncUpdatedProductsCount));
            OnPropertyChanged(nameof(SyncNoChangeRowsCount));
            OnPropertyChanged(nameof(SyncSkippedRowsCount));
            OnPropertyChanged(nameof(SyncWarningsCount));
            OnPropertyChanged(nameof(SyncErrorsCount));
            OnPropertyChanged(nameof(SyncTotalRowsCount));
            OnPropertyChanged(nameof(SyncCanApply));
            OnPropertyChanged(nameof(CanApply));
            RaiseCanExecuteChanged();
        }

        private void ClearSyncPreview()
        {
            SyncPreview = null;
            IsSyncPreviewStale = false;
            SyncNewProducts.Clear();
            SyncUpdatedProducts.Clear();
            SyncNoChangeRows.Clear();
            SyncSkippedRows.Clear();
            SyncWarnings.Clear();
            SyncErrors.Clear();
            SyncSearchText = string.Empty;
            RefreshSyncFilters();
        }

        private void InvalidateSyncPreview()
        {
            if (SyncPreview == null) return;
            IsSyncPreviewStale = true;
            Status = PosLocalization.T("supplierExcelImport.syncPreviewStale");
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
                e.PropertyName == nameof(SupplierImportEditableRow.SecondProductName) ||
                e.PropertyName == nameof(SupplierImportEditableRow.PurchasePrice) ||
                e.PropertyName == nameof(SupplierImportEditableRow.Quantity) ||
                e.PropertyName == nameof(SupplierImportEditableRow.Supplier) ||
                e.PropertyName == nameof(SupplierImportEditableRow.Category) ||
                e.PropertyName == nameof(SupplierImportEditableRow.IsSkipped))
            {
                InvalidateSyncPreview();
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

        private void InitializeSyncViews()
        {
            SyncNewProductsView = CollectionViewSource.GetDefaultView(SyncNewProducts);
            SyncUpdatedProductsView = CollectionViewSource.GetDefaultView(SyncUpdatedProducts);
            SyncNoChangeRowsView = CollectionViewSource.GetDefaultView(SyncNoChangeRows);
            SyncSkippedRowsView = CollectionViewSource.GetDefaultView(SyncSkippedRows);
            SyncWarningsView = CollectionViewSource.GetDefaultView(SyncWarnings);
            SyncErrorsView = CollectionViewSource.GetDefaultView(SyncErrors);

            SyncNewProductsView.Filter = item => SyncProductMatches(item as SupplierImportProductRow);
            SyncUpdatedProductsView.Filter = item => SyncUpdateMatches(item as SupplierImportSyncRow);
            SyncNoChangeRowsView.Filter = item => SyncUpdateMatches(item as SupplierImportSyncRow);
            SyncSkippedRowsView.Filter = item => SyncSkippedMatches(item as SupplierImportSyncSkippedRow);
            SyncWarningsView.Filter = item => SyncWarningMatches(item as SupplierImportWarning);
            SyncErrorsView.Filter = item => SyncErrorMatches(item as SupplierImportError);
        }

        private void RefreshSyncFilters()
        {
            SyncNewProductsView?.Refresh();
            SyncUpdatedProductsView?.Refresh();
            SyncNoChangeRowsView?.Refresh();
            SyncSkippedRowsView?.Refresh();
            SyncWarningsView?.Refresh();
            SyncErrorsView?.Refresh();
        }

        private bool SyncProductMatches(SupplierImportProductRow row)
        {
            if (row == null) return false;
            var query = SyncSearchText.Trim();
            if (query.Length == 0) return true;
            return TextMatches(row.RowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), query) ||
                TextMatches(row.Barcode, query) ||
                TextMatches(row.ItemNumber, query) ||
                TextMatches(row.ProductName, query) ||
                TextMatches(row.SecondProductName, query) ||
                TextMatches(row.PurchasePrice, query) ||
                TextMatches(row.RetailPrice, query) ||
                TextMatches(row.Quantity, query) ||
                TextMatches(row.Supplier, query) ||
                TextMatches(row.Category, query);
        }

        private bool SyncUpdateMatches(SupplierImportSyncRow row)
        {
            if (row == null) return false;
            var query = SyncSearchText.Trim();
            if (query.Length == 0) return true;
            return TextMatches(row.RowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), query) ||
                TextMatches(row.Barcode, query) ||
                TextMatches(row.DiffSummary, query) ||
                SyncProductMatches(row.Existing) ||
                SyncProductMatches(row.Updated);
        }

        private bool SyncSkippedMatches(SupplierImportSyncSkippedRow row)
        {
            if (row == null) return false;
            var query = SyncSearchText.Trim();
            if (query.Length == 0) return true;
            return TextMatches(row.RowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), query) ||
                TextMatches(row.Barcode, query) ||
                TextMatches(row.ItemNumber, query) ||
                TextMatches(row.ProductName, query);
        }

        private bool SyncWarningMatches(SupplierImportWarning warning)
        {
            if (warning == null) return false;
            var query = SyncSearchText.Trim();
            if (query.Length == 0) return true;
            return TextMatches(warning.Message, query) || RowsMatch(warning.Rows, query);
        }

        private bool SyncErrorMatches(SupplierImportError error)
        {
            if (error == null) return false;
            var query = SyncSearchText.Trim();
            if (query.Length == 0) return true;
            return TextMatches(error.Message, query) ||
                TextMatches(error.Barcode, query) ||
                TextMatches(error.RowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), query);
        }

        private static bool RowsMatch(IReadOnlyList<int> rows, string query)
        {
            if (rows == null) return false;
            foreach (var row in rows)
            {
                if (TextMatches(row.ToString(System.Globalization.CultureInfo.InvariantCulture), query))
                    return true;
            }
            return false;
        }

        private static bool TextMatches(string value, string query)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
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
            (SyncPreviewCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ApplyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ApplyMarkupCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        public async Task<SupplierExcelViewModelSmokeResult> RunSmokeAsync()
        {
            BrowseCommand.Execute(null);
            if (string.IsNullOrWhiteSpace(SelectedPath) || !File.Exists(SelectedPath))
                throw new InvalidOperationException("Supplier Excel smoke did not select a valid file.");

            await AnalyzeAsync().ConfigureAwait(true);
            if (Analysis == null || StepIndex != 1)
                throw new InvalidOperationException("Supplier Excel smoke did not reach Analyze results.");

            await NextAsync().ConfigureAwait(true);
            if (StepIndex != 2)
                throw new InvalidOperationException("Supplier Excel smoke did not reach Step 3.");

            await BuildSyncPreviewAsync().ConfigureAwait(true);
            if (SyncPreview == null || StepIndex != 3 || !CanApply)
                throw new InvalidOperationException("Supplier Excel smoke did not reach appliable Step 4.");

            await ApplyAsync().ConfigureAwait(true);
            if (LastApplyResult == null || !LastApplyResult.Success)
                throw new InvalidOperationException("Supplier Excel smoke did not apply successfully.");

            return new SupplierExcelViewModelSmokeResult
            {
                BackupPath = LastApplyResult.BackupPath,
                CatalogImportOutboxId = LastApplyResult.CatalogImportOutboxId,
                CatalogImportOutboxStatus = LastApplyResult.CatalogImportOutboxStatus,
                Inserted = LastApplyResult.Inserted,
                NoChange = LastApplyResult.NoChange,
                SelectedFileName = SelectedFileName,
                Status = Status,
                Updated = LastApplyResult.Updated
            };
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

    public interface ISupplierExcelFileDialogService
    {
        string SelectSupplierExcelFile();
    }

    public interface ISupplierExcelCompletionDialogService
    {
        void ShowCompletion(string title, string message);
    }

    public sealed class SupplierExcelCompletionDialogService : ISupplierExcelCompletionDialogService
    {
        public void ShowCompletion(string title, string message)
        {
            ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), title, message);
        }
    }

    public sealed class SupplierExcelViewModelSmokeResult
    {
        public string BackupPath { get; set; } = string.Empty;
        public long CatalogImportOutboxId { get; set; }
        public string CatalogImportOutboxStatus { get; set; } = string.Empty;
        public int Inserted { get; set; }
        public int NoChange { get; set; }
        public string SelectedFileName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int Updated { get; set; }
    }

    public sealed class SupplierExcelFileDialogService : ISupplierExcelFileDialogService
    {
        private readonly Func<Window> _ownerProvider;

        public SupplierExcelFileDialogService(Func<Window> ownerProvider = null)
        {
            _ownerProvider = ownerProvider;
        }

        public string SelectSupplierExcelFile()
        {
            var dlg = new OpenFileDialog
            {
                Title = PosLocalization.T("supplierExcelImport.filePickerTitle"),
                Filter = "Excel (*.xls;*.xlsx)|*.xls;*.xlsx",
                CheckFileExists = true,
                Multiselect = false
            };

            var owner = DialogOwnerHelper.GetSafeOwner(_ownerProvider == null ? null : _ownerProvider());
            if (owner == null)
                throw new InvalidOperationException(PosLocalization.T("supplierExcelImport.noActiveOwner"));

            owner.Activate();
            return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
        }
    }
}
