using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Pos;
using Win7POS.Data.Online;

namespace Win7POS.Data.Repositories
{
    public sealed class SaleRepository
    {
        public const long SalesSyncInProgressLeaseMilliseconds = 15 * 60 * 1000L;

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

                if (sale.Kind == (int)SaleKind.Sale)
                {
                    await CatalogShopStateRepository
                        .RequireSaleSafeForOrdinarySaleAsync(conn, tx)
                        .ConfigureAwait(false);
                }

                await ValidateReversalBoundaryAsync(conn, tx, sale, lines).ConfigureAwait(false);

                var saleId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(code, createdAt, kind, related_sale_id, voided_by_sale_id, voided_at, reason, total, paidCash, paidCard, change, operator_id, receipt_shop_snapshot)
VALUES(@Code, @CreatedAt, @Kind, @RelatedSaleId, @VoidedBySaleId, @VoidedAt, @Reason, @Total, @PaidCash, @PaidCard, @Change, @OperatorId, @ReceiptShopSnapshotJson);
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
                @"SELECT id, client_sale_id AS ClientSaleId, code, createdAt, kind, related_sale_id AS RelatedSaleId, voided_by_sale_id AS VoidedBySaleId, voided_at AS VoidedAt, reason, total, paidCash, paidCard, change, operator_id AS OperatorId, COALESCE(pdf_printed, 0) AS PdfPrinted, sync_status AS SyncStatus, receipt_shop_snapshot AS ReceiptShopSnapshotJson
                  FROM sales
                  ORDER BY id DESC LIMIT @take",
                new { take }
            ).ConfigureAwait(false);
            return rows.ToList();
        }

        public async Task<IReadOnlyList<Sale>> GetSalesBetweenAsync(long fromMs, long toMs, int? operatorId = null, bool includeFiscalPrinted = true)
        {
            using var conn = _factory.Open();
            var sql = @"SELECT id, client_sale_id AS ClientSaleId, code, createdAt, kind, related_sale_id AS RelatedSaleId, voided_by_sale_id AS VoidedBySaleId, voided_at AS VoidedAt, reason, total, paidCash, paidCard, change, operator_id AS OperatorId, COALESCE(pdf_printed, 0) AS PdfPrinted, sync_status AS SyncStatus, receipt_shop_snapshot AS ReceiptShopSnapshotJson
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
                @"SELECT id, client_sale_id AS ClientSaleId, code, createdAt, kind, related_sale_id AS RelatedSaleId, voided_by_sale_id AS VoidedBySaleId, voided_at AS VoidedAt, reason, total, paidCash, paidCard, change, operator_id AS OperatorId, COALESCE(pdf_printed, 0) AS PdfPrinted, sync_status AS SyncStatus, receipt_shop_snapshot AS ReceiptShopSnapshotJson
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
	SELECT id, client_sale_id AS ClientSaleId, code, createdAt, kind, related_sale_id AS RelatedSaleId, voided_by_sale_id AS VoidedBySaleId, voided_at AS VoidedAt, reason, total, paidCash, paidCard, change, operator_id AS OperatorId, COALESCE(pdf_printed, 0) AS PdfPrinted, sync_status AS SyncStatus, receipt_shop_snapshot AS ReceiptShopSnapshotJson
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
WHERE s.kind IN (@kindRefund, @kindVoid)
  AND s.related_sale_id = @originalSaleId
  AND sl.related_original_line_id = @originalLineId",
                new
                {
                    kindRefund = (int)SaleKind.Refund,
                    kindVoid = (int)SaleKind.Void,
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
    WHERE rs.kind IN (@kindRefund, @kindVoid)
      AND rs.related_sale_id = @saleId
      AND rl.related_original_line_id = l.id
  ), 0) AS RefundedQty
FROM sale_lines l
WHERE l.saleId = @saleId
  AND COALESCE(l.barcode, '') NOT LIKE @discountPrefix
  AND COALESCE(l.barcode, '') NOT LIKE @taxPrefix
