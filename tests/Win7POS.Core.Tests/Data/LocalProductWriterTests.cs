using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Models;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class LocalProductWriterTests
{
    [TestMethod]
    public async Task LocalProductWriter_AndProductFacade_KeepLocalMutationParity()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        const string barcode = "E3-LOCAL-PARITY";

        var direct = new LocalProductWriter(directDb.Factory);
        var facade = new ProductRepository(facadeDb.Factory);
        var directId = await direct.UpsertProductAndMetaInTransactionAsync(
            NewProduct(barcode, "Initial local product", 125),
            "ART-E3",
            "Initial secondary name",
            45,
            null,
            "  Local   Supplier  ",
            null,
            "  Local   Category  ",
            12);
        var facadeId = await facade.UpsertProductAndMetaInTransactionAsync(
            NewProduct(barcode, "Initial local product", 125),
            "ART-E3",
            "Initial secondary name",
            45,
            null,
            "  Local   Supplier  ",
            null,
            "  Local   Category  ",
            12);

        await direct.UpdateProductAndMetaWithPriceHistoryAsync(
            directId,
            "Updated local product",
            165,
            barcode,
            "ART-E3-UPDATED",
            "Updated secondary name",
            55,
            null,
            "  Local   Supplier  ",
            null,
            "  Local   Category  ",
            15,
            "E3_TEST");
        await facade.UpdateProductAndMetaWithPriceHistoryAsync(
            facadeId,
            "Updated local product",
            165,
            barcode,
            "ART-E3-UPDATED",
            "Updated secondary name",
            55,
            null,
            "  Local   Supplier  ",
            null,
            "  Local   Category  ",
            15,
            "E3_TEST");

        var directSnapshot = await LoadSnapshotAsync(directDb.Factory, barcode);
        var facadeSnapshot = await LoadSnapshotAsync(facadeDb.Factory, barcode);

        AssertSnapshotsEqual(directSnapshot, facadeSnapshot);
        Assert.AreEqual("Local Supplier", directSnapshot.SupplierName);
        Assert.AreEqual("Local Category", directSnapshot.CategoryName);
        Assert.AreEqual(2, directSnapshot.PriceHistory.Count);
        Assert.AreEqual("purchase", directSnapshot.PriceHistory[0].Type);
        Assert.AreEqual("retail", directSnapshot.PriceHistory[1].Type);

        Assert.IsTrue(await direct.DeleteByBarcodeAsync(barcode));
        Assert.IsTrue(await facade.DeleteByBarcodeAsync(barcode));

        directSnapshot = await LoadSnapshotAsync(directDb.Factory, barcode);
        facadeSnapshot = await LoadSnapshotAsync(facadeDb.Factory, barcode);
        AssertSnapshotsEqual(directSnapshot, facadeSnapshot);
        Assert.AreEqual(0, directSnapshot.IsActive);
        Assert.IsFalse(string.IsNullOrWhiteSpace(directSnapshot.RemoteDeletedAt));
    }

    [TestMethod]
    public async Task LocalProductWriter_CallerTransactionRollbackLeavesNoLocalRows()
    {
        using var db = TestDb.Create();
        using (var conn = db.Factory.Open())
        using (var tx = conn.BeginTransaction())
        {
            var id = await LocalProductWriter.UpsertProductAndMetaInTransactionCoreAsync(
                conn,
                tx,
                NewProduct("E3-ROLLBACK", "Rollback product", 140),
                "ART-ROLLBACK",
                "Rollback secondary",
                60,
                null,
                "Rollback Supplier",
                null,
                "Rollback Category",
                7);

            Assert.IsTrue(id > 0);
            Assert.AreEqual(1L, await ScalarAsync(conn, "SELECT COUNT(1) FROM products WHERE barcode = 'E3-ROLLBACK';", tx));
            Assert.AreEqual(1L, await ScalarAsync(conn, "SELECT COUNT(1) FROM product_meta WHERE barcode = 'E3-ROLLBACK';", tx));
            tx.Rollback();
        }

        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await ScalarAsync(verify, "SELECT COUNT(1) FROM products WHERE barcode = 'E3-ROLLBACK';"));
        Assert.AreEqual(0L, await ScalarAsync(verify, "SELECT COUNT(1) FROM product_meta WHERE barcode = 'E3-ROLLBACK';"));
        Assert.AreEqual(0L, await ScalarAsync(verify, "SELECT COUNT(1) FROM suppliers WHERE name = 'Rollback Supplier';"));
        Assert.AreEqual(0L, await ScalarAsync(verify, "SELECT COUNT(1) FROM categories WHERE name = 'Rollback Category';"));
    }

    [TestMethod]
    public async Task LocalProductWriter_ConcurrentFacadeReads_LeaveLocalStateUnchanged()
    {
        using var db = TestDb.Create();
        const string barcode = "E3-CONCURRENT-READ";
        var writer = new LocalProductWriter(db.Factory);
        await writer.UpsertProductAndMetaInTransactionAsync(
            NewProduct(barcode, "Read-only local product", 130),
            "ART-READ",
            "Read secondary",
            40,
            null,
            "Read Supplier",
            null,
            "Read Category",
            5);
        var before = await LoadSnapshotAsync(db.Factory, barcode);
        var facade = new ProductRepository(db.Factory);

        var reads = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => facade.GetByBarcodeAsync(barcode)))
            .ToArray();
        var products = await Task.WhenAll(reads);

        Assert.IsTrue(products.All(product => product != null && product.Barcode == barcode));
        AssertSnapshotsEqual(before, await LoadSnapshotAsync(db.Factory, barcode));
    }

    [TestMethod]
    public async Task LocalProductWriter_AndProductFacade_RejectReservedBarcodeWithoutMutation()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        var direct = new LocalProductWriter(directDb.Factory);
        var facade = new ProductRepository(facadeDb.Factory);

        await AssertThrowsInvalidOperationAsync(() => direct.UpsertAsync(
            NewProduct("DISC:E3-LOCAL", "Reserved barcode", 100)));
        await AssertThrowsInvalidOperationAsync(() => facade.UpsertAsync(
            NewProduct("DISC:E3-FACADE", "Reserved barcode", 100)));

        using var directVerify = directDb.Factory.Open();
        using var facadeVerify = facadeDb.Factory.Open();
        Assert.AreEqual(0L, await ScalarAsync(directVerify, "SELECT COUNT(1) FROM products;"));
        Assert.AreEqual(0L, await ScalarAsync(facadeVerify, "SELECT COUNT(1) FROM products;"));
    }

    [TestMethod]
    public async Task ProductRepository_NonBlankRemoteProductId_RemainsOnRemoteMutationPath()
    {
        using var db = TestDb.Create();
        var facade = new ProductRepository(db.Factory);

        await facade.UpsertProductAndMetaInTransactionAsync(
            NewProduct("E3-REMOTE", "Remote product", 190),
            "ART-REMOTE",
            "Remote secondary",
            70,
            null,
            "Remote Supplier",
            null,
            "Remote Category",
            9,
            "remote-e3-identity");

        using var verify = db.Factory.Open();
        Assert.AreEqual(
            "remote-e3-identity",
            await verify.ExecuteScalarAsync<string>(
                "SELECT remote_product_id FROM products WHERE barcode = 'E3-REMOTE';"));
    }

    private static Product NewProduct(string barcode, string name, long unitPrice)
    {
        return new Product
        {
            Barcode = barcode,
            Name = name,
            UnitPrice = unitPrice
        };
    }

    private static async Task<LocalProductSnapshot> LoadSnapshotAsync(
        SqliteConnectionFactory factory,
        string barcode)
    {
        using var conn = factory.Open();
        var snapshot = await conn.QuerySingleAsync<LocalProductSnapshot>(@"
SELECT p.name AS Name,
       p.unitPrice AS UnitPrice,
       COALESCE(p.is_active, 1) AS IsActive,
       COALESCE(p.remote_deleted_at, '') AS RemoteDeletedAt,
       COALESCE(m.article_code, '') AS ArticleCode,
       COALESCE(m.name2, '') AS Name2,
       COALESCE(m.purchase_price, 0) AS PurchasePrice,
       COALESCE(m.supplier_name, '') AS SupplierName,
       COALESCE(m.category_name, '') AS CategoryName,
       COALESCE(m.stock_qty, 0) AS StockQty
FROM products p
LEFT JOIN product_meta m ON m.barcode = p.barcode
WHERE p.barcode = @barcode;", new { barcode });
        var history = await conn.QueryAsync<LocalPriceHistorySnapshot>(@"
SELECT type AS Type,
       COALESCE(old_price, -1) AS OldPrice,
       new_price AS NewPrice,
       source AS Source
FROM product_price_history
WHERE barcode = @barcode
ORDER BY type ASC, id ASC;", new { barcode });
        snapshot.PriceHistory = new List<LocalPriceHistorySnapshot>(history);
        return snapshot;
    }

    private static async Task<long> ScalarAsync(
        SqliteConnection conn,
        string sql,
        SqliteTransaction? tx = null)
    {
        var value = await conn.ExecuteScalarAsync(sql, transaction: tx);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task AssertThrowsInvalidOperationAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        Assert.Fail("Expected InvalidOperationException.");
    }

    private static void AssertSnapshotsEqual(LocalProductSnapshot expected, LocalProductSnapshot actual)
    {
        Assert.AreEqual(expected.Name, actual.Name);
        Assert.AreEqual(expected.UnitPrice, actual.UnitPrice);
        Assert.AreEqual(expected.IsActive, actual.IsActive);
        Assert.AreEqual(
            string.IsNullOrWhiteSpace(expected.RemoteDeletedAt),
            string.IsNullOrWhiteSpace(actual.RemoteDeletedAt));
        Assert.AreEqual(expected.ArticleCode, actual.ArticleCode);
        Assert.AreEqual(expected.Name2, actual.Name2);
        Assert.AreEqual(expected.PurchasePrice, actual.PurchasePrice);
        Assert.AreEqual(expected.SupplierName, actual.SupplierName);
        Assert.AreEqual(expected.CategoryName, actual.CategoryName);
        Assert.AreEqual(expected.StockQty, actual.StockQty);
        Assert.AreEqual(expected.PriceHistory.Count, actual.PriceHistory.Count);
        for (var index = 0; index < expected.PriceHistory.Count; index++)
        {
            Assert.AreEqual(expected.PriceHistory[index].Type, actual.PriceHistory[index].Type);
            Assert.AreEqual(expected.PriceHistory[index].OldPrice, actual.PriceHistory[index].OldPrice);
            Assert.AreEqual(expected.PriceHistory[index].NewPrice, actual.PriceHistory[index].NewPrice);
            Assert.AreEqual(expected.PriceHistory[index].Source, actual.PriceHistory[index].Source);
        }
    }

    private sealed class LocalProductSnapshot
    {
        public string Name { get; set; } = string.Empty;
        public long UnitPrice { get; set; }
        public long IsActive { get; set; }
        public string RemoteDeletedAt { get; set; } = string.Empty;
        public string ArticleCode { get; set; } = string.Empty;
        public string Name2 { get; set; } = string.Empty;
        public int PurchasePrice { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
        public int StockQty { get; set; }
        public IReadOnlyList<LocalPriceHistorySnapshot> PriceHistory { get; set; } =
            Array.Empty<LocalPriceHistorySnapshot>();
    }

    private sealed class LocalPriceHistorySnapshot
    {
        public string Type { get; set; } = string.Empty;
        public int OldPrice { get; set; }
        public int NewPrice { get; set; }
        public string Source { get; set; } = string.Empty;
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
            var root = Path.Combine(Path.GetTempPath(), "win7pos-local-product-writer-" + Guid.NewGuid().ToString("N"));
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
