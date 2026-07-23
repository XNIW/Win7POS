using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Models;
using Win7POS.Core.Receipt;

namespace Win7POS.Data.Repositories
{
    /// <summary>
    /// Owns pure sale reads and reporting queries. It deliberately opens only
    /// short-lived read connections and has no transaction, line, stock or outbox
    /// mutation behavior.
    /// </summary>
    internal sealed class SaleReadRepository
    {
        private readonly SqliteConnectionFactory _factory;

        internal SaleReadRepository(SqliteConnectionFactory factory)
        {
            _factory = factory;
        }

        internal async Task<IReadOnlyList<Sale>> LastSalesAsync(int take = 5)
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<Sale>(
                @"SELECT id, client_sale_id AS ClientSaleId, code, createdAt, kind, related_sale_id AS RelatedSaleId, voided_by_sale_id AS VoidedBySaleId, voided_at AS VoidedAt, reason, total, paidCash, paidCard, change, operator_id AS OperatorId, COALESCE(pdf_printed, 0) AS PdfPrinted, sync_status AS SyncStatus
                  FROM sales
                  ORDER BY id DESC LIMIT @take",
                new { take }
            ).ConfigureAwait(false);
            return rows.ToList();
        }

        internal async Task<IReadOnlyList<Sale>> GetSalesBetweenAsync(
            long fromMs,
            long toMs,
            int? operatorId = null,
            bool includeFiscalPrinted = true)
        {
            using var conn = _factory.Open();
            var sql = @"SELECT id, client_sale_id AS ClientSaleId, code, createdAt, kind, related_sale_id AS RelatedSaleId, voided_by_sale_id AS VoidedBySaleId, voided_at AS VoidedAt, reason, total, paidCash, paidCard, change, operator_id AS OperatorId, COALESCE(pdf_printed, 0) AS PdfPrinted, sync_status AS SyncStatus
                  FROM sales
                  WHERE createdAt >= @fromMs AND createdAt < @toMs";
            if (operatorId.HasValue)
                sql += " AND (operator_id IS NULL OR operator_id = @operatorId)";
            sql += " ORDER BY createdAt ASC, id ASC";
            var rows = await conn.QueryAsync<Sale>(sql, new { fromMs, toMs, operatorId }).ConfigureAwait(false);
            return rows.ToList();
        }

        internal async Task<DailySalesSummary> GetDailySummaryAsync(
            DateTime date,
            bool includeFiscalPrinted = true)
        {
            var from = new DateTimeOffset(date.Date).ToUnixTimeMilliseconds();
            var to = new DateTimeOffset(date.Date.AddDays(1)).ToUnixTimeMilliseconds();
            using var conn = _factory.Open();
            var row = await conn.QuerySingleAsync<DailySalesSummary>($@"
	SELECT
	  COUNT(CASE WHEN kind = 0 THEN 1 END) AS SalesCount,
	  COALESCE(SUM(total), 0) AS TotalAmount,
	  COALESCE(SUM(paidCash), 0) AS CashAmount,
	  COALESCE(SUM(paidCard), 0) AS CardAmount,
	  COALESCE(SUM(CASE WHEN kind = 0 THEN total ELSE 0 END), 0) AS GrossSalesAmount,
	  COALESCE(SUM(CASE WHEN kind IN (1, 2) THEN ABS(total) ELSE 0 END), 0) AS RefundsAmount
	FROM sales
	WHERE createdAt >= @from AND createdAt < @to",
                new { from, to }).ConfigureAwait(false);
            row.NetAmount = row.TotalAmount;
            row.Date = date.Date;
            return row;
        }

        internal Task<IReadOnlyList<DailySalesSummary>> GetDailySummariesAsync(
            DateTime fromDate,
            DateTime toDate,
            bool includeFiscalPrinted = true)
        {
            return GetDailySummariesRangeAsync(fromDate, toDate, includeFiscalPrinted);
        }

        /// <summary>Report range veloce: una sola query aggregata + riempimento giorni mancanti.</summary>
        internal async Task<IReadOnlyList<DailySalesSummary>> GetDailySummariesRangeAsync(
            DateTime fromDate,
            DateTime toDate,
            bool includeFiscalPrinted = true)
        {
            var start = fromDate.Date;
            var end = toDate.Date;
            if (start > end) return new List<DailySalesSummary>();

            var fromMs = new DateTimeOffset(start).ToUnixTimeMilliseconds();
            var toMs = new DateTimeOffset(end.AddDays(1)).ToUnixTimeMilliseconds();

            using var conn = _factory.Open();
            var rows = (await conn.QueryAsync<DailySummaryRow>($@"
	SELECT
	  date(createdAt/1000, 'unixepoch', 'localtime') AS DayStr,
	  COUNT(CASE WHEN kind = 0 THEN 1 END) AS SalesCount,
	  COALESCE(SUM(total), 0) AS TotalAmount,
	  COALESCE(SUM(paidCash), 0) AS CashAmount,
	  COALESCE(SUM(paidCard), 0) AS CardAmount,
	  COALESCE(SUM(CASE WHEN kind = 0 THEN total ELSE 0 END), 0) AS GrossSalesAmount,
	  COALESCE(SUM(CASE WHEN kind IN (1, 2) THEN ABS(total) ELSE 0 END), 0) AS RefundsAmount
	FROM sales
	WHERE createdAt >= @fromMs AND createdAt < @toMs
	GROUP BY DayStr",
                new { fromMs, toMs }).ConfigureAwait(false)).ToList();

            var byDay = new Dictionary<string, DailySummaryRow>(StringComparer.Ordinal);
            foreach (var r in rows)
                if (!string.IsNullOrEmpty(r.DayStr))
                    byDay[r.DayStr] = r;

            var result = new List<DailySalesSummary>();
            for (var d = start; d <= end; d = d.AddDays(1))
            {
                var key = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (byDay.TryGetValue(key, out var row))
                {
                    result.Add(new DailySalesSummary
                    {
                        Date = d,
                        SalesCount = row.SalesCount,
                        TotalAmount = row.TotalAmount,
                        CashAmount = row.CashAmount,
                        CardAmount = row.CardAmount,
                        GrossSalesAmount = row.GrossSalesAmount,
                        RefundsAmount = row.RefundsAmount,
                        NetAmount = row.TotalAmount
                    });
                }
                else
                {
                    result.Add(new DailySalesSummary
                    {
                        Date = d,
                        SalesCount = 0,
                        TotalAmount = 0,
                        CashAmount = 0,
                        CardAmount = 0,
                        GrossSalesAmount = 0,
                        RefundsAmount = 0,
                        NetAmount = 0
                    });
                }
            }
            return result;
        }

