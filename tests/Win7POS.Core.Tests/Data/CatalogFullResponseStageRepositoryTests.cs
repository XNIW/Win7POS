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
        var bytes = await repository.AppendAsync(generation, 1, Response("cursor-1", true, "P-1"), 0);
        bytes = await repository.AppendAsync(generation, 2, Response("cursor-2", false, "P-2"), bytes);

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
WHERE key GLOB 'pos.catalog.full_stage.*.page.000001';"));
    }

    [TestMethod]
    public async Task Stage_IsGenerationScopedRejectsDuplicatesAndClearsStaleRuns()
    {
        using var db = TestDb.Create();
        var repository = new CatalogFullResponseStageRepository(db.Factory);
        var firstGeneration = Guid.NewGuid().ToString("N");
        await repository.BeginAsync(firstGeneration);
        await repository.AppendAsync(firstGeneration, 1, Response("cursor-1", false, "P-1"), 0);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => repository.AppendAsync(
            firstGeneration,
            1,
            Response("cursor-duplicate", false, "P-X"),
            0));

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

        await repository.AppendAsync(secondGeneration, 1, Response("cursor-2", false, "P-2"), 0);
        await repository.ClearAsync(secondGeneration);
        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM app_settings
WHERE key GLOB 'pos.catalog.full_stage.*';"));
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
