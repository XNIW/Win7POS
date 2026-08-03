using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public static class CustomerOrderInboxStates
    {
        public const string Acked = "acked";
        public const string AckInProgress = "ack_in_progress";
        public const string AckPending = "ack_pending";
        public const string FailedBlocked = "failed_blocked";
        public const string Received = "received";
        public const string RetryWait = "retry_wait";
    }

    public sealed class CustomerOrderInboxItem
    {
        public int AckAttemptCount { get; set; }
        public string AckClaimToken { get; set; }
        public long AckExpectedStatusVersion { get; set; }
        public string AckIdempotencyKey { get; set; }
        public string AckOutcome { get; set; }
        public string AckPosSaleId { get; set; }
        public long CurrentStatusVersion { get; set; }
        public string HandoffId { get; set; }
        public long Id { get; set; }
        public string LeaseToken { get; set; }
        public string OrderId { get; set; }
        public string OrderStatus { get; set; }
        public string ShopId { get; set; }
        public string State { get; set; }
        public long TotalClp { get; set; }
    }

    public sealed class CustomerOrderInboxPersistResult
    {
        public CustomerOrderInboxPersistResult(int inserted, int replayed, int dueAcks)
        {
            Inserted = Math.Max(0, inserted);
            Replayed = Math.Max(0, replayed);
            DueAcks = Math.Max(0, dueAcks);
        }

        public int DueAcks { get; }
        public int Inserted { get; }
        public int Replayed { get; }
    }

    public sealed class CustomerOrderInboxDrainState
    {
        public int DueAcks { get; set; }
        public long? NextRetryAt { get; set; }
        public int Unresolved { get; set; }
    }

    /// <summary>
    /// Durable privacy-bounded inbox for customer-order operational snapshots.
    /// It never inserts sales. A fiscal reference can only be queued after an
    /// already acknowledged local sale from the same shop has a server sale ID.
    /// </summary>
    public sealed class CustomerOrderInboxRepository
    {
        public const int MaximumRetainedRows = 2000;
        public const int MaximumUnresolvedRows = 1000;
        public const int MaximumAckAttempts = 12;
        public const long AckClaimLeaseMilliseconds = 60 * 1000L;
        public const long RetentionMilliseconds = 90L * 24L * 60L * 60L * 1000L;

        private readonly SqliteConnectionFactory _factory;

        public CustomerOrderInboxRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<CustomerOrderInboxPersistResult> PersistClaimAsync(
            IReadOnlyList<PosCustomerOrderHandoff> handoffs,
            OnlineSyncGeneration generation,
            long nowMs)
        {
            if (handoffs == null) throw new ArgumentNullException(nameof(handoffs));
            if (generation == null) throw new ArgumentNullException(nameof(generation));
            if (nowMs < 0) throw new ArgumentOutOfRangeException(nameof(nowMs));

            using (var connection = await _factory.OpenAsync().ConfigureAwait(false))
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                if (!await OnlineSyncGenerationRepository.IsCurrentAndActiveAsync(
                        connection,
                        transaction,
                        generation).ConfigureAwait(false))
                {
                    transaction.Rollback();
                    throw new OnlineSyncGenerationChangedException();
                }

                await PruneAsync(connection, transaction, nowMs).ConfigureAwait(false);
                var unresolved = await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM customer_order_inbox
WHERE state <> 'acked';",
                    transaction: transaction).ConfigureAwait(false);
                var inserted = 0;
                var replayed = 0;

                foreach (var handoff in handoffs)
                {
                    var validation = PosCustomerOrderHandoffCodec.ValidateHandoff(
                        handoff,
                        generation,
                        nowMs);
                    if (validation.Length > 0)
                    {
                        throw new InvalidDataException(validation);
                    }

                    var payloadJson = PosCustomerOrderHandoffCodec.SerializePersistent(
                        handoff);
                    var payloadHash = PosCustomerOrderHandoffCodec.Sha256(payloadJson);
                    var existing = await connection.QuerySingleOrDefaultAsync<ExistingRow>(@"
SELECT
  id AS Id,
  handoff_id AS HandoffId,
  event_idempotency_key AS EventIdempotencyKey,
  payload_hash AS PayloadHash,
  state AS State
FROM customer_order_inbox
WHERE handoff_id = @handoffId
   OR event_idempotency_key = @eventIdempotencyKey
LIMIT 1;",
                        new
                        {
                            handoffId = NormalizeUuid(handoff.HandoffId),
                            eventIdempotencyKey = NormalizeUuid(
                                handoff.EventIdempotencyKey)
                        },
                        transaction).ConfigureAwait(false);

                    if (existing != null)
                    {
                        if (!string.Equals(
                                existing.HandoffId,
                                NormalizeUuid(handoff.HandoffId),
                                StringComparison.Ordinal) ||
                            !string.Equals(
                                existing.EventIdempotencyKey,
                                NormalizeUuid(handoff.EventIdempotencyKey),
                                StringComparison.Ordinal) ||
                            !string.Equals(
                                existing.PayloadHash,
                                payloadHash,
                                StringComparison.Ordinal))
                        {
                            await connection.ExecuteAsync(@"
UPDATE customer_order_inbox
SET state = 'failed_blocked',
    ack_last_error_code = 'customer_order_payload_conflict',
    updated_at = @nowMs
WHERE id = @id
  AND state <> 'acked';",
                                new { id = existing.Id, nowMs },
                                transaction).ConfigureAwait(false);
                            throw new InvalidDataException(
                                "customer_order_payload_conflict");
                        }

                        var accepted = RequiresDeliveryAck(handoff);
                        await connection.ExecuteAsync(@"
UPDATE customer_order_inbox
SET lease_token = @leaseToken,
    lease_expires_at = @leaseExpiresAt,
    remote_attempt_count = @remoteAttemptCount,
    state = CASE
      WHEN @accepted = 1 AND state = 'received' THEN 'ack_pending'
      ELSE state
    END,
    ack_outcome = CASE
      WHEN @accepted = 1 AND state = 'received' THEN 'accepted'
      ELSE ack_outcome
    END,
    ack_idempotency_key = CASE
      WHEN @accepted = 1 AND state = 'received'
        THEN lower(hex(randomblob(4))) || '-' ||
             lower(hex(randomblob(2))) || '-4' ||
             substr(lower(hex(randomblob(2))), 2) || '-' ||
             substr('89ab', abs(random()) % 4 + 1, 1) ||
             substr(lower(hex(randomblob(2))), 2) || '-' ||
             lower(hex(randomblob(6)))
      ELSE ack_idempotency_key
    END,
    ack_expected_status_version = CASE
      WHEN @accepted = 1 AND state = 'received' THEN status_version
      ELSE ack_expected_status_version
    END,
    ack_next_retry_at = CASE
      WHEN @accepted = 1 AND state = 'received' THEN 0
      ELSE ack_next_retry_at
    END,
    updated_at = @nowMs
WHERE id = @id;",
                            new
                            {
                                accepted = accepted ? 1 : 0,
                                existing.Id,
                                leaseExpiresAt = ParseTimestampMs(
                                    handoff.LeaseExpiresAt),
                                leaseToken = NormalizeUuid(handoff.LeaseToken),
                                remoteAttemptCount = handoff.AttemptCount,
                                nowMs
                            },
                            transaction).ConfigureAwait(false);
                        replayed += 1;
                    }
                    else
                    {
                        if (unresolved + inserted >= MaximumUnresolvedRows)
                        {
                            throw new InvalidOperationException(
                                "customer_order_inbox_capacity");
                        }

                        var accepted = RequiresDeliveryAck(handoff);
                        var ackIdempotencyKey = accepted
                            ? Guid.NewGuid().ToString("D").ToLowerInvariant()
                            : null;
                        await connection.ExecuteAsync(@"
INSERT INTO customer_order_inbox(
  handoff_id,
  event_idempotency_key,
  order_id,
  shop_id,
  order_code,
  event_type,
  correlation_id,
  status_version,
  current_status_version,
  order_status,
  fulfillment_mode,
  currency_code,
  total_clp,
  payload_json,
  payload_hash,
  lease_token,
  lease_expires_at,
  remote_attempt_count,
  state,
  ack_outcome,
  ack_idempotency_key,
  ack_expected_status_version,
  received_at,
  updated_at)
VALUES(
  @handoffId,
  @eventIdempotencyKey,
  @orderId,
  @shopId,
  @orderCode,
  @eventType,
  @correlationId,
  @statusVersion,
  @currentStatusVersion,
  @orderStatus,
  @fulfillmentMode,
  @currencyCode,
  @totalClp,
  @payloadJson,
  @payloadHash,
  @leaseToken,
  @leaseExpiresAt,
  @remoteAttemptCount,
  @state,
  @ackOutcome,
  @ackIdempotencyKey,
  @ackExpectedStatusVersion,
  @nowMs,
  @nowMs);",
                            new
                            {
                                ackExpectedStatusVersion = accepted
                                    ? (long?)handoff.Order.StatusVersion
                                    : null,
                                ackIdempotencyKey,
                                ackOutcome = accepted ? "accepted" : null,
                                correlationId = NormalizeBounded(
                                    handoff.CorrelationId,
                                    160),
                                currencyCode = handoff.Order.CurrencyCode,
                                currentStatusVersion = handoff.Order.CurrentStatusVersion,
                                eventIdempotencyKey = NormalizeUuid(
                                    handoff.EventIdempotencyKey),
                                eventType = handoff.EventType,
                                fulfillmentMode = handoff.Order.FulfillmentMode,
                                handoffId = NormalizeUuid(handoff.HandoffId),
                                leaseExpiresAt = ParseTimestampMs(
                                    handoff.LeaseExpiresAt),
                                leaseToken = NormalizeUuid(handoff.LeaseToken),
                                nowMs,
                                orderCode = handoff.Order.OrderCode,
                                orderId = NormalizeUuid(handoff.Order.OrderId),
                                orderStatus = handoff.Order.Status,
                                payloadHash,
                                payloadJson,
                                remoteAttemptCount = handoff.AttemptCount,
                                shopId = NormalizeUuid(handoff.Order.ShopId),
                                state = accepted
                                    ? CustomerOrderInboxStates.AckPending
                                    : CustomerOrderInboxStates.Received,
                                statusVersion = handoff.Order.StatusVersion,
                                totalClp = handoff.Order.TotalClp
                            },
                            transaction).ConfigureAwait(false);
                        inserted += 1;
                    }

                    await connection.ExecuteAsync(@"
UPDATE customer_order_inbox
SET current_status_version = @currentStatusVersion,
    order_status = @orderStatus,
    updated_at = @nowMs
WHERE shop_id = @shopId
  AND order_id = @orderId
  AND current_status_version <= @currentStatusVersion;",
                        new
                        {
                            currentStatusVersion = handoff.Order.CurrentStatusVersion,
                            nowMs,
                            orderId = NormalizeUuid(handoff.Order.OrderId),
                            orderStatus = handoff.Order.Status,
                            shopId = NormalizeUuid(handoff.Order.ShopId)
                        },
                        transaction).ConfigureAwait(false);
                }

                var due = await DueAckCountAsync(
                    connection,
                    transaction,
                    nowMs).ConfigureAwait(false);
                transaction.Commit();
                return new CustomerOrderInboxPersistResult(inserted, replayed, due);
            }
        }

        public async Task<CustomerOrderInboxItem> ClaimNextAckAsync(
            OnlineSyncGeneration generation,
            long nowMs)
        {
            if (generation == null) throw new ArgumentNullException(nameof(generation));
            using (var connection = await _factory.OpenAsync().ConfigureAwait(false))
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                if (!await OnlineSyncGenerationRepository.IsCurrentAndActiveAsync(
                        connection,
                        transaction,
                        generation).ConfigureAwait(false))
                {
                    transaction.Rollback();
                    throw new OnlineSyncGenerationChangedException();
                }

                var staleBefore = nowMs - AckClaimLeaseMilliseconds;
                var candidate = await connection.QuerySingleOrDefaultAsync<CustomerOrderInboxItem>(@"
SELECT
  id AS Id,
  handoff_id AS HandoffId,
  order_id AS OrderId,
  shop_id AS ShopId,
  order_status AS OrderStatus,
  current_status_version AS CurrentStatusVersion,
  total_clp AS TotalClp,
  lease_token AS LeaseToken,
  state AS State,
  ack_outcome AS AckOutcome,
  ack_idempotency_key AS AckIdempotencyKey,
  ack_pos_sale_id AS AckPosSaleId,
  ack_expected_status_version AS AckExpectedStatusVersion,
  ack_attempt_count AS AckAttemptCount
FROM customer_order_inbox
WHERE ack_outcome IS NOT NULL
  AND ack_attempt_count < @maximumAttempts
  AND (
    (state IN ('ack_pending', 'retry_wait') AND ack_next_retry_at <= @nowMs)
    OR
    (state = 'ack_in_progress' AND ack_last_attempt_at <= @staleBefore)
  )
ORDER BY ack_next_retry_at, id
LIMIT 1;",
                    new
                    {
                        maximumAttempts = MaximumAckAttempts,
                        nowMs,
                        staleBefore
                    },
                    transaction).ConfigureAwait(false);
                if (candidate == null)
                {
                    transaction.Rollback();
                    return null;
                }

                var claimToken = OnlineSyncAttemptFence.CreateClaimToken();
                var rows = await connection.ExecuteAsync(@"
UPDATE customer_order_inbox
SET state = 'ack_in_progress',
    ack_attempt_count = ack_attempt_count + 1,
    ack_last_attempt_at = @nowMs,
    ack_claim_generation_id = @generationId,
    ack_claim_generation_fingerprint = @generationFingerprint,
    ack_claim_token = @claimToken,
    updated_at = @nowMs
WHERE id = @id
  AND state = @expectedState
  AND ack_attempt_count = @expectedAttemptCount;",
                    new
                    {
                        claimToken,
                        generationFingerprint = generation.Fingerprint,
                        generationId = generation.GenerationId,
                        id = candidate.Id,
                        expectedAttemptCount = candidate.AckAttemptCount,
                        expectedState = candidate.State,
                        nowMs
                    },
                    transaction).ConfigureAwait(false);
                if (rows != 1)
                {
                    transaction.Rollback();
                    return null;
                }

                candidate.AckAttemptCount += 1;
                candidate.AckClaimToken = claimToken;
                candidate.State = CustomerOrderInboxStates.AckInProgress;
                transaction.Commit();
                return candidate;
            }
        }

        public async Task<bool> MarkAckedAsync(
            CustomerOrderInboxItem item,
            PosCustomerOrderAckResponse response,
            OnlineSyncGeneration generation,
            long nowMs)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (generation == null) throw new ArgumentNullException(nameof(generation));
            using (var connection = await _factory.OpenAsync().ConfigureAwait(false))
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                var rows = await connection.ExecuteAsync(@"
UPDATE customer_order_inbox
SET state = 'acked',
    accepted_acked_at = CASE
      WHEN ack_outcome = 'accepted'
        AND event_type = 'customer_order.accepted.v1'
        THEN COALESCE(accepted_acked_at, @nowMs)
      ELSE accepted_acked_at
    END,
    current_status_version = @serverStatusVersion,
    order_status = @serverStatus,
    ack_server_status = @serverStatus,
    ack_server_status_version = @serverStatusVersion,
    ack_last_error_code = NULL,
    ack_next_retry_at = 0,
    ack_claim_generation_id = NULL,
    ack_claim_generation_fingerprint = NULL,
    ack_claim_token = NULL,
    acked_at = @nowMs,
    updated_at = @nowMs
WHERE id = @id
  AND state = 'ack_in_progress'
  AND ack_attempt_count = @expectedAttemptCount
  AND ack_claim_generation_id = @generationId
  AND ack_claim_generation_fingerprint = @generationFingerprint
  AND ack_claim_token = @claimToken
  AND EXISTS (
    SELECT 1
    FROM pos_sync_session_generation generation
    WHERE generation.singleton_id = 1
      AND generation.active = 1
      AND generation.generation_id = @generationId
      AND generation.fingerprint = @generationFingerprint
  );",
                    new
                    {
                        claimToken = item.AckClaimToken,
                        expectedAttemptCount = item.AckAttemptCount,
                        generationFingerprint = generation.Fingerprint,
                        generationId = generation.GenerationId,
                        id = item.Id,
                        nowMs,
                        serverStatus = response.OrderStatus,
                        serverStatusVersion = response.OrderStatusVersion
                    },
                    transaction).ConfigureAwait(false);
                if (rows != 1)
                {
                    transaction.Rollback();
                    return false;
                }
                transaction.Commit();
                return true;
            }
        }

        public async Task<bool> MarkAttemptFailureAsync(
            CustomerOrderInboxItem item,
            OnlineSyncGeneration generation,
            string code,
            long nowMs,
            bool retryable,
            bool requireFreshLease)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (generation == null) throw new ArgumentNullException(nameof(generation));
            var normalizedCode = NormalizeCode(code);
            var blocked = !retryable || item.AckAttemptCount >= MaximumAckAttempts;
            var nextState = blocked
                ? CustomerOrderInboxStates.FailedBlocked
                : requireFreshLease
                    ? CustomerOrderInboxStates.Received
                    : CustomerOrderInboxStates.RetryWait;
            var nextRetryAt = blocked || requireFreshLease
                ? 0L
                : nowMs + Math.Min(300L, 10L * item.AckAttemptCount) * 1000L;

            using (var connection = await _factory.OpenAsync().ConfigureAwait(false))
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                var rows = await connection.ExecuteAsync(@"
UPDATE customer_order_inbox
SET state = @nextState,
    ack_next_retry_at = @nextRetryAt,
    ack_last_error_code = @normalizedCode,
    ack_claim_generation_id = NULL,
    ack_claim_generation_fingerprint = NULL,
    ack_claim_token = NULL,
    updated_at = @nowMs
WHERE id = @id
  AND state = 'ack_in_progress'
  AND ack_attempt_count = @expectedAttemptCount
  AND ack_claim_generation_id = @generationId
  AND ack_claim_generation_fingerprint = @generationFingerprint
  AND ack_claim_token = @claimToken;",
                    new
                    {
                        claimToken = item.AckClaimToken,
                        expectedAttemptCount = item.AckAttemptCount,
                        generationFingerprint = generation.Fingerprint,
                        generationId = generation.GenerationId,
                        id = item.Id,
                        nextRetryAt,
                        nextState,
                        normalizedCode,
                        nowMs
                    },
                    transaction).ConfigureAwait(false);
                if (rows != 1)
                {
                    transaction.Rollback();
                    return false;
                }
                transaction.Commit();
                return true;
            }
        }

        public async Task<bool> QueueOutcomeAsync(
            long inboxId,
            string outcome,
            long? localSaleId,
            long nowMs)
        {
            var normalizedOutcome = NormalizeOutcome(outcome);
            if (normalizedOutcome == "accepted")
                throw new ArgumentException("Accepted is queued by claim persistence.", nameof(outcome));
            if (localSaleId.HasValue && normalizedOutcome != "completed")
                throw new ArgumentException("Only completion may reference a sale.", nameof(localSaleId));

            using (var connection = await _factory.OpenAsync().ConfigureAwait(false))
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                var row = await connection.QuerySingleOrDefaultAsync<OutcomeRow>(@"
SELECT
  id AS Id,
  shop_id AS ShopId,
  total_clp AS TotalClp,
  state AS State,
  order_status AS OrderStatus,
  current_status_version AS CurrentStatusVersion,
  accepted_acked_at AS AcceptedAckedAt,
  ack_outcome AS AckOutcome,
  ack_pos_sale_id AS AckPosSaleId,
  event_type AS EventType
FROM customer_order_inbox
WHERE id = @inboxId;",
                    new { inboxId },
                    transaction).ConfigureAwait(false);
                if (row == null || !row.AcceptedAckedAt.HasValue ||
                    !string.Equals(
                        row.EventType,
                        "customer_order.accepted.v1",
                        StringComparison.Ordinal))
                {
                    transaction.Rollback();
                    return false;
                }
                if (!string.Equals(row.State, CustomerOrderInboxStates.Acked, StringComparison.Ordinal))
                {
                    var alreadyQueued = string.Equals(
                        row.AckOutcome,
                        normalizedOutcome,
                        StringComparison.Ordinal);
                    transaction.Rollback();
                    return alreadyQueued;
                }
                if (!OutcomeAllowed(normalizedOutcome, row.OrderStatus))
                {
                    transaction.Rollback();
                    return false;
                }

                string serverSaleId = null;
                if (localSaleId.HasValue)
                {
                    serverSaleId = await connection.ExecuteScalarAsync<string>(@"
SELECT outbox.server_sale_id
FROM sales sale
JOIN sales_sync_outbox outbox ON outbox.sale_id = sale.id
WHERE sale.id = @localSaleId
  AND sale.kind = @saleKind
  AND sale.total = @totalClp
  AND sale.sync_status = 'acked'
  AND outbox.status = 'acked'
  AND outbox.operation_type = 'sale'
  AND outbox.origin_shop_id = @shopId
  AND outbox.server_sale_id IS NOT NULL
LIMIT 1;",
                        new
                        {
                            localSaleId = localSaleId.Value,
                            saleKind = (int)SaleKind.Sale,
                            shopId = row.ShopId,
                            totalClp = row.TotalClp
                        },
                        transaction).ConfigureAwait(false);
                    if (!IsUuid(serverSaleId))
                    {
                        transaction.Rollback();
                        return false;
                    }
                    serverSaleId = NormalizeUuid(serverSaleId);
                }

                var idempotencyKey = Guid.NewGuid().ToString("D").ToLowerInvariant();
                var rows = await connection.ExecuteAsync(@"
UPDATE customer_order_inbox
SET state = 'ack_pending',
    ack_outcome = @normalizedOutcome,
    ack_idempotency_key = @idempotencyKey,
    ack_pos_sale_id = @serverSaleId,
    ack_expected_status_version = current_status_version,
    ack_attempt_count = 0,
    ack_next_retry_at = 0,
    ack_last_attempt_at = NULL,
    ack_last_error_code = NULL,
    ack_server_status = NULL,
    ack_server_status_version = NULL,
    ack_claim_generation_id = NULL,
    ack_claim_generation_fingerprint = NULL,
    ack_claim_token = NULL,
    acked_at = NULL,
    updated_at = @nowMs
WHERE id = @inboxId
  AND state = 'acked'
  AND accepted_acked_at IS NOT NULL;",
                    new
                    {
                        idempotencyKey,
                        inboxId,
                        normalizedOutcome,
                        nowMs,
                        serverSaleId
                    },
                    transaction).ConfigureAwait(false);
                if (rows != 1)
                {
                    transaction.Rollback();
                    return false;
                }
                transaction.Commit();
                return true;
            }
        }

        public async Task<CustomerOrderInboxDrainState> GetDrainStateAsync(long nowMs)
        {
            using (var connection = await _factory.OpenAsync().ConfigureAwait(false))
            {
                var staleBefore = nowMs - AckClaimLeaseMilliseconds;
                return await connection.QuerySingleAsync<CustomerOrderInboxDrainState>(@"
SELECT
  SUM(CASE WHEN ack_outcome IS NOT NULL AND (
    (state IN ('ack_pending', 'retry_wait') AND ack_next_retry_at <= @nowMs)
    OR (state = 'ack_in_progress' AND ack_last_attempt_at <= @staleBefore)
  ) THEN 1 ELSE 0 END) AS DueAcks,
  MIN(CASE WHEN state = 'retry_wait' THEN ack_next_retry_at END) AS NextRetryAt,
  SUM(CASE WHEN state <> 'acked' THEN 1 ELSE 0 END) AS Unresolved
FROM customer_order_inbox;",
                    new { nowMs, staleBefore }).ConfigureAwait(false);
            }
        }

        private static async Task<int> DueAckCountAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long nowMs)
        {
            return (int)await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM customer_order_inbox
WHERE ack_outcome IS NOT NULL
  AND ack_attempt_count < @maximumAttempts
  AND (
    (state IN ('ack_pending', 'retry_wait') AND ack_next_retry_at <= @nowMs)
    OR
    (state = 'ack_in_progress' AND
      ack_last_attempt_at <= @staleBefore)
  );",
                new
                {
                    maximumAttempts = MaximumAckAttempts,
                    nowMs,
                    staleBefore = nowMs - AckClaimLeaseMilliseconds
                },
                transaction).ConfigureAwait(false);
        }

        private static async Task PruneAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long nowMs)
        {
            await connection.ExecuteAsync(@"
DELETE FROM customer_order_inbox
WHERE state = 'acked'
  AND acked_at IS NOT NULL
  AND acked_at < @retainedAfter;

DELETE FROM customer_order_inbox
WHERE id IN (
  SELECT id
  FROM customer_order_inbox
  WHERE state = 'acked'
  ORDER BY acked_at DESC, id DESC
  LIMIT -1 OFFSET @maximumRows
);",
                new
                {
                    maximumRows = MaximumRetainedRows,
                    retainedAfter = Math.Max(0L, nowMs - RetentionMilliseconds)
                },
                transaction).ConfigureAwait(false);
        }

        private static bool RequiresDeliveryAck(PosCustomerOrderHandoff handoff)
        {
            return handoff != null;
        }

        private static bool OutcomeAllowed(string outcome, string orderStatus)
        {
            if (outcome == "rejected" || outcome == "prepared")
                return string.Equals(orderStatus, "accepted", StringComparison.Ordinal);
            return outcome == "completed" &&
                (string.Equals(orderStatus, "ready", StringComparison.Ordinal) ||
                 string.Equals(orderStatus, "out_for_delivery", StringComparison.Ordinal));
        }

        private static string NormalizeOutcome(string value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized != "accepted" && normalized != "rejected" &&
                normalized != "prepared" && normalized != "completed")
            {
                throw new ArgumentException("Unsupported customer-order outcome.", nameof(value));
            }
            return normalized;
        }

        internal static string NormalizeCode(string value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Length == 0 || normalized.Length > 96)
                return "customer_order_sync_failure";
            for (var index = 0; index < normalized.Length; index++)
            {
                var character = normalized[index];
                var allowed = (character >= 'a' && character <= 'z') ||
                    (character >= '0' && character <= '9') ||
                    character == '_' || character == '-' || character == '.';
                if (!allowed) return "customer_order_sync_failure";
            }
            return normalized;
        }

        internal static bool IsUuid(string value)
        {
            Guid ignored;
            return Guid.TryParse((value ?? string.Empty).Trim(), out ignored);
        }

        internal static string NormalizeUuid(string value)
        {
            Guid parsed;
            if (!Guid.TryParse((value ?? string.Empty).Trim(), out parsed))
                throw new InvalidDataException("customer_order_uuid_invalid");
            return parsed.ToString("D").ToLowerInvariant();
        }

        private static string NormalizeBounded(string value, int maximumLength)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0 || normalized.Length > maximumLength ||
                normalized.Any(char.IsControl))
            {
                throw new InvalidDataException("customer_order_string_invalid");
            }
            return normalized;
        }

        private static long ParseTimestampMs(string value)
        {
            DateTimeOffset parsed;
            if (!DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out parsed))
            {
                throw new InvalidDataException("customer_order_timestamp_invalid");
            }
            return parsed.ToUnixTimeMilliseconds();
        }

        private sealed class ExistingRow
        {
            public string EventIdempotencyKey { get; set; }
            public string HandoffId { get; set; }
            public long Id { get; set; }
            public string PayloadHash { get; set; }
            public string State { get; set; }
        }

        private sealed class OutcomeRow
        {
            public long? AcceptedAckedAt { get; set; }
            public string AckOutcome { get; set; }
            public string AckPosSaleId { get; set; }
            public long CurrentStatusVersion { get; set; }
            public string EventType { get; set; }
            public long Id { get; set; }
            public string OrderStatus { get; set; }
            public string ShopId { get; set; }
            public string State { get; set; }
            public long TotalClp { get; set; }
        }
    }

    internal static class PosCustomerOrderHandoffCodec
    {
        private static readonly string[] EventTypes =
        {
            "customer_order.accepted.v1",
            "customer_order.cancelled.v1",
            "customer_order.completed.v1",
            "customer_order.out_for_delivery.v1",
            "customer_order.preparing.v1",
            "customer_order.ready.v1",
            "customer_order.rejected.v1"
        };

        private static readonly string[] OrderStatuses =
        {
            "accepted",
            "cancelled",
            "completed",
            "out_for_delivery",
            "preparing",
            "ready",
            "rejected"
        };

        internal static string ValidateClaimResponse(
            PosCustomerOrderClaimResponse response,
            OnlineSyncGeneration generation,
            int requestedLimit,
            long nowMs)
        {
            if (response == null || !response.Ok || response.Code != "success" ||
                response.SchemaVersion != PosOnlineContract.CustomerOrderHandoffSchemaVersion ||
                !ValidTimestamp(response.ServerTime) || response.Handoffs == null ||
                response.Handoffs.Length > requestedLimit ||
                response.Handoffs.Length > PosOnlineContract.CustomerOrderMaximumBatchCount)
            {
                return "customer_order_claim_response_invalid";
            }
            var handoffIds = new HashSet<string>(StringComparer.Ordinal);
            var eventKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var handoff in response.Handoffs)
            {
                var code = ValidateHandoff(handoff, generation, nowMs);
                if (code.Length > 0) return code;
                if (!handoffIds.Add(CustomerOrderInboxRepository.NormalizeUuid(
                        handoff.HandoffId)) ||
                    !eventKeys.Add(CustomerOrderInboxRepository.NormalizeUuid(
                        handoff.EventIdempotencyKey)))
                {
                    return "customer_order_claim_duplicate";
                }
            }
            return string.Empty;
        }

        internal static string ValidateHandoff(
            PosCustomerOrderHandoff handoff,
            OnlineSyncGeneration generation,
            long nowMs)
        {
            if (handoff == null || generation == null ||
                handoff.SchemaVersion != PosOnlineContract.CustomerOrderHandoffSchemaVersion ||
                !CustomerOrderInboxRepository.IsUuid(handoff.HandoffId) ||
                !CustomerOrderInboxRepository.IsUuid(handoff.LeaseToken) ||
                !CustomerOrderInboxRepository.IsUuid(handoff.EventIdempotencyKey) ||
                handoff.AttemptCount < 1 || handoff.AttemptCount > 1000 ||
                !EventTypes.Contains(handoff.EventType, StringComparer.Ordinal) ||
                !ValidBounded(handoff.CorrelationId, 160) ||
                !ValidTimestamp(handoff.LeaseExpiresAt) ||
                ParseTimestamp(handoff.LeaseExpiresAt) <= nowMs ||
                handoff.Order == null)
            {
                return "customer_order_handoff_invalid";
            }

            var order = handoff.Order;
            if (order.DocumentKind != "customer_order" ||
                order.FiscalStatus != "not_created" ||
                !CustomerOrderInboxRepository.IsUuid(order.OrderId) ||
                !CustomerOrderInboxRepository.IsUuid(order.ShopId) ||
                !string.Equals(
                    CustomerOrderInboxRepository.NormalizeUuid(order.ShopId),
                    CustomerOrderInboxRepository.NormalizeUuid(generation.ShopId),
                    StringComparison.Ordinal) ||
                !ValidOrderCode(order.OrderCode) ||
                !OrderStatuses.Contains(order.Status, StringComparer.Ordinal) ||
                (order.FulfillmentMode != "pickup" &&
                 order.FulfillmentMode != "reservation" &&
                 order.FulfillmentMode != "delivery") ||
                order.CurrencyCode != "CLP" ||
                order.StatusVersion < 1 ||
                order.CurrentStatusVersion < order.StatusVersion ||
                order.SubtotalClp < 0 || order.DeliveryFeeClp < 0 ||
                order.TotalClp != order.SubtotalClp + order.DeliveryFeeClp ||
                !ValidTimestamp(order.PlacedAt) || !ValidTimestamp(order.UpdatedAt) ||
                !ValidFulfillment(order.Fulfillment, order.FulfillmentMode) ||
                order.Items == null || order.Items.Length < 1 || order.Items.Length > 100)
            {
                return "customer_order_snapshot_invalid";
            }

            var positions = new HashSet<int>();
            foreach (var item in order.Items)
            {
                if (item == null || item.LinePosition < 1 || item.LinePosition > 100 ||
                    !positions.Add(item.LinePosition) ||
                    item.Quantity < 1 || item.Quantity > 99 ||
                    item.UnitPriceClp < 0 ||
                    item.LineTotalClp != item.UnitPriceClp * item.Quantity ||
                    !ValidBounded(item.PublicName, 200))
                {
                    return "customer_order_item_invalid";
                }
            }
            return string.Empty;
        }

        internal static string ValidateAckResponse(
            PosCustomerOrderAckResponse response,
            CustomerOrderInboxItem expected)
        {
            if (response == null || expected == null || !response.Ok ||
                response.Code != "success" ||
                response.SchemaVersion != PosOnlineContract.CustomerOrderAckSchemaVersion ||
                !CustomerOrderInboxRepository.IsUuid(response.HandoffId) ||
                !CustomerOrderInboxRepository.IsUuid(response.OrderId) ||
                !string.Equals(
                    CustomerOrderInboxRepository.NormalizeUuid(response.HandoffId),
                    expected.HandoffId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    CustomerOrderInboxRepository.NormalizeUuid(response.OrderId),
                    expected.OrderId,
                    StringComparison.Ordinal) ||
                !string.Equals(response.Outcome, expected.AckOutcome, StringComparison.Ordinal) ||
                !OrderStatuses.Contains(response.OrderStatus, StringComparer.Ordinal) ||
                response.OrderStatusVersion < expected.AckExpectedStatusVersion ||
                (response.FiscalStatus != "not_created" && response.FiscalStatus != "linked") ||
                !ValidTimestamp(response.ServerTime))
            {
                return "customer_order_ack_response_invalid";
            }
            if (response.FiscalStatus == "linked" &&
                (!CustomerOrderInboxRepository.IsUuid(response.PosSaleId) ||
                 !string.Equals(
                    CustomerOrderInboxRepository.NormalizeUuid(response.PosSaleId),
                    expected.AckPosSaleId,
                    StringComparison.Ordinal)))
            {
                return "customer_order_fiscal_reference_mismatch";
            }
            if (!string.IsNullOrWhiteSpace(response.PosSaleId) &&
                !string.Equals(
                    CustomerOrderInboxRepository.NormalizeUuid(response.PosSaleId),
                    expected.AckPosSaleId,
                    StringComparison.Ordinal))
            {
                return "customer_order_fiscal_reference_mismatch";
            }
            return string.Empty;
        }

        internal static string Serialize(PosCustomerOrderHandoff value)
        {
            var serializer = new DataContractJsonSerializer(
                typeof(PosCustomerOrderHandoff));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        internal static string SerializePersistent(PosCustomerOrderHandoff value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            return Serialize(new PosCustomerOrderHandoff
            {
                AttemptCount = 0,
                CorrelationId = value.CorrelationId,
                EventIdempotencyKey = value.EventIdempotencyKey,
                EventType = value.EventType,
                HandoffId = value.HandoffId,
                LeaseExpiresAt = null,
                LeaseToken = null,
                Order = value.Order,
                SchemaVersion = value.SchemaVersion
            });
        }

        internal static string Sha256(string value)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var builder = new StringBuilder("sha256:");
                foreach (var item in hash) builder.Append(item.ToString("x2"));
                return builder.ToString();
            }
        }

        private static bool ValidFulfillment(
            PosCustomerOrderFulfillment value,
            string expectedMode)
        {
            if (value == null || value.Mode != expectedMode) return false;
            if (value.PickupPoint != null &&
                (!ValidOptional(value.PickupPoint.PublicName, 200) ||
                 !ValidOptional(value.PickupPoint.Commune, 200) ||
                 !ValidOptional(value.PickupPoint.Region, 200)))
            {
                return false;
            }
            if (value.DeliveryZone != null &&
                (!ValidOptional(value.DeliveryZone.Name, 200) ||
                 !ValidOptional(value.DeliveryZone.Region, 200) ||
                 value.DeliveryZone.FeeClp < 0))
            {
                return false;
            }
            return value.Slot == null ||
                (ValidOptional(value.Slot.Label, 200) &&
                 ValidOptionalTimestamp(value.Slot.StartsAt) &&
                 ValidOptionalTimestamp(value.Slot.EndsAt));
        }

        private static bool ValidOrderCode(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 23 ||
                !value.StartsWith("MC-", StringComparison.Ordinal))
            {
                return false;
            }
            for (var index = 3; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'A' && character <= 'F')))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool ValidBounded(string value, int maximumLength)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                value == value.Trim() && value.Length <= maximumLength &&
                !value.Any(char.IsControl);
        }

        private static bool ValidOptional(string value, int maximumLength)
        {
            return string.IsNullOrEmpty(value) || ValidBounded(value, maximumLength);
        }

        private static bool ValidTimestamp(string value)
        {
            DateTimeOffset parsed;
            return DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed);
        }

        private static bool ValidOptionalTimestamp(string value)
        {
            return string.IsNullOrEmpty(value) || ValidTimestamp(value);
        }

        private static long ParseTimestamp(string value)
        {
            DateTimeOffset parsed;
            return DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed)
                ? parsed.ToUnixTimeMilliseconds()
                : -1L;
        }
    }
}
