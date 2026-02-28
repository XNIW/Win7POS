using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Win7POS.Core.Models;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Products
{
    public sealed class ProductsViewModel : INotifyPropertyChanged
    {
        private readonly ProductsWorkflowService _service = new ProductsWorkflowService();
        private readonly FileLogger _logger = new FileLogger();

        private string _searchText = string.Empty;
        private string _statusMessage = "Pronto.";
        private bool _isBusy;
        private ProductRow _selectedProduct;

        public ObservableCollection<ProductRow> Items { get; } = new ObservableCollection<ProductRow>();

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

        public ProductRow SelectedProduct
        {
            get => _selectedProduct;
            set { _selectedProduct = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public ICommand SearchCommand { get; }
        public ICommand SaveSelectedCommand { get; }
        public ICommand ExportCsvCommand { get; }

        public ProductsViewModel()
        {
            SearchCommand = new AsyncRelayCommand(SearchAsync, _ => !IsBusy);
            SaveSelectedCommand = new AsyncRelayCommand(SaveSelectedAsync, _ => !IsBusy && SelectedProduct != null);
            ExportCsvCommand = new AsyncRelayCommand(ExportCsvAsync, _ => !IsBusy);
            _ = SearchAsync();
        }

        private async Task SearchAsync()
        {
            IsBusy = true;
            try
            {
                var rows = await _service.SearchAsync(SearchText, 200).ConfigureAwait(true);
                Items.Clear();
                foreach (var p in rows)
                {
                    Items.Add(new ProductRow
                    {
                        Id = p.Id,
                        Barcode = p.Barcode ?? string.Empty,
                        Name = p.Name ?? string.Empty,
                        UnitPrice = p.UnitPrice
                    });
                }
                StatusMessage = "Trovati: " + Items.Count;
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

        private async Task SaveSelectedAsync()
        {
            if (SelectedProduct == null) return;
            IsBusy = true;
            try
            {
                await _service.UpdateAsync(SelectedProduct.Id, SelectedProduct.Name, SelectedProduct.UnitPrice).ConfigureAwait(true);
                StatusMessage = "Prodotto aggiornato: " + SelectedProduct.Barcode;
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore salvataggio: " + ex.Message;
                _logger.LogError(ex, "Products save failed");
            }
            finally
            {
                IsBusy = false;
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
            (SaveSelectedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ExportCsvCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public sealed class ProductRow
        {
            public long Id { get; set; }
            public string Barcode { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public int UnitPrice { get; set; }
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
            public async void Execute(object parameter) => await _executeAsync().ConfigureAwait(true);
            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
