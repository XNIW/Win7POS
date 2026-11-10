using System;
using System.Collections.Generic;
using Win7POS.Core.Models;
using Win7POS.Core.Receipt;

namespace Win7POS.Core.Pos
{
    public static class OrdinarySaleEconomicsPolicy
    {
        public static void EnsureValid(Sale sale, IReadOnlyList<SaleLine> lines)
        {
            if (sale == null || lines == null || lines.Count == 0 || lines.Count > SalesReceiptContentPolicy.MaxSaleLines)
                throw new InvalidOperationException("sale_lines_invalid");
            try
            {
                long total = 0;
                foreach (var line in lines)
                {
                    if (line == null || line.Quantity <= 0 || line.Quantity > 100000)
                        throw new InvalidOperationException("sale_quantity_invalid");
                    if (DiscountKeys.IsEconomicAdjustment(line.Barcode))
                    {
                        if (line.Quantity != 1 || (DiscountKeys.IsDiscount(line.Barcode) ? line.UnitPrice > 0 : line.UnitPrice < 0))
                            throw new InvalidOperationException("sale_adjustment_invalid");
                    }
                    else if (line.UnitPrice < 0) throw new InvalidOperationException("sale_price_invalid");
                    total = checked(total + checked(line.UnitPrice * line.Quantity));
                }
                if (total < 0 || sale.Total != total) throw new InvalidOperationException("sale_total_invalid");
                if (sale.PaidCash < 0 || sale.PaidCard < 0 || sale.PaidCard > Math.Max(0, total - sale.PaidCash) ||
                    checked(sale.PaidCash + sale.PaidCard) < total ||
                    sale.Change != Math.Max(0, sale.PaidCash - (total - sale.PaidCard)))
                    throw new InvalidOperationException("sale_payment_invalid");
            }
            catch (OverflowException error) { throw new InvalidOperationException("sale_economics_overflow", error); }
        }
    }
}
