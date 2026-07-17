using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Models;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class SalesSyncPreflightCasTests
{
    [TestMethod]
    public async Task PreflightBlock_CannotOverwriteAttemptAcquiredByAnotherWorker()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory);
        var sales = new SaleRepository(db.Factory);
        var saleId = await InsertSaleAsync(sales);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var staleRead = (await sales.GetPendingSalesSyncOutboxAsync(1, nowMs + 1)).Single();

        Assert.IsTrue(await sales.PrepareSalesSyncAttemptAsync(
            staleRead.Id,
            staleRead.ClientBatchId,
            staleRead.PayloadJson,
            staleRead.PayloadHash,
            nowMs + 2,
            staleRead.AttemptCount));

        Assert.IsFalse(await sales.MarkSalesSyncOriginBlockedAsync(
            staleRead.Id,
            staleRead.SaleId,
            "payload_hash_mismatch",
            nowMs + 3,
            staleRead.Status,
            staleRead.AttemptCount,
            staleRead.LeaseObservedAt));

        using var verify = db.Factory.Open();
        Assert.AreEqual("in_progress", await verify.ExecuteScalarAsync<string>(
            "SELECT status FROM sales_sync_outbox WHERE id = @id;",
            new { id = staleRead.Id }));
        Assert.AreEqual(1L, await verify.ExecuteScalarAsync<long>(
            "SELECT attempt_count FROM sales_sync_outbox WHERE id = @id;",
            new { id = staleRead.Id }));
        Assert.AreEqual("pending", await verify.ExecuteScalarAsync<string>(
            "SELECT sync_status FROM sales WHERE id = @saleId;",
            new { saleId }));
    }

    [TestMethod]
    public async Task PreflightBlock_RequiresUnchangedStatusAttemptAndLeaseEvidence()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory);
        var sales = new SaleRepository(db.Factory);
        await InsertSaleAsync(sales);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var item = (await sales.GetPendingSalesSyncOutboxAsync(1, nowMs + 1)).Single();

        Assert.IsFalse(await sales.MarkSalesSyncOriginBlockedAsync(
            item.Id,
            item.SaleId,
            "schema_mismatch",
            nowMs + 2,
            item.Status,
            item.AttemptCount + 1,
            item.LeaseObservedAt));
        Assert.IsFalse(await sales.MarkSalesSyncOriginBlockedAsync(
            item.Id,
            item.SaleId,
            "schema_mismatch",
            nowMs + 2,
            item.Status,
            item.AttemptCount,
            item.LeaseObservedAt + 1));
        Assert.IsTrue(await sales.MarkSalesSyncOriginBlockedAsync(
            item.Id,
            item.SaleId,
            "schema_mismatch",
            nowMs + 2,
            item.Status,
            item.AttemptCount,
            item.LeaseObservedAt));

        using var verify = db.Factory.Open();
        Assert.AreEqual("failed_blocked", await verify.ExecuteScalarAsync<string>(
            "SELECT status FROM sales_sync_outbox WHERE id = @id;",
            new { id = item.Id }));
        Assert.AreEqual("blocked", await verify.ExecuteScalarAsync<string>(
            "SELECT sync_status FROM sales WHERE id = @saleId;",
            new { saleId = item.SaleId }));
    }

    private static async Task<long> InsertSaleAsync(SaleRepository sales)
    {
        return await sales.InsertSaleAsync(
            new Sale
            {
                Code = "CAS-SALE",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Kind = (int)SaleKind.Sale,
                Total = 100,
                PaidCash = 100
            },
            new[]
            {
                new SaleLine
                {
                    Barcode = "CAS-ITEM",
                    Name = "CAS item",
                    Quantity = 1,
                    UnitPrice = 100
                }
            });
    }

    private static Task SaveShopAsync(SqliteConnectionFactory factory)
    {
        return new ShopOfficialSnapshotRepository(factory).SaveAsync(new OfficialShopSnapshot
        {
            ShopCode = "SHOP-CAS",
            ShopId = "shop-cas",
            ShopName = "CAS shop",
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
