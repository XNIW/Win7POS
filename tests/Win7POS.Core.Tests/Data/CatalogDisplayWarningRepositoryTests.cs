using System;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CatalogDisplayWarningRepositoryTests
{
    [TestMethod]
    public async Task SuccessfulWarningSync_PersistsOnlyAggregateCountsAndCleansUpOnNextCleanRevision()
    {
        using var db = TestDb.Create();
        var response = new PosCatalogPullResponse
        {
            Catalog = new PosCatalogPayload
            {
                Products = new[]
                {
                    new PosCatalogProductResponse
                    {
                        ProductName = "private\nname",
                        SecondProductName = "",
                        Barcode = "private-barcode"
                    }
                }
            }
        };
        var summary = CatalogDisplayRecoveryPolicy.Recover(response).WarningSummary;
        var repository = new CatalogDisplayWarningRepository(db.Factory);

        await repository.StoreSuccessfulSyncAsync(summary, "revision-1", generation: null);
        var stored = await repository.LoadAsync();

        Assert.IsTrue(stored.WarningCount > 0);
        Assert.AreEqual(1, stored.ProductsAffected);
        Assert.AreEqual("revision-1", stored.Revision);
        Assert.IsTrue(await repository.TryMarkDisplayedAsync("revision-1"));
        Assert.IsFalse(await repository.TryMarkDisplayedAsync("revision-1"));

        await repository.StoreSuccessfulSyncAsync(new CatalogWarningSummary(), "revision-2", generation: null);
        var clean = await repository.LoadAsync();
        Assert.AreEqual(0, clean.WarningCount);
        Assert.AreEqual("revision-1", await ReadSettingAsync(db.Factory, CatalogDisplayWarningRepository.LastWarningRevisionKey));
        Assert.AreEqual(0L, await CountRawValuesAsync(db.Factory, "private name", "private-barcode"));
    }

    [TestMethod]
    public async Task RecoveredProductName_IsPersistedCanonicallyWithoutSkippingOrRewritingPriceHistory()
    {
        using var db = TestDb.Create();
        var response = new PosCatalogPullResponse
        {
            Catalog = new PosCatalogPayload
            {
                Products = new[]
                {
                    new PosCatalogProductResponse
                    {
                        Barcode = "warning-barcode",
                        ProductId = "warning-product",
                        ProductName = "Single\nline",
                        RetailPrice = 100
                    }
                }
            }
        };
        var assessment = CatalogDisplayRecoveryPolicy.Recover(response);
        var batch = RemoteCatalogBatchMapper.BuildRemoteCatalogBatch(
            assessment.RecoveredResponse,
            authoritativeFullRefresh: false,
            stagePage: null);
        var repository = new RemoteCatalogBatchRepository(db.Factory);

        var first = await repository.ApplyAsync(batch);
        var firstHistoryCount = await PriceHistoryCountAsync(db.Factory);
        var second = await repository.ApplyAsync(batch);

        Assert.AreEqual(1, first.ProductsApplied);
        Assert.AreEqual(0, first.RowsSkipped);
        Assert.AreEqual("Single line", await ProductNameAsync(db.Factory, "warning-product"));
        Assert.AreEqual(0, second.RowsSkipped);
        Assert.AreEqual(firstHistoryCount, await PriceHistoryCountAsync(db.Factory));
    }

    private static async Task<string> ReadSettingAsync(SqliteConnectionFactory factory, string key)
    {
        using var conn = factory.Open();
        return await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT value FROM app_settings WHERE key = @key;",
            new { key }) ?? string.Empty;
    }

    private static async Task<long> CountRawValuesAsync(
        SqliteConnectionFactory factory,
        string rawName,
        string rawBarcode)
    {
        using var conn = factory.Open();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM app_settings WHERE value IN (@rawName, @rawBarcode);",
                new { rawName, rawBarcode });
    }

    private static async Task<string> ProductNameAsync(
        SqliteConnectionFactory factory,
        string remoteProductId)
    {
        using var conn = factory.Open();
        return await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT name FROM products WHERE remote_product_id = @remoteProductId;",
            new { remoteProductId }) ?? string.Empty;
    }

    private static async Task<long> PriceHistoryCountAsync(SqliteConnectionFactory factory)
    {
        using var conn = factory.Open();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM product_price_history;");
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

        internal SqliteConnectionFactory Factory { get; }
        private string Root { get; }

        internal static TestDb Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "win7pos-catalog-display-warning-" + Guid.NewGuid().ToString("N"));
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
