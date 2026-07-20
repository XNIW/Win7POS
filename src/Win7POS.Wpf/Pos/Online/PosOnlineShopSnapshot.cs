using System;
using System.Threading.Tasks;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Wpf.Pos.Online
{
    internal static class PosOnlineShopSnapshot
    {
        public static Task SaveAsync(
            SqliteConnectionFactory factory,
            PosShopResponse shop,
            OnlineSyncGeneration generation = null)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            if (shop == null)
            {
                return Task.CompletedTask;
            }

            var snapshot = new OfficialShopSnapshot
            {
                BusinessAddress = shop.BusinessAddress,
                BusinessCity = shop.BusinessCity,
                BusinessGiro = shop.BusinessGiro,
                CompanyRut = shop.CompanyRut,
                FiscalIdentityLockedByPlatform = shop.FiscalIdentityLockedByPlatform,
                LegalRepresentativeRut = shop.LegalRepresentativeRut,
                ShopCode = shop.ShopCode,
                ShopId = shop.ShopId,
                ShopName = shop.ShopName,
                ShopStatus = shop.ShopStatus,
                Source = string.IsNullOrWhiteSpace(shop.Source) ? "supabase_admin_server" : shop.Source,
                SyncedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAt = shop.UpdatedAt
            };

            return new ShopOfficialSnapshotRepository(factory).SaveAsync(snapshot, generation);
        }
    }
}
