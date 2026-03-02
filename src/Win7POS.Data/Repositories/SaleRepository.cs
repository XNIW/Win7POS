using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;

namespace Win7POS.Data.Repositories
{
    public sealed class SaleRepository
    {
        private readonly SqliteConnectionFactory _factory;

        public SaleRepository(SqliteConnectionFactory factory) => _factory = factory;

        public async Task<long> InsertSaleAsync(Sale sale, IReadOnlyList<SaleLine> lines)
        {
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();

            try
            {
                if (sale.Kind == 0)
                    sale.Kind = (int)SaleKind.Sale;

                var saleId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(code, createdAt, kind, related_sale_id, voided_by_sale_id, voided_at, reason, total, paidCash, paidCard, change)
VALUES(@Code, @CreatedAt, @Kind, @RelatedSaleId, @VoidedBySaleId, @VoidedAt, @Reason, @Total, @PaidCash, @PaidCard, @Change);
SELECT last_insert_rowid();", sale, tx);

                foreach (var l in lines)
                {
                    l.SaleId = saleId;
                    l.LineTotal = l.Quantity * l.UnitPrice;
                }

                await conn.ExecuteAsync(@"
INSERT INTO sale_lines(saleId, productId, barcode, name, quantity, unitPrice, lineTotal, related_original_line_id)
VALUES(@SaleId, @ProductId, @Barcode, @Name, @Quantity, @UnitPrice, @LineTotal, @RelatedOriginalLineId);", lines, tx);

                tx.Commit();
                return saleId;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        public async Task<IReadOnlyList<Sale>> LastSalesAsync(int take = 5)
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<Sale>(
                @"SELECT id, code, createdAt, kind, related_sale_id AS RelatedSaleId, voided_by_sale_id AS VoidedBySaleId, voided_at AS VoidedAt, reason, total, paidCash, paidCard, change
                  FROM sales
                  ORDER BY id DESC LIMIT @take",
                new { take }
            );
            return rows.ToList();
        }

        public async Task<IReadOnlyList<Sale>> GetSalesBetweenAsync(long fromMs, long toMs)
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<Sale>(
                @"SELECT id, code, createdAt, kind, related_sale_id AS RelatedSaleId, voided_by_sale_id AS VoidedBySaleId, voided_at AS VoidedAt, reason, total, paidCash, paidCard, change
                  FROM sales
                  WHERE createdAt >= @fromMs AND createdAt < @toMs
                  ORDER BY createdAt ASC, id ASC",
                new { fromMs, toMs }
            );
            return rows.ToList();
        }

        public async Task<DailySalesSummary> GetDailySummaryAsync(DateTime date)
        {
            var from = new DateTimeOffset(date.Date).ToUnixTimeMilliseconds();
            var to = new DateTimeOffset(date.Date.AddDays(1)).ToUnixTimeMilliseconds();
            using var conn = _factory.Open();
            var row = await conn.QuerySingleAsync<DailySalesSummary>(@"
SELECT
  COUNT(CASE WHEN kind = 0 THEN 1 END) AS SalesCount,
  COALESCE(SUM(total), 0) AS TotalAmount,
  COALESCE(SUM(paidCash), 0) AS CashAmount,
  COALESCE(SUM(paidCard), 0) AS CardAmount,
  COALESCE(SUM(CASE WHEN kind = 0 THEN total ELSE 0 END), 0) AS GrossSalesAmount,
  COALESCE(SUM(CASE WHEN kind = 1 THEN ABS(total) ELSE 0 END), 0) AS RefundsAmount
FROM sales
WHERE createdAt >= @from AND createdAt < @to",
                new { from, to });
            row.NetAmount = row.TotalAmount;
            row.Date = date.Date;
            return row;
        }

        public Task<IReadOnlyList<Sale>> GetSalesForDateAsync(DateTime date)
        {
            var from = new DateTimeOffset(date.Date).ToUnixTimeMilliseconds();
            var to = new DateTimeOffset(date.Date.AddDays(1)).ToUnixTimeMilliseconds();
            return GetSalesBetweenAsync(from, to);
        }

        public async Task<Sale> GetByIdAsync(long saleId)
        {
            using var conn = _factory.Open();
            return await conn.QuerySingleOrDefaultAsync<Sale>(
                @"SELECT id, code, createdAt, kind, related_sale_id AS RelatedSaleId, voided_by_sale_id AS VoidedBySaleId, voided_at AS VoidedAt, reason, total, paidCash, paidCard, change
                  FROM sales WHERE id = @saleId",
                new { saleId });
        }

        public async Task<IReadOnlyList<Sale>> GetByCodeLikeAsync(string codeFilter)
        {
            if (string.IsNullOrWhiteSpace(codeFilter))
                return new List<Sale>();
            using var conn = _factory.Open();
            var pattern = "%" + codeFilter.Trim() + "%";
            var rows = await conn.QueryAsync<Sale>(
                @"SELECT id, code, createdAt, kind, related_sale_id AS RelatedSaleId, voided_by_sale_id AS VoidedBySaleId, voided_at AS VoidedAt, reason, total, paidCash, paidCard, change
                  FROM sales
                  WHERE code LIKE @pattern
                  ORDER BY createdAt DESC, id DESC
                  LIMIT 200",
                new { pattern });
            return rows.ToList();
        }

        public async Task<IReadOnlyList<SaleLine>> GetLinesBySaleIdAsync(long saleId)
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<SaleLine>(
                @"SELECT id, saleId, productId, barcode, name, quantity, unitPrice, lineTotal, related_original_line_id AS RelatedOriginalLineId
                  FROM sale_lines
                  WHERE saleId = @saleId
                  ORDER BY id ASC",
                new { saleId });
            return rows.ToList();
        }

