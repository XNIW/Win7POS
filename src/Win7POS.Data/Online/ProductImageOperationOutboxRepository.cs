using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public static class ProductImageOperationStates
    {
        public const string WaitingDependency = "waiting_dependency";
        public const string PendingIntent = "pending_intent";
        public const string PendingUpload = "pending_upload";
        public const string PendingFinalize = "pending_finalize";
        public const string PendingRemove = "pending_remove";
        public const string InProgress = "in_progress";
        public const string RetryWait = "retry_wait";
        public const string FailedBlocked = "failed_blocked";
        public const string Completed = "completed";
        public const string CleanupPending = "cleanup_pending";
    }

    public static class ProductImageOperationKinds
    {
        public const string Replace = "replace";
        public const string Remove = "remove";
    }

    public sealed class ProductImageStagedVariant
    {
        public int Bytes { get; set; }
        public int Height { get; set; }
        public string Identity { get; set; }
        public string Sha256 { get; set; }
        public int Width { get; set; }
    }

    public sealed class ProductImageReplaceEnqueueRequest
    {
        public long LocalProductId { get; set; }
        public string ExpectedCurrentVersionId { get; set; }
        public string IntendedLocalVersionIdentity { get; set; }
        public ProductImageStagedVariant Main { get; set; }
        public string PayloadHash { get; set; }
        public ProductImageStagedVariant Thumb { get; set; }
    }

    public sealed class ProductImageRemoveEnqueueRequest
    {
        public long LocalProductId { get; set; }
        public string ExpectedCurrentVersionId { get; set; }
        public string PayloadHash { get; set; }
    }

    public sealed class ProductImageOperationEnqueueResult
    {
        public string IdempotencyKey { get; set; }
        public string OperationId { get; set; }
        public string State { get; set; }
    }

    public sealed class ProductImageOperationRow
    {
        public long Id { get; set; }
        public int AttemptCount { get; set; }
        public string ClaimGenerationId { get; set; }
        public string ClaimFence { get; set; }
        public string CompletionState { get; set; }
        public string ExpectedCurrentVersionId { get; set; }
        public string IdempotencyKey { get; set; }
        public string IntendedLocalVersionIdentity { get; set; }
        public long? LocalProductId { get; set; }
        public int? MainBytes { get; set; }
        public int? MainHeight { get; set; }
        public string MainSha256 { get; set; }
        public int? MainWidth { get; set; }
        public long NextAttemptAt { get; set; }
        public string OperationId { get; set; }
        public string OperationKind { get; set; }
        public string PayloadHash { get; set; }
        public string RemoteProductId { get; set; }
        public string ResumeState { get; set; }
        public string ServerRevision { get; set; }
        public string ServerVersionId { get; set; }
        public string StagedMainIdentity { get; set; }
        public string StagedThumbIdentity { get; set; }
        public string State { get; set; }
        public int? ThumbBytes { get; set; }
        public int? ThumbHeight { get; set; }
        public string ThumbSha256 { get; set; }
        public int? ThumbWidth { get; set; }
    }

    public sealed class ProductImageOperationClaim
    {
        public string ClaimGenerationId { get; set; }
        public string ClaimFence { get; set; }
        public ProductImageOperationRow Operation { get; set; }
    }

    public sealed class ProductImageCancelledStaging
    {
        public string MainIdentity { get; set; }
        public string ThumbIdentity { get; set; }
    }

    public sealed class ProductImageOutboxDrainState
    {
        public bool HasImmediateMore { get; set; }
        public long? NextRetryAt { get; set; }
        public int RemainingDue { get; set; }
        public int Unresolved { get; set; }
    }

    /// <summary>
    /// Durable image mutation state. Ephemeral capabilities and trusted-session
    /// material are deliberately absent from both the schema and this API.
    /// </summary>
    public sealed class ProductImageOperationOutboxRepository
    {
        private readonly SqliteConnectionFactory _factory;

        public ProductImageOperationOutboxRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<ProductImageOperationEnqueueResult> EnqueueReplaceAsync(
            ProductImageReplaceEnqueueRequest request,
            Func<string, string, string> sealPayloadHash = null,
            CancellationToken cancellationToken = default)
        {
            ValidateReplace(request);
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = _factory.Open())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                try
                {
                    var identity = await LoadProductIdentityAsync(
                        connection,
                        transaction,
                        request.LocalProductId).ConfigureAwait(false);
                    if (identity == null) throw new InvalidOperationException("product_image_product_not_found");
                    var operationId = "image-op-" + Guid.NewGuid().ToString("N");
                    var idempotencyKey = "image-idem-" + Guid.NewGuid().ToString("N");
                    var state = string.IsNullOrEmpty(identity.RemoteProductId)
                        ? ProductImageOperationStates.WaitingDependency
                        : ProductImageOperationStates.PendingIntent;
                    var payloadHash = request.PayloadHash;
                    if (state == ProductImageOperationStates.PendingIntent)
                    {
                        if (sealPayloadHash == null)
                        {
                            throw new InvalidOperationException(
                                "product_image_payload_sealer_required");
                        }
                        payloadHash = sealPayloadHash(operationId, idempotencyKey);
                        if (!PosProductImageContractV1.IsPayloadHash(payloadHash))
                        {
                            throw new InvalidOperationException(
                                "product_image_sealed_payload_invalid");
                        }
                    }
                    var now = CanonicalNow();
                    var insert = new DynamicParameters();
                    insert.Add("operationId", operationId);
                    insert.Add("idempotencyKey", idempotencyKey);
                    insert.Add("payloadHash", payloadHash);
                    insert.Add("localProductId", request.LocalProductId);
                    insert.Add("remoteProductId", identity.RemoteProductId);
                    insert.Add("expectedCurrentVersionId", request.ExpectedCurrentVersionId);
                    insert.Add("intendedLocalVersionIdentity", request.IntendedLocalVersionIdentity);
                    insert.Add("mainBytes", request.Main.Bytes);
                    insert.Add("mainWidth", request.Main.Width);
                    insert.Add("mainHeight", request.Main.Height);
                    insert.Add("mainSha256", request.Main.Sha256);
                    insert.Add("thumbBytes", request.Thumb.Bytes);
                    insert.Add("thumbWidth", request.Thumb.Width);
                    insert.Add("thumbHeight", request.Thumb.Height);
                    insert.Add("thumbSha256", request.Thumb.Sha256);
                    insert.Add("stagedMainIdentity", request.Main.Identity);
                    insert.Add("stagedThumbIdentity", request.Thumb.Identity);
                    insert.Add("now", now);
                    insert.Add("state", state);
                    await connection.ExecuteAsync(@"
INSERT INTO product_image_operation_outbox(
  operation_id, idempotency_key, payload_hash, operation_kind,
  local_product_id, remote_product_id, expected_current_version_id,
  intended_local_version_identity,
  main_bytes, main_width, main_height, main_sha256,
  thumb_bytes, thumb_width, thumb_height, thumb_sha256,
  staged_main_identity, staged_thumb_identity,
  created_at, updated_at, state, next_attempt_at)
VALUES(
  @operationId, @idempotencyKey, @payloadHash, 'replace',
  @localProductId, @remoteProductId, @expectedCurrentVersionId,
  @intendedLocalVersionIdentity,
  @mainBytes, @mainWidth, @mainHeight, @mainSha256,
  @thumbBytes, @thumbWidth, @thumbHeight, @thumbSha256,
  @stagedMainIdentity, @stagedThumbIdentity,
  @now, @now, @state, 0);",
                        insert,
                        transaction).ConfigureAwait(false);
                    transaction.Commit();
                    return new ProductImageOperationEnqueueResult
                    {
                        OperationId = operationId,
                        IdempotencyKey = idempotencyKey,
                        State = state
                    };
                }
                catch
                {
                    try { transaction.Rollback(); } catch { }
                    throw;
                }
            }
        }

        public async Task<ProductImageOperationEnqueueResult> EnqueueRemoveAsync(
            ProductImageRemoveEnqueueRequest request,
            Func<string, string, string> sealPayloadHash,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.LocalProductId <= 0 ||
                !PosProductImageContractV1.IsCanonicalUuid(request.ExpectedCurrentVersionId) ||
                !PosProductImageContractV1.IsPayloadHash(request.PayloadHash) ||
                sealPayloadHash == null)
            {
                throw new ArgumentException("product_image_remove_invalid", nameof(request));
            }
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = _factory.Open())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                try
                {
                    var identity = await LoadProductIdentityAsync(
                        connection,
                        transaction,
                        request.LocalProductId).ConfigureAwait(false);
                    if (identity == null || !PosProductImageContractV1.IsCanonicalUuid(identity.RemoteProductId))
                        throw new InvalidOperationException("product_image_remote_dependency_missing");
                    var operationId = "image-op-" + Guid.NewGuid().ToString("N");
                    var idempotencyKey = "image-idem-" + Guid.NewGuid().ToString("N");
                    var payloadHash = sealPayloadHash(operationId, idempotencyKey);
                    if (!PosProductImageContractV1.IsPayloadHash(payloadHash))
                    {
                        throw new InvalidOperationException(
                            "product_image_sealed_payload_invalid");
                    }
                    var now = CanonicalNow();
                    var insert = new DynamicParameters();
                    insert.Add("operationId", operationId);
                    insert.Add("idempotencyKey", idempotencyKey);
                    insert.Add("payloadHash", payloadHash);
                    insert.Add("localProductId", request.LocalProductId);
                    insert.Add("remoteProductId", identity.RemoteProductId);
                    insert.Add("expectedCurrentVersionId", request.ExpectedCurrentVersionId);
                    insert.Add("now", now);
                    await connection.ExecuteAsync(@"
INSERT INTO product_image_operation_outbox(
  operation_id, idempotency_key, payload_hash, operation_kind,
  local_product_id, remote_product_id, expected_current_version_id,
  created_at, updated_at, state, next_attempt_at)
VALUES(
  @operationId, @idempotencyKey, @payloadHash, 'remove',
  @localProductId, @remoteProductId, @expectedCurrentVersionId,
  @now, @now, 'pending_remove', 0);",
                        insert,
                        transaction).ConfigureAwait(false);
                    transaction.Commit();
                    return new ProductImageOperationEnqueueResult
                    {
                        OperationId = operationId,
                        IdempotencyKey = idempotencyKey,
                        State = ProductImageOperationStates.PendingRemove
                    };
                }
                catch
                {
                    try { transaction.Rollback(); } catch { }
                    throw;
                }
            }
        }

        public async Task<int> ReleaseDependenciesAsync(
            long localProductId,
            string remoteProductId,
            Func<ProductImageOperationRow, string> sealPayloadHash,
            CancellationToken cancellationToken = default)
        {
            if (localProductId <= 0 || !PosProductImageContractV1.IsCanonicalUuid(remoteProductId))
                throw new ArgumentException("product_image_dependency_invalid");
            if (sealPayloadHash == null) throw new ArgumentNullException(nameof(sealPayloadHash));
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = _factory.Open())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                try
                {
                    var rows = (await connection.QueryAsync<ProductImageOperationRow>(
                        SelectColumns + @"
WHERE local_product_id = @localProductId
  AND state = 'waiting_dependency'
ORDER BY id;",
                        new { localProductId },
                        transaction).ConfigureAwait(false)).ToArray();
                    var now = CanonicalNow();
                    foreach (var row in rows)
                    {
                        row.RemoteProductId = remoteProductId;
                        var payloadHash = sealPayloadHash(row);
                        if (!PosProductImageContractV1.IsPayloadHash(payloadHash))
                            throw new InvalidOperationException("product_image_sealed_payload_invalid");
                        var changed = await connection.ExecuteAsync(@"
UPDATE product_image_operation_outbox
SET remote_product_id = @remoteProductId,
    payload_hash = @payloadHash,
    state = 'pending_intent',
    updated_at = @now,
    next_attempt_at = 0
WHERE id = @id
  AND state = 'waiting_dependency';",
                            new { id = row.Id, remoteProductId, payloadHash, now },
                            transaction).ConfigureAwait(false);
                        if (changed != 1) throw new InvalidOperationException("product_image_dependency_cas_failed");
                    }
                    transaction.Commit();
                    return rows.Length;
                }
                catch
                {
                    try { transaction.Rollback(); } catch { }
                    throw;
                }
            }
        }

        public async Task<IReadOnlyList<ProductImageOperationRow>>
            GetCatalogReconciledDependenciesAsync()
        {
            using (var connection = _factory.Open())
            {
                var rows = await connection.QueryAsync<ProductImageOperationRow>(@"
SELECT DISTINCT
       operation.local_product_id AS LocalProductId,
       product.remote_product_id AS RemoteProductId
FROM product_image_operation_outbox operation
JOIN products product ON product.id = operation.local_product_id
JOIN product_image_remote_shadow shadow
  ON shadow.local_product_id = operation.local_product_id
 AND shadow.remote_product_id = product.remote_product_id
WHERE operation.state = 'waiting_dependency'
  AND product.remote_product_id IS NOT NULL
ORDER BY operation.local_product_id;");
                return rows.ToArray();
            }
        }

        public async Task<IReadOnlyList<ProductImageCancelledStaging>> CancelWaitingForDeletedProductAsync(
            long localProductId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = _factory.Open())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                try
                {
                    var rows = (await connection.QueryAsync<ProductImageCancelledStaging>(@"
SELECT staged_main_identity AS MainIdentity,
       staged_thumb_identity AS ThumbIdentity
FROM product_image_operation_outbox
WHERE local_product_id = @localProductId
  AND state = 'waiting_dependency';",
                        new { localProductId },
                        transaction).ConfigureAwait(false)).ToArray();
                    await connection.ExecuteAsync(@"
UPDATE product_image_operation_outbox
SET state = 'completed',
    completion_state = 'cancelled_before_remote_identity',
    staged_main_identity = NULL,
    staged_thumb_identity = NULL,
    completed_at = @now,
    updated_at = @now
WHERE local_product_id = @localProductId
  AND state = 'waiting_dependency';",
                        new { localProductId, now = CanonicalNow() },
                        transaction).ConfigureAwait(false);
                    transaction.Commit();
                    return rows;
                }
                catch
                {
                    try { transaction.Rollback(); } catch { }
                    throw;
                }
            }
        }

        public async Task<ProductImageOperationClaim> ClaimNextAsync(
            string generationId,
            long nowUnixMilliseconds,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(generationId))
                throw new ArgumentException("product_image_generation_required", nameof(generationId));
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = _factory.Open())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                try
                {
                    var row = await connection.QueryFirstOrDefaultAsync<ProductImageOperationRow>(
                        SelectColumns + @"
WHERE state IN (
    'pending_intent', 'pending_upload', 'pending_finalize',
    'pending_remove', 'retry_wait', 'cleanup_pending')
  AND next_attempt_at <= @now
  AND remote_product_id IS NOT NULL
  AND NOT EXISTS (
    SELECT 1
    FROM product_image_operation_outbox earlier
    WHERE earlier.local_product_id = product_image_operation_outbox.local_product_id
      AND earlier.id < product_image_operation_outbox.id
      AND earlier.state NOT IN ('completed', 'failed_blocked'))
ORDER BY id ASC
LIMIT 1;",
                        new { now = nowUnixMilliseconds },
                        transaction).ConfigureAwait(false);
                    if (row == null)
                    {
                        transaction.Commit();
                        return null;
                    }
                    var claimFence = "image-claim-" + Guid.NewGuid().ToString("N");
                    var resumeState = row.State == ProductImageOperationStates.RetryWait
                        ? row.ResumeState
                        : row.State;
                    if (!IsRunnableState(resumeState))
                        throw new InvalidOperationException("product_image_resume_state_invalid");
                    var changed = await connection.ExecuteAsync(@"
UPDATE product_image_operation_outbox
SET state = 'in_progress',
    resume_state = @resumeState,
    claim_generation_id = @generationId,
    claim_fence = @claimFence,
    updated_at = @updatedAt
WHERE id = @id
  AND state = @expectedState;",
                        new
                        {
                            id = row.Id,
                            resumeState,
                            generationId,
                            claimFence,
                            updatedAt = CanonicalNow(),
                            expectedState = row.State
                        },
                        transaction).ConfigureAwait(false);
                    if (changed != 1) throw new InvalidOperationException("product_image_claim_cas_failed");
                    row.State = ProductImageOperationStates.InProgress;
                    row.ResumeState = resumeState;
                    row.ClaimGenerationId = generationId;
                    row.ClaimFence = claimFence;
                    transaction.Commit();
                    return new ProductImageOperationClaim
                    {
                        ClaimGenerationId = generationId,
                        ClaimFence = claimFence,
                        Operation = row
                    };
                }
                catch
                {
                    try { transaction.Rollback(); } catch { }
                    throw;
                }
            }
        }

        public Task<bool> AdvanceAsync(
            ProductImageOperationClaim claim,
            string nextState,
            string serverVersionId = null,
            string serverRevision = null)
        {
            if (!IsRunnableState(nextState) &&
                nextState != ProductImageOperationStates.Completed &&
                nextState != ProductImageOperationStates.FailedBlocked)
            {
                throw new ArgumentException("product_image_next_state_invalid", nameof(nextState));
            }
            return CompleteClaimUpdateAsync(
                claim,
                nextState,
                null,
                0,
                serverVersionId,
                serverRevision,
                nextState == ProductImageOperationStates.Completed ? "remote_complete" : null);
        }

        public Task<bool> RetryAsync(
            ProductImageOperationClaim claim,
            string typedFailureCode,
            DateTimeOffset nextAttemptAt)
        {
            return CompleteClaimUpdateAsync(
                claim,
                ProductImageOperationStates.RetryWait,
                typedFailureCode,
                nextAttemptAt.ToUnixTimeMilliseconds(),
                null,
                null,
                null);
        }

        public Task<bool> BlockAsync(
            ProductImageOperationClaim claim,
            string typedFailureCode)
        {
            return CompleteClaimUpdateAsync(
                claim,
                ProductImageOperationStates.FailedBlocked,
                typedFailureCode,
                0,
                null,
                null,
                "operator_resolution_required");
        }

        public async Task<bool> CompleteCleanupAsync(
            ProductImageOperationClaim claim,
            string completionState = "remote_complete")
        {
            RequireClaim(claim);
            using (var connection = _factory.Open())
            {
                var now = CanonicalNow();
                var changed = await connection.ExecuteAsync(@"
UPDATE product_image_operation_outbox
SET state = 'completed',
    attempt_count = attempt_count + 1,
    next_attempt_at = 0,
    last_typed_failure_code = NULL,
    completion_state = @completionState,
    staged_main_identity = NULL,
    staged_thumb_identity = NULL,
    completed_at = @now,
    claim_generation_id = NULL,
    claim_fence = NULL,
    updated_at = @now
WHERE id = @id
  AND state = 'in_progress'
  AND claim_generation_id = @generationId
  AND claim_fence = @claimFence;",
                    new
                    {
                        completionState,
                        now,
                        id = claim.Operation.Id,
                        generationId = claim.ClaimGenerationId,
                        claimFence = claim.ClaimFence
                    }).ConfigureAwait(false);
                return changed == 1;
            }
        }

        public async Task<bool> RotateExpiredIntentAsync(
            ProductImageOperationClaim claim,
            Func<string, string, string> resealPayloadHash)
        {
            RequireClaim(claim);
            if (claim.Operation.OperationKind != ProductImageOperationKinds.Replace)
                throw new ArgumentException("product_image_rotation_replace_only", nameof(claim));
            if (resealPayloadHash == null)
                throw new ArgumentNullException(nameof(resealPayloadHash));
            var operationId = "image-op-" + Guid.NewGuid().ToString("N");
            var idempotencyKey = "image-idem-" + Guid.NewGuid().ToString("N");
            var payloadHash = resealPayloadHash(operationId, idempotencyKey);
            if (!PosProductImageContractV1.IsPayloadHash(payloadHash))
                throw new InvalidDataException("product_image_rotation_payload_hash_invalid");
            using (var connection = _factory.Open())
            {
                var changed = await connection.ExecuteAsync(@"
UPDATE product_image_operation_outbox
SET operation_id = @operationId,
    idempotency_key = @idempotencyKey,
    payload_hash = @payloadHash,
    state = 'pending_intent',
    resume_state = NULL,
    attempt_count = attempt_count + 1,
    next_attempt_at = 0,
    last_typed_failure_code = 'expired_intent_rotated',
    server_version_id = NULL,
    server_revision = NULL,
    claim_generation_id = NULL,
    claim_fence = NULL,
    updated_at = @now
WHERE id = @id
  AND state = 'in_progress'
  AND claim_generation_id = @generationId
  AND claim_fence = @claimFence;",
                    new
                    {
                        operationId,
                        idempotencyKey,
                        payloadHash,
                        now = CanonicalNow(),
                        id = claim.Operation.Id,
                        generationId = claim.ClaimGenerationId,
                        claimFence = claim.ClaimFence
                    }).ConfigureAwait(false);
                return changed == 1;
            }
        }

        public async Task<int> RecoverInterruptedClaimsAsync(
            string generationId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(generationId))
                throw new ArgumentException("product_image_generation_required", nameof(generationId));
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = _factory.Open())
            {
                return await connection.ExecuteAsync(@"
UPDATE product_image_operation_outbox
SET state = 'retry_wait',
    last_typed_failure_code = 'interrupted_claim_recovered',
    next_attempt_at = 0,
    claim_generation_id = NULL,
    claim_fence = NULL,
    updated_at = @now
WHERE state = 'in_progress'
  -- This method runs before the single supervised image lane claims its next
  -- row.  Recover same-generation rows too: after a process restart the
  -- durable trusted generation can remain unchanged even though no old worker
  -- still exists.
  AND claim_generation_id IS NOT NULL;",
                    new { generationId, now = CanonicalNow() }).ConfigureAwait(false);
            }
        }

        public async Task<ProductImageOperationRow> GetAsync(string operationId)
        {
            using (var connection = _factory.Open())
            {
                return await connection.QueryFirstOrDefaultAsync<ProductImageOperationRow>(
                    SelectColumns + "WHERE operation_id = @operationId LIMIT 1;",
                    new { operationId }).ConfigureAwait(false);
            }
        }

        public async Task<ProductImageOperationRow> GetLatestForProductAsync(
            long localProductId)
        {
            using (var connection = _factory.Open())
            {
                return await connection.QueryFirstOrDefaultAsync<ProductImageOperationRow>(
                    SelectColumns + @"
WHERE local_product_id = @localProductId
ORDER BY id DESC
LIMIT 1;",
                    new { localProductId }).ConfigureAwait(false);
            }
        }

        public async Task<bool> RetryBlockedAsNewAsync(
            string operationId,
            string remoteProductId,
            string expectedCurrentVersionId,
            Func<string, string, string> resealPayloadHash)
        {
            if (string.IsNullOrWhiteSpace(operationId) ||
                resealPayloadHash == null ||
                !PosProductImageContractV1.IsCanonicalUuid(remoteProductId) ||
                (expectedCurrentVersionId != null &&
                 !PosProductImageContractV1.IsCanonicalUuid(expectedCurrentVersionId)))
            {
                throw new ArgumentException("product_image_retry_invalid");
            }
            var newOperationId = "image-op-" + Guid.NewGuid().ToString("N");
            var newIdempotencyKey = "image-idem-" + Guid.NewGuid().ToString("N");
            var payloadHash = resealPayloadHash(
                newOperationId,
                newIdempotencyKey);
            if (!PosProductImageContractV1.IsPayloadHash(payloadHash))
                throw new InvalidDataException("product_image_retry_payload_hash_invalid");
            using (var connection = _factory.Open())
            {
                var changed = await connection.ExecuteAsync(@"
UPDATE product_image_operation_outbox
SET operation_id = @newOperationId,
    idempotency_key = @newIdempotencyKey,
    payload_hash = @payloadHash,
    remote_product_id = @remoteProductId,
    expected_current_version_id = @expectedCurrentVersionId,
    state = CASE operation_kind
      WHEN 'replace' THEN 'pending_intent'
      ELSE 'pending_remove'
    END,
    attempt_count = 0,
    next_attempt_at = 0,
    last_typed_failure_code = NULL,
    server_version_id = NULL,
    server_revision = NULL,
    completion_state = NULL,
    completed_at = NULL,
    resume_state = NULL,
    claim_generation_id = NULL,
    claim_fence = NULL,
    updated_at = @now
WHERE operation_id = @operationId
  AND state = 'failed_blocked'
  AND remote_product_id IS NOT NULL;",
                    new
                    {
                        operationId,
                        newOperationId,
                        newIdempotencyKey,
                        payloadHash,
                        remoteProductId,
                        expectedCurrentVersionId,
                        now = CanonicalNow()
                    }).ConfigureAwait(false);
                return changed == 1;
            }
        }

        public async Task<ProductImageOutboxDrainState> GetDrainStateAsync(
            long nowUnixMilliseconds)
        {
            using (var connection = _factory.Open())
            {
                var unresolved = await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(1)
FROM product_image_operation_outbox
WHERE state NOT IN ('completed', 'failed_blocked');").ConfigureAwait(false);
                var due = await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(1)
FROM product_image_operation_outbox candidate
WHERE candidate.state IN (
    'pending_intent', 'pending_upload', 'pending_finalize',
    'pending_remove', 'retry_wait', 'cleanup_pending')
  AND candidate.next_attempt_at <= @now
  AND candidate.remote_product_id IS NOT NULL
  AND NOT EXISTS (
    SELECT 1
    FROM product_image_operation_outbox earlier
    WHERE earlier.local_product_id = candidate.local_product_id
      AND earlier.id < candidate.id
                          AND earlier.state NOT IN ('completed', 'failed_blocked'));",
                    new { now = nowUnixMilliseconds }).ConfigureAwait(false);
                var next = await connection.ExecuteScalarAsync<long?>(@"
SELECT MIN(next_attempt_at)
FROM product_image_operation_outbox
WHERE state = 'retry_wait'
  AND next_attempt_at > @now;",
                    new { now = nowUnixMilliseconds }).ConfigureAwait(false);
                var waitingDependencies = await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(1)
FROM product_image_operation_outbox
WHERE state = 'waiting_dependency';").ConfigureAwait(false);
                if (!next.HasValue && due == 0 && waitingDependencies > 0)
                {
                    // Catalog reconciliation commits independently from this lane.
                    // A bounded poll is the durable lost-signal fallback.
                    next = checked(nowUnixMilliseconds + 5000L);
                }
                return new ProductImageOutboxDrainState
                {
                    Unresolved = unresolved,
                    RemainingDue = due,
                    HasImmediateMore = due > 0,
                    NextRetryAt = next
                };
            }
        }

        public async Task<long> CountUnresolvedAsync()
        {
            using (var connection = _factory.Open())
            {
                return await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM product_image_operation_outbox
WHERE state <> 'completed';").ConfigureAwait(false);
            }
        }

        public async Task<IReadOnlyList<string>> GetReferencedStagingIdentitiesAsync()
        {
            using (var connection = _factory.Open())
            {
                var values = await connection.QueryAsync<string>(@"
SELECT staged_main_identity
FROM product_image_operation_outbox
WHERE staged_main_identity IS NOT NULL
UNION
SELECT staged_thumb_identity
FROM product_image_operation_outbox
WHERE staged_thumb_identity IS NOT NULL;").ConfigureAwait(false);
                return values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            }
        }

        private async Task<bool> CompleteClaimUpdateAsync(
            ProductImageOperationClaim claim,
            string state,
            string failureCode,
            long nextAttemptAt,
            string serverVersionId,
            string serverRevision,
            string completionState)
        {
            RequireClaim(claim);
            using (var connection = _factory.Open())
            {
                var now = CanonicalNow();
                var changed = await connection.ExecuteAsync(@"
UPDATE product_image_operation_outbox
SET state = @state,
    attempt_count = attempt_count + 1,
    next_attempt_at = @nextAttemptAt,
    last_typed_failure_code = @failureCode,
    server_version_id = COALESCE(@serverVersionId, server_version_id),
    server_revision = COALESCE(@serverRevision, server_revision),
    completion_state = COALESCE(@completionState, completion_state),
    completed_at = CASE WHEN @state IN ('completed', 'failed_blocked') THEN @now ELSE completed_at END,
    claim_generation_id = NULL,
    claim_fence = NULL,
    updated_at = @now
WHERE id = @id
  AND state = 'in_progress'
  AND claim_generation_id = @generationId
  AND claim_fence = @claimFence;",
                    new
                    {
                        state,
                        nextAttemptAt,
                        failureCode = NormalizeCode(failureCode),
                        serverVersionId = EmptyToNull(serverVersionId),
                        serverRevision = EmptyToNull(serverRevision),
                        completionState,
                        now,
                        id = claim.Operation.Id,
                        generationId = claim.ClaimGenerationId,
                        claimFence = claim.ClaimFence
                    }).ConfigureAwait(false);
                return changed == 1;
            }
        }

        private static void ValidateReplace(ProductImageReplaceEnqueueRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.LocalProductId <= 0 ||
                !PosProductImageContractV1.IsPayloadHash(request.PayloadHash) ||
                string.IsNullOrWhiteSpace(request.IntendedLocalVersionIdentity) ||
                request.IntendedLocalVersionIdentity.Length > 120 ||
                (request.ExpectedCurrentVersionId != null &&
                 !PosProductImageContractV1.IsCanonicalUuid(request.ExpectedCurrentVersionId)) ||
                !IsVariantValid(request.Main, 1600, 1024 * 1024) ||
                !IsVariantValid(request.Thumb, 384, 90 * 1024))
            {
                throw new ArgumentException("product_image_replace_invalid", nameof(request));
            }
        }

        private static bool IsVariantValid(ProductImageStagedVariant value, int maximumSide, int maximumBytes)
        {
            return value != null &&
                   value.Bytes >= 1 && value.Bytes <= maximumBytes &&
                   value.Width >= 1 && value.Width <= maximumSide &&
                   value.Height >= 1 && value.Height <= maximumSide &&
                   IsOpaqueIdentity(value.Identity) &&
                   value.Sha256 != null && value.Sha256.Length == 64 &&
                   value.Sha256.All(character =>
                       (character >= '0' && character <= '9') ||
                       (character >= 'a' && character <= 'f'));
        }

        private static bool IsOpaqueIdentity(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 120 ||
                value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0) return false;
            return value.All(character =>
                (character >= 'A' && character <= 'Z') ||
                (character >= 'a' && character <= 'z') ||
                (character >= '0' && character <= '9') ||
                character == '.' || character == '_' || character == '-');
        }

        private static bool IsRunnableState(string state)
        {
            return state == ProductImageOperationStates.PendingIntent ||
                   state == ProductImageOperationStates.PendingUpload ||
                   state == ProductImageOperationStates.PendingFinalize ||
                   state == ProductImageOperationStates.PendingRemove ||
                   state == ProductImageOperationStates.CleanupPending;
        }

        private static void RequireClaim(ProductImageOperationClaim claim)
        {
            if (claim?.Operation == null ||
                string.IsNullOrWhiteSpace(claim.ClaimGenerationId) ||
                string.IsNullOrWhiteSpace(claim.ClaimFence))
            {
                throw new ArgumentException("product_image_claim_required", nameof(claim));
            }
        }

        private static async Task<ProductIdentityRow> LoadProductIdentityAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long localProductId)
        {
            return await connection.QueryFirstOrDefaultAsync<ProductIdentityRow>(@"
SELECT id AS Id,
       remote_product_id AS RemoteProductId
FROM products
WHERE id = @localProductId
  AND COALESCE(is_active, 1) = 1
LIMIT 1;",
                new { localProductId },
                transaction).ConfigureAwait(false);
        }

        private static string CanonicalNow()
        {
            return DateTimeOffset.UtcNow.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
                CultureInfo.InvariantCulture);
        }

        private static string NormalizeCode(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0) return null;
            if (normalized.Length > 80 || normalized.Any(character =>
                !((character >= 'a' && character <= 'z') ||
                  (character >= '0' && character <= '9') || character == '_')))
            {
                throw new ArgumentException("product_image_failure_code_invalid", nameof(value));
            }
            return normalized;
        }

        private static string EmptyToNull(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return normalized.Length == 0 ? null : normalized;
        }

        private const string SelectColumns = @"
