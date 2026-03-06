using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;

namespace Win7POS.Data.Repositories
{
    public sealed class ProductRepository
    {
        private readonly SqliteConnectionFactory _factory;

        public ProductRepository(SqliteConnectionFactory factory) => _factory = factory;

        public async Task<Product> GetByBarcodeAsync(string barcode)
        {
            using var conn = _factory.Open();
            return await conn.QuerySingleOrDefaultAsync<Product>(
                "SELECT id, barcode, name, unitPrice FROM products WHERE barcode = @barcode",
                new { barcode }
            ).ConfigureAwait(false);
        }

        private const int GetByBarcodesChunkSize = 900;

        /// <summary>Batch lookup per ridurre query N+1. Dedup + chunking per evitare limite parametri SQLite.</summary>
        public async Task<IReadOnlyDictionary<string, Product>> GetByBarcodesAsync(IEnumerable<string> barcodes)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in barcodes ?? Array.Empty<string>())
            {
                var t = (b ?? string.Empty).Trim();
                if (t.Length > 0) set.Add(t);
            }
            if (set.Count == 0) return new Dictionary<string, Product>();

            var list = set.ToList();
            var dict = new Dictionary<string, Product>(list.Count, StringComparer.Ordinal);
            using var conn = _factory.Open();
            for (var i = 0; i < list.Count; i += GetByBarcodesChunkSize)
            {
                var batch = list.Skip(i).Take(GetByBarcodesChunkSize).ToArray();
                if (batch.Length == 0) break;
                var rows = await conn.QueryAsync<Product>(
                    "SELECT id, barcode, name, unitPrice FROM products WHERE barcode IN @barcodes",
                    new { barcodes = batch }
                ).ConfigureAwait(false);
                foreach (var p in rows ?? Array.Empty<Product>())
                    if (!string.IsNullOrEmpty(p?.Barcode)) dict[p.Barcode] = p;
            }
            return dict;
        }

        public async Task<Product> GetByIdAsync(long id)
        {
            using var conn = _factory.Open();
            return await conn.QuerySingleOrDefaultAsync<Product>(
                "SELECT id, barcode, name, unitPrice FROM products WHERE id = @id",
                new { id }).ConfigureAwait(false);
        }

        private static bool IsReservedBarcode(string barcode)
        {
            if (string.IsNullOrEmpty(barcode)) return false;
            return barcode.StartsWith("DISC:", StringComparison.Ordinal)
                || barcode.StartsWith("MANUAL:", StringComparison.Ordinal);
        }

        public async Task<long> UpsertAsync(Product p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            if (IsReservedBarcode(p.Barcode))
                throw new InvalidOperationException("Barcode riservato (DISC:/MANUAL:).");

            using var conn = _factory.Open();

            var updated = await conn.ExecuteAsync(@"
UPDATE products
SET name = @Name, unitPrice = @UnitPrice
WHERE barcode = @Barcode", p).ConfigureAwait(false);

            if (updated == 0)
            {
                return await conn.ExecuteScalarAsync<long>(@"
INSERT INTO products(barcode, name, unitPrice)
VALUES(@Barcode, @Name, @UnitPrice);
SELECT last_insert_rowid();", p).ConfigureAwait(false);
            }

            return await conn.ExecuteScalarAsync<long>(
                "SELECT id FROM products WHERE barcode = @Barcode",
                new { p.Barcode }
            ).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<Product>> ListAllAsync()
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<Product>(
                "SELECT id, barcode, name, unitPrice FROM products ORDER BY barcode ASC").ConfigureAwait(false);
            return rows.ToList();
        }

        public async Task<IReadOnlyList<Product>> SearchAsync(string query, int limit)
        {
            if (limit <= 0) limit = 50;
            using var conn = _factory.Open();
            var q = (query ?? string.Empty).Trim();
            if (q.Length == 0)
            {
                var all = await conn.QueryAsync<Product>(
                    "SELECT id, barcode, name, unitPrice FROM products ORDER BY barcode ASC LIMIT @limit",
                    new { limit }).ConfigureAwait(false);
                return all.ToList();
            }

            var like = "%" + q.Replace("%", "[%]").Replace("_", "[_]") + "%";
            var rows = await conn.QueryAsync<Product>(
                @"SELECT id, barcode, name, unitPrice
                  FROM products
                  WHERE barcode = @q OR name LIKE @like
                  ORDER BY CASE WHEN barcode = @q THEN 0 ELSE 1 END, barcode ASC
                  LIMIT @limit",
                new { q, like, limit }).ConfigureAwait(false);
            return rows.ToList();
        }

        public async Task<IReadOnlyList<ProductDetailsRow>> SearchDetailsAsync(string query, int limit, int? categoryId = null)
        {
            if (limit <= 0) limit = 50;
            using var conn = _factory.Open();
            var q = (query ?? string.Empty).Trim();
            var like = q.Length == 0 ? "%" : "%" + q.Replace("%", "[%]").Replace("_", "[_]") + "%";

            const string sql = @"
SELECT
  p.id AS Id,
  p.barcode AS Barcode,
  p.name AS Name,
  p.unitPrice AS UnitPrice,
  COALESCE(m.article_code, '') AS ArticleCode,
  COALESCE(m.name2, '') AS Name2,
  COALESCE(m.purchase_price, 0) AS PurchasePrice,
  COALESCE(m.stock_qty, 0) AS StockQty,
  m.supplier_id AS SupplierId,
  COALESCE(m.supplier_name, '') AS SupplierName,
  m.category_id AS CategoryId,
  COALESCE(m.category_name, '') AS CategoryName
FROM products p
LEFT JOIN product_meta m ON m.barcode = p.barcode";

            var whereCategory = categoryId.HasValue && categoryId.Value != 0
                ? " AND (m.category_id = @categoryId)"
                : "";
            var prm = new { q, like, limit, categoryId = categoryId ?? 0 };

            if (q.Length == 0)
            {
                var allSql = categoryId.HasValue && categoryId.Value != 0
                    ? sql + " WHERE m.category_id = @categoryId ORDER BY p.barcode ASC LIMIT @limit"
                    : sql + " ORDER BY p.barcode ASC LIMIT @limit";
                var all = await conn.QueryAsync<ProductDetailsRow>(allSql, prm).ConfigureAwait(false);
                return all.ToList();
            }

            var whereQuery = "WHERE (p.barcode = @q OR p.name LIKE @like)" + whereCategory;
            var rows = await conn.QueryAsync<ProductDetailsRow>(
                sql + " " + whereQuery + @"
ORDER BY CASE WHEN p.barcode = @q THEN 0 ELSE 1 END, p.barcode ASC
LIMIT @limit",
                prm).ConfigureAwait(false);
            return rows.ToList();
        }

        public async Task<int> CountDetailsAsync(string query, int? categoryId = null)
        {
            using var conn = _factory.Open();
            var q = (query ?? string.Empty).Trim();
            var like = q.Length == 0 ? "%" : "%" + q.Replace("%", "[%]").Replace("_", "[_]") + "%";

            var where = "WHERE ( @q = '' OR p.barcode = @q OR p.name LIKE @like )";
            if (categoryId.HasValue && categoryId.Value != 0)
                where += " AND m.category_id = @categoryId";

            var sql = @"
SELECT COUNT(1)
FROM products p
LEFT JOIN product_meta m ON m.barcode = p.barcode
" + where;

            return await conn.ExecuteScalarAsync<int>(sql, new { q, like, categoryId = categoryId ?? 0 }).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<ProductDetailsRow>> SearchDetailsPageAsync(string query, int limit, int offset, int? categoryId = null)
        {
            if (limit <= 0) limit = 200;
            if (offset < 0) offset = 0;

            using var conn = _factory.Open();
            var q = (query ?? string.Empty).Trim();
            var like = q.Length == 0 ? "%" : "%" + q.Replace("%", "[%]").Replace("_", "[_]") + "%";

            var where = "WHERE ( @q = '' OR p.barcode = @q OR p.name LIKE @like )";
            if (categoryId.HasValue && categoryId.Value != 0)
                where += " AND m.category_id = @categoryId";

            var sql = @"
SELECT
  p.id AS Id,
  p.barcode AS Barcode,
  p.name AS Name,
  p.unitPrice AS UnitPrice,
  COALESCE(m.article_code, '') AS ArticleCode,
  COALESCE(m.name2, '') AS Name2,
  COALESCE(m.purchase_price, 0) AS PurchasePrice,
  COALESCE(m.stock_qty, 0) AS StockQty,
  m.supplier_id AS SupplierId,
  COALESCE(m.supplier_name, '') AS SupplierName,
  m.category_id AS CategoryId,
  COALESCE(m.category_name, '') AS CategoryName
FROM products p
LEFT JOIN product_meta m ON m.barcode = p.barcode
" + where + @"
ORDER BY p.barcode ASC
LIMIT @limit OFFSET @offset";

            var rows = await conn.QueryAsync<ProductDetailsRow>(sql, new { q, like, limit, offset, categoryId = categoryId ?? 0 }).ConfigureAwait(false);
            return rows.ToList();
        }

        public async Task UpsertMetaAsync(string barcode, int purchasePrice, int? supplierId, string supplierName, int? categoryId, string categoryName, int stockQty)
        {
            using var conn = _factory.Open();
            await conn.ExecuteAsync(@"
INSERT OR REPLACE INTO product_meta(barcode, article_code, name2, purchase_price, purchase_old, retail_old, supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES(@barcode, '', '', @purchasePrice, 0, 0, @supplierId, @supplierName, @categoryId, @categoryName, @stockQty)",
                new
                {
                    barcode,
                    purchasePrice,
                    supplierId,
                    supplierName = supplierName ?? string.Empty,
                    categoryId,
                    categoryName = categoryName ?? string.Empty,
                    stockQty
                }).ConfigureAwait(false);
        }

        public async Task UpsertMetaFullAsync(string barcode, string articleCode, string name2, int purchasePrice, int? supplierId, string supplierName, int? categoryId, string categoryName, int stockQty)
        {
            using var conn = _factory.Open();
            await conn.ExecuteAsync(@"
INSERT OR REPLACE INTO product_meta(barcode, article_code, name2, purchase_price, purchase_old, retail_old, supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES(@barcode, @articleCode, @name2, @purchasePrice, 0, 0, @supplierId, @supplierName, @categoryId, @categoryName, @stockQty)",
                new
                {
                    barcode,
                    articleCode = articleCode ?? string.Empty,
                    name2 = name2 ?? string.Empty,
                    purchasePrice,
                    supplierId,
                    supplierName = supplierName ?? string.Empty,
                    categoryId,
                    categoryName = categoryName ?? string.Empty,
                    stockQty
                }).ConfigureAwait(false);
        }

        public async Task<bool> DeleteByBarcodeAsync(string barcode)
        {
            if (string.IsNullOrWhiteSpace(barcode)) return false;
            using var conn = _factory.Open();
            await conn.ExecuteAsync("DELETE FROM product_meta WHERE barcode = @barcode", new { barcode }).ConfigureAwait(false);
            var rows = await conn.ExecuteAsync("DELETE FROM products WHERE barcode = @barcode", new { barcode }).ConfigureAwait(false);
            return rows > 0;
        }

        /// <summary>Upsert prodotto + meta in una transazione (robustezza negozio).</summary>
        public async Task<long> UpsertProductAndMetaInTransactionAsync(Product p, string articleCode, string name2, int purchasePrice, int? supplierId, string supplierName, int? categoryId, string categoryName, int stockQty)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            if (IsReservedBarcode(p.Barcode))
                throw new InvalidOperationException("Barcode riservato (DISC:/MANUAL:).");
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                var updated = await conn.ExecuteAsync(@"
UPDATE products SET name = @Name, unitPrice = @UnitPrice WHERE barcode = @Barcode", p, tx).ConfigureAwait(false);
                long id;
                if (updated == 0)
                {
                    id = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO products(barcode, name, unitPrice) VALUES(@Barcode, @Name, @UnitPrice);
SELECT last_insert_rowid();", p, tx).ConfigureAwait(false);
                }
                else
                {
                    id = await conn.ExecuteScalarAsync<long>("SELECT id FROM products WHERE barcode = @Barcode", new { p.Barcode }, tx).ConfigureAwait(false);
                }
                await conn.ExecuteAsync(@"
INSERT OR REPLACE INTO product_meta(barcode, article_code, name2, purchase_price, purchase_old, retail_old, supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES(@barcode, @articleCode, @name2, @purchasePrice, 0, 0, @supplierId, @supplierName, @categoryId, @categoryName, @stockQty)",
                    new
                    {
                        barcode = p.Barcode,
                        articleCode = articleCode ?? string.Empty,
                        name2 = name2 ?? string.Empty,
                        purchasePrice,
                        supplierId,
                        supplierName = supplierName ?? string.Empty,
                        categoryId,
                        categoryName = categoryName ?? string.Empty,
                        stockQty
                    }, tx).ConfigureAwait(false);
                tx.Commit();
                return id;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        /// <summary>Update prodotto + meta in una transazione.</summary>
        public async Task UpdateProductAndMetaInTransactionAsync(long productId, string name, long unitPriceMinor, string barcode, string articleCode, string name2, int purchasePrice, int? supplierId, string supplierName, int? categoryId, string categoryName, int stockQty)
        {
            if (productId <= 0) throw new ArgumentException("invalid product id");
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                var rows = await conn.ExecuteAsync(
                    "UPDATE products SET name = @name, unitPrice = @unitPriceMinor WHERE id = @productId",
                    new { productId, name = name ?? string.Empty, unitPriceMinor }, tx).ConfigureAwait(false);
                if (rows == 0) { tx.Rollback(); throw new InvalidOperationException("Product not found."); }
                await conn.ExecuteAsync(@"
INSERT OR REPLACE INTO product_meta(barcode, article_code, name2, purchase_price, purchase_old, retail_old, supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES(@barcode, @articleCode, @name2, @purchasePrice, 0, 0, @supplierId, @supplierName, @categoryId, @categoryName, @stockQty)",
                    new
                    {
                        barcode,
                        articleCode = articleCode ?? string.Empty,
                        name2 = name2 ?? string.Empty,
                        purchasePrice,
                        supplierId,
                        supplierName = supplierName ?? string.Empty,
                        categoryId,
                        categoryName = categoryName ?? string.Empty,
                        stockQty
                    }, tx).ConfigureAwait(false);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        public async Task InsertPriceHistoryAsync(string barcode, string type, int newPrice, string source = "MANUAL")
        {
            var timestamp = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            using var conn = _factory.Open();
            await conn.ExecuteAsync(@"
INSERT INTO product_price_history(barcode, timestamp, type, old_price, new_price, source)
VALUES(@barcode, @timestamp, @type, NULL, @newPrice, @source)",
                new { barcode, timestamp, type, newPrice, source }).ConfigureAwait(false);
        }

        public async Task<bool> UpdateAsync(long productId, string name, long unitPriceMinor)
        {
            using var conn = _factory.Open();
            var rows = await conn.ExecuteAsync(
                "UPDATE products SET name = @name, unitPrice = @unitPriceMinor WHERE id = @productId",
                new
                {
                    productId,
                    name = name ?? string.Empty,
                    unitPriceMinor
                }).ConfigureAwait(false);
            return rows > 0;
        }
    }
}
