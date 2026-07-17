using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class CatalogSyncConcurrencyTests
{
    [TestMethod]
    public async Task RestoreReset_WaitsForBarrierAndRejectsPreRestoreEpoch()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory);
        var state = new CatalogShopStateRepository(db.Factory);
        var before = await state.EnsureAndLoadCursorAsync("shop-fence", "SHOP-FENCE");
        await state.StoreLastSyncAsync(
            "shop-fence",
            "SHOP-FENCE",
            "cursor-before-restore",
            "2026-07-16T20:00:00Z",
            before.Epoch,
            "delta");

        var lease = await new CatalogShopTransitionBarrier(db.Factory).EnterAsync();
        var reset = state.ResetForRestoreReviewAsync("shop-fence", "SHOP-FENCE");
        await Task.Delay(50);
        Assert.IsFalse(reset.IsCompleted);
        lease.Dispose();
        await reset;

        var after = await state.EnsureAndLoadCursorAsync("shop-fence", "SHOP-FENCE");
        Assert.IsTrue(after.Epoch > before.Epoch);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            state.StorePullCursorAsync(
                "shop-fence",
                "SHOP-FENCE",
                "late-cursor",
                "2026-07-16T20:01:00Z",
                before.Epoch,
                "delta",
                authoritativeSnapshotCommitted: false,
                expectedPreviousCursor: "cursor-before-restore",
                expectedPreviousMode: "delta"));
        Assert.AreEqual(string.Empty, (await state.EnsureAndLoadCursorAsync(
            "shop-fence",
            "SHOP-FENCE")).Cursor);
    }

    [TestMethod]
    public async Task LateCatalogPage_AfterRestoreFenceCannotMutateProducts()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory);
        var state = new CatalogShopStateRepository(db.Factory);
        var before = await state.EnsureAndLoadCursorAsync("shop-fence", "SHOP-FENCE");
        await state.StoreLastSyncAsync(
            "shop-fence",
            "SHOP-FENCE",
            "cursor-before-restore",
            "2026-07-16T20:00:00Z",
            before.Epoch,
            "delta");
        await state.ResetForRestoreReviewAsync("shop-fence", "SHOP-FENCE");

        var lateBatch = new RemoteCatalogBatch
        {
            Products = new[]
            {
                new RemoteCatalogProductWrite
                {
                    Barcode = "LATE-001",
                    Name = "Late product",
                    RemoteProductId = "remote-late-001",
                    UnitPrice = 100
                }
            }
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new RemoteCatalogBatchRepository(db.Factory).ApplyAsync(
                lateBatch,
                CancellationToken.None,
                new RemoteCatalogCommitFence
                {
                    ExpectedEpoch = before.Epoch,
                    ExpectedPreviousCursor = "cursor-before-restore",
                    ExpectedPreviousMode = "delta",
                    ShopCode = "SHOP-FENCE",
                    ShopId = "shop-fence"
                }));

        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM products WHERE barcode = 'LATE-001';"));
    }

    [TestMethod]
    public async Task CursorAndModeCas_RejectsLateWriterInSameEpoch()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory);
        var state = new CatalogShopStateRepository(db.Factory);
        var initial = await state.EnsureAndLoadCursorAsync("shop-fence", "SHOP-FENCE");
        Assert.IsTrue(await state.StorePullCursorAsync(
            "shop-fence",
            "SHOP-FENCE",
            "cursor-new",
            "2026-07-16T20:00:00Z",
            initial.Epoch,
            "delta",
            authoritativeSnapshotCommitted: false,
            expectedPreviousCursor: initial.Cursor,
            expectedPreviousMode: initial.Mode));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            state.StorePullCursorAsync(
                "shop-fence",
                "SHOP-FENCE",
                "cursor-late",
                "2026-07-16T20:01:00Z",
                initial.Epoch,
                "delta",
                authoritativeSnapshotCommitted: false,
                expectedPreviousCursor: initial.Cursor,
                expectedPreviousMode: initial.Mode));

        var after = await state.EnsureAndLoadCursorAsync("shop-fence", "SHOP-FENCE");
        Assert.AreEqual("cursor-new", after.Cursor);
        Assert.AreEqual("delta", after.Mode);
    }

    [TestMethod]
    public async Task CallerCancellation_PropagatesFromOnlineTransport()
    {
        Assert.IsTrue(PosAdminWebOptions.TryCreate(
            "http://127.0.0.1:9",
            out var options,
            out var error),
            error);
        using var client = new PosAdminWebClient(options!);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            client.CatalogPullAsync(new PosCatalogPullRequest(), cancellation.Token));
    }

    private static Task SaveShopAsync(SqliteConnectionFactory factory)
    {
        return new ShopOfficialSnapshotRepository(factory).SaveAsync(new OfficialShopSnapshot
        {
            ShopCode = "SHOP-FENCE",
            ShopId = "shop-fence",
            ShopName = "Fence shop",
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
            var root = Path.Combine(
                Path.GetTempPath(),
                "Win7POS.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestDb(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
