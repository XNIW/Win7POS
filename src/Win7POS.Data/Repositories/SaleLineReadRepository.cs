using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Core.Receipt;

namespace Win7POS.Data.Repositories
{
    /// <summary>
    /// Owns persisted sale-line reads and the aggregate receipt-line budget check.
    /// Writer callers reuse the static budget guard with their own connection and
    /// transaction so budget validation remains consistent without changing
    /// transaction ownership.
    /// </summary>
    internal sealed class SaleLineReadRepository
    {
        private readonly SqliteConnectionFactory _factory;

        internal SaleLineReadRepository(SqliteConnectionFactory factory)
        {
            _factory = factory;
        }

        internal async Task<IReadOnlyList<SaleLine>> GetLinesBySaleIdAsync(long saleId)
        {
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction(deferred: true);
            await EnsureStoredLineBudgetAsync(conn, tx, saleId).ConfigureAwait(false);
            var rows = await conn.QueryAsync<SaleLine>(
                @"SELECT id, saleId, productId, barcode, name, quantity, unitPrice, lineTotal, related_original_line_id AS RelatedOriginalLineId
                  FROM sale_lines
                  WHERE saleId = @saleId
                  ORDER BY id ASC",
                new { saleId },
                tx).ConfigureAwait(false);
            var result = rows.ToList();
            SalesReceiptContentPolicy.EnsureValidLines(result);
            tx.Commit();
            return result;
        }

        internal static async Task<SaleLineBudgetRow> EnsureStoredLineBudgetAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long saleId)
        {
            var budget = await ReadSaleLineBudgetAsync(conn, tx, saleId).ConfigureAwait(false);
            SalesReceiptContentPolicy.EnsureStoredLineBudget(
                budget.LineCount,
                budget.MaximumNameCharacters,
                budget.MaximumBarcodeCharacters,
                budget.AggregateNameCharacters,
                budget.AggregateNameUtf8Bytes);
            return budget;
        }

        private static async Task<SaleLineBudgetRow> ReadSaleLineBudgetAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long saleId)
        {
            return await conn.QuerySingleAsync<SaleLineBudgetRow>(@"
SELECT COUNT(1) AS LineCount,
       COALESCE(MAX(LENGTH(COALESCE(name, ''))), 0) AS MaximumNameCharacters,
       COALESCE(MAX(LENGTH(COALESCE(barcode, ''))), 0) AS MaximumBarcodeCharacters,
       COALESCE(SUM(LENGTH(COALESCE(name, ''))), 0) AS AggregateNameCharacters,
       COALESCE(SUM(LENGTH(CAST(COALESCE(name, '') AS BLOB))), 0) AS AggregateNameUtf8Bytes
FROM sale_lines
WHERE saleId = @saleId;",
                new { saleId },
                tx).ConfigureAwait(false);
        }

        internal sealed class SaleLineBudgetRow
        {
            public long AggregateNameCharacters { get; set; }
            public long AggregateNameUtf8Bytes { get; set; }
            public long LineCount { get; set; }
            public long MaximumBarcodeCharacters { get; set; }
            public long MaximumNameCharacters { get; set; }
        }
    }
}
