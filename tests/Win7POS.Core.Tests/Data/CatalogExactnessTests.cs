using System.Runtime.Serialization.Json;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CatalogExactnessTests
{
    [TestMethod]
    public void CatalogSummaryContract_IsOptionalAndRoundTripsAuthoritativeEvidence()
    {
        var legacy = Deserialize<PosCatalogPullResponse>(@"{
  ""ok"": true,
  ""schemaVersion"": 2,
  ""syncMode"": ""full_refresh"",
  ""syncCursor"": ""cursor-legacy"",
  ""catalogVersion"": ""catalog-legacy"",
  ""catalog"": {}
}");
        legacy.Policy = ValidPolicy();

        Assert.IsNull(legacy.CatalogSummary);
        Assert.AreEqual(string.Empty, PosOnlineCompatibilityValidator.ValidateCatalogPull(legacy));

        var response = ValidCatalogResponse();
        response.CatalogSummary = Summary(products: 2, categories: 1, suppliers: 1, prices: 3);
        response.CatalogSummary.Checksum = new string('a', 64);
        response.CatalogSummary.ChecksumAlgorithm = "sha256";
        var json = Serialize(response);
        var roundTrip = Deserialize<PosCatalogPullResponse>(json);

        StringAssert.Contains(json, "\"catalogSummary\"");
        Assert.AreEqual(2L, roundTrip.CatalogSummary.Products);
        Assert.AreEqual(2L, roundTrip.CatalogSummary.ActiveProducts);
        Assert.AreEqual(1L, roundTrip.CatalogSummary.Categories);
        Assert.AreEqual(1L, roundTrip.CatalogSummary.Suppliers);
        Assert.AreEqual(3L, roundTrip.CatalogSummary.Prices);
        Assert.AreEqual(new string('a', 64), roundTrip.CatalogSummary.Checksum);
        Assert.AreEqual("sha256", roundTrip.CatalogSummary.ChecksumAlgorithm);
    }

    [TestMethod]
    public void CatalogSummaryValidator_RejectsImpossibleOrUnsafeEvidence()
    {
        var response = ValidCatalogResponse();
        response.CatalogSummary = Summary(products: -1, categories: 0, suppliers: 0, prices: 0);
        Assert.AreEqual(
            "catalog_summary_count_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response = ValidCatalogResponse();
        response.CatalogSummary = Summary(products: 1, categories: 0, suppliers: 0, prices: 0);
        response.CatalogSummary.ActiveProducts = 2;
        Assert.AreEqual(
            "catalog_summary_relationship_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response = ValidCatalogResponse();
        response.CatalogSummary = Summary(products: 1, categories: 0, suppliers: 0, prices: 0);
        response.CatalogSummary.ChecksumAlgorithm = "sha256";
        Assert.AreEqual(
            "catalog_summary_checksum_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response = ValidCatalogResponse();
        response.CatalogSummary = Summary(products: 1, categories: 0, suppliers: 0, prices: 0);
        response.CatalogSummary.Checksum = new string('a', 64);
        Assert.AreEqual(
            "catalog_summary_checksum_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));
    }

    [TestMethod]
    public void CatalogVersionValidator_RejectsValuesThatCannotRoundTripCheckpoint()
    {
        var response = ValidCatalogResponse();
        response.CatalogVersion = new string('v', 128);
        Assert.AreEqual(string.Empty, PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response.CatalogVersion = new string('v', 129);
        Assert.AreEqual(
            "catalog_version_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response.CatalogVersion = "catalog\u0001version";
        Assert.AreEqual(
            "catalog_version_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response.CatalogVersion = " catalog-v1";
        Assert.AreEqual(
            "catalog_version_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response.CatalogVersion = "catalog-v1 ";
        Assert.AreEqual(
            "catalog_version_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response.CatalogVersion = "catalog-v1\uD800";
        Assert.AreEqual(
            "catalog_version_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));
    }

    [TestMethod]
    public void CatalogCursorValidator_RejectsMalformedUnicodeBeforeFingerprintingOrPersistence()
    {
        var response = ValidCatalogResponse();
        response.SyncCursor = new string('c', 512);
        Assert.AreEqual(string.Empty, PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response.SyncCursor = new string('c', 513);
        Assert.AreEqual(
            "catalog_sync_cursor_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response.SyncCursor = "cursor-v1\uD800";
        Assert.AreEqual(
            "catalog_sync_cursor_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response = Deserialize<PosCatalogPullResponse>(@"{
  ""ok"": true,
  ""schemaVersion"": 2,
  ""syncMode"": ""full_refresh"",
  ""syncCursor"": ""cursor-\uD800"",
  ""catalogVersion"": ""catalog-v1"",
  ""catalog"": {}
}");
        response.Policy = ValidPolicy();
        Assert.AreEqual(
            "catalog_sync_cursor_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));
    }

    [TestMethod]
    public void CatalogGeneratedAt_MustBeABoundedSemanticTimestamp()
    {
        var response = ValidCatalogResponse();
        response.GeneratedAt = "2026-07-21T12:34:56.1234567-04:00";
        Assert.AreEqual(string.Empty, PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response.GeneratedAt = "zzzz";
        Assert.AreEqual(
            "catalog_generated_at_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response.GeneratedAt = new string('g', 65);
        Assert.AreEqual(
            "catalog_generated_at_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response.GeneratedAt = "invalid\uD800";
        Assert.AreEqual(
            "catalog_generated_at_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));
    }

    [TestMethod]
    public void CatalogSyncMode_RejectsWhitespaceThatWouldChangeFullRefreshClassification()
    {
        var response = ValidCatalogResponse();
        response.SyncMode = " full_refresh ";

        Assert.AreEqual(
            "catalog_sync_mode_not_supported",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));
    }

    [TestMethod]
    public async Task ReconcilerRejectsOversizedGeneratedAtBeforeCatalogMutation()
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new CatalogFullRefreshReconciler(db.Factory).ReconcileAsync(
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                new string('g', 500_000)));

        using var verify = db.Factory.Open();
        Assert.AreEqual(2L, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM products WHERE is_active = 1;"));
        Assert.AreEqual(1L, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM categories WHERE is_active = 1;"));
        Assert.AreEqual(1L, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM suppliers WHERE is_active = 1;"));
    }

    [TestMethod]
    public void CatalogRowValidator_RejectsDuplicateRemoteIdsWithinOnePage()
    {
        var response = ValidCatalogResponse();
        response.SyncMode = "delta";
        response.Catalog.Products = new[]
        {
            ValidProductRow("product-1", "BARCODE-1"),
            ValidProductRow("product-1", "BARCODE-2")
        };
        Assert.AreEqual(
            "catalog_duplicate_product_ids",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response = ValidCatalogResponse();
        response.SyncMode = "delta";
        response.Catalog.Categories = new[]
        {
            new PosCatalogCategoryResponse { CategoryId = "category-1", Name = "First" },
            new PosCatalogCategoryResponse { CategoryId = " category-1 ", Name = "Second" }
        };
        Assert.AreEqual(
            "catalog_duplicate_category_ids",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response = ValidCatalogResponse();
        response.SyncMode = "delta";
        response.Catalog.Suppliers = new[]
        {
            new PosCatalogSupplierResponse { SupplierId = "supplier-1", Name = "First" },
            new PosCatalogSupplierResponse { SupplierId = "supplier-1", Name = "Second" }
        };
        Assert.AreEqual(
            "catalog_duplicate_supplier_ids",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));
    }

    [TestMethod]
    public void CatalogRowValidator_BoundsProductReceiptTextAndRejectsMalformedUnicode()
    {
        var response = ValidCatalogResponse();
        var product = ValidProductRow("product-text", "BARCODE-TEXT");
        product.ProductName = new string('n', 512);
        product.SecondProductName = new string('s', 512);
        product.ItemNumber = new string('i', 128);
        product.UpdatedAt = "2026-07-21T12:34:56Z";
        response.Catalog.Products = new[] { product };
        Assert.AreEqual(string.Empty, PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        product.ProductName = "valid \uD83D\uDE03 name";
        Assert.AreEqual(string.Empty, PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        product.ProductName = new string('n', 513);
        Assert.AreEqual(
            "catalog_product_row_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        product.ProductName = new string('n', 500_000);
        Assert.AreEqual(
            "catalog_product_row_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        product.ProductName = "invalid\uD800";
        Assert.AreEqual(
            "catalog_product_row_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        product.ProductName = "invalid\nname";
        Assert.AreEqual(
            "catalog_product_row_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        product.ProductName = "invalid\u0085name";
        Assert.AreEqual(
            "catalog_product_row_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        product.ProductName = "invalid\uDC00name";
        Assert.AreEqual(
            "catalog_product_row_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));
    }

    [TestMethod]
    public void CatalogRowValidator_RejectsMalformedSemanticTimestamps()
    {
        var response = ValidCatalogResponse();
        response.Catalog.Categories = new[]
        {
            new PosCatalogCategoryResponse
            {
                CategoryId = "category-invalid-time",
                Name = "Invalid time",
                UpdatedAt = "zzzz"
            }
        };
        Assert.AreEqual(
            "catalog_category_row_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response = ValidCatalogResponse();
        response.Catalog.Suppliers = new[]
        {
            new PosCatalogSupplierResponse
            {
                SupplierId = "supplier-invalid-time",
                Name = "Invalid time",
                UpdatedAt = "2026-07-21T12:00:00Z trailing"
            }
        };
        Assert.AreEqual(
            "catalog_supplier_row_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response = ValidCatalogResponse();
        var product = ValidProductRow("product-invalid-time", "INVALID-TIME");
        product.UpdatedAt = "not-a-timestamp";
        response.Catalog.Products = new[] { product };
        Assert.AreEqual(
            "catalog_product_row_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response = ValidCatalogResponse();
        response.Catalog.Prices = new[]
        {
            new PosCatalogPriceResponse
            {
                EffectiveAt = "not-a-timestamp",
                Price = 100,
                PriceId = "price-invalid-time",
                ProductId = "product-invalid-time",
                Type = "retail"
            }
        };
        Assert.AreEqual(
            "catalog_price_row_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response = ValidCatalogResponse();
        response.Catalog.Tombstones = new PosCatalogTombstonesResponse
        {
            Categories = new[]
            {
                new PosCatalogCategoryTombstoneResponse
                {
                    CategoryId = "category-invalid-time",
                    DeletedAt = "zzzz"
                }
            }
        };
        Assert.AreEqual(
            "catalog_category_tombstone_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));
    }

    [TestMethod]
    public async Task CleanFullRefresh_IsVerifiedOnlyWhenEveryAuthoritativeCountMatches()
    {
        using var db = TestDb.Create();
        var audit = await CreateCleanAuditAsync(db);
        var context = CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2);

        var verified = CatalogExactnessVerifier.Evaluate(
            Summary(products: 2, categories: 1, suppliers: 1, prices: 2),
            audit,
            context);
        Assert.AreEqual(CatalogCompletenessStatus.Verified, verified.Status);
        Assert.AreEqual("catalog_exactness_verified", verified.Code);
        Assert.IsFalse(verified.RepairRequired);
        Assert.AreEqual(2L, audit.ActiveRemoteProducts);
        Assert.AreEqual(2L, audit.DistinctActiveRemoteProductIds);
        Assert.AreEqual(0L, audit.DuplicateActiveRemoteProductIds);
        Assert.AreEqual(0L, audit.RemoteProductsWithoutMeta);
        Assert.AreEqual(0L, audit.OrphanCategoryReferences);
        Assert.AreEqual(0L, audit.OrphanSupplierReferences);
        Assert.AreEqual(0L, audit.PendingRemotePrices);
        Assert.AreEqual(0L, audit.NonAuthoritativeActiveProducts);

        var mismatchSummary = Summary(products: 3, categories: 1, suppliers: 1, prices: 2);
        mismatchSummary.ActiveProducts = 2;
        var mismatch = CatalogExactnessVerifier.Evaluate(mismatchSummary, audit, context);
        Assert.AreEqual(CatalogCompletenessStatus.Mismatch, mismatch.Status);
        Assert.AreEqual("catalog_products_count_mismatch", mismatch.Code);
        Assert.IsTrue(mismatch.RepairRequired);
    }

    [TestMethod]
    public async Task TwoPageFullRefresh_RepeatedReferenceRowsUseDistinctExactnessEvidence()
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);
        var evidence = new CatalogFullLaneEvidenceTracker();
        var page = new PosCatalogPayload
        {
            Categories = new[]
            {
                new PosCatalogCategoryResponse
                {
                    CategoryId = "category-1",
                    Name = "Category",
                    UpdatedAt = "2026-07-19T01:00:00Z"
                }
            },
            Suppliers = new[]
            {
                new PosCatalogSupplierResponse
                {
                    SupplierId = "supplier-1",
                    Name = "Supplier",
                    UpdatedAt = "2026-07-19T01:00:00Z"
                }
            }
        };
        evidence.Add(page);
        var counts = evidence.Add(page);
        var context = CompleteContext(products: 2, categories: counts.Categories, suppliers: counts.Suppliers, prices: 2);
        context.Pages = 2;

        var exactness = await new CatalogFullRefreshReconciler(db.Factory)
            .ReconcileAndVerifyAsync(
                new[] { "product-1", "product-2" },
                evidence.CategoryIds,
                evidence.SupplierIds,
                "2026-07-19T01:00:00Z",
                Summary(products: 2, categories: 1, suppliers: 1, prices: 2),
                context);

        Assert.AreEqual(string.Empty, evidence.ConflictCode);
        Assert.AreEqual(CatalogCompletenessStatus.Verified, exactness.Status);
        Assert.AreEqual(0L, exactness.Audit.DuplicateAuthoritativeCategoryIds);
        Assert.AreEqual(0L, exactness.Audit.DuplicateAuthoritativeSupplierIds);
    }

    [TestMethod]
    public async Task StagedTwoPageFullRefresh_RepeatedReferenceRowsRemainDistinctAndVerified()
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);
        var stage = await CreateAuthoritativeStageAsync(db, "verified");
        var repository = new RemoteCatalogBatchRepository(db.Factory);

        await repository.StageAuthoritativePageAsync(
            StageBatch(stage.FullRunId, 1, hasMore: true, "product-1", "BARCODE-1", "price-1"),
            CancellationToken.None,
            stage.Fence);
        var evidence = await repository.StageAuthoritativePageAsync(
            StageBatch(stage.FullRunId, 2, hasMore: false, "product-2", "BARCODE-2", "price-2"),
            CancellationToken.None,
            stage.Fence);

        Assert.AreEqual(string.Empty, evidence.ConflictCode);
        Assert.AreEqual(2L, evidence.LaneCounts.Products);
        Assert.AreEqual(1L, evidence.LaneCounts.Categories);
        Assert.AreEqual(1L, evidence.LaneCounts.Suppliers);
        Assert.AreEqual(2L, evidence.LaneCounts.Prices);
        var context = CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2);
        context.Pages = 2;
        var exactness = await new CatalogFullRefreshReconciler(db.Factory)
            .ReconcileAndVerifyStagedAsync(
                stage.FullRunId,
                "2026-07-19T01:00:00Z",
                Summary(products: 2, categories: 1, suppliers: 1, prices: 2),
                context,
                stage.Fence);

        Assert.AreEqual(CatalogCompletenessStatus.Verified, exactness.Status);
        Assert.AreEqual("catalog_exactness_verified", exactness.Code);
        Assert.AreEqual(0L, exactness.Audit.DuplicateAuthoritativeProductIds);
        Assert.AreEqual(0L, exactness.Audit.DuplicateAuthoritativeCategoryIds);
        Assert.AreEqual(0L, exactness.Audit.DuplicateAuthoritativeSupplierIds);
        using var verify = db.Factory.Open();
        Assert.AreEqual(2L, await verify.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM catalog_authoritative_id_stage
JOIN catalog_authoritative_stage_scope scope USING(scope_id)
WHERE scope.full_run_id = 'full-verified'
  AND entity_kind = 'page';"));
        Assert.AreEqual(2L, await verify.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM catalog_authoritative_id_stage
JOIN catalog_authoritative_stage_scope scope USING(scope_id)
WHERE scope.full_run_id = 'full-verified'
  AND entity_kind = 'category';"));
    }

    [TestMethod]
    public async Task StagedPreflight_DuplicateProductLeavesOldFenceAndLiveCatalogUnchanged()
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);
        await SeedUnrelatedRemoteProductAsync(db, "product-must-stay", "MUST-STAY");
        var stage = await CreateAuthoritativeStageAsync(db, "preflight-duplicate");
        var state = new CatalogShopStateRepository(db.Factory);
        var before = await state.EnsureAndLoadCursorAsync(
            stage.ShopId,
            stage.ShopCode,
            stage.Generation);
        var repository = new RemoteCatalogBatchRepository(db.Factory);
        await repository.StageAuthoritativePageAsync(
            StageBatch(stage.FullRunId, 1, hasMore: true, "product-1", "BARCODE-1", "price-1"),
            CancellationToken.None,
            stage.Fence);
        await repository.StageAuthoritativePageAsync(
            StageBatch(stage.FullRunId, 2, hasMore: false, "product-1", "BARCODE-1", "price-2"),
            CancellationToken.None,
            stage.Fence);
        var context = CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2);
        context.Pages = 2;

        var code = await new CatalogFullRefreshReconciler(db.Factory)
            .ValidateStagedPreflightAsync(
                stage.FullRunId,
                Summary(products: 1, categories: 1, suppliers: 1, prices: 2),
                context,
                stage.Fence);

        Assert.AreEqual("catalog_duplicate_product_ids", code);
        var after = await state.EnsureAndLoadCursorAsync(
            stage.ShopId,
            stage.ShopCode,
            stage.Generation);
        Assert.AreEqual(before.Epoch, after.Epoch);
        Assert.AreEqual(before.Cursor, after.Cursor);
        Assert.AreEqual(before.Mode, after.Mode);
        Assert.AreEqual(stage.Fence.ExpectedEpoch, after.Epoch);
        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await verify.ExecuteScalarAsync<long>(@"
SELECT is_active
FROM products
WHERE remote_product_id = 'product-must-stay';"));
    }

    [TestMethod]
    public async Task StagedPreflight_ActiveProductsMismatchLeavesOldFenceAndLiveCatalogUnchanged()
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);
        await SeedUnrelatedRemoteProductAsync(db, "product-must-stay", "MUST-STAY");
        var stage = await CreateAuthoritativeStageAsync(db, "active-mismatch");
        var state = new CatalogShopStateRepository(db.Factory);
        var before = await state.EnsureAndLoadCursorAsync(
            stage.ShopId,
            stage.ShopCode,
            stage.Generation);
        var repository = new RemoteCatalogBatchRepository(db.Factory);
        await repository.StageAuthoritativePageAsync(
            StageBatch(stage.FullRunId, 1, hasMore: true, "product-1", "BARCODE-1", "price-1"),
            CancellationToken.None,
            stage.Fence);
        await repository.StageAuthoritativePageAsync(
            StageBatch(stage.FullRunId, 2, hasMore: false, "product-2", "BARCODE-2", "price-2"),
            CancellationToken.None,
            stage.Fence);
        var context = CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2);
        context.Pages = 2;
        var summary = Summary(products: 2, categories: 1, suppliers: 1, prices: 2);
        summary.ActiveProducts = 1;

        var code = await new CatalogFullRefreshReconciler(db.Factory)
            .ValidateStagedPreflightAsync(
                stage.FullRunId,
                summary,
                context,
                stage.Fence);

        Assert.AreEqual("catalog_active_products_count_mismatch", code);
        await AssertPreflightPreservedStateAsync(db, stage, before, "product-must-stay");
    }

    [TestMethod]
    public async Task StagedPreflight_EmptyAuthoritativeCatalogLeavesOldFenceAndLiveCatalogUnchanged()
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);
        await SeedUnrelatedRemoteProductAsync(db, "product-must-stay", "MUST-STAY");
        var stage = await CreateAuthoritativeStageAsync(db, "empty-catalog");
        var state = new CatalogShopStateRepository(db.Factory);
        var before = await state.EnsureAndLoadCursorAsync(
            stage.ShopId,
            stage.ShopCode,
            stage.Generation);
        await new RemoteCatalogBatchRepository(db.Factory).StageAuthoritativePageAsync(
            new RemoteCatalogBatch
            {
                AuthoritativeFullRefresh = true,
                AuthoritativeStagePage = new CatalogAuthoritativeStagePage
                {
                    FullRunId = stage.FullRunId,
                    HasMore = false,
                    PageNumber = 1
                }
            },
            CancellationToken.None,
            stage.Fence);

        var code = await new CatalogFullRefreshReconciler(db.Factory)
            .ValidateStagedPreflightAsync(
                stage.FullRunId,
                Summary(products: 0, categories: 0, suppliers: 0, prices: 0),
                CompleteContext(products: 0, categories: 0, suppliers: 0, prices: 0),
                stage.Fence);

        Assert.AreEqual("full_refresh_no_active_products", code);
        await AssertPreflightPreservedStateAsync(db, stage, before, "product-must-stay");
    }

    [TestMethod]
    public async Task StagedPreflight_DuplicateNormalizedBarcodeLeavesOldFenceAndLiveCatalogUnchanged()
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);
        await SeedUnrelatedRemoteProductAsync(db, "product-must-stay", "MUST-STAY");
        var stage = await CreateAuthoritativeStageAsync(db, "duplicate-barcode");
        var state = new CatalogShopStateRepository(db.Factory);
        var before = await state.EnsureAndLoadCursorAsync(
            stage.ShopId,
            stage.ShopCode,
            stage.Generation);
        var repository = new RemoteCatalogBatchRepository(db.Factory);
        await repository.StageAuthoritativePageAsync(
            StageBatch(stage.FullRunId, 1, hasMore: true, "product-1", "DUPLICATE-BARCODE", "price-1"),
            CancellationToken.None,
            stage.Fence);
        await repository.StageAuthoritativePageAsync(
            StageBatch(stage.FullRunId, 2, hasMore: false, "product-2", "  DUPLICATE-BARCODE  ", "price-2"),
            CancellationToken.None,
            stage.Fence);
        var context = CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2);
        context.Pages = 2;

        var code = await new CatalogFullRefreshReconciler(db.Factory)
            .ValidateStagedPreflightAsync(
                stage.FullRunId,
                Summary(products: 2, categories: 1, suppliers: 1, prices: 2),
                context,
                stage.Fence);

        Assert.AreEqual("catalog_duplicate_active_barcodes", code);
        await AssertPreflightPreservedStateAsync(db, stage, before, "product-must-stay");
    }

    [TestMethod]
    [DataRow("DISC:REMOTE-1")]
    [DataRow("MANUAL:REMOTE-1")]
    public async Task StagedPreflight_ReservedBarcodeLeavesOldFenceAndLiveCatalogUnchanged(
        string reservedBarcode)
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);
        await SeedUnrelatedRemoteProductAsync(db, "product-must-stay", "MUST-STAY");
        var stage = await CreateAuthoritativeStageAsync(db, "reserved-barcode");
        var state = new CatalogShopStateRepository(db.Factory);
        var before = await state.EnsureAndLoadCursorAsync(
            stage.ShopId,
            stage.ShopCode,
            stage.Generation);

        await new RemoteCatalogBatchRepository(db.Factory).StageAuthoritativePageAsync(
            StageBatch(
                stage.FullRunId,
                1,
                hasMore: false,
                "product-reserved",
                reservedBarcode,
                "price-reserved"),
            CancellationToken.None,
            stage.Fence);

        var code = await new CatalogFullRefreshReconciler(db.Factory)
            .ValidateStagedPreflightAsync(
                stage.FullRunId,
                Summary(products: 1, categories: 1, suppliers: 1, prices: 1),
                CompleteContext(products: 1, categories: 1, suppliers: 1, prices: 1),
                stage.Fence);

        Assert.AreEqual("catalog_product_row_invalid", code);
        await AssertPreflightPreservedStateAsync(db, stage, before, "product-must-stay");
    }

    [TestMethod]
    public async Task StagedPreflight_PriceForNonAuthoritativeProductLeavesLiveCatalogUnchanged()
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);
        await SeedUnrelatedRemoteProductAsync(db, "product-must-stay", "MUST-STAY");
        var stage = await CreateAuthoritativeStageAsync(db, "orphan-price");
        var state = new CatalogShopStateRepository(db.Factory);
        var before = await state.EnsureAndLoadCursorAsync(
            stage.ShopId,
            stage.ShopCode,
            stage.Generation);
        var batch = StageBatch(
            stage.FullRunId,
            1,
            hasMore: false,
            "product-1",
            "BARCODE-1",
            "price-orphan");
        batch.Prices.Single().RemoteProductId = "product-not-authoritative";
        await new RemoteCatalogBatchRepository(db.Factory).StageAuthoritativePageAsync(
            batch,
            CancellationToken.None,
            stage.Fence);

        var code = await new CatalogFullRefreshReconciler(db.Factory)
            .ValidateStagedPreflightAsync(
                stage.FullRunId,
                Summary(products: 1, categories: 1, suppliers: 1, prices: 1),
                CompleteContext(products: 1, categories: 1, suppliers: 1, prices: 1),
                stage.Fence);

        Assert.AreEqual("catalog_price_product_not_authoritative", code);
        await AssertPreflightPreservedStateAsync(db, stage, before, "product-must-stay");
    }

    [TestMethod]
    public async Task StagedPreflight_ProductWithMissingCategoryLaneLeavesLiveCatalogUnchanged()
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);
        await SeedUnrelatedRemoteProductAsync(db, "product-must-stay", "MUST-STAY");
        var stage = await CreateAuthoritativeStageAsync(db, "orphan-category");
        var state = new CatalogShopStateRepository(db.Factory);
        var before = await state.EnsureAndLoadCursorAsync(
            stage.ShopId,
            stage.ShopCode,
            stage.Generation);
        var batch = StageBatch(
            stage.FullRunId,
            1,
            hasMore: false,
            "product-1",
            "BARCODE-1",
            "price-1");
        batch.Categories = Array.Empty<RemoteCatalogCategoryWrite>();
        batch.Products.Single().RemoteCategoryId = "category-not-authoritative";
        await new RemoteCatalogBatchRepository(db.Factory).StageAuthoritativePageAsync(
            batch,
            CancellationToken.None,
            stage.Fence);

        var code = await new CatalogFullRefreshReconciler(db.Factory)
            .ValidateStagedPreflightAsync(
                stage.FullRunId,
                Summary(products: 1, categories: 0, suppliers: 1, prices: 1),
                CompleteContext(products: 1, categories: 0, suppliers: 1, prices: 1),
                stage.Fence);

        Assert.AreEqual("catalog_category_references_orphaned", code);
        await AssertPreflightPreservedStateAsync(db, stage, before, "product-must-stay");
    }

    [TestMethod]
    public async Task StagedPreflight_ProductWithMissingSupplierLaneLeavesLiveCatalogUnchanged()
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);
        await SeedUnrelatedRemoteProductAsync(db, "product-must-stay", "MUST-STAY");
        var stage = await CreateAuthoritativeStageAsync(db, "orphan-supplier");
        var state = new CatalogShopStateRepository(db.Factory);
        var before = await state.EnsureAndLoadCursorAsync(
            stage.ShopId,
            stage.ShopCode,
            stage.Generation);
        var batch = StageBatch(
            stage.FullRunId,
            1,
            hasMore: false,
            "product-1",
            "BARCODE-1",
            "price-1");
        batch.Suppliers = Array.Empty<RemoteCatalogSupplierWrite>();
        batch.Products.Single().RemoteSupplierId = "supplier-not-authoritative";
        await new RemoteCatalogBatchRepository(db.Factory).StageAuthoritativePageAsync(
            batch,
            CancellationToken.None,
            stage.Fence);

        var code = await new CatalogFullRefreshReconciler(db.Factory)
            .ValidateStagedPreflightAsync(
                stage.FullRunId,
                Summary(products: 1, categories: 1, suppliers: 0, prices: 1),
                CompleteContext(products: 1, categories: 1, suppliers: 0, prices: 1),
                stage.Fence);

        Assert.AreEqual("catalog_supplier_references_orphaned", code);
        await AssertPreflightPreservedStateAsync(db, stage, before, "product-must-stay");
    }

    [TestMethod]
    public async Task StagedCrossPageDuplicateProductOrPrice_RejectsBeforeDeactivation()
    {
        using (var productDb = TestDb.Create())
        {
            await SeedCleanCatalogAsync(productDb);
            await SeedUnrelatedRemoteProductAsync(productDb, "product-must-stay", "MUST-STAY");
            var stage = await CreateAuthoritativeStageAsync(productDb, "duplicate-product");
            var repository = new RemoteCatalogBatchRepository(productDb.Factory);
            await repository.StageAuthoritativePageAsync(
                StageBatch(stage.FullRunId, 1, hasMore: true, "product-1", "BARCODE-1", "price-1"),
                CancellationToken.None,
                stage.Fence);
            await repository.StageAuthoritativePageAsync(
                StageBatch(stage.FullRunId, 2, hasMore: false, "product-1", "BARCODE-1", "price-2"),
                CancellationToken.None,
                stage.Fence);
            var context = CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2);
            context.Pages = 2;

            var result = await new CatalogFullRefreshReconciler(productDb.Factory)
                .ReconcileAndVerifyStagedAsync(
                    stage.FullRunId,
                    "2026-07-19T01:00:00Z",
                    Summary(products: 1, categories: 1, suppliers: 1, prices: 2),
                    context,
                    stage.Fence);

            Assert.AreEqual(CatalogCompletenessStatus.Mismatch, result.Status);
            Assert.AreEqual("catalog_duplicate_product_ids", result.Code);
            using var verify = productDb.Factory.Open();
            Assert.AreEqual(1L, await verify.ExecuteScalarAsync<long>(@"
SELECT is_active
FROM products
WHERE remote_product_id = 'product-must-stay';"));
        }

        using (var priceDb = TestDb.Create())
        {
            await SeedCleanCatalogAsync(priceDb);
            await SeedUnrelatedRemoteProductAsync(priceDb, "product-must-stay", "MUST-STAY");
            var stage = await CreateAuthoritativeStageAsync(priceDb, "duplicate-price");
            var repository = new RemoteCatalogBatchRepository(priceDb.Factory);
            await repository.StageAuthoritativePageAsync(
                StageBatch(stage.FullRunId, 1, hasMore: true, "product-1", "BARCODE-1", "price-1"),
                CancellationToken.None,
                stage.Fence);
            await repository.StageAuthoritativePageAsync(
                StageBatch(stage.FullRunId, 2, hasMore: false, "product-2", "BARCODE-2", "price-1"),
                CancellationToken.None,
                stage.Fence);
            var context = CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2);
            context.Pages = 2;

            var result = await new CatalogFullRefreshReconciler(priceDb.Factory)
                .ReconcileAndVerifyStagedAsync(
                    stage.FullRunId,
                    "2026-07-19T01:00:00Z",
                    Summary(products: 2, categories: 1, suppliers: 1, prices: 2),
                    context,
                    stage.Fence);

            Assert.AreEqual(CatalogCompletenessStatus.Mismatch, result.Status);
            Assert.AreEqual("catalog_duplicate_price_rows", result.Code);
            using var verify = priceDb.Factory.Open();
            Assert.AreEqual(1L, await verify.ExecuteScalarAsync<long>(@"
SELECT is_active
FROM products
WHERE remote_product_id = 'product-must-stay';"));
        }
    }

    [TestMethod]
    public async Task StagedFullRefresh_MissingPageMarkerRejectsBeforeDeactivation()
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);
        await SeedUnrelatedRemoteProductAsync(db, "product-must-stay", "MUST-STAY");
        var stage = await CreateAuthoritativeStageAsync(db, "missing-page");
        var repository = new RemoteCatalogBatchRepository(db.Factory);
        await repository.StageAuthoritativePageAsync(
            StageBatch(stage.FullRunId, 1, hasMore: true, "product-1", "BARCODE-1", "price-1"),
            CancellationToken.None,
            stage.Fence);
        await repository.StageAuthoritativePageAsync(
            StageBatch(stage.FullRunId, 3, hasMore: false, "product-2", "BARCODE-2", "price-2"),
            CancellationToken.None,
            stage.Fence);
        var context = CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2);
        context.Pages = 3;

        var result = await new CatalogFullRefreshReconciler(db.Factory)
            .ReconcileAndVerifyStagedAsync(
                stage.FullRunId,
                "2026-07-19T01:00:00Z",
                Summary(products: 2, categories: 1, suppliers: 1, prices: 2),
                context,
                stage.Fence);

        Assert.AreEqual(CatalogCompletenessStatus.Mismatch, result.Status);
        Assert.AreEqual("catalog_authoritative_stage_incomplete", result.Code);
        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await verify.ExecuteScalarAsync<long>(@"
SELECT is_active
FROM products
WHERE remote_product_id = 'product-must-stay';"));
    }

    [TestMethod]
    public async Task StagedFullRefresh_StaleGenerationBeforeReconcileCannotDeactivate()
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);
        await SeedUnrelatedRemoteProductAsync(db, "product-must-stay", "MUST-STAY");
        var stage = await CreateAuthoritativeStageAsync(db, "stale-reconcile");
        await new RemoteCatalogBatchRepository(db.Factory).StageAuthoritativePageAsync(
            StageBatch(stage.FullRunId, 1, hasMore: false, "product-1", "BARCODE-1", "price-1"),
            CancellationToken.None,
            stage.Fence);
        var replacement = new OnlineSyncGeneration(
            "generation-reconcile-replacement",
            "session-reconcile-replacement",
            "device-reconcile-replacement",
            stage.ShopId,
            stage.ShopCode);
        await new OnlineSyncGenerationRepository(db.Factory)
            .ActivateAndRecoverAsync(replacement, 2);
        var context = CompleteContext(products: 1, categories: 1, suppliers: 1, prices: 1);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new CatalogFullRefreshReconciler(db.Factory).ReconcileAndVerifyStagedAsync(
                stage.FullRunId,
                "2026-07-19T01:00:00Z",
                Summary(products: 1, categories: 1, suppliers: 1, prices: 1),
                context,
                stage.Fence));

        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await verify.ExecuteScalarAsync<long>(@"
SELECT is_active
FROM products
WHERE remote_product_id = 'product-must-stay';"));
    }

    [TestMethod]
    public async Task AuthoritativeStageCleanup_IsBoundedAndShopRunScoped()
    {
        using var db = TestDb.Create();
        using (var seed = db.Factory.Open())
        {
            await seed.ExecuteAsync(@"
INSERT INTO catalog_authoritative_stage_scope(
  scope_id, shop_id, shop_code, transition_epoch, generation_id,
  generation_fingerprint, full_run_id, created_at)
VALUES
  (101, 'shop-a', 'SHOP-A', 1, 'generation-a', 'fingerprint-a', 'run-stale', 1),
  (102, 'shop-a', 'SHOP-A', 1, 'generation-a', 'fingerprint-a', 'run-keep', 1),
  (103, 'shop-b', 'SHOP-B', 1, 'generation-b', 'fingerprint-b', 'run-stale', 1);

INSERT INTO catalog_authoritative_id_stage(
  scope_id, page_number, entity_kind, remote_id, content_fingerprint,
  category_remote_id, supplier_remote_id, product_remote_id,
  occurrence_count, has_more, staged_at)
VALUES
  (101, 1, 'page', '', '', '', '', '', 0, 1, 1),
  (101, 2, 'page', '', '', '', '', '', 0, 1, 1),
  (101, 3, 'page', '', '', '', '', '', 0, 0, 1),
  (102, 1, 'page', '', '', '', '', '', 0, 0, 1),
  (103, 1, 'page', '', '', '', '', '', 0, 0, 1);");
        }
        var reconciler = new CatalogFullRefreshReconciler(db.Factory);

        Assert.AreEqual(2, await reconciler.ClearStaleAuthoritativeStagesAsync(
            "shop-a", "SHOP-A", "run-keep", maximumRows: 2));
        using (var afterBoundedCleanup = db.Factory.Open())
        {
            Assert.AreEqual(1L, await afterBoundedCleanup.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM catalog_authoritative_id_stage item
JOIN catalog_authoritative_stage_scope scope ON scope.scope_id = item.scope_id
WHERE scope.shop_id = 'shop-a' AND scope.full_run_id = 'run-stale';"));
            Assert.AreEqual(1L, await afterBoundedCleanup.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM catalog_authoritative_id_stage item
JOIN catalog_authoritative_stage_scope scope ON scope.scope_id = item.scope_id
WHERE scope.shop_id = 'shop-a' AND scope.full_run_id = 'run-keep';"));
            Assert.AreEqual(1L, await afterBoundedCleanup.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM catalog_authoritative_id_stage item
JOIN catalog_authoritative_stage_scope scope ON scope.scope_id = item.scope_id
WHERE scope.shop_id = 'shop-b' AND scope.full_run_id = 'run-stale';"));
        }

        Assert.AreEqual(1, await reconciler.ClearAuthoritativeStageAsync(
            "run-stale", "shop-a", "SHOP-A"));
        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM catalog_authoritative_id_stage item
JOIN catalog_authoritative_stage_scope scope ON scope.scope_id = item.scope_id
WHERE scope.shop_id = 'shop-a' AND scope.full_run_id = 'run-stale';"));
        Assert.AreEqual(1L, await verify.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM catalog_authoritative_id_stage item
JOIN catalog_authoritative_stage_scope scope ON scope.scope_id = item.scope_id
WHERE scope.shop_id = 'shop-a' AND scope.full_run_id = 'run-keep';"));
        Assert.AreEqual(1L, await verify.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM catalog_authoritative_id_stage item
JOIN catalog_authoritative_stage_scope scope ON scope.scope_id = item.scope_id
WHERE scope.shop_id = 'shop-b' AND scope.full_run_id = 'run-stale';"));
    }

    [TestMethod]
    public async Task MissingSummary_RemainsExplicitlyUnverifiedForLegacyServer()
    {
        using var db = TestDb.Create();
        var audit = await CreateCleanAuditAsync(db);

        var result = CatalogExactnessVerifier.Evaluate(
            null,
            audit,
            CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2));

        Assert.AreEqual(CatalogCompletenessStatus.Unverified, result.Status);
        Assert.AreEqual("catalog_summary_missing", result.Code);
        Assert.IsFalse(result.RepairRequired);
    }

    [TestMethod]
    public async Task MissingSummary_ReconcileAndVerifyPreservesHealthyCatalogWithoutFalseMismatch()
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);

        var result = await new CatalogFullRefreshReconciler(db.Factory)
            .ReconcileAndVerifyAsync(
                new[] { "product-1" },
                Array.Empty<string>(),
                Array.Empty<string>(),
                "2026-07-14T01:00:00Z",
                summary: null,
                CompleteContext(products: 1, categories: 0, suppliers: 0, prices: 0));

        Assert.AreEqual(CatalogCompletenessStatus.Unverified, result.Status);
        Assert.AreEqual("catalog_summary_missing", result.Code);
        Assert.IsFalse(result.RepairRequired);
        Assert.AreEqual(2L, result.Audit.DistinctAuthoritativeProductIds);
        Assert.AreEqual(2L, result.Audit.DistinctActiveRemoteProductIds);

        using var conn = db.Factory.Open();
        Assert.AreEqual(2L, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM products WHERE COALESCE(is_active, 1) = 1;"));
        Assert.AreEqual(1L, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM categories WHERE COALESCE(is_active, 1) = 1;"));
        Assert.AreEqual(1L, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM suppliers WHERE COALESCE(is_active, 1) = 1;"));
        Assert.AreEqual(2L, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM remote_catalog_product_references;"));
    }

    [TestMethod]
    public async Task DuplicateRemoteIdAndOrphanReference_CannotBeHiddenByReconciliation()
    {
        using (var duplicateDb = TestDb.Create())
        {
            await SeedCleanCatalogAsync(duplicateDb);
            var duplicateAudit = await new CatalogFullRefreshReconciler(duplicateDb.Factory).ReconcileAsync(
                new[] { "product-1", "product-1", "product-2" },
                new[] { "category-1" },
                new[] { "supplier-1" },
                "2026-07-14T01:00:00Z");
            var duplicate = CatalogExactnessVerifier.Evaluate(
                Summary(products: 2, categories: 1, suppliers: 1, prices: 2),
                duplicateAudit,
                CompleteContext(products: 3, categories: 1, suppliers: 1, prices: 2));

            Assert.AreEqual(1L, duplicateAudit.DuplicateAuthoritativeProductIds);
            Assert.AreEqual(CatalogCompletenessStatus.Mismatch, duplicate.Status);
            Assert.AreEqual("catalog_duplicate_product_ids", duplicate.Code);
        }

        using (var orphanDb = TestDb.Create())
        {
            var audit = await CreateCleanAuditAsync(orphanDb);
            using (var conn = orphanDb.Factory.Open())
            {
                await conn.ExecuteAsync(@"
INSERT INTO product_meta(barcode, category_id)
VALUES('ORPHAN-META', 9999);");
            }

            audit = await new CatalogFullRefreshReconciler(orphanDb.Factory).ReconcileAsync(
                new[] { "product-1", "product-2" },
                new[] { "category-1" },
                new[] { "supplier-1" },
                "2026-07-14T02:00:00Z");
            var orphan = CatalogExactnessVerifier.Evaluate(
                Summary(products: 2, categories: 1, suppliers: 1, prices: 2),
                audit,
                CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2));

            Assert.AreEqual(1L, audit.OrphanCategoryReferences);
            Assert.AreEqual(CatalogCompletenessStatus.Mismatch, orphan.Status);
            Assert.AreEqual("catalog_category_references_orphaned", orphan.Code);
        }
    }

    [TestMethod]
    public async Task HasMoreAndChecksumWithoutComparableEvidence_NeverBecomeVerified()
    {
        using var db = TestDb.Create();
        var audit = await CreateCleanAuditAsync(db);
        var summary = Summary(products: 2, categories: 1, suppliers: 1, prices: 2);

        var partialContext = CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2);
        partialContext.HasMore = true;
        var partial = CatalogExactnessVerifier.Evaluate(summary, audit, partialContext);
        Assert.AreEqual(CatalogCompletenessStatus.Unverified, partial.Status);
        Assert.AreEqual("catalog_has_more_not_drained", partial.Code);
        Assert.IsTrue(partial.RepairRequired);

        summary.Checksum = new string('a', 64);
        summary.ChecksumAlgorithm = "sha256";
        var noChecksum = CatalogExactnessVerifier.Evaluate(
            summary,
            audit,
            CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2));
        Assert.AreEqual(CatalogCompletenessStatus.Unverified, noChecksum.Status);
        Assert.AreEqual("catalog_checksum_unverifiable", noChecksum.Code);

        var mismatchContext = CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2);
        mismatchContext.ActualChecksum = new string('b', 64);
        mismatchContext.ActualChecksumAlgorithm = "sha256";
        var checksumMismatch = CatalogExactnessVerifier.Evaluate(summary, audit, mismatchContext);
        Assert.AreEqual(CatalogCompletenessStatus.Mismatch, checksumMismatch.Status);
        Assert.AreEqual("catalog_checksum_mismatch", checksumMismatch.Code);

        mismatchContext.ActualChecksum = new string('a', 64);
        var checksumVerified = CatalogExactnessVerifier.Evaluate(summary, audit, mismatchContext);
        Assert.AreEqual(CatalogCompletenessStatus.Verified, checksumVerified.Status);
    }

    [TestMethod]
    public void CatalogRowValidator_RejectsMalformedRowsBeforeAnyApply()
    {
        var response = ValidCatalogResponse();
        response.Catalog.Products = new[]
        {
            new PosCatalogProductResponse
            {
                ProductId = "product-1",
                Barcode = string.Empty,
                RetailPrice = 100
            }
        };
        Assert.AreEqual(
            "catalog_product_row_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response = ValidCatalogResponse();
        response.Catalog.Prices = new[]
        {
            new PosCatalogPriceResponse
            {
                PriceId = string.Empty,
                ProductId = "product-1",
                Type = "retail",
                Price = 100
            }
        };
        Assert.AreEqual(
            "catalog_price_row_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));

        response = ValidCatalogResponse();
        response.Catalog.Tombstones = new PosCatalogTombstonesResponse
        {
            Suppliers = new[]
            {
                new PosCatalogSupplierTombstoneResponse { SupplierId = " " }
            }
        };
        Assert.AreEqual(
            "catalog_supplier_tombstone_invalid",
            PosOnlineCompatibilityValidator.ValidateCatalogPull(response));
    }

    [TestMethod]
    public async Task InvalidDuplicateOrSkippedPriceEvidence_CannotBecomeVerified()
    {
        using var db = TestDb.Create();
        var audit = await CreateCleanAuditAsync(db);
        var summary = Summary(products: 2, categories: 1, suppliers: 1, prices: 2);

        var invalid = CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2);
        invalid.InvalidPriceRows = 1;
        invalid.PriceRowsAccepted = 1;
        var invalidResult = CatalogExactnessVerifier.Evaluate(summary, audit, invalid);
        Assert.AreEqual(CatalogCompletenessStatus.Mismatch, invalidResult.Status);
        Assert.AreEqual("catalog_invalid_price_rows", invalidResult.Code);

        var duplicate = CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2);
        duplicate.DuplicatePriceRows = 1;
        var duplicateResult = CatalogExactnessVerifier.Evaluate(summary, audit, duplicate);
        Assert.AreEqual(CatalogCompletenessStatus.Mismatch, duplicateResult.Status);
        Assert.AreEqual("catalog_duplicate_price_rows", duplicateResult.Code);

        var skipped = CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2);
        skipped.PriceRowsAccepted = 1;
        var skippedResult = CatalogExactnessVerifier.Evaluate(summary, audit, skipped);
        Assert.AreEqual(CatalogCompletenessStatus.Mismatch, skippedResult.Status);
        Assert.AreEqual("catalog_prices_not_fully_applied", skippedResult.Code);
    }

    [TestMethod]
    public async Task LegacySummaryCannotHideMissingAuthoritativeIdentityOrOrphanReferenceMap()
    {
        using var db = TestDb.Create();
        var audit = await CreateCleanAuditAsync(db);
        audit.DistinctActiveRemoteProductIds = 1;
        audit.ActiveRemoteProducts = 1;

        var collapsed = CatalogExactnessVerifier.Evaluate(
            null,
            audit,
            CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2));
        Assert.AreEqual(CatalogCompletenessStatus.Mismatch, collapsed.Status);
        Assert.AreEqual("catalog_authoritative_products_not_applied", collapsed.Code);
        Assert.IsTrue(collapsed.RepairRequired);

        using (var seed = db.Factory.Open())
        {
            await seed.ExecuteAsync(@"
INSERT INTO remote_catalog_product_references(remote_product_id)
VALUES('orphan-reference-map');
INSERT INTO products(barcode, name, unitPrice, remote_product_id, is_active)
VALUES('WHITESPACE-REMOTE', 'Whitespace identity', 100, '   ', 1);");
        }

        var current = await new CatalogFullRefreshReconciler(db.Factory).AuditCurrentAsync();
        Assert.AreEqual(1L, current.ReferenceMapsWithoutProduct);
        Assert.AreEqual("catalog_product_reference_map_orphaned", CatalogExactnessVerifier.FindInvariantError(current));
        Assert.AreEqual(2L, await new ProductRepository(db.Factory).CountActiveRemoteProductsAsync());
    }

    [TestMethod]
    public async Task PersistedMismatch_ClearsSaleSafetyAndBlocksItUntilRepair()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db, "shop-a", "SHOP-A");
        var state = new CatalogShopStateRepository(db.Factory);
        var binding = await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        var audit = await CreateCleanAuditAsync(db);
        var context = CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2);
        var verified = CatalogExactnessVerifier.Evaluate(
            Summary(products: 2, categories: 1, suppliers: 1, prices: 2),
            audit,
            context);

        await state.StoreExactnessAsync("shop-a", "SHOP-A", verified, binding.Epoch);
        using (var verifyEvidence = db.Factory.Open())
        {
            Assert.AreEqual("2", await verifyEvidence.ExecuteScalarAsync<string>(
                "SELECT value FROM app_settings WHERE key = 'pos.catalog.exactness.accepted_prices';"));
            Assert.AreEqual("0", await verifyEvidence.ExecuteScalarAsync<string>(
                "SELECT value FROM app_settings WHERE key = 'pos.catalog.exactness.invalid_prices';"));
            Assert.AreEqual("0", await verifyEvidence.ExecuteScalarAsync<string>(
                "SELECT value FROM app_settings WHERE key = 'pos.catalog.exactness.duplicate_prices';"));
        }
        Assert.IsTrue(await state.StorePullCursorAsync(
            "shop-a",
            "SHOP-A",
            "verified-full-cursor",
            "2026-07-14T03:00:00Z",
            binding.Epoch,
            "full_refresh",
            authoritativeSnapshotCommitted: true));
        await state.StoreSaleSafeAsync(
            "shop-a",
            "SHOP-A",
            "2026-07-14T03:00:00Z",
            binding.Epoch);
        Assert.IsTrue(await state.IsSaleSafeForOfficialShopAsync());
        Assert.AreEqual(CatalogCompletenessStatus.Verified, (await state.LoadExactnessAsync()).Status);

        var mismatchSummary = Summary(products: 3, categories: 1, suppliers: 1, prices: 2);
        mismatchSummary.ActiveProducts = 2;
        var mismatch = CatalogExactnessVerifier.Evaluate(mismatchSummary, audit, context);
        await state.StoreExactnessAsync("shop-a", "SHOP-A", mismatch, binding.Epoch);

        Assert.IsFalse(await state.IsSaleSafeForOfficialShopAsync());
        var persisted = await state.LoadExactnessAsync();
        Assert.AreEqual(CatalogCompletenessStatus.Mismatch, persisted.Status);
        Assert.IsTrue(persisted.RepairRequired);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            state.StorePullCursorAsync(
                "shop-a",
                "SHOP-A",
                "unsafe-full-cursor",
                "2026-07-14T04:00:00Z",
                binding.Epoch,
                "full_refresh",
                authoritativeSnapshotCommitted: true));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            state.StoreSaleSafeAsync(
                "shop-a",
                "SHOP-A",
                "2026-07-14T04:00:00Z",
                binding.Epoch));
    }

    [TestMethod]
    public async Task ObservedAndCommittedRevisions_AreSeparateDurableAndCommitOnlyWithSaleSafety()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db, "shop-a", "SHOP-A");
        var state = new CatalogShopStateRepository(db.Factory);
        var binding = await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");

        await state.StoreObservedRevisionAsync(
            "shop-a",
            "SHOP-A",
            " revision-2 ",
            DateTimeOffset.Parse("2026-07-14T02:00:00Z"),
            binding.Epoch);
        var observedOnly = await new CatalogShopStateRepository(db.Factory)
            .LoadRevisionStateAsync("shop-a", "SHOP-A", binding.Epoch);
        Assert.AreEqual("revision-2", observedOnly.ObservedRevision);
        Assert.AreEqual(string.Empty, observedOnly.CommittedRevision);

        var audit = await CreateCleanAuditAsync(db);
        var verified = CatalogExactnessVerifier.Evaluate(
            Summary(products: 2, categories: 1, suppliers: 1, prices: 2),
            audit,
            CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2));
        await state.StoreExactnessAsync("shop-a", "SHOP-A", verified, binding.Epoch);
        Assert.IsTrue(await state.StorePullCursorAsync(
            "shop-a",
            "SHOP-A",
            "revision-cursor",
            "2026-07-14T03:00:00Z",
            binding.Epoch,
            "full_refresh",
            authoritativeSnapshotCommitted: true));
        await state.StoreSaleSafeAsync(
            "shop-a",
            "SHOP-A",
            "2026-07-14T03:00:00Z",
            binding.Epoch,
            committedRevision: "revision-1");

        var committed = await state.LoadRevisionStateAsync("shop-a", "SHOP-A", binding.Epoch);
        Assert.AreEqual("revision-2", committed.ObservedRevision);
        Assert.AreEqual("revision-1", committed.CommittedRevision);
        Assert.IsFalse(committed.IsMatch);

        var mismatchSummary = Summary(products: 3, categories: 1, suppliers: 1, prices: 2);
        mismatchSummary.ActiveProducts = 2;
        await state.StoreExactnessAsync(
            "shop-a",
            "SHOP-A",
            CatalogExactnessVerifier.Evaluate(
                mismatchSummary,
                audit,
                CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2)),
            binding.Epoch);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => state.StoreSaleSafeAsync(
            "shop-a",
            "SHOP-A",
            "2026-07-14T04:00:00Z",
            binding.Epoch,
            committedRevision: "revision-2"));
        Assert.AreEqual(
            "revision-1",
            (await state.LoadRevisionStateAsync("shop-a", "SHOP-A", binding.Epoch)).CommittedRevision);

        await state.RequestFullRepairAsync("shop-a", "SHOP-A", binding.Epoch);
        var afterRepairBinding = await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        var afterRepair = await state.LoadRevisionStateAsync(
            "shop-a",
            "SHOP-A",
            afterRepairBinding.Epoch);
        Assert.AreEqual("revision-2", afterRepair.ObservedRevision);
        Assert.AreEqual(string.Empty, afterRepair.CommittedRevision);
    }

    [TestMethod]
    public async Task ImportAckWatermark_ReconcilesOnlyWithSaleSafeAndGuardsHeartbeatSkip()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db, "shop-a", "SHOP-A");
        var state = new CatalogShopStateRepository(db.Factory);
        var binding = await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        await state.StoreObservedRevisionAsync(
            "shop-a",
            "SHOP-A",
            "revision-2",
            DateTimeOffset.Parse("2026-07-19T12:00:00Z"),
            binding.Epoch);
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value)
VALUES(@ackKey, '2'), (@reconciledKey, '0')
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new
                {
                    ackKey = CatalogShopStateRepository.ImportAckGenerationKey,
                    reconciledKey = CatalogShopStateRepository.ImportReconciledGenerationKey
                });
        }

        var audit = await CreateCleanAuditAsync(db);
        var verified = CatalogExactnessVerifier.Evaluate(
            Summary(products: 2, categories: 1, suppliers: 1, prices: 2),
            audit,
            CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2));
        await state.StoreExactnessAsync("shop-a", "SHOP-A", verified, binding.Epoch);
        Assert.IsTrue(await state.StorePullCursorAsync(
            "shop-a",
            "SHOP-A",
            "cursor-partial-reconcile",
            "2026-07-19T12:01:00Z",
            binding.Epoch,
            "full_refresh",
            authoritativeSnapshotCommitted: true));
        await state.StoreSaleSafeAsync(
            "shop-a",
            "SHOP-A",
            "2026-07-19T12:01:00Z",
            binding.Epoch,
            committedRevision: "revision-2",
            reconciledImportAckGeneration: 1);

        var partiallyReconciled = await state.LoadRevisionStateAsync(
            "shop-a",
            "SHOP-A",
            binding.Epoch);
        Assert.AreEqual(2L, partiallyReconciled.ImportAckGeneration);
        Assert.AreEqual(1L, partiallyReconciled.ReconciledImportAckGeneration);
        Assert.IsTrue(partiallyReconciled.ImportAckReconciliationPending);
        Assert.IsFalse(await state.TryConfirmCatalogUnchangedAsync(
            "shop-a",
            "SHOP-A",
            binding.Epoch,
            "revision-2",
            "revision-2",
            expectedAckGeneration: 2,
            clearStaleError: true));

        await state.RequestFullRepairAsync("shop-a", "SHOP-A", binding.Epoch);
        binding = await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        var afterRepair = await state.LoadRevisionStateAsync(
            "shop-a",
            "SHOP-A",
            binding.Epoch);
        Assert.AreEqual(2L, afterRepair.ImportAckGeneration);
        Assert.AreEqual(1L, afterRepair.ReconciledImportAckGeneration);

        await state.StoreExactnessAsync("shop-a", "SHOP-A", verified, binding.Epoch);
        Assert.IsTrue(await state.StorePullCursorAsync(
            "shop-a",
            "SHOP-A",
            "cursor-fully-reconciled",
            "2026-07-19T12:02:00Z",
            binding.Epoch,
            "full_refresh",
            authoritativeSnapshotCommitted: true));
        await state.StoreSaleSafeAsync(
            "shop-a",
            "SHOP-A",
            "2026-07-19T12:02:00Z",
            binding.Epoch,
            committedRevision: "revision-2",
            reconciledImportAckGeneration: 2);
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value)
VALUES('pos.catalog.last_error', 'timeout'),
      ('pos.catalog.bootstrap_status', 'failed_retryable'),
      ('pos.catalog.last_has_more', '1')
