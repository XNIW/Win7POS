using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Win7POS.Core.Models;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Products
{
    public sealed class ProductPriceHistoryViewModel : INotifyPropertyChanged
    {
        private static readonly FileLogger _logger = new FileLogger("ProductPriceHistoryViewModel");
        private readonly long _productId;
        private readonly ProductsWorkflowService _service;
        private string _currentRetail;
        private string _currentPurchase;
        private string _newRetailText = "";
        private string _newPurchaseText = "";
        private string _statusMessage = "";
        private bool _isBusy;

        public ProductPriceHistoryViewModel(long productId, string barcode, string name, int currentRetail, int currentPurchase, ProductsWorkflowService service)
        {
            _productId = productId;
            _service = service ?? throw new ArgumentNullException(nameof(service));
            Barcode = barcode ?? "";
            ProductName = name ?? "";
            _currentRetail = currentRetail.ToString();
            _currentPurchase = currentPurchase.ToString();

            RetailHistory = new ObservableCollection<ProductPriceHistoryRow>();
            PurchaseHistory = new ObservableCollection<ProductPriceHistoryRow>();

            RefreshCommand = new AsyncRelayCommand(RefreshAsync, _ => !IsBusy);
            ApplyNewPricesCommand = new AsyncRelayCommand(ApplyNewPricesAsync, _ => !IsBusy && (HasNewRetail || HasNewPurchase));
        }

        public string Barcode { get; }
        public string ProductName { get; }

        public string CurrentRetailPrice
        {
            get => _currentRetail;
            set { _currentRetail = value ?? ""; OnPropertyChanged(); }
        }

        public string CurrentPurchasePrice
        {
            get => _currentPurchase;
            set { _currentPurchase = value ?? ""; OnPropertyChanged(); }
        }

        public string NewRetailText
        {
            get => _newRetailText;
            set { _newRetailText = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(HasNewRetail)); RaiseCanExecuteChanged(); }
        }

        public string NewPurchaseText
        {
            get => _newPurchaseText;
            set { _newPurchaseText = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(HasNewPurchase)); RaiseCanExecuteChanged(); }
        }

        public bool HasNewRetail => int.TryParse(NewRetailText?.Trim().Replace(".", "").Replace(",", ""), out var v) && v >= 0;
        public bool HasNewPurchase => int.TryParse(NewPurchaseText?.Trim().Replace(".", "").Replace(",", ""), out var v) && v >= 0;

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value ?? ""; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public ObservableCollection<ProductPriceHistoryRow> RetailHistory { get; }
        public ObservableCollection<ProductPriceHistoryRow> PurchaseHistory { get; }

        public ICommand RefreshCommand { get; }
        public ICommand ApplyNewPricesCommand { get; }

        public event PropertyChangedEventHandler PropertyChanged;

        private void RaiseCanExecuteChanged()
        {
            (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ApplyNewPricesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        public async Task LoadAsync()
        {
            await RefreshAsync().ConfigureAwait(true);
        }

        private async Task RefreshAsync()
        {
            IsBusy = true;
            try
            {
                var list = await _service.GetPriceHistoryAsync(_productId).ConfigureAwait(true);
                RetailHistory.Clear();
                PurchaseHistory.Clear();
                foreach (var row in list ?? Enumerable.Empty<ProductPriceHistoryRow>())
                {
                    if (string.Equals(row.PriceType, "retail", StringComparison.OrdinalIgnoreCase))
                        RetailHistory.Add(row);
                    else
                        PurchaseHistory.Add(row);
                }

                var details = await _service.GetDetailsByIdAsync(_productId).ConfigureAwait(true);
                if (details != null)
                {
                    CurrentRetailPrice = details.UnitPrice.ToString();
                    CurrentPurchasePrice = details.PurchasePrice.ToString();
                }
                StatusMessage = "Storico aggiornato.";
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ApplyNewPricesAsync()
        {
            var retail = ParseClp(NewRetailText);
            var purchase = ParseClp(NewPurchaseText);
            var details = await _service.GetDetailsByIdAsync(_productId).ConfigureAwait(true);
            if (details == null) { StatusMessage = "Prodotto non trovato."; return; }
            var currentRetail = (int)details.UnitPrice;
            var currentPurchase = details.PurchasePrice;
            if (retail < 0) retail = currentRetail;
            if (purchase < 0) purchase = currentPurchase;

            IsBusy = true;
            try
            {
                await _service.UpdateProductPricesAsync(_productId, purchase, retail, "MANUAL_EDIT").ConfigureAwait(true);
                CurrentRetailPrice = retail.ToString();
                CurrentPurchasePrice = purchase.ToString();
                NewRetailText = "";
                NewPurchaseText = "";
                await RefreshAsync().ConfigureAwait(true);
                StatusMessage = "Prezzi aggiornati e storico registrato.";
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static int ParseClp(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return -1;
            var s = text.Trim().Replace(".", "").Replace(",", "");
            return int.TryParse(s, out var v) ? v : -1;
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private sealed class AsyncRelayCommand : ICommand
        {
            private readonly Func<Task> _execute;
            private readonly Func<object, bool> _canExecute;

            public AsyncRelayCommand(Func<Task> execute, Func<object, bool> canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;
            public async void Execute(object parameter)
            {
                try { await _execute().ConfigureAwait(true); }
                catch (Exception ex) { UiErrorHandler.Handle(ex, _logger, "ProductPriceHistoryViewModel.AsyncRelayCommand"); }
            }
            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
