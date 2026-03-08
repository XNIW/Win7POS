using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Win7POS.Core.Models;
using Win7POS.Core.Util;

namespace Win7POS.Core.Pos
{
    /// <summary>Prefissi e helper per chiavi sconto (DISC:…); evita stringhe hardcoded.</summary>
    public static class DiscountKeys
    {
        public const string Prefix = "DISC:";
        public const string CartPrefix = "DISC:CART:";
        public const string LinePrefix = "DISC:LINE:";
        public const string CartPctPrefix = "DISC:CART:PCT:";
        public const string ManualPrefix = "MANUAL:";

        public static bool IsDiscount(string barcode)
        {
            return !string.IsNullOrEmpty(barcode) && barcode.StartsWith(Prefix, StringComparison.Ordinal);
        }

        public static bool IsReservedPrefix(string barcode)
        {
            return !string.IsNullOrEmpty(barcode) &&
                (barcode.StartsWith(Prefix, StringComparison.Ordinal) || barcode.StartsWith(ManualPrefix, StringComparison.Ordinal));
        }

        public static string BuildCartPct(int percent) => CartPctPrefix + percent;

        public static string BuildLine(string lineKey) => LinePrefix + (lineKey ?? string.Empty);

        public static string BuildLinePct(string lineKey, int percent) => LinePrefix + (lineKey ?? string.Empty) + ":PCT:" + percent;

        public static bool IsCartDiscount(string barcode) => !string.IsNullOrEmpty(barcode) && barcode.StartsWith(CartPrefix, StringComparison.Ordinal);

        public static bool IsLineDiscount(string barcode) => !string.IsNullOrEmpty(barcode) && barcode.StartsWith(LinePrefix, StringComparison.Ordinal);

        /// <summary>Ritorna il percentuale da barcode tipo DISC:CART:PCT:10, oppure null.</summary>
        public static int? ParseCartPct(string barcode)
        {
            if (string.IsNullOrEmpty(barcode) || !barcode.StartsWith(CartPctPrefix, StringComparison.Ordinal)) return null;
            var suffix = barcode.Substring(CartPctPrefix.Length);
            return int.TryParse(suffix, out var pct) ? (int?)pct : null;
        }

        /// <summary>Per barcode tipo DISC:LINE:KEY:PCT:10 ritorna (KEY, 10). Altrimenti (null, null).</summary>
        public static (string lineKey, int? percent) ParseLinePct(string barcode)
        {
            if (string.IsNullOrEmpty(barcode) || !barcode.StartsWith(LinePrefix, StringComparison.Ordinal)) return (null, null);
            var idx = barcode.IndexOf(":PCT:", StringComparison.Ordinal);
            if (idx < 0) return (null, null);
            var key = barcode.Substring(LinePrefix.Length, idx - LinePrefix.Length);
            var pctStr = barcode.Substring(idx + 5);
            return int.TryParse(pctStr, out var pct) ? (key, (int?)pct) : (key, null);
        }

        /// <summary>True se il barcode è uno sconto riga per la data lineKey (esatto o con :PCT:).</summary>
        public static bool IsLineDiscountFor(string barcode, string lineKey)
        {
            if (string.IsNullOrEmpty(barcode) || string.IsNullOrEmpty(lineKey)) return false;
            return barcode == BuildLine(lineKey) || (barcode.StartsWith(LinePrefix + lineKey + ":", StringComparison.Ordinal));
        }
    }

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
            if (DiscountKeys.IsReservedPrefix(code))
                throw new PosException(PosErrorCode.InvalidBarcode, code);

            var product = await _productLookup.GetByBarcodeAsync(code);
            if (product == null) throw new PosException(PosErrorCode.ProductNotFound, code);

            var existing = _lines.FirstOrDefault(x => x.Barcode == product.Barcode && !DiscountKeys.IsDiscount(x.Barcode));
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

            var line = _lines.FirstOrDefault(x => x.Barcode == code && !DiscountKeys.IsDiscount(x.Barcode));
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

        /// <summary>Imposta il prezzo unitario di una riga (es. dopo modifica prodotto). Ricalcola sconti.</summary>
        public void SetLineUnitPrice(string barcode, long unitPriceMinor)
        {
            var code = (barcode ?? "").Trim();
            if (code.Length == 0) return;
            if (DiscountKeys.IsDiscount(code)) return;

            var line = _lines.FirstOrDefault(x => x.Barcode == code);
            if (line == null) return;
            line.UnitPrice = unitPriceMinor;
            RecalcDiscounts();
        }

