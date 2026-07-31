using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Receipt;

namespace Win7POS.Data.Repositories
{
    /// <summary>
    /// Owns remote product identity, canonicalization and tombstone mutations.
    /// Batch callers retain ownership of their connection and transaction through
    /// the static core methods; the instance methods are autonomous façade paths.
    /// </summary>
    internal sealed class RemoteCatalogProductWriter
    {
        private readonly SqliteConnectionFactory _factory;

        internal RemoteCatalogProductWriter(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        internal async Task<bool> ApplyRemoteProductTombstoneAsync(
            string remoteProductId,
            string remoteDeletedAt)
        {
            await CatalogMutationGate.Instance.WaitAsync().ConfigureAwait(false);
            try
            {
                using var conn = _factory.Open();
                return await ApplyRemoteProductTombstoneInTransactionAsync(
                    conn,
                    null,
                    remoteProductId,
                    remoteDeletedAt,
                    null).ConfigureAwait(false);
            }
            finally
            {
                CatalogMutationGate.Instance.Release();
            }
        }

        internal static async Task<bool> ApplyRemoteProductTombstoneInTransactionAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remoteProductId,
            string remoteDeletedAt,
            string remoteUpdatedAt = null)
        {
            if (string.IsNullOrWhiteSpace(remoteProductId)) return false;
            var normalizedRemoteProductId = remoteProductId.Trim();
            var protectedProductId = await FindProtectedProductIdAsync(
                conn,
                tx,
                normalizedRemoteProductId,
                null).ConfigureAwait(false);
            if (protectedProductId.HasValue)
            {
                if (PosArticleMutationIntentPolicy.IsProductRevision(remoteUpdatedAt))
                {
                    await conn.ExecuteAsync(@"
UPDATE products
SET remote_base_revision = @authoritativeRevision
WHERE id = @productId
  AND remote_product_id = @remoteProductId;

UPDATE article_product_remote_shadow
SET is_active = 0,
    authoritative_revision = @authoritativeRevision,
    updated_at = @updatedAt
WHERE remote_product_id = @remoteProductId
  AND (
    local_product_id = @productId
    OR local_product_id IS NULL
  );",
                        new
                        {
                            productId = protectedProductId.Value,
                            remoteProductId = normalizedRemoteProductId,
                            authoritativeRevision = remoteUpdatedAt,
                            updatedAt = DateTimeOffset.UtcNow.ToString("O")
                        },
                        tx).ConfigureAwait(false);
                }
                return true;
            }

            var normalizedRemoteDeletedAt =
                string.IsNullOrWhiteSpace(remoteDeletedAt)
                    ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                    : remoteDeletedAt.Trim();
            var hasAuthoritativeRevision =
                PosArticleMutationIntentPolicy.IsProductRevision(remoteUpdatedAt);
            var authoritativeRevision = hasAuthoritativeRevision
                ? remoteUpdatedAt
                : string.Empty;
            var rows = await conn.ExecuteAsync(@"
UPDATE products
SET is_active = 0,
    remote_deleted_at = @remoteDeletedAt,
    remote_base_revision = CASE
      WHEN @hasAuthoritativeRevision = 1 THEN @authoritativeRevision
      ELSE remote_base_revision
    END
WHERE remote_product_id = @remoteProductId
  AND (
    COALESCE(is_active, 1) <> 0
    OR COALESCE(remote_deleted_at, '') <> @remoteDeletedAt
    OR (
      @hasAuthoritativeRevision = 1
      AND COALESCE(remote_base_revision, '') <> @authoritativeRevision
    )
  );

UPDATE article_product_remote_shadow
SET is_active = 0,
    authoritative_revision = CASE
      WHEN @hasAuthoritativeRevision = 1 THEN @authoritativeRevision
      ELSE authoritative_revision
    END,
    updated_at = @updatedAt
WHERE remote_product_id = @remoteProductId
  AND (
    COALESCE(is_active, 1) <> 0
    OR (
      @hasAuthoritativeRevision = 1
      AND COALESCE(authoritative_revision, '') <> @authoritativeRevision
    )
  );",
                new
                {
                    remoteProductId = normalizedRemoteProductId,
                    remoteDeletedAt = normalizedRemoteDeletedAt,
                    hasAuthoritativeRevision,
                    authoritativeRevision,
                    updatedAt = DateTimeOffset.UtcNow.ToString("O")
                },
                tx).ConfigureAwait(false);

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
            int stockQty,
            string remoteProductId)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            SalesReceiptContentPolicy.EnsureValidProductIdentity(p.Barcode, p.Name);
            if (ProductIdentityPolicy.IsReservedBarcode(p.Barcode))
                throw new InvalidOperationException("Barcode riservato (DISC:/MANUAL:).");

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
            CatalogProductBatchContext batchContext = null,
            string remoteUpdatedAt = null,
            string remoteCategoryId = null,
            string remoteSupplierId = null)
        {
            if (string.IsNullOrWhiteSpace(remoteProductId))
                throw new ArgumentException("A remote product identity is required.", nameof(remoteProductId));
            if (p == null) throw new ArgumentNullException(nameof(p));
            SalesReceiptContentPolicy.EnsureValidProductIdentity(p.Barcode, p.Name);
            if (ProductIdentityPolicy.IsReservedBarcode(p.Barcode))
                throw new InvalidOperationException("Barcode riservato (DISC:/MANUAL:).");

            var normalizedRemoteProductId = remoteProductId.Trim();
            var protectedProductId = await FindProtectedProductIdAsync(
                conn,
                tx,
                normalizedRemoteProductId,
                p.Barcode).ConfigureAwait(false);
            if (protectedProductId.HasValue)
            {
                await UpsertRemoteShadowAsync(
                    conn,
                    tx,
                    protectedProductId,
                    normalizedRemoteProductId,
                    p.Barcode,
                    articleCode,
                    p.Name,
                    name2,
                    remoteCategoryId,
                    remoteSupplierId,
                    p.UnitPrice,
                    purchasePrice,
                    stockQty,
                    true,
                    remoteUpdatedAt).ConfigureAwait(false);
                return protectedProductId.Value;
            }
            await CanonicalizeRemoteProductBeforeUpsertAsync(
                conn,
                tx,
                normalizedRemoteProductId,
                p.Barcode,
                p.Name,
                p.UnitPrice).ConfigureAwait(false);

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

            await DeactivateRemoteProductDuplicatesAsync(
                conn,
                tx,
                normalizedRemoteProductId,
                p.Barcode).ConfigureAwait(false);

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

            await UpsertRemoteShadowAsync(
                conn,
                tx,
                id,
                normalizedRemoteProductId,
                p.Barcode,
                articleCode,
                p.Name,
                name2,
                remoteCategoryId,
                remoteSupplierId,
                p.UnitPrice,
                purchasePrice,
                stockQty,
                true,
                remoteUpdatedAt).ConfigureAwait(false);
            return id;
        }

