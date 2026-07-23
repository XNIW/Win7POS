using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;

namespace Win7POS.Data.Repositories
{
    /// <summary>
    /// Owns local stock-movement ledger writes within a caller-owned connection
    /// and transaction. It deliberately does not open, begin, commit, or roll
    /// back anything, so sale write flows retain their atomic boundary.
    /// </summary>
    internal sealed class SaleStockMovementWriter
    {
        internal async Task ApplyAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            Sale sale,
            IReadOnlyList<SaleLine> lines,
            string clientSaleId)
        {
            if (sale == null || lines == null || lines.Count == 0)
            {
                return;
            }

            foreach (var line in lines)
            {
                var barcode = (line.Barcode ?? string.Empty).Trim();
                if (barcode.Length == 0 ||
                    line.Quantity == 0 ||
                    DiscountKeys.IsEconomicAdjustment(barcode) ||
                    barcode.StartsWith(DiscountKeys.ManualPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var quantityDelta = sale.Kind == (int)SaleKind.Refund || sale.Kind == (int)SaleKind.Void
                    ? Math.Abs(line.Quantity)
                    : -Math.Abs(line.Quantity);
                var movementKind = sale.Kind == (int)SaleKind.Refund
                    ? "refund_increment"
                    : sale.Kind == (int)SaleKind.Void
                        ? "void_reverse"
                        : "sale_decrement";
                var movementKey = clientSaleId + ":" +
                    line.Id.ToString(CultureInfo.InvariantCulture) + ":" +
                    movementKind;

                var inserted = await conn.ExecuteAsync(@"
INSERT OR IGNORE INTO local_stock_movements(
  movement_key, sale_id, sale_line_id, barcode, quantity_delta, movement_kind, created_at)
VALUES(
  @movementKey, @saleId, @saleLineId, @barcode, @quantityDelta, @movementKind, @createdAt);",
                    new
                    {
                        movementKey,
                        saleId = sale.Id,
                        saleLineId = line.Id == 0 ? (long?)null : line.Id,
                        barcode,
                        quantityDelta,
                        movementKind,
                        createdAt = sale.CreatedAt
                    }, tx).ConfigureAwait(false);

                if (inserted == 0)
                {
                    continue;
                }

                await conn.ExecuteAsync(@"
UPDATE product_meta
SET stock_qty =
  CASE
    WHEN stock_qty + @quantityDelta < 0 THEN 0
    ELSE stock_qty + @quantityDelta
  END
WHERE barcode = @barcode;",
                    new { barcode, quantityDelta }, tx).ConfigureAwait(false);
            }
        }
    }
}
