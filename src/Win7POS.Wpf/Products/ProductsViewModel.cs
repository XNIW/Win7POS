using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Win7POS.Core.Models;
using Win7POS.Core.Security;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Products
{
    public sealed class ProductsViewModel : INotifyPropertyChanged
    {
        private readonly ProductsWorkflowService _service;
        private readonly FileLogger _logger = new FileLogger("ProductsViewModel");
        private readonly IPermissionService _permissionService;

        private string _searchText = string.Empty;
        private string _statusMessage = PosLocalization.T("products.ready");
        private bool _isBusy;
        private ProductDetailsRow _selectedProduct;
        private CategoryListItem _selectedCategory;
        private SupplierListItem _selectedSupplier;
        private bool _suppressCategoryRefresh;
        private bool _suppressSupplierRefresh;
        private bool _loaded;
        private int _pageIndex = 1;
        private int _totalCount;
        private const int PageSize = 200;

        public ObservableCollection<ProductDetailsRow> Items { get; } = new ObservableCollection<ProductDetailsRow>();
        public ObservableCollection<CategoryListItem> Categories { get; } = new ObservableCollection<CategoryListItem>();
        public ObservableCollection<SupplierListItem> Suppliers { get; } = new ObservableCollection<SupplierListItem>();

        public int PageSizeValue => PageSize;
        public int PageIndex { get => _pageIndex; set { var v = value < 1 ? 1 : value; if (_pageIndex == v) return; _pageIndex = v; OnPropertyChanged(); RaiseCanExecuteChanged(); } }
        public int TotalCount { get => _totalCount; private set { _totalCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(TotalPages)); OnPropertyChanged(nameof(PagingStatus)); RaiseCanExecuteChanged(); } }
        public int TotalPages => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
        public string PagingStatus => PosLocalization.F(
            "products.pageStatus",
            Items.Count,
            TotalCount,
            PageIndex,
            TotalPages);

        private string _goPageText = "";
        public string GoPageText { get => _goPageText; set { _goPageText = value ?? ""; OnPropertyChanged(); } }

        public string SearchText
        {
            get => _searchText;
            set { _searchText = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(HasActiveFilters)); OnPropertyChanged(nameof(FilterSummary)); RaiseCanExecuteChanged(); }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value ?? string.Empty; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public ProductDetailsRow SelectedProduct
        {
            get => _selectedProduct;
            set { _selectedProduct = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public CategoryListItem SelectedCategory
        {
            get => _selectedCategory;
            set
            {
                if (_selectedCategory == value) return;
                _selectedCategory = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasActiveFilters));
                OnPropertyChanged(nameof(FilterSummary));
                if (!_suppressCategoryRefresh)
                {
                    PageIndex = 1;
                    _ = SearchAsync();
                }
            }
        }

        public SupplierListItem SelectedSupplier
        {
            get => _selectedSupplier;
            set
            {
                if (_selectedSupplier == value) return;
                _selectedSupplier = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasActiveFilters));
                OnPropertyChanged(nameof(FilterSummary));
                if (!_suppressSupplierRefresh)
                {
                    PageIndex = 1;
                    _ = SearchAsync();
                }
            }
        }

        public bool HasActiveFilters =>
            !string.IsNullOrWhiteSpace(SearchText) ||
            (SelectedCategory != null && SelectedCategory.Id != 0) ||
            (SelectedSupplier != null && SelectedSupplier.Id != 0);

        public string FilterSummary
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();
                if (SelectedCategory != null && SelectedCategory.Id != 0)
                    parts.Add(PosLocalization.F("products.filterCategory", SelectedCategory.Name ?? ""));
                if (SelectedSupplier != null && SelectedSupplier.Id != 0)
                    parts.Add(PosLocalization.F("products.filterSupplier", SelectedSupplier.Name ?? ""));
                if (!string.IsNullOrWhiteSpace(SearchText))
                    parts.Add(PosLocalization.F("products.filterText", SearchText.Trim()));
                return parts.Count == 0 ? "" : PosLocalization.F("products.filtersSummary", string.Join(" | ", parts));
            }
        }

        public ICommand SearchCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand NewProductCommand { get; }
        public ICommand EditProductCommand { get; }
        public ICommand CopyNewCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand ImportCommand { get; }
        public ICommand SupplierExcelImportCommand { get; }
        public ICommand ExportDataCommand { get; }
        public ICommand OpenPriceHistoryCommand { get; }
        public ICommand PrevPageCommand { get; }
        public ICommand NextPageCommand { get; }
        public ICommand GoToPageCommand { get; }
        public ICommand ClearFiltersCommand { get; }

        public ProductsViewModel(IPermissionService permissionService = null, ProductsWorkflowService service = null)
        {
            _service = service ?? ProductsWorkflowService.CreateDefault();
            _permissionService = permissionService ?? CreatePermissionService();
            SearchCommand = new AsyncRelayCommand(SearchAsync, _ => !IsBusy, _logger);
            RefreshCommand = new AsyncRelayCommand(RefreshAsync, _ => !IsBusy, _logger);
            NewProductCommand = new AsyncRelayCommand(NewProductAsync, _ => CanEditCatalog, _logger);
            EditProductCommand = new AsyncRelayCommand(EditProductAsync, _ => CanEditSelectedProduct, _logger);
            CopyNewCommand = new AsyncRelayCommand(CopyNewAsync, _ => CanEditSelectedProduct, _logger);
            DeleteCommand = new AsyncRelayCommand(DeleteAsync, _ => CanEditSelectedProduct, _logger);
            ImportCommand = new AsyncRelayCommand(ImportAsync, _ => CanImportCatalog, _logger);
            SupplierExcelImportCommand = new AsyncRelayCommand(SupplierExcelImportAsync, _ => CanImportCatalog, _logger);
            ExportDataCommand = new AsyncRelayCommand(ExportDataAsync, _ => !IsBusy, _logger);
            OpenPriceHistoryCommand = new AsyncRelayCommand(OpenPriceHistoryAsync, _ => !IsBusy && SelectedProduct != null, _logger);
            PrevPageCommand = new AsyncRelayCommand(PrevPageAsync, _ => !IsBusy && PageIndex > 1, _logger);
            NextPageCommand = new AsyncRelayCommand(NextPageAsync, _ => !IsBusy && PageIndex < TotalPages, _logger);
            GoToPageCommand = new AsyncRelayCommand(GoToPageAsync, _ => !IsBusy, _logger);
            ClearFiltersCommand = new AsyncRelayCommand(ClearFiltersAsync, _ => !IsBusy && HasActiveFilters, _logger);
        }

        public async Task LoadAsync()
        {
            if (_loaded)
                return;

            await LoadCategoriesAndSearchAsync().ConfigureAwait(true);
            _loaded = true;
        }

        private static IPermissionService CreatePermissionService()
        {
            var session = OperatorSessionHolder.Current;
            return session == null ? null : new PermissionService(session);
        }

        private bool HasPermission(string permissionCode)
        {
            if (_permissionService != null)
                return _permissionService.Has(permissionCode);

            var user = OperatorSessionHolder.Current?.CurrentUser;
            if (user == null) return false;
            if (user.IsAdmin) return true;
            return user.PermissionCodes?.Contains(permissionCode) == true;
        }

        private bool CanEditCatalog => !IsBusy && HasPermission(PermissionCodes.CatalogEdit);
        private bool CanEditSelectedProduct => CanEditCatalog && SelectedProduct != null;
        private bool CanImportCatalog => !IsBusy && HasPermission(PermissionCodes.CatalogImport);
        private bool CanEditPrices => !IsBusy && HasPermission(PermissionCodes.CatalogPriceEdit);

        private bool DemandProductPermission(string permissionCode, string operationKey)
        {
            if (HasPermission(permissionCode))
                return true;

            StatusMessage = PosLocalization.F("products.permissionDenied", PosLocalization.T(operationKey));
            return false;
        }

        private async Task LoadCategoriesAndSearchAsync()
        {
            IsBusy = true;
            try
            {
                await LoadCategoriesAsync().ConfigureAwait(true);
                await LoadSuppliersAsync().ConfigureAwait(true);
                await SearchAsync().ConfigureAwait(true);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task LoadCategoriesAsync()
        {
            var currentId = SelectedCategory?.Id ?? 0;
            var raw = await _service.GetCategoriesAsync().ConfigureAwait(true);
            Categories.Clear();
            Categories.Add(new CategoryListItem { Id = 0, Name = PosLocalization.T("products.allCategories") });
            foreach (var c in (raw ?? Enumerable.Empty<CategoryListItem>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Name))
                .GroupBy(x => x.Id)
                .Select(g => g.First())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                Categories.Add(c);
            _suppressCategoryRefresh = true;
            try
            {
                SelectedCategory = Categories.FirstOrDefault(c => c.Id == currentId) ?? Categories.FirstOrDefault();
            }
            finally
            {
                _suppressCategoryRefresh = false;
            }
        }

        private async Task RefreshAsync()
        {
            await LoadCategoriesAsync().ConfigureAwait(true);
            await LoadSuppliersAsync().ConfigureAwait(true);
            await SearchAsync().ConfigureAwait(true);
        }

        private async Task LoadSuppliersAsync()
        {
            var currentId = SelectedSupplier?.Id ?? 0;
            var raw = await _service.GetSuppliersAsync().ConfigureAwait(true);
            Suppliers.Clear();
            Suppliers.Add(new SupplierListItem { Id = 0, Name = PosLocalization.T("products.allSuppliers") });
            foreach (var s in (raw ?? Enumerable.Empty<SupplierListItem>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Name))
                .GroupBy(x => x.Id)
                .Select(g => g.First())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                Suppliers.Add(s);
            _suppressSupplierRefresh = true;
            try
            {
                SelectedSupplier = Suppliers.FirstOrDefault(s => s.Id == currentId) ?? Suppliers.FirstOrDefault();
            }
            finally
            {
                _suppressSupplierRefresh = false;
            }
        }

        private async Task ClearFiltersAsync()
        {
            _suppressCategoryRefresh = true;
            _suppressSupplierRefresh = true;
            try
            {
                SelectedCategory = Categories.FirstOrDefault();
                SelectedSupplier = Suppliers.FirstOrDefault();
                SearchText = string.Empty;
                PageIndex = 1;
                OnPropertyChanged(nameof(HasActiveFilters));
                OnPropertyChanged(nameof(FilterSummary));
            }
            finally
            {
                _suppressCategoryRefresh = false;
                _suppressSupplierRefresh = false;
            }

            RaiseCanExecuteChanged();
            await SearchAsync().ConfigureAwait(true);
        }

        private int? GetCategoryIdForSearch()
        {
            if (SelectedCategory == null || SelectedCategory.Id == 0) return null;
            return SelectedCategory.Id;
        }

        private int? GetSupplierIdForSearch()
        {
            if (SelectedSupplier == null || SelectedSupplier.Id == 0) return null;
            return SelectedSupplier.Id;
        }

        private async Task SearchAsync()
        {
            PageIndex = 1;
            SelectedProduct = null;
            await LoadPageAsync().ConfigureAwait(false);
        }

        private async Task LoadPageAsync()
        {
            IsBusy = true;
            try
            {
                var categoryId = GetCategoryIdForSearch();
                var supplierId = GetSupplierIdForSearch();
                TotalCount = await _service.CountDetailsAsync(SearchText, categoryId, supplierId).ConfigureAwait(true);
                var offset = (PageIndex - 1) * PageSize;
                var rows = await _service.SearchDetailsPageAsync(SearchText, PageSize, offset, categoryId, supplierId).ConfigureAwait(true);
                Items.Clear();
                foreach (var p in rows)
                {
                    Items.Add(new ProductDetailsRow
                    {
                        Id = p.Id,
                        Barcode = p.Barcode ?? string.Empty,
                        Name = p.Name ?? string.Empty,
                        UnitPrice = p.UnitPrice,
                        ArticleCode = p.ArticleCode ?? string.Empty,
                        Name2 = p.Name2 ?? string.Empty,
                        PurchasePrice = p.PurchasePrice,
                        StockQty = p.StockQty,
                        SupplierId = p.SupplierId,
                        SupplierName = p.SupplierName ?? string.Empty,
                        CategoryId = p.CategoryId,
                        CategoryName = p.CategoryName ?? string.Empty
                    });
                }
                StatusMessage = PagingStatus;
                OnPropertyChanged(nameof(PagingStatus));
                RaiseCanExecuteChanged();
            }
            catch (Exception ex)
            {
                StatusMessage = PosLocalization.F("products.searchError", ex.Message);
                _logger.LogError(ex, "Products search failed");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task PrevPageAsync()
        {
            if (PageIndex <= 1) return;
            PageIndex--;
            await LoadPageAsync().ConfigureAwait(false);
        }

        private async Task NextPageAsync()
        {
            if (PageIndex >= TotalPages) return;
            PageIndex++;
            await LoadPageAsync().ConfigureAwait(false);
        }

        private async Task GoToPageAsync()
        {
            if (!string.IsNullOrWhiteSpace(GoPageText) && int.TryParse(GoPageText.Trim(), out var p) && p >= 1 && p <= TotalPages)
                PageIndex = p;
            await LoadPageAsync().ConfigureAwait(false);
        }

        private async Task NewProductAsync()
        {
            if (!DemandProductPermission(PermissionCodes.CatalogEdit, "products.operationEditCatalog")) return;
            try
            {
                var ok = await ProductEditDialog.ShowAsync(ProductEditMode.New, null, _service).ConfigureAwait(true);
                if (ok) await RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Products NewProduct dialog");
                StatusMessage = PosLocalization.F("products.dialogOpenError", ex.GetType().Name);
            }
        }

        private async Task EditProductAsync()
        {
            if (SelectedProduct == null) return;
            if (!DemandProductPermission(PermissionCodes.CatalogEdit, "products.operationEditCatalog")) return;
            try
            {
                var full = await _service.GetDetailsByIdAsync(SelectedProduct.Id).ConfigureAwait(true);
                if (full == null)
                {
                    StatusMessage = PosLocalization.T("products.notFound");
                    return;
                }
                var ok = await ProductEditDialog.ShowAsync(ProductEditMode.Edit, full, _service).ConfigureAwait(true);
                if (ok) await RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Products EditProduct dialog");
                StatusMessage = PosLocalization.F("products.dialogOpenError", ex.GetType().Name);
            }
        }

        private async Task CopyNewAsync()
        {
            if (SelectedProduct == null) return;
            if (!DemandProductPermission(PermissionCodes.CatalogEdit, "products.operationEditCatalog")) return;
            try
            {
                var full = await _service.GetDetailsByIdAsync(SelectedProduct.Id).ConfigureAwait(true);
                if (full == null)
                {
                    StatusMessage = PosLocalization.T("products.notFound");
                    return;
                }
                var ok = await ProductEditDialog.ShowAsync(ProductEditMode.Duplicate, full, _service).ConfigureAwait(true);
                if (ok) await RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                StatusMessage = PosLocalization.F("common.errorWithMessage", ex.Message);
                _logger.LogError(ex, "Products Duplicate failed");
            }
        }

        private async Task DeleteAsync()
        {
            if (SelectedProduct == null) return;
            if (!DemandProductPermission(PermissionCodes.CatalogEdit, "products.operationEditCatalog")) return;

            var okToDelete = DeleteProductConfirmDialog.ShowDialog(
                DialogOwnerHelper.GetSafeOwner(),
                SelectedProduct.Barcode,
                SelectedProduct.Name);

            if (!okToDelete)
                return;

            try
            {
                var ok = await _service.DeleteProductAsync(SelectedProduct.Barcode).ConfigureAwait(true);
                StatusMessage = ok
                    ? PosLocalization.T("products.deleted")
                    : PosLocalization.T("products.deleteFailed");
                if (ok)
                    await RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Products Delete failed");
                StatusMessage = PosLocalization.F("products.deleteError", ex.Message);
            }
        }

        private async Task ImportAsync()
        {
            if (!DemandProductPermission(PermissionCodes.CatalogImport, "products.operationImportCatalog")) return;
            try
            {
                ImportDataDialog.ShowDialog(DialogOwnerHelper.GetSafeOwner());
                StatusMessage = PosLocalization.T("products.catalogUpdating");
                await RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                StatusMessage = PosLocalization.F("common.errorWithMessage", ex.Message);
                _logger.LogError(ex, "Products Import failed");
            }
        }

        private async Task SupplierExcelImportAsync()
        {
            if (!DemandProductPermission(PermissionCodes.CatalogImport, "products.operationImportCatalog")) return;
            try
            {
                var applied = SupplierExcelImportDialog.ShowDialog(DialogOwnerHelper.GetSafeOwner());
                if (applied)
                {
                    CatalogEvents.RaiseCatalogChanged(null);
                    StatusMessage = PosLocalization.T("products.catalogUpdating");
                    await RefreshAsync().ConfigureAwait(true);
                }
                else
                {
                    StatusMessage = PosLocalization.T("products.ready");
                }
            }
            catch (Exception ex)
            {
                StatusMessage = PosLocalization.F("common.errorWithMessage", ex.Message);
                _logger.LogError(ex, "Products Supplier Excel import failed");
            }
        }

        private async Task ExportDataAsync()
        {
            IsBusy = true;
            try
            {
                StatusMessage = PosLocalization.T("products.exportOpening");
                var choice = ExportDataDialog.ShowDialogAndGetChoice(DialogOwnerHelper.GetSafeOwner());
                if (choice == null)
                {
                    StatusMessage = PosLocalization.T("products.exportCancelled");
                    return;
                }

                var path = choice.TargetPath;
                StatusMessage = PosLocalization.T("products.exportInProgress");
                if (choice.Format == ExportDataFormat.Xlsx)
                {
                    await _service.ExportWorkbookAsync(path).ConfigureAwait(true);
                    StatusMessage = PosLocalization.F("products.exportXlsxDone", path);
                }
                else
                {
                    await _service.ExportSingleCsvAsync(path).ConfigureAwait(true);
                    StatusMessage = PosLocalization.F("products.exportCsvDone", path);
                }
            }
            catch (IOException ioEx)
            {
                StatusMessage = PosLocalization.T("products.exportFileBusy");
                _logger.LogError(ioEx, "Products export I/O error");
            }
            catch (UnauthorizedAccessException uaEx)
            {
                StatusMessage = PosLocalization.T("products.exportPermissionDenied");
                _logger.LogError(uaEx, "Products export permission error");
            }
            catch (Exception ex)
            {
                StatusMessage = PosLocalization.F("products.exportDataError", ex.Message);
                _logger.LogError(ex, "Products export data failed");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task OpenPriceHistoryAsync()
        {
            if (SelectedProduct == null) return;
            try
            {
                var full = await _service.GetDetailsByIdAsync(SelectedProduct.Id).ConfigureAwait(true);
                if (full == null)
                {
                    StatusMessage = PosLocalization.T("products.notFound");
                    return;
                }
                ProductPriceHistoryDialog.ShowDialog(DialogOwnerHelper.GetSafeOwner(), full.Id, full.Barcode ?? "", full.Name ?? "", (int)full.UnitPrice, full.PurchasePrice, CanEditPrices);
            }
            catch (Exception ex)
            {
                StatusMessage = PosLocalization.F("products.priceHistoryOpenError", ex.Message);
                _logger.LogError(ex, "Open price history failed");
            }
        }

        private void RaiseCanExecuteChanged()
        {
            (SearchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (NewProductCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (EditProductCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (CopyNewCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (DeleteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ImportCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (SupplierExcelImportCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ExportDataCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (OpenPriceHistoryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (PrevPageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (NextPageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (GoToPageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ClearFiltersCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private sealed class AsyncRelayCommand : ICommand
        {
            private readonly Func<Task> _executeAsync;
            private readonly Func<object, bool> _canExecute;
            private readonly FileLogger _logger;

            public AsyncRelayCommand(Func<Task> executeAsync, Func<object, bool> canExecute = null, FileLogger logger = null)
            {
                _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
                _canExecute = canExecute;
                _logger = logger ?? new FileLogger("ProductsViewModel");
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
                    UiErrorHandler.Handle(ex, _logger, "Products AsyncRelayCommand failed");
                }
            }
            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
