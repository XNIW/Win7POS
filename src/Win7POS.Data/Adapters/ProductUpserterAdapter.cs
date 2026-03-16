using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Import;
using Win7POS.Core.Models;
using Win7POS.Data.Import;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Adapters
{
    public sealed class ProductUpserterAdapter : IProductUpserter
    {
        private readonly ProductRepository _products;
        private readonly SqliteConnection _conn;
        private readonly SqliteTransaction _tx;
        private readonly CategorySupplierResolver _resolver;

        public ProductUpserterAdapter(ProductRepository products)
        {
            _products = products;
        }

        public ProductUpserterAdapter(SqliteConnection conn, SqliteTransaction tx, CategorySupplierResolver resolver = null)
        {
            _conn = conn;
            _tx = tx;
            _resolver = resolver;
        }

        public async Task<UpsertOutcome> UpsertAsync(Product product)
        {
            Product existing;
            if (_conn != null)
            {
                existing = await _conn.QuerySingleOrDefaultAsync<Product>(
                    "SELECT id, barcode, name, unitPrice FROM products WHERE barcode = @barcode",
                    new { barcode = product.Barcode },
                    _tx).ConfigureAwait(false);

                var updated = await _conn.ExecuteAsync(@"
UPDATE products
SET name = @Name, unitPrice = @UnitPrice
WHERE barcode = @Barcode", product, _tx).ConfigureAwait(false);
                if (updated == 0)
                {
                    await _conn.ExecuteScalarAsync<long>(@"
INSERT INTO products(barcode, name, unitPrice)
VALUES(@Barcode, @Name, @UnitPrice);
SELECT last_insert_rowid();", product, _tx).ConfigureAwait(false);
                }
            }
            else
            {
                existing = await _products.GetByBarcodeAsync(product.Barcode).ConfigureAwait(false);
                await _products.UpsertAsync(product).ConfigureAwait(false);
            }

            return existing == null ? UpsertOutcome.Inserted : UpsertOutcome.Updated;
        }

        /// <summary>Upsert prodotto + product_meta. Risolve supplier_id/category_id via resolver se presente. Usato da Import CSV e XLSX unificato.</summary>
        public async Task<UpsertOutcome> UpsertAsync(ImportRow row)
        {
            if (row == null) return UpsertOutcome.Updated;
            var barcode = (row.Barcode ?? string.Empty).Trim();
            var name = row.Name ?? string.Empty;
            var unitPrice = row.UnitPrice;
            var articleCode = row.ArticleCode ?? string.Empty;
            var name2 = row.Name2 ?? string.Empty;
            var purchasePrice = row.Cost ?? 0;
            var supplierName = row.SupplierName ?? string.Empty;
            var categoryName = row.CategoryName ?? string.Empty;
            var stockQty = row.Stock ?? 0;
            int? supplierId = row.SupplierId;
            int? categoryId = row.CategoryId;

            if (_conn != null && _tx != null)
            {
                if (_resolver != null)
                {
                    if (!supplierId.HasValue && !string.IsNullOrWhiteSpace(supplierName))
                        supplierId = await _resolver.GetOrCreateSupplierIdAsync(supplierName).ConfigureAwait(false);
                    if (!categoryId.HasValue && !string.IsNullOrWhiteSpace(categoryName))
                        categoryId = await _resolver.GetOrCreateCategoryIdAsync(categoryName).ConfigureAwait(false);
                }

                var existing = await _conn.QuerySingleOrDefaultAsync<Product>(
                    "SELECT id, barcode, name, unitPrice FROM products WHERE barcode = @barcode",
                    new { barcode },
                    _tx).ConfigureAwait(false);

                var updated = await _conn.ExecuteAsync(@"
UPDATE products SET name = @name, unitPrice = @unitPrice WHERE barcode = @barcode",
                    new { barcode, name, unitPrice }, _tx).ConfigureAwait(false);
                if (updated == 0)
                {
                    await _conn.ExecuteAsync(@"
INSERT INTO products(barcode, name, unitPrice) VALUES(@barcode, @name, @unitPrice)",
                        new { barcode, name, unitPrice }, _tx).ConfigureAwait(false);
                }

                await _conn.ExecuteAsync(@"
INSERT OR REPLACE INTO product_meta(barcode, article_code, name2, purchase_price, purchase_old, retail_old, supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES(@barcode, @articleCode, @name2, @purchasePrice, 0, 0, @supplierId, @supplierName, @categoryId, @categoryName, @stockQty)",
                    new { barcode, articleCode, name2, purchasePrice, supplierId, supplierName, categoryId, categoryName, stockQty }, _tx).ConfigureAwait(false);

                return existing == null ? UpsertOutcome.Inserted : UpsertOutcome.Updated;
            }

            var p = new Product { Barcode = barcode, Name = name, UnitPrice = unitPrice };
            var existingP = await _products.GetByBarcodeAsync(barcode).ConfigureAwait(false);
            await _products.UpsertProductAndMetaInTransactionAsync(p, articleCode, name2, purchasePrice, null, supplierName, null, categoryName, stockQty).ConfigureAwait(false);
            return existingP == null ? UpsertOutcome.Inserted : UpsertOutcome.Updated;
        }
    }
}
