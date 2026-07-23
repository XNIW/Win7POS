using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Core.Products;
using Win7POS.Core.Receipt;

namespace Win7POS.Data.Repositories
{
    public sealed class ProductRepository
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly ProductQueryRepository _queries;
        private readonly LocalProductWriter _localProductWriter;
        private readonly RemotePriceHistoryRepository _remotePriceHistory;

        public ProductRepository(SqliteConnectionFactory factory)
        {
            _factory = factory;
            _queries = new ProductQueryRepository(factory);
            _localProductWriter = new LocalProductWriter(factory);
            _remotePriceHistory = new RemotePriceHistoryRepository(factory);
        }

        internal sealed class ProductMetaReference
        {
            public int? Id { get; set; }
            public string Name { get; set; } = string.Empty;
        }

        public sealed class ProductDetailsPageSnapshot
        {
            internal ProductDetailsPageSnapshot(int totalCount, IReadOnlyList<ProductDetailsRow> items)
            {
                TotalCount = totalCount;
                Items = items ?? throw new ArgumentNullException(nameof(items));
            }

            public int TotalCount { get; }
            public IReadOnlyList<ProductDetailsRow> Items { get; }
        }

        public Task<Product> GetByBarcodeAsync(string barcode) => _queries.GetByBarcodeAsync(barcode);

        /// <summary>Batch lookup per ridurre query N+1. Dedup + chunking per evitare limite parametri SQLite.</summary>
        public Task<IReadOnlyDictionary<string, Product>> GetByBarcodesAsync(IEnumerable<string> barcodes) =>
            _queries.GetByBarcodesAsync(barcodes);

        public Task<Product> GetByIdAsync(long id) => _queries.GetByIdAsync(id);

        internal static bool IsReservedBarcode(string barcode)
        {
            return LocalProductWriter.IsReservedBarcode(barcode);
        }

        public Task<long> UpsertAsync(Product p) => _localProductWriter.UpsertAsync(p);

        public Task<IReadOnlyList<Product>> ListAllAsync() => _queries.ListAllAsync();

        public Task<IReadOnlyList<Product>> SearchAsync(string query, int limit) =>
            _queries.SearchAsync(query, limit);

        public Task<IReadOnlyList<ProductDetailsRow>> SearchDetailsAsync(string query, int limit, int? categoryId = null) =>
            _queries.SearchDetailsAsync(query, limit, categoryId);

        public Task<int> CountDetailsAsync(string query, int? categoryId = null, int? supplierId = null) =>
            _queries.CountDetailsAsync(query, categoryId, supplierId);

        public Task<ProductCatalogStats> GetCatalogStatsAsync() => _queries.GetCatalogStatsAsync();

        public Task<ProductDetailsPageSnapshot> SearchDetailsPageAsync(
            ProductPageFilter filter,
            ProductPagePlan plan) =>
            _queries.SearchDetailsPageAsync(filter, plan);

        public Task<ProductDetailsRow> GetDetailsByIdAsync(long productId) =>
            _queries.GetDetailsByIdAsync(productId);

        public Task<ProductDetailsRow> GetDetailsByBarcodeAsync(string barcode) =>
            _queries.GetDetailsByBarcodeAsync(barcode);

        public Task UpsertMetaAsync(string barcode, int purchasePrice, int? supplierId, string supplierName, int? categoryId, string categoryName, int stockQty) =>
            _localProductWriter.UpsertMetaAsync(barcode, purchasePrice, supplierId, supplierName, categoryId, categoryName, stockQty);

        public Task UpsertMetaFullAsync(string barcode, string articleCode, string name2, int purchasePrice, int? supplierId, string supplierName, int? categoryId, string categoryName, int stockQty) =>
            _localProductWriter.UpsertMetaFullAsync(barcode, articleCode, name2, purchasePrice, supplierId, supplierName, categoryId, categoryName, stockQty);

        public async Task<bool> ApplyRemoteProductTombstoneAsync(string remoteProductId, string remoteDeletedAt)
        {
            using var conn = _factory.Open();
            return await ApplyRemoteProductTombstoneInTransactionAsync(
                conn,
                null,
                remoteProductId,
                remoteDeletedAt).ConfigureAwait(false);
        }

