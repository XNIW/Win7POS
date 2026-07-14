using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Import;
using Win7POS.Core.ImportDb;
using Win7POS.Data.Adapters;

namespace Win7POS.Data.Import
{
    public sealed class ProductImportApplyService
    {
        private readonly SqliteConnectionFactory _factory;

        public ProductImportApplyService(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<ImportApplyResult> ApplyAsync(
            IReadOnlyList<ImportRow> rows,
            ImportApplyOptions options,
            IReadOnlyList<SupplierRow> dedicatedSuppliers = null,
            IReadOnlyList<CategoryRow> dedicatedCategories = null,
            IReadOnlyList<PriceHistoryRow> priceHistoryRows = null)
        {
            options = options ?? new ImportApplyOptions();
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    var usableSuppliers = (dedicatedSuppliers ?? Array.Empty<SupplierRow>())
                        .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name))
                        .ToList();
                    var usableCategories = (dedicatedCategories ?? Array.Empty<CategoryRow>())
                        .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name))
                        .ToList();

                    await EnsureDedicatedRowsDoNotTargetRemoteIdentitiesAsync(
                        conn,
                        tx,
                        "suppliers",
                        "remote_supplier_id",
                        usableSuppliers.Select(row => row.Id)).ConfigureAwait(false);
                    await EnsureDedicatedRowsDoNotTargetRemoteIdentitiesAsync(
                        conn,
                        tx,
                        "categories",
                        "remote_category_id",
                        usableCategories.Select(row => row.Id)).ConfigureAwait(false);

                    var supplierOverwrites = await CountSheetNameOverwritesAsync(conn, tx, "suppliers", usableSuppliers.Select(r => new IdNamePair
                    {
                        Id = r.Id,
                        Name = CategorySupplierResolver.Normalize(r.Name)
                    })).ConfigureAwait(false);
                    var categoryOverwrites = await CountSheetNameOverwritesAsync(conn, tx, "categories", usableCategories.Select(r => new IdNamePair
                    {
                        Id = r.Id,
                        Name = CategorySupplierResolver.Normalize(r.Name)
                    })).ConfigureAwait(false);

                    if (!options.DryRun && usableSuppliers.Count > 0)
                    {
                        foreach (var r in usableSuppliers)
                        {
                            await conn.ExecuteAsync(
                                @"INSERT INTO suppliers(id, name, is_active)
VALUES(@Id, @Name, 1)
ON CONFLICT(id) DO UPDATE SET
  name = excluded.name,
  is_active = 1,
  remote_deleted_at = NULL
WHERE suppliers.remote_supplier_id IS NULL;",
                                new { r.Id, r.Name },
                                tx).ConfigureAwait(false);
                        }
                    }

                    if (!options.DryRun && usableCategories.Count > 0)
                    {
                        foreach (var r in usableCategories)
                        {
                            await conn.ExecuteAsync(
                                @"INSERT INTO categories(id, name, is_active)
VALUES(@Id, @Name, 1)
ON CONFLICT(id) DO UPDATE SET
  name = excluded.name,
  is_active = 1,
  remote_deleted_at = NULL
WHERE categories.remote_category_id IS NULL;",
                                new { r.Id, r.Name },
                                tx).ConfigureAwait(false);
                        }
                    }

                    var resolver = new CategorySupplierResolver(conn, tx, usableSuppliers, usableCategories);
                    IProductUpserter upserter = new ProductUpserterAdapter(conn, tx, resolver);
                    var lookup = new ProductSnapshotLookupAdapter(conn, tx);
                    var applier = new ImportApplier(upserter, lookup);
                    var result = await applier.ApplyAsync(rows, options).ConfigureAwait(false);

                    if (result.ErrorsCount > 0)
                    {
                        tx.Rollback();
                        throw new InvalidOperationException("Apply failed with row errors.");
                    }

                    var priceHistory = await InsertPriceHistoryAsync(conn, tx, priceHistoryRows).ConfigureAwait(false);
                    result.PriceHistoryInserted = priceHistory.Inserted;
                    result.PriceHistorySkipped = priceHistory.Skipped;
                    result.SuppliersFromSheet = resolver.SuppliersFromSheet;
                    result.SuppliersFromDb = resolver.SuppliersFromDb;
                    result.SuppliersCreated = resolver.SuppliersCreated;
                    result.CategoriesFromSheet = resolver.CategoriesFromSheet;
                    result.CategoriesFromDb = resolver.CategoriesFromDb;
                    result.CategoriesCreated = resolver.CategoriesCreated;
                    result.SupplierNameOverwrittenCount = supplierOverwrites;
                    result.CategoryNameOverwrittenCount = categoryOverwrites;

                    if (options.DryRun) tx.Rollback();
                    else tx.Commit();
                    return result;
                }
                catch
                {
                    try { tx.Rollback(); } catch { }
                    throw;
                }
            }
        }

        private static async Task<PriceHistoryInsertResult> InsertPriceHistoryAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            IReadOnlyList<PriceHistoryRow> rows)
        {
            var result = new PriceHistoryInsertResult();
            if (rows == null || rows.Count == 0) return result;

            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row?.ProductBarcode) || string.IsNullOrWhiteSpace(row.Timestamp))
                {
                    result.Skipped++;
                    continue;
                }

                try
                {
                    var affected = await conn.ExecuteAsync(@"
INSERT OR IGNORE INTO product_price_history(barcode, timestamp, type, old_price, new_price, source)
VALUES(@Barcode, @Timestamp, @Type, @OldPrice, @NewPrice, @Source)",
                        new
                        {
                            Barcode = row.ProductBarcode,
                            Timestamp = row.Timestamp,
                            Type = string.IsNullOrWhiteSpace(row.Type) ? "retail" : row.Type,
                            OldPrice = row.OldPrice,
                            NewPrice = row.NewPrice,
                            Source = string.IsNullOrWhiteSpace(row.Source) ? "IMPORT" : row.Source
                        }, tx).ConfigureAwait(false);

                    if (affected > 0) result.Inserted++;
                    else result.Skipped++;
                }
                catch
                {
                    result.Skipped++;
                }
            }

            return result;
        }

        private static async Task<int> CountSheetNameOverwritesAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string tableName,
            IEnumerable<IdNamePair> rows)
        {
            if (!string.Equals(tableName, "suppliers", StringComparison.Ordinal) &&
                !string.Equals(tableName, "categories", StringComparison.Ordinal))
            {
                throw new ArgumentOutOfRangeException(nameof(tableName));
            }

            var finalById = new Dictionary<int, string>();
            foreach (var row in rows ?? Enumerable.Empty<IdNamePair>())
            {
                if (row == null) continue;
                var normalized = CategorySupplierResolver.Normalize(row.Name);
                if (normalized.Length == 0) continue;
                finalById[row.Id] = normalized;
            }

            if (finalById.Count == 0) return 0;

            var existingRows = await conn.QueryAsync<IdNamePair>(
                "SELECT id AS Id, name AS Name FROM " + tableName + " WHERE id IN @ids",
                new { ids = finalById.Keys.ToArray() },
                tx).ConfigureAwait(false);

            var existingById = new Dictionary<int, string>();
            foreach (var existing in existingRows ?? Enumerable.Empty<IdNamePair>())
                existingById[existing.Id] = existing.Name ?? string.Empty;

            var count = 0;
            foreach (var pair in finalById)
            {
                if (existingById.TryGetValue(pair.Key, out var currentName) &&
                    !string.Equals(CategorySupplierResolver.Normalize(currentName), pair.Value, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }

            return count;
        }

        private static async Task EnsureDedicatedRowsDoNotTargetRemoteIdentitiesAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string tableName,
            string remoteIdColumn,
            IEnumerable<int> ids)
        {
            var validTarget =
                (string.Equals(tableName, "suppliers", StringComparison.Ordinal) &&
                 string.Equals(remoteIdColumn, "remote_supplier_id", StringComparison.Ordinal)) ||
                (string.Equals(tableName, "categories", StringComparison.Ordinal) &&
                 string.Equals(remoteIdColumn, "remote_category_id", StringComparison.Ordinal));
            if (!validTarget)
            {
                throw new ArgumentOutOfRangeException(nameof(tableName));
            }

            var distinctIds = (ids ?? Enumerable.Empty<int>()).Distinct().ToArray();
            if (distinctIds.Length == 0)
            {
                return;
            }

            var conflicts = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM " + tableName +
                " WHERE id IN @ids AND " + remoteIdColumn + " IS NOT NULL",
                new { ids = distinctIds },
                tx).ConfigureAwait(false);
            if (conflicts > 0)
            {
                throw new InvalidOperationException(
                    "Dedicated local catalog rows cannot overwrite remote category or supplier identities.");
            }
        }

        private sealed class IdNamePair
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;
        }

        private sealed class PriceHistoryInsertResult
        {
            public int Inserted { get; set; }
            public int Skipped { get; set; }
        }
    }
}
