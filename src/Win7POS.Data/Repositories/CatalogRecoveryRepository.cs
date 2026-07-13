using System;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Security;
using Win7POS.Core.Util;

namespace Win7POS.Data.Repositories
{
    public sealed class CatalogRecoveryRepository
    {
        private const string CatalogBootstrapStatusSettingKey = "pos.catalog.bootstrap_status";
        private const string CatalogInitialCompletedAtSettingKey = "pos.catalog.initial_completed_at";
        private const string CatalogSaleSafeAtSettingKey = "pos.catalog.sale_safe_at";

        private readonly SqliteConnectionFactory _factory;

        public CatalogRecoveryRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<bool> TryApproveLocalCatalogAsync(int userId)
        {
            if (userId <= 0) return false;

            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction(deferred: false);
            try
            {
                var activeSellableProducts = await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(*)
FROM products
WHERE COALESCE(is_active, 1) = 1
  AND TRIM(COALESCE(barcode, '')) <> ''
  AND TRIM(COALESCE(name, '')) <> ''
  AND unitPrice > 0",
                    transaction: tx).ConfigureAwait(false);
                if (activeSellableProducts == 0)
                {
                    tx.Rollback();
                    return false;
                }

                var utc = DateTimeOffset.UtcNow.ToString("O");
                await conn.ExecuteAsync(@"
INSERT OR REPLACE INTO app_settings(key, value) VALUES(@saleSafeKey, @saleSafeValue);
INSERT OR IGNORE INTO app_settings(key, value) VALUES(@initialCompletedKey, @utc);
INSERT OR REPLACE INTO app_settings(key, value) VALUES(@bootstrapStatusKey, 'completed_local_recovery');",
                    new
                    {
                        saleSafeKey = CatalogSaleSafeAtSettingKey,
                        saleSafeValue = "local-recovery:" + utc,
                        initialCompletedKey = CatalogInitialCompletedAtSettingKey,
                        bootstrapStatusKey = CatalogBootstrapStatusSettingKey,
                        utc
                    },
                    tx).ConfigureAwait(false);

                await conn.ExecuteAsync(
                    "INSERT INTO security_events(ts, user_id, event_type, details) VALUES(@ts, @userId, @eventType, 'source=local_recovery')",
                    new
                    {
                        ts = UnixTime.NowSeconds(),
                        userId,
                        eventType = SecurityEventCodes.LocalRecoveryCatalogApproved
                    },
                    tx).ConfigureAwait(false);

                tx.Commit();
                return true;
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }
    }
}
