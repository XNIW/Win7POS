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
public sealed class RemoteCatalogProductWriterTests
{
    [TestMethod]
    public async Task RemoteCatalogProductWriter_AndProductFacade_KeepRemoteMutationParity()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        const string remoteProductId = "remote-e4-parity";
        var direct = new RemoteCatalogProductWriter(directDb.Factory);
        var facade = new ProductRepository(facadeDb.Factory);

        await direct.UpsertProductAndMetaInTransactionAsync(
            NewProduct("E4-PARITY-OLD", "Initial remote product", 125),
            "ART-E4-INITIAL",
            "Initial secondary name",
            45,
            null,
            "  Remote   Supplier  ",
            null,
            "  Remote   Category  ",
            12,
            " " + remoteProductId + " ");
        await facade.UpsertProductAndMetaInTransactionAsync(
            NewProduct("E4-PARITY-OLD", "Initial remote product", 125),
            "ART-E4-INITIAL",
            "Initial secondary name",
            45,
            null,
            "  Remote   Supplier  ",
            null,
            "  Remote   Category  ",
            12,
            " " + remoteProductId + " ");

        await direct.UpsertProductAndMetaInTransactionAsync(
            NewProduct("E4-PARITY-NEW", "Updated remote product", 175),
            "ART-E4-UPDATED",
            "Updated secondary name",
            55,
            null,
            "  Remote   Supplier  ",
            null,
            "  Remote   Category  ",
            18,
            remoteProductId);
        await facade.UpsertProductAndMetaInTransactionAsync(
            NewProduct("E4-PARITY-NEW", "Updated remote product", 175),
            "ART-E4-UPDATED",
            "Updated secondary name",
            55,
            null,
            "  Remote   Supplier  ",
            null,
            "  Remote   Category  ",
            18,
            remoteProductId);

        var directSnapshot = await LoadSnapshotAsync(directDb.Factory, remoteProductId);
        var facadeSnapshot = await LoadSnapshotAsync(facadeDb.Factory, remoteProductId);

