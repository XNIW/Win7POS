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
        private static readonly System.Threading.SemaphoreSlim CatalogMetaWriteGate =
            new System.Threading.SemaphoreSlim(1, 1);
        private readonly SqliteConnectionFactory _factory;

        public ProductRepository(SqliteConnectionFactory factory) => _factory = factory;

        private sealed class ProductMetaReference
        {
            public int? Id { get; set; }
            public string Name { get; set; } = string.Empty;
        }

        public async Task<Product> GetByBarcodeAsync(string barcode)
        {
            using var conn = _factory.Open();
            return await conn.QuerySingleOrDefaultAsync<Product>(
                @"SELECT id, barcode, name, unitPrice
FROM products
WHERE barcode = @barcode
  AND COALESCE(is_active, 1) = 1",
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
                    @"SELECT id, barcode, name, unitPrice
FROM products
WHERE COALESCE(is_active, 1) = 1
  AND barcode IN @barcodes",
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
                @"SELECT id, barcode, name, unitPrice
FROM products
WHERE id = @id
  AND COALESCE(is_active, 1) = 1",
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
                new { p.Barcode }
            ).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<Product>> ListAllAsync()
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<Product>(
                @"SELECT id, barcode, name, unitPrice
FROM products
WHERE COALESCE(is_active, 1) = 1
ORDER BY barcode ASC").ConfigureAwait(false);
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
                    @"SELECT id, barcode, name, unitPrice
FROM products
WHERE COALESCE(is_active, 1) = 1
ORDER BY barcode ASC LIMIT @limit",
                    new { limit }).ConfigureAwait(false);
                return all.ToList();
            }

            var like = "%" + q.Replace("%", "[%]").Replace("_", "[_]") + "%";
            var rows = await conn.QueryAsync<Product>(
                @"SELECT id, barcode, name, unitPrice
                  FROM products
                  WHERE COALESCE(is_active, 1) = 1
                    AND (barcode = @q OR name LIKE @like)
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
                    ? sql + " WHERE COALESCE(p.is_active, 1) = 1 AND m.category_id = @categoryId ORDER BY p.barcode ASC LIMIT @limit"
                    : sql + " WHERE COALESCE(p.is_active, 1) = 1 ORDER BY p.barcode ASC LIMIT @limit";
                var all = await conn.QueryAsync<ProductDetailsRow>(allSql, prm).ConfigureAwait(false);
                return all.ToList();
            }

            var whereQuery = "WHERE COALESCE(p.is_active, 1) = 1 AND (p.barcode = @q OR p.name LIKE @like)" + whereCategory;
            var rows = await conn.QueryAsync<ProductDetailsRow>(
                sql + " " + whereQuery + @"
ORDER BY CASE WHEN p.barcode = @q THEN 0 ELSE 1 END, p.barcode ASC
LIMIT @limit",
                prm).ConfigureAwait(false);
            return rows.ToList();
        }

        public async Task<int> CountDetailsAsync(string query, int? categoryId = null, int? supplierId = null)
        {
            using var conn = _factory.Open();
            var q = (query ?? string.Empty).Trim();
            var like = q.Length == 0 ? "%" : "%" + q.Replace("%", "[%]").Replace("_", "[_]") + "%";

            var where = "WHERE COALESCE(p.is_active, 1) = 1 AND ( @q = '' OR p.barcode = @q OR p.name LIKE @like )";
            if (categoryId.HasValue && categoryId.Value != 0)
                where += " AND m.category_id = @categoryId";
            if (supplierId.HasValue && supplierId.Value != 0)
                where += " AND m.supplier_id = @supplierId";

            var sql = @"
SELECT COUNT(1)
FROM products p
LEFT JOIN product_meta m ON m.barcode = p.barcode
" + where;

            return await conn.ExecuteScalarAsync<int>(sql, new { q, like, categoryId = categoryId ?? 0, supplierId = supplierId ?? 0 }).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<ProductDetailsRow>> SearchDetailsPageAsync(string query, int limit, int offset, int? categoryId = null, int? supplierId = null)
        {
            if (limit <= 0) limit = 200;
            if (offset < 0) offset = 0;

            using var conn = _factory.Open();
            var q = (query ?? string.Empty).Trim();
            var like = q.Length == 0 ? "%" : "%" + q.Replace("%", "[%]").Replace("_", "[_]") + "%";

            var where = "WHERE COALESCE(p.is_active, 1) = 1 AND ( @q = '' OR p.barcode = @q OR p.name LIKE @like )";
            if (categoryId.HasValue && categoryId.Value != 0)
                where += " AND m.category_id = @categoryId";
            if (supplierId.HasValue && supplierId.Value != 0)
                where += " AND m.supplier_id = @supplierId";

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

            var rows = await conn.QueryAsync<ProductDetailsRow>(sql, new { q, like, limit, offset, categoryId = categoryId ?? 0, supplierId = supplierId ?? 0 }).ConfigureAwait(false);
            return rows.ToList();
        }

        public async Task<ProductDetailsRow> GetDetailsByIdAsync(long productId)
        {
            if (productId <= 0) return null;
            using var conn = _factory.Open();
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
LEFT JOIN product_meta m ON m.barcode = p.barcode
WHERE p.id = @productId
  AND COALESCE(p.is_active, 1) = 1
LIMIT 1";
            return await conn.QueryFirstOrDefaultAsync<ProductDetailsRow>(sql, new { productId }).ConfigureAwait(false);
        }

        public async Task<ProductDetailsRow> GetDetailsByBarcodeAsync(string barcode)
        {
            if (string.IsNullOrWhiteSpace(barcode)) return null;
            using var conn = _factory.Open();
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
LEFT JOIN product_meta m ON m.barcode = p.barcode
WHERE p.barcode = @barcode
  AND COALESCE(p.is_active, 1) = 1
LIMIT 1";
            return await conn.QueryFirstOrDefaultAsync<ProductDetailsRow>(sql, new { barcode = barcode.Trim() }).ConfigureAwait(false);
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

        public async Task<bool> ApplyRemoteProductTombstoneAsync(string remoteProductId, string remoteDeletedAt)
        {
            if (string.IsNullOrWhiteSpace(remoteProductId)) return false;

            using var conn = _factory.Open();
            var rows = await conn.ExecuteAsync(@"
UPDATE products
SET is_active = 0,
    remote_deleted_at = @remoteDeletedAt
WHERE remote_product_id = @remoteProductId
  AND COALESCE(is_active, 1) = 1",
                new
                {
                    remoteProductId = remoteProductId.Trim(),
                    remoteDeletedAt = string.IsNullOrWhiteSpace(remoteDeletedAt)
                        ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                        : remoteDeletedAt.Trim()
                }).ConfigureAwait(false);

            return rows > 0;
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
        public async Task<long> UpsertProductAndMetaInTransactionAsync(Product p, string articleCode, string name2, int purchasePrice, int? supplierId, string supplierName, int? categoryId, string categoryName, int stockQty, string remoteProductId = null)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            if (IsReservedBarcode(p.Barcode))
                throw new InvalidOperationException("Barcode riservato (DISC:/MANUAL:).");
            await CatalogMetaWriteGate.WaitAsync().ConfigureAwait(false);
            try
            {
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                var updated = await conn.ExecuteAsync(@"
UPDATE products
SET name = @Name,
    unitPrice = @UnitPrice,
    remote_product_id = COALESCE(NULLIF(@RemoteProductId, ''), remote_product_id),
    remote_deleted_at = NULL,
    is_active = 1
WHERE barcode = @Barcode", new
                {
                    p.Barcode,
                    p.Name,
                    p.UnitPrice,
                    RemoteProductId = remoteProductId ?? string.Empty
                }, tx).ConfigureAwait(false);
                long id;
                if (updated == 0)
                {
                    id = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO products(barcode, name, unitPrice, remote_product_id, remote_deleted_at, is_active)
VALUES(@Barcode, @Name, @UnitPrice, NULLIF(@RemoteProductId, ''), NULL, 1);
SELECT last_insert_rowid();", new
                    {
                        p.Barcode,
                        p.Name,
                        p.UnitPrice,
                        RemoteProductId = remoteProductId ?? string.Empty
                    }, tx).ConfigureAwait(false);
                }
                else
                {
                    id = await conn.ExecuteScalarAsync<long>("SELECT id FROM products WHERE barcode = @Barcode", new { p.Barcode }, tx).ConfigureAwait(false);
                }
                var supplierRef = await ResolveSupplierReferenceAsync(conn, tx, supplierId, supplierName).ConfigureAwait(false);
                var categoryRef = await ResolveCategoryReferenceAsync(conn, tx, categoryId, categoryName).ConfigureAwait(false);
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
            finally
            {
                CatalogMetaWriteGate.Release();
            }
        }

        /// <summary>Update prodotto + meta in una transazione.</summary>
        public async Task UpdateProductAndMetaInTransactionAsync(long productId, string name, long unitPriceMinor, string barcode, string articleCode, string name2, int purchasePrice, int? supplierId, string supplierName, int? categoryId, string categoryName, int stockQty)
        {
            if (productId <= 0) throw new ArgumentException("invalid product id");
            await CatalogMetaWriteGate.WaitAsync().ConfigureAwait(false);
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
                var supplierRef = await ResolveSupplierReferenceAsync(conn, tx, supplierId, supplierName).ConfigureAwait(false);
                var categoryRef = await ResolveCategoryReferenceAsync(conn, tx, categoryId, categoryName).ConfigureAwait(false);
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
                CatalogMetaWriteGate.Release();
            }
        }

        /// <summary>Update prodotto + meta e scrive righe in price_history se prezzi cambiano. source es. MANUAL_EDIT.</summary>
        public async Task UpdateProductAndMetaWithPriceHistoryAsync(long productId, string name, long unitPriceMinor, string barcode, string articleCode, string name2, int purchasePrice, int? supplierId, string supplierName, int? categoryId, string categoryName, int stockQty, string source)
        {
            if (productId <= 0) throw new ArgumentException("invalid product id");
            using var conn = _factory.Open();
            var current = await conn.QueryFirstOrDefaultAsync<(long UnitPrice, int PurchasePrice)>(@"
SELECT p.unitPrice AS UnitPrice, COALESCE(m.purchase_price, 0) AS PurchasePrice
FROM products p LEFT JOIN product_meta m ON m.barcode = p.barcode WHERE p.id = @productId", new { productId }).ConfigureAwait(false);

            await CatalogMetaWriteGate.WaitAsync().ConfigureAwait(false);
            try
            {
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    var rows = await conn.ExecuteAsync(
                        "UPDATE products SET name = @name, unitPrice = @unitPriceMinor WHERE id = @productId",
                        new { productId, name = name ?? string.Empty, unitPriceMinor }, tx).ConfigureAwait(false);
                    if (rows == 0) { tx.Rollback(); throw new InvalidOperationException("Product not found."); }
                    var supplierRef = await ResolveSupplierReferenceAsync(conn, tx, supplierId, supplierName).ConfigureAwait(false);
                    var categoryRef = await ResolveCategoryReferenceAsync(conn, tx, categoryId, categoryName).ConfigureAwait(false);
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
            }
            finally
            {
                CatalogMetaWriteGate.Release();
            }

            var changedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var src = source ?? "MANUAL_EDIT";
            var newRetail = (int)unitPriceMinor;
            if (current.UnitPrice != unitPriceMinor)
            {
                await conn.ExecuteAsync(@"
INSERT INTO product_price_history(barcode, timestamp, type, old_price, new_price, source)
VALUES(@barcode, @changedAt, 'retail', @oldPrice, @newPrice, @source)",
                    new { barcode, changedAt, oldPrice = (int)current.UnitPrice, newPrice = newRetail, source = src }).ConfigureAwait(false);
            }
            if (current.PurchasePrice != purchasePrice)
            {
                await conn.ExecuteAsync(@"
INSERT INTO product_price_history(barcode, timestamp, type, old_price, new_price, source)
VALUES(@barcode, @changedAt, 'purchase', @oldPrice, @newPrice, @source)",
                    new { barcode, changedAt, oldPrice = current.PurchasePrice, newPrice = purchasePrice, source = src }).ConfigureAwait(false);
            }
        }

        private static async Task<ProductMetaReference> ResolveSupplierReferenceAsync(SqliteConnection conn, SqliteTransaction tx, int? supplierId, string supplierName)
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
                "INSERT OR IGNORE INTO suppliers(name) VALUES(@name)",
                new { name = normalizedName },
                tx).ConfigureAwait(false);
            return await FindSupplierByNormalizedNameAsync(conn, tx, normalizedName).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Supplier reference could not be resolved.");
        }

        private static async Task<ProductMetaReference> ResolveCategoryReferenceAsync(SqliteConnection conn, SqliteTransaction tx, int? categoryId, string categoryName)
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
                "INSERT OR IGNORE INTO categories(name) VALUES(@name)",
                new { name = normalizedName },
                tx).ConfigureAwait(false);
            return await FindCategoryByNormalizedNameAsync(conn, tx, normalizedName).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Category reference could not be resolved.");
        }

        private static Task<ProductMetaReference> FindSupplierByIdAsync(SqliteConnection conn, SqliteTransaction tx, int? supplierId)
        {
            if (!supplierId.HasValue || supplierId.Value == 0)
                return Task.FromResult<ProductMetaReference>(null);

            return conn.QueryFirstOrDefaultAsync<ProductMetaReference>(
                "SELECT id AS Id, name AS Name FROM suppliers WHERE id = @id LIMIT 1",
                new { id = supplierId.Value },
                tx);
        }

        private static Task<ProductMetaReference> FindCategoryByIdAsync(SqliteConnection conn, SqliteTransaction tx, int? categoryId)
        {
            if (!categoryId.HasValue || categoryId.Value == 0)
                return Task.FromResult<ProductMetaReference>(null);

            return conn.QueryFirstOrDefaultAsync<ProductMetaReference>(
                "SELECT id AS Id, name AS Name FROM categories WHERE id = @id LIMIT 1",
                new { id = categoryId.Value },
                tx);
        }

        private static Task<ProductMetaReference> FindSupplierByNormalizedNameAsync(SqliteConnection conn, SqliteTransaction tx, string normalizedName)
        {
            return conn.QueryFirstOrDefaultAsync<ProductMetaReference>(
                @"SELECT id AS Id, name AS Name
FROM suppliers
WHERE LOWER(TRIM(name)) = LOWER(@name)
ORDER BY id ASC
LIMIT 1",
                new { name = normalizedName },
                tx);
        }

        private static Task<ProductMetaReference> FindCategoryByNormalizedNameAsync(SqliteConnection conn, SqliteTransaction tx, string normalizedName)
        {
            return conn.QueryFirstOrDefaultAsync<ProductMetaReference>(
                @"SELECT id AS Id, name AS Name
FROM categories
WHERE LOWER(TRIM(name)) = LOWER(@name)
ORDER BY id ASC
LIMIT 1",
                new { name = normalizedName },
                tx);
        }

        private static bool NamesMatch(string left, string right)
        {
            return string.Equals(
                NormalizeCatalogName(left),
                NormalizeCatalogName(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeCatalogName(string name)
        {
            var value = (name ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;
            return string.Join(" ", value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }

        public async Task InsertPriceHistoryAsync(string barcode, string type, int newPrice, string source = "MANUAL")
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            using var conn = _factory.Open();
            await conn.ExecuteAsync(@"
INSERT INTO product_price_history(barcode, timestamp, type, old_price, new_price, source)
VALUES(@barcode, @timestamp, @type, NULL, @newPrice, @source)",
                new { barcode, timestamp, type, newPrice, source }).ConfigureAwait(false);
        }

        /// <summary>Storico prezzi per barcode, ordinato per data DESC.</summary>
        public async Task<IReadOnlyList<ProductPriceHistoryRow>> GetPriceHistoryByBarcodeAsync(string barcode)
        {
            if (string.IsNullOrWhiteSpace(barcode)) return Array.Empty<ProductPriceHistoryRow>();
            using var conn = _factory.Open();
            const string sql = @"
SELECT barcode AS ProductBarcode, timestamp AS ChangedAt, type AS PriceType, old_price AS OldPrice, new_price AS NewPrice, source AS Source
FROM product_price_history
WHERE barcode = @barcode
ORDER BY timestamp DESC, id DESC";
            var rows = await conn.QueryAsync<ProductPriceHistoryRow>(sql, new { barcode = barcode.Trim() }).ConfigureAwait(false);
            return rows?.ToList() ?? new List<ProductPriceHistoryRow>();
        }

        /// <summary>Tutti i prodotti con dettagli per export (products + product_meta).</summary>
        public async Task<IReadOnlyList<ProductDetailsRow>> ListAllDetailsAsync()
        {
            using var conn = _factory.Open();
            const string sql = @"
SELECT p.id AS Id, p.barcode AS Barcode, p.name AS Name, p.unitPrice AS UnitPrice,
  COALESCE(m.article_code, '') AS ArticleCode, COALESCE(m.name2, '') AS Name2,
  COALESCE(m.purchase_price, 0) AS PurchasePrice, COALESCE(m.stock_qty, 0) AS StockQty,
  m.supplier_id AS SupplierId, COALESCE(m.supplier_name, '') AS SupplierName,
  m.category_id AS CategoryId, COALESCE(m.category_name, '') AS CategoryName
FROM products p
LEFT JOIN product_meta m ON m.barcode = p.barcode
WHERE COALESCE(p.is_active, 1) = 1
ORDER BY p.barcode ASC";
            var rows = await conn.QueryAsync<ProductDetailsRow>(sql).ConfigureAwait(false);
            return rows?.ToList() ?? new List<ProductDetailsRow>();
        }

        /// <summary>Tutte le righe price_history per export.</summary>
        public async Task<IReadOnlyList<ProductPriceHistoryRow>> ListAllPriceHistoryAsync()
        {
            using var conn = _factory.Open();
            const string sql = @"
SELECT barcode AS ProductBarcode, timestamp AS ChangedAt, type AS PriceType, old_price AS OldPrice, new_price AS NewPrice, source AS Source
FROM product_price_history
ORDER BY barcode, timestamp DESC";
            var rows = await conn.QueryAsync<ProductPriceHistoryRow>(sql).ConfigureAwait(false);
            return rows?.ToList() ?? new List<ProductPriceHistoryRow>();
        }

        /// <summary>Aggiorna prezzi prodotto e scrive storico nella stessa transazione. source es. MANUAL_EDIT, IMPORT.</summary>
        public async Task UpdateProductPricesAsync(long productId, int newPurchasePrice, int newRetailPrice, string source)
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
