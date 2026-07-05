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
        private const int PendingRemotePriceReplayBatchSize = 2000;
        private readonly SqliteConnectionFactory _factory;

        public ProductRepository(SqliteConnectionFactory factory) => _factory = factory;

        private sealed class ProductMetaReference
        {
            public int? Id { get; set; }
            public string Name { get; set; } = string.Empty;
        }

        private sealed class PendingRemotePriceRow
        {
            public long Id { get; set; }
            public string Barcode { get; set; } = string.Empty;
            public string EffectiveAt { get; set; } = string.Empty;
            public int Price { get; set; }
            public string RemotePriceId { get; set; } = string.Empty;
            public string RemoteProductId { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
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

        public async Task<bool> UpsertRemotePriceHistoryAsync(string remoteProductId, string type, int price, string timestamp, string source)
        {
            var result = await UpsertOrQueueRemotePriceHistoryAsync(
                remoteProductId,
                null,
                type,
                price,
                timestamp,
                source).ConfigureAwait(false);
            return result.Applied;
        }

        public async Task<RemotePriceHistoryApplyResult> UpsertOrQueueRemotePriceHistoryAsync(
            string remoteProductId,
            string remotePriceId,
            string type,
            int price,
            string timestamp,
            string source)
        {
            var normalizedRemoteProductId = (remoteProductId ?? string.Empty).Trim();
            var normalizedType = (type ?? string.Empty).Trim().ToUpperInvariant();
            if (normalizedRemoteProductId.Length == 0 || normalizedType.Length == 0 || price < 0)
                return RemotePriceHistoryApplyResult.Skipped();

            var normalizedTimestamp = string.IsNullOrWhiteSpace(timestamp)
                ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                : timestamp.Trim();
            var normalizedSource = string.IsNullOrWhiteSpace(source) ? "remote_catalog" : source.Trim();
            var normalizedRemotePriceId = (remotePriceId ?? string.Empty).Trim();
            using var conn = _factory.Open();
            var barcode = await conn.QuerySingleOrDefaultAsync<string>(
                @"SELECT barcode
	FROM products
	WHERE remote_product_id = @remoteProductId
	  AND COALESCE(is_active, 1) = 1
	ORDER BY id ASC
	LIMIT 1",
	                new { remoteProductId = normalizedRemoteProductId }).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(barcode))
            {
                await QueuePendingRemotePriceAsync(
                    conn,
                    normalizedRemoteProductId,
                    normalizedRemotePriceId,
                    normalizedType,
                    price,
                    normalizedTimestamp,
                    normalizedSource).ConfigureAwait(false);
                return RemotePriceHistoryApplyResult.QueuedOk();
            }

            await InsertRemotePriceHistoryAsync(
                conn,
                null,
                barcode.Trim(),
                normalizedRemotePriceId,
                normalizedType,
                price,
                normalizedTimestamp,
                normalizedSource).ConfigureAwait(false);

            await DeletePendingRemotePriceAsync(
                conn,
                null,
                null,
                normalizedRemoteProductId,
                normalizedRemotePriceId,
                normalizedType,
                price,
                normalizedTimestamp,
                normalizedSource).ConfigureAwait(false);

            return RemotePriceHistoryApplyResult.AppliedOk();
        }

        public async Task<int> ApplyPendingRemotePricesAsync()
        {
            using var conn = _factory.Open();
            var applied = 0;

            while (true)
            {
                var rows = (await conn.QueryAsync<PendingRemotePriceRow>(@"
	SELECT
	  p.id AS Id,
	  pr.barcode AS Barcode,
  p.remote_price_id AS RemotePriceId,
  p.remote_product_id AS RemoteProductId,
  p.type AS Type,
  p.price AS Price,
	  p.effective_at AS EffectiveAt,
	  COALESCE(p.source, '') AS Source
	FROM remote_catalog_pending_prices p
	JOIN (
	  SELECT remote_product_id, MIN(id) AS product_id
	  FROM products
	  WHERE COALESCE(is_active, 1) = 1
	    AND COALESCE(remote_product_id, '') <> ''
	  GROUP BY remote_product_id
	) canonical
	  ON canonical.remote_product_id = p.remote_product_id
	JOIN products pr
	  ON pr.id = canonical.product_id
	ORDER BY p.id ASC
	LIMIT @limit", new { limit = PendingRemotePriceReplayBatchSize }).ConfigureAwait(false)).ToList();

                if (rows.Count == 0)
                {
                    break;
                }

                foreach (var row in rows)
                {
                    await InsertRemotePriceHistoryAsync(
                        conn,
                        null,
                        row.Barcode,
                        row.RemotePriceId,
                        row.Type,
                        row.Price,
                        row.EffectiveAt,
                        string.IsNullOrWhiteSpace(row.Source) ? "remote_catalog" : row.Source)
                        .ConfigureAwait(false);

                    await DeletePendingRemotePriceAsync(
                        conn,
                        null,
                        row.Id,
                        row.RemoteProductId,
                        row.RemotePriceId,
                        row.Type,
                        row.Price,
                        row.EffectiveAt,
                        row.Source).ConfigureAwait(false);
                    applied += 1;
                }

                if (rows.Count < PendingRemotePriceReplayBatchSize)
                {
                    break;
                }
            }

            return applied;
        }

        public async Task<long> CountActiveRemoteProductsAsync()
        {
            using var conn = _factory.Open();
            return await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM products
WHERE COALESCE(is_active, 1) = 1
  AND COALESCE(remote_product_id, '') <> ''").ConfigureAwait(false);
        }

        private static Task<int> InsertRemotePriceHistoryAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string barcode,
            string remotePriceId,
            string type,
            int price,
            string timestamp,
            string source)
        {
            return conn.ExecuteAsync(@"
INSERT OR IGNORE INTO product_price_history(
    barcode,
    timestamp,
    type,
    old_price,
    new_price,
    source,
    remote_price_id)
VALUES(
    @barcode,
    @timestamp,
    @type,
    NULL,
    @price,
    @source,
    NULLIF(@remotePriceId, ''))",
                new
                {
                    barcode = (barcode ?? string.Empty).Trim(),
                    timestamp = string.IsNullOrWhiteSpace(timestamp)
                        ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                        : timestamp.Trim(),
                    type = (type ?? string.Empty).Trim().ToUpperInvariant(),
                    price,
                    source = string.IsNullOrWhiteSpace(source) ? "remote_catalog" : source.Trim(),
                    remotePriceId = (remotePriceId ?? string.Empty).Trim()
                },
                tx);
        }

        private static Task<int> QueuePendingRemotePriceAsync(
            SqliteConnection conn,
            string remoteProductId,
            string remotePriceId,
            string type,
            int price,
            string timestamp,
            string source)
        {
            return conn.ExecuteAsync(@"
INSERT OR IGNORE INTO remote_catalog_pending_prices(
    remote_price_id,
    remote_product_id,
    type,
    price,
    effective_at,
    source,
    created_at)
VALUES(
    NULLIF(@remotePriceId, ''),
    @remoteProductId,
    @type,
    @price,
    @effectiveAt,
    @source,
    @createdAt)",
                new
                {
                    remotePriceId = (remotePriceId ?? string.Empty).Trim(),
                    remoteProductId = (remoteProductId ?? string.Empty).Trim(),
                    type = (type ?? string.Empty).Trim().ToUpperInvariant(),
                    price,
                    effectiveAt = string.IsNullOrWhiteSpace(timestamp)
                        ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                        : timestamp.Trim(),
                    source = string.IsNullOrWhiteSpace(source) ? "remote_catalog" : source.Trim(),
                    createdAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                });
        }

        private static Task<int> DeletePendingRemotePriceAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long? id,
            string remoteProductId,
            string remotePriceId,
            string type,
            int price,
            string timestamp,
            string source)
        {
            if (id.HasValue)
            {
                return conn.ExecuteAsync(
                    "DELETE FROM remote_catalog_pending_prices WHERE id = @id",
                    new { id = id.Value },
                    tx);
            }

            var normalizedRemotePriceId = (remotePriceId ?? string.Empty).Trim();
            if (normalizedRemotePriceId.Length > 0)
            {
                return conn.ExecuteAsync(
                    "DELETE FROM remote_catalog_pending_prices WHERE remote_price_id = @remotePriceId",
                    new { remotePriceId = normalizedRemotePriceId },
                    tx);
            }

            return conn.ExecuteAsync(@"
DELETE FROM remote_catalog_pending_prices
WHERE remote_price_id IS NULL
  AND remote_product_id = @remoteProductId
  AND type = @type
  AND effective_at = @effectiveAt
  AND price = @price
  AND COALESCE(source, '') = @source",
                new
                {
                    remoteProductId = (remoteProductId ?? string.Empty).Trim(),
                    type = (type ?? string.Empty).Trim().ToUpperInvariant(),
                    effectiveAt = string.IsNullOrWhiteSpace(timestamp)
                        ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                        : timestamp.Trim(),
                    price,
                    source = string.IsNullOrWhiteSpace(source) ? "remote_catalog" : source.Trim()
                },
                tx);
        }

        public async Task<bool> DeleteByBarcodeAsync(string barcode)
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

        /// <summary>Upsert prodotto + meta in una transazione (robustezza negozio).</summary>
        public async Task<long> UpsertProductAndMetaInTransactionAsync(Product p, string articleCode, string name2, int purchasePrice, int? supplierId, string supplierName, int? categoryId, string categoryName, int stockQty, string remoteProductId = null)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            if (IsReservedBarcode(p.Barcode))
                throw new InvalidOperationException("Barcode riservato (DISC:/MANUAL:).");

            var normalizedRemoteProductId = (remoteProductId ?? string.Empty).Trim();
            await CatalogMetaWriteGate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var conn = _factory.Open();
                using var tx = conn.BeginTransaction();
                try
                {
                    if (normalizedRemoteProductId.Length > 0)
                    {
                        await CanonicalizeRemoteProductBeforeUpsertAsync(
                            conn,
                            tx,
                            normalizedRemoteProductId,
                            p.Barcode,
                            p.Name,
                            p.UnitPrice).ConfigureAwait(false);
                    }

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
                        RemoteProductId = normalizedRemoteProductId
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
                            RemoteProductId = normalizedRemoteProductId
                        }, tx).ConfigureAwait(false);
                    }
                    else
                    {
                        id = await conn.ExecuteScalarAsync<long>(
                            "SELECT id FROM products WHERE barcode = @Barcode",
                            new { p.Barcode },
                            tx).ConfigureAwait(false);
                    }

                    if (normalizedRemoteProductId.Length > 0)
                    {
                        await DeactivateRemoteProductDuplicatesAsync(
                            conn,
                            tx,
                            normalizedRemoteProductId,
                            p.Barcode).ConfigureAwait(false);
                    }

                    var supplierRef = await ResolveSupplierReferenceAsync(conn, tx, supplierId, supplierName).ConfigureAwait(false);
                    var categoryRef = await ResolveCategoryReferenceAsync(conn, tx, categoryId, categoryName).ConfigureAwait(false);
                    var hasPendingLocalStock = await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM sales_sync_outbox o