        public async Task<bool> IsVoidedAsync(long saleId)
        {
            using var conn = _factory.Open();
            var voidedBy = await conn.QuerySingleOrDefaultAsync<long?>(
                "SELECT voided_by_sale_id FROM sales WHERE id = @saleId",
                new { saleId });
            return voidedBy.HasValue;
        }

        public async Task<int> GetRefundedQtyAsync(long originalSaleId, long originalLineId)
        {
            using var conn = _factory.Open();
            var qty = await conn.QuerySingleAsync<int>(@"
SELECT COALESCE(SUM(ABS(sl.quantity)), 0)
FROM sale_lines sl
JOIN sales s ON s.id = sl.saleId
WHERE s.kind = @kindRefund
  AND s.related_sale_id = @originalSaleId
  AND sl.related_original_line_id = @originalLineId",
                new
                {
                    kindRefund = (int)SaleKind.Refund,
                    originalSaleId,
                    originalLineId
                });
            return qty;
        }

        public async Task<List<SaleLineReturnableDto>> GetReturnableLinesAsync(long saleId)
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<SaleLineReturnableDto>(@"
SELECT
  l.id AS OriginalLineId,
  l.saleId AS OriginalSaleId,
  l.productId,
  l.barcode,
  l.name,
  l.unitPrice,
  l.quantity AS SoldQty,
  COALESCE((
    SELECT SUM(ABS(rl.quantity))
    FROM sale_lines rl
    JOIN sales rs ON rs.id = rl.saleId
    WHERE rs.kind = @kindRefund
      AND rs.related_sale_id = @saleId
      AND rl.related_original_line_id = l.id
  ), 0) AS RefundedQty
FROM sale_lines l
WHERE l.saleId = @saleId
ORDER BY l.id ASC",
                new { saleId, kindRefund = (int)SaleKind.Refund });

            var list = rows.ToList();
            foreach (var x in list)
            {
                x.RemainingQty = x.SoldQty - x.RefundedQty;
                if (x.RemainingQty < 0) x.RemainingQty = 0;
            }
            return list;
        }

        public async Task MarkSaleVoidedAsync(long originalSaleId, long refundSaleId, long nowMs)
        {
            using var conn = _factory.Open();
            await MarkSaleVoidedAsync(conn, null, originalSaleId, refundSaleId, nowMs).ConfigureAwait(false);
        }

        public async Task<long> InsertRefundSaleAsync(
            RefundCreateRequest req,
            int totalMinor,
            int paidCashMinor,
            int paidCardMinor,
            int changeMinor)
        {
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                var saleId = await InsertRefundSaleAsync(conn, tx, req, totalMinor, paidCashMinor, paidCardMinor, changeMinor).ConfigureAwait(false);
                tx.Commit();
                return saleId;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        public async Task<long> InsertRefundSaleAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            RefundCreateRequest req,
            int totalMinor,
            int paidCashMinor,
            int paidCardMinor,
            int changeMinor)
        {
            var sale = new Sale
            {
                Code = "R" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Kind = (int)SaleKind.Refund,
                RelatedSaleId = req?.OriginalSaleId,
                Reason = req?.Reason,
                Total = totalMinor,
                PaidCash = paidCashMinor,
                PaidCard = paidCardMinor,
                Change = changeMinor
            };

            var saleId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(code, createdAt, kind, related_sale_id, voided_by_sale_id, voided_at, reason, total, paidCash, paidCard, change)
VALUES(@Code, @CreatedAt, @Kind, @RelatedSaleId, @VoidedBySaleId, @VoidedAt, @Reason, @Total, @PaidCash, @PaidCard, @Change);
SELECT last_insert_rowid();", sale, tx).ConfigureAwait(false);

            return saleId;
        }

        public async Task InsertSaleLinesAsync(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<SaleLine> lines)
        {
            if (lines == null || lines.Count == 0) return;
            await conn.ExecuteAsync(@"
INSERT INTO sale_lines(saleId, productId, barcode, name, quantity, unitPrice, lineTotal, related_original_line_id)
VALUES(@SaleId, @ProductId, @Barcode, @Name, @Quantity, @UnitPrice, @LineTotal, @RelatedOriginalLineId);", lines, tx).ConfigureAwait(false);
        }

        public async Task MarkSaleVoidedAsync(SqliteConnection conn, SqliteTransaction tx, long originalSaleId, long refundSaleId, long nowMs)
        {
            await conn.ExecuteAsync(
                @"UPDATE sales
                  SET voided_by_sale_id = @refundSaleId,
                      voided_at = @nowMs
                  WHERE id = @originalSaleId",
                new { originalSaleId, refundSaleId, nowMs }, tx).ConfigureAwait(false);
        }
    }

    public sealed class DailySalesSummary
    {
        public DateTime Date { get; set; }
        public int SalesCount { get; set; }
        public int TotalAmount { get; set; }
        public int CashAmount { get; set; }
        public int CardAmount { get; set; }
        public int GrossSalesAmount { get; set; }
        public int RefundsAmount { get; set; }
        public int NetAmount { get; set; }
    }

    public sealed class SaleLineReturnableDto
    {
        public long OriginalLineId { get; set; }
        public long OriginalSaleId { get; set; }
        public long? ProductId { get; set; }
        public string Barcode { get; set; }
        public string Name { get; set; }
        public int UnitPrice { get; set; }
        public int SoldQty { get; set; }
        public int RefundedQty { get; set; }
        public int RemainingQty { get; set; }
    }
}
