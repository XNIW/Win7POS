using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;

namespace Win7POS.Data.Repositories
{
    public sealed class SaleRepository
    {
        private readonly SqliteConnectionFactory _factory;

        public SaleRepository(SqliteConnectionFactory factory) => _factory = factory;

        public async Task<long> InsertSaleAsync(Sale sale, IReadOnlyList<SaleLine> lines)
        {
            using var conn = await _factory.OpenAsync().ConfigureAwait(false);
            using var tx = conn.BeginTransaction();

            try
            {
                if (sale.Kind == 0)
                    sale.Kind = (int)SaleKind.Sale;

                var saleId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(code, createdAt, kind, related_sale_id, voided_by_sale_id, voided_at, reason, total, paidCash, paidCard, change, operator_id)
VALUES(@Code, @CreatedAt, @Kind, @RelatedSaleId, @VoidedBySaleId, @VoidedAt, @Reason, @Total, @PaidCash, @PaidCard, @Change, @OperatorId);
SELECT last_insert_rowid();", sale, tx).ConfigureAwait(false);
                sale.Id = saleId;
                sale.ClientSaleId = await EnsureClientSaleIdAsync(conn, tx, saleId).ConfigureAwait(false);

                foreach (var l in lines.Select((line, index) => new { line, index }))
                {
                    l.line.SaleId = saleId;
                    l.line.LineTotal = l.line.Quantity * l.line.UnitPrice;
                    l.line.Id = await InsertSaleLineAsync(conn, tx, l.line).ConfigureAwait(false);
                }

                await ApplyLocalStockMovementsAsync(conn, tx, sale, lines).ConfigureAwait(false);
                await EnqueueSalesSyncOutboxAsync(conn, tx, saleId, sale.ClientSaleId).ConfigureAwait(false);

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
                @"SELECT id, client_sale_id AS ClientSaleId, code, createdAt, kind, related_sale_id AS RelatedSaleId, voided_by_sale_id AS VoidedBySaleId, voided_at AS VoidedAt, reason, total, paidCash, paidCard, change, operator_id AS OperatorId, COALESCE(pdf_printed, 0) AS PdfPrinted, sync_status AS SyncStatus
                  FROM sales
                  ORDER BY id DESC LIMIT @take",
                new { take }
            ).ConfigureAwait(false);
            return rows.ToList();
        }

        public async Task<IReadOnlyList<Sale>> GetSalesBetweenAsync(long fromMs, long toMs, int? operatorId = null, bool includeFiscalPrinted = true)
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

        public async Task<DailySalesSummary> GetDailySummaryAsync(DateTime date, bool includeFiscalPrinted = true)
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

        public Task<IReadOnlyList<DailySalesSummary>> GetDailySummariesAsync(DateTime fromDate, DateTime toDate, bool includeFiscalPrinted = true)
        {
            return GetDailySummariesRangeAsync(fromDate, toDate, includeFiscalPrinted);
        }

        /// <summary>Report range veloce: una sola query aggregata + riempimento giorni mancanti.</summary>
        public async Task<IReadOnlyList<DailySalesSummary>> GetDailySummariesRangeAsync(DateTime fromDate, DateTime toDate, bool includeFiscalPrinted = true)
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

        public Task<IReadOnlyList<Sale>> GetSalesForDateAsync(DateTime date, bool includeFiscalPrinted = true)
        {
            var from = new DateTimeOffset(date.Date).ToUnixTimeMilliseconds();
            var to = new DateTimeOffset(date.Date.AddDays(1)).ToUnixTimeMilliseconds();
            return GetSalesBetweenAsync(from, to, null, includeFiscalPrinted);
        }

        /// <summary>Vendite per fascia oraria (0-23) del giorno, solo vendite (kind=0).</summary>
        public async Task<IReadOnlyList<long>> GetHourlySalesAsync(DateTime date, bool includeFiscalPrinted = true)
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