SELECT id AS Id,
       operation_id AS OperationId,
       idempotency_key AS IdempotencyKey,
       payload_hash AS PayloadHash,
       operation_kind AS OperationKind,
       local_product_id AS LocalProductId,
       remote_product_id AS RemoteProductId,
       expected_current_version_id AS ExpectedCurrentVersionId,
       intended_local_version_identity AS IntendedLocalVersionIdentity,
       main_bytes AS MainBytes,
       main_width AS MainWidth,
       main_height AS MainHeight,
       main_sha256 AS MainSha256,
       thumb_bytes AS ThumbBytes,
       thumb_width AS ThumbWidth,
       thumb_height AS ThumbHeight,
       thumb_sha256 AS ThumbSha256,
       staged_main_identity AS StagedMainIdentity,
       staged_thumb_identity AS StagedThumbIdentity,
       state AS State,
       attempt_count AS AttemptCount,
       next_attempt_at AS NextAttemptAt,
       server_version_id AS ServerVersionId,
       server_revision AS ServerRevision,
       completion_state AS CompletionState,
       resume_state AS ResumeState,
       claim_generation_id AS ClaimGenerationId,
       claim_fence AS ClaimFence
FROM product_image_operation_outbox
";

        private sealed class ProductIdentityRow
        {
            public long Id { get; set; }
            public string RemoteProductId { get; set; }
        }
    }
}
