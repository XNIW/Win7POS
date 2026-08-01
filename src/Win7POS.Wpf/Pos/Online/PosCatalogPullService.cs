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
        private const string LastCatalogErrorAtSettingKey = "pos.catalog.last_error_at";
        private const string LastCatalogErrorStageSettingKey = "pos.catalog.last_error_stage";
        private const string LastCatalogHttpStatusSettingKey = "pos.catalog.last_http_status";
        private const string LastCatalogIncidentIdSettingKey = "pos.catalog.last_incident_id";
        private const string LastCatalogPagesProcessedSettingKey = "pos.catalog.last_pages_processed";
        private const string LastCatalogRowsAppliedSettingKey = "pos.catalog.last_rows_applied";
        private const string LastCatalogRetryableSettingKey = "pos.catalog.last_retryable";
        private const string LastCatalogUpdatedProductsSettingKey = "pos.catalog.last_updated_products";
        private const string LastCatalogTombstonesReceivedSettingKey = "pos.catalog.last_tombstones_received";
        private const string LastCatalogTombstonesAppliedSettingKey = "pos.catalog.last_tombstones_applied";
        private const string LastCatalogHasMoreSettingKey = "pos.catalog.last_has_more";
        private const string LastCatalogVersionSettingKey = "pos.catalog.last_catalog_version";
        private const string CatalogBootstrapStatusSettingKey = "pos.catalog.bootstrap_status";
        private const string CatalogInitialCompletedAtSettingKey = "pos.catalog.initial_completed_at";
        private const string CatalogSaleSafeAtSettingKey = "pos.catalog.sale_safe_at";
        private const string BootstrapStatusCompleted = "completed";
        private const string BootstrapStatusCompletedWithWarnings = "completed_with_warnings";
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
                maxPages: MaxBackgroundCatalogPullPages,
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
                maxPages: MaxBackgroundCatalogPullPages,
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
                maxPages: MaxBackgroundCatalogPullPages,
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
            var incidentId = PosRuntimeDiagnostic.CreateLocalIncidentId();
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
            var authoritativeRunObserved = false;
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
                    var catalogBatchRepository = new RemoteCatalogBatchRepository(_factory);
                    using var catalogApplyRun = catalogBatchRepository.CreateRunContext();
                    var catalogReconciler = new CatalogFullRefreshReconciler(_factory);
                    var syncTimer = Stopwatch.StartNew();
                    var deltaPageLimit = Math.Max(1L, maxPages);
                    var totalStats = new CatalogApplyStats();
                    var displayWarnings = new CatalogWarningSummary();
                    PosCatalogPullResponse lastResponse = null;
                    PosOnlineResult<PosCatalogPullResponse> lastResult = null;
                    var pagesProcessed = 0L;
                    var fullRefresh = false;
                    var receivedFullLanes = new CatalogPaginationLaneCounts(0, 0, 0, 0);
                    var fullLaneConflictCode = string.Empty;
                    var fullStage = new CatalogFullResponseStageRepository(_factory);
                    CatalogFullResponseStageResourceBudget fullStageResourceBudget = null;
                    CatalogAuthoritativeStageEvidence authoritativeEvidence = null;
                    CatalogExactnessResult exactness = null;
                    var fullStageGeneration = Guid.NewGuid().ToString("N");
                    _logger.LogInfo(
                        "Catalog pull started: category=catalog.pull operation=catalog.pull stage=request" +
                        " incidentId=" + SafeId(incidentId) +
                        " bootstrap=" + BoolText(bootstrapRun));
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
                            "Catalog stale full response cleanup deferred: category=catalog.pull code=catalog_full_stage_cleanup_failed");
                    }
                    try
                    {
                        const int authoritativeCleanupBatchSize = 16384;
                        int removed;
                        do
                        {
                            removed = await catalogReconciler.ClearStaleAuthoritativeStagesAsync(
                                trustedSession.ShopId,
                                trustedSession.ShopCode,
                                fullStageGeneration,
                                authoritativeCleanupBatchSize).ConfigureAwait(false);
                        }
                        while (removed == authoritativeCleanupBatchSize);
                    }
                    catch (Exception)
                    {
                        _logger.LogWarning(
                            "Catalog stale authoritative stage cleanup deferred: category=catalog.pull code=catalog_authoritative_stage_cleanup_failed");
                    }
                    var fullStageBytes = 0L;
                    var fullStageStarted = false;
                    try
                    {
                    CatalogAuthoritativeDrainDecision authoritativeDrainPlan = null;
                    CatalogAuthoritativeProgressBudget authoritativeProgressBudget = null;
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

                    var page = 1L;
                    while (fullRefresh || page <= deltaPageLimit)
                    {
                        if (fullRefresh &&
                            authoritativeProgressBudget != null &&
                            syncTimer.ElapsedMilliseconds >
                            authoritativeProgressBudget.OverallTimeoutMilliseconds)
                        {
                            var timeoutCode =
                                CatalogAuthoritativeDrainBudgetPolicy.ProgressTimeoutCode;
                            await StoreCatalogFailureAsync(timeoutCode).ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(
                                    BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                            return PosCatalogPullOutcome.Failure(
                                timeoutCode,
                                false,
                                true,
                                pagesProcessed);
                        }

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
                        CatalogPullAttempt catalogAttempt;
                        using (var pageProgressCts =
                            authoritativeProgressBudget == null
                                ? null
                                : CancellationTokenSource.CreateLinkedTokenSource(
                                    cancellationToken))
                        {
                            if (pageProgressCts != null)
                            {
                                var remainingOverall = Math.Max(
                                    1L,
                                    authoritativeProgressBudget
                                        .OverallTimeoutMilliseconds -
                                    syncTimer.ElapsedMilliseconds);
                                var requestBudget = Math.Min(
                                    authoritativeProgressBudget
                                        .NoProgressTimeoutMilliseconds,
                                    remainingOverall);
                                pageProgressCts.CancelAfter(
                                    TimeSpan.FromMilliseconds(requestBudget));
                            }

                            try
                            {
                                catalogAttempt = await CatalogPullWithRetryAsync(
                                    client,
                                    request,
                                    executionContext,
                                    pageProgressCts?.Token ?? cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (
                                !cancellationToken.IsCancellationRequested &&
                                pageProgressCts != null &&
                                pageProgressCts.IsCancellationRequested)
                            {
                                var timeoutCode =
                                    CatalogAuthoritativeDrainBudgetPolicy
                                        .ProgressTimeoutCode;
                                await StoreCatalogFailureAsync(timeoutCode)
                                    .ConfigureAwait(false);
                                await StoreCatalogBootstrapStatusAsync(
                                        BootstrapStatusFailedRetryable)
                                    .ConfigureAwait(false);
                                return PosCatalogPullOutcome.Failure(
                                    timeoutCode,
                                    false,
                                    true,
                                    pagesProcessed);
                            }
                        }
                        var result = catalogAttempt.Result;
                        var attemptNumber = catalogAttempt.AttemptNumber;
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
                            catalogAttempt = await CatalogPullWithRetryAsync(
                                client,
                                request,
                                executionContext,
                                cancellationToken)
                                .ConfigureAwait(false);
                            result = catalogAttempt.Result;
                            attemptNumber = catalogAttempt.AttemptNumber;
                            resultCode = result.Value != null && !result.Value.Ok
                                ? FirstNonEmpty(result.Value.Code, "catalog_response_not_ok")
                                : result.Code;
                        }

                        if (!result.Success || result.Value == null || !result.Value.Ok || result.Value.Catalog == null)
                        {
                            var authenticationDenied = result.Denied ||
                                SharedAuthStopPolicy.IsAuthenticationDenied(resultCode);
                            var diagnostic = CreateCatalogFailureDiagnostic(
                                result,
                                resultCode,
                                authenticationDenied,
                                attemptNumber,
                                page,
                                pagesProcessed,
                                totalStats,
                                CountCatalogRows(receivedFullLanes),
                                incidentId,
                                syncTimer.ElapsedMilliseconds);
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

                            await StoreCatalogRuntimeDiagnosticAsync(diagnostic).ConfigureAwait(false);

                            _logger.LogWarning(
                                "Catalog pull summary: category=catalog.pull result=failure operation=" + diagnostic.Operation +
                                " stage=" + diagnostic.Stage +
                                " code=" + diagnostic.Code +
                                " httpStatus=" + (diagnostic.HttpStatus?.ToString() ?? "none") +
                                " contentType=" + SafeId(result.ResponseContentType) +
                                " responseLength=" + (result.ResponseLength?.ToString() ?? "none") +
                                " attempt=" + diagnostic.AttemptNumber.ToString() +
                                " page=" + (diagnostic.PageNumber?.ToString() ?? "none") +
                                " pagesProcessed=" + diagnostic.PagesProcessed.ToString() +
                                " rowsApplied=" + diagnostic.RowsApplied.ToString() +
                                " saleSafe=" + BoolText(diagnostic.CatalogSaleSafe) +
                                " retryable=" + BoolText(diagnostic.Retryable) +
                                " incidentId=" + SafeId(diagnostic.LocalIncidentId) +
                                " clientRequestId=" + SafeId(result.ClientRequestId) +
                                " serverRequestId=" + SafeId(result.ServerRequestId) +
                                " cfRay=" + SafeId(result.CfRay));
                            return PosCatalogPullOutcome.Failure(
                                SafeCode(resultCode),
                                authenticationDenied,
                                false,
                                pagesProcessed,
                                diagnostic: diagnostic);
                        }

                        var compatibilityAssessment = PosOnlineCompatibilityValidator.AssessCatalogPull(result.Value);
                        var compatibilityError = compatibilityAssessment.BlockingCode;
                        var response = compatibilityAssessment.RecoveredResponse ?? result.Value;
                        if (!string.IsNullOrWhiteSpace(compatibilityError))
                        {
                            if (string.Equals(
                                compatibilityError,
                                "catalog_product_row_invalid",
                                StringComparison.OrdinalIgnoreCase))
                            {
                                var invalidProduct = CatalogProductRowDiagnostic.FindFirstInvalid(
                                    response.Catalog?.Products);
                                _logger.LogWarning(
                                    "Catalog product row rejected before full-response staging: category=catalog.pull" +
                                    " page=" + page.ToString() +
                                    " lane=products" +
                                    " row=" + invalidProduct.Row.ToString() +
                                    " reason=" + SafeCode(invalidProduct.Reason) +
                                    " productIdLength=" + invalidProduct.ProductIdLength.ToString() +
                                    " barcodeLength=" + invalidProduct.BarcodeLength.ToString() +
                                    " productNameLength=" + invalidProduct.ProductNameLength.ToString() +
                                    " secondProductNameLength=" + invalidProduct.SecondProductNameLength.ToString() +
                                    " itemNumberLength=" + invalidProduct.ItemNumberLength.ToString() +
                                    " categoryIdLength=" + invalidProduct.CategoryIdLength.ToString() +
                                    " supplierIdLength=" + invalidProduct.SupplierIdLength.ToString() +
                                    " updatedAtLength=" + invalidProduct.UpdatedAtLength.ToString() +
                                    " priceClass=" + SafeCode(invalidProduct.PriceClass) +
                                    " purchasePriceClass=" + SafeCode(invalidProduct.PurchasePriceClass) +
                                    " stockQuantityClass=" + SafeCode(invalidProduct.StockQuantityClass));
                            }
                            await StoreCatalogFailureAsync(compatibilityError).ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                            return PosCatalogPullOutcome.Failure(
                                compatibilityError,
                                false,
                                false,
                                pagesProcessed);
                        }
                        displayWarnings.Add(compatibilityAssessment.WarningSummary);

                        var pageIsFullRefresh = string.Equals(
                            response.SyncMode,
                            "full_refresh",
                            StringComparison.OrdinalIgnoreCase);
                        authoritativeRunObserved =
                            authoritativeRunObserved || pageIsFullRefresh;
                        var fullSnapshotExpected = pageIsFullRefresh ||
                            requiresFullBoundary ||
                            requestCursor.Length == 0;
                        var paginationSafety = fullSnapshotExpected
                            ? null
                            : CatalogPaginationSafetyPolicy.EvaluateTerminalPage(
                                response,
                                CatalogPullPageLimit,
                                fullSnapshotExpected: false,
                                receivedBeforePage: receivedFullLanes,
                                pageAfterContinuation: page > 1);
                        if (paginationSafety != null && !paginationSafety.Allowed)
                        {
                            await StoreCatalogFailureAsync(paginationSafety.Code).ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                            _logger.LogWarning(
                                "Catalog full page rejected before reset/apply: category=catalog.pull code=" +
                                SafeCode(paginationSafety.Code) +
                                " page=" + page.ToString() +
                                " limit=" + CatalogPullPageLimit.ToString() +
                                " hasMore=" + response.HasMore.ToString());
                            return PosCatalogPullOutcome.Failure(
                                paginationSafety.Code,
                                false,
                                false,
                                pagesProcessed);
                        }

                        if (page == 1 && pageIsFullRefresh)
                        {
                            authoritativeDrainPlan =
                                CatalogAuthoritativeDrainBudgetPolicy.Calculate(
                                    response.CatalogSummary);
                            if (!authoritativeDrainPlan.Allowed)
                            {
                                await StoreCatalogFailureAsync(authoritativeDrainPlan.Code)
                                    .ConfigureAwait(false);
                                await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                    .ConfigureAwait(false);
                                return PosCatalogPullOutcome.Failure(
                                    authoritativeDrainPlan.Code,
                                    false,
                                    false,
                                    pagesProcessed);
                            }

                            authoritativeProgressBudget =
                                CatalogAuthoritativeDrainBudgetPolicy
                                    .CalculateProgressBudget(authoritativeDrainPlan);
                            if (!authoritativeProgressBudget.Allowed)
                            {
                                await StoreCatalogFailureAsync(
                                        authoritativeProgressBudget.Code)
                                    .ConfigureAwait(false);
                                await StoreCatalogBootstrapStatusAsync(
                                        BootstrapStatusFailedRetryable)
                                    .ConfigureAwait(false);
                                return PosCatalogPullOutcome.Failure(
                                    authoritativeProgressBudget.Code,
                                    false,
                                    response.HasMore,
                                    pagesProcessed);
                            }
                        }

                        var stagedResponseShopError = OutboxShopBinding.GetMismatchCode(
                            trustedSession.ShopId,
                            trustedSession.ShopCode,
                            response.Shop?.ShopId,
                            response.Shop?.ShopCode);
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

                        var responseCatalogVersion = Normalize(response.CatalogVersion);
                        var responseSummaryFingerprint = CatalogSummaryFingerprint(
                            response.CatalogSummary);
                        if (page == 1 && persistedDeltaChain.HasState)
                        {
                            var crossRunPinError = persistedDeltaChain.GetSnapshotMismatchCode(
                                responseCatalogVersion,
                                responseSummaryFingerprint,
                                response.CatalogSummary != null,
                                response.SyncMode);
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

                        if (snapshotSummaryPinned && response.CatalogSummary == null)
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

                        if (response.CatalogSummary != null)
                        {
                            if (!snapshotSummaryPinned)
                            {
                                snapshotSummary = response.CatalogSummary;
                                snapshotSummaryFingerprint = responseSummaryFingerprint;
                                snapshotSummaryPinned = true;
                            }
                            else if ((snapshotSummary != null &&
                                     !CatalogSummariesEqual(snapshotSummary, response.CatalogSummary)) ||
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

                        var responseCursor = Normalize(response.SyncCursor);
                        var responseCursorFingerprint = CatalogShopStateRepository.FingerprintValue(
                            responseCursor);
                        var sameCursor = string.Equals(
                            responseCursor,
                            Normalize(requestCursor),
                            StringComparison.Ordinal);
                        var allowsDeltaNoOpCursor =
                            !response.HasMore &&
                            !CatalogHasMutations(response.Catalog) &&
                            string.Equals(response.SyncMode, "delta", StringComparison.OrdinalIgnoreCase) &&
                            sameCursor;
                        var authoritativeCursor = pageIsFullRefresh || fullRefresh;
                        var responseCursorAlreadySeen = !authoritativeCursor &&
                            responseCursorFingerprint.Length > 0 &&
                            seenCursorFingerprints.Contains(responseCursorFingerprint);
                        if (responseCursor.Length == 0 ||
                            (!allowsDeltaNoOpCursor &&
                             (sameCursor ||
                              responseCursorFingerprint.Length == 0 ||
                              responseCursorAlreadySeen)))
                        {
                            var cursorProgressCode = authoritativeCursor
                                ? CatalogAuthoritativeDrainBudgetPolicy.CursorRepeatedCode
                                : "catalog_cursor_not_progressing";
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
                                response.HasMore,
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
                                response.HasMore,
                                pagesProcessed);
                        }

                        if (!authoritativeCursor && !allowsDeltaNoOpCursor)
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
                                // The first complete manifest already selected the sequential-lane
                                // active budget above. Tombstone continuation may expand it only through
                                // CatalogAuthoritativeDrainBudgetPolicy after bounded evidence is staged.
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
                            if (!fullStageStarted)
                            {
                                if (!fullStage.TryCreateResourceBudget(
                                    out fullStageResourceBudget,
                                    out var availableStageBytes,
                                    out var requiredStageBytes))
                                {
                                    var resourceCode =
                                        CatalogAuthoritativeDrainBudgetPolicy
                                            .InsufficientDiskCode;
                                    await StoreCatalogFailureAsync(resourceCode)
                                        .ConfigureAwait(false);
                                    await StoreCatalogBootstrapStatusAsync(
                                            BootstrapStatusFailedRetryable)
                                        .ConfigureAwait(false);
                                    _logger.LogWarning(
                                        "Catalog authoritative disk preflight rejected: category=catalog.pull" +
                                        " code=" + resourceCode +
                                        " availableBytes=" +
                                        Math.Max(0L, availableStageBytes).ToString() +
                                        " requiredBytes=" +
                                        requiredStageBytes.ToString());
                                    return PosCatalogPullOutcome.Failure(
                                        resourceCode,
                                        false,
                                        response.HasMore,
                                        pagesProcessed);
                                }
                                await fullStage.BeginAsync(fullStageGeneration).ConfigureAwait(false);
                                fullStageStarted = true;
                            }

                            try
                            {
                                fullStageBytes = await fullStage.AppendAsync(
                                    fullStageGeneration,
                                    page,
                                    responseCursorFingerprint,
                                    response,
                                    fullStageBytes,
                                    fullStageResourceBudget).ConfigureAwait(false);
                            }
                            catch (InvalidOperationException ex) when (
                                string.Equals(
                                    ex.Message,
                                    "catalog_full_stage_page_too_large",
                                    StringComparison.Ordinal) ||
                                string.Equals(
                                    ex.Message,
                                    CatalogAuthoritativeDrainBudgetPolicy
                                        .StageByteBudgetExceededCode,
                                    StringComparison.Ordinal) ||
                                string.Equals(
                                    ex.Message,
                                    CatalogAuthoritativeDrainBudgetPolicy
                                        .InsufficientDiskCode,
                                    StringComparison.Ordinal) ||
                                string.Equals(
                                    ex.Message,
                                    CatalogAuthoritativeDrainBudgetPolicy
                                        .NumericOverflowCode,
                                    StringComparison.Ordinal) ||
                                string.Equals(
                                    ex.Message,
                                    CatalogAuthoritativeDrainBudgetPolicy
                                        .CursorRepeatedCode,
                                    StringComparison.Ordinal))
                            {
                                var resourceCode = string.Equals(
                                    ex.Message,
                                    "catalog_full_stage_page_too_large",
                                    StringComparison.Ordinal)
                                    ? CatalogAuthoritativeDrainBudgetPolicy
                                        .StageByteBudgetExceededCode
                                    : ex.Message;
                                await StoreCatalogFailureAsync(resourceCode)
                                    .ConfigureAwait(false);
                                await StoreCatalogBootstrapStatusAsync(
                                        BootstrapStatusFailedRetryable)
                                    .ConfigureAwait(false);
                                _logger.LogWarning(
                                    "Catalog authoritative stage resource ceiling reached: category=catalog.pull" +
                                    " code=" + SafeCode(resourceCode) +
                                    " page=" + page.ToString() +
                                    " activeBudget=" +
                                    (authoritativeDrainPlan?.ActivePageBudget ?? 0L).ToString() +
                                    " stageBudgetBytes=" +
                                    (fullStageResourceBudget?.MaximumStagedBytes ?? 0L).ToString() +
                                    " stagedBytes=" + fullStageBytes.ToString());
                                return PosCatalogPullOutcome.Failure(
                                    resourceCode,
                                    false,
                                    response.HasMore,
                                    pagesProcessed);
                            }

                            var receivedBeforeFullPage = receivedFullLanes;
                            authoritativeEvidence = await catalogBatchRepository
                                .StageAuthoritativePageAsync(
                                    RemoteCatalogBatchMapper.BuildRemoteCatalogBatch(
                                        response,
                                        true,
                                        new CatalogAuthoritativeStagePage
                                        {
                                            FullRunId = fullStageGeneration,
                                            HasMore = response.HasMore,
                                            PageNumber = page
                                        }),
                                    cancellationToken,
                                    CreateCommitFence(
                                        trustedSession,
                                        binding.Epoch,
                                        committedCursor,
                                        committedMode,
                                        generation),
                                    loadCumulativeEvidence: !response.HasMore)
                                .ConfigureAwait(false);
                            if (authoritativeEvidence != null)
                            {
                                fullLaneConflictCode =
                                    authoritativeEvidence.ConflictCode;
                                if (fullLaneConflictCode.Length > 0)
                                {
                                    await StoreCatalogFailureAsync(
                                            fullLaneConflictCode)
                                        .ConfigureAwait(false);
                                    await StoreCatalogBootstrapStatusAsync(
                                            BootstrapStatusFailedRetryable)
                                        .ConfigureAwait(false);
                                    return PosCatalogPullOutcome.Failure(
                                        fullLaneConflictCode,
                                        false,
                                        response.HasMore,
                                        pagesProcessed);
                                }
                            }

                            var cumulativeFullLaneCounts =
                                authoritativeEvidence?.LaneCounts ??
                                receivedBeforeFullPage.Add(
                                    CatalogPaginationLaneCounts.FromPayload(
                                        response.Catalog));
                            var stagedPaginationSafety =
                                CatalogPaginationSafetyPolicy.EvaluateTerminalPage(
                                    response,
                                    CatalogPullPageLimit,
                                    fullSnapshotExpected: true,
                                    receivedBeforePage: receivedBeforeFullPage,
                                    cumulativeEvidence: cumulativeFullLaneCounts,
                                    pageAfterContinuation: page > 1);
                            if (!stagedPaginationSafety.Allowed)
                            {
                                await StoreCatalogFailureAsync(
                                        stagedPaginationSafety.Code)
                                    .ConfigureAwait(false);
                                await StoreCatalogBootstrapStatusAsync(
                                        BootstrapStatusFailedRetryable)
                                    .ConfigureAwait(false);
                                return PosCatalogPullOutcome.Failure(
                                    stagedPaginationSafety.Code,
                                    false,
                                    response.HasMore,
                                    pagesProcessed);
                            }

                            receivedFullLanes = cumulativeFullLaneCounts;
                            networkCursor = response.SyncCursor;
                            lastResponse = response;
                            lastResult = result;
                            pagesProcessed = page;
                            progress?.Report(PosCatalogPullProgress.ForCatalogPage(
                                page,
                                response.HasMore,
                                receivedFullLanes.Products,
                                receivedFullLanes.Categories,
                                receivedFullLanes.Suppliers,
                                receivedFullLanes.Prices,
                                0,
                                0,
                                checked(
                                    checked(receivedFullLanes.ProductTombstones +
                                        receivedFullLanes.CategoryTombstones) +
                                    receivedFullLanes.SupplierTombstones),
                                0));
                            _logger.LogInfo(
                                "Catalog full page staged: category=catalog.pull page=" + page.ToString() +
                                ", activeBudget=" +
                                (authoritativeDrainPlan?.ActivePageBudget ?? 0L).ToString() +
                                ", stageBudgetBytes=" +
                                (fullStageResourceBudget?.MaximumStagedBytes ?? 0L).ToString() +
                                ", categories=" + receivedFullLanes.Categories.ToString() +
                                ", suppliers=" + receivedFullLanes.Suppliers.ToString() +
                                ", products=" + receivedFullLanes.Products.ToString() +
                                ", prices=" + receivedFullLanes.Prices.ToString() +
                                ", tombstones=" +
                                checked(
                                    checked(receivedFullLanes.ProductTombstones +
                                        receivedFullLanes.CategoryTombstones) +
                                    receivedFullLanes.SupplierTombstones).ToString() +
                                ", stagedBytes=" + fullStageBytes.ToString() +
                                ", hasMore=" + response.HasMore.ToString());
                            if (!response.HasMore)
                            {
                                break;
                            }

                            try
                            {
                                page = checked(page + 1L);
                            }
                            catch (OverflowException)
                            {
                                var overflowCode =
                                    CatalogAuthoritativeDrainBudgetPolicy
                                        .NumericOverflowCode;
                                await StoreCatalogFailureAsync(overflowCode)
                                    .ConfigureAwait(false);
                                await StoreCatalogBootstrapStatusAsync(
                                        BootstrapStatusFailedRetryable)
                                    .ConfigureAwait(false);
                                return PosCatalogPullOutcome.Failure(
                                    overflowCode,
                                    false,
                                    true,
                                    pagesProcessed);
                            }

                            continue;
                        }

                        var applyStats = await ApplyCatalogAsync(
                            catalogApplyRun,
                            response,
                            fullRefresh,
                            null,
                            0,
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
                                response.HasMore,
                                pagesProcessed);
                        }

                        var deltaCheckpoint = fullRefresh
                            ? null
                            : new CatalogDeltaChainCheckpoint
                            {
                                CatalogVersion = snapshotCatalogVersion,
                                CursorFingerprints = seenCursorFingerprints.ToArray(),
                                HasMore = response.HasMore,
                                SummaryFingerprint = snapshotSummaryFingerprint,
                                SummaryPinned = snapshotSummaryPinned,
                                SyncMode = "delta"
                            };
                        await StoreCatalogDiagnosticsAsync(
                            response,
                            applyStats,
                            trustedSession,
                            binding.Epoch,
                            deltaCheckpoint,
                            fullRefresh,
                            committedCursor,
                            committedMode,
                            generation).ConfigureAwait(false);

                        networkCursor = response.SyncCursor;
                        committedCursor = response.SyncCursor;
                        committedMode = response.SyncMode;

                        lastResponse = response;
                        lastResult = result;
                        pagesProcessed = page;
                        progress?.Report(PosCatalogPullProgress.ForCatalogPage(
                            page,
                            response.HasMore,
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
                            ", deltaPageLimit=" + deltaPageLimit.ToString() +
                            ", limit=" + CatalogPullPageLimit.ToString() +
                            ", products=" + applyStats.UpdatedProducts.ToString() +
                            ", prices=" + applyStats.PriceRowsApplied.ToString() +
                            ", queuedPrices=" + applyStats.PriceRowsQueued.ToString() +
                            ", pendingPricesApplied=" + applyStats.PendingPriceRowsApplied.ToString() +
                            ", hasMore=" + response.HasMore.ToString() +
                            ", catalogVersion=" + SafeId(response.CatalogVersion));

                        if (!response.HasMore)
                        {
                            break;
                        }

                        try
                        {
                            page = checked(page + 1L);
                        }
                        catch (OverflowException)
                        {
                            var overflowCode =
                                CatalogAuthoritativeDrainBudgetPolicy.NumericOverflowCode;
                            await StoreCatalogFailureAsync(overflowCode)
                                .ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(
                                    BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                            return PosCatalogPullOutcome.Failure(
                                overflowCode,
                                false,
                                true,
                                pagesProcessed);
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
                            ", deltaPageLimit=" + deltaPageLimit.ToString() +
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
                        if (!fullStageStarted ||
                            authoritativeEvidence == null ||
                            pagesProcessed <= 0)
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

                        var stagedPreflightCode = await catalogReconciler
                            .ValidateStagedPreflightAsync(
                                fullStageGeneration,
                                snapshotSummary,
                                new CatalogExactnessRunContext
                                {
                                    CatalogVersion = FirstNonEmpty(
                                        snapshotCatalogVersion,
                                        lastResponse.CatalogVersion),
                                    DurationMilliseconds = syncTimer.ElapsedMilliseconds,
                                    HasMore = lastResponse.HasMore,
                                    Pages = pagesProcessed,
                                    SyncCursor = lastResponse.SyncCursor,
                                    SyncMode = lastResponse.SyncMode
                                },
                                CreateCommitFence(
                                    trustedSession,
                                    binding.Epoch,
                                    committedCursor,
                                    committedMode,
                                    generation),
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (stagedPreflightCode.Length > 0)
                        {
                            await StoreCatalogFailureAsync(stagedPreflightCode).ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                            return PosCatalogPullOutcome.Failure(
                                stagedPreflightCode,
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
                        try
                        {
                            await catalogApplyRun
                                .BeginAtomicFullRefreshAsync(cancellationToken)
                                .ConfigureAwait(false);
                            for (var stagedPageNumber = 1L;
                                 stagedPageNumber <= pagesProcessed;
                                 stagedPageNumber = checked(stagedPageNumber + 1L))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var stagedResponse = await catalogApplyRun
                                    .LoadFullStagePageAsync(
                                    fullStageGeneration,
                                    stagedPageNumber).ConfigureAwait(false);
                                var stagedStats = await ApplyCatalogAsync(
                                    catalogApplyRun,
                                    stagedResponse,
                                    true,
                                    fullStageGeneration,
                                    stagedPageNumber,
                                    trustedSession,
                                    binding.Epoch,
                                    committedCursor,
                                    committedMode,
                                    generation,
                                    cancellationToken,
                                    publishRevision: false).ConfigureAwait(false);
                                totalStats.Add(stagedStats);
                                if (stagedStats.RowsSkipped > 0)
                                {
                                    const string stagedRowsCode = "catalog_rows_not_fully_applied";
                                    await catalogApplyRun.AbortAtomicFullRefreshAsync()
                                        .ConfigureAwait(false);
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
                            }

                            syncTimer.Stop();
                            exactness = await catalogReconciler
                                .ReconcileAndVerifyWithinAtomicApplyAsync(
                                catalogApplyRun,
                                fullStageGeneration,
                                lastResponse.GeneratedAt,
                                snapshotSummary,
                                new CatalogExactnessRunContext
                                {
                                    CatalogVersion = FirstNonEmpty(snapshotCatalogVersion, lastResponse.CatalogVersion),
                                    DurationMilliseconds = syncTimer.ElapsedMilliseconds,
                                    HasMore = lastResponse.HasMore,
                                    Pages = pagesProcessed,
                                    PriceRowsReceived = totalStats.PriceRowsReceived,
                                    PriceRowsAccepted = checked(totalStats.PriceRowsApplied + totalStats.PriceRowsQueued),
                                    InvalidPriceRows = totalStats.PriceRowsSkipped,
                                    DuplicatePriceRows = 0,
                                    ProductRowsReceived = totalStats.UpdatedProducts,
                                    CategoryRowsReceived = receivedFullLanes.Categories,
                                    SupplierRowsReceived = receivedFullLanes.Suppliers,
                                    SyncCursor = lastResponse.SyncCursor,
                                    SyncMode = lastResponse.SyncMode,
                                    TombstonesReceived = totalStats.TombstonesReceived
                                },
                                CreateCommitFence(
                                    trustedSession,
                                    binding.Epoch,
                                    committedCursor,
                                    committedMode,
                                    generation),
                                cancellationToken).ConfigureAwait(false);
                            if (exactness.Status != CatalogCompletenessStatus.Verified ||
                                exactness.RepairRequired)
                            {
                                await catalogApplyRun.AbortAtomicFullRefreshAsync()
                                    .ConfigureAwait(false);
                                var exactnessCode = SafeCode(exactness.Code);
                                await catalogState.StoreExactnessAsync(
                                    trustedSession.ShopId,
                                    trustedSession.ShopCode,
                                    exactness,
                                    binding.Epoch,
                                    committedCursor,
                                    committedMode,
                                    generation).ConfigureAwait(false);
                                await StoreCatalogFailureAsync(exactnessCode).ConfigureAwait(false);
                                await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                    .ConfigureAwait(false);
                                _logger.LogWarning(
                                    "Catalog exactness rejected authoritative snapshot before live promotion: category=catalog.pull code=" +
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

                            await catalogApplyRun
                                .CommitAtomicFullRefreshAsync(
                                (connection, transaction) =>
                                    catalogState.FinalizeVerifiedFullRefreshWithinTransactionAsync(
                                        connection,
                                        transaction,
                                        trustedSession.ShopId,
                                        trustedSession.ShopCode,
                                        exactness,
                                        lastResponse.SyncCursor,
                                        lastResponse.GeneratedAt,
                                        binding.Epoch,
                                        committedCursor,
                                        committedMode,
                                        FirstNonEmpty(
                                            snapshotCatalogVersion,
                                            lastResponse.CatalogVersion),
                                        capturedImportAckGeneration,
                                        generation),
                                cancellationToken)
                                .ConfigureAwait(false);
                            committedCursor = lastResponse.SyncCursor;
                            committedMode = lastResponse.SyncMode;
                        }
                        catch
                        {
                            await catalogApplyRun.AbortAtomicFullRefreshAsync()
                                .ConfigureAwait(false);
                            throw;
                        }
                        CatalogEvents.AdvanceRevision();
                        try
                        {
                            await StoreCatalogDiagnosticsAsync(
                                lastResponse,
                                totalStats,
                                trustedSession,
                                binding.Epoch,
                                null,
                                true,
                                committedCursor,
                                committedMode,
                                generation).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                "Catalog post-commit diagnostics deferred: category=catalog.pull",
                                ex);
                        }
                    }

                    if (fullRefresh)
                    {
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

                    if (!fullRefresh)
                    {
                        var activeRemoteProducts = await new ProductRepository(_factory)
                            .CountActiveRemoteProductsAsync()
                            .ConfigureAwait(false);
                        if (activeRemoteProducts <= 0)
                        {
                            const string noProductsCode = "no_catalog_products";
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
                    }

                    if (!fullRefresh)
                    {
                        await StoreCatalogSaleSafeAsync(
                            lastResponse.GeneratedAt,
                            FirstNonEmpty(snapshotCatalogVersion, lastResponse.CatalogVersion),
                            trustedSession,
                            binding.Epoch,
                            committedCursor,
                            committedMode,
                            capturedImportAckGeneration,
                            generation).ConfigureAwait(false);
                    }
                    try
                    {
                        await new CatalogDisplayWarningRepository(_factory)
                            .StoreSuccessfulSyncAsync(
                                displayWarnings,
                                FirstNonEmpty(snapshotCatalogVersion, lastResponse.CatalogVersion),
                                generation).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Display-quality telemetry is explicitly non-blocking once
                        // the exact catalog and sale-safety state have committed.
                        _logger.LogWarning(
                            "Catalog display warning summary persistence deferred: category=catalog.pull",
                            ex);
                    }
                    try
                    {
                        await StoreCatalogBootstrapStatusAsync(displayWarnings.HasWarnings
                                ? BootstrapStatusCompletedWithWarnings
                                : BootstrapStatusCompleted)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            "Catalog post-commit bootstrap status deferred: category=catalog.pull",
                            ex);
                    }
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
                        try
                        {
                            await catalogReconciler.ClearAuthoritativeStageAsync(
                                fullStageGeneration,
                                trustedSession.ShopId,
                                trustedSession.ShopCode).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            _logger.LogWarning(
                                "Catalog authoritative stage cleanup deferred: category=catalog.pull code=catalog_authoritative_stage_cleanup_failed");
                        }
                    }
                    CatalogEvents.AdvanceRevision();
                    _logger.LogInfo(
                        "Catalog pull completed: category=catalog.pull products=" + totalStats.UpdatedProducts.ToString() +
                        ", prices=" + totalStats.PriceRowsApplied.ToString() +
                        ", queuedPrices=" + totalStats.PriceRowsQueued.ToString() +
                        ", pendingPricesApplied=" + totalStats.PendingPriceRowsApplied.ToString() +
                        ", pages=" + pagesProcessed.ToString() +
                        ", displayWarnings=" + displayWarnings.WarningCount.ToString() +
                        ", normalizedDisplayText=" + displayWarnings.NormalizedCount.ToString() +
                        ", removedDisplayControls=" + displayWarnings.RemovedControlCount.ToString() +
                        ", replacementCharacters=" + displayWarnings.ReplacementCharacterCount.ToString() +
                        ", displayFallbacks=" + displayWarnings.FallbackCount.ToString() +
                        ", limit=" + CatalogPullPageLimit.ToString() +
                        ", hasMore=" + lastResponse.HasMore.ToString() +
                        ", catalogVersionHash=" +
                        CatalogShopStateRepository.FingerprintValue(
                            lastResponse.CatalogVersion) +
                        " incidentId=" + SafeId(incidentId) +
                        " clientRequestId=" + SafeId(lastResult?.ClientRequestId) +
                        " serverRequestId=" + SafeId(lastResult?.ServerRequestId) +
                        " cfRay=" + SafeId(lastResult?.CfRay));
                    return PosCatalogPullOutcome.CompletedOk(
                        pagesProcessed,
                        totalStats.UpdatedProducts,
                        totalStats.PriceRowsApplied,
                        totalStats.PriceRowsQueued,
                        totalStats.PendingPriceRowsApplied,
                        displayWarningCount: displayWarnings.WarningCount);
                    }
                    finally
                    {
                        if (fullStageStarted)
                        {
                            try
                            {
                                await fullStage.ClearAsync(fullStageGeneration)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                _logger.LogWarning(
                                    "Catalog failed full stage cleanup deferred: category=catalog.pull code=catalog_full_stage_cleanup_failed");
                            }
                        }
                        try
                        {
                            await catalogReconciler.ClearAuthoritativeStageAsync(
                                fullStageGeneration,
                                trustedSession.ShopId,
                                trustedSession.ShopCode).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            _logger.LogWarning(
                                "Catalog authoritative stage cleanup deferred: category=catalog.pull code=catalog_authoritative_stage_cleanup_failed");
                        }
                    }
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
            catch (Exception ex) when (
                authoritativeRunObserved &&
                CatalogPersistenceFailureClassifier.IsSqliteFailure(ex))
            {
                var sqliteCode =
                    CatalogAuthoritativeDrainBudgetPolicy.SqliteFailureCode;
                await StoreCatalogFailureForGenerationAsync(
                    trustedSession,
                    capturedEpoch,
                    sqliteCode,
                    BootstrapStatusFailedRetryable).ConfigureAwait(false);

                _logger.LogWarning(
                    "Catalog authoritative SQLite operation failed: category=catalog.pull code=" +
                    sqliteCode,
                    ex);
                return PosCatalogPullOutcome.Failure(
                    sqliteCode,
                    false,
                    false,
                    0);
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

        private async Task<CatalogPullAttempt> CatalogPullWithRetryAsync(
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
                    return new CatalogPullAttempt(result, attempt);
                }

                await Task.Delay(CatalogPullBackoff(attempt), cancellationToken).ConfigureAwait(false);
            }

            return new CatalogPullAttempt(
                PosOnlineResult<PosCatalogPullResponse>.Failure(
                    "retry_exhausted",
                    "Catalog pull retry exhausted.",
                    false),
                MaxCatalogPullAttempts);
        }

        private sealed class CatalogPullAttempt
        {
            public CatalogPullAttempt(PosOnlineResult<PosCatalogPullResponse> result, int attemptNumber)
            {
                Result = result ?? PosOnlineResult<PosCatalogPullResponse>.Failure(
                    "catalog_request_missing",
                    "Catalog request result missing.",
                    false);
                AttemptNumber = Math.Max(0, attemptNumber);
            }

            public int AttemptNumber { get; }
            public PosOnlineResult<PosCatalogPullResponse> Result { get; }
        }

        private sealed class CatalogApplyStats
        {
            public long CategoryRowsReceived { get; set; }
            public long PendingPriceRowsApplied { get; set; }
            public long PriceRowsApplied { get; set; }
            public long PriceRowsQueued { get; set; }
            public long PriceRowsReceived { get; set; }
            public long PriceRowsSkipped { get; set; }
            public long RowsSkipped { get; set; }
            public long SupplierRowsReceived { get; set; }
            public long TombstonesApplied { get; set; }
            public long TombstonesReceived { get; set; }
            public long UpdatedProducts { get; set; }

            public void Add(CatalogApplyStats stats)
            {
                if (stats == null)
                {
                    return;
                }

                CategoryRowsReceived = checked(CategoryRowsReceived + stats.CategoryRowsReceived);
                PendingPriceRowsApplied = checked(PendingPriceRowsApplied + stats.PendingPriceRowsApplied);
                PriceRowsApplied = checked(PriceRowsApplied + stats.PriceRowsApplied);
                PriceRowsQueued = checked(PriceRowsQueued + stats.PriceRowsQueued);
                PriceRowsReceived = checked(PriceRowsReceived + stats.PriceRowsReceived);
                PriceRowsSkipped = checked(PriceRowsSkipped + stats.PriceRowsSkipped);
                RowsSkipped = checked(RowsSkipped + stats.RowsSkipped);
                SupplierRowsReceived = checked(SupplierRowsReceived + stats.SupplierRowsReceived);
                TombstonesApplied = checked(TombstonesApplied + stats.TombstonesApplied);
                TombstonesReceived = checked(TombstonesReceived + stats.TombstonesReceived);
                UpdatedProducts = checked(UpdatedProducts + stats.UpdatedProducts);
            }
        }

        private async Task<CatalogApplyStats> ApplyCatalogAsync(
            RemoteCatalogApplyRunContext applyRun,
            PosCatalogPullResponse response,
            bool authoritativeFullRefresh,
            string fullRunId,
            long fullPageNumber,
            PosTrustedDeviceSession trustedSession,
            long expectedEpoch,
            string expectedPreviousCursor,
            string expectedPreviousMode,
            OnlineSyncGeneration generation,
            CancellationToken cancellationToken,
            bool publishRevision = true)
        {
            var catalog = response.Catalog;
            var products = catalog.Products ?? Array.Empty<PosCatalogProductResponse>();
            var priceRows = catalog.Prices ?? Array.Empty<PosCatalogPriceResponse>();
            var tombstones = checked(
                checked((long)(catalog.Tombstones?.Products?.Length ?? 0) +
                    (catalog.Tombstones?.Categories?.Length ?? 0)) +
                (catalog.Tombstones?.Suppliers?.Length ?? 0));
            var batch = RemoteCatalogBatchMapper.BuildRemoteCatalogBatch(
                response,
                authoritativeFullRefresh,
                string.IsNullOrWhiteSpace(fullRunId)
                    ? null
                    : new CatalogAuthoritativeStagePage
                    {
                        FullRunId = fullRunId,
                        HasMore = response.HasMore,
                        PageNumber = fullPageNumber
                    });

            var applied = await applyRun
                .ApplyAsync(
                    batch,
                    cancellationToken,
                    CreateCommitFence(
                        trustedSession,
                        expectedEpoch,
                        expectedPreviousCursor,
                        expectedPreviousMode,
                        generation))
                .ConfigureAwait(false);

            // Product paging observes this monotonic process revision without invoking
            // UI subscribers from the background sync thread. Every committed page
            // invalidates cursors before another page can be requested.
            if (publishRevision)
            {
                CatalogEvents.AdvanceRevision();
            }
            // A committed catalog page may have supplied the remote identity
            // awaited by an offline-staged product image. Wake the image lane;
            // its durable dependency query remains the source of truth.
            PosOnlineSyncSignalBus.Signal(
                OnlineSyncLane.ProductImageOutbox,
                OnlineSyncLaneTrigger.RevisionChanged);

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

        private static RemoteCatalogCommitFence CreateCommitFence(
            PosTrustedDeviceSession trustedSession,
            long expectedEpoch,
            string expectedPreviousCursor,
            string expectedPreviousMode,
            OnlineSyncGeneration generation)
        {
            if (trustedSession == null) throw new ArgumentNullException(nameof(trustedSession));
            return new RemoteCatalogCommitFence
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

        private async Task StoreCatalogRuntimeDiagnosticAsync(PosRuntimeDiagnostic diagnostic)
        {
            if (diagnostic == null)
            {
                return;
            }

            try
            {
                var settings = new SettingsRepository(_factory);
                await settings.SetStringIfGenerationCurrentAsync(
                    LastCatalogErrorAtSettingKey,
                    diagnostic.OccurredAtUtc.ToString("o", CultureInfo.InvariantCulture),
                    _diagnosticGeneration).ConfigureAwait(false);
                await settings.SetStringIfGenerationCurrentAsync(
                    LastCatalogErrorStageSettingKey,
                    diagnostic.Stage,
                    _diagnosticGeneration).ConfigureAwait(false);
                await settings.SetStringIfGenerationCurrentAsync(
                    LastCatalogHttpStatusSettingKey,
                    diagnostic.HttpStatus.HasValue
                        ? diagnostic.HttpStatus.Value.ToString(CultureInfo.InvariantCulture)
                        : string.Empty,
                    _diagnosticGeneration).ConfigureAwait(false);
                await settings.SetStringIfGenerationCurrentAsync(
                    LastCatalogIncidentIdSettingKey,
                    diagnostic.SupportId,
                    _diagnosticGeneration).ConfigureAwait(false);
                await settings.SetStringIfGenerationCurrentAsync(
                    LastCatalogPagesProcessedSettingKey,
                    diagnostic.PagesProcessed.ToString(
                        CultureInfo.InvariantCulture),
                    _diagnosticGeneration).ConfigureAwait(false);
                await settings.SetStringIfGenerationCurrentAsync(
                    LastCatalogRowsAppliedSettingKey,
                    diagnostic.RowsApplied.ToString(CultureInfo.InvariantCulture),
                    _diagnosticGeneration).ConfigureAwait(false);
                await settings.SetBoolIfGenerationCurrentAsync(
                    LastCatalogRetryableSettingKey,
                    diagnostic.Retryable,
                    _diagnosticGeneration).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Catalog diagnostic persistence deferred: category=catalog.pull incidentId=" +
                    SafeId(diagnostic.LocalIncidentId),
                    ex);
            }
        }

        private async Task ClearCatalogRuntimeDiagnosticAsync(
            SettingsRepository settings,
            OnlineSyncGeneration generation)
        {
            try
            {
                await settings.SetStringIfGenerationCurrentAsync(
                    LastCatalogErrorAtSettingKey,
                    string.Empty,
                    generation).ConfigureAwait(false);
                await settings.SetStringIfGenerationCurrentAsync(
                    LastCatalogErrorStageSettingKey,
                    string.Empty,
                    generation).ConfigureAwait(false);
                await settings.SetStringIfGenerationCurrentAsync(
                    LastCatalogHttpStatusSettingKey,
                    string.Empty,
                    generation).ConfigureAwait(false);
                await settings.SetStringIfGenerationCurrentAsync(
                    LastCatalogIncidentIdSettingKey,
                    string.Empty,
                    generation).ConfigureAwait(false);
                await settings.SetStringIfGenerationCurrentAsync(
                    LastCatalogPagesProcessedSettingKey,
                    "0",
                    generation).ConfigureAwait(false);
                await settings.SetStringIfGenerationCurrentAsync(
                    LastCatalogRowsAppliedSettingKey,
                    "0",
                    generation).ConfigureAwait(false);
                await settings.SetBoolIfGenerationCurrentAsync(
                    LastCatalogRetryableSettingKey,
                    false,
                    generation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Catalog diagnostic clear deferred: category=catalog.pull",
                    ex);
            }
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
            await ClearCatalogRuntimeDiagnosticAsync(settings, generation).ConfigureAwait(false);
            await settings.SetStringIfGenerationCurrentAsync(
                LastCatalogUpdatedProductsSettingKey,
                (stats?.UpdatedProducts ?? 0).ToString(CultureInfo.InvariantCulture),
                generation).ConfigureAwait(false);
            await settings.SetStringIfGenerationCurrentAsync(
                LastCatalogTombstonesReceivedSettingKey,
                (stats?.TombstonesReceived ?? 0).ToString(CultureInfo.InvariantCulture),
                generation).ConfigureAwait(false);
            await settings.SetStringIfGenerationCurrentAsync(
                LastCatalogTombstonesAppliedSettingKey,
                (stats?.TombstonesApplied ?? 0).ToString(CultureInfo.InvariantCulture),
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

        private static PosRuntimeDiagnostic CreateCatalogFailureDiagnostic(
            PosOnlineResult<PosCatalogPullResponse> result,
            string code,
            bool authenticationDenied,
            int attemptNumber,
            long pageNumber,
            long pagesProcessed,
            CatalogApplyStats stats,
            long stagedRowsReceived,
            string incidentId,
            long elapsedMilliseconds)
        {
            var normalizedCode = SafeCode(code);
            var stage = authenticationDenied
                ? "authentication"
                : result?.HttpStatus.HasValue == true
                    ? "server_response"
                    : IsDeserializationCode(normalizedCode)
                        ? "deserialization"
                        : IsNetworkCode(normalizedCode)
                            ? "network"
                            : "request";
            var appliedRowsReceived = checked(
                checked(
                    checked(
                        checked((stats?.UpdatedProducts ?? 0) +
                            (stats?.CategoryRowsReceived ?? 0)) +
                        (stats?.SupplierRowsReceived ?? 0)) +
                    (stats?.PriceRowsReceived ?? 0)) +
                (stats?.TombstonesReceived ?? 0));
            var rowsReceived = checked(
                appliedRowsReceived +
                Math.Max(0L, stagedRowsReceived));
            var rowsApplied = checked(
                checked(
                    checked((stats?.UpdatedProducts ?? 0) +
                        (stats?.PriceRowsApplied ?? 0)) +
                    (stats?.PendingPriceRowsApplied ?? 0)) +
                (stats?.TombstonesApplied ?? 0));
            var summary = stage == "server_response"
                ? "Server response stopped the catalog pull."
                : stage == "network"
                    ? "Network request did not complete the catalog pull."
                    : stage == "deserialization"
                        ? "Catalog response could not be read safely."
                        : stage == "authentication"
                            ? "Catalog authorization is no longer valid."
                            : "Catalog pull did not complete.";

            return new PosRuntimeDiagnostic(
                "catalog.pull",
                stage,
                normalizedCode,
                result?.HttpStatus,
                !authenticationDenied && IsRetryableCatalogPullCode(normalizedCode),
                authenticationDenied,
                attemptNumber,
                pageNumber,
                pagesProcessed,
                rowsReceived,
                rowsApplied,
                false,
                false,
                result?.ClientRequestId,
                result?.ServerRequestId,
                result?.CfRay,
                incidentId,
                DateTimeOffset.UtcNow,
                elapsedMilliseconds,
                string.Empty,
                summary);
        }

        private static long CountCatalogRows(
            CatalogPaginationLaneCounts lanes)
        {
            if (lanes == null)
            {
                return 0L;
            }

            return checked(
                checked(
                    checked(
                        checked(
                            checked(
                                checked(lanes.Products + lanes.Categories) +
                                lanes.Suppliers) +
                            lanes.Prices) +
                        lanes.ProductTombstones) +
                    lanes.CategoryTombstones) +
                lanes.SupplierTombstones);
        }

        private static bool IsDeserializationCode(string code)
        {
            return string.Equals(code, "invalid_response", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "response_too_large", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNetworkCode(string code)
        {
            return string.Equals(code, "timeout", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "network_error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "io_error", StringComparison.OrdinalIgnoreCase);
        }

        private static string BoolText(bool value)
        {
            return value ? "yes" : "no";
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
            var redacted = PosTechnicalIdentifier.Redact(value);
            return string.IsNullOrWhiteSpace(redacted) ? "none" : redacted;
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
        public long CategoriesReceived { get; set; }
        public bool HasMore { get; set; }
        public long Page { get; set; }
        public long PendingPricesApplied { get; set; }
        public string Phase { get; set; } = string.Empty;
        public long PricesApplied { get; set; }
        public long PricesQueued { get; set; }
        public long ProductsApplied { get; set; }
        public long SuppliersReceived { get; set; }
        public long TombstonesApplied { get; set; }
        public long TombstonesReceived { get; set; }

        public static PosCatalogPullProgress ForPhase(string phase)
        {
            return new PosCatalogPullProgress
            {
                Phase = phase ?? string.Empty
            };
        }

        public static PosCatalogPullProgress ForCatalogPage(
            long page,
            bool hasMore,
            long productsApplied,
            long categoriesReceived,
            long suppliersReceived,
            long pricesApplied,
            long pricesQueued,
            long pendingPricesApplied,
            long tombstonesReceived,
            long tombstonesApplied)
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
            long pagesProcessed,
            bool catalogSaleSafe,
            long productsApplied,
            long pricesApplied,
            long pricesQueued,
            long pendingPricesApplied,
            long displayWarningCount,
            PosRuntimeDiagnostic diagnostic)
        {
            AuthDenied = authDenied;
            CatalogSaleSafe = catalogSaleSafe;
            Completed = completed;
            DisplayWarningCount = Math.Max(0, displayWarningCount);
            HasMore = hasMore;
            PagesProcessed = pagesProcessed;
            PendingPricesApplied = pendingPricesApplied;
            PricesApplied = pricesApplied;
            PricesQueued = pricesQueued;
            ProductsApplied = productsApplied;
            StatusCode = string.IsNullOrWhiteSpace(statusCode) ? "failure" : statusCode;
            Diagnostic = diagnostic;
        }

        public bool AuthDenied { get; }
        public bool CatalogSaleSafe { get; }
        public bool Completed { get; }
        public long DisplayWarningCount { get; }
        public PosRuntimeDiagnostic Diagnostic { get; }
        public bool HasMore { get; }
        public long PagesProcessed { get; }
        public long PendingPricesApplied { get; }
        public long PricesApplied { get; }
        public long PricesQueued { get; }
        public long ProductsApplied { get; }
        public string StatusCode { get; }

        public static PosCatalogPullOutcome CompletedOk(
            long pagesProcessed,
            long productsApplied = 0,
            long pricesApplied = 0,
            long pricesQueued = 0,
            long pendingPricesApplied = 0,
            long displayWarningCount = 0,
            PosRuntimeDiagnostic diagnostic = null)
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
                pendingPricesApplied,
                displayWarningCount,
                diagnostic);
        }

        public static PosCatalogPullOutcome Failure(
            string statusCode,
            bool authDenied,
            bool hasMore,
            long pagesProcessed,
            long productsApplied = 0,
            long pricesApplied = 0,
            long pricesQueued = 0,
            long pendingPricesApplied = 0,
            PosRuntimeDiagnostic diagnostic = null)
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
                pendingPricesApplied,
                0,
                diagnostic);
        }
    }
}
