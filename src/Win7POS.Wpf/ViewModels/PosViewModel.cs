using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Win7POS.Core.Pos;
using Win7POS.Data;
using Win7POS.Data.Adapters;
using Win7POS.Data.Repositories;

namespace Win7POS.Wpf.ViewModels
{
    public sealed class PosViewModel : ObservableObject
    {
        private readonly PosSession _session;
        private bool _isSyncing;

        public ObservableCollection<CartLineVm> Cart { get; } = new ObservableCollection<CartLineVm>();

        private string _barcodeInput;
        public string BarcodeInput
        {
            get => _barcodeInput;
            set => SetProperty(ref _barcodeInput, value);
        }

        public int Total => Cart.Sum(x => x.LineTotal);

        public RelayCommand PayCashCommand { get; }

        public PosViewModel()
        {
            var opt = PosDbOptions.Default();
            DbInitializer.EnsureCreated(opt);

            var factory = new SqliteConnectionFactory(opt);
            var products = new ProductRepository(factory);
            var sales = new SaleRepository(factory);
            _session = new PosSession(new DataProductLookup(products), new DataSalesStore(sales));

            Cart.CollectionChanged += (_, __) => RaisePropertyChanged(nameof(Total));

            PayCashCommand = new RelayCommand(async () => await PayCashAsync(), () => Cart.Any());
        }

        public async Task OnBarcodeEnterAsync()
        {
            var code = (BarcodeInput ?? "").Trim();
            if (code.Length == 0) return;

            try
            {
                await _session.AddByBarcodeAsync(code);
                SyncCartFromSession();
                RaisePropertyChanged(nameof(Total));
                PayCashCommand.RaiseCanExecuteChanged();
            }
            catch (PosException ex)
            {
                MessageBox.Show(ex.Message);
            }

            BarcodeInput = "";
        }

        private async Task PayCashAsync()
        {
            if (!Cart.Any()) return;
            try
            {
                var completed = await _session.PayCashAsync();
                SyncCartFromSession();
                RaisePropertyChanged(nameof(Total));
                PayCashCommand.RaiseCanExecuteChanged();
                MessageBox.Show($"Vendita salvata: {completed.Sale.Code}");
            }
            catch (PosException ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void SyncCartFromSession()
        {
            _isSyncing = true;
            try
            {
                UnsubscribeCartLineEvents();
                Cart.Clear();
                foreach (var x in _session.Lines)
                {
                    var vm = new CartLineVm
                    {
                        ProductId = x.ProductId,
                        Barcode = x.Barcode,
                        Name = x.Name,
                        Quantity = x.Quantity,
                        UnitPrice = x.UnitPrice
                    };
                    vm.PropertyChanged += OnCartLinePropertyChanged;
                    Cart.Add(vm);
                }
            }
            finally
            {
                _isSyncing = false;
            }
        }

        private void UnsubscribeCartLineEvents()
        {
            foreach (var oldLine in Cart)
                oldLine.PropertyChanged -= OnCartLinePropertyChanged;
        }

        private void OnCartLinePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_isSyncing) return;
            if (e.PropertyName != nameof(CartLineVm.Quantity)) return;

            var line = sender as CartLineVm;
            if (line == null) return;

            try
            {
                _session.SetQuantity(line.Barcode, line.Quantity);
                SyncCartFromSession();
                RaisePropertyChanged(nameof(Total));
                PayCashCommand.RaiseCanExecuteChanged();
            }
            catch (PosException ex)
            {
                MessageBox.Show(ex.Message);
                SyncCartFromSession();
            }
        }
    }
}
