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
        public long Total => _lines.Sum(x => x.LineTotal);

        public async Task AddByBarcodeAsync(string barcode)
        {
            var code = (barcode ?? "").Trim();
            if (code.Length == 0) return;

            var product = await _productLookup.GetByBarcodeAsync(code);
            if (product == null) throw new PosException(PosErrorCode.ProductNotFound, code);

            var existing = _lines.FirstOrDefault(x => x.Barcode == product.Barcode && !IsDiscount(x.Barcode));
            if (existing != null)
            {
                existing.Quantity += 1;
                RecalcDiscounts();
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

            var line = _lines.FirstOrDefault(x => x.Barcode == code && !IsDiscount(x.Barcode));
            if (line == null) throw new PosException(PosErrorCode.ProductNotFound, code);

            if (quantity == 0)
            {
                _lines.Remove(line);
                ClearDiscountForLine(code);
                RecalcDiscounts();
                return;
            }

            line.Quantity = quantity;
            RecalcDiscounts();
        }

        public void RemoveLine(string barcode)
        {
            var code = (barcode ?? "").Trim();
            if (code.Length == 0) throw new PosException(PosErrorCode.InvalidBarcode);

            if (IsDiscount(code))
            {
                var line = _lines.FirstOrDefault(x => x.Barcode == code);
                if (line != null)
                {
                    _lines.Remove(line);
                    RecalcDiscounts();
                }
                return;
            }

            SetQuantity(barcode, 0);
        }

        internal static bool IsDiscount(string barcode)
        {
            if (string.IsNullOrEmpty(barcode)) return false;
            return barcode.StartsWith("DISC:", StringComparison.Ordinal);
        }

        private void SetOrReplaceLine(string pseudo, string name, long unitPrice, int qty)
        {
            var existing = _lines.FirstOrDefault(x => x.Barcode == pseudo);
            if (existing != null)
            {
                if (qty <= 0)
                {
                    _lines.Remove(existing);
                }
                else
                {
                    existing.UnitPrice = unitPrice;
                    existing.Quantity = qty;
                }
            }
            else if (qty > 0)
            {
                _lines.Add(new PosLine
                {
                    ProductId = null,
                    Barcode = pseudo,
                    Name = name,
                    UnitPrice = unitPrice,
                    Quantity = qty
                });
            }
        }

        public void ApplyCartDiscountPercent(int percent)
        {
            if (percent <= 0 || percent > 100) return;
            var baseTotal = _lines.Where(l => !IsDiscount(l.Barcode)).Sum(l => l.LineTotal);
            if (baseTotal <= 0) return;
            var disc = (long)Math.Round(baseTotal * (percent / 100.0), MidpointRounding.AwayFromZero);
            if (disc <= 0) return;
            SetOrReplaceLine("DISC:CART:PCT:" + percent, "Sconto carrello " + percent + "%", -disc, 1);
        }

        public void ApplyLineDiscountPercent(string lineKey, int percent)
        {
            if (percent <= 0 || percent > 100) return;
            var line = _lines.FirstOrDefault(l => l.Barcode == lineKey && !IsDiscount(l.Barcode));
            if (line == null) return;
            var lineTotal = line.LineTotal;
            var disc = (long)Math.Round(lineTotal * (percent / 100.0), MidpointRounding.AwayFromZero);
            if (disc <= 0) return;
            SetOrReplaceLine("DISC:LINE:" + lineKey + ":PCT:" + percent, "Sconto " + percent + "%", -disc, 1);
        }

        public void ApplyLineDiscountAmount(string lineKey, long amountMinor)
        {
            if (amountMinor <= 0) return;
            var line = _lines.FirstOrDefault(l => l.Barcode == lineKey && !IsDiscount(l.Barcode));
            if (line == null) return;
            var lineTotal = line.LineTotal;
            var disc = Math.Min(amountMinor, lineTotal);
            if (disc <= 0) return;
            SetOrReplaceLine("DISC:LINE:" + lineKey, "Sconto riga", -disc, 1);
        }

        public void ClearDiscountForLine(string lineKey)
        {
            var toRemove = _lines.Where(l => IsDiscount(l.Barcode) &&
                (l.Barcode == "DISC:LINE:" + lineKey || l.Barcode.StartsWith("DISC:LINE:" + lineKey + ":", StringComparison.Ordinal))).ToList();
            foreach (var l in toRemove) _lines.Remove(l);
        }

        public void ClearCartDiscount()
        {
            var toRemove = _lines.Where(l => IsDiscount(l.Barcode) && l.Barcode.StartsWith("DISC:CART", StringComparison.Ordinal)).ToList();
            foreach (var l in toRemove) _lines.Remove(l);
        }

        public void RecalcDiscounts()
        {
            var cartDisc = _lines.FirstOrDefault(l => l.Barcode.StartsWith("DISC:CART:PCT:", StringComparison.Ordinal));
            if (cartDisc != null)
            {
                var parts = cartDisc.Barcode.Split(':');
                if (parts.Length >= 4 && int.TryParse(parts[3], out var pct))
                {
                    var baseTotal = _lines.Where(l => !IsDiscount(l.Barcode)).Sum(l => l.LineTotal);
                    var disc = (long)Math.Round(baseTotal * (pct / 100.0), MidpointRounding.AwayFromZero);
                    cartDisc.UnitPrice = disc > 0 ? -disc : 0L;
                }
            }

            var linePctDiscs = _lines.Where(l => l.Barcode.StartsWith("DISC:LINE:", StringComparison.Ordinal) && l.Barcode.Contains(":PCT:")).ToList();
            foreach (var discLine in linePctDiscs)
            {
                var b = discLine.Barcode;
                var idx = b.IndexOf(":PCT:", StringComparison.Ordinal);
                if (idx < 0) continue;
                var key = b.Substring("DISC:LINE:".Length, idx - "DISC:LINE:".Length);
                var pctStr = b.Substring(idx + 5);
                if (!int.TryParse(pctStr, out var pct)) continue;
                var line = _lines.FirstOrDefault(l => l.Barcode == key && !IsDiscount(l.Barcode));
                if (line == null) continue;
                var lineTotal = line.LineTotal;
                var disc = (long)Math.Round(lineTotal * (pct / 100.0), MidpointRounding.AwayFromZero);
                discLine.UnitPrice = disc > 0 ? -disc : 0L;
            }
        }

        public Task AddManualPriceAsync(long unitPriceMinor, string name = null)
        {
            if (unitPriceMinor <= 0)
                throw new PosException(PosErrorCode.InvalidPrice, unitPriceMinor.ToString());

            var pseudo = $"MANUAL:{unitPriceMinor}";
            var existing = _lines.FirstOrDefault(x => x.Barcode == pseudo);
            if (existing != null)
            {
                existing.Quantity += 1;
                return Task.CompletedTask;
            }

            _lines.Add(new PosLine
            {
                ProductId = null,
                Barcode = pseudo,
                Name = name ?? "Senza codice",
                UnitPrice = unitPriceMinor,
                Quantity = 1
            });
            return Task.CompletedTask;
        }

        public void Clear() => _lines.Clear();

        /// <summary>Sostituisce il carrello con le righe restaurate (per Recupera sospeso).</summary>
        public void ReplaceWithLines(IReadOnlyList<RestoredLine> lines)
        {
            _lines.Clear();
            if (lines == null) return;
            foreach (var l in lines)
            {
                if (l == null || l.Quantity <= 0) continue;
                _lines.Add(new PosLine
                {
                    ProductId = l.ProductId,
                    Barcode = l.Barcode ?? string.Empty,
                    Name = l.Name ?? string.Empty,
                    UnitPrice = l.UnitPrice,
                    Quantity = l.Quantity
                });
            }
        }

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

            var saleId = await _salesStore.InsertSaleAsync(sale, saleLines);
            sale.Id = saleId;
            Clear();

            return new SaleCompleted(sale, saleLines);
        }
    }

    public sealed class RestoredLine
    {
        public long? ProductId { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public long UnitPrice { get; set; }
        public int Quantity { get; set; }
    }
}
