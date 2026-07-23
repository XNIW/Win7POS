using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Online
{
    public sealed class CatalogImportOutboxRepository
    {
        public const long CatalogImportInProgressLeaseMilliseconds = 15 * 60 * 1000L;

        private readonly SqliteConnectionFactory _factory;

        public CatalogImportOutboxRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<long> EnqueueAsync(CatalogImportOutboxEntry entry)
        {
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                var id = await EnqueueAsync(conn, tx, entry).ConfigureAwait(false);
                tx.Commit();
                return id;
            }
        }

        public static async Task<long> EnqueueAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            CatalogImportOutboxEntry entry)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (tx == null) throw new ArgumentNullException(nameof(tx));
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrWhiteSpace(entry.ClientImportId)) throw new ArgumentException("client import id is required.");
            if (string.IsNullOrWhiteSpace(entry.IdempotencyKey)) throw new ArgumentException("idempotency key is required.");
            if (string.IsNullOrWhiteSpace(entry.PayloadJson)) throw new ArgumentException("payload json is required.");
            if (string.IsNullOrWhiteSpace(entry.PayloadHash)) throw new ArgumentException("payload hash is required.");

            var nowMs = entry.CreatedAt > 0 ? entry.CreatedAt : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var origin = await OutboxShopBinding.ResolveRequiredAsync(conn, tx).ConfigureAwait(false);
            await conn.ExecuteAsync(@"
INSERT OR IGNORE INTO catalog_import_outbox(
  client_import_id, idempotency_key, schema_version, operation_type,
  origin_shop_id, origin_shop_code, source, payload_json, payload_hash,
  status, attempt_count, next_retry_at, created_at, updated_at)
VALUES(
  @ClientImportId, @IdempotencyKey, @SchemaVersion, 'catalog_import',
  @OriginShopId, @OriginShopCode, @Source, @PayloadJson, @PayloadHash,
  'pending', 0, 0, @NowMs, @NowMs);",
                new
                {
                    entry.ClientImportId,
                    entry.IdempotencyKey,
                    entry.SchemaVersion,
                    entry.Source,
                    entry.PayloadJson,
                    entry.PayloadHash,
                    OriginShopId = origin.ShopId,
                    OriginShopCode = origin.ShopCode,
                    NowMs = nowMs
                },
                tx).ConfigureAwait(false);

            var existing = await conn.QuerySingleAsync<CatalogImportExistingRow>(@"
SELECT
  id AS Id,
  client_import_id AS ClientImportId,
  idempotency_key AS IdempotencyKey,
  schema_version AS SchemaVersion,
  operation_type AS OperationType,
  origin_shop_id AS OriginShopId,
  origin_shop_code AS OriginShopCode,
  source AS Source,
  payload_hash AS PayloadHash
FROM catalog_import_outbox
WHERE client_import_id = @ClientImportId
   OR idempotency_key = @IdempotencyKey
ORDER BY id ASC
LIMIT 1;",
                new { entry.ClientImportId, entry.IdempotencyKey },
                tx).ConfigureAwait(false);

            if (!string.Equals(existing.ClientImportId, entry.ClientImportId, StringComparison.Ordinal) ||
                !string.Equals(existing.IdempotencyKey, entry.IdempotencyKey, StringComparison.Ordinal) ||
                !string.Equals(existing.SchemaVersion, entry.SchemaVersion, StringComparison.Ordinal) ||
                !string.Equals(existing.OperationType, "catalog_import", StringComparison.Ordinal) ||
                !string.Equals(existing.OriginShopId ?? string.Empty, origin.ShopId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existing.OriginShopCode, origin.ShopCode, StringComparison.Ordinal) ||
                !string.Equals(existing.Source, entry.Source, StringComparison.Ordinal) ||
                !string.Equals(existing.PayloadHash, entry.PayloadHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Catalog import outbox idempotency conflict.");
            }

            return existing.Id;
        }

        public async Task<IReadOnlyList<CatalogImportOutboxItem>> GetPendingAsync(int take, long nowMs)
        {
            if (take <= 0) take = 1;
            if (take > 50) take = 50;

            using (var conn = _factory.Open())
            {
                var staleInProgressBefore = nowMs - CatalogImportInProgressLeaseMilliseconds;
                var rows = await conn.QueryAsync<CatalogImportOutboxItem>(@"
SELECT
  id AS Id,
  client_import_id AS ClientImportId,
  idempotency_key AS IdempotencyKey,
  schema_version AS SchemaVersion,
  operation_type AS OperationType,
  origin_shop_id AS OriginShopId,
  origin_shop_code AS OriginShopCode,
  source AS Source,
  payload_json AS PayloadJson,
  payload_hash AS PayloadHash,
  status AS Status,
  attempt_count AS AttemptCount,
  next_retry_at AS NextRetryAt,
  COALESCE(last_attempt_at, updated_at, 0) AS LeaseObservedAt,
  last_error_code AS LastErrorCode
FROM catalog_import_outbox
WHERE (
    status IN ('pending', 'retry')
    AND next_retry_at <= @nowMs
  )
  OR (
    status = 'in_progress'
    AND COALESCE(last_attempt_at, updated_at, 0) <= @staleInProgressBefore
  )
ORDER BY id ASC
LIMIT @take;",
                    new { take, nowMs, staleInProgressBefore }).ConfigureAwait(false);
                return rows.ToList();
            }
        }

        public async Task<OutboxDrainState> GetDrainStateAsync(long nowMs)
        {
            using (var conn = _factory.Open())
            {
                var staleInProgressBefore = nowMs - CatalogImportInProgressLeaseMilliseconds;
                var state = await conn.QuerySingleAsync<CatalogImportDrainStateRow>(@"
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
FROM catalog_import_outbox;",
                    new
                    {
                        nowMs,
                        staleInProgressBefore,
                        leaseMilliseconds = CatalogImportInProgressLeaseMilliseconds
                    }).ConfigureAwait(false);
                return new OutboxDrainState(state.RemainingDue, state.NextRetryAt);
            }
        }

        public async Task<CatalogImportOutboxSummary> GetSummaryAsync()
        {
            using (var conn = _factory.Open())
            {
                var rows = (await conn.QueryAsync<CatalogImportStatusCount>(@"
SELECT status AS Status, COUNT(1) AS Count
FROM catalog_import_outbox
GROUP BY status;").ConfigureAwait(false)).ToList();

                var lastAckedAt = await conn.ExecuteScalarAsync<long?>(@"
SELECT updated_at
FROM catalog_import_outbox
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

                return new CatalogImportOutboxSummary
                {
                    Acked = CountFor("acked"),
                    Blocked = CountFor("failed_blocked"),
                    InProgress = CountFor("in_progress"),
                    LastAckedAt = lastAckedAt,
                    Pending = CountFor("pending"),
                    Retry = CountFor("retry")
                };
            }
        }

        public async Task<bool> HasUnresolvedAsync()
        {
            using (var conn = _factory.Open())
            {
                var count = await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM catalog_import_outbox
WHERE status IN ('pending', 'retry', 'in_progress', 'failed_blocked');").ConfigureAwait(false);
                return count > 0;
            }
        }

        public async Task<bool> PrepareAttemptAsync(long outboxId, long nowMs)
        {
            var item = await GetByIdForClaimAsync(outboxId).ConfigureAwait(false);
            return item != null && await PrepareAttemptAsync(item, nowMs).ConfigureAwait(false);
        }

        public async Task<bool> PrepareAttemptAsync(CatalogImportOutboxItem item, long nowMs)
        {
            return await PrepareAttemptAsync(item, nowMs, null, null).ConfigureAwait(false);
        }

        public async Task<bool> PrepareAttemptAsync(
            CatalogImportOutboxItem item,
            long nowMs,
            OnlineSyncGeneration generation,
            string claimToken)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (generation != null && string.IsNullOrWhiteSpace(claimToken))
                throw new ArgumentException("A claim token is required for generation-scoped sync.", nameof(claimToken));
            using (var conn = _factory.Open())
            {
                var staleInProgressBefore = nowMs - CatalogImportInProgressLeaseMilliseconds;
                var rows = await conn.ExecuteAsync(@"
UPDATE catalog_import_outbox
SET status = 'in_progress',
    attempt_count = attempt_count + 1,
    last_attempt_at = @nowMs,
    claim_generation_id = @generationId,
    claim_token = @claimToken,
    updated_at = @nowMs
WHERE id = @outboxId
  AND status = @expectedStatus
  AND attempt_count = @expectedAttemptCount
  AND payload_hash = @expectedPayloadHash
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
      SELECT 1 FROM pos_sync_session_generation generation
      WHERE generation.singleton_id = 1
        AND generation.active = 1
        AND generation.generation_id = @generationId
        AND generation.fingerprint = @generationFingerprint
    )
  );",
                    new
                    {
                        outboxId = item.Id,
                        nowMs,
                        staleInProgressBefore,
                        expectedStatus = item.Status,
                        expectedAttemptCount = item.AttemptCount,
                        expectedPayloadHash = item.PayloadHash,
                        expectedNextRetryAt = item.NextRetryAt,
                        expectedLeaseObservedAt = item.LeaseObservedAt,
                        generationId = generation?.GenerationId,
                        generationFingerprint = generation?.Fingerprint,
                        claimToken
                    }).ConfigureAwait(false);
                return rows == 1;
            }
        }

        public async Task<bool> MarkOriginBlockedAsync(long outboxId, string errorCode, long nowMs)
        {
            var item = await GetByIdForClaimAsync(outboxId).ConfigureAwait(false);
            if (item == null || !await PrepareAttemptAsync(item, nowMs).ConfigureAwait(false))
            {
                return false;
            }

            return await MarkBlockedAsync(
                outboxId,
                errorCode,
                nowMs,
                item.AttemptCount + 1).ConfigureAwait(false);
        }

        private async Task<CatalogImportOutboxItem> GetByIdForClaimAsync(long outboxId)
        {
            using (var conn = _factory.Open())
            {
                return await conn.QuerySingleOrDefaultAsync<CatalogImportOutboxItem>(@"
SELECT
  id AS Id,
  client_import_id AS ClientImportId,
  idempotency_key AS IdempotencyKey,
  schema_version AS SchemaVersion,
  operation_type AS OperationType,
  origin_shop_id AS OriginShopId,
  origin_shop_code AS OriginShopCode,
  source AS Source,
  payload_json AS PayloadJson,
  payload_hash AS PayloadHash,
  status AS Status,
  attempt_count AS AttemptCount,
  next_retry_at AS NextRetryAt,
  COALESCE(last_attempt_at, updated_at, 0) AS LeaseObservedAt,
  last_error_code AS LastErrorCode
FROM catalog_import_outbox
WHERE id = @outboxId;",
                    new { outboxId }).ConfigureAwait(false);
            }
        }

        public Task<bool> MarkAckedAsync(
            long outboxId,
            string serverImportId,
            string serverRequestId,
            long nowMs,
            int expectedAttemptCount)
        {
            return MarkAckedAsync(
                outboxId,
                new CatalogImportAckResult
                {
                    ServerImportId = serverImportId,
                    ServerRequestId = serverRequestId
                },
                nowMs,
                expectedAttemptCount);
        }

        public async Task<bool> MarkAckedAsync(
            long outboxId,
            CatalogImportAckResult ack,
            long nowMs,
            int expectedAttemptCount)
        {
            return await MarkAckedAsync(
                outboxId,
                ack,
                nowMs,
                expectedAttemptCount,
                null).ConfigureAwait(false);
        }

        public async Task<bool> MarkAckedAsync(
            long outboxId,
            CatalogImportAckResult ack,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence fence)
        {
            if (expectedAttemptCount <= 0) throw new ArgumentException("expected attempt count is required.");
            ValidateFenceAttempt(fence, expectedAttemptCount);
            ack = ack ?? new CatalogImportAckResult();

            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    var rows = await conn.ExecuteAsync(@"
UPDATE catalog_import_outbox
SET status = 'acked',
    server_import_id = @serverImportId,
    server_request_id = @serverRequestId,
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
                            new
                            {
                                outboxId,
                                serverImportId = NormalizeTechnicalId(ack.ServerImportId, 160),
                                serverRequestId = NormalizeTechnicalId(ack.ServerRequestId, 160),
                                nowMs,
                                expectedAttemptCount
                            },
                            fence),
                        tx).ConfigureAwait(false);

                    if (rows != 1)
                    {
                        tx.Rollback();
                        return false;
                    }

                    var idempotencyKey = await conn.ExecuteScalarAsync<string>(
                        "SELECT idempotency_key FROM catalog_import_outbox WHERE id = @outboxId",
                        new { outboxId },
                        tx).ConfigureAwait(false) ?? string.Empty;

                    await ApplyRemoteProductIdsAsync(conn, tx, ack.RemoteProductIds).ConfigureAwait(false);
                    await ApplyRemotePriceIdsAsync(conn, tx, ack.RemotePriceIds, idempotencyKey).ConfigureAwait(false);
                    var rawGeneration = await conn.ExecuteScalarAsync<string>(
                        "SELECT value FROM app_settings WHERE key = @key;",
                        new { key = CatalogShopStateRepository.ImportAckGenerationKey },
                        tx).ConfigureAwait(false);
                    var normalizedGeneration = (rawGeneration ?? string.Empty).Trim();
                    long generation;
                    if (normalizedGeneration.Length == 0)
                    {
                        generation = 0;
                    }
                    else if (!long.TryParse(
                        normalizedGeneration,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out generation) || generation < 0)
                    {
                        throw new InvalidOperationException("catalog_import_ack_generation_invalid");
                    }

                    if (generation == long.MaxValue)
                    {
                        throw new InvalidOperationException("catalog_import_ack_generation_exhausted");
                    }

                    await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value)
