using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Online;

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
                result.ReceivedProductIds = products.ReceivedCount;
                result.DistinctAuthoritativeProductIds = products.Values.Count;
                result.DuplicateAuthoritativeProductIds = products.DuplicateCount;
                result.InvalidAuthoritativeProductIds = products.InvalidCount;
                result.ReceivedCategoryIds = categories.ReceivedCount;
                result.DistinctAuthoritativeCategoryIds = categories.Values.Count;
                result.DuplicateAuthoritativeCategoryIds = categories.DuplicateCount;
                result.InvalidAuthoritativeCategoryIds = categories.InvalidCount;
                result.ReceivedSupplierIds = suppliers.ReceivedCount;
                result.DistinctAuthoritativeSupplierIds = suppliers.Values.Count;
                result.DuplicateAuthoritativeSupplierIds = suppliers.DuplicateCount;
                result.InvalidAuthoritativeSupplierIds = suppliers.InvalidCount;
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