        internal static async Task<long?> FindProtectedProductIdAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string remoteProductId,
            string barcode)
        {
            return await connection.ExecuteScalarAsync<long?>(@"
SELECT p.id
FROM products p
WHERE (
    (TRIM(COALESCE(@remoteProductId, '')) <> ''
     AND p.remote_product_id = TRIM(@remoteProductId))
    OR
    (TRIM(COALESCE(@barcode, '')) <> ''
     AND p.barcode = TRIM(@barcode) COLLATE NOCASE)
  )
  AND EXISTS (
    SELECT 1
    FROM article_mutation_outbox mutation
    WHERE mutation.local_product_id = p.id
      AND mutation.state <> 'completed'
  )
ORDER BY CASE
  WHEN p.remote_product_id = TRIM(COALESCE(@remoteProductId, '')) THEN 0
  ELSE 1
END,
p.id
LIMIT 1;",
                new
                {
                    remoteProductId = remoteProductId ?? string.Empty,
                    barcode = barcode ?? string.Empty
                },
                transaction).ConfigureAwait(false);
        }

        internal static async Task UpsertRemoteShadowAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long? candidateLocalProductId,
            string remoteProductId,
            string barcode,
            string itemNumber,
            string primaryName,
            string secondaryName,
            string remoteCategoryId,
            string remoteSupplierId,
            long retailPrice,
            int purchasePrice,
            int stockQuantity,
            bool active,
            string authoritativeRevision)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (string.IsNullOrWhiteSpace(remoteProductId) ||
                !PosArticleMutationIntentPolicy.IsProductRevision(
                    authoritativeRevision))
            {
                return;
            }

            var normalizedRemoteId = remoteProductId.Trim();
            var localProductId = await connection.ExecuteScalarAsync<long?>(@"
SELECT id
FROM products
WHERE id = @candidateLocalProductId
  AND remote_product_id = @remoteProductId
LIMIT 1;",
                new
                {
                    candidateLocalProductId,
                    remoteProductId = normalizedRemoteId
                },
                transaction).ConfigureAwait(false);
            if (localProductId.HasValue)
            {
                await connection.ExecuteAsync(@"
UPDATE products
SET remote_base_revision = @authoritativeRevision
WHERE id = @localProductId
  AND remote_product_id = @remoteProductId;",
                    new
                    {
                        localProductId,
                        remoteProductId = normalizedRemoteId,
                        authoritativeRevision
                    },
                    transaction).ConfigureAwait(false);
            }

            await connection.ExecuteAsync(@"
INSERT INTO article_product_remote_shadow(
  remote_product_id,
  local_product_id,
  barcode,
  item_number,
  primary_name,
  secondary_name,
  category_remote_id,
  supplier_remote_id,
  retail_price,
  purchase_price,
  stock_quantity,
  is_active,
  authoritative_revision,
  updated_at)
VALUES(
  @remoteProductId,
  @localProductId,
  @barcode,
  @itemNumber,
  @primaryName,
  @secondaryName,
  @remoteCategoryId,
  @remoteSupplierId,
  @retailPrice,
  @purchasePrice,
  @stockQuantity,
  @isActive,
  @authoritativeRevision,
  @updatedAt)
ON CONFLICT(remote_product_id) DO UPDATE SET
  local_product_id = excluded.local_product_id,
  barcode = excluded.barcode,
  item_number = excluded.item_number,
  primary_name = excluded.primary_name,
  secondary_name = excluded.secondary_name,
  category_remote_id = excluded.category_remote_id,
  supplier_remote_id = excluded.supplier_remote_id,
  retail_price = excluded.retail_price,
  purchase_price = excluded.purchase_price,
  stock_quantity = excluded.stock_quantity,
  is_active = excluded.is_active,
  authoritative_revision = excluded.authoritative_revision,
  updated_at = excluded.updated_at;",
                new
                {
                    remoteProductId = normalizedRemoteId,
                    localProductId,
                    barcode = (barcode ?? string.Empty).Trim(),
                    itemNumber = string.IsNullOrWhiteSpace(itemNumber)
                        ? null
                        : itemNumber.Trim(),
                    primaryName = (primaryName ?? string.Empty).Trim(),
                    secondaryName = string.IsNullOrWhiteSpace(secondaryName)
                        ? null
                        : secondaryName.Trim(),
                    remoteCategoryId = string.IsNullOrWhiteSpace(remoteCategoryId)
                        ? null
                        : remoteCategoryId.Trim(),
                    remoteSupplierId = string.IsNullOrWhiteSpace(remoteSupplierId)
                        ? null
                        : remoteSupplierId.Trim(),
                    retailPrice,
                    purchasePrice,
                    stockQuantity,
                    isActive = active ? 1 : 0,
                    authoritativeRevision,
                    updatedAt = DateTimeOffset.UtcNow.ToString("O")
                },
                transaction).ConfigureAwait(false);
        }

        internal sealed class RemoteCatalogSetProductWrite
        {
            public bool ApplyImageProjection { get; set; }
            public string ArticleCode { get; set; } = string.Empty;
            public string Barcode { get; set; } = string.Empty;
            public int? CategoryId { get; set; }
            public string CategoryName { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public int PurchasePrice { get; set; }
            public string PrimaryImageUpdatedAt { get; set; } = string.Empty;
            public string PrimaryImageVersionId { get; set; } = string.Empty;
            public string RemoteCategoryId { get; set; } = string.Empty;
            public string RemoteProductId { get; set; } = string.Empty;
            public string RemoteSupplierId { get; set; } = string.Empty;
            public string RemoteUpdatedAt { get; set; } = string.Empty;
            public string SecondName { get; set; } = string.Empty;
            public int StockQuantity { get; set; }
            public int? SupplierId { get; set; }
            public string SupplierName { get; set; } = string.Empty;
            public long UnitPrice { get; set; }
        }

        internal static async Task ApplyImageProjectionInTransactionAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long? localProductId,
            string remoteProductId,
            bool apply,
            string primaryImageVersionId,
            string primaryImageUpdatedAt,
            string catalogRevision,
            bool updateProduct)
        {
            if (!apply) return;
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            var normalizedRemoteId = (remoteProductId ?? string.Empty).Trim();
            if (normalizedRemoteId.Length == 0)
                throw new ArgumentException("catalog_image_remote_product_required", nameof(remoteProductId));
            var version = string.IsNullOrWhiteSpace(primaryImageVersionId)
                ? null
                : primaryImageVersionId.Trim();
            var updatedAt = string.IsNullOrWhiteSpace(primaryImageUpdatedAt)
                ? null
                : primaryImageUpdatedAt.Trim();
            var resolvedLocalId = localProductId;
            if (!resolvedLocalId.HasValue)
            {
                resolvedLocalId = await connection.ExecuteScalarAsync<long?>(@"
SELECT id
FROM products
WHERE remote_product_id = @remoteProductId
LIMIT 1;",
                    new { remoteProductId = normalizedRemoteId },
                    transaction).ConfigureAwait(false);
            }
            if (updateProduct && resolvedLocalId.HasValue)
            {
                await connection.ExecuteAsync(@"
UPDATE products
SET primary_image_version_id = @version,
    primary_image_updated_at = @updatedAt
WHERE id = @localProductId
  AND remote_product_id = @remoteProductId;",
                    new
                    {
                        version,
                        updatedAt,
                        localProductId = resolvedLocalId.Value,
                        remoteProductId = normalizedRemoteId
                    },
                    transaction).ConfigureAwait(false);
            }
            await connection.ExecuteAsync(@"
INSERT INTO product_image_remote_shadow(
  remote_product_id,
  local_product_id,
  primary_image_version_id,
  primary_image_updated_at,
  catalog_revision,
  updated_at)
VALUES(
  @remoteProductId,
  @localProductId,
  @version,
  @updatedAt,
  @catalogRevision,
  @shadowUpdatedAt)
ON CONFLICT(remote_product_id) DO UPDATE SET
  local_product_id = excluded.local_product_id,
  primary_image_version_id = excluded.primary_image_version_id,
  primary_image_updated_at = excluded.primary_image_updated_at,
  catalog_revision = excluded.catalog_revision,
  updated_at = excluded.updated_at;",
                new
                {
                    remoteProductId = normalizedRemoteId,
                    localProductId = resolvedLocalId,
                    version,
                    updatedAt,
                    catalogRevision = string.IsNullOrWhiteSpace(catalogRevision)
                        ? null
                        : catalogRevision.Trim(),
                    shadowUpdatedAt = DateTimeOffset.UtcNow.ToString("O")
                },
                transaction).ConfigureAwait(false);
        }

        internal static async Task ApplyCleanProductsSetBasedInTransactionAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            IReadOnlyList<RemoteCatalogSetProductWrite> products)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (tx == null) throw new ArgumentNullException(nameof(tx));
            var rows = products ?? Array.Empty<RemoteCatalogSetProductWrite>();
            if (rows.Count == 0)
            {
                return;
            }

            await conn.ExecuteAsync(@"
INSERT INTO products(
  barcode,
  name,
  unitPrice,
  remote_product_id,
  remote_base_revision,
  remote_deleted_at,
  is_active,
  primary_image_version_id,
  primary_image_updated_at)
SELECT
  staged.barcode,
  staged.name,
  staged.unit_price,
  staged.remote_product_id,
  NULLIF(staged.remote_updated_at, ''),
  NULL,
  1,
  CASE WHEN staged.apply_image_projection = 1
    THEN NULLIF(staged.primary_image_version_id, '') ELSE NULL END,
  CASE WHEN staged.apply_image_projection = 1
    THEN NULLIF(staged.primary_image_updated_at, '') ELSE NULL END
FROM temp_catalog_page_products staged
WHERE 1 = 1
ON CONFLICT(barcode) DO UPDATE SET
  name = excluded.name,
  unitPrice = excluded.unitPrice,
  remote_product_id = excluded.remote_product_id,
  remote_base_revision = COALESCE(
    excluded.remote_base_revision,
    products.remote_base_revision),
  remote_deleted_at = NULL,
  is_active = 1;

UPDATE products
SET primary_image_version_id = (
      SELECT NULLIF(staged.primary_image_version_id, '')
      FROM temp_catalog_page_products staged
      WHERE staged.remote_product_id = products.remote_product_id
        AND staged.apply_image_projection = 1
      LIMIT 1),
    primary_image_updated_at = (
      SELECT NULLIF(staged.primary_image_updated_at, '')
      FROM temp_catalog_page_products staged
      WHERE staged.remote_product_id = products.remote_product_id
        AND staged.apply_image_projection = 1
      LIMIT 1)
WHERE EXISTS (
  SELECT 1
  FROM temp_catalog_page_products staged
  WHERE staged.remote_product_id = products.remote_product_id
    AND staged.apply_image_projection = 1);

UPDATE products
SET is_active = 0,
    remote_deleted_at = @remoteDeletedAt
WHERE COALESCE(is_active, 1) = 1
  AND EXISTS (
    SELECT 1
    FROM temp_catalog_page_products staged
    WHERE staged.remote_product_id = products.remote_product_id
      AND staged.barcode <> products.barcode
  );

INSERT OR REPLACE INTO product_meta(
  barcode,
  article_code,
  name2,
  purchase_price,
  purchase_old,
  retail_old,
  supplier_id,
  supplier_name,
  category_id,
  category_name,
  stock_qty)
SELECT
  barcode,
  article_code,
  second_name,
  purchase_price,
  0,
  0,
  supplier_id,
  supplier_name,
  category_id,
  category_name,
  stock_quantity
FROM temp_catalog_page_products;

INSERT INTO remote_catalog_product_references(
  remote_product_id,
  remote_category_id,
  remote_supplier_id)
SELECT
  remote_product_id,
  NULLIF(remote_category_id, ''),
  NULLIF(remote_supplier_id, '')
FROM temp_catalog_page_products
WHERE 1 = 1
ON CONFLICT(remote_product_id) DO UPDATE SET
  remote_category_id = excluded.remote_category_id,
  remote_supplier_id = excluded.remote_supplier_id;

INSERT INTO article_product_remote_shadow(
  remote_product_id,
  local_product_id,
  barcode,
  item_number,
  primary_name,
  secondary_name,
  category_remote_id,
  supplier_remote_id,
  retail_price,
  purchase_price,
  stock_quantity,
  is_active,
  authoritative_revision,
  updated_at)
SELECT staged.remote_product_id,
       product.id,
       staged.barcode,
       NULLIF(staged.article_code, ''),
       staged.name,
       NULLIF(staged.second_name, ''),
       NULLIF(staged.remote_category_id, ''),
       NULLIF(staged.remote_supplier_id, ''),
       staged.unit_price,
       staged.purchase_price,
       staged.stock_quantity,
       1,
       staged.remote_updated_at,
       @shadowUpdatedAt
FROM temp_catalog_page_products staged
JOIN products product
  ON product.remote_product_id = staged.remote_product_id
WHERE staged.remote_updated_at <> ''
ON CONFLICT(remote_product_id) DO UPDATE SET
  local_product_id = excluded.local_product_id,
  barcode = excluded.barcode,
  item_number = excluded.item_number,
  primary_name = excluded.primary_name,
  secondary_name = excluded.secondary_name,
  category_remote_id = excluded.category_remote_id,
  supplier_remote_id = excluded.supplier_remote_id,
  retail_price = excluded.retail_price,
  purchase_price = excluded.purchase_price,
  stock_quantity = excluded.stock_quantity,
  is_active = excluded.is_active,
  authoritative_revision = excluded.authoritative_revision,
  updated_at = excluded.updated_at;

INSERT INTO product_image_remote_shadow(
  remote_product_id,
  local_product_id,
  primary_image_version_id,
  primary_image_updated_at,
  catalog_revision,
  updated_at)
SELECT staged.remote_product_id,
       product.id,
       NULLIF(staged.primary_image_version_id, ''),
       NULLIF(staged.primary_image_updated_at, ''),
       NULLIF(staged.remote_updated_at, ''),
       @shadowUpdatedAt
FROM temp_catalog_page_products staged
JOIN products product
  ON product.remote_product_id = staged.remote_product_id
WHERE staged.apply_image_projection = 1
ON CONFLICT(remote_product_id) DO UPDATE SET
  local_product_id = excluded.local_product_id,
  primary_image_version_id = excluded.primary_image_version_id,
  primary_image_updated_at = excluded.primary_image_updated_at,
  catalog_revision = excluded.catalog_revision,
  updated_at = excluded.updated_at;",
                new
                {
                    remoteDeletedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    shadowUpdatedAt = DateTimeOffset.UtcNow.ToString("O")
                },
                tx).ConfigureAwait(false);

            var verified = await conn.ExecuteScalarAsync<long>(@"
SELECT CASE WHEN
  (
    SELECT COUNT(1)
    FROM temp_catalog_page_products staged
    JOIN products product
      ON product.barcode = staged.barcode
     AND product.remote_product_id = staged.remote_product_id
     AND product.name = staged.name
     AND product.unitPrice = staged.unit_price
     AND COALESCE(product.is_active, 1) = 1
     AND (
       staged.apply_image_projection = 0
       OR (
         product.primary_image_version_id IS NULLIF(staged.primary_image_version_id, '')
         AND product.primary_image_updated_at IS NULLIF(staged.primary_image_updated_at, '')))
    JOIN product_meta meta
      ON meta.barcode = staged.barcode
     AND meta.article_code = staged.article_code
     AND meta.name2 = staged.second_name
     AND meta.purchase_price = staged.purchase_price
     AND meta.supplier_id IS staged.supplier_id
     AND meta.supplier_name = staged.supplier_name
     AND meta.category_id IS staged.category_id
     AND meta.category_name = staged.category_name
     AND meta.stock_qty = staged.stock_quantity
    JOIN remote_catalog_product_references reference
      ON reference.remote_product_id = staged.remote_product_id
     AND COALESCE(reference.remote_category_id, '') = staged.remote_category_id
     AND COALESCE(reference.remote_supplier_id, '') = staged.remote_supplier_id
  ) = (SELECT COUNT(1) FROM temp_catalog_page_products)
  AND NOT EXISTS (
    SELECT product.remote_product_id
    FROM products product
    JOIN temp_catalog_page_products staged
      ON staged.remote_product_id = product.remote_product_id
    WHERE COALESCE(product.is_active, 1) = 1
    GROUP BY product.remote_product_id
    HAVING COUNT(1) > 1
  )
THEN 1 ELSE 0 END;",
                transaction: tx).ConfigureAwait(false);
            if (verified != 1)
            {
                throw new InvalidOperationException("catalog_product_set_apply_verification_failed");
            }

            await conn.ExecuteAsync(
                "DELETE FROM temp_catalog_page_products;",
                transaction: tx).ConfigureAwait(false);
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
    }
}
