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
using Win7POS.Core.Receipt;
using Win7POS.Data.Online;

namespace Win7POS.Data.Repositories
{
    public enum ReversalDependencyState
    {
        Ready = 0,
        Wait = 1,
        PermanentBlock = 2
    }

    public sealed class ReversalDependencyDecision
    {
        public ReversalDependencyDecision(ReversalDependencyState state, string code)
        {
            State = state;
            Code = code ?? string.Empty;
        }

        public string Code { get; }
        public ReversalDependencyState State { get; }
    }

    public sealed class SaleRepository
    {
        public const long SalesSyncInProgressLeaseMilliseconds = 15 * 60 * 1000L;

        private readonly SqliteConnectionFactory _factory;
        private readonly SaleReadRepository _reads;
        private readonly SaleLineReadRepository _lineReads;
        private readonly SaleStockMovementWriter _stockMovementWriter;
        private readonly SalesSyncOutboxRepository _salesSyncOutbox;

        public SaleRepository(SqliteConnectionFactory factory)
        {
            _factory = factory;
            _reads = new SaleReadRepository(factory);
            _lineReads = new SaleLineReadRepository(factory);
            _stockMovementWriter = new SaleStockMovementWriter();
            _salesSyncOutbox = new SalesSyncOutboxRepository(
                factory,
                SalesSyncInProgressLeaseMilliseconds);
        }

        public async Task<long> InsertSaleAsync(Sale sale, IReadOnlyList<SaleLine> lines)
        {
            if (sale == null) throw new ArgumentNullException(nameof(sale));
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            SalesReceiptContentPolicy.EnsureValid(sale, lines);
            ReceiptDocumentPolicy.EnsureValidSnapshotJson(sale.ReceiptShopSnapshotJson);
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

        public Task<IReadOnlyList<Sale>> LastSalesAsync(int take = 5) =>
            _reads.LastSalesAsync(take);

        public Task<IReadOnlyList<Sale>> GetSalesBetweenAsync(
            long fromMs,
            long toMs,
            int? operatorId = null,
            bool includeFiscalPrinted = true) =>
            _reads.GetSalesBetweenAsync(fromMs, toMs, operatorId, includeFiscalPrinted);

        public Task<DailySalesSummary> GetDailySummaryAsync(DateTime date, bool includeFiscalPrinted = true) =>
            _reads.GetDailySummaryAsync(date, includeFiscalPrinted);

        public Task<IReadOnlyList<DailySalesSummary>> GetDailySummariesAsync(
            DateTime fromDate,
            DateTime toDate,
            bool includeFiscalPrinted = true) =>
            _reads.GetDailySummariesAsync(fromDate, toDate, includeFiscalPrinted);

        /// <summary>Report range veloce: una sola query aggregata + riempimento giorni mancanti.</summary>
        public Task<IReadOnlyList<DailySalesSummary>> GetDailySummariesRangeAsync(
            DateTime fromDate,
            DateTime toDate,
            bool includeFiscalPrinted = true) =>
            _reads.GetDailySummariesRangeAsync(fromDate, toDate, includeFiscalPrinted);

        public Task<IReadOnlyList<Sale>> GetSalesForDateAsync(DateTime date, bool includeFiscalPrinted = true) =>
            _reads.GetSalesForDateAsync(date, includeFiscalPrinted);

        /// <summary>Vendite per fascia oraria (0-23) del giorno, solo vendite (kind=0).</summary>
        public Task<IReadOnlyList<long>> GetHourlySalesAsync(DateTime date, bool includeFiscalPrinted = true) =>
            _reads.GetHourlySalesAsync(date, includeFiscalPrinted);

        public Task<Sale> GetByIdAsync(long saleId) => _reads.GetByIdAsync(saleId);

        /// <summary>Imposta pdf_printed=1 come stato documentale locale senza rimuovere la vendita da report o sync.</summary>
        public Task MarkPdfPrintedAsync(long saleId)
        {
            using var conn = _factory.Open();
            return conn.ExecuteAsync(
                "UPDATE sales SET pdf_printed = 1 WHERE id = @saleId",
                new { saleId });
        }

        public Task<IReadOnlyList<Sale>> GetByCodeLikeAsync(string codeFilter, bool includeFiscalPrinted = true) =>
            _reads.GetByCodeLikeAsync(codeFilter, includeFiscalPrinted);

        public Task<IReadOnlyList<SaleLine>> GetLinesBySaleIdAsync(long saleId) =>
            _lineReads.GetLinesBySaleIdAsync(saleId);

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
            if (req == null || req.OriginalSaleId <= 0)
                throw new InvalidOperationException("Refund requires an existing original sale.");
            SalesReceiptContentPolicy.EnsureValidSaleReason(req.Reason);
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
            SalesReceiptContentPolicy.EnsureValidSaleReason(req.Reason);
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
            SalesReceiptContentPolicy.EnsureValid(sale, Array.Empty<SaleLine>());

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
            SalesReceiptContentPolicy.EnsureValid(refundSale, refundLines);
            ReceiptDocumentPolicy.EnsureValidSnapshotJson(refundSale.ReceiptShopSnapshotJson);

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
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (tx == null) throw new ArgumentNullException(nameof(tx));
            if (!ReferenceEquals(tx.Connection, conn))
            {
                throw new ArgumentException(
                    "The sale-line transaction must belong to the supplied connection.",
                    nameof(tx));
            }
            SalesReceiptContentPolicy.EnsureValidLines(lines);
            if (lines == null || lines.Count == 0) return;
            foreach (var group in lines.GroupBy(line => line.SaleId))
            {
                var appended = group.ToList();
                var budget = await SaleLineReadRepository
                    .EnsureStoredLineBudgetAsync(conn, tx, group.Key)
                    .ConfigureAwait(false);
                SalesReceiptContentPolicy.EnsureCumulativeLineBudget(
                    budget.LineCount,
                    budget.AggregateNameCharacters,
                    budget.AggregateNameUtf8Bytes,
                    appended);
            }
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

            await _stockMovementWriter
                .ApplyAsync(conn, tx, sale, lines, clientSaleId)
                .ConfigureAwait(false);
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
            await _salesSyncOutbox
                .EnqueueAsync(conn, tx, saleId, normalizedClientSaleId)
                .ConfigureAwait(false);
        }

