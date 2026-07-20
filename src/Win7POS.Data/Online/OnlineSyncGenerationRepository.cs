using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public sealed class OnlineSyncGenerationPredecessorState
    {
        public OnlineSyncGenerationPredecessorState(
            bool exists,
            string fingerprint,
            bool active)
        {
            Exists = exists;
            Fingerprint = exists
                ? (fingerprint ?? string.Empty).Trim()
                : string.Empty;
            Active = exists && active;
            if (Exists && Fingerprint.Length == 0)
            {
                throw new ArgumentException(
                    "An existing sync generation requires a fingerprint.",
                    nameof(fingerprint));
            }
        }

        public bool Active { get; }
        public bool Exists { get; }
        public string Fingerprint { get; }

        public static OnlineSyncGenerationPredecessorState None =>
            new OnlineSyncGenerationPredecessorState(false, null, false);
    }

    public sealed class OnlineSyncAttemptFence
    {
        public OnlineSyncAttemptFence(
            OnlineSyncGeneration generation,
            string claimToken,
            int expectedAttemptCount)
        {
            Generation = generation ?? throw new ArgumentNullException(nameof(generation));
            ClaimToken = (claimToken ?? string.Empty).Trim();
            if (ClaimToken.Length == 0 || ClaimToken.Length > 64)
                throw new ArgumentException("A bounded claim token is required.", nameof(claimToken));
            if (expectedAttemptCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(expectedAttemptCount));
            ExpectedAttemptCount = expectedAttemptCount;
        }

        public string ClaimToken { get; }
        public int ExpectedAttemptCount { get; }
        public OnlineSyncGeneration Generation { get; }

        public static string CreateClaimToken()
        {
            return Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>
    /// Durable point of linearization for relink, auth-stop, and generation-scoped
    /// SQLite commits. All mutation methods that consume remote responses also
    /// verify this singleton inside their own transaction.
    /// </summary>
    public sealed class OnlineSyncGenerationRepository
    {
        public const string TableName = "pos_sync_session_generation";
        private static readonly SemaphoreSlim ActivationGate = new SemaphoreSlim(1, 1);

        private readonly SqliteConnectionFactory _factory;

        public OnlineSyncGenerationRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task ActivateAndRecoverAsync(
            OnlineSyncGeneration generation,
            long activatedAt,
            OnlineSyncGenerationPredecessorState expectedCurrentState = null)
        {
            if (generation == null) throw new ArgumentNullException(nameof(generation));
            if (activatedAt < 0) throw new ArgumentOutOfRangeException(nameof(activatedAt));

            await ActivationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var connection = await _factory.OpenAsync().ConfigureAwait(false))
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var current = await connection.QuerySingleOrDefaultAsync<GenerationState>(@"
SELECT
  generation_id AS GenerationId,
  fingerprint AS Fingerprint,
  active AS Active,
  activated_at AS ActivatedAt
FROM pos_sync_session_generation
WHERE singleton_id = 1;",
                        transaction: transaction).ConfigureAwait(false);

                    if (expectedCurrentState != null &&
                        ((current == null && expectedCurrentState.Exists) ||
                         (current != null &&
                          (!expectedCurrentState.Exists ||
                           !string.Equals(
                               current.Fingerprint,
                               expectedCurrentState.Fingerprint,
                               StringComparison.Ordinal) ||
                           (current.Active == 1) != expectedCurrentState.Active))))
                    {
                        throw new InvalidOperationException(
                            "The sync generation predecessor changed before activation.");
                    }

                    var sameActiveGeneration = current != null &&
                        string.Equals(
                            current.GenerationId,
                            generation.GenerationId,
                            StringComparison.Ordinal);
                    if (sameActiveGeneration)
                    {
                        if (current.Active != 1 ||
                            !string.Equals(
                                current.Fingerprint,
                                generation.Fingerprint,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                "A stopped or mismatched sync generation cannot be reactivated.");
                        }
                    }
                    else
                    {
                        if (current != null)
                        {
                            // An authenticated relink after a process restart must
                            // survive a local wall-clock rollback. Preserve ordering
                            // with the durable singleton itself instead of trusting
                            // only the process-local timestamp source.
                            activatedAt = current.ActivatedAt == long.MaxValue
                                ? long.MaxValue
                                : Math.Max(activatedAt, current.ActivatedAt + 1);
                        }

                        await connection.ExecuteAsync(@"
INSERT INTO pos_sync_session_generation(
  singleton_id,
  generation_id,
  fingerprint,
  pos_session_id,
  shop_device_id,
  shop_id,
  shop_code,
  active,
  auth_stop_reason,
  activated_at,
  stopped_at)
VALUES(
  1,
  @GenerationId,
  @Fingerprint,
  @PosSessionId,
  @ShopDeviceId,
  @ShopId,
  @ShopCode,
  1,
  NULL,
  @activatedAt,
  NULL)
ON CONFLICT(singleton_id) DO UPDATE SET
  generation_id = excluded.generation_id,
  fingerprint = excluded.fingerprint,
  pos_session_id = excluded.pos_session_id,
  shop_device_id = excluded.shop_device_id,
  shop_id = excluded.shop_id,
  shop_code = excluded.shop_code,
  active = 1,
  auth_stop_reason = NULL,
  activated_at = excluded.activated_at,
  stopped_at = NULL;",
                            new
                            {
                                generation.GenerationId,
                                generation.Fingerprint,
                                generation.PosSessionId,
                                generation.ShopDeviceId,
                                generation.ShopId,
                                generation.ShopCode,
                                activatedAt
                            },
                            transaction).ConfigureAwait(false);
                    }

                    if (!sameActiveGeneration)
                    {
                        await ReleaseAllClaimsAsync(
                            connection,
                            transaction,
                            activatedAt,
                            "session_generation_changed").ConfigureAwait(false);
                    }
                    transaction.Commit();
                }
            }
            finally
            {
                ActivationGate.Release();
            }
        }

        public async Task<OnlineSyncGenerationPredecessorState>
            ReadCurrentPredecessorAsync()
        {
            using (var connection = await _factory.OpenAsync().ConfigureAwait(false))
            {
                var current = await connection.QuerySingleOrDefaultAsync<GenerationState>(@"
SELECT
  generation_id AS GenerationId,
  fingerprint AS Fingerprint,
  active AS Active,
  activated_at AS ActivatedAt
FROM pos_sync_session_generation
WHERE singleton_id = 1;").ConfigureAwait(false);
                return current == null
                    ? OnlineSyncGenerationPredecessorState.None
                    : new OnlineSyncGenerationPredecessorState(
                        true,
                        current.Fingerprint,
                        current.Active == 1);
            }
        }

        /// <summary>
        /// Resumes only the exact active generation. A missing singleton is the
        /// one-time upgrade case from schemas predating generation fencing; an
        /// inactive or mismatched row (including a restore tombstone) is denied.
        /// </summary>
        public async Task<bool> AttachOrInitializeCurrentAsync(
            OnlineSyncGeneration generation,
            long initializedAt)
        {
            if (generation == null) throw new ArgumentNullException(nameof(generation));
            if (initializedAt < 0) throw new ArgumentOutOfRangeException(nameof(initializedAt));

            await ActivationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var connection = await _factory.OpenAsync().ConfigureAwait(false))
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var current = await connection.QuerySingleOrDefaultAsync<GenerationState>(@"
SELECT
  generation_id AS GenerationId,
  fingerprint AS Fingerprint,
  active AS Active,
  activated_at AS ActivatedAt
FROM pos_sync_session_generation
WHERE singleton_id = 1;",
                        transaction: transaction).ConfigureAwait(false);
                    if (current != null)
                    {
                        var matches = current.Active == 1 &&
                            string.Equals(
                                current.GenerationId,
                                generation.GenerationId,
                                StringComparison.Ordinal) &&
                            string.Equals(
                                current.Fingerprint,
                                generation.Fingerprint,
                                StringComparison.Ordinal);
                        transaction.Rollback();
                        return matches;
                    }

                    await connection.ExecuteAsync(@"
INSERT INTO pos_sync_session_generation(
  singleton_id,
  generation_id,
  fingerprint,
  pos_session_id,
  shop_device_id,
  shop_id,
  shop_code,
  active,
  auth_stop_reason,
  activated_at,
  stopped_at)
VALUES(
  1,
  @GenerationId,
  @Fingerprint,
  @PosSessionId,
  @ShopDeviceId,
  @ShopId,
  @ShopCode,
  1,
  NULL,
  @initializedAt,
  NULL);",
                        new
                        {
                            generation.GenerationId,
                            generation.Fingerprint,
                            generation.PosSessionId,
                            generation.ShopDeviceId,
                            generation.ShopId,
                            generation.ShopCode,
                            initializedAt
                        },
                        transaction).ConfigureAwait(false);
                    await ReleaseAllClaimsAsync(
                        connection,
                        transaction,
                        initializedAt,
                        "session_generation_initialized").ConfigureAwait(false);
                    transaction.Commit();
                    return true;
                }
            }
            finally
            {
                ActivationGate.Release();
            }
        }

        public async Task<bool> StopIfCurrentAsync(
            OnlineSyncGeneration generation,
            string reason,
            long stoppedAt)
        {
            if (generation == null) throw new ArgumentNullException(nameof(generation));
            if (stoppedAt < 0) throw new ArgumentOutOfRangeException(nameof(stoppedAt));

            using (var connection = await _factory.OpenAsync().ConfigureAwait(false))
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                var rows = await connection.ExecuteAsync(@"
UPDATE pos_sync_session_generation
SET active = 0,
    auth_stop_reason = @reason,
    stopped_at = @stoppedAt
WHERE singleton_id = 1
  AND active = 1
  AND generation_id = @GenerationId
  AND fingerprint = @Fingerprint;",
                    new
                    {
                        generation.GenerationId,
                        generation.Fingerprint,
                        reason = NormalizeReason(reason),
                        stoppedAt
                    },
                    transaction).ConfigureAwait(false);
                if (rows != 1)
                {
                    transaction.Rollback();
                    return false;
                }

                await ReleaseGenerationClaimsAsync(
                    connection,
                    transaction,
                    generation.GenerationId,
                    stoppedAt,
                    "auth_denied").ConfigureAwait(false);
                transaction.Commit();
                return true;
            }
        }

        public async Task<bool> StopIfCurrentPredecessorAsync(
            OnlineSyncGenerationPredecessorState expectedCurrentState,
            string reason,
            long stoppedAt)
        {
            if (expectedCurrentState == null)
                throw new ArgumentNullException(nameof(expectedCurrentState));
            if (stoppedAt < 0) throw new ArgumentOutOfRangeException(nameof(stoppedAt));
            if (!expectedCurrentState.Exists || !expectedCurrentState.Active)
                return false;

            using (var connection = await _factory.OpenAsync().ConfigureAwait(false))
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                var generationId = await connection.QuerySingleOrDefaultAsync<string>(@"
SELECT generation_id
FROM pos_sync_session_generation
WHERE singleton_id = 1
  AND active = 1
  AND fingerprint = @Fingerprint;",
                    new { expectedCurrentState.Fingerprint },
                    transaction).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(generationId))
                {
                    transaction.Rollback();
                    return false;
                }

                var rows = await connection.ExecuteAsync(@"
UPDATE pos_sync_session_generation
SET active = 0,
    auth_stop_reason = @reason,
    stopped_at = @stoppedAt
WHERE singleton_id = 1
  AND active = 1
  AND fingerprint = @Fingerprint;",
                    new
                    {
                        expectedCurrentState.Fingerprint,
                        reason = NormalizeReason(reason),
                        stoppedAt
                    },
                    transaction).ConfigureAwait(false);
                if (rows != 1)
                {
                    transaction.Rollback();
                    return false;
                }

                await ReleaseGenerationClaimsAsync(
                    connection,
                    transaction,
                    generationId,
                    stoppedAt,
                    "auth_denied").ConfigureAwait(false);
                transaction.Commit();
                return true;
            }
        }

        /// <summary>
        /// Administrative restore boundary. The caller must already have stopped
        /// the process supervisor and must own the exclusive restore/catalog
        /// barriers. The restored database may contain a foreign active marker, so
        /// it is removed before restore-review mutations are allowed.
        /// </summary>
        public async Task ResetForRestoreAsync(
            OnlineSyncGeneration invalidatedGeneration,
            string shopId,
            string shopCode,
            long resetAt)
        {
            if (resetAt < 0) throw new ArgumentOutOfRangeException(nameof(resetAt));
            var restoreBoundary = invalidatedGeneration ??
                new OnlineSyncGeneration(
                    "restore-" + Guid.NewGuid().ToString("N"),
                    "restore-boundary",
                    "restore-boundary",
                    shopId,
                    shopCode);
            await ActivationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var connection = await _factory.OpenAsync().ConfigureAwait(false))
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    await ReleaseAllClaimsAsync(
                        connection,
                        transaction,
                        resetAt,
                        "database_restored").ConfigureAwait(false);
                    await connection.ExecuteAsync(@"
INSERT INTO pos_sync_session_generation(
  singleton_id,
  generation_id,
  fingerprint,
  pos_session_id,
  shop_device_id,
  shop_id,
  shop_code,
  active,
  auth_stop_reason,
  activated_at,
  stopped_at)
VALUES(
  1,
  @GenerationId,
  @Fingerprint,
  @PosSessionId,
  @ShopDeviceId,
  @ShopId,
  @ShopCode,
  0,
  'database_restored',
  @resetAt,
  @resetAt)
ON CONFLICT(singleton_id) DO UPDATE SET
  generation_id = excluded.generation_id,
  fingerprint = excluded.fingerprint,
  pos_session_id = excluded.pos_session_id,
  shop_device_id = excluded.shop_device_id,
  shop_id = excluded.shop_id,
  shop_code = excluded.shop_code,
  active = 0,
  auth_stop_reason = 'database_restored',
  activated_at = excluded.activated_at,
  stopped_at = excluded.stopped_at;",
                        new
                        {
                            restoreBoundary.GenerationId,
                            restoreBoundary.Fingerprint,
                            restoreBoundary.PosSessionId,
                            restoreBoundary.ShopDeviceId,
                            restoreBoundary.ShopId,
                            restoreBoundary.ShopCode,
                            resetAt
                        },
                        transaction).ConfigureAwait(false);
                    transaction.Commit();
                }
            }
            finally
            {
                ActivationGate.Release();
            }
        }

        public async Task<bool> IsCurrentAndActiveAsync(OnlineSyncGeneration generation)
        {
            if (generation == null) return false;
            using (var connection = _factory.Open())
            {
                return await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM pos_sync_session_generation
WHERE singleton_id = 1
  AND active = 1
  AND generation_id = @GenerationId
  AND fingerprint = @Fingerprint;",
                    new
                    {
                        generation.GenerationId,
                        generation.Fingerprint
                    }).ConfigureAwait(false) == 1;
            }
        }

        public static async Task<bool> IsCurrentAndActiveAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            OnlineSyncGeneration generation)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (generation == null) return false;
            return await IsCurrentAndActiveAsync(
                connection,
                transaction,
                generation.GenerationId,
                generation.Fingerprint).ConfigureAwait(false);
        }

        public static async Task<bool> IsCurrentAndActiveAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string generationId,
            string fingerprint)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (string.IsNullOrWhiteSpace(generationId) ||
                string.IsNullOrWhiteSpace(fingerprint))
            {
                return false;
            }
            return await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM pos_sync_session_generation
