using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Models;
using Win7POS.Core.Products;

namespace Win7POS.Data.Repositories
{
    internal sealed class ProductQueryRepository
    {
        private const int GetByBarcodesChunkSize = 900;
        private readonly SqliteConnectionFactory _factory;

        internal ProductQueryRepository(SqliteConnectionFactory factory) => _factory = factory;

        internal async Task<Product> GetByBarcodeAsync(string barcode)
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

        /// <summary>Batch lookup per ridurre query N+1. Dedup + chunking per evitare limite parametri SQLite.</summary>
        internal async Task<IReadOnlyDictionary<string, Product>> GetByBarcodesAsync(IEnumerable<string> barcodes)
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

        internal async Task<Product> GetByIdAsync(long id)
        {
            using var conn = _factory.Open();
            return await conn.QuerySingleOrDefaultAsync<Product>(
                @"SELECT id, barcode, name, unitPrice
FROM products
WHERE id = @id
  AND COALESCE(is_active, 1) = 1",
                new { id }).ConfigureAwait(false);
        }

        internal async Task<IReadOnlyList<Product>> ListAllAsync()
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<Product>(
                @"SELECT id, barcode, name, unitPrice
FROM products
WHERE COALESCE(is_active, 1) = 1
ORDER BY barcode ASC").ConfigureAwait(false);
            return rows.ToList();
        }

        internal async Task<IReadOnlyList<Product>> SearchAsync(string query, int limit)
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

        internal async Task<IReadOnlyList<ProductDetailsRow>> SearchDetailsAsync(string query, int limit, int? categoryId = null)
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

        internal async Task<int> CountDetailsAsync(string query, int? categoryId = null, int? supplierId = null)
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

