using System;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Data;
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
            await RefreshOutboxAsync(result, sales).ConfigureAwait(false);

            if (!result.CatalogSaleSafe)
            {
                return Block(result, "catalog_not_sale_safe", T("startOfDay.blockCatalogNotSafe"), "database", progress);
            }

            if (result.RestoreNeedsReview)
            {
                return Block(result, "restore_needs_review", T("startOfDay.blockRestoreReview"), "database", progress);
            }

            if (result.BlockedSales > 0)
            {
                return Block(result, "sales_blocked", T("startOfDay.blockSalesBlocked"), "outbox", progress);
            }

            if (await HasStoredAuthDeniedAsync(settings).ConfigureAwait(false))
            {
                return Block(result, "auth_denied", T("startOfDay.blockAuthDenied"), "session", progress);
            }

            Report(progress, "database", "ok", T("startOfDay.stepDatabase"));
            Report(progress, "session", "active", T("startOfDay.stepSession"));

            if (!PosAdminWebOptions.TryLoad(out var options, out _))
            {
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
                return ContinueLocal(
                    result,
                    T("startOfDay.noTrustedSession"),
                    shouldContinueInBackground: false,
                    requiresOperatorAction: false,
                    "session",
                    progress);
            }

            var heartbeatOk = await TryHeartbeatAsync(options, trustedSession, result, settings, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!result.CanOpenPos && result.RequiresOperatorAction)
            {
                return result;
            }

            if (!heartbeatOk)
            {
                return ContinueLocal(
                    result,
                    T("startOfDay.continueBackground"),
                    shouldContinueInBackground: true,
                    requiresOperatorAction: false,
                    "session",
                    progress);
            }

            Report(progress, "outbox", "active", T("startOfDay.stepOutbox"));
            await RefreshOutboxAsync(result, sales).ConfigureAwait(false);
            if (result.BlockedSales > 0)
            {
                return Block(result, "sales_blocked", T("startOfDay.blockSalesBlocked"), "outbox", progress);
            }

            Report(progress, "outbox", "ok", T("startOfDay.stepOutbox"));
            Report(progress, "sales", "active", T("startOfDay.stepSales"));
            var salesComplete = await TrySalesSyncAsync(options, result, cancellationToken).ConfigureAwait(false);
            await RefreshOutboxAsync(result, sales).ConfigureAwait(false);
            if (await HasStoredAuthDeniedAsync(settings).ConfigureAwait(false))
            {
                return Block(result, "auth_denied", T("startOfDay.blockAuthDenied"), "sales", progress);
            }

            if (result.BlockedSales > 0)
            {
                return Block(result, "sales_blocked", T("startOfDay.blockSalesBlocked"), "sales", progress);
            }

            Report(progress, "sales", salesComplete ? "ok" : "warning", T("startOfDay.stepSales"));
            Report(progress, "catalog", "active", T("startOfDay.stepCatalog"));
            var catalogComplete = await TryCatalogDeltaAsync(options, result, cancellationToken).ConfigureAwait(false);
            if (await HasStoredAuthDeniedAsync(settings).ConfigureAwait(false))
            {
                return Block(result, "auth_denied", T("startOfDay.blockAuthDenied"), "catalog", progress);
            }

            result.CanOpenPos = true;
            result.RequiresOperatorAction = false;
            result.ShouldContinueInBackground = !salesComplete || !catalogComplete;
            result.BlockingReason = string.Empty;
            result.CatalogStatus = catalogComplete ? "completed" : "background";
            result.StatusMessage = result.ShouldContinueInBackground
                ? T("startOfDay.continueBackground")
                : T("startOfDay.complete");

            Report(progress, "catalog", catalogComplete ? "ok" : "warning", T("startOfDay.stepCatalog"));
            Report(progress, "complete", "ok", result.StatusMessage);
            return result;
        }

        private async Task<bool> TryHeartbeatAsync(
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
                    var response = await client.HeartbeatAsync(new PosHeartbeatRequest
                    {
                        AppVersion = typeof(PosStartOfDaySyncService).Assembly.GetName().Version?.ToString(),
                        DeviceToken = trustedSession.DeviceToken,
                        PosSessionId = trustedSession.PosSessionId,
                        SessionToken = trustedSession.SessionToken,
                        ShopDeviceId = trustedSession.ShopDeviceId,
                    }, cts.Token).ConfigureAwait(false);

                    if (response.Success && response.Value != null)
                    {
                        _store.SaveHeartbeat(trustedSession, response.Value);
                        Report(progress, "session", "ok", T("startOfDay.stepSession"));
                        return true;
                    }

                    if (response.Denied)
                    {
                        _store.Clear();
                        await settings.SetStringAsync(LastCatalogErrorSettingKey, "auth_denied").ConfigureAwait(false);
                        await settings.SetStringAsync(LastSalesErrorSettingKey, "auth_denied").ConfigureAwait(false);
                        await settings.SetStringAsync(CatalogBootstrapStatusSettingKey, "failed_auth_denied").ConfigureAwait(false);
                        return Block(result, "auth_denied", T("startOfDay.blockAuthDenied"), "session", progress).CanOpenPos;
                    }

                    result.LastTransientError = SafeCode(response.Code);
                    _logger.LogWarning(
                        "Start-of-day heartbeat retryable: category=startofday.sync code=" +
                        SafeCode(response.Code) +
                        " clientRequestId=" + SafeId(response.ClientRequestId) +
                        " serverRequestId=" + SafeId(response.ServerRequestId) +
                        " cfRay=" + SafeId(response.CfRay));
                    Report(progress, "session", "warning", T("startOfDay.networkSlow"));
                    return false;
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
                    return false;
                }
                catch (Exception ex)
                {
                    result.LastTransientError = "heartbeat_exception";
                    Report(progress, "session", "warning", T("startOfDay.networkSlow"));
                    _logger.LogWarning("Start-of-day heartbeat failed.", ex);
                    return false;
                }
            }
        }

        private async Task<bool> TrySalesSyncAsync(
            PosAdminWebOptions options,
            StartOfDaySyncResult result,
            CancellationToken cancellationToken)
        {
            using (var cts = CreateStepCts(SalesSyncTimeout, cancellationToken))
            {
                try
                {
                    await new PosSalesSyncService(_factory)
                        .TrySyncPendingAsync(options, cts.Token)
                        .ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    result.LastTransientError = "sales_timeout";
                    _logger.LogWarning("Start-of-day sales sync timeout; continuing with local sale-safe catalog.");
                    return false;
                }
                catch (Exception ex)
                {
                    result.LastTransientError = "sales_exception";
                    _logger.LogWarning("Start-of-day sales sync failed; continuing with local sale-safe catalog.", ex);
                    return false;
                }
            }
        }

        private async Task<bool> TryCatalogDeltaAsync(
            PosAdminWebOptions options,
            StartOfDaySyncResult result,
            CancellationToken cancellationToken)
        {
            using (var cts = CreateStepCts(CatalogDeltaTimeout, cancellationToken))
            {
                try
                {
                    var completed = await new PosCatalogPullService(_factory)
                        .TryPullCatalogAsync(options, cts.Token)
                        .ConfigureAwait(false);
                    if (!completed)
                    {
                        result.LastTransientError = "catalog_background";
                    }

                    return completed;
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    result.LastTransientError = "catalog_timeout";
                    _logger.LogWarning("Start-of-day catalog delta timeout; continuing with local sale-safe catalog.");
                    return false;
                }
                catch (Exception ex)
                {
                    result.LastTransientError = "catalog_exception";
                    _logger.LogWarning("Start-of-day catalog delta failed; continuing with local sale-safe catalog.", ex);
                    return false;
                }
            }
        }

        private static async Task RefreshOutboxAsync(StartOfDaySyncResult result, SaleRepository sales)
        {
            var outbox = await sales.GetSalesSyncOutboxSummaryAsync().ConfigureAwait(false);
            result.PendingSales = ToSafeInt(outbox.Pending);
            result.RetrySales = ToSafeInt(outbox.Retry);
            result.BlockedSales = ToSafeInt(outbox.Blocked);
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
        public int BlockedSales { get; set; }
        public string CatalogStatus { get; set; } = string.Empty;
        public bool CatalogSaleSafe { get; set; }
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
