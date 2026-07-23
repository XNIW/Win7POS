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
        internal static readonly System.Threading.SemaphoreSlim CatalogMetaWriteGate =
            new System.Threading.SemaphoreSlim(1, 1);
        private const int PendingRemotePriceReplayBatchSize = 2000;
        private readonly SqliteConnectionFactory _factory;
        private readonly ProductQueryRepository _queries;

        public ProductRepository(SqliteConnectionFactory factory)
        {
            _factory = factory;
            _queries = new ProductQueryRepository(factory);
        }

        internal sealed class ProductMetaReference
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

        private sealed class RemotePriceHistoryRow
        {
            public string Barcode { get; set; } = string.Empty;
            public string EffectiveAt { get; set; } = string.Empty;
            public int Price { get; set; }
            public string Source { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
        }

        private enum RemotePriceIdEvidenceState
        {
            Collision,
            Applied,
            Queued
        }

        private sealed class RemotePriceIdEvidence
        {
            public string EffectiveAt { get; set; } = string.Empty;
            public long? PendingId { get; set; }
            public RemotePriceIdEvidenceState State { get; set; }
        }

        internal sealed class PendingRemotePriceReplayResult
        {
            public int Applied { get; set; }
            public HashSet<long> CollisionIds { get; } = new HashSet<long>();
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
            if (string.IsNullOrEmpty(barcode)) return false;
            return barcode.StartsWith("DISC:", StringComparison.Ordinal)
                || barcode.StartsWith("MANUAL:", StringComparison.Ordinal);
        }

        public async Task<long> UpsertAsync(Product p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            SalesReceiptContentPolicy.EnsureValidProductIdentity(p.Barcode, p.Name);
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

            var normalizedSource = string.IsNullOrWhiteSpace(source) ? "remote_catalog" : source.Trim();
            var normalizedRemotePriceId = (remotePriceId ?? string.Empty).Trim();
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                var result = await UpsertOrQueueRemotePriceHistoryInTransactionAsync(
                    conn,
                    tx,
                    normalizedRemoteProductId,
                    normalizedRemotePriceId,
                    normalizedType,
                    price,
                    timestamp,
                    normalizedSource).ConfigureAwait(false);
                tx.Commit();
                return result;
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }

        internal static async Task<RemotePriceHistoryApplyResult> UpsertOrQueueRemotePriceHistoryInTransactionAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
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
            if (normalizedRemotePriceId.Length > 0)
            {
                var storedOwner = await LoadRemotePriceOwnerAsync(
                    conn,
                    tx,
                    normalizedRemotePriceId).ConfigureAwait(false);
                if (storedOwner.Length > 0 &&
                    !string.Equals(storedOwner, normalizedRemoteProductId, StringComparison.Ordinal))
                {
                    return RemotePriceHistoryApplyResult.Skipped();
                }
            }

            var barcode = await conn.QuerySingleOrDefaultAsync<string>(
                @"SELECT barcode
	FROM products
	WHERE remote_product_id = @remoteProductId
	  AND COALESCE(is_active, 1) = 1
	ORDER BY id ASC
	LIMIT 1",
	                new { remoteProductId = normalizedRemoteProductId },
                    tx).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(barcode))
            {
                var queuedRows = await QueuePendingRemotePriceAsync(
                    conn,
                    normalizedRemoteProductId,
                    normalizedRemotePriceId,
                    normalizedType,
                    price,
                    normalizedTimestamp,
                    normalizedSource,
                    tx).ConfigureAwait(false);
                if (normalizedRemotePriceId.Length == 0)
                {
                    return RemotePriceHistoryApplyResult.QueuedOk();
                }

                if (queuedRows > 0)
                {
                    if (!await StoreRemotePriceOwnershipAsync(
                            conn,
                            tx,
                            normalizedRemotePriceId,
                            normalizedRemoteProductId).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException("catalog_remote_price_owner_write_conflict");
                    }

                    return RemotePriceHistoryApplyResult.QueuedOk();
                }

                var evidence = await EvaluateRemotePriceIdEvidenceAsync(
                    conn,
                    tx,
                    normalizedRemoteProductId,
                    normalizedRemotePriceId,
                    normalizedType,
                    price,
                    timestamp,
                    normalizedTimestamp,
                    normalizedSource).ConfigureAwait(false);
                if (evidence.State == RemotePriceIdEvidenceState.Applied)
                {
                    await DeleteRemotePriceIdEvidencePendingAsync(
                        conn,
                        tx,
                        evidence,
                        normalizedRemoteProductId,
                        normalizedRemotePriceId,
                        normalizedType,
                        price,
                        normalizedSource).ConfigureAwait(false);
                    return RemotePriceHistoryApplyResult.AppliedOk();
                }

                return evidence.State == RemotePriceIdEvidenceState.Queued
                    ? RemotePriceHistoryApplyResult.QueuedOk()
                    : RemotePriceHistoryApplyResult.Skipped();
            }

            var insertedRows = await InsertRemotePriceHistoryAsync(
                conn,
                tx,
                barcode.Trim(),
                normalizedRemotePriceId,
                normalizedType,
                price,
                normalizedTimestamp,
                normalizedSource,
                normalizedRemoteProductId).ConfigureAwait(false);

            if (normalizedRemotePriceId.Length == 0)
            {
                await DeletePendingRemotePriceAsync(
                    conn,
                    tx,
                    null,
                    normalizedRemoteProductId,
                    normalizedRemotePriceId,
                    normalizedType,
                    price,
                    normalizedTimestamp,
                    normalizedSource).ConfigureAwait(false);
                return RemotePriceHistoryApplyResult.AppliedOk();
            }

            if (insertedRows > 0)
            {
                if (!await StoreRemotePriceOwnershipAsync(
                        conn,
                        tx,
                        normalizedRemotePriceId,
                        normalizedRemoteProductId).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("catalog_remote_price_owner_write_conflict");
                }

                await DeletePendingRemotePriceAsync(
                    conn,
                    tx,
                    null,
                    normalizedRemoteProductId,
                    normalizedRemotePriceId,
                    normalizedType,
                    price,
                    normalizedTimestamp,
                    normalizedSource).ConfigureAwait(false);
                return RemotePriceHistoryApplyResult.AppliedOk();
            }

            var conflictEvidence = await EvaluateRemotePriceIdEvidenceAsync(
                conn,
                tx,
                normalizedRemoteProductId,
                normalizedRemotePriceId,
                normalizedType,
                price,
                timestamp,
                normalizedTimestamp,
                normalizedSource).ConfigureAwait(false);
            if (conflictEvidence.State == RemotePriceIdEvidenceState.Collision)
            {
                return RemotePriceHistoryApplyResult.Skipped();
            }

            if (conflictEvidence.State == RemotePriceIdEvidenceState.Queued)
            {
                var retryInsertedRows = await InsertRemotePriceHistoryAsync(
                    conn,
                    tx,
                    barcode.Trim(),
                    normalizedRemotePriceId,
                    normalizedType,
                    price,
                    conflictEvidence.EffectiveAt,
                    normalizedSource,
                    normalizedRemoteProductId).ConfigureAwait(false);
                if (retryInsertedRows == 0)
                {
                    conflictEvidence = await EvaluateRemotePriceIdEvidenceAsync(
                        conn,
                        tx,
                        normalizedRemoteProductId,
                        normalizedRemotePriceId,
                        normalizedType,
                        price,
                        conflictEvidence.EffectiveAt,
                        conflictEvidence.EffectiveAt,
                        normalizedSource).ConfigureAwait(false);
                    if (conflictEvidence.State != RemotePriceIdEvidenceState.Applied)
                    {
                        return RemotePriceHistoryApplyResult.Skipped();
                    }
                }
            }

            await DeleteRemotePriceIdEvidencePendingAsync(
                conn,
                tx,
                conflictEvidence,
                normalizedRemoteProductId,
                normalizedRemotePriceId,
                normalizedType,
                price,
                normalizedSource).ConfigureAwait(false);

            return RemotePriceHistoryApplyResult.AppliedOk();
        }

        private static async Task<RemotePriceIdEvidence> EvaluateRemotePriceIdEvidenceAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remoteProductId,
            string remotePriceId,
            string type,
            int price,
            string requestedEffectiveAt,
            string fallbackEffectiveAt,
            string source)
        {
            var existingHistory = await LoadRemotePriceHistoryAsync(
                conn,
                tx,
                remotePriceId).ConfigureAwait(false);
            var existingPending = await LoadPendingRemotePriceAsync(
                conn,
                tx,
                remotePriceId).ConfigureAwait(false);
            if (!await EnsureRemotePriceOwnershipForEvidenceAsync(
                    conn,
                    tx,
                    remotePriceId,
                    remoteProductId,
                    existingPending).ConfigureAwait(false))
            {
                return new RemotePriceIdEvidence { State = RemotePriceIdEvidenceState.Collision };
            }

            var effectiveAt = string.IsNullOrWhiteSpace(requestedEffectiveAt)
                ? existingHistory?.EffectiveAt ?? existingPending?.EffectiveAt ?? fallbackEffectiveAt
                : fallbackEffectiveAt;

            if (existingHistory != null)
            {
                var historyMatches = RemotePriceHistoryMatches(
                    existingHistory,
                    type,
                    price,
                    effectiveAt,
                    source);
                if (!historyMatches ||
                    (existingPending != null && !PendingRemotePriceMatches(
                        existingPending,
                        remoteProductId,
                        type,
                        price,
                        effectiveAt,
                        source)))
                {
                    return new RemotePriceIdEvidence { State = RemotePriceIdEvidenceState.Collision };
                }

                return new RemotePriceIdEvidence
                {
                    EffectiveAt = effectiveAt,
                    PendingId = existingPending?.Id,
                    State = RemotePriceIdEvidenceState.Applied
                };
            }

            if (existingPending != null && PendingRemotePriceMatches(
                    existingPending,
                    remoteProductId,
                    type,
                    price,
                    effectiveAt,
                    source))
            {
                return new RemotePriceIdEvidence
                {
                    EffectiveAt = effectiveAt,
                    PendingId = existingPending.Id,
                    State = RemotePriceIdEvidenceState.Queued
                };
            }

            return new RemotePriceIdEvidence { State = RemotePriceIdEvidenceState.Collision };
        }

        private static Task DeleteRemotePriceIdEvidencePendingAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            RemotePriceIdEvidence evidence,
            string remoteProductId,
            string remotePriceId,
            string type,
            int price,
            string source)
        {
            if (!evidence.PendingId.HasValue)
            {
                return Task.FromResult(0);
            }

            return DeletePendingRemotePriceAsync(
                conn,
                tx,
                evidence.PendingId,
                remoteProductId,
                remotePriceId,
                type,
                price,
                evidence.EffectiveAt,
                source);
        }

        private static Task<RemotePriceHistoryRow> LoadRemotePriceHistoryAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remotePriceId)
        {
            return conn.QuerySingleOrDefaultAsync<RemotePriceHistoryRow>(@"
SELECT
  barcode AS Barcode,
  timestamp AS EffectiveAt,
  type AS Type,
  new_price AS Price,
  COALESCE(source, '') AS Source
FROM product_price_history
WHERE remote_price_id = @remotePriceId
LIMIT 1",
                new { remotePriceId },
                tx);
        }

        private static Task<PendingRemotePriceRow> LoadPendingRemotePriceAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remotePriceId)
        {
            return conn.QuerySingleOrDefaultAsync<PendingRemotePriceRow>(@"
SELECT
  id AS Id,
  remote_price_id AS RemotePriceId,
  remote_product_id AS RemoteProductId,
  type AS Type,
  price AS Price,
  effective_at AS EffectiveAt,
  COALESCE(source, '') AS Source
FROM remote_catalog_pending_prices
WHERE remote_price_id = @remotePriceId
LIMIT 1",
                new { remotePriceId },
                tx);
        }

        /// <summary>
        /// Resolves legacy price-id evidence only while applying an authoritative full refresh.
        /// The original history/pending evidence is copied to an append-only quarantine table;
        /// history rows are retained with a cleared remote id, and only then is the authoritative
        /// owner adopted. Ordinary delta/retry paths never call this method and remain fail-closed.
        /// </summary>
        internal static async Task<bool> PrepareAuthoritativeRemotePriceRepairAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remoteProductId,
            string remotePriceId,
            string type,
            int price,
            string timestamp,
            string source)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (tx == null) throw new ArgumentNullException(nameof(tx));

            var normalizedRemoteProductId = (remoteProductId ?? string.Empty).Trim();
            var normalizedRemotePriceId = (remotePriceId ?? string.Empty).Trim();
            var normalizedType = (type ?? string.Empty).Trim().ToUpperInvariant();
            if (normalizedRemoteProductId.Length == 0 ||
                normalizedRemotePriceId.Length == 0 ||
                normalizedType.Length == 0 ||
                price < 0)
            {
                return false;
            }

            var normalizedTimestamp = string.IsNullOrWhiteSpace(timestamp)
                ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                : timestamp.Trim();
            var normalizedSource = string.IsNullOrWhiteSpace(source)
                ? "remote_catalog"
                : source.Trim();
            var existingHistory = await LoadRemotePriceHistoryAsync(
                conn,
                tx,
                normalizedRemotePriceId).ConfigureAwait(false);
            var existingPending = await LoadPendingRemotePriceAsync(
                conn,
                tx,
                normalizedRemotePriceId).ConfigureAwait(false);
            var storedOwner = await LoadRemotePriceOwnerAsync(
                conn,
                tx,
                normalizedRemotePriceId).ConfigureAwait(false);
            if (storedOwner.Length > 0 &&
                !string.Equals(storedOwner, normalizedRemoteProductId, StringComparison.Ordinal))
            {
                return false;
            }

            if (existingHistory == null && existingPending == null)
            {
                return true;
            }

            var effectiveAt = string.IsNullOrWhiteSpace(timestamp)
                ? existingHistory?.EffectiveAt ?? existingPending?.EffectiveAt ?? normalizedTimestamp
                : normalizedTimestamp;
            var historyMatches = existingHistory != null && RemotePriceHistoryMatches(
                existingHistory,
                normalizedType,
                price,
                effectiveAt,
                normalizedSource);
            var pendingMatches = existingPending != null && PendingRemotePriceMatches(
                existingPending,
                normalizedRemoteProductId,
                normalizedType,
                price,
                effectiveAt,
                normalizedSource);

            if (storedOwner.Length > 0)
            {
                // Once ownership exists, the id is immutable. Even an authoritative
                // full refresh may not quarantine or rewrite same-owner tuple drift.
                return (existingHistory == null || historyMatches) &&
                    (existingPending == null || pendingMatches);
            }

            if (storedOwner.Length == 0 && existingHistory == null && pendingMatches)
            {
                // A pending row records its remote product id explicitly and remains
                // sufficient immutable ownership evidence without quarantine.
                return true;
            }

            var quarantinedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            const string reason = "authoritative_full_refresh_rebind";
            await conn.ExecuteAsync(@"
INSERT OR IGNORE INTO remote_catalog_price_evidence_quarantine(
  evidence_kind,
  evidence_row_id,
  remote_price_id,
  remote_product_id,
  barcode,
  effective_at,
  type,
  old_price,
  price,
  source,
  catalog_import_client_item_id,
  catalog_import_idempotency_key,
  original_created_at,
  authoritative_remote_product_id,
  reason,
  quarantined_at)
SELECT
  'history',
  id,
  remote_price_id,
  NULL,
  barcode,
  timestamp,
  type,
  old_price,
  new_price,
  source,
  catalog_import_client_item_id,
  catalog_import_idempotency_key,
  NULL,
  @remoteProductId,
  @reason,
  @quarantinedAt
FROM product_price_history
WHERE remote_price_id = @remotePriceId;",
                new
                {
                    remotePriceId = normalizedRemotePriceId,
                    remoteProductId = normalizedRemoteProductId,
                    reason,
                    quarantinedAt
                },
                tx).ConfigureAwait(false);
            await conn.ExecuteAsync(@"
INSERT OR IGNORE INTO remote_catalog_price_evidence_quarantine(
  evidence_kind,
  evidence_row_id,
  remote_price_id,
  remote_product_id,
  barcode,
  effective_at,
  type,
  old_price,
  price,
  source,
  catalog_import_client_item_id,
  catalog_import_idempotency_key,
  original_created_at,
  authoritative_remote_product_id,
  reason,
  quarantined_at)
SELECT
  'pending',
  id,
  remote_price_id,
  remote_product_id,
  NULL,
  effective_at,
  type,
  NULL,
  price,
  source,
  NULL,
  NULL,
  created_at,
  @remoteProductId,
  @reason,
  @quarantinedAt
FROM remote_catalog_pending_prices
WHERE remote_price_id = @remotePriceId;",
                new
                {
                    remotePriceId = normalizedRemotePriceId,
                    remoteProductId = normalizedRemoteProductId,
                    reason,
                    quarantinedAt
                },
                tx).ConfigureAwait(false);

            // History is never deleted. Clearing only the ambiguous remote id allows
            // the authoritative row to establish a canonical identity while the
            // quarantine table retains the complete pre-repair evidence.
            await conn.ExecuteAsync(@"
UPDATE product_price_history
SET remote_price_id = NULL
WHERE remote_price_id = @remotePriceId;

DELETE FROM remote_catalog_pending_prices
WHERE remote_price_id = @remotePriceId;",
                new { remotePriceId = normalizedRemotePriceId },
                tx).ConfigureAwait(false);

            if (!await StoreRemotePriceOwnershipAsync(
                    conn,
                    tx,
                    normalizedRemotePriceId,
                    normalizedRemoteProductId).ConfigureAwait(false))
            {
                throw new InvalidOperationException("catalog_remote_price_owner_repair_conflict");
            }

            // Reuse an exact unbound history tuple for the current authoritative
            // product when possible. Otherwise the normal apply path inserts a fresh
            // canonical row and leaves the retained legacy row unbound.
            await conn.ExecuteAsync(@"
UPDATE product_price_history
SET remote_price_id = @remotePriceId
WHERE id = (
  SELECT history.id
  FROM product_price_history history
  JOIN products product
    ON product.barcode = history.barcode
  WHERE history.remote_price_id IS NULL
    AND product.remote_product_id = @remoteProductId
    AND COALESCE(product.is_active, 1) = 1
    AND history.timestamp = @effectiveAt
    AND history.type = @type
    AND history.new_price = @price
    AND COALESCE(history.source, '') = @source
  ORDER BY history.id DESC
  LIMIT 1
)
AND NOT EXISTS (
  SELECT 1
  FROM product_price_history existing
  WHERE existing.remote_price_id = @remotePriceId
);",
                new
                {
                    remotePriceId = normalizedRemotePriceId,
                    remoteProductId = normalizedRemoteProductId,
                    effectiveAt,
                    type = normalizedType,
                    price,
                    source = normalizedSource
                },
                tx).ConfigureAwait(false);
            return true;
        }

        private static async Task<string> LoadRemotePriceOwnerAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remotePriceId)
        {
            return (await conn.QuerySingleOrDefaultAsync<string>(@"
SELECT remote_product_id
FROM remote_catalog_price_ownership
WHERE remote_price_id = @remotePriceId
LIMIT 1",
                new { remotePriceId = (remotePriceId ?? string.Empty).Trim() },
                tx).ConfigureAwait(false) ?? string.Empty).Trim();
        }

        private static async Task<bool> EnsureRemotePriceOwnershipForEvidenceAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remotePriceId,
            string remoteProductId,
            PendingRemotePriceRow pending)
        {
            var normalizedRemotePriceId = (remotePriceId ?? string.Empty).Trim();
            var normalizedRemoteProductId = (remoteProductId ?? string.Empty).Trim();
            if (normalizedRemotePriceId.Length == 0 || normalizedRemoteProductId.Length == 0)
            {
                return false;
            }

            var storedOwner = await LoadRemotePriceOwnerAsync(
                conn,
                tx,
                normalizedRemotePriceId).ConfigureAwait(false);
            if (storedOwner.Length > 0)
            {
                return string.Equals(storedOwner, normalizedRemoteProductId, StringComparison.Ordinal);
            }

            if (pending == null)
            {
                // Legacy history rows do not record their remote product owner. The
                // current barcode owner is not immutable evidence because barcodes may
                // be renamed and later reused, so history-only evidence stays unclaimed.
                return false;
            }

            var pendingOwner = (pending.RemoteProductId ?? string.Empty).Trim();
            if (!string.Equals(pendingOwner, normalizedRemoteProductId, StringComparison.Ordinal))
            {
                return false;
            }

            return await StoreRemotePriceOwnershipAsync(
                conn,
                tx,
                normalizedRemotePriceId,
                normalizedRemoteProductId).ConfigureAwait(false);
        }

        internal static async Task<bool> StoreRemotePriceOwnershipAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remotePriceId,
            string remoteProductId)
        {
            var normalizedRemotePriceId = (remotePriceId ?? string.Empty).Trim();
            var normalizedRemoteProductId = (remoteProductId ?? string.Empty).Trim();
            if (normalizedRemotePriceId.Length == 0 || normalizedRemoteProductId.Length == 0)
            {
                return false;
            }

            var matchingOwners = await conn.ExecuteScalarAsync<long>(@"
INSERT OR IGNORE INTO remote_catalog_price_ownership(
  remote_price_id,
  remote_product_id)
VALUES(@remotePriceId, @remoteProductId);
SELECT COUNT(1)
FROM remote_catalog_price_ownership
WHERE remote_price_id = @remotePriceId
  AND TRIM(remote_product_id) = @remoteProductId;",
                new
                {
                    remotePriceId = normalizedRemotePriceId,
                    remoteProductId = normalizedRemoteProductId
                },
                tx).ConfigureAwait(false);
            return matchingOwners == 1;
        }

        private static bool RemotePriceHistoryMatches(
            RemotePriceHistoryRow existing,
            string type,
            int price,
            string effectiveAt,
            string source)
        {
            if (existing == null ||
                !string.Equals(existing.Type, type, StringComparison.Ordinal) ||
                existing.Price != price ||
                !string.Equals(existing.EffectiveAt, effectiveAt, StringComparison.Ordinal) ||
                !string.Equals(existing.Source, source, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        private static bool PendingRemotePriceMatches(
            PendingRemotePriceRow existing,
            string remoteProductId,
            string type,
            int price,
            string effectiveAt,
            string source)
        {
            return existing != null &&
                   string.Equals(
                       (existing.RemoteProductId ?? string.Empty).Trim(),
                       remoteProductId,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       (existing.Type ?? string.Empty).Trim().ToUpperInvariant(),
                       type,
                       StringComparison.Ordinal) &&
                   existing.Price == price &&
                   string.Equals(
                       (existing.EffectiveAt ?? string.Empty).Trim(),
                       effectiveAt,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       (existing.Source ?? string.Empty).Trim(),
                       source,
                       StringComparison.Ordinal);
        }

        public async Task<int> ApplyPendingRemotePricesAsync()
        {
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                var replay = await ApplyPendingRemotePricesInTransactionAsync(conn, tx).ConfigureAwait(false);
                tx.Commit();
                return replay.Applied;
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }

        internal static async Task<PendingRemotePriceReplayResult> ApplyPendingRemotePricesInTransactionAsync(
            SqliteConnection conn,
            SqliteTransaction tx)
        {
            var result = new PendingRemotePriceReplayResult();

            while (true)
            {
                var blockedByRemotePriceIdCollision = false;
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
	LIMIT @limit", new { limit = PendingRemotePriceReplayBatchSize }, tx).ConfigureAwait(false)).ToList();

                if (rows.Count == 0)
                {
                    break;
                }

                foreach (var row in rows)
                {
                    var normalizedType = (row.Type ?? string.Empty).Trim().ToUpperInvariant();
                    var normalizedEffectiveAt = string.IsNullOrWhiteSpace(row.EffectiveAt)
                        ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                        : row.EffectiveAt.Trim();
                    var normalizedSource = string.IsNullOrWhiteSpace(row.Source)
                        ? "remote_catalog"
                        : row.Source.Trim();
                    var normalizedRemotePriceId = (row.RemotePriceId ?? string.Empty).Trim();
                    if (normalizedRemotePriceId.Length > 0)
                    {
                        var storedOwner = await LoadRemotePriceOwnerAsync(
                            conn,
                            tx,
                            normalizedRemotePriceId).ConfigureAwait(false);
                        if (storedOwner.Length > 0 &&
                            !string.Equals(
                                storedOwner,
                                (row.RemoteProductId ?? string.Empty).Trim(),
                                StringComparison.Ordinal))
                        {
                            result.CollisionIds.Add(row.Id);
                            blockedByRemotePriceIdCollision = true;
                            continue;
                        }
                    }

                    var insertedRows = await InsertRemotePriceHistoryAsync(
                        conn,
                        tx,
                        row.Barcode,
                        row.RemotePriceId,
                        normalizedType,
                        row.Price,
                        normalizedEffectiveAt,
                        normalizedSource,
                        (row.RemoteProductId ?? string.Empty).Trim())
                        .ConfigureAwait(false);

                    if (normalizedRemotePriceId.Length > 0 && insertedRows == 0)
                    {
                        var evidence = await EvaluateRemotePriceIdEvidenceAsync(
                            conn,
                            tx,
                            (row.RemoteProductId ?? string.Empty).Trim(),
                            normalizedRemotePriceId,
                            normalizedType,
                            row.Price,
                            row.EffectiveAt,
                            normalizedEffectiveAt,
                            normalizedSource).ConfigureAwait(false);
                        if (evidence.State != RemotePriceIdEvidenceState.Applied)
                        {
                            result.CollisionIds.Add(row.Id);
                            blockedByRemotePriceIdCollision = true;
                            continue;
                        }
                    }
                    else if (normalizedRemotePriceId.Length > 0 &&
                        !await StoreRemotePriceOwnershipAsync(
                                conn,
                                tx,
                                normalizedRemotePriceId,
                                (row.RemoteProductId ?? string.Empty).Trim())
                            .ConfigureAwait(false))
                    {
                        throw new InvalidOperationException("catalog_remote_price_owner_write_conflict");
                    }

                    await DeletePendingRemotePriceAsync(
                        conn,
                        tx,
                        row.Id,
                        row.RemoteProductId,
                        row.RemotePriceId,
                        row.Type,
                        row.Price,
                        row.EffectiveAt,
                        row.Source).ConfigureAwait(false);
                    result.Applied += 1;
                }

                if (blockedByRemotePriceIdCollision || rows.Count < PendingRemotePriceReplayBatchSize)
                {
                    break;
                }
            }

            return result;
        }

        public Task<long> CountActiveRemoteProductsAsync() => _queries.CountActiveRemoteProductsAsync();

        private static Task<int> InsertRemotePriceHistoryAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string barcode,
            string remotePriceId,
            string type,
            int price,
            string timestamp,
            string source,
            string remoteProductId)
        {
            var normalizedRemotePriceId = (remotePriceId ?? string.Empty).Trim();
            var normalizedRemoteProductId = (remoteProductId ?? string.Empty).Trim();
            var normalizedTimestamp = string.IsNullOrWhiteSpace(timestamp)
                ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                : timestamp.Trim();
            var normalizedType = (type ?? string.Empty).Trim().ToUpperInvariant();
            var normalizedSource = string.IsNullOrWhiteSpace(source) ? "remote_catalog" : source.Trim();
            return conn.ExecuteAsync(@"
INSERT OR IGNORE INTO product_price_history(
    barcode,
    timestamp,
    type,
    old_price,
    new_price,
    source,
    remote_price_id)
SELECT
    @barcode,
    @timestamp,
    @type,
    NULL,
    @price,
    @source,
    NULLIF(@remotePriceId, '')
WHERE (
      @remotePriceId = ''
      OR NOT EXISTS (
        SELECT 1
        FROM remote_catalog_price_ownership ownership
        WHERE ownership.remote_price_id = @remotePriceId
          AND TRIM(ownership.remote_product_id) <> @remoteProductId
      )
   )
  AND (
      @remotePriceId = ''
      OR NOT EXISTS (
        SELECT 1
        FROM remote_catalog_pending_prices
        WHERE remote_price_id = @remotePriceId
          AND NOT (
              TRIM(COALESCE(remote_product_id, '')) = @remoteProductId
              AND type = @type
              AND price = @price
              AND effective_at = @timestamp
              AND COALESCE(source, '') = @source
          )
      )
   )",
                new
                {
                    barcode = (barcode ?? string.Empty).Trim(),
                    timestamp = normalizedTimestamp,
                    type = normalizedType,
                    price,
                    source = normalizedSource,
                    remotePriceId = normalizedRemotePriceId,
                    remoteProductId = normalizedRemoteProductId
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
            string source,
            SqliteTransaction tx)
        {
            var normalizedRemotePriceId = (remotePriceId ?? string.Empty).Trim();
            var normalizedTimestamp = string.IsNullOrWhiteSpace(timestamp)
                ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                : timestamp.Trim();
            var normalizedType = (type ?? string.Empty).Trim().ToUpperInvariant();
            var normalizedSource = string.IsNullOrWhiteSpace(source) ? "remote_catalog" : source.Trim();
            return conn.ExecuteAsync(@"
INSERT OR IGNORE INTO remote_catalog_pending_prices(
    remote_price_id,
    remote_product_id,
    type,
    price,
    effective_at,
    source,
    created_at)
SELECT
    NULLIF(@remotePriceId, ''),
    @remoteProductId,
    @type,
    @price,
    @effectiveAt,
    @source,
    @createdAt
WHERE (
      @remotePriceId = ''
      OR NOT EXISTS (
        SELECT 1
        FROM remote_catalog_price_ownership ownership
        WHERE ownership.remote_price_id = @remotePriceId
          AND TRIM(ownership.remote_product_id) <> @remoteProductId
      )
   )
  AND (
      @remotePriceId = ''
      OR NOT EXISTS (
        SELECT 1
        FROM product_price_history
        WHERE remote_price_id = @remotePriceId
      )
   )",
                new
                {
                    remotePriceId = normalizedRemotePriceId,
                    remoteProductId = (remoteProductId ?? string.Empty).Trim(),
                    type = normalizedType,
                    price,
                    effectiveAt = normalizedTimestamp,
                    source = normalizedSource,
                    createdAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                },
                tx);
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
                return conn.ExecuteAsync(@"
DELETE FROM remote_catalog_pending_prices
WHERE remote_price_id = @remotePriceId
  AND TRIM(COALESCE(remote_product_id, '')) = @remoteProductId
  AND type = @type
  AND price = @price
  AND effective_at = @effectiveAt
  AND COALESCE(source, '') = @source",
                    new
                    {
                        remotePriceId = normalizedRemotePriceId,
                        remoteProductId = (remoteProductId ?? string.Empty).Trim(),
                        type = (type ?? string.Empty).Trim().ToUpperInvariant(),
                        price,
                        effectiveAt = string.IsNullOrWhiteSpace(timestamp)
                            ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                            : timestamp.Trim(),
                        source = string.IsNullOrWhiteSpace(source) ? "remote_catalog" : source.Trim()
                    },
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
            SalesReceiptContentPolicy.EnsureValidProductIdentity(p.Barcode, p.Name);
            await CatalogMetaWriteGate.WaitAsync().ConfigureAwait(false);
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
                CatalogMetaWriteGate.Release();
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
                ? await ResolveSupplierReferenceAsync(conn, tx, supplierId, supplierName).ConfigureAwait(false)
                : await batchContext.ResolveSupplierAsync(conn, tx, supplierId, supplierName).ConfigureAwait(false);
            var categoryRef = batchContext == null
                ? await ResolveCategoryReferenceAsync(conn, tx, categoryId, categoryName).ConfigureAwait(false)
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
                var name = NormalizeCatalogName(existing.Name);
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
                var name = NormalizeCatalogName(existing.Name);
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
                    var normalizedName = NormalizeCatalogName(reference.Name);
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
                var normalizedName = NormalizeCatalogName(name);
                if (id.HasValue && id.Value != 0 && byId.TryGetValue(id.Value, out var existingById) &&
                    (normalizedName.Length == 0 || NamesMatch(normalizedName, existingById.Name)))
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
        public async Task UpdateProductAndMetaInTransactionAsync(long productId, string name, long unitPriceMinor, string barcode, string articleCode, string name2, int purchasePrice, int? supplierId, string supplierName, int? categoryId, string categoryName, int stockQty)
        {
            if (productId <= 0) throw new ArgumentException("invalid product id");
            SalesReceiptContentPolicy.EnsureValidProductIdentity(barcode, name);
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
            SalesReceiptContentPolicy.EnsureValidProductIdentity(barcode, name);
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
                "INSERT OR IGNORE INTO suppliers(name, is_active) VALUES(@name, 1)",
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
                "INSERT OR IGNORE INTO categories(name, is_active) VALUES(@name, 1)",
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
                "SELECT id AS Id, name AS Name FROM suppliers WHERE id = @id AND COALESCE(is_active, 1) = 1 LIMIT 1",
                new { id = supplierId.Value },
                tx);
        }

        private static Task<ProductMetaReference> FindCategoryByIdAsync(SqliteConnection conn, SqliteTransaction tx, int? categoryId)
        {
            if (!categoryId.HasValue || categoryId.Value == 0)
                return Task.FromResult<ProductMetaReference>(null);

            return conn.QueryFirstOrDefaultAsync<ProductMetaReference>(
                "SELECT id AS Id, name AS Name FROM categories WHERE id = @id AND COALESCE(is_active, 1) = 1 LIMIT 1",
                new { id = categoryId.Value },
                tx);
        }

        private static Task<ProductMetaReference> FindSupplierByNormalizedNameAsync(SqliteConnection conn, SqliteTransaction tx, string normalizedName)
        {
            return conn.QueryFirstOrDefaultAsync<ProductMetaReference>(
                @"SELECT id AS Id, name AS Name
FROM suppliers
WHERE COALESCE(is_active, 1) = 1
  AND LOWER(TRIM(name)) = LOWER(@name)
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
WHERE COALESCE(is_active, 1) = 1
  AND LOWER(TRIM(name)) = LOWER(@name)
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
