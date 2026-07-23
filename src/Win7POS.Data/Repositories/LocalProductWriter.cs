using System;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Core.Receipt;

namespace Win7POS.Data.Repositories
{
    /// <summary>
    /// Owns local product, metadata and local price-history mutations. The
    /// collaborator intentionally has no remote-product identity behavior.
    /// </summary>
    internal sealed class LocalProductWriter
    {
        private readonly SqliteConnectionFactory _factory;

        internal LocalProductWriter(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        internal async Task<long> UpsertAsync(Product p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            SalesReceiptContentPolicy.EnsureValidProductIdentity(p.Barcode, p.Name);
            if (ProductIdentityPolicy.IsReservedBarcode(p.Barcode))
                throw new InvalidOperationException("Barcode riservato (DISC:/MANUAL:).");

            using var conn = _factory.Open();

            var updated = await conn.ExecuteAsync(@"
UPDATE products
SET name = @Name, unitPrice = @UnitPrice, is_active = 1, remote_deleted_at = NULL
WHERE barcode = @Barcode", p).ConfigureAwait(false);

            if (updated == 0)
            {
                return await conn.ExecuteScalarAsync<long>(@"
INSERT INTO products(barcode, name, unitPrice, is_active, remote_deleted_at)
VALUES(@Barcode, @Name, @UnitPrice, 1, NULL);
SELECT last_insert_rowid();", p).ConfigureAwait(false);
            }

            return await conn.ExecuteScalarAsync<long>(
                "SELECT id FROM products WHERE barcode = @Barcode",
                new { p.Barcode }).ConfigureAwait(false);
        }

        internal async Task UpsertMetaAsync(
            string barcode,
            int purchasePrice,
            int? supplierId,
            string supplierName,
            int? categoryId,
            string categoryName,
            int stockQty)
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

        internal async Task UpsertMetaFullAsync(
            string barcode,
            string articleCode,
            string name2,
            int purchasePrice,
            int? supplierId,
            string supplierName,
            int? categoryId,
            string categoryName,
            int stockQty)
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

        internal async Task<bool> DeleteByBarcodeAsync(string barcode)
        {
            if (string.IsNullOrWhiteSpace(barcode)) return false;
            using var conn = _factory.Open();
            var rows = await conn.ExecuteAsync(@"
UPDATE products
SET is_active = 0,
    remote_deleted_at = @deletedAt
WHERE barcode = @barcode
  AND COALESCE(is_active, 1) = 1",
                new
                {
                    barcode = barcode.Trim(),
                    deletedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                }).ConfigureAwait(false);
            return rows > 0;
        }

        internal async Task<long> UpsertProductAndMetaInTransactionAsync(
            Product p,
            string articleCode,
            string name2,
            int purchasePrice,
            int? supplierId,
            string supplierName,
            int? categoryId,
            string categoryName,
            int stockQty)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            SalesReceiptContentPolicy.EnsureValidProductIdentity(p.Barcode, p.Name);
            await CatalogMutationGate.Instance.WaitAsync().ConfigureAwait(false);
            try
            {
                using var conn = _factory.Open();
                using var tx = conn.BeginTransaction();
                try
                {
                    var id = await UpsertProductAndMetaInTransactionCoreAsync(
                        conn,
                        tx,
                        p,
                        articleCode,
                        name2,
                        purchasePrice,
                        supplierId,
                        supplierName,
                        categoryId,
                        categoryName,
                        stockQty).ConfigureAwait(false);
                    tx.Commit();
                    return id;
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
            finally
            {
                CatalogMutationGate.Instance.Release();
            }
        }

        /// <summary>
        /// Applies local product and metadata mutations within the caller's
        /// transaction. It never begins, commits, or rolls back a transaction.
        /// </summary>
        internal static async Task<long> UpsertProductAndMetaInTransactionCoreAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            Product p,
            string articleCode,
            string name2,
            int purchasePrice,
            int? supplierId,
            string supplierName,
            int? categoryId,
            string categoryName,
            int stockQty)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (p == null) throw new ArgumentNullException(nameof(p));
            SalesReceiptContentPolicy.EnsureValidProductIdentity(p.Barcode, p.Name);
            if (ProductIdentityPolicy.IsReservedBarcode(p.Barcode))
                throw new InvalidOperationException("Barcode riservato (DISC:/MANUAL:).");

            var updated = await conn.ExecuteAsync(@"
UPDATE products
SET name = @Name,
    unitPrice = @UnitPrice,
    remote_deleted_at = NULL,
    is_active = 1
WHERE barcode = @Barcode", new
                {
                    p.Barcode,
                    p.Name,
                    p.UnitPrice
                }, tx).ConfigureAwait(false);

            long id;
            if (updated == 0)
            {
                id = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO products(barcode, name, unitPrice, remote_deleted_at, is_active)
VALUES(@Barcode, @Name, @UnitPrice, NULL, 1);
SELECT last_insert_rowid();", new
                {
                    p.Barcode,
                    p.Name,
                    p.UnitPrice
                }, tx).ConfigureAwait(false);
            }
            else
            {
                id = await conn.ExecuteScalarAsync<long>(
                    "SELECT id FROM products WHERE barcode = @Barcode",
                    new { p.Barcode },
                    tx).ConfigureAwait(false);
            }

            var supplierRef = await ProductMetaResolver.ResolveSupplierReferenceAsync(
                conn,
                tx,
                supplierId,
                supplierName).ConfigureAwait(false);
            var categoryRef = await ProductMetaResolver.ResolveCategoryReferenceAsync(
                conn,
                tx,
                categoryId,
                categoryName).ConfigureAwait(false);
            var hasPendingLocalStock = await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM sales_sync_outbox o
JOIN local_stock_movements m ON m.sale_id = o.sale_id
WHERE m.barcode = @Barcode
  AND o.status IN ('pending', 'retry', 'in_progress', 'failed_blocked')",
                new { p.Barcode },
                tx).ConfigureAwait(false) > 0;

            var stockQtyToWrite = stockQty;
            if (hasPendingLocalStock)
            {
                var existingStock = await conn.ExecuteScalarAsync<int?>(@"
SELECT stock_qty
FROM product_meta
WHERE barcode = @Barcode
LIMIT 1",
                    new { p.Barcode },
                    tx).ConfigureAwait(false);
                if (existingStock.HasValue)
                {
                    stockQtyToWrite = existingStock.Value;
                }
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
                    supplierId = supplierRef.Id,
                    supplierName = supplierRef.Name,
                    categoryId = categoryRef.Id,
                    categoryName = categoryRef.Name,
                    stockQty = stockQtyToWrite
                }, tx).ConfigureAwait(false);

