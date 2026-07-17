using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Data.Online;

namespace Win7POS.Data.Repositories
{
    /// <summary>
    /// Applies one bounded remote catalog page on a single SQLite connection and transaction.
    /// Cancellation is observed before the transaction starts; once writes begin the page is
    /// committed or rolled back as one unit.
    /// </summary>
    public sealed class RemoteCatalogBatchRepository
    {
        private readonly SqliteConnectionFactory _factory;

        public RemoteCatalogBatchRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<RemoteCatalogBatchApplyResult> ApplyAsync(
            RemoteCatalogBatch batch,
            CancellationToken cancellationToken = default,
            RemoteCatalogCommitFence commitFence = null)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));

            cancellationToken.ThrowIfCancellationRequested();
            await ProductRepository.CatalogMetaWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var conn = _factory.Open();
                using var tx = conn.BeginTransaction();
                try
                {
                    await RequireCommitFenceAsync(conn, tx, commitFence).ConfigureAwait(false);
                    var result = new RemoteCatalogBatchApplyResult();
                    var pendingPriceCollisionIds = new HashSet<long>();
                    var relinkProductRemoteIds = new HashSet<string>(StringComparer.Ordinal);
                    var relinkCategoryRemoteIds = new HashSet<string>(
                        (batch.Categories ?? Array.Empty<RemoteCatalogCategoryWrite>())
                            .Where(row => row != null && !string.IsNullOrWhiteSpace(row.RemoteCategoryId))
                            .Select(row => row.RemoteCategoryId.Trim())
                            .Concat((batch.CategoryTombstones ?? Array.Empty<RemoteCatalogCategoryTombstoneWrite>())
                                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.RemoteCategoryId))
                                .Select(row => row.RemoteCategoryId.Trim())),
                        StringComparer.Ordinal);
                    var relinkSupplierRemoteIds = new HashSet<string>(
                        (batch.Suppliers ?? Array.Empty<RemoteCatalogSupplierWrite>())
                            .Where(row => row != null && !string.IsNullOrWhiteSpace(row.RemoteSupplierId))
                            .Select(row => row.RemoteSupplierId.Trim())
                            .Concat((batch.SupplierTombstones ?? Array.Empty<RemoteCatalogSupplierTombstoneWrite>())
                                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.RemoteSupplierId))
                                .Select(row => row.RemoteSupplierId.Trim())),
                        StringComparer.Ordinal);

                    foreach (var category in batch.Categories ?? Array.Empty<RemoteCatalogCategoryWrite>())
                    {
                        if (category == null ||
                            string.IsNullOrWhiteSpace(category.RemoteCategoryId) ||
                            string.IsNullOrWhiteSpace(category.Name))
                        {
                            result.CategoriesSkipped += 1;
                            continue;
                        }

                        if (await CategoryRepository.UpsertRemoteInTransactionAsync(
                            conn,
                            tx,
                            category.RemoteCategoryId,
                            category.Name,
                            category.RemoteUpdatedAt).ConfigureAwait(false))
                        {
                            result.CategoriesApplied += 1;
                        }
                    }

                    foreach (var supplier in batch.Suppliers ?? Array.Empty<RemoteCatalogSupplierWrite>())
                    {
                        if (supplier == null ||
                            string.IsNullOrWhiteSpace(supplier.RemoteSupplierId) ||
                            string.IsNullOrWhiteSpace(supplier.Name))
                        {
                            result.SuppliersSkipped += 1;
                            continue;
                        }

                        if (await SupplierRepository.UpsertRemoteInTransactionAsync(
                            conn,
                            tx,
                            supplier.RemoteSupplierId,
                            supplier.Name,
                            supplier.RemoteUpdatedAt).ConfigureAwait(false))
                        {
                            result.SuppliersApplied += 1;
                        }
                    }

                    var categoryIdsByRemoteId = (await conn.QueryAsync<RemoteCatalogReferenceRow>(@"
SELECT id AS Id, remote_category_id AS RemoteId
FROM categories
WHERE COALESCE(is_active, 1) = 1
  AND TRIM(COALESCE(remote_category_id, '')) <> '';", transaction: tx).ConfigureAwait(false))
                        .GroupBy(row => (row.RemoteId ?? string.Empty).Trim(), StringComparer.Ordinal)
                        .Where(group => group.Key.Length > 0)
                        .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.Ordinal);
                    var supplierIdsByRemoteId = (await conn.QueryAsync<RemoteCatalogReferenceRow>(@"
SELECT id AS Id, remote_supplier_id AS RemoteId
FROM suppliers
WHERE COALESCE(is_active, 1) = 1
  AND TRIM(COALESCE(remote_supplier_id, '')) <> '';", transaction: tx).ConfigureAwait(false))
                        .GroupBy(row => (row.RemoteId ?? string.Empty).Trim(), StringComparer.Ordinal)
                        .Where(group => group.Key.Length > 0)
                        .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.Ordinal);

                    var productBatchContext = await ProductRepository.CatalogProductBatchContext
                        .LoadAsync(conn, tx)
                        .ConfigureAwait(false);
                    var tombstonedRemoteProductIds = new HashSet<string>(
                        (batch.ProductTombstones ?? Array.Empty<RemoteCatalogProductTombstoneWrite>())
                            .Where(row => row != null && !string.IsNullOrWhiteSpace(row.RemoteProductId))
                            .Select(row => row.RemoteProductId.Trim()),
                        StringComparer.Ordinal);
                    var activeProductIdentities = (await conn.QueryAsync<RemoteCatalogProductIdentityRow>(@"
SELECT barcode AS Barcode, TRIM(remote_product_id) AS RemoteProductId
FROM products
WHERE COALESCE(is_active, 1) = 1
  AND TRIM(COALESCE(remote_product_id, '')) <> '';", transaction: tx).ConfigureAwait(false))
                        .Where(row => !string.IsNullOrWhiteSpace(row.Barcode) &&
                            !string.IsNullOrWhiteSpace(row.RemoteProductId))
                        .ToArray();
                    var activeRemoteIdsByBarcode = activeProductIdentities
                        .GroupBy(row => NormalizeBarcode(row.Barcode), StringComparer.OrdinalIgnoreCase)
                        .Where(group => group.Key.Length > 0)
                        .ToDictionary(
                            group => group.Key,
                            group => new HashSet<string>(
                                group.Select(row => row.RemoteProductId.Trim()),
                                StringComparer.Ordinal),
                            StringComparer.OrdinalIgnoreCase);
                    var activeBarcodeByRemoteId = activeProductIdentities
                        .GroupBy(row => row.RemoteProductId.Trim(), StringComparer.Ordinal)
                        .Where(group => group.Key.Length > 0)
                        .ToDictionary(
                            group => group.Key,
                            group => NormalizeBarcode(group.First().Barcode),
                            StringComparer.Ordinal);
                    using var preparedProductCommands =
                        new ProductRepository.CatalogProductPreparedCommands(conn, tx);
                    using var preparedReferenceCommand =
                        new RemoteProductReferencePreparedCommand(conn, tx);
                    foreach (var product in batch.Products ?? Array.Empty<RemoteCatalogProductWrite>())
                    {
                        var normalizedBarcode = NormalizeBarcode(product?.Barcode);
                        var normalizedRemoteProductId = (product?.RemoteProductId ?? string.Empty).Trim();
                        if (product == null ||
                            normalizedBarcode.Length == 0 ||
                            normalizedRemoteProductId.Length == 0)
                        {
                            result.ProductsSkipped += 1;
                            continue;
                        }

                        if (activeRemoteIdsByBarcode.TryGetValue(
                                normalizedBarcode,
                                out var existingRemoteProductIds) &&
                            existingRemoteProductIds.Any(existingRemoteProductId =>
                                !string.Equals(
                                    existingRemoteProductId,
                                    normalizedRemoteProductId,
                                    StringComparison.Ordinal) &&
                                !tombstonedRemoteProductIds.Contains(existingRemoteProductId)))
                        {
                            // A barcode may adopt a remote identity only from a local/unbound row.
                            // Replacing a different live remote identity would collapse two
                            // authoritative products, so fail this row closed and let the pull
                            // service request a full repair before advancing its cursor.
                            result.ProductsSkipped += 1;
                            result.ProductIdentityConflicts += 1;
                            continue;
                        }

                        var categoryId = product.CategoryId;
                        var remoteCategoryId = (product.RemoteCategoryId ?? string.Empty).Trim();
                        if (!categoryId.HasValue && remoteCategoryId.Length > 0 &&
                            categoryIdsByRemoteId.TryGetValue(remoteCategoryId, out var resolvedCategoryId))
                        {
                            categoryId = resolvedCategoryId;
                        }

                        var supplierId = product.SupplierId;
                        var remoteSupplierId = (product.RemoteSupplierId ?? string.Empty).Trim();
                        if (!supplierId.HasValue && remoteSupplierId.Length > 0 &&
                            supplierIdsByRemoteId.TryGetValue(remoteSupplierId, out var resolvedSupplierId))
                        {
                            supplierId = resolvedSupplierId;
                        }

                        await ProductRepository.UpsertProductAndMetaInTransactionCoreAsync(
                            conn,
                            tx,
                             new Product
                             {
                                 Barcode = product.Barcode.Trim(),
                                Name = product.Name,
                                UnitPrice = product.UnitPrice
                            },
                            product.ArticleCode,
                            product.SecondName,
                            product.PurchasePrice,
                            supplierId,
                            product.SupplierName,
                            categoryId,
                            product.CategoryName,
                            product.StockQuantity,
                             normalizedRemoteProductId,
                            preparedProductCommands,
                            productBatchContext).ConfigureAwait(false);
                         await preparedReferenceCommand.UpsertAsync(
                             normalizedRemoteProductId,
                             product.RemoteCategoryId,
                             product.RemoteSupplierId).ConfigureAwait(false);
                        if (activeBarcodeByRemoteId.TryGetValue(
                                normalizedRemoteProductId,
                                out var previousBarcode) &&
                            !string.Equals(previousBarcode, normalizedBarcode, StringComparison.OrdinalIgnoreCase) &&
                            activeRemoteIdsByBarcode.TryGetValue(previousBarcode, out var previousIds))
                        {
                            previousIds.Remove(normalizedRemoteProductId);
                        }

                        if (!activeRemoteIdsByBarcode.TryGetValue(
                                normalizedBarcode,
                                out var currentRemoteProductIds))
                        {
                            currentRemoteProductIds = new HashSet<string>(StringComparer.Ordinal);
                            activeRemoteIdsByBarcode[normalizedBarcode] = currentRemoteProductIds;
                        }

                        currentRemoteProductIds.RemoveWhere(tombstonedRemoteProductIds.Contains);
                        currentRemoteProductIds.Add(normalizedRemoteProductId);
                        activeBarcodeByRemoteId[normalizedRemoteProductId] = normalizedBarcode;
                        relinkProductRemoteIds.Add(normalizedRemoteProductId);
                         result.ProductsApplied += 1;
                     }

                    var pendingReplay = await ProductRepository
                        .ApplyPendingRemotePricesInTransactionAsync(conn, tx)
                        .ConfigureAwait(false);
                    result.PendingPricesApplied += pendingReplay.Applied;
                    pendingPriceCollisionIds.UnionWith(pendingReplay.CollisionIds);

                    foreach (var tombstone in batch.ProductTombstones ?? Array.Empty<RemoteCatalogProductTombstoneWrite>())
                    {
                        if (tombstone == null || string.IsNullOrWhiteSpace(tombstone.RemoteProductId))
                        {
                            result.TombstonesSkipped += 1;
                            continue;
                        }

                        if (await ProductRepository.ApplyRemoteProductTombstoneInTransactionAsync(
                            conn,
                            tx,
                            tombstone.RemoteProductId,
                            tombstone.RemoteDeletedAt).ConfigureAwait(false))
                        {
                            result.ProductTombstonesApplied += 1;
                        }
                    }

                    await conn.ExecuteAsync(@"
DELETE FROM remote_catalog_product_references
WHERE NOT EXISTS (
  SELECT 1
  FROM products p
  WHERE p.remote_product_id = remote_catalog_product_references.remote_product_id
);", transaction: tx).ConfigureAwait(false);

                    foreach (var tombstone in batch.CategoryTombstones ?? Array.Empty<RemoteCatalogCategoryTombstoneWrite>())
                    {
                        if (tombstone == null || string.IsNullOrWhiteSpace(tombstone.RemoteCategoryId))
                        {
                            result.TombstonesSkipped += 1;
                            continue;
                        }

                        if (await CategoryRepository.ApplyRemoteTombstoneInTransactionAsync(
                            conn,
                            tx,
                            tombstone.RemoteCategoryId,
                            tombstone.RemoteDeletedAt,
                            tombstone.RemoteUpdatedAt).ConfigureAwait(false))
                        {
                            result.CategoryTombstonesApplied += 1;
                        }
                    }

                    foreach (var tombstone in batch.SupplierTombstones ?? Array.Empty<RemoteCatalogSupplierTombstoneWrite>())
                    {
                        if (tombstone == null || string.IsNullOrWhiteSpace(tombstone.RemoteSupplierId))
                        {
                            result.TombstonesSkipped += 1;
                            continue;
                        }

                        if (await SupplierRepository.ApplyRemoteTombstoneInTransactionAsync(
                            conn,
                            tx,
                            tombstone.RemoteSupplierId,
                            tombstone.RemoteDeletedAt,
                            tombstone.RemoteUpdatedAt).ConfigureAwait(false))
                        {
                            result.SupplierTombstonesApplied += 1;
                        }
                    }

                    result.ProductReferencesRelinked += await RelinkRemoteProductReferencesAsync(
                        conn,
                        tx,
                        relinkProductRemoteIds,
                        relinkCategoryRemoteIds,
                        relinkSupplierRemoteIds).ConfigureAwait(false);

                    foreach (var price in batch.Prices ?? Array.Empty<RemoteCatalogPriceWrite>())
                    {
                        if (price == null)
                        {
                            result.PricesSkipped += 1;
                            continue;
                        }

                        if (batch.AuthoritativeFullRefresh &&
                            !string.IsNullOrWhiteSpace(price.RemotePriceId))
                        {
                            if (!await ProductRepository.PrepareAuthoritativeRemotePriceRepairAsync(
                                    conn,
                                    tx,
                                    price.RemoteProductId,
                                    price.RemotePriceId,
                                    price.Type,
                                    price.Price,
                                    price.EffectiveAt,
                                    price.Source).ConfigureAwait(false))
                            {
                                result.PricesSkipped += 1;
                                continue;
                            }
                        }

                        var applied = await ProductRepository.UpsertOrQueueRemotePriceHistoryInTransactionAsync(
                            conn,
                            tx,
                            price.RemoteProductId,
                            price.RemotePriceId,
                            price.Type,
                            price.Price,
                            price.EffectiveAt,
                            price.Source).ConfigureAwait(false);
                        if (applied.Applied) result.PricesApplied += 1;
                        if (applied.Queued) result.PricesQueued += 1;
                        if (!applied.Applied && !applied.Queued) result.PricesSkipped += 1;
                    }

                    pendingReplay = await ProductRepository
                        .ApplyPendingRemotePricesInTransactionAsync(conn, tx)
                        .ConfigureAwait(false);
                    result.PendingPricesApplied += pendingReplay.Applied;
                    pendingPriceCollisionIds.UnionWith(pendingReplay.CollisionIds);
                    result.PricesSkipped += pendingPriceCollisionIds.Count;

                    tx.Commit();
                    return result;
                }
                catch
                {
                    try { tx.Rollback(); } catch { }
                    throw;
                }
            }
            finally
            {
                ProductRepository.CatalogMetaWriteGate.Release();
            }
        }

        private static async Task RequireCommitFenceAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            RemoteCatalogCommitFence commitFence)
        {
            if (commitFence == null)
            {
                return;
            }

            var boundShopId = await ReadSettingAsync(
                conn,
                tx,
                CatalogShopStateRepository.BoundShopIdKey).ConfigureAwait(false);
            var boundShopCode = await ReadSettingAsync(
                conn,
                tx,
                CatalogShopStateRepository.BoundShopCodeKey).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                boundShopId,
                boundShopCode,
                commitFence.ShopId,
                commitFence.ShopCode)))
            {
                throw new InvalidOperationException("Catalog state shop binding mismatch.");
            }

            var rawEpoch = await ReadSettingAsync(
                conn,
                tx,
                CatalogShopStateRepository.TransitionEpochKey).ConfigureAwait(false);
            if (!long.TryParse(rawEpoch, out var epoch) || epoch != commitFence.ExpectedEpoch)
            {
                throw new InvalidOperationException("Catalog state transition epoch mismatch.");
            }

            var currentCursor = (await ReadSettingAsync(
                conn,
                tx,
                CatalogShopStateRepository.LastSyncCursorKey).ConfigureAwait(false) ?? string.Empty).Trim();
            if (!string.Equals(
                currentCursor,
                (commitFence.ExpectedPreviousCursor ?? string.Empty).Trim(),
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Catalog state previous cursor mismatch.");
            }

            var currentMode = (await ReadSettingAsync(
                conn,
                tx,
                CatalogShopStateRepository.LastSyncModeKey).ConfigureAwait(false) ?? string.Empty).Trim();
            if (!string.Equals(
                currentMode,
                (commitFence.ExpectedPreviousMode ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Catalog state previous mode mismatch.");
            }
        }

        private static Task<string> ReadSettingAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string key)
        {
            return conn.ExecuteScalarAsync<string>(
                "SELECT value FROM app_settings WHERE key = @key;",
                new { key },
                tx);
        }

        private static Task<int> RelinkRemoteProductReferencesAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            IReadOnlyCollection<string> productRemoteIds,
            IReadOnlyCollection<string> categoryRemoteIds,
            IReadOnlyCollection<string> supplierRemoteIds)
        {
            if ((productRemoteIds?.Count ?? 0) == 0 &&
                (categoryRemoteIds?.Count ?? 0) == 0 &&
                (supplierRemoteIds?.Count ?? 0) == 0)
            {
                return Task.FromResult(0);
            }

            return RelinkRemoteProductReferencesCoreAsync(
                conn,
                tx,
                productRemoteIds ?? Array.Empty<string>(),
                categoryRemoteIds ?? Array.Empty<string>(),
                supplierRemoteIds ?? Array.Empty<string>());
        }

        private static async Task<int> RelinkRemoteProductReferencesCoreAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            IReadOnlyCollection<string> productRemoteIds,
            IReadOnlyCollection<string> categoryRemoteIds,
            IReadOnlyCollection<string> supplierRemoteIds)
        {
            await conn.ExecuteAsync(@"
CREATE TEMP TABLE IF NOT EXISTS temp_catalog_relink_product_ids (
  remote_product_id TEXT PRIMARY KEY
);
CREATE TEMP TABLE IF NOT EXISTS temp_catalog_relink_category_ids (
  remote_category_id TEXT PRIMARY KEY
);
CREATE TEMP TABLE IF NOT EXISTS temp_catalog_relink_supplier_ids (
  remote_supplier_id TEXT PRIMARY KEY
);
CREATE TEMP TABLE IF NOT EXISTS temp_catalog_relink_barcodes (
  barcode TEXT PRIMARY KEY
);
DELETE FROM temp_catalog_relink_product_ids;
DELETE FROM temp_catalog_relink_category_ids;
DELETE FROM temp_catalog_relink_supplier_ids;
DELETE FROM temp_catalog_relink_barcodes;",
                transaction: tx).ConfigureAwait(false);

            await FillTempRelinkIdsAsync(
                conn,
                tx,
                "temp_catalog_relink_product_ids",
                "remote_product_id",
                productRemoteIds).ConfigureAwait(false);
            await FillTempRelinkIdsAsync(
                conn,
                tx,
                "temp_catalog_relink_category_ids",
                "remote_category_id",
                categoryRemoteIds).ConfigureAwait(false);
            await FillTempRelinkIdsAsync(
                conn,
                tx,
                "temp_catalog_relink_supplier_ids",
                "remote_supplier_id",
                supplierRemoteIds).ConfigureAwait(false);

            await conn.ExecuteAsync(@"
INSERT OR IGNORE INTO temp_catalog_relink_product_ids(remote_product_id)
SELECT r.remote_product_id
FROM remote_catalog_product_references r
JOIN temp_catalog_relink_category_ids incoming
  ON incoming.remote_category_id = r.remote_category_id;

INSERT OR IGNORE INTO temp_catalog_relink_product_ids(remote_product_id)
SELECT r.remote_product_id
FROM remote_catalog_product_references r
JOIN temp_catalog_relink_supplier_ids incoming
  ON incoming.remote_supplier_id = r.remote_supplier_id;

INSERT OR IGNORE INTO temp_catalog_relink_barcodes(barcode)
SELECT p.barcode
FROM products p
JOIN temp_catalog_relink_product_ids target
  ON target.remote_product_id = p.remote_product_id
WHERE COALESCE(p.is_active, 1) = 1;",
                transaction: tx).ConfigureAwait(false);

            return await conn.ExecuteAsync(@"
UPDATE product_meta
SET category_id = (
      SELECT c.id
      FROM products p
      JOIN remote_catalog_product_references r
        ON r.remote_product_id = p.remote_product_id
      JOIN categories c
        ON c.remote_category_id = r.remote_category_id
       AND COALESCE(c.is_active, 1) = 1
      WHERE p.barcode = product_meta.barcode
        AND COALESCE(p.is_active, 1) = 1
        AND COALESCE(r.remote_category_id, '') <> ''
      LIMIT 1
    ),
    category_name = COALESCE((
      SELECT c.name
      FROM products p
      JOIN remote_catalog_product_references r
        ON r.remote_product_id = p.remote_product_id
      JOIN categories c
        ON c.remote_category_id = r.remote_category_id
       AND COALESCE(c.is_active, 1) = 1
      WHERE p.barcode = product_meta.barcode
        AND COALESCE(p.is_active, 1) = 1
        AND COALESCE(r.remote_category_id, '') <> ''
      LIMIT 1
    ), ''),
    supplier_id = (
      SELECT s.id
      FROM products p
      JOIN remote_catalog_product_references r
        ON r.remote_product_id = p.remote_product_id
      JOIN suppliers s
        ON s.remote_supplier_id = r.remote_supplier_id
       AND COALESCE(s.is_active, 1) = 1
      WHERE p.barcode = product_meta.barcode
        AND COALESCE(p.is_active, 1) = 1
        AND COALESCE(r.remote_supplier_id, '') <> ''
      LIMIT 1
    ),
    supplier_name = COALESCE((
      SELECT s.name
      FROM products p
      JOIN remote_catalog_product_references r
        ON r.remote_product_id = p.remote_product_id
      JOIN suppliers s
        ON s.remote_supplier_id = r.remote_supplier_id
       AND COALESCE(s.is_active, 1) = 1
      WHERE p.barcode = product_meta.barcode
        AND COALESCE(p.is_active, 1) = 1
        AND COALESCE(r.remote_supplier_id, '') <> ''
      LIMIT 1
    ), '')
WHERE barcode IN (
  SELECT barcode
  FROM temp_catalog_relink_barcodes
);",
                transaction: tx).ConfigureAwait(false);
        }

        private static async Task FillTempRelinkIdsAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string tableName,
            string columnName,
            IEnumerable<string> values)
        {
            using var command = conn.CreateCommand();
            command.Transaction = tx;
            command.CommandText =
                "INSERT OR IGNORE INTO " + tableName + "(" + columnName + ") VALUES(@value);";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@value";
            command.Parameters.Add(parameter);
            command.Prepare();
            foreach (var value in (values ?? Enumerable.Empty<string>())
                .Select(item => (item ?? string.Empty).Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.Ordinal))
            {
                parameter.Value = value;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        private static string NormalizeBarcode(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private sealed class RemoteProductReferencePreparedCommand : IDisposable
        {
            private readonly SqliteCommand _command;

            public RemoteProductReferencePreparedCommand(SqliteConnection conn, SqliteTransaction tx)
            {
                _command = conn.CreateCommand();
                _command.Transaction = tx;
                _command.CommandText = @"
INSERT INTO remote_catalog_product_references(
  remote_product_id,
  remote_category_id,
  remote_supplier_id)
VALUES(
  @RemoteProductId,
  NULLIF(@RemoteCategoryId, ''),
  NULLIF(@RemoteSupplierId, ''))
ON CONFLICT(remote_product_id) DO UPDATE SET
  remote_category_id = excluded.remote_category_id,
  remote_supplier_id = excluded.remote_supplier_id;";
                _command.Parameters.Add(new SqliteParameter("@RemoteProductId", string.Empty));
                _command.Parameters.Add(new SqliteParameter("@RemoteCategoryId", string.Empty));
                _command.Parameters.Add(new SqliteParameter("@RemoteSupplierId", string.Empty));
                _command.Prepare();
            }

            public void Dispose()
            {
                _command.Dispose();
            }

            public async Task UpsertAsync(
                string remoteProductId,
                string remoteCategoryId,
                string remoteSupplierId)
            {
                _command.Parameters["@RemoteProductId"].Value =
                    (remoteProductId ?? string.Empty).Trim();
                _command.Parameters["@RemoteCategoryId"].Value =
                    (remoteCategoryId ?? string.Empty).Trim();
                _command.Parameters["@RemoteSupplierId"].Value =
                    (remoteSupplierId ?? string.Empty).Trim();
                await _command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
    }

    public sealed class RemoteCatalogCommitFence
    {
        public long ExpectedEpoch { get; set; }
        public string ExpectedPreviousCursor { get; set; } = string.Empty;
        public string ExpectedPreviousMode { get; set; } = string.Empty;
        public string ShopCode { get; set; } = string.Empty;
        public string ShopId { get; set; } = string.Empty;
    }

    public sealed class RemoteCatalogBatch
    {
        public bool AuthoritativeFullRefresh { get; set; }
        public IReadOnlyList<RemoteCatalogCategoryWrite> Categories { get; set; } =
            Array.Empty<RemoteCatalogCategoryWrite>();
        public IReadOnlyList<RemoteCatalogCategoryTombstoneWrite> CategoryTombstones { get; set; } =
            Array.Empty<RemoteCatalogCategoryTombstoneWrite>();
        public IReadOnlyList<RemoteCatalogPriceWrite> Prices { get; set; } =
            Array.Empty<RemoteCatalogPriceWrite>();
        public IReadOnlyList<RemoteCatalogProductWrite> Products { get; set; } =
            Array.Empty<RemoteCatalogProductWrite>();
        public IReadOnlyList<RemoteCatalogProductTombstoneWrite> ProductTombstones { get; set; } =
            Array.Empty<RemoteCatalogProductTombstoneWrite>();
        public IReadOnlyList<RemoteCatalogSupplierWrite> Suppliers { get; set; } =
            Array.Empty<RemoteCatalogSupplierWrite>();
        public IReadOnlyList<RemoteCatalogSupplierTombstoneWrite> SupplierTombstones { get; set; } =
            Array.Empty<RemoteCatalogSupplierTombstoneWrite>();
    }

    public sealed class RemoteCatalogCategoryWrite
    {
        public string Name { get; set; } = string.Empty;
        public string RemoteCategoryId { get; set; } = string.Empty;
        public string RemoteUpdatedAt { get; set; } = string.Empty;
    }

    public sealed class RemoteCatalogSupplierWrite
    {
        public string Name { get; set; } = string.Empty;
        public string RemoteSupplierId { get; set; } = string.Empty;
        public string RemoteUpdatedAt { get; set; } = string.Empty;
    }

    public sealed class RemoteCatalogProductWrite
    {
        public string ArticleCode { get; set; } = string.Empty;
        public string Barcode { get; set; } = string.Empty;
        public int? CategoryId { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int PurchasePrice { get; set; }
        public string RemoteCategoryId { get; set; } = string.Empty;
        public string RemoteProductId { get; set; } = string.Empty;
        public string RemoteSupplierId { get; set; } = string.Empty;
        public string SecondName { get; set; } = string.Empty;
        public int StockQuantity { get; set; }
        public int? SupplierId { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public long UnitPrice { get; set; }
    }

    internal sealed class RemoteCatalogReferenceRow
    {
        public int Id { get; set; }
        public string RemoteId { get; set; } = string.Empty;
    }

    internal sealed class RemoteCatalogProductIdentityRow
    {
        public string Barcode { get; set; } = string.Empty;
        public string RemoteProductId { get; set; } = string.Empty;
    }

    public sealed class RemoteCatalogPriceWrite
    {
        public string EffectiveAt { get; set; } = string.Empty;
        public int Price { get; set; }
        public string RemotePriceId { get; set; } = string.Empty;
        public string RemoteProductId { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
    }

    public sealed class RemoteCatalogProductTombstoneWrite
    {
        public string RemoteDeletedAt { get; set; } = string.Empty;
        public string RemoteProductId { get; set; } = string.Empty;
    }

    public sealed class RemoteCatalogCategoryTombstoneWrite
    {
        public string RemoteCategoryId { get; set; } = string.Empty;
        public string RemoteDeletedAt { get; set; } = string.Empty;
        public string RemoteUpdatedAt { get; set; } = string.Empty;
    }

    public sealed class RemoteCatalogSupplierTombstoneWrite
    {
        public string RemoteDeletedAt { get; set; } = string.Empty;
        public string RemoteSupplierId { get; set; } = string.Empty;
        public string RemoteUpdatedAt { get; set; } = string.Empty;
    }

    public sealed class RemoteCatalogBatchApplyResult
    {
        public int CategoriesApplied { get; internal set; }
        public int CategoriesSkipped { get; internal set; }
        public int CategoryTombstonesApplied { get; internal set; }
        public int PendingPricesApplied { get; internal set; }
        public int PricesApplied { get; internal set; }
        public int PricesQueued { get; internal set; }
        public int PricesSkipped { get; internal set; }
        public int ProductsApplied { get; internal set; }
        public int ProductsSkipped { get; internal set; }
        public int ProductTombstonesApplied { get; internal set; }
        public int ProductReferencesRelinked { get; internal set; }
        public int ProductIdentityConflicts { get; internal set; }
        public int SuppliersApplied { get; internal set; }
        public int SuppliersSkipped { get; internal set; }
        public int SupplierTombstonesApplied { get; internal set; }
        public int TombstonesApplied =>
            ProductTombstonesApplied + CategoryTombstonesApplied + SupplierTombstonesApplied;
        public int TombstonesSkipped { get; internal set; }
        public int RowsSkipped =>
            CategoriesSkipped + SuppliersSkipped + ProductsSkipped + PricesSkipped + TombstonesSkipped;
    }
}
