using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Online
{
    public sealed class PosSyncStatusReader
    {
        private const string LastCatalogSyncSettingKey = "pos.catalog.last_sync_at";
        private const string LastCatalogErrorSettingKey = "pos.catalog.last_error";
        private const string LastCatalogHasMoreSettingKey = "pos.catalog.last_has_more";
        private const string LastCatalogVersionSettingKey = "pos.catalog.last_catalog_version";
        private const string CatalogBootstrapStatusSettingKey = "pos.catalog.bootstrap_status";
        private const string CatalogSaleSafeAtSettingKey = "pos.catalog.sale_safe_at";
        private const string LastSalesSyncSettingKey = "pos.sales_sync.last_success_at";
        private const string LastSalesErrorSettingKey = "pos.sales_sync.last_error";
        private const string SalesSyncInProgressSettingKey = "pos.sales_sync.in_progress";
        private const string RestoreNeedsReviewSettingKey = "pos.restore.needs_sync_review";
        private const string RestoreLastCompletedAtSettingKey = "pos.restore.last_completed_at";
        private const string RestoreLastPreBackupPathSettingKey = "pos.restore.last_pre_backup_path";
        private const string PolicyContractVersionSettingKey = "pos.policy.contract_version";
        private const string PolicyPaymentMethodsSettingKey = "pos.policy.payment_methods";
        private const string PolicyStaffOfflineMirrorSettingKey = "pos.policy.staff_offline_mirror";
        private const string PolicyTaxStatusSettingKey = "pos.policy.tax_status";
        private const string PolicyWarningSettingKey = "pos.policy.warning";

        private readonly SqliteConnectionFactory _factory;
        private readonly PosTrustedDeviceStore _trustedDeviceStore;

        public PosSyncStatusReader(SqliteConnectionFactory factory)
            : this(factory, new PosTrustedDeviceStore())
        {
        }

        internal PosSyncStatusReader(SqliteConnectionFactory factory, PosTrustedDeviceStore trustedDeviceStore)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _trustedDeviceStore = trustedDeviceStore ?? throw new ArgumentNullException(nameof(trustedDeviceStore));
        }

        public async Task<PosSyncStatusSnapshot> ReadAsync()
        {
            var settings = new SettingsRepository(_factory);
            var sales = new SaleRepository(_factory);
            var outbox = await sales.GetSalesSyncOutboxSummaryAsync().ConfigureAwait(false);

            _trustedDeviceStore.TryRead(out var trustedSession);

            var lastCatalog = await settings.GetStringAsync(LastCatalogSyncSettingKey).ConfigureAwait(false);
            var lastCatalogError = await settings.GetStringAsync(LastCatalogErrorSettingKey).ConfigureAwait(false);
            var lastCatalogHasMore = await settings.GetBoolAsync(LastCatalogHasMoreSettingKey).ConfigureAwait(false) == true;
            var lastCatalogVersion = await settings.GetStringAsync(LastCatalogVersionSettingKey).ConfigureAwait(false);
            var catalogBootstrapStatus = await settings.GetStringAsync(CatalogBootstrapStatusSettingKey).ConfigureAwait(false);
            var catalogSaleSafeAt = await settings.GetStringAsync(CatalogSaleSafeAtSettingKey).ConfigureAwait(false);
            var lastSales = await settings.GetStringAsync(LastSalesSyncSettingKey).ConfigureAwait(false);
            var lastSalesError = await settings.GetStringAsync(LastSalesErrorSettingKey).ConfigureAwait(false);
            var salesSyncInProgress = await settings.GetBoolAsync(SalesSyncInProgressSettingKey).ConfigureAwait(false) == true;
            var restoreNeedsReview = await settings.GetBoolAsync(RestoreNeedsReviewSettingKey).ConfigureAwait(false) == true;
            var restoreCompletedAt = await settings.GetStringAsync(RestoreLastCompletedAtSettingKey).ConfigureAwait(false);
            var restorePreBackupPath = await settings.GetStringAsync(RestoreLastPreBackupPathSettingKey).ConfigureAwait(false);
            var policyContractVersion = await settings.GetStringAsync(PolicyContractVersionSettingKey).ConfigureAwait(false);
            var policyPaymentMethods = await settings.GetStringAsync(PolicyPaymentMethodsSettingKey).ConfigureAwait(false);
            var policyStaffOfflineMirror = await settings.GetStringAsync(PolicyStaffOfflineMirrorSettingKey).ConfigureAwait(false);
            var policyTaxStatus = await settings.GetStringAsync(PolicyTaxStatusSettingKey).ConfigureAwait(false);
            var policyWarning = await settings.GetStringAsync(PolicyWarningSettingKey).ConfigureAwait(false);
            var requiresAttention =
                outbox.Blocked > 0 ||
                restoreNeedsReview ||
                !string.IsNullOrWhiteSpace(policyWarning) ||
                CatalogRequiresAttention(catalogBootstrapStatus, lastCatalogError, lastCatalogHasMore) ||
                !string.IsNullOrWhiteSpace(lastSalesError);
            var connectivityState = ConnectivityState(trustedSession);

            return new PosSyncStatusSnapshot
            {
                CatalogBootstrapText = CatalogBootstrapText(catalogBootstrapStatus, lastCatalogHasMore, lastCatalogError, catalogSaleSafeAt),
                CatalogErrorText = T("sync.lastCatalogError") + ": " + SafeCode(lastCatalogError),
                CatalogReadinessText = CatalogReadinessText(catalogSaleSafeAt, catalogBootstrapStatus),
                CatalogVersionText = T("sync.catalog") + ": " + (string.IsNullOrWhiteSpace(lastCatalogVersion) ? T("sync.versionUnavailable") : lastCatalogVersion.Trim()),
                ConnectivityState = connectivityState,
                ConnectivityText = ConnectivityText(connectivityState),
                DeviceText = DeviceText(trustedSession),
                IsTrusted = trustedSession != null,
                IsSyncing = salesSyncInProgress,
                LastCatalogSyncText = T("sync.lastCatalog") + ": " + FormatIso(lastCatalog),
                LastOnlineText = T("sync.sessionVerified") + ": " + FormatIso(trustedSession?.LastOkServerAt),
                LastSalesSyncText = T("sync.lastSaleSent") + ": " + FormatIso(lastSales),
                RequiresAttention = requiresAttention,
                PendingSalesText = T("sync.pendingSales") + ": " + outbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture) +
                    " | " + T("sync.toRetry") + ": " + outbox.Retry.ToString(CultureInfo.InvariantCulture) +
                    " | " + T("sync.blocked") + ": " + outbox.Blocked.ToString(CultureInfo.InvariantCulture),
                PolicyText = PolicyText(policyContractVersion, policyPaymentMethods, policyStaffOfflineMirror, policyTaxStatus, policyWarning),
                RestoreReviewText = RestoreReviewText(restoreNeedsReview, restoreCompletedAt, restorePreBackupPath),
                SalesAttentionText = SalesAttentionText(outbox, restoreNeedsReview),
                SalesErrorText = T("sync.lastSalesError") + ": " + SafeCode(lastSalesError),
                StaffText = StaffText(trustedSession),
                SummaryText = SummaryText(
                    connectivityState,
                    outbox,
                    lastCatalog,
                    lastSales,
                    restoreNeedsReview,
                    salesSyncInProgress,
                    catalogBootstrapStatus,
                    lastCatalogError,
                    lastCatalogHasMore,
                    catalogSaleSafeAt,
                    lastSalesError)
            };
        }

        private static string T(string key)
        {
            return PosLocalization.Current.Text(key);
        }

        private static string ConnectivityState(PosTrustedDeviceSession session)
        {
            if (session == null)
            {
                return "not_connected";
            }

            if (TryParseIso(session.SessionExpiresAt, out var expiresAt) &&
                expiresAt <= DateTimeOffset.UtcNow)
            {
                return "reconnect";
            }

            if (!TryParseIso(session.LastOkServerAt, out var lastOk) ||
                lastOk < DateTimeOffset.UtcNow.AddMinutes(-10))
            {
                return "offline";
            }

            return "online";
        }

        private static string ConnectivityText(string state)
        {
            switch (state)
            {
                case "online":
                    return T("sync.online");
                case "offline":
                    return T("sync.offline");
                case "reconnect":
                    return T("sync.reconnectSession");
                default:
                    return T("sync.notConnected");
            }
        }

        private static string DeviceText(PosTrustedDeviceSession session)
        {
            if (session == null)
            {
                return T("sync.shop") + ": " + T("sync.notConnected") +
                    " | " + T("sync.device") + ": " + T("sync.notConnected");
            }

            return T("sync.shop") + ": " + SafeLabel(session.ShopCode) +
                " | " + T("sync.device") + ": " + ShortId(session.ShopDeviceId);
        }

        private static string StaffText(PosTrustedDeviceSession session)
        {
            if (session == null)
            {
                return T("sync.staffOnline") + ": " + T("sync.notConnected");
            }

            var staff = string.IsNullOrWhiteSpace(session.StaffDisplayName)
                ? session.StaffCode
                : session.StaffDisplayName;
            return T("sync.staffOnline") + ": " + SafeLabel(staff);
        }

        private static string SummaryText(
            string connectivityState,
            SalesSyncOutboxSummary outbox,
            string lastCatalog,
            string lastSales,
            bool restoreNeedsReview,
            bool salesSyncInProgress,
            string catalogBootstrapStatus,
            string lastCatalogError,
            bool lastCatalogHasMore,
            string catalogSaleSafeAt,
            string lastSalesError)
        {
            if (string.Equals((catalogBootstrapStatus ?? string.Empty).Trim(), "failed_auth_denied", StringComparison.OrdinalIgnoreCase))
            {
                return T("sync.reconnectSession") +
                    " | " + T("sync.pendingSales") + ": " + outbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture);
            }

            if (outbox.Blocked > 0 || restoreNeedsReview)
            {
                return T("sync.requiresAttention") +
                    " | " + ConnectivityText(connectivityState) +
                    " | " + T("sync.pendingSales") + ": " + outbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture) +
                    " | " + T("sync.blocked") + ": " + outbox.Blocked.ToString(CultureInfo.InvariantCulture);
            }

            if (outbox.Retry > 0)
            {
                return T("sync.retrySync") +
                    " | " + ConnectivityText(connectivityState) +
                    " | " + T("sync.toRetry") + ": " + outbox.Retry.ToString(CultureInfo.InvariantCulture) +
                    " | " + T("sync.pendingSales") + ": " + outbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture);
            }

            if (outbox.PendingOrRetry > 0)
            {
                return T("sync.pendingSync") +
                    " | " + ConnectivityText(connectivityState) +
                    " | " + T("sync.pendingSales") + ": " + outbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(lastSalesError))
            {
                return T("sync.requiresAttention") +
                    " | " + ConnectivityText(connectivityState) +
                    " | " + T("sync.lastSalesError") + ": " + SafeCode(lastSalesError);
            }

            if (string.Equals((catalogBootstrapStatus ?? string.Empty).Trim(), "in_progress", StringComparison.OrdinalIgnoreCase))
            {
                return T("sync.catalogPreparing") +
                    " | " + ConnectivityText(connectivityState) +
                    " | " + T("sync.pendingSales") + ": " + outbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture);
            }

            if (string.Equals((catalogBootstrapStatus ?? string.Empty).Trim(), "updating", StringComparison.OrdinalIgnoreCase))
            {
                return T("sync.catalogUpdating") +
                    " | " + ConnectivityText(connectivityState) +
                    " | " + T("sync.pendingSales") + ": " + outbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture);
            }

            if (CatalogRequiresAttention(catalogBootstrapStatus, lastCatalogError, lastCatalogHasMore))
            {
                return CatalogAttentionText(catalogBootstrapStatus, lastCatalogHasMore) +
                    " | " + ConnectivityText(connectivityState) +
                    " | " + T("sync.lastCatalogError") + ": " + SafeCode(lastCatalogError);
            }

            if (salesSyncInProgress)
            {
                return T("sync.inProgress") +
                    " | " + ConnectivityText(connectivityState) +
                    " | " + T("sync.pendingSales") + ": " + outbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture) +
                    " | " + T("sync.toRetry") + ": " + outbox.Retry.ToString(CultureInfo.InvariantCulture);
            }

            if (string.IsNullOrWhiteSpace(lastCatalog))
            {
                return T("sync.catalogNeverDownloaded") +
                    " | " + ConnectivityText(connectivityState) +
                    " | " + T("sync.pendingSales") + ": " + outbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(catalogSaleSafeAt))
            {
                return ConnectivityText(connectivityState) +
                    " | " + T("sync.catalogReady") +
                    " | " + T("sync.lastCatalog") + ": " + FormatIso(lastCatalog) +
                    " | " + T("sync.pendingSales") + ": " + outbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture);
            }

            return ConnectivityText(connectivityState) +
                " | " + T("sync.lastCatalog") + ": " + FormatIso(lastCatalog) +
                " | " + T("sync.lastSaleSent") + ": " + FormatIso(lastSales) +
                " | " + T("sync.pendingSales") + ": " + outbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture);
        }

        private static bool CatalogRequiresAttention(
            string bootstrapStatus,
            string lastCatalogError,
            bool lastCatalogHasMore)
        {
            if (lastCatalogHasMore || !string.IsNullOrWhiteSpace(lastCatalogError))
            {
                return true;
            }

            var status = (bootstrapStatus ?? string.Empty).Trim();
            return string.Equals(status, "partial_has_more", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "failed_retryable", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "failed_auth_denied", StringComparison.OrdinalIgnoreCase);
        }

        private static string CatalogAttentionText(string bootstrapStatus, bool lastCatalogHasMore)
        {
            var status = (bootstrapStatus ?? string.Empty).Trim();
            if (string.Equals(status, "failed_auth_denied", StringComparison.OrdinalIgnoreCase))
            {
                return T("sync.reconnectSession");
            }

            if (lastCatalogHasMore ||
                string.Equals(status, "partial_has_more", StringComparison.OrdinalIgnoreCase))
            {
                return T("sync.catalogInterruptedResume");
            }

            if (string.Equals(status, "failed_retryable", StringComparison.OrdinalIgnoreCase))
            {
                return T("sync.catalogBootstrapFailed");
            }

            return T("sync.catalogPartial");
        }

        private static string CatalogBootstrapText(
            string bootstrapStatus,
            bool lastCatalogHasMore,
            string lastCatalogError,
            string catalogSaleSafeAt)
        {
            var status = SafeCode(bootstrapStatus);
            if (!string.IsNullOrWhiteSpace(catalogSaleSafeAt) &&
                string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                return T("sync.catalogBootstrap") + ": " + T("sync.catalogSaleSafe") +
                    " | " + T("sync.lastCatalog") + ": " + FormatIso(catalogSaleSafeAt);
            }

            if (string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase))
            {
                return T("sync.catalogBootstrap") + ": " + T("sync.catalogPreparing");
            }

            if (string.Equals(status, "updating", StringComparison.OrdinalIgnoreCase))
            {
                return T("sync.catalogBootstrap") + ": " + T("sync.catalogUpdating");
            }

            if (lastCatalogHasMore ||
                string.Equals(status, "partial_has_more", StringComparison.OrdinalIgnoreCase))
            {
                return T("sync.catalogBootstrap") + ": " + T("sync.catalogInterruptedResume") +
                    " | " + T("sync.lastCatalogError") + ": " + SafeCode(FirstNonEmpty(lastCatalogError, "has_more_not_drained"));
            }

            if (string.Equals(status, "failed_retryable", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "failed_auth_denied", StringComparison.OrdinalIgnoreCase))
            {
                return T("sync.catalogBootstrap") + ": " + T("sync.catalogBootstrapFailed") +
                    " | " + T("sync.lastCatalogError") + ": " + SafeCode(FirstNonEmpty(lastCatalogError, status));
            }

            return T("sync.catalogBootstrap") + ": " + status;
        }

        private static string CatalogReadinessText(string catalogSaleSafeAt, string bootstrapStatus)
        {
            if (!string.IsNullOrWhiteSpace(catalogSaleSafeAt))
            {
                return T("sync.catalogReady") + ": " + FormatIso(catalogSaleSafeAt);
            }

            if (string.Equals((bootstrapStatus ?? string.Empty).Trim(), "in_progress", StringComparison.OrdinalIgnoreCase))
            {
                return T("sync.catalogPreparing");
            }

            if (string.Equals((bootstrapStatus ?? string.Empty).Trim(), "updating", StringComparison.OrdinalIgnoreCase))
            {
                return T("sync.catalogUpdating");
            }

            return T("sync.catalogNeverDownloaded");
        }

        private static string SalesAttentionText(SalesSyncOutboxSummary outbox, bool restoreNeedsReview)
        {
            if (outbox.Blocked <= 0 && !restoreNeedsReview)
            {
                return T("sync.noBlockedSales");
            }

            var text = T("sync.localSaleDoNotDelete");
            if (outbox.Blocked > 0)
            {
                text += " " + T("sync.blockedSales") + ": " + outbox.Blocked.ToString(CultureInfo.InvariantCulture) + ". " + T("sync.callSupport");
            }

            if (restoreNeedsReview)
            {
                text += " " + T("sync.restoreVerifyBeforeClose");
            }

            return text;
        }

        private static string RestoreReviewText(bool needsReview, string completedAt, string preBackupPath)
        {
            if (!needsReview)
            {
                return T("sync.restoreNoReview");
            }

            return T("sync.restoreNeedsReview") + " " + T("sync.lastRestore") + ": " +
                FormatIso(completedAt) +
                " | " + T("sync.preBackup") + ": " +
                SafePathLabel(preBackupPath);
        }

        private static string PolicyText(
            string contractVersion,
            string paymentMethods,
            string staffOfflineMirror,
            string taxStatus,
            string warning)
        {
            if (string.IsNullOrWhiteSpace(contractVersion))
            {
                return T("sync.policyUnavailable");
            }

            var text = T("sync.policyPos") + ": " + SafeCode(contractVersion) +
                " | " + T("sync.payments") + ": " + SafeCode(paymentMethods) +
                " | " + T("sync.offlineStaff") + ": " + SafeCode(staffOfflineMirror) +
                " | " + T("sync.tax") + ": " + SafeCode(taxStatus);

            if (!string.IsNullOrWhiteSpace(warning))
            {
                text += " | " + T("sync.attention") + ": " + SafeCode(warning);
            }

            return text;
        }

        private static string FormatIso(string value)
        {
            if (TryParseIso(value, out var parsed))
            {
                return parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }

            return T("sync.never");
        }

        private static bool TryParseIso(string value, out DateTimeOffset parsed)
        {
            return DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed);
        }

        private static string SafeCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return T("sync.none");
            }

            var code = new string(value
                .Trim()
                .Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.')
                .Take(60)
                .ToArray());
            return code.Length == 0 ? T("sync.none") : code;
        }

        private static string FirstNonEmpty(string left, string right)
        {
            return string.IsNullOrWhiteSpace(left) ? right : left;
        }

        private static string SafeLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return T("sync.unavailable");
            }

            var label = value.Trim();
            return label.Length > 40 ? label.Substring(0, 40) : label;
        }

        private static string SafePathLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return T("sync.unavailable");
            }

            var fileName = System.IO.Path.GetFileName(value.Trim());
            return SafeLabel(fileName);
        }

        private static string ShortId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return T("sync.unavailable");
            }

            var normalized = value.Trim();
            return normalized.Length <= 8 ? normalized : normalized.Substring(0, 8);
        }
    }

    public sealed class PosSyncStatusSnapshot
    {
        public string CatalogBootstrapText { get; set; } = string.Empty;
        public string CatalogErrorText { get; set; } = string.Empty;
        public string CatalogReadinessText { get; set; } = string.Empty;
        public string CatalogVersionText { get; set; } = string.Empty;
        public string ConnectivityState { get; set; } = string.Empty;
        public string ConnectivityText { get; set; } = string.Empty;
        public string DeviceText { get; set; } = string.Empty;
        public bool IsTrusted { get; set; }
        public bool IsSyncing { get; set; }
        public string LastCatalogSyncText { get; set; } = string.Empty;
        public string LastOnlineText { get; set; } = string.Empty;
        public string LastSalesSyncText { get; set; } = string.Empty;
        public string PendingSalesText { get; set; } = string.Empty;
        public string PolicyText { get; set; } = string.Empty;
        public bool RequiresAttention { get; set; }
        public string RestoreReviewText { get; set; } = string.Empty;
        public string SalesAttentionText { get; set; } = string.Empty;
        public string SalesErrorText { get; set; } = string.Empty;
        public string StaffText { get; set; } = string.Empty;
        public string SummaryText { get; set; } = string.Empty;
    }
}
