using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Online
{
    public sealed class PosCatalogPullService
    {
        private const string LastCatalogSyncSettingKey = "pos.catalog.last_sync_at";
        private const string LastCatalogSyncCursorSettingKey = "pos.catalog.last_sync_cursor";
        private const string LastCatalogErrorSettingKey = "pos.catalog.last_error";
        private const string LastCatalogUpdatedProductsSettingKey = "pos.catalog.last_updated_products";
        private const string LastCatalogTombstonesReceivedSettingKey = "pos.catalog.last_tombstones_received";
        private const string LastCatalogTombstonesAppliedSettingKey = "pos.catalog.last_tombstones_applied";
        private const string LastCatalogHasMoreSettingKey = "pos.catalog.last_has_more";
        private const string LastCatalogVersionSettingKey = "pos.catalog.last_catalog_version";
        private const string CatalogBootstrapStatusSettingKey = "pos.catalog.bootstrap_status";
        private const string CatalogInitialCompletedAtSettingKey = "pos.catalog.initial_completed_at";
        private const string CatalogSaleSafeAtSettingKey = "pos.catalog.sale_safe_at";
        private const string BootstrapStatusCompleted = "completed";
        private const string BootstrapStatusFailedAuthDenied = "failed_auth_denied";
        private const string BootstrapStatusFailedRetryable = "failed_retryable";
        private const string BootstrapStatusInProgress = "in_progress";
        private const string BootstrapStatusNotStarted = "not_started";
        private const string BootstrapStatusPartialHasMore = "partial_has_more";
        private const string BootstrapStatusUpdating = "updating";
        private const string CatalogHasMoreNotDrainedCode = "has_more_not_drained";
        private const int MaxCatalogPullAttempts = 3;
        private const int CatalogPullPageLimit = 1000;
        private const int MaxBackgroundCatalogPullPages = 8;
        private const int LegacyFullCatalogPullPages = 120;
        private const int MaxAuthoritativeCatalogPullPages = 512;

        private readonly SqliteConnectionFactory _factory;
        private readonly FileLogger _logger;
        private readonly PosTrustedDeviceStore _store;
        private readonly OnlineSyncGeneration _diagnosticGeneration;

        public PosCatalogPullService(SqliteConnectionFactory factory)
            : this(factory, new PosTrustedDeviceStore(), new FileLogger("PosCatalogPullService"))
        {
        }

        internal PosCatalogPullService(
            SqliteConnectionFactory factory,
            PosTrustedDeviceStore store,
            FileLogger logger,
            OnlineSyncGeneration diagnosticGeneration = null)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _diagnosticGeneration = diagnosticGeneration;
        }

        public async Task<bool> TryPullCatalogAsync(
            PosAdminWebOptions options,
            CancellationToken cancellationToken)
        {
            if (!_store.TryRead(out var trustedSession))
            {
                return false;
            }

            var outcome = await TryPullCatalogWithSessionAsync(
                options,
                trustedSession,
                clearStoredStateOnDenied: true,
                maxPages: MaxBackgroundCatalogPullPages,
                bootstrapRun: false,
                cancellationToken,
                progress: null).ConfigureAwait(false);
            return outcome.Completed;
        }

        public async Task<bool> TryPullCatalogAsync(
            PosAdminWebOptions options,
            PosTrustedDeviceSession trustedSession,
            CancellationToken cancellationToken)
        {
            var outcome = await TryPullCatalogWithSessionAsync(
                options,
                trustedSession,
                clearStoredStateOnDenied: false,
                maxPages: MaxBackgroundCatalogPullPages,
                bootstrapRun: false,
                cancellationToken,
                progress: null).ConfigureAwait(false);
            return outcome.Completed;
        }

        public Task<PosCatalogPullOutcome> TryPullIncrementalCatalogAsync(
            PosAdminWebOptions options,
            PosTrustedDeviceSession trustedSession,
            CancellationToken cancellationToken,
            IProgress<PosCatalogPullProgress> progress = null)
        {
            return TryPullCatalogWithSessionAsync(
                options,
                trustedSession,
                clearStoredStateOnDenied: false,
                maxPages: MaxBackgroundCatalogPullPages,
                bootstrapRun: false,
                cancellationToken,
                progress);
        }

        public Task<PosCatalogPullOutcome> TryPullIncrementalCatalogAsync(
            PosAdminWebOptions options,
            PosTrustedDeviceSession trustedSession,
            OnlineSyncGeneration generation,
            OnlineSyncLaneExecutionContext executionContext,
            CancellationToken cancellationToken,
            IProgress<PosCatalogPullProgress> progress = null)
        {
            return new PosCatalogPullService(
                _factory,
                _store,
                _logger,
                generation).TryPullCatalogWithSessionAsync(
                options,
                trustedSession,
                clearStoredStateOnDenied: false,
                maxPages: MaxBackgroundCatalogPullPages,
                bootstrapRun: false,
                cancellationToken,
                progress,
                forceFullRepair: false,
                generation: generation,
                executionContext: executionContext);
        }

        public Task<PosCatalogPullOutcome> TryPullCatalogForSupervisorAsync(
            PosAdminWebOptions options,
            PosTrustedDeviceSession trustedSession,
            OnlineSyncGeneration generation,
            OnlineSyncLaneExecutionContext executionContext,
            bool forceFullRepair,
            bool bootstrapRun,
            CancellationToken cancellationToken,
            IProgress<PosCatalogPullProgress> progress = null)
        {
            return new PosCatalogPullService(
                _factory,
                _store,
                _logger,
                generation).TryPullCatalogWithSessionAsync(
                options,
                trustedSession,
                clearStoredStateOnDenied: false,
                maxPages: forceFullRepair || bootstrapRun
                    ? MaxAuthoritativeCatalogPullPages
                    : MaxBackgroundCatalogPullPages,
                bootstrapRun: bootstrapRun,
                cancellationToken,
                progress,
                forceFullRepair: forceFullRepair,
                generation: generation,
                executionContext: executionContext);
        }

        public async Task<PosCatalogPullOutcome> TryPullInitialCatalogAsync(
            PosAdminWebOptions options,
            CancellationToken cancellationToken,
            IProgress<PosCatalogPullProgress> progress = null)
        {
            if (!_store.TryRead(out var trustedSession))
            {
                await StoreCatalogBootstrapStatusAsync(BootstrapStatusNotStarted)
                    .ConfigureAwait(false);
                return PosCatalogPullOutcome.Failure(
                    "trusted_session_missing",
                    false,
                    false,
                    0);
            }

            return await TryPullCatalogWithSessionAsync(
                options,
                trustedSession,
                clearStoredStateOnDenied: true,
                maxPages: LegacyFullCatalogPullPages,
                bootstrapRun: true,
                cancellationToken,
                progress).ConfigureAwait(false);
        }

        public async Task<PosCatalogPullOutcome> TryRepairCatalogAsync(
            PosAdminWebOptions options,
            CancellationToken cancellationToken,
            IProgress<PosCatalogPullProgress> progress = null)
        {
            if (!_store.TryRead(out var trustedSession))
            {
                await StoreCatalogBootstrapStatusAsync(BootstrapStatusNotStarted)
                    .ConfigureAwait(false);
                return PosCatalogPullOutcome.Failure(
                    "trusted_session_missing",
                    false,
                    false,
                    0);
            }

            return await TryPullCatalogWithSessionAsync(
                options,
                trustedSession,
                clearStoredStateOnDenied: true,
                maxPages: LegacyFullCatalogPullPages,
                bootstrapRun: true,
                cancellationToken,
                progress,
                forceFullRepair: true).ConfigureAwait(false);
        }

        public static async Task<bool> IsCatalogSaleSafeAsync(SqliteConnectionFactory factory)
        {
            if (factory == null)
            {
                return false;
            }

            return await new CatalogShopStateRepository(factory)
                .IsSaleSafeForOfficialShopAsync()
                .ConfigureAwait(false);
        }

        private async Task<PosCatalogPullOutcome> TryPullCatalogWithSessionAsync(
            PosAdminWebOptions options,
            PosTrustedDeviceSession trustedSession,
            bool clearStoredStateOnDenied,
            int maxPages,
            bool bootstrapRun,
            CancellationToken cancellationToken,
            IProgress<PosCatalogPullProgress> progress,
            bool forceFullRepair = false,
            OnlineSyncGeneration generation = null,
            OnlineSyncLaneExecutionContext executionContext = null)
        {
            if (options == null ||
                trustedSession == null ||
                string.IsNullOrWhiteSpace(trustedSession.DeviceToken) ||
                string.IsNullOrWhiteSpace(trustedSession.PosSessionId) ||
                string.IsNullOrWhiteSpace(trustedSession.SessionToken) ||
                string.IsNullOrWhiteSpace(trustedSession.ShopDeviceId))
            {
                if (bootstrapRun)
                {
                    await StoreCatalogBootstrapStatusAsync(BootstrapStatusNotStarted)
                        .ConfigureAwait(false);
                }

                return PosCatalogPullOutcome.Failure("invalid_session", false, false, 0);
            }
            if (generation != null &&
                !_store.TryReadGeneration(
                    generation,
                    out trustedSession,
                    out _))
            {
                return PosCatalogPullOutcome.Failure(
                    "trusted_generation_changed",
                    false,
                    false,
                    0);
            }

            var capturedEpoch = -1L;
            try
            {
                using (await new CatalogShopTransitionBarrier(_factory)
                    .EnterAsync(cancellationToken)
                    .ConfigureAwait(false))
                {
                var catalogState = new CatalogShopStateRepository(_factory);
                var capturedSessionError = await catalogState.ValidateCapturedSessionAsync(
                    trustedSession.ShopId,
                    trustedSession.ShopCode).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(capturedSessionError))
                {
                    await StoreCatalogFailureAsync(capturedSessionError).ConfigureAwait(false);
                    await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                        .ConfigureAwait(false);
                    return PosCatalogPullOutcome.Failure(
                        capturedSessionError,
                        false,
                        false,
                        0);
                }

                var binding = await catalogState.EnsureAndLoadCursorAsync(
                    trustedSession.ShopId,
                    trustedSession.ShopCode,
                    generation).ConfigureAwait(false);
                if (!binding.IsValid)
                {
                    await StoreCatalogFailureAsync(binding.Code).ConfigureAwait(false);
                    await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                        .ConfigureAwait(false);
                    return PosCatalogPullOutcome.Failure(binding.Code, false, false, 0);
                }

                capturedEpoch = binding.Epoch;
                var pullRevisionState = await catalogState.LoadRevisionStateAsync(
                    trustedSession.ShopId,
                    trustedSession.ShopCode,
                    binding.Epoch).ConfigureAwait(false);
                if (!pullRevisionState.ImportAckStateValid)
                {
                    const string ackGenerationCode = "catalog_import_ack_generation_invalid";
                    await StoreCatalogFailureAsync(ackGenerationCode).ConfigureAwait(false);
                    await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                        .ConfigureAwait(false);
                    return PosCatalogPullOutcome.Failure(
                        ackGenerationCode,
                        false,
                        false,
                        0);
                }
                var capturedImportAckGeneration = pullRevisionState.ImportAckGeneration;

                // Full responses are staged and validated before the destructive generation
                // reset. This preserves the prior sale-safe catalog on an ambiguous terminal page.
                await StoreCatalogBootstrapStatusAsync(bootstrapRun
                        ? BootstrapStatusInProgress
                        : BootstrapStatusUpdating)
                    .ConfigureAwait(false);

                using (var client = new PosAdminWebClient(options))
                {
                    using var catalogApplyRun = new RemoteCatalogBatchRepository(_factory)
                        .CreateRunContext();
                    var syncTimer = Stopwatch.StartNew();
                    var effectiveMaxPages = maxPages;
                    var totalStats = new CatalogApplyStats();
                    PosCatalogPullResponse lastResponse = null;
                    PosOnlineResult<PosCatalogPullResponse> lastResult = null;
                    var pagesProcessed = 0;
                    var fullRefresh = false;
                    var receivedFullLanes = new CatalogPaginationLaneCounts(0, 0, 0, 0);
                    var fullLaneEvidence = new CatalogFullLaneEvidenceTracker();
                    var fullStage = new CatalogFullResponseStageRepository(_factory);
                    // This implementation does not resume response bodies across process
                    // lifetimes. Remove any abandoned scratch generation before either a
                    // delta or full pull so a crash cannot leave large non-authoritative
                    // blobs in backups indefinitely.
                    try
                    {
                        await fullStage.ClearAllAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        _logger.LogWarning(
                            "Catalog stale full stage cleanup deferred: category=catalog.pull code=catalog_full_stage_cleanup_failed");
                    }
                    var fullStageGeneration = Guid.NewGuid().ToString("N");
                    var fullStageBytes = 0L;
                    var fullStageStarted = false;
                    CatalogPageBudgetDecision firstPageBudget = null;
                    var authoritativeProductIds = new List<string>();
                    var authoritativeCategoryIds = new HashSet<string>(StringComparer.Ordinal);
                    var authoritativeSupplierIds = new HashSet<string>(StringComparer.Ordinal);
                    var authoritativePriceIds = new HashSet<string>(StringComparer.Ordinal);
                    var duplicatePriceRows = 0;
                    var persistedDeltaChain = forceFullRepair
                        ? CatalogDeltaChainState.Empty()
                        : await catalogState.LoadDeltaChainAsync(
                            trustedSession.ShopId,
                            trustedSession.ShopCode,
                            binding.Epoch).ConfigureAwait(false);
                    var persistedCursorFingerprint = CatalogShopStateRepository.FingerprintValue(
                        binding.Cursor);
                    var checkpointError = persistedDeltaChain.IsValid
                        ? string.Empty
                        : persistedDeltaChain.Code;
                    if (checkpointError.Length == 0 && persistedDeltaChain.HasState)
                    {
                        if (persistedCursorFingerprint.Length == 0 ||
                            !persistedDeltaChain.CursorFingerprints.Contains(
                                persistedCursorFingerprint,
                                StringComparer.Ordinal))
                        {
                            checkpointError = CatalogShopStateRepository.DeltaChainCursorMismatchCode;
                        }
                        else if (!string.Equals(
                            persistedDeltaChain.SyncMode,
                            "delta",
                            StringComparison.Ordinal))
                        {
                            checkpointError = CatalogShopStateRepository.DeltaChainModeInvalidCode;
                        }
                    }

                    var requiresFullBoundary = forceFullRepair || checkpointError.Length > 0;
                    if (checkpointError.Length > 0)
                    {
                        // Keep the live generation intact until a replacement full chain has
                        // been completely downloaded and validated.
                        await StoreCatalogFailureAsync(checkpointError).ConfigureAwait(false);
                        persistedDeltaChain = CatalogDeltaChainState.Empty();
                    }

                    var committedCursor = binding.Cursor;
                    var committedMode = binding.Mode;
                    var networkCursor = requiresFullBoundary ? string.Empty : committedCursor;

                    var seenCursorFingerprints = new HashSet<string>(
                        persistedDeltaChain.CursorFingerprints,
                        StringComparer.Ordinal);
                    var snapshotCatalogVersionPinned = persistedDeltaChain.HasState;
                    var snapshotCatalogVersion = persistedDeltaChain.CatalogVersion;
                    var snapshotSummaryPinned = persistedDeltaChain.HasState &&
                        persistedDeltaChain.SummaryPinned;
                    var snapshotSummaryFingerprint = persistedDeltaChain.SummaryFingerprint;
                    PosCatalogSummaryResponse snapshotSummary = null;
                    if (!requiresFullBoundary && !string.IsNullOrWhiteSpace(binding.Cursor))
                    {
                        seenCursorFingerprints.Add(
                            CatalogShopStateRepository.FingerprintValue(binding.Cursor));
                    }

                    for (var page = 1; page <= effectiveMaxPages; page++)
                    {
                        var requestCursor = networkCursor;
                        var request = new PosCatalogPullRequest
                        {
                            AppVersion = typeof(PosCatalogPullService).Assembly.GetName().Version?.ToString(),
                            DeviceToken = trustedSession.DeviceToken,
                            Limit = CatalogPullPageLimit,
                            PosSessionId = trustedSession.PosSessionId,
                            SessionToken = trustedSession.SessionToken,
                            ShopDeviceId = trustedSession.ShopDeviceId,
                            // TASK-027 scanner marker: SyncCursor is loaded from persistent shop-bound state.
                            SyncCursor = requestCursor,
                        };
                        var result = await CatalogPullWithRetryAsync(
                            client,
                            request,
                            executionContext,
                            cancellationToken)
                            .ConfigureAwait(false);
                        var resultCode = result.Value != null && !result.Value.Ok
                            ? FirstNonEmpty(result.Value.Code, "catalog_response_not_ok")
                            : result.Code;

                        if ((!result.Success || result.Value == null || !result.Value.Ok || result.Value.Catalog == null) &&
                            !result.Denied &&
                            page == 1 &&
                            requestCursor.Length > 0 &&
                            IsCatalogCursorRejectionCode(resultCode))
                        {
                            // Probe the required empty-cursor boundary without changing local state.
                            // A valid full response is fenced below only after its terminal-page and
                            // authoritative-budget evidence has passed.
                            requestCursor = string.Empty;
                            request.SyncCursor = requestCursor;
                            result = await CatalogPullWithRetryAsync(
                                client,
                                request,
                                executionContext,
                                cancellationToken)
                                .ConfigureAwait(false);
                            resultCode = result.Value != null && !result.Value.Ok
                                ? FirstNonEmpty(result.Value.Code, "catalog_response_not_ok")
                                : result.Code;
                        }

                        if (!result.Success || result.Value == null || !result.Value.Ok || result.Value.Catalog == null)
                        {
                            var authenticationDenied = result.Denied ||
                                SharedAuthStopPolicy.IsAuthenticationDenied(resultCode);
                            if (authenticationDenied && clearStoredStateOnDenied)
                            {
                                _store.Clear();
                            }

                            if (authenticationDenied)
                            {
                                try
                                {
                                    await StoreCatalogFailureAsync(resultCode).ConfigureAwait(false);
                                    await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedAuthDenied)
                                        .ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(
                                        "Catalog auth-stop settings persistence deferred: category=catalog.pull code=auth_denied",
                                        ex);
                                }
                            }
                            else
                            {
                                await StoreCatalogFailureAsync(resultCode).ConfigureAwait(false);
                                await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                    .ConfigureAwait(false);
                            }

                            _logger.LogWarning(
                                "Catalog pull skipped: category=catalog.pull code=" + SafeCode(resultCode) +
                                " clientRequestId=" + SafeId(result.ClientRequestId) +
                                " serverRequestId=" + SafeId(result.ServerRequestId) +
                                " cfRay=" + SafeId(result.CfRay));
                            return PosCatalogPullOutcome.Failure(
                                SafeCode(resultCode),
                                authenticationDenied,
                                false,
                                pagesProcessed);
                        }

                        var compatibilityError = PosOnlineCompatibilityValidator.ValidateCatalogPull(result.Value);
                        if (!string.IsNullOrWhiteSpace(compatibilityError))
                        {
                            await StoreCatalogFailureAsync(compatibilityError).ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                            return PosCatalogPullOutcome.Failure(
                                compatibilityError,
                                false,
                                false,
                                pagesProcessed);
                        }

                        var pageIsFullRefresh = string.Equals(
                            result.Value.SyncMode,
                            "full_refresh",
                            StringComparison.OrdinalIgnoreCase);
                        var fullSnapshotExpected = pageIsFullRefresh ||
                            requiresFullBoundary ||
                            requestCursor.Length == 0;
                        var cumulativeFullEvidence = fullSnapshotExpected
                            ? fullLaneEvidence.Add(result.Value.Catalog)
                            : null;
                        var paginationSafety = CatalogPaginationSafetyPolicy.EvaluateTerminalPage(
                            result.Value,
                            CatalogPullPageLimit,
                            fullSnapshotExpected,
                            receivedBeforePage: receivedFullLanes,
                            cumulativeEvidence: cumulativeFullEvidence,
                            pageAfterContinuation: page > 1);
                        if (!paginationSafety.Allowed)
                        {
                            await StoreCatalogFailureAsync(paginationSafety.Code).ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                            _logger.LogWarning(
                                "Catalog full page rejected before reset/apply: category=catalog.pull code=" +
                                SafeCode(paginationSafety.Code) +
                                " page=" + page.ToString() +
                                " limit=" + CatalogPullPageLimit.ToString() +
                                " hasMore=" + result.Value.HasMore.ToString());
                            return PosCatalogPullOutcome.Failure(
                                paginationSafety.Code,
                                false,
                                false,
                                pagesProcessed);
                        }

                        if (page == 1 && pageIsFullRefresh)
                        {
                            firstPageBudget = CatalogPaginationSafetyPolicy.CalculatePageBudget(
                                result.Value.CatalogSummary,
                                CatalogPullPageLimit,
                                LegacyFullCatalogPullPages,
                                MaxAuthoritativeCatalogPullPages);
                            if (!firstPageBudget.Allowed)
                            {
                                await StoreCatalogFailureAsync(firstPageBudget.Code).ConfigureAwait(false);
                                await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                    .ConfigureAwait(false);
                                return PosCatalogPullOutcome.Failure(
                                    firstPageBudget.Code,
                                    false,
                                    false,
                                    pagesProcessed);
                            }
                        }

                        if (fullSnapshotExpected && fullLaneEvidence.ConflictCode.Length > 0)
                        {
                            await StoreCatalogFailureAsync(fullLaneEvidence.ConflictCode).ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                            return PosCatalogPullOutcome.Failure(
                                fullLaneEvidence.ConflictCode,
                                false,
                                false,
                                pagesProcessed);
                        }

                        var stagedResponseShopError = OutboxShopBinding.GetMismatchCode(
                            trustedSession.ShopId,
                            trustedSession.ShopCode,
                            result.Value.Shop?.ShopId,
                            result.Value.Shop?.ShopCode);
                        if (!string.IsNullOrWhiteSpace(stagedResponseShopError))
                        {
                            await StoreCatalogFailureAsync("response_shop_mismatch").ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                            return PosCatalogPullOutcome.Failure(
                                "response_shop_mismatch",
                                false,
                                false,
                                pagesProcessed);
                        }

                        if (page == 1 && persistedDeltaChain.HasState && pageIsFullRefresh)
                        {
                            // A server-selected full snapshot is a new authoritative boundary,
                            // so a prior resumable delta pin must not be compared with it.
                            persistedDeltaChain = CatalogDeltaChainState.Empty();
                            seenCursorFingerprints.Clear();
                            snapshotCatalogVersionPinned = false;
                            snapshotCatalogVersion = string.Empty;
                            snapshotSummaryPinned = false;
                            snapshotSummaryFingerprint = string.Empty;
                            snapshotSummary = null;
                        }

                        var responseCatalogVersion = Normalize(result.Value.CatalogVersion);
                        var responseSummaryFingerprint = CatalogSummaryFingerprint(
                            result.Value.CatalogSummary);
                        if (page == 1 && persistedDeltaChain.HasState)
                        {
                            var crossRunPinError = persistedDeltaChain.GetSnapshotMismatchCode(
                                responseCatalogVersion,
                                responseSummaryFingerprint,
                                result.Value.CatalogSummary != null,
                                result.Value.SyncMode);
                            if (!string.IsNullOrWhiteSpace(crossRunPinError))
                            {
                                await catalogState.RequestFullRepairWhileBarrierHeldAsync(
                                    trustedSession.ShopId,
                                    trustedSession.ShopCode,
                                    binding.Epoch,
                                    generation).ConfigureAwait(false);
                                await StoreCatalogFailureAsync(crossRunPinError).ConfigureAwait(false);
                                await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                    .ConfigureAwait(false);
                                return PosCatalogPullOutcome.Failure(
                                    crossRunPinError,
                                    false,
                                    false,
                                    pagesProcessed);
                            }
                        }

                        if (!snapshotCatalogVersionPinned)
                        {
                            snapshotCatalogVersion = responseCatalogVersion;
                            snapshotCatalogVersionPinned = true;
                        }
                        else if (!string.Equals(snapshotCatalogVersion, responseCatalogVersion, StringComparison.Ordinal))
                        {
                            const string versionChangedCode = "catalog_version_changed_mid_pull";
                            if (!pageIsFullRefresh && !fullRefresh)
                            {
                                await catalogState.RequestFullRepairWhileBarrierHeldAsync(
                                    trustedSession.ShopId,
                                    trustedSession.ShopCode,
                                    binding.Epoch,
                                    generation).ConfigureAwait(false);
                            }
                            await StoreCatalogFailureAsync(versionChangedCode).ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                            return PosCatalogPullOutcome.Failure(
                                versionChangedCode,
                                false,
                                false,
                                pagesProcessed);
                        }

                        if (snapshotSummaryPinned && result.Value.CatalogSummary == null)
                        {
                            const string summaryMissingCode = "catalog_summary_missing_mid_pull";
                            if (!pageIsFullRefresh && !fullRefresh)
                            {
                                await catalogState.RequestFullRepairWhileBarrierHeldAsync(
                                    trustedSession.ShopId,
                                    trustedSession.ShopCode,
                                    binding.Epoch,
                                    generation).ConfigureAwait(false);
                            }
                            await StoreCatalogFailureAsync(summaryMissingCode).ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                            return PosCatalogPullOutcome.Failure(
                                summaryMissingCode,
                                false,
                                false,
                                pagesProcessed);
                        }

                        if (result.Value.CatalogSummary != null)
                        {
                            if (!snapshotSummaryPinned)
                            {
                                snapshotSummary = result.Value.CatalogSummary;
                                snapshotSummaryFingerprint = responseSummaryFingerprint;
                                snapshotSummaryPinned = true;
                            }
                            else if ((snapshotSummary != null &&
                                     !CatalogSummariesEqual(snapshotSummary, result.Value.CatalogSummary)) ||
                                     (snapshotSummary == null &&
                                      !string.Equals(
                                          snapshotSummaryFingerprint,
                                          responseSummaryFingerprint,
                                          StringComparison.OrdinalIgnoreCase)))
                            {
                                const string summaryChangedCode = "catalog_summary_changed_mid_pull";
                                if (!pageIsFullRefresh && !fullRefresh)
                                {
                                    await catalogState.RequestFullRepairWhileBarrierHeldAsync(
                                        trustedSession.ShopId,
                                        trustedSession.ShopCode,
                                        binding.Epoch,
                                        generation).ConfigureAwait(false);
                                }
                                await StoreCatalogFailureAsync(summaryChangedCode).ConfigureAwait(false);
                                await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                    .ConfigureAwait(false);
                                return PosCatalogPullOutcome.Failure(
                                    summaryChangedCode,
                                    false,
                                    false,
                                    pagesProcessed);
                            }
                        }

                        var responseCursor = Normalize(result.Value.SyncCursor);
                        var responseCursorFingerprint = CatalogShopStateRepository.FingerprintValue(
                            responseCursor);
                        var sameCursor = string.Equals(
                            responseCursor,
                            Normalize(requestCursor),
                            StringComparison.Ordinal);
                        var allowsDeltaNoOpCursor =
                            !result.Value.HasMore &&
                            !CatalogHasMutations(result.Value.Catalog) &&
                            string.Equals(result.Value.SyncMode, "delta", StringComparison.OrdinalIgnoreCase) &&
                            sameCursor;
                        var responseCursorAlreadySeen = responseCursorFingerprint.Length > 0 &&
                            seenCursorFingerprints.Contains(responseCursorFingerprint);
                        if (responseCursor.Length == 0 ||
                            (!allowsDeltaNoOpCursor &&
                             (sameCursor ||
                              responseCursorFingerprint.Length == 0 ||
                              responseCursorAlreadySeen)))
                        {
                            const string cursorProgressCode = "catalog_cursor_not_progressing";
                            if (!pageIsFullRefresh && !fullRefresh)
                            {
                                await catalogState.RequestFullRepairWhileBarrierHeldAsync(
                                    trustedSession.ShopId,
                                    trustedSession.ShopCode,
                                    binding.Epoch,
                                    generation).ConfigureAwait(false);
                            }
                            await StoreCatalogFailureAsync(cursorProgressCode).ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                            return PosCatalogPullOutcome.Failure(
                                cursorProgressCode,
                                false,
                                result.Value.HasMore,
                                pagesProcessed);
                        }

                        if (!pageIsFullRefresh &&
                            !fullRefresh &&
                            !allowsDeltaNoOpCursor &&
                            seenCursorFingerprints.Count >=
                                CatalogShopStateRepository.MaxDeltaChainCursorFingerprints)
                        {
                            var cursorLimitCode = CatalogShopStateRepository.DeltaChainCursorLimitCode;
                            await catalogState.RequestFullRepairWhileBarrierHeldAsync(
                                trustedSession.ShopId,
                                trustedSession.ShopCode,
                                binding.Epoch,
                                generation).ConfigureAwait(false);
                            await StoreCatalogFailureAsync(cursorLimitCode).ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                            return PosCatalogPullOutcome.Failure(
                                cursorLimitCode,
                                false,
                                result.Value.HasMore,
                                pagesProcessed);
                        }

                        if (!allowsDeltaNoOpCursor)
                        {
                            seenCursorFingerprints.Add(responseCursorFingerprint);
                        }

                        if (page > 1 && pageIsFullRefresh != fullRefresh)
                        {
                            if (!fullRefresh)
                            {
                                await catalogState.RequestFullRepairWhileBarrierHeldAsync(
                                    trustedSession.ShopId,
                                    trustedSession.ShopCode,
                                    binding.Epoch,
                                    generation).ConfigureAwait(false);
                            }
                            await StoreCatalogFailureAsync("catalog_sync_mode_changed").ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                            return PosCatalogPullOutcome.Failure(
                                "catalog_sync_mode_changed",
                                false,
                                false,
                                pagesProcessed);
                        }

                        if (page == 1)
                        {
                            fullRefresh = pageIsFullRefresh;
                            if (fullRefresh)
                            {
                                // A server-selected full refresh must drain in this run. Keeping the
                                // background delta cap here would restart from page one forever because
                                // full-refresh cursors are intentionally not checkpointed mid-snapshot.
                                effectiveMaxPages = firstPageBudget == null
                                    ? LegacyFullCatalogPullPages
                                    : firstPageBudget.PageBudget;
                            }
                            if (requiresFullBoundary && !fullRefresh)
                            {
                                const string repairModeCode = "catalog_full_repair_requires_full_refresh";
                                await StoreCatalogFailureAsync(repairModeCode).ConfigureAwait(false);
                                await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                    .ConfigureAwait(false);
                                return PosCatalogPullOutcome.Failure(
                                    repairModeCode,
                                    false,
                                    false,
                                    0);
                            }

                            if (!fullRefresh && requestCursor.Length == 0)
                            {
                                const string emptyCursorModeCode = "catalog_empty_cursor_requires_full_refresh";
                                await StoreCatalogFailureAsync(emptyCursorModeCode).ConfigureAwait(false);
                                await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                    .ConfigureAwait(false);
                                return PosCatalogPullOutcome.Failure(
                                    emptyCursorModeCode,
                                    false,
                                    false,
                                    0);
                            }

                            if (!fullRefresh)
                            {
                                var exactnessState = await catalogState.LoadExactnessAsync()
                                    .ConfigureAwait(false);
                                if (exactnessState.RepairRequired ||
                                    exactnessState.Status != CatalogCompletenessStatus.Verified)
                                {
                                    const string repairRequiredCode = "catalog_full_repair_required";
                                    await StoreCatalogFailureAsync(repairRequiredCode).ConfigureAwait(false);
                                    await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                        .ConfigureAwait(false);
                                    return PosCatalogPullOutcome.Failure(
                                        repairRequiredCode,
                                        false,
                                        false,
                                        0);
                                }
                            }
                        }

                        if (fullRefresh)
                        {
                            effectiveMaxPages = CatalogPaginationSafetyPolicy
                                .ExpandFullPageBudgetForTombstoneContinuation(
                                    effectiveMaxPages,
                                    MaxAuthoritativeCatalogPullPages,
                                    fullSnapshot: true,
                                    hasMore: result.Value.HasMore,
                                    cumulativeEvidence: cumulativeFullEvidence,
                                    summary: result.Value.CatalogSummary);
                        }

                        if (fullRefresh)
                        {
                            AddRemoteIds(
                                authoritativeProductIds,
                                result.Value.Catalog.Products,
                                product => product?.ProductId);
                            AddRemoteIds(
                                authoritativeCategoryIds,
                                result.Value.Catalog.Categories,
                                category => category?.CategoryId);
                            AddRemoteIds(
                                authoritativeSupplierIds,
                                result.Value.Catalog.Suppliers,
                                supplier => supplier?.SupplierId);
                            AddPriceEvidence(
                                authoritativePriceIds,
                                result.Value.Catalog.Prices,
                                ref duplicatePriceRows);
                        }

                        if (fullRefresh)
                        {
                            if (!fullStageStarted)
                            {
                                await fullStage.BeginAsync(fullStageGeneration).ConfigureAwait(false);
                                fullStageStarted = true;
                            }

                            fullStageBytes = await fullStage.AppendAsync(
                                fullStageGeneration,
                                page,
                                result.Value,
                                fullStageBytes).ConfigureAwait(false);
                            receivedFullLanes = cumulativeFullEvidence ?? receivedFullLanes;
                            networkCursor = result.Value.SyncCursor;
                            lastResponse = result.Value;
                            lastResult = result;
                            pagesProcessed = page;
                            progress?.Report(PosCatalogPullProgress.ForCatalogPage(
                                page,
                                result.Value.HasMore,
                                ToSafeProgressCount(receivedFullLanes.Products),
                                ToSafeProgressCount(receivedFullLanes.Categories),
                                ToSafeProgressCount(receivedFullLanes.Suppliers),
                                ToSafeProgressCount(receivedFullLanes.Prices),
                                0,
                                0,
                                ToSafeProgressCount(
                                    receivedFullLanes.ProductTombstones +
                                    receivedFullLanes.CategoryTombstones +
                                    receivedFullLanes.SupplierTombstones),
                                0));
                            _logger.LogInfo(
                                "Catalog full page staged: category=catalog.pull page=" + page.ToString() +
                                ", maxPages=" + effectiveMaxPages.ToString() +
                                ", limit=" + CatalogPullPageLimit.ToString() +
                                ", bytes=" + fullStageBytes.ToString() +
                                ", hasMore=" + result.Value.HasMore.ToString() +
                                ", catalogVersion=" + SafeId(result.Value.CatalogVersion));
                            if (!result.Value.HasMore)
                            {
                                break;
                            }

                            continue;
                        }

                        var applyStats = await ApplyCatalogAsync(
                            catalogApplyRun,
                            result.Value,
                            fullRefresh,
                            trustedSession,
                            binding.Epoch,
                            committedCursor,
                            committedMode,
                            generation,
                            cancellationToken)
                            .ConfigureAwait(false);
                        totalStats.Add(applyStats);
                        if (applyStats.RowsSkipped > 0)
                        {
                            const string skippedRowsCode = "catalog_rows_not_fully_applied";
                            await catalogState.RequestFullRepairWhileBarrierHeldAsync(
                                trustedSession.ShopId,
                                trustedSession.ShopCode,
                                binding.Epoch,
                                generation).ConfigureAwait(false);
                            await StoreCatalogFailureAsync(skippedRowsCode).ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                            return PosCatalogPullOutcome.Failure(
                                skippedRowsCode,
                                false,
                                result.Value.HasMore,
                                pagesProcessed);
                        }

                        var deltaCheckpoint = fullRefresh
                            ? null
                            : new CatalogDeltaChainCheckpoint
                            {
                                CatalogVersion = snapshotCatalogVersion,
                                CursorFingerprints = seenCursorFingerprints.ToArray(),
                                HasMore = result.Value.HasMore,
                                SummaryFingerprint = snapshotSummaryFingerprint,
                                SummaryPinned = snapshotSummaryPinned,
                                SyncMode = "delta"
                            };
                        await StoreCatalogDiagnosticsAsync(
                            result.Value,
                            applyStats,
                            trustedSession,
                            binding.Epoch,
                            deltaCheckpoint,
                            fullRefresh,
                            committedCursor,
                            committedMode,
                            generation).ConfigureAwait(false);

                        networkCursor = result.Value.SyncCursor;
                        committedCursor = result.Value.SyncCursor;
                        committedMode = result.Value.SyncMode;

                        lastResponse = result.Value;
                        lastResult = result;
                        pagesProcessed = page;
                        progress?.Report(PosCatalogPullProgress.ForCatalogPage(
                            page,
                            result.Value.HasMore,
                            totalStats.UpdatedProducts,
                            totalStats.CategoryRowsReceived,
                            totalStats.SupplierRowsReceived,
                            totalStats.PriceRowsApplied,
                            totalStats.PriceRowsQueued,
                            totalStats.PendingPriceRowsApplied,
                            totalStats.TombstonesReceived,
                            totalStats.TombstonesApplied));
                        _logger.LogInfo(
                            "Catalog pull page applied: category=catalog.pull page=" + page.ToString() +
                            ", maxPages=" + effectiveMaxPages.ToString() +
                            ", limit=" + CatalogPullPageLimit.ToString() +
                            ", products=" + applyStats.UpdatedProducts.ToString() +
                            ", prices=" + applyStats.PriceRowsApplied.ToString() +
                            ", queuedPrices=" + applyStats.PriceRowsQueued.ToString() +
                            ", pendingPricesApplied=" + applyStats.PendingPriceRowsApplied.ToString() +
                            ", hasMore=" + result.Value.HasMore.ToString() +
                            ", catalogVersion=" + SafeId(result.Value.CatalogVersion));

                        if (!result.Value.HasMore)
                        {
                            break;
                        }
                    }

                    if (lastResponse == null)
                    {
                        await StoreCatalogFailureAsync("empty_response").ConfigureAwait(false);
                        if (bootstrapRun)
                        {
                            await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                        }

                        return PosCatalogPullOutcome.Failure(
                            "empty_response",
                            false,
                            false,
                            pagesProcessed);
                    }

                    if (lastResponse.HasMore)
                    {
                        if (!fullRefresh &&
                            (await new ProductRepository(_factory)
                                .CountActiveRemoteProductsAsync()
                                .ConfigureAwait(false)) <= 0)
                        {
                            const string partialEmptyCode = "catalog_partial_delta_no_active_products";
                            await catalogState.RequestFullRepairWhileBarrierHeldAsync(
                                trustedSession.ShopId,
                                trustedSession.ShopCode,
                                binding.Epoch,
                                generation).ConfigureAwait(false);
                            await StoreCatalogFailureAsync(partialEmptyCode).ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                            return PosCatalogPullOutcome.Failure(
                                partialEmptyCode,
                                false,
                                true,
                                pagesProcessed,
                                totalStats.UpdatedProducts,
                                totalStats.PriceRowsApplied,
                                totalStats.PriceRowsQueued,
                                totalStats.PendingPriceRowsApplied);
                        }

                        await StoreCatalogFailureAsync(CatalogHasMoreNotDrainedCode).ConfigureAwait(false);
                        await StoreCatalogBootstrapStatusAsync(BootstrapStatusPartialHasMore)
                            .ConfigureAwait(false);
                        _logger.LogWarning(
                            "Catalog pull stopped before draining all pages: category=catalog.pull code=" +
                            CatalogHasMoreNotDrainedCode +
                            " pages=" + pagesProcessed.ToString() +
                            ", maxPages=" + effectiveMaxPages.ToString() +
                            ", limit=" + CatalogPullPageLimit.ToString() +
                            ", cursorSaved=" + (!fullRefresh).ToString() + ".");
                        return PosCatalogPullOutcome.Failure(
                            CatalogHasMoreNotDrainedCode,
                            false,
                            true,
                            pagesProcessed,
                            totalStats.UpdatedProducts,
                            totalStats.PriceRowsApplied,
                            totalStats.PriceRowsQueued,
                            totalStats.PendingPriceRowsApplied);
                    }

                    if (fullRefresh)
                    {
                        if (!fullStageStarted || pagesProcessed <= 0)
                        {
                            const string stageMissingCode = "catalog_full_stage_missing";
                            await StoreCatalogFailureAsync(stageMissingCode).ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                            return PosCatalogPullOutcome.Failure(
                                stageMissingCode,
                                false,
                                false,
                                pagesProcessed);
                        }

                        // Only a completely drained and protocol-validated full chain may
                        // replace the live generation. Network/protocol failures above never
                        // reach this destructive boundary.
                        await catalogState.RequestFullRepairWhileBarrierHeldAsync(
                            trustedSession.ShopId,
                            trustedSession.ShopCode,
                            binding.Epoch,
                            generation).ConfigureAwait(false);
                        binding = await catalogState.EnsureAndLoadCursorAsync(
                            trustedSession.ShopId,
                            trustedSession.ShopCode,
                            generation).ConfigureAwait(false);
                        if (!binding.IsValid)
                        {
                            return PosCatalogPullOutcome.Failure(
                                binding.Code,
                                false,
                                false,
                                pagesProcessed);
                        }

                        capturedEpoch = binding.Epoch;
                        committedCursor = binding.Cursor;
                        committedMode = binding.Mode;
                        totalStats = new CatalogApplyStats();
                        for (var stagedPageNumber = 1;
                             stagedPageNumber <= pagesProcessed;
                             stagedPageNumber++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var stagedResponse = await fullStage.LoadPageAsync(
                                fullStageGeneration,
                                stagedPageNumber).ConfigureAwait(false);
                            var stagedStats = await ApplyCatalogAsync(
                                catalogApplyRun,
                                stagedResponse,
                                true,
                                trustedSession,
                                binding.Epoch,
                                committedCursor,
                                committedMode,
                                generation,
                                cancellationToken).ConfigureAwait(false);
                            totalStats.Add(stagedStats);
                            if (stagedStats.RowsSkipped > 0)
                            {
                                const string stagedRowsCode = "catalog_rows_not_fully_applied";
                                await StoreCatalogFailureAsync(stagedRowsCode).ConfigureAwait(false);
                                await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                    .ConfigureAwait(false);
                                return PosCatalogPullOutcome.Failure(
                                    stagedRowsCode,
                                    false,
                                    false,
                                    stagedPageNumber,
                                    totalStats.UpdatedProducts,
                                    totalStats.PriceRowsApplied,
                                    totalStats.PriceRowsQueued,
                                    totalStats.PendingPriceRowsApplied);
                            }

                            await StoreCatalogDiagnosticsAsync(
                                stagedResponse,
                                stagedStats,
                                trustedSession,
                                binding.Epoch,
                                null,
                                true,
                                committedCursor,
                                committedMode,
                                generation).ConfigureAwait(false);
                        }
                    }

                    if (fullRefresh)
                    {
                        syncTimer.Stop();
                        var exactness = await new CatalogFullRefreshReconciler(_factory)
                            .ReconcileAndVerifyAsync(
                            authoritativeProductIds,
                            authoritativeCategoryIds,
                            authoritativeSupplierIds,
                            lastResponse.GeneratedAt,
                            snapshotSummary,
                            new CatalogExactnessRunContext
                            {
                                CatalogVersion = FirstNonEmpty(snapshotCatalogVersion, lastResponse.CatalogVersion),
                                DurationMilliseconds = syncTimer.ElapsedMilliseconds,
                                HasMore = lastResponse.HasMore,
                                Pages = pagesProcessed,
                                PriceRowsReceived = totalStats.PriceRowsReceived,
                                PriceRowsAccepted = totalStats.PriceRowsApplied + totalStats.PriceRowsQueued,
                                InvalidPriceRows = totalStats.PriceRowsSkipped,
                                DuplicatePriceRows = duplicatePriceRows,
                                ProductRowsReceived = totalStats.UpdatedProducts,
                                CategoryRowsReceived = receivedFullLanes.Categories,
                                SupplierRowsReceived = receivedFullLanes.Suppliers,
                                SyncCursor = lastResponse.SyncCursor,
                                SyncMode = lastResponse.SyncMode,
                                TombstonesReceived = totalStats.TombstonesReceived
                            },
                            generation).ConfigureAwait(false);
                        await catalogState.StoreExactnessAsync(
                            trustedSession.ShopId,
                            trustedSession.ShopCode,
                            exactness,
                            binding.Epoch,
                            committedCursor,
                            committedMode,
                            generation).ConfigureAwait(false);

                        if (exactness.Status != CatalogCompletenessStatus.Verified ||
                            exactness.RepairRequired)
                        {
                            var exactnessCode = SafeCode(exactness.Code);
                            await StoreCatalogFailureAsync(exactnessCode).ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                            _logger.LogWarning(
                                "Catalog exactness rejected authoritative snapshot: category=catalog.pull code=" +
                                exactnessCode +
                                " pages=" + pagesProcessed.ToString() +
                                " products=" + totalStats.UpdatedProducts.ToString() +
                                " categories=" + totalStats.CategoryRowsReceived.ToString() +
                                " suppliers=" + totalStats.SupplierRowsReceived.ToString() + ".");
                            return PosCatalogPullOutcome.Failure(
                                exactnessCode,
                                false,
                                false,
                                pagesProcessed,
                                totalStats.UpdatedProducts,
                                totalStats.PriceRowsApplied,
                                totalStats.PriceRowsQueued,
                                totalStats.PendingPriceRowsApplied);
                        }

                    }
                    else
                    {
                        var deltaAudit = await new CatalogFullRefreshReconciler(_factory)
                            .AuditCurrentAsync()
                            .ConfigureAwait(false);
                        var deltaIntegrityError = CatalogExactnessVerifier.FindInvariantError(deltaAudit);
                        if (!string.IsNullOrWhiteSpace(deltaIntegrityError))
                        {
                            var safeDeltaCode = SafeCode(deltaIntegrityError);
                            await catalogState.RequestFullRepairWhileBarrierHeldAsync(
                                trustedSession.ShopId,
                                trustedSession.ShopCode,
                                binding.Epoch,
                                generation).ConfigureAwait(false);
                            await StoreCatalogFailureAsync(safeDeltaCode).ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                            return PosCatalogPullOutcome.Failure(
                                safeDeltaCode,
                                false,
                                false,
                                pagesProcessed,
                                totalStats.UpdatedProducts,
                                totalStats.PriceRowsApplied,
                                totalStats.PriceRowsQueued,
                                totalStats.PendingPriceRowsApplied);
                        }
                    }

                    var activeRemoteProducts = await new ProductRepository(_factory)
                        .CountActiveRemoteProductsAsync()
                        .ConfigureAwait(false);
                    if (activeRemoteProducts <= 0)
                    {
                        var noProductsCode = fullRefresh
                            ? "full_refresh_no_active_products"
                            : "no_catalog_products";
                        await catalogState.RequestFullRepairWhileBarrierHeldAsync(
                            trustedSession.ShopId,
                            trustedSession.ShopCode,
                            binding.Epoch,
                            generation).ConfigureAwait(false);
                        await StoreCatalogFailureAsync(noProductsCode).ConfigureAwait(false);
                        await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                            .ConfigureAwait(false);
                        _logger.LogWarning(
                            "Catalog pull completed without sale-safe product rows: category=catalog.pull code=" +
                            noProductsCode + " pages=" + pagesProcessed.ToString());
                        return PosCatalogPullOutcome.Failure(
                            noProductsCode,
                            false,
                            false,
                            pagesProcessed,
                            totalStats.UpdatedProducts,
                            totalStats.PriceRowsApplied,
                            totalStats.PriceRowsQueued,
                            totalStats.PendingPriceRowsApplied);
                    }

                    if (fullRefresh)
                    {
                        if (!await StoreLastSyncAsync(
                            lastResponse.SyncCursor,
                            lastResponse.GeneratedAt,
                            trustedSession,
                            binding.Epoch,
                            lastResponse.SyncMode,
                            authoritativeSnapshotCommitted: true,
                            expectedPreviousCursor: committedCursor,
                            expectedPreviousMode: committedMode,
                            generation: generation).ConfigureAwait(false))
                        {
                            throw new InvalidOperationException("Catalog full cursor commit was rejected.");
                        }

                        committedCursor = lastResponse.SyncCursor;
                        committedMode = lastResponse.SyncMode;
                    }

                    await StoreCatalogSaleSafeAsync(
                        lastResponse.GeneratedAt,
                        FirstNonEmpty(snapshotCatalogVersion, lastResponse.CatalogVersion),
                        trustedSession,
                        binding.Epoch,
                        committedCursor,
                        committedMode,
                        capturedImportAckGeneration,
                        generation).ConfigureAwait(false);
                    await StoreCatalogBootstrapStatusAsync(BootstrapStatusCompleted)
                        .ConfigureAwait(false);
                    if (fullRefresh && fullStageStarted)
                    {
                        try
                        {
                            await fullStage.ClearAsync(fullStageGeneration).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // The staged copy is non-authoritative. Once the live cursor and
                            // sale-safe state are committed, cleanup failure must not turn a
                            // successful sync into a false failure; the next BeginAsync also
                            // removes every stale generation.
                            _logger.LogWarning(
                                "Catalog full stage cleanup deferred: category=catalog.pull code=catalog_full_stage_cleanup_failed");
                        }
                    }
                    _logger.LogInfo(
                        "Catalog pull completed: category=catalog.pull products=" + totalStats.UpdatedProducts.ToString() +
                        ", prices=" + totalStats.PriceRowsApplied.ToString() +
                        ", queuedPrices=" + totalStats.PriceRowsQueued.ToString() +
                        ", pendingPricesApplied=" + totalStats.PendingPriceRowsApplied.ToString() +
                        ", pages=" + pagesProcessed.ToString() +
                        ", limit=" + CatalogPullPageLimit.ToString() +
                        ", hasMore=" + lastResponse.HasMore.ToString() +
                        ", catalogVersion=" + (lastResponse.CatalogVersion ?? string.Empty) +
                        " clientRequestId=" + SafeId(lastResult?.ClientRequestId) +
                        " serverRequestId=" + SafeId(lastResult?.ServerRequestId) +
                        " cfRay=" + SafeId(lastResult?.CfRay));
                    return PosCatalogPullOutcome.CompletedOk(
                        pagesProcessed,
                        totalStats.UpdatedProducts,
                        totalStats.PriceRowsApplied,
                        totalStats.PriceRowsQueued,
                        totalStats.PendingPriceRowsApplied);
                }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                await StoreCatalogFailureForGenerationAsync(
                    trustedSession,
                    capturedEpoch,
                    "timeout",
                    BootstrapStatusFailedRetryable).ConfigureAwait(false);

                _logger.LogWarning("Catalog pull timeout.");
                return PosCatalogPullOutcome.Failure("timeout", false, false, 0);
            }
            catch (Exception ex)
            {
                await StoreCatalogFailureForGenerationAsync(
                    trustedSession,
                    capturedEpoch,
                    "exception",
                    BootstrapStatusFailedRetryable).ConfigureAwait(false);

                _logger.LogWarning("Catalog pull skipped.", ex);
                return PosCatalogPullOutcome.Failure("exception", false, false, 0);
            }
        }

        private async Task<PosOnlineResult<PosCatalogPullResponse>> CatalogPullWithRetryAsync(
            PosAdminWebClient client,
            PosCatalogPullRequest request,
            OnlineSyncLaneExecutionContext executionContext,
            CancellationToken cancellationToken)
        {
            for (var attempt = 1; attempt <= MaxCatalogPullAttempts; attempt++)
            {
                PosOnlineResult<PosCatalogPullResponse> result;
                if (executionContext == null)
                {
                    result = await client.CatalogPullAsync(
                        request,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        result = await executionContext.ExecuteCredentialedRequestAsync(
                        async (credentials, token) =>
                        {
                            request.DeviceToken = credentials.DeviceToken;
                            request.PosSessionId = credentials.PosSessionId;
                            request.SessionToken = credentials.SessionToken;
                            request.ShopDeviceId = credentials.ShopDeviceId;
                            return await client.CatalogPullAsync(
                                request,
                                token).ConfigureAwait(false);
                        },
                        response =>
                        {
                            var responseCode = response.Value != null && !response.Value.Ok
                                ? FirstNonEmpty(response.Value.Code, "catalog_response_not_ok")
                                : response.Code;
                            return response.Denied ||
                                SharedAuthStopPolicy.IsAuthenticationDenied(responseCode)
                                ? responseCode
                                : string.Empty;
                        },
                        cancellationToken).ConfigureAwait(false);
                    }
                    catch (OnlineSyncCredentialsChangedException) when (
                        attempt < MaxCatalogPullAttempts)
                    {
                        continue;
                    }
                }

                if (result.Success ||
                    result.Denied ||
                    !IsRetryableCatalogPullCode(result.Code) ||
                    attempt == MaxCatalogPullAttempts)
                {
                    return result;
                }

                await Task.Delay(CatalogPullBackoff(attempt), cancellationToken).ConfigureAwait(false);
            }

            return PosOnlineResult<PosCatalogPullResponse>.Failure(
                "retry_exhausted",
                "Catalog pull retry exhausted.",
                false);
        }

        private sealed class CatalogApplyStats
        {
            public int CategoryRowsReceived { get; set; }
            public int PendingPriceRowsApplied { get; set; }
            public int PriceRowsApplied { get; set; }
            public int PriceRowsQueued { get; set; }
            public int PriceRowsReceived { get; set; }
            public int PriceRowsSkipped { get; set; }
            public int RowsSkipped { get; set; }
            public int SupplierRowsReceived { get; set; }
            public int TombstonesApplied { get; set; }
            public int TombstonesReceived { get; set; }
            public int UpdatedProducts { get; set; }

            public void Add(CatalogApplyStats stats)
            {
                if (stats == null)
                {
                    return;
                }

                CategoryRowsReceived += stats.CategoryRowsReceived;
                PendingPriceRowsApplied += stats.PendingPriceRowsApplied;
                PriceRowsApplied += stats.PriceRowsApplied;
                PriceRowsQueued += stats.PriceRowsQueued;
                PriceRowsReceived += stats.PriceRowsReceived;
                PriceRowsSkipped += stats.PriceRowsSkipped;
                RowsSkipped += stats.RowsSkipped;
                SupplierRowsReceived += stats.SupplierRowsReceived;
                TombstonesApplied += stats.TombstonesApplied;
                TombstonesReceived += stats.TombstonesReceived;
                UpdatedProducts += stats.UpdatedProducts;
            }
        }

        private async Task<CatalogApplyStats> ApplyCatalogAsync(
            RemoteCatalogApplyRunContext applyRun,
            PosCatalogPullResponse response,
            bool authoritativeFullRefresh,
            PosTrustedDeviceSession trustedSession,
            long expectedEpoch,
            string expectedPreviousCursor,
            string expectedPreviousMode,
            OnlineSyncGeneration generation,
            CancellationToken cancellationToken)
        {
            var catalog = response.Catalog;
            var products = catalog.Products ?? Array.Empty<PosCatalogProductResponse>();
            var priceRows = catalog.Prices ?? Array.Empty<PosCatalogPriceResponse>();
            var categories = BuildCategoryMap(catalog.Categories);
            var suppliers = BuildSupplierMap(catalog.Suppliers);
            var tombstones =
                (catalog.Tombstones?.Products?.Length ?? 0) +
                (catalog.Tombstones?.Categories?.Length ?? 0) +
                (catalog.Tombstones?.Suppliers?.Length ?? 0);
            var batch = new RemoteCatalogBatch
            {
                AuthoritativeFullRefresh = authoritativeFullRefresh,
                Categories = (catalog.Categories ?? Array.Empty<PosCatalogCategoryResponse>())
                    .Select(row => row == null ? null : new RemoteCatalogCategoryWrite
                    {
                        RemoteCategoryId = Normalize(row.CategoryId),
                        Name = Normalize(row.Name),
                        RemoteUpdatedAt = Normalize(row.UpdatedAt)
                    })
                    .ToArray(),
                Suppliers = (catalog.Suppliers ?? Array.Empty<PosCatalogSupplierResponse>())
                    .Select(row => row == null ? null : new RemoteCatalogSupplierWrite
                    {
                        RemoteSupplierId = Normalize(row.SupplierId),
                        Name = Normalize(row.Name),
                        RemoteUpdatedAt = Normalize(row.UpdatedAt)
                    })
                    .ToArray(),
                Products = products
                    .Select(row => row == null ? null : new RemoteCatalogProductWrite
                    {
                        ArticleCode = Normalize(row.ItemNumber),
                        Barcode = Normalize(row.Barcode),
                        CategoryName = NameFor(categories, row.CategoryId),
                        Name = FirstNonEmpty(row.ProductName, row.SecondProductName, row.Barcode),
                        PurchasePrice = ToInt(row.PurchasePrice),
                        RemoteCategoryId = Normalize(row.CategoryId),
                        RemoteProductId = Normalize(row.ProductId),
                        RemoteSupplierId = Normalize(row.SupplierId),
                        SecondName = Normalize(row.SecondProductName),
                        StockQuantity = ToInt(row.StockQuantity),
                        SupplierName = NameFor(suppliers, row.SupplierId),
                        UnitPrice = ToLong(row.RetailPrice)
                    })
                    .ToArray(),
                Prices = priceRows
                    .Select(row => row == null ? null : new RemoteCatalogPriceWrite
                    {
                        EffectiveAt = Normalize(row.EffectiveAt),
                        Price = row.Price < 0 || double.IsNaN(row.Price) || double.IsInfinity(row.Price)
                            ? -1
                            : ToInt(row.Price),
                        RemotePriceId = Normalize(row.PriceId),
                        RemoteProductId = Normalize(row.ProductId),
                        Source = Normalize(row.Source),
                        Type = Normalize(row.Type)
                    })
                    .ToArray(),
                ProductTombstones = (catalog.Tombstones?.Products ?? Array.Empty<PosCatalogProductTombstoneResponse>())
                    .Select(row => row == null ? null : new RemoteCatalogProductTombstoneWrite
                    {
                        RemoteProductId = Normalize(row.ProductId),
                        RemoteDeletedAt = Normalize(row.DeletedAt)
                    })
                    .ToArray(),
                CategoryTombstones = (catalog.Tombstones?.Categories ?? Array.Empty<PosCatalogCategoryTombstoneResponse>())
                    .Select(row => row == null ? null : new RemoteCatalogCategoryTombstoneWrite
                    {
                        RemoteCategoryId = Normalize(row.CategoryId),
                        RemoteDeletedAt = Normalize(row.DeletedAt),
                        RemoteUpdatedAt = Normalize(row.UpdatedAt)
                    })
                    .ToArray(),
                SupplierTombstones = (catalog.Tombstones?.Suppliers ?? Array.Empty<PosCatalogSupplierTombstoneResponse>())
                    .Select(row => row == null ? null : new RemoteCatalogSupplierTombstoneWrite
                    {
                        RemoteSupplierId = Normalize(row.SupplierId),
                        RemoteDeletedAt = Normalize(row.DeletedAt),
                        RemoteUpdatedAt = Normalize(row.UpdatedAt)
                    })
                    .ToArray()
            };

            var applied = await applyRun
                .ApplyAsync(
                    batch,
                    cancellationToken,
                    new RemoteCatalogCommitFence
                    {
                        ExpectedEpoch = expectedEpoch,
                        ExpectedPreviousCursor = expectedPreviousCursor,
                        ExpectedPreviousMode = expectedPreviousMode,
                        GenerationFingerprint = generation?.Fingerprint ?? string.Empty,
                        GenerationId = generation?.GenerationId ?? string.Empty,
                        PosSessionId = generation?.PosSessionId ?? string.Empty,
                        ShopCode = trustedSession.ShopCode,
                        ShopDeviceId = generation?.ShopDeviceId ?? string.Empty,
                        ShopId = trustedSession.ShopId
                    })
                .ConfigureAwait(false);

            if (tombstones > 0)
            {
                _logger.LogInfo(
                    "Catalog tombstones received: count=" + tombstones.ToString() +
                    ", appliedProducts=" + applied.ProductTombstonesApplied.ToString() +
                    ", appliedCategories=" + applied.CategoryTombstonesApplied.ToString() +
                    ", appliedSuppliers=" + applied.SupplierTombstonesApplied.ToString() +
                    "; local purge disabled; tombstones are stored as inactive rows.");
            }

            return new CatalogApplyStats
            {
                CategoryRowsReceived = catalog.Categories?.Length ?? 0,
                PendingPriceRowsApplied = applied.PendingPricesApplied,
                PriceRowsApplied = applied.PricesApplied,
                PriceRowsQueued = applied.PricesQueued,
                PriceRowsReceived = priceRows.Length,
                PriceRowsSkipped = applied.PricesSkipped,
                RowsSkipped = applied.RowsSkipped,
                SupplierRowsReceived = catalog.Suppliers?.Length ?? 0,
                TombstonesApplied = applied.TombstonesApplied,
                TombstonesReceived = tombstones,
                UpdatedProducts = products.Length
            };
        }

        private async Task<bool> StoreLastSyncAsync(
            string syncCursor,
            string generatedAt,
            PosTrustedDeviceSession trustedSession,
            long expectedEpoch,
            string syncMode,
            bool authoritativeSnapshotCommitted,
            CatalogDeltaChainCheckpoint deltaCheckpoint = null,
            string expectedPreviousCursor = null,
            string expectedPreviousMode = null,
            OnlineSyncGeneration generation = null)
        {
            return await new CatalogShopStateRepository(_factory).StorePullCursorAsync(
                trustedSession.ShopId,
                trustedSession.ShopCode,
                syncCursor,
                generatedAt,
                expectedEpoch,
                syncMode,
                authoritativeSnapshotCommitted,
                deltaCheckpoint,
                expectedPreviousCursor,
                expectedPreviousMode,
                generation).ConfigureAwait(false);
        }

        private async Task StoreCatalogFailureAsync(string code)
        {
            var settings = new SettingsRepository(_factory);
            await settings.SetStringIfGenerationCurrentAsync(
                LastCatalogErrorSettingKey,
                SafeCode(code),
                _diagnosticGeneration).ConfigureAwait(false);
        }

        private async Task StoreCatalogFailureForGenerationAsync(
            PosTrustedDeviceSession trustedSession,
            long expectedEpoch,
            string code,
            string bootstrapStatus)
        {
            if (trustedSession == null || expectedEpoch < 0)
            {
                return;
            }

            using (await new CatalogShopTransitionBarrier(_factory).EnterAsync().ConfigureAwait(false))
            {
                try
                {
                    await new CatalogShopStateRepository(_factory).ValidateBindingEpochAsync(
                        trustedSession.ShopId,
                        trustedSession.ShopCode,
                        expectedEpoch,
                        _diagnosticGeneration).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    _logger.LogInfo("Catalog failure state skipped: generation changed.");
                    return;
                }

                await StoreCatalogFailureAsync(code).ConfigureAwait(false);
                await StoreCatalogBootstrapStatusAsync(bootstrapStatus).ConfigureAwait(false);
            }
        }

        private async Task StoreCatalogBootstrapStatusAsync(string status)
        {
            var settings = new SettingsRepository(_factory);
            await settings.SetStringIfGenerationCurrentAsync(
                CatalogBootstrapStatusSettingKey,
                SafeCode(status),
                _diagnosticGeneration).ConfigureAwait(false);
        }

        private async Task StoreCatalogSaleSafeAsync(
            string generatedAt,
            string committedRevision,
            PosTrustedDeviceSession trustedSession,
            long expectedEpoch,
            string expectedPreviousCursor,
            string expectedPreviousMode,
            long reconciledImportAckGeneration,
            OnlineSyncGeneration generation)
        {
            await new CatalogShopStateRepository(_factory).StoreSaleSafeAsync(
                trustedSession.ShopId,
                trustedSession.ShopCode,
                generatedAt,
                expectedEpoch,
                expectedPreviousCursor,
                expectedPreviousMode,
                committedRevision,
                reconciledImportAckGeneration,
                generation).ConfigureAwait(false);
        }

        private async Task StoreCatalogDiagnosticsAsync(
            PosCatalogPullResponse response,
            CatalogApplyStats stats,
            PosTrustedDeviceSession trustedSession,
            long expectedEpoch,
            CatalogDeltaChainCheckpoint deltaCheckpoint,
            bool fullRefresh,
            string expectedPreviousCursor,
            string expectedPreviousMode,
            OnlineSyncGeneration generation)
        {
            var settings = new SettingsRepository(_factory);
            var state = new CatalogShopStateRepository(_factory);

            if (fullRefresh)
            {
                await state.ValidateCommitStateAsync(
                    trustedSession.ShopId,
                    trustedSession.ShopCode,
                    expectedEpoch,
                    expectedPreviousCursor,
                    expectedPreviousMode,
                    generation).ConfigureAwait(false);
            }
            else if (!await StoreLastSyncAsync(
                response.SyncCursor,
                response.GeneratedAt,
                trustedSession,
                expectedEpoch,
                response.SyncMode,
                authoritativeSnapshotCommitted: false,
                deltaCheckpoint: deltaCheckpoint,
                expectedPreviousCursor: expectedPreviousCursor,
                expectedPreviousMode: expectedPreviousMode,
                generation: generation).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Catalog delta cursor commit was rejected.");
            }

            await PosOnlineShopSnapshot.SaveAsync(_factory, response?.Shop, generation).ConfigureAwait(false);
            await PosOnlinePolicySnapshot.SaveAsync(_factory, response?.Policy, generation).ConfigureAwait(false);
            await settings.SetStringIfGenerationCurrentAsync(
                LastCatalogErrorSettingKey,
                string.Empty,
                generation).ConfigureAwait(false);
            await settings.SetIntIfGenerationCurrentAsync(
                LastCatalogUpdatedProductsSettingKey,
                stats?.UpdatedProducts ?? 0,
                generation).ConfigureAwait(false);
            await settings.SetIntIfGenerationCurrentAsync(
                LastCatalogTombstonesReceivedSettingKey,
                stats?.TombstonesReceived ?? 0,
                generation).ConfigureAwait(false);
            await settings.SetIntIfGenerationCurrentAsync(
                LastCatalogTombstonesAppliedSettingKey,
                stats?.TombstonesApplied ?? 0,
                generation).ConfigureAwait(false);
            await settings.SetBoolIfGenerationCurrentAsync(
                LastCatalogHasMoreSettingKey,
                response != null && response.HasMore,
                generation).ConfigureAwait(false);
            await settings.SetStringIfGenerationCurrentAsync(
                LastCatalogVersionSettingKey,
                response?.CatalogVersion ?? string.Empty,
                generation).ConfigureAwait(false);
        }

        private static TimeSpan CatalogPullBackoff(int attempt)
        {
            return TimeSpan.FromMilliseconds(attempt <= 1 ? 250 : 750);
        }

        private static int ToSafeProgressCount(long value)
        {
            if (value <= 0) return 0;
            return value >= int.MaxValue ? int.MaxValue : (int)value;
        }

        private static void AddRemoteIds<T>(
            ICollection<string> target,
            T[] values,
            Func<T, string> selector)
        {
            foreach (var value in values ?? Array.Empty<T>())
            {
                target.Add(Normalize(selector(value)));
            }
        }

        private static void AddPriceEvidence(
            HashSet<string> seenPriceIds,
            PosCatalogPriceResponse[] prices,
            ref int duplicateRows)
        {
            foreach (var price in prices ?? Array.Empty<PosCatalogPriceResponse>())
            {
                var priceId = Normalize(price?.PriceId);
                if (priceId.Length > 0 && !seenPriceIds.Add(priceId))
                {
                    duplicateRows += 1;
                }
            }
        }

        private static bool CatalogSummariesEqual(
            PosCatalogSummaryResponse left,
            PosCatalogSummaryResponse right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null)
            {
                return false;
            }

            return left.Products == right.Products &&
                left.ActiveProducts == right.ActiveProducts &&
                left.Categories == right.Categories &&
                left.Suppliers == right.Suppliers &&
                left.Prices == right.Prices &&
                string.Equals(
                    Normalize(left.Checksum),
                    Normalize(right.Checksum),
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    Normalize(left.ChecksumAlgorithm),
                    Normalize(right.ChecksumAlgorithm),
                    StringComparison.OrdinalIgnoreCase);
        }

        private static string CatalogSummaryFingerprint(PosCatalogSummaryResponse summary)
        {
            if (summary == null)
            {
                return string.Empty;
            }

            var values = new[]
            {
                NullableLong(summary.Products),
                NullableLong(summary.ActiveProducts),
                NullableLong(summary.Categories),
                NullableLong(summary.Suppliers),
                NullableLong(summary.Prices),
                Normalize(summary.Checksum).ToLowerInvariant(),
                Normalize(summary.ChecksumAlgorithm).ToLowerInvariant()
            };
            var canonical = string.Join(
                "|",
                values.Select(value => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value));
            return CatalogShopStateRepository.FingerprintValue(canonical);
        }

        private static string NullableLong(long? value)
        {
            return value.HasValue
                ? value.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static bool CatalogHasMutations(PosCatalogPayload catalog)
        {
            return (catalog?.Products?.Length ?? 0) > 0 ||
                (catalog?.Categories?.Length ?? 0) > 0 ||
                (catalog?.Suppliers?.Length ?? 0) > 0 ||
                (catalog?.Prices?.Length ?? 0) > 0 ||
                (catalog?.Tombstones?.Products?.Length ?? 0) > 0 ||
                (catalog?.Tombstones?.Categories?.Length ?? 0) > 0 ||
                (catalog?.Tombstones?.Suppliers?.Length ?? 0) > 0;
        }

        private static bool IsRetryableCatalogPullCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return true;
            }

            return string.Equals(code, "timeout", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "network_error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "io_error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "db_failure", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCatalogCursorRejectionCode(string code)
        {
            var normalized = Normalize(code);
            return string.Equals(normalized, "cursor_expired", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "cursor_rejected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "catalog_cursor_expired", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "catalog_cursor_rejected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "sync_cursor_expired", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "sync_cursor_rejected", StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeCode(string code)
        {
            var normalized = (code ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return "failure";
            }

            var safe = new string(normalized
                .Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.')
                .Take(60)
                .ToArray());
            return safe.Length == 0 ? "failure" : safe;
        }

        private static string SafeId(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return "none";
            }

            return normalized.Length > 80 ? normalized.Substring(0, 80) : normalized;
        }

        private static IReadOnlyDictionary<string, string> BuildCategoryMap(
            PosCatalogCategoryResponse[] categories)
        {
            return (categories ?? Array.Empty<PosCatalogCategoryResponse>())
                .Where(row => !string.IsNullOrWhiteSpace(row?.CategoryId))
                .GroupBy(row => row.CategoryId.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => Normalize(group.First().Name),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static IReadOnlyDictionary<string, string> BuildSupplierMap(
            PosCatalogSupplierResponse[] suppliers)
        {
            return (suppliers ?? Array.Empty<PosCatalogSupplierResponse>())
                .Where(row => !string.IsNullOrWhiteSpace(row?.SupplierId))
                .GroupBy(row => row.SupplierId.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => Normalize(group.First().Name),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values ?? Array.Empty<string>())
            {
                var normalized = Normalize(value);
                if (normalized.Length > 0)
                {
                    return normalized;
                }
            }

            return string.Empty;
        }

        private static string NameFor(IReadOnlyDictionary<string, string> rows, string id)
        {
            var normalizedId = Normalize(id);
            if (normalizedId.Length == 0)
            {
                return string.Empty;
            }

            return rows.TryGetValue(normalizedId, out var name) ? name : string.Empty;
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static int ToInt(double? value)
        {
            var rounded = ToLong(value);

            if (rounded > int.MaxValue)
            {
                return int.MaxValue;
            }

            return (int)rounded;
        }

        private static long ToLong(double? value)
        {
            if (!value.HasValue || value.Value <= 0)
            {
                return 0;
            }

            if (value.Value >= long.MaxValue)
            {
                return long.MaxValue;
            }

            return (long)Math.Round(value.Value, MidpointRounding.AwayFromZero);
        }
    }

    public sealed class PosCatalogPullProgress
    {
        public int CategoriesReceived { get; set; }
        public bool HasMore { get; set; }
        public int Page { get; set; }
        public int PendingPricesApplied { get; set; }
        public string Phase { get; set; } = string.Empty;
        public int PricesApplied { get; set; }
        public int PricesQueued { get; set; }
        public int ProductsApplied { get; set; }
        public int SuppliersReceived { get; set; }
        public int TombstonesApplied { get; set; }
        public int TombstonesReceived { get; set; }

        public static PosCatalogPullProgress ForPhase(string phase)
        {
            return new PosCatalogPullProgress
            {
                Phase = phase ?? string.Empty
            };
        }

        public static PosCatalogPullProgress ForCatalogPage(
            int page,
            bool hasMore,
            int productsApplied,
            int categoriesReceived,
            int suppliersReceived,
            int pricesApplied,
            int pricesQueued,
            int pendingPricesApplied,
            int tombstonesReceived,
            int tombstonesApplied)
        {
            return new PosCatalogPullProgress
            {
                CategoriesReceived = categoriesReceived,
                HasMore = hasMore,
                Page = page,
                PendingPricesApplied = pendingPricesApplied,
                Phase = "catalog",
                PricesApplied = pricesApplied,
                PricesQueued = pricesQueued,
                ProductsApplied = productsApplied,
                SuppliersReceived = suppliersReceived,
                TombstonesApplied = tombstonesApplied,
                TombstonesReceived = tombstonesReceived
            };
        }
    }

    public sealed class PosCatalogPullOutcome
    {
        private PosCatalogPullOutcome(
            bool completed,
            string statusCode,
            bool authDenied,
            bool hasMore,
            int pagesProcessed,
            bool catalogSaleSafe,
            int productsApplied,
            int pricesApplied,
            int pricesQueued,
            int pendingPricesApplied)
        {
            AuthDenied = authDenied;
            CatalogSaleSafe = catalogSaleSafe;
            Completed = completed;
            HasMore = hasMore;
            PagesProcessed = pagesProcessed;
            PendingPricesApplied = pendingPricesApplied;
            PricesApplied = pricesApplied;
            PricesQueued = pricesQueued;
            ProductsApplied = productsApplied;
            StatusCode = string.IsNullOrWhiteSpace(statusCode) ? "failure" : statusCode;
        }

        public bool AuthDenied { get; }
        public bool CatalogSaleSafe { get; }
        public bool Completed { get; }
        public bool HasMore { get; }
        public int PagesProcessed { get; }
        public int PendingPricesApplied { get; }
        public int PricesApplied { get; }
        public int PricesQueued { get; }
        public int ProductsApplied { get; }
        public string StatusCode { get; }

        public static PosCatalogPullOutcome CompletedOk(
            int pagesProcessed,
            int productsApplied = 0,
            int pricesApplied = 0,
            int pricesQueued = 0,
            int pendingPricesApplied = 0)
        {
            return new PosCatalogPullOutcome(
                true,
                "completed",
                false,
                false,
                pagesProcessed,
                true,
                productsApplied,
                pricesApplied,
                pricesQueued,
                pendingPricesApplied);
        }

        public static PosCatalogPullOutcome Failure(
            string statusCode,
            bool authDenied,
            bool hasMore,
            int pagesProcessed,
            int productsApplied = 0,
            int pricesApplied = 0,
            int pricesQueued = 0,
            int pendingPricesApplied = 0)
        {
            return new PosCatalogPullOutcome(
                false,
                statusCode,
                authDenied,
                hasMore,
                pagesProcessed,
                false,
                productsApplied,
                pricesApplied,
                pricesQueued,
                pendingPricesApplied);
        }
    }
}