WHERE singleton_id = 1
  AND active = 1
  AND generation_id = @GenerationId
  AND fingerprint = @Fingerprint;",
                new
                {
                    GenerationId = generationId.Trim(),
                    Fingerprint = fingerprint.Trim()
                },
                transaction).ConfigureAwait(false) == 1;
        }

        private static async Task ReleaseAllClaimsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long nowMs,
            string code)
        {
            await connection.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET status = 'retry',
    attempt_count = CASE WHEN attempt_count > 0 THEN attempt_count - 1 ELSE 0 END,
    next_retry_at = 0,
    last_attempt_at = NULL,
    last_error_code = @code,
    last_error_at = @nowMs,
    claim_generation_id = NULL,
    claim_token = NULL,
    updated_at = @nowMs
WHERE status = 'in_progress';

UPDATE sales
SET sync_status = 'retry'
WHERE id IN (
  SELECT sale_id
  FROM sales_sync_outbox
  WHERE status = 'retry'
    AND last_error_code = @code
);

UPDATE catalog_import_outbox
SET status = 'retry',
    attempt_count = CASE WHEN attempt_count > 0 THEN attempt_count - 1 ELSE 0 END,
    next_retry_at = 0,
    last_attempt_at = NULL,
    last_error_code = @code,
    last_error_at = @nowMs,
    claim_generation_id = NULL,
    claim_token = NULL,
    updated_at = @nowMs
WHERE status = 'in_progress';",
                new { nowMs, code },
                transaction).ConfigureAwait(false);
        }

        private static async Task ReleaseGenerationClaimsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string generationId,
            long nowMs,
            string code)
        {
            await connection.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET status = 'retry',
    attempt_count = CASE WHEN attempt_count > 0 THEN attempt_count - 1 ELSE 0 END,
    next_retry_at = 0,
    last_attempt_at = NULL,
    last_error_code = @code,
    last_error_at = @nowMs,
    claim_generation_id = NULL,
    claim_token = NULL,
    updated_at = @nowMs
WHERE status = 'in_progress'
  AND claim_generation_id = @generationId;

UPDATE sales
SET sync_status = 'retry'
WHERE id IN (
  SELECT sale_id
  FROM sales_sync_outbox
  WHERE status = 'retry'
    AND last_error_code = @code
);

UPDATE catalog_import_outbox
SET status = 'retry',
    attempt_count = CASE WHEN attempt_count > 0 THEN attempt_count - 1 ELSE 0 END,
    next_retry_at = 0,
    last_attempt_at = NULL,
    last_error_code = @code,
    last_error_at = @nowMs,
    claim_generation_id = NULL,
    claim_token = NULL,
    updated_at = @nowMs
WHERE status = 'in_progress'
  AND claim_generation_id = @generationId;",
                new { generationId, nowMs, code },
                transaction).ConfigureAwait(false);
        }

        private static string NormalizeReason(string reason)
        {
            var value = (reason ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0 || value.Length > 96)
                return "auth_denied";
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                var allowed = (character >= 'a' && character <= 'z') ||
                    (character >= '0' && character <= '9') ||
                    character == '_' || character == '-' || character == '.';
                if (!allowed)
                    return "auth_denied";
            }
            return value;
        }

        private sealed class GenerationState
        {
            public int Active { get; set; }
            public long ActivatedAt { get; set; }
            public string Fingerprint { get; set; }
            public string GenerationId { get; set; }
        }
    }
}
