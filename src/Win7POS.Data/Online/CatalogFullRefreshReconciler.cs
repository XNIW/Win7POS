using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace Win7POS.Data.Online
{
    public sealed class CatalogFullRefreshReconciler
    {
        private readonly SqliteConnectionFactory _factory;

        public CatalogFullRefreshReconciler(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<CatalogFullRefreshResult> ReconcileAsync(
            IEnumerable<string> productIds,
            IEnumerable<string> categoryIds,
            IEnumerable<string> supplierIds,
            string generatedAt)
        {
            var products = NormalizeIds(productIds);
            var categories = NormalizeIds(categoryIds);
            var suppliers = NormalizeIds(supplierIds);
            var removedAt = string.IsNullOrWhiteSpace(generatedAt)
                ? DateTimeOffset.UtcNow.ToString("O")
                : generatedAt.Trim();

            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await CreateAndFillAsync(conn, tx, "temp_full_product_ids", products).ConfigureAwait(false);
                await CreateAndFillAsync(conn, tx, "temp_full_category_ids", categories).ConfigureAwait(false);
                await CreateAndFillAsync(conn, tx, "temp_full_supplier_ids", suppliers).ConfigureAwait(false);

                var deactivatedProducts = await conn.ExecuteAsync(@"
UPDATE products
SET is_active = 0,
    remote_deleted_at = @removedAt
WHERE TRIM(COALESCE(remote_product_id, '')) <> ''
  AND COALESCE(is_active, 1) = 1
  AND NOT EXISTS (
    SELECT 1 FROM temp_full_product_ids incoming
    WHERE incoming.id = products.remote_product_id
  );",
                    new { removedAt },
                    tx).ConfigureAwait(false);
                var deactivatedCategories = await conn.ExecuteAsync(@"
UPDATE categories
SET is_active = 0,
    remote_deleted_at = @removedAt,
    remote_updated_at = @removedAt
WHERE TRIM(COALESCE(remote_category_id, '')) <> ''
  AND COALESCE(is_active, 1) = 1
  AND NOT EXISTS (
    SELECT 1 FROM temp_full_category_ids incoming
    WHERE incoming.id = categories.remote_category_id
  );",
                    new { removedAt },
                    tx).ConfigureAwait(false);
                var deactivatedSuppliers = await conn.ExecuteAsync(@"
UPDATE suppliers
SET is_active = 0,
    remote_deleted_at = @removedAt,
    remote_updated_at = @removedAt
WHERE TRIM(COALESCE(remote_supplier_id, '')) <> ''
  AND COALESCE(is_active, 1) = 1
  AND NOT EXISTS (
    SELECT 1 FROM temp_full_supplier_ids incoming
    WHERE incoming.id = suppliers.remote_supplier_id
  );",
                    new { removedAt },
                    tx).ConfigureAwait(false);
                await conn.ExecuteAsync(@"
DELETE FROM remote_catalog_pending_prices
WHERE NOT EXISTS (
  SELECT 1 FROM temp_full_product_ids incoming
  WHERE incoming.id = remote_catalog_pending_prices.remote_product_id
);",
                    transaction: tx).ConfigureAwait(false);

                tx.Commit();
                return new CatalogFullRefreshResult
                {
                    DeactivatedCategories = deactivatedCategories,
                    DeactivatedProducts = deactivatedProducts,
                    DeactivatedSuppliers = deactivatedSuppliers
                };
            }
        }

        private static async Task CreateAndFillAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string table,
            IReadOnlyList<string> values)
        {
            await conn.ExecuteAsync(
                "CREATE TEMP TABLE IF NOT EXISTS " + table + " (id TEXT PRIMARY KEY); DELETE FROM " + table + ";",
                transaction: tx).ConfigureAwait(false);
            foreach (var value in values)
            {
                await conn.ExecuteAsync(
                    "INSERT OR IGNORE INTO " + table + "(id) VALUES(@value);",
                    new { value },
                    tx).ConfigureAwait(false);
            }
        }

        private static IReadOnlyList<string> NormalizeIds(IEnumerable<string> values)
        {
            return (values ?? Enumerable.Empty<string>())
                .Select(value => (value ?? string.Empty).Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
    }

    public sealed class CatalogFullRefreshResult
    {
        public int DeactivatedCategories { get; set; }
        public int DeactivatedProducts { get; set; }
        public int DeactivatedSuppliers { get; set; }
    }
}
