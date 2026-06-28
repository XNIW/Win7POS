using System;
using System.Globalization;
using System.Threading.Tasks;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Wpf.Pos.Online
{
    public sealed class PosSyncStatusReader
    {
        private const string LastCatalogSyncSettingKey = "pos.catalog.last_sync_at";
        private const string LastCatalogErrorSettingKey = "pos.catalog.last_error";
        private const string LastCatalogVersionSettingKey = "pos.catalog.last_catalog_version";
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
            var lastCatalogVersion = await settings.GetStringAsync(LastCatalogVersionSettingKey).ConfigureAwait(false);
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
            var requiresAttention = outbox.Blocked > 0 || restoreNeedsReview || !string.IsNullOrWhiteSpace(policyWarning);

            return new PosSyncStatusSnapshot
            {
                CatalogErrorText = "Ultimo errore catalogo: " + SafeCode(lastCatalogError),
                CatalogVersionText = "Catalogo: " + (string.IsNullOrWhiteSpace(lastCatalogVersion) ? "versione non disponibile" : lastCatalogVersion.Trim()),
                ConnectivityText = ConnectivityText(trustedSession),
                DeviceText = DeviceText(trustedSession),
                IsTrusted = trustedSession != null,
                IsSyncing = salesSyncInProgress,
                LastCatalogSyncText = "Ultimo catalogo: " + FormatIso(lastCatalog),
                LastOnlineText = "Sessione verificata: " + FormatIso(trustedSession?.LastOkServerAt),
                LastSalesSyncText = "Ultima vendita inviata: " + FormatIso(lastSales),
                RequiresAttention = requiresAttention,
                PendingSalesText = "Vendite in coda: " + outbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture) +
                    " | Da ritentare: " + outbox.Retry.ToString(CultureInfo.InvariantCulture) +
                    " | Bloccate/attenzione: " + outbox.Blocked.ToString(CultureInfo.InvariantCulture),
                PolicyText = PolicyText(policyContractVersion, policyPaymentMethods, policyStaffOfflineMirror, policyTaxStatus, policyWarning),
                RestoreReviewText = RestoreReviewText(restoreNeedsReview, restoreCompletedAt, restorePreBackupPath),
                SalesAttentionText = SalesAttentionText(outbox, restoreNeedsReview),
                SalesErrorText = "Ultimo errore vendite: " + SafeCode(lastSalesError),
                StaffText = StaffText(trustedSession),
                SummaryText = SummaryText(trustedSession, outbox, lastCatalog, lastSales, restoreNeedsReview, salesSyncInProgress)
            };
        }

        private static string ConnectivityText(PosTrustedDeviceSession session)
        {
            if (session == null)
            {
                return "Non collegato";
            }

            if (TryParseIso(session.SessionExpiresAt, out var expiresAt) &&
                expiresAt <= DateTimeOffset.UtcNow)
            {
                return "Sessione da ricollegare";
            }

            if (!TryParseIso(session.LastOkServerAt, out var lastOk) ||
                lastOk < DateTimeOffset.UtcNow.AddMinutes(-10))
            {
                return "Offline";
            }

            return "Online";
        }

        private static string DeviceText(PosTrustedDeviceSession session)
        {
            if (session == null)
            {
                return "Negozio: non collegato | Dispositivo: non collegato";
            }

            return "Negozio: " + SafeLabel(session.ShopCode) +
                " | Dispositivo: " + ShortId(session.ShopDeviceId);
        }

        private static string StaffText(PosTrustedDeviceSession session)
        {
            if (session == null)
            {
                return "Staff online: non collegato";
            }

            var staff = string.IsNullOrWhiteSpace(session.StaffDisplayName)
                ? session.StaffCode
                : session.StaffDisplayName;
            return "Staff online: " + SafeLabel(staff);
        }

        private static string SummaryText(
            PosTrustedDeviceSession session,
            SalesSyncOutboxSummary outbox,
            string lastCatalog,
            string lastSales,
            bool restoreNeedsReview,
            bool salesSyncInProgress)
        {
            if (salesSyncInProgress)
            {
                return "Sync in corso" +
                    " | " + ConnectivityText(session) +
                    " | Vendite in coda: " + outbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture) +
                    " | Da ritentare: " + outbox.Retry.ToString(CultureInfo.InvariantCulture);
            }

            if (outbox.Blocked > 0 || restoreNeedsReview)
            {
                return "Richiede attenzione" +
                    " | " + ConnectivityText(session) +
                    " | Vendite in coda: " + outbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture) +
                    " | Bloccate: " + outbox.Blocked.ToString(CultureInfo.InvariantCulture);
            }

            if (outbox.Retry > 0)
            {
                return "Retry sync" +
                    " | " + ConnectivityText(session) +
                    " | Da ritentare: " + outbox.Retry.ToString(CultureInfo.InvariantCulture) +
                    " | Vendite in coda: " + outbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture);
            }

            if (outbox.PendingOrRetry > 0)
            {
                return "Pending sync" +
                    " | " + ConnectivityText(session) +
                    " | Vendite in coda: " + outbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture);
            }

            return ConnectivityText(session) +
                " | Ultimo catalogo: " + FormatIso(lastCatalog) +
                " | Ultima vendita inviata: " + FormatIso(lastSales) +
                " | Vendite in coda: " + outbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture);
        }

        private static string SalesAttentionText(SalesSyncOutboxSummary outbox, bool restoreNeedsReview)
        {
            if (outbox.Blocked <= 0 && !restoreNeedsReview)
            {
                return "Attenzione sync: nessuna vendita bloccata.";
            }

            var text = "Attenzione sync: vendita salvata localmente; non cancellare dati.";
            if (outbox.Blocked > 0)
            {
                text += " Vendite bloccate: " + outbox.Blocked.ToString(CultureInfo.InvariantCulture) + ". Chiamare manager/assistenza.";
            }

            if (restoreNeedsReview)
            {
                text += " Dopo restore verificare stato sincronizzazione prima di chiudere intervento.";
            }

            return text;
        }

        private static string RestoreReviewText(bool needsReview, string completedAt, string preBackupPath)
        {
            if (!needsReview)
            {
                return "Restore DB: nessuna revisione sync richiesta.";
            }

            return "Restore DB: verificare stato sincronizzazione. Ultimo restore: " +
                FormatIso(completedAt) +
                " | Pre-backup: " +
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
                return "Policy POS: non disponibile dal server.";
            }

            var text = "Policy POS: " + SafeCode(contractVersion) +
                " | pagamenti: " + SafeCode(paymentMethods) +
                " | staff offline: " + SafeCode(staffOfflineMirror) +
                " | tax: " + SafeCode(taxStatus);

            if (!string.IsNullOrWhiteSpace(warning))
            {
                text += " | attenzione: " + SafeCode(warning);
            }

            return text;
        }

        private static string FormatIso(string value)
        {
            if (TryParseIso(value, out var parsed))
            {
                return parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }

            return "mai";
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
                return "nessuno";
            }

            var code = value.Trim();
            return code.Length > 60 ? code.Substring(0, 60) : code;
        }

        private static string SafeLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "non disponibile";
            }

            var label = value.Trim();
            return label.Length > 40 ? label.Substring(0, 40) : label;
        }

        private static string SafePathLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "non disponibile";
            }

            var fileName = System.IO.Path.GetFileName(value.Trim());
            return SafeLabel(fileName);
        }

        private static string ShortId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "non disponibile";
            }

            var normalized = value.Trim();
            return normalized.Length <= 8 ? normalized : normalized.Substring(0, 8);
        }
    }

    public sealed class PosSyncStatusSnapshot
    {
        public string CatalogErrorText { get; set; } = string.Empty;
        public string CatalogVersionText { get; set; } = string.Empty;
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
