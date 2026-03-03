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

                var productMap = await UpsertProductsAndMetaAsync(conn, tx, workbook.Products, dryRun);
                result.ProductsUpserted = productMap.Count;

                result.PriceHistoryInserted = await InsertPriceHistoryAsync(conn, tx, workbook.PriceHistory ?? Array.Empty<PriceHistoryRow>(), productMap, dryRun);

                if (dryRun)
                    tx.Rollback();
                else
                    tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                result.Errors.Add("Import failed: " + ex.Message);
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

        private static async Task<Dictionary<string, long>> UpsertProductsAndMetaAsync(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<ProductRow> rows, bool dryRun)
        {
            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var r in rows)
            {
                if (string.IsNullOrWhiteSpace(r.Barcode)) continue;
                if (!seen.Add(r.Barcode)) continue;
                if (r.RetailPrice < 0) continue;

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
                            ArticleCode = r.ArticleCode,
                            Name2 = r.Name2,
                            PurchasePrice = r.PurchasePrice,
                            PurchaseOld = r.PurchaseOld,
                            RetailOld = r.RetailOld,
                            SupplierId = r.SupplierId,
                            SupplierName = r.SupplierName,
                            CategoryId = r.CategoryId,
                            CategoryName = r.CategoryName,
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