            return id;
        }

        internal async Task UpdateProductAndMetaInTransactionAsync(
            long productId,
            string name,
            long unitPriceMinor,
            string barcode,
            string articleCode,
            string name2,
            int purchasePrice,
            int? supplierId,
            string supplierName,
            int? categoryId,
            string categoryName,
            int stockQty)
        {
            if (productId <= 0) throw new ArgumentException("invalid product id");
            SalesReceiptContentPolicy.EnsureValidProductIdentity(barcode, name);
            await CatalogMutationGate.Instance.WaitAsync().ConfigureAwait(false);
            try
            {
                using var conn = _factory.Open();
                using var tx = conn.BeginTransaction();
                try
                {
                    var rows = await conn.ExecuteAsync(
                        "UPDATE products SET name = @name, unitPrice = @unitPriceMinor WHERE id = @productId",
                        new { productId, name = name ?? string.Empty, unitPriceMinor }, tx).ConfigureAwait(false);
                    if (rows == 0) { tx.Rollback(); throw new InvalidOperationException("Product not found."); }
                    var supplierRef = await ProductMetaResolver.ResolveSupplierReferenceAsync(conn, tx, supplierId, supplierName).ConfigureAwait(false);
                    var categoryRef = await ProductMetaResolver.ResolveCategoryReferenceAsync(conn, tx, categoryId, categoryName).ConfigureAwait(false);
                    await conn.ExecuteAsync(@"
INSERT OR REPLACE INTO product_meta(barcode, article_code, name2, purchase_price, purchase_old, retail_old, supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES(@barcode, @articleCode, @name2, @purchasePrice, 0, 0, @supplierId, @supplierName, @categoryId, @categoryName, @stockQty)",
                        new
                        {
                            barcode,
                            articleCode = articleCode ?? string.Empty,
                            name2 = name2 ?? string.Empty,
                            purchasePrice,
                            supplierId = supplierRef.Id,
                            supplierName = supplierRef.Name,
                            categoryId = categoryRef.Id,
                            categoryName = categoryRef.Name,
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
            finally
            {
                CatalogMutationGate.Instance.Release();
            }
        }

        internal async Task UpdateProductAndMetaWithPriceHistoryAsync(
            long productId,
            string name,
            long unitPriceMinor,
            string barcode,
            string articleCode,
            string name2,
            int purchasePrice,
            int? supplierId,
            string supplierName,
            int? categoryId,
            string categoryName,
            int stockQty,
            string source)
        {
            if (productId <= 0) throw new ArgumentException("invalid product id");
            SalesReceiptContentPolicy.EnsureValidProductIdentity(barcode, name);
            await CatalogMutationGate.Instance.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var conn = _factory.Open())
                using (var tx = conn.BeginTransaction())
                {
                    try
                    {
                        var current = await conn.QueryFirstOrDefaultAsync<(long UnitPrice, int PurchasePrice)>(@"
SELECT p.unitPrice AS UnitPrice, COALESCE(m.purchase_price, 0) AS PurchasePrice
FROM products p LEFT JOIN product_meta m ON m.barcode = p.barcode WHERE p.id = @productId",
                            new { productId },
                            tx).ConfigureAwait(false);

                        var rows = await conn.ExecuteAsync(
                            "UPDATE products SET name = @name, unitPrice = @unitPriceMinor WHERE id = @productId",
                            new { productId, name = name ?? string.Empty, unitPriceMinor }, tx).ConfigureAwait(false);
                        if (rows == 0) { throw new InvalidOperationException("Product not found."); }
                        var supplierRef = await ProductMetaResolver.ResolveSupplierReferenceAsync(conn, tx, supplierId, supplierName).ConfigureAwait(false);
                        var categoryRef = await ProductMetaResolver.ResolveCategoryReferenceAsync(conn, tx, categoryId, categoryName).ConfigureAwait(false);
                        await conn.ExecuteAsync(@"
INSERT OR REPLACE INTO product_meta(barcode, article_code, name2, purchase_price, purchase_old, retail_old, supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES(@barcode, @articleCode, @name2, @purchasePrice, 0, 0, @supplierId, @supplierName, @categoryId, @categoryName, @stockQty)",
                            new
                            {
                                barcode,
                                articleCode = articleCode ?? string.Empty,
                                name2 = name2 ?? string.Empty,
                                purchasePrice,
                                supplierId = supplierRef.Id,
                                supplierName = supplierRef.Name,
                                categoryId = categoryRef.Id,
                                categoryName = categoryRef.Name,
                                stockQty
                            }, tx).ConfigureAwait(false);

                        var changedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                        var src = source ?? "MANUAL_EDIT";
                        var newRetail = (int)unitPriceMinor;
                        if (current.UnitPrice != unitPriceMinor)
                        {
                            await conn.ExecuteAsync(@"
INSERT INTO product_price_history(barcode, timestamp, type, old_price, new_price, source)
VALUES(@barcode, @changedAt, 'retail', @oldPrice, @newPrice, @source)",
                                new { barcode, changedAt, oldPrice = (int)current.UnitPrice, newPrice = newRetail, source = src },
                                tx).ConfigureAwait(false);
                        }

                        if (current.PurchasePrice != purchasePrice)
                        {
                            await conn.ExecuteAsync(@"
INSERT INTO product_price_history(barcode, timestamp, type, old_price, new_price, source)
VALUES(@barcode, @changedAt, 'purchase', @oldPrice, @newPrice, @source)",
                                new { barcode, changedAt, oldPrice = current.PurchasePrice, newPrice = purchasePrice, source = src },
                                tx).ConfigureAwait(false);
                        }

                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }
            finally
            {
                CatalogMutationGate.Instance.Release();
            }
        }

        internal async Task InsertPriceHistoryAsync(string barcode, string type, int newPrice, string source)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            using var conn = _factory.Open();
            await conn.ExecuteAsync(@"
INSERT INTO product_price_history(barcode, timestamp, type, old_price, new_price, source)
VALUES(@barcode, @timestamp, @type, NULL, @newPrice, @source)",
                new { barcode, timestamp, type, newPrice, source }).ConfigureAwait(false);
        }

        internal async Task UpdateProductPricesAsync(long productId, int newPurchasePrice, int newRetailPrice, string source)
        {
            if (productId <= 0) throw new ArgumentException("Invalid product id.");
            using var conn = _factory.Open();
            var product = await conn.QueryFirstOrDefaultAsync<(string Barcode, long UnitPrice)>(@"
SELECT p.barcode, p.unitPrice FROM products p WHERE p.id = @productId", new { productId }).ConfigureAwait(false);
            if (product.Barcode == null) throw new InvalidOperationException("Prodotto non trovato.");

            var purchaseCurrent = await conn.ExecuteScalarAsync<int?>(@"
SELECT purchase_price FROM product_meta WHERE barcode = @barcode", new { barcode = product.Barcode }).ConfigureAwait(false);
            var currentPurchase = purchaseCurrent ?? 0;
            var currentRetail = (int)product.UnitPrice;

            using var tx = conn.BeginTransaction();
            try
            {
                var changedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                if (currentPurchase != newPurchasePrice)
                {
                    await conn.ExecuteAsync(@"
INSERT INTO product_price_history(barcode, timestamp, type, old_price, new_price, source)
VALUES(@barcode, @changedAt, 'purchase', @oldPrice, @newPrice, @source)",
                        new { barcode = product.Barcode, changedAt, oldPrice = currentPurchase, newPrice = newPurchasePrice, source }, tx).ConfigureAwait(false);
                    var metaRows = await conn.ExecuteAsync(@"UPDATE product_meta SET purchase_price = @newPrice WHERE barcode = @barcode",
                        new { barcode = product.Barcode, newPrice = newPurchasePrice }, tx).ConfigureAwait(false);
                    if (metaRows == 0)
                        await conn.ExecuteAsync(@"
INSERT OR REPLACE INTO product_meta(barcode, article_code, name2, purchase_price, purchase_old, retail_old, supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES(@barcode, '', '', @newPrice, 0, 0, NULL, '', NULL, '', 0)",
                            new { barcode = product.Barcode, newPrice = newPurchasePrice }, tx).ConfigureAwait(false);
                }
                if (currentRetail != newRetailPrice)
                {
                    await conn.ExecuteAsync(@"
INSERT INTO product_price_history(barcode, timestamp, type, old_price, new_price, source)
VALUES(@barcode, @changedAt, 'retail', @oldPrice, @newPrice, @source)",
                        new { barcode = product.Barcode, changedAt, oldPrice = currentRetail, newPrice = newRetailPrice, source }, tx).ConfigureAwait(false);
                    await conn.ExecuteAsync(@"UPDATE products SET unitPrice = @newPrice WHERE id = @productId",
                        new { productId, newPrice = newRetailPrice }, tx).ConfigureAwait(false);
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        internal async Task<bool> UpdateAsync(long productId, string name, long unitPriceMinor)
        {
            SalesReceiptContentPolicy.EnsureValidProductIdentity(null, name);
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
