using System;
using System.Threading.Tasks;
using Win7POS.Core.Receipt;

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
            return new ReceiptShopInfo
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
        }

        private static string Normalize(string value)
        {
            return value?.Trim() ?? string.Empty;
        }
    }

    public sealed class ShopOfficialSnapshotRepository
    {
        private const string KeyPrefix = "pos.official_shop.";
        private readonly SettingsRepository _settings;

        public ShopOfficialSnapshotRepository(SqliteConnectionFactory factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _settings = new SettingsRepository(factory);
        }

        public async Task<OfficialShopSnapshot> GetAsync()
        {
            return new OfficialShopSnapshot
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
        }

        public async Task SaveAsync(OfficialShopSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.HasOfficialData)
            {
                return;
            }

            var syncedAt = string.IsNullOrWhiteSpace(snapshot.SyncedAtUtc)
                ? DateTimeOffset.UtcNow.ToString("O")
                : snapshot.SyncedAtUtc.Trim();

            await SetAsync("business_address", snapshot.BusinessAddress).ConfigureAwait(false);
            await SetAsync("business_city", snapshot.BusinessCity).ConfigureAwait(false);
            await SetAsync("business_giro", snapshot.BusinessGiro).ConfigureAwait(false);
            await SetAsync("business_phone", snapshot.BusinessPhone).ConfigureAwait(false);
            await SetAsync("company_rut", snapshot.CompanyRut).ConfigureAwait(false);
            await _settings.SetBoolAsync(Key("fiscal_locked"), snapshot.FiscalIdentityLockedByPlatform).ConfigureAwait(false);
            await SetAsync("footer", snapshot.Footer).ConfigureAwait(false);
            await SetAsync("legal_representative_rut", snapshot.LegalRepresentativeRut).ConfigureAwait(false);
            await SetAsync("shop_code", snapshot.ShopCode).ConfigureAwait(false);
            await SetAsync("shop_id", snapshot.ShopId).ConfigureAwait(false);
            await SetAsync("shop_name", snapshot.ShopName).ConfigureAwait(false);
            await SetAsync("shop_status", snapshot.ShopStatus).ConfigureAwait(false);
            await SetAsync("source", string.IsNullOrWhiteSpace(snapshot.Source) ? "supabase_admin_server" : snapshot.Source).ConfigureAwait(false);
            await SetAsync("synced_at_utc", syncedAt).ConfigureAwait(false);
            await SetAsync("updated_at", snapshot.UpdatedAt).ConfigureAwait(false);
        }

        private Task<string> GetAsync(string suffix)
        {
            return _settings.GetStringAsync(Key(suffix));
        }

        private Task SetAsync(string suffix, string value)
        {
            return _settings.SetStringAsync(Key(suffix), value ?? string.Empty);
        }

        private static string Key(string suffix)
        {
            return KeyPrefix + suffix;
        }
    }
}
