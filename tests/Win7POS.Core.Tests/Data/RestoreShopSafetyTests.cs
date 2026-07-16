using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class RestoreShopSafetyTests
{
    [TestMethod]
    public async Task CandidateValidation_RejectsCrossShopSnapshotAndAnyUnresolvedOutbox()
    {
        using var crossShop = TestDb.Create();
        await SaveShopAsync(crossShop.Factory, "shop-b", "SHOP-B");
        var mismatch = await new RestoreShopSafetyRepository(crossShop.Factory)
            .ValidateCandidateAsync("shop-a", "SHOP-A");
        Assert.IsFalse(mismatch.IsValid);
        Assert.AreEqual("restore_shop_mismatch", mismatch.Code);

        using var outboxDb = TestDb.Create();
        await SaveShopAsync(outboxDb.Factory, "shop-a", "SHOP-A");
        await new CatalogShopStateRepository(outboxDb.Factory)
            .EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        using (var conn = outboxDb.Factory.Open())
        {
            await conn.ExecuteAsync(@"
INSERT INTO sales(client_sale_id, code, createdAt, total, paidCash, paidCard, change, sync_status)
VALUES('restore-sale', 'RESTORE-SALE', 1, 100, 100, 0, 0, 'pending');
INSERT INTO sales_sync_outbox(
  sale_id, client_sale_id, idempotency_key, schema_version, operation_type,
  origin_shop_id, origin_shop_code, payload_json, payload_hash,
  status, attempt_count, next_retry_at, created_at, updated_at)
VALUES(last_insert_rowid(), 'restore-sale', 'restore-sale:pos-sales-ledger-v2',
  'pos-sales-ledger-v2', 'sale', 'shop-b', 'SHOP-B', '{}', 'hash',
  'failed_blocked', 0, 0, 1, 1);");
        }
        var outboxMismatch = await new RestoreShopSafetyRepository(outboxDb.Factory)
            .ValidateCandidateAsync("shop-a", "SHOP-A");
        Assert.IsFalse(outboxMismatch.IsValid);
        Assert.AreEqual("restore_candidate_outbox_unresolved", outboxMismatch.Code);
    }

    [TestMethod]
    public async Task CandidateValidation_RejectsCatalogBindingMismatch()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES('pos.catalog.bound_shop_id', 'shop-b');
INSERT INTO app_settings(key, value) VALUES('pos.catalog.bound_shop_code', 'SHOP-B');");
        }

        var result = await new RestoreShopSafetyRepository(db.Factory)
            .ValidateCandidateAsync("shop-a", "SHOP-A");

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("restore_catalog_shop_mismatch", result.Code);
    }

    [TestMethod]
    public async Task AtomicInstaller_UsesValidatedCopyAfterSourceMutation()
    {
        using var files = RestoreFiles.Create();
        File.WriteAllText(files.Source, "validated");
        File.Copy(files.Source, files.Validated, true);
        File.WriteAllText(files.Source, "mutated-after-validation");
        File.WriteAllText(files.Live, "live-before-restore");
        File.WriteAllText(files.Live + "-wal", "stale-wal");
        File.WriteAllText(files.Live + "-shm", "stale-shm");
        File.Copy(files.Live, files.Rollback, true);

        await new AtomicRestoreInstaller().InstallAsync(
            files.Validated,
            files.Live,
            files.Rollback,
            () => Task.CompletedTask);

        Assert.AreEqual("validated", File.ReadAllText(files.Live));
        Assert.IsFalse(File.Exists(files.Live + "-wal"));
        Assert.IsFalse(File.Exists(files.Live + "-shm"));
    }

    [TestMethod]
    public async Task AtomicInstaller_RollsBackOnPostSwapFailure()
    {
        using var files = RestoreFiles.Create();
        File.WriteAllText(files.Validated, "validated");
        File.WriteAllText(files.Live, "live-before-restore");
        File.Copy(files.Live, files.Rollback, true);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new AtomicRestoreInstaller().InstallAsync(
                files.Validated,
                files.Live,
                files.Rollback,
                () => throw new InvalidOperationException("failure-injection-after-swap")));

        Assert.AreEqual("live-before-restore", File.ReadAllText(files.Live));
    }

    [TestMethod]
    public async Task CompleteReview_RequiresPostRestoreCatalogAndNoUnresolvedOutbox()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var state = new CatalogShopStateRepository(db.Factory);
        await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        var settings = new SettingsRepository(db.Factory);
        await settings.SetBoolAsync(RestoreShopSafetyRepository.RestoreNeedsReviewKey, true);
        await settings.SetStringAsync(
            RestoreShopSafetyRepository.RestoreCompletedAtKey,
            "2026-07-14T01:00:00Z");
        await state.StoreLastSyncAsync("shop-a", "SHOP-A", "cursor", "2026-07-14T00:00:00Z", syncMode: "delta");
        await state.StoreSaleSafeAsync("shop-a", "SHOP-A", "2026-07-14T00:00:00Z");

        var early = await new RestoreShopSafetyRepository(db.Factory).CompleteReviewAsync();
        Assert.IsFalse(early.IsValid);
        Assert.AreEqual("restore_review_catalog_not_reconciled", early.Code);

        await state.StoreLastSyncAsync("shop-a", "SHOP-A", "cursor-2", "2026-07-14T02:00:00Z", syncMode: "full_refresh");
        await state.StoreSaleSafeAsync("shop-a", "SHOP-A", "2026-07-14T02:00:00Z");
        var complete = await new RestoreShopSafetyRepository(db.Factory).CompleteReviewAsync();
        Assert.IsTrue(complete.IsValid);
        Assert.IsFalse(await settings.GetBoolAsync(RestoreShopSafetyRepository.RestoreNeedsReviewKey));
    }

    [TestMethod]
    public async Task RestoreReset_DiscardsCursorAndSaleSafeUntilNewFullRefresh()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var state = new CatalogShopStateRepository(db.Factory);
        var binding = await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        Assert.IsTrue(await state.StorePullCursorAsync(
            "shop-a",
            "SHOP-A",
            "restored-cursor",
            "2026-07-14T00:00:00Z",
            binding.Epoch,
            "delta",
            authoritativeSnapshotCommitted: false,
            new CatalogDeltaChainCheckpoint
            {
                CatalogVersion = "catalog-before-restore",
                CursorFingerprints = new[]
                {
                    CatalogShopStateRepository.FingerprintValue("restored-cursor")
                },
                HasMore = true,
                SyncMode = "delta"
            }));
        await state.StoreSaleSafeAsync("shop-a", "SHOP-A", "2026-07-14T00:00:00Z");
        Assert.IsTrue((await state.LoadDeltaChainAsync(
            "shop-a",
            "SHOP-A",
            binding.Epoch)).HasState);

        await state.ResetForRestoreReviewAsync("shop-a", "SHOP-A");

        var rebound = await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        Assert.IsTrue(rebound.IsValid);
        Assert.AreEqual(string.Empty, rebound.Cursor);
        Assert.IsFalse(await state.IsSaleSafeForOfficialShopAsync());
        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(@"
SELECT COUNT(1) FROM app_settings
WHERE key IN ('pos.catalog.last_sync_mode', 'pos.catalog.sale_safe_at');"));
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(@"
SELECT COUNT(1) FROM app_settings
WHERE key LIKE 'pos.catalog.delta_chain.%';"));
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

    private sealed class RestoreFiles : IDisposable
    {
        private RestoreFiles(string root)
        {
            Root = root;
            Source = Path.Combine(root, "source.db");
            Validated = Path.Combine(root, "validated.db");
            Live = Path.Combine(root, "live.db");
            Rollback = Path.Combine(root, "rollback.db");
        }

        public string Source { get; }
        public string Validated { get; }
        public string Live { get; }
        public string Rollback { get; }
        private string Root { get; }

        public static RestoreFiles Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "win7pos-restore-files-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new RestoreFiles(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
