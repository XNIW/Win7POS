using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
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
            );
        }

        public async Task<Product> GetByIdAsync(long id)
        {
            using var conn = _factory.Open();
            return await conn.QuerySingleOrDefaultAsync<Product>(
                "SELECT id, barcode, name, unitPrice FROM products WHERE id = @id",
                new { id });
        }

        public async Task<long> UpsertAsync(Product p)
        {
            using var conn = _factory.Open();

            var updated = await conn.ExecuteAsync(@"
UPDATE products
SET name = @Name, unitPrice = @UnitPrice
WHERE barcode = @Barcode", p);

            if (updated == 0)
            {
                return await conn.ExecuteScalarAsync<long>(@"
INSERT INTO products(barcode, name, unitPrice)
VALUES(@Barcode, @Name, @UnitPrice);
SELECT last_insert_rowid();", p);
            }

            return await conn.ExecuteScalarAsync<long>(
                "SELECT id FROM products WHERE barcode = @Barcode",
                new { p.Barcode }
            );
        }

        public async Task<IReadOnlyList<Product>> ListAllAsync()
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<Product>(
                "SELECT id, barcode, name, unitPrice FROM products ORDER BY barcode ASC");
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
                    new { limit });
                return all.ToList();
            }

            var like = "%" + q.Replace("%", "[%]").Replace("_", "[_]") + "%";
            var rows = await conn.QueryAsync<Product>(
                @"SELECT id, barcode, name, unitPrice
                  FROM products
                  WHERE barcode = @q OR name LIKE @like
                  ORDER BY CASE WHEN barcode = @q THEN 0 ELSE 1 END, barcode ASC
                  LIMIT @limit",
                new { q, like, limit });
            return rows.ToList();
        }

        public async Task<IReadOnlyList<ProductDetailsRow>> SearchDetailsAsync(string query, int limit)
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
  COALESCE(m.supplier_name, '') AS SupplierName,
  COALESCE(m.category_name, '') AS CategoryName
FROM products p
LEFT JOIN product_meta m ON m.barcode = p.barcode";

            if (q.Length == 0)
            {
                var all = await conn.QueryAsync<ProductDetailsRow>(
                    sql + " ORDER BY p.barcode ASC LIMIT @limit",
                    new { limit });
                return all.ToList();
            }

            var rows = await conn.QueryAsync<ProductDetailsRow>(
                sql + @"
WHERE p.barcode = @q OR p.name LIKE @like
ORDER BY CASE WHEN p.barcode = @q THEN 0 ELSE 1 END, p.barcode ASC
LIMIT @limit",
                new { q, like, limit });
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
                });
        }

        public async Task InsertPriceHistoryAsync(string barcode, string type, int newPrice, string source = "MANUAL")
        {
            var timestamp = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            using var conn = _factory.Open();
            await conn.ExecuteAsync(@"
INSERT INTO product_price_history(barcode, timestamp, type, old_price, new_price, source)
VALUES(@barcode, @timestamp, @type, NULL, @newPrice, @source)",
                new { barcode, timestamp, type, newPrice, source });
        }

        public async Task<bool> UpdateAsync(long productId, string name, int unitPriceMinor)
        {
            using var conn = _factory.Open();
            var rows = await conn.ExecuteAsync(
                "UPDATE products SET name = @name, unitPrice = @unitPriceMinor WHERE id = @productId",
                new
                {
                    productId,
                    name = name ?? string.Empty,
                    unitPriceMinor
                });
            return rows > 0;
        }
    }
}
