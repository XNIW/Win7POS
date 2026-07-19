using System;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Online
{
    public sealed class PosStartOfDaySyncService
    {
        internal static readonly TimeSpan StartOfDayTotalTimeout = TimeSpan.FromSeconds(28);
        internal static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(4);
        internal static readonly TimeSpan SalesSyncTimeout = TimeSpan.FromSeconds(8);
        internal static readonly TimeSpan CatalogImportSyncTimeout = TimeSpan.FromSeconds(6);
        internal static readonly TimeSpan CatalogDeltaTimeout = TimeSpan.FromSeconds(12);

        private const string CatalogBootstrapStatusSettingKey = "pos.catalog.bootstrap_status";
        private const string LastCatalogErrorSettingKey = "pos.catalog.last_error";
        private const string LastSalesErrorSettingKey = "pos.sales_sync.last_error";
        private const string RestoreNeedsReviewSettingKey = "pos.restore.needs_sync_review";

        private readonly SqliteConnectionFactory _factory;
        private readonly FileLogger _logger;
        private readonly PosTrustedDeviceStore _store;

        public PosStartOfDaySyncService(SqliteConnectionFactory factory)
            : this(factory, new PosTrustedDeviceStore(), new FileLogger("PosStartOfDaySyncService"))
        {
        }

        internal PosStartOfDaySyncService(
            SqliteConnectionFactory factory,
            PosTrustedDeviceStore store,
            FileLogger logger)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<StartOfDaySyncResult> RunAsync(
            IProgress<PosStartOfDaySyncProgress> progress,
            CancellationToken cancellationToken)
        {
            var result = new StartOfDaySyncResult();
            var settings = new SettingsRepository(_factory);
            var sales = new SaleRepository(_factory);

            Report(progress, "database", "active", T("startOfDay.stepDatabase"));
            result.CatalogSaleSafe = await PosCatalogPullService
                .IsCatalogSaleSafeAsync(_factory)
                .ConfigureAwait(false);
            result.RestoreNeedsReview = await settings
                .GetBoolAsync(RestoreNeedsReviewSettingKey)
                .ConfigureAwait(false) == true;
            await RefreshOutboxAsync(result, sales, _factory).ConfigureAwait(false);

            if (!result.CatalogSaleSafe && !result.RestoreNeedsReview)
            {
                return Block(result, "catalog_not_sale_safe", T("startOfDay.blockCatalogNotSafe"), "database", progress);
            }

            if (result.RestoreNeedsReview && HasAnyUnresolvedOutbox(result))
            {
                return Block(result, "restore_needs_review", T("startOfDay.blockRestoreReview"), "database", progress);
            }

            if (result.BlockedSales > 0)
            {
                return Block(result, "sales_blocked", T("startOfDay.blockSalesBlocked"), "outbox", progress);
            }

            if (result.BlockedCatalogImports > 0)
            {
                return Block(result, "catalog_import_blocked", T("startOfDay.blockCatalogImportBlocked"), "outbox", progress);
            }

            if (!result.RestoreNeedsReview &&
                await HasStoredAuthDeniedAsync(settings).ConfigureAwait(false))
            {
                return Block(result, "auth_denied", T("startOfDay.blockAuthDenied"), "session", progress);
            }

            Report(progress, "database", "ok", T("startOfDay.stepDatabase"));
            Report(progress, "session", "active", T("startOfDay.stepSession"));

            if (!PosAdminWebOptions.TryLoad(out var options, out _))
            {
                if (result.RestoreNeedsReview)
                {
                    return Block(result, "restore_needs_review", T("startOfDay.blockRestoreReview"), "session", progress);
                }

                return ContinueLocal(
                    result,
                    T("startOfDay.noServerConfig"),
                    shouldContinueInBackground: false,
                    requiresOperatorAction: false,
                    "session",
                    progress);
            }

            if (!_store.TryRead(out var trustedSession))
            {
                if (result.RestoreNeedsReview)
                {
                    return Block(result, "restore_needs_review", T("startOfDay.blockRestoreReview"), "session", progress);
                }

                return ContinueLocal(
                    result,
                    T("startOfDay.noTrustedSession"),
                    shouldContinueInBackground: false,
                    requiresOperatorAction: false,
                    "session",
                    progress);
            }

            var heartbeat = await TryHeartbeatAsync(options, trustedSession, result, settings, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!result.CanOpenPos && result.RequiresOperatorAction)
            {
                return result;
            }

            if (!_store.TryRead(out var refreshedTrustedSession) ||
                !IsSameTrustedSession(trustedSession, refreshedTrustedSession))
            {
                return Block(
                    result,
                    "trusted_session_changed",
                    T("startOfDay.blockAuthDenied"),
                    "session",
                    progress);
            }

            trustedSession = refreshedTrustedSession;
            if (!heartbeat.Success)
            {
                if (result.RestoreNeedsReview)
                {
                    return Block(result, "restore_needs_review", T("startOfDay.blockRestoreReview"), "session", progress);
                }
            }

            if (result.RestoreNeedsReview)
            {
                return await RunRestoreReconciliationAsync(result, options, progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            Report(progress, "outbox", "active", T("startOfDay.stepOutbox"));
            await RefreshOutboxAsync(result, sales, _factory).ConfigureAwait(false);
            if (result.BlockedSales > 0)
            {
                return Block(result, "sales_blocked", T("startOfDay.blockSalesBlocked"), "outbox", progress);
            }

            if (result.BlockedCatalogImports > 0)
            {
                return Block(result, "catalog_import_blocked", T("startOfDay.blockCatalogImportBlocked"), "outbox", progress);
            }

            Report(progress, "outbox", "ok", T("startOfDay.stepOutbox"));
            Report(progress, "sales", "active", T("startOfDay.stepSales"));
            var salesDrain = await TrySalesSyncAsync(options, result, cancellationToken).ConfigureAwait(false);
            if (salesDrain.AuthenticationDenied)
            {
                return Block(result, "auth_denied", T("startOfDay.blockAuthDenied"), "sales", progress);
            }

            await RefreshOutboxAsync(result, sales, _factory).ConfigureAwait(false);
            var salesDrainDecision = StartOfDaySalesDrainPolicy.Evaluate(
                result.PendingSales,
                result.RetrySales,
                result.InProgressSales,
                result.BlockedSales);
            var salesComplete = salesDrainDecision == StartOfDaySalesDrainDecision.Complete;
            if (await HasStoredAuthDeniedAsync(settings).ConfigureAwait(false))
            {
                return Block(result, "auth_denied", T("startOfDay.blockAuthDenied"), "sales", progress);
            }

            if (salesDrainDecision == StartOfDaySalesDrainDecision.Blocked)
            {
                return Block(result, "sales_blocked", T("startOfDay.blockSalesBlocked"), "sales", progress);
            }

            Report(progress, "sales", salesComplete ? "ok" : "warning", T("startOfDay.stepSales"));
            Report(progress, "catalog-import", "active", T("startOfDay.stepCatalogImport"));
            var catalogImportDrain = await TryCatalogImportSyncAsync(
                options,
                trustedSession,
                result,
                cancellationToken).ConfigureAwait(false);
            if (catalogImportDrain.AuthenticationDenied)
            {
                return Block(result, "auth_denied", T("startOfDay.blockAuthDenied"), "catalog-import", progress);
            }

            await RefreshOutboxAsync(result, sales, _factory).ConfigureAwait(false);
            var catalogImportComplete = result.PendingCatalogImports == 0 &&
                result.RetryCatalogImports == 0;
            if (await HasStoredAuthDeniedAsync(settings).ConfigureAwait(false))
            {
                return Block(result, "auth_denied", T("startOfDay.blockAuthDenied"), "catalog-import", progress);
            }

            if (result.BlockedCatalogImports > 0)
            {
                return Block(result, "catalog_import_blocked", T("startOfDay.blockCatalogImportBlocked"), "catalog-import", progress);
            }

            Report(progress, "catalog-import", catalogImportComplete ? "ok" : "warning", T("startOfDay.stepCatalogImport"));
            if (!heartbeat.Success)
            {
                return ContinueLocal(
                    result,
                    T("startOfDay.continueBackground"),
                    shouldContinueInBackground: true,
                    requiresOperatorAction: false,
                    "catalog",
                    progress);
            }

            Report(progress, "catalog", "active", T("startOfDay.stepCatalog"));
            var postImportCatalogState = new CatalogShopStateRepository(_factory);
            var postImportBinding = await postImportCatalogState.EnsureAndLoadCursorAsync(
                trustedSession.ShopId,
                trustedSession.ShopCode).ConfigureAwait(false);
            var postImportRevision = postImportBinding.IsValid
                ? await postImportCatalogState.LoadRevisionStateAsync(
                    trustedSession.ShopId,
                    trustedSession.ShopCode,
                    postImportBinding.Epoch).ConfigureAwait(false)
                : new CatalogRevisionState(
                    heartbeat.Decision?.ObservedRevision,
                    heartbeat.CommittedRevision,
                    string.Empty,
                    string.Empty,
                    importAckStateValid: false);
            var postImportHeartbeatDecision = CatalogHeartbeatPolicy.Evaluate(
                heartbeat.Decision?.ObservedRevision,
                heartbeat.CatalogChangesAvailable,
                heartbeat.Decision?.NextPollAfterSeconds,
                postImportRevision.CommittedRevision,
                fullOrRepairRequired: false,
                partialCursorPending: heartbeat.PartialCursorPending,
                manualTrigger: false,
                catalogImportAckPending: postImportRevision.ImportAckReconciliationPending);
            var catalogSkipConfirmed = postImportHeartbeatDecision.ShouldSkipCatalogPull &&
                postImportBinding.IsValid &&
                await postImportCatalogState.TryConfirmCatalogUnchangedAsync(
                    trustedSession.ShopId,
                    trustedSession.ShopCode,
                    postImportBinding.Epoch,
                    postImportHeartbeatDecision.ObservedRevision,
                    postImportRevision.CommittedRevision,
                    postImportRevision.ImportAckGeneration,
                    clearStaleError: catalogImportComplete).ConfigureAwait(false);
            var catalogOutcome = catalogSkipConfirmed
                ? PosCatalogPullOutcome.CompletedOk(0)
                : await TryCatalogDeltaAsync(
                    options,
                    trustedSession,
                    result,
                    cancellationToken).ConfigureAwait(false);
            if (catalogOutcome.AuthDenied)
            {
                return Block(result, "auth_denied", T("startOfDay.blockAuthDenied"), "catalog", progress);
            }

            var catalogComplete = catalogSkipConfirmed || catalogOutcome.Completed;
            if (catalogSkipConfirmed)
            {
                result.CatalogStatus = "unchanged";
                await RecordHeartbeatSkipBestEffortAsync(
                    trustedSession,
                    postImportBinding.Cursor,
                    postImportHeartbeatDecision.Code).ConfigureAwait(false);
            }
            if (await HasStoredAuthDeniedAsync(settings).ConfigureAwait(false))
            {
                return Block(result, "auth_denied", T("startOfDay.blockAuthDenied"), "catalog", progress);
            }

            result.CanOpenPos = true;
            result.RequiresOperatorAction = false;
            result.ShouldContinueInBackground = !salesComplete || !catalogImportComplete || !catalogComplete;
            result.BlockingReason = string.Empty;
            result.CatalogStatus = catalogSkipConfirmed
                ? "unchanged"
                : catalogComplete ? "completed" : "background";
            result.StatusMessage = result.ShouldContinueInBackground
                ? T("startOfDay.continueBackground")
                : T("startOfDay.complete");

            Report(progress, "catalog", catalogComplete ? "ok" : "warning", T("startOfDay.stepCatalog"));
            Report(progress, "complete", "ok", result.StatusMessage);
            return result;
        }

        private async Task<StartOfDaySyncResult> RunRestoreReconciliationAsync(
            StartOfDaySyncResult result,
            PosAdminWebOptions options,
            IProgress<PosStartOfDaySyncProgress> progress,
            CancellationToken cancellationToken)
        {
            Report(progress, "outbox", "ok", T("startOfDay.stepOutbox"));
            Report(progress, "sales", "ok", T("startOfDay.stepSales"));
            Report(progress, "catalog-import", "ok", T("startOfDay.stepCatalogImport"));
            Report(progress, "catalog", "active", T("startOfDay.stepCatalog"));

            var catalogComplete = await TryRestoreCatalogReconciliationAsync(options, result, cancellationToken)
                .ConfigureAwait(false);
            if (!catalogComplete)
            {
                return Block(result, "restore_needs_review", T("startOfDay.blockRestoreReview"), "catalog", progress);
            }

            var review = await new RestoreShopSafetyRepository(_factory)
                .CompleteReviewAsync()
                .ConfigureAwait(false);
            if (!review.IsValid)
            {
                _logger.LogWarning("Restore reconciliation did not complete: code=" + SafeCode(review.Code));
                return Block(result, review.Code, T("startOfDay.blockRestoreReview"), "catalog", progress);
            }

            result.CatalogSaleSafe = await PosCatalogPullService
                .IsCatalogSaleSafeAsync(_factory)
                .ConfigureAwait(false);
            if (!result.CatalogSaleSafe)
            {
                return Block(result, "catalog_not_sale_safe", T("startOfDay.blockCatalogNotSafe"), "catalog", progress);
            }

            result.RestoreNeedsReview = false;
            result.CanOpenPos = true;
            result.RequiresOperatorAction = false;
            result.ShouldContinueInBackground = false;
            result.BlockingReason = string.Empty;
            result.CatalogStatus = "completed";
            result.StatusMessage = T("startOfDay.complete");
            Report(progress, "catalog", "ok", T("startOfDay.stepCatalog"));
            Report(progress, "complete", "ok", result.StatusMessage);
            return result;
        }

        private async Task<bool> TryRestoreCatalogReconciliationAsync(
            PosAdminWebOptions options,
            StartOfDaySyncResult result,
            CancellationToken cancellationToken)
        {
            using (var cts = CreateStepCts(CatalogDeltaTimeout, cancellationToken))
            {
                try
                {
                    var outcome = await new PosCatalogPullService(_factory)
                        .TryPullInitialCatalogAsync(options, cts.Token)
                        .ConfigureAwait(false);
                    if (!outcome.Completed)
                    {
                        result.LastTransientError = SafeCode(outcome.StatusCode);
                    }

                    return outcome.Completed;
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    result.LastTransientError = "restore_catalog_timeout";
                    return false;
                }
                catch (Exception ex)
                {
                    result.LastTransientError = "restore_catalog_exception";
                    _logger.LogWarning("Restore catalog reconciliation failed.", ex);
                    return false;
                }
            }
        }

        private async Task<StartOfDayHeartbeatResult> TryHeartbeatAsync(
            PosAdminWebOptions options,
            PosTrustedDeviceSession trustedSession,
            StartOfDaySyncResult result,
            SettingsRepository settings,
            IProgress<PosStartOfDaySyncProgress> progress,
            CancellationToken cancellationToken)
        {
            using (var client = new PosAdminWebClient(options))
            using (var cts = CreateStepCts(HeartbeatTimeout, cancellationToken))
            {
                try
                {
                    var catalogState = new CatalogShopStateRepository(_factory);
                    var binding = await catalogState.EnsureAndLoadCursorAsync(
                        trustedSession.ShopId,
                        trustedSession.ShopCode).ConfigureAwait(false);
                    if (!binding.IsValid)
                    {
                        result.LastTransientError = binding.Code;
                        return StartOfDayHeartbeatResult.Failed();
                    }

                    var revision = await catalogState.LoadRevisionStateAsync(
                        trustedSession.ShopId,
                        trustedSession.ShopCode,
                        binding.Epoch).ConfigureAwait(false);
                    var delta = await catalogState.LoadDeltaChainAsync(
                        trustedSession.ShopId,
                        trustedSession.ShopCode,
                        binding.Epoch).ConfigureAwait(false);
                    var response = await client.HeartbeatAsync(new PosHeartbeatRequest
                    {
                        AppVersion = typeof(PosStartOfDaySyncService).Assembly.GetName().Version?.ToString(),
                        CatalogRevision = revision.CommittedRevision,
                        DeviceToken = trustedSession.DeviceToken,
                        PosSessionId = trustedSession.PosSessionId,
                        SessionToken = trustedSession.SessionToken,
                        ShopDeviceId = trustedSession.ShopDeviceId,
                    }, cts.Token).ConfigureAwait(false);

                    if (response.Success && response.Value != null && response.Value.Ok)
                    {
                        _store.SaveHeartbeat(trustedSession, response.Value);
                        var decision = CatalogHeartbeatPolicy.Evaluate(
                            response.Value.CatalogRevision,
                            response.Value.CatalogChangesAvailable,
                            response.Value.NextPollAfterSeconds,
                            revision.CommittedRevision,
                            fullOrRepairRequired: result.RestoreNeedsReview,
                            partialCursorPending: delta.IsValid && delta.HasState,
                            manualTrigger: false,
                            catalogImportAckPending: revision.ImportAckReconciliationPending);
                        if (decision.ObservedRevision.Length > 0)
                        {
                            await catalogState.StoreObservedRevisionAsync(
                                trustedSession.ShopId,
                                trustedSession.ShopCode,
                                decision.ObservedRevision,
                                DateTimeOffset.UtcNow,
                                binding.Epoch).ConfigureAwait(false);
                        }
                        Report(progress, "session", "ok", T("startOfDay.stepSession"));
                        return StartOfDayHeartbeatResult.Succeeded(
                            decision,
                            response.Value.CatalogChangesAvailable,
                            revision.CommittedRevision,
                            delta.IsValid && delta.HasState);
                    }

                    var responseCode = FirstNonEmpty(response.Value?.Code, response.Code);
                    if (response.Denied || SharedAuthStopPolicy.IsAuthenticationDenied(responseCode))
                    {
                        Block(result, "auth_denied", T("startOfDay.blockAuthDenied"), "session", progress);
                        _store.Clear();
                        await StoreAuthDeniedBestEffortAsync(settings, "heartbeat").ConfigureAwait(false);
                        return StartOfDayHeartbeatResult.Failed();
                    }

                    result.LastTransientError = SafeCode(response.Code);
                    _logger.LogWarning(
                        "Start-of-day heartbeat retryable: category=startofday.sync code=" +
                        SafeCode(response.Code) +
                        " clientRequestId=" + SafeId(response.ClientRequestId) +
                        " serverRequestId=" + SafeId(response.ServerRequestId) +
                        " cfRay=" + SafeId(response.CfRay));
                    Report(progress, "session", "warning", T("startOfDay.networkSlow"));
                    return StartOfDayHeartbeatResult.Failed();
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    result.LastTransientError = "heartbeat_timeout";
                    Report(progress, "session", "warning", T("startOfDay.networkSlow"));
                    _logger.LogWarning("Start-of-day heartbeat timeout.");
                    return StartOfDayHeartbeatResult.Failed();
                }
                catch (Exception ex)
                {
                    result.LastTransientError = "heartbeat_exception";
                    Report(progress, "session", "warning", T("startOfDay.networkSlow"));
                    _logger.LogWarning("Start-of-day heartbeat failed.", ex);
                    return StartOfDayHeartbeatResult.Failed();
                }
            }
        }

        private async Task<OutboxDrainResult> TrySalesSyncAsync(
            PosAdminWebOptions options,
            StartOfDaySyncResult result,
            CancellationToken cancellationToken)
        {
            using (var cts = CreateStepCts(SalesSyncTimeout, cancellationToken))
            {
                try
                {
                    return await new PosSalesSyncService(_factory)
                        .TrySyncPendingAsync(options, cts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    result.LastTransientError = "sales_timeout";
                    _logger.LogWarning("Start-of-day sales sync timeout; continuing with local sale-safe catalog.");
                    return OutboxDrainResult.Empty(
                        failureKind: SyncFailureKind.Timeout,
                        diagnosticCode: "sales_timeout");
                }
                catch (Exception ex)
                {
                    result.LastTransientError = "sales_exception";
                    _logger.LogWarning("Start-of-day sales sync failed; continuing with local sale-safe catalog.", ex);
                    return OutboxDrainResult.Empty(
                        failureKind: SyncFailureKind.Unexpected,
                        diagnosticCode: "sales_exception");
                }
            }
        }

        private async Task<PosCatalogPullOutcome> TryCatalogDeltaAsync(
            PosAdminWebOptions options,
            PosTrustedDeviceSession trustedSession,
            StartOfDaySyncResult result,
            CancellationToken cancellationToken)
        {
            using (var cts = CreateStepCts(CatalogDeltaTimeout, cancellationToken))
            {
                try
                {
                    var outcome = await new PosCatalogPullService(_factory)
                        .TryPullIncrementalCatalogAsync(options, trustedSession, cts.Token)
                        .ConfigureAwait(false);
                    if (outcome.AuthDenied)
                    {
                        _store.Clear();
                        await StoreAuthDeniedBestEffortAsync(
                            new SettingsRepository(_factory),
                            "catalog").ConfigureAwait(false);
                    }
                    else if (!outcome.Completed)
                    {
                        result.LastTransientError = "catalog_background";
                    }

                    return outcome;
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    result.LastTransientError = "catalog_timeout";
                    _logger.LogWarning("Start-of-day catalog delta timeout; continuing with local sale-safe catalog.");
                    return PosCatalogPullOutcome.Failure(
                        "catalog_timeout",
                        authDenied: false,
                        hasMore: false,
                        pagesProcessed: 0);
                }
                catch (Exception ex)
                {
                    result.LastTransientError = "catalog_exception";
                    _logger.LogWarning("Start-of-day catalog delta failed; continuing with local sale-safe catalog.", ex);
                    return PosCatalogPullOutcome.Failure(
                        "catalog_exception",
                        authDenied: false,
                        hasMore: false,
                        pagesProcessed: 0);
                }
            }
        }

        private async Task<OutboxDrainResult> TryCatalogImportSyncAsync(
            PosAdminWebOptions options,
            PosTrustedDeviceSession trustedSession,
            StartOfDaySyncResult result,
            CancellationToken cancellationToken)
        {
            using (var cts = CreateStepCts(CatalogImportSyncTimeout, cancellationToken))
            {
                try
                {
                    var sync = await new CatalogImportSyncService(_factory)
                        .SyncPendingAsync(options, trustedSession, cts.Token)
                        .ConfigureAwait(false);
                    result.CatalogImportAckedThisRun = sync.Acked > 0;
                    if (sync.AuthenticationDenied)
                    {
                        _store.Clear();
                        var settings = new SettingsRepository(_factory);
                        await StoreAuthDeniedBestEffortAsync(settings, "catalog_import").ConfigureAwait(false);
                    }

                    return sync;
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    result.LastTransientError = "catalog_import_timeout";
                    _logger.LogWarning("Start-of-day catalog import sync timeout; continuing only if outbox remains retryable.");
                    return OutboxDrainResult.Empty(
                        failureKind: SyncFailureKind.Timeout,
                        diagnosticCode: "catalog_import_timeout");
                }
                catch (Exception ex)
                {
                    result.LastTransientError = "catalog_import_exception";
                    _logger.LogWarning("Start-of-day catalog import sync failed; continuing only if outbox remains retryable.", ex);
                    return OutboxDrainResult.Empty(
                        failureKind: SyncFailureKind.Unexpected,
                        diagnosticCode: "catalog_import_exception");
                }
            }
        }

        private async Task StoreAuthDeniedBestEffortAsync(
            SettingsRepository settings,
            string lane)
        {
            try
            {
                await settings.SetStringAsync(LastCatalogErrorSettingKey, "auth_denied").ConfigureAwait(false);
                await settings.SetStringAsync(LastSalesErrorSettingKey, "auth_denied").ConfigureAwait(false);
                await settings.SetStringAsync(CatalogBootstrapStatusSettingKey, "failed_auth_denied").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Start-of-day auth-stop settings persistence deferred: lane=" + SafeCode(lane),
                    ex);
            }
        }

        private async Task RecordHeartbeatSkipBestEffortAsync(
            PosTrustedDeviceSession trustedSession,
            string cursor,
            string code)
        {
            try
            {
                var decision = CatalogSyncPolicy.Evaluate(
                    CatalogSyncTrigger.StartOfDay,
                    new CatalogSyncState(cursor));
                await new CatalogSyncDiagnosticsRepository(_factory).RecordAsync(
                    new CatalogSyncRunRequest(
                        FirstNonEmpty(trustedSession?.ShopId, trustedSession?.ShopCode),
                        CatalogSyncTrigger.StartOfDay,
                        decision),
                    new CatalogSyncRunResult(
                        success: true,
                        code: code,
                        catalogPullAttempted: false,
                        catalogPullSkippedCode: code),
                    DateTimeOffset.UtcNow).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Start-of-day heartbeat skip diagnostics deferred.", ex);
            }
        }

        private static async Task RefreshOutboxAsync(
            StartOfDaySyncResult result,
            SaleRepository sales,
            SqliteConnectionFactory factory)
        {
            var outbox = await sales.GetSalesSyncOutboxSummaryAsync().ConfigureAwait(false);
            var catalogOutbox = await new CatalogImportOutboxRepository(factory)
                .GetSummaryAsync()
                .ConfigureAwait(false);
            result.PendingSales = ToSafeInt(outbox.Pending);
            result.RetrySales = ToSafeInt(outbox.Retry);
            result.InProgressSales = ToSafeInt(outbox.InProgress);
            result.BlockedSales = ToSafeInt(outbox.Blocked);
            result.PendingCatalogImports = ToSafeInt(catalogOutbox.Pending + catalogOutbox.InProgress);
            result.RetryCatalogImports = ToSafeInt(catalogOutbox.Retry);
            result.BlockedCatalogImports = ToSafeInt(catalogOutbox.Blocked);
        }

        private static bool HasAnyUnresolvedOutbox(StartOfDaySyncResult result)
        {
            return result.PendingSales > 0 ||
                result.RetrySales > 0 ||
                result.InProgressSales > 0 ||
                result.BlockedSales > 0 ||
                result.PendingCatalogImports > 0 ||
                result.RetryCatalogImports > 0 ||
                result.BlockedCatalogImports > 0;
        }

        private static async Task<bool> HasStoredAuthDeniedAsync(SettingsRepository settings)
        {
            var catalogStatus = await settings.GetStringAsync(CatalogBootstrapStatusSettingKey).ConfigureAwait(false);
            var catalogError = await settings.GetStringAsync(LastCatalogErrorSettingKey).ConfigureAwait(false);
            var salesError = await settings.GetStringAsync(LastSalesErrorSettingKey).ConfigureAwait(false);
            return IsAuthDenied(catalogStatus) || IsAuthDenied(catalogError) || IsAuthDenied(salesError);
        }

        private static bool IsAuthDenied(string value)
        {
            return string.Equals((value ?? string.Empty).Trim(), "auth_denied", StringComparison.OrdinalIgnoreCase) ||
                string.Equals((value ?? string.Empty).Trim(), "failed_auth_denied", StringComparison.OrdinalIgnoreCase);
        }

        private static StartOfDaySyncResult Block(
            StartOfDaySyncResult result,
            string reason,
            string message,
            string step,
            IProgress<PosStartOfDaySyncProgress> progress)
        {
            result.CanOpenPos = false;
            result.ShouldContinueInBackground = false;
            result.RequiresOperatorAction = true;
            result.BlockingReason = SafeCode(reason);
            result.StatusMessage = message;
            result.CatalogStatus = reason;
            Report(progress, step, "error", message);
            return result;
        }

        private static StartOfDaySyncResult ContinueLocal(
            StartOfDaySyncResult result,
            string message,
            bool shouldContinueInBackground,
            bool requiresOperatorAction,
            string step,
            IProgress<PosStartOfDaySyncProgress> progress)
        {
            result.CanOpenPos = true;
            result.ShouldContinueInBackground = shouldContinueInBackground;
            result.RequiresOperatorAction = requiresOperatorAction;
            result.BlockingReason = string.Empty;
            result.StatusMessage = message;
            result.CatalogStatus = shouldContinueInBackground ? "background" : "local_ready";
            Report(progress, step, shouldContinueInBackground ? "warning" : "ok", message);
            Report(progress, "complete", "ok", message);
            return result;
        }

        private static CancellationTokenSource CreateStepCts(TimeSpan timeout, CancellationToken cancellationToken)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            return cts;
        }

        private static void Report(
            IProgress<PosStartOfDaySyncProgress> progress,
            string step,
            string state,
            string message)
        {
            progress?.Report(new PosStartOfDaySyncProgress
            {
                Message = message ?? string.Empty,
                State = state ?? string.Empty,
                Step = step ?? string.Empty,
            });
        }

        private static string T(string key)
        {
            return PosLocalization.Current.Text(key);
        }

        private static int ToSafeInt(long value)
        {
            if (value <= 0) return 0;
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        private static string SafeCode(string code)
        {
            var normalized = (code ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return "failure";
            }

            var safe = string.Empty;
            foreach (var ch in normalized)
            {
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.')
                {
                    safe += ch;
                    if (safe.Length >= 60)
                    {
                        break;
                    }
                }
            }

            return safe.Length == 0 ? "failure" : safe;
        }

        private static string SafeId(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return string.Empty;
            }

            var safe = string.Empty;
            foreach (var ch in normalized)
            {
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.')
                {
                    safe += ch;
                    if (safe.Length >= 120)
                    {
                        break;
                    }
                }
            }

            return safe;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values ?? Array.Empty<string>())
            {
                var normalized = (value ?? string.Empty).Trim();
                if (normalized.Length > 0)
                {
                    return normalized;
                }
            }

            return string.Empty;
        }

        private static bool IsSameTrustedSession(
            PosTrustedDeviceSession expected,
            PosTrustedDeviceSession current)
        {
            return expected != null && current != null &&
                string.Equals(expected.PosSessionId, current.PosSessionId, StringComparison.Ordinal) &&
                string.Equals(expected.ShopDeviceId, current.ShopDeviceId, StringComparison.Ordinal) &&
                string.Equals(expected.ShopId, current.ShopId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(expected.ShopCode, current.ShopCode, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class StartOfDayHeartbeatResult
        {
            public bool? CatalogChangesAvailable { get; private set; }
            public string CommittedRevision { get; private set; } = string.Empty;
            public CatalogHeartbeatDecision Decision { get; private set; }
            public bool PartialCursorPending { get; private set; }
            public bool Success { get; private set; }

            public static StartOfDayHeartbeatResult Failed()
            {
                return new StartOfDayHeartbeatResult();
            }

            public static StartOfDayHeartbeatResult Succeeded(
                CatalogHeartbeatDecision decision,
                bool? catalogChangesAvailable,
                string committedRevision,
                bool partialCursorPending)
            {
                return new StartOfDayHeartbeatResult
                {
                    CatalogChangesAvailable = catalogChangesAvailable,
                    CommittedRevision = committedRevision ?? string.Empty,
                    Decision = decision,
                    PartialCursorPending = partialCursorPending,
                    Success = true
                };
            }
        }
    }

    public sealed class StartOfDaySyncResult
    {
        public bool CanOpenPos { get; set; }
        public bool ShouldContinueInBackground { get; set; }
        public bool RequiresOperatorAction { get; set; }
        public string BlockingReason { get; set; } = string.Empty;
        public string StatusMessage { get; set; } = string.Empty;
        public int PendingSales { get; set; }
        public int RetrySales { get; set; }
        public int InProgressSales { get; set; }
        public int BlockedSales { get; set; }
        public int PendingCatalogImports { get; set; }
        public int RetryCatalogImports { get; set; }
        public int BlockedCatalogImports { get; set; }
        public string CatalogStatus { get; set; } = string.Empty;
        public bool CatalogSaleSafe { get; set; }
        internal bool CatalogImportAckedThisRun { get; set; }
        public bool RestoreNeedsReview { get; set; }
        public string LastTransientError { get; set; } = string.Empty;
    }

    public sealed class PosStartOfDaySyncProgress
    {
        public string Step { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }
}
