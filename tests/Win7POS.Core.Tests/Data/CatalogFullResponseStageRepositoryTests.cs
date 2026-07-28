using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CatalogFullResponseStageRepositoryTests
{
    [TestMethod]
    public void DiskPreflight_UsesTechnicalByteReserve()
    {
        var required =
            CatalogFullResponseStageRepository.MinimumFreeDiskReserveBytes +
            2L * CatalogFullResponseStageRepository.MaximumPageBytes;
        Assert.IsFalse(
            CatalogFullResponseStageRepository.TryCalculateMaximumStagedBytes(
                required - 1,
                out var rejectedBudget));
        Assert.IsTrue(
            CatalogFullResponseStageRepository.TryCalculateMaximumStagedBytes(
                required,
                out var acceptedBudget));
        Assert.AreEqual(0L, rejectedBudget);
        Assert.AreEqual(
            CatalogFullResponseStageRepository.MaximumPageBytes,
            acceptedBudget);
    }

    [TestMethod]
    public async Task FullRefreshStage_StoresOnlyTheRecoveredDisplayText()
    {
        using var db = TestDb.Create();
        var repository = new CatalogFullResponseStageRepository(db.Factory);
        var generation = Guid.NewGuid().ToString("N");
        var response = Response("cursor-1", false, "P-WARNING");
        response.Catalog.Products[0].ProductName = "Full\nrefresh product";
        var assessment = CatalogDisplayRecoveryPolicy.Recover(response);

        await repository.BeginAsync(generation);
        await repository.AppendAsync(
            generation,
            1,
            Fingerprint("cursor-1"),
            assessment.RecoveredResponse,
            0,
            Budget());
        var staged = await repository.LoadPageAsync(generation, 1);

        Assert.IsTrue(assessment.CanContinue);
        Assert.IsTrue(assessment.WarningSummary.WarningCount > 0);
        Assert.AreEqual("Full refresh product", staged.Catalog.Products.Single().ProductName);
        Assert.AreEqual(1, staged.Catalog.Products.Length);
    }

    [TestMethod]
    public async Task Stage_RoundTripsPagesAndPreservesLiveSettings()
    {
        using var db = TestDb.Create();
        const string liveKey = "pos.catalog.sale_safe_at";
        const string liveValue = "2026-07-19T12:00:00.0000000Z";
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value)
VALUES(@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new { key = liveKey, value = liveValue });
        }

        var repository = new CatalogFullResponseStageRepository(db.Factory);
        var generation = Guid.NewGuid().ToString("N");
        await repository.BeginAsync(generation);
        var bytes = await repository.AppendAsync(
            generation,
            1,
            Fingerprint("cursor-1"),
            Response("cursor-1", true, "P-1"),
            0,
            Budget());
        bytes = await repository.AppendAsync(
            generation,
            2,
            Fingerprint("cursor-2"),
            Response("cursor-2", false, "P-2"),
            bytes,
            Budget());

        Assert.IsTrue(bytes > 0L);
        var first = await repository.LoadPageAsync(generation, 1);
        var second = await repository.LoadPageAsync(generation, 2);
        Assert.AreEqual("cursor-1", first.SyncCursor);
        Assert.IsTrue(first.HasMore);
        Assert.AreEqual("P-1", first.Catalog.Products.Single().ProductId);
        Assert.AreEqual("cursor-2", second.SyncCursor);
        Assert.IsFalse(second.HasMore);
        Assert.AreEqual("P-2", second.Catalog.Products.Single().ProductId);

        using var verify = db.Factory.Open();
        Assert.AreEqual(liveValue, await verify.ExecuteScalarAsync<string>(
            "SELECT value FROM app_settings WHERE key = @key;",
            new { key = liveKey }));
        Assert.AreEqual("blob", await verify.ExecuteScalarAsync<string>(@"
SELECT typeof(value)
FROM app_settings
WHERE key GLOB 'pos.catalog.full_stage.*.page.00000000000000000001';"));
    }

    [TestMethod]
    public async Task Stage_IsGenerationScopedRejectsDuplicatesAndClearsStaleRuns()
    {
        using var db = TestDb.Create();
        var repository = new CatalogFullResponseStageRepository(db.Factory);
        var firstGeneration = Guid.NewGuid().ToString("N");
        await repository.BeginAsync(firstGeneration);
        await repository.AppendAsync(
            firstGeneration,
            1,
            Fingerprint("cursor-1"),
            Response("cursor-1", false, "P-1"),
            0,
            Budget());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => repository.AppendAsync(
            firstGeneration,
            1,
            Fingerprint("cursor-duplicate"),
            Response("cursor-duplicate", false, "P-X"),
            0,
            Budget()));

        var secondGeneration = Guid.NewGuid().ToString("N");
        await repository.BeginAsync(secondGeneration);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => repository.LoadPageAsync(firstGeneration, 1));

        using (var conn = db.Factory.Open())
        {
            Assert.AreEqual(1L, await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM app_settings
WHERE key GLOB 'pos.catalog.full_stage.*';"));
        }

        await repository.AppendAsync(
            secondGeneration,
            1,
            Fingerprint("cursor-2"),
            Response("cursor-2", false, "P-2"),
            0,
            Budget());
        await repository.ClearAsync(secondGeneration);
        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM app_settings
WHERE key GLOB 'pos.catalog.full_stage.*';"));
    }

    [TestMethod]
    public async Task Stage_RejectsNonAdjacentRepeatedCursorTransactionally()
    {
        using var db = TestDb.Create();
        var repository = new CatalogFullResponseStageRepository(db.Factory);
        var generation = Guid.NewGuid().ToString("N");
        await repository.BeginAsync(generation);
        var budget = Budget();
        var bytes = await repository.AppendAsync(
            generation,
            1,
            Fingerprint("cursor-1"),
            Response("cursor-1", true, "P-1"),
            0,
            budget);
        bytes = await repository.AppendAsync(
            generation,
            2,
            Fingerprint("cursor-2"),
            Response("cursor-2", true, "P-2"),
            bytes,
            budget);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => repository.AppendAsync(
                generation,
                3,
                Fingerprint("cursor-1"),
                Response("cursor-1", false, "P-3"),
                bytes,
                budget));

        Assert.AreEqual(
            CatalogAuthoritativeDrainBudgetPolicy.CursorRepeatedCode,
            error.Message);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => repository.LoadPageAsync(generation, 3));
    }

    [TestMethod]
    public async Task Stage_RejectsCalculatedCumulativeByteBudgetWithTypedCode()
    {
        using var db = TestDb.Create();
        var repository = new CatalogFullResponseStageRepository(db.Factory);
        var generation = Guid.NewGuid().ToString("N");
        await repository.BeginAsync(generation);
        var budget = new CatalogFullResponseStageResourceBudget(
            availableBytesAtStart: 1024L * 1024L * 1024L,
            minimumFreeDiskReserveBytes:
                CatalogFullResponseStageRepository.MinimumFreeDiskReserveBytes,
            maximumStagedBytes: 1L);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => repository.AppendAsync(
                generation,
                1,
                Fingerprint("cursor-budget"),
                Response("cursor-budget", false, "P-BUDGET"),
                0,
                budget));

        Assert.AreEqual(
            CatalogAuthoritativeDrainBudgetPolicy.StageByteBudgetExceededCode,
            error.Message);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => repository.LoadPageAsync(generation, 1));
    }

    private static CatalogFullResponseStageResourceBudget Budget()
    {
        return new CatalogFullResponseStageResourceBudget(
            availableBytesAtStart: 4L * 1024L * 1024L * 1024L,
            minimumFreeDiskReserveBytes:
                CatalogFullResponseStageRepository.MinimumFreeDiskReserveBytes,
            maximumStagedBytes: 1024L * 1024L * 1024L);
    }

    private static string Fingerprint(string cursor)
    {
        return CatalogShopStateRepository.FingerprintValue(cursor);
    }

    private static PosCatalogPullResponse Response(string cursor, bool hasMore, string productId)
    {
        return new PosCatalogPullResponse
        {
            Catalog = new PosCatalogPayload
            {
                Categories = Array.Empty<PosCatalogCategoryResponse>(),
                Prices = Array.Empty<PosCatalogPriceResponse>(),
                Products = new[]
                {
                    new PosCatalogProductResponse
                    {
                        Barcode = productId,
                        ProductName = productId,
                        ProductId = productId
                    }
                },
                Suppliers = Array.Empty<PosCatalogSupplierResponse>(),
                Tombstones = new PosCatalogTombstonesResponse
                {
                    Categories = Array.Empty<PosCatalogCategoryTombstoneResponse>(),
                    Products = Array.Empty<PosCatalogProductTombstoneResponse>(),
                    Suppliers = Array.Empty<PosCatalogSupplierTombstoneResponse>()
                }
            },
            CatalogSummary = new PosCatalogSummaryResponse
            {
                ActiveProducts = 2,
                Categories = 0,
                Prices = 0,
                Products = 2,
                Suppliers = 0
            },
            CatalogVersion = "revision-1",
            HasMore = hasMore,
            Ok = true,
            SyncCursor = cursor,
            SyncMode = "full_refresh"
        };
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
                "win7pos-full-stage-tests-" + Guid.NewGuid().ToString("N"));
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
