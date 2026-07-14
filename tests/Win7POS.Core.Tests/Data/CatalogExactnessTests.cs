using System.Runtime.Serialization.Json;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CatalogExactnessTests
{
    [TestMethod]
    public void CatalogSummaryContract_IsOptionalAndRoundTripsAuthoritativeEvidence()
    {
        var legacy = Deserialize<PosCatalogPullResponse>(@"{
  ""schemaVersion"": 2,
  ""syncMode"": ""full_refresh""
}");
        legacy.Policy = ValidPolicy();

        Assert.IsNull(legacy.CatalogSummary);
        Assert.AreEqual(string.Empty, PosOnlineCompatibilityValidator.ValidateCatalogPull(legacy));

        var response = ValidCatalogResponse();
        response.CatalogSummary = Summary(products: 2, categories: 1, suppliers: 1, prices: 3);
        response.CatalogSummary.Checksum = "checksum-value";
        response.CatalogSummary.ChecksumAlgorithm = "sha256";
        var json = Serialize(response);
        var roundTrip = Deserialize<PosCatalogPullResponse>(json);

        StringAssert.Contains(json, "\"catalogSummary\"");
        Assert.AreEqual(2L, roundTrip.CatalogSummary.Products);
        Assert.AreEqual(2L, roundTrip.CatalogSummary.ActiveProducts);
        Assert.AreEqual(1L, roundTrip.CatalogSummary.Categories);
        Assert.AreEqual(1L, roundTrip.CatalogSummary.Suppliers);
        Assert.AreEqual(3L, roundTrip.CatalogSummary.Prices);
        Assert.AreEqual("checksum-value", roundTrip.CatalogSummary.Checksum);
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

        summary.Checksum = "expected-checksum";
        summary.ChecksumAlgorithm = "sha256";
        var noChecksum = CatalogExactnessVerifier.Evaluate(
            summary,
            audit,
            CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2));
        Assert.AreEqual(CatalogCompletenessStatus.Unverified, noChecksum.Status);
        Assert.AreEqual("catalog_checksum_unverifiable", noChecksum.Code);

        var mismatchContext = CompleteContext(products: 2, categories: 1, suppliers: 1, prices: 2);
        mismatchContext.ActualChecksum = "other-checksum";
        mismatchContext.ActualChecksumAlgorithm = "sha256";
        var checksumMismatch = CatalogExactnessVerifier.Evaluate(summary, audit, mismatchContext);
        Assert.AreEqual(CatalogCompletenessStatus.Mismatch, checksumMismatch.Status);
        Assert.AreEqual("catalog_checksum_mismatch", checksumMismatch.Code);

        mismatchContext.ActualChecksum = "expected-checksum";
        var checksumVerified = CatalogExactnessVerifier.Evaluate(summary, audit, mismatchContext);
        Assert.AreEqual(CatalogCompletenessStatus.Verified, checksumVerified.Status);
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
            state.StoreSaleSafeAsync(
                "shop-a",
                "SHOP-A",
                "2026-07-14T04:00:00Z",
                binding.Epoch));
    }

    [TestMethod]
    public async Task LegacyUnverifiedEvidence_IsShopBoundButBackwardCompatibleForSaleSafety()
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
        await state.StoreSaleSafeAsync("shop-a", "SHOP-A", "2026-07-14T03:00:00Z", binding.Epoch);

        Assert.AreEqual(CatalogCompletenessStatus.Unverified, (await state.LoadExactnessAsync()).Status);
        Assert.IsTrue(await state.IsSaleSafeForOfficialShopAsync());
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
        await state.StoreSaleSafeAsync("shop-a", "SHOP-A", "2026-07-14T03:00:00Z", binding.Epoch);

        var lease = await new CatalogShopTransitionBarrier(db.Factory).EnterAsync();
        var repair = state.RequestFullRepairAsync("shop-a", "SHOP-A", binding.Epoch);
        await Task.Delay(50);
        Assert.IsFalse(repair.IsCompleted, "The repair reset must serialize behind the catalog transition barrier.");
        lease.Dispose();
        await repair;

        var after = await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        Assert.AreEqual(binding.Epoch, after.Epoch);
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
        Assert.AreEqual(binding.Epoch, after.Epoch);
        Assert.AreEqual(string.Empty, after.Cursor);
        Assert.AreEqual(CatalogCompletenessStatus.Unverified, exactness.Status);
        Assert.AreEqual("catalog_full_repair_requested", exactness.Code);
        Assert.IsTrue(exactness.RepairRequired);
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
      ('BARCODE-2', 101, 201);");
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

    private static PosCatalogPullResponse ValidCatalogResponse()
    {
        return new PosCatalogPullResponse
        {
            Policy = ValidPolicy(),
            SchemaVersion = PosOnlineContract.CatalogPullSchemaVersion,
            SyncMode = "full_refresh"
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
