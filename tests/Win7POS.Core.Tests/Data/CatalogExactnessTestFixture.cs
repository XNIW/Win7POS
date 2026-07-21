using Dapper;
using Win7POS.Data;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Data;

internal static class CatalogExactnessTestFixture
{
    public static async Task SeedVerifiedAsync(
        SqliteConnectionFactory factory,
        string shopId,
        string shopCode)
    {
        using var conn = factory.Open();
        var now = DateTimeOffset.UtcNow.ToString("O");
        await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES
  (@statusKey, 'Verified'),
  (@codeKey, 'catalog_exactness_verified'),
  (@repairKey, '0'),
  (@shopIdKey, @shopId),
  (@shopCodeKey, @shopCode),
  (@evaluatedKey, @now),
  (@verifiedKey, @now)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
            new
            {
                statusKey = CatalogShopStateRepository.CompletenessStatusKey,
                codeKey = CatalogShopStateRepository.CompletenessCodeKey,
                repairKey = CatalogShopStateRepository.RepairRequiredKey,
                shopIdKey = CatalogShopStateRepository.ExactnessShopIdKey,
                shopCodeKey = CatalogShopStateRepository.ExactnessShopCodeKey,
                evaluatedKey = CatalogShopStateRepository.ExactnessEvaluatedAtKey,
                verifiedKey = CatalogShopStateRepository.ExactnessVerifiedAtKey,
                shopId,
                shopCode,
                now
            });
    }
}