        AssertSnapshotEqual(directSnapshot, facadeSnapshot);
        Assert.AreEqual("E4-PARITY-NEW", directSnapshot.Barcode);
        Assert.AreEqual(1L, directSnapshot.IsActive);
        Assert.AreEqual("Remote Supplier", directSnapshot.SupplierName);
        Assert.AreEqual("Remote Category", directSnapshot.CategoryName);
        Assert.AreEqual(0L, await CountRowsAsync(directDb.Factory,
            "SELECT COUNT(1) FROM product_meta WHERE barcode = 'E4-PARITY-OLD';"));
        Assert.AreEqual(0L, await CountRowsAsync(facadeDb.Factory,
            "SELECT COUNT(1) FROM product_meta WHERE barcode = 'E4-PARITY-OLD';"));
    }

    [TestMethod]
    public async Task RemoteCatalogProductWriter_CallerTransactionRollbackLeavesNoRemoteRows()
    {
        using var db = TestDb.Create();
        using (var conn = db.Factory.Open())
        using (var tx = conn.BeginTransaction())
        {
            var id = await RemoteCatalogProductWriter.UpsertProductAndMetaInTransactionCoreAsync(
                conn,
                tx,
                NewProduct("E4-ROLLBACK", "Rollback remote product", 140),
                "ART-E4-ROLLBACK",
                "Rollback secondary",
                60,
                null,
                "Rollback Remote Supplier",
                null,
                "Rollback Remote Category",
                7,
                "remote-e4-rollback");

            Assert.IsTrue(id > 0);
            Assert.AreEqual(1L, await ScalarAsync(conn,
                "SELECT COUNT(1) FROM products WHERE remote_product_id = 'remote-e4-rollback';", tx));
            Assert.AreEqual(1L, await ScalarAsync(conn,
                "SELECT COUNT(1) FROM product_meta WHERE barcode = 'E4-ROLLBACK';", tx));
            tx.Rollback();
        }

        Assert.AreEqual(0L, await CountRowsAsync(db.Factory,
            "SELECT COUNT(1) FROM products WHERE remote_product_id = 'remote-e4-rollback';"));
        Assert.AreEqual(0L, await CountRowsAsync(db.Factory,
            "SELECT COUNT(1) FROM product_meta WHERE barcode = 'E4-ROLLBACK';"));
        Assert.AreEqual(0L, await CountRowsAsync(db.Factory,
            "SELECT COUNT(1) FROM suppliers WHERE name = 'Rollback Remote Supplier';"));
        Assert.AreEqual(0L, await CountRowsAsync(db.Factory,
            "SELECT COUNT(1) FROM categories WHERE name = 'Rollback Remote Category';"));
    }

    [TestMethod]
    public async Task RemoteCatalogProductWriter_AndProductFacade_RejectReservedBarcodeWithoutMutation()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        var direct = new RemoteCatalogProductWriter(directDb.Factory);
        var facade = new ProductRepository(facadeDb.Factory);

        await AssertThrowsInvalidOperationAsync(() => direct.UpsertProductAndMetaInTransactionAsync(
            NewProduct("DISC:E4-REMOTE-DIRECT", "Reserved remote barcode", 100),
            string.Empty,
            string.Empty,
            0,
            null,
            string.Empty,
            null,
            string.Empty,
            0,
            "remote-e4-reserved-direct"));
        await AssertThrowsInvalidOperationAsync(() => facade.UpsertProductAndMetaInTransactionAsync(
            NewProduct("MANUAL:E4-REMOTE-FACADE", "Reserved remote barcode", 100),
            string.Empty,
            string.Empty,
            0,
            null,
            string.Empty,
            null,
            string.Empty,
            0,
            "remote-e4-reserved-facade"));

        Assert.AreEqual(0L, await CountRowsAsync(directDb.Factory, "SELECT COUNT(1) FROM products;"));
        Assert.AreEqual(0L, await CountRowsAsync(facadeDb.Factory, "SELECT COUNT(1) FROM products;"));
    }

    [TestMethod]
    public async Task RemoteCatalogProductWriter_AndProductFacade_KeepTombstoneParityAndIdempotence()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        const string remoteProductId = "remote-e4-tombstone";
        const string remoteDeletedAt = "2026-07-23T10:00:00Z";
        var direct = new RemoteCatalogProductWriter(directDb.Factory);
        var facade = new ProductRepository(facadeDb.Factory);

        await direct.UpsertProductAndMetaInTransactionAsync(
            NewProduct("E4-TOMBSTONE", "Remote tombstone product", 100),
            string.Empty,
            string.Empty,
            0,
            null,
            string.Empty,
            null,
            string.Empty,
            0,
            remoteProductId);
        await facade.UpsertProductAndMetaInTransactionAsync(
            NewProduct("E4-TOMBSTONE", "Remote tombstone product", 100),
            string.Empty,
            string.Empty,
            0,
            null,
            string.Empty,
            null,
            string.Empty,
            0,
            remoteProductId);

        Assert.IsTrue(await direct.ApplyRemoteProductTombstoneAsync(
            " " + remoteProductId + " ", remoteDeletedAt));
        Assert.IsTrue(await facade.ApplyRemoteProductTombstoneAsync(
            " " + remoteProductId + " ", remoteDeletedAt));
        Assert.IsFalse(await direct.ApplyRemoteProductTombstoneAsync(remoteProductId, remoteDeletedAt));
        Assert.IsFalse(await facade.ApplyRemoteProductTombstoneAsync(remoteProductId, remoteDeletedAt));

        var directSnapshot = await LoadSnapshotAsync(directDb.Factory, remoteProductId);
        var facadeSnapshot = await LoadSnapshotAsync(facadeDb.Factory, remoteProductId);
        AssertSnapshotEqual(directSnapshot, facadeSnapshot);
        Assert.AreEqual(0L, directSnapshot.IsActive);
        Assert.AreEqual(remoteDeletedAt, directSnapshot.RemoteDeletedAt);
        Assert.AreEqual(1L, await CountRowsAsync(directDb.Factory,
            "SELECT COUNT(1) FROM product_meta WHERE barcode = 'E4-TOMBSTONE';"));
        Assert.AreEqual(1L, await CountRowsAsync(facadeDb.Factory,
            "SELECT COUNT(1) FROM product_meta WHERE barcode = 'E4-TOMBSTONE';"));
    }

    [TestMethod]
    public async Task RemoteCatalogProductWriter_PreservesPendingLocalStockAcrossCanonicalBarcodeChange()
    {
        using var db = TestDb.Create();
        var writer = new RemoteCatalogProductWriter(db.Factory);
        await writer.UpsertProductAndMetaInTransactionAsync(
            NewProduct("E4-STOCK-OLD", "Initial stock product", 100),
            string.Empty,
            string.Empty,
            0,
            null,
            string.Empty,
            null,
            string.Empty,
            10,
            "remote-e4-stock");

        using (var seed = db.Factory.Open())
        {
            await seed.ExecuteAsync(@"
UPDATE product_meta SET stock_qty = 7 WHERE barcode = 'E4-STOCK-OLD';
INSERT INTO sales(client_sale_id, code, createdAt, total, paidCash, paidCard, change, sync_status)
VALUES('e4-stock-sale', 'E4-STOCK-SALE', 1, 100, 100, 0, 0, 'pending');
INSERT INTO sales_sync_outbox(
  sale_id, client_sale_id, idempotency_key, status, attempt_count,
  next_retry_at, created_at, updated_at)
VALUES(last_insert_rowid(), 'e4-stock-sale', 'e4-stock-sale:pos-sales-ledger-v2', 'pending', 0, 0, 1, 1);
INSERT INTO local_stock_movements(
  movement_key, sale_id, barcode, quantity_delta, movement_kind, created_at)
VALUES('e4-stock-sale:1:sale', (SELECT id FROM sales WHERE client_sale_id = 'e4-stock-sale'),
       'E4-STOCK-OLD', -1, 'sale', 1);");
        }

        await writer.UpsertProductAndMetaInTransactionAsync(
            NewProduct("E4-STOCK-NEW", "Renamed stock product", 100),
            string.Empty,
            string.Empty,
            0,
            null,
            string.Empty,
            null,
            string.Empty,
            100,
            "remote-e4-stock");

        Assert.AreEqual(1L, await CountRowsAsync(db.Factory, @"
SELECT COUNT(1)
FROM products
WHERE remote_product_id = 'remote-e4-stock'
  AND barcode = 'E4-STOCK-NEW'
  AND is_active = 1;"));
        Assert.AreEqual(7L, await CountRowsAsync(db.Factory,
            "SELECT stock_qty FROM product_meta WHERE barcode = 'E4-STOCK-NEW';"));
        Assert.AreEqual(0L, await CountRowsAsync(db.Factory,
            "SELECT COUNT(1) FROM product_meta WHERE barcode = 'E4-STOCK-OLD';"));
        Assert.AreEqual(1L, await CountRowsAsync(db.Factory,
            "SELECT COUNT(1) FROM local_stock_movements WHERE barcode = 'E4-STOCK-NEW';"));
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

    private static async Task<RemoteProductSnapshot> LoadSnapshotAsync(
        SqliteConnectionFactory factory,
        string remoteProductId)
    {
        using var conn = factory.Open();
        return await conn.QuerySingleAsync<RemoteProductSnapshot>(@"
SELECT p.barcode AS Barcode,
       p.name AS Name,
       p.unitPrice AS UnitPrice,
       COALESCE(p.remote_product_id, '') AS RemoteProductId,
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
WHERE p.remote_product_id = @remoteProductId;", new { remoteProductId });
    }

    private static async Task<long> CountRowsAsync(SqliteConnectionFactory factory, string sql)
    {
        using var conn = factory.Open();
        return await ScalarAsync(conn, sql);
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

    private static void AssertSnapshotEqual(RemoteProductSnapshot expected, RemoteProductSnapshot actual)
    {
        Assert.AreEqual(expected.Barcode, actual.Barcode);
        Assert.AreEqual(expected.Name, actual.Name);
        Assert.AreEqual(expected.UnitPrice, actual.UnitPrice);
        Assert.AreEqual(expected.RemoteProductId, actual.RemoteProductId);
        Assert.AreEqual(expected.IsActive, actual.IsActive);
        Assert.AreEqual(expected.RemoteDeletedAt, actual.RemoteDeletedAt);
        Assert.AreEqual(expected.ArticleCode, actual.ArticleCode);
        Assert.AreEqual(expected.Name2, actual.Name2);
        Assert.AreEqual(expected.PurchasePrice, actual.PurchasePrice);
        Assert.AreEqual(expected.SupplierName, actual.SupplierName);
        Assert.AreEqual(expected.CategoryName, actual.CategoryName);
        Assert.AreEqual(expected.StockQty, actual.StockQty);
    }

    private sealed class RemoteProductSnapshot
    {
        public string Barcode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public long UnitPrice { get; set; }
        public string RemoteProductId { get; set; } = string.Empty;
        public long IsActive { get; set; }
        public string RemoteDeletedAt { get; set; } = string.Empty;
        public string ArticleCode { get; set; } = string.Empty;
        public string Name2 { get; set; } = string.Empty;
        public int PurchasePrice { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
        public int StockQty { get; set; }
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
            var root = Path.Combine(Path.GetTempPath(), "win7pos-remote-catalog-product-writer-" + Guid.NewGuid().ToString("N"));
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
