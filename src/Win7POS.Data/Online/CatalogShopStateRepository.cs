using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public sealed class CatalogShopStateRepository
    {
        public const string BoundShopCodeKey = "pos.catalog.bound_shop_code";
        public const string BoundShopIdKey = "pos.catalog.bound_shop_id";
        public const string CompletenessCodeKey = "pos.catalog.exactness.code";
        public const string CompletenessStatusKey = "pos.catalog.exactness.status";
        public const string CommittedRevisionAtKey = "pos.catalog.committed_revision_at";
        public const string CommittedRevisionKey = "pos.catalog.committed_revision";
        public const string ImportAckGenerationKey = "pos.catalog.import_ack_generation";
        public const string ImportReconciledGenerationKey = "pos.catalog.import_reconciled_generation";
        public const string DeltaChainActiveKey = "pos.catalog.delta_chain.active";
        public const string DeltaChainCatalogVersionKey = "pos.catalog.delta_chain.catalog_version";
        public const string DeltaChainCursorFingerprintsKey = "pos.catalog.delta_chain.cursor_fingerprints";
        public const string DeltaChainModeKey = "pos.catalog.delta_chain.sync_mode";
        public const string DeltaChainPrefix = "pos.catalog.delta_chain.";
        public const string DeltaChainSummaryFingerprintKey = "pos.catalog.delta_chain.summary_fingerprint";
        public const string DeltaChainSummaryPinnedKey = "pos.catalog.delta_chain.summary_pinned";
        public const int MaxDeltaChainCursorFingerprints = 256;
        public const string DeltaChainActiveInvalidCode = "catalog_delta_checkpoint_active_invalid";
        public const string DeltaChainCatalogVersionInvalidCode = "catalog_delta_checkpoint_catalog_version_invalid";
        public const string DeltaChainCursorEvidenceInvalidCode = "catalog_delta_checkpoint_cursor_evidence_invalid";
        public const string DeltaChainCursorLimitCode = "catalog_delta_checkpoint_cursor_limit_exceeded";
        public const string DeltaChainCursorMismatchCode = "catalog_delta_checkpoint_cursor_mismatch";
        public const string DeltaChainModeInvalidCode = "catalog_delta_checkpoint_mode_invalid";
        public const string DeltaChainPartialCode = "catalog_delta_checkpoint_partial";
        public const string DeltaChainSummaryInvalidCode = "catalog_delta_checkpoint_summary_invalid";
        public const string ExactnessActiveCategoriesKey = "pos.catalog.exactness.actual_active_categories";
        public const string ExactnessActiveProductsKey = "pos.catalog.exactness.actual_active_products";
        public const string ExactnessActiveSuppliersKey = "pos.catalog.exactness.actual_active_suppliers";
        public const string ExactnessCatalogVersionKey = "pos.catalog.exactness.catalog_version";
        public const string ExactnessCursorFingerprintKey = "pos.catalog.exactness.cursor_fingerprint";
        public const string ExactnessEvaluatedAtKey = "pos.catalog.exactness.evaluated_at";
        public const string ExactnessPagesKey = "pos.catalog.exactness.pages";
        public const string ExactnessPrefix = "pos.catalog.exactness.";
        public const string ExactnessShopCodeKey = "pos.catalog.exactness.shop_code";
        public const string ExactnessShopIdKey = "pos.catalog.exactness.shop_id";
        public const string ExactnessVerifiedAtKey = "pos.catalog.exactness.verified_at";
        public const string InitialCompletedAtKey = "pos.catalog.initial_completed_at";
        public const string LastSyncAtKey = "pos.catalog.last_sync_at";
        public const string LastSyncCursorKey = "pos.catalog.last_sync_cursor";
        public const string LastSyncModeKey = "pos.catalog.last_sync_mode";
        public const string ObservedRevisionAtKey = "pos.catalog.observed_revision_at";
        public const string ObservedRevisionKey = "pos.catalog.observed_revision";
        public const string RepairRequiredKey = "pos.catalog.exactness.repair_required";
        public const string TransitionEpochKey = "pos.catalog.transition_epoch";
        public const string SaleSafeAtKey = "pos.catalog.sale_safe_at";

        private readonly SqliteConnectionFactory _factory;

        public CatalogShopStateRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<string> ValidateCapturedSessionAsync(
            string capturedShopId,
            string capturedShopCode)
        {
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                var officialId = await GetAsync(conn, tx, OutboxShopBinding.OfficialShopIdKey)
                    .ConfigureAwait(false);
                var officialCode = await GetAsync(conn, tx, OutboxShopBinding.OfficialShopCodeKey)
                    .ConfigureAwait(false);
                var mismatch = OutboxShopBinding.GetMismatchCode(
                    capturedShopId,
                    capturedShopCode,
                    officialId,
                    officialCode);
                tx.Commit();
                return string.IsNullOrWhiteSpace(mismatch)
                    ? string.Empty
                    : "catalog_session_shop_changed";
            }
        }

        public async Task<CatalogShopBindingResult> EnsureAndLoadCursorAsync(
            string trustedShopId,
            string trustedShopCode)
        {
            var normalizedCode = OutboxShopBinding.NormalizeCode(trustedShopCode);
            var normalizedId = Normalize(trustedShopId);
            if (normalizedCode.Length == 0)
            {
                return CatalogShopBindingResult.Failure("catalog_shop_binding_missing");
            }

            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                var boundCode = OutboxShopBinding.NormalizeCode(await GetAsync(conn, tx, BoundShopCodeKey).ConfigureAwait(false));
                var boundId = Normalize(await GetAsync(conn, tx, BoundShopIdKey).ConfigureAwait(false));
                var hasExistingState = await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM app_settings
WHERE key IN (@CursorKey, @SaleSafeKey, @LastSyncKey, @ImportAckGenerationKey, @ImportReconciledGenerationKey)
  AND TRIM(value) <> '';",
                    new
                    {
                        CursorKey = LastSyncCursorKey,
                        SaleSafeKey = SaleSafeAtKey,
                        LastSyncKey = LastSyncAtKey,
                        ImportAckGenerationKey,
                        ImportReconciledGenerationKey
                    },
                    tx).ConfigureAwait(false) > 0;

                if (boundCode.Length == 0)
                {
                    if (hasExistingState)
                    {
                        await conn.ExecuteAsync(@"
DELETE FROM app_settings
WHERE key IN (
  @CursorKey, @SaleSafeKey, @LastSyncKey, @InitialCompletedKey, @LastSyncModeKey,
  @ObservedRevisionKey, @ObservedRevisionAtKey, @CommittedRevisionKey, @CommittedRevisionAtKey,
  @ImportAckGenerationKey, @ImportReconciledGenerationKey);",
                            new
                            {
                                CursorKey = LastSyncCursorKey,
                                InitialCompletedKey = InitialCompletedAtKey,
                                 LastSyncKey = LastSyncAtKey,
                                 LastSyncModeKey,
                                 ObservedRevisionKey,
                                 ObservedRevisionAtKey,
                                 CommittedRevisionKey,
                                 CommittedRevisionAtKey,
                                 ImportAckGenerationKey,
                                 ImportReconciledGenerationKey,
                                 SaleSafeKey = SaleSafeAtKey
                            },
                            tx).ConfigureAwait(false);
                    }

                    // Exactness evidence is shop-bound. A transition deliberately removes
                    // the catalog binding, so stale diagnostics must not follow the next shop.
                    await ClearExactnessAsync(conn, tx).ConfigureAwait(false);
                    await ClearDeltaChainAsync(conn, tx).ConfigureAwait(false);

                    await SetAsync(conn, tx, BoundShopCodeKey, normalizedCode).ConfigureAwait(false);
                    await SetAsync(conn, tx, BoundShopIdKey, normalizedId).ConfigureAwait(false);
                    boundCode = normalizedCode;
                    boundId = normalizedId;
                }
                else if (!string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                    boundId,
                    boundCode,
                    normalizedId,
                    normalizedCode)))
                {
                    tx.Rollback();
                    return CatalogShopBindingResult.Failure("catalog_shop_binding_mismatch");
                }

                if (boundId.Length == 0 && normalizedId.Length > 0)
                {
                    await SetAsync(conn, tx, BoundShopIdKey, normalizedId).ConfigureAwait(false);
                }

                var epoch = await LoadEpochAsync(conn, tx).ConfigureAwait(false);
                var cursor = await GetAsync(conn, tx, LastSyncCursorKey).ConfigureAwait(false);
                var mode = await GetAsync(conn, tx, LastSyncModeKey).ConfigureAwait(false);
                tx.Commit();
                return CatalogShopBindingResult.Success(cursor, epoch, mode);
            }
        }

        public async Task StoreLastSyncAsync(
            string trustedShopId,
            string trustedShopCode,
            string syncCursor,
            string generatedAt,
            long expectedEpoch = -1,
            string syncMode = null,
            string expectedPreviousCursor = null,
            string expectedPreviousMode = null)
        {
            var value = string.IsNullOrWhiteSpace(generatedAt)
                ? DateTimeOffset.UtcNow.ToString("O")
                : generatedAt.Trim();
            var cursor = string.IsNullOrWhiteSpace(syncCursor) ? value : syncCursor.Trim();

            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await RequireCommitStateAsync(
                    conn,
                    tx,
                    trustedShopId,
                    trustedShopCode,
                    expectedEpoch,
                    expectedPreviousCursor,
                    expectedPreviousMode).ConfigureAwait(false);
                await SetAsync(conn, tx, LastSyncAtKey, value).ConfigureAwait(false);
                await SetAsync(conn, tx, LastSyncCursorKey, cursor).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(syncMode))
                {
                    await SetAsync(conn, tx, LastSyncModeKey, syncMode.Trim()).ConfigureAwait(false);
                }
                tx.Commit();
            }
        }

        public async Task<bool> StorePullCursorAsync(
            string trustedShopId,
            string trustedShopCode,
            string syncCursor,
            string generatedAt,
            long expectedEpoch,
            string syncMode,
            bool authoritativeSnapshotCommitted,
            CatalogDeltaChainCheckpoint deltaCheckpoint = null,
            string expectedPreviousCursor = null,
            string expectedPreviousMode = null)
        {
            var normalizedSyncMode = (syncMode ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedSyncMode != "delta" && normalizedSyncMode != "full_refresh")
            {
                throw new InvalidOperationException("Catalog sync mode is not supported.");
            }

            if (normalizedSyncMode == "full_refresh" &&
                !authoritativeSnapshotCommitted)
            {
                return false;
            }

            if (normalizedSyncMode == "full_refresh")
            {
                var value = string.IsNullOrWhiteSpace(generatedAt)
                    ? DateTimeOffset.UtcNow.ToString("O")
                    : generatedAt.Trim();
                var cursor = string.IsNullOrWhiteSpace(syncCursor) ? value : syncCursor.Trim();
                using (var conn = _factory.Open())
                using (var tx = conn.BeginTransaction())
                {
                    await RequireCommitStateAsync(
                        conn,
                        tx,
                        trustedShopId,
                        trustedShopCode,
                        expectedEpoch,
                        expectedPreviousCursor,
                        expectedPreviousMode).ConfigureAwait(false);
                    await RequireExactnessSaleSafetyAsync(
                        conn,
                        tx,
                        trustedShopId,
                        trustedShopCode,
                        requireEvidence: true).ConfigureAwait(false);
                    await SetAsync(conn, tx, LastSyncAtKey, value).ConfigureAwait(false);
                    await SetAsync(conn, tx, LastSyncCursorKey, cursor).ConfigureAwait(false);
                    await SetAsync(conn, tx, LastSyncModeKey, normalizedSyncMode).ConfigureAwait(false);
                    await ClearDeltaChainAsync(conn, tx).ConfigureAwait(false);
                    tx.Commit();
                    return true;
                }
            }

            var deltaValue = string.IsNullOrWhiteSpace(generatedAt)
                ? DateTimeOffset.UtcNow.ToString("O")
                : generatedAt.Trim();
            var deltaCursor = string.IsNullOrWhiteSpace(syncCursor) ? deltaValue : syncCursor.Trim();
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await RequireCommitStateAsync(
                    conn,
                    tx,
                    trustedShopId,
                    trustedShopCode,
                    expectedEpoch,
                    expectedPreviousCursor,
                    expectedPreviousMode).ConfigureAwait(false);
                await SetAsync(conn, tx, LastSyncAtKey, deltaValue).ConfigureAwait(false);
                await SetAsync(conn, tx, LastSyncCursorKey, deltaCursor).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(syncMode))
                {
                    await SetAsync(conn, tx, LastSyncModeKey, normalizedSyncMode).ConfigureAwait(false);
                }

                if (deltaCheckpoint != null)
                {
                    if (deltaCheckpoint.HasMore)
                    {
                        await StoreDeltaChainAsync(conn, tx, deltaCheckpoint, deltaCursor)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await ClearDeltaChainAsync(conn, tx).ConfigureAwait(false);
                    }
                }

                tx.Commit();
            }
            return true;
        }

        public async Task ValidateCommitStateAsync(
            string trustedShopId,
            string trustedShopCode,
            long expectedEpoch,
            string expectedPreviousCursor,
            string expectedPreviousMode)
        {
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await RequireCommitStateAsync(
                    conn,
                    tx,
                    trustedShopId,
                    trustedShopCode,
                    expectedEpoch,
                    expectedPreviousCursor ?? string.Empty,
                    expectedPreviousMode ?? string.Empty).ConfigureAwait(false);
                tx.Commit();
            }
        }

        public async Task ValidateBindingEpochAsync(
            string trustedShopId,
            string trustedShopCode,
            long expectedEpoch)
        {
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await RequireBindingAsync(
                    conn,
                    tx,
                    trustedShopId,
                    trustedShopCode,
                    expectedEpoch).ConfigureAwait(false);
                tx.Commit();
            }
        }

        public async Task<CatalogDeltaChainState> LoadDeltaChainAsync(
            string trustedShopId,
            string trustedShopCode,
            long expectedEpoch)
        {
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await RequireBindingAsync(
                    conn,
                    tx,
                    trustedShopId,
                    trustedShopCode,
                    expectedEpoch).ConfigureAwait(false);
                var rawActive = await GetAsync(conn, tx, DeltaChainActiveKey).ConfigureAwait(false);
                var rawCatalogVersion = await GetAsync(conn, tx, DeltaChainCatalogVersionKey).ConfigureAwait(false);
                var rawCursorFingerprints = await GetAsync(conn, tx, DeltaChainCursorFingerprintsKey).ConfigureAwait(false);
                var rawMode = await GetAsync(conn, tx, DeltaChainModeKey).ConfigureAwait(false);
                var rawSummaryFingerprint = await GetAsync(conn, tx, DeltaChainSummaryFingerprintKey).ConfigureAwait(false);
                var rawSummaryPinned = await GetAsync(conn, tx, DeltaChainSummaryPinnedKey).ConfigureAwait(false);
                var rawValues = new[]
                {
                    rawActive,
                    rawCatalogVersion,
                    rawCursorFingerprints,
                    rawMode,
                    rawSummaryFingerprint,
                    rawSummaryPinned
                };
                var presentValues = rawValues.Count(value => value != null);
                if (presentValues == 0)
                {
                    tx.Commit();
                    return CatalogDeltaChainState.Empty();
                }

                if (presentValues != rawValues.Length)
                {
                    tx.Commit();
                    return CatalogDeltaChainState.Invalid(DeltaChainPartialCode);
                }

                if (!string.Equals(rawActive, "1", StringComparison.Ordinal))
                {
                    tx.Commit();
                    return CatalogDeltaChainState.Invalid(DeltaChainActiveInvalidCode);
                }

                if (!string.Equals(rawMode, "delta", StringComparison.Ordinal))
                {
                    tx.Commit();
                    return CatalogDeltaChainState.Invalid(DeltaChainModeInvalidCode);
                }

                if (!string.IsNullOrWhiteSpace(
                        PosOnlineCompatibilityValidator.ValidateCatalogVersion(rawCatalogVersion)) ||
                    !string.Equals(rawCatalogVersion, Normalize(rawCatalogVersion), StringComparison.Ordinal))
                {
                    tx.Commit();
                    return CatalogDeltaChainState.Invalid(DeltaChainCatalogVersionInvalidCode);
                }

                var fingerprintParts = (rawCursorFingerprints ?? string.Empty)
                    .Split(new[] { ';' }, StringSplitOptions.None)
                    .Select(value => value.Trim().ToLowerInvariant())
                    .ToArray();
                if (fingerprintParts.Length == 0 ||
                    fingerprintParts.Any(value => !IsSha256Hex(value)) ||
                    fingerprintParts.Distinct(StringComparer.Ordinal).Count() != fingerprintParts.Length)
                {
                    tx.Commit();
                    return CatalogDeltaChainState.Invalid(DeltaChainCursorEvidenceInvalidCode);
                }

                if (fingerprintParts.Length > MaxDeltaChainCursorFingerprints)
                {
                    tx.Commit();
                    return CatalogDeltaChainState.Invalid(DeltaChainCursorLimitCode);
                }

                bool summaryPinned;
                if (string.Equals(rawSummaryPinned, "1", StringComparison.Ordinal))
                {
                    summaryPinned = true;
                }
                else if (string.Equals(rawSummaryPinned, "0", StringComparison.Ordinal))
                {
                    summaryPinned = false;
                }
                else
                {
                    tx.Commit();
                    return CatalogDeltaChainState.Invalid(DeltaChainSummaryInvalidCode);
                }

                var summaryFingerprint = Normalize(rawSummaryFingerprint).ToLowerInvariant();
                if ((summaryPinned && !IsSha256Hex(summaryFingerprint)) ||
                    (!summaryPinned && summaryFingerprint.Length > 0))
                {
                    tx.Commit();
                    return CatalogDeltaChainState.Invalid(DeltaChainSummaryInvalidCode);
                }

                var state = CatalogDeltaChainState.Create(
                    rawCatalogVersion,
                    fingerprintParts,
                    rawMode,
                    summaryFingerprint,
                    summaryPinned);
                tx.Commit();
                return state;
            }
        }

        public async Task<long> LoadTransitionEpochAsync()
        {
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                var epoch = await LoadEpochAsync(conn, tx).ConfigureAwait(false);
                tx.Commit();
                return epoch;
            }
        }

        public async Task<CatalogRevisionState> LoadRevisionStateAsync(
            string trustedShopId,
            string trustedShopCode,
            long expectedEpoch = -1)
        {
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await RequireBindingAsync(
                    conn,
                    tx,
                    trustedShopId,
                    trustedShopCode,
                    expectedEpoch).ConfigureAwait(false);
                var importAckRaw = await GetAsync(conn, tx, ImportAckGenerationKey).ConfigureAwait(false);
                var importReconciledRaw = await GetAsync(conn, tx, ImportReconciledGenerationKey).ConfigureAwait(false);
                var ackParsed = TryParseNonNegativeGeneration(importAckRaw, out var importAckGeneration);
                var reconciledParsed = TryParseNonNegativeGeneration(
                    importReconciledRaw,
                    out var importReconciledGeneration);
                var ackStateValid = ackParsed &&
                    reconciledParsed &&
                    importReconciledGeneration <= importAckGeneration;
                var state = new CatalogRevisionState(
                    CatalogHeartbeatPolicy.NormalizeRevision(
                        await GetAsync(conn, tx, ObservedRevisionKey).ConfigureAwait(false)),
                    CatalogHeartbeatPolicy.NormalizeRevision(
                        await GetAsync(conn, tx, CommittedRevisionKey).ConfigureAwait(false)),
                    await GetAsync(conn, tx, ObservedRevisionAtKey).ConfigureAwait(false),
                    await GetAsync(conn, tx, CommittedRevisionAtKey).ConfigureAwait(false),
                    importAckGeneration,
                    importReconciledGeneration,
                    ackStateValid);
                tx.Commit();
                return state;
            }
        }

        public async Task StoreObservedRevisionAsync(
            string trustedShopId,
            string trustedShopCode,
            string revision,
            DateTimeOffset observedAt,
            long expectedEpoch)
        {
            var normalizedRevision = CatalogHeartbeatPolicy.NormalizeRevision(revision);
            if (normalizedRevision.Length == 0)
            {
                return;
            }

            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await RequireBindingAsync(
                    conn,
                    tx,
                    trustedShopId,
                    trustedShopCode,
                    expectedEpoch).ConfigureAwait(false);
                await SetAsync(conn, tx, ObservedRevisionKey, normalizedRevision).ConfigureAwait(false);
                await SetAsync(
                    conn,
                    tx,
                    ObservedRevisionAtKey,
                    observedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).ConfigureAwait(false);
                tx.Commit();
            }
        }

        public async Task<bool> TryConfirmCatalogUnchangedAsync(
            string trustedShopId,
            string trustedShopCode,
            long expectedEpoch,
            string expectedObservedRevision,
            string expectedCommittedRevision,
            long expectedAckGeneration,
            bool clearStaleError)
        {
            var expectedObserved = CatalogHeartbeatPolicy.NormalizeRevision(expectedObservedRevision);
            var expectedCommitted = CatalogHeartbeatPolicy.NormalizeRevision(expectedCommittedRevision);
            if (expectedObserved.Length == 0 ||
                !string.Equals(expectedObserved, expectedCommitted, StringComparison.Ordinal) ||
                expectedAckGeneration < 0)
            {
                return false;
            }

            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await RequireBindingAsync(
                    conn,
                    tx,
                    trustedShopId,
                    trustedShopCode,
                    expectedEpoch).ConfigureAwait(false);
                var observed = CatalogHeartbeatPolicy.NormalizeRevision(
                    await GetAsync(conn, tx, ObservedRevisionKey).ConfigureAwait(false));
                var committed = CatalogHeartbeatPolicy.NormalizeRevision(
                    await GetAsync(conn, tx, CommittedRevisionKey).ConfigureAwait(false));
                var ackRaw = await GetAsync(conn, tx, ImportAckGenerationKey).ConfigureAwait(false);
                var reconciledRaw = await GetAsync(conn, tx, ImportReconciledGenerationKey).ConfigureAwait(false);
                var repairRaw = await GetAsync(conn, tx, RepairRequiredKey).ConfigureAwait(false);
                var deltaCheckpointKeys = await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM app_settings
WHERE key GLOB @pattern;",
                    new { pattern = DeltaChainPrefix + "*" },
                    tx).ConfigureAwait(false);
                var lastError = Normalize(await GetAsync(conn, tx, "pos.catalog.last_error").ConfigureAwait(false));
                var safeEvaluation = await EvaluateSaleSafetyAsync(
                    conn,
                    tx,
                    allowLegacyUnbound: false).ConfigureAwait(false);

                var valid = string.Equals(observed, expectedObserved, StringComparison.Ordinal) &&
                    string.Equals(committed, expectedCommitted, StringComparison.Ordinal) &&
                    TryParseNonNegativeGeneration(ackRaw, out var ackGeneration) &&
                    TryParseNonNegativeGeneration(reconciledRaw, out var reconciledGeneration) &&
                    ackGeneration == expectedAckGeneration &&
                    reconciledGeneration == expectedAckGeneration &&
                    TryParseOptionalBinaryFlag(repairRaw, out var repairRequired) &&
                    !repairRequired &&
                    deltaCheckpointKeys == 0 &&
                    safeEvaluation.IsSaleSafe &&
                    !string.Equals(
                        lastError,
                        CatalogPaginationSafetyPolicy.AmbiguousEndCode,
                        StringComparison.Ordinal);
                if (!valid)
                {
                    tx.Commit();
                    return false;
                }

                if (clearStaleError)
                {
                    await SetAsync(conn, tx, "pos.catalog.last_error", string.Empty).ConfigureAwait(false);
                    await SetAsync(conn, tx, "pos.catalog.bootstrap_status", "completed").ConfigureAwait(false);
                    await SetAsync(conn, tx, "pos.catalog.last_has_more", "0").ConfigureAwait(false);
                }

                await SetAsync(
                    conn,
                    tx,
                    "pos.catalog.sync.last_skip_code",
                    "catalog_unchanged_at_committed_revision").ConfigureAwait(false);
                await SetAsync(
                    conn,
                    tx,
                    "pos.catalog.sync.last_checked_at",
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)).ConfigureAwait(false);
                tx.Commit();
                return true;
            }
        }

        public async Task ResetForRestoreReviewAsync(
            string trustedShopId,
            string trustedShopCode)
        {
            using (await new CatalogShopTransitionBarrier(_factory).EnterAsync().ConfigureAwait(false))
            {
                var previousEpoch = await LoadTransitionEpochAsync().ConfigureAwait(false);
                await ResetForRestoreReviewWhileBarrierHeldAsync(
                    trustedShopId,
                    trustedShopCode,
                    previousEpoch).ConfigureAwait(false);
            }
        }

        // The caller must own CatalogShopTransitionBarrier across the database swap and
        // this reset. minimumPreviousEpoch carries the live generation across that swap.
        public async Task<long> ResetForRestoreReviewWhileBarrierHeldAsync(
            string trustedShopId,
            string trustedShopCode,
            long minimumPreviousEpoch)
        {
            var binding = await EnsureAndLoadCursorAsync(trustedShopId, trustedShopCode).ConfigureAwait(false);
            if (!binding.IsValid)
            {
                throw new InvalidOperationException(binding.Code);
            }

            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await RequireBindingAsync(
                    conn,
                    tx,
                    trustedShopId,
                    trustedShopCode,
                    binding.Epoch).ConfigureAwait(false);
                var currentEpoch = await LoadEpochAsync(conn, tx).ConfigureAwait(false);
                var epochFloor = Math.Max(Math.Max(0, minimumPreviousEpoch), currentEpoch);
                if (epochFloor == long.MaxValue)
                {
                    throw new InvalidOperationException("Catalog state transition epoch is exhausted.");
                }

                var nextEpoch = epochFloor + 1;
                await SetAsync(
                    conn,
                    tx,
                    TransitionEpochKey,
                    nextEpoch.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                await conn.ExecuteAsync(@"
DELETE FROM app_settings
WHERE key IN (
  @CursorKey, @SaleSafeKey, @LastSyncKey, @InitialCompletedKey, @LastSyncModeKey,
  @ObservedRevisionKey, @ObservedRevisionAtKey, @CommittedRevisionKey, @CommittedRevisionAtKey,
  @ImportAckGenerationKey, @ImportReconciledGenerationKey);",
                    new
                    {
                        CursorKey = LastSyncCursorKey,
                        InitialCompletedKey = InitialCompletedAtKey,
                         LastSyncKey = LastSyncAtKey,
                         LastSyncModeKey,
                         ObservedRevisionKey,
                         ObservedRevisionAtKey,
                         CommittedRevisionKey,
                         CommittedRevisionAtKey,
                         ImportAckGenerationKey,
                         ImportReconciledGenerationKey,
                         SaleSafeKey = SaleSafeAtKey
                    },
                    tx).ConfigureAwait(false);
                await ClearExactnessAsync(conn, tx).ConfigureAwait(false);
                await ClearDeltaChainAsync(conn, tx).ConfigureAwait(false);
                var resetAt = DateTimeOffset.UtcNow.ToString("O");
                await SetAsync(conn, tx, CompletenessStatusKey, CatalogCompletenessStatus.Unverified.ToString()).ConfigureAwait(false);
                await SetAsync(conn, tx, CompletenessCodeKey, "catalog_restore_review_required").ConfigureAwait(false);
                await SetAsync(conn, tx, RepairRequiredKey, "1").ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessEvaluatedAtKey, resetAt).ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessVerifiedAtKey, string.Empty).ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessShopIdKey, SafeOpaque(Normalize(trustedShopId), 128)).ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessShopCodeKey, OutboxShopBinding.NormalizeCode(trustedShopCode)).ConfigureAwait(false);
                tx.Commit();
                return nextEpoch;
            }
        }

        public async Task RequestFullRepairAsync(
            string trustedShopId,
            string trustedShopCode,
            long expectedEpoch)
        {
            using (await new CatalogShopTransitionBarrier(_factory).EnterAsync().ConfigureAwait(false))
            {
                await RequestFullRepairWhileBarrierHeldAsync(
                    trustedShopId,
                    trustedShopCode,
                    expectedEpoch).ConfigureAwait(false);
            }
        }

        // Use only from a flow that already owns CatalogShopTransitionBarrier. The public
        // RequestFullRepairAsync wrapper is the safe entry point for all other callers.
        public async Task RequestFullRepairWhileBarrierHeldAsync(
            string trustedShopId,
            string trustedShopCode,
            long expectedEpoch)
        {
            var normalizedShopId = Normalize(trustedShopId);
            var normalizedShopCode = OutboxShopBinding.NormalizeCode(trustedShopCode);
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await RequireBindingAsync(
                    conn,
                    tx,
                    normalizedShopId,
                    normalizedShopCode,
                    expectedEpoch).ConfigureAwait(false);
                var currentEpoch = await LoadEpochAsync(conn, tx).ConfigureAwait(false);
                if (currentEpoch == long.MaxValue)
                {
                    throw new InvalidOperationException("Catalog state transition epoch is exhausted.");
                }

                await SetAsync(
                    conn,
                    tx,
                    TransitionEpochKey,
                    (currentEpoch + 1).ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                await conn.ExecuteAsync(@"
DELETE FROM app_settings
WHERE key IN (
  @CursorKey, @SaleSafeKey, @InitialCompletedKey, @LastSyncKey, @LastSyncModeKey,
  @CommittedRevisionKey, @CommittedRevisionAtKey);",
                    new
                    {
                        CursorKey = LastSyncCursorKey,
                        InitialCompletedKey = InitialCompletedAtKey,
                         LastSyncKey = LastSyncAtKey,
                         LastSyncModeKey,
                         CommittedRevisionKey,
                         CommittedRevisionAtKey,
                         SaleSafeKey = SaleSafeAtKey
                    },
                    tx).ConfigureAwait(false);
                await ClearExactnessAsync(conn, tx).ConfigureAwait(false);
                await ClearDeltaChainAsync(conn, tx).ConfigureAwait(false);
                var requestedAt = DateTimeOffset.UtcNow.ToString("O");
                await SetAsync(conn, tx, CompletenessStatusKey, CatalogCompletenessStatus.Unverified.ToString()).ConfigureAwait(false);
                await SetAsync(conn, tx, CompletenessCodeKey, "catalog_full_repair_requested").ConfigureAwait(false);
                await SetAsync(conn, tx, RepairRequiredKey, "1").ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessEvaluatedAtKey, requestedAt).ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessVerifiedAtKey, string.Empty).ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessShopIdKey, SafeOpaque(normalizedShopId, 128)).ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessShopCodeKey, normalizedShopCode).ConfigureAwait(false);
                tx.Commit();
            }
        }

        public async Task StoreExactnessAsync(
            string trustedShopId,
            string trustedShopCode,
            CatalogExactnessResult exactness,
            long expectedEpoch = -1,
            string expectedPreviousCursor = null,
            string expectedPreviousMode = null)
        {
            if (exactness == null) throw new ArgumentNullException(nameof(exactness));

            // Re-evaluate instead of trusting a mutable DTO supplied by a caller. This keeps
            // the persisted Verified state tied to the fail-closed verifier invariants.
            var canonical = CatalogExactnessVerifier.Evaluate(
                exactness.Expected,
                exactness.Audit,
                exactness.Context);
            var audit = canonical.Audit ?? new CatalogFullRefreshResult();
            var context = canonical.Context ?? new CatalogExactnessRunContext();
            var expected = canonical.Expected;
            var normalizedShopId = Normalize(trustedShopId);
            var normalizedShopCode = OutboxShopBinding.NormalizeCode(trustedShopCode);

            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await RequireCommitStateAsync(
                    conn,
                    tx,
                    normalizedShopId,
                    normalizedShopCode,
                    expectedEpoch,
                    expectedPreviousCursor,
                    expectedPreviousMode).ConfigureAwait(false);
                await ClearExactnessAsync(conn, tx).ConfigureAwait(false);

                await SetAsync(conn, tx, CompletenessStatusKey, canonical.Status.ToString()).ConfigureAwait(false);
                await SetAsync(conn, tx, CompletenessCodeKey, SafeCode(canonical.Code)).ConfigureAwait(false);
                await SetAsync(conn, tx, RepairRequiredKey, canonical.RepairRequired ? "1" : "0").ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessEvaluatedAtKey, canonical.EvaluatedAt).ConfigureAwait(false);
                await SetAsync(
                    conn,
                    tx,
                    ExactnessVerifiedAtKey,
                    canonical.Status == CatalogCompletenessStatus.Verified ? canonical.EvaluatedAt : string.Empty)
                    .ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessShopIdKey, SafeOpaque(normalizedShopId, 128)).ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessShopCodeKey, normalizedShopCode).ConfigureAwait(false);
                await SetAsync(
                    conn,
                    tx,
                    ExactnessCatalogVersionKey,
                    SafeOpaque(context.CatalogVersion, 128)).ConfigureAwait(false);
                await SetAsync(
                    conn,
                    tx,
                    ExactnessCursorFingerprintKey,
                    Fingerprint(context.SyncCursor)).ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessPrefix + "sync_mode", SafeCode(context.SyncMode)).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPagesKey, context.Pages).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "duration_ms", context.DurationMilliseconds).ConfigureAwait(false);
                await SetAsync(
                    conn,
                    tx,
                    ExactnessPrefix + "rows_per_second",
                    canonical.RowsPerSecond.ToString("0.###", CultureInfo.InvariantCulture)).ConfigureAwait(false);

                await SetLongAsync(conn, tx, ExactnessPrefix + "received_products", canonical.ProductsReceived).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "received_categories", canonical.CategoriesReceived).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "received_suppliers", canonical.SuppliersReceived).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "received_prices", canonical.PricesReceived).ConfigureAwait(false);
                await SetNullableLongAsync(conn, tx, ExactnessPrefix + "accepted_prices", context.PriceRowsAccepted).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "invalid_prices", context.InvalidPriceRows).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "duplicate_prices", context.DuplicatePriceRows).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "received_tombstones", context.TombstonesReceived).ConfigureAwait(false);

                await SetNullableLongAsync(conn, tx, ExactnessPrefix + "expected_products", expected?.Products).ConfigureAwait(false);
                await SetNullableLongAsync(conn, tx, ExactnessPrefix + "expected_active_products", expected?.ActiveProducts).ConfigureAwait(false);
                await SetNullableLongAsync(conn, tx, ExactnessPrefix + "expected_categories", expected?.Categories).ConfigureAwait(false);
                await SetNullableLongAsync(conn, tx, ExactnessPrefix + "expected_suppliers", expected?.Suppliers).ConfigureAwait(false);
                await SetNullableLongAsync(conn, tx, ExactnessPrefix + "expected_prices", expected?.Prices).ConfigureAwait(false);
                await SetAsync(
                    conn,
                    tx,
                    ExactnessPrefix + "expected_checksum_fingerprint",
                    Fingerprint(expected?.Checksum)).ConfigureAwait(false);
                await SetAsync(
                    conn,
                    tx,
                    ExactnessPrefix + "expected_checksum_algorithm",
                    SafeOpaque(expected?.ChecksumAlgorithm, 64)).ConfigureAwait(false);
                await SetAsync(
                    conn,
                    tx,
                    ExactnessPrefix + "actual_checksum_fingerprint",
                    Fingerprint(context.ActualChecksum)).ConfigureAwait(false);
                await SetAsync(
                    conn,
                    tx,
                    ExactnessPrefix + "actual_checksum_algorithm",
                    SafeOpaque(context.ActualChecksumAlgorithm, 64)).ConfigureAwait(false);

                await SetLongAsync(conn, tx, ExactnessActiveProductsKey, audit.ActiveRemoteProducts).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "actual_distinct_product_ids", audit.DistinctActiveRemoteProductIds).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessActiveCategoriesKey, audit.ActiveRemoteCategories).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "actual_distinct_category_ids", audit.DistinctActiveRemoteCategoryIds).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessActiveSuppliersKey, audit.ActiveRemoteSuppliers).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "actual_distinct_supplier_ids", audit.DistinctActiveRemoteSupplierIds).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "duplicate_product_ids", audit.DuplicateAuthoritativeProductIds + audit.DuplicateActiveRemoteProductIds).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "duplicate_category_ids", audit.DuplicateAuthoritativeCategoryIds + audit.DuplicateActiveRemoteCategoryIds).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "duplicate_supplier_ids", audit.DuplicateAuthoritativeSupplierIds + audit.DuplicateActiveRemoteSupplierIds).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "duplicate_active_barcodes", audit.DuplicateActiveBarcodes).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "invalid_product_ids", audit.InvalidAuthoritativeProductIds).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "invalid_category_ids", audit.InvalidAuthoritativeCategoryIds).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "invalid_supplier_ids", audit.InvalidAuthoritativeSupplierIds).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "products_without_meta", audit.RemoteProductsWithoutMeta).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "products_without_reference_map", audit.RemoteProductsWithoutReferenceMap).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "reference_maps_without_product", audit.ReferenceMapsWithoutProduct).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "invalid_active_products", audit.InvalidActiveRemoteProducts).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "invalid_category_reference_mappings", audit.InvalidCategoryReferenceMappings).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "invalid_supplier_reference_mappings", audit.InvalidSupplierReferenceMappings).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "orphan_category_refs", audit.OrphanCategoryReferences).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "orphan_supplier_refs", audit.OrphanSupplierReferences).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "inactive_category_refs", audit.InactiveCategoryReferences).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "inactive_supplier_refs", audit.InactiveSupplierReferences).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "pending_prices", audit.PendingRemotePrices).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "inactive_products", audit.InactiveRemoteProducts).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "inactive_categories", audit.InactiveRemoteCategories).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "inactive_suppliers", audit.InactiveRemoteSuppliers).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "non_authoritative_active_products", audit.NonAuthoritativeActiveProducts).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "non_authoritative_active_categories", audit.NonAuthoritativeActiveCategories).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "non_authoritative_active_suppliers", audit.NonAuthoritativeActiveSuppliers).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "remote_price_history_rows", audit.RemotePriceHistoryRows).ConfigureAwait(false);

                if (canonical.RepairRequired || canonical.Status == CatalogCompletenessStatus.Mismatch)
                {
                    await conn.ExecuteAsync(@"
DELETE FROM app_settings
WHERE key IN (@SaleSafeKey, @InitialCompletedKey);",
                        new
                        {
                            InitialCompletedKey = InitialCompletedAtKey,
                            SaleSafeKey = SaleSafeAtKey
                        },
                        tx).ConfigureAwait(false);
                }

                tx.Commit();
            }
        }

        public async Task<CatalogExactnessState> LoadExactnessAsync()
        {
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                var rawStatus = await GetAsync(conn, tx, CompletenessStatusKey).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(rawStatus))
                {
                    return CatalogExactnessState.Unverified("catalog_exactness_not_evaluated");
                }

                if (!Enum.TryParse(rawStatus, true, out CatalogCompletenessStatus status))
                {
                    return CatalogExactnessState.Mismatch("catalog_exactness_state_invalid");
                }

                var exactnessShopId = await GetAsync(conn, tx, ExactnessShopIdKey).ConfigureAwait(false);
                var exactnessShopCode = await GetAsync(conn, tx, ExactnessShopCodeKey).ConfigureAwait(false);
                var boundShopId = await GetAsync(conn, tx, BoundShopIdKey).ConfigureAwait(false);
                var boundShopCode = await GetAsync(conn, tx, BoundShopCodeKey).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                    exactnessShopId,
                    exactnessShopCode,
                    boundShopId,
                    boundShopCode)))
                {
                    return CatalogExactnessState.Mismatch("catalog_exactness_shop_binding_mismatch");
                }

                return new CatalogExactnessState
                {
                    ActiveCategories = ParseLong(await GetAsync(conn, tx, ExactnessActiveCategoriesKey).ConfigureAwait(false)),
                    ActiveProducts = ParseLong(await GetAsync(conn, tx, ExactnessActiveProductsKey).ConfigureAwait(false)),
                    ActiveSuppliers = ParseLong(await GetAsync(conn, tx, ExactnessActiveSuppliersKey).ConfigureAwait(false)),
                    CatalogVersion = await GetAsync(conn, tx, ExactnessCatalogVersionKey).ConfigureAwait(false) ?? string.Empty,
                    Code = await GetAsync(conn, tx, CompletenessCodeKey).ConfigureAwait(false) ?? string.Empty,
                    EvaluatedAt = await GetAsync(conn, tx, ExactnessEvaluatedAtKey).ConfigureAwait(false) ?? string.Empty,
                    RepairRequired = ParseBool(await GetAsync(conn, tx, RepairRequiredKey).ConfigureAwait(false)),
                    ShopCode = OutboxShopBinding.NormalizeCode(exactnessShopCode),
                    ShopId = Normalize(exactnessShopId),
                    Status = status,
                    VerifiedAt = await GetAsync(conn, tx, ExactnessVerifiedAtKey).ConfigureAwait(false) ?? string.Empty
                };
            }
        }

        public async Task StoreSaleSafeAsync(
            string trustedShopId,
            string trustedShopCode,
            string generatedAt,
            long expectedEpoch = -1,
            string expectedPreviousCursor = null,
            string expectedPreviousMode = null,
            string committedRevision = null,
            long? reconciledImportAckGeneration = null)
        {
            var value = string.IsNullOrWhiteSpace(generatedAt)
                ? DateTimeOffset.UtcNow.ToString("O")
                : generatedAt.Trim();

            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await RequireCommitStateAsync(
                    conn,
                    tx,
                    trustedShopId,
                    trustedShopCode,
                    expectedEpoch,
                    expectedPreviousCursor,
                    expectedPreviousMode).ConfigureAwait(false);
                await RequireExactnessSaleSafetyAsync(
                    conn,
                    tx,
                    trustedShopId,
                    trustedShopCode).ConfigureAwait(false);
                await SetAsync(conn, tx, SaleSafeAtKey, value).ConfigureAwait(false);
                var initialCompleted = await GetAsync(conn, tx, InitialCompletedAtKey).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(initialCompleted))
                {
                    await SetAsync(conn, tx, InitialCompletedAtKey, value).ConfigureAwait(false);
                }

                var normalizedRevision = CatalogHeartbeatPolicy.NormalizeRevision(committedRevision);
                if (normalizedRevision.Length > 0)
                {
                    await SetAsync(conn, tx, CommittedRevisionKey, normalizedRevision).ConfigureAwait(false);
                    await SetAsync(conn, tx, CommittedRevisionAtKey, value).ConfigureAwait(false);
                }

                if (reconciledImportAckGeneration.HasValue)
                {
                    var ackRaw = await GetAsync(conn, tx, ImportAckGenerationKey).ConfigureAwait(false);
                    var reconciledRaw = await GetAsync(conn, tx, ImportReconciledGenerationKey).ConfigureAwait(false);
                    if (!TryParseNonNegativeGeneration(ackRaw, out var currentAckGeneration) ||
                        !TryParseNonNegativeGeneration(reconciledRaw, out var currentReconciledGeneration) ||
                        currentReconciledGeneration > currentAckGeneration ||
                        reconciledImportAckGeneration.Value < 0 ||
                        reconciledImportAckGeneration.Value > currentAckGeneration)
                    {
                        throw new InvalidOperationException("catalog_import_ack_generation_invalid");
                    }

                    var nextReconciled = Math.Max(
                        currentReconciledGeneration,
                        reconciledImportAckGeneration.Value);
                    await SetAsync(
                        conn,
                        tx,
                        ImportReconciledGenerationKey,
                        nextReconciled.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                }

                tx.Commit();
            }
        }

        public async Task<bool> IsSaleSafeForOfficialShopAsync()
        {
            return (await EvaluateSaleSafetyForOfficialShopAsync().ConfigureAwait(false))
                .IsSaleSafe;
        }

        public async Task<CatalogSaleSafetyEvaluation> EvaluateSaleSafetyForOfficialShopAsync()
        {
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                var evaluation = await EvaluateSaleSafetyAsync(
                    conn,
                    tx,
                    allowLegacyUnbound: false).ConfigureAwait(false);
                tx.Commit();
                return evaluation;
            }
        }

        internal static async Task RequireSaleSafeForOrdinarySaleAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (tx == null) throw new ArgumentNullException(nameof(tx));

            var evaluation = await EvaluateSaleSafetyAsync(
                conn,
                tx,
                allowLegacyUnbound: true).ConfigureAwait(false);
            if (!evaluation.IsSaleSafe)
            {
                throw new InvalidOperationException(evaluation.ReasonCode);
            }
        }

        private static async Task<CatalogSaleSafetyEvaluation> EvaluateSaleSafetyAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            bool allowLegacyUnbound)
        {
            var rows = await conn.QueryAsync<CatalogSaleSafetySettingRow>(@"
SELECT key AS Key, value AS Value
FROM app_settings
WHERE key IN (
    @BoundShopIdKey,
    @BoundShopCodeKey,
    @OfficialShopIdKey,
    @OfficialShopCodeKey,
    @RepairRequiredKey,
    @SaleSafeAtKey,
    @CompletenessStatusKey,
    @ExactnessShopIdKey,
    @ExactnessShopCodeKey
);",
                new
                {
                    BoundShopCodeKey,
                    BoundShopIdKey,
                    CompletenessStatusKey,
                    ExactnessShopCodeKey,
                    ExactnessShopIdKey,
                    OfficialShopCodeKey = OutboxShopBinding.OfficialShopCodeKey,
                    OfficialShopIdKey = OutboxShopBinding.OfficialShopIdKey,
                    RepairRequiredKey,
                    SaleSafeAtKey
                },
                tx).ConfigureAwait(false);
            var values = rows.ToDictionary(
                row => row.Key ?? string.Empty,
                row => row.Value ?? string.Empty,
                StringComparer.Ordinal);

            string Read(string key)
            {
                return values.TryGetValue(key, out var value) ? value : string.Empty;
            }

            var boundShopId = Normalize(Read(BoundShopIdKey));
            var boundShopCode = OutboxShopBinding.NormalizeCode(Read(BoundShopCodeKey));
            var hasBoundShopId = boundShopId.Length > 0;
            var hasBoundShopCode = boundShopCode.Length > 0;

            // Databases that have never been linked retain their legacy/local sale
            // behavior. Official-catalog readiness remains false for that same state.
            if (!hasBoundShopId && !hasBoundShopCode)
            {
                return allowLegacyUnbound
                    ? CatalogSaleSafetyEvaluation.Safe(
                        isCatalogBound: false,
                        "catalog_sale_safe_legacy_unbound")
                    : CatalogSaleSafetyEvaluation.Blocked(
                        isCatalogBound: false,
                        "catalog_sale_blocked_not_bound");
            }

            if (!hasBoundShopId || !hasBoundShopCode)
            {
                return CatalogSaleSafetyEvaluation.Blocked(
                    isCatalogBound: true,
                    "catalog_sale_blocked_binding_partial");
            }

            var officialShopId = Normalize(Read(OutboxShopBinding.OfficialShopIdKey));
            var officialShopCode = OutboxShopBinding.NormalizeCode(
                Read(OutboxShopBinding.OfficialShopCodeKey));
            if (officialShopId.Length == 0 || officialShopCode.Length == 0)
            {
                return CatalogSaleSafetyEvaluation.Blocked(
                    isCatalogBound: true,
                    "catalog_sale_blocked_official_shop_partial");
            }

            if (!string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                boundShopId,
                boundShopCode,
                officialShopId,
                officialShopCode)))
            {
                return CatalogSaleSafetyEvaluation.Blocked(
                    isCatalogBound: true,
                    "catalog_sale_blocked_shop_mismatch");
            }

            var rawRepairRequired = Read(RepairRequiredKey);
            if (!TryParseOptionalBinaryFlag(rawRepairRequired, out var repairRequired))
            {
                return CatalogSaleSafetyEvaluation.Blocked(
                    isCatalogBound: true,
                    "catalog_sale_blocked_repair_state_invalid");
            }

            if (repairRequired)
            {
                return CatalogSaleSafetyEvaluation.Blocked(
                    isCatalogBound: true,
                    "catalog_sale_blocked_repair_required");
            }

            if (string.IsNullOrWhiteSpace(Read(SaleSafeAtKey)))
            {
                return CatalogSaleSafetyEvaluation.Blocked(
                    isCatalogBound: true,
                    "catalog_sale_blocked_not_sale_safe");
            }

            var rawExactnessStatus = Read(CompletenessStatusKey);
            if (string.IsNullOrWhiteSpace(rawExactnessStatus))
            {
                // Compatibility for already sale-safe bound databases created before
                // exactness evidence was introduced.
                return CatalogSaleSafetyEvaluation.Safe(
                    isCatalogBound: true,
                    "catalog_sale_safe_legacy_exactness");
            }

            if (!Enum.TryParse(rawExactnessStatus, true, out CatalogCompletenessStatus exactnessStatus) ||
                exactnessStatus == CatalogCompletenessStatus.Mismatch)
            {
                return CatalogSaleSafetyEvaluation.Blocked(
                    isCatalogBound: true,
                    "catalog_sale_blocked_exactness_mismatch");
            }

            var exactnessShopId = Normalize(Read(ExactnessShopIdKey));
            var exactnessShopCode = OutboxShopBinding.NormalizeCode(Read(ExactnessShopCodeKey));
            if (exactnessShopId.Length == 0 || exactnessShopCode.Length == 0)
            {
                return CatalogSaleSafetyEvaluation.Blocked(
                    isCatalogBound: true,
                    "catalog_sale_blocked_exactness_binding_partial");
            }

            if (!string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                exactnessShopId,
                exactnessShopCode,
                officialShopId,
                officialShopCode)))
            {
                return CatalogSaleSafetyEvaluation.Blocked(
                    isCatalogBound: true,
                    "catalog_sale_blocked_exactness_shop_mismatch");
            }

            return CatalogSaleSafetyEvaluation.Safe(
                isCatalogBound: true,
                "catalog_sale_safe");
        }

        private sealed class CatalogSaleSafetySettingRow
        {
            public string Key { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
        }

        private static Task<int> ClearExactnessAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx)
        {
            return conn.ExecuteAsync(
                "DELETE FROM app_settings WHERE key LIKE @prefix;",
                new { prefix = ExactnessPrefix + "%" },
                tx);
        }

        private static Task<int> ClearDeltaChainAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx)
        {
            return conn.ExecuteAsync(
                "DELETE FROM app_settings WHERE key LIKE @prefix;",
                new { prefix = DeltaChainPrefix + "%" },
                tx);
        }

        private static async Task StoreDeltaChainAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            CatalogDeltaChainCheckpoint checkpoint,
            string cursor)
        {
            if (!string.Equals(checkpoint.SyncMode, "delta", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Catalog delta checkpoint mode is invalid.");
            }

            if (!string.IsNullOrWhiteSpace(
                    PosOnlineCompatibilityValidator.ValidateCatalogVersion(checkpoint.CatalogVersion)))
            {
                throw new InvalidOperationException(DeltaChainCatalogVersionInvalidCode);
            }

            var catalogVersion = Normalize(checkpoint.CatalogVersion);
            var summaryFingerprint = Normalize(checkpoint.SummaryFingerprint).ToLowerInvariant();
            if ((checkpoint.SummaryPinned && !IsSha256Hex(summaryFingerprint)) ||
                (!checkpoint.SummaryPinned && summaryFingerprint.Length > 0))
            {
                throw new InvalidOperationException(DeltaChainSummaryInvalidCode);
            }

            var fingerprintValues = (checkpoint.CursorFingerprints ?? Array.Empty<string>())
                .Select(value => Normalize(value).ToLowerInvariant())
                .ToArray();
            if (fingerprintValues.Any(value => !IsSha256Hex(value)))
            {
                throw new InvalidOperationException(DeltaChainCursorEvidenceInvalidCode);
            }

            var fingerprints = fingerprintValues
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var currentFingerprint = Fingerprint(cursor);
            if (currentFingerprint.Length > 0 && !fingerprints.Contains(currentFingerprint))
            {
                fingerprints.Add(currentFingerprint);
            }

            if (fingerprints.Count == 0)
            {
                throw new InvalidOperationException("Catalog delta checkpoint cursor evidence is missing.");
            }

            if (fingerprints.Count > MaxDeltaChainCursorFingerprints)
            {
                throw new InvalidOperationException(DeltaChainCursorLimitCode);
            }

            await SetAsync(conn, tx, DeltaChainActiveKey, "1").ConfigureAwait(false);
            await SetAsync(
                conn,
                tx,
                DeltaChainCatalogVersionKey,
                catalogVersion).ConfigureAwait(false);
            await SetAsync(
                conn,
                tx,
                DeltaChainCursorFingerprintsKey,
                string.Join(";", fingerprints)).ConfigureAwait(false);
            await SetAsync(conn, tx, DeltaChainModeKey, "delta").ConfigureAwait(false);
            await SetAsync(
                conn,
                tx,
                DeltaChainSummaryFingerprintKey,
                checkpoint.SummaryPinned ? summaryFingerprint : string.Empty).ConfigureAwait(false);
            await SetAsync(
                conn,
                tx,
                DeltaChainSummaryPinnedKey,
                checkpoint.SummaryPinned ? "1" : "0").ConfigureAwait(false);
        }

        public static string FingerprintValue(string value)
        {
            return Fingerprint(value);
        }

        private static string Fingerprint(string value)
        {
            var normalized = Normalize(value);
            if (normalized.Length == 0)
            {
                return string.Empty;
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static bool IsSha256Hex(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            {
                return false;
            }

            foreach (var character in value)
            {
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f') ||
                      (character >= 'A' && character <= 'F')))
                {
                    return false;
                }
            }

            return true;
        }

        private static long ParseLong(string value)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
        }

        private static bool ParseBool(string value)
        {
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseOptionalBinaryFlag(string value, out bool parsed)
        {
            if (string.IsNullOrEmpty(value) || string.Equals(value, "0", StringComparison.Ordinal))
            {
                parsed = false;
                return true;
            }

            if (string.Equals(value, "1", StringComparison.Ordinal))
            {
                parsed = true;
                return true;
            }

            parsed = false;
            return false;
        }

        private static async Task RequireExactnessSaleSafetyAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string trustedShopId,
            string trustedShopCode,
            bool requireEvidence = false)
        {
            var rawRepairRequired = await GetAsync(conn, tx, RepairRequiredKey).ConfigureAwait(false);
            if (!TryParseOptionalBinaryFlag(rawRepairRequired, out var repairRequired))
            {
                throw new InvalidOperationException("Catalog exactness repair flag is invalid.");
            }

            var exactnessStatus = await GetAsync(conn, tx, CompletenessStatusKey).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(exactnessStatus))
            {
                if (repairRequired)
                {
                    throw new InvalidOperationException("Catalog exactness requires repair before sale-safe can be stored.");
                }

                if (requireEvidence)
                {
                    throw new InvalidOperationException("Catalog exactness evidence is required before authoritative cursor commit.");
                }

                return;
            }

            if (!Enum.TryParse(exactnessStatus, true, out CatalogCompletenessStatus status) ||
                status == CatalogCompletenessStatus.Mismatch ||
                repairRequired)
            {
                throw new InvalidOperationException("Catalog exactness requires repair before sale-safe can be stored.");
            }

            var exactnessShopId = await GetAsync(conn, tx, ExactnessShopIdKey).ConfigureAwait(false);
            var exactnessShopCode = await GetAsync(conn, tx, ExactnessShopCodeKey).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                exactnessShopId,
                exactnessShopCode,
                trustedShopId,
                trustedShopCode)))
            {
                throw new InvalidOperationException("Catalog exactness shop binding mismatch.");
            }
        }

        private static string SafeCode(string value)
        {
            var normalized = Normalize(value);
            var safe = new StringBuilder(Math.Min(normalized.Length, 80));
            foreach (var character in normalized)
            {
                if (safe.Length >= 80)
                {
                    break;
                }

                if (char.IsLetterOrDigit(character) ||
                    character == '_' ||
                    character == '-' ||
                    character == '.')
                {
                    safe.Append(character);
                }
            }

            return safe.Length == 0 ? "catalog_exactness_unknown" : safe.ToString();
        }

        private static string SafeOpaque(string value, int maximumLength)
        {
            var normalized = Normalize(value);
            var safe = new StringBuilder(Math.Min(normalized.Length, maximumLength));
            foreach (var character in normalized)
            {
                if (safe.Length >= maximumLength)
                {
                    break;
                }

                if (!char.IsControl(character))
                {
                    safe.Append(character);
                }
            }

            return safe.ToString();
        }

        private static Task<int> SetLongAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string key,
            long value)
        {
            return SetAsync(conn, tx, key, value.ToString(CultureInfo.InvariantCulture));
        }

        private static Task<int> SetNullableLongAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string key,
            long? value)
        {
            return SetAsync(
                conn,
                tx,
                key,
                value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);
        }

        private static async Task RequireBindingAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string trustedShopId,
            string trustedShopCode,
            long expectedEpoch)
        {
            var boundCode = await GetAsync(conn, tx, BoundShopCodeKey).ConfigureAwait(false);
            var boundId = await GetAsync(conn, tx, BoundShopIdKey).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                boundId,
                boundCode,
                trustedShopId,
                trustedShopCode)))
            {
                throw new InvalidOperationException("Catalog state shop binding mismatch.");
            }

            if (expectedEpoch >= 0 &&
                await LoadEpochAsync(conn, tx).ConfigureAwait(false) != expectedEpoch)
            {
                throw new InvalidOperationException("Catalog state transition epoch mismatch.");
            }
        }

        private static async Task RequireCommitStateAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string trustedShopId,
            string trustedShopCode,
            long expectedEpoch,
            string expectedPreviousCursor,
            string expectedPreviousMode)
        {
            await RequireBindingAsync(
                conn,
                tx,
                trustedShopId,
                trustedShopCode,
                expectedEpoch).ConfigureAwait(false);

            if (expectedPreviousCursor != null)
            {
                var currentCursor = Normalize(await GetAsync(conn, tx, LastSyncCursorKey).ConfigureAwait(false));
                if (!string.Equals(
                    currentCursor,
                    Normalize(expectedPreviousCursor),
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Catalog state previous cursor mismatch.");
                }
            }

            if (expectedPreviousMode != null)
            {
                var currentMode = Normalize(await GetAsync(conn, tx, LastSyncModeKey).ConfigureAwait(false));
                if (!string.Equals(
                    currentMode,
                    Normalize(expectedPreviousMode),
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Catalog state previous mode mismatch.");
                }
            }
        }

        private static async Task<long> LoadEpochAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx)
        {
            var value = await GetAsync(conn, tx, TransitionEpochKey).ConfigureAwait(false);
            if (!long.TryParse(value, out var epoch) || epoch < 0)
            {
                epoch = 0;
                await SetAsync(conn, tx, TransitionEpochKey, "0").ConfigureAwait(false);
            }

            return epoch;
        }

        private static Task<string> GetAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string key)
        {
            return conn.ExecuteScalarAsync<string>(
                "SELECT value FROM app_settings WHERE key = @key;",
                new { key },
                tx);
        }

        private static Task<int> SetAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string key,
            string value)
        {
            return conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value)