        public Task<IReadOnlyList<SalesSyncOutboxItem>> GetPendingSalesSyncOutboxAsync(
            int take,
            long nowMs) =>
            _salesSyncOutbox.GetPendingAsync(take, nowMs);

        public Task<SalesSyncOutboxSummary> GetSalesSyncOutboxSummaryAsync() =>
            _salesSyncOutbox.GetSummaryAsync();

        public Task<OutboxDrainState> GetSalesSyncDrainStateAsync(long nowMs) =>
            _salesSyncOutbox.GetDrainStateAsync(nowMs);

        public Task<bool> HasUnresolvedSalesSyncOutboxAsync() =>
            _salesSyncOutbox.HasUnresolvedAsync();

        public async Task<bool> IsReversalDependencyReadyAsync(long saleId)
        {
            var decision = await EvaluateReversalDependencyAsync(saleId).ConfigureAwait(false);
            return decision.State == ReversalDependencyState.Ready;
        }

        public async Task<ReversalDependencyDecision> EvaluateReversalDependencyAsync(long saleId)
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
  ,(
    SELECT COUNT(1)
    FROM sales prior
    LEFT JOIN sales_sync_outbox prior_outbox ON prior_outbox.sale_id = prior.id
    WHERE prior.related_sale_id = current.related_sale_id
      AND prior.kind IN (@kindRefund, @kindVoid)
      AND prior.id < current.id
      AND (prior_outbox.status IS NULL OR prior_outbox.status = 'failed_blocked')
  ) AS PriorReversalPermanent
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
                return Dependency(ReversalDependencyState.PermanentBlock, "missing_sale");
            }

