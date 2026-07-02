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
            var purchase = SupplierImportAnalyzer.ParseNumber(purchasePrice);
            if (!purchase.HasValue || purchase.Value <= 0) return null;

            var markedUp = purchase.Value * (1.0 + (markupPercent / 100.0));
            var step = roundTo <= 0 ? 1 : roundTo;
            var rounded = Math.Round(markedUp / step, MidpointRounding.AwayFromZero) * step;
            return Convert.ToInt64(Math.Round(rounded, MidpointRounding.AwayFromZero));
        }
    }
}
