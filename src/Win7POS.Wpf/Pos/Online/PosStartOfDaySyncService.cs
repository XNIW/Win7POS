using System;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Online
{
    public sealed class PosStartOfDaySyncService
    {
        internal static readonly TimeSpan StartOfDayTotalTimeout = TimeSpan.FromSeconds(28);
        private const string RestoreNeedsReviewSettingKey = "pos.restore.needs_sync_review";

        private readonly SqliteConnectionFactory _factory;
        private readonly PosTrustedDeviceStore _store;
        private readonly PosOnlineSyncSupervisorHost _syncHost;

        public PosStartOfDaySyncService(
            SqliteConnectionFactory factory,
            PosOnlineSyncSupervisorHost syncHost)
            : this(factory, new PosTrustedDeviceStore(), syncHost)
        {
        }

        internal PosStartOfDaySyncService(
            SqliteConnectionFactory factory,
            PosTrustedDeviceStore store,
            PosOnlineSyncSupervisorHost syncHost)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _syncHost = syncHost ?? throw new ArgumentNullException(nameof(syncHost));
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
                .IsCatalogSaleSafeAsync(_factory).ConfigureAwait(false);
            result.RestoreNeedsReview = await settings
                .GetBoolAsync(RestoreNeedsReviewSettingKey).ConfigureAwait(false) == true;
            await RefreshOutboxAsync(result, sales, _factory).ConfigureAwait(false);

            if (!result.CatalogSaleSafe && !result.RestoreNeedsReview)
                return Block(result, "catalog_not_sale_safe", T("startOfDay.blockCatalogNotSafe"), "database", progress);
            if (result.RestoreNeedsReview && HasAnyUnresolvedOutbox(result))
                return Block(result, "restore_needs_review", T("startOfDay.blockRestoreReview"), "database", progress);
            if (result.BlockedSales > 0)
                return Block(result, "sales_blocked", T("startOfDay.blockSalesBlocked"), "outbox", progress);
            if (result.BlockedCatalogImports > 0)
                return Block(result, "catalog_import_blocked", T("startOfDay.blockCatalogImportBlocked"), "outbox", progress);

            Report(progress, "database", "ok", T("startOfDay.stepDatabase"));
            Report(progress, "session", "active", T("startOfDay.stepSession"));
            if (!PosAdminWebOptions.TryLoad(out _, out _))
            {
                return result.RestoreNeedsReview
                    ? Block(result, "restore_needs_review", T("startOfDay.blockRestoreReview"), "session", progress)
                    : ContinueLocal(result, T("startOfDay.noServerConfig"), false, false, "session", progress);
            }
            if (!_store.TryRead(out _))
            {
                return result.RestoreNeedsReview
                    ? Block(result, "restore_needs_review", T("startOfDay.blockRestoreReview"), "session", progress)
                    : ContinueLocal(result, T("startOfDay.noTrustedSession"), false, false, "session", progress);
            }
            if (await _syncHost.AttachCurrentTrustAsync(cancellationToken).ConfigureAwait(false) == null)
                return Block(result, "trusted_session_changed", T("startOfDay.blockAuthDenied"), "session", progress);

            Report(progress, "session", "ok", T("startOfDay.stepSession"));
            Report(progress, "outbox", "active", T("startOfDay.stepOutbox"));
            Report(progress, "sales", "active", T("startOfDay.stepSales"));
            Report(progress, "catalog-import", "active", T("startOfDay.stepCatalogImport"));
            var lanes = await _syncHost.RunStartOfDayAsync(
                result.RestoreNeedsReview,
                cancellationToken).ConfigureAwait(false);
            if (lanes == null)
                return Block(result, "sync_supervisor_inactive", T("startOfDay.blockAuthDenied"), "session", progress);

            if (lanes.Heartbeat?.AuthenticationDenied == true ||
                lanes.Sales?.AuthenticationDenied == true ||
                lanes.CatalogImport?.AuthenticationDenied == true ||
                lanes.CatalogDelta?.AuthenticationDenied == true)
            {
                return Block(result, "auth_denied", T("startOfDay.blockAuthDenied"), "session", progress);
            }

            var catalogRequired = result.RestoreNeedsReview ||
                lanes.Heartbeat?.RequestCatalogNow == true ||
                lanes.CatalogImport?.RequestCatalogNow == true;
            var catalogLane = lanes.CatalogDelta;
            if (catalogRequired && catalogLane == null)
            {
                catalogLane = await _syncHost.TriggerAsync(
                    OnlineSyncLane.CatalogDelta,
                    OnlineSyncLaneTrigger.StartOfDay,
                    cancellationToken).ConfigureAwait(false);
            }
            if (catalogLane?.AuthenticationDenied == true)
                return Block(result, "auth_denied", T("startOfDay.blockAuthDenied"), "catalog", progress);

            await RefreshOutboxAsync(result, sales, _factory).ConfigureAwait(false);
            if (result.BlockedSales > 0)
                return Block(result, "sales_blocked", T("startOfDay.blockSalesBlocked"), "outbox", progress);
            if (result.BlockedCatalogImports > 0)
                return Block(result, "catalog_import_blocked", T("startOfDay.blockCatalogImportBlocked"), "outbox", progress);

            var salesComplete = StartOfDaySalesDrainPolicy.Evaluate(
                result.PendingSales,
                result.RetrySales,
                result.InProgressSales,
                result.BlockedSales) == StartOfDaySalesDrainDecision.Complete;
            var importComplete = result.PendingCatalogImports == 0 &&
                result.RetryCatalogImports == 0;
            var catalogComplete = !catalogRequired ||
                (catalogLane?.Success == true && catalogLane.CatalogHasMore == false);

            result.CatalogSaleSafe = await PosCatalogPullService
                .IsCatalogSaleSafeAsync(_factory).ConfigureAwait(false);
            if (result.RestoreNeedsReview)
            {
                if (!catalogComplete || !result.CatalogSaleSafe)
                    return Block(result, "restore_needs_review", T("startOfDay.blockRestoreReview"), "catalog", progress);
                var review = await new RestoreShopSafetyRepository(_factory)
                    .CompleteReviewAsync().ConfigureAwait(false);
                if (!review.IsValid)
                    return Block(result, "restore_needs_review", T("startOfDay.blockRestoreReview"), "catalog", progress);
                result.RestoreNeedsReview = false;
            }

            var heartbeatHealthy = lanes.Heartbeat?.Success == true;
            result.CanOpenPos = true;
            result.RequiresOperatorAction = false;
            result.ShouldContinueInBackground = !heartbeatHealthy ||
                !salesComplete ||
                !importComplete ||
                !catalogComplete;
            result.BlockingReason = string.Empty;
            result.CatalogStatus = !catalogRequired
                ? "unchanged"
                : catalogComplete ? "completed" : "background";
            result.StatusMessage = result.ShouldContinueInBackground
                ? T("startOfDay.continueBackground")
                : T("startOfDay.complete");

            Report(progress, "outbox", "ok", T("startOfDay.stepOutbox"));
            Report(progress, "sales", salesComplete ? "ok" : "warning", T("startOfDay.stepSales"));
            Report(progress, "catalog-import", importComplete ? "ok" : "warning", T("startOfDay.stepCatalogImport"));
            Report(progress, "catalog", catalogComplete ? "ok" : "warning", T("startOfDay.stepCatalog"));
            Report(progress, "complete", "ok", result.StatusMessage);
            return result;
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
