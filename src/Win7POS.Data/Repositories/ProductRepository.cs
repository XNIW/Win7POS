using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
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
