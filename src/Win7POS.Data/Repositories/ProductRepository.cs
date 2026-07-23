using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Win7POS.Core.Models;
using Win7POS.Core.Products;

namespace Win7POS.Data.Repositories
{
    /// <summary>
    /// Public compatibility façade for product reads and mutations. Local writes,
    /// remote product identity writes and remote price history are owned by focused
    /// collaborators.
    /// </summary>
    public sealed class ProductRepository
    {
        private readonly ProductQueryRepository _queries;
        private readonly LocalProductWriter _localProductWriter;
        private readonly RemoteCatalogProductWriter _remoteProductWriter;
        private readonly RemotePriceHistoryRepository _remotePriceHistory;

        public ProductRepository(SqliteConnectionFactory factory)
        {
            _queries = new ProductQueryRepository(factory);
            _localProductWriter = new LocalProductWriter(factory);
            _remoteProductWriter = new RemoteCatalogProductWriter(factory);
            _remotePriceHistory = new RemotePriceHistoryRepository(factory);
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

        public Task<bool> ApplyRemoteProductTombstoneAsync(string remoteProductId, string remoteDeletedAt) =>
            _remoteProductWriter.ApplyRemoteProductTombstoneAsync(remoteProductId, remoteDeletedAt);

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
        public Task<long> UpsertProductAndMetaInTransactionAsync(
            Product p,
            string articleCode,
            string name2,
            int purchasePrice,
            int? supplierId,
            string supplierName,
            int? categoryId,
            string categoryName,
            int stockQty,
            string remoteProductId = null)
        {
            if (string.IsNullOrWhiteSpace(remoteProductId))
            {
                return _localProductWriter.UpsertProductAndMetaInTransactionAsync(
                    p,
                    articleCode,
                    name2,
                    purchasePrice,
                    supplierId,
                    supplierName,
                    categoryId,
                    categoryName,
                    stockQty);
            }

            return _remoteProductWriter.UpsertProductAndMetaInTransactionAsync(
                p,
                articleCode,
                name2,
                purchasePrice,
                supplierId,
                supplierName,
                categoryId,
                categoryName,
                stockQty,
                remoteProductId);
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
