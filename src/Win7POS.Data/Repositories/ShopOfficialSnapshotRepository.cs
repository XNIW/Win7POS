using System;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Online;
using Win7POS.Core.Receipt;
using Win7POS.Data.Online;

namespace Win7POS.Data.Repositories
{
    public sealed class OfficialShopSnapshot
    {
        public string BusinessAddress { get; set; }
        public string BusinessCity { get; set; }
        public string BusinessGiro { get; set; }
        public string BusinessPhone { get; set; }
        public string CompanyRut { get; set; }
        public bool FiscalIdentityLockedByPlatform { get; set; } = true;
        public string Footer { get; set; }
        public string LegalRepresentativeRut { get; set; }
        public string ShopCode { get; set; }
        public string ShopId { get; set; }
        public string ShopName { get; set; }
        public string ShopStatus { get; set; }
        public string Source { get; set; }
        public string SyncedAtUtc { get; set; }
        public string UpdatedAt { get; set; }

        public bool HasOfficialData =>
            !string.IsNullOrWhiteSpace(ShopId) ||
            !string.IsNullOrWhiteSpace(ShopCode) ||
            !string.IsNullOrWhiteSpace(ShopName) ||
            !string.IsNullOrWhiteSpace(CompanyRut) ||
            !string.IsNullOrWhiteSpace(BusinessAddress);

        public ReceiptShopInfo ToReceiptShopInfo()
        {
            var shop = new ReceiptShopInfo
            {
                Address = Normalize(BusinessAddress),
                BusinessGiro = Normalize(BusinessGiro),
                City = Normalize(BusinessCity),
                Footer = string.IsNullOrWhiteSpace(Footer) ? "Grazie e arrivederci" : Footer.Trim(),
                LegalRepresentativeRut = Normalize(LegalRepresentativeRut),
                Name = string.IsNullOrWhiteSpace(ShopName) ? "Win7 POS Store" : ShopName.Trim(),
                Phone = Normalize(BusinessPhone),
                Rut = Normalize(CompanyRut),
                ShopCode = Normalize(ShopCode),
                ShopStatus = Normalize(ShopStatus),
                Source = Normalize(Source),
                SyncedAtUtc = Normalize(SyncedAtUtc)
            };
            ReceiptShopMetadataPolicy.EnsureValidReceiptShop(shop);
            return shop;
        }

        private static string Normalize(string value)
        {
            return value?.Trim() ?? string.Empty;
        }
    }

    public sealed class ShopOfficialSnapshotRepository
    {
        private const string KeyPrefix = "pos.official_shop.";
        private readonly SqliteConnectionFactory _factory;
        private readonly SettingsRepository _settings;

        public ShopOfficialSnapshotRepository(SqliteConnectionFactory factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _factory = factory;
            _settings = new SettingsRepository(factory);
        }

        public async Task<OfficialShopSnapshot> GetAsync()
        {
            var snapshot = new OfficialShopSnapshot
            {
                BusinessAddress = await GetAsync("business_address").ConfigureAwait(false),
                BusinessCity = await GetAsync("business_city").ConfigureAwait(false),
                BusinessGiro = await GetAsync("business_giro").ConfigureAwait(false),
                BusinessPhone = await GetAsync("business_phone").ConfigureAwait(false),
                CompanyRut = await GetAsync("company_rut").ConfigureAwait(false),
                FiscalIdentityLockedByPlatform = (await _settings.GetBoolAsync(Key("fiscal_locked")).ConfigureAwait(false)) ?? true,
                Footer = await GetAsync("footer").ConfigureAwait(false),
                LegalRepresentativeRut = await GetAsync("legal_representative_rut").ConfigureAwait(false),
                ShopCode = await GetAsync("shop_code").ConfigureAwait(false),
                ShopId = await GetAsync("shop_id").ConfigureAwait(false),
                ShopName = await GetAsync("shop_name").ConfigureAwait(false),
                ShopStatus = await GetAsync("shop_status").ConfigureAwait(false),
                Source = await GetAsync("source").ConfigureAwait(false),
                SyncedAtUtc = await GetAsync("synced_at_utc").ConfigureAwait(false),
                UpdatedAt = await GetAsync("updated_at").ConfigureAwait(false)
            };
            EnsureValid(snapshot);
            return snapshot;
        }

