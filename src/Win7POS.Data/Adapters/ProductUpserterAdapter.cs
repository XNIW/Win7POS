using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Import;
using Win7POS.Core.Models;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Adapters
{
    public sealed class ProductUpserterAdapter : IProductUpserter
    {
        private readonly ProductRepository _products;
        private readonly SqliteConnection _conn;
        private readonly SqliteTransaction _tx;

        public ProductUpserterAdapter(ProductRepository products)
        {
            _products = products;
        }

        public ProductUpserterAdapter(SqliteConnection conn, SqliteTransaction tx)
        {
            _conn = conn;
            _tx = tx;
        }

        public async Task<UpsertOutcome> UpsertAsync(Product product)
        {
            Product existing;
            if (_conn != null)
            {
                existing = await _conn.QuerySingleOrDefaultAsync<Product>(
                    "SELECT id, barcode, name, unitPrice FROM products WHERE barcode = @barcode",
                    new { barcode = product.Barcode },
                    _tx);

                var updated = await _conn.ExecuteAsync(@"
UPDATE products
SET name = @Name, unitPrice = @UnitPrice
WHERE barcode = @Barcode", product, _tx);
                if (updated == 0)
                {
                    await _conn.ExecuteScalarAsync<long>(@"
INSERT INTO products(barcode, name, unitPrice)
VALUES(@Barcode, @Name, @UnitPrice);
SELECT last_insert_rowid();", product, _tx);
                }
            }
            else
            {
                existing = await _products.GetByBarcodeAsync(product.Barcode);
                await _products.UpsertAsync(product);
            }

            return existing == null ? UpsertOutcome.Inserted : UpsertOutcome.Updated;
        }
    }
}
