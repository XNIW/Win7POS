using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Receipt;
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

        public RemoteCatalogApplyRunContext CreateRunContext()
        {
            return new RemoteCatalogApplyRunContext(this, _factory);
        }

        public async Task<RemoteCatalogBatchApplyResult> ApplyAsync(
            RemoteCatalogBatch batch,
            CancellationToken cancellationToken = default,
            RemoteCatalogCommitFence commitFence = null)
        {
            using (var runContext = CreateRunContext())
            {
                return await runContext.ApplyAsync(
                    batch,
                    cancellationToken,
                    commitFence).ConfigureAwait(false);
            }
        }

        public async Task<CatalogAuthoritativeStageEvidence> StageAuthoritativePageAsync(
            RemoteCatalogBatch batch,
            CancellationToken cancellationToken = default,
            RemoteCatalogCommitFence commitFence = null)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            ValidateBatchContent(batch);
            ValidateAuthoritativeStage(batch, commitFence, required: true);

            cancellationToken.ThrowIfCancellationRequested();
            await CatalogMutationGate.Instance.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var conn = _factory.Open();
                using var tx = conn.BeginTransaction(deferred: false);
                try
                {
                    await RequireCommitFenceAsync(conn, tx, commitFence).ConfigureAwait(false);
                    await ReplaceAuthoritativeStagePageAsync(
                        conn,
                        tx,
                        batch,
                        commitFence).ConfigureAwait(false);
                    var evidence = await LoadAuthoritativeStageEvidenceAsync(
                        conn,
                        tx,
                        batch.AuthoritativeStagePage,
                        commitFence).ConfigureAwait(false);
                    tx.Commit();
                    return evidence;
                }
                catch
                {
                    try { tx.Rollback(); } catch { }
                    throw;
                }
            }
            finally
            {
                CatalogMutationGate.Instance.Release();
            }
        }

        internal async Task<RemoteCatalogBatchApplyResult> ApplyWithinRunAsync(
            RemoteCatalogApplyRunContext runContext,
            RemoteCatalogBatch batch,
            CancellationToken cancellationToken,
            RemoteCatalogCommitFence commitFence)
        {
            if (runContext == null) throw new ArgumentNullException(nameof(runContext));
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            ValidateBatchContent(batch);
            ValidateAuthoritativeStage(batch, commitFence, required: false);

            cancellationToken.ThrowIfCancellationRequested();
            await CatalogMutationGate.Instance.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var conn = runContext.Connection;
                using var tx = conn.BeginTransaction(deferred: false);
                runContext.BindTransaction(tx);
                try
                {
                    await RequireCommitFenceAsync(conn, tx, commitFence).ConfigureAwait(false);
                    if (batch.AuthoritativeStagePage != null)
                    {
                        await ReplaceAuthoritativeStagePageAsync(
                            conn,
                            tx,
                            batch,
                            commitFence).ConfigureAwait(false);
                    }
                    var result = new RemoteCatalogBatchApplyResult();
                    var pageRemotePriceApply = new RemotePriceApplyDiagnostics();
                    var appliedCategoryTombstoneIds = new HashSet<string>(StringComparer.Ordinal);
                    var appliedSupplierTombstoneIds = new HashSet<string>(StringComparer.Ordinal);
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
                        else if (batch.AuthoritativeFullRefresh)
                        {
                            result.CategoriesSkipped += 1;
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
                        else if (batch.AuthoritativeFullRefresh)
                        {
                            result.SuppliersSkipped += 1;
                        }
                    }

                    var pageState = await runContext
                        .CreatePageStateAsync(batch, tx)
                        .ConfigureAwait(false);
                    var categoryIdsByRemoteId = pageState.CategoryIdsByRemoteId;
                    var supplierIdsByRemoteId = pageState.SupplierIdsByRemoteId;
                    await runContext.StagePageProductsAsync(batch, tx).ConfigureAwait(false);
                    var pendingStockIdentities = await runContext
                        .LoadPagePendingStockAsync(tx)
                        .ConfigureAwait(false);
                    var productBatchContext = pageState.ProductContext.CloneWithPendingStock(
                        pendingStockIdentities.Select(row => row.Barcode),
                        pendingStockIdentities.Select(row => row.RemoteProductId));
                    var tombstonedRemoteProductIds = new HashSet<string>(
                        (batch.ProductTombstones ?? Array.Empty<RemoteCatalogProductTombstoneWrite>())
                            .Where(row => row != null && !string.IsNullOrWhiteSpace(row.RemoteProductId))
                            .Select(row => row.RemoteProductId.Trim()),
                        StringComparer.Ordinal);
                    var activeProductIdentities = (await runContext
                        .LoadPageProductIdentitiesAsync(tx)
                        .ConfigureAwait(false))
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
                    var preparedProductCommands = runContext.ProductCommands;
                    var preparedReferenceCommand = runContext.ReferenceCommand;
                    var pageProducts = batch.Products ?? Array.Empty<RemoteCatalogProductWrite>();
                    var pageRequiresLegacyProductPath = await runContext
                        .PageRequiresLegacyProductRebindAsync(tx)
                        .ConfigureAwait(false);
                    if (!pageRequiresLegacyProductPath)
                    {
                        var validIdentities = pageProducts
                            .Where(product =>
                                product != null &&
                                !string.IsNullOrWhiteSpace(product.Barcode) &&
                                !string.IsNullOrWhiteSpace(product.RemoteProductId))
                            .Select(product => new
                            {
                                Barcode = NormalizeBarcode(product.Barcode),
                                RemoteProductId = product.RemoteProductId.Trim()
                            })
                            .ToArray();
                        pageRequiresLegacyProductPath =
                            validIdentities
                                .GroupBy(identity => identity.Barcode, StringComparer.OrdinalIgnoreCase)
                                .Any(group => group.Count() > 1) ||
                            validIdentities
                                .GroupBy(identity => identity.RemoteProductId, StringComparer.Ordinal)
                                .Any(group => group.Count() > 1);
                    }

                    var cleanProducts =
                        new List<RemoteCatalogProductWriter.RemoteCatalogSetProductWrite>(
                            pageProducts.Count);
                    foreach (var product in pageProducts)
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

                        SalesReceiptContentPolicy.EnsureValidProductIdentity(
                            normalizedBarcode,
                            product.Name);
                        if (ProductIdentityPolicy.IsReservedBarcode(normalizedBarcode))
                        {
                            throw new InvalidOperationException("Barcode riservato (DISC:/MANUAL:).");
                        }

                        var hasPendingLocalStock = productBatchContext.HasPendingLocalStock(
                            normalizedBarcode,
                            normalizedRemoteProductId);
                        if (!pageRequiresLegacyProductPath && !hasPendingLocalStock)
                        {
                            var supplierRef = await productBatchContext.ResolveSupplierAsync(
                                conn,
                                tx,
                                supplierId,
                                product.SupplierName).ConfigureAwait(false);
                            var categoryRef = await productBatchContext.ResolveCategoryAsync(
                                conn,
                                tx,
                                categoryId,
                                product.CategoryName).ConfigureAwait(false);
                            cleanProducts.Add(
                                new RemoteCatalogProductWriter.RemoteCatalogSetProductWrite
                                {
                                    ArticleCode = product.ArticleCode ?? string.Empty,
                                    Barcode = normalizedBarcode,
                                    CategoryId = categoryRef.Id,
                                    CategoryName = categoryRef.Name ?? string.Empty,
                                    Name = product.Name,
                                    PurchasePrice = product.PurchasePrice,
                                    RemoteCategoryId =
                                        (product.RemoteCategoryId ?? string.Empty).Trim(),
                                    RemoteProductId = normalizedRemoteProductId,
                                    RemoteSupplierId =
                                        (product.RemoteSupplierId ?? string.Empty).Trim(),
                                    SecondName = product.SecondName ?? string.Empty,
                                    StockQuantity = product.StockQuantity,
                                    SupplierId = supplierRef.Id,
                                    SupplierName = supplierRef.Name ?? string.Empty,
                                    UnitPrice = product.UnitPrice
                                });
                        }
                        else
                        {
                            await RemoteCatalogProductWriter.UpsertProductAndMetaInTransactionCoreAsync(
                                conn,
                                tx,
                                new Product
                                {
                                    Barcode = normalizedBarcode,
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
                        }
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

                    await runContext.StageCleanProductsAsync(cleanProducts).ConfigureAwait(false);
                    await RemoteCatalogProductWriter.ApplyCleanProductsSetBasedInTransactionAsync(
                        conn,
                        tx,
                        cleanProducts).ConfigureAwait(false);

                    var pendingReplay = await RemotePriceHistoryRepository
                        .ApplyPendingRemotePricesInTransactionAsync(
                            conn,
                            tx,
                            pageRemotePriceApply)
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

                        if (await RemoteCatalogProductWriter.ApplyRemoteProductTombstoneInTransactionAsync(
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
WHERE remote_product_id IN (
  SELECT remote_product_id
  FROM temp_catalog_page_product_identities
  WHERE remote_product_id <> ''
)
AND NOT EXISTS (
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
                            appliedCategoryTombstoneIds.Add(tombstone.RemoteCategoryId.Trim());
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
                            appliedSupplierTombstoneIds.Add(tombstone.RemoteSupplierId.Trim());
                        }
                    }

                    result.ProductReferencesRelinked += await RelinkRemoteProductReferencesAsync(
                        conn,
                        tx,
                        relinkProductRemoteIds,
                        relinkCategoryRemoteIds,
                        relinkSupplierRemoteIds).ConfigureAwait(false);

                    var prices = batch.Prices ?? Array.Empty<RemoteCatalogPriceWrite>();
                    var setBasedPrices = await RemotePriceHistoryRepository
                        .TryApplyRemotePricesSetBasedInTransactionAsync(
                            conn,
                            tx,
                            prices,
                            pageRemotePriceApply)
                        .ConfigureAwait(false);
                    if (setBasedPrices != null)
                    {
                        result.PricesApplied += setBasedPrices.Applied;
                        result.PricesQueued += setBasedPrices.Queued;
                        result.PricesSkipped += setBasedPrices.Skipped;
                    }
                    else
                    {
                        foreach (var price in prices)
                        {
                            if (price == null)
                            {
                                result.PricesSkipped += 1;
                                continue;
                            }

                            if (batch.AuthoritativeFullRefresh &&
                                !string.IsNullOrWhiteSpace(price.RemotePriceId))
                            {
                                if (!await RemotePriceHistoryRepository.PrepareAuthoritativeRemotePriceRepairAsync(
                                        conn,
                                        tx,
                                        price.RemoteProductId,
                                        price.RemotePriceId,
                                        price.Type,
                                        price.Price,
                                        price.EffectiveAt,
                                        price.Source,
                                        pageRemotePriceApply).ConfigureAwait(false))
                                {
                                    result.PricesSkipped += 1;
                                    continue;
                                }
                            }

                            var applied = await RemotePriceHistoryRepository.UpsertOrQueueRemotePriceHistoryInTransactionAsync(
                                conn,
                                tx,
                                price.RemoteProductId,
                                price.RemotePriceId,
                                price.Type,
                                price.Price,
                                price.EffectiveAt,
                                price.Source,
                                pageRemotePriceApply).ConfigureAwait(false);
                            if (applied.Applied) result.PricesApplied += 1;
                            if (applied.Queued) result.PricesQueued += 1;
                            if (!applied.Applied && !applied.Queued) result.PricesSkipped += 1;
                        }
                    }

                    pendingReplay = await RemotePriceHistoryRepository
                        .ApplyPendingRemotePricesInTransactionAsync(
                            conn,
                            tx,
                            pageRemotePriceApply)
                        .ConfigureAwait(false);
                    result.PendingPricesApplied += pendingReplay.Applied;
                    pendingPriceCollisionIds.UnionWith(pendingReplay.CollisionIds);
                    result.PricesSkipped += pendingPriceCollisionIds.Count;

                    pageState.ProductContext = productBatchContext.WithoutPendingStock();
                    pageState.RemoveTombstonedReferences(
                        appliedCategoryTombstoneIds,
                        appliedSupplierTombstoneIds);
                    tx.Commit();
                    runContext.RecordRemotePriceApply(pageRemotePriceApply);
                    runContext.CommitPageState(pageState);
                    runContext.RecordPageApplied();
                    return result;
                }
                catch
                {
                    try { tx.Rollback(); } catch { }
                    throw;
                }
                finally
                {
                    runContext.BindTransaction(null);
                }
            }
            finally
            {
                CatalogMutationGate.Instance.Release();
            }
        }

        private static void ValidateBatchContent(RemoteCatalogBatch batch)
        {
            foreach (var row in batch.Categories ?? Array.Empty<RemoteCatalogCategoryWrite>())
            {
                if (row == null) continue;
                EnsureSafe(row.RemoteCategoryId, RemoteCatalogContentPolicy.RemoteIdMaximumLength, "category.remote_id");
                EnsureSafe(row.Name, RemoteCatalogContentPolicy.NameMaximumLength, "category.name");
                RemoteCatalogContentPolicy.EnsureOptionalTimestamp(row.RemoteUpdatedAt, "category.updated_at");
            }

            foreach (var row in batch.Suppliers ?? Array.Empty<RemoteCatalogSupplierWrite>())
            {
                if (row == null) continue;
                EnsureSafe(row.RemoteSupplierId, RemoteCatalogContentPolicy.RemoteIdMaximumLength, "supplier.remote_id");
                EnsureSafe(row.Name, RemoteCatalogContentPolicy.NameMaximumLength, "supplier.name");
                RemoteCatalogContentPolicy.EnsureOptionalTimestamp(row.RemoteUpdatedAt, "supplier.updated_at");
            }

            foreach (var row in batch.Products ?? Array.Empty<RemoteCatalogProductWrite>())
            {
                if (row == null) continue;
                EnsureSafe(row.ArticleCode, RemoteCatalogContentPolicy.ItemNumberMaximumLength, "product.article_code");
                EnsureSafe(row.Barcode, RemoteCatalogContentPolicy.BarcodeMaximumLength, "product.barcode");
                EnsureSafe(row.CategoryName, RemoteCatalogContentPolicy.NameMaximumLength, "product.category_name");
                EnsureSafe(row.Name, RemoteCatalogContentPolicy.NameMaximumLength, "product.name");
                EnsureSafe(row.RemoteCategoryId, RemoteCatalogContentPolicy.RemoteIdMaximumLength, "product.remote_category_id");
                EnsureSafe(row.RemoteProductId, RemoteCatalogContentPolicy.RemoteIdMaximumLength, "product.remote_id");
                EnsureSafe(row.RemoteSupplierId, RemoteCatalogContentPolicy.RemoteIdMaximumLength, "product.remote_supplier_id");
                EnsureSafe(row.SecondName, RemoteCatalogContentPolicy.NameMaximumLength, "product.second_name");
                EnsureSafe(row.SupplierName, RemoteCatalogContentPolicy.NameMaximumLength, "product.supplier_name");
            }

            foreach (var row in batch.Prices ?? Array.Empty<RemoteCatalogPriceWrite>())
            {
                if (row == null) continue;
                RemoteCatalogContentPolicy.EnsureOptionalTimestamp(row.EffectiveAt, "price.effective_at");
                EnsureSafe(row.RemotePriceId, RemoteCatalogContentPolicy.RemoteIdMaximumLength, "price.remote_id");
                EnsureSafe(row.RemoteProductId, RemoteCatalogContentPolicy.RemoteIdMaximumLength, "price.remote_product_id");
                EnsureSafe(row.Source, RemoteCatalogContentPolicy.SourceMaximumLength, "price.source");
                EnsureSafe(row.Type, RemoteCatalogContentPolicy.TypeMaximumLength, "price.type");
            }

            foreach (var row in batch.ProductTombstones ?? Array.Empty<RemoteCatalogProductTombstoneWrite>())
            {
                if (row == null) continue;
                RemoteCatalogContentPolicy.EnsureOptionalTimestamp(row.RemoteDeletedAt, "product_tombstone.deleted_at");
                EnsureSafe(row.RemoteProductId, RemoteCatalogContentPolicy.RemoteIdMaximumLength, "product_tombstone.remote_id");
                RemoteCatalogContentPolicy.EnsureOptionalTimestamp(row.RemoteUpdatedAt, "product_tombstone.updated_at");
            }

            foreach (var row in batch.CategoryTombstones ?? Array.Empty<RemoteCatalogCategoryTombstoneWrite>())
            {
                if (row == null) continue;
                EnsureSafe(row.RemoteCategoryId, RemoteCatalogContentPolicy.RemoteIdMaximumLength, "category_tombstone.remote_id");
                RemoteCatalogContentPolicy.EnsureOptionalTimestamp(row.RemoteDeletedAt, "category_tombstone.deleted_at");
                RemoteCatalogContentPolicy.EnsureOptionalTimestamp(row.RemoteUpdatedAt, "category_tombstone.updated_at");
            }

            foreach (var row in batch.SupplierTombstones ?? Array.Empty<RemoteCatalogSupplierTombstoneWrite>())
            {
                if (row == null) continue;
                RemoteCatalogContentPolicy.EnsureOptionalTimestamp(row.RemoteDeletedAt, "supplier_tombstone.deleted_at");
                EnsureSafe(row.RemoteSupplierId, RemoteCatalogContentPolicy.RemoteIdMaximumLength, "supplier_tombstone.remote_id");
                RemoteCatalogContentPolicy.EnsureOptionalTimestamp(row.RemoteUpdatedAt, "supplier_tombstone.updated_at");
            }
        }

        private static void EnsureSafe(string value, int maximumLength, string field)
        {
            if (!RemoteCatalogContentPolicy.IsOptionalText(value, maximumLength))
            {
                    throw new InvalidDataException("Remote catalog field rejected before persistence: " + field + ".");
            }
        }

        private static void ValidateAuthoritativeStage(
            RemoteCatalogBatch batch,
            RemoteCatalogCommitFence commitFence,
            bool required)
        {
            var stage = batch?.AuthoritativeStagePage;
            if (stage == null)
            {
                if (required)
                    throw new InvalidDataException("Authoritative catalog stage page is required.");
                return;
            }

            if (!batch.AuthoritativeFullRefresh)
                throw new InvalidDataException("Authoritative catalog stage requires a full refresh batch.");
            if (commitFence == null)
                throw new InvalidDataException("Authoritative catalog stage requires a commit fence.");

            var runId = (stage.FullRunId ?? string.Empty).Trim();
            if (runId.Length == 0 ||
                runId.Length > 64 ||
                !string.Equals(runId, stage.FullRunId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Authoritative catalog full run identity is invalid.");
            }

            if (stage.PageNumber <= 0 || stage.PageNumber > 1000000)
                throw new InvalidDataException("Authoritative catalog page number is invalid.");
            if (commitFence.ExpectedEpoch < 0 ||
                string.IsNullOrWhiteSpace(commitFence.ShopId) ||
                string.IsNullOrWhiteSpace(commitFence.ShopCode))
            {
                throw new InvalidDataException("Authoritative catalog shop fence is invalid.");
            }

            var generationId = (commitFence.GenerationId ?? string.Empty).Trim();
            var generationFingerprint = (commitFence.GenerationFingerprint ?? string.Empty).Trim();
            if ((generationId.Length == 0) != (generationFingerprint.Length == 0))
                throw new InvalidDataException("Authoritative catalog generation fence is incomplete.");
        }

        private static async Task ReplaceAuthoritativeStagePageAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            RemoteCatalogBatch batch,
            RemoteCatalogCommitFence commitFence)
        {
            var stage = batch.AuthoritativeStagePage;
            var identity = CatalogAuthoritativeStageIdentity.Create(stage, commitFence);
            var stagedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            identity.ScopeId = await EnsureAuthoritativeStageScopeAsync(
                conn,
                tx,
                identity,
                stagedAt).ConfigureAwait(false);
            await conn.ExecuteAsync(@"
DELETE FROM catalog_authoritative_id_stage
WHERE scope_id = @ScopeId
  AND page_number = @PageNumber;",
                identity,
                tx).ConfigureAwait(false);

            var occurrences = new Dictionary<CatalogAuthoritativeStageKey, int>();
            AddStageOccurrences(
                occurrences,
                "product",
                batch.Products,
                row => row?.RemoteProductId,
                ProductStageFingerprint,
                row => row?.RemoteCategoryId,
                row => row?.RemoteSupplierId);
            AddStageOccurrences(
                occurrences,
                "category",
                batch.Categories,
                row => row?.RemoteCategoryId,
                CategoryStageFingerprint);
            AddStageOccurrences(
                occurrences,
                "supplier",
                batch.Suppliers,
                row => row?.RemoteSupplierId,
                SupplierStageFingerprint);
            AddStageOccurrences(
                occurrences,
                "price",
                batch.Prices,
                row => row?.RemotePriceId,
                PriceStageFingerprint,
                selectProductRemoteId: row => row?.RemoteProductId);
            AddStageOccurrences(
                occurrences,
                "product_tombstone",
                batch.ProductTombstones,
                row => row?.RemoteProductId,
                ProductTombstoneStageFingerprint);
            AddStageOccurrences(
                occurrences,
                "category_tombstone",
                batch.CategoryTombstones,
                row => row?.RemoteCategoryId,
                CategoryTombstoneStageFingerprint);
            AddStageOccurrences(
                occurrences,
                "supplier_tombstone",
                batch.SupplierTombstones,
                row => row?.RemoteSupplierId,
                SupplierTombstoneStageFingerprint);

            await conn.ExecuteAsync(@"
INSERT INTO catalog_authoritative_id_stage(
  scope_id,
  page_number,
  entity_kind,
  remote_id,
  content_fingerprint,
  category_remote_id,
  supplier_remote_id,
  product_remote_id,
  occurrence_count,
  has_more,
  staged_at)
VALUES(
  @scopeId,
  @pageNumber,
  'page',
  '',
  '',
  '',
  '',
  '',
  0,
  @hasMore,
  @stagedAt);",
                new
                {
                    scopeId = identity.ScopeId,
                    pageNumber = identity.PageNumber,
                    hasMore = stage.HasMore ? 1 : 0,
                    stagedAt
                },
                tx).ConfigureAwait(false);
            await InsertStageOccurrencesAsync(
                conn,
                tx,
                identity,
                occurrences,
                stagedAt).ConfigureAwait(false);
        }

        private static async Task<long> EnsureAuthoritativeStageScopeAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            CatalogAuthoritativeStageIdentity identity,
            long createdAt)
        {
            await conn.ExecuteAsync(@"
INSERT OR IGNORE INTO catalog_authoritative_stage_scope(
  shop_id,
  shop_code,
  transition_epoch,
  generation_id,
  generation_fingerprint,
  full_run_id,
  created_at)
VALUES(
  @ShopId,
  @ShopCode,
  @TransitionEpoch,
  @GenerationId,
  @GenerationFingerprint,
  @FullRunId,
  @createdAt);",
                new
                {
                    identity.ShopId,
                    identity.ShopCode,
                    identity.TransitionEpoch,
                    identity.GenerationId,
                    identity.GenerationFingerprint,
                    identity.FullRunId,
                    createdAt
                },
                tx).ConfigureAwait(false);
            return await conn.QuerySingleAsync<long>(@"
SELECT scope_id
FROM catalog_authoritative_stage_scope
WHERE shop_id = @ShopId
  AND shop_code = @ShopCode
  AND transition_epoch = @TransitionEpoch
  AND generation_id = @GenerationId
  AND generation_fingerprint = @GenerationFingerprint
  AND full_run_id = @FullRunId;",
                identity,
                tx).ConfigureAwait(false);
        }

        private static async Task InsertStageOccurrencesAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            CatalogAuthoritativeStageIdentity identity,
            IReadOnlyDictionary<CatalogAuthoritativeStageKey, int> occurrences,
            long stagedAt)
        {
            if (occurrences == null || occurrences.Count == 0) return;

            // One bounded page is encoded as JSON and expanded by SQLite's built-in
            // JSON table function. This keeps the durable insert at one statement per
            // page without retaining identities beyond the current page on x86.
            var rowsJson = new StringBuilder(Math.Max(256, occurrences.Count * 96));
            rowsJson.Append('[');
            var first = true;
            foreach (var occurrence in occurrences)
            {
                if (!first) rowsJson.Append(',');
                first = false;
                rowsJson.Append('[');
                AppendJsonString(rowsJson, occurrence.Key.EntityKind);
                rowsJson.Append(',');
                AppendJsonString(rowsJson, occurrence.Key.RemoteId);
                rowsJson.Append(',');
                AppendJsonString(rowsJson, occurrence.Key.ContentFingerprint);
                rowsJson.Append(',');
                AppendJsonString(rowsJson, occurrence.Key.CategoryRemoteId);
                rowsJson.Append(',');
                AppendJsonString(rowsJson, occurrence.Key.SupplierRemoteId);
                rowsJson.Append(',');
                AppendJsonString(rowsJson, occurrence.Key.ProductRemoteId);
                rowsJson.Append(',');
                rowsJson.Append(occurrence.Value.ToString(CultureInfo.InvariantCulture));
                rowsJson.Append(']');
            }
            rowsJson.Append(']');

            using var command = conn.CreateCommand();
            command.Transaction = tx;
            command.CommandText = @"
WITH incoming AS (
  SELECT
    CAST(json_extract(value, '$[0]') AS TEXT) AS entity_kind,
    CAST(json_extract(value, '$[1]') AS TEXT) AS remote_id,
    CAST(json_extract(value, '$[2]') AS TEXT) AS content_fingerprint,
    CAST(json_extract(value, '$[3]') AS TEXT) AS category_remote_id,
    CAST(json_extract(value, '$[4]') AS TEXT) AS supplier_remote_id,
    CAST(json_extract(value, '$[5]') AS TEXT) AS product_remote_id,
    CAST(json_extract(value, '$[6]') AS INTEGER) AS occurrence_count
  FROM json_each(@rowsJson)
)
INSERT INTO catalog_authoritative_id_stage(
  scope_id,
  page_number,
  entity_kind,
  remote_id,
  content_fingerprint,
  category_remote_id,
  supplier_remote_id,
  product_remote_id,
  occurrence_count,
  has_more,
  staged_at)
SELECT
  @scopeId,
  @pageNumber,
  entity_kind,
  remote_id,
  content_fingerprint,
  category_remote_id,
  supplier_remote_id,
  product_remote_id,
  occurrence_count,
  NULL,
  @stagedAt
FROM incoming;";
            AddParameter(command, "@scopeId", identity.ScopeId);
            AddParameter(command, "@pageNumber", identity.PageNumber);
            AddParameter(command, "@stagedAt", stagedAt);
            AddParameter(command, "@rowsJson", rowsJson.ToString());
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static void AppendJsonString(StringBuilder target, string value)
        {
            target.Append('"');
            foreach (var character in value ?? string.Empty)
            {
                switch (character)
                {
                    case '"': target.Append("\\\""); break;
                    case '\\': target.Append("\\\\"); break;
                    case '\b': target.Append("\\b"); break;
                    case '\f': target.Append("\\f"); break;
                    case '\n': target.Append("\\n"); break;
                    case '\r': target.Append("\\r"); break;
                    case '\t': target.Append("\\t"); break;
                    default:
                        if (char.IsControl(character))
                        {
                            target.Append("\\u");
                            target.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            target.Append(character);
                        }
                        break;
                }
            }
            target.Append('"');
        }

        private static SqliteParameter AddParameter(
            SqliteCommand command,
            string name,
            object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
            return parameter;
        }

        private static void AddStageOccurrences<T>(
            IDictionary<CatalogAuthoritativeStageKey, int> occurrences,
            string entityKind,
            IEnumerable<T> values,
            Func<T, string> selectRemoteId,
            Func<T, string> selectFingerprint,
            Func<T, string> selectCategoryRemoteId = null,
            Func<T, string> selectSupplierRemoteId = null,
            Func<T, string> selectProductRemoteId = null)
        {
            foreach (var value in values ?? Array.Empty<T>())
            {
                var key = new CatalogAuthoritativeStageKey(
                    entityKind,
                    (selectRemoteId(value) ?? string.Empty).Trim(),
                    selectFingerprint(value) ?? string.Empty,
                    (selectCategoryRemoteId?.Invoke(value) ?? string.Empty).Trim(),
                    (selectSupplierRemoteId?.Invoke(value) ?? string.Empty).Trim(),
                    (selectProductRemoteId?.Invoke(value) ?? string.Empty).Trim());
                occurrences.TryGetValue(key, out var count);
                occurrences[key] = checked(count + 1);
            }
        }

        private const string InvalidStageFingerprint = "invalid";

        private static string ProductStageFingerprint(RemoteCatalogProductWrite row)
        {
            if (row == null ||
                string.IsNullOrWhiteSpace(row.RemoteProductId) ||
                string.IsNullOrWhiteSpace(row.Barcode) ||
                string.IsNullOrWhiteSpace(row.Name) ||
                row.UnitPrice <= 0 ||
                ProductIdentityPolicy.IsReservedBarcode(row.Barcode.Trim()))
            {
                return InvalidStageFingerprint;
            }
            return "barcode:" + NormalizeBarcode(row.Barcode).ToUpperInvariant();
        }

        private static string CategoryStageFingerprint(RemoteCatalogCategoryWrite row)
        {
            return row == null ||
                string.IsNullOrWhiteSpace(row.RemoteCategoryId) ||
                string.IsNullOrWhiteSpace(row.Name)
                ? InvalidStageFingerprint
                : Fingerprint(row.Name, row.RemoteUpdatedAt);
        }

        private static string SupplierStageFingerprint(RemoteCatalogSupplierWrite row)
        {
            return row == null ||
                string.IsNullOrWhiteSpace(row.RemoteSupplierId) ||
                string.IsNullOrWhiteSpace(row.Name)
                ? InvalidStageFingerprint
                : Fingerprint(row.Name, row.RemoteUpdatedAt);
        }

        private static string PriceStageFingerprint(RemoteCatalogPriceWrite row)
        {
            return row == null ||
                string.IsNullOrWhiteSpace(row.RemotePriceId) ||
                string.IsNullOrWhiteSpace(row.RemoteProductId) ||
                string.IsNullOrWhiteSpace(row.Type) ||
                row.Price < 0
                ? InvalidStageFingerprint
                : string.Empty;
        }

        private static string ProductTombstoneStageFingerprint(RemoteCatalogProductTombstoneWrite row)
        {
            return row == null || string.IsNullOrWhiteSpace(row.RemoteProductId)
                ? InvalidStageFingerprint
                : Fingerprint(row.RemoteDeletedAt, row.RemoteUpdatedAt);
        }

        private static string CategoryTombstoneStageFingerprint(RemoteCatalogCategoryTombstoneWrite row)
        {
            return row == null || string.IsNullOrWhiteSpace(row.RemoteCategoryId)
                ? InvalidStageFingerprint
                : Fingerprint(row.RemoteDeletedAt, row.RemoteUpdatedAt);
        }

        private static string SupplierTombstoneStageFingerprint(RemoteCatalogSupplierTombstoneWrite row)
        {
            return row == null || string.IsNullOrWhiteSpace(row.RemoteSupplierId)
                ? InvalidStageFingerprint
                : Fingerprint(row.RemoteDeletedAt, row.RemoteUpdatedAt);
        }

        private static string Fingerprint(params string[] values)
        {
            using var sha = SHA256.Create();
            var builder = new StringBuilder();
            foreach (var value in values ?? Array.Empty<string>())
            {
                var normalized = (value ?? string.Empty).Trim();
                builder.Append(normalized.Length.ToString(CultureInfo.InvariantCulture));
                builder.Append(':');
                builder.Append(normalized);
                builder.Append(';');
            }

            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
            var result = new StringBuilder(bytes.Length * 2);
            foreach (var item in bytes)
                result.Append(item.ToString("x2", CultureInfo.InvariantCulture));
            return result.ToString();
        }

        private static async Task<CatalogAuthoritativeStageEvidence> LoadAuthoritativeStageEvidenceAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            CatalogAuthoritativeStagePage stage,
            RemoteCatalogCommitFence commitFence)
        {
            var identity = CatalogAuthoritativeStageIdentity.Create(stage, commitFence);
            var row = await conn.QuerySingleAsync<CatalogAuthoritativeStageEvidenceRow>(@"
WITH scoped AS (
  SELECT entity_kind, remote_id, content_fingerprint,
         category_remote_id, supplier_remote_id, product_remote_id,
         occurrence_count
  FROM catalog_authoritative_id_stage item
  JOIN catalog_authoritative_stage_scope scope ON scope.scope_id = item.scope_id
  WHERE scope.shop_id = @ShopId
    AND scope.shop_code = @ShopCode
    AND scope.transition_epoch = @TransitionEpoch
    AND scope.generation_id = @GenerationId
    AND scope.generation_fingerprint = @GenerationFingerprint
    AND scope.full_run_id = @FullRunId
)
SELECT
  COUNT(DISTINCT CASE WHEN entity_kind = 'product' THEN remote_id END) AS Products,
  COUNT(DISTINCT CASE WHEN entity_kind = 'category' THEN remote_id END) AS Categories,
  COUNT(DISTINCT CASE WHEN entity_kind = 'supplier' THEN remote_id END) AS Suppliers,
  COUNT(DISTINCT CASE WHEN entity_kind = 'price' THEN remote_id END) AS Prices,
  COUNT(DISTINCT CASE WHEN entity_kind = 'product_tombstone' THEN remote_id END) AS ProductTombstones,
  COUNT(DISTINCT CASE WHEN entity_kind = 'category_tombstone' THEN remote_id END) AS CategoryTombstones,
  COUNT(DISTINCT CASE WHEN entity_kind = 'supplier_tombstone' THEN remote_id END) AS SupplierTombstones,
  COALESCE(SUM(CASE WHEN entity_kind = 'product' AND content_fingerprint = 'invalid'
    THEN occurrence_count ELSE 0 END), 0) AS InvalidProducts,
  COALESCE(SUM(CASE WHEN entity_kind = 'category' AND content_fingerprint = 'invalid'
    THEN occurrence_count ELSE 0 END), 0) AS InvalidCategories,
  COALESCE(SUM(CASE WHEN entity_kind = 'supplier' AND content_fingerprint = 'invalid'
    THEN occurrence_count ELSE 0 END), 0) AS InvalidSuppliers,
  COALESCE(SUM(CASE WHEN entity_kind = 'price' AND content_fingerprint = 'invalid'
    THEN occurrence_count ELSE 0 END), 0) AS InvalidPrices,
  COALESCE(SUM(CASE WHEN entity_kind = 'product_tombstone' AND content_fingerprint = 'invalid'
    THEN occurrence_count ELSE 0 END), 0) AS InvalidProductTombstones,
  COALESCE(SUM(CASE WHEN entity_kind = 'category_tombstone' AND content_fingerprint = 'invalid'
    THEN occurrence_count ELSE 0 END), 0) AS InvalidCategoryTombstones,
  COALESCE(SUM(CASE WHEN entity_kind = 'supplier_tombstone' AND content_fingerprint = 'invalid'
    THEN occurrence_count ELSE 0 END), 0) AS InvalidSupplierTombstones,
  (SELECT COUNT(1) FROM (
     SELECT remote_id FROM scoped WHERE entity_kind = 'category'
     GROUP BY remote_id HAVING COUNT(DISTINCT content_fingerprint) > 1
   )) AS CategoryConflicts,
  (SELECT COUNT(1) FROM (
     SELECT remote_id FROM scoped WHERE entity_kind = 'supplier'
     GROUP BY remote_id HAVING COUNT(DISTINCT content_fingerprint) > 1
   )) AS SupplierConflicts,
  (SELECT COUNT(1) FROM (
     SELECT remote_id FROM scoped WHERE entity_kind = 'product_tombstone'
     GROUP BY remote_id HAVING COUNT(DISTINCT content_fingerprint) > 1
   )) AS ProductTombstoneConflicts,
  (SELECT COUNT(1) FROM (
     SELECT remote_id FROM scoped WHERE entity_kind = 'category_tombstone'
     GROUP BY remote_id HAVING COUNT(DISTINCT content_fingerprint) > 1
   )) AS CategoryTombstoneConflicts,
  (SELECT COUNT(1) FROM (
     SELECT remote_id FROM scoped WHERE entity_kind = 'supplier_tombstone'
     GROUP BY remote_id HAVING COUNT(DISTINCT content_fingerprint) > 1
   )) AS SupplierTombstoneConflicts,
  (SELECT COUNT(1) FROM scoped active
   JOIN scoped tombstone ON tombstone.remote_id = active.remote_id
   WHERE active.entity_kind = 'product' AND tombstone.entity_kind = 'product_tombstone')
    AS ProductActiveTombstoneConflicts,
  (SELECT COUNT(1) FROM scoped active
   JOIN scoped tombstone ON tombstone.remote_id = active.remote_id
   WHERE active.entity_kind = 'category' AND tombstone.entity_kind = 'category_tombstone')
    AS CategoryActiveTombstoneConflicts,
  (SELECT COUNT(1) FROM scoped active
   JOIN scoped tombstone ON tombstone.remote_id = active.remote_id
   WHERE active.entity_kind = 'supplier' AND tombstone.entity_kind = 'supplier_tombstone')
    AS SupplierActiveTombstoneConflicts
FROM scoped;",
                identity,
                tx).ConfigureAwait(false);

            return new CatalogAuthoritativeStageEvidence(
                new CatalogPaginationLaneCounts(
                    row.Products,
                    row.Categories,
                    row.Suppliers,
                    row.Prices,
                    row.ProductTombstones,
                    row.CategoryTombstones,
                    row.SupplierTombstones),
                FirstStageConflictCode(row));
        }

        private static string FirstStageConflictCode(CatalogAuthoritativeStageEvidenceRow row)
        {
            if (row.InvalidProducts > 0) return "catalog_product_row_invalid";
            if (row.InvalidCategories > 0) return "catalog_category_row_invalid";
            if (row.InvalidSuppliers > 0) return "catalog_supplier_row_invalid";
            if (row.InvalidPrices > 0) return "catalog_price_row_invalid";
            if (row.InvalidProductTombstones > 0) return "catalog_product_tombstone_invalid";
            if (row.InvalidCategoryTombstones > 0) return "catalog_category_tombstone_invalid";
            if (row.InvalidSupplierTombstones > 0) return "catalog_supplier_tombstone_invalid";
            if (row.CategoryConflicts > 0)
                return CatalogFullLaneEvidenceTracker.CategoryConflictCode;
            if (row.SupplierConflicts > 0)
                return CatalogFullLaneEvidenceTracker.SupplierConflictCode;
            if (row.ProductTombstoneConflicts > 0)
                return CatalogFullLaneEvidenceTracker.ProductTombstoneConflictCode;
            if (row.CategoryTombstoneConflicts > 0)
                return CatalogFullLaneEvidenceTracker.CategoryTombstoneConflictCode;
            if (row.SupplierTombstoneConflicts > 0)
                return CatalogFullLaneEvidenceTracker.SupplierTombstoneConflictCode;
            if (row.ProductActiveTombstoneConflicts > 0)
                return CatalogFullLaneEvidenceTracker.ProductActiveTombstoneConflictCode;
            if (row.CategoryActiveTombstoneConflicts > 0)
                return CatalogFullLaneEvidenceTracker.CategoryActiveTombstoneConflictCode;
            if (row.SupplierActiveTombstoneConflicts > 0)
                return CatalogFullLaneEvidenceTracker.SupplierActiveTombstoneConflictCode;
            return string.Empty;
        }

        private sealed class CatalogAuthoritativeStageIdentity
        {
            public string FullRunId { get; private set; } = string.Empty;
            public string GenerationFingerprint { get; private set; } = string.Empty;
            public string GenerationId { get; private set; } = string.Empty;
            public int PageNumber { get; private set; }
            public long ScopeId { get; set; }
            public string ShopCode { get; private set; } = string.Empty;
            public string ShopId { get; private set; } = string.Empty;
            public long TransitionEpoch { get; private set; }

            public static CatalogAuthoritativeStageIdentity Create(
                CatalogAuthoritativeStagePage stage,
                RemoteCatalogCommitFence commitFence)
            {
                return new CatalogAuthoritativeStageIdentity
                {
                    FullRunId = stage.FullRunId.Trim(),
                    GenerationFingerprint = (commitFence.GenerationFingerprint ?? string.Empty).Trim(),
                    GenerationId = (commitFence.GenerationId ?? string.Empty).Trim(),
                    PageNumber = stage.PageNumber,
                    ShopCode = commitFence.ShopCode.Trim(),
                    ShopId = commitFence.ShopId.Trim(),
                    TransitionEpoch = commitFence.ExpectedEpoch
                };
            }
        }

        private sealed class CatalogAuthoritativeStageKey : IEquatable<CatalogAuthoritativeStageKey>
        {
            public CatalogAuthoritativeStageKey(
                string entityKind,
                string remoteId,
                string contentFingerprint,
                string categoryRemoteId,
                string supplierRemoteId,
                string productRemoteId)
            {
                EntityKind = entityKind ?? string.Empty;
                RemoteId = remoteId ?? string.Empty;
                ContentFingerprint = contentFingerprint ?? string.Empty;
                CategoryRemoteId = categoryRemoteId ?? string.Empty;
                SupplierRemoteId = supplierRemoteId ?? string.Empty;
                ProductRemoteId = productRemoteId ?? string.Empty;
            }

            public string CategoryRemoteId { get; }
            public string ContentFingerprint { get; }
            public string EntityKind { get; }
            public string ProductRemoteId { get; }
            public string RemoteId { get; }
            public string SupplierRemoteId { get; }

            public bool Equals(CatalogAuthoritativeStageKey other)
            {
                return other != null &&
                    string.Equals(EntityKind, other.EntityKind, StringComparison.Ordinal) &&
                    string.Equals(RemoteId, other.RemoteId, StringComparison.Ordinal) &&
                    string.Equals(ContentFingerprint, other.ContentFingerprint, StringComparison.Ordinal) &&
                    string.Equals(CategoryRemoteId, other.CategoryRemoteId, StringComparison.Ordinal) &&
                    string.Equals(SupplierRemoteId, other.SupplierRemoteId, StringComparison.Ordinal) &&
                    string.Equals(ProductRemoteId, other.ProductRemoteId, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as CatalogAuthoritativeStageKey);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = StringComparer.Ordinal.GetHashCode(EntityKind);
                    hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(RemoteId);
                    hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(ContentFingerprint);
                    hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(CategoryRemoteId);
                    hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(SupplierRemoteId);
                    hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(ProductRemoteId);
                    return hash;
                }
            }
        }

        private sealed class CatalogAuthoritativeStageEvidenceRow
        {
            public long Categories { get; set; }
            public long CategoryActiveTombstoneConflicts { get; set; }
            public long CategoryConflicts { get; set; }
            public long CategoryTombstoneConflicts { get; set; }
            public long CategoryTombstones { get; set; }
            public long InvalidCategories { get; set; }
            public long InvalidCategoryTombstones { get; set; }
            public long InvalidPrices { get; set; }
            public long InvalidProducts { get; set; }
            public long InvalidProductTombstones { get; set; }
            public long InvalidSuppliers { get; set; }
            public long InvalidSupplierTombstones { get; set; }
            public long Prices { get; set; }
            public long ProductActiveTombstoneConflicts { get; set; }
            public long Products { get; set; }
            public long ProductTombstoneConflicts { get; set; }
            public long ProductTombstones { get; set; }
            public long SupplierActiveTombstoneConflicts { get; set; }
            public long SupplierConflicts { get; set; }
            public long Suppliers { get; set; }
            public long SupplierTombstoneConflicts { get; set; }
            public long SupplierTombstones { get; set; }
        }

        internal static async Task RequireCommitFenceAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            RemoteCatalogCommitFence commitFence)
        {
            if (commitFence == null)
            {
                var activeGeneration = await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM pos_sync_session_generation
WHERE singleton_id = 1 AND active = 1;",
                    transaction: tx).ConfigureAwait(false);
                if (activeGeneration != 0)
                    throw new InvalidOperationException("Online sync generation is required.");
                return;
            }

            if (string.IsNullOrWhiteSpace(commitFence.GenerationId))
            {
                var activeGeneration = await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM pos_sync_session_generation
WHERE singleton_id = 1 AND active = 1;",
                    transaction: tx).ConfigureAwait(false);
                if (activeGeneration != 0)
                    throw new InvalidOperationException("Online sync generation is required.");
            }
            else if (!await OnlineSyncGenerationRepository.IsCurrentAndActiveAsync(
                         conn,
                         tx,
                         commitFence.GenerationId,
                         commitFence.GenerationFingerprint).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Online sync generation mismatch.");
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

        internal sealed class RemoteProductReferencePreparedCommand : IDisposable
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

            internal void SetTransaction(SqliteTransaction tx)
            {
                _command.Transaction = tx;
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

    /// <summary>
    /// Holds the SQLite connection, prepared statements and small reference maps for one
    /// catalog sync run. Each page still owns an independent transaction and commit fence.
    /// </summary>
    public sealed class RemoteCatalogApplyRunContext : IDisposable
    {
        private readonly RemoteCatalogBatchRepository _repository;
        private readonly SemaphoreSlim _singleFlight = new SemaphoreSlim(1, 1);
        private readonly SqliteCommand _clearPageProducts;
        private readonly SqliteCommand _clearPageSetProducts;
        private readonly SqliteCommand _stagePageProduct;
        private readonly SqliteCommand _stagePageSetProduct;
        private Dictionary<string, int> _categoryIdsByRemoteId =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private Dictionary<string, int> _supplierIdsByRemoteId =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private RemoteCatalogProductWriter.CatalogProductBatchContext _productContext =
            RemoteCatalogProductWriter.CatalogProductBatchContext.FromReferences(
                Array.Empty<ProductMetaReference>(),
                Array.Empty<ProductMetaReference>());
        private bool _disposed;
        private bool _referencesLoaded;

        internal RemoteCatalogApplyRunContext(
            RemoteCatalogBatchRepository repository,
            SqliteConnectionFactory factory)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            Connection = factory.Open();
            try
            {
                using (var create = Connection.CreateCommand())
                {
                    create.CommandText = @"
CREATE TEMP TABLE IF NOT EXISTS temp_catalog_page_product_identities (
  barcode TEXT NOT NULL COLLATE NOCASE,
  remote_product_id TEXT NOT NULL,
  PRIMARY KEY(barcode, remote_product_id)
);
CREATE INDEX IF NOT EXISTS temp_catalog_page_product_remote_id_idx
  ON temp_catalog_page_product_identities(remote_product_id);
CREATE TEMP TABLE IF NOT EXISTS temp_catalog_page_products(
  ordinal INTEGER PRIMARY KEY NOT NULL,
  barcode TEXT NOT NULL COLLATE NOCASE,
  name TEXT NOT NULL,
  unit_price INTEGER NOT NULL,
  remote_product_id TEXT NOT NULL,
  article_code TEXT NOT NULL,
  second_name TEXT NOT NULL,
  purchase_price INTEGER NOT NULL,
  supplier_id INTEGER,
  supplier_name TEXT NOT NULL,
  category_id INTEGER,
  category_name TEXT NOT NULL,
  stock_quantity INTEGER NOT NULL,
  remote_category_id TEXT NOT NULL,
  remote_supplier_id TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS temp_catalog_page_products_remote_id_idx
  ON temp_catalog_page_products(remote_product_id);
CREATE TEMP TABLE IF NOT EXISTS temp_catalog_page_remote_prices(
  ordinal INTEGER PRIMARY KEY NOT NULL,
  remote_price_id TEXT NOT NULL,
  remote_product_id TEXT NOT NULL,
  type TEXT NOT NULL,
  price INTEGER NOT NULL,
  effective_at TEXT NOT NULL,
  source TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS temp_catalog_page_remote_prices_remote_id_idx
  ON temp_catalog_page_remote_prices(remote_price_id);
CREATE INDEX IF NOT EXISTS temp_catalog_page_remote_prices_product_id_idx
  ON temp_catalog_page_remote_prices(remote_product_id);";
                    create.ExecuteNonQuery();
                }

                ProductCommands = new RemoteCatalogProductWriter.CatalogProductPreparedCommands(
                    Connection,
                    null);
                ReferenceCommand = new RemoteCatalogBatchRepository.RemoteProductReferencePreparedCommand(
                    Connection,
                    null);
                _clearPageProducts = Connection.CreateCommand();
                _clearPageProducts.CommandText =
                    "DELETE FROM temp_catalog_page_product_identities;";
                _clearPageProducts.Prepare();
                _clearPageSetProducts = Connection.CreateCommand();
                _clearPageSetProducts.CommandText =
                    "DELETE FROM temp_catalog_page_products;";
                _clearPageSetProducts.Prepare();
                _stagePageProduct = Connection.CreateCommand();
                _stagePageProduct.CommandText = @"
INSERT OR IGNORE INTO temp_catalog_page_product_identities(barcode, remote_product_id)
VALUES(@barcode, @remoteProductId);";
                _stagePageProduct.Parameters.Add(new SqliteParameter("@barcode", string.Empty));
                _stagePageProduct.Parameters.Add(new SqliteParameter("@remoteProductId", string.Empty));
                _stagePageProduct.Prepare();
                _stagePageSetProduct = Connection.CreateCommand();
                _stagePageSetProduct.CommandText = @"
INSERT INTO temp_catalog_page_products(
  ordinal,
  barcode,
  name,
  unit_price,
  remote_product_id,
  article_code,
  second_name,
  purchase_price,
  supplier_id,
  supplier_name,
  category_id,
  category_name,
  stock_quantity,
  remote_category_id,
  remote_supplier_id)
VALUES(
  @ordinal,
  @barcode,
  @name,
  @unitPrice,
  @remoteProductId,
  @articleCode,
  @secondName,
  @purchasePrice,
  @supplierId,
  @supplierName,
  @categoryId,
  @categoryName,
  @stockQuantity,
  @remoteCategoryId,
  @remoteSupplierId);";
                _stagePageSetProduct.Parameters.Add(
                    new SqliteParameter("@ordinal", SqliteType.Integer));
                _stagePageSetProduct.Parameters.Add(
                    new SqliteParameter("@barcode", SqliteType.Text));
                _stagePageSetProduct.Parameters.Add(
                    new SqliteParameter("@name", SqliteType.Text));
                _stagePageSetProduct.Parameters.Add(
                    new SqliteParameter("@unitPrice", SqliteType.Integer));
                _stagePageSetProduct.Parameters.Add(
                    new SqliteParameter("@remoteProductId", SqliteType.Text));
                _stagePageSetProduct.Parameters.Add(
                    new SqliteParameter("@articleCode", SqliteType.Text));
                _stagePageSetProduct.Parameters.Add(
                    new SqliteParameter("@secondName", SqliteType.Text));
                _stagePageSetProduct.Parameters.Add(
                    new SqliteParameter("@purchasePrice", SqliteType.Integer));
                _stagePageSetProduct.Parameters.Add(
                    new SqliteParameter("@supplierId", SqliteType.Integer));
                _stagePageSetProduct.Parameters.Add(
                    new SqliteParameter("@supplierName", SqliteType.Text));
                _stagePageSetProduct.Parameters.Add(
                    new SqliteParameter("@categoryId", SqliteType.Integer));
                _stagePageSetProduct.Parameters.Add(
                    new SqliteParameter("@categoryName", SqliteType.Text));
                _stagePageSetProduct.Parameters.Add(
                    new SqliteParameter("@stockQuantity", SqliteType.Integer));
                _stagePageSetProduct.Parameters.Add(
                    new SqliteParameter("@remoteCategoryId", SqliteType.Text));
                _stagePageSetProduct.Parameters.Add(
                    new SqliteParameter("@remoteSupplierId", SqliteType.Text));
                _stagePageSetProduct.Prepare();
                Diagnostics.PreparedCommandCount = 10;
                Diagnostics.ContextSqlCommandCount = 1;
            }
            catch
            {
                Connection.Dispose();
                throw;
            }
        }

        public CatalogApplyRunDiagnostics Diagnostics { get; } =
            new CatalogApplyRunDiagnostics();

        internal SqliteConnection Connection { get; }
        internal RemoteCatalogProductWriter.CatalogProductPreparedCommands ProductCommands { get; }
        internal RemoteCatalogBatchRepository.RemoteProductReferencePreparedCommand ReferenceCommand { get; }

        public async Task<RemoteCatalogBatchApplyResult> ApplyAsync(
            RemoteCatalogBatch batch,
            CancellationToken cancellationToken = default,
            RemoteCatalogCommitFence commitFence = null)
        {
            EnsureNotDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureNotDisposed();
                return await _repository.ApplyWithinRunAsync(
                    this,
                    batch,
                    cancellationToken,
                    commitFence).ConfigureAwait(false);
            }
            finally
            {
                _singleFlight.Release();
            }
        }

        internal void BindTransaction(SqliteTransaction transaction)
        {
            ProductCommands.SetTransaction(transaction);
            ReferenceCommand.SetTransaction(transaction);
            _clearPageProducts.Transaction = transaction;
            _clearPageSetProducts.Transaction = transaction;
            _stagePageProduct.Transaction = transaction;
            _stagePageSetProduct.Transaction = transaction;
        }

        internal async Task<RemoteCatalogPageState> CreatePageStateAsync(
            RemoteCatalogBatch batch,
            SqliteTransaction transaction)
        {
            var refresh = !_referencesLoaded ||
                (batch.Categories?.Count ?? 0) > 0 ||
                (batch.Suppliers?.Count ?? 0) > 0 ||
                (batch.CategoryTombstones?.Count ?? 0) > 0 ||
                (batch.SupplierTombstones?.Count ?? 0) > 0;
            if (!refresh)
            {
                return new RemoteCatalogPageState(
                    new Dictionary<string, int>(_categoryIdsByRemoteId, StringComparer.Ordinal),
                    new Dictionary<string, int>(_supplierIdsByRemoteId, StringComparer.Ordinal),
                    _productContext.WithoutPendingStock());
            }

            var categories = (await Connection.QueryAsync<RemoteCatalogReferenceRow>(@"
SELECT id AS Id,
       name AS Name,
       COALESCE(remote_category_id, '') AS RemoteId
FROM categories
WHERE COALESCE(is_active, 1) = 1
ORDER BY id ASC;", transaction: transaction).ConfigureAwait(false)).ToArray();
            var suppliers = (await Connection.QueryAsync<RemoteCatalogReferenceRow>(@"
SELECT id AS Id,
       name AS Name,
       COALESCE(remote_supplier_id, '') AS RemoteId
FROM suppliers
WHERE COALESCE(is_active, 1) = 1
ORDER BY id ASC;", transaction: transaction).ConfigureAwait(false)).ToArray();
            Diagnostics.ReferenceMapRefreshQueryCount += 2;
            Diagnostics.ScopeSqlQueryCount += 2;
            Diagnostics.ContextSqlCommandCount += 2;

            return new RemoteCatalogPageState(
                ToRemoteIdMap(categories),
                ToRemoteIdMap(suppliers),
                RemoteCatalogProductWriter.CatalogProductBatchContext.FromReferences(
                    categories.Select(row => new ProductMetaReference
                    {
                        Id = row.Id,
                        Name = row.Name
                    }),
                    suppliers.Select(row => new ProductMetaReference
                    {
                        Id = row.Id,
                        Name = row.Name
                    })));
        }

        internal async Task StagePageProductsAsync(
            RemoteCatalogBatch batch,
            SqliteTransaction transaction)
        {
            await _clearPageProducts.ExecuteNonQueryAsync().ConfigureAwait(false);
            Diagnostics.ContextSqlCommandCount += 1;

            foreach (var product in batch.Products ?? Array.Empty<RemoteCatalogProductWrite>())
            {
                if (product == null)
                {
                    continue;
                }

                await StageIdentityAsync(product.Barcode, product.RemoteProductId).ConfigureAwait(false);
            }

            foreach (var tombstone in batch.ProductTombstones ?? Array.Empty<RemoteCatalogProductTombstoneWrite>())
            {
                if (tombstone == null)
                {
                    continue;
                }

                await StageIdentityAsync(string.Empty, tombstone.RemoteProductId).ConfigureAwait(false);
            }
        }

        internal async Task<IReadOnlyList<RemoteCatalogProductIdentityRow>> LoadPageProductIdentitiesAsync(
            SqliteTransaction transaction)
        {
            var rows = (await Connection.QueryAsync<RemoteCatalogProductIdentityRow>(@"
SELECT p.barcode AS Barcode, TRIM(p.remote_product_id) AS RemoteProductId
FROM products p
JOIN temp_catalog_page_product_identities incoming
  ON incoming.barcode <> '' AND incoming.barcode = p.barcode
WHERE COALESCE(p.is_active, 1) = 1
  AND TRIM(COALESCE(p.remote_product_id, '')) <> ''
UNION
SELECT p.barcode AS Barcode, TRIM(p.remote_product_id) AS RemoteProductId
FROM products p
JOIN temp_catalog_page_product_identities incoming
  ON incoming.remote_product_id <> ''
 AND incoming.remote_product_id = p.remote_product_id
WHERE COALESCE(p.is_active, 1) = 1
  AND TRIM(COALESCE(p.remote_product_id, '')) <> '';",
                transaction: transaction).ConfigureAwait(false)).ToArray();
            Diagnostics.ProductIdentityQueryCount += 1;
            Diagnostics.ProductIdentityRowsLoaded += rows.Length;
            Diagnostics.ScopeSqlQueryCount += 1;
            Diagnostics.ContextSqlCommandCount += 1;
            return rows;
        }

        internal async Task<IReadOnlyList<RemoteCatalogPendingStockIdentityRow>> LoadPagePendingStockAsync(
            SqliteTransaction transaction)
        {
            var rows = (await Connection.QueryAsync<RemoteCatalogPendingStockIdentityRow>(@"
SELECT DISTINCT
       m.barcode AS Barcode,
       COALESCE(p.remote_product_id, '') AS RemoteProductId
FROM sales_sync_outbox o
JOIN local_stock_movements m ON m.sale_id = o.sale_id
JOIN temp_catalog_page_product_identities incoming
  ON incoming.barcode <> '' AND incoming.barcode = m.barcode
LEFT JOIN products p ON p.barcode = m.barcode
WHERE o.status IN ('pending', 'retry', 'in_progress', 'failed_blocked')
UNION
SELECT DISTINCT
       m.barcode AS Barcode,
       COALESCE(p.remote_product_id, '') AS RemoteProductId
FROM sales_sync_outbox o
JOIN local_stock_movements m ON m.sale_id = o.sale_id
JOIN products p ON p.barcode = m.barcode
JOIN temp_catalog_page_product_identities incoming
  ON incoming.remote_product_id <> ''
 AND incoming.remote_product_id = p.remote_product_id
WHERE o.status IN ('pending', 'retry', 'in_progress', 'failed_blocked');",
                transaction: transaction).ConfigureAwait(false)).ToArray();
            Diagnostics.PendingStockQueryCount += 1;
            Diagnostics.PendingStockRowsLoaded += rows.Length;
            Diagnostics.ScopeSqlQueryCount += 1;
            Diagnostics.ContextSqlCommandCount += 1;
            return rows;
        }

        internal async Task<bool> PageRequiresLegacyProductRebindAsync(
            SqliteTransaction transaction)
        {
            var required = await Connection.ExecuteScalarAsync<long>(@"
SELECT EXISTS (
  SELECT 1
  FROM temp_catalog_page_product_identities incoming
  JOIN products existing_remote
    ON incoming.remote_product_id <> ''
   AND existing_remote.remote_product_id = incoming.remote_product_id
   AND existing_remote.barcode <> incoming.barcode
  WHERE incoming.barcode <> ''
    AND NOT EXISTS (
      SELECT 1
      FROM products target
      WHERE target.barcode = incoming.barcode
    )
);",
                transaction: transaction).ConfigureAwait(false);
            Diagnostics.ScopeSqlQueryCount += 1;
            Diagnostics.ContextSqlCommandCount += 1;
            return required != 0;
        }

        internal async Task StageCleanProductsAsync(
            IReadOnlyList<RemoteCatalogProductWriter.RemoteCatalogSetProductWrite> products)
        {
            await _clearPageSetProducts.ExecuteNonQueryAsync().ConfigureAwait(false);
            Diagnostics.ContextSqlCommandCount += 1;
            var rows = products ??
                Array.Empty<RemoteCatalogProductWriter.RemoteCatalogSetProductWrite>();
            for (var index = 0; index < rows.Count; index += 1)
            {
                var row = rows[index];
                _stagePageSetProduct.Parameters["@ordinal"].Value = index;
                _stagePageSetProduct.Parameters["@barcode"].Value = row.Barcode;
                _stagePageSetProduct.Parameters["@name"].Value = row.Name;
                _stagePageSetProduct.Parameters["@unitPrice"].Value = row.UnitPrice;
                _stagePageSetProduct.Parameters["@remoteProductId"].Value = row.RemoteProductId;
                _stagePageSetProduct.Parameters["@articleCode"].Value = row.ArticleCode;
                _stagePageSetProduct.Parameters["@secondName"].Value = row.SecondName;
                _stagePageSetProduct.Parameters["@purchasePrice"].Value = row.PurchasePrice;
                _stagePageSetProduct.Parameters["@supplierId"].Value =
                    (object)row.SupplierId ?? DBNull.Value;
                _stagePageSetProduct.Parameters["@supplierName"].Value = row.SupplierName;
                _stagePageSetProduct.Parameters["@categoryId"].Value =
                    (object)row.CategoryId ?? DBNull.Value;
                _stagePageSetProduct.Parameters["@categoryName"].Value = row.CategoryName;
                _stagePageSetProduct.Parameters["@stockQuantity"].Value = row.StockQuantity;
                _stagePageSetProduct.Parameters["@remoteCategoryId"].Value = row.RemoteCategoryId;
                _stagePageSetProduct.Parameters["@remoteSupplierId"].Value = row.RemoteSupplierId;
                await _stagePageSetProduct.ExecuteNonQueryAsync().ConfigureAwait(false);
                Diagnostics.ContextSqlCommandCount += 1;
            }
        }

        internal void CommitPageState(RemoteCatalogPageState pageState)
        {
            _categoryIdsByRemoteId = pageState.CategoryIdsByRemoteId;
            _supplierIdsByRemoteId = pageState.SupplierIdsByRemoteId;
            _productContext = pageState.ProductContext;
            _referencesLoaded = true;
        }

        internal void RecordPageApplied()
        {
            Diagnostics.PagesApplied += 1;
        }

        internal void RecordRemotePriceApply(RemotePriceApplyDiagnostics pageDiagnostics)
        {
            Diagnostics.RemotePriceApply.MergeFrom(pageDiagnostics);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            BindTransaction(null);
            _stagePageSetProduct.Dispose();
            _stagePageProduct.Dispose();
            _clearPageSetProducts.Dispose();
            _clearPageProducts.Dispose();
            ReferenceCommand.Dispose();
            ProductCommands.Dispose();
            Connection.Dispose();
            _singleFlight.Dispose();
        }

        private async Task StageIdentityAsync(string barcode, string remoteProductId)
        {
            var normalizedBarcode = (barcode ?? string.Empty).Trim();
            var normalizedRemoteProductId = (remoteProductId ?? string.Empty).Trim();
            if (normalizedBarcode.Length == 0 && normalizedRemoteProductId.Length == 0)
            {
                return;
            }

            _stagePageProduct.Parameters["@barcode"].Value = normalizedBarcode;
            _stagePageProduct.Parameters["@remoteProductId"].Value = normalizedRemoteProductId;
            await _stagePageProduct.ExecuteNonQueryAsync().ConfigureAwait(false);
            Diagnostics.StagedProductIdentityCount += 1;
            Diagnostics.ContextSqlCommandCount += 1;
        }

        private static Dictionary<string, int> ToRemoteIdMap(
            IEnumerable<RemoteCatalogReferenceRow> rows)
        {
            return (rows ?? Array.Empty<RemoteCatalogReferenceRow>())
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.RemoteId))
                .GroupBy(row => row.RemoteId.Trim(), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.Ordinal);
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RemoteCatalogApplyRunContext));
            }
        }
    }

    public sealed class CatalogApplyRunDiagnostics
    {
        public long ContextSqlCommandCount { get; internal set; }
        public int LegacyScopeSqlQueryEstimate => PagesApplied * 6;
        public int PagesApplied { get; internal set; }
        public int PendingStockQueryCount { get; internal set; }
        public long PendingStockRowsLoaded { get; internal set; }
        public int PreparedCommandCount { get; internal set; }
        public int ProductIdentityQueryCount { get; internal set; }
        public long ProductIdentityRowsLoaded { get; internal set; }
        public int ReferenceMapRefreshQueryCount { get; internal set; }
        public RemotePriceApplyDiagnostics RemotePriceApply { get; } =
            new RemotePriceApplyDiagnostics();
        public int ScopeSqlQueryCount { get; internal set; }
        public long StagedProductIdentityCount { get; internal set; }
    }

    public sealed class RemotePriceApplyDiagnostics
    {
        public long FallbackPageCount { get; internal set; }
        public long PreparedCommandCount { get; internal set; }
        public long SetBasedPageCount { get; internal set; }
        public long SqlCommandCount { get; internal set; }
        public long SqlStatementCount { get; internal set; }
        public long StagedRowCount { get; internal set; }

        internal void RecordFallbackPage()
        {
            FallbackPageCount += 1;
        }

        internal void RecordPreparedCommand()
        {
            PreparedCommandCount += 1;
        }

        internal void RecordSetBasedPage(int stagedRows)
        {
            if (stagedRows < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(stagedRows));
            }

            SetBasedPageCount += 1;
            StagedRowCount += stagedRows;
        }

        internal void RecordSqlCommand(int statementCount)
        {
            if (statementCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(statementCount));
            }

            SqlCommandCount += 1;
            SqlStatementCount += statementCount;
        }

        internal void MergeFrom(RemotePriceApplyDiagnostics pageDiagnostics)
        {
            if (pageDiagnostics == null)
            {
                throw new ArgumentNullException(nameof(pageDiagnostics));
            }

            FallbackPageCount += pageDiagnostics.FallbackPageCount;
            PreparedCommandCount += pageDiagnostics.PreparedCommandCount;
            SetBasedPageCount += pageDiagnostics.SetBasedPageCount;
            SqlCommandCount += pageDiagnostics.SqlCommandCount;
            SqlStatementCount += pageDiagnostics.SqlStatementCount;
            StagedRowCount += pageDiagnostics.StagedRowCount;
        }
    }

    internal sealed class RemoteCatalogPageState
    {
        internal RemoteCatalogPageState(
            Dictionary<string, int> categoryIdsByRemoteId,
            Dictionary<string, int> supplierIdsByRemoteId,
            RemoteCatalogProductWriter.CatalogProductBatchContext productContext)
        {
            CategoryIdsByRemoteId = categoryIdsByRemoteId;
            SupplierIdsByRemoteId = supplierIdsByRemoteId;
            ProductContext = productContext;
        }

        internal Dictionary<string, int> CategoryIdsByRemoteId { get; }
        internal Dictionary<string, int> SupplierIdsByRemoteId { get; }
        internal RemoteCatalogProductWriter.CatalogProductBatchContext ProductContext { get; set; }

        internal void RemoveTombstonedReferences(
            IEnumerable<string> categoryRemoteIds,
            IEnumerable<string> supplierRemoteIds)
        {
            foreach (var categoryRemoteId in categoryRemoteIds ?? Array.Empty<string>())
            {
                var remoteId = (categoryRemoteId ?? string.Empty).Trim();
                if (remoteId.Length > 0 && CategoryIdsByRemoteId.TryGetValue(remoteId, out var id))
                {
                    CategoryIdsByRemoteId.Remove(remoteId);
                    ProductContext.RemoveCategory(id);
                }
            }

            foreach (var supplierRemoteId in supplierRemoteIds ?? Array.Empty<string>())
            {
                var remoteId = (supplierRemoteId ?? string.Empty).Trim();
                if (remoteId.Length > 0 && SupplierIdsByRemoteId.TryGetValue(remoteId, out var id))
                {
                    SupplierIdsByRemoteId.Remove(remoteId);
                    ProductContext.RemoveSupplier(id);
                }
            }
        }
    }

    public sealed class RemoteCatalogCommitFence
    {
        public long ExpectedEpoch { get; set; }
        public string ExpectedPreviousCursor { get; set; } = string.Empty;
        public string ExpectedPreviousMode { get; set; } = string.Empty;
        public string GenerationFingerprint { get; set; } = string.Empty;
        public string GenerationId { get; set; } = string.Empty;
        public string PosSessionId { get; set; } = string.Empty;
        public string ShopCode { get; set; } = string.Empty;
        public string ShopDeviceId { get; set; } = string.Empty;
        public string ShopId { get; set; } = string.Empty;
    }

    public sealed class CatalogAuthoritativeStagePage
    {
        public string FullRunId { get; set; } = string.Empty;
        public bool HasMore { get; set; }
        public int PageNumber { get; set; }
    }

    public sealed class CatalogAuthoritativeStageEvidence
    {
        public CatalogAuthoritativeStageEvidence(
            CatalogPaginationLaneCounts laneCounts,
            string conflictCode)
        {
            LaneCounts = laneCounts ?? throw new ArgumentNullException(nameof(laneCounts));
            ConflictCode = conflictCode ?? string.Empty;
        }

        public string ConflictCode { get; }
        public CatalogPaginationLaneCounts LaneCounts { get; }
    }

    public sealed class RemoteCatalogBatch
    {
        public bool AuthoritativeFullRefresh { get; set; }
        public CatalogAuthoritativeStagePage AuthoritativeStagePage { get; set; }
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
        public string Name { get; set; } = string.Empty;
        public string RemoteId { get; set; } = string.Empty;
    }

    internal sealed class RemoteCatalogProductIdentityRow
    {
        public string Barcode { get; set; } = string.Empty;
        public string RemoteProductId { get; set; } = string.Empty;
    }

    internal sealed class RemoteCatalogPendingStockIdentityRow
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
        public string RemoteUpdatedAt { get; set; } = string.Empty;
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