        internal async Task<ProductCatalogStats> GetCatalogStatsAsync()
        {
            using var conn = _factory.Open();
            var productStats = await conn.QuerySingleAsync<ProductCatalogStats>(@"
SELECT
  COUNT(1) AS TotalProducts,
  COALESCE(SUM(COALESCE(m.stock_qty, 0)), 0) AS TotalStockUnits,
  COALESCE(SUM(CASE WHEN COALESCE(m.stock_qty, 0) <= 0 THEN 1 ELSE 0 END), 0) AS ZeroStockProducts
FROM products p
LEFT JOIN product_meta m ON m.barcode = p.barcode
WHERE COALESCE(p.is_active, 1) = 1").ConfigureAwait(false);

            productStats.TotalCategories = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM categories WHERE TRIM(COALESCE(name, '')) <> ''")
                .ConfigureAwait(false);
            productStats.TotalSuppliers = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM suppliers WHERE TRIM(COALESCE(name, '')) <> ''")
                .ConfigureAwait(false);

            if (productStats.TotalCategories == 0)
            {
                productStats.TotalCategories = await conn.ExecuteScalarAsync<int>(@"
SELECT COUNT(1)
FROM (
  SELECT DISTINCT LOWER(TRIM(COALESCE(m.category_name, ''))) AS name
  FROM products p
  LEFT JOIN product_meta m ON m.barcode = p.barcode
  WHERE COALESCE(p.is_active, 1) = 1
    AND TRIM(COALESCE(m.category_name, '')) <> ''
)").ConfigureAwait(false);
            }

            if (productStats.TotalSuppliers == 0)
            {
                productStats.TotalSuppliers = await conn.ExecuteScalarAsync<int>(@"
SELECT COUNT(1)
FROM (
  SELECT DISTINCT LOWER(TRIM(COALESCE(m.supplier_name, ''))) AS name
  FROM products p
  LEFT JOIN product_meta m ON m.barcode = p.barcode
  WHERE COALESCE(p.is_active, 1) = 1
    AND TRIM(COALESCE(m.supplier_name, '')) <> ''
)").ConfigureAwait(false);
            }

            return productStats;
        }

        internal async Task<ProductRepository.ProductDetailsPageSnapshot> SearchDetailsPageAsync(
            ProductPageFilter filter,
            ProductPagePlan plan)
        {
            if (filter == null) throw new ArgumentNullException(nameof(filter));
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (!plan.Matches(filter))
                throw new InvalidOperationException("Product page plan/filter mismatch.");
            if (plan.Cursor != null && !plan.Cursor.Matches(filter))
                throw new InvalidOperationException("Product page cursor/filter mismatch.");

            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            var q = filter.Query;
            var like = q.Length == 0 ? "%" : "%" + q.Replace("%", "[%]").Replace("_", "[_]") + "%";

            var where = "WHERE COALESCE(p.is_active, 1) = 1";
            if (q.Length > 0)
                where += " AND (p.barcode = @q OR p.name LIKE @like)";
            if (filter.CategoryId.HasValue)
                where += " AND m.category_id = @categoryId";
            if (filter.SupplierId.HasValue)
                where += " AND m.supplier_id = @supplierId";

            var parameters = new
            {
                q,
                like,
                categoryId = filter.CategoryId ?? 0,
                supplierId = filter.SupplierId ?? 0,
                limit = filter.PageSize,
                offset = plan.Offset,
                cursorRank = plan.Cursor?.ExactRank ?? 0,
                cursorBarcode = plan.Cursor?.Barcode ?? string.Empty,
                cursorId = plan.Cursor?.Id ?? 0L
            };

            var totalCount = await conn.ExecuteScalarAsync<int>(@"
SELECT COUNT(1)
FROM products p
LEFT JOIN product_meta m ON m.barcode = p.barcode
" + where, parameters, tx).ConfigureAwait(false);

            var rankExpression = q.Length == 0
                ? string.Empty
                : "CASE WHEN p.barcode = @q THEN 0 ELSE 1 END";
            var keyset = BuildProductKeysetPredicate(plan, rankExpression);
            var ordering = BuildProductPageOrdering(plan.Kind, rankExpression);
            var offset = plan.Kind == ProductPageQueryKind.OffsetFallback
                ? " OFFSET @offset"
                : string.Empty;

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
" + where + keyset + "\n" + ordering + "\nLIMIT @limit" + offset;

            var rows = (await conn.QueryAsync<ProductDetailsRow>(sql, parameters, tx).ConfigureAwait(false)).ToList();
            if (plan.Kind == ProductPageQueryKind.Reverse)
                rows.Reverse();

            tx.Commit();
            return new ProductRepository.ProductDetailsPageSnapshot(totalCount, rows);
        }

        private static string BuildProductKeysetPredicate(
            ProductPagePlan plan,
            string rankExpression)
        {
            if (plan.Kind != ProductPageQueryKind.Forward &&
                plan.Kind != ProductPageQueryKind.Reverse)
            {
                return string.Empty;
            }

            if (plan.Cursor == null)
                throw new InvalidOperationException("Keyset product paging requires a cursor.");

            var barcodeComparison = plan.Kind == ProductPageQueryKind.Forward ? ">" : "<";
            var idComparison = barcodeComparison;
            var tuplePredicate = @"
  p.barcode COLLATE BINARY " + barcodeComparison + @" @cursorBarcode COLLATE BINARY
  OR (p.barcode COLLATE BINARY = @cursorBarcode COLLATE BINARY AND p.id " + idComparison + @" @cursorId)";

            if (rankExpression.Length == 0)
            {
                var inclusiveComparison = plan.Kind == ProductPageQueryKind.Forward ? ">=" : "<=";
                return @"
AND p.barcode COLLATE BINARY " + inclusiveComparison + @" @cursorBarcode COLLATE BINARY
AND (" + tuplePredicate + "\n)";
            }

            var rankComparison = plan.Kind == ProductPageQueryKind.Forward ? ">" : "<";
            return @"
AND (
  " + rankExpression + " " + rankComparison + @" @cursorRank
  OR (
    " + rankExpression + @" = @cursorRank
    AND (" + tuplePredicate + @")
  )
)";
        }

        private static string BuildProductPageOrdering(
            ProductPageQueryKind kind,
            string rankExpression)
        {
            var direction = kind == ProductPageQueryKind.Reverse ? "DESC" : "ASC";
            var rank = rankExpression.Length == 0
                ? string.Empty
                : rankExpression + " " + direction + ", ";
            return "ORDER BY " + rank +
                   "p.barcode COLLATE BINARY " + direction +
                   ", p.id " + direction;
        }

        internal async Task<ProductDetailsRow> GetDetailsByIdAsync(long productId)
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

        internal async Task<ProductDetailsRow> GetDetailsByBarcodeAsync(string barcode)
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

        /// <summary>Storico prezzi per barcode, ordinato per data DESC.</summary>
        internal async Task<IReadOnlyList<ProductPriceHistoryRow>> GetPriceHistoryByBarcodeAsync(string barcode)
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
        internal async Task<IReadOnlyList<ProductDetailsRow>> ListAllDetailsAsync()
        {
            using var conn = _factory.Open();
            const string sql = @"
SELECT p.id AS Id, p.barcode AS Barcode, p.name AS Name, p.unitPrice AS UnitPrice,
  COALESCE(p.is_active, 1) AS IsActive,
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

        internal async Task<IReadOnlyList<ProductDetailsRow>> ListDetailsByBarcodesAsync(IEnumerable<string> barcodes)
        {
            var normalized = (barcodes ?? Enumerable.Empty<string>())
                .Where(barcode => !string.IsNullOrWhiteSpace(barcode))
                .Select(barcode => barcode.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (normalized.Length == 0)
            {
                return new List<ProductDetailsRow>();
            }

            using var conn = _factory.Open();
            var result = new List<ProductDetailsRow>();
            const int batchSize = 900;
            for (var offset = 0; offset < normalized.Length; offset += batchSize)
            {
                var batch = normalized
                    .Skip(offset)
                    .Take(batchSize)
                    .ToArray();
                var rows = await conn.QueryAsync<ProductDetailsRow>(@"
SELECT p.id AS Id, p.barcode AS Barcode, p.name AS Name, p.unitPrice AS UnitPrice,
  COALESCE(p.is_active, 1) AS IsActive,
  COALESCE(m.article_code, '') AS ArticleCode, COALESCE(m.name2, '') AS Name2,
  COALESCE(m.purchase_price, 0) AS PurchasePrice, COALESCE(m.stock_qty, 0) AS StockQty,
  m.supplier_id AS SupplierId, COALESCE(m.supplier_name, '') AS SupplierName,
  m.category_id AS CategoryId, COALESCE(m.category_name, '') AS CategoryName
FROM products p
LEFT JOIN product_meta m ON m.barcode = p.barcode
WHERE p.barcode IN @barcodes
ORDER BY p.barcode ASC",
                    new { barcodes = batch }).ConfigureAwait(false);
                result.AddRange(rows);
            }

            return result;
        }

        /// <summary>Tutte le righe price_history per export.</summary>
        internal async Task<IReadOnlyList<ProductPriceHistoryRow>> ListAllPriceHistoryAsync()
        {
            using var conn = _factory.Open();
            const string sql = @"
SELECT barcode AS ProductBarcode, timestamp AS ChangedAt, type AS PriceType, old_price AS OldPrice, new_price AS NewPrice, source AS Source
FROM product_price_history
ORDER BY barcode, timestamp DESC";
            var rows = await conn.QueryAsync<ProductPriceHistoryRow>(sql).ConfigureAwait(false);
            return rows?.ToList() ?? new List<ProductPriceHistoryRow>();
        }

        internal async Task<long> CountActiveRemoteProductsAsync()
        {
            using var conn = _factory.Open();
            return await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM products
WHERE COALESCE(is_active, 1) = 1
  AND TRIM(COALESCE(remote_product_id, '')) <> ''").ConfigureAwait(false);
        }
    }
}