ON CONFLICT(key) DO UPDATE SET value = excluded.value;");
        }

        Assert.IsTrue(await state.TryConfirmCatalogUnchangedAsync(
            "shop-a",
            "SHOP-A",
            binding.Epoch,
            "revision-2",
            "revision-2",
            expectedAckGeneration: 2,
            clearStaleError: true));
        using (var conn = db.Factory.Open())
        {
            Assert.AreEqual(string.Empty, await conn.ExecuteScalarAsync<string>(
                "SELECT value FROM app_settings WHERE key = 'pos.catalog.last_error';"));
            Assert.AreEqual("completed", await conn.ExecuteScalarAsync<string>(
                "SELECT value FROM app_settings WHERE key = 'pos.catalog.bootstrap_status';"));
            Assert.AreEqual("0", await conn.ExecuteScalarAsync<string>(
                "SELECT value FROM app_settings WHERE key = 'pos.catalog.last_has_more';"));
            await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value)
VALUES(@key, 'orphaned-version')
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new { key = CatalogShopStateRepository.DeltaChainCatalogVersionKey });
        }

        Assert.IsFalse(await state.TryConfirmCatalogUnchangedAsync(
            "shop-a",
            "SHOP-A",
            binding.Epoch,
            "revision-2",
            "revision-2",
            expectedAckGeneration: 2,
            clearStaleError: true));
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(
                "DELETE FROM app_settings WHERE key = @key;",
                new { key = CatalogShopStateRepository.DeltaChainCatalogVersionKey });
            await conn.ExecuteAsync(@"
UPDATE app_settings
SET value = @code
WHERE key = 'pos.catalog.last_error';",
                new { code = CatalogPaginationSafetyPolicy.AmbiguousEndCode });
        }

        Assert.IsFalse(await state.TryConfirmCatalogUnchangedAsync(
            "shop-a",
            "SHOP-A",
            binding.Epoch,
            "revision-2",
            "revision-2",
            expectedAckGeneration: 2,
            clearStaleError: true));
    }

    [TestMethod]
    public async Task ImportAckWatermark_CorruptOrReversedStateFailsClosed()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db, "shop-a", "SHOP-A");
        var state = new CatalogShopStateRepository(db.Factory);
        var binding = await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");

        async Task<CatalogRevisionState> WriteAndLoadAsync(string ack, string reconciled)
        {
            using (var conn = db.Factory.Open())
            {
                await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value)
VALUES(@ackKey, @ack), (@reconciledKey, @reconciled)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                    new
                    {
                        ack,
                        ackKey = CatalogShopStateRepository.ImportAckGenerationKey,
                        reconciled,
                        reconciledKey = CatalogShopStateRepository.ImportReconciledGenerationKey
                    });
            }

            return await state.LoadRevisionStateAsync(
                "shop-a",
                "SHOP-A",
                binding.Epoch);
        }

        var malformed = await WriteAndLoadAsync("not-a-number", "0");
        Assert.IsFalse(malformed.ImportAckStateValid);
        Assert.IsTrue(malformed.ImportAckReconciliationPending);

        var reversed = await WriteAndLoadAsync("1", "2");
        Assert.IsFalse(reversed.ImportAckStateValid);
        Assert.IsTrue(reversed.ImportAckReconciliationPending);
    }

    [TestMethod]
    public async Task UnverifiedEvidence_IsShopBoundAndCannotBecomeSaleSafe()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db, "shop-a", "SHOP-A");
        var state = new CatalogShopStateRepository(db.Factory);
        var binding = await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        var audit = await CreateCleanAuditAsync(db);
        var legacy = CatalogExactnessVerifier.Evaluate(
            null,
            audit,
            CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2));

        await state.StoreExactnessAsync("shop-a", "SHOP-A", legacy, binding.Epoch);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            state.StoreSaleSafeAsync(
                "shop-a",
                "SHOP-A",
                "2026-07-14T03:00:00Z",
                binding.Epoch));

        Assert.AreEqual(CatalogCompletenessStatus.Unverified, (await state.LoadExactnessAsync()).Status);
        Assert.IsFalse(await state.IsSaleSafeForOfficialShopAsync());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            state.StoreExactnessAsync("shop-b", "SHOP-B", legacy, binding.Epoch));
    }

    [TestMethod]
    public async Task FullRepairRequest_WaitsForBarrierAndAtomicallyPreservesBindingAndEpoch()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db, "shop-a", "SHOP-A");
        var state = new CatalogShopStateRepository(db.Factory);
        var binding = await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        await state.StoreLastSyncAsync(
            "shop-a",
            "SHOP-A",
            "cursor-before-repair",
            "2026-07-14T03:00:00Z",
            binding.Epoch,
            "delta");
        await CatalogExactnessTestFixture.SeedVerifiedAsync(
            db.Factory,
            "shop-a",
            "SHOP-A");
        await state.StoreSaleSafeAsync("shop-a", "SHOP-A", "2026-07-14T03:00:00Z", binding.Epoch);

        var lease = await new CatalogShopTransitionBarrier(db.Factory).EnterAsync();
        var repair = state.RequestFullRepairAsync("shop-a", "SHOP-A", binding.Epoch);
        await Task.Delay(50);
        Assert.IsFalse(repair.IsCompleted, "The repair reset must serialize behind the catalog transition barrier.");
        lease.Dispose();
        await repair;

        var after = await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        Assert.IsTrue(after.Epoch > binding.Epoch);
        Assert.AreEqual(string.Empty, after.Cursor);
        using var verify = db.Factory.Open();
        Assert.AreEqual("SHOP-A", await verify.ExecuteScalarAsync<string>(
            "SELECT value FROM app_settings WHERE key = 'pos.catalog.bound_shop_code';"));
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM app_settings WHERE key IN ('pos.catalog.sale_safe_at', 'pos.catalog.initial_completed_at');"));
        var exactness = await state.LoadExactnessAsync();
        Assert.AreEqual(CatalogCompletenessStatus.Unverified, exactness.Status);
        Assert.AreEqual("catalog_full_repair_requested", exactness.Code);
        Assert.IsTrue(exactness.RepairRequired);
    }

    [TestMethod]
    public async Task FullRepairTransactionOnlyApi_CompletesWhenCallerAlreadyOwnsBarrier()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db, "shop-a", "SHOP-A");
        var state = new CatalogShopStateRepository(db.Factory);
        var binding = await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        await state.StoreLastSyncAsync(
            "shop-a",
            "SHOP-A",
            "cursor-before-repair",
            "2026-07-14T03:00:00Z",
            binding.Epoch,
            "delta");

        using (await new CatalogShopTransitionBarrier(db.Factory).EnterAsync())
        {
            var repair = state.RequestFullRepairWhileBarrierHeldAsync(
                "shop-a",
                "SHOP-A",
                binding.Epoch);
            Assert.AreSame(repair, await Task.WhenAny(repair, Task.Delay(1000)),
                "The transaction-only API must not attempt to reacquire the caller-owned barrier.");
            await repair;
        }

        var after = await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        var exactness = await state.LoadExactnessAsync();
        Assert.IsTrue(after.Epoch > binding.Epoch);
        Assert.AreEqual(string.Empty, after.Cursor);
        Assert.AreEqual(CatalogCompletenessStatus.Unverified, exactness.Status);
        Assert.AreEqual("catalog_full_repair_requested", exactness.Code);
        Assert.IsTrue(exactness.RepairRequired);
    }

    [TestMethod]
    public async Task DeltaCheckpoint_PersistsSnapshotPinsAcrossRunsAndClearsAtBoundary()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db, "shop-a", "SHOP-A");
        var stateRepository = new CatalogShopStateRepository(db.Factory);
        var binding = await stateRepository.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        var cursorA = CatalogShopStateRepository.FingerprintValue("delta-cursor-a");
        var cursorB = CatalogShopStateRepository.FingerprintValue("delta-cursor-b");
        var summary = CatalogShopStateRepository.FingerprintValue("summary-v1");

        Assert.IsTrue(await stateRepository.StorePullCursorAsync(
            "shop-a",
            "SHOP-A",
            "delta-cursor-b",
            "2026-07-14T03:00:00Z",
            binding.Epoch,
            "delta",
            authoritativeSnapshotCommitted: false,
            new CatalogDeltaChainCheckpoint
            {
                CatalogVersion = "catalog-v1",
                CursorFingerprints = new[] { cursorA, cursorB },
                HasMore = true,
                SummaryFingerprint = summary,
                SummaryPinned = true,
                SyncMode = "delta"
            }));

        var persisted = await stateRepository.LoadDeltaChainAsync(
            "shop-a",
            "SHOP-A",
            binding.Epoch);
        Assert.IsTrue(persisted.IsValid);
        Assert.AreEqual(string.Empty, persisted.Code);
        Assert.IsTrue(persisted.HasState);
        CollectionAssert.AreEquivalent(
            new[] { cursorA, cursorB },
            persisted.CursorFingerprints.ToArray());
        Assert.AreEqual(string.Empty, persisted.GetSnapshotMismatchCode(
            "catalog-v1", summary, true, "delta"));
        Assert.AreEqual("catalog_version_changed_across_runs", persisted.GetSnapshotMismatchCode(
            "catalog-v2", summary, true, "delta"));
        Assert.AreEqual("catalog_summary_changed_across_runs", persisted.GetSnapshotMismatchCode(
            "catalog-v1", CatalogShopStateRepository.FingerprintValue("summary-v2"), true, "delta"));
        Assert.AreEqual("catalog_summary_missing_across_runs", persisted.GetSnapshotMismatchCode(
            "catalog-v1", string.Empty, false, "delta"));
        Assert.AreEqual("catalog_sync_mode_changed_across_runs", persisted.GetSnapshotMismatchCode(
            "catalog-v1", summary, true, "full_refresh"));

        Assert.IsTrue(await stateRepository.StorePullCursorAsync(
            "shop-a",
            "SHOP-A",
            "delta-final",
            "2026-07-14T03:01:00Z",
            binding.Epoch,
            "delta",
            authoritativeSnapshotCommitted: false,
            new CatalogDeltaChainCheckpoint
            {
                CatalogVersion = "catalog-v1",
                CursorFingerprints = new[]
                {
                    cursorA,
                    cursorB,
                    CatalogShopStateRepository.FingerprintValue("delta-final")
                },
                HasMore = false,
                SummaryFingerprint = summary,
                SummaryPinned = true,
                SyncMode = "delta"
            }));
        Assert.IsFalse((await stateRepository.LoadDeltaChainAsync(
            "shop-a",
            "SHOP-A",
            binding.Epoch)).HasState);
    }

    [TestMethod]
    public async Task DeltaCheckpoint_CorruptionIsReportedWithoutDiscardingIndividualBadFields()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db, "shop-a", "SHOP-A");
        var repository = new CatalogShopStateRepository(db.Factory);
        var binding = await repository.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        var cursorFingerprint = CatalogShopStateRepository.FingerprintValue("delta-cursor");

        async Task StoreValidAsync()
        {
            Assert.IsTrue(await repository.StorePullCursorAsync(
                "shop-a",
                "SHOP-A",
                "delta-cursor",
                "2026-07-14T03:00:00Z",
                binding.Epoch,
                "delta",
                authoritativeSnapshotCommitted: false,
                new CatalogDeltaChainCheckpoint
                {
                    CatalogVersion = "catalog-v1",
                    CursorFingerprints = new[] { cursorFingerprint },
                    HasMore = true,
                    SummaryFingerprint = string.Empty,
                    SummaryPinned = false,
                    SyncMode = "delta"
                }));
        }

        async Task AssertInvalidAsync(string expectedCode)
        {
            var state = await repository.LoadDeltaChainAsync("shop-a", "SHOP-A", binding.Epoch);
            Assert.IsFalse(state.IsValid);
            Assert.IsFalse(state.HasState);
            Assert.AreEqual(expectedCode, state.Code);
        }

        await StoreValidAsync();
        var legacyWithoutSummary = await repository.LoadDeltaChainAsync(
            "shop-a",
            "SHOP-A",
            binding.Epoch);
        Assert.IsTrue(legacyWithoutSummary.IsValid);
        Assert.AreEqual(string.Empty, legacyWithoutSummary.GetSnapshotMismatchCode(
            "catalog-v1",
            string.Empty,
            summaryPresent: false,
            syncMode: "delta"));
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(
                "DELETE FROM app_settings WHERE key = @key;",
                new { key = CatalogShopStateRepository.DeltaChainSummaryPinnedKey });
        }
        await AssertInvalidAsync(CatalogShopStateRepository.DeltaChainPartialCode);

        await StoreValidAsync();
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(
                "UPDATE app_settings SET value = 'true' WHERE key = @key;",
                new { key = CatalogShopStateRepository.DeltaChainActiveKey });
        }
        await AssertInvalidAsync(CatalogShopStateRepository.DeltaChainActiveInvalidCode);

        await StoreValidAsync();
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(
                "UPDATE app_settings SET value = 'true' WHERE key = @key;",
                new { key = CatalogShopStateRepository.DeltaChainSummaryPinnedKey });
        }
        await AssertInvalidAsync(CatalogShopStateRepository.DeltaChainSummaryInvalidCode);

        await StoreValidAsync();
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(
                "UPDATE app_settings SET value = value || ';not-a-fingerprint' WHERE key = @key;",
                new { key = CatalogShopStateRepository.DeltaChainCursorFingerprintsKey });
        }
        await AssertInvalidAsync(CatalogShopStateRepository.DeltaChainCursorEvidenceInvalidCode);

        await StoreValidAsync();
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(
                "UPDATE app_settings SET value = @value WHERE key = @key;",
                new
                {
                    key = CatalogShopStateRepository.DeltaChainCatalogVersionKey,
                    value = new string('v', 129)
                });
        }
        await AssertInvalidAsync(CatalogShopStateRepository.DeltaChainCatalogVersionInvalidCode);
    }

    [TestMethod]
    public async Task DeltaCheckpoint_NonCanonicalVersionCannotAdvanceCursor()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db, "shop-a", "SHOP-A");
        var repository = new CatalogShopStateRepository(db.Factory);
        var binding = await repository.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        await repository.StoreLastSyncAsync(
            "shop-a",
            "SHOP-A",
            "cursor-before-invalid-version",
            "2026-07-14T03:00:00Z",
            binding.Epoch,
            "delta");

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            repository.StorePullCursorAsync(
                "shop-a",
                "SHOP-A",
                "cursor-after-invalid-version",
                "2026-07-14T03:01:00Z",
                binding.Epoch,
                "delta",
                authoritativeSnapshotCommitted: false,
                new CatalogDeltaChainCheckpoint
                {
                    CatalogVersion = " catalog-v1 ",
                    CursorFingerprints = new[]
                    {
                        CatalogShopStateRepository.FingerprintValue("cursor-after-invalid-version")
                    },
                    HasMore = true,
                    SyncMode = "delta"
                }));
        StringAssert.Contains(
            exception.Message,
            CatalogShopStateRepository.DeltaChainCatalogVersionInvalidCode);

        var afterFailure = await repository.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        Assert.AreEqual("cursor-before-invalid-version", afterFailure.Cursor);
        var checkpoint = await repository.LoadDeltaChainAsync("shop-a", "SHOP-A", binding.Epoch);
        Assert.IsTrue(checkpoint.IsValid);
        Assert.IsFalse(checkpoint.HasState);
    }

    [TestMethod]
    public async Task LegacyUnsafeCursorAndMode_CanBeAtomicallyReplacedByValidState()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db, "shop-a", "SHOP-A");
        var repository = new CatalogShopStateRepository(db.Factory);
        var binding = await repository.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        var legacyCursor = " " + new string('c', 600) + " ";
        var legacyMode = " " + new string('m', 80) + " ";
        using (var seed = db.Factory.Open())
        {
            await seed.ExecuteAsync(@"
INSERT INTO app_settings(key, value)
VALUES(@cursorKey, @legacyCursor), (@modeKey, @legacyMode)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new
                {
                    cursorKey = CatalogShopStateRepository.LastSyncCursorKey,
                    legacyCursor,
                    legacyMode,
                    modeKey = CatalogShopStateRepository.LastSyncModeKey
                });
        }

        var legacy = await repository.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        await repository.StoreLastSyncAsync(
            "shop-a",
            "SHOP-A",
            "cursor-repaired",
            "2026-07-21T12:00:00Z",
            binding.Epoch,
            "full_refresh",
            expectedPreviousCursor: legacy.Cursor,
            expectedPreviousMode: legacy.Mode);

        var repaired = await repository.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        Assert.AreEqual("cursor-repaired", repaired.Cursor);
        Assert.AreEqual("full_refresh", repaired.Mode);
    }

    [TestMethod]
    public async Task InvalidNewCursor_IsRejectedBeforeCatalogCheckpointMutation()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db, "shop-a", "SHOP-A");
        var repository = new CatalogShopStateRepository(db.Factory);
        var binding = await repository.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        await repository.StoreLastSyncAsync(
            "shop-a",
            "SHOP-A",
            "cursor-before-invalid",
            "2026-07-21T12:00:00Z",
            binding.Epoch,
            "delta");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            repository.StorePullCursorAsync(
                "shop-a",
                "SHOP-A",
                "cursor-invalid\uD800",
                "2026-07-21T12:01:00Z",
                binding.Epoch,
                "delta",
                authoritativeSnapshotCommitted: false,
                expectedPreviousCursor: "cursor-before-invalid",
                expectedPreviousMode: "delta"));

        var retained = await repository.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        Assert.AreEqual("cursor-before-invalid", retained.Cursor);
        Assert.AreEqual("delta", retained.Mode);
    }

    [TestMethod]
    public async Task DeltaCheckpoint_CursorLimitRejectsAndRollsBackCursorAtomically()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db, "shop-a", "SHOP-A");
        var repository = new CatalogShopStateRepository(db.Factory);
        var binding = await repository.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        var acceptedVersion = new string('v', 128);
        var acceptedFingerprints = Enumerable.Range(
                0,
                CatalogShopStateRepository.MaxDeltaChainCursorFingerprints - 1)
            .Select(index => CatalogShopStateRepository.FingerprintValue("cursor-" + index))
            .ToArray();

        Assert.IsTrue(await repository.StorePullCursorAsync(
            "shop-a",
            "SHOP-A",
            "cursor-at-limit",
            "2026-07-14T03:00:00Z",
            binding.Epoch,
            "delta",
            authoritativeSnapshotCommitted: false,
            new CatalogDeltaChainCheckpoint
            {
                CatalogVersion = acceptedVersion,
                CursorFingerprints = acceptedFingerprints,
                HasMore = true,
                SyncMode = "delta"
            }));

        var atLimit = await repository.LoadDeltaChainAsync("shop-a", "SHOP-A", binding.Epoch);
        Assert.IsTrue(atLimit.IsValid);
        Assert.AreEqual(acceptedVersion, atLimit.CatalogVersion);
        Assert.AreEqual(
            CatalogShopStateRepository.MaxDeltaChainCursorFingerprints,
            atLimit.CursorFingerprints.Count);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            repository.StorePullCursorAsync(
                "shop-a",
                "SHOP-A",
                "cursor-over-limit",
                "2026-07-14T03:01:00Z",
                binding.Epoch,
                "delta",
                authoritativeSnapshotCommitted: false,
                new CatalogDeltaChainCheckpoint
                {
                    CatalogVersion = acceptedVersion,
                    CursorFingerprints = atLimit.CursorFingerprints,
                    HasMore = true,
                    SyncMode = "delta"
                }));
        StringAssert.Contains(exception.Message, CatalogShopStateRepository.DeltaChainCursorLimitCode);

        var afterFailure = await repository.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        Assert.AreEqual("cursor-at-limit", afterFailure.Cursor);
        var checkpointAfterFailure = await repository.LoadDeltaChainAsync(
            "shop-a",
            "SHOP-A",
            binding.Epoch);
        Assert.IsTrue(checkpointAfterFailure.IsValid);
        Assert.AreEqual(
            CatalogShopStateRepository.MaxDeltaChainCursorFingerprints,
            checkpointAfterFailure.CursorFingerprints.Count);
    }

    private static async Task<AuthoritativeStageContext> CreateAuthoritativeStageAsync(
        TestDb db,
        string suffix)
    {
        var shopId = "shop-" + suffix;
        var shopCode = "SHOP-" + suffix.ToUpperInvariant();
        await SaveShopAsync(db, shopId, shopCode);
        var binding = await new CatalogShopStateRepository(db.Factory)
            .EnsureAndLoadCursorAsync(shopId, shopCode);
        var generation = new OnlineSyncGeneration(
            "generation-" + suffix,
            "session-" + suffix,
            "device-" + suffix,
            shopId,
            shopCode);
        await new OnlineSyncGenerationRepository(db.Factory)
            .ActivateAndRecoverAsync(generation, 1);

        return new AuthoritativeStageContext
        {
            Fence = new RemoteCatalogCommitFence
            {
                ExpectedEpoch = binding.Epoch,
                ExpectedPreviousCursor = binding.Cursor,
                ExpectedPreviousMode = binding.Mode,
                GenerationFingerprint = generation.Fingerprint,
                GenerationId = generation.GenerationId,
                PosSessionId = generation.PosSessionId,
                ShopCode = shopCode,
                ShopDeviceId = generation.ShopDeviceId,
                ShopId = shopId
            },
            FullRunId = "full-" + suffix,
            Generation = generation,
            ShopCode = shopCode,
            ShopId = shopId
        };
    }

    private static async Task AssertPreflightPreservedStateAsync(
        TestDb db,
        AuthoritativeStageContext stage,
        CatalogShopBindingResult before,
        string activeProductId)
    {
        var after = await new CatalogShopStateRepository(db.Factory)
            .EnsureAndLoadCursorAsync(
                stage.ShopId,
                stage.ShopCode,
                stage.Generation);
        Assert.AreEqual(before.Epoch, after.Epoch);
        Assert.AreEqual(before.Cursor, after.Cursor);
        Assert.AreEqual(before.Mode, after.Mode);
        Assert.AreEqual(stage.Fence.ExpectedEpoch, after.Epoch);
        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await verify.ExecuteScalarAsync<long>(@"
SELECT is_active
FROM products
WHERE remote_product_id = @activeProductId;",
            new { activeProductId }));
    }

    private static RemoteCatalogBatch StageBatch(
        string fullRunId,
        int pageNumber,
        bool hasMore,
        string productId,
        string barcode,
        string priceId)
    {
        return new RemoteCatalogBatch
        {
            AuthoritativeFullRefresh = true,
            AuthoritativeStagePage = new CatalogAuthoritativeStagePage
            {
                FullRunId = fullRunId,
                HasMore = hasMore,
                PageNumber = pageNumber
            },
            Categories = new[]
            {
                new RemoteCatalogCategoryWrite
                {
                    Name = "Category",
                    RemoteCategoryId = "category-1",
                    RemoteUpdatedAt = "2026-07-19T01:00:00Z"
                }
            },
            Suppliers = new[]
            {
                new RemoteCatalogSupplierWrite
                {
                    Name = "Supplier",
                    RemoteSupplierId = "supplier-1",
                    RemoteUpdatedAt = "2026-07-19T01:00:00Z"
                }
            },
            Products = new[]
            {
                new RemoteCatalogProductWrite
                {
                    Barcode = barcode,
                    CategoryName = "Category",
                    Name = "Product " + barcode,
                    RemoteCategoryId = "category-1",
                    RemoteProductId = productId,
                    RemoteSupplierId = "supplier-1",
                    StockQuantity = 1,
                    SupplierName = "Supplier",
                    UnitPrice = 100
                }
            },
            Prices = new[]
            {
                new RemoteCatalogPriceWrite
                {
                    EffectiveAt = "2026-07-19T01:00:00Z",
                    Price = 100,
                    RemotePriceId = priceId,
                    RemoteProductId = productId,
                    Source = "catalog_pull",
                    Type = "retail"
                }
            }
        };
    }

    private static async Task SeedUnrelatedRemoteProductAsync(
        TestDb db,
        string productId,
        string barcode)
    {
        using var conn = db.Factory.Open();
        await conn.ExecuteAsync(@"
INSERT INTO products(barcode, name, unitPrice, remote_product_id, is_active)
VALUES(@barcode, 'Must stay active', 100, @productId, 1);
INSERT INTO product_meta(barcode, stock_qty)
VALUES(@barcode, 1);",
            new { barcode, productId });
    }

    private static async Task<CatalogFullRefreshResult> CreateCleanAuditAsync(TestDb db)
    {
        await SeedCleanCatalogAsync(db);
        return await new CatalogFullRefreshReconciler(db.Factory).ReconcileAsync(
            new[] { "product-1", "product-2" },
            new[] { "category-1" },
            new[] { "supplier-1" },
            "2026-07-14T01:00:00Z");
    }

    private static async Task SeedCleanCatalogAsync(TestDb db)
    {
        using var conn = db.Factory.Open();
        await conn.ExecuteAsync(@"
INSERT OR IGNORE INTO categories(id, name, remote_category_id, is_active)
VALUES(101, 'Category', 'category-1', 1);
INSERT OR IGNORE INTO suppliers(id, name, remote_supplier_id, is_active)
VALUES(201, 'Supplier', 'supplier-1', 1);
INSERT OR IGNORE INTO products(barcode, name, unitPrice, remote_product_id, is_active)
VALUES('BARCODE-1', 'Product 1', 100, 'product-1', 1),
      ('BARCODE-2', 'Product 2', 200, 'product-2', 1);
INSERT OR IGNORE INTO product_meta(barcode, category_id, supplier_id)
VALUES('BARCODE-1', 101, 201),
      ('BARCODE-2', 101, 201);
INSERT OR IGNORE INTO remote_catalog_product_references(
  remote_product_id, remote_category_id, remote_supplier_id)
VALUES('product-1', 'category-1', 'supplier-1'),
      ('product-2', 'category-1', 'supplier-1');");
    }

    private static CatalogExactnessRunContext CompleteContext(
        long products,
        long categories,
        long suppliers,
        long prices)
    {
        return new CatalogExactnessRunContext
        {
            CatalogVersion = "catalog-version-1",
            CategoryRowsReceived = categories,
            DurationMilliseconds = 1000,
            HasMore = false,
            Pages = 1,
            PriceRowsAccepted = prices,
            PriceRowsReceived = prices,
            ProductRowsReceived = products,
            SupplierRowsReceived = suppliers,
            SyncCursor = "final-cursor",
            SyncMode = "full_refresh"
        };
    }

    private static PosCatalogSummaryResponse Summary(
        long products,
        long categories,
        long suppliers,
        long prices)
    {
        return new PosCatalogSummaryResponse
        {
            ActiveProducts = products,
            Categories = categories,
            Prices = prices,
            Products = products,
            Suppliers = suppliers
        };
    }

    private static PosCatalogProductResponse ValidProductRow(string productId, string barcode)
    {
        return new PosCatalogProductResponse
        {
            Barcode = barcode,
            ProductId = productId,
            ProductName = "Product " + barcode,
            RetailPrice = 100
        };
    }

    private static PosCatalogPullResponse ValidCatalogResponse()
    {
        return new PosCatalogPullResponse
        {
            Catalog = new PosCatalogPayload(),
            CatalogVersion = "catalog-v1",
            Ok = true,
            Policy = ValidPolicy(),
            SchemaVersion = PosOnlineContract.CatalogPullSchemaVersion,
            SyncMode = "full_refresh",
            SyncCursor = "cursor-v1"
        };
    }

    private static PosPolicyResponse ValidPolicy()
    {
        return new PosPolicyResponse
        {
            Capabilities = new PosPolicyCapabilitiesResponse
            {
                CatalogPull = PosOnlineContract.CatalogCapabilityVersion,
                OfflineSales = true,
                SalesSync = PosOnlineContract.SalesSchemaVersion
            },
            ContractVersion = PosOnlineContract.PolicyContractVersion,
            PaymentPolicy = new PosPaymentPolicyResponse
            {
                Currency = "CLP",
                SupportedMethods = new[] { PosOnlineContract.PaymentCash, PosOnlineContract.PaymentCard }
            }
        };
    }

    private static async Task SaveShopAsync(TestDb db, string shopId, string shopCode)
    {
        using var conn = db.Factory.Open();
        await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value)
VALUES(@IdKey, @shopId), (@CodeKey, @shopCode)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
            new
            {
                CodeKey = OutboxShopBinding.OfficialShopCodeKey,
                IdKey = OutboxShopBinding.OfficialShopIdKey,
                shopCode,
                shopId
            });
    }

    private static T Deserialize<T>(string json) where T : class
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (T)serializer.ReadObject(stream)!;
    }

    private static string Serialize<T>(T value) where T : class
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class AuthoritativeStageContext
    {
        public RemoteCatalogCommitFence Fence { get; set; } = null!;
        public string FullRunId { get; set; } = string.Empty;
        public OnlineSyncGeneration Generation { get; set; } = null!;
        public string ShopCode { get; set; } = string.Empty;
        public string ShopId { get; set; } = string.Empty;
    }

    private sealed class TestDb : IDisposable
    {
        private TestDb(string root)
        {
            Root = root;
            var options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));
            Factory = new SqliteConnectionFactory(options);
            DbInitializer.EnsureCreated(options);
        }

        public SqliteConnectionFactory Factory { get; }
        private string Root { get; }

        public static TestDb Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "win7pos-exactness-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestDb(root);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
