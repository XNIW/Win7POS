using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Receipt;
using Win7POS.Core.Util;
using Win7POS.Data.Repositories;
using Win7POS.Data.Online;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Products.Images;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Products
{
    public enum ProductEditMode { New, Edit, Duplicate }

    public sealed class ProductEditViewModel : INotifyPropertyChanged
    {
        private string _barcode = string.Empty;
        private string _productName = string.Empty;
        private string _priceText = "0";
        private string _purchasePriceText = "0";
        private string _stockText = "0";
        private string _articleCode = string.Empty;
        private string _name2 = string.Empty;
        private string _categoryText = string.Empty;
        private string _supplierText = string.Empty;
        private StockReasonOption _selectedStockReason;
        private CategoryListItem _selectedCategory;
        private SupplierListItem _selectedSupplier;
        private readonly ProductDetailsRow _imageProduct;
        private readonly ProductImageWorkflowService _imageWorkflow =
            new ProductImageWorkflowService();
        private bool _imageBusy;
        private bool _hasLocalImagePreview;
        private string _imageOperationStatus = string.Empty;

        public ProductEditMode Mode { get; }
        public long? ProductId { get; }
        public bool IsEditMode => Mode == ProductEditMode.Edit;
        public bool IsBarcodeReadOnly => false;

        public ObservableCollection<CategoryListItem> Categories { get; } = new ObservableCollection<CategoryListItem>();
        public ObservableCollection<SupplierListItem> Suppliers { get; } = new ObservableCollection<SupplierListItem>();
        public ObservableCollection<StockReasonOption> StockReasons { get; } =
            new ObservableCollection<StockReasonOption>();

        public string Title => Mode == ProductEditMode.Edit
            ? PosLocalization.T("products.editTitle")
            : Mode == ProductEditMode.Duplicate
                ? PosLocalization.T("products.duplicateTitle")
                : PosLocalization.T("products.newTitle");
        public string DialogTitle => Title;
        /// <summary>Nuovo/Duplica = Stock iniziale, Modifica = Stock.</summary>
        public string StockLabel => IsEditMode
            ? PosLocalization.T("products.stock")
            : PosLocalization.T("products.initialStock");

        private readonly ProductsWorkflowService _service;

        public bool ProductImagesPhaseAEnabled =>
            ProductImageFeatureFlags.IsPhaseAEnabled;
        public bool CanManageImages => ProductImagesPhaseAEnabled && IsEditMode;
        public bool ImageBusy
        {
            get => _imageBusy;
            private set
            {
                _imageBusy = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanUseImageCommands));
            }
        }
        public bool CanUseImageCommands => CanManageImages && !ImageBusy;
        public bool CanRemoveImage => CanUseImageCommands &&
            PosProductImageContractV1.IsCanonicalUuid(_imageProduct?.PrimaryImageVersionId);
        public string ImageChooseActionText =>
            _hasLocalImagePreview ||
            PosProductImageContractV1.IsCanonicalUuid(
                _imageProduct?.PrimaryImageVersionId)
                ? PosLocalization.T("productImage.replace")
                : PosLocalization.T("productImage.choose");
        public string ImageOperationStatus
        {
            get => _imageOperationStatus;
            private set
            {
                _imageOperationStatus = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowsImageOperationStatus));
            }
        }
        public bool ShowsImageOperationStatus =>
            !string.IsNullOrWhiteSpace(ImageOperationStatus);

        public ProductImageDisplayViewModel ImageDisplay { get; } =
            new ProductImageDisplayViewModel();

        public ProductEditViewModel(ProductEditMode mode, ProductDetailsRow source, ProductsWorkflowService service)
        {
            Mode = mode;
            ProductId = source?.Id;
            _imageProduct = source;
            _service = service ?? throw new ArgumentNullException(nameof(service));
            PopulateStockReasons();
            if (source != null)
            {
                _barcode = Mode == ProductEditMode.Duplicate ? string.Empty : (source?.Barcode ?? string.Empty);
                _productName = source.Name ?? string.Empty;
                _priceText = source.UnitPrice > 0 ? source.UnitPrice.ToString() : "0";
                _purchasePriceText = source.PurchasePrice.ToString();
                _stockText = source.StockQty.ToString();
                _articleCode = source.ArticleCode ?? string.Empty;
                _name2 = source.Name2 ?? string.Empty;
            }
            ConfirmCommand = new RelayCommand(_ => Confirm(), _ => IsValid);
            CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false), _ => true);
            ChooseImageCommand = new RelayCommand(
                _ => ChooseImage(),
                _ => CanUseImageCommands);
            RemoveImageCommand = new RelayCommand(
                _ => RemoveImage(),
                _ => CanRemoveImage);
            RetryImageCommand = new RelayCommand(
                _ => RetryImage(),
                _ => CanUseImageCommands);
        }

        private void Confirm()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(ConfirmOnDispatcher));
                return;
            }

            ConfirmOnDispatcher();
        }

        private async void ConfirmOnDispatcher()
        {
            if (!IsValid) return;
            var finalName = string.IsNullOrWhiteSpace(ProductName)
                ? PosLocalization.T("products.unnamedProduct")
                : ProductName.Trim();
            var finalPurchasePrice = PurchasePriceMinor > 0 ? PurchasePriceMinor : (int)(UnitPriceMinor / 2);
            if (Mode == ProductEditMode.Edit)
                finalPurchasePrice = PurchasePriceMinor;
            try
            {
                BuildCategorySelection(out var catId, out var catName);
                BuildSupplierSelection(out var supId, out var supName);

                if (Mode == ProductEditMode.New)
                    await _service.CreateProductAsync(Barcode, finalName, UnitPriceMinor, finalPurchasePrice, supId, supName, catId, catName, StockQtyInt, ArticleCode, Name2);
                else if (Mode == ProductEditMode.Duplicate)
                    await _service.DuplicateProductAsync(ProductId.Value, Barcode, finalName, UnitPriceMinor, finalPurchasePrice, supId, supName, catId, catName, StockQtyInt, ArticleCode, Name2);
                else
                    await _service.UpdateProductFullAsync(ProductId.Value, Barcode, finalName, UnitPriceMinor, finalPurchasePrice, supId, supName, catId, catName, StockQtyInt, ArticleCode, Name2, SelectedStockReason?.Code ?? "count_correction");
                RequestClose?.Invoke(true);
            }
            catch (Exception ex)
            {
                Win7POS.Wpf.Import.ModernMessageDialog.Show(
                    System.Windows.Application.Current?.MainWindow,
                    PosLocalization.T("products.saveErrorTitle"),
                    ex.Message);
            }
        }

        public void SetCategories(System.Collections.Generic.IReadOnlyList<CategoryListItem> items)
        {
            Categories.Clear();
            Categories.Add(new CategoryListItem { Id = 0, Name = PosLocalization.T("products.none") });
            foreach (var x in items ?? Enumerable.Empty<CategoryListItem>())
                Categories.Add(x);
        }

        public void SetSuppliers(System.Collections.Generic.IReadOnlyList<SupplierListItem> items)
        {
            Suppliers.Clear();
            Suppliers.Add(new SupplierListItem { Id = 0, Name = PosLocalization.T("products.none") });
            foreach (var x in items ?? Enumerable.Empty<SupplierListItem>())
                Suppliers.Add(x);
        }

        /// <summary>Imposta categoria/fornitore e campi da source (Edit/Duplicate). Chiamare dopo SetCategories e SetSuppliers.</summary>
        public void SetSelectionFromSource(ProductDetailsRow source)
        {
            if (source != null)
            {
                StockText = source.StockQty.ToString();
                SelectedCategory = Categories.FirstOrDefault(c => c.Id == (source.CategoryId ?? 0)) ?? Categories.FirstOrDefault(c => string.Equals(c.Name, source.CategoryName, StringComparison.OrdinalIgnoreCase)) ?? Categories.FirstOrDefault();
                SelectedSupplier = Suppliers.FirstOrDefault(s => s.Id == (source.SupplierId ?? 0)) ?? Suppliers.FirstOrDefault(s => string.Equals(s.Name, source.SupplierName, StringComparison.OrdinalIgnoreCase)) ?? Suppliers.FirstOrDefault();
            }
            else
            {
                SelectedCategory = Categories.FirstOrDefault();
                SelectedSupplier = Suppliers.FirstOrDefault();
            }
        }

        public string Barcode
        {
            get => _barcode;
            set
            {
                var candidate = value ?? string.Empty;
                _barcode = candidate.Length <= SalesReceiptContentPolicy.MaxSaleLineBarcodeCharacters
                    ? candidate.Trim()
                    : candidate;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsValid));
            }
        }

        public string ProductName
        {
            get => _productName;
            set { _productName = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(IsValid)); }
        }

        public string PriceText
        {
            get => _priceText;
            set { _priceText = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(IsValid)); }
        }

        public string PurchasePriceText
        {
            get => _purchasePriceText;
            set { _purchasePriceText = value ?? string.Empty; OnPropertyChanged(); }
        }

        public string StockText
        {
            get => _stockText;
            set { _stockText = value ?? string.Empty; OnPropertyChanged(); }
        }

        public string ArticleCode { get => _articleCode; set { _articleCode = value ?? string.Empty; OnPropertyChanged(); } }
        public string Name2 { get => _name2; set { _name2 = value ?? string.Empty; OnPropertyChanged(); } }

        public StockReasonOption SelectedStockReason
        {
            get => _selectedStockReason;
            set
            {
                _selectedStockReason = value;
                OnPropertyChanged();
            }
        }

        public CategoryListItem SelectedCategory
        {
            get => _selectedCategory;
            set
            {
                _selectedCategory = value;
                OnPropertyChanged();
                if (value != null)
                    CategoryText = value.Name ?? string.Empty;
            }
        }

        public SupplierListItem SelectedSupplier
        {
            get => _selectedSupplier;
            set
            {
                _selectedSupplier = value;
                OnPropertyChanged();
                if (value != null)
                    SupplierText = value.Name ?? string.Empty;
            }
        }

        public string CategoryText
        {
            get => _categoryText;
            set
            {
                _categoryText = value ?? string.Empty;
                if (_selectedCategory != null && !TextMatchesSelection(_categoryText, _selectedCategory.Name))
                {
                    _selectedCategory = null;
                    OnPropertyChanged(nameof(SelectedCategory));
                }
                OnPropertyChanged();
            }
        }

        public string SupplierText
        {
            get => _supplierText;
            set
            {
                _supplierText = value ?? string.Empty;
                if (_selectedSupplier != null && !TextMatchesSelection(_supplierText, _selectedSupplier.Name))
                {
                    _selectedSupplier = null;
                    OnPropertyChanged(nameof(SelectedSupplier));
                }
                OnPropertyChanged();
            }
        }

        public long UnitPriceMinor => MoneyClp.Parse(PriceText);
        public int PurchasePriceMinor => MoneyClp.Parse(PurchasePriceText);
        public int StockQtyInt
        {
            get => int.TryParse(StockText?.Trim() ?? "0", out var n) && n >= 0 ? n : 0;
        }

        public bool IsValid =>
            Barcode.Length > 0 &&
            UnitPriceMinor >= 0 &&
            SalesReceiptContentPolicy.IsValidBarcode(Barcode) &&
            SalesReceiptContentPolicy.IsValidProductName(ProductName);

        public ICommand ConfirmCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand ChooseImageCommand { get; }
        public ICommand RemoveImageCommand { get; }
        public ICommand RetryImageCommand { get; }

        public async Task InitializeImageAsync(CancellationToken cancellationToken)
        {
            if (!CanManageImages || _imageProduct == null)
            {
                ImageDisplay.SetNoImage();
                return;
            }
            await _imageWorkflow.LoadEditorImageAsync(
                _imageProduct,
                ImageDisplay,
                cancellationToken).ConfigureAwait(true);
            var latest = await _imageWorkflow.GetLatestOperationAsync(_imageProduct.Id)
                .ConfigureAwait(true);
            if (latest != null && latest.State != ProductImageOperationStates.Completed)
                ImageOperationStatus = StatusText(latest.State, latest.CompletionState);
        }

        private void ChooseImage()
        {
            _ = ChooseImageAsync();
        }

        private async Task ChooseImageAsync()
        {
            if (!CanUseImageCommands || _imageProduct == null) return;
            var picker = new OpenFileDialog
            {
                CheckFileExists = true,
                Multiselect = false,
                Filter = PosLocalization.T("productImage.fileFilter"),
                Title = PosLocalization.T("productImage.choose")
            };
            if (picker.ShowDialog(DialogOwnerHelper.GetSafeOwner()) != true) return;
            ImageBusy = true;
            ImageOperationStatus = PosLocalization.T("productImage.preprocessing");
            try
            {
                var progress = new Progress<string>(code =>
                    ImageOperationStatus = StatusText(code, null));
                var result = await _imageWorkflow.ChooseOrReplaceAsync(
                    _imageProduct,
                    picker.FileName,
                    progress,
                    CancellationToken.None).ConfigureAwait(true);
                var preview = await ProductImageWorkflowService.DecodeLocalPreviewAsync(
                    result.PreviewBytes,
                    CancellationToken.None).ConfigureAwait(true);
                ImageDisplay.SetLoaded(preview);
                _hasLocalImagePreview = true;
                OnPropertyChanged(nameof(ImageChooseActionText));
                ImageOperationStatus = StatusText(result.State, null);
            }
            catch (Exception error)
            {
                ImageOperationStatus = PosLocalization.T("productImage.corrupt");
                ModernMessageDialog.Show(
                    DialogOwnerHelper.GetSafeOwner(),
                    PosLocalization.T("productImage.choose"),
                    SafeImageError(error));
            }
            finally
            {
                ImageBusy = false;
            }
        }

        private void RemoveImage()
        {
            _ = RemoveImageAsync();
        }

        private async Task RemoveImageAsync()
        {
            if (!CanRemoveImage || _imageProduct == null) return;
            if (!ApplyConfirmDialog.ShowConfirm(
                DialogOwnerHelper.GetSafeOwner(),
                PosLocalization.T("productImage.remove"),
                PosLocalization.T("productImage.removeConfirmation")))
            {
                return;
            }
            ImageBusy = true;
            try
            {
                var result = await _imageWorkflow.RemoveAsync(
                    _imageProduct,
                    CancellationToken.None).ConfigureAwait(true);
                // The current image remains visible until catalog confirms removal.
                ImageOperationStatus = StatusText(result.State, null);
            }
            catch (Exception error)
            {
                ImageOperationStatus = PosLocalization.T("productImage.unavailable");
                ModernMessageDialog.Show(
                    DialogOwnerHelper.GetSafeOwner(),
                    PosLocalization.T("productImage.remove"),
                    SafeImageError(error));
            }
            finally
            {
                ImageBusy = false;
            }
        }

        private void RetryImage()
        {
            _ = RetryImageAsync();
        }

        private async Task RetryImageAsync()
        {
            if (!CanUseImageCommands || _imageProduct == null) return;
            ImageBusy = true;
            ImageOperationStatus = PosLocalization.T("productImage.retrying");
            try
            {
                var retried = await _imageWorkflow.RetryLatestBlockedAsync(
                    _imageProduct,
                    CancellationToken.None).ConfigureAwait(true);
                ImageOperationStatus = retried
                    ? PosLocalization.T("productImage.queued")
                    : PosLocalization.T("productImage.unavailable");
            }
            catch (Exception error)
            {
                ImageOperationStatus = PosLocalization.T(
                    "productImage.unavailable");
                ModernMessageDialog.Show(
                    DialogOwnerHelper.GetSafeOwner(),
                    PosLocalization.T("productImage.retry"),
                    SafeImageError(error));
            }
            finally
            {
                ImageBusy = false;
            }
        }

        private static string StatusText(string state, string completionState)
        {
            switch ((state ?? string.Empty).Trim())
            {
                case "preprocessing": return PosLocalization.T("productImage.preprocessing");
                case "staging": return PosLocalization.T("productImage.preprocessing");
                case "pending_upload": return PosLocalization.T("productImage.uploading");
                case "pending_finalize": return PosLocalization.T("productImage.finalizing");
                case "retry_wait": return PosLocalization.T("productImage.retrying");
                case "failed_blocked": return PosLocalization.T("productImage.conflict");
                case "cleanup_pending": return PosLocalization.T("productImage.cleanupPending");
                case "completed": return PosLocalization.T("productImage.completed");
                case "waiting_dependency":
                case "pending_intent":
                case "pending_remove":
                case "queued":
                    return PosLocalization.T("productImage.queued");
                default:
                    return string.Equals(
                        completionState,
                        "operator_resolution_required",
                        StringComparison.Ordinal)
                        ? PosLocalization.T("productImage.conflict")
                        : PosLocalization.T("productImage.queued");
            }
        }

        private static string SafeImageError(Exception error)
        {
            if (error is InvalidDataException)
                return PosLocalization.T("productImage.corrupt");
            return PosLocalization.T("productImage.unavailable");
        }

        public event Action<bool> RequestClose;
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void BuildSupplierSelection(out int? supplierId, out string supplierName)
        {
            supplierName = NormalizeChoiceText(SupplierText);
            supplierId = null;

            if ((SelectedSupplier != null &&
                 SelectedSupplier.Id == 0 &&
                 TextMatchesSelection(
                     supplierName,
                     SelectedSupplier.Name)) ||
                IsEmptyChoice(supplierName))
            {
                supplierName = string.Empty;
                return;
            }

            if (SelectedSupplier != null &&
                SelectedSupplier.Id != 0 &&
                TextMatchesSelection(supplierName, SelectedSupplier.Name))
            {
                supplierId = SelectedSupplier.Id;
                supplierName = NormalizeChoiceText(SelectedSupplier.Name);
            }
        }

        private void BuildCategorySelection(out int? categoryId, out string categoryName)
        {
            categoryName = NormalizeChoiceText(CategoryText);
            categoryId = null;

            if ((SelectedCategory != null &&
                 SelectedCategory.Id == 0 &&
                 TextMatchesSelection(
                     categoryName,
                     SelectedCategory.Name)) ||
                IsEmptyChoice(categoryName))
            {
                categoryName = string.Empty;
                return;
            }

            if (SelectedCategory != null &&
                SelectedCategory.Id != 0 &&
                TextMatchesSelection(categoryName, SelectedCategory.Name))
            {
                categoryId = SelectedCategory.Id;
                categoryName = NormalizeChoiceText(SelectedCategory.Name);
            }
        }

        private static bool TextMatchesSelection(string text, string selectedName)
        {
            return string.Equals(
                NormalizeChoiceText(text),
                NormalizeChoiceText(selectedName),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEmptyChoice(string text)
        {
            var normalized = NormalizeChoiceText(text);
            return normalized.Length == 0 ||
                string.Equals(
                    normalized,
                    NormalizeChoiceText(
                        PosLocalization.T("products.none")),
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "(Nessuno)", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "(Nessuna)", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeChoiceText(string text)
        {
            var value = (text ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;
            return string.Join(" ", value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }

        private void PopulateStockReasons()
        {
            StockReasons.Add(new StockReasonOption(
                "count_correction",
                PosLocalization.T("products.stockReason.countCorrection")));
            StockReasons.Add(new StockReasonOption(
                "damage",
                PosLocalization.T("products.stockReason.damage")));
            StockReasons.Add(new StockReasonOption(
                "loss",
                PosLocalization.T("products.stockReason.loss")));
            StockReasons.Add(new StockReasonOption(
                "found",
                PosLocalization.T("products.stockReason.found")));
            StockReasons.Add(new StockReasonOption(
                "return_to_stock",
                PosLocalization.T("products.stockReason.returnToStock")));
            StockReasons.Add(new StockReasonOption(
                "transfer",
                PosLocalization.T("products.stockReason.transfer")));
            StockReasons.Add(new StockReasonOption(
                "other",
                PosLocalization.T("products.stockReason.other")));
            SelectedStockReason = StockReasons.First();
        }

        public sealed class StockReasonOption
        {
            public StockReasonOption(string code, string displayName)
            {
                Code = code ?? string.Empty;
                DisplayName = displayName ?? string.Empty;
            }

            public string Code { get; }
            public string DisplayName { get; }
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
#pragma warning disable 0067
            public event EventHandler CanExecuteChanged;
#pragma warning restore 0067
        }
    }
}