VALUES(@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                        new
                        {
                            key = CatalogShopStateRepository.ImportAckGenerationKey,
                            value = (generation + 1L).ToString(CultureInfo.InvariantCulture)
                        },
                        tx).ConfigureAwait(false);
                    tx.Commit();
                    return true;
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }

        public async Task<bool> MarkRetryAsync(long outboxId, string errorCode, long nextRetryAt, long nowMs, int expectedAttemptCount)
        {
            return await MarkRetryAsync(
                outboxId,
                errorCode,
                nextRetryAt,
                nowMs,
                expectedAttemptCount,
                null).ConfigureAwait(false);
        }

        public async Task<bool> MarkRetryAsync(
            long outboxId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence fence)
        {
            if (expectedAttemptCount <= 0) throw new ArgumentException("expected attempt count is required.");
            ValidateFenceAttempt(fence, expectedAttemptCount);

            using (var conn = _factory.Open())
            {
                var rows = await conn.ExecuteAsync(@"
UPDATE catalog_import_outbox
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
                        new { outboxId, errorCode, nextRetryAt, nowMs, expectedAttemptCount },
                        fence)).ConfigureAwait(false);
                return rows == 1;
            }
        }

        public async Task<bool> ReleaseAttemptAsync(
            long outboxId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount)
        {
            return await ReleaseAttemptAsync(
                outboxId,
                errorCode,
                nextRetryAt,
                nowMs,
                expectedAttemptCount,
                null).ConfigureAwait(false);
        }

        public async Task<bool> ReleaseAttemptAsync(
            long outboxId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence fence)
        {
            if (expectedAttemptCount <= 0) throw new ArgumentException("expected attempt count is required.");
            ValidateFenceAttempt(fence, expectedAttemptCount);

            using (var conn = _factory.Open())
            {
                var rows = await conn.ExecuteAsync(@"
UPDATE catalog_import_outbox
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
                        new { outboxId, errorCode, nextRetryAt, nowMs, expectedAttemptCount },
                        fence)).ConfigureAwait(false);
                return rows == 1;
            }
        }

        public async Task<bool> MarkBlockedAsync(long outboxId, string errorCode, long nowMs, int expectedAttemptCount)
        {
            return await MarkBlockedAsync(
                outboxId,
                errorCode,
                nowMs,
                expectedAttemptCount,
                null).ConfigureAwait(false);
        }

        public async Task<bool> MarkBlockedAsync(
            long outboxId,
            string errorCode,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence fence)
        {
            if (expectedAttemptCount <= 0) throw new ArgumentException("expected attempt count is required.");
            ValidateFenceAttempt(fence, expectedAttemptCount);

            using (var conn = _factory.Open())
            {
                var rows = await conn.ExecuteAsync(@"
UPDATE catalog_import_outbox
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
                        new { outboxId, errorCode, nowMs, expectedAttemptCount },
                        fence)).ConfigureAwait(false);
                return rows == 1;
            }
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

        private static async Task ApplyRemoteProductIdsAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            IReadOnlyList<CatalogImportRemoteProductId> remoteProductIds)
        {
            foreach (var mapping in DeduplicateRemoteProducts(remoteProductIds))
            {
                var rows = await conn.ExecuteAsync(@"
UPDATE products
SET remote_product_id = @RemoteProductId,
    remote_deleted_at = NULL,
    is_active = 1
WHERE barcode = @Barcode
  AND (COALESCE(remote_product_id, '') = '' OR remote_product_id = @RemoteProductId);",
                    new
                    {
                        mapping.Barcode,
                        mapping.RemoteProductId
                    },
                    tx).ConfigureAwait(false);

                if (rows <= 0)
                {
                    continue;
                }

                await conn.ExecuteAsync(@"
UPDATE products
SET is_active = 0,
    remote_deleted_at = @remoteDeletedAt
WHERE remote_product_id = @RemoteProductId
  AND barcode <> @Barcode
  AND COALESCE(is_active, 1) = 1;",
                    new
                    {
                        mapping.Barcode,
                        mapping.RemoteProductId,
                        remoteDeletedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                    },
                    tx).ConfigureAwait(false);
            }
        }

        private static async Task ApplyRemotePriceIdsAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            IReadOnlyList<CatalogImportRemotePriceId> remotePriceIds,
            string idempotencyKey)
        {
            foreach (var mapping in DeduplicateRemotePrices(remotePriceIds))
            {
                if (!string.IsNullOrWhiteSpace(mapping.ClientItemId) &&
                    !string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    var exactRows = await conn.ExecuteAsync(@"
UPDATE product_price_history
SET remote_price_id = @RemotePriceId,
    type = UPPER(TRIM(type))
WHERE id = (
    SELECT id
    FROM product_price_history
    WHERE catalog_import_idempotency_key = @IdempotencyKey
      AND catalog_import_client_item_id = @ClientItemId
      AND barcode = @Barcode
      AND COALESCE(remote_price_id, '') = ''
      AND (@PriceType = '' OR LOWER(type) = LOWER(@PriceType))
    ORDER BY id DESC
    LIMIT 1
)
AND NOT EXISTS (
    SELECT 1
    FROM product_price_history existing
    WHERE existing.remote_price_id = @RemotePriceId
);",
                        new
                        {
                            mapping.Barcode,
                            mapping.ClientItemId,
                            IdempotencyKey = idempotencyKey,
                            mapping.PriceType,
                            mapping.RemotePriceId
                        },
                        tx).ConfigureAwait(false);

                    if (exactRows > 0)
                    {
                        await EnsureAssignedRemotePriceOwnershipAsync(
                            conn,
                            tx,
                            mapping.Barcode,
                            mapping.PriceType,
                            mapping.RemotePriceId).ConfigureAwait(false);
                        continue;
                    }

                    var importRows = await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM product_price_history
WHERE catalog_import_idempotency_key = @IdempotencyKey;",
                        new { IdempotencyKey = idempotencyKey },
                        tx).ConfigureAwait(false);

                    if (importRows > 0)
                    {
                        await EnsureAssignedRemotePriceOwnershipAsync(
                            conn,
                            tx,
                            mapping.Barcode,
                            mapping.PriceType,
                            mapping.RemotePriceId).ConfigureAwait(false);
                        continue;
                    }
                }

                await conn.ExecuteAsync(@"
UPDATE product_price_history
SET remote_price_id = @RemotePriceId,
    type = UPPER(TRIM(type))
WHERE id = (
    SELECT id
    FROM product_price_history
    WHERE barcode = @Barcode
      AND COALESCE(remote_price_id, '') = ''
      AND (@PriceType = '' OR LOWER(type) = LOWER(@PriceType))
    ORDER BY
      CASE WHEN COALESCE(source, '') = 'IMPORT' THEN 0 ELSE 1 END,
      timestamp DESC,
      id DESC
    LIMIT 1
)
AND NOT EXISTS (
    SELECT 1
    FROM product_price_history existing
    WHERE existing.remote_price_id = @RemotePriceId
);",
                    new
                    {
                        mapping.Barcode,
                        mapping.PriceType,
                        mapping.RemotePriceId
                    },
                    tx).ConfigureAwait(false);
                await EnsureAssignedRemotePriceOwnershipAsync(
                    conn,
                    tx,
                    mapping.Barcode,
                    mapping.PriceType,
                    mapping.RemotePriceId).ConfigureAwait(false);
            }
        }

        private static async Task EnsureAssignedRemotePriceOwnershipAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string barcode,
            string priceType,
            string remotePriceId)
        {
            var normalizedBarcode = NormalizeKey(barcode, 80);
            var normalizedPriceType = NormalizePriceType(priceType);
            var normalizedRemotePriceId = NormalizeTechnicalId(remotePriceId, 160);
            var assignedRows = await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM product_price_history
WHERE remote_price_id = @remotePriceId
  AND barcode = @barcode
  AND (@priceType = '' OR LOWER(type) = LOWER(@priceType));",
                new
                {
                    barcode = normalizedBarcode,
                    priceType = normalizedPriceType,
                    remotePriceId = normalizedRemotePriceId
                },
                tx).ConfigureAwait(false);
            if (assignedRows != 1)
            {
                throw new InvalidOperationException("catalog_import_remote_price_mapping_unmatched");
            }

            var remoteProductIds = (await conn.QueryAsync<string>(@"
SELECT TRIM(remote_product_id)
FROM products
WHERE barcode = @barcode
  AND TRIM(COALESCE(remote_product_id, '')) <> '';",
                new { barcode = normalizedBarcode },
                tx).ConfigureAwait(false))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (remoteProductIds.Length != 1)
            {
                throw new InvalidOperationException("catalog_import_remote_price_owner_missing_or_ambiguous");
            }

            if (!await RemotePriceHistoryRepository.StoreRemotePriceOwnershipAsync(
                    conn,
                    tx,
                    normalizedRemotePriceId,
                    remoteProductIds[0]).ConfigureAwait(false))
            {
                throw new InvalidOperationException("catalog_import_remote_price_owner_conflict");
            }
        }

        private static IReadOnlyList<CatalogImportRemoteProductId> DeduplicateRemoteProducts(
            IReadOnlyList<CatalogImportRemoteProductId> remoteProductIds)
        {
            var result = new List<CatalogImportRemoteProductId>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mapping in remoteProductIds ?? Array.Empty<CatalogImportRemoteProductId>())
            {
                var barcode = NormalizeKey(mapping == null ? null : mapping.Barcode, 80);
                var remoteProductId = NormalizeTechnicalId(mapping == null ? null : mapping.RemoteProductId, 160);
                if (barcode.Length == 0 || remoteProductId.Length == 0)
                {
                    continue;
                }

                var key = barcode + "|" + remoteProductId;
                if (!seen.Add(key))
                {
                    continue;
                }

                result.Add(new CatalogImportRemoteProductId
                {
                    Barcode = barcode,
                    ClientItemId = NormalizeTechnicalId(mapping.ClientItemId, 160),
                    RemoteProductId = remoteProductId
                });
            }

            return result;
        }

        private static IReadOnlyList<CatalogImportRemotePriceId> DeduplicateRemotePrices(
            IReadOnlyList<CatalogImportRemotePriceId> remotePriceIds)
        {
            var result = new List<CatalogImportRemotePriceId>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mapping in remotePriceIds ?? Array.Empty<CatalogImportRemotePriceId>())
            {
                var barcode = NormalizeKey(mapping == null ? null : mapping.Barcode, 80);
                var remotePriceId = NormalizeTechnicalId(mapping == null ? null : mapping.RemotePriceId, 160);
                var priceType = NormalizePriceType(mapping == null ? null : mapping.PriceType);
                if (barcode.Length == 0 || remotePriceId.Length == 0)
                {
                    continue;
                }

                var key = barcode + "|" + priceType + "|" + remotePriceId;
                if (!seen.Add(key))
                {
                    continue;
                }

                result.Add(new CatalogImportRemotePriceId
                {
                    Barcode = barcode,
                    ClientItemId = NormalizeTechnicalId(mapping.ClientItemId, 160),
                    PriceType = priceType,
                    RemotePriceId = remotePriceId
                });
            }

            return result;
        }

        private static string NormalizePriceType(string value)
        {
            var normalized = NormalizeTechnicalId(value, 40).ToLowerInvariant();
            return string.Equals(normalized, "purchase", StringComparison.Ordinal) ||
                string.Equals(normalized, "retail", StringComparison.Ordinal)
                ? normalized
                : string.Empty;
        }

        private static string NormalizeKey(string value, int maxLength)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return string.Empty;
            }

            return normalized.Length > maxLength
                ? normalized.Substring(0, maxLength)
                : normalized;
        }

        private static string NormalizeTechnicalId(string value, int maxLength)
        {
            var normalized = new string((value ?? string.Empty)
                .Trim()
                .Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == ':' || ch == '.')
                .Take(maxLength)
                .ToArray());
            return normalized;
        }

        private sealed class CatalogImportStatusCount
        {
            public long Count { get; set; }
            public string Status { get; set; } = string.Empty;
        }

        private sealed class CatalogImportDrainStateRow
        {
            public long? NextRetryAt { get; set; }
            public long RemainingDue { get; set; }
        }

        private sealed class CatalogImportExistingRow
        {
            public string ClientImportId { get; set; } = string.Empty;
            public long Id { get; set; }
            public string IdempotencyKey { get; set; } = string.Empty;
            public string OperationType { get; set; } = string.Empty;
            public string OriginShopCode { get; set; } = string.Empty;
            public string OriginShopId { get; set; } = string.Empty;
            public string PayloadHash { get; set; } = string.Empty;
            public string SchemaVersion { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
        }
    }

    public sealed class CatalogImportOutboxEntry
    {
        public string ClientImportId { get; set; } = string.Empty;
        public long CreatedAt { get; set; }
        public string IdempotencyKey { get; set; } = string.Empty;
        public string PayloadHash { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = string.Empty;
        public string SchemaVersion { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }

    public sealed class CatalogImportOutboxItem
    {
        public int AttemptCount { get; set; }
        public string ClientImportId { get; set; } = string.Empty;
        public long Id { get; set; }
        public string IdempotencyKey { get; set; } = string.Empty;
        public string LastErrorCode { get; set; } = string.Empty;
        public long LeaseObservedAt { get; set; }
        public long NextRetryAt { get; set; }
        public string OperationType { get; set; } = string.Empty;
        public string OriginShopCode { get; set; } = string.Empty;
        public string OriginShopId { get; set; } = string.Empty;
        public string PayloadHash { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = string.Empty;
        public string SchemaVersion { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    public sealed class CatalogImportOutboxSummary
    {
        public long Acked { get; set; }
        public long Blocked { get; set; }
        public long InProgress { get; set; }
        public long? LastAckedAt { get; set; }
        public long Pending { get; set; }
        public long PendingOrRetry => Pending + Retry + InProgress;
        public long Retry { get; set; }
    }

    public sealed class CatalogImportAckResult
    {
        public IReadOnlyList<CatalogImportRemotePriceId> RemotePriceIds { get; set; } =
            Array.Empty<CatalogImportRemotePriceId>();
        public IReadOnlyList<CatalogImportRemoteProductId> RemoteProductIds { get; set; } =
            Array.Empty<CatalogImportRemoteProductId>();
        public string ServerImportId { get; set; } = string.Empty;
        public string ServerRequestId { get; set; } = string.Empty;
    }

    public sealed class CatalogImportRemoteProductId
    {
        public string Barcode { get; set; } = string.Empty;
        public string ClientItemId { get; set; } = string.Empty;
        public string RemoteProductId { get; set; } = string.Empty;
    }

    public sealed class CatalogImportRemotePriceId
    {
        public string Barcode { get; set; } = string.Empty;
        public string ClientItemId { get; set; } = string.Empty;
        public string PriceType { get; set; } = string.Empty;
        public string RemotePriceId { get; set; } = string.Empty;
    }
}
