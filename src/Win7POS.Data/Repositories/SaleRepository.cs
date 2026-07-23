using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Pos;
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

        private readonly SaleReadRepository _reads;
        private readonly SaleLineReadRepository _lineReads;
        private readonly SaleStockMovementWriter _stockMovementWriter;
        private readonly SaleReversalWriter _reversalWriter;
        private readonly SalesSyncOutboxRepository _salesSyncOutbox;
        private readonly SaleTransactionWriter _transactionWriter;

        public SaleRepository(SqliteConnectionFactory factory)
        {
            _reads = new SaleReadRepository(factory);
            _lineReads = new SaleLineReadRepository(factory);
            _stockMovementWriter = new SaleStockMovementWriter();
            _reversalWriter = new SaleReversalWriter(factory);
            _salesSyncOutbox = new SalesSyncOutboxRepository(
                factory,
                SalesSyncInProgressLeaseMilliseconds);
            _transactionWriter = new SaleTransactionWriter(
                factory,
                _stockMovementWriter,
                _reversalWriter,
                _salesSyncOutbox);
        }

        public Task<long> InsertSaleAsync(Sale sale, IReadOnlyList<SaleLine> lines) =>
            _transactionWriter.InsertSaleAsync(sale, lines);

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
        public Task MarkPdfPrintedAsync(long saleId) =>
            _transactionWriter.MarkPdfPrintedAsync(saleId);

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

        public Task<long> InsertRefundSaleAsync(
            RefundCreateRequest req,
            long totalMinor,
            long paidCashMinor,
            long paidCardMinor,
            long changeMinor) =>
            _transactionWriter.InsertRefundSaleAsync(
                req,
                totalMinor,
                paidCashMinor,
                paidCardMinor,
                changeMinor);

        public Task<long> InsertRefundSaleAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            RefundCreateRequest req,
            long totalMinor,
            long paidCashMinor,
            long paidCardMinor,
            long changeMinor) =>
            _transactionWriter.InsertRefundSaleAsync(
                conn,
                tx,
                req,
                totalMinor,
                paidCashMinor,
                paidCardMinor,
                changeMinor);

        public Task<long> InsertRefundOrVoidAsync(
            Sale refundSale,
            IReadOnlyList<SaleLine> refundLines,
            long? originalSaleIdToMarkVoided,
            string auditAction,
            Func<long, string> auditDetailsFactory) =>
            _transactionWriter.InsertRefundOrVoidAsync(
                refundSale,
                refundLines,
                originalSaleIdToMarkVoided,
                auditAction,
                auditDetailsFactory);

        public Task InsertSaleLinesAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            IReadOnlyList<SaleLine> lines) =>
            _transactionWriter.InsertSaleLinesAsync(conn, tx, lines);

        public Task<string> EnsureClientSaleIdAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long saleId) =>
            _transactionWriter.EnsureClientSaleIdAsync(conn, tx, saleId);

        public Task ApplyLocalStockMovementsAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            Sale sale,
            IReadOnlyList<SaleLine> lines) =>
            _transactionWriter.ApplyLocalStockMovementsAsync(conn, tx, sale, lines);

        public Task EnqueueSalesSyncOutboxAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long saleId,
            string clientSaleId) =>
            _transactionWriter.EnqueueSalesSyncOutboxAsync(conn, tx, saleId, clientSaleId);

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

        internal Task ValidateReversalBoundaryAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            Sale sale,
            IReadOnlyList<SaleLine> lines) =>
            _reversalWriter.ValidateReversalBoundaryAsync(conn, tx, sale, lines);

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
