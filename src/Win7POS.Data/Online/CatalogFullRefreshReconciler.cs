using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Online
{
    public sealed class CatalogFullRefreshReconciler
    {
        private readonly SqliteConnectionFactory _factory;

        public CatalogFullRefreshReconciler(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<CatalogExactnessResult> ReconcileAndVerifyAsync(
            IEnumerable<string> productIds,
            IEnumerable<string> categoryIds,
            IEnumerable<string> supplierIds,
            string generatedAt,
            PosCatalogSummaryResponse summary,
            CatalogExactnessRunContext context,
            OnlineSyncGeneration generation = null)
        {
            if (!CatalogPaginationSafetyPolicy.HasCompleteValidSummary(summary))
            {
                // Rejection must happen before the destructive reconciliation
                // boundary so the last known-good live catalog remains intact.
                var currentAudit = await AuditCurrentAsync().ConfigureAwait(false);
                return CatalogExactnessVerifier.Evaluate(summary, currentAudit, context);
            }

            var audit = await ReconcileAsync(
                productIds,
                categoryIds,
                supplierIds,
                generatedAt,
                generation).ConfigureAwait(false);
            return CatalogExactnessVerifier.Evaluate(summary, audit, context);
        }

        public async Task<CatalogExactnessResult> ReconcileAndVerifyStagedAsync(
            string fullRunId,
            string generatedAt,
            PosCatalogSummaryResponse summary,
            CatalogExactnessRunContext context,
            RemoteCatalogCommitFence commitFence,
            CancellationToken cancellationToken = default)
        {
            if (!CatalogPaginationSafetyPolicy.HasCompleteValidSummary(summary))
            {
                var currentAudit = await AuditCurrentAsync().ConfigureAwait(false);
                return CatalogExactnessVerifier.Evaluate(summary, currentAudit, context);
            }

            RemoteCatalogContentPolicy.EnsureOptionalTimestamp(
                generatedAt,
                "catalog.generated_at");
            var normalizedRunId = (fullRunId ?? string.Empty).Trim();
            if (normalizedRunId.Length == 0 ||
                normalizedRunId.Length > 64 ||
                !string.Equals(normalizedRunId, fullRunId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Authoritative catalog full run identity is invalid.",
                    nameof(fullRunId));
            }
            if (commitFence == null)
                throw new ArgumentNullException(nameof(commitFence));

            var runContext = context ?? new CatalogExactnessRunContext();
            var removedAt = string.IsNullOrWhiteSpace(generatedAt)
                ? DateTimeOffset.UtcNow.ToString("O")
                : generatedAt.Trim();

            cancellationToken.ThrowIfCancellationRequested();
            await ProductRepository.CatalogMetaWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var conn = _factory.Open();
                using var tx = conn.BeginTransaction(deferred: false);
                try
                {
                    await RemoteCatalogBatchRepository.RequireCommitFenceAsync(
                        conn,
                        tx,
                        commitFence).ConfigureAwait(false);
                    await PrepareStagedAuthoritativeTablesAsync(
                        conn,
                        tx,
                        normalizedRunId,
                        commitFence).ConfigureAwait(false);
                    var evidence = await LoadStagedEvidenceAsync(
                        conn,
                        tx,
                        normalizedRunId,
                        commitFence).ConfigureAwait(false);
                    var rejectionCode = FindStagedPreflightError(
                        evidence,
                        summary,
                        runContext);
                    if (rejectionCode.Length > 0)
                    {
                        var currentAudit = await LoadAuditAsync(conn, tx).ConfigureAwait(false);
                        ApplyStageEvidence(currentAudit, evidence);
                        tx.Commit();
                        return CreateStageRejection(
                            rejectionCode,
                            summary,
                            currentAudit,
                            runContext);
                    }

                    runContext.DuplicatePriceRows = evidence.DuplicatePrices;
                    var audit = await ReconcilePreparedAsync(
                        conn,
                        tx,
                        removedAt,
                        evidence.ToAuthoritativeCounts()).ConfigureAwait(false);
                    tx.Commit();
                    return CatalogExactnessVerifier.Evaluate(summary, audit, runContext);
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

        public async Task<string> ValidateStagedPreflightAsync(
            string fullRunId,
            PosCatalogSummaryResponse summary,
            CatalogExactnessRunContext context,
            RemoteCatalogCommitFence commitFence,
            CancellationToken cancellationToken = default)
        {
            var summaryError = PosOnlineCompatibilityValidator.ValidateCatalogSummary(summary);
            if (!string.IsNullOrWhiteSpace(summaryError))
                return summaryError;
            if (!CatalogPaginationSafetyPolicy.HasCompleteValidSummary(summary))
                return "catalog_summary_incomplete";

            var normalizedRunId = (fullRunId ?? string.Empty).Trim();
            if (normalizedRunId.Length == 0 ||
                normalizedRunId.Length > 64 ||
                !string.Equals(normalizedRunId, fullRunId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Authoritative catalog full run identity is invalid.",
                    nameof(fullRunId));
            }
            if (commitFence == null)
                throw new ArgumentNullException(nameof(commitFence));

            cancellationToken.ThrowIfCancellationRequested();
            await ProductRepository.CatalogMetaWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var conn = _factory.Open();
                using var tx = conn.BeginTransaction(deferred: false);
                try
                {
                    await RemoteCatalogBatchRepository.RequireCommitFenceAsync(
                        conn,
                        tx,
                        commitFence).ConfigureAwait(false);
                    await PrepareStagedAuthoritativeTablesAsync(
                        conn,
                        tx,
                        normalizedRunId,
                        commitFence).ConfigureAwait(false);
                    var evidence = await LoadStagedEvidenceAsync(
                        conn,
                        tx,
                        normalizedRunId,
                        commitFence).ConfigureAwait(false);
                    var code = FindStagedPreflightError(
                        evidence,
                        summary,
                        context ?? new CatalogExactnessRunContext(),
                        requireAppliedEvidence: false);
                    tx.Commit();
                    return code;
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

        public async Task<int> ClearStaleAuthoritativeStagesAsync(
            string shopId,
            string shopCode,
            string keepFullRunId,
            int maximumRows = 4096)
        {
            var normalizedShopId = RequireStageScopeValue(shopId, nameof(shopId));
            var normalizedShopCode = RequireStageScopeValue(shopCode, nameof(shopCode));
            var normalizedKeepRunId = (keepFullRunId ?? string.Empty).Trim();
            if (maximumRows <= 0 || maximumRows > 16384)
                throw new ArgumentOutOfRangeException(nameof(maximumRows));

            await ProductRepository.CatalogMetaWriteGate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var conn = _factory.Open();
                using var tx = conn.BeginTransaction(deferred: false);
                var removed = await conn.ExecuteAsync(@"
DELETE FROM catalog_authoritative_id_stage
WHERE stage_id IN (
  SELECT item.stage_id
  FROM catalog_authoritative_id_stage item
  JOIN catalog_authoritative_stage_scope scope ON scope.scope_id = item.scope_id
  WHERE scope.shop_id = @normalizedShopId
    AND scope.shop_code = @normalizedShopCode
    AND (@normalizedKeepRunId = '' OR scope.full_run_id <> @normalizedKeepRunId)
  ORDER BY item.stage_id
  LIMIT @maximumRows
);",
                    new
                    {
                        normalizedShopId,
                        normalizedShopCode,
                        normalizedKeepRunId,
                        maximumRows
                    },
                    tx).ConfigureAwait(false);
                await conn.ExecuteAsync(@"
DELETE FROM catalog_authoritative_stage_scope
WHERE scope_id IN (
  SELECT scope.scope_id
  FROM catalog_authoritative_stage_scope scope
  WHERE scope.shop_id = @normalizedShopId
    AND scope.shop_code = @normalizedShopCode
    AND (@normalizedKeepRunId = '' OR scope.full_run_id <> @normalizedKeepRunId)
    AND NOT EXISTS (
      SELECT 1 FROM catalog_authoritative_id_stage item
      WHERE item.scope_id = scope.scope_id)
  ORDER BY scope.scope_id
  LIMIT @maximumRows
);",
                    new { normalizedShopId, normalizedShopCode, normalizedKeepRunId, maximumRows },
                    tx).ConfigureAwait(false);
                tx.Commit();
                return removed;
            }
            finally
            {
                ProductRepository.CatalogMetaWriteGate.Release();
            }
        }

        public async Task<int> ClearAuthoritativeStageAsync(
            string fullRunId,
            string shopId,
            string shopCode)
        {
            var normalizedRunId = RequireStageScopeValue(fullRunId, nameof(fullRunId));
            var normalizedShopId = RequireStageScopeValue(shopId, nameof(shopId));
            var normalizedShopCode = RequireStageScopeValue(shopCode, nameof(shopCode));

            await ProductRepository.CatalogMetaWriteGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var totalRemoved = 0;
                const int maximumRowsPerTransaction = 16384;
                while (true)
                {
                    using var conn = _factory.Open();
                    using var tx = conn.BeginTransaction(deferred: false);
                    var removed = await conn.ExecuteAsync(@"
DELETE FROM catalog_authoritative_id_stage
WHERE stage_id IN (
  SELECT item.stage_id
  FROM catalog_authoritative_id_stage item
  JOIN catalog_authoritative_stage_scope scope ON scope.scope_id = item.scope_id
  WHERE scope.shop_id = @normalizedShopId
    AND scope.shop_code = @normalizedShopCode
    AND scope.full_run_id = @normalizedRunId
  ORDER BY item.stage_id
  LIMIT @maximumRowsPerTransaction
);",
                        new
                        {
                            normalizedShopId,
                            normalizedShopCode,
                            normalizedRunId,
                            maximumRowsPerTransaction
                        },
                        tx).ConfigureAwait(false);
                    if (removed < maximumRowsPerTransaction)
                    {
                        await conn.ExecuteAsync(@"
DELETE FROM catalog_authoritative_stage_scope
WHERE shop_id = @normalizedShopId
  AND shop_code = @normalizedShopCode
  AND full_run_id = @normalizedRunId
  AND NOT EXISTS (
    SELECT 1 FROM catalog_authoritative_id_stage item
    WHERE item.scope_id = catalog_authoritative_stage_scope.scope_id);",
                            new { normalizedShopId, normalizedShopCode, normalizedRunId },
                            tx).ConfigureAwait(false);
                    }
                    tx.Commit();
                    totalRemoved = checked(totalRemoved + removed);
                    if (removed < maximumRowsPerTransaction) return totalRemoved;
                    await Task.Yield();
                }
            }
            finally
            {
                ProductRepository.CatalogMetaWriteGate.Release();
            }
        }

        private static string RequireStageScopeValue(string value, string parameterName)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0 || normalized.Length > 128)
                throw new ArgumentException("Catalog authoritative stage scope is invalid.", parameterName);
            return normalized;
        }

        public async Task<CatalogFullRefreshResult> ReconcileAsync(
            IEnumerable<string> productIds,
            IEnumerable<string> categoryIds,
            IEnumerable<string> supplierIds,
            string generatedAt,
            OnlineSyncGeneration generation = null)
        {
            RemoteCatalogContentPolicy.EnsureOptionalTimestamp(
                generatedAt,
                "catalog.generated_at");
            var products = CatalogIdSet.Create(productIds);
            var categories = CatalogIdSet.Create(categoryIds);
            var suppliers = CatalogIdSet.Create(supplierIds);
            var removedAt = string.IsNullOrWhiteSpace(generatedAt)
                ? DateTimeOffset.UtcNow.ToString("O")
                : generatedAt.Trim();

            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction(deferred: false))
            {
                var permitted = generation != null
                    ? await OnlineSyncGenerationRepository.IsCurrentAndActiveAsync(
                        conn,
                        tx,
                        generation).ConfigureAwait(false)
                    : await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM pos_sync_session_generation
WHERE singleton_id = 1 AND active = 1;",
                        transaction: tx).ConfigureAwait(false) == 0;
                if (!permitted)
                    throw new InvalidOperationException("Online sync generation mismatch.");

                await CreateAndFillAsync(conn, tx, "temp_full_product_ids", products.Values).ConfigureAwait(false);
                await CreateAndFillAsync(conn, tx, "temp_full_category_ids", categories.Values).ConfigureAwait(false);
                await CreateAndFillAsync(conn, tx, "temp_full_supplier_ids", suppliers.Values).ConfigureAwait(false);
                var result = await ReconcilePreparedAsync(
                    conn,
                    tx,
                    removedAt,
                    new CatalogAuthoritativeCounts
                    {
                        ReceivedProducts = products.ReceivedCount,
                        DistinctProducts = products.Values.Count,
                        DuplicateProducts = products.DuplicateCount,
                        InvalidProducts = products.InvalidCount,
                        ReceivedCategories = categories.ReceivedCount,
                        DistinctCategories = categories.Values.Count,
                        DuplicateCategories = categories.DuplicateCount,
                        InvalidCategories = categories.InvalidCount,
                        ReceivedSuppliers = suppliers.ReceivedCount,
                        DistinctSuppliers = suppliers.Values.Count,
                        DuplicateSuppliers = suppliers.DuplicateCount,
                        InvalidSuppliers = suppliers.InvalidCount
                    }).ConfigureAwait(false);
                tx.Commit();
                return result;
            }
        }

        public async Task<CatalogFullRefreshResult> AuditCurrentAsync()
        {
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await conn.ExecuteAsync(@"
CREATE TEMP TABLE IF NOT EXISTS temp_full_product_ids (id TEXT PRIMARY KEY);
CREATE TEMP TABLE IF NOT EXISTS temp_full_category_ids (id TEXT PRIMARY KEY);
CREATE TEMP TABLE IF NOT EXISTS temp_full_supplier_ids (id TEXT PRIMARY KEY);
DELETE FROM temp_full_product_ids;
DELETE FROM temp_full_category_ids;
DELETE FROM temp_full_supplier_ids;
INSERT OR IGNORE INTO temp_full_product_ids(id)
SELECT TRIM(remote_product_id)
FROM products
WHERE TRIM(COALESCE(remote_product_id, '')) <> ''
  AND COALESCE(is_active, 1) = 1;
INSERT OR IGNORE INTO temp_full_category_ids(id)
SELECT TRIM(remote_category_id)
FROM categories
WHERE TRIM(COALESCE(remote_category_id, '')) <> ''
  AND COALESCE(is_active, 1) = 1;
INSERT OR IGNORE INTO temp_full_supplier_ids(id)
SELECT TRIM(remote_supplier_id)
FROM suppliers
WHERE TRIM(COALESCE(remote_supplier_id, '')) <> ''
  AND COALESCE(is_active, 1) = 1;",
                    transaction: tx).ConfigureAwait(false);
                var result = await LoadAuditAsync(conn, tx).ConfigureAwait(false);
                // The non-mutating audit treats the current active identity sets
                // as its comparison baseline.  Without this evidence a healthy
                // nonempty catalog would look like an unapplied authoritative set
                // before the verifier can classify a missing summary as Unverified.
                result.ReceivedProductIds = result.DistinctActiveRemoteProductIds;
                result.DistinctAuthoritativeProductIds = result.DistinctActiveRemoteProductIds;
                result.ReceivedCategoryIds = result.DistinctActiveRemoteCategoryIds;
                result.DistinctAuthoritativeCategoryIds = result.DistinctActiveRemoteCategoryIds;
                result.ReceivedSupplierIds = result.DistinctActiveRemoteSupplierIds;
                result.DistinctAuthoritativeSupplierIds = result.DistinctActiveRemoteSupplierIds;
                tx.Commit();
                return result;
            }
        }

        private static async Task PrepareStagedAuthoritativeTablesAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string fullRunId,
            RemoteCatalogCommitFence commitFence)
        {
            await conn.ExecuteAsync(@"
CREATE TEMP TABLE IF NOT EXISTS temp_full_product_ids (id TEXT PRIMARY KEY);
CREATE TEMP TABLE IF NOT EXISTS temp_full_category_ids (id TEXT PRIMARY KEY);
CREATE TEMP TABLE IF NOT EXISTS temp_full_supplier_ids (id TEXT PRIMARY KEY);
DELETE FROM temp_full_product_ids;
DELETE FROM temp_full_category_ids;
DELETE FROM temp_full_supplier_ids;
INSERT OR IGNORE INTO temp_full_product_ids(id)
SELECT item.remote_id
FROM catalog_authoritative_id_stage item
JOIN catalog_authoritative_stage_scope scope ON scope.scope_id = item.scope_id
WHERE scope.shop_id = @ShopId
  AND scope.shop_code = @ShopCode
  AND scope.transition_epoch = @TransitionEpoch
  AND scope.generation_id = @GenerationId
  AND scope.generation_fingerprint = @GenerationFingerprint
  AND scope.full_run_id = @FullRunId
  AND item.entity_kind = 'product'
  AND TRIM(item.remote_id) <> '';
INSERT OR IGNORE INTO temp_full_category_ids(id)
SELECT item.remote_id
FROM catalog_authoritative_id_stage item
JOIN catalog_authoritative_stage_scope scope ON scope.scope_id = item.scope_id
WHERE scope.shop_id = @ShopId
  AND scope.shop_code = @ShopCode
  AND scope.transition_epoch = @TransitionEpoch
  AND scope.generation_id = @GenerationId
  AND scope.generation_fingerprint = @GenerationFingerprint
  AND scope.full_run_id = @FullRunId
  AND item.entity_kind = 'category'
  AND TRIM(item.remote_id) <> '';
INSERT OR IGNORE INTO temp_full_supplier_ids(id)
SELECT item.remote_id
FROM catalog_authoritative_id_stage item
JOIN catalog_authoritative_stage_scope scope ON scope.scope_id = item.scope_id
WHERE scope.shop_id = @ShopId
  AND scope.shop_code = @ShopCode
  AND scope.transition_epoch = @TransitionEpoch
  AND scope.generation_id = @GenerationId
  AND scope.generation_fingerprint = @GenerationFingerprint
  AND scope.full_run_id = @FullRunId
  AND item.entity_kind = 'supplier'
  AND TRIM(item.remote_id) <> '';",
                StageScope.Create(fullRunId, commitFence),
                tx).ConfigureAwait(false);
        }

        private static Task<CatalogStagedEvidence> LoadStagedEvidenceAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string fullRunId,
            RemoteCatalogCommitFence commitFence)
        {
            return conn.QuerySingleAsync<CatalogStagedEvidence>(@"
WITH scoped AS (
  SELECT page_number, entity_kind, remote_id, content_fingerprint,
         category_remote_id, supplier_remote_id, product_remote_id,
         occurrence_count, has_more
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
  SUM(CASE WHEN entity_kind = 'page' THEN 1 ELSE 0 END) AS PageMarkers,
  COALESCE(MIN(CASE WHEN entity_kind = 'page' THEN page_number END), 0) AS FirstPage,
  COALESCE(MAX(CASE WHEN entity_kind = 'page' THEN page_number END), 0) AS LastPage,
  SUM(CASE WHEN entity_kind = 'page' AND has_more = 1 THEN 1 ELSE 0 END) AS ContinuationPages,
  SUM(CASE WHEN entity_kind = 'page' AND has_more = 0 THEN 1 ELSE 0 END) AS TerminalPages,
  COALESCE(MAX(CASE WHEN entity_kind = 'page' AND has_more = 0 THEN page_number END), 0)
    AS TerminalPageNumber,
  (SELECT COUNT(DISTINCT rows.page_number)
   FROM scoped rows
   WHERE rows.entity_kind <> 'page'
     AND NOT EXISTS (
       SELECT 1 FROM scoped marker
       WHERE marker.entity_kind = 'page'
         AND marker.page_number = rows.page_number)) AS OrphanPageRows,
  COALESCE(SUM(CASE WHEN entity_kind = 'product' THEN occurrence_count ELSE 0 END), 0)
    AS ReceivedProducts,
  COUNT(DISTINCT CASE
    WHEN entity_kind = 'product' AND TRIM(remote_id) <> '' THEN remote_id END)
    AS DistinctProducts,
  COALESCE(SUM(CASE
    WHEN entity_kind = 'product' AND TRIM(remote_id) = '' THEN occurrence_count ELSE 0 END), 0)
    AS InvalidProducts,
  COALESCE(SUM(CASE
    WHEN entity_kind = 'product' AND TRIM(remote_id) <> '' THEN occurrence_count ELSE 0 END), 0)
    - COUNT(DISTINCT CASE
        WHEN entity_kind = 'product' AND TRIM(remote_id) <> '' THEN remote_id END)
    AS DuplicateProducts,
  COUNT(DISTINCT CASE WHEN entity_kind = 'category' THEN remote_id END)
    AS ReceivedCategories,
  COUNT(DISTINCT CASE
    WHEN entity_kind = 'category' AND TRIM(remote_id) <> '' THEN remote_id END)
    AS DistinctCategories,
  COUNT(DISTINCT CASE
    WHEN entity_kind = 'category' AND TRIM(remote_id) = '' THEN remote_id END)
    AS InvalidCategories,
  COUNT(DISTINCT CASE WHEN entity_kind = 'supplier' THEN remote_id END)
    AS ReceivedSuppliers,
  COUNT(DISTINCT CASE
    WHEN entity_kind = 'supplier' AND TRIM(remote_id) <> '' THEN remote_id END)
    AS DistinctSuppliers,
  COUNT(DISTINCT CASE
    WHEN entity_kind = 'supplier' AND TRIM(remote_id) = '' THEN remote_id END)
    AS InvalidSuppliers,
  COALESCE(SUM(CASE WHEN entity_kind = 'price' THEN occurrence_count ELSE 0 END), 0)
    AS ReceivedPrices,
  COALESCE(SUM(CASE
    WHEN entity_kind = 'price' AND TRIM(remote_id) <> '' THEN occurrence_count ELSE 0 END), 0)
    - COUNT(DISTINCT CASE
        WHEN entity_kind = 'price' AND TRIM(remote_id) <> '' THEN remote_id END)
    AS DuplicatePrices,
  COALESCE(SUM(CASE WHEN entity_kind IN (
    'product_tombstone', 'category_tombstone', 'supplier_tombstone')
    THEN occurrence_count ELSE 0 END), 0) AS ReceivedTombstones,
  COALESCE(SUM(CASE WHEN entity_kind = 'product' AND content_fingerprint = 'invalid'
    THEN occurrence_count ELSE 0 END), 0) AS InvalidProductRows,
  COALESCE(SUM(CASE WHEN entity_kind = 'category' AND content_fingerprint = 'invalid'
    THEN occurrence_count ELSE 0 END), 0) AS InvalidCategoryRows,
  COALESCE(SUM(CASE WHEN entity_kind = 'supplier' AND content_fingerprint = 'invalid'
    THEN occurrence_count ELSE 0 END), 0) AS InvalidSupplierRows,
  COALESCE(SUM(CASE WHEN entity_kind = 'price' AND content_fingerprint = 'invalid'
    THEN occurrence_count ELSE 0 END), 0) AS InvalidPriceRows,
  COALESCE(SUM(CASE WHEN entity_kind = 'product_tombstone' AND content_fingerprint = 'invalid'
    THEN occurrence_count ELSE 0 END), 0) AS InvalidProductTombstoneRows,
  COALESCE(SUM(CASE WHEN entity_kind = 'category_tombstone' AND content_fingerprint = 'invalid'
    THEN occurrence_count ELSE 0 END), 0) AS InvalidCategoryTombstoneRows,
  COALESCE(SUM(CASE WHEN entity_kind = 'supplier_tombstone' AND content_fingerprint = 'invalid'
    THEN occurrence_count ELSE 0 END), 0) AS InvalidSupplierTombstoneRows,
  (SELECT COUNT(1) FROM (
     SELECT content_fingerprint FROM scoped
     WHERE entity_kind = 'product' AND content_fingerprint LIKE 'barcode:%'
     GROUP BY content_fingerprint HAVING COUNT(DISTINCT remote_id) > 1
   )) AS ProductIdentityConflicts,
  (SELECT COUNT(1) FROM scoped product
   WHERE product.entity_kind = 'product'
     AND TRIM(product.category_remote_id) <> ''
     AND NOT EXISTS (
       SELECT 1 FROM temp_full_category_ids category
       WHERE category.id = product.category_remote_id)) AS OrphanCategoryReferences,
  (SELECT COUNT(1) FROM scoped product
   WHERE product.entity_kind = 'product'
     AND TRIM(product.supplier_remote_id) <> ''
     AND NOT EXISTS (
       SELECT 1 FROM temp_full_supplier_ids supplier
       WHERE supplier.id = product.supplier_remote_id)) AS OrphanSupplierReferences,
  (SELECT COUNT(1) FROM scoped price
   WHERE price.entity_kind = 'price'
     AND (TRIM(price.product_remote_id) = '' OR NOT EXISTS (
       SELECT 1 FROM temp_full_product_ids product
       WHERE product.id = price.product_remote_id))) AS OrphanPriceProductReferences,
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
  EXISTS(
    SELECT 1 FROM scoped active JOIN scoped tombstone
      ON tombstone.remote_id = active.remote_id
    WHERE active.entity_kind = 'product'
      AND tombstone.entity_kind = 'product_tombstone') AS ProductActiveTombstoneConflicts,
  EXISTS(
    SELECT 1 FROM scoped active JOIN scoped tombstone
      ON tombstone.remote_id = active.remote_id
    WHERE active.entity_kind = 'category'
      AND tombstone.entity_kind = 'category_tombstone') AS CategoryActiveTombstoneConflicts,
  EXISTS(
    SELECT 1 FROM scoped active JOIN scoped tombstone
      ON tombstone.remote_id = active.remote_id
    WHERE active.entity_kind = 'supplier'
      AND tombstone.entity_kind = 'supplier_tombstone') AS SupplierActiveTombstoneConflicts
FROM scoped;",
                StageScope.Create(fullRunId, commitFence),
                tx);
        }

        private static string FindStagedPreflightError(
            CatalogStagedEvidence evidence,
            PosCatalogSummaryResponse summary,
            CatalogExactnessRunContext context,
            bool requireAppliedEvidence = true)
        {
            if (context.Pages <= 0 ||
                evidence.PageMarkers != context.Pages ||
                evidence.FirstPage != 1 ||
                evidence.LastPage != context.Pages ||
                evidence.ContinuationPages != context.Pages - 1 ||
                evidence.TerminalPages != 1 ||
                evidence.TerminalPageNumber != context.Pages ||
                evidence.OrphanPageRows != 0)
            {
                return "catalog_authoritative_stage_incomplete";
            }

            if (context.HasMore)
                return "catalog_pagination_not_complete";
            if (!string.Equals(context.SyncMode, "full_refresh", StringComparison.OrdinalIgnoreCase))
                return "catalog_exactness_requires_full_refresh";
            if (string.IsNullOrWhiteSpace(context.CatalogVersion))
                return "catalog_version_missing";
            if (string.IsNullOrWhiteSpace(context.SyncCursor))
                return "catalog_cursor_missing";
            var expectedChecksum = (summary.Checksum ?? string.Empty).Trim();
            if (expectedChecksum.Length > 0)
            {
                if (!string.Equals(
                        (summary.ChecksumAlgorithm ?? string.Empty).Trim(),
                        "sha256",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return "catalog_checksum_algorithm_unsupported";
                }
                if (!IsSha256HexValue(expectedChecksum))
                    return "catalog_summary_checksum_invalid";

                var actualChecksum = (context.ActualChecksum ?? string.Empty).Trim();
                if (actualChecksum.Length == 0)
                    return "catalog_checksum_unverifiable";
                if (!string.Equals(
                        (context.ActualChecksumAlgorithm ?? string.Empty).Trim(),
                        "sha256",
                        StringComparison.OrdinalIgnoreCase) ||
                    !IsSha256HexValue(actualChecksum))
                {
                    return "catalog_checksum_algorithm_unsupported";
                }
                if (!string.Equals(expectedChecksum, actualChecksum, StringComparison.OrdinalIgnoreCase))
                    return "catalog_checksum_mismatch";
            }
            if (context.DurationMilliseconds < 0 ||
                context.InvalidPriceRows < 0 ||
                context.PriceRowsAccepted < 0 ||
                context.PriceRowsReceived < 0 ||
                context.ProductRowsReceived < 0 ||
                context.CategoryRowsReceived < 0 ||
                context.SupplierRowsReceived < 0 ||
                context.TombstonesReceived < 0)
            {
                return "catalog_run_evidence_invalid";
            }

            if (evidence.InvalidProductRows > 0) return "catalog_product_row_invalid";
            if (evidence.InvalidCategoryRows > 0) return "catalog_category_row_invalid";
            if (evidence.InvalidSupplierRows > 0) return "catalog_supplier_row_invalid";
            if (evidence.InvalidPriceRows > 0) return "catalog_price_row_invalid";
            if (evidence.InvalidProductTombstoneRows > 0) return "catalog_product_tombstone_invalid";
            if (evidence.InvalidCategoryTombstoneRows > 0) return "catalog_category_tombstone_invalid";
            if (evidence.InvalidSupplierTombstoneRows > 0) return "catalog_supplier_tombstone_invalid";
            if (evidence.InvalidProducts > 0) return "catalog_invalid_product_ids";
            if (evidence.InvalidCategories > 0) return "catalog_invalid_category_ids";
            if (evidence.InvalidSuppliers > 0) return "catalog_invalid_supplier_ids";
            var conflictCode = evidence.GetConflictCode();
            if (conflictCode.Length > 0) return conflictCode;
            if (evidence.DuplicateProducts > 0) return "catalog_duplicate_product_ids";
            if (evidence.DuplicatePrices > 0) return "catalog_duplicate_price_rows";
            if (context.InvalidPriceRows > 0) return "catalog_invalid_price_rows";

            if (requireAppliedEvidence)
            {
                if (context.ProductRowsReceived != evidence.ReceivedProducts)
                    return "catalog_product_row_evidence_mismatch";
                if (context.CategoryRowsReceived != evidence.ReceivedCategories)
                    return "catalog_category_row_evidence_mismatch";
                if (context.SupplierRowsReceived != evidence.ReceivedSuppliers)
                    return "catalog_supplier_row_evidence_mismatch";
                if (context.PriceRowsReceived != evidence.ReceivedPrices)
                    return "catalog_price_row_evidence_mismatch";
                if (context.PriceRowsAccepted != evidence.ReceivedPrices)
                    return "catalog_prices_not_fully_applied";
                if (context.TombstonesReceived != evidence.ReceivedTombstones)
                    return "catalog_tombstone_row_evidence_mismatch";
            }

            if (summary.Products != evidence.DistinctProducts)
                return "catalog_products_count_mismatch";
            if (summary.ActiveProducts.GetValueOrDefault() <= 0)
                return "full_refresh_no_active_products";
            if (summary.ActiveProducts != evidence.DistinctProducts)
                return "catalog_active_products_count_mismatch";
            if (summary.Categories != evidence.DistinctCategories)
                return "catalog_categories_count_mismatch";
            if (summary.Suppliers != evidence.DistinctSuppliers)
                return "catalog_suppliers_count_mismatch";
            if (summary.Prices != evidence.ReceivedPrices)
                return "catalog_prices_count_mismatch";
            return string.Empty;
        }

        private static bool IsSha256HexValue(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return normalized.Length == 64 && normalized.All(Uri.IsHexDigit);
        }

        private static CatalogExactnessResult CreateStageRejection(
            string code,
            PosCatalogSummaryResponse summary,
            CatalogFullRefreshResult audit,
            CatalogExactnessRunContext context)
        {
            return new CatalogExactnessResult
            {
                Audit = audit,
                Code = code ?? string.Empty,
                Context = context,
                EvaluatedAt = DateTimeOffset.UtcNow.ToString("O"),
                Expected = summary,
                RepairRequired = true,
                Status = CatalogCompletenessStatus.Mismatch
            };
        }

        private static async Task<CatalogFullRefreshResult> ReconcilePreparedAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string removedAt,
            CatalogAuthoritativeCounts counts)
        {
            var deactivatedProducts = await conn.ExecuteAsync(@"
UPDATE products
SET is_active = 0,
    remote_deleted_at = @removedAt
WHERE TRIM(COALESCE(remote_product_id, '')) <> ''
  AND COALESCE(is_active, 1) = 1
  AND NOT EXISTS (
    SELECT 1 FROM temp_full_product_ids incoming
    WHERE incoming.id = TRIM(products.remote_product_id)
  );",
                new { removedAt },
                tx).ConfigureAwait(false);
            var deactivatedCategories = await conn.ExecuteAsync(@"
UPDATE categories
SET is_active = 0,
    remote_deleted_at = @removedAt,
    remote_updated_at = @removedAt
WHERE TRIM(COALESCE(remote_category_id, '')) <> ''
  AND COALESCE(is_active, 1) = 1
  AND NOT EXISTS (
    SELECT 1 FROM temp_full_category_ids incoming
    WHERE incoming.id = TRIM(categories.remote_category_id)
  );",
                new { removedAt },
                tx).ConfigureAwait(false);
            var deactivatedSuppliers = await conn.ExecuteAsync(@"
UPDATE suppliers
SET is_active = 0,
    remote_deleted_at = @removedAt,
    remote_updated_at = @removedAt
WHERE TRIM(COALESCE(remote_supplier_id, '')) <> ''
  AND COALESCE(is_active, 1) = 1
  AND NOT EXISTS (
    SELECT 1 FROM temp_full_supplier_ids incoming
    WHERE incoming.id = TRIM(suppliers.remote_supplier_id)
  );",
                new { removedAt },
                tx).ConfigureAwait(false);
            await conn.ExecuteAsync(@"
DELETE FROM remote_catalog_pending_prices
WHERE NOT EXISTS (
  SELECT 1 FROM temp_full_product_ids incoming
  WHERE incoming.id = TRIM(remote_catalog_pending_prices.remote_product_id)
);",
                transaction: tx).ConfigureAwait(false);
            await conn.ExecuteAsync(@"
DELETE FROM remote_catalog_product_references
WHERE NOT EXISTS (
  SELECT 1 FROM temp_full_product_ids incoming
  WHERE incoming.id = TRIM(remote_catalog_product_references.remote_product_id)
);",
                transaction: tx).ConfigureAwait(false);

            var result = await LoadAuditAsync(conn, tx).ConfigureAwait(false);
            result.DeactivatedCategories = deactivatedCategories;
            result.DeactivatedProducts = deactivatedProducts;
            result.DeactivatedSuppliers = deactivatedSuppliers;
            result.ReceivedProductIds = counts.ReceivedProducts;
            result.DistinctAuthoritativeProductIds = counts.DistinctProducts;
            result.DuplicateAuthoritativeProductIds = counts.DuplicateProducts;
            result.InvalidAuthoritativeProductIds = counts.InvalidProducts;
            result.ReceivedCategoryIds = counts.ReceivedCategories;
            result.DistinctAuthoritativeCategoryIds = counts.DistinctCategories;
            result.DuplicateAuthoritativeCategoryIds = counts.DuplicateCategories;
            result.InvalidAuthoritativeCategoryIds = counts.InvalidCategories;
            result.ReceivedSupplierIds = counts.ReceivedSuppliers;
            result.DistinctAuthoritativeSupplierIds = counts.DistinctSuppliers;
            result.DuplicateAuthoritativeSupplierIds = counts.DuplicateSuppliers;
            result.InvalidAuthoritativeSupplierIds = counts.InvalidSuppliers;
            return result;
        }

        private static void ApplyStageEvidence(
            CatalogFullRefreshResult audit,
            CatalogStagedEvidence evidence)
        {
            var counts = evidence.ToAuthoritativeCounts();
            audit.ReceivedProductIds = counts.ReceivedProducts;
            audit.DistinctAuthoritativeProductIds = counts.DistinctProducts;
            audit.DuplicateAuthoritativeProductIds = counts.DuplicateProducts;
            audit.InvalidAuthoritativeProductIds = counts.InvalidProducts;
            audit.ReceivedCategoryIds = counts.ReceivedCategories;
            audit.DistinctAuthoritativeCategoryIds = counts.DistinctCategories;
            audit.DuplicateAuthoritativeCategoryIds = counts.DuplicateCategories;
            audit.InvalidAuthoritativeCategoryIds = counts.InvalidCategories;
            audit.ReceivedSupplierIds = counts.ReceivedSuppliers;
            audit.DistinctAuthoritativeSupplierIds = counts.DistinctSuppliers;
            audit.DuplicateAuthoritativeSupplierIds = counts.DuplicateSuppliers;
            audit.InvalidAuthoritativeSupplierIds = counts.InvalidSuppliers;
        }

        private static Task<CatalogFullRefreshResult> LoadAuditAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx)
        {
            return conn.QuerySingleAsync<CatalogFullRefreshResult>(@"
SELECT
  (SELECT COUNT(1)
   FROM products
   WHERE TRIM(COALESCE(remote_product_id, '')) <> ''
     AND COALESCE(is_active, 1) = 1) AS ActiveRemoteProducts,
  (SELECT COUNT(DISTINCT TRIM(remote_product_id))
   FROM products
   WHERE TRIM(COALESCE(remote_product_id, '')) <> ''
     AND COALESCE(is_active, 1) = 1) AS DistinctActiveRemoteProductIds,
  (SELECT COUNT(1)
   FROM (
     SELECT TRIM(remote_product_id)
     FROM products
     WHERE TRIM(COALESCE(remote_product_id, '')) <> ''
       AND COALESCE(is_active, 1) = 1
     GROUP BY TRIM(remote_product_id)
     HAVING COUNT(1) > 1
   )) AS DuplicateActiveRemoteProductIds,
  (SELECT COUNT(1)
   FROM (
     SELECT UPPER(TRIM(barcode))
     FROM products
     WHERE COALESCE(is_active, 1) = 1
     GROUP BY UPPER(TRIM(barcode))
     HAVING COUNT(1) > 1
   )) AS DuplicateActiveBarcodes,
  (SELECT COUNT(1)
   FROM products p
   LEFT JOIN product_meta m ON m.barcode = p.barcode
   WHERE TRIM(COALESCE(p.remote_product_id, '')) <> ''
     AND COALESCE(p.is_active, 1) = 1
     AND m.barcode IS NULL) AS RemoteProductsWithoutMeta,
  (SELECT COUNT(1)
   FROM products
   WHERE TRIM(COALESCE(remote_product_id, '')) <> ''
     AND COALESCE(is_active, 1) = 1
     AND (TRIM(COALESCE(barcode, '')) = ''
       OR TRIM(COALESCE(name, '')) = ''
       OR COALESCE(unitPrice, 0) <= 0)) AS InvalidActiveRemoteProducts,
  (SELECT COUNT(1)
   FROM categories
   WHERE TRIM(COALESCE(remote_category_id, '')) <> ''
     AND COALESCE(is_active, 1) = 1) AS ActiveRemoteCategories,
  (SELECT COUNT(DISTINCT TRIM(remote_category_id))
   FROM categories
   WHERE TRIM(COALESCE(remote_category_id, '')) <> ''
     AND COALESCE(is_active, 1) = 1) AS DistinctActiveRemoteCategoryIds,
  (SELECT COUNT(1)
   FROM (
     SELECT TRIM(remote_category_id)
     FROM categories
     WHERE TRIM(COALESCE(remote_category_id, '')) <> ''
       AND COALESCE(is_active, 1) = 1
     GROUP BY TRIM(remote_category_id)
     HAVING COUNT(1) > 1
   )) AS DuplicateActiveRemoteCategoryIds,
  (SELECT COUNT(1)
   FROM suppliers
   WHERE TRIM(COALESCE(remote_supplier_id, '')) <> ''
     AND COALESCE(is_active, 1) = 1) AS ActiveRemoteSuppliers,
  (SELECT COUNT(DISTINCT TRIM(remote_supplier_id))
   FROM suppliers
   WHERE TRIM(COALESCE(remote_supplier_id, '')) <> ''
     AND COALESCE(is_active, 1) = 1) AS DistinctActiveRemoteSupplierIds,
  (SELECT COUNT(1)
   FROM (
     SELECT TRIM(remote_supplier_id)
     FROM suppliers
     WHERE TRIM(COALESCE(remote_supplier_id, '')) <> ''
       AND COALESCE(is_active, 1) = 1
     GROUP BY TRIM(remote_supplier_id)
     HAVING COUNT(1) > 1
   )) AS DuplicateActiveRemoteSupplierIds,
  (SELECT COUNT(1)
   FROM product_meta m
   LEFT JOIN categories c ON c.id = m.category_id
   WHERE m.category_id IS NOT NULL
      AND c.id IS NULL) AS OrphanCategoryReferences,
  (SELECT COUNT(1)
   FROM product_meta m
   LEFT JOIN suppliers s ON s.id = m.supplier_id
   WHERE m.supplier_id IS NOT NULL
      AND s.id IS NULL) AS OrphanSupplierReferences,
  (SELECT COUNT(1)
   FROM products p
   LEFT JOIN remote_catalog_product_references r
     ON r.remote_product_id = TRIM(p.remote_product_id)
   WHERE TRIM(COALESCE(p.remote_product_id, '')) <> ''
     AND COALESCE(p.is_active, 1) = 1
     AND r.remote_product_id IS NULL) AS RemoteProductsWithoutReferenceMap,
  (SELECT COUNT(1)
   FROM remote_catalog_product_references r
   LEFT JOIN products p
     ON p.remote_product_id = TRIM(r.remote_product_id)
   WHERE p.id IS NULL) AS ReferenceMapsWithoutProduct,
  (SELECT COUNT(1)
   FROM products p
   JOIN product_meta m ON m.barcode = p.barcode
   JOIN remote_catalog_product_references r
     ON r.remote_product_id = TRIM(p.remote_product_id)
   LEFT JOIN categories c
     ON c.remote_category_id = TRIM(r.remote_category_id)
    AND COALESCE(c.is_active, 1) = 1
   WHERE TRIM(COALESCE(p.remote_product_id, '')) <> ''
     AND COALESCE(p.is_active, 1) = 1
     AND (
       (TRIM(COALESCE(r.remote_category_id, '')) = '' AND m.category_id IS NOT NULL)
       OR
       (TRIM(COALESCE(r.remote_category_id, '')) <> '' AND
        (c.id IS NULL OR m.category_id IS NULL OR m.category_id <> c.id))
     )) AS InvalidCategoryReferenceMappings,
  (SELECT COUNT(1)
   FROM products p
   JOIN product_meta m ON m.barcode = p.barcode
   JOIN remote_catalog_product_references r
     ON r.remote_product_id = TRIM(p.remote_product_id)
   LEFT JOIN suppliers s
     ON s.remote_supplier_id = TRIM(r.remote_supplier_id)
    AND COALESCE(s.is_active, 1) = 1
   WHERE TRIM(COALESCE(p.remote_product_id, '')) <> ''
     AND COALESCE(p.is_active, 1) = 1
     AND (
       (TRIM(COALESCE(r.remote_supplier_id, '')) = '' AND m.supplier_id IS NOT NULL)
       OR
       (TRIM(COALESCE(r.remote_supplier_id, '')) <> '' AND
        (s.id IS NULL OR m.supplier_id IS NULL OR m.supplier_id <> s.id))
     )) AS InvalidSupplierReferenceMappings,
  (SELECT COUNT(1)
   FROM product_meta m
   JOIN products p ON p.barcode = m.barcode
   JOIN categories c ON c.id = m.category_id
   WHERE TRIM(COALESCE(p.remote_product_id, '')) <> ''
     AND COALESCE(p.is_active, 1) = 1
     AND COALESCE(c.is_active, 1) = 0) AS InactiveCategoryReferences,
  (SELECT COUNT(1)
   FROM product_meta m
   JOIN products p ON p.barcode = m.barcode
   JOIN suppliers s ON s.id = m.supplier_id
   WHERE TRIM(COALESCE(p.remote_product_id, '')) <> ''
     AND COALESCE(p.is_active, 1) = 1
     AND COALESCE(s.is_active, 1) = 0) AS InactiveSupplierReferences,
  (SELECT COUNT(1) FROM remote_catalog_pending_prices) AS PendingRemotePrices,
  (SELECT COUNT(1)
   FROM product_price_history
   WHERE TRIM(COALESCE(remote_price_id, '')) <> '') AS RemotePriceHistoryRows,
  (SELECT COUNT(1)
   FROM products
   WHERE TRIM(COALESCE(remote_product_id, '')) <> ''
     AND COALESCE(is_active, 1) = 0) AS InactiveRemoteProducts,
  (SELECT COUNT(1)
   FROM categories
   WHERE TRIM(COALESCE(remote_category_id, '')) <> ''
     AND COALESCE(is_active, 1) = 0) AS InactiveRemoteCategories,
  (SELECT COUNT(1)
   FROM suppliers
   WHERE TRIM(COALESCE(remote_supplier_id, '')) <> ''
     AND COALESCE(is_active, 1) = 0) AS InactiveRemoteSuppliers,
  (SELECT COUNT(1)
   FROM products p
   WHERE TRIM(COALESCE(p.remote_product_id, '')) <> ''
     AND COALESCE(p.is_active, 1) = 1
     AND NOT EXISTS (
       SELECT 1 FROM temp_full_product_ids incoming
       WHERE incoming.id = TRIM(p.remote_product_id)
     )) AS NonAuthoritativeActiveProducts,
  (SELECT COUNT(1)
   FROM categories c
   WHERE TRIM(COALESCE(c.remote_category_id, '')) <> ''
     AND COALESCE(c.is_active, 1) = 1
     AND NOT EXISTS (
       SELECT 1 FROM temp_full_category_ids incoming
       WHERE incoming.id = TRIM(c.remote_category_id)
     )) AS NonAuthoritativeActiveCategories,
  (SELECT COUNT(1)
   FROM suppliers s
   WHERE TRIM(COALESCE(s.remote_supplier_id, '')) <> ''
     AND COALESCE(s.is_active, 1) = 1
     AND NOT EXISTS (
       SELECT 1 FROM temp_full_supplier_ids incoming
       WHERE incoming.id = TRIM(s.remote_supplier_id)
     )) AS NonAuthoritativeActiveSuppliers;",
                transaction: tx);
        }

        private static async Task CreateAndFillAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string table,
            IReadOnlyList<string> values)
        {
            await conn.ExecuteAsync(
                "CREATE TEMP TABLE IF NOT EXISTS " + table + " (id TEXT PRIMARY KEY); DELETE FROM " + table + ";",
                transaction: tx).ConfigureAwait(false);
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO " + table + "(id) VALUES(@value);";
            var parameter = insert.CreateParameter();
            parameter.ParameterName = "@value";
            insert.Parameters.Add(parameter);
            insert.Prepare();
            foreach (var value in values)
            {
                parameter.Value = value;
                await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        private sealed class StageScope
        {
            public string FullRunId { get; private set; } = string.Empty;
            public string GenerationFingerprint { get; private set; } = string.Empty;
            public string GenerationId { get; private set; } = string.Empty;
            public string ShopCode { get; private set; } = string.Empty;
            public string ShopId { get; private set; } = string.Empty;
            public long TransitionEpoch { get; private set; }

            public static StageScope Create(
                string fullRunId,
                RemoteCatalogCommitFence commitFence)
            {
                return new StageScope
                {
                    FullRunId = (fullRunId ?? string.Empty).Trim(),
                    GenerationFingerprint = (commitFence.GenerationFingerprint ?? string.Empty).Trim(),
                    GenerationId = (commitFence.GenerationId ?? string.Empty).Trim(),
                    ShopCode = (commitFence.ShopCode ?? string.Empty).Trim(),
                    ShopId = (commitFence.ShopId ?? string.Empty).Trim(),
                    TransitionEpoch = commitFence.ExpectedEpoch
                };
            }
        }

        private sealed class CatalogAuthoritativeCounts
        {
            public long DistinctCategories { get; set; }
            public long DistinctProducts { get; set; }
            public long DistinctSuppliers { get; set; }
            public long DuplicateCategories { get; set; }
            public long DuplicateProducts { get; set; }
            public long DuplicateSuppliers { get; set; }
            public long InvalidCategories { get; set; }
            public long InvalidProducts { get; set; }
            public long InvalidSuppliers { get; set; }
            public long ReceivedCategories { get; set; }
            public long ReceivedProducts { get; set; }
            public long ReceivedSuppliers { get; set; }
        }

        private sealed class CatalogStagedEvidence
        {
            public long CategoryActiveTombstoneConflicts { get; set; }
            public long CategoryConflicts { get; set; }
            public long CategoryTombstoneConflicts { get; set; }
            public long ContinuationPages { get; set; }
            public long DistinctCategories { get; set; }
            public long DistinctProducts { get; set; }
            public long DistinctSuppliers { get; set; }
            public long DuplicatePrices { get; set; }
            public long DuplicateProducts { get; set; }
            public long FirstPage { get; set; }
            public long InvalidCategories { get; set; }
            public long InvalidCategoryRows { get; set; }
            public long InvalidCategoryTombstoneRows { get; set; }
            public long InvalidPriceRows { get; set; }
            public long InvalidProducts { get; set; }
            public long InvalidProductRows { get; set; }
            public long InvalidProductTombstoneRows { get; set; }
            public long InvalidSuppliers { get; set; }
            public long InvalidSupplierRows { get; set; }
            public long InvalidSupplierTombstoneRows { get; set; }
            public long LastPage { get; set; }
            public long OrphanPageRows { get; set; }
            public long OrphanCategoryReferences { get; set; }
            public long OrphanPriceProductReferences { get; set; }
            public long OrphanSupplierReferences { get; set; }
            public long PageMarkers { get; set; }
            public long ProductActiveTombstoneConflicts { get; set; }
            public long ProductIdentityConflicts { get; set; }
            public long ProductTombstoneConflicts { get; set; }
            public long ReceivedCategories { get; set; }
            public long ReceivedPrices { get; set; }
            public long ReceivedProducts { get; set; }
            public long ReceivedSuppliers { get; set; }
            public long ReceivedTombstones { get; set; }
            public long SupplierActiveTombstoneConflicts { get; set; }
            public long SupplierConflicts { get; set; }
            public long SupplierTombstoneConflicts { get; set; }
            public long TerminalPageNumber { get; set; }
            public long TerminalPages { get; set; }

            public string GetConflictCode()
            {
                if (ProductIdentityConflicts > 0)
                    return "catalog_duplicate_active_barcodes";
                if (OrphanCategoryReferences > 0)
                    return "catalog_category_references_orphaned";
                if (OrphanSupplierReferences > 0)
                    return "catalog_supplier_references_orphaned";
                if (OrphanPriceProductReferences > 0)
                    return "catalog_price_product_not_authoritative";
                if (CategoryConflicts > 0)
                    return CatalogFullLaneEvidenceTracker.CategoryConflictCode;
                if (SupplierConflicts > 0)
                    return CatalogFullLaneEvidenceTracker.SupplierConflictCode;
                if (ProductTombstoneConflicts > 0)
                    return CatalogFullLaneEvidenceTracker.ProductTombstoneConflictCode;
                if (CategoryTombstoneConflicts > 0)
                    return CatalogFullLaneEvidenceTracker.CategoryTombstoneConflictCode;
                if (SupplierTombstoneConflicts > 0)
                    return CatalogFullLaneEvidenceTracker.SupplierTombstoneConflictCode;
                if (ProductActiveTombstoneConflicts > 0)
                    return CatalogFullLaneEvidenceTracker.ProductActiveTombstoneConflictCode;
                if (CategoryActiveTombstoneConflicts > 0)
                    return CatalogFullLaneEvidenceTracker.CategoryActiveTombstoneConflictCode;
                if (SupplierActiveTombstoneConflicts > 0)
                    return CatalogFullLaneEvidenceTracker.SupplierActiveTombstoneConflictCode;
                return string.Empty;
            }

            public CatalogAuthoritativeCounts ToAuthoritativeCounts()
            {
                return new CatalogAuthoritativeCounts
                {
                    ReceivedProducts = ReceivedProducts,
                    DistinctProducts = DistinctProducts,
                    DuplicateProducts = DuplicateProducts,
                    InvalidProducts = InvalidProducts,
                    ReceivedCategories = ReceivedCategories,
                    DistinctCategories = DistinctCategories,
                    InvalidCategories = InvalidCategories,
                    ReceivedSuppliers = ReceivedSuppliers,
                    DistinctSuppliers = DistinctSuppliers,
                    InvalidSuppliers = InvalidSuppliers
                };
            }
        }

        private sealed class CatalogIdSet
        {
            private CatalogIdSet()
            {
            }

            public long DuplicateCount { get; private set; }
            public long InvalidCount { get; private set; }
            public long ReceivedCount { get; private set; }
            public IReadOnlyList<string> Values { get; private set; } = Array.Empty<string>();

            public static CatalogIdSet Create(IEnumerable<string> values)
            {
                var received = 0L;
                var invalid = 0L;
                var duplicate = 0L;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var normalized = new List<string>();
                foreach (var value in values ?? Enumerable.Empty<string>())
                {
                    received += 1;
                    var id = (value ?? string.Empty).Trim();
                    if (id.Length == 0)
                    {
                        invalid += 1;
                    }
                    else if (!seen.Add(id))
                    {
                        duplicate += 1;
                    }
                    else
                    {
                        normalized.Add(id);
                    }
                }

                return new CatalogIdSet
                {
                    DuplicateCount = duplicate,
                    InvalidCount = invalid,
                    ReceivedCount = received,
                    Values = normalized
                };
            }
        }
    }

    public enum CatalogCompletenessStatus
    {
        Unverified = 0,
        Verified = 1,
        Mismatch = 2
    }

    public sealed class CatalogExactnessRunContext
    {
        public string ActualChecksum { get; set; } = string.Empty;
        public string ActualChecksumAlgorithm { get; set; } = string.Empty;
        public string CatalogVersion { get; set; } = string.Empty;
        public long DurationMilliseconds { get; set; }
        public bool HasMore { get; set; }
        public int Pages { get; set; }
        public long DuplicatePriceRows { get; set; }
        public long InvalidPriceRows { get; set; }
        public long? PriceRowsAccepted { get; set; }
        public long? PriceRowsReceived { get; set; }
        public long? ProductRowsReceived { get; set; }
        public long? CategoryRowsReceived { get; set; }
        public long? SupplierRowsReceived { get; set; }
        public string SyncCursor { get; set; } = string.Empty;
        public string SyncMode { get; set; } = string.Empty;
        public long TombstonesReceived { get; set; }
    }

    public sealed class CatalogExactnessResult
    {
        public CatalogFullRefreshResult Audit { get; internal set; }
        public string Code { get; internal set; } = string.Empty;
        public CatalogExactnessRunContext Context { get; internal set; }
        public string EvaluatedAt { get; internal set; } = string.Empty;
        public PosCatalogSummaryResponse Expected { get; internal set; }
        public bool RepairRequired { get; internal set; }
        public CatalogCompletenessStatus Status { get; internal set; }

        public long CategoriesReceived => Context?.CategoryRowsReceived ?? Audit?.ReceivedCategoryIds ?? 0;
        public long PricesReceived => Context?.PriceRowsReceived ?? 0;
        public long ProductsReceived => Context?.ProductRowsReceived ?? Audit?.ReceivedProductIds ?? 0;
        public long SuppliersReceived => Context?.SupplierRowsReceived ?? Audit?.ReceivedSupplierIds ?? 0;

        public double RowsPerSecond
        {
            get
            {
                var duration = Context?.DurationMilliseconds ?? 0;
                if (duration <= 0)
                {
                    return 0;
                }

                var rows = ProductsReceived + CategoriesReceived + SuppliersReceived + PricesReceived;
                return rows * 1000d / duration;
            }
        }
    }

    public static class CatalogExactnessVerifier
    {
        public static CatalogExactnessResult Evaluate(
            PosCatalogSummaryResponse summary,
            CatalogFullRefreshResult audit,
            CatalogExactnessRunContext context)
        {
            var run = context ?? new CatalogExactnessRunContext();
            var result = new CatalogExactnessResult
            {
                Audit = audit,
                Context = run,
                EvaluatedAt = DateTimeOffset.UtcNow.ToString("O"),
                Expected = summary,
                Status = CatalogCompletenessStatus.Unverified
            };

            if (audit == null)
            {
                return Set(result, CatalogCompletenessStatus.Unverified, "catalog_audit_missing", true);
            }

            var summaryError = PosOnlineCompatibilityValidator.ValidateCatalogSummary(summary);
            if (!string.IsNullOrWhiteSpace(summaryError))
            {
                return Set(result, CatalogCompletenessStatus.Mismatch, summaryError, true);
            }

            if (!string.Equals(run.SyncMode, "full_refresh", StringComparison.OrdinalIgnoreCase))
            {
                return Set(result, CatalogCompletenessStatus.Unverified, "catalog_exactness_requires_full_refresh", false);
            }

            if (run.HasMore)
            {
                return Set(result, CatalogCompletenessStatus.Unverified, "catalog_has_more_not_drained", true);
            }

            if (run.Pages <= 0)
            {
                return Set(result, CatalogCompletenessStatus.Unverified, "catalog_page_evidence_missing", true);
            }

            if (run.DurationMilliseconds < 0 ||
                run.TombstonesReceived < 0 ||
                run.DuplicatePriceRows < 0 ||
                run.InvalidPriceRows < 0 ||
                IsNegative(run.PriceRowsAccepted) ||
                IsNegative(run.ProductRowsReceived) ||
                IsNegative(run.CategoryRowsReceived) ||
                IsNegative(run.SupplierRowsReceived) ||
                IsNegative(run.PriceRowsReceived))
            {
                return Set(result, CatalogCompletenessStatus.Mismatch, "catalog_run_evidence_invalid", true);
            }

            if (run.InvalidPriceRows > 0)
            {
                return Set(result, CatalogCompletenessStatus.Mismatch, "catalog_invalid_price_rows", true);
            }

            if (run.DuplicatePriceRows > 0)
            {
                return Set(result, CatalogCompletenessStatus.Mismatch, "catalog_duplicate_price_rows", true);
            }

            var invariantError = FindInvariantError(audit);
            if (invariantError.Length > 0)
            {
                return Set(result, CatalogCompletenessStatus.Mismatch, invariantError, true);
            }

            // Reconciliation owns the authoritative identity sets even when an older
            // server omits catalogSummary. Those sets must still map one-for-one to the
            // active local mirror; otherwise Unverified could hide a collapsed row.
            if (audit.DistinctAuthoritativeProductIds != audit.DistinctActiveRemoteProductIds)
            {
                return Set(result, CatalogCompletenessStatus.Mismatch, "catalog_authoritative_products_not_applied", true);
            }

            if (audit.DistinctAuthoritativeCategoryIds != audit.DistinctActiveRemoteCategoryIds)
            {
                return Set(result, CatalogCompletenessStatus.Mismatch, "catalog_authoritative_categories_not_applied", true);
            }

            if (audit.DistinctAuthoritativeSupplierIds != audit.DistinctActiveRemoteSupplierIds)
            {
                return Set(result, CatalogCompletenessStatus.Mismatch, "catalog_authoritative_suppliers_not_applied", true);
            }

            if (summary == null)
            {
                return Set(result, CatalogCompletenessStatus.Unverified, "catalog_summary_missing", false);
            }

            if (!HasAllCounts(summary))
            {
                return Set(result, CatalogCompletenessStatus.Unverified, "catalog_summary_incomplete", false);
            }

            if (run.ProductRowsReceived.HasValue &&
                run.ProductRowsReceived.Value != audit.ReceivedProductIds)
            {
                return Set(result, CatalogCompletenessStatus.Mismatch, "catalog_product_row_evidence_mismatch", true);
            }

            if (run.CategoryRowsReceived.HasValue &&
                run.CategoryRowsReceived.Value != audit.ReceivedCategoryIds)
            {
                return Set(result, CatalogCompletenessStatus.Mismatch, "catalog_category_row_evidence_mismatch", true);
            }

            if (run.SupplierRowsReceived.HasValue &&
                run.SupplierRowsReceived.Value != audit.ReceivedSupplierIds)
            {
                return Set(result, CatalogCompletenessStatus.Mismatch, "catalog_supplier_row_evidence_mismatch", true);
            }

            if (string.IsNullOrWhiteSpace(run.CatalogVersion))
            {
                return Set(result, CatalogCompletenessStatus.Unverified, "catalog_version_missing", false);
            }

            if (string.IsNullOrWhiteSpace(run.SyncCursor))
            {
                return Set(result, CatalogCompletenessStatus.Unverified, "catalog_cursor_missing", false);
            }

            if (summary.Products.Value != audit.DistinctAuthoritativeProductIds)
            {
                return Set(result, CatalogCompletenessStatus.Mismatch, "catalog_products_count_mismatch", true);
            }

            if (summary.ActiveProducts.Value != audit.ActiveRemoteProducts)
            {
                return Set(result, CatalogCompletenessStatus.Mismatch, "catalog_active_products_count_mismatch", true);
            }

            if (summary.Categories.Value != audit.DistinctAuthoritativeCategoryIds ||
                summary.Categories.Value != audit.ActiveRemoteCategories)
            {
                return Set(result, CatalogCompletenessStatus.Mismatch, "catalog_categories_count_mismatch", true);
            }

            if (summary.Suppliers.Value != audit.DistinctAuthoritativeSupplierIds ||
                summary.Suppliers.Value != audit.ActiveRemoteSuppliers)
            {
                return Set(result, CatalogCompletenessStatus.Mismatch, "catalog_suppliers_count_mismatch", true);
            }

            if (!run.PriceRowsReceived.HasValue)
            {
                return Set(result, CatalogCompletenessStatus.Unverified, "catalog_prices_evidence_missing", false);
            }

            if (!run.PriceRowsAccepted.HasValue)
            {
                return Set(result, CatalogCompletenessStatus.Unverified, "catalog_prices_applied_evidence_missing", false);
            }

            if (run.PriceRowsAccepted.Value != run.PriceRowsReceived.Value)
            {
                return Set(result, CatalogCompletenessStatus.Mismatch, "catalog_prices_not_fully_applied", true);
            }

            if (summary.Prices.Value != run.PriceRowsReceived.Value)
            {
                return Set(result, CatalogCompletenessStatus.Mismatch, "catalog_prices_count_mismatch", true);
            }

            var expectedChecksum = Normalize(summary.Checksum);
            if (expectedChecksum.Length > 0)
            {
                var expectedAlgorithm = Normalize(summary.ChecksumAlgorithm);
                if (!string.Equals(expectedAlgorithm, "sha256", StringComparison.OrdinalIgnoreCase))
                {
                    return Set(result, CatalogCompletenessStatus.Unverified, "catalog_checksum_algorithm_unsupported", false);
                }

                if (!IsSha256Hex(expectedChecksum))
                {
                    return Set(result, CatalogCompletenessStatus.Mismatch, "catalog_summary_checksum_invalid", true);
                }

                var actualChecksum = Normalize(run.ActualChecksum);
                if (actualChecksum.Length == 0)
                {
                    return Set(result, CatalogCompletenessStatus.Unverified, "catalog_checksum_unverifiable", false);
                }

                var actualAlgorithm = Normalize(run.ActualChecksumAlgorithm);
                if (!string.Equals(actualAlgorithm, "sha256", StringComparison.OrdinalIgnoreCase) ||
                    !IsSha256Hex(actualChecksum))
                {
                    return Set(result, CatalogCompletenessStatus.Unverified, "catalog_checksum_algorithm_unsupported", false);
                }

                if (!string.Equals(expectedChecksum, actualChecksum, StringComparison.OrdinalIgnoreCase))
                {
                    return Set(result, CatalogCompletenessStatus.Mismatch, "catalog_checksum_mismatch", true);
                }
            }

            return Set(result, CatalogCompletenessStatus.Verified, "catalog_exactness_verified", false);
        }

        public static string FindInvariantError(CatalogFullRefreshResult audit)
        {
            if (audit == null) return "catalog_audit_missing";
            if (audit.InvalidAuthoritativeProductIds > 0) return "catalog_invalid_product_ids";
            if (audit.InvalidAuthoritativeCategoryIds > 0) return "catalog_invalid_category_ids";
            if (audit.InvalidAuthoritativeSupplierIds > 0) return "catalog_invalid_supplier_ids";
            if (audit.DuplicateAuthoritativeProductIds > 0) return "catalog_duplicate_product_ids";
            if (audit.DuplicateAuthoritativeCategoryIds > 0) return "catalog_duplicate_category_ids";
            if (audit.DuplicateAuthoritativeSupplierIds > 0) return "catalog_duplicate_supplier_ids";
            if (audit.DuplicateActiveRemoteProductIds > 0) return "catalog_duplicate_local_product_ids";
            if (audit.DuplicateActiveRemoteCategoryIds > 0) return "catalog_duplicate_local_category_ids";
            if (audit.DuplicateActiveRemoteSupplierIds > 0) return "catalog_duplicate_local_supplier_ids";
            if (audit.DuplicateActiveBarcodes > 0) return "catalog_duplicate_active_barcodes";
            if (audit.RemoteProductsWithoutMeta > 0) return "catalog_product_meta_missing";
            if (audit.RemoteProductsWithoutReferenceMap > 0) return "catalog_product_reference_map_missing";
            if (audit.ReferenceMapsWithoutProduct > 0) return "catalog_product_reference_map_orphaned";
            if (audit.InvalidCategoryReferenceMappings > 0) return "catalog_category_reference_mapping_invalid";
            if (audit.InvalidSupplierReferenceMappings > 0) return "catalog_supplier_reference_mapping_invalid";
            if (audit.InvalidActiveRemoteProducts > 0) return "catalog_active_products_invalid";
            if (audit.OrphanCategoryReferences > 0) return "catalog_category_references_orphaned";
            if (audit.OrphanSupplierReferences > 0) return "catalog_supplier_references_orphaned";
            if (audit.InactiveCategoryReferences > 0) return "catalog_category_references_inactive";
            if (audit.InactiveSupplierReferences > 0) return "catalog_supplier_references_inactive";
            if (audit.NonAuthoritativeActiveProducts > 0) return "catalog_non_authoritative_products_active";
            if (audit.NonAuthoritativeActiveCategories > 0) return "catalog_non_authoritative_categories_active";
            if (audit.NonAuthoritativeActiveSuppliers > 0) return "catalog_non_authoritative_suppliers_active";
            if (audit.PendingRemotePrices > 0) return "catalog_pending_prices_not_drained";
            return string.Empty;
        }

        private static bool HasAllCounts(PosCatalogSummaryResponse summary)
        {
            return summary.Products.HasValue &&
                summary.ActiveProducts.HasValue &&
                summary.Categories.HasValue &&
                summary.Suppliers.HasValue &&
                summary.Prices.HasValue;
        }

        private static bool IsNegative(long? value)
        {
            return value.HasValue && value.Value < 0;
        }

        private static bool IsSha256Hex(string value)
        {
            var normalized = Normalize(value);
            return normalized.Length == 64 && normalized.All(Uri.IsHexDigit);
        }

        private static CatalogExactnessResult Set(
            CatalogExactnessResult result,
            CatalogCompletenessStatus status,
            string code,
            bool repairRequired)
        {
            result.Status = status;
            result.Code = code ?? string.Empty;
            result.RepairRequired = repairRequired || status == CatalogCompletenessStatus.Mismatch;
            return result;
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim();
        }
    }

    public sealed class CatalogFullRefreshResult
    {
        public long ActiveRemoteCategories { get; set; }
        public long ActiveRemoteProducts { get; set; }
        public long ActiveRemoteSuppliers { get; set; }
        public int DeactivatedCategories { get; set; }
        public int DeactivatedProducts { get; set; }
        public int DeactivatedSuppliers { get; set; }
        public long DistinctActiveRemoteCategoryIds { get; set; }
        public long DistinctActiveRemoteProductIds { get; set; }
        public long DistinctActiveRemoteSupplierIds { get; set; }
        public long DistinctAuthoritativeCategoryIds { get; set; }
        public long DistinctAuthoritativeProductIds { get; set; }
        public long DistinctAuthoritativeSupplierIds { get; set; }
        public long DuplicateActiveBarcodes { get; set; }
        public long DuplicateActiveRemoteCategoryIds { get; set; }
        public long DuplicateActiveRemoteProductIds { get; set; }
        public long DuplicateActiveRemoteSupplierIds { get; set; }
        public long DuplicateAuthoritativeCategoryIds { get; set; }
        public long DuplicateAuthoritativeProductIds { get; set; }
        public long DuplicateAuthoritativeSupplierIds { get; set; }
        public long InactiveCategoryReferences { get; set; }
        public long InactiveRemoteCategories { get; set; }
        public long InactiveRemoteProducts { get; set; }
        public long InactiveRemoteSuppliers { get; set; }
        public long InactiveSupplierReferences { get; set; }
        public long InvalidActiveRemoteProducts { get; set; }
        public long InvalidCategoryReferenceMappings { get; set; }
        public long InvalidAuthoritativeCategoryIds { get; set; }
        public long InvalidAuthoritativeProductIds { get; set; }
        public long InvalidAuthoritativeSupplierIds { get; set; }
        public long InvalidSupplierReferenceMappings { get; set; }
        public long NonAuthoritativeActiveCategories { get; set; }
        public long NonAuthoritativeActiveProducts { get; set; }
        public long NonAuthoritativeActiveSuppliers { get; set; }
        public long OrphanCategoryReferences { get; set; }
        public long OrphanSupplierReferences { get; set; }
        public long PendingRemotePrices { get; set; }
        public long ReceivedCategoryIds { get; set; }
        public long ReceivedProductIds { get; set; }
        public long ReceivedSupplierIds { get; set; }
        public long RemotePriceHistoryRows { get; set; }
        public long RemoteProductsWithoutReferenceMap { get; set; }
        public long ReferenceMapsWithoutProduct { get; set; }
        public long RemoteProductsWithoutMeta { get; set; }
    }
}