        internal Task<IReadOnlyList<Sale>> GetSalesForDateAsync(
            DateTime date,
            bool includeFiscalPrinted = true)
        {
            var from = new DateTimeOffset(date.Date).ToUnixTimeMilliseconds();
            var to = new DateTimeOffset(date.Date.AddDays(1)).ToUnixTimeMilliseconds();
            return GetSalesBetweenAsync(from, to, null, includeFiscalPrinted);
        }

        /// <summary>Vendite per fascia oraria (0-23) del giorno, solo vendite (kind=0).</summary>
        internal async Task<IReadOnlyList<long>> GetHourlySalesAsync(
            DateTime date,
            bool includeFiscalPrinted = true)
        {
            var from = new DateTimeOffset(date.Date).ToUnixTimeMilliseconds();
            var to = new DateTimeOffset(date.Date.AddDays(1)).ToUnixTimeMilliseconds();
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<(int Hour, long Amount)>($@"
	SELECT CAST(strftime('%H', datetime(createdAt/1000, 'unixepoch', 'localtime')) AS INTEGER) AS Hour,
	       COALESCE(SUM(CASE WHEN kind = 0 THEN total ELSE 0 END), 0) AS Amount
	FROM sales
	WHERE createdAt >= @from AND createdAt < @to
	GROUP BY strftime('%H', datetime(createdAt/1000, 'unixepoch', 'localtime'))
	ORDER BY Hour", new { from, to }).ConfigureAwait(false);
            var result = new long[24];
            foreach (var r in rows)
                if (r.Hour >= 0 && r.Hour < 24)
                    result[r.Hour] = r.Amount;
            return result;
        }

        internal async Task<Sale> GetByIdAsync(long saleId)
        {
            using var conn = _factory.Open();
            var snapshotLength = await conn.ExecuteScalarAsync<long?>(
                "SELECT length(receipt_shop_snapshot) FROM sales WHERE id = @saleId;",
                new { saleId }).ConfigureAwait(false);
            if (snapshotLength > ReceiptDocumentPolicy.MaxSnapshotJsonCharacters)
            {
                throw new ReceiptContentValidationException(
                    "receipt_shop_snapshot_too_large",
                    "receiptShopSnapshotJson",
                    checked((int)Math.Min(snapshotLength.Value, int.MaxValue)),
                    -1);
            }
            var sale = await conn.QuerySingleOrDefaultAsync<Sale>(
                @"SELECT id, client_sale_id AS ClientSaleId, code, createdAt, kind, related_sale_id AS RelatedSaleId, voided_by_sale_id AS VoidedBySaleId, voided_at AS VoidedAt, reason, total, paidCash, paidCard, change, operator_id AS OperatorId, COALESCE(pdf_printed, 0) AS PdfPrinted, sync_status AS SyncStatus, receipt_shop_snapshot AS ReceiptShopSnapshotJson
                  FROM sales WHERE id = @saleId",
                new { saleId }).ConfigureAwait(false);
            ReceiptDocumentPolicy.EnsureValidSnapshotJson(sale?.ReceiptShopSnapshotJson);
            return sale;
        }

        internal async Task<IReadOnlyList<Sale>> GetByCodeLikeAsync(
            string codeFilter,
            bool includeFiscalPrinted = true)
        {
            if (string.IsNullOrWhiteSpace(codeFilter))
                return new List<Sale>();
            using var conn = _factory.Open();
            var pattern = "%" + codeFilter.Trim() + "%";
            var rows = await conn.QueryAsync<Sale>($@"
	SELECT id, client_sale_id AS ClientSaleId, code, createdAt, kind, related_sale_id AS RelatedSaleId, voided_by_sale_id AS VoidedBySaleId, voided_at AS VoidedAt, reason, total, paidCash, paidCard, change, operator_id AS OperatorId, COALESCE(pdf_printed, 0) AS PdfPrinted, sync_status AS SyncStatus
	FROM sales
	WHERE code LIKE @pattern
	ORDER BY createdAt DESC, id DESC
	LIMIT 200",
                new { pattern }).ConfigureAwait(false);
            return rows.ToList();
        }

        private sealed class DailySummaryRow
        {
            public string DayStr { get; set; }
            public int SalesCount { get; set; }
            public long TotalAmount { get; set; }
            public long CashAmount { get; set; }
            public long CardAmount { get; set; }
            public long GrossSalesAmount { get; set; }
            public long RefundsAmount { get; set; }
        }
    }
}
