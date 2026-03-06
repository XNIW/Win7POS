using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.ImportDb;
using Win7POS.Core.Models;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.ImportDb
{
    public sealed class ProductDbImporter
    {
        private readonly SqliteConnectionFactory _factory;

        public ProductDbImporter(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<ProductDbImportResult> ImportAsync(ProductDbWorkbook workbook, bool dryRun)
        {
            var result = new ProductDbImportResult();
            if (workbook?.Products == null || workbook.Products.Count == 0)
            {
                result.Errors.Add("Nessun prodotto da importare.");
                return result;
            }

            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                await UpsertSuppliersAsync(conn, tx, workbook.Suppliers ?? Array.Empty<SupplierRow>(), dryRun);
                await UpsertCategoriesAsync(conn, tx, workbook.Categories ?? Array.Empty<CategoryRow>(), dryRun);

                var productMap = await UpsertProductsAndMetaAsync(conn, tx, workbook.Products,
                    workbook.Suppliers ?? Array.Empty<SupplierRow>(),
                    workbook.Categories ?? Array.Empty<CategoryRow>(),
                    dryRun);
                result.ProductsUpserted = productMap.Count;

                result.PriceHistoryInserted = await InsertPriceHistoryAsync(conn, tx, workbook.PriceHistory ?? Array.Empty<PriceHistoryRow>(), productMap, dryRun);

                if (dryRun)
                    tx.Rollback();
                else
                    tx.Commit();
            }
            catch (Exception ex)
            {
                tx?.Rollback();
                result.Errors.Add("Import failed: " + ex.ToString());
            }

            return result;
        }

        private static async Task UpsertSuppliersAsync(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<SupplierRow> rows, bool dryRun)
        {
            if (dryRun || rows == null || rows.Count == 0) return;

            foreach (var r in rows)
            {
                if (string.IsNullOrWhiteSpace(r.Name)) continue;
                await conn.ExecuteAsync(@"INSERT OR REPLACE INTO suppliers(id, name) VALUES(@Id, @Name)",
                    new { r.Id, r.Name }, tx);
            }
        }

        private static async Task UpsertCategoriesAsync(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<CategoryRow> rows, bool dryRun)
        {
            if (dryRun || rows == null || rows.Count == 0) return;

            foreach (var r in rows)
            {
                if (string.IsNullOrWhiteSpace(r.Name)) continue;
                await conn.ExecuteAsync(@"INSERT OR REPLACE INTO categories(id, name) VALUES(@Id, @Name)",
                    new { r.Id, r.Name }, tx);
            }
        }

        private static Dictionary<string, int> BuildSupplierNameToId(IReadOnlyList<SupplierRow> suppliers)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in suppliers ?? Array.Empty<SupplierRow>())
            {
                if (string.IsNullOrWhiteSpace(s.Name)) continue;
                var key = s.Name.Trim();
                if (!map.ContainsKey(key))
                    map[key] = s.Id;
            }
            return map;
        }

        private static Dictionary<string, int> BuildCategoryNameToId(IReadOnlyList<CategoryRow> categories)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in categories ?? Array.Empty<CategoryRow>())
            {
                if (string.IsNullOrWhiteSpace(c.Name)) continue;
                var key = c.Name.Trim();
                if (!map.ContainsKey(key))
                    map[key] = c.Id;
            }
            return map;
        }

        private static async Task<Dictionary<string, long>> UpsertProductsAndMetaAsync(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<ProductRow> rows,
            IReadOnlyList<SupplierRow> suppliers, IReadOnlyList<CategoryRow> categories, bool dryRun)
        {
            var supplierByName = BuildSupplierNameToId(suppliers);
            var categoryByName = BuildCategoryNameToId(categories);

            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var r in rows)
            {
                if (string.IsNullOrWhiteSpace(r.Barcode)) continue;
                if (!seen.Add(r.Barcode)) continue;
                if (r.RetailPrice < 0) continue;

                var supplierId = r.SupplierId;
                if (!supplierId.HasValue && !string.IsNullOrWhiteSpace(r.SupplierName))
                {
                    if (supplierByName.TryGetValue(r.SupplierName.Trim(), out var sid))
                        supplierId = sid;
                }

                var categoryId = r.CategoryId;
                if (!categoryId.HasValue && !string.IsNullOrWhiteSpace(r.CategoryName))
                {
                    if (categoryByName.TryGetValue(r.CategoryName.Trim(), out var cid))
                        categoryId = cid;
                }

                var product = new Product
                {
                    Barcode = r.Barcode,
                    Name = string.IsNullOrWhiteSpace(r.Name) ? r.Barcode : r.Name,
                    UnitPrice = r.RetailPrice
                };

                if (!dryRun)
                {
                    var id = await ExecuteUpsertProductAsync(conn, tx, product);
                    map[r.Barcode] = id;

                    await conn.ExecuteAsync(@"INSERT OR REPLACE INTO product_meta(barcode, article_code, name2, purchase_price, purchase_old, retail_old, supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES(@Barcode, @ArticleCode, @Name2, @PurchasePrice, @PurchaseOld, @RetailOld, @SupplierId, @SupplierName, @CategoryId, @CategoryName, @StockQty)",
                        new
                        {
                            Barcode = r.Barcode,
                            ArticleCode = r.ArticleCode ?? string.Empty,
                            Name2 = r.Name2 ?? string.Empty,
                            PurchasePrice = r.PurchasePrice,
                            PurchaseOld = r.PurchaseOld,
                            RetailOld = r.RetailOld,
                            SupplierId = supplierId,
                            SupplierName = r.SupplierName ?? string.Empty,
                            CategoryId = categoryId,
                            CategoryName = r.CategoryName ?? string.Empty,
                            StockQty = r.StockQty
                        }, tx);
                }
                else
                {
                    map[r.Barcode] = 0;
                }
            }

            return map;
        }

        private static async Task<long> ExecuteUpsertProductAsync(SqliteConnection conn, SqliteTransaction tx, Product p)
        {
            var updated = await conn.ExecuteAsync(
                @"UPDATE products SET name = @Name, unitPrice = @UnitPrice WHERE barcode = @Barcode", p, tx);
            if (updated > 0)
            {
                return await conn.ExecuteScalarAsync<long>(
                    "SELECT id FROM products WHERE barcode = @Barcode", new { p.Barcode }, tx);
            }
            await conn.ExecuteAsync(
                @"INSERT INTO products(barcode, name, unitPrice) VALUES(@Barcode, @Name, @UnitPrice)", p, tx);
            return await conn.ExecuteScalarAsync<long>("SELECT last_insert_rowid()", null, tx);
        }

        private static async Task<int> InsertPriceHistoryAsync(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<PriceHistoryRow> rows, Dictionary<string, long> productMap, bool dryRun)
        {
            if (dryRun || rows == null || rows.Count == 0) return 0;

            var count = 0;
            foreach (var r in rows)
            {
                if (string.IsNullOrWhiteSpace(r.ProductBarcode) || string.IsNullOrWhiteSpace(r.Timestamp)) continue;

                try
                {
                    await conn.ExecuteAsync(@"
INSERT OR IGNORE INTO product_price_history(barcode, timestamp, type, old_price, new_price, source)
VALUES(@Barcode, @Timestamp, @Type, @OldPrice, @NewPrice, @Source)",
                        new
                        {
                            Barcode = r.ProductBarcode,
                            Timestamp = r.Timestamp,
                            Type = r.Type ?? "retail",
                            OldPrice = r.OldPrice,
                            NewPrice = r.NewPrice,
                            Source = r.Source ?? string.Empty
                        }, tx);
                    count++;
                }
                catch
                {
                }
            }
            return count;
        }
    }

    public sealed class ProductDbImportResult
    {
        public int ProductsUpserted { get; set; }
        public int PriceHistoryInserted { get; set; }
        public List<string> Errors { get; } = new List<string>();
    }

}
