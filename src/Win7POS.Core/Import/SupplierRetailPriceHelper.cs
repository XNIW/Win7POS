using System;
using System.Collections.Generic;

namespace Win7POS.Core.Import
{
    public static class SupplierRetailPriceHelper
    {
        public static int ApplyMarkupToRetailPriceRows(
            IEnumerable<SupplierImportEditableRow> rows,
            double markupPercent,
            int roundTo,
            bool applyOnlyEmptyRetailPrice)
        {
            var changed = 0;
            foreach (var row in rows ?? Array.Empty<SupplierImportEditableRow>())
            {
                if (row == null) continue;
                if (applyOnlyEmptyRetailPrice && !string.IsNullOrWhiteSpace(row.RetailPrice)) continue;

                var retail = CalculateRetailPrice(row.PurchasePrice, markupPercent, roundTo);
                if (!retail.HasValue) continue;

                row.RetailPrice = retail.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                changed++;
            }
            return changed;
        }

        public static long? CalculateRetailPrice(string purchasePrice, double markupPercent, int roundTo)
        {
            if (double.IsNaN(markupPercent) || double.IsInfinity(markupPercent) ||
                !SupplierImportAnalyzer.TryParsePrice(purchasePrice, true, out _)) return null;
            var purchase = SupplierImportAnalyzer.ParseNumber(purchasePrice);
            if (!purchase.HasValue || purchase.Value <= 0) return null;

            var markedUp = purchase.Value * (1.0 + (markupPercent / 100.0));
            if (double.IsNaN(markedUp) || double.IsInfinity(markedUp) || markedUp < 0) return null;
            var step = roundTo <= 0 ? 1 : roundTo;
            var rounded = Math.Round(markedUp / step, MidpointRounding.AwayFromZero) * step;
            // Int64.MaxValue rounds to 2^63 as a Double; exclude that boundary
            // explicitly before Convert.ToInt64 instead of allowing overflow.
            if (double.IsNaN(rounded) || double.IsInfinity(rounded) || rounded < 0 || rounded >= 9223372036854775808d)
                return null;
            return Convert.ToInt64(Math.Round(rounded, MidpointRounding.AwayFromZero));
        }
    }
}
