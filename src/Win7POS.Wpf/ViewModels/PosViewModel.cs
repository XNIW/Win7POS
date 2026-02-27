using System.Collections.ObjectModel;
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
            catch (System.InvalidOperationException ex)
            {
                MessageBox.Show(ex.Message);
            }

            BarcodeInput = "";
        }

        private async Task PayCashAsync()
        {
            if (!Cart.Any()) return;
            var sale = await _session.PayCashAsync();
            SyncCartFromSession();
            RaisePropertyChanged(nameof(Total));
            PayCashCommand.RaiseCanExecuteChanged();
            MessageBox.Show($"Vendita salvata: {sale.Code}");
        }

        private void SyncCartFromSession()
        {
            Cart.Clear();
            foreach (var x in _session.Lines)
            {
                Cart.Add(new CartLineVm
                {
                    ProductId = x.ProductId,
                    Barcode = x.Barcode,
                    Name = x.Name,
                    Quantity = x.Quantity,
                    UnitPrice = x.UnitPrice
                });
            }
        }
    }
}
