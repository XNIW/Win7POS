using System;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Win7POS.Data.Repositories
{
    /// <summary>
    /// Resolves the normalized supplier and category references used by local
    /// product metadata writes. All work is deliberately performed through the
    /// caller-owned connection and transaction.
    /// </summary>
    internal sealed class ProductMetaResolver
    {
        internal static async Task<ProductMetaReference> ResolveSupplierReferenceAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            int? supplierId,
            string supplierName)
        {
            var normalizedName = NormalizeCatalogName(supplierName);
            var existingById = await FindSupplierByIdAsync(conn, tx, supplierId).ConfigureAwait(false);

            if (existingById != null &&
                (normalizedName.Length == 0 || NamesMatch(normalizedName, existingById.Name)))
            {
                return existingById;
            }

            if (normalizedName.Length == 0)
                return new ProductMetaReference();

            var existingByName = await FindSupplierByNormalizedNameAsync(conn, tx, normalizedName).ConfigureAwait(false);
            if (existingByName != null)
                return existingByName;

            await conn.ExecuteAsync(
                "INSERT OR IGNORE INTO suppliers(name, is_active) VALUES(@name, 1)",
                new { name = normalizedName },
                tx).ConfigureAwait(false);
            return await FindSupplierByNormalizedNameAsync(conn, tx, normalizedName).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Supplier reference could not be resolved.");
        }

        internal static async Task<ProductMetaReference> ResolveCategoryReferenceAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            int? categoryId,
            string categoryName)
        {
            var normalizedName = NormalizeCatalogName(categoryName);
            var existingById = await FindCategoryByIdAsync(conn, tx, categoryId).ConfigureAwait(false);

            if (existingById != null &&
                (normalizedName.Length == 0 || NamesMatch(normalizedName, existingById.Name)))
            {
                return existingById;
            }

            if (normalizedName.Length == 0)
                return new ProductMetaReference();

            var existingByName = await FindCategoryByNormalizedNameAsync(conn, tx, normalizedName).ConfigureAwait(false);
            if (existingByName != null)
                return existingByName;

            await conn.ExecuteAsync(
                "INSERT OR IGNORE INTO categories(name, is_active) VALUES(@name, 1)",
                new { name = normalizedName },
                tx).ConfigureAwait(false);
            return await FindCategoryByNormalizedNameAsync(conn, tx, normalizedName).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Category reference could not be resolved.");
        }

        internal static bool NamesMatch(string left, string right)
        {
            return string.Equals(
                NormalizeCatalogName(left),
                NormalizeCatalogName(right),
                StringComparison.OrdinalIgnoreCase);
        }

        internal static string NormalizeCatalogName(string name)
        {
            var value = (name ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;
            return string.Join(" ", value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static Task<ProductMetaReference> FindSupplierByIdAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            int? supplierId)
        {
            if (!supplierId.HasValue || supplierId.Value == 0)
                return Task.FromResult<ProductMetaReference>(null);

            return conn.QueryFirstOrDefaultAsync<ProductMetaReference>(
                "SELECT id AS Id, name AS Name FROM suppliers WHERE id = @id AND COALESCE(is_active, 1) = 1 LIMIT 1",
                new { id = supplierId.Value },
                tx);
        }

        private static Task<ProductMetaReference> FindCategoryByIdAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            int? categoryId)
        {
            if (!categoryId.HasValue || categoryId.Value == 0)
                return Task.FromResult<ProductMetaReference>(null);

            return conn.QueryFirstOrDefaultAsync<ProductMetaReference>(
                "SELECT id AS Id, name AS Name FROM categories WHERE id = @id AND COALESCE(is_active, 1) = 1 LIMIT 1",
                new { id = categoryId.Value },
                tx);
        }

        private static Task<ProductMetaReference> FindSupplierByNormalizedNameAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string normalizedName)
        {
            return conn.QueryFirstOrDefaultAsync<ProductMetaReference>(
                @"SELECT id AS Id, name AS Name
FROM suppliers
WHERE COALESCE(is_active, 1) = 1
  AND LOWER(TRIM(name)) = LOWER(@name)
ORDER BY id ASC
LIMIT 1",
                new { name = normalizedName },
                tx);
        }

        private static Task<ProductMetaReference> FindCategoryByNormalizedNameAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string normalizedName)
        {
            return conn.QueryFirstOrDefaultAsync<ProductMetaReference>(
                @"SELECT id AS Id, name AS Name
FROM categories
WHERE COALESCE(is_active, 1) = 1
  AND LOWER(TRIM(name)) = LOWER(@name)
ORDER BY id ASC
LIMIT 1",
                new { name = normalizedName },
                tx);
        }
    }
}
