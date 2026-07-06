using System;
using System.Threading.Tasks;
using Dapper;

namespace Win7POS.Data.Online
{
    public sealed class CatalogImportReconciliationService
    {
        private readonly SqliteConnectionFactory _factory;

        public CatalogImportReconciliationService(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<int> RecoverExpiredInProgressAsync(long nowMs)
        {
            using (var conn = _factory.Open())
            {
                var staleInProgressBefore = nowMs -
                    CatalogImportOutboxRepository.CatalogImportInProgressLeaseMilliseconds;
                return await conn.ExecuteAsync(@"
UPDATE catalog_import_outbox
SET status = 'retry',
    next_retry_at = @nowMs,
    last_error_code = 'recovered_stale_in_progress',
    last_error_at = @nowMs,
    updated_at = @nowMs
WHERE status = 'in_progress'
  AND COALESCE(last_attempt_at, updated_at, 0) <= @staleInProgressBefore;",
                    new { nowMs, staleInProgressBefore }).ConfigureAwait(false);
            }
        }

        public async Task<int> GetFailedBlockedCountAsync()
        {
            using (var conn = _factory.Open())
            {
                return await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(1) FROM catalog_import_outbox WHERE status = 'failed_blocked';")
                    .ConfigureAwait(false);
            }
        }

        public async Task<bool> ReconcileRemoteProductIdAsync(string barcode, string remoteProductId)
        {
            var normalizedBarcode = (barcode ?? string.Empty).Trim();
            var normalizedRemoteProductId = (remoteProductId ?? string.Empty).Trim();
            if (normalizedBarcode.Length == 0 || normalizedRemoteProductId.Length == 0)
            {
                return false;
            }

            using (var conn = _factory.Open())
            {
                var rows = await conn.ExecuteAsync(@"
UPDATE products
SET remote_product_id = @remoteProductId,
    remote_deleted_at = NULL,
    is_active = 1
WHERE barcode = @barcode
  AND (COALESCE(remote_product_id, '') = '' OR remote_product_id = @remoteProductId);",
                    new
                    {
                        barcode = normalizedBarcode,
                        remoteProductId = normalizedRemoteProductId
                    }).ConfigureAwait(false);

                return rows > 0;
            }
        }
    }
}
