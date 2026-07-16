using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CatalogSafetyInvariantTests
{
    [TestMethod]
    public async Task LegacyCursorWithoutBinding_IsDiscardedAndForcesFullBootstrap()
    {
        using var db = TestDb.Create();
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES('pos.catalog.last_sync_cursor', 'legacy-cursor');
INSERT INTO app_settings(key, value) VALUES('pos.catalog.sale_safe_at', '2026-07-14T00:00:00Z');");
        }

        var result = await new CatalogShopStateRepository(db.Factory)
            .EnsureAndLoadCursorAsync("shop-a", "SHOP-A");

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(string.Empty, result.Cursor);
        using var verify = db.Factory.Open();
        Assert.AreEqual("SHOP-A", await verify.ExecuteScalarAsync<string>(
            "SELECT value FROM app_settings WHERE key = 'pos.catalog.bound_shop_code';"));
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM app_settings WHERE key = 'pos.catalog.sale_safe_at';"));
    }

    [TestMethod]
    public async Task TransitionBarrierAndEpoch_PreventLateCatalogWrite()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var state = new CatalogShopStateRepository(db.Factory);
        var binding = await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        var guard = new PosShopTransitionGuard(db.Factory);
        var decision = await guard.EvaluateAsync("shop-a", "SHOP-A", "shop-b", "SHOP-B");
        Assert.IsTrue(decision.Allowed);

        var lease = await new CatalogShopTransitionBarrier(db.Factory).EnterAsync();
        var transition = guard.ApplyAuthorizedTransitionAsync(decision);
        await Task.Delay(50);
        Assert.IsFalse(transition.IsCompleted);
        lease.Dispose();
        await transition;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            state.StoreLastSyncAsync(
                "shop-a",
                "SHOP-A",
                "late-a",
                "2026-07-14T01:00:00Z",
                binding.Epoch));
    }

    [TestMethod]
    public async Task TransitionLease_CoversResetUntilDestinationIdentityIsCommitted()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var guard = new PosShopTransitionGuard(db.Factory);
        var decision = await guard.EvaluateAsync("shop-a", "SHOP-A", "shop-b", "SHOP-B");
        Assert.IsTrue(decision.Allowed);

        using var transitionLease = await guard.ApplyAuthorizedTransitionAndHoldAsync(decision);
        var catalogPull = new CatalogShopTransitionBarrier(db.Factory).EnterAsync();
        await Task.Delay(50);
        Assert.IsFalse(catalogPull.IsCompleted, "A catalog pull must not enter between reset and identity commit.");

        await SaveShopAsync(db.Factory, "shop-b", "SHOP-B");
        transitionLease.Dispose();
        using (await catalogPull)
        {
            var snapshot = await new ShopOfficialSnapshotRepository(db.Factory).GetAsync();
            Assert.AreEqual("shop-b", snapshot.ShopId);
            Assert.AreEqual("SHOP-B", snapshot.ShopCode);
        }
    }

    [TestMethod]
    public async Task CapturedSession_IsRejectedAfterWaitingBehindShopTransition()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var capturedShopId = "shop-a";
        var capturedShopCode = "SHOP-A";
        var guard = new PosShopTransitionGuard(db.Factory);
        var decision = await guard.EvaluateAsync(
            capturedShopId,
            capturedShopCode,
            "shop-b",
            "SHOP-B");
        using var transitionLease = await guard.ApplyAuthorizedTransitionAndHoldAsync(decision);
        var attemptedGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var networkCalled = false;
        var applyCalled = false;

        var stalePull = Task.Run(async () =>
        {
            var enterBarrier = new CatalogShopTransitionBarrier(db.Factory).EnterAsync();
            attemptedGate.TrySetResult(true);
            using (await enterBarrier)
            {
                var state = new CatalogShopStateRepository(db.Factory);
                var validationCode = await state.ValidateCapturedSessionAsync(
                    capturedShopId,
                    capturedShopCode);
                if (!string.IsNullOrWhiteSpace(validationCode))
                {
                    return validationCode;
                }

                networkCalled = true;
                await state.EnsureAndLoadCursorAsync(capturedShopId, capturedShopCode);
                applyCalled = true;
                return string.Empty;
            }
        });

        await attemptedGate.Task;
        Assert.IsFalse(stalePull.IsCompleted, "Captured session A must still be waiting behind the transition barrier.");
        await SaveShopAsync(db.Factory, "shop-b", "SHOP-B");
        transitionLease.Dispose();

        Assert.AreEqual("catalog_session_shop_changed", await stalePull);
        Assert.IsFalse(networkCalled);
        Assert.IsFalse(applyCalled);
        var official = await new ShopOfficialSnapshotRepository(db.Factory).GetAsync();
        Assert.AreEqual("shop-b", official.ShopId);
        Assert.AreEqual("SHOP-B", official.ShopCode);
        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM app_settings
