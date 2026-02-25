using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Win7POS.Core.Models;
using Win7POS.Core.Util;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Wpf.ViewModels
{
    public sealed class PosViewModel : ObservableObject
    {
        private readonly PosDbOptions _opt;
        private readonly SqliteConnectionFactory _factory;
        private readonly ProductRepository _products;
        private readonly SaleRepository _sales;

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
            _opt = PosDbOptions.Default();
            DbInitializer.EnsureCreated(_opt);

            _factory = new SqliteConnectionFactory(_opt);
            _products = new ProductRepository(_factory);
            _sales = new SaleRepository(_factory);

            Cart.CollectionChanged += (_, __) => RaisePropertyChanged(nameof(Total));

            PayCashCommand = new RelayCommand(async () => await PayCashAsync(), () => Cart.Any());
        }

        public async Task OnBarcodeEnterAsync()
        {
            var code = (BarcodeInput ?? "").Trim();
            if (code.Length == 0) return;

            var p = await _products.GetByBarcodeAsync(code);
            if (p == null)
            {
                MessageBox.Show($"Prodotto non trovato: {code}");
                BarcodeInput = "";
                return;
            }

            var existing = Cart.FirstOrDefault(x => x.Barcode == p.Barcode);
            if (existing != null) existing.Quantity += 1;
            else
                Cart.Add(new CartLineVm
                {
                    ProductId = p.Id,
                    Barcode = p.Barcode,
                    Name = p.Name,
                    UnitPrice = p.UnitPrice,
                    Quantity = 1
                });

            RaisePropertyChanged(nameof(Total));
            PayCashCommand.RaiseCanExecuteChanged();
            BarcodeInput = "";
        }

        private async Task PayCashAsync()
        {
            if (!Cart.Any()) return;

            var total = Total;
            var sale = new Sale
            {
                Code = SaleCodeGenerator.NewCode("V"),
                CreatedAt = UnixTime.NowMs(),
                Total = total,
                PaidCash = total,
                PaidCard = 0,
                Change = 0
            };

            var lines = Cart.Select(x => new SaleLine
            {
                ProductId = x.ProductId,
                Barcode = x.Barcode,
                Name = x.Name,
                Quantity = x.Quantity,
                UnitPrice = x.UnitPrice
            }).ToList();

            await _sales.InsertSaleAsync(sale, lines);

            Cart.Clear();
            RaisePropertyChanged(nameof(Total));
            PayCashCommand.RaiseCanExecuteChanged();
            MessageBox.Show($"Vendita salvata: {sale.Code}");
        }
    }
}
