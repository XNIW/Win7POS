using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public static class ArticleMutationOutboxStates
    {
        public const string WaitingDependency = "waiting_dependency";
        public const string Pending = "pending";
        public const string InProgress = "in_progress";
        public const string RetryWait = "retry_wait";
        public const string FailedBlocked = "failed_blocked";
        public const string Completed = "completed";
    }

    internal static class ArticleMutationReferenceDependency
    {
        internal const string Code = "dependency_missing_remote_reference";
        private const string CategoryPrefix = "00000000-0000-4000-a001-";
        private const string SupplierPrefix = "00000000-0000-4000-a002-";

        internal static string Category(int id)
        {
            return CategoryPrefix + id.ToString("x12", CultureInfo.InvariantCulture);
        }

        internal static string Supplier(int id)
        {
            return SupplierPrefix + id.ToString("x12", CultureInfo.InvariantCulture);
        }

        internal static bool TryParseCategory(string value, out int id)
        {
            return TryParse(value, CategoryPrefix, out id);
        }

        internal static bool TryParseSupplier(string value, out int id)
        {
            return TryParse(value, SupplierPrefix, out id);
        }

        private static bool TryParse(string value, string prefix, out int id)
        {
            id = 0;
            var normalized = (value ?? string.Empty).Trim();
            return normalized.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(
                    normalized.Substring(prefix.Length),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out id) &&
                id > 0;
        }
    }

    public sealed class ArticleMutationEnqueueRequest
    {
        public long LocalProductId { get; set; }
        public string MutationKind { get; set; }
        public IDictionary<string, object> Changes { get; set; } =
            new Dictionary<string, object>(StringComparer.Ordinal);
        public IReadOnlyList<string> FieldMask { get; set; } =
            Array.Empty<string>();
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
        public string TargetRemoteProductId { get; set; }
        public string TargetBaseRevision { get; set; }
        public string DependencyCode { get; set; }
        public long? LocalPriceHistoryId { get; set; }
        public long? LocalStockAdjustmentId { get; set; }
    }

    public sealed class ArticleMutationEnqueueResult
    {
        public string ClientProductId { get; set; }
        public string IdempotencyKey { get; set; }
        public long LocalSequence { get; set; }
        public string MutationId { get; set; }
        public string PayloadHash { get; set; }
        public string State { get; set; }
    }

    public sealed class ArticleMutationClaim
    {
        public string ClaimGenerationId { get; set; }
        public string ClaimToken { get; set; }
        public IReadOnlyList<PosArticleMutationRequest> Requests { get; set; } =
            Array.Empty<PosArticleMutationRequest>();
    }

    public sealed class ArticleMutationOutboxSummary
    {
        public long WaitingDependency { get; set; }
        public long Pending { get; set; }
        public long InProgress { get; set; }
        public long RetryWait { get; set; }
        public long FailedBlocked { get; set; }
        public long Completed { get; set; }
        public long AffectedArticleCount { get; set; }
        public string LastTypedCode { get; set; }
    }

    public sealed class ArticleMutationOutboxRepository
    {
        private static readonly string[] NonTerminalStates =
        {
            ArticleMutationOutboxStates.WaitingDependency,
            ArticleMutationOutboxStates.Pending,
            ArticleMutationOutboxStates.InProgress,
            ArticleMutationOutboxStates.RetryWait
        };

        private readonly SqliteConnectionFactory _factory;

        public ArticleMutationOutboxRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        internal static async Task<ArticleMutationEnqueueResult> EnqueueInTransactionAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            ArticleMutationEnqueueRequest request)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.LocalProductId <= 0)
                throw new ArgumentException("A local product is required.");

            var product = await connection.QueryFirstOrDefaultAsync<ProductMutationIdentityRow>(@"
SELECT id AS Id,
       client_product_id AS ClientProductId,
       remote_product_id AS RemoteProductId,
       remote_base_revision AS RemoteBaseRevision
FROM products
WHERE id = @productId
LIMIT 1;",
                new { productId = request.LocalProductId },
                transaction).ConfigureAwait(false);
            if (product == null)
                throw new InvalidOperationException("Product not found.");

            if (string.IsNullOrWhiteSpace(product.ClientProductId))
            {
                product.ClientProductId = "client-" + Guid.NewGuid().ToString("N");
                var assigned = await connection.ExecuteAsync(@"
UPDATE products
SET client_product_id = @clientProductId
WHERE id = @productId
  AND client_product_id IS NULL;",
                    new
                    {
                        clientProductId = product.ClientProductId,
                        productId = product.Id
                    },
                    transaction).ConfigureAwait(false);
                if (assigned != 1)
                {
                    product.ClientProductId = await connection.ExecuteScalarAsync<string>(@"
SELECT client_product_id
FROM products
WHERE id = @productId;",
                        new { productId = product.Id },
                        transaction).ConfigureAwait(false);
                }
            }

            var localSequence = checked(
                await connection.ExecuteScalarAsync<long>(@"
SELECT COALESCE(MAX(local_sequence), 0)
FROM article_mutation_outbox
WHERE client_product_id = @clientProductId;",
                    new { clientProductId = product.ClientProductId },
                    transaction).ConfigureAwait(false) + 1L);
            var mutationId = "mutation-" + Guid.NewGuid().ToString("N");
            var idempotencyKey = "idempotency-" + Guid.NewGuid().ToString("N");
            var createdAt = request.CreatedAt == default(DateTimeOffset)
                ? DateTimeOffset.UtcNow
                : request.CreatedAt;
            var occurredAt = request.OccurredAt == default(DateTimeOffset)
                ? createdAt
                : request.OccurredAt;

            var targetRemoteId = string.IsNullOrWhiteSpace(
                request.TargetRemoteProductId)
                ? product.RemoteProductId
                : request.TargetRemoteProductId;
            var targetBaseRevision = string.IsNullOrWhiteSpace(
                request.TargetBaseRevision)
                ? product.RemoteBaseRevision
                : request.TargetBaseRevision;

            var hasEarlierDependency = await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_outbox
