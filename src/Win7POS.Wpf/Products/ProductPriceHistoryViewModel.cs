using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Win7POS.Core.Models;
using Win7POS.Core.Util;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Products
{
    public sealed class ProductPriceHistoryViewModel : INotifyPropertyChanged
    {
        private static readonly FileLogger _logger = new FileLogger("ProductPriceHistoryViewModel");
        private readonly long _productId;
        private readonly ProductsWorkflowService _service;
        private readonly bool _canEditPrices;
        private string _currentRetail;
        private string _currentPurchase;
        private string _newRetailText = "";
        private string _newPurchaseText = "";
        private string _statusMessage = "";
        private bool _isBusy;

        public ProductPriceHistoryViewModel(long productId, string barcode, string name, long currentRetail, int currentPurchase, ProductsWorkflowService service, bool canEditPrices)
        {
            _productId = productId;
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _canEditPrices = canEditPrices;
            Barcode = barcode ?? "";
            ProductName = name ?? "";
            _currentRetail = MoneyClp.Format(currentRetail);
            _currentPurchase = MoneyClp.Format(currentPurchase);

            RetailHistory = new ObservableCollection<ProductPriceHistoryRow>();
            PurchaseHistory = new ObservableCollection<ProductPriceHistoryRow>();

            RefreshCommand = new AsyncRelayCommand(RefreshAsync, _ => !IsBusy);
            ApplyNewPricesCommand = new AsyncRelayCommand(ApplyNewPricesAsync, _ => CanApplyNewPrices);
        }

        public string Barcode { get; }
        public string ProductName { get; }

        public string CurrentRetailPrice
        {
            get => _currentRetail;
            set { _currentRetail = value ?? ""; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public string CurrentPurchasePrice
        {
            get => _currentPurchase;
            set { _currentPurchase = value ?? ""; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public string NewRetailText
        {
            get => _newRetailText;
            set { _newRetailText = value ?? ""; if (!IsBusy) StatusMessage = ""; OnPropertyChanged(); OnPropertyChanged(nameof(HasNewRetail)); OnPropertyChanged(nameof(NewRetailError)); RaiseCanExecuteChanged(); }
        }

        public string NewPurchaseText
        {
            get => _newPurchaseText;
            set { _newPurchaseText = value ?? ""; if (!IsBusy) StatusMessage = ""; OnPropertyChanged(); OnPropertyChanged(nameof(HasNewPurchase)); OnPropertyChanged(nameof(NewPurchaseError)); RaiseCanExecuteChanged(); }
        }

        public bool HasNewRetail => ParsePriceInput(NewRetailText).Kind == PriceInputKind.Valid;
        public bool HasNewPurchase => ParsePriceInput(NewPurchaseText).Kind == PriceInputKind.Valid;
        public string NewRetailError => PriceInputError(ParsePriceInput(NewRetailText));
        public string NewPurchaseError => PriceInputError(ParsePriceInput(NewPurchaseText));
        public bool CanEditPrices => _canEditPrices;
        public bool CanEditPriceInputs => CanEditPrices && !IsBusy;
        public bool CanApplyNewPrices
        {
            get
            {
                if (!CanEditPrices || IsBusy) return false;
                var retail = ParsePriceInput(NewRetailText);
                var purchase = ParsePriceInput(NewPurchaseText);
                if (retail.Kind == PriceInputKind.Invalid || purchase.Kind == PriceInputKind.Invalid) return false;
                return (retail.Kind == PriceInputKind.Valid && !SamePrice(CurrentRetailPrice, retail.Value))
                    || (purchase.Kind == PriceInputKind.Valid && !SamePrice(CurrentPurchasePrice, purchase.Value));
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value ?? ""; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanEditPriceInputs)); RaiseCanExecuteChanged(); }
        }

        public ObservableCollection<ProductPriceHistoryRow> RetailHistory { get; }
        public ObservableCollection<ProductPriceHistoryRow> PurchaseHistory { get; }

        public ICommand RefreshCommand { get; }
        public ICommand ApplyNewPricesCommand { get; }

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action<bool> PriceInputFocusRequested;

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
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                await RefreshCoreAsync().ConfigureAwait(true);
                StatusMessage = PosLocalization.T("priceHistory.updated");
            }
            catch (Exception ex)
            {
                StatusMessage = PosLocalization.F("common.errorWithMessage", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RefreshCoreAsync()
        {
            var list = await _service.GetPriceHistoryAsync(_productId).ConfigureAwait(true);
            var details = await _service.GetDetailsByIdAsync(_productId).ConfigureAwait(true);
            RetailHistory.Clear();
            PurchaseHistory.Clear();
            foreach (var row in list ?? Enumerable.Empty<ProductPriceHistoryRow>())
            {
                if (string.Equals(row.PriceType, "retail", StringComparison.OrdinalIgnoreCase))
                    RetailHistory.Add(row);
                else
                    PurchaseHistory.Add(row);
            }
            if (details != null)
            {
                CurrentRetailPrice = MoneyClp.Format(details.UnitPrice);
                CurrentPurchasePrice = MoneyClp.Format(details.PurchasePrice);
            }
        }

        private async Task ApplyNewPricesAsync()
        {
            if (IsBusy) return;
            if (!CanEditPrices)
            {
                StatusMessage = PosLocalization.T("priceHistory.priceEditDenied");
                return;
            }
            var retailInput = ParsePriceInput(NewRetailText);
            var purchaseInput = ParsePriceInput(NewPurchaseText);
            if (retailInput.Kind == PriceInputKind.Invalid || purchaseInput.Kind == PriceInputKind.Invalid)
            {
                StatusMessage = PriceInputError(retailInput.Kind == PriceInputKind.Invalid ? retailInput : purchaseInput);
                PriceInputFocusRequested?.Invoke(retailInput.Kind == PriceInputKind.Invalid);
                return;
            }
            if (retailInput.Kind == PriceInputKind.Blank && purchaseInput.Kind == PriceInputKind.Blank)
            {
                StatusMessage = PosLocalization.T("priceHistory.noPriceChanges");
                return;
            }

            // Own the operation before the first service await. Both validated values
            // are captured above, and refresh keeps the same busy interval.
            IsBusy = true;
            StatusMessage = PosLocalization.T("priceHistory.savingPrices");
            try
            {
                var details = await _service.GetDetailsByIdAsync(_productId).ConfigureAwait(true);
                if (details == null) { StatusMessage = PosLocalization.T("products.notFound"); return; }
                long retail = retailInput.Kind == PriceInputKind.Blank ? details.UnitPrice : retailInput.Value;
                var purchase = purchaseInput.Kind == PriceInputKind.Blank ? details.PurchasePrice : purchaseInput.Value;
                if (retail == details.UnitPrice && purchase == details.PurchasePrice)
                {
                    StatusMessage = PosLocalization.T("priceHistory.noPriceChanges");
                    return;
                }
                await _service.UpdateProductPricesAsync(_productId, purchase, retail, "MANUAL_EDIT").ConfigureAwait(true);
                CurrentRetailPrice = MoneyClp.Format(retail);
                CurrentPurchasePrice = MoneyClp.Format(purchase);
                NewRetailText = "";
                NewPurchaseText = "";
                await RefreshCoreAsync().ConfigureAwait(true);
                StatusMessage = PosLocalization.T("priceHistory.pricesUpdated");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ApplyNewPrices");
                StatusMessage = PosLocalization.F("common.errorWithMessage", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static bool SamePrice(string current, int value)
            => long.TryParse(current, NumberStyles.Integer, CultureInfo.InvariantCulture, out var price) && price == value;

        private static string PriceInputError(PriceInput input)
            => input.Kind == PriceInputKind.Invalid
                ? PosLocalization.F("priceHistory.invalidPrice", MoneyClp.Format(int.MaxValue)) : "";

        private static PriceInput ParsePriceInput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new PriceInput(PriceInputKind.Blank);
            var raw = text.Trim();
            if (raw[0] == '+') raw = raw.Substring(1);
            if (raw.Length == 0) return new PriceInput(PriceInputKind.Invalid);
            // MoneyClp defines integral CLP and accepts thousand groups. Check the
            // shape first so malformed decimals/mixed separators cannot become a
            // different amount when the shared parser removes separators.
            char separator = '\0';
            foreach (var character in raw)
            {
                if (character >= '0' && character <= '9') continue;
                if (character != '.' && character != ',' && character != ' ')
                    return new PriceInput(PriceInputKind.Invalid);
                if (separator != '\0' && separator != character)
                    return new PriceInput(PriceInputKind.Invalid);
                separator = character;
            }
            if (separator != '\0')
            {
                var groups = raw.Split(separator);
                if (groups[0].Length < 1 || groups[0].Length > 3 || groups.Skip(1).Any(group => group.Length != 3))
                    return new PriceInput(PriceInputKind.Invalid);
            }
            var value = MoneyClp.Parse(raw);
            return value < 0 ? new PriceInput(PriceInputKind.Invalid) : new PriceInput(PriceInputKind.Valid, value);
        }

        private enum PriceInputKind { Blank, Valid, Invalid }

        private struct PriceInput
        {
            internal PriceInput(PriceInputKind kind, int value = 0) { Kind = kind; Value = value; }
            internal PriceInputKind Kind { get; }
            internal int Value { get; }
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