VALUES(@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new { key, value = value ?? string.Empty },
                tx);
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static bool TryParseNonNegativeGeneration(string value, out long generation)
        {
            var normalized = Normalize(value);
            if (normalized.Length == 0)
            {
                generation = 0;
                return true;
            }

            return long.TryParse(
                    normalized,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out generation) &&
                generation >= 0;
        }
    }

    public sealed class CatalogSaleSafetyEvaluation
    {
        public bool IsCatalogBound { get; private set; }
        public bool IsSaleSafe { get; private set; }
        public string ReasonCode { get; private set; } = string.Empty;

        internal static CatalogSaleSafetyEvaluation Blocked(
            bool isCatalogBound,
            string reasonCode)
        {
            return Create(false, isCatalogBound, reasonCode);
        }

        internal static CatalogSaleSafetyEvaluation Safe(
            bool isCatalogBound,
            string reasonCode)
        {
            return Create(true, isCatalogBound, reasonCode);
        }

        private static CatalogSaleSafetyEvaluation Create(
            bool isSaleSafe,
            bool isCatalogBound,
            string reasonCode)
        {
            return new CatalogSaleSafetyEvaluation
            {
                IsCatalogBound = isCatalogBound,
                IsSaleSafe = isSaleSafe,
                ReasonCode = string.IsNullOrWhiteSpace(reasonCode)
                    ? "catalog_sale_safety_unknown"
                    : reasonCode.Trim()
            };
        }
    }

    public sealed class CatalogDeltaChainCheckpoint
    {
        public string CatalogVersion { get; set; } = string.Empty;
        public IReadOnlyCollection<string> CursorFingerprints { get; set; } = Array.Empty<string>();
        public bool HasMore { get; set; }
        public string SummaryFingerprint { get; set; } = string.Empty;
        public bool SummaryPinned { get; set; }
        public string SyncMode { get; set; } = "delta";
    }

    public sealed class CatalogDeltaChainState
    {
        public string CatalogVersion { get; private set; } = string.Empty;
        public string Code { get; private set; } = string.Empty;
        public IReadOnlyCollection<string> CursorFingerprints { get; private set; } = Array.Empty<string>();
        public bool HasState { get; private set; }
        public bool IsValid { get; private set; }
        public string SummaryFingerprint { get; private set; } = string.Empty;
        public bool SummaryPinned { get; private set; }
        public string SyncMode { get; private set; } = string.Empty;

        public string GetSnapshotMismatchCode(
            string catalogVersion,
            string summaryFingerprint,
            bool summaryPresent,
            string syncMode)
        {
            if (!HasState)
            {
                return string.Empty;
            }

            if (!string.Equals(SyncMode, syncMode, StringComparison.OrdinalIgnoreCase))
            {
                return "catalog_sync_mode_changed_across_runs";
            }

            if (!string.Equals(
                CatalogVersion,
                (catalogVersion ?? string.Empty).Trim(),
                StringComparison.Ordinal))
            {
                return "catalog_version_changed_across_runs";
            }

            if (SummaryPinned && !summaryPresent)
            {
                return "catalog_summary_missing_across_runs";
            }

            if (SummaryPinned && !string.Equals(
                    SummaryFingerprint,
                    (summaryFingerprint ?? string.Empty).Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return "catalog_summary_changed_across_runs";
            }

            return string.Empty;
        }

        public static CatalogDeltaChainState Empty()
        {
            return new CatalogDeltaChainState { IsValid = true };
        }

        internal static CatalogDeltaChainState Invalid(string code)
        {
            return new CatalogDeltaChainState
            {
                Code = string.IsNullOrWhiteSpace(code)
                    ? CatalogShopStateRepository.DeltaChainPartialCode
                    : code.Trim(),
                IsValid = false
            };
        }

        internal static CatalogDeltaChainState Create(
            string catalogVersion,
            IReadOnlyCollection<string> cursorFingerprints,
            string syncMode,
            string summaryFingerprint,
            bool summaryPinned)
        {
            return new CatalogDeltaChainState
            {
                CatalogVersion = (catalogVersion ?? string.Empty).Trim(),
                CursorFingerprints = cursorFingerprints ?? Array.Empty<string>(),
                HasState = true,
                IsValid = true,
                SummaryFingerprint = (summaryFingerprint ?? string.Empty).Trim().ToLowerInvariant(),
                SummaryPinned = summaryPinned,
                SyncMode = (syncMode ?? string.Empty).Trim()
            };
        }
    }

    public sealed class CatalogShopBindingResult
    {
        public string Code { get; private set; } = string.Empty;
        public string Cursor { get; private set; } = string.Empty;
        public long Epoch { get; private set; }
        public bool IsValid { get; private set; }
        public string Mode { get; private set; } = string.Empty;

        public static CatalogShopBindingResult Failure(string code)
        {
            return new CatalogShopBindingResult { Code = code ?? string.Empty };
        }

        public static CatalogShopBindingResult Success(string cursor, long epoch, string mode)
        {
            return new CatalogShopBindingResult
            {
                Cursor = cursor ?? string.Empty,
                Epoch = epoch,
                IsValid = true,
                Mode = mode ?? string.Empty
            };
        }
    }

    public sealed class CatalogExactnessState
    {
        public long ActiveCategories { get; set; }
        public long ActiveProducts { get; set; }
        public long ActiveSuppliers { get; set; }
        public string CatalogVersion { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string EvaluatedAt { get; set; } = string.Empty;
        public bool RepairRequired { get; set; }
        public string ShopCode { get; set; } = string.Empty;
        public string ShopId { get; set; } = string.Empty;
        public CatalogCompletenessStatus Status { get; set; }
        public string VerifiedAt { get; set; } = string.Empty;

        public static CatalogExactnessState Mismatch(string code)
        {
            return new CatalogExactnessState
            {
                Code = code ?? string.Empty,
                RepairRequired = true,
                Status = CatalogCompletenessStatus.Mismatch
            };
        }

        public static CatalogExactnessState Unverified(string code)
        {
            return new CatalogExactnessState
            {
                Code = code ?? string.Empty,
                Status = CatalogCompletenessStatus.Unverified
            };
        }
    }
}