WHERE key = 'pos.catalog.bound_shop_code'
  AND UPPER(TRIM(value)) = 'SHOP-A';"));
    }

    [TestMethod]
    public async Task FullRefresh_ReconcilesMissingRemoteRowsButPreservesLocalRows()
    {
        using var db = TestDb.Create();
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
INSERT INTO products(barcode, name, unitPrice, remote_product_id, is_active)
VALUES('P-KEEP', 'Keep', 100, 'product-keep', 1),
      ('P-OLD', 'Old', 100, 'product-old', 1),
      ('P-LOCAL', 'Local', 100, NULL, 1);
INSERT INTO categories(name, remote_category_id, is_active)
VALUES('Keep Category', 'category-keep', 1), ('Old Category', 'category-old', 1), ('Local Category', NULL, 1);
INSERT INTO suppliers(name, remote_supplier_id, is_active)
VALUES('Keep Supplier', 'supplier-keep', 1), ('Old Supplier', 'supplier-old', 1), ('Local Supplier', NULL, 1);
INSERT INTO remote_catalog_pending_prices(remote_price_id, remote_product_id, type, price, effective_at, source, created_at)
VALUES('price-old', 'product-old', 'retail', 100, '2026-07-14T00:00:00Z', 'test', '2026-07-14T00:00:00Z');");
        }

        var result = await new CatalogFullRefreshReconciler(db.Factory).ReconcileAsync(
            new[] { "product-keep" },
            new[] { "category-keep" },
            new[] { "supplier-keep" },
            "2026-07-14T01:00:00Z");

        Assert.AreEqual(1, result.DeactivatedProducts);
        Assert.AreEqual(1, result.DeactivatedCategories);
        Assert.AreEqual(1, result.DeactivatedSuppliers);
        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await verify.ExecuteScalarAsync<long>(
            "SELECT is_active FROM products WHERE barcode = 'P-LOCAL';"));
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(
            "SELECT is_active FROM products WHERE barcode = 'P-OLD';"));
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM remote_catalog_pending_prices WHERE remote_product_id = 'product-old';"));
    }

    [TestMethod]
    public async Task FullRefreshCursor_IsNotAdvancedBeforeAuthoritativeCommit()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var state = new CatalogShopStateRepository(db.Factory);
        var binding = await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");

        var pageStored = await state.StorePullCursorAsync(
            "shop-a", "SHOP-A", "page-1", "2026-07-14T01:00:00Z",
            binding.Epoch, "full_refresh", authoritativeSnapshotCommitted: false);
        Assert.IsFalse(pageStored);

        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
