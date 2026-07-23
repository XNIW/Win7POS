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
        private readonly SaleReversalWriter _reversalWriter;
        private readonly SalesSyncOutboxRepository _salesSyncOutbox;

        public SaleRepository(SqliteConnectionFactory factory)
        {
            _factory = factory;
            _reads = new SaleReadRepository(factory);
            _lineReads = new SaleLineReadRepository(factory);
            _stockMovementWriter = new SaleStockMovementWriter();
            _reversalWriter = new SaleReversalWriter(factory);
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

                await ValidateReversalBoundaryAsync(conn, tx, sale, lines)
                    .ConfigureAwait(false);

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

        public Task<bool> IsVoidedAsync(long saleId) =>
            _reversalWriter.IsVoidedAsync(saleId);

        public Task<int> GetRefundedQtyAsync(long originalSaleId, long originalLineId) =>
            _reversalWriter.GetRefundedQtyAsync(originalSaleId, originalLineId);

        public Task<List<SaleLineReturnableDto>> GetReturnableLinesAsync(long saleId) =>
            _reversalWriter.GetReturnableLinesAsync(saleId);

        public Task<ReversalEconomicsSnapshot> GetReversalEconomicsSnapshotAsync(long originalSaleId) =>
            _reversalWriter.GetReversalEconomicsSnapshotAsync(originalSaleId);

        internal Task<ReversalEconomicsSnapshot> GetReversalEconomicsSnapshotExcludingAsync(
            long originalSaleId,
            long excludedReversalSaleId) =>
            _reversalWriter.GetReversalEconomicsSnapshotExcludingAsync(
                originalSaleId,
                excludedReversalSaleId);

        public Task<string> GetPersistedReversalEconomicsErrorAsync(
            long saleId,
            PosSalesSyncRequest request) =>
            _reversalWriter.GetPersistedReversalEconomicsErrorAsync(saleId, request);

        public Task MarkSaleVoidedAsync(long originalSaleId, long refundSaleId, long nowMs) =>
            _reversalWriter.MarkSaleVoidedAsync(originalSaleId, refundSaleId, nowMs);

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
                await ValidateReversalBoundaryAsync(conn, tx, refundSale, refundLines)
                    .ConfigureAwait(false);
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

        public Task<bool> IsReversalDependencyReadyAsync(long saleId) =>
            _reversalWriter.IsReversalDependencyReadyAsync(saleId);

        public Task<ReversalDependencyDecision> EvaluateReversalDependencyAsync(long saleId) =>
            _reversalWriter.EvaluateReversalDependencyAsync(saleId);

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

        private static string BuildClientSaleId(long saleId)
        {
            return "win7pos-sale-" + saleId.ToString(CultureInfo.InvariantCulture);
        }

        internal Task ValidateReversalBoundaryAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            Sale sale,
            IReadOnlyList<SaleLine> lines) =>
            _reversalWriter.ValidateReversalBoundaryAsync(conn, tx, sale, lines);

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

        public Task MarkSaleVoidedAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long originalSaleId,
            long refundSaleId,
            long nowMs) =>
            _reversalWriter.MarkSaleVoidedAsync(
                conn,
                tx,
                originalSaleId,
                refundSaleId,
                nowMs);
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

}