            if (row.Kind != (int)SaleKind.Refund && row.Kind != (int)SaleKind.Void)
            {
                return Dependency(ReversalDependencyState.Ready, string.Empty);
            }

            if (!row.RelatedSaleId.HasValue || row.OriginalKind != (int)SaleKind.Sale)
            {
                return Dependency(ReversalDependencyState.PermanentBlock, "original_sale_missing");
            }

            var currentBindingError = OutboxShopBinding.GetMismatchCode(
                row.OriginalOriginShopId,
                row.OriginalOriginShopCode,
                row.CurrentOriginShopId,
                row.CurrentOriginShopCode);
            if (!string.IsNullOrWhiteSpace(currentBindingError))
            {
                return Dependency(ReversalDependencyState.PermanentBlock, currentBindingError);
            }

            var officialBindingError = OutboxShopBinding.GetMismatchCode(
                row.OriginalOriginShopId,
                row.OriginalOriginShopCode,
                row.OfficialShopId,
                row.OfficialShopCode);
            if (!string.IsNullOrWhiteSpace(officialBindingError))
            {
                return Dependency(ReversalDependencyState.PermanentBlock, officialBindingError);
            }

            if (string.Equals(row.OriginalOutboxStatus, "failed_blocked", StringComparison.Ordinal))
            {
                return Dependency(ReversalDependencyState.PermanentBlock, "original_sale_blocked");
            }

            if (string.IsNullOrWhiteSpace(row.OriginalOutboxStatus))
            {
                return Dependency(ReversalDependencyState.PermanentBlock, "original_sale_outbox_missing");
            }

            if (!string.Equals(row.OriginalOutboxStatus, "acked", StringComparison.Ordinal))
            {
                return Dependency(ReversalDependencyState.Wait, "original_sale_not_acked");
            }

            if (row.PriorReversalPermanent > 0)
            {
                return Dependency(ReversalDependencyState.PermanentBlock, "prior_reversal_blocked");
            }

            if (row.PriorReversalNotAcked > 0)
            {
                return Dependency(ReversalDependencyState.Wait, "prior_reversal_not_acked");
            }