JOIN local_stock_movements m ON m.sale_id = o.sale_id
WHERE (
    m.barcode = @Barcode
    OR (
        @RemoteProductId <> ''
        AND EXISTS (
            SELECT 1
            FROM products remote_product
            WHERE remote_product.remote_product_id = @RemoteProductId
              AND remote_product.barcode = m.barcode
        )
    )
)
AND (o.status IN ('pending', 'retry') OR o.status = 'failed_blocked')",
                        new { p.Barcode, RemoteProductId = normalizedRemoteProductId },
                        tx).ConfigureAwait(false) > 0;

                    var stockQtyToWrite = stockQty;
                    if (hasPendingLocalStock)
                    {
                        var existingStock = await conn.ExecuteScalarAsync<int?>(@"
SELECT stock_qty
FROM product_meta
WHERE barcode = @Barcode
   OR (
       @RemoteProductId <> ''
       AND barcode IN (
           SELECT barcode
           FROM products
           WHERE remote_product_id = @RemoteProductId
       )
   )
ORDER BY CASE WHEN barcode = @Barcode THEN 0 ELSE 1 END
LIMIT 1",
                            new { p.Barcode, RemoteProductId = normalizedRemoteProductId },
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
                        },
                        tx).ConfigureAwait(false);

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

        private static async Task CanonicalizeRemoteProductBeforeUpsertAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remoteProductId,
            string barcode,
            string name,
            long unitPrice)
        {
            var existingRemoteBarcode = await conn.QuerySingleOrDefaultAsync<string>(@"
SELECT barcode
FROM products
WHERE remote_product_id = @remoteProductId
  AND barcode <> @barcode
ORDER BY COALESCE(is_active, 1) DESC, id ASC
LIMIT 1",
                new { remoteProductId, barcode },
                tx).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(existingRemoteBarcode))
            {
                return;
            }

            var targetExists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM products WHERE barcode = @barcode",
                new { barcode },
                tx).ConfigureAwait(false) > 0;
            if (targetExists)
            {
                return;
            }

            await conn.ExecuteAsync(@"
UPDATE products
SET barcode = @barcode,
    name = @name,
    unitPrice = @unitPrice,
    remote_deleted_at = NULL,
    is_active = 1
WHERE remote_product_id = @remoteProductId
  AND barcode = @existingRemoteBarcode",
                new
                {
                    remoteProductId,
                    existingRemoteBarcode,
                    barcode,
                    name,
                    unitPrice
                },
                tx).ConfigureAwait(false);

            await conn.ExecuteAsync(@"
UPDATE product_meta
SET barcode = @barcode
WHERE barcode = @existingRemoteBarcode
  AND NOT EXISTS (
      SELECT 1
      FROM product_meta existing
      WHERE existing.barcode = @barcode
  )",
                new { existingRemoteBarcode, barcode },
                tx).ConfigureAwait(false);
        }

        private static Task<int> DeactivateRemoteProductDuplicatesAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remoteProductId,
            string barcode)
        {
            var remoteDeletedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            return conn.ExecuteAsync(@"
UPDATE products
SET is_active = 0,
    remote_deleted_at = @remoteDeletedAt
WHERE remote_product_id = @remoteProductId
  AND barcode <> @barcode
  AND COALESCE(is_active, 1) = 1",
                new { remoteProductId, barcode, remoteDeletedAt },
                tx);
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
            await CatalogMetaWriteGate.WaitAsync().ConfigureAwait(false);
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
                CatalogMetaWriteGate.Release();
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

        public async Task<IReadOnlyList<ProductDetailsRow>> ListDetailsByBarcodesAsync(IEnumerable<string> barcodes)
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

    public sealed class RemotePriceHistoryApplyResult
    {
        private RemotePriceHistoryApplyResult(bool applied, bool queued)
        {
            Applied = applied;
            Queued = queued;
        }

        public bool Applied { get; }
        public bool Queued { get; }

        public static RemotePriceHistoryApplyResult AppliedOk()
        {
            return new RemotePriceHistoryApplyResult(true, false);
        }

        public static RemotePriceHistoryApplyResult QueuedOk()
        {
            return new RemotePriceHistoryApplyResult(false, true);
        }

        public static RemotePriceHistoryApplyResult Skipped()
        {
            return new RemotePriceHistoryApplyResult(false, false);
        }
    }
}