INSERT INTO products(barcode, name, unitPrice, remote_product_id, is_active)
VALUES('PAGE-1', 'Page 1', 100, 'product-page-1', 1),
      ('PAGE-2', 'Page 2', 100, 'product-page-2', 1);");
        }

        var afterCrash = await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        Assert.AreEqual(string.Empty, afterCrash.Cursor, "A restarted full refresh must resume from offset zero.");
        await new CatalogFullRefreshReconciler(db.Factory).ReconcileAsync(
            new[] { "product-page-1", "product-page-2" },
            Array.Empty<string>(),
            Array.Empty<string>(),
            "2026-07-14T02:00:00Z");
        using (var verify = db.Factory.Open())
        {
            Assert.AreEqual(1L, await verify.ExecuteScalarAsync<long>(
                "SELECT is_active FROM products WHERE remote_product_id = 'product-page-1';"));
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            state.StorePullCursorAsync(
                "shop-a", "SHOP-A", "final-page", "2026-07-14T02:00:00Z",
                binding.Epoch, "full_refresh", authoritativeSnapshotCommitted: true));
        Assert.AreEqual(
            string.Empty,
            (await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A")).Cursor,
            "A full-refresh cursor must remain empty until exactness evidence is stored.");
    }

    [TestMethod]
    public void CompatibilityValidator_FailsClosedOnSchemaModePolicyAndCapability()
    {
        var valid = ValidCatalogResponse();
        Assert.AreEqual(string.Empty, PosOnlineCompatibilityValidator.ValidateCatalogPull(valid));

        valid.SchemaVersion = 1;
        Assert.AreEqual("catalog_schema_not_supported", PosOnlineCompatibilityValidator.ValidateCatalogPull(valid));
        valid = ValidCatalogResponse();
        valid.SyncMode = "incremental";
        Assert.AreEqual("catalog_sync_mode_not_supported", PosOnlineCompatibilityValidator.ValidateCatalogPull(valid));
        valid = ValidCatalogResponse();
        valid.SyncMode = "replace_unknown";
        Assert.AreEqual("catalog_sync_mode_not_supported", PosOnlineCompatibilityValidator.ValidateCatalogPull(valid));
        valid = ValidCatalogResponse();
        valid.Policy.Capabilities.SalesSync = string.Empty;
        Assert.AreEqual("sales_sync_contract_not_supported", PosOnlineCompatibilityValidator.ValidateCatalogPull(valid));
        valid = ValidCatalogResponse();
        valid.Policy.Capabilities.CatalogPull = "catalog-v99";
        Assert.AreEqual("catalog_capability_not_supported", PosOnlineCompatibilityValidator.ValidateCatalogPull(valid));
        valid = ValidCatalogResponse();
        valid.Policy.ContractVersion = "pos-policy-v99";
        Assert.AreEqual("policy_contract_not_supported", PosOnlineCompatibilityValidator.ValidateCatalogPull(valid));
    }

    private static PosCatalogPullResponse ValidCatalogResponse()
    {
        return new PosCatalogPullResponse
        {
            Catalog = new PosCatalogPayload(),
            SchemaVersion = PosOnlineContract.CatalogPullSchemaVersion,
            SyncMode = "delta",
            Policy = new PosPolicyResponse
            {
                ContractVersion = PosOnlineContract.PolicyContractVersion,
                Capabilities = new PosPolicyCapabilitiesResponse
                {
                    CatalogPull = PosOnlineContract.CatalogCapabilityVersion,
                    OfflineSales = true,
                    SalesSync = PosOnlineContract.SalesSchemaVersion
                },
                PaymentPolicy = new PosPaymentPolicyResponse
                {
                    Currency = "CLP",
                    SupportedMethods = new[] { PosOnlineContract.PaymentCash, PosOnlineContract.PaymentCard }
                }
            }
        };
    }

    private static async Task SaveShopAsync(SqliteConnectionFactory factory, string shopId, string shopCode)
    {
        await new ShopOfficialSnapshotRepository(factory).SaveAsync(new OfficialShopSnapshot
        {
            ShopCode = shopCode,
            ShopId = shopId,
            ShopName = shopCode,
            Source = "test"
        });
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
            var root = Path.Combine(Path.GetTempPath(), "win7pos-tests-" + Guid.NewGuid().ToString("N"));
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