            return Dependency(ReversalDependencyState.Ready, string.Empty);
        }

        private static ReversalDependencyDecision Dependency(
            ReversalDependencyState state,
            string code)
        {
            return new ReversalDependencyDecision(state, code);
        }

        public Task<IReadOnlyDictionary<long, string>> GetRemoteProductIdsAsync(
            IEnumerable<long> productIds) =>
            _salesSyncOutbox.GetRemoteProductIdsAsync(productIds);

        public Task<bool> PrepareSalesSyncAttemptAsync(
            long outboxId,
            string clientBatchId,
            string payloadJson,
            string payloadHash,
            long nowMs,
            int expectedAttemptCount) =>
            _salesSyncOutbox.PrepareAttemptAsync(
                outboxId,
                clientBatchId,
                payloadJson,
                payloadHash,
                nowMs,
                expectedAttemptCount);

        public Task<bool> PrepareSalesSyncAttemptAsync(
            long outboxId,
            string clientBatchId,
            string payloadJson,
            string payloadHash,
            long nowMs,
            int expectedAttemptCount,
            string expectedStatus,
            long expectedNextRetryAt,
            long expectedLeaseObservedAt) =>
            _salesSyncOutbox.PrepareAttemptAsync(
                outboxId,
                clientBatchId,
                payloadJson,
                payloadHash,
                nowMs,
                expectedAttemptCount,
                expectedStatus,
                expectedNextRetryAt,
                expectedLeaseObservedAt);

        public Task<bool> PrepareSalesSyncAttemptAsync(
            long outboxId,
            string clientBatchId,
            string payloadJson,
            string payloadHash,
            long nowMs,
            int expectedAttemptCount,
            string expectedStatus,
            long expectedNextRetryAt,
            long expectedLeaseObservedAt,
            OnlineSyncGeneration generation,
            string claimToken) =>
            _salesSyncOutbox.PrepareAttemptAsync(
                outboxId,
                clientBatchId,
                payloadJson,
                payloadHash,
                nowMs,
                expectedAttemptCount,
                expectedStatus,
                expectedNextRetryAt,
                expectedLeaseObservedAt,
                generation,
                claimToken);

        public Task<bool> MarkSalesSyncAckedAsync(
            long outboxId,
            long saleId,
            string serverBatchId,
            string serverSaleId,
            long nowMs,
            int expectedAttemptCount) =>
            _salesSyncOutbox.MarkAckedAsync(
                outboxId,
                saleId,
                serverBatchId,
                serverSaleId,
                nowMs,
                expectedAttemptCount);

        public Task<bool> MarkSalesSyncAckedAsync(
            long outboxId,
            long saleId,
            string serverBatchId,
            string serverSaleId,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence fence) =>
            _salesSyncOutbox.MarkAckedAsync(
                outboxId,
                saleId,
                serverBatchId,
                serverSaleId,
                nowMs,
                expectedAttemptCount,
                fence);

        public Task<bool> MarkSalesSyncRetryAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount) =>
            _salesSyncOutbox.MarkRetryAsync(
                outboxId,
                saleId,
                errorCode,
                nextRetryAt,
                nowMs,
                expectedAttemptCount);

        public Task<bool> MarkSalesSyncRetryAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence fence) =>
            _salesSyncOutbox.MarkRetryAsync(
                outboxId,
                saleId,
                errorCode,
                nextRetryAt,
                nowMs,
                expectedAttemptCount,
                fence);

        public Task<bool> DeferSalesSyncDependencyAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount) =>
            _salesSyncOutbox.DeferDependencyAsync(
                outboxId,
                saleId,
                errorCode,
                nextRetryAt,
                nowMs,
                expectedAttemptCount);

        public Task<bool> DeferSalesSyncDependencyAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence fence) =>
            _salesSyncOutbox.DeferDependencyAsync(
                outboxId,
                saleId,
                errorCode,
                nextRetryAt,
                nowMs,
                expectedAttemptCount,
                fence);

        public Task<bool> ReleaseSalesSyncAttemptAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount) =>
            _salesSyncOutbox.ReleaseAttemptAsync(
                outboxId,
                saleId,
                errorCode,
                nextRetryAt,
                nowMs,
                expectedAttemptCount);

        public Task<bool> ReleaseSalesSyncAttemptAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence fence) =>
            _salesSyncOutbox.ReleaseAttemptAsync(
                outboxId,
                saleId,
                errorCode,
                nextRetryAt,
                nowMs,
                expectedAttemptCount,
                fence);

        public Task<bool> MarkSalesSyncBlockedAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nowMs,
            int expectedAttemptCount) =>
            _salesSyncOutbox.MarkBlockedAsync(
                outboxId,
                saleId,
                errorCode,
                nowMs,
                expectedAttemptCount);

        public Task<bool> MarkSalesSyncBlockedAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence fence) =>
            _salesSyncOutbox.MarkBlockedAsync(
                outboxId,
                saleId,
                errorCode,
                nowMs,
                expectedAttemptCount,
                fence);

        public Task<bool> MarkSalesSyncOriginBlockedAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nowMs,
            string expectedStatus,
            int expectedAttemptCount,
            long expectedLeaseObservedAt) =>
            _salesSyncOutbox.MarkOriginBlockedAsync(
                outboxId,
                saleId,
                errorCode,
                nowMs,
                expectedStatus,
                expectedAttemptCount,
                expectedLeaseObservedAt);

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
        public long PriorReversalPermanent { get; set; }
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
