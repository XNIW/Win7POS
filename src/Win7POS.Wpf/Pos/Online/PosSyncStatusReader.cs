using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
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
        private const string CatalogSyncDiagnosticsPrefix = "pos.catalog.sync.";

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
            var catalogOutboxRepository = new CatalogImportOutboxRepository(_factory);
            var catalogOutbox = await catalogOutboxRepository
                .GetSummaryAsync()
                .ConfigureAwait(false);
            var drainNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var salesDrain = await sales.GetSalesSyncDrainStateAsync(drainNow).ConfigureAwait(false);
            var importDrain = await catalogOutboxRepository.GetDrainStateAsync(drainNow).ConfigureAwait(false);

            _trustedDeviceStore.TryRead(out var trustedSession);

            var lastCatalog = await settings.GetStringAsync(LastCatalogSyncSettingKey).ConfigureAwait(false);
            var lastCatalogError = await settings.GetStringAsync(LastCatalogErrorSettingKey).ConfigureAwait(false);
            var lastCatalogHasMore = await settings.GetBoolAsync(LastCatalogHasMoreSettingKey).ConfigureAwait(false) == true;
            var lastCatalogVersion = await settings.GetStringAsync(LastCatalogVersionSettingKey).ConfigureAwait(false);
            var catalogBootstrapStatus = await settings.GetStringAsync(CatalogBootstrapStatusSettingKey).ConfigureAwait(false);
            var catalogSaleSafeAt = await settings.GetStringAsync(CatalogSaleSafeAtSettingKey).ConfigureAwait(false);
            var catalogSyncMode = await settings.GetStringAsync(CatalogShopStateRepository.LastSyncModeKey).ConfigureAwait(false);
            var catalogDiagnosticMode = await settings.GetStringAsync(CatalogSyncDiagnosticsPrefix + "last_mode").ConfigureAwait(false);
            var catalogCursor = await settings.GetStringAsync(CatalogShopStateRepository.LastSyncCursorKey).ConfigureAwait(false);
            var catalogLastTrigger = await settings.GetStringAsync(CatalogSyncDiagnosticsPrefix + "last_trigger").ConfigureAwait(false);
            var catalogLastFullReason = await settings.GetStringAsync(CatalogSyncDiagnosticsPrefix + "last_full_reason").ConfigureAwait(false);
            var catalogLastSuccess = await settings.GetStringAsync(CatalogSyncDiagnosticsPrefix + "last_success_at").ConfigureAwait(false);
            var catalogPages = await settings.GetStringAsync(CatalogSyncDiagnosticsPrefix + "pages").ConfigureAwait(false);
            var catalogRows = await settings.GetStringAsync(CatalogSyncDiagnosticsPrefix + "rows").ConfigureAwait(false);
            var catalogDuration = await settings.GetStringAsync(CatalogSyncDiagnosticsPrefix + "duration_ms").ConfigureAwait(false);
            var catalogFullRatio = await settings.GetStringAsync(CatalogSyncDiagnosticsPrefix + "full_ratio_percent").ConfigureAwait(false);
            var catalogObservedRevision = await settings.GetStringAsync(
                CatalogShopStateRepository.ObservedRevisionKey).ConfigureAwait(false);
            var catalogCommittedRevision = await settings.GetStringAsync(
                CatalogShopStateRepository.CommittedRevisionKey).ConfigureAwait(false);
            var catalogState = new CatalogShopStateRepository(_factory);
            var catalogExactness = await catalogState.LoadExactnessAsync().ConfigureAwait(false);
            var catalogSaleSafety = await catalogState
                .EvaluateSaleSafetyForOfficialShopAsync()
                .ConfigureAwait(false);
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
            var catalogRevisionMatchCode = RevisionMatchCode(
                catalogObservedRevision,
                catalogCommittedRevision);
            var requiresAttention =
                outbox.Blocked > 0 ||
                catalogOutbox.Blocked > 0 ||
                restoreNeedsReview ||
                !string.IsNullOrWhiteSpace(policyWarning) ||
                !catalogSaleSafety.IsSaleSafe ||
                catalogExactness.Status != CatalogCompletenessStatus.Verified ||
                catalogExactness.RepairRequired ||
                string.Equals(catalogRevisionMatchCode, "mismatch", StringComparison.Ordinal) ||
                CatalogRequiresAttention(catalogBootstrapStatus, lastCatalogError, lastCatalogHasMore) ||
                !string.IsNullOrWhiteSpace(lastSalesError);
            var connectivityState = ConnectivityState(trustedSession);

            return new PosSyncStatusSnapshot
            {
                CatalogBootstrapText = CatalogBootstrapText(
                    catalogBootstrapStatus,
                    lastCatalogHasMore,
                    lastCatalogError,
                    catalogSaleSafeAt,
                    catalogSaleSafety),
                CatalogCompletenessText = CatalogCompletenessText(catalogExactness),
                CatalogCountsText = CatalogCountsText(catalogExactness),
                CatalogCursorFingerprint = RedactedFingerprint(catalogCursor),
                CatalogDurationMilliseconds = ParseNonNegativeLong(catalogDuration),
                CatalogErrorCode = SafeCode(lastCatalogError),
                CatalogErrorText = T("sync.lastCatalogError") + ": " + SafeCode(lastCatalogError),
                CatalogHasError = !string.IsNullOrWhiteSpace(lastCatalogError),
                CatalogObservedRevisionFingerprint = RedactedFingerprint(catalogObservedRevision),
                CatalogCommittedRevisionFingerprint = RedactedFingerprint(catalogCommittedRevision),
                CatalogRevisionMatchCode = catalogRevisionMatchCode,
                CatalogFullRatioPercent = SafeNumber(catalogFullRatio),
                CatalogHasMore = lastCatalogHasMore,
                CatalogLastFullReasonCode = SafeCode(catalogLastFullReason),
                CatalogLastSuccessText = FormatIso(FirstNonEmpty(catalogLastSuccess, lastCatalog)),
                CatalogLastTriggerCode = SafeCode(catalogLastTrigger),
                CatalogPages = ParseNonNegativeLong(catalogPages),
                CatalogRepairRequired = catalogExactness.RepairRequired || catalogExactness.Status == CatalogCompletenessStatus.Mismatch,
                CatalogRepairText = CatalogRepairText(catalogExactness, catalogSaleSafety),
                CatalogReadinessText = CatalogReadinessText(
                    catalogSaleSafeAt,
                    catalogBootstrapStatus,
                    catalogSaleSafety),
                CatalogSaleSafe = catalogSaleSafety.IsSaleSafe,
                CatalogSaleSafetyCode = SafeCode(catalogSaleSafety.ReasonCode),
                CatalogSyncModeText = CatalogSyncModeText(
                    FirstNonEmpty(catalogDiagnosticMode, catalogSyncMode)),
                CatalogRows = ParseNonNegativeLong(catalogRows),
                CatalogVersionText = T("sync.catalog") + ": " + (string.IsNullOrWhiteSpace(lastCatalogVersion) ? T("sync.versionUnavailable") : lastCatalogVersion.Trim()),
                ConnectivityState = connectivityState,
                ConnectivityText = ConnectivityText(connectivityState),
                DeviceText = DeviceText(trustedSession),
                IsTrusted = trustedSession != null,
                IsSyncing = salesSyncInProgress,
                ImportBlocked = catalogOutbox.Blocked,
                ImportInProgress = catalogOutbox.InProgress,
                ImportLastAckText = FormatEpochMilliseconds(catalogOutbox.LastAckedAt),
                ImportNextRetryText = FormatEpochMilliseconds(importDrain.NextRetryAt),
                ImportPending = catalogOutbox.Pending,
                ImportRemainingDue = importDrain.RemainingDue,
                ImportRetry = catalogOutbox.Retry,
                LastCatalogSyncText = T("sync.lastCatalog") + ": " + FormatIso(lastCatalog),
                LastOnlineText = T("sync.sessionVerified") + ": " + FormatIso(trustedSession?.LastOkServerAt),
                LastSalesSyncText = T("sync.lastSaleSent") + ": " + FormatIso(lastSales),
                RequiresAttention = requiresAttention,
                PendingSalesText = DetailedPendingOutboxText(outbox, catalogOutbox),
                PolicyText = PolicyText(policyContractVersion, policyPaymentMethods, policyStaffOfflineMirror, policyTaxStatus, policyWarning),
                RestoreReviewText = RestoreReviewText(restoreNeedsReview, restoreCompletedAt, restorePreBackupPath),
                SalesAttentionText = SalesAttentionText(outbox, restoreNeedsReview),
                SalesBlocked = outbox.Blocked,
                SalesErrorText = T("sync.lastSalesError") + ": " + SafeCode(lastSalesError),
                SalesInProgress = outbox.InProgress,
                SalesLastAckText = FormatEpochMilliseconds(outbox.LastAckedAt),
                SalesNextRetryText = FormatEpochMilliseconds(salesDrain.NextRetryAt),
                SalesPending = outbox.Pending,
                SalesRemainingDue = salesDrain.RemainingDue,
                SalesRetry = outbox.Retry,
                StaffText = StaffText(trustedSession),
                SummaryText = SummaryText(
                    connectivityState,
                    outbox,
                    catalogOutbox,
                    lastCatalog,
                    lastSales,
                    restoreNeedsReview,
                    salesSyncInProgress,
                    catalogBootstrapStatus,
                    lastCatalogError,
                    lastCatalogHasMore,
                    catalogSaleSafeAt,
                    lastSalesError,
                    catalogExactness,
                    catalogSaleSafety)
            };
        }

        private static string T(string key)
        {
            return PosLocalization.Current.Text(key);
        }

        private static string RevisionMatchCode(string observed, string committed)
        {
            var normalizedObserved = CatalogHeartbeatPolicy.NormalizeRevision(observed);
            var normalizedCommitted = CatalogHeartbeatPolicy.NormalizeRevision(committed);
            if (normalizedObserved.Length == 0 || normalizedCommitted.Length == 0)
            {
                return "unknown";
            }

            return string.Equals(normalizedObserved, normalizedCommitted, StringComparison.Ordinal)
                ? "match"
                : "mismatch";
        }

        private static string ConnectivityState(PosTrustedDeviceSession session)
        {
            if (session == null)
            {
                return "not_connected";
            }

            var authorization = PosOfflineAuthorizationLeasePolicy.Evaluate(
                session,
                DateTimeOffset.UtcNow);
            if (!authorization.Allowed)
            {
                return "reconnect";
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
            CatalogImportOutboxSummary catalogOutbox,
            string lastCatalog,
            string lastSales,
            bool restoreNeedsReview,
            bool salesSyncInProgress,
            string catalogBootstrapStatus,
            string lastCatalogError,
            bool lastCatalogHasMore,
            string catalogSaleSafeAt,
            string lastSalesError,
            CatalogExactnessState catalogExactness,
            CatalogSaleSafetyEvaluation catalogSaleSafety)
        {
            if (string.Equals((catalogBootstrapStatus ?? string.Empty).Trim(), "failed_auth_denied", StringComparison.OrdinalIgnoreCase))
            {
                return T("sync.reconnectSession") +
                    " | " + PendingOutboxText(outbox, catalogOutbox);
            }

            if (outbox.Blocked > 0 || catalogOutbox.Blocked > 0 || restoreNeedsReview)
            {
                return T("sync.requiresAttention") +
                    " | " + ConnectivityText(connectivityState) +
                    " | " + PendingOutboxText(outbox, catalogOutbox) +
                    " | " + BlockedOutboxText(outbox, catalogOutbox);
            }

            if (outbox.Retry > 0 || catalogOutbox.Retry > 0)
            {
                return T("sync.retrySync") +
                    " | " + ConnectivityText(connectivityState) +
                    " | " + RetryOutboxText(outbox, catalogOutbox) +
                    " | " + PendingOutboxText(outbox, catalogOutbox);
            }

            if (outbox.PendingOrRetry > 0 || catalogOutbox.PendingOrRetry > 0)
            {
                return T("sync.pendingSync") +
                    " | " + ConnectivityText(connectivityState) +
                    " | " + PendingOutboxText(outbox, catalogOutbox);
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
                    " | " + PendingOutboxText(outbox, catalogOutbox);
            }

            if (string.Equals((catalogBootstrapStatus ?? string.Empty).Trim(), "updating", StringComparison.OrdinalIgnoreCase))
            {
                return T("sync.catalogUpdating") +
                    " | " + ConnectivityText(connectivityState) +
                    " | " + PendingOutboxText(outbox, catalogOutbox);
            }

            if (CatalogRequiresAttention(catalogBootstrapStatus, lastCatalogError, lastCatalogHasMore))
            {
                return CatalogAttentionText(catalogBootstrapStatus, lastCatalogHasMore) +
                    " | " + ConnectivityText(connectivityState) +
                    " | " + T("sync.lastCatalogError") + ": " + SafeCode(lastCatalogError);
            }

            if (catalogSaleSafety == null || !catalogSaleSafety.IsSaleSafe)
            {
                return T("sync.requiresAttention") +
                    " | " + CatalogSaleSafetyText(catalogSaleSafety) +
                    " | " + ConnectivityText(connectivityState);
            }

            if (catalogExactness == null || catalogExactness.Status != CatalogCompletenessStatus.Verified)
            {
                return T("sync.requiresAttention") +
                    " | " + CatalogCompletenessText(catalogExactness) +
                    " | " + ConnectivityText(connectivityState);
            }

            if (salesSyncInProgress)
            {
                return T("sync.inProgress") +
                    " | " + ConnectivityText(connectivityState) +
                    " | " + PendingOutboxText(outbox, catalogOutbox) +
                    " | " + RetryOutboxText(outbox, catalogOutbox);
            }

            if (string.IsNullOrWhiteSpace(lastCatalog))
            {
                return T("sync.catalogNeverDownloaded") +
                    " | " + ConnectivityText(connectivityState) +
                    " | " + PendingOutboxText(outbox, catalogOutbox);
            }

            if (catalogSaleSafety.IsSaleSafe && !string.IsNullOrWhiteSpace(catalogSaleSafeAt))
            {
                return ConnectivityText(connectivityState) +
                    " | " + T("sync.catalogReady") +
                    " | " + T("sync.lastCatalog") + ": " + FormatIso(lastCatalog) +
                    " | " + PendingOutboxText(outbox, catalogOutbox);
            }

            return ConnectivityText(connectivityState) +
                " | " + T("sync.lastCatalog") + ": " + FormatIso(lastCatalog) +
                " | " + T("sync.lastSaleSent") + ": " + FormatIso(lastSales) +
                " | " + PendingOutboxText(outbox, catalogOutbox);
        }

        private static string PendingOutboxText(
            SalesSyncOutboxSummary salesOutbox,
            CatalogImportOutboxSummary catalogOutbox)
        {
            return T("sync.pendingSales") + ": " + salesOutbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture) +
                " | " + T("sync.pendingCatalogImports") + ": " + catalogOutbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture);
        }

        private static string DetailedPendingOutboxText(
            SalesSyncOutboxSummary salesOutbox,
            CatalogImportOutboxSummary catalogOutbox)
        {
            return T("sync.pendingSales") + ": " + salesOutbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture) +
                " (" + T("sync.toRetry") + ": " + salesOutbox.Retry.ToString(CultureInfo.InvariantCulture) +
                ", " + T("sync.blockedAttention") + ": " + salesOutbox.Blocked.ToString(CultureInfo.InvariantCulture) + ")" +
                " | " + T("sync.pendingCatalogImports") + ": " + catalogOutbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture) +
                " (" + T("sync.toRetry") + ": " + catalogOutbox.Retry.ToString(CultureInfo.InvariantCulture) +
                ", " + T("sync.blockedAttention") + ": " + catalogOutbox.Blocked.ToString(CultureInfo.InvariantCulture) + ")";
        }

        private static string BlockedOutboxText(
            SalesSyncOutboxSummary salesOutbox,
            CatalogImportOutboxSummary catalogOutbox)
        {
            return T("sync.blockedAttention") +
                " | " + T("sync.pendingSales") + ": " + salesOutbox.Blocked.ToString(CultureInfo.InvariantCulture) +
                " | " + T("sync.pendingCatalogImports") + ": " + catalogOutbox.Blocked.ToString(CultureInfo.InvariantCulture);
        }

        private static string RetryOutboxText(
            SalesSyncOutboxSummary salesOutbox,
            CatalogImportOutboxSummary catalogOutbox)
        {
            return T("sync.toRetry") +
                " | " + T("sync.pendingSales") + ": " + salesOutbox.Retry.ToString(CultureInfo.InvariantCulture) +
                " | " + T("sync.pendingCatalogImports") + ": " + catalogOutbox.Retry.ToString(CultureInfo.InvariantCulture);
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
            string catalogSaleSafeAt,
            CatalogSaleSafetyEvaluation catalogSaleSafety)
        {
            var status = SafeCode(bootstrapStatus);
            if (catalogSaleSafety != null &&
                catalogSaleSafety.IsSaleSafe &&
                !string.IsNullOrWhiteSpace(catalogSaleSafeAt) &&
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

        private static string CatalogReadinessText(
            string catalogSaleSafeAt,
            string bootstrapStatus,
            CatalogSaleSafetyEvaluation catalogSaleSafety)
        {
            if (catalogSaleSafety != null &&
                catalogSaleSafety.IsSaleSafe &&
                !string.IsNullOrWhiteSpace(catalogSaleSafeAt))
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

            return CatalogSaleSafetyText(catalogSaleSafety);
        }

        private static string CatalogCompletenessText(CatalogExactnessState exactness)
        {
            var status = exactness?.Status ?? CatalogCompletenessStatus.Unverified;
            string statusText;
            switch (status)
            {
                case CatalogCompletenessStatus.Verified:
                    statusText = T("sync.catalogCompletenessVerified");
                    break;
                case CatalogCompletenessStatus.Mismatch:
                    statusText = T("sync.catalogCompletenessMismatch");
                    break;
                default:
                    statusText = T("sync.catalogCompletenessUnverified");
                    break;
            }

            var text = T("sync.catalogCompleteness") + ": " + statusText;
            if (!string.IsNullOrWhiteSpace(exactness?.VerifiedAt) && status == CatalogCompletenessStatus.Verified)
            {
                text += " | " + T("sync.catalogVerifiedAt") + ": " + FormatIso(exactness.VerifiedAt);
            }

            if (!string.IsNullOrWhiteSpace(exactness?.Code) && status != CatalogCompletenessStatus.Verified)
            {
                text += " | " + T("sync.reason") + ": " + SafeCode(exactness.Code);
            }

            return text;
        }

        private static string CatalogCountsText(CatalogExactnessState exactness)
        {
            if (exactness == null || string.IsNullOrWhiteSpace(exactness.EvaluatedAt))
            {
                return T("sync.catalogLocalCounts") + ": " + T("sync.unavailable");
            }

            return T("sync.catalogLocalCounts") + ": " +
                T("sync.products") + " " + (exactness?.ActiveProducts ?? 0).ToString(CultureInfo.InvariantCulture) +
                " | " + T("sync.categories") + " " + (exactness?.ActiveCategories ?? 0).ToString(CultureInfo.InvariantCulture) +
                " | " + T("sync.suppliers") + " " + (exactness?.ActiveSuppliers ?? 0).ToString(CultureInfo.InvariantCulture);
        }

        private static string CatalogSyncModeText(string syncMode)
        {
            var normalized = (syncMode ?? string.Empty).Trim();
            string modeText;
            if (string.Equals(normalized, "full_refresh", StringComparison.OrdinalIgnoreCase))
            {
                modeText = T("sync.catalogModeFullRefresh");
            }
            else if (string.Equals(normalized, "delta", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Incremental", StringComparison.OrdinalIgnoreCase))
            {
                modeText = T("sync.catalogModeDelta");
            }
            else if (string.Equals(normalized, "ResumeIncremental", StringComparison.OrdinalIgnoreCase))
            {
                modeText = T("sync.catalogInterruptedResume");
            }
            else if (string.Equals(normalized, "Full", StringComparison.OrdinalIgnoreCase))
            {
                modeText = T("sync.catalogModeFullRefresh");
            }
            else if (string.Equals(normalized, "Blocked", StringComparison.OrdinalIgnoreCase))
            {
                modeText = T("sync.blocked");
            }
            else
            {
                modeText = T("sync.unavailable");
            }

            return T("sync.catalogSyncMode") + ": " + modeText;
        }

        private static string CatalogRepairText(
            CatalogExactnessState exactness,
            CatalogSaleSafetyEvaluation catalogSaleSafety)
        {
            if (exactness != null &&
                (exactness.RepairRequired || exactness.Status == CatalogCompletenessStatus.Mismatch))
            {
                return T("sync.catalogRepairRequired") +
                    " | " + T("sync.reason") + ": " + SafeCode(exactness.Code);
            }

            if (catalogSaleSafety == null || !catalogSaleSafety.IsSaleSafe)
            {
                return CatalogSaleSafetyText(catalogSaleSafety);
            }

            if (exactness == null || exactness.Status == CatalogCompletenessStatus.Unverified)
            {
                return T("sync.catalogVerificationPending");
            }

            return T("sync.catalogRepairNotRequired");
        }

        private static string CatalogSaleSafetyText(CatalogSaleSafetyEvaluation evaluation)
        {
            return T("sync.catalogNotSaleSafe") +
                " | " + T("sync.reason") + ": " +
                SafeCode(evaluation?.ReasonCode);
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

        private static string FormatEpochMilliseconds(long? value)
        {
            if (!value.HasValue || value.Value <= 0)
            {
                return T("sync.never");
            }

            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(value.Value)
                    .ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException)
            {
                return T("sync.never");
            }
        }

        private static long ParseNonNegativeLong(string value)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? Math.Max(0L, parsed)
                : 0L;
        }

        private static string SafeNumber(string value)
        {
            return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? Math.Max(0m, parsed).ToString("0.###", CultureInfo.InvariantCulture)
                : "0";
        }

        private static string RedactedFingerprint(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return T("sync.none");
            }

            var fingerprint = CatalogShopStateRepository.FingerprintValue(value.Trim());
            return fingerprint.Length <= 12 ? fingerprint : fingerprint.Substring(0, 12);
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
        public string CatalogCompletenessText { get; set; } = string.Empty;
        public string CatalogCountsText { get; set; } = string.Empty;
        public string CatalogCursorFingerprint { get; set; } = string.Empty;
        public long CatalogDurationMilliseconds { get; set; }
        public string CatalogErrorCode { get; set; } = string.Empty;
        public string CatalogErrorText { get; set; } = string.Empty;
        public bool CatalogHasError { get; set; }
        public string CatalogObservedRevisionFingerprint { get; set; } = string.Empty;
        public string CatalogCommittedRevisionFingerprint { get; set; } = string.Empty;
        public string CatalogRevisionMatchCode { get; set; } = string.Empty;
        public string CatalogFullRatioPercent { get; set; } = string.Empty;
        public bool CatalogHasMore { get; set; }
        public string CatalogLastFullReasonCode { get; set; } = string.Empty;
        public string CatalogLastSuccessText { get; set; } = string.Empty;
        public string CatalogLastTriggerCode { get; set; } = string.Empty;
        public long CatalogPages { get; set; }
        public bool CatalogRepairRequired { get; set; }
        public string CatalogRepairText { get; set; } = string.Empty;
        public string CatalogReadinessText { get; set; } = string.Empty;
        public bool CatalogSaleSafe { get; set; }
        public string CatalogSaleSafetyCode { get; set; } = string.Empty;
        public string CatalogSyncModeText { get; set; } = string.Empty;
        public long CatalogRows { get; set; }
        public string CatalogVersionText { get; set; } = string.Empty;
        public string ConnectivityState { get; set; } = string.Empty;
        public string ConnectivityText { get; set; } = string.Empty;
        public string DeviceText { get; set; } = string.Empty;
        public bool IsTrusted { get; set; }
        public bool IsSyncing { get; set; }
        public long ImportBlocked { get; set; }
        public long ImportInProgress { get; set; }
        public string ImportLastAckText { get; set; } = string.Empty;
        public string ImportNextRetryText { get; set; } = string.Empty;
        public long ImportPending { get; set; }
        public long ImportRemainingDue { get; set; }
        public long ImportRetry { get; set; }
        public string LastCatalogSyncText { get; set; } = string.Empty;
        public string LastOnlineText { get; set; } = string.Empty;
        public string LastSalesSyncText { get; set; } = string.Empty;
        public string PendingSalesText { get; set; } = string.Empty;
        public string PolicyText { get; set; } = string.Empty;
        public bool RequiresAttention { get; set; }
        public string RestoreReviewText { get; set; } = string.Empty;
        public string SalesAttentionText { get; set; } = string.Empty;
        public long SalesBlocked { get; set; }
        public string SalesErrorText { get; set; } = string.Empty;
        public long SalesInProgress { get; set; }
        public string SalesLastAckText { get; set; } = string.Empty;
        public string SalesNextRetryText { get; set; } = string.Empty;
        public long SalesPending { get; set; }
        public long SalesRemainingDue { get; set; }
        public long SalesRetry { get; set; }
        public string StaffText { get; set; } = string.Empty;
        public string SummaryText { get; set; } = string.Empty;
    }
}