ORDER BY l.id ASC",
                new
                {
                    saleId,
                    kindRefund = (int)SaleKind.Refund,
                    kindVoid = (int)SaleKind.Void,
                    discountPrefix = DiscountKeys.Prefix + "%",
                    taxPrefix = DiscountKeys.TaxPrefix + "%"
                }).ConfigureAwait(false);

            var list = rows.ToList();
            foreach (var x in list)
            {
                x.RemainingQty = x.SoldQty - x.RefundedQty;
                if (x.RemainingQty < 0) x.RemainingQty = 0;
            }
            return list;
        }

        public async Task<ReversalEconomicsSnapshot> GetReversalEconomicsSnapshotAsync(long originalSaleId)
        {
            using var conn = await _factory.OpenAsync().ConfigureAwait(false);
            return await PosReversalEconomicsReader
                .LoadAsync(conn, null, originalSaleId, null)
                .ConfigureAwait(false);
        }

        internal async Task<ReversalEconomicsSnapshot> GetReversalEconomicsSnapshotExcludingAsync(
            long originalSaleId,
            long excludedReversalSaleId)
        {
            using var conn = await _factory.OpenAsync().ConfigureAwait(false);
            return await PosReversalEconomicsReader
                .LoadAsync(conn, null, originalSaleId, excludedReversalSaleId)
                .ConfigureAwait(false);
        }

        public async Task<string> GetPersistedReversalEconomicsErrorAsync(
            long saleId,
            PosSalesSyncRequest request)
        {
            using var conn = await _factory.OpenAsync().ConfigureAwait(false);
            var row = await conn.QuerySingleOrDefaultAsync<PersistedReversalRow>(@"
SELECT kind AS Kind, related_sale_id AS RelatedSaleId, total AS SaleTotal
FROM sales
WHERE id = @saleId;",
                new { saleId }).ConfigureAwait(false);
            if (row == null ||
                (row.Kind != (int)SaleKind.Refund && row.Kind != (int)SaleKind.Void))
            {
                return null;
            }

            if (!row.RelatedSaleId.HasValue ||
                !PosSalesSyncRequestBuilder.TryGetReversalGross(request, out var grossClp))
            {
                return ReversalEconomicsPolicy.MismatchCode;
            }

            try
            {
                var snapshot = await PosReversalEconomicsReader
                    .LoadAsync(conn, null, row.RelatedSaleId.Value, saleId)
                    .ConfigureAwait(false);
                var expected = ReversalEconomicsPolicy.Calculate(snapshot, grossClp);
                return row.SaleTotal == expected.NetClp &&
                    PosSalesSyncRequestBuilder.HasExpectedReversalEconomics(request, expected)
                        ? null
                        : ReversalEconomicsPolicy.MismatchCode;
            }
            catch (InvalidOperationException ex) when (IsReversalEconomicsCode(ex.Message))
            {
                return ex.Message;
            }
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
            if (req == null || req.OriginalSaleId <= 0)
                throw new InvalidOperationException("Refund requires an existing original sale.");
            var originalKind = await conn.ExecuteScalarAsync<int?>(
                "SELECT kind FROM sales WHERE id = @originalSaleId;",
                new { originalSaleId = req.OriginalSaleId },
                tx).ConfigureAwait(false);
            if (originalKind != (int)SaleKind.Sale)
                throw new InvalidOperationException("Refund original sale is missing or incoherent.");

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
INSERT INTO sales(code, createdAt, kind, related_sale_id, voided_by_sale_id, voided_at, reason, total, paidCash, paidCard, change, receipt_shop_snapshot)
VALUES(@Code, @CreatedAt, @Kind, @RelatedSaleId, @VoidedBySaleId, @VoidedAt, @Reason, @Total, @PaidCash, @PaidCard, @Change, @ReceiptShopSnapshotJson);
SELECT last_insert_rowid();", sale, tx).ConfigureAwait(false);

            return saleId;
        }

        public async Task<long> InsertRefundOrVoidAsync(
            Sale refundSale,
            IReadOnlyList<SaleLine> refundLines,
            long? originalSaleIdToMarkVoided,
            string auditAction,
            Func<long, string> auditDetailsFactory)
        {
            if (refundSale == null) throw new ArgumentNullException(nameof(refundSale));
            if (refundLines == null) throw new ArgumentNullException(nameof(refundLines));

            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                await ValidateReversalBoundaryAsync(conn, tx, refundSale, refundLines).ConfigureAwait(false);
                var saleId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(code, createdAt, kind, related_sale_id, reason, total, paidCash, paidCard, change, receipt_shop_snapshot)
VALUES(@Code, @CreatedAt, @Kind, @RelatedSaleId, @Reason, @Total, @PaidCash, @PaidCard, @Change, @ReceiptShopSnapshotJson);
SELECT last_insert_rowid();", refundSale, tx).ConfigureAwait(false);

                refundSale.Id = saleId;
                refundSale.ClientSaleId = await EnsureClientSaleIdAsync(conn, tx, saleId).ConfigureAwait(false);

                foreach (var line in refundLines)
                {
                    line.SaleId = saleId;
                }

                await InsertSaleLinesAsync(conn, tx, refundLines).ConfigureAwait(false);
                await ApplyLocalStockMovementsAsync(conn, tx, refundSale, refundLines).ConfigureAwait(false);
                await EnqueueSalesSyncOutboxAsync(conn, tx, saleId, refundSale.ClientSaleId).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(auditAction))
                {
                    await new AuditLogRepository()
                        .AppendAsync(
                            conn,
                            tx,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            auditAction,
                            auditDetailsFactory == null ? string.Empty : auditDetailsFactory(saleId))
                        .ConfigureAwait(false);
                }

                if (originalSaleIdToMarkVoided.HasValue)
                {
                    await MarkSaleVoidedAsync(
                        conn,
                        tx,
                        originalSaleIdToMarkVoided.Value,
                        saleId,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ConfigureAwait(false);
                }

                tx.Commit();
                return saleId;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
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
            var idempotencyKey = normalizedClientSaleId + ":" + PosOnlineContract.SalesSchemaVersion;
            var origin = await OutboxShopBinding.ResolveRequiredAsync(conn, tx).ConfigureAwait(false);
            var saleKind = await conn.ExecuteScalarAsync<int>(
                "SELECT kind FROM sales WHERE id = @saleId;",
                new { saleId },
                tx).ConfigureAwait(false);
            var operationType = GetOperationType(saleKind);
            var persistedSale = await conn.QuerySingleAsync<Sale>(@"
SELECT id, client_sale_id AS ClientSaleId, code, createdAt, kind,
       related_sale_id AS RelatedSaleId, voided_by_sale_id AS VoidedBySaleId,
       voided_at AS VoidedAt, reason, total, paidCash, paidCard, change,
       operator_id AS OperatorId, COALESCE(pdf_printed, 0) AS PdfPrinted,
       sync_status AS SyncStatus
FROM sales
WHERE id = @saleId;",
                new { saleId },
                tx).ConfigureAwait(false);
            var persistedLines = (await conn.QueryAsync<SaleLine>(@"
SELECT id, saleId, productId, barcode, name, quantity, unitPrice, lineTotal,
       related_original_line_id AS RelatedOriginalLineId
FROM sale_lines
WHERE saleId = @saleId
ORDER BY id ASC;",
                new { saleId },
                tx).ConfigureAwait(false)).ToArray();
            var productIds = persistedLines
                .Where(line => line.ProductId.HasValue)
                .Select(line => line.ProductId.Value)
                .Distinct()
                .ToArray();
            var remoteProductIds = productIds.Length == 0
                ? new Dictionary<long, string>()
                : (await conn.QueryAsync<RemoteProductIdRow>(@"
SELECT id AS ProductId, remote_product_id AS RemoteProductId
FROM products
WHERE id IN @productIds;",
                    new { productIds },
                    tx).ConfigureAwait(false))
                    .Where(row => !string.IsNullOrWhiteSpace(row.RemoteProductId))
                    .ToDictionary(row => row.ProductId, row => row.RemoteProductId);
            var canonicalItem = new SalesSyncOutboxItem
            {
                ClientSaleId = normalizedClientSaleId,
                IdempotencyKey = idempotencyKey,
                OperationType = operationType,
                OriginShopCode = origin.ShopCode,
                OriginShopId = origin.ShopId,
                SaleId = saleId,
                SchemaVersion = PosOnlineContract.SalesSchemaVersion
            };
            ReversalEconomicsResult reversalEconomics = null;
            if (persistedSale.Kind == (int)SaleKind.Refund ||
                persistedSale.Kind == (int)SaleKind.Void)
            {
                if (!persistedSale.RelatedSaleId.HasValue)
                {
                    throw new InvalidOperationException(ReversalEconomicsPolicy.InvalidOriginalCode);
                }

                var snapshot = await PosReversalEconomicsReader
                    .LoadAsync(conn, tx, persistedSale.RelatedSaleId.Value, persistedSale.Id)
                    .ConfigureAwait(false);
                reversalEconomics = ReversalEconomicsPolicy.Calculate(
                    snapshot,
                    ReversalEconomicsPolicy.CalculateItemGross(persistedLines));
            }

            var canonicalRequest = PosSalesSyncRequestBuilder.BuildCanonical(
                canonicalItem,
                persistedSale,
                persistedLines,
                remoteProductIds,
                reversalEconomics);
            var clientBatchId = canonicalRequest.Batch.ClientBatchId;
            var payloadJson = PosSalesSyncRequestBuilder.SerializeCanonical(canonicalRequest);
            var payloadHash = PosSalesSyncRequestBuilder.Sha256Hex(payloadJson);

            await conn.ExecuteAsync(@"
INSERT OR IGNORE INTO sales_sync_outbox(
  sale_id, client_sale_id, client_batch_id, idempotency_key, schema_version, operation_type,
  origin_shop_id, origin_shop_code, payload_json, payload_hash, status, created_at, updated_at)
VALUES(
  @saleId, @clientSaleId, @clientBatchId, @idempotencyKey, @schemaVersion, @operationType,
  @originShopId, @originShopCode, @payloadJson, @payloadHash, 'pending', @nowMs, @nowMs);

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
                    clientBatchId,
                    idempotencyKey,
                    schemaVersion = PosOnlineContract.SalesSchemaVersion,
                    operationType,
                    originShopId = origin.ShopId,
                    originShopCode = origin.ShopCode,
                    payloadHash,
                    payloadJson,
                    nowMs
                }, tx).ConfigureAwait(false);

            var existing = await conn.QuerySingleAsync<SalesSyncBindingRow>(@"
SELECT
  client_sale_id AS ClientSaleId,
  client_batch_id AS ClientBatchId,
  idempotency_key AS IdempotencyKey,
  schema_version AS SchemaVersion,
  operation_type AS OperationType,
  origin_shop_id AS OriginShopId,
  origin_shop_code AS OriginShopCode,
  payload_hash AS PayloadHash,
  payload_json AS PayloadJson
FROM sales_sync_outbox
WHERE sale_id = @saleId;",
                new { saleId },
                tx).ConfigureAwait(false);
            if (!string.Equals(existing.ClientSaleId, normalizedClientSaleId, StringComparison.Ordinal) ||
                !string.Equals(existing.IdempotencyKey, idempotencyKey, StringComparison.Ordinal) ||
                !string.Equals(existing.SchemaVersion, PosOnlineContract.SalesSchemaVersion, StringComparison.Ordinal) ||
                !string.Equals(existing.OperationType, operationType, StringComparison.Ordinal) ||
                !string.Equals(existing.OriginShopId ?? string.Empty, origin.ShopId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existing.OriginShopCode, origin.ShopCode, StringComparison.Ordinal) ||
                !string.Equals(existing.ClientBatchId, clientBatchId, StringComparison.Ordinal) ||
                !string.Equals(existing.PayloadJson, payloadJson, StringComparison.Ordinal) ||
                !string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Sales sync outbox idempotency conflict.");
            }
        }

        public async Task<IReadOnlyList<SalesSyncOutboxItem>> GetPendingSalesSyncOutboxAsync(int take, long nowMs)
        {
            if (take <= 0) take = 1;
            if (take > 50) take = 50;

            using var conn = _factory.Open();
            var staleInProgressBefore = nowMs - SalesSyncInProgressLeaseMilliseconds;
            var rows = await conn.QueryAsync<SalesSyncOutboxItem>(@"
SELECT
  id AS Id,
  sale_id AS SaleId,
  client_sale_id AS ClientSaleId,
  client_batch_id AS ClientBatchId,
  idempotency_key AS IdempotencyKey,
  schema_version AS SchemaVersion,
  operation_type AS OperationType,
  origin_shop_id AS OriginShopId,
  origin_shop_code AS OriginShopCode,
  payload_json AS PayloadJson,
  payload_hash AS PayloadHash,
  status AS Status,
  attempt_count AS AttemptCount,
  COALESCE(last_attempt_at, updated_at, 0) AS LeaseObservedAt,
  next_retry_at AS NextRetryAt,
  last_error_code AS LastErrorCode
FROM sales_sync_outbox
WHERE (
    status IN ('pending', 'retry')
    AND next_retry_at <= @nowMs
  )
  OR (
    status = 'in_progress'
    AND COALESCE(last_attempt_at, updated_at, 0) <= @staleInProgressBefore
  )
ORDER BY id ASC
LIMIT @take",
                new { take, nowMs, staleInProgressBefore }).ConfigureAwait(false);
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
                InProgress = CountFor("in_progress"),
                LastAckedAt = lastAckedAt,
                Pending = CountFor("pending"),
                Retry = CountFor("retry")
            };
        }

        public async Task<bool> HasUnresolvedSalesSyncOutboxAsync()
        {
            using var conn = _factory.Open();
            var count = await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM sales_sync_outbox
WHERE status IN ('pending', 'retry', 'in_progress', 'failed_blocked');").ConfigureAwait(false);

            return count > 0;
        }

        public async Task<bool> IsReversalDependencyReadyAsync(long saleId)
        {
            using var conn = _factory.Open();
            var row = await conn.QuerySingleOrDefaultAsync<ReversalDependencyRow>(@"
SELECT
  current.kind AS Kind,
  current.related_sale_id AS RelatedSaleId,
  original.kind AS OriginalKind,
  original_outbox.status AS OriginalOutboxStatus,
  original_outbox.origin_shop_id AS OriginalOriginShopId,
  original_outbox.origin_shop_code AS OriginalOriginShopCode,
  current_outbox.origin_shop_id AS CurrentOriginShopId,
  current_outbox.origin_shop_code AS CurrentOriginShopCode,
  official_id.value AS OfficialShopId,
  official_code.value AS OfficialShopCode,
  (
    SELECT COUNT(1)
    FROM sales prior
    LEFT JOIN sales_sync_outbox prior_outbox ON prior_outbox.sale_id = prior.id
    WHERE prior.related_sale_id = current.related_sale_id
      AND prior.kind IN (@kindRefund, @kindVoid)
      AND prior.id < current.id
      AND COALESCE(prior_outbox.status, '') <> 'acked'
  ) AS PriorReversalNotAcked
FROM sales current
LEFT JOIN sales original ON original.id = current.related_sale_id
LEFT JOIN sales_sync_outbox original_outbox ON original_outbox.sale_id = original.id
LEFT JOIN sales_sync_outbox current_outbox ON current_outbox.sale_id = current.id
LEFT JOIN app_settings official_id ON official_id.key = 'pos.official_shop.shop_id'
LEFT JOIN app_settings official_code ON official_code.key = 'pos.official_shop.shop_code'
WHERE current.id = @saleId;",
                new
                {
                    saleId,
                    kindRefund = (int)SaleKind.Refund,
                    kindVoid = (int)SaleKind.Void
                }).ConfigureAwait(false);
            if (row == null)
            {
                return false;
            }

            if (row.Kind != (int)SaleKind.Refund && row.Kind != (int)SaleKind.Void)
            {
                return true;
            }

            return row.RelatedSaleId.HasValue &&
                row.OriginalKind == (int)SaleKind.Sale &&
                string.Equals(row.OriginalOutboxStatus, "acked", StringComparison.Ordinal) &&
                row.PriorReversalNotAcked == 0 &&
                string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                    row.OriginalOriginShopId,
                    row.OriginalOriginShopCode,
                    row.CurrentOriginShopId,
                    row.CurrentOriginShopCode)) &&
                string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                    row.OriginalOriginShopId,
                    row.OriginalOriginShopCode,
                    row.OfficialShopId,
                    row.OfficialShopCode));
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

        public async Task<bool> PrepareSalesSyncAttemptAsync(
            long outboxId,
            string clientBatchId,
            string payloadJson,
            string payloadHash,
            long nowMs,
            int expectedAttemptCount)
        {
            using var conn = _factory.Open();
            var staleInProgressBefore = nowMs - SalesSyncInProgressLeaseMilliseconds;
            var rows = await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET status = 'in_progress',
    attempt_count = attempt_count + 1,
    last_attempt_at = @nowMs,
    updated_at = @nowMs
WHERE id = @outboxId
  AND attempt_count = @expectedAttemptCount
  AND client_batch_id = @clientBatchId
  AND payload_json = @payloadJson
  AND payload_hash = @payloadHash
  AND (
    (
      status IN ('pending', 'retry')
      AND next_retry_at <= @nowMs
    )
    OR (
      status = 'in_progress'
      AND COALESCE(last_attempt_at, updated_at, 0) <= @staleInProgressBefore
    )
  );",
                new
                {
                    outboxId,
                    clientBatchId,
                    payloadJson,
                    payloadHash,
                    nowMs,
                    expectedAttemptCount,
                    staleInProgressBefore
                }).ConfigureAwait(false);
            return rows == 1;
        }

        public async Task<bool> MarkSalesSyncAckedAsync(
            long outboxId,
            long saleId,
            string serverBatchId,
            string serverSaleId,
            long nowMs,
            int expectedAttemptCount)
        {
            using var conn = await _factory.OpenAsync().ConfigureAwait(false);
            using var tx = conn.BeginTransaction();
            var rows = await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET status = 'acked',
    server_batch_id = @serverBatchId,
    server_sale_id = @serverSaleId,
    last_error_code = NULL,
    last_error_at = NULL,
    updated_at = @nowMs
WHERE id = @outboxId
  AND status = 'in_progress'
  AND attempt_count = @expectedAttemptCount;",
                new { outboxId, saleId, serverBatchId, serverSaleId, nowMs, expectedAttemptCount }, tx).ConfigureAwait(false);
            if (rows != 1)
            {
                tx.Rollback();
                return false;
            }

            await conn.ExecuteAsync(
                "UPDATE sales SET sync_status = 'acked' WHERE id = @saleId;",
                new { saleId },
                tx).ConfigureAwait(false);
            tx.Commit();
            return true;
        }

        public async Task<bool> MarkSalesSyncRetryAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount)
        {
            using var conn = await _factory.OpenAsync().ConfigureAwait(false);
            using var tx = conn.BeginTransaction();
            var rows = await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET status = 'retry',
    next_retry_at = @nextRetryAt,
    last_error_code = @errorCode,
    last_error_at = @nowMs,
    updated_at = @nowMs
WHERE id = @outboxId
  AND status = 'in_progress'
  AND attempt_count = @expectedAttemptCount;",
                new { outboxId, saleId, errorCode, nextRetryAt, nowMs, expectedAttemptCount }, tx).ConfigureAwait(false);
            if (rows != 1)
            {
                tx.Rollback();
                return false;
            }

            await conn.ExecuteAsync(
                "UPDATE sales SET sync_status = 'retry' WHERE id = @saleId;",
                new { saleId },
                tx).ConfigureAwait(false);
            tx.Commit();
            return true;
        }

        public async Task<bool> MarkSalesSyncBlockedAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nowMs,
            int expectedAttemptCount)
        {
            using var conn = await _factory.OpenAsync().ConfigureAwait(false);
            using var tx = conn.BeginTransaction();
            var rows = await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET status = 'failed_blocked',
    last_error_code = @errorCode,
    last_error_at = @nowMs,
    updated_at = @nowMs
WHERE id = @outboxId
  AND status = 'in_progress'
  AND attempt_count = @expectedAttemptCount;",
                new { outboxId, saleId, errorCode, nowMs, expectedAttemptCount }, tx).ConfigureAwait(false);
            if (rows != 1)
            {
                tx.Rollback();
                return false;
            }

            await conn.ExecuteAsync(
                "UPDATE sales SET sync_status = 'blocked' WHERE id = @saleId;",
                new { saleId },
                tx).ConfigureAwait(false);
            tx.Commit();
            return true;
        }

        public async Task<bool> MarkSalesSyncOriginBlockedAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nowMs,
            string expectedStatus,
            int expectedAttemptCount,
            long expectedLeaseObservedAt)
        {
            var normalizedExpectedStatus = (expectedStatus ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedExpectedStatus != "pending" &&
                normalizedExpectedStatus != "retry" &&
                normalizedExpectedStatus != "in_progress")
            {
                return false;
            }

            using var conn = await _factory.OpenAsync().ConfigureAwait(false);
            using var tx = conn.BeginTransaction();
            var rows = await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET status = 'failed_blocked',
    last_error_code = @errorCode,
    last_error_at = @nowMs,
    updated_at = @nowMs
WHERE id = @outboxId
  AND sale_id = @saleId
  AND status = @normalizedExpectedStatus
  AND attempt_count = @expectedAttemptCount
  AND COALESCE(last_attempt_at, updated_at, 0) = @expectedLeaseObservedAt;",
                new
                {
                    outboxId,
                    saleId,
                    errorCode,
                    nowMs,
                    normalizedExpectedStatus,
                    expectedAttemptCount,
                    expectedLeaseObservedAt
                },
                tx).ConfigureAwait(false);
            if (rows == 1)
            {
                await conn.ExecuteAsync(
                    "UPDATE sales SET sync_status = 'blocked' WHERE id = @saleId;",
                    new { saleId },
                    tx).ConfigureAwait(false);
            }

            tx.Commit();
            return rows == 1;
        }

        private static string GetOperationType(int saleKind)
        {
            return saleKind == (int)SaleKind.Void
                ? "void"
                : saleKind == (int)SaleKind.Refund
                    ? "refund"
                    : "sale";
        }

        private static bool IsReversalEconomicsCode(string code)
        {
            return string.Equals(code, ReversalEconomicsPolicy.InvalidOriginalCode, StringComparison.Ordinal) ||
                string.Equals(code, ReversalEconomicsPolicy.InvalidHistoryCode, StringComparison.Ordinal) ||
                string.Equals(code, ReversalEconomicsPolicy.PriorSyncUnresolvedCode, StringComparison.Ordinal) ||
                string.Equals(code, ReversalEconomicsPolicy.MismatchCode, StringComparison.Ordinal);
        }

        private static string BuildClientSaleId(long saleId)
        {
            return "win7pos-sale-" + saleId.ToString(CultureInfo.InvariantCulture);
        }

        private static async Task ValidateReversalBoundaryAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            Sale sale,
            IReadOnlyList<SaleLine> lines)
        {
            if (sale == null) throw new ArgumentNullException(nameof(sale));
            if (sale.Kind != (int)SaleKind.Refund && sale.Kind != (int)SaleKind.Void)
            {
                return;
            }

            if (!sale.RelatedSaleId.HasValue || sale.RelatedSaleId.Value <= 0)
            {
                throw new InvalidOperationException("Reversal requires an existing original sale.");
            }

            var originalKind = await conn.ExecuteScalarAsync<int?>(
                "SELECT kind FROM sales WHERE id = @originalSaleId;",
                new { originalSaleId = sale.RelatedSaleId.Value },
                tx).ConfigureAwait(false);
            if (originalKind != (int)SaleKind.Sale)
            {
                throw new InvalidOperationException("Reversal original sale is missing or incoherent.");
            }

            var ackedOrigin = await conn.QuerySingleOrDefaultAsync<OriginalAckBindingRow>(@"
SELECT
  status AS Status,
  origin_shop_id AS OriginShopId,
  origin_shop_code AS OriginShopCode
FROM sales_sync_outbox
WHERE sale_id = @originalSaleId;",
                new { originalSaleId = sale.RelatedSaleId.Value },
                tx).ConfigureAwait(false);
            if (string.Equals(ackedOrigin?.Status, "acked", StringComparison.Ordinal))
            {
                var official = await OutboxShopBinding.ResolveRequiredAsync(conn, tx).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                    ackedOrigin.OriginShopId,
                    ackedOrigin.OriginShopCode,
                    official.ShopId,
                    official.ShopCode)))
                {
                    throw new InvalidOperationException("Reversal original ACK belongs to a different official shop.");
                }
            }

            var reversalLines = (lines ?? Array.Empty<SaleLine>()).ToArray();
            if (reversalLines.Length == 0 || reversalLines.Any(line =>
                line == null ||
                line.Quantity <= 0 ||
                line.UnitPrice < 0 ||
                DiscountKeys.IsEconomicAdjustment(line.Barcode) ||
                !line.RelatedOriginalLineId.HasValue ||
                line.RelatedOriginalLineId.Value <= 0))
            {
                throw new InvalidOperationException(ReversalEconomicsPolicy.MismatchCode);
            }

            var originalLineIds = reversalLines
                .Select(line => line.RelatedOriginalLineId.Value)
                .Distinct()
                .ToArray();
            var originalLines = (await conn.QueryAsync<BoundaryOriginalLineRow>(@"
SELECT
  original.id AS OriginalLineId,
  original.quantity AS SoldQty,
  original.unitPrice AS UnitPrice,
  original.barcode AS Barcode,
  COALESCE(SUM(CASE WHEN reversal_sale.id IS NOT NULL THEN ABS(reversal.quantity) ELSE 0 END), 0) AS ReversedQty
FROM sale_lines original
LEFT JOIN sale_lines reversal ON reversal.related_original_line_id = original.id
LEFT JOIN sales reversal_sale ON reversal_sale.id = reversal.saleId
  AND reversal_sale.related_sale_id = @originalSaleId
  AND reversal_sale.kind IN (@kindRefund, @kindVoid)
WHERE original.saleId = @originalSaleId
  AND original.id IN @originalLineIds
GROUP BY original.id, original.quantity, original.unitPrice, original.barcode;",
                    new
                    {
                        originalSaleId = sale.RelatedSaleId.Value,
                        originalLineIds,
                        kindRefund = (int)SaleKind.Refund,
                        kindVoid = (int)SaleKind.Void
                    },
                    tx).ConfigureAwait(false)).ToArray();
            if (originalLines.Length != originalLineIds.Length)
            {
                throw new InvalidOperationException("Reversal lines do not belong to the original sale.");
            }

            var originalById = originalLines.ToDictionary(line => line.OriginalLineId);
            foreach (var group in reversalLines.GroupBy(line => line.RelatedOriginalLineId.Value))
            {
                var source = originalById[group.Key];
                long requestedQty;
                try
                {
                    requestedQty = group.Aggregate(
                        0L,
                        (total, line) => checked(total + (long)line.Quantity));
                }
                catch (OverflowException)
                {
                    throw new InvalidOperationException(ReversalEconomicsPolicy.MismatchCode);
                }

                if (DiscountKeys.IsEconomicAdjustment(source.Barcode) ||
                    group.Any(line => line.UnitPrice != source.UnitPrice) ||
                    requestedQty > source.SoldQty - source.ReversedQty)
                {
                    throw new InvalidOperationException(ReversalEconomicsPolicy.MismatchCode);
                }
            }

            var snapshot = await PosReversalEconomicsReader
                .LoadAsync(conn, tx, sale.RelatedSaleId.Value, null)
                .ConfigureAwait(false);
            var economics = ReversalEconomicsPolicy.Calculate(
                snapshot,
                ReversalEconomicsPolicy.CalculateItemGross(reversalLines));
            long paidTotal;
            try
            {
                paidTotal = checked(sale.PaidCash + sale.PaidCard);
            }
            catch (OverflowException)
            {
                throw new InvalidOperationException(ReversalEconomicsPolicy.MismatchCode);
            }

            if (sale.Total != economics.NetClp ||
                sale.PaidCash > 0 ||
                sale.PaidCard > 0 ||
                sale.Change != 0 ||
                paidTotal != sale.Total)
            {
                throw new InvalidOperationException(ReversalEconomicsPolicy.MismatchCode);
            }
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
        public long InProgress { get; set; }
        public long? LastAckedAt { get; set; }
        public long Pending { get; set; }
        public long Retry { get; set; }
        public long PendingOrRetry => Pending + Retry + InProgress;
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
        public string SchemaVersion { get; set; }
        public string OperationType { get; set; }
        public string OriginShopId { get; set; }
        public string OriginShopCode { get; set; }
        public string PayloadJson { get; set; }
        public string PayloadHash { get; set; }
        public string Status { get; set; }
        public int AttemptCount { get; set; }
        public long LeaseObservedAt { get; set; }
        public long NextRetryAt { get; set; }
        public string LastErrorCode { get; set; }
    }

    internal sealed class SalesSyncBindingRow
    {
        public string ClientBatchId { get; set; }
        public string ClientSaleId { get; set; }
        public string IdempotencyKey { get; set; }
        public string OperationType { get; set; }
        public string OriginShopCode { get; set; }
        public string OriginShopId { get; set; }
        public string PayloadHash { get; set; }
        public string PayloadJson { get; set; }
        public string SchemaVersion { get; set; }
    }

    internal sealed class RemoteProductIdRow
    {
        public long ProductId { get; set; }
        public string RemoteProductId { get; set; }
    }

    internal sealed class ReversalDependencyRow
    {
        public int Kind { get; set; }
        public long? RelatedSaleId { get; set; }
        public int? OriginalKind { get; set; }
        public string OriginalOutboxStatus { get; set; }
        public string OriginalOriginShopId { get; set; }
        public string OriginalOriginShopCode { get; set; }
        public string CurrentOriginShopId { get; set; }
        public string CurrentOriginShopCode { get; set; }
        public string OfficialShopId { get; set; }
        public string OfficialShopCode { get; set; }
        public long PriorReversalNotAcked { get; set; }
    }

    internal sealed class PersistedReversalRow
    {
        public int Kind { get; set; }
        public long? RelatedSaleId { get; set; }
        public long SaleTotal { get; set; }
    }

    internal sealed class BoundaryOriginalLineRow
    {
        public string Barcode { get; set; }
        public long OriginalLineId { get; set; }
        public long ReversedQty { get; set; }
        public long SoldQty { get; set; }
        public long UnitPrice { get; set; }
    }

    internal sealed class OriginalAckBindingRow
    {
        public string Status { get; set; }
        public string OriginShopId { get; set; }
        public string OriginShopCode { get; set; }
    }
}
