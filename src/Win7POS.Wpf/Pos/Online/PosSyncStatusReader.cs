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

            return new PosSyncStatusSnapshot
            {
                CatalogErrorText = "Ultimo errore catalogo: " + SafeCode(lastCatalogError),
                CatalogVersionText = "Catalogo: " + (string.IsNullOrWhiteSpace(lastCatalogVersion) ? "versione non disponibile" : lastCatalogVersion.Trim()),
                ConnectivityText = ConnectivityText(trustedSession),
                DeviceText = DeviceText(trustedSession),
                IsTrusted = trustedSession != null,
                LastCatalogSyncText = "Ultimo catalogo: " + FormatIso(lastCatalog),
                LastOnlineText = "Sessione verificata: " + FormatIso(trustedSession?.LastOkServerAt),
                LastSalesSyncText = "Ultima vendita inviata: " + FormatIso(lastSales),
                PendingSalesText = "Vendite in coda: " + outbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture) +
                    " | Da ritentare: " + outbox.Retry.ToString(CultureInfo.InvariantCulture) +
                    " | Bloccate: " + outbox.Blocked.ToString(CultureInfo.InvariantCulture),
                SalesErrorText = "Ultimo errore vendite: " + SafeCode(lastSalesError),
                StaffText = StaffText(trustedSession),
                SummaryText = SummaryText(trustedSession, outbox, lastCatalog, lastSales)
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
            string lastSales)
        {
            return ConnectivityText(session) +
                " | Ultimo catalogo: " + FormatIso(lastCatalog) +
                " | Ultima vendita inviata: " + FormatIso(lastSales) +
                " | Vendite in coda: " + outbox.PendingOrRetry.ToString(CultureInfo.InvariantCulture);
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
        public string LastCatalogSyncText { get; set; } = string.Empty;
        public string LastOnlineText { get; set; } = string.Empty;
        public string LastSalesSyncText { get; set; } = string.Empty;
        public string PendingSalesText { get; set; } = string.Empty;
        public string SalesErrorText { get; set; } = string.Empty;
        public string StaffText { get; set; } = string.Empty;
        public string SummaryText { get; set; } = string.Empty;
    }
}