        internal static async Task<bool> ApplyRemoteProductTombstoneInTransactionAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remoteProductId,
            string remoteDeletedAt)
        {
            if (string.IsNullOrWhiteSpace(remoteProductId)) return false;

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
                },
                tx).ConfigureAwait(false);

            return rows > 0;
        }

        public Task<bool> UpsertRemotePriceHistoryAsync(
            string remoteProductId,
            string type,
            int price,
            string timestamp,
            string source) =>
            _remotePriceHistory.UpsertRemotePriceHistoryAsync(
                remoteProductId,
                type,
                price,
                timestamp,
                source);

        public Task<RemotePriceHistoryApplyResult> UpsertOrQueueRemotePriceHistoryAsync(
            string remoteProductId,
            string remotePriceId,
            string type,
            int price,
            string timestamp,
            string source) =>
            _remotePriceHistory.UpsertOrQueueRemotePriceHistoryAsync(
                remoteProductId,
                remotePriceId,
                type,
                price,
                timestamp,
                source);

        public Task<int> ApplyPendingRemotePricesAsync() =>
            _remotePriceHistory.ApplyPendingRemotePricesAsync();

        public Task<long> CountActiveRemoteProductsAsync() => _queries.CountActiveRemoteProductsAsync();

        public Task<bool> DeleteByBarcodeAsync(string barcode) => _localProductWriter.DeleteByBarcodeAsync(barcode);

        /// <summary>Upsert prodotto + meta in una transazione (robustezza negozio).</summary>
        public async Task<long> UpsertProductAndMetaInTransactionAsync(Product p, string articleCode, string name2, int purchasePrice, int? supplierId, string supplierName, int? categoryId, string categoryName, int stockQty, string remoteProductId = null)
        {
            if (string.IsNullOrWhiteSpace(remoteProductId))
            {
                return await _localProductWriter.UpsertProductAndMetaInTransactionAsync(
                    p,
                    articleCode,
                    name2,
                    purchasePrice,
                    supplierId,
                    supplierName,
                    categoryId,
                    categoryName,
                    stockQty).ConfigureAwait(false);
            }

            // Remote identity canonicalization remains owned here until E4.
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
                        stockQty,
                        remoteProductId).ConfigureAwait(false);
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
            int stockQty,
            string remoteProductId,
            CatalogProductPreparedCommands preparedCommands = null,
            CatalogProductBatchContext batchContext = null)
        {
            if (string.IsNullOrWhiteSpace(remoteProductId))
            {
                return await LocalProductWriter.UpsertProductAndMetaInTransactionCoreAsync(
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
            }

            if (p == null) throw new ArgumentNullException(nameof(p));
            SalesReceiptContentPolicy.EnsureValidProductIdentity(p.Barcode, p.Name);
            if (IsReservedBarcode(p.Barcode))
                throw new InvalidOperationException("Barcode riservato (DISC:/MANUAL:).");

            var normalizedRemoteProductId = (remoteProductId ?? string.Empty).Trim();
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

            var updated = preparedCommands == null
                ? await conn.ExecuteAsync(@"
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
                }, tx).ConfigureAwait(false)
                : await preparedCommands.UpdateProductAsync(
                    p.Barcode,
                    p.Name,
                    p.UnitPrice,
                    normalizedRemoteProductId).ConfigureAwait(false);

            long id;
            if (updated == 0)
            {
                if (preparedCommands == null)
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
                    id = await preparedCommands.InsertProductAsync(
                        p.Barcode,
                        p.Name,
                        p.UnitPrice,
                        normalizedRemoteProductId).ConfigureAwait(false);
                }
            }
            else
            {
                id = preparedCommands == null
                    ? await conn.ExecuteScalarAsync<long>(
                        "SELECT id FROM products WHERE barcode = @Barcode",
                        new { p.Barcode },
                        tx).ConfigureAwait(false)
                    : await preparedCommands.GetProductIdAsync(p.Barcode).ConfigureAwait(false);
            }

            if (normalizedRemoteProductId.Length > 0)
            {
                await DeactivateRemoteProductDuplicatesAsync(
                    conn,
                    tx,
                    normalizedRemoteProductId,
                    p.Barcode).ConfigureAwait(false);
            }

            var supplierRef = batchContext == null
                ? await ProductMetaResolver.ResolveSupplierReferenceAsync(conn, tx, supplierId, supplierName).ConfigureAwait(false)
                : await batchContext.ResolveSupplierAsync(conn, tx, supplierId, supplierName).ConfigureAwait(false);
            var categoryRef = batchContext == null
                ? await ProductMetaResolver.ResolveCategoryReferenceAsync(conn, tx, categoryId, categoryName).ConfigureAwait(false)
                : await batchContext.ResolveCategoryAsync(conn, tx, categoryId, categoryName).ConfigureAwait(false);
            var hasPendingLocalStock = batchContext == null
                ? await conn.ExecuteScalarAsync<long>(@"
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
AND o.status IN ('pending', 'retry', 'in_progress', 'failed_blocked')",
                    new { p.Barcode, RemoteProductId = normalizedRemoteProductId },
                    tx).ConfigureAwait(false) > 0
                : batchContext.HasPendingLocalStock(p.Barcode, normalizedRemoteProductId);

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

            if (preparedCommands == null)
            {
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
            }
            else
            {
                await preparedCommands.UpsertMetaAsync(
                    p.Barcode,
                    articleCode,
                    name2,
                    purchasePrice,
                    supplierRef.Id,
                    supplierRef.Name,
                    categoryRef.Id,
                    categoryRef.Name,
                    stockQtyToWrite).ConfigureAwait(false);
            }

            return id;
        }

        internal sealed class CatalogProductPreparedCommands : IDisposable
        {
            private readonly SqliteCommand _getProductId;
            private readonly SqliteCommand _insertProduct;
            private readonly SqliteCommand _lastInsertId;
            private readonly SqliteCommand _updateProduct;
            private readonly SqliteCommand _upsertMeta;

            internal CatalogProductPreparedCommands(SqliteConnection conn, SqliteTransaction tx)
            {
                _updateProduct = CreatePrepared(conn, tx, @"
UPDATE products
SET name = @Name,
    unitPrice = @UnitPrice,
    remote_product_id = COALESCE(NULLIF(@RemoteProductId, ''), remote_product_id),
    remote_deleted_at = NULL,
    is_active = 1
WHERE barcode = @Barcode", "@Name", "@UnitPrice", "@RemoteProductId", "@Barcode");
                _insertProduct = CreatePrepared(conn, tx, @"
INSERT INTO products(barcode, name, unitPrice, remote_product_id, remote_deleted_at, is_active)
VALUES(@Barcode, @Name, @UnitPrice, NULLIF(@RemoteProductId, ''), NULL, 1)",
                    "@Barcode", "@Name", "@UnitPrice", "@RemoteProductId");
                _lastInsertId = CreatePrepared(conn, tx, "SELECT last_insert_rowid()");
                _getProductId = CreatePrepared(
                    conn,
                    tx,
                    "SELECT id FROM products WHERE barcode = @Barcode",
                    "@Barcode");
                _upsertMeta = CreatePrepared(conn, tx, @"
INSERT OR REPLACE INTO product_meta(
    barcode, article_code, name2, purchase_price, purchase_old, retail_old,
    supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES(
    @barcode, @articleCode, @name2, @purchasePrice, 0, 0,
    @supplierId, @supplierName, @categoryId, @categoryName, @stockQty)",
                    "@barcode",
                    "@articleCode",
                    "@name2",
                    "@purchasePrice",
                    "@supplierId",
                    "@supplierName",
                    "@categoryId",
                    "@categoryName",
                    "@stockQty");
            }

            internal void SetTransaction(SqliteTransaction tx)
            {
                _getProductId.Transaction = tx;
                _insertProduct.Transaction = tx;
                _lastInsertId.Transaction = tx;
                _updateProduct.Transaction = tx;
                _upsertMeta.Transaction = tx;
            }

            internal async Task<int> UpdateProductAsync(
                string barcode,
                string name,
                long unitPrice,
                string remoteProductId)
            {
                Set(_updateProduct, "@Barcode", barcode);
                Set(_updateProduct, "@Name", name);
                Set(_updateProduct, "@UnitPrice", unitPrice);
                Set(_updateProduct, "@RemoteProductId", remoteProductId);
                return await _updateProduct.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            internal async Task<long> InsertProductAsync(
                string barcode,
                string name,
                long unitPrice,
                string remoteProductId)
            {
                Set(_insertProduct, "@Barcode", barcode);
                Set(_insertProduct, "@Name", name);
                Set(_insertProduct, "@UnitPrice", unitPrice);
                Set(_insertProduct, "@RemoteProductId", remoteProductId);
                await _insertProduct.ExecuteNonQueryAsync().ConfigureAwait(false);
                return Convert.ToInt64(
                    await _lastInsertId.ExecuteScalarAsync().ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            internal async Task<long> GetProductIdAsync(string barcode)
            {
                Set(_getProductId, "@Barcode", barcode);
                return Convert.ToInt64(
                    await _getProductId.ExecuteScalarAsync().ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            internal async Task UpsertMetaAsync(
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
                Set(_upsertMeta, "@barcode", barcode);
                Set(_upsertMeta, "@articleCode", articleCode ?? string.Empty);
                Set(_upsertMeta, "@name2", name2 ?? string.Empty);
                Set(_upsertMeta, "@purchasePrice", purchasePrice);
                Set(_upsertMeta, "@supplierId", supplierId);
                Set(_upsertMeta, "@supplierName", supplierName ?? string.Empty);
                Set(_upsertMeta, "@categoryId", categoryId);
                Set(_upsertMeta, "@categoryName", categoryName ?? string.Empty);
                Set(_upsertMeta, "@stockQty", stockQty);
                await _upsertMeta.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            public void Dispose()
            {
                _getProductId.Dispose();
                _insertProduct.Dispose();
                _lastInsertId.Dispose();
                _updateProduct.Dispose();
                _upsertMeta.Dispose();
            }

            private static SqliteCommand CreatePrepared(
                SqliteConnection conn,
                SqliteTransaction tx,
                string sql,
                params string[] parameterNames)
            {
                var command = conn.CreateCommand();
                command.Transaction = tx;
                command.CommandText = sql;
                foreach (var parameterName in parameterNames)
                {
                    command.Parameters.Add(new SqliteParameter(parameterName, string.Empty));
                }
                command.Prepare();
                return command;
            }

            private static void Set(SqliteCommand command, string parameterName, object value)
            {
                command.Parameters[parameterName].Value = value ?? DBNull.Value;
            }
        }

        internal sealed class CatalogProductBatchContext
        {
            private readonly Dictionary<int, ProductMetaReference> _categoriesById;
            private readonly Dictionary<string, ProductMetaReference> _categoriesByName;
            private readonly Dictionary<int, ProductMetaReference> _suppliersById;
            private readonly Dictionary<string, ProductMetaReference> _suppliersByName;
            private readonly HashSet<string> _pendingStockBarcodes;
            private readonly HashSet<string> _pendingStockRemoteProductIds;

            private CatalogProductBatchContext(
                IEnumerable<ProductMetaReference> categories,
                IEnumerable<ProductMetaReference> suppliers,
                IEnumerable<string> pendingStockBarcodes,
                IEnumerable<string> pendingStockRemoteProductIds)
            {
                _categoriesById = new Dictionary<int, ProductMetaReference>();
                _categoriesByName = new Dictionary<string, ProductMetaReference>(StringComparer.OrdinalIgnoreCase);
                _suppliersById = new Dictionary<int, ProductMetaReference>();
                _suppliersByName = new Dictionary<string, ProductMetaReference>(StringComparer.OrdinalIgnoreCase);
                _pendingStockBarcodes = new HashSet<string>(StringComparer.Ordinal);
                _pendingStockRemoteProductIds = new HashSet<string>(StringComparer.Ordinal);
                AddReferences(categories, _categoriesById, _categoriesByName);
                AddReferences(suppliers, _suppliersById, _suppliersByName);
                foreach (var barcodeValue in pendingStockBarcodes ?? Array.Empty<string>())
                {
                    var barcode = (barcodeValue ?? string.Empty).Trim();
                    if (barcode.Length > 0) _pendingStockBarcodes.Add(barcode);
                }
                foreach (var remoteProductIdValue in pendingStockRemoteProductIds ?? Array.Empty<string>())
                {
                    var remoteProductId = (remoteProductIdValue ?? string.Empty).Trim();
                    if (remoteProductId.Length > 0) _pendingStockRemoteProductIds.Add(remoteProductId);
                }
            }

            internal bool HasPendingLocalStock(string barcode, string remoteProductId)
            {
                var normalizedBarcode = (barcode ?? string.Empty).Trim();
                var normalizedRemoteProductId = (remoteProductId ?? string.Empty).Trim();
                return (normalizedBarcode.Length > 0 && _pendingStockBarcodes.Contains(normalizedBarcode)) ||
                    (normalizedRemoteProductId.Length > 0 &&
                     _pendingStockRemoteProductIds.Contains(normalizedRemoteProductId));
            }

            internal static CatalogProductBatchContext FromReferences(
                IEnumerable<ProductMetaReference> categories,
                IEnumerable<ProductMetaReference> suppliers)
            {
                return new CatalogProductBatchContext(
                    categories,
                    suppliers,
                    Array.Empty<string>(),
                    Array.Empty<string>());
            }

            internal CatalogProductBatchContext CloneWithPendingStock(
                IEnumerable<string> pendingStockBarcodes,
                IEnumerable<string> pendingStockRemoteProductIds)
            {
                return new CatalogProductBatchContext(
                    _categoriesById.Values,
                    _suppliersById.Values,
                    pendingStockBarcodes,
                    pendingStockRemoteProductIds);
            }

            internal CatalogProductBatchContext WithoutPendingStock()
            {
                return CloneWithPendingStock(Array.Empty<string>(), Array.Empty<string>());
            }

            internal void RemoveCategory(int id)
            {
                if (!_categoriesById.TryGetValue(id, out var existing))
                {
                    return;
                }

                _categoriesById.Remove(id);
                var name = ProductMetaResolver.NormalizeCatalogName(existing.Name);
                if (name.Length > 0 &&
                    _categoriesByName.TryGetValue(name, out var byName) &&
                    byName.Id == id)
                {
                    _categoriesByName.Remove(name);
                }
            }

            internal void RemoveSupplier(int id)
            {
                if (!_suppliersById.TryGetValue(id, out var existing))
                {
                    return;
                }

                _suppliersById.Remove(id);
                var name = ProductMetaResolver.NormalizeCatalogName(existing.Name);
                if (name.Length > 0 &&
                    _suppliersByName.TryGetValue(name, out var byName) &&
                    byName.Id == id)
                {
                    _suppliersByName.Remove(name);
                }
            }

            internal async Task<ProductMetaReference> ResolveCategoryAsync(
                SqliteConnection conn,
                SqliteTransaction tx,
                int? categoryId,
                string categoryName)
            {
                return await ResolveAsync(
                    conn,
                    tx,
                    "categories",
                    categoryId,
                    categoryName,
                    _categoriesById,
                    _categoriesByName).ConfigureAwait(false);
            }

            internal async Task<ProductMetaReference> ResolveSupplierAsync(
                SqliteConnection conn,
                SqliteTransaction tx,
                int? supplierId,
                string supplierName)
            {
                return await ResolveAsync(
                    conn,
                    tx,
                    "suppliers",
                    supplierId,
                    supplierName,
                    _suppliersById,
                    _suppliersByName).ConfigureAwait(false);
            }

            private static void AddReferences(
                IEnumerable<ProductMetaReference> references,
                IDictionary<int, ProductMetaReference> byId,
                IDictionary<string, ProductMetaReference> byName)
            {
                foreach (var reference in references ?? Array.Empty<ProductMetaReference>())
                {
                    if (reference == null || !reference.Id.HasValue) continue;
                    byId[reference.Id.Value] = reference;
                    var normalizedName = ProductMetaResolver.NormalizeCatalogName(reference.Name);
                    if (normalizedName.Length > 0 && !byName.ContainsKey(normalizedName))
                    {
                        byName[normalizedName] = reference;
                    }
                }
            }

            private static async Task<ProductMetaReference> ResolveAsync(
                SqliteConnection conn,
                SqliteTransaction tx,
                string table,
                int? id,
                string name,
                IDictionary<int, ProductMetaReference> byId,
                IDictionary<string, ProductMetaReference> byName)
            {
                var normalizedName = ProductMetaResolver.NormalizeCatalogName(name);
                if (id.HasValue && id.Value != 0 && byId.TryGetValue(id.Value, out var existingById) &&
                    (normalizedName.Length == 0 || ProductMetaResolver.NamesMatch(normalizedName, existingById.Name)))
                {
                    return existingById;
                }

                if (normalizedName.Length == 0)
                {
                    return new ProductMetaReference();
                }

                if (byName.TryGetValue(normalizedName, out var existingByName))
                {
                    return existingByName;
                }

                var insertedId = await conn.ExecuteScalarAsync<int>(
                    "INSERT INTO " + table + "(name, is_active) VALUES(@name, 1); SELECT last_insert_rowid();",
                    new { name = normalizedName },
                    tx).ConfigureAwait(false);
                var inserted = new ProductMetaReference { Id = insertedId, Name = normalizedName };
                byId[insertedId] = inserted;
                byName[normalizedName] = inserted;
                return inserted;
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

            await conn.ExecuteAsync(@"
UPDATE local_stock_movements
SET barcode = @barcode
WHERE barcode = @existingRemoteBarcode
  AND EXISTS (
      SELECT 1
      FROM sales_sync_outbox pending_outbox
      WHERE pending_outbox.sale_id = local_stock_movements.sale_id
        AND pending_outbox.status IN ('pending', 'retry', 'in_progress', 'failed_blocked')
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
        public Task UpdateProductAndMetaInTransactionAsync(long productId, string name, long unitPriceMinor, string barcode, string articleCode, string name2, int purchasePrice, int? supplierId, string supplierName, int? categoryId, string categoryName, int stockQty) =>
            _localProductWriter.UpdateProductAndMetaInTransactionAsync(productId, name, unitPriceMinor, barcode, articleCode, name2, purchasePrice, supplierId, supplierName, categoryId, categoryName, stockQty);

        /// <summary>Update prodotto + meta e scrive righe in price_history se prezzi cambiano. source es. MANUAL_EDIT.</summary>
        public Task UpdateProductAndMetaWithPriceHistoryAsync(long productId, string name, long unitPriceMinor, string barcode, string articleCode, string name2, int purchasePrice, int? supplierId, string supplierName, int? categoryId, string categoryName, int stockQty, string source) =>
            _localProductWriter.UpdateProductAndMetaWithPriceHistoryAsync(productId, name, unitPriceMinor, barcode, articleCode, name2, purchasePrice, supplierId, supplierName, categoryId, categoryName, stockQty, source);

        public Task InsertPriceHistoryAsync(string barcode, string type, int newPrice, string source = "MANUAL") =>
            _localProductWriter.InsertPriceHistoryAsync(barcode, type, newPrice, source);

        /// <summary>Storico prezzi per barcode, ordinato per data DESC.</summary>
        public Task<IReadOnlyList<ProductPriceHistoryRow>> GetPriceHistoryByBarcodeAsync(string barcode) =>
            _queries.GetPriceHistoryByBarcodeAsync(barcode);

        /// <summary>Tutti i prodotti con dettagli per export (products + product_meta).</summary>
        public Task<IReadOnlyList<ProductDetailsRow>> ListAllDetailsAsync() => _queries.ListAllDetailsAsync();

        public Task<IReadOnlyList<ProductDetailsRow>> ListDetailsByBarcodesAsync(IEnumerable<string> barcodes) =>
            _queries.ListDetailsByBarcodesAsync(barcodes);

        /// <summary>Tutte le righe price_history per export.</summary>
        public Task<IReadOnlyList<ProductPriceHistoryRow>> ListAllPriceHistoryAsync() =>
            _queries.ListAllPriceHistoryAsync();

        /// <summary>Aggiorna prezzi prodotto e scrive storico nella stessa transazione. source es. MANUAL_EDIT, IMPORT.</summary>
        public Task UpdateProductPricesAsync(long productId, int newPurchasePrice, int newRetailPrice, string source) =>
            _localProductWriter.UpdateProductPricesAsync(productId, newPurchasePrice, newRetailPrice, source);

        public Task<bool> UpdateAsync(long productId, string name, long unitPriceMinor) =>
            _localProductWriter.UpdateAsync(productId, name, unitPriceMinor);
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

    public sealed class ProductCatalogStats
    {
        public int TotalProducts { get; set; }
        public int TotalCategories { get; set; }
        public int TotalSuppliers { get; set; }
        public long TotalStockUnits { get; set; }
        public int ZeroStockProducts { get; set; }
    }
}