        public async Task SaveAsync(
            OfficialShopSnapshot snapshot,
            OnlineSyncGeneration generation = null)
        {
            if (snapshot == null || !snapshot.HasOfficialData)
            {
                return;
            }
            EnsureValid(snapshot);

            var syncedAt = string.IsNullOrWhiteSpace(snapshot.SyncedAtUtc)
                ? DateTimeOffset.UtcNow.ToString("O")
                : snapshot.SyncedAtUtc.Trim();

            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction(deferred: false))
            {
                var permitted = generation != null
                    ? await OnlineSyncGenerationRepository.IsCurrentAndActiveAsync(
                        conn,
                        tx,
                        generation).ConfigureAwait(false)
                    : await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM pos_sync_session_generation
WHERE singleton_id = 1 AND active = 1;",
                        transaction: tx).ConfigureAwait(false) == 0;
                if (!permitted)
                {
                    tx.Rollback();
                    return;
                }
                await SetAsync(conn, tx, "business_address", snapshot.BusinessAddress).ConfigureAwait(false);
                await SetAsync(conn, tx, "business_city", snapshot.BusinessCity).ConfigureAwait(false);
                await SetAsync(conn, tx, "business_giro", snapshot.BusinessGiro).ConfigureAwait(false);
                await SetAsync(conn, tx, "business_phone", snapshot.BusinessPhone).ConfigureAwait(false);
                await SetAsync(conn, tx, "company_rut", snapshot.CompanyRut).ConfigureAwait(false);
                await SetAsync(conn, tx, "fiscal_locked", snapshot.FiscalIdentityLockedByPlatform ? "1" : "0").ConfigureAwait(false);
                await SetAsync(conn, tx, "footer", snapshot.Footer).ConfigureAwait(false);
                await SetAsync(conn, tx, "legal_representative_rut", snapshot.LegalRepresentativeRut).ConfigureAwait(false);
                await SetAsync(conn, tx, "shop_code", snapshot.ShopCode).ConfigureAwait(false);
                await SetAsync(conn, tx, "shop_id", snapshot.ShopId).ConfigureAwait(false);
                await SetAsync(conn, tx, "shop_name", snapshot.ShopName).ConfigureAwait(false);
                await SetAsync(conn, tx, "shop_status", snapshot.ShopStatus).ConfigureAwait(false);
                await SetAsync(conn, tx, "source", string.IsNullOrWhiteSpace(snapshot.Source) ? "supabase_admin_server" : snapshot.Source).ConfigureAwait(false);
                await SetAsync(conn, tx, "synced_at_utc", syncedAt).ConfigureAwait(false);
                await SetAsync(conn, tx, "updated_at", snapshot.UpdatedAt).ConfigureAwait(false);
                tx.Commit();
            }
        }

        private Task<string> GetAsync(string suffix)
        {
            return _settings.GetStringAsync(Key(suffix));
        }

        private static void EnsureValid(OfficialShopSnapshot snapshot)
        {
            ReceiptShopMetadataPolicy.EnsureValidSnapshot(new ReceiptShopMetadata
            {
                BusinessAddress = snapshot.BusinessAddress,
                BusinessCity = snapshot.BusinessCity,
                BusinessGiro = snapshot.BusinessGiro,
                BusinessPhone = snapshot.BusinessPhone,
                CompanyRut = snapshot.CompanyRut,
                Footer = snapshot.Footer,
                LegalRepresentativeRut = snapshot.LegalRepresentativeRut,
                ShopCode = snapshot.ShopCode,
                ShopId = snapshot.ShopId,
                ShopName = snapshot.ShopName,
                ShopStatus = snapshot.ShopStatus,
                Source = snapshot.Source,
                SyncedAtUtc = snapshot.SyncedAtUtc,
                UpdatedAt = snapshot.UpdatedAt
            });
        }

        private static Task<int> SetAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string suffix,
            string value)
        {
            return conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value)
VALUES(@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new { key = Key(suffix), value = value ?? string.Empty },
                tx);
        }

        private static string Key(string suffix)
        {
            return KeyPrefix + suffix;
        }
    }
}
