using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Pos;
using Win7POS.Data.Online;

namespace Win7POS.Data.Repositories
{
    /// <summary>
    /// Owns persisted sales-sync outbox payloads, queue reads and fenced state
    /// transitions. Enqueue participates in the caller-owned sale transaction;
    /// independent queue operations retain their existing connection and
    /// transaction boundaries.
    /// </summary>
    internal sealed class SalesSyncOutboxRepository
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly long _inProgressLeaseMilliseconds;

        internal SalesSyncOutboxRepository(
            SqliteConnectionFactory factory,
            long inProgressLeaseMilliseconds)
        {
            _factory = factory;
            _inProgressLeaseMilliseconds = inProgressLeaseMilliseconds;
        }

        internal async Task EnqueueAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long saleId,
            string normalizedClientSaleId)
        {
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

        internal async Task<IReadOnlyList<SalesSyncOutboxItem>> GetPendingAsync(int take, long nowMs)
        {
            if (take <= 0) take = 1;
            if (take > 50) take = 50;

            using var conn = _factory.Open();
            var staleInProgressBefore = nowMs - _inProgressLeaseMilliseconds;
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

        internal async Task<SalesSyncOutboxSummary> GetSummaryAsync()
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

        internal async Task<OutboxDrainState> GetDrainStateAsync(long nowMs)
        {
            using var conn = _factory.Open();
            var staleInProgressBefore = nowMs - _inProgressLeaseMilliseconds;
            var state = await conn.QuerySingleAsync<SalesSyncDrainStateRow>(@"
SELECT
  COALESCE(SUM(CASE
    WHEN status IN ('pending', 'retry') AND next_retry_at <= @nowMs THEN 1
    WHEN status = 'in_progress'
      AND COALESCE(last_attempt_at, updated_at, 0) <= @staleInProgressBefore THEN 1
    ELSE 0
  END), 0) AS RemainingDue,
  MIN(CASE
    WHEN status IN ('pending', 'retry') AND next_retry_at > @nowMs
      THEN next_retry_at
    WHEN status = 'in_progress'
      AND COALESCE(last_attempt_at, updated_at, 0) > @staleInProgressBefore
      THEN COALESCE(last_attempt_at, updated_at, 0) + @leaseMilliseconds
    ELSE NULL
  END) AS NextRetryAt
FROM sales_sync_outbox;",
                new
                {
                    nowMs,
                    staleInProgressBefore,
                    leaseMilliseconds = _inProgressLeaseMilliseconds
                }).ConfigureAwait(false);
            return new OutboxDrainState(state.RemainingDue, state.NextRetryAt);
        }

        internal async Task<bool> HasUnresolvedAsync()
        {
            using var conn = _factory.Open();
            var count = await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM sales_sync_outbox
WHERE status IN ('pending', 'retry', 'in_progress', 'failed_blocked');").ConfigureAwait(false);

            return count > 0;
        }

        internal async Task<IReadOnlyDictionary<long, string>> GetRemoteProductIdsAsync(
            IEnumerable<long> productIds)
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

        internal async Task<bool> PrepareAttemptAsync(
            long outboxId,
            string clientBatchId,
            string payloadJson,
            string payloadHash,
            long nowMs,
            int expectedAttemptCount)
        {
            using (var lookup = _factory.Open())
            {
                var snapshot = await lookup.QuerySingleOrDefaultAsync<SalesSyncClaimSnapshot>(@"
SELECT
  status AS Status,
  next_retry_at AS NextRetryAt,
  COALESCE(last_attempt_at, updated_at, 0) AS LeaseObservedAt
FROM sales_sync_outbox
WHERE id = @outboxId;",
                    new { outboxId }).ConfigureAwait(false);
                if (snapshot == null)
                {
                    return false;
                }

                return await PrepareAttemptAsync(
                    outboxId,
                    clientBatchId,
                    payloadJson,
                    payloadHash,
                    nowMs,
                    expectedAttemptCount,
                    snapshot.Status,
                    snapshot.NextRetryAt,
                    snapshot.LeaseObservedAt).ConfigureAwait(false);
            }
        }

        internal async Task<bool> PrepareAttemptAsync(
            long outboxId,
            string clientBatchId,
            string payloadJson,
            string payloadHash,
            long nowMs,
            int expectedAttemptCount,
            string expectedStatus,
            long expectedNextRetryAt,
            long expectedLeaseObservedAt)
        {
            return await PrepareAttemptAsync(
                outboxId,
                clientBatchId,
                payloadJson,
                payloadHash,
                nowMs,
                expectedAttemptCount,
                expectedStatus,
                expectedNextRetryAt,
                expectedLeaseObservedAt,
                null,
                null).ConfigureAwait(false);
        }

        internal async Task<bool> PrepareAttemptAsync(
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
            string claimToken)
        {
            if (generation != null && string.IsNullOrWhiteSpace(claimToken))
                throw new ArgumentException("A claim token is required for generation-scoped sync.", nameof(claimToken));
            using var conn = _factory.Open();
            var staleInProgressBefore = nowMs - _inProgressLeaseMilliseconds;
            var rows = await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET status = 'in_progress',
    attempt_count = attempt_count + 1,
    last_attempt_at = @nowMs,
    claim_generation_id = @generationId,
    claim_token = @claimToken,
    updated_at = @nowMs
WHERE id = @outboxId
  AND attempt_count = @expectedAttemptCount
  AND client_batch_id IS @clientBatchId
  AND payload_json IS @payloadJson
  AND payload_hash IS @payloadHash
  AND status = @expectedStatus
  AND next_retry_at = @expectedNextRetryAt
  AND COALESCE(last_attempt_at, updated_at, 0) = @expectedLeaseObservedAt
  AND (
    (
      status IN ('pending', 'retry')
      AND next_retry_at <= @nowMs
    )
    OR (
      status = 'in_progress'
      AND COALESCE(last_attempt_at, updated_at, 0) <= @staleInProgressBefore
    )
  )
  AND (
    (
      @generationId IS NULL
      AND NOT EXISTS (
        SELECT 1 FROM pos_sync_session_generation current_generation
        WHERE current_generation.singleton_id = 1
          AND current_generation.active = 1
      )
    )
    OR EXISTS (
      SELECT 1
      FROM pos_sync_session_generation generation
      WHERE generation.singleton_id = 1
        AND generation.active = 1
        AND generation.generation_id = @generationId
        AND generation.fingerprint = @generationFingerprint
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
                    expectedStatus,
                    expectedNextRetryAt,
                    expectedLeaseObservedAt,
                    staleInProgressBefore,
                    generationId = generation?.GenerationId,
                    generationFingerprint = generation?.Fingerprint,
                    claimToken
                }).ConfigureAwait(false);
            return rows == 1;
        }

        internal async Task<bool> MarkAckedAsync(
            long outboxId,
            long saleId,
            string serverBatchId,
            string serverSaleId,
            long nowMs,
            int expectedAttemptCount)
        {
            return await MarkAckedAsync(
                outboxId,
                saleId,
                serverBatchId,
                serverSaleId,
                nowMs,
                expectedAttemptCount,
                null).ConfigureAwait(false);
        }

        internal async Task<bool> MarkAckedAsync(
            long outboxId,
            long saleId,
            string serverBatchId,
            string serverSaleId,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence fence)
        {
            ValidateFenceAttempt(fence, expectedAttemptCount);
            using var conn = await _factory.OpenAsync().ConfigureAwait(false);
            using var tx = conn.BeginTransaction();
            var rows = await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET status = 'acked',
    server_batch_id = @serverBatchId,
    server_sale_id = @serverSaleId,
    last_error_code = NULL,
    last_error_at = NULL,
    claim_generation_id = NULL,
    claim_token = NULL,
    updated_at = @nowMs
WHERE id = @outboxId
  AND status = 'in_progress'
  AND attempt_count = @expectedAttemptCount
  AND (
    (
      @generationId IS NULL
      AND claim_generation_id IS NULL
      AND claim_token IS NULL
      AND NOT EXISTS (
        SELECT 1 FROM pos_sync_session_generation current_generation
        WHERE current_generation.singleton_id = 1
          AND current_generation.active = 1
      )
    )
    OR (
      claim_generation_id = @generationId
      AND claim_token = @claimToken
      AND EXISTS (
        SELECT 1 FROM pos_sync_session_generation generation
        WHERE generation.singleton_id = 1
          AND generation.active = 1
          AND generation.generation_id = @generationId
          AND generation.fingerprint = @generationFingerprint
      )
    )
  );",
                FenceParameters(
                    new { outboxId, saleId, serverBatchId, serverSaleId, nowMs, expectedAttemptCount },
                    fence),
                tx).ConfigureAwait(false);
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

        internal async Task<bool> MarkRetryAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount)
        {
            return await MarkRetryAsync(
                outboxId,
                saleId,
                errorCode,
                nextRetryAt,
                nowMs,
                expectedAttemptCount,
                null).ConfigureAwait(false);
        }

        internal async Task<bool> MarkRetryAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence fence)
        {
            ValidateFenceAttempt(fence, expectedAttemptCount);
            using var conn = await _factory.OpenAsync().ConfigureAwait(false);
            using var tx = conn.BeginTransaction();
            var rows = await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET status = 'retry',
    next_retry_at = @nextRetryAt,
    last_error_code = @errorCode,
    last_error_at = @nowMs,
    claim_generation_id = NULL,
    claim_token = NULL,
    updated_at = @nowMs
WHERE id = @outboxId
  AND status = 'in_progress'
  AND attempt_count = @expectedAttemptCount
  AND (
    (
      @generationId IS NULL
      AND claim_generation_id IS NULL
      AND claim_token IS NULL
      AND NOT EXISTS (
        SELECT 1 FROM pos_sync_session_generation current_generation
        WHERE current_generation.singleton_id = 1
          AND current_generation.active = 1
      )
    )
    OR (
      claim_generation_id = @generationId
      AND claim_token = @claimToken
      AND EXISTS (
        SELECT 1 FROM pos_sync_session_generation generation
        WHERE generation.singleton_id = 1
          AND generation.active = 1
          AND generation.generation_id = @generationId
          AND generation.fingerprint = @generationFingerprint
      )
    )
  );",
                FenceParameters(
                    new { outboxId, saleId, errorCode, nextRetryAt, nowMs, expectedAttemptCount },
                    fence),
                tx).ConfigureAwait(false);
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

        internal Task<bool> DeferDependencyAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount)
        {
            return ReleaseAttemptAsync(
                outboxId,
                saleId,
                errorCode,
                nextRetryAt,
                nowMs,
                expectedAttemptCount);
        }

        internal Task<bool> DeferDependencyAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence fence)
        {
            return ReleaseAttemptAsync(
                outboxId,
                saleId,
                errorCode,
                nextRetryAt,
                nowMs,
                expectedAttemptCount,
                fence);
        }

        internal async Task<bool> ReleaseAttemptAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount)
        {
            return await ReleaseAttemptAsync(
                outboxId,
                saleId,
                errorCode,
                nextRetryAt,
                nowMs,
                expectedAttemptCount,
                null).ConfigureAwait(false);
        }

        internal async Task<bool> ReleaseAttemptAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence fence)
        {
            ValidateFenceAttempt(fence, expectedAttemptCount);
            using var conn = await _factory.OpenAsync().ConfigureAwait(false);
            using var tx = conn.BeginTransaction();
            var rows = await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET status = 'retry',
    attempt_count = attempt_count - 1,
    next_retry_at = @nextRetryAt,
    last_attempt_at = NULL,
    last_error_code = @errorCode,
    last_error_at = @nowMs,
    claim_generation_id = NULL,
    claim_token = NULL,
    updated_at = @nowMs
WHERE id = @outboxId
  AND status = 'in_progress'
  AND attempt_count = @expectedAttemptCount
  AND attempt_count > 0
  AND (
    (
      @generationId IS NULL
      AND claim_generation_id IS NULL
      AND claim_token IS NULL
      AND NOT EXISTS (
        SELECT 1 FROM pos_sync_session_generation current_generation
        WHERE current_generation.singleton_id = 1
          AND current_generation.active = 1
      )
    )
    OR (
      claim_generation_id = @generationId
      AND claim_token = @claimToken
      AND EXISTS (
        SELECT 1 FROM pos_sync_session_generation generation
        WHERE generation.singleton_id = 1
          AND generation.active = 1
          AND generation.generation_id = @generationId
          AND generation.fingerprint = @generationFingerprint
      )
    )
  );",
                FenceParameters(
                    new { outboxId, saleId, errorCode, nextRetryAt, nowMs, expectedAttemptCount },
                    fence),
                tx).ConfigureAwait(false);
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

        internal async Task<bool> MarkBlockedAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nowMs,
            int expectedAttemptCount)
        {
            return await MarkBlockedAsync(
                outboxId,
                saleId,
                errorCode,
                nowMs,
                expectedAttemptCount,
                null).ConfigureAwait(false);
        }

        internal async Task<bool> MarkBlockedAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence fence)
        {
            ValidateFenceAttempt(fence, expectedAttemptCount);
            using var conn = await _factory.OpenAsync().ConfigureAwait(false);
            using var tx = conn.BeginTransaction();
            var rows = await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET status = 'failed_blocked',
    last_error_code = @errorCode,
    last_error_at = @nowMs,
    claim_generation_id = NULL,
    claim_token = NULL,
    updated_at = @nowMs
WHERE id = @outboxId
  AND status = 'in_progress'
  AND attempt_count = @expectedAttemptCount
  AND (
    (
      @generationId IS NULL
      AND claim_generation_id IS NULL
      AND claim_token IS NULL
      AND NOT EXISTS (
        SELECT 1 FROM pos_sync_session_generation current_generation
        WHERE current_generation.singleton_id = 1
          AND current_generation.active = 1
      )
    )
    OR (
      claim_generation_id = @generationId
      AND claim_token = @claimToken
      AND EXISTS (
        SELECT 1 FROM pos_sync_session_generation generation
        WHERE generation.singleton_id = 1
          AND generation.active = 1
          AND generation.generation_id = @generationId
          AND generation.fingerprint = @generationFingerprint
      )
    )
  );",
                FenceParameters(
                    new { outboxId, saleId, errorCode, nowMs, expectedAttemptCount },
                    fence),
                tx).ConfigureAwait(false);
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

        internal async Task<bool> MarkOriginBlockedAsync(
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

        private static DynamicParameters FenceParameters(
            object values,
            OnlineSyncAttemptFence fence)
        {
            var parameters = new DynamicParameters(values);
            parameters.Add("generationId", fence?.Generation.GenerationId);
            parameters.Add("generationFingerprint", fence?.Generation.Fingerprint);
            parameters.Add("claimToken", fence?.ClaimToken);
            return parameters;
        }

        private static void ValidateFenceAttempt(
            OnlineSyncAttemptFence fence,
            int expectedAttemptCount)
        {
            if (fence != null && fence.ExpectedAttemptCount != expectedAttemptCount)
            {
                throw new ArgumentException(
                    "The sync fence attempt does not match the requested transition.",
                    nameof(fence));
            }
        }

        private static string GetOperationType(int saleKind)
        {
            return saleKind == (int)SaleKind.Void
                ? "void"
                : saleKind == (int)SaleKind.Refund
                    ? "refund"
                    : "sale";
        }

        private sealed class SalesSyncStatusCount
        {
            public long Count { get; set; }
            public string Status { get; set; }
        }

        private sealed class SalesSyncDrainStateRow
        {
            public long? NextRetryAt { get; set; }
            public long RemainingDue { get; set; }
        }

        private sealed class SalesSyncClaimSnapshot
        {
            public long LeaseObservedAt { get; set; }
            public long NextRetryAt { get; set; }
            public string Status { get; set; }
        }

        private sealed class SalesSyncBindingRow
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

        private sealed class RemoteProductIdRow
        {
            public long ProductId { get; set; }
            public string RemoteProductId { get; set; }
        }
    }
}
