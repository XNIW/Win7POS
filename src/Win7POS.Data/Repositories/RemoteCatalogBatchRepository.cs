using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Models;

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
            CancellationToken cancellationToken = default)
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
                    var result = new RemoteCatalogBatchApplyResult();

                    foreach (var category in batch.Categories ?? Array.Empty<RemoteCatalogCategoryWrite>())
                    {
                        if (category != null && await CategoryRepository.UpsertRemoteInTransactionAsync(
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
                        if (supplier != null && await SupplierRepository.UpsertRemoteInTransactionAsync(
                            conn,
                            tx,
                            supplier.RemoteSupplierId,
                            supplier.Name,
                            supplier.RemoteUpdatedAt).ConfigureAwait(false))
                        {
                            result.SuppliersApplied += 1;
                        }
                    }

                    var productBatchContext = await ProductRepository.CatalogProductBatchContext
                        .LoadAsync(conn, tx)
                        .ConfigureAwait(false);
                    using var preparedProductCommands =
                        new ProductRepository.CatalogProductPreparedCommands(conn, tx);
                    foreach (var product in batch.Products ?? Array.Empty<RemoteCatalogProductWrite>())
                    {
                        if (product == null || string.IsNullOrWhiteSpace(product.Barcode))
                        {
                            continue;
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
                            product.SupplierId,
                            product.SupplierName,
                            product.CategoryId,
                            product.CategoryName,
                            product.StockQuantity,
                            product.RemoteProductId,
                            preparedProductCommands,
                            productBatchContext).ConfigureAwait(false);
                        result.ProductsApplied += 1;
                    }

                    result.PendingPricesApplied += await ProductRepository
                        .ApplyPendingRemotePricesInTransactionAsync(conn, tx)
                        .ConfigureAwait(false);

                    foreach (var tombstone in batch.ProductTombstones ?? Array.Empty<RemoteCatalogProductTombstoneWrite>())
                    {
                        if (tombstone != null && await ProductRepository.ApplyRemoteProductTombstoneInTransactionAsync(
                            conn,
                            tx,
                            tombstone.RemoteProductId,
                            tombstone.RemoteDeletedAt).ConfigureAwait(false))
                        {
                            result.ProductTombstonesApplied += 1;
                        }
                    }

                    foreach (var tombstone in batch.CategoryTombstones ?? Array.Empty<RemoteCatalogCategoryTombstoneWrite>())
                    {
                        if (tombstone != null && await CategoryRepository.ApplyRemoteTombstoneInTransactionAsync(
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
                        if (tombstone != null && await SupplierRepository.ApplyRemoteTombstoneInTransactionAsync(
                            conn,
                            tx,
                            tombstone.RemoteSupplierId,
                            tombstone.RemoteDeletedAt,
                            tombstone.RemoteUpdatedAt).ConfigureAwait(false))
                        {
                            result.SupplierTombstonesApplied += 1;
                        }
                    }

                    foreach (var price in batch.Prices ?? Array.Empty<RemoteCatalogPriceWrite>())
                    {
                        if (price == null)
                        {
                            continue;
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
                    }

                    result.PendingPricesApplied += await ProductRepository
                        .ApplyPendingRemotePricesInTransactionAsync(conn, tx)
                        .ConfigureAwait(false);

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
    }

    public sealed class RemoteCatalogBatch
    {
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
        public string RemoteProductId { get; set; } = string.Empty;
        public string SecondName { get; set; } = string.Empty;
        public int StockQuantity { get; set; }
        public int? SupplierId { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public long UnitPrice { get; set; }
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
        public int CategoryTombstonesApplied { get; internal set; }
        public int PendingPricesApplied { get; internal set; }
        public int PricesApplied { get; internal set; }
        public int PricesQueued { get; internal set; }
        public int ProductsApplied { get; internal set; }
        public int ProductTombstonesApplied { get; internal set; }
        public int SuppliersApplied { get; internal set; }
        public int SupplierTombstonesApplied { get; internal set; }
        public int TombstonesApplied =>
            ProductTombstonesApplied + CategoryTombstonesApplied + SupplierTombstonesApplied;
    }
}
