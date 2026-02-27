using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Win7POS.Core.Models;
using Win7POS.Core.Util;

namespace Win7POS.Core.Pos
{
    public sealed class PosSession
    {
        private const int MaxQuantity = 100000;
        private readonly IProductLookup _productLookup;
        private readonly ISalesStore _salesStore;
        private readonly List<PosLine> _lines = new List<PosLine>();

        public PosSession(IProductLookup productLookup, ISalesStore salesStore)
        {
            _productLookup = productLookup ?? throw new ArgumentNullException(nameof(productLookup));
            _salesStore = salesStore ?? throw new ArgumentNullException(nameof(salesStore));
        }

        public IReadOnlyList<PosLine> Lines => _lines;
        public int Total => _lines.Sum(x => x.LineTotal);

        public async Task AddByBarcodeAsync(string barcode)
        {
            var code = (barcode ?? "").Trim();
            if (code.Length == 0) return;

            var product = await _productLookup.GetByBarcodeAsync(code);
            if (product == null) throw new PosException(PosErrorCode.ProductNotFound, code);

            var existing = _lines.FirstOrDefault(x => x.Barcode == product.Barcode);
            if (existing != null)
            {
                existing.Quantity += 1;
                return;
            }

            _lines.Add(new PosLine
            {
                ProductId = product.Id,
                Barcode = product.Barcode,
                Name = product.Name,
                UnitPrice = product.UnitPrice,
                Quantity = 1
            });
        }

        public void SetQuantity(string barcode, int quantity)
        {
            var code = (barcode ?? "").Trim();
            if (code.Length == 0) throw new PosException(PosErrorCode.InvalidBarcode);
            if (quantity < 0 || quantity > MaxQuantity) throw new PosException(PosErrorCode.InvalidQuantity, quantity.ToString());

            var line = _lines.FirstOrDefault(x => x.Barcode == code);
            if (line == null) throw new PosException(PosErrorCode.ProductNotFound, code);

            if (quantity == 0)
            {
                _lines.Remove(line);
                return;
            }

            line.Quantity = quantity;
        }

        public void RemoveLine(string barcode) => SetQuantity(barcode, 0);

        public void Clear() => _lines.Clear();

        public async Task<SaleCompleted> PayCashAsync()
        {
            if (_lines.Count == 0) throw new PosException(PosErrorCode.EmptyCart);

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

            var saleLines = _lines.Select(x => new SaleLine
            {
                ProductId = x.ProductId,
                Barcode = x.Barcode,
                Name = x.Name,
                Quantity = x.Quantity,
                UnitPrice = x.UnitPrice,
                LineTotal = x.LineTotal
            }).ToList();

            await _salesStore.InsertSaleAsync(sale, saleLines);
            Clear();

            return new SaleCompleted(sale, saleLines);
        }
    }
}
