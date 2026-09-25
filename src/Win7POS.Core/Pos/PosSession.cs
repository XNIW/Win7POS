using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
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
        public const string TaxPrefix = "TAX:";
        public const string ManualPrefix = "MANUAL:";

        public static bool IsDiscount(string barcode)
        {
            return !string.IsNullOrEmpty(barcode) && barcode.StartsWith(Prefix, StringComparison.Ordinal);
        }

        public static bool IsReservedPrefix(string barcode)
        {
            return !string.IsNullOrEmpty(barcode) &&
                (barcode.StartsWith(Prefix, StringComparison.Ordinal) ||
                 barcode.StartsWith(TaxPrefix, StringComparison.Ordinal) ||
                 barcode.StartsWith(ManualPrefix, StringComparison.Ordinal));
        }

        public static bool IsTax(string barcode)
        {
            return !string.IsNullOrEmpty(barcode) && barcode.StartsWith(TaxPrefix, StringComparison.Ordinal);
        }

        public static bool IsEconomicAdjustment(string barcode) => IsDiscount(barcode) || IsTax(barcode);

        public static string BuildCartPct(int percent) => CartPctPrefix + percent;

        public static string BuildLine(string lineKey) => LinePrefix + (lineKey ?? string.Empty);

        public static string BuildLinePct(string lineKey, int percent) => LinePrefix + (lineKey ?? string.Empty) + ":PCT:" + percent;
        public static string BuildLineFinal(string lineKey, long price) => LinePrefix + lineKey + ":FINAL:" + price.ToString(CultureInfo.InvariantCulture);

        public static (string lineKey, long? finalPrice) ParseLineFinal(string barcode)
        {
            if (!IsLineDiscount(barcode)) return (null, null);
            var index = barcode.LastIndexOf(":FINAL:", StringComparison.Ordinal);
            if (index < LinePrefix.Length) return (null, null);
            return long.TryParse(barcode.Substring(index + 7), NumberStyles.None, CultureInfo.InvariantCulture, out var price)
                ? (barcode.Substring(LinePrefix.Length, index - LinePrefix.Length), (long?)price) : (null, null);
        }

        public static string LineDiscountTarget(string barcode)
        {
            if (!IsLineDiscount(barcode)) return null;
            return ParseLinePct(barcode).lineKey ?? ParseLineFinal(barcode).lineKey ?? barcode.Substring(LinePrefix.Length);
        }

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
            var idx = barcode.LastIndexOf(":PCT:", StringComparison.Ordinal);
            if (idx < 0) return (null, null);
            var key = barcode.Substring(LinePrefix.Length, idx - LinePrefix.Length);
            var pctStr = barcode.Substring(idx + 5);
            return int.TryParse(pctStr, out var pct) ? (key, (int?)pct) : (key, null);
        }

        /// <summary>True se il barcode è uno sconto riga per la data lineKey (esatto o con :PCT:).</summary>
        public static bool IsLineDiscountFor(string barcode, string lineKey)
        {
            if (string.IsNullOrEmpty(barcode) || string.IsNullOrEmpty(lineKey)) return false;
            return string.Equals(LineDiscountTarget(barcode), lineKey, StringComparison.Ordinal);
        }
    }

    public sealed class PosSession
    {
        private const int MaxQuantity = 100000;
        private readonly IProductLookup _productLookup;
        private readonly ISalesStore _salesStore;
        private readonly List<PosLine> _lines = new List<PosLine>();

        public PosSession(IProductLookup productLookup)
        {
            _productLookup = productLookup ??
                throw new ArgumentNullException(nameof(productLookup));
        }

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
                SetQuantity(existing.Barcode, checked(existing.Quantity + 1));
                return;
            }

            ValidateItemChange(null, product.UnitPrice, 1);
            _lines.Add(new PosLine
            {
                ProductId = product.Id,
                Barcode = product.Barcode,
                Name = product.Name,
                UnitPrice = product.UnitPrice,
                Quantity = 1
            });
            RecalcDiscounts();
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

            ValidateItemChange(line, line.UnitPrice, quantity);
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
            ValidateItemChange(line, unitPriceMinor, line.Quantity);
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
            if (percent < 0 || percent > 100) return;
            ClearCartDiscount();
            if (percent <= 0) return;
            var baseTotal = _lines.Where(l => !DiscountKeys.IsDiscount(l.Barcode)).Sum(l => l.LineTotal);
            if (baseTotal <= 0) return;
            var disc = PercentAmount(baseTotal, percent);
            SetOrReplaceLine(DiscountKeys.BuildCartPct(percent), "Sconto carrello " + percent + "%", -disc, 1);
            RecalcDiscounts();
        }

        /// <summary>lineKey = Barcode della riga prodotto (session garantisce una sola riga per barcode).</summary>
        public void ApplyLineDiscountPercent(string lineKey, int percent)
        {
            if (percent < 0 || percent > 100) return;
            var line = _lines.FirstOrDefault(l => l.Barcode == lineKey && !DiscountKeys.IsDiscount(l.Barcode));
            if (line == null) return;
            ClearDiscountForLine(lineKey);
            if (percent == 0)
                return;
            var lineTotal = line.LineTotal;
            var disc = PercentAmount(lineTotal, percent);
            SetOrReplaceLine(DiscountKeys.BuildLinePct(lineKey, percent), "Sconto " + percent + "%", -disc, 1);
            RecalcDiscounts();
        }

        public void ApplyLineDiscountAmount(string lineKey, long amountMinor)
        {
            var line = _lines.FirstOrDefault(l => l.Barcode == lineKey && !DiscountKeys.IsDiscount(l.Barcode));
            if (line == null) return;
            ClearDiscountForLine(lineKey);
            if (amountMinor <= 0) return;
            var lineTotal = line.LineTotal;
            var disc = Math.Min(amountMinor, lineTotal);
            if (disc <= 0) return;
            SetOrReplaceLine(DiscountKeys.BuildLine(lineKey), "Sconto riga", -disc, 1);
            RecalcDiscounts();
        }

        /// <summary>Prezzo unitario originale della riga prodotto identificata da lineKey (Barcode). Session: una sola riga per barcode.</summary>
        private long GetOriginalUnitPrice(string lineKey)
        {
            var line = _lines.FirstOrDefault(l => l.Barcode == lineKey && !DiscountKeys.IsDiscount(l.Barcode));
            return line?.UnitPrice ?? 0L;
        }

        /// <summary>Applica sconto riga per prezzo finale unitario desiderato. Usa importo esatto (BuildLine) per evitare arrotondamenti diversi da preview.</summary>
        public void ApplyLineDiscountByFinalUnitPrice(string lineKey, long finalUnitPriceMinor)
        {
            var original = GetOriginalUnitPrice(lineKey);
            if (original <= 0) return;
            var clamped = Math.Max(0L, Math.Min(finalUnitPriceMinor, original));
            ClearDiscountForLine(lineKey);
            if (clamped >= original)
                return;
            var line = _lines.FirstOrDefault(l => l.Barcode == lineKey && !DiscountKeys.IsDiscount(l.Barcode));
            if (line == null) return;
            var discountUnit = original - clamped;
            var disc = checked(discountUnit * line.Quantity);
            SetOrReplaceLine(DiscountKeys.BuildLineFinal(lineKey, clamped), "Sconto riga", -disc, 1);
            RecalcDiscounts();
        }

        /// <summary>Rimuove tutte le righe sconto associate a questa riga prodotto (lineKey = Barcode). Garantisce rimozione completa.</summary>
        public void ClearDiscountForLine(string lineKey)
        {
            var toRemove = _lines.Where(l => DiscountKeys.IsDiscount(l.Barcode) && DiscountKeys.IsLineDiscountFor(l.Barcode, lineKey)).ToList();
            foreach (var l in toRemove) _lines.Remove(l);
            RecalcDiscounts();
        }

        public void ClearCartDiscount()
        {
            var toRemove = _lines.Where(l => DiscountKeys.IsCartDiscount(l.Barcode)).ToList();
            foreach (var l in toRemove) _lines.Remove(l);
            RecalcDiscounts();
        }

        public void RecalcDiscounts()
        {
            var items = _lines.Where(l => !DiscountKeys.IsDiscount(l.Barcode)).ToDictionary(l => l.Barcode, StringComparer.Ordinal);
            if (items.Count == 0) { _lines.Clear(); return; }
            var gross = items.Values.Sum(l => l.LineTotal);
            long lineDiscount = 0;
            foreach (var adjustment in _lines.Where(l => DiscountKeys.IsLineDiscount(l.Barcode)).ToList())
            {
                var key = DiscountKeys.LineDiscountTarget(adjustment.Barcode);
                if (!items.TryGetValue(key, out var item)) { _lines.Remove(adjustment); continue; }
                var percent = DiscountKeys.ParseLinePct(adjustment.Barcode).percent;
                var finalPrice = DiscountKeys.ParseLineFinal(adjustment.Barcode).finalPrice;
                var amount = percent.HasValue ? PercentAmount(item.LineTotal, percent.Value)
                    : finalPrice.HasValue ? checked(Math.Max(0, item.UnitPrice - finalPrice.Value) * item.Quantity)
                    : checked(-adjustment.LineTotal);
                amount = Math.Max(0, Math.Min(amount, item.LineTotal));
                adjustment.UnitPrice = -amount;
                adjustment.Quantity = 1;
                lineDiscount = checked(lineDiscount + amount);
            }
            // Retain the existing additive, gross-based percentages; cap the cart
            // adjustment at the remaining payable amount instead of creating a negative sale.
            var remaining = checked(gross - lineDiscount);
            foreach (var adjustment in _lines.Where(l => DiscountKeys.IsCartDiscount(l.Barcode)))
            {
                var pct = DiscountKeys.ParseCartPct(adjustment.Barcode);
                var amount = pct.HasValue ? PercentAmount(gross, pct.Value) : checked(-adjustment.LineTotal);
                amount = Math.Max(0, Math.Min(amount, remaining));
                adjustment.UnitPrice = -amount;
                adjustment.Quantity = 1;
                remaining -= amount;
            }
        }

        public static long PercentAmount(long amount, int percent)
        {
            if (amount < 0 || percent < 0 || percent > 100) throw new ArgumentOutOfRangeException(nameof(percent));
            return checked((long)Math.Round(amount * (decimal)percent / 100m, 0, MidpointRounding.AwayFromZero));
        }

        private void ValidateItemChange(PosLine replacing, long price, int quantity)
        {
            if (price < 0) throw new PosException(PosErrorCode.InvalidPrice);
            if (quantity <= 0 || quantity > MaxQuantity) throw new PosException(PosErrorCode.InvalidQuantity);
            var total = checked(price * quantity);
            foreach (var line in _lines)
                if (!ReferenceEquals(line, replacing) && !DiscountKeys.IsDiscount(line.Barcode))
                    total = checked(total + line.LineTotal);
        }

        public Task AddManualPriceAsync(long unitPriceMinor, string name = null)
        {
            if (unitPriceMinor <= 0)
                throw new PosException(PosErrorCode.InvalidPrice, unitPriceMinor.ToString());

            var pseudo = DiscountKeys.ManualPrefix + unitPriceMinor;
            var existing = _lines.FirstOrDefault(x => x.Barcode == pseudo);
            if (existing != null)
            {
                SetQuantity(existing.Barcode, checked(existing.Quantity + 1));
                return Task.CompletedTask;
            }

            ValidateItemChange(null, unitPriceMinor, 1);
            _lines.Add(new PosLine
            {
                ProductId = null,
                Barcode = pseudo,
                Name = name ?? "Senza codice",
                UnitPrice = unitPriceMinor,
                Quantity = 1
            });
            RecalcDiscounts();
            return Task.CompletedTask;
        }

        public void Clear() => _lines.Clear();

        /// <summary>Sostituisce il carrello con le righe restaurate (per Recupera sospeso).</summary>
        public void ReplaceWithLines(IReadOnlyList<RestoredLine> lines)
        {
            if (lines == null) { Clear(); return; }
            var candidate = new PosSession(_productLookup);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var discountTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var l in lines)
            {
                if (l == null || string.IsNullOrWhiteSpace(l.Barcode) || !seen.Add(l.Barcode))
                    throw new PosException(PosErrorCode.InvalidBarcode);
                if (DiscountKeys.IsDiscount(l.Barcode))
                {
                    if (l.Quantity != 1 || l.UnitPrice > 0 || l.UnitPrice == long.MinValue)
                        throw new PosException(PosErrorCode.InvalidPrice);
                    var target = DiscountKeys.IsCartDiscount(l.Barcode) ? DiscountKeys.CartPrefix : DiscountKeys.LineDiscountTarget(l.Barcode);
                    if (target == null || !discountTargets.Add(target)) throw new PosException(PosErrorCode.InvalidBarcode);
                }
                else candidate.ValidateItemChange(null, l.UnitPrice, l.Quantity);
                candidate._lines.Add(new PosLine
                {
                    ProductId = l.ProductId,
                    Barcode = l.Barcode ?? string.Empty,
                    Name = l.Name ?? string.Empty,
                    UnitPrice = l.UnitPrice,
                    Quantity = l.Quantity
                });
            }
            candidate.RecalcDiscounts();
            _lines.Clear();
            _lines.AddRange(candidate._lines);
        }

        public async Task<SaleCompleted> PayCashAsync()
        {
            if (_salesStore == null)
            {
                throw new InvalidOperationException(
                    "Legacy sale persistence is not configured.");
            }
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