WHERE client_product_id = @clientProductId
  AND state IN ('waiting_dependency', 'pending', 'in_progress', 'retry_wait');",
                new { clientProductId = product.ClientProductId },
                transaction).ConfigureAwait(false) > 0;
            var isCreate = string.Equals(
                request.MutationKind,
                PosArticleMutationKinds.ProductCreate,
                StringComparison.Ordinal);
            var isDuplicate = string.Equals(
                request.MutationKind,
                PosArticleMutationKinds.ProductDuplicate,
                StringComparison.Ordinal);
            var dependencyMissing = !string.IsNullOrWhiteSpace(
                request.DependencyCode);
            var canSeal =
                !dependencyMissing &&
                (isCreate ||
                (isDuplicate &&
                 !hasEarlierDependency &&
                 !string.IsNullOrWhiteSpace(targetRemoteId) &&
                 !string.IsNullOrWhiteSpace(targetBaseRevision)) ||
                (!isDuplicate &&
                 !hasEarlierDependency &&
                 !string.IsNullOrWhiteSpace(targetRemoteId) &&
                 !string.IsNullOrWhiteSpace(targetBaseRevision)));

            PosArticleMutationIntent localIntent;
            PosArticleMutationIntent sealedIntent = null;
            if (dependencyMissing && !hasEarlierDependency)
            {
                // A reference dependency is a complete immutable product
                // intent whose local reference placeholders are never
                // wire-eligible. Keep its target/base identity now, then seal
                // only after every placeholder resolves to an Admin UUID.
                localIntent = PosArticleMutationIntentPolicy.Create(
                    isCreate ? null : targetBaseRevision,
                    request.Changes,
                    product.ClientProductId,
                    createdAt,
                    request.FieldMask,
                    idempotencyKey,
                    localSequence,
                    mutationId,
                    request.MutationKind,
                    occurredAt,
                    isCreate ? null : targetRemoteId);
            }
            else if (dependencyMissing)
            {
                // Work already queued for this product must receive the
                // revision assigned by its predecessor ACK. Persist only the
                // immutable local change here; materialization will chain it.
                localIntent = PosArticleMutationIntentPolicy.CreateUnresolved(
                    request.Changes,
                    product.ClientProductId,
                    createdAt,
                    request.FieldMask,
                    idempotencyKey,
                    localSequence,
                    mutationId,
                    request.MutationKind,
                    occurredAt);
            }
            else if (canSeal)
            {
                sealedIntent = PosArticleMutationIntentPolicy.Create(
                    isCreate ? null : targetBaseRevision,
                    request.Changes,
                    product.ClientProductId,
                    createdAt,
                    request.FieldMask,
                    idempotencyKey,
                    localSequence,
                    mutationId,
                    request.MutationKind,
                    occurredAt,
                    isCreate ? null : targetRemoteId);
                localIntent = sealedIntent;
            }
            else
            {
                localIntent = PosArticleMutationIntentPolicy.CreateUnresolved(
                    request.Changes,
                    product.ClientProductId,
                    createdAt,
                    request.FieldMask,
                    idempotencyKey,
                    localSequence,
                    mutationId,
                    request.MutationKind,
                    occurredAt);
            }

            var intentJson = PosArticleMutationCanonicalWriter.Write(localIntent);
            var intentHash = PosArticleMutationPayloadHash.Compute(
                new UTF8Encoding(false, true).GetBytes(intentJson));
            var canonicalPayload = sealedIntent == null
                ? null
                : PosArticleMutationCanonicalWriter.Write(sealedIntent);
            var payloadHash = sealedIntent == null
                ? null
                : PosArticleMutationPayloadHash.Compute(sealedIntent);
            var state = canSeal
                    ? ArticleMutationOutboxStates.Pending
                    : ArticleMutationOutboxStates.WaitingDependency;
            var now = DateTimeOffset.UtcNow;
            var nowText = now.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture);
            var nextAttemptAt = dependencyMissing
                ? now.AddSeconds(30).ToUnixTimeMilliseconds()
                : 0L;

            await connection.ExecuteAsync(@"
INSERT INTO article_mutation_outbox(
  local_product_id,
  mutation_id,
  idempotency_key,
  client_product_id,
  remote_product_id,
  mutation_kind,
  local_sequence,
  base_revision,
  field_mask_json,
  intent_json,
  intent_hash,
  canonical_payload_json,
  payload_hash,
  created_at,
  occurred_at,
  state,
  attempt_count,
  next_attempt_at,
  last_typed_code,
  local_price_history_id,
  local_stock_adjustment_id,
  updated_at)
VALUES(
  @LocalProductId,
  @MutationId,
  @IdempotencyKey,
  @ClientProductId,
  @RemoteProductId,
  @MutationKind,
  @LocalSequence,
  @BaseRevision,
  @FieldMaskJson,
  @IntentJson,
  @IntentHash,
  @CanonicalPayloadJson,
  @PayloadHash,
  @CreatedAt,
  @OccurredAt,
  @State,
  0,
  @NextAttemptAt,
  @LastTypedCode,
  @LocalPriceHistoryId,
  @LocalStockAdjustmentId,
  @UpdatedAt);",
                new
                {
                    request.LocalProductId,
                    MutationId = mutationId,
                    IdempotencyKey = idempotencyKey,
                    ClientProductId = product.ClientProductId,
                    RemoteProductId = sealedIntent?.RemoteProductId ??
                        (isDuplicate ? targetRemoteId : null),
                    request.MutationKind,
                    LocalSequence = localSequence,
                    BaseRevision = sealedIntent?.BaseRevision ??
                        (isDuplicate ? targetBaseRevision : null),
                    FieldMaskJson = WriteStringArray(localIntent.FieldMask),
                    IntentJson = intentJson,
                    IntentHash = intentHash,
                    CanonicalPayloadJson = canonicalPayload,
                    PayloadHash = payloadHash,
                    CreatedAt = localIntent.CreatedAt,
                    OccurredAt = localIntent.OccurredAt,
                    State = state,
                    NextAttemptAt = nextAttemptAt,
                    LastTypedCode = NormalizeCode(request.DependencyCode),
                    request.LocalPriceHistoryId,
                    request.LocalStockAdjustmentId,
                    UpdatedAt = nowText
                },
                transaction).ConfigureAwait(false);

            return new ArticleMutationEnqueueResult
            {
                ClientProductId = product.ClientProductId,
                IdempotencyKey = idempotencyKey,
                LocalSequence = localSequence,
                MutationId = mutationId,
                PayloadHash = payloadHash,
                State = state
            };
        }

        public async Task<int> RecoverInterruptedAsync(
            TimeSpan? minimumClaimAge = null,
            string activeClaimOwnerId = null)
        {
            activeClaimOwnerId =
                string.IsNullOrWhiteSpace(activeClaimOwnerId)
                    ? null
                    : activeClaimOwnerId.Trim();
            if (activeClaimOwnerId != null &&
                !PosArticleMutationIntentPolicy.IsSafeId(
                    activeClaimOwnerId))
            {
                throw new ArgumentException(
                    "Active claim owner ID is invalid.",
                    nameof(activeClaimOwnerId));
            }
            using (var connection = _factory.Open())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    var nowText = FormatTimestamp(now);
                    var cutoffText = FormatTimestamp(
                        now.Subtract(minimumClaimAge ?? TimeSpan.Zero));
                    var activeClaimTokenPrefix = activeClaimOwnerId == null
                        ? null
                        : "claim-" + activeClaimOwnerId + "-";
                    var interruptedMutationIds =
                        (await connection.QueryAsync<string>(@"
SELECT mutation_id
FROM article_mutation_outbox
WHERE state = 'in_progress'
  AND updated_at <= @cutoffAt
  AND (
    @activeClaimTokenPrefix IS NULL
    OR claim_token IS NULL
    OR substr(
         claim_token,
         1,
         length(@activeClaimTokenPrefix)) <> @activeClaimTokenPrefix
  );",
                            new
                            {
                                activeClaimTokenPrefix,
                                cutoffAt = cutoffText
                            },
                            transaction).ConfigureAwait(false)).ToArray();
                    if (interruptedMutationIds.Length == 0)
                    {
                        transaction.Commit();
                        return 0;
                    }
                    var recovered = await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET state = 'retry_wait',
    next_attempt_at = @nextAttemptAt,
    last_typed_code = 'client_interrupted',
    claim_generation_id = NULL,
    claim_token = NULL,
    updated_at = @updatedAt
WHERE state = 'in_progress'
  AND mutation_id IN @mutationIds;",
                        new
                        {
                            mutationIds = interruptedMutationIds,
                            nextAttemptAt = now.ToUnixTimeMilliseconds(),
                            updatedAt = nowText
                        },
                        transaction).ConfigureAwait(false);
                    if (recovered != interruptedMutationIds.Length)
                    {
                        throw new InvalidOperationException(
                            "Interrupted article mutation recovery lost its claim fence.");
                    }
                    await connection.ExecuteAsync(@"
UPDATE article_mutation_attempts
SET completed_at = @completedAt,
    outcome = 'client_interrupted'
WHERE completed_at IS NULL
  AND mutation_id IN @mutationIds;",
                        new
                        {
                            completedAt = nowText,
                            mutationIds = interruptedMutationIds
                        },
                        transaction).ConfigureAwait(false);
                    transaction.Commit();
                    return recovered;
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        public async Task<ArticleMutationClaim> ClaimBatchAsync(
            string generationId,
            int maximumCount = PosArticleMutationContract.MaximumBatchCount,
            string claimOwnerId = null)
        {
            generationId = (generationId ?? string.Empty).Trim();
            if (!PosArticleMutationIntentPolicy.IsSafeId(generationId))
                throw new ArgumentException("A safe generation ID is required.");
            claimOwnerId = string.IsNullOrWhiteSpace(claimOwnerId)
                ? null
                : claimOwnerId.Trim();
            if (claimOwnerId != null &&
                !PosArticleMutationIntentPolicy.IsSafeId(claimOwnerId))
            {
                throw new ArgumentException(
                    "A safe claim owner ID is required.",
                    nameof(claimOwnerId));
            }
            if (maximumCount < 1 ||
                maximumCount > PosArticleMutationContract.MaximumBatchCount)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumCount));
            }

            using (var connection = _factory.Open())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    await MaterializeReadyReferenceDependenciesAsync(
                        connection,
                        transaction,
                        now).ConfigureAwait(false);
                    var candidates = (await connection.QueryAsync<ArticleMutationOutboxRow>(@"
SELECT id AS Id,
       local_product_id AS LocalProductId,
       mutation_id AS MutationId,
       idempotency_key AS IdempotencyKey,
       client_product_id AS ClientProductId,
       remote_product_id AS RemoteProductId,
       mutation_kind AS MutationKind,
       local_sequence AS LocalSequence,
       base_revision AS BaseRevision,
       intent_json AS IntentJson,
       canonical_payload_json AS CanonicalPayloadJson,
       payload_hash AS PayloadHash,
       state AS State,
       attempt_count AS AttemptCount
FROM article_mutation_outbox candidate
WHERE candidate.state IN ('pending', 'retry_wait')
  AND candidate.next_attempt_at <= @now
  AND NOT EXISTS (
    SELECT 1
    FROM article_mutation_outbox earlier
    WHERE earlier.client_product_id = candidate.client_product_id
      AND earlier.local_sequence < candidate.local_sequence
      AND earlier.state IN (
        'waiting_dependency',
        'pending',
        'in_progress',
        'retry_wait'))
ORDER BY candidate.next_attempt_at ASC,
         candidate.id ASC
LIMIT 100;",
                        new { now = now.ToUnixTimeMilliseconds() },
                        transaction).ConfigureAwait(false)).ToArray();

                    var selected = new List<ArticleMutationOutboxRow>();
                    var products = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var candidate in candidates)
                    {
                        if (!products.Add(candidate.ClientProductId))
                            continue;
                        if (candidate.AttemptCount == 0 &&
                            await HasStaleBaseAsync(
                                connection,
                                transaction,
                                candidate).ConfigureAwait(false))
                        {
                            await BlockStaleBaseAsync(
                                connection,
                                transaction,
                                candidate,
                                now).ConfigureAwait(false);
                            continue;
                        }
                        selected.Add(candidate);
                        if (selected.Count == maximumCount) break;
                    }

                    if (selected.Count == 0)
                    {
                        transaction.Commit();
                        return new ArticleMutationClaim
                        {
                            ClaimGenerationId = generationId,
                            ClaimToken = string.Empty
                        };
                    }

                    var claimToken = "claim-" +
                        (claimOwnerId == null
                            ? string.Empty
                            : claimOwnerId + "-") +
                        Guid.NewGuid().ToString("N");
                    var requests = new List<PosArticleMutationRequest>(selected.Count);
                    foreach (var item in selected)
                    {
                        var attemptToken = "attempt-" + Guid.NewGuid().ToString("N");
                        var updated = await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET state = 'in_progress',
    attempt_count = attempt_count + 1,
    claim_generation_id = @generationId,
    claim_token = @claimToken,
    updated_at = @updatedAt
WHERE id = @id
  AND state IN ('pending', 'retry_wait');",
                            new
                            {
                                id = item.Id,
                                generationId,
                                claimToken,
                                updatedAt = FormatTimestamp(now)
                            },
                            transaction).ConfigureAwait(false);
                        if (updated != 1)
                            throw new InvalidOperationException(
                                "Article mutation claim lost its compare-and-set fence.");

                        await connection.ExecuteAsync(@"
INSERT INTO article_mutation_attempts(
  mutation_id,
  attempt_token,
  created_at,
  started_at,
  completed_at,
  outcome)
VALUES(
  @mutationId,
  @attemptToken,
  @createdAt,
  @startedAt,
  NULL,
  NULL);",
                            new
                            {
                                mutationId = item.MutationId,
                                attemptToken,
                                createdAt = FormatTimestamp(now),
                                startedAt = FormatTimestamp(now)
                            },
                            transaction).ConfigureAwait(false);
                        requests.Add(new PosArticleMutationRequest
                        {
                            Intent = ParseSealedIntent(item),
                            PayloadHash = item.PayloadHash,
                            AttemptToken = attemptToken
                        });
                    }

                    transaction.Commit();
                    return new ArticleMutationClaim
                    {
                        ClaimGenerationId = generationId,
                        ClaimToken = claimToken,
                        Requests = new ReadOnlyCollection<PosArticleMutationRequest>(
                            requests)
                    };
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        public async Task<IReadOnlyDictionary<string, ISet<string>>>
            GetKnownAttemptTokensAsync(IEnumerable<string> mutationIds)
        {
            var ids = (mutationIds ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var result = new Dictionary<string, ISet<string>>(StringComparer.Ordinal);
            if (ids.Length == 0) return result;

            using (var connection = _factory.Open())
            {
                var rows = await connection.QueryAsync<AttemptTokenRow>(@"
SELECT mutation_id AS MutationId,
       attempt_token AS AttemptToken
FROM article_mutation_attempts
WHERE mutation_id IN @mutationIds;",
                    new { mutationIds = ids }).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    ISet<string> tokens;
                    if (!result.TryGetValue(row.MutationId, out tokens))
                    {
                        tokens = new HashSet<string>(StringComparer.Ordinal);
                        result.Add(row.MutationId, tokens);
                    }
                    tokens.Add(row.AttemptToken);
                }
            }
            return result;
        }

        public async Task ApplyValidatedResponseAsync(
            ArticleMutationClaim claim,
            PosArticleMutationResponseValidation validation)
        {
            if (claim == null) throw new ArgumentNullException(nameof(claim));
            if (validation == null || !validation.IsValid)
                throw new ArgumentException("A validated article mutation response is required.");
            if (claim.Requests.Count != validation.ResultsByMutationId.Count)
                throw new ArgumentException("Validated response count differs from the claim.");

            using (var connection = _factory.Open())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    foreach (var request in claim.Requests)
                    {
                        PosArticleMutationResult result;
                        if (!validation.ResultsByMutationId.TryGetValue(
                                request.Intent.MutationId,
                                out result))
                        {
                            throw new InvalidDataException(
                                "Validated article mutation result is missing.");
                        }

                        var row = await connection.QueryFirstOrDefaultAsync<ArticleMutationOutboxRow>(@"
SELECT id AS Id,
       local_product_id AS LocalProductId,
       mutation_id AS MutationId,
       idempotency_key AS IdempotencyKey,
       client_product_id AS ClientProductId,
       remote_product_id AS RemoteProductId,
       mutation_kind AS MutationKind,
       local_sequence AS LocalSequence,
       base_revision AS BaseRevision,
       field_mask_json AS FieldMaskJson,
       state AS State,
       attempt_count AS AttemptCount,
       local_price_history_id AS LocalPriceHistoryId,
       claim_generation_id AS ClaimGenerationId,
       claim_token AS ClaimToken
FROM article_mutation_outbox
WHERE mutation_id = @mutationId
LIMIT 1;",
                            new { mutationId = request.Intent.MutationId },
                            transaction).ConfigureAwait(false);
                        if (row == null ||
                            !string.Equals(
                                row.State,
                                ArticleMutationOutboxStates.InProgress,
                                StringComparison.Ordinal) ||
                            !string.Equals(
                                row.ClaimGenerationId,
                                claim.ClaimGenerationId,
                                StringComparison.Ordinal) ||
                            !string.Equals(
                                row.ClaimToken,
                                claim.ClaimToken,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                "Stale article mutation ACK rejected by the claim fence.");
                        }

                        var disposition = PosArticleMutationStatusPolicy.Classify(
                            result.DeliveryStatus);
                        if (disposition == PosArticleMutationLocalDisposition.Completed)
                        {
                            await ApplySuccessAsync(
                                connection,
                                transaction,
                                row,
                                request,
                                result,
                                now).ConfigureAwait(false);
                        }
                        else
                        {
                            await ApplyFailureAsync(
                                connection,
                                transaction,
                                row,
                                request,
                                result,
                                disposition,
                                now).ConfigureAwait(false);
                        }
                    }
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        public async Task ReleaseClaimForTransportFailureAsync(
            ArticleMutationClaim claim,
            string typedCode,
            bool authenticationDenied)
        {
            if (claim == null || string.IsNullOrWhiteSpace(claim.ClaimToken))
                return;
            var code = NormalizeCode(typedCode) ??
                (authenticationDenied ? "failed_auth" : "transport_failure");
            using (var connection = _factory.Open())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    foreach (var request in claim.Requests)
                    {
                        var attempt = request.AttemptToken;
                        var attemptCount = await connection.ExecuteScalarAsync<int?>(@"
SELECT attempt_count
FROM article_mutation_outbox
WHERE mutation_id = @mutationId
  AND state = 'in_progress'
  AND claim_generation_id = @generationId
  AND claim_token = @claimToken;",
                            new
                            {
                                mutationId = request.Intent.MutationId,
                                generationId = claim.ClaimGenerationId,
                                claimToken = claim.ClaimToken
                            },
                            transaction).ConfigureAwait(false);
                        if (!attemptCount.HasValue)
                        {
                            // A generation transition/auth stop releases its claims
                            // transactionally before the in-flight request returns.
                            // A stale sender must neither rewrite that released row nor
                            // turn the expected fence loss into a second failure.
                            continue;
                        }
                        var next = authenticationDenied
                            ? now.AddMinutes(1)
                            : now.AddMilliseconds(
                                ComputeBackoffMilliseconds(
                                    attemptCount.Value,
                                    request.Intent.MutationId));
                        await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET state = 'retry_wait',
    next_attempt_at = @nextAttemptAt,
    last_typed_code = @code,
    claim_generation_id = NULL,
    claim_token = NULL,
    updated_at = @updatedAt
WHERE mutation_id = @mutationId;",
                            new
                            {
                                mutationId = request.Intent.MutationId,
                                nextAttemptAt = next.ToUnixTimeMilliseconds(),
                                code,
                                updatedAt = FormatTimestamp(now)
                            },
                            transaction).ConfigureAwait(false);
                        await CompleteAttemptAsync(
                            connection,
                            transaction,
                            request.Intent.MutationId,
                            attempt,
                            code,
                            now).ConfigureAwait(false);
                    }
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        public async Task<ArticleMutationOutboxSummary> GetSummaryAsync()
        {
            using (var connection = _factory.Open())
            {
                var rows = (await connection.QueryAsync<StateCountRow>(@"
SELECT state AS State,
       COUNT(1) AS Count
FROM article_mutation_outbox
GROUP BY state;").ConfigureAwait(false)).ToArray();
                var summary = new ArticleMutationOutboxSummary();
                foreach (var row in rows)
                {
                    switch (row.State)
                    {
                        case ArticleMutationOutboxStates.WaitingDependency:
                            summary.WaitingDependency = row.Count;
                            break;
                        case ArticleMutationOutboxStates.Pending:
                            summary.Pending = row.Count;
                            break;
                        case ArticleMutationOutboxStates.InProgress:
                            summary.InProgress = row.Count;
                            break;
                        case ArticleMutationOutboxStates.RetryWait:
                            summary.RetryWait = row.Count;
                            break;
                        case ArticleMutationOutboxStates.FailedBlocked:
                            summary.FailedBlocked = row.Count;
                            break;
                        case ArticleMutationOutboxStates.Completed:
                            summary.Completed = row.Count;
                            break;
                    }
                }
                summary.AffectedArticleCount = await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(DISTINCT local_product_id)
FROM article_mutation_outbox
WHERE state <> 'completed';").ConfigureAwait(false);
                summary.LastTypedCode = await connection.ExecuteScalarAsync<string>(@"
SELECT last_typed_code
FROM article_mutation_outbox
WHERE last_typed_code IS NOT NULL
ORDER BY CASE WHEN state = 'failed_blocked' THEN 0 ELSE 1 END,
         updated_at DESC,
         id DESC
LIMIT 1;").ConfigureAwait(false);
                return summary;
            }
        }

        public async Task<OutboxDrainState> GetDrainStateAsync()
        {
            using (var connection = _factory.Open())
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var remainingDue = await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_outbox candidate
WHERE candidate.state IN ('pending', 'retry_wait')
  AND candidate.next_attempt_at <= @now
  AND NOT EXISTS (
    SELECT 1
    FROM article_mutation_outbox earlier
    WHERE earlier.client_product_id = candidate.client_product_id
      AND earlier.local_sequence < candidate.local_sequence
      AND earlier.state IN (
        'waiting_dependency',
        'pending',
        'in_progress',
        'retry_wait'));",
                    new { now }).ConfigureAwait(false);
                var nextRetryAt = await connection.ExecuteScalarAsync<long?>(@"
SELECT MIN(next_attempt_at)
FROM article_mutation_outbox
WHERE (
    state IN ('pending', 'retry_wait')
    OR (
      state = 'waiting_dependency'
      AND last_typed_code = @dependencyCode
    )
  )
  AND next_attempt_at > @now;",
                    new
                    {
                        now,
                        dependencyCode = ArticleMutationReferenceDependency.Code
                    }).ConfigureAwait(false);
                return new OutboxDrainState(remainingDue, nextRetryAt);
            }
        }

        public async Task<long> CountUnresolvedAsync(SqliteConnection connection = null)
        {
            if (connection != null)
            {
                return await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state <> 'completed';").ConfigureAwait(false);
            }
            using (var owned = _factory.Open())
            {
                return await owned.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state <> 'completed';").ConfigureAwait(false);
            }
        }

        private static async Task ApplySuccessAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            ArticleMutationOutboxRow row,
            PosArticleMutationRequest request,
            PosArticleMutationResult result,
            DateTimeOffset now)
        {
            var ack = result.Ack;
            var identityRows = await connection.ExecuteAsync(@"
UPDATE products
SET remote_product_id = @remoteProductId,
    remote_base_revision = @authoritativeRevision,
    remote_deleted_at = CASE
      WHEN @mutationKind IN (
        'product_create',
        'product_duplicate',
        'product_activate') THEN NULL
      ELSE remote_deleted_at
    END,
    is_active = CASE
      WHEN @mutationKind = 'product_deactivate' THEN 0
      WHEN @mutationKind IN (
        'product_create',
        'product_duplicate',
        'product_activate') THEN 1
      ELSE is_active
    END
WHERE id = @localProductId
  AND client_product_id = @clientProductId;",
                new
                {
                    remoteProductId = ack.RemoteProductId,
                    authoritativeRevision = ack.AuthoritativeRevision,
                    mutationKind = row.MutationKind,
                    localProductId = row.LocalProductId,
                    clientProductId = row.ClientProductId
                },
                transaction).ConfigureAwait(false);
            if (identityRows != 1)
                throw new InvalidDataException(
                    "Article mutation ACK could not update the stable local product.");

            if (IsPriceMutation(row.MutationKind))
            {
                var remotePriceId = (ack.PriceHistoryId ?? string.Empty).Trim();
                if (!row.LocalPriceHistoryId.HasValue ||
                    remotePriceId.Length == 0)
                {
                    throw new InvalidDataException(
                        "Price mutation ACK is missing durable price identity.");
                }
                var localPrice =
                    await connection.QueryFirstOrDefaultAsync<PriceHistoryBindingRow>(@"
SELECT id AS Id,
       timestamp AS Timestamp,
       type AS Type,
       new_price AS NewPrice,
       source AS Source,
       remote_price_id AS RemotePriceId,
       article_mutation_id AS ArticleMutationId
FROM product_price_history
WHERE id = @localPriceHistoryId
  AND article_mutation_id = @mutationId
LIMIT 1;",
                        new
                        {
                            localPriceHistoryId =
                                row.LocalPriceHistoryId.Value,
                            mutationId = row.MutationId
                        },
                        transaction).ConfigureAwait(false);
                if (localPrice == null ||
                    (!string.IsNullOrWhiteSpace(localPrice.RemotePriceId) &&
                     !string.Equals(
                         localPrice.RemotePriceId,
                         remotePriceId,
                         StringComparison.Ordinal)))
                {
                    throw new InvalidDataException(
                        "Price mutation ACK conflicts with its local history identity.");
                }

                var pulledPrice =
                    await connection.QueryFirstOrDefaultAsync<PriceHistoryBindingRow>(@"
SELECT id AS Id,
       timestamp AS Timestamp,
       type AS Type,
       new_price AS NewPrice,
       source AS Source,
       remote_price_id AS RemotePriceId,
       article_mutation_id AS ArticleMutationId
FROM product_price_history
WHERE remote_price_id = @remotePriceId
LIMIT 1;",
                        new { remotePriceId },
                        transaction).ConfigureAwait(false);
                if (pulledPrice != null &&
                    pulledPrice.Id != localPrice.Id)
                {
                    if (pulledPrice.ArticleMutationId != null ||
                        !string.Equals(
                            pulledPrice.Type,
                            localPrice.Type,
                            StringComparison.OrdinalIgnoreCase) ||
                        pulledPrice.NewPrice != localPrice.NewPrice ||
                        !string.Equals(
                            pulledPrice.Source,
                            "pos_article_mutation_v1",
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Price mutation ACK conflicts with pulled price evidence.");
                    }
                    var removed = await connection.ExecuteAsync(@"
DELETE FROM product_price_history
WHERE id = @id
  AND remote_price_id = @remotePriceId
  AND article_mutation_id IS NULL;",
                        new
                        {
                            id = pulledPrice.Id,
                            remotePriceId
                        },
                        transaction).ConfigureAwait(false);
                    if (removed != 1)
                    {
                        throw new InvalidDataException(
                            "Price mutation ACK could not merge pulled price evidence.");
                    }
                }
                var priceRows = await connection.ExecuteAsync(@"
UPDATE product_price_history
SET remote_price_id = @remotePriceId,
    timestamp = COALESCE(@pulledTimestamp, timestamp),
    type = COALESCE(@pulledType, type),
    source = COALESCE(@pulledSource, source)
WHERE id = @localPriceHistoryId
  AND article_mutation_id = @mutationId
  AND (
    remote_price_id IS NULL
    OR remote_price_id = @remotePriceId
  );",
                    new
                    {
                        remotePriceId,
                        pulledTimestamp = pulledPrice != null &&
                            pulledPrice.Id != localPrice.Id
                                ? pulledPrice.Timestamp
                                : null,
                        pulledType = pulledPrice != null &&
                            pulledPrice.Id != localPrice.Id
                                ? pulledPrice.Type
                                : null,
                        pulledSource = pulledPrice != null &&
                            pulledPrice.Id != localPrice.Id
                                ? pulledPrice.Source
                                : null,
                        localPriceHistoryId = row.LocalPriceHistoryId.Value,
                        mutationId = row.MutationId
                    },
                    transaction).ConfigureAwait(false);
                if (priceRows != 1)
                {
                    throw new InvalidDataException(
                        "Price mutation ACK could not bind its local history row.");
                }
                var matchingOwnership = await connection.ExecuteScalarAsync<long>(@"
INSERT OR IGNORE INTO remote_catalog_price_ownership(
  remote_price_id,
  remote_product_id)
VALUES(@remotePriceId, @remoteProductId);
SELECT COUNT(1)
FROM remote_catalog_price_ownership
WHERE remote_price_id = @remotePriceId
  AND remote_product_id = @remoteProductId;",
                    new
                    {
                        remotePriceId,
                        remoteProductId = ack.RemoteProductId
                    },
                    transaction).ConfigureAwait(false);
                if (matchingOwnership != 1)
                {
                    throw new InvalidDataException(
                        "Price mutation ACK conflicts with remote price ownership.");
                }
            }

            await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET state = 'completed',
    last_typed_code = @deliveryStatus,
    authoritative_revision = @authoritativeRevision,
    catalog_revision = @catalogRevision,
    remote_price_history_id = @priceHistoryId,
    remote_stock_movement_id = @stockMovementId,
    remote_assigned_product_id = @remoteProductId,
    ack_status = @ackStatus,
    ack_code = @ackCode,
    ack_attempt_token = @ackAttemptToken,
    ack_server_timestamp = @ackServerTimestamp,
    ack_terminal = @ackTerminal,
    ack_retryable = @ackRetryable,
    claim_generation_id = NULL,
    claim_token = NULL,
    completed_at = @completedAt,
    updated_at = @updatedAt
WHERE id = @id;",
                new
                {
                    id = row.Id,
                    deliveryStatus = result.DeliveryStatus,
                    authoritativeRevision = ack.AuthoritativeRevision,
                    catalogRevision = ack.CatalogRevision,
                    priceHistoryId = ack.PriceHistoryId,
                    stockMovementId = ack.StockMovementId,
                    remoteProductId = ack.RemoteProductId,
                    ackStatus = ack.Status,
                    ackCode = ack.Code,
                    ackAttemptToken = ack.AttemptToken,
                    ackServerTimestamp = ack.ServerTimestamp,
                    ackTerminal = ack.Terminal ? 1 : 0,
                    ackRetryable = ack.Retryable ? 1 : 0,
                    completedAt = FormatTimestamp(now),
                    updatedAt = FormatTimestamp(now)
                },
                transaction).ConfigureAwait(false);
            await ResolveCoveredBlockedMutationsAsync(
                connection,
                transaction,
                row,
                now).ConfigureAwait(false);
            await CompleteAttemptAsync(
                connection,
                transaction,
                row.MutationId,
                request.AttemptToken,
                result.DeliveryStatus,
                now).ConfigureAwait(false);
            await FreezeNextDependencyTargetAsync(
                connection,
                transaction,
                row,
                ack.RemoteProductId,
                ack.AuthoritativeRevision,
                now).ConfigureAwait(false);
            await MaterializeNextDependencyAsync(
                connection,
                transaction,
                row.LocalProductId,
                row.ClientProductId,
                now).ConfigureAwait(false);
        }

        private static bool IsPriceMutation(string mutationKind)
        {
            return string.Equals(
                    mutationKind,
                    PosArticleMutationKinds.ProductRetailPriceChange,
                    StringComparison.Ordinal) ||
                string.Equals(
                    mutationKind,
                    PosArticleMutationKinds.ProductPurchasePriceChange,
                    StringComparison.Ordinal);
        }

        private static async Task ApplyFailureAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            ArticleMutationOutboxRow row,
            PosArticleMutationRequest request,
            PosArticleMutationResult result,
            PosArticleMutationLocalDisposition disposition,
            DateTimeOffset now)
        {
            var state = disposition == PosArticleMutationLocalDisposition.FailedBlocked
                ? ArticleMutationOutboxStates.FailedBlocked
                : ArticleMutationOutboxStates.RetryWait;
            var next = disposition == PosArticleMutationLocalDisposition.RetryWait
                ? now.AddMilliseconds(
                    ComputeBackoffMilliseconds(
                        row.AttemptCount,
                        row.MutationId))
                : disposition == PosArticleMutationLocalDisposition.AuthStop
                    ? now.AddMinutes(1)
                    : now;
            if (disposition == PosArticleMutationLocalDisposition.FailedBlocked &&
                string.Equals(
                    result.DeliveryStatus,
                    PosArticleMutationStatusPolicy.FailedConflict,
                    StringComparison.Ordinal) &&
                PosArticleMutationIntentPolicy.IsProductRevision(
                    result.Ack.AuthoritativeRevision))
            {
                var identityRows = await connection.ExecuteAsync(@"
UPDATE products
SET remote_base_revision = @authoritativeRevision
WHERE id = @localProductId
  AND client_product_id = @clientProductId;",
                    new
                    {
                        authoritativeRevision =
                            result.Ack.AuthoritativeRevision,
                        localProductId = row.LocalProductId,
                        clientProductId = row.ClientProductId
                    },
                    transaction).ConfigureAwait(false);
                if (identityRows != 1)
                {
                    throw new InvalidDataException(
                        "Conflict ACK could not update the stable local product revision.");
                }
            }
            await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET state = @state,
    next_attempt_at = @nextAttemptAt,
    last_typed_code = @deliveryStatus,
    authoritative_revision = @authoritativeRevision,
    catalog_revision = @catalogRevision,
    ack_status = @ackStatus,
    ack_code = @ackCode,
    ack_attempt_token = @ackAttemptToken,
    ack_server_timestamp = @ackServerTimestamp,
    ack_terminal = @ackTerminal,
    ack_retryable = @ackRetryable,
    claim_generation_id = NULL,
    claim_token = NULL,
    completed_at = CASE WHEN @state = 'failed_blocked' THEN @completedAt ELSE NULL END,
    updated_at = @updatedAt
WHERE id = @id;",
                new
                {
                    id = row.Id,
                    state,
                    nextAttemptAt = next.ToUnixTimeMilliseconds(),
                    deliveryStatus = result.DeliveryStatus,
                    authoritativeRevision =
                        result.Ack.AuthoritativeRevision,
                    catalogRevision = result.Ack.CatalogRevision,
                    ackStatus = result.Ack.Status,
                    ackCode = result.Ack.Code,
                    ackAttemptToken = result.Ack.AttemptToken,
                    ackServerTimestamp = result.Ack.ServerTimestamp,
                    ackTerminal = result.Ack.Terminal ? 1 : 0,
                    ackRetryable = result.Ack.Retryable ? 1 : 0,
                    completedAt = FormatTimestamp(now),
                    updatedAt = FormatTimestamp(now)
                },
                transaction).ConfigureAwait(false);
            await CompleteAttemptAsync(
                connection,
                transaction,
                row.MutationId,
                request.AttemptToken,
                result.DeliveryStatus,
                now).ConfigureAwait(false);
            if (string.Equals(
                    state,
                    ArticleMutationOutboxStates.FailedBlocked,
                    StringComparison.Ordinal) &&
                await FreezeNextDependencyFromCurrentProductAsync(
                    connection,
                    transaction,
                    row,
                    now).ConfigureAwait(false))
            {
                await MaterializeNextDependencyAsync(
                    connection,
                    transaction,
                    row.LocalProductId,
                    row.ClientProductId,
                    now).ConfigureAwait(false);
            }
        }

        private static async Task MaterializeReadyReferenceDependenciesAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            DateTimeOffset now)
        {
            var targets = (await connection.QueryAsync<DependencyTargetRow>(@"
SELECT candidate.local_product_id AS LocalProductId,
       candidate.client_product_id AS ClientProductId
FROM article_mutation_outbox candidate
WHERE candidate.state = 'waiting_dependency'
  AND candidate.last_typed_code = @dependencyCode
  AND candidate.next_attempt_at <= @now
  AND NOT EXISTS (
    SELECT 1
    FROM article_mutation_outbox earlier
    WHERE earlier.client_product_id = candidate.client_product_id
      AND earlier.local_sequence < candidate.local_sequence
      AND earlier.state IN (
        'waiting_dependency',
        'pending',
        'in_progress',
        'retry_wait'))
ORDER BY candidate.next_attempt_at ASC,
         candidate.id ASC
LIMIT 100;",
                new
                {
                    dependencyCode = ArticleMutationReferenceDependency.Code,
                    now = now.ToUnixTimeMilliseconds()
                },
                transaction).ConfigureAwait(false)).ToArray();
            foreach (var target in targets)
            {
                await MaterializeNextDependencyAsync(
                    connection,
                    transaction,
                    target.LocalProductId,
                    target.ClientProductId,
                    now).ConfigureAwait(false);
            }
        }

        private static async Task MaterializeNextDependencyAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long localProductId,
            string clientProductId,
            DateTimeOffset now)
        {
            while (true)
            {
                var next = await connection.QueryFirstOrDefaultAsync<ArticleMutationOutboxRow>(@"
SELECT id AS Id,
       local_product_id AS LocalProductId,
       mutation_id AS MutationId,
       idempotency_key AS IdempotencyKey,
       client_product_id AS ClientProductId,
       mutation_kind AS MutationKind,
       local_sequence AS LocalSequence,
       remote_product_id AS RemoteProductId,
       base_revision AS BaseRevision,
       intent_json AS IntentJson,
       intent_hash AS IntentHash,
       last_typed_code AS LastTypedCode,
       state AS State
FROM article_mutation_outbox
WHERE local_product_id = @localProductId
  AND client_product_id = @clientProductId
  AND state = 'waiting_dependency'
ORDER BY local_sequence ASC
LIMIT 1;",
                    new { localProductId, clientProductId },
                    transaction).ConfigureAwait(false);
                if (next == null) return;

                PersistedCanonicalIntent unresolved;
                IDictionary<string, object> changes;
                try
                {
                    var computedIntentHash =
                        PosArticleMutationPayloadHash.Compute(
                            new UTF8Encoding(false, true).GetBytes(
                                next.IntentJson ?? string.Empty));
                    if (!string.Equals(
                            computedIntentHash,
                            next.IntentHash,
                            StringComparison.Ordinal))
                    {
                        if (!await BlockDependencyAndFreezeSuccessorAsync(
                            connection,
                            transaction,
                            next,
                            "dependency_intent_hash_mismatch",
                            now).ConfigureAwait(false))
                        {
                            return;
                        }
                        continue;
                    }
                    unresolved = DeserializePersistedIntent(next.IntentJson);
                    changes = RebuildChanges(unresolved);
                }
                catch (InvalidDataException)
                {
                    if (!await BlockDependencyAndFreezeSuccessorAsync(
                        connection,
                        transaction,
                        next,
                        "dependency_intent_invalid",
                        now).ConfigureAwait(false))
                    {
                        return;
                    }
                    continue;
                }

                if (!await ResolveReferenceDependenciesAsync(
                        connection,
                        transaction,
                        changes).ConfigureAwait(false))
                {
                    await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET next_attempt_at = @nextAttemptAt,
    last_typed_code = @dependencyCode,
    updated_at = @updatedAt
WHERE id = @id
  AND state = 'waiting_dependency';",
                    new
                    {
                        id = next.Id,
                        dependencyCode = ArticleMutationReferenceDependency.Code,
                        nextAttemptAt = now.AddSeconds(30).ToUnixTimeMilliseconds(),
                        updatedAt = FormatTimestamp(now)
                    },
                    transaction).ConfigureAwait(false);
                    return;
                }

                var product =
                    await connection.QueryFirstOrDefaultAsync<ProductMutationIdentityRow>(@"
SELECT id AS Id,
       client_product_id AS ClientProductId,
       remote_product_id AS RemoteProductId,
       remote_base_revision AS RemoteBaseRevision
FROM products
WHERE id = @localProductId
LIMIT 1;",
                        new { localProductId },
                        transaction).ConfigureAwait(false);
                if (product == null ||
                    !string.Equals(
                        product.ClientProductId,
                        next.ClientProductId,
                        StringComparison.Ordinal))
                {
                    if (!await BlockDependencyAndFreezeSuccessorAsync(
                        connection,
                        transaction,
                        next,
                        "dependency_product_identity_missing",
                        now).ConfigureAwait(false))
                    {
                        return;
                    }
                    continue;
                }

                var isCreate = string.Equals(
                    next.MutationKind,
                    PosArticleMutationKinds.ProductCreate,
                    StringComparison.Ordinal);
                var isDuplicate = string.Equals(
                    next.MutationKind,
                    PosArticleMutationKinds.ProductDuplicate,
                    StringComparison.Ordinal);
                var hasFrozenBase =
                    !isCreate &&
                    !isDuplicate &&
                    (!string.IsNullOrWhiteSpace(next.RemoteProductId) ||
                     !string.IsNullOrWhiteSpace(next.BaseRevision) ||
                     !string.IsNullOrWhiteSpace(unresolved.RemoteProductId) ||
                     !string.IsNullOrWhiteSpace(unresolved.BaseRevision));
                var hasPersistedTarget =
                    !string.IsNullOrWhiteSpace(next.RemoteProductId) ||
                    !string.IsNullOrWhiteSpace(next.BaseRevision);
                var remoteProductId = isCreate
                    ? null
                    : isDuplicate
                        ? next.RemoteProductId
                        : hasFrozenBase
                            ? hasPersistedTarget
                                ? next.RemoteProductId
                                : unresolved.RemoteProductId
                            : product.RemoteProductId;
                var baseRevision = isCreate
                    ? null
                    : isDuplicate
                        ? next.BaseRevision
                        : hasFrozenBase
                            ? hasPersistedTarget
                                ? next.BaseRevision
                                : unresolved.BaseRevision
                            : product.RemoteBaseRevision;
                if (hasFrozenBase &&
                    (!string.Equals(
                         product.RemoteProductId,
                         remoteProductId,
                         StringComparison.Ordinal) ||
                     !string.Equals(
                         product.RemoteBaseRevision,
                         baseRevision,
                         StringComparison.Ordinal)))
                {
                    if (!await BlockDependencyAndFreezeSuccessorAsync(
                        connection,
                        transaction,
                        next,
                        "failed_conflict",
                        now).ConfigureAwait(false))
                    {
                        return;
                    }
                    continue;
                }
                if (!isCreate &&
                    (string.IsNullOrWhiteSpace(remoteProductId) ||
                     string.IsNullOrWhiteSpace(baseRevision)))
                {
                    if (!await BlockDependencyAndFreezeSuccessorAsync(
                        connection,
                        transaction,
                        next,
                        "dependency_target_identity_missing",
                        now).ConfigureAwait(false))
                    {
                        return;
                    }
                    continue;
                }

                PosArticleMutationIntent sealedIntent;
                try
                {
                    sealedIntent = PosArticleMutationIntentPolicy.Rehydrate(
                        baseRevision,
                        changes,
                        next.ClientProductId,
                        unresolved.CreatedAt,
                        unresolved.FieldMask,
                        next.IdempotencyKey,
                        next.LocalSequence,
                        next.MutationId,
                        next.MutationKind,
                        unresolved.OccurredAt,
                        remoteProductId);
                }
                catch (ArgumentException)
                {
                    if (!await BlockDependencyAndFreezeSuccessorAsync(
                        connection,
                        transaction,
                        next,
                        "dependency_materialization_invalid",
                        now).ConfigureAwait(false))
                    {
                        return;
                    }
                    continue;
                }

                var canonical =
                    PosArticleMutationCanonicalWriter.Write(sealedIntent);
                var payloadHash =
                    PosArticleMutationPayloadHash.Compute(sealedIntent);
                await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET remote_product_id = @remoteProductId,
    base_revision = @baseRevision,
    canonical_payload_json = @canonicalPayloadJson,
    payload_hash = @payloadHash,
    state = 'pending',
    next_attempt_at = 0,
    last_typed_code = NULL,
    updated_at = @updatedAt
WHERE id = @id
  AND state = 'waiting_dependency'
  AND canonical_payload_json IS NULL
  AND payload_hash IS NULL;",
                new
                {
                    id = next.Id,
                    remoteProductId = sealedIntent.RemoteProductId,
                    baseRevision = sealedIntent.BaseRevision,
                    canonicalPayloadJson = canonical,
                    payloadHash,
                    updatedAt = FormatTimestamp(now)
                },
                    transaction).ConfigureAwait(false);
                return;
            }
        }

        private static async Task<bool>
            FreezeNextDependencyFromCurrentProductAsync(
                SqliteConnection connection,
                SqliteTransaction transaction,
                ArticleMutationOutboxRow row,
                DateTimeOffset now)
        {
            var product =
                await connection.QueryFirstOrDefaultAsync<ProductMutationIdentityRow>(@"
SELECT id AS Id,
       client_product_id AS ClientProductId,
       remote_product_id AS RemoteProductId,
       remote_base_revision AS RemoteBaseRevision
FROM products
WHERE id = @localProductId
  AND client_product_id = @clientProductId
LIMIT 1;",
                    new
                    {
                        localProductId = row.LocalProductId,
                        clientProductId = row.ClientProductId
                    },
                    transaction).ConfigureAwait(false);
            if (product == null ||
                !Guid.TryParse(product.RemoteProductId, out _) ||
                !PosArticleMutationIntentPolicy.IsProductRevision(
                    product.RemoteBaseRevision))
            {
                return false;
            }

            await FreezeNextDependencyTargetAsync(
                connection,
                transaction,
                row,
                product.RemoteProductId,
                product.RemoteBaseRevision,
                now).ConfigureAwait(false);
            return true;
        }

        private static Task FreezeNextDependencyTargetAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            ArticleMutationOutboxRow row,
            string remoteProductId,
            string authoritativeRevision,
            DateTimeOffset now)
        {
            return connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET remote_product_id = @remoteProductId,
    base_revision = @authoritativeRevision,
    updated_at = @updatedAt
WHERE id = (
    SELECT successor.id
    FROM article_mutation_outbox successor
    WHERE successor.client_product_id = @clientProductId
      AND successor.local_sequence > @localSequence
      AND successor.state = 'waiting_dependency'
      AND successor.mutation_kind NOT IN (
        'product_create',
        'product_duplicate')
    ORDER BY successor.local_sequence ASC
    LIMIT 1
  )
  AND state = 'waiting_dependency';",
                new
                {
                    remoteProductId,
                    authoritativeRevision,
                    clientProductId = row.ClientProductId,
                    localSequence = row.LocalSequence,
                    updatedAt = FormatTimestamp(now)
                },
                transaction);
        }

        private static async Task ResolveCoveredBlockedMutationsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            ArticleMutationOutboxRow current,
            DateTimeOffset now)
        {
            if (!CanResolveBlockedKind(current.MutationKind))
                return;

            var currentMask = TryReadFieldMask(current.FieldMaskJson);
            if (string.Equals(
                    current.MutationKind,
                    PosArticleMutationKinds.ProductUpdate,
                    StringComparison.Ordinal) &&
                currentMask == null)
            {
                return;
            }

            var candidates =
                (await connection.QueryAsync<ArticleMutationOutboxRow>(@"
SELECT id AS Id,
       mutation_id AS MutationId,
       mutation_kind AS MutationKind,
       field_mask_json AS FieldMaskJson
FROM article_mutation_outbox
WHERE client_product_id = @clientProductId
  AND local_sequence < @localSequence
  AND state = 'failed_blocked'
ORDER BY local_sequence ASC;",
                    new
                    {
                        clientProductId = current.ClientProductId,
                        localSequence = current.LocalSequence
                    },
                    transaction).ConfigureAwait(false)).ToArray();

            var coveredIds = candidates
                .Where(candidate => IsCoveredCorrection(
                    candidate,
                    current.MutationKind,
                    currentMask))
                .Select(candidate => candidate.Id)
                .ToArray();
            if (coveredIds.Length == 0)
                return;

            await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET state = 'completed',
    resolution_code = 'superseded_by_correction',
    resolved_at = @resolvedAt,
    superseded_by_mutation_id = @supersededByMutationId,
    updated_at = @updatedAt
WHERE id IN @ids
  AND state = 'failed_blocked';",
                new
                {
                    ids = coveredIds,
                    resolvedAt = FormatTimestamp(now),
                    supersededByMutationId = current.MutationId,
                    updatedAt = FormatTimestamp(now)
                },
                transaction).ConfigureAwait(false);
        }

        private static bool CanResolveBlockedKind(string mutationKind)
        {
            return string.Equals(
                    mutationKind,
                    PosArticleMutationKinds.ProductUpdate,
                    StringComparison.Ordinal) ||
                IsPriceMutation(mutationKind) ||
                IsActivationMutation(mutationKind);
        }

        private static bool IsCoveredCorrection(
            ArticleMutationOutboxRow blocked,
            string currentKind,
            ISet<string> currentMask)
        {
            if (string.Equals(
                    currentKind,
                    PosArticleMutationKinds.ProductUpdate,
                    StringComparison.Ordinal))
            {
                if (!string.Equals(
                        blocked.MutationKind,
                        PosArticleMutationKinds.ProductUpdate,
                        StringComparison.Ordinal))
                {
                    return false;
                }
                var blockedMask = TryReadFieldMask(blocked.FieldMaskJson);
                return blockedMask != null &&
                    blockedMask.All(currentMask.Contains);
            }
            if (IsPriceMutation(currentKind))
            {
                return string.Equals(
                    blocked.MutationKind,
                    currentKind,
                    StringComparison.Ordinal);
            }
            return IsActivationMutation(currentKind) &&
                IsActivationMutation(blocked.MutationKind);
        }

        private static bool IsActivationMutation(string mutationKind)
        {
            return string.Equals(
                    mutationKind,
                    PosArticleMutationKinds.ProductActivate,
                    StringComparison.Ordinal) ||
                string.Equals(
                    mutationKind,
                    PosArticleMutationKinds.ProductDeactivate,
                    StringComparison.Ordinal);
        }

        private static ISet<string> TryReadFieldMask(string json)
        {
            try
            {
                var bytes = new UTF8Encoding(false, true).GetBytes(
                    json ?? string.Empty);
                using (var stream = new MemoryStream(bytes))
                {
                    var serializer = new DataContractJsonSerializer(
                        typeof(string[]));
                    var values = serializer.ReadObject(stream) as string[];
                    if (values == null ||
                        values.Any(string.IsNullOrWhiteSpace))
                    {
                        return null;
                    }
                    return new HashSet<string>(
                        values,
                        StringComparer.Ordinal);
                }
            }
            catch (Exception ex) when (
                ex is SerializationException ||
                ex is System.Xml.XmlException ||
                ex is EncoderFallbackException)
            {
                return null;
            }
        }

        private static async Task<bool> ResolveReferenceDependenciesAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            IDictionary<string, object> changes)
        {
            if (changes == null) return true;
            object value;
            int localId;
            if (changes.TryGetValue(
                    PosArticleMutationFields.CategoryId,
                    out value) &&
                ArticleMutationReferenceDependency.TryParseCategory(
                    value as string,
                    out localId))
            {
                var remoteId = await connection.ExecuteScalarAsync<string>(@"
SELECT remote_category_id
FROM categories
WHERE id = @id
  AND COALESCE(is_active, 1) = 1
LIMIT 1;",
                    new { id = localId },
                    transaction).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(remoteId)) return false;
                changes[PosArticleMutationFields.CategoryId] = remoteId.Trim();
            }
            if (changes.TryGetValue(
                    PosArticleMutationFields.SupplierId,
                    out value) &&
                ArticleMutationReferenceDependency.TryParseSupplier(
                    value as string,
                    out localId))
            {
                var remoteId = await connection.ExecuteScalarAsync<string>(@"
SELECT remote_supplier_id
FROM suppliers
WHERE id = @id
  AND COALESCE(is_active, 1) = 1
LIMIT 1;",
                    new { id = localId },
                    transaction).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(remoteId)) return false;
                changes[PosArticleMutationFields.SupplierId] = remoteId.Trim();
            }
            return true;
        }

        private static async Task<bool> BlockDependencyAndFreezeSuccessorAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            ArticleMutationOutboxRow row,
            string code,
            DateTimeOffset now)
        {
            var blocked = await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET state = 'failed_blocked',
    last_typed_code = @code,
    completed_at = @completedAt,
    updated_at = @updatedAt
WHERE id = @id
  AND state = 'waiting_dependency';",
                new
                {
                    id = row.Id,
                    code,
                    completedAt = FormatTimestamp(now),
                    updatedAt = FormatTimestamp(now)
                },
                transaction).ConfigureAwait(false);
            if (blocked != 1)
            {
                throw new InvalidOperationException(
                    "dependency_block_transition_failed");
            }

            return await FreezeNextDependencyFromCurrentProductAsync(
                connection,
                transaction,
                row,
                now).ConfigureAwait(false);
        }

        private static async Task<bool> HasStaleBaseAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            ArticleMutationOutboxRow row)
        {
            if (string.Equals(
                    row.MutationKind,
                    PosArticleMutationKinds.ProductCreate,
                    StringComparison.Ordinal) ||
                string.Equals(
                    row.MutationKind,
                    PosArticleMutationKinds.ProductDuplicate,
                    StringComparison.Ordinal))
            {
                return false;
            }
            var current = await connection.ExecuteScalarAsync<string>(@"
SELECT remote_base_revision
FROM products
WHERE id = @localProductId;",
                new { localProductId = row.LocalProductId },
                transaction).ConfigureAwait(false);
            return !string.Equals(current, row.BaseRevision, StringComparison.Ordinal);
        }

        private static async Task BlockStaleBaseAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            ArticleMutationOutboxRow row,
            DateTimeOffset now)
        {
            var blocked = await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET state = 'failed_blocked',
    last_typed_code = 'failed_conflict',
    completed_at = @completedAt,
    updated_at = @updatedAt
WHERE id = @id
  AND state IN ('pending', 'retry_wait');",
                new
                {
                    id = row.Id,
                    completedAt = FormatTimestamp(now),
                    updatedAt = FormatTimestamp(now)
                },
                transaction).ConfigureAwait(false);
            if (blocked != 1)
            {
                throw new InvalidOperationException(
                    "stale_base_block_transition_failed");
            }
            if (!await FreezeNextDependencyFromCurrentProductAsync(
                    connection,
                    transaction,
                    row,
                    now).ConfigureAwait(false))
            {
                return;
            }
            await MaterializeNextDependencyAsync(
                connection,
                transaction,
                row.LocalProductId,
                row.ClientProductId,
                now).ConfigureAwait(false);
        }

        private static PosArticleMutationIntent ParseSealedIntent(
            ArticleMutationOutboxRow row)
        {
            if (string.IsNullOrWhiteSpace(row.CanonicalPayloadJson) ||
                string.IsNullOrWhiteSpace(row.PayloadHash))
            {
                throw new InvalidDataException(
                    "Wire-eligible article mutation is not sealed.");
            }
            var persisted = DeserializePersistedIntent(row.CanonicalPayloadJson);
            var intent = PosArticleMutationIntentPolicy.Rehydrate(
                persisted.BaseRevision,
                RebuildChanges(persisted),
                persisted.ClientProductId,
                persisted.CreatedAt,
                persisted.FieldMask,
                persisted.IdempotencyKey,
                persisted.LocalSequence,
                persisted.MutationId,
                persisted.MutationKind,
                persisted.OccurredAt,
                persisted.RemoteProductId);
            var canonical = PosArticleMutationCanonicalWriter.Write(intent);
            if (!string.Equals(
                    canonical,
                    row.CanonicalPayloadJson,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    PosArticleMutationPayloadHash.Compute(intent),
                    row.PayloadHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Persisted article mutation payload seal is invalid.");
            }
            return intent;
        }

        private static PersistedCanonicalIntent DeserializePersistedIntent(
            string json)
        {
            try
            {
                var bytes = new UTF8Encoding(false, true).GetBytes(json ?? string.Empty);
                using (var stream = new MemoryStream(bytes))
                {
                    var serializer = new DataContractJsonSerializer(
                        typeof(PersistedCanonicalIntent));
                    return serializer.ReadObject(stream) as PersistedCanonicalIntent ??
                        throw new SerializationException("Persisted intent is null.");
                }
            }
            catch (Exception ex) when (
                ex is SerializationException ||
                ex is System.Xml.XmlException ||
                ex is EncoderFallbackException)
            {
                throw new InvalidDataException(
                    "Persisted article mutation intent is invalid.",
                    ex);
            }
        }

        private static IDictionary<string, object> RebuildChanges(
            PersistedCanonicalIntent persisted)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            var fields = FieldsFor(persisted);
            foreach (var field in fields)
            {
                switch (field)
                {
                    case PosArticleMutationFields.Barcode:
                        result[field] = persisted.Changes?.Barcode;
                        break;
                    case PosArticleMutationFields.ItemNumber:
                        result[field] = persisted.Changes?.ItemNumber;
                        break;
                    case PosArticleMutationFields.PrimaryName:
                        result[field] = persisted.Changes?.PrimaryName;
                        break;
                    case PosArticleMutationFields.SecondaryName:
                        result[field] = persisted.Changes?.SecondaryName;
                        break;
                    case PosArticleMutationFields.CategoryId:
                        result[field] = persisted.Changes?.CategoryId;
                        break;
                    case PosArticleMutationFields.SupplierId:
                        result[field] = persisted.Changes?.SupplierId;
                        break;
                    case PosArticleMutationFields.PurchasePrice:
                        result[field] = RequiredNumber(
                            persisted.Changes?.PurchasePrice,
                            field);
                        break;
                    case PosArticleMutationFields.RetailPrice:
                        result[field] = RequiredNumber(
                            persisted.Changes?.RetailPrice,
                            field);
                        break;
                    case PosArticleMutationFields.StockQuantity:
                        result[field] = RequiredNumber(
                            persisted.Changes?.StockQuantity,
                            field);
                        break;
                    case PosArticleMutationFields.Price:
                        result[field] = RequiredNumber(
                            persisted.Changes?.Price,
                            field);
                        break;
                    case PosArticleMutationFields.QuantityDelta:
                        result[field] = RequiredNumber(
                            persisted.Changes?.QuantityDelta,
                            field);
                        break;
                    case PosArticleMutationFields.Reason:
                        result[field] = persisted.Changes?.Reason;
                        break;
                }
            }
            return result;
        }

        private static IEnumerable<string> FieldsFor(PersistedCanonicalIntent persisted)
        {
            if (string.Equals(
                persisted.MutationKind,
                PosArticleMutationKinds.ProductUpdate,
                StringComparison.Ordinal))
            {
                return persisted.FieldMask ?? Array.Empty<string>();
            }
            if (string.Equals(
                    persisted.MutationKind,
                    PosArticleMutationKinds.ProductRetailPriceChange,
                    StringComparison.Ordinal) ||
                string.Equals(
                    persisted.MutationKind,
                    PosArticleMutationKinds.ProductPurchasePriceChange,
                    StringComparison.Ordinal))
            {
                return new[] { PosArticleMutationFields.Price };
            }
            if (string.Equals(
                persisted.MutationKind,
                PosArticleMutationKinds.ProductManualStockAdjustment,
                StringComparison.Ordinal))
            {
                return new[]
                {
                    PosArticleMutationFields.QuantityDelta,
                    PosArticleMutationFields.Reason
                };
            }
            if (string.Equals(
                    persisted.MutationKind,
                    PosArticleMutationKinds.ProductActivate,
                    StringComparison.Ordinal) ||
                string.Equals(
                    persisted.MutationKind,
                    PosArticleMutationKinds.ProductDeactivate,
                    StringComparison.Ordinal))
            {
                return Array.Empty<string>();
            }

            return new[]
            {
                PosArticleMutationFields.Barcode,
                PosArticleMutationFields.ItemNumber,
                PosArticleMutationFields.PrimaryName,
                PosArticleMutationFields.SecondaryName,
                PosArticleMutationFields.CategoryId,
                PosArticleMutationFields.SupplierId,
                PosArticleMutationFields.PurchasePrice,
                PosArticleMutationFields.RetailPrice,
                PosArticleMutationFields.StockQuantity
            }.Where(field => HasPersistedValue(persisted.Changes, field));
        }

        private static bool HasPersistedValue(
            PersistedChanges changes,
            string field)
        {
            if (changes == null) return false;
            switch (field)
            {
                case PosArticleMutationFields.Barcode:
                    return changes.Barcode != null;
                case PosArticleMutationFields.ItemNumber:
                    return changes.ItemNumber != null;
                case PosArticleMutationFields.PrimaryName:
                    return changes.PrimaryName != null;
                case PosArticleMutationFields.SecondaryName:
                    return changes.SecondaryName != null;
                case PosArticleMutationFields.CategoryId:
                    return changes.CategoryId != null;
                case PosArticleMutationFields.SupplierId:
                    return changes.SupplierId != null;
                case PosArticleMutationFields.PurchasePrice:
                    return changes.PurchasePrice.HasValue;
                case PosArticleMutationFields.RetailPrice:
                    return changes.RetailPrice.HasValue;
                case PosArticleMutationFields.StockQuantity:
                    return changes.StockQuantity.HasValue;
                default:
                    return false;
            }
        }

        private static decimal RequiredNumber(decimal? value, string field)
        {
            if (!value.HasValue)
                throw new InvalidDataException(
                    "Persisted article mutation is missing " + field + ".");
            return value.Value;
        }

        private static async Task CompleteAttemptAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string mutationId,
            string attemptToken,
            string outcome,
            DateTimeOffset now)
        {
            var rows = await connection.ExecuteAsync(@"
UPDATE article_mutation_attempts
SET completed_at = @completedAt,
    outcome = @outcome
WHERE mutation_id = @mutationId
  AND attempt_token = @attemptToken
  AND completed_at IS NULL;",
                new
                {
                    mutationId,
                    attemptToken,
                    completedAt = FormatTimestamp(now),
                    outcome = NormalizeCode(outcome)
                },
                transaction).ConfigureAwait(false);
            if (rows != 1)
                throw new InvalidDataException(
                    "Article mutation attempt ledger rejected completion.");
        }

        private static long ComputeBackoffMilliseconds(
            int attemptCount,
            string mutationId)
        {
            var exponent = Math.Max(0, Math.Min(8, attemptCount - 1));
            var baseDelay = Math.Min(300000L, 2000L << exponent);
            var seed = 17;
            foreach (var character in mutationId ?? string.Empty)
                seed = unchecked((seed * 31) + character);
            var jitter = Math.Abs((long)seed) % Math.Max(1L, baseDelay / 4L);
            return baseDelay + jitter;
        }

        private static string WriteStringArray(IReadOnlyList<string> values)
        {
            using (var stream = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(string[]));
                serializer.WriteObject(
                    stream,
                    (values ?? Array.Empty<string>()).ToArray());
                return new UTF8Encoding(false, true).GetString(stream.ToArray());
            }
        }

        private static string NormalizeCode(string value)
        {
            var source = (value ?? string.Empty).Trim();
            if (source.Length == 0) return null;
            var builder = new StringBuilder(Math.Min(64, source.Length));
            foreach (var character in source)
            {
                if (builder.Length == 64) break;
                builder.Append(
                    char.IsLetterOrDigit(character) ||
                    character == '_' ||
                    character == '-' ||
                    character == '.'
                        ? character
                        : '_');
            }
            return builder.Length == 0 ? null : builder.ToString();
        }

        private static string FormatTimestamp(DateTimeOffset value)
        {
            return value.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture);
        }

        private sealed class ProductMutationIdentityRow
        {
            public long Id { get; set; }
            public string ClientProductId { get; set; }
            public string RemoteProductId { get; set; }
            public string RemoteBaseRevision { get; set; }
        }

        private sealed class ArticleMutationOutboxRow
        {
            public long Id { get; set; }
            public long LocalProductId { get; set; }
            public string MutationId { get; set; }
            public string IdempotencyKey { get; set; }
            public string ClientProductId { get; set; }
            public string RemoteProductId { get; set; }
            public string MutationKind { get; set; }
            public long LocalSequence { get; set; }
            public string BaseRevision { get; set; }
            public string FieldMaskJson { get; set; }
            public string IntentJson { get; set; }
            public string IntentHash { get; set; }
            public string CanonicalPayloadJson { get; set; }
            public string PayloadHash { get; set; }
            public string State { get; set; }
            public int AttemptCount { get; set; }
            public string ClaimGenerationId { get; set; }
            public string ClaimToken { get; set; }
            public string LastTypedCode { get; set; }
            public long? LocalPriceHistoryId { get; set; }
        }

        private sealed class AttemptTokenRow
        {
            public string MutationId { get; set; }
            public string AttemptToken { get; set; }
        }

        private sealed class PriceHistoryBindingRow
        {
            public long Id { get; set; }
            public string Timestamp { get; set; }
            public string Type { get; set; }
            public int NewPrice { get; set; }
            public string Source { get; set; }
            public string RemotePriceId { get; set; }
            public string ArticleMutationId { get; set; }
        }

        private sealed class StateCountRow
        {
            public string State { get; set; }
            public long Count { get; set; }
        }

        private sealed class DependencyTargetRow
        {
            public long LocalProductId { get; set; }
            public string ClientProductId { get; set; }
        }

        [DataContract]
        private sealed class PersistedCanonicalIntent
        {
            [DataMember(Name = "baseRevision")]
            public string BaseRevision { get; set; }

            [DataMember(Name = "changes")]
            public PersistedChanges Changes { get; set; }

            [DataMember(Name = "clientProductId")]
            public string ClientProductId { get; set; }

            [DataMember(Name = "createdAt")]
            public string CreatedAt { get; set; }

            [DataMember(Name = "fieldMask")]
            public string[] FieldMask { get; set; }

            [DataMember(Name = "idempotencyKey")]
            public string IdempotencyKey { get; set; }

            [DataMember(Name = "localSequence")]
            public long LocalSequence { get; set; }

            [DataMember(Name = "mutationId")]
            public string MutationId { get; set; }

            [DataMember(Name = "mutationKind")]
            public string MutationKind { get; set; }

            [DataMember(Name = "occurredAt")]
            public string OccurredAt { get; set; }

            [DataMember(Name = "remoteProductId")]
            public string RemoteProductId { get; set; }
        }

        [DataContract]
        private sealed class PersistedChanges
        {
            [DataMember(Name = "barcode", EmitDefaultValue = false)]
            public string Barcode { get; set; }

            [DataMember(Name = "itemNumber", EmitDefaultValue = false)]
            public string ItemNumber { get; set; }

            [DataMember(Name = "primaryName", EmitDefaultValue = false)]
            public string PrimaryName { get; set; }

            [DataMember(Name = "secondaryName", EmitDefaultValue = false)]
            public string SecondaryName { get; set; }

            [DataMember(Name = "categoryId", EmitDefaultValue = false)]
            public string CategoryId { get; set; }

            [DataMember(Name = "supplierId", EmitDefaultValue = false)]
            public string SupplierId { get; set; }

            [DataMember(Name = "purchasePrice", EmitDefaultValue = false)]
            public decimal? PurchasePrice { get; set; }

            [DataMember(Name = "retailPrice", EmitDefaultValue = false)]
            public decimal? RetailPrice { get; set; }

            [DataMember(Name = "stockQuantity", EmitDefaultValue = false)]
            public decimal? StockQuantity { get; set; }

            [DataMember(Name = "price", EmitDefaultValue = false)]
            public decimal? Price { get; set; }

            [DataMember(Name = "quantityDelta", EmitDefaultValue = false)]
            public decimal? QuantityDelta { get; set; }

            [DataMember(Name = "reason", EmitDefaultValue = false)]
            public string Reason { get; set; }
        }
    }
}