        public async Task<Sale> GetByIdAsync(long saleId)
        {
            using var conn = _factory.Open();
            return await conn.QuerySingleOrDefaultAsync<Sale>(
                @"SELECT id, client_sale_id AS ClientSaleId, code, createdAt, kind, related_sale_id AS RelatedSaleId, voided_by_sale_id AS VoidedBySaleId, voided_at AS VoidedAt, reason, total, paidCash, paidCard, change, operator_id AS OperatorId, COALESCE(pdf_printed, 0) AS PdfPrinted, sync_status AS SyncStatus
                  FROM sales WHERE id = @saleId",
                new { saleId }).ConfigureAwait(false);
        }

        /// <summary>Imposta pdf_printed=1 come stato documentale locale senza rimuovere la vendita da report o sync.</summary>
        public Task MarkPdfPrintedAsync(long saleId)
        {
            using var conn = _factory.Open();
            return conn.ExecuteAsync(
                "UPDATE sales SET pdf_printed = 1 WHERE id = @saleId",
                new { saleId });
        }

        public async Task<IReadOnlyList<Sale>> GetByCodeLikeAsync(string codeFilter, bool includeFiscalPrinted = true)
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

        public async Task<IReadOnlyList<SaleLine>> GetLinesBySaleIdAsync(long saleId)
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<SaleLine>(
                @"SELECT id, saleId, productId, barcode, name, quantity, unitPrice, lineTotal, related_original_line_id AS RelatedOriginalLineId
                  FROM sale_lines
                  WHERE saleId = @saleId
                  ORDER BY id ASC",
                new { saleId }).ConfigureAwait(false);
            return rows.ToList();
        }

