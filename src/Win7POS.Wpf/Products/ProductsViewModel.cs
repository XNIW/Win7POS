using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Win7POS.Core.Models;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;

namespace Win7POS.Wpf.Products
{
    public sealed class ProductsViewModel : INotifyPropertyChanged
    {
        private readonly ProductsWorkflowService _service = new ProductsWorkflowService();
        private readonly PosWorkflowService _posService = new PosWorkflowService();
        private readonly FileLogger _logger = new FileLogger();

        private string _searchText = string.Empty;
        private string _statusMessage = "Pronto.";
        private bool _isBusy;
        private ProductDetailsRow _selectedProduct;
        private CategoryListItem _selectedCategory;
        private bool _suppressCategoryRefresh;
        private int _pageIndex = 1;
        private int _totalCount;
        private const int PageSize = 200;

        public ObservableCollection<ProductDetailsRow> Items { get; } = new ObservableCollection<ProductDetailsRow>();
        public ObservableCollection<CategoryListItem> Categories { get; } = new ObservableCollection<CategoryListItem>();

        public int PageSizeValue => PageSize;
        public int PageIndex { get => _pageIndex; set { var v = value < 1 ? 1 : value; if (_pageIndex == v) return; _pageIndex = v; OnPropertyChanged(); RaiseCanExecuteChanged(); } }
        public int TotalCount { get => _totalCount; private set { _totalCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(TotalPages)); OnPropertyChanged(nameof(PagingStatus)); RaiseCanExecuteChanged(); } }
        public int TotalPages => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
        public string PagingStatus => $"Trovati: {Items.Count}/{TotalCount}  |  Pagina {PageIndex}/{TotalPages}";

        private string _goPageText = "";
        public string GoPageText { get => _goPageText; set { _goPageText = value ?? ""; OnPropertyChanged(); } }