        /// <summary>Imposta il nome di una riga (es. dopo modifica prodotto).</summary>
        public void SetLineName(string barcode, string name)
        {
            var code = (barcode ?? "").Trim();
            if (code.Length == 0) return;
            if (DiscountKeys.IsDiscount(code)) return;

            var line = _lines.FirstOrDefault(x => x.Barcode == code);
            if (line == null) return;
            line.Name = name ?? string.Empty;
        }

        public void RemoveLine(string barcode)
        {
            var code = (barcode ?? "").Trim();
            if (code.Length == 0) throw new PosException(PosErrorCode.InvalidBarcode);

            if (DiscountKeys.IsDiscount(code))
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
            var baseTotal = _lines.Where(l => !DiscountKeys.IsDiscount(l.Barcode)).Sum(l => l.LineTotal);
            if (baseTotal <= 0) return;
            var disc = (long)Math.Round(baseTotal * (percent / 100.0), MidpointRounding.AwayFromZero);
            if (disc <= 0) return;
            SetOrReplaceLine(DiscountKeys.BuildCartPct(percent), "Sconto carrello " + percent + "%", -disc, 1);
        }

        public void ApplyLineDiscountPercent(string lineKey, int percent)
        {
            if (percent <= 0 || percent > 100) return;
            var line = _lines.FirstOrDefault(l => l.Barcode == lineKey && !DiscountKeys.IsDiscount(l.Barcode));
            if (line == null) return;
            var lineTotal = line.LineTotal;
            var disc = (long)Math.Round(lineTotal * (percent / 100.0), MidpointRounding.AwayFromZero);
            if (disc <= 0) return;
            SetOrReplaceLine(DiscountKeys.BuildLinePct(lineKey, percent), "Sconto " + percent + "%", -disc, 1);
        }

        public void ApplyLineDiscountAmount(string lineKey, long amountMinor)
        {
            if (amountMinor <= 0) return;
            var line = _lines.FirstOrDefault(l => l.Barcode == lineKey && !DiscountKeys.IsDiscount(l.Barcode));
            if (line == null) return;
            var lineTotal = line.LineTotal;
            var disc = Math.Min(amountMinor, lineTotal);
            if (disc <= 0) return;
            SetOrReplaceLine(DiscountKeys.BuildLine(lineKey), "Sconto riga", -disc, 1);
        }

        public void ClearDiscountForLine(string lineKey)
        {
            var toRemove = _lines.Where(l => DiscountKeys.IsDiscount(l.Barcode) && DiscountKeys.IsLineDiscountFor(l.Barcode, lineKey)).ToList();
            foreach (var l in toRemove) _lines.Remove(l);
        }

        public void ClearCartDiscount()
        {
            var toRemove = _lines.Where(l => DiscountKeys.IsCartDiscount(l.Barcode)).ToList();
            foreach (var l in toRemove) _lines.Remove(l);
        }

        public void RecalcDiscounts()
        {
            var cartDisc = _lines.FirstOrDefault(l => DiscountKeys.ParseCartPct(l.Barcode).HasValue);
            if (cartDisc != null && DiscountKeys.ParseCartPct(cartDisc.Barcode) is int pct)
            {
                var baseTotal = _lines.Where(l => !DiscountKeys.IsDiscount(l.Barcode)).Sum(l => l.LineTotal);
                var disc = (long)Math.Round(baseTotal * (pct / 100.0), MidpointRounding.AwayFromZero);
                cartDisc.UnitPrice = disc > 0 ? -disc : 0L;
            }

            var linePctDiscs = _lines.Where(l => DiscountKeys.ParseLinePct(l.Barcode).percent.HasValue).ToList();
            foreach (var discLine in linePctDiscs)
            {
                var (key, pctVal) = DiscountKeys.ParseLinePct(discLine.Barcode);
                if (key == null || !pctVal.HasValue) continue;
                var line = _lines.FirstOrDefault(l => l.Barcode == key && !DiscountKeys.IsDiscount(l.Barcode));
                if (line == null) continue;
                var lineTotal = line.LineTotal;
                var disc = (long)Math.Round(lineTotal * (pctVal.Value / 100.0), MidpointRounding.AwayFromZero);
                discLine.UnitPrice = disc > 0 ? -disc : 0L;
            }
        }

        public Task AddManualPriceAsync(long unitPriceMinor, string name = null)
        {
            if (unitPriceMinor <= 0)
                throw new PosException(PosErrorCode.InvalidPrice, unitPriceMinor.ToString());

            var pseudo = DiscountKeys.ManualPrefix + unitPriceMinor;
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