        public async Task<bool> IsVoidedAsync(long saleId)
        {
            using var conn = _factory.Open();
            var voidedBy = await conn.QuerySingleOrDefaultAsync<long?>(
                "SELECT voided_by_sale_id FROM sales WHERE id = @saleId",
                new { saleId }).ConfigureAwait(false);
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
                }).ConfigureAwait(false);
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
                new { saleId, kindRefund = (int)SaleKind.Refund }).ConfigureAwait(false);

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
            long totalMinor,
            long paidCashMinor,
            long paidCardMinor,
            long changeMinor)
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
            long totalMinor,
            long paidCashMinor,
            long paidCardMinor,
            long changeMinor)
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
            foreach (var line in lines)
            {
                line.Id = await InsertSaleLineAsync(conn, tx, line).ConfigureAwait(false);
            }
        }

        public async Task<string> EnsureClientSaleIdAsync(SqliteConnection conn, SqliteTransaction tx, long saleId)
        {
            var existing = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT client_sale_id FROM sales WHERE id = @saleId",
                new { saleId }, tx).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            var clientSaleId = BuildClientSaleId(saleId);
            await conn.ExecuteAsync(
                "UPDATE sales SET client_sale_id = @clientSaleId WHERE id = @saleId",
                new { clientSaleId, saleId }, tx).ConfigureAwait(false);
            return clientSaleId;
        }

        public async Task ApplyLocalStockMovementsAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            Sale sale,
            IReadOnlyList<SaleLine> lines)
        {
            if (sale == null || lines == null || lines.Count == 0)
            {
                return;
            }

            var clientSaleId = sale.ClientSaleId;
            if (string.IsNullOrWhiteSpace(clientSaleId))
            {
                clientSaleId = await EnsureClientSaleIdAsync(conn, tx, sale.Id).ConfigureAwait(false);
                sale.ClientSaleId = clientSaleId;
            }

            foreach (var line in lines)
            {
                var barcode = (line.Barcode ?? string.Empty).Trim();
                if (barcode.Length == 0 ||
                    line.Quantity == 0 ||
                    DiscountKeys.IsDiscount(barcode) ||
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

        public async Task EnqueueSalesSyncOutboxAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long saleId,
            string clientSaleId)
        {
            var normalizedClientSaleId = string.IsNullOrWhiteSpace(clientSaleId)
                ? BuildClientSaleId(saleId)
                : clientSaleId.Trim();
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var idempotencyKey = normalizedClientSaleId + ":pos-sales-ledger-v2";

            await conn.ExecuteAsync(@"
INSERT OR IGNORE INTO sales_sync_outbox(
  sale_id, client_sale_id, idempotency_key, status, created_at, updated_at)
VALUES(
  @saleId, @clientSaleId, @idempotencyKey, 'pending', @nowMs, @nowMs);

UPDATE sales
SET sync_status = CASE
    WHEN sync_status = 'acked' THEN sync_status
    ELSE 'pending'
  END,
  client_sale_id = COALESCE(client_sale_id, @clientSaleId)
WHERE id = @saleId;",
                new
                {
                    saleId,
                    clientSaleId = normalizedClientSaleId,
                    idempotencyKey,
                    nowMs
                }, tx).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<SalesSyncOutboxItem>> GetPendingSalesSyncOutboxAsync(int take, long nowMs)
        {
            if (take <= 0) take = 1;
            if (take > 50) take = 50;

            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<SalesSyncOutboxItem>(@"
SELECT
  id AS Id,
  sale_id AS SaleId,
  client_sale_id AS ClientSaleId,
  client_batch_id AS ClientBatchId,
  idempotency_key AS IdempotencyKey,
  payload_hash AS PayloadHash,
  status AS Status,
  attempt_count AS AttemptCount,
  next_retry_at AS NextRetryAt,
  last_error_code AS LastErrorCode
FROM sales_sync_outbox
WHERE status IN ('pending', 'retry')
  AND next_retry_at <= @nowMs
ORDER BY id ASC
LIMIT @take",
                new { take, nowMs }).ConfigureAwait(false);
            return rows.ToList();
        }

        public async Task<SalesSyncOutboxSummary> GetSalesSyncOutboxSummaryAsync()
        {
            using var conn = _factory.Open();
            var rows = (await conn.QueryAsync<SalesSyncStatusCount>(@"
SELECT status AS Status, COUNT(1) AS Count
FROM sales_sync_outbox
GROUP BY status;").ConfigureAwait(false)).ToList();

            var lastAckedAt = await conn.ExecuteScalarAsync<long?>(@"
SELECT updated_at
FROM sales_sync_outbox
WHERE status = 'acked'
ORDER BY updated_at DESC
LIMIT 1;").ConfigureAwait(false);

            long CountFor(string status)
            {
                return rows
                    .Where(row => string.Equals(row.Status, status, StringComparison.OrdinalIgnoreCase))
                    .Select(row => row.Count)
                    .DefaultIfEmpty(0)
                    .Sum();
            }

            return new SalesSyncOutboxSummary
            {
                Acked = CountFor("acked"),
                Blocked = CountFor("failed_blocked"),
                LastAckedAt = lastAckedAt,
                Pending = CountFor("pending"),
                Retry = CountFor("retry")
            };
        }

        public async Task<IReadOnlyDictionary<long, string>> GetRemoteProductIdsAsync(IEnumerable<long> productIds)
        {
            var ids = (productIds ?? Enumerable.Empty<long>())
                .Where(id => id > 0)
                .Distinct()
                .ToArray();

            if (ids.Length == 0)
            {
                return new Dictionary<long, string>();
            }

            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<RemoteProductIdRow>(
                "SELECT id AS ProductId, remote_product_id AS RemoteProductId FROM products WHERE id IN @ids",
                new { ids }).ConfigureAwait(false);
            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.RemoteProductId))
                .ToDictionary(row => row.ProductId, row => row.RemoteProductId);
        }

        public async Task PrepareSalesSyncAttemptAsync(
            long outboxId,
            string clientBatchId,
            string payloadJson,
            string payloadHash,
            long nowMs)
        {
            using var conn = _factory.Open();
            await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET client_batch_id = @clientBatchId,
    payload_json = @payloadJson,
    payload_hash = @payloadHash,
    attempt_count = attempt_count + 1,
    last_attempt_at = @nowMs,
    updated_at = @nowMs
WHERE id = @outboxId
  AND status IN ('pending', 'retry');",
                new { outboxId, clientBatchId, payloadJson, payloadHash, nowMs }).ConfigureAwait(false);
        }

        public async Task MarkSalesSyncAckedAsync(
            long outboxId,
            long saleId,
            string serverBatchId,
            string serverSaleId,
            long nowMs)
        {
            using var conn = await _factory.OpenAsync().ConfigureAwait(false);
            using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET status = 'acked',
    server_batch_id = @serverBatchId,
    server_sale_id = @serverSaleId,
    last_error_code = NULL,
    last_error_at = NULL,
    updated_at = @nowMs
WHERE id = @outboxId;

UPDATE sales
SET sync_status = 'acked'
WHERE id = @saleId;",
                new { outboxId, saleId, serverBatchId, serverSaleId, nowMs }, tx).ConfigureAwait(false);
            tx.Commit();
        }

        public async Task MarkSalesSyncRetryAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs)
        {
            using var conn = await _factory.OpenAsync().ConfigureAwait(false);
            using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET status = 'retry',
    next_retry_at = @nextRetryAt,
    last_error_code = @errorCode,
    last_error_at = @nowMs,
    updated_at = @nowMs
WHERE id = @outboxId;

UPDATE sales
SET sync_status = 'retry'
WHERE id = @saleId;",
                new { outboxId, saleId, errorCode, nextRetryAt, nowMs }, tx).ConfigureAwait(false);
            tx.Commit();
        }

        public async Task MarkSalesSyncBlockedAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nowMs)
        {
            using var conn = await _factory.OpenAsync().ConfigureAwait(false);
            using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
	SET status = 'failed_blocked',
    last_error_code = @errorCode,
    last_error_at = @nowMs,
    updated_at = @nowMs
WHERE id = @outboxId;

UPDATE sales
SET sync_status = 'blocked'
WHERE id = @saleId;",
                new { outboxId, saleId, errorCode, nowMs }, tx).ConfigureAwait(false);
            tx.Commit();
        }

        private static string BuildClientSaleId(long saleId)
        {
            return "win7pos-sale-" + saleId.ToString(CultureInfo.InvariantCulture);
        }

        private static async Task<long> InsertSaleLineAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            SaleLine line)
        {
            return await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sale_lines(saleId, productId, barcode, name, quantity, unitPrice, lineTotal, related_original_line_id)
VALUES(@SaleId, @ProductId, @Barcode, @Name, @Quantity, @UnitPrice, @LineTotal, @RelatedOriginalLineId);
SELECT last_insert_rowid();", line, tx).ConfigureAwait(false);
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

    internal sealed class DailySummaryRow
    {
        public string DayStr { get; set; }
        public int SalesCount { get; set; }
        public long TotalAmount { get; set; }
        public long CashAmount { get; set; }
        public long CardAmount { get; set; }
        public long GrossSalesAmount { get; set; }
        public long RefundsAmount { get; set; }
    }

    public sealed class DailySalesSummary
    {
        public DateTime Date { get; set; }
        public int SalesCount { get; set; }
        public long TotalAmount { get; set; }
        public long CashAmount { get; set; }
        public long CardAmount { get; set; }
        public long GrossSalesAmount { get; set; }
        public long RefundsAmount { get; set; }
        public long NetAmount { get; set; }
    }

    public sealed class SalesSyncOutboxSummary
    {
        public long Acked { get; set; }
        public long Blocked { get; set; }
        public long? LastAckedAt { get; set; }
        public long Pending { get; set; }
        public long Retry { get; set; }
        public long PendingOrRetry => Pending + Retry;
    }

    internal sealed class SalesSyncStatusCount
    {
        public long Count { get; set; }
        public string Status { get; set; }
    }

    public sealed class SaleLineReturnableDto
    {
        public long OriginalLineId { get; set; }
        public long OriginalSaleId { get; set; }
        public long? ProductId { get; set; }
        public string Barcode { get; set; }
        public string Name { get; set; }
        public long UnitPrice { get; set; }
        public int SoldQty { get; set; }
        public int RefundedQty { get; set; }
        public int RemainingQty { get; set; }
    }

    public sealed class SalesSyncOutboxItem
    {
        public long Id { get; set; }
        public long SaleId { get; set; }
        public string ClientSaleId { get; set; }
        public string ClientBatchId { get; set; }
        public string IdempotencyKey { get; set; }
        public string PayloadHash { get; set; }
        public string Status { get; set; }
        public int AttemptCount { get; set; }
        public long NextRetryAt { get; set; }
        public string LastErrorCode { get; set; }
    }

    internal sealed class RemoteProductIdRow
    {
        public long ProductId { get; set; }
        public string RemoteProductId { get; set; }
    }
}