        public string SearchText
        {
            get => _searchText;
            set { _searchText = value ?? string.Empty; OnPropertyChanged(); }
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
                if (!_suppressCategoryRefresh)
                    _ = RefreshAsync();
            }
        }

        public ICommand SearchCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand NewCommand { get; }
        public ICommand EditCommand { get; }
        public ICommand CopyNewCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand ImportCommand { get; }
        public ICommand ExportCsvCommand { get; }
        public ICommand PrevPageCommand { get; }
        public ICommand NextPageCommand { get; }
        public ICommand GoToPageCommand { get; }

        public ProductsViewModel()
        {
            SearchCommand = new AsyncRelayCommand(SearchAsync, _ => !IsBusy, _logger);
            RefreshCommand = new AsyncRelayCommand(RefreshAsync, _ => !IsBusy, _logger);
            NewCommand = new AsyncRelayCommand(NewAsync, _ => !IsBusy, _logger);
            EditCommand = new AsyncRelayCommand(EditAsync, _ => !IsBusy && SelectedProduct != null, _logger);
            CopyNewCommand = new AsyncRelayCommand(CopyNewAsync, _ => !IsBusy && SelectedProduct != null, _logger);
            DeleteCommand = new AsyncRelayCommand(DeleteAsync, _ => !IsBusy && SelectedProduct != null, _logger);
            ImportCommand = new AsyncRelayCommand(ImportAsync, _ => !IsBusy, _logger);
            ExportCsvCommand = new AsyncRelayCommand(ExportCsvAsync, _ => !IsBusy, _logger);
            PrevPageCommand = new AsyncRelayCommand(PrevPageAsync, _ => !IsBusy && PageIndex > 1, _logger);
            NextPageCommand = new AsyncRelayCommand(NextPageAsync, _ => !IsBusy && PageIndex < TotalPages, _logger);
            GoToPageCommand = new AsyncRelayCommand(GoToPageAsync, _ => !IsBusy, _logger);
            _ = LoadCategoriesAndSearchAsync();
        }

        private async Task LoadCategoriesAndSearchAsync()
        {
            IsBusy = true;
            try
            {
                await LoadCategoriesAsync().ConfigureAwait(true);
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
            Categories.Add(new CategoryListItem { Id = 0, Name = "(Tutte)" });
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
            await SearchAsync().ConfigureAwait(true);
        }

        private int? GetCategoryIdForSearch()
        {
            if (SelectedCategory == null || SelectedCategory.Id == 0) return null;
            return SelectedCategory.Id;
        }

        private async Task SearchAsync()
        {
            PageIndex = 1;
            await LoadPageAsync().ConfigureAwait(false);
        }

        private async Task LoadPageAsync()
        {
            IsBusy = true;
            try
            {
                var categoryId = GetCategoryIdForSearch();
                TotalCount = await _service.CountDetailsAsync(SearchText, categoryId).ConfigureAwait(true);
                var offset = (PageIndex - 1) * PageSize;
                var rows = await _service.SearchDetailsPageAsync(SearchText, PageSize, offset, categoryId).ConfigureAwait(true);
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
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore ricerca: " + ex.Message;
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

        private async Task NewAsync()
        {
            try
            {
                var dlg = new AddProductDialog("", _posService, focusRetailPrice: true)
                {
                    Owner = Application.Current?.MainWindow
                };
                if (dlg.ShowDialog() != true) return;
                var vm = dlg.ViewModel;
                await _posService.CreateProductFullAsync(
                    vm.Barcode ?? "",
                    vm.ProductName ?? "",
                    vm.PriceMinor,
                    vm.PurchasePriceMinor,
                    vm.SelectedSupplier?.Id == 0 ? null : vm.SelectedSupplier?.Id,
                    vm.SelectedSupplier?.Name ?? "",
                    vm.SelectedCategory?.Id == 0 ? null : vm.SelectedCategory?.Id,
                    vm.SelectedCategory?.Name ?? "",
                    vm.StockQty).ConfigureAwait(true);
                await RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Products New dialog");
                StatusMessage = "Errore apertura dialog: " + ex.GetType().Name + " (vedi app.log)";
            }
        }

        private async Task EditAsync()
        {
            if (SelectedProduct == null) return;
            try
            {
                var dlg = new EditProductDialog(SelectedProduct.Barcode ?? "", SelectedProduct.Name ?? "", SelectedProduct.UnitPrice)
                {
                    Owner = Application.Current?.MainWindow
                };
                if (dlg.ShowDialog() != true) return;
                var vm = dlg.ViewModel;
                await _posService.UpdateProductAsync(vm.Barcode ?? "", vm.ProductName ?? "", vm.PriceMinor).ConfigureAwait(true);
                await RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Products Edit dialog");
                StatusMessage = "Errore apertura dialog: " + ex.GetType().Name + " (vedi app.log)";
            }
        }

        private async Task CopyNewAsync()
        {
            if (SelectedProduct == null) return;
            try
            {
                var ok = await ProductEditDialog.ShowAsync(ProductEditMode.Duplicate, SelectedProduct, _service).ConfigureAwait(true);
                if (ok) await RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore: " + ex.Message;
                _logger.LogError(ex, "Products Duplicate failed");
            }
        }

        private async Task DeleteAsync()
        {
            if (SelectedProduct == null) return;
            if (MessageBox.Show("Eliminare il prodotto \"" + SelectedProduct.Barcode + "\"?", "Conferma", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            try
            {
                var ok = await _service.DeleteProductAsync(SelectedProduct.Barcode).ConfigureAwait(true);
                StatusMessage = ok ? "Prodotto eliminato." : "Eliminazione fallita.";
                if (ok) await RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore: " + ex.Message;
                _logger.LogError(ex, "Products Delete failed");
            }
        }

        private async Task ImportAsync()
        {
            try
            {
                var win = new Window
                {
                    Title = "Import dati",
                    Width = 900,
                    Height = 700,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = Application.Current?.MainWindow,
                    Content = new ImportView()
                };
                win.ShowDialog();
                await RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore: " + ex.Message;
                _logger.LogError(ex, "Products Import failed");
            }
        }

        private async Task ExportCsvAsync()
        {
            IsBusy = true;
            try
            {
                var path = await _service.ExportCsvAsync().ConfigureAwait(true);
                StatusMessage = "Export CSV: " + path;
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore export CSV: " + ex.Message;
                _logger.LogError(ex, "Products export failed");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void RaiseCanExecuteChanged()
        {
            (SearchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (NewCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (EditCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (CopyNewCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (DeleteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ImportCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ExportCsvCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (PrevPageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (NextPageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (GoToPageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
                _logger = logger ?? new FileLogger();
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
