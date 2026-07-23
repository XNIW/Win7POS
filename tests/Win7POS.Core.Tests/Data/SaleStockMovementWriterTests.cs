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
public sealed class SaleStockMovementWriterTests
{
    [TestMethod]
    public async Task SaleStockMovementWriter_AndSaleFacade_KeepMovementParity()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        const string clientSaleId = "  f3-parity-sale  ";
        const string firstBarcode = "F3-PARITY-ONE";
        const string secondBarcode = "F3-PARITY-TWO";

        var directSaleId = await InsertSaleAsync(directDb.Factory, "F3-PARITY-DIRECT", clientSaleId);
        var facadeSaleId = await InsertSaleAsync(facadeDb.Factory, "F3-PARITY-FACADE", clientSaleId);
        await SeedStockAsync(directDb.Factory, firstBarcode, 5);
        await SeedStockAsync(directDb.Factory, secondBarcode, 1);
        await SeedStockAsync(facadeDb.Factory, firstBarcode, 5);
        await SeedStockAsync(facadeDb.Factory, secondBarcode, 1);

        var directSale = NewSale(directSaleId, clientSaleId, SaleKind.Sale);
        var facadeSale = NewSale(facadeSaleId, clientSaleId, SaleKind.Sale);
        var directLines = new[]
        {
            NewLine(directSaleId, 101, "  " + firstBarcode + "  ", 2),
            NewLine(directSaleId, 102, secondBarcode, 4)
        };
        var facadeLines = new[]
        {
            NewLine(facadeSaleId, 101, "  " + firstBarcode + "  ", 2),
            NewLine(facadeSaleId, 102, secondBarcode, 4)
        };

        await ApplyDirectAndCommitAsync(directDb.Factory, directSale, directLines, clientSaleId);
        await ApplyFacadeAndCommitAsync(facadeDb.Factory, facadeSale, facadeLines);

        var directSnapshot = await LoadSnapshotAsync(directDb.Factory, firstBarcode, secondBarcode);
        var facadeSnapshot = await LoadSnapshotAsync(facadeDb.Factory, firstBarcode, secondBarcode);

        AssertSnapshotEqual(directSnapshot, facadeSnapshot);
        Assert.AreEqual(3L, directSnapshot.StockByBarcode[firstBarcode]);
        Assert.AreEqual(0L, directSnapshot.StockByBarcode[secondBarcode]);
        Assert.AreEqual(2, directSnapshot.Movements.Count);
        StringAssert.StartsWith(
            directSnapshot.Movements[0].MovementKey,
            clientSaleId + ":");
        AssertMovement(
            directSnapshot.Movements.Single(row => row.Barcode == firstBarcode),
            clientSaleId + ":101:sale_decrement",
            directSaleId,
            101,
            -2,
            "sale_decrement");
        AssertMovement(
            directSnapshot.Movements.Single(row => row.Barcode == secondBarcode),
            clientSaleId + ":102:sale_decrement",
            directSaleId,
            102,
            -4,
            "sale_decrement");
    }

    [TestMethod]
    public async Task SaleStockMovementWriter_OrdinaryNegativeQuantityUsesNegativeAbsoluteDelta()
    {
        using var db = TestDb.Create();
        const string barcode = "F3-ORDINARY-NEGATIVE";
        const string clientSaleId = "f3-ordinary-negative-sale";
        var saleId = await InsertSaleAsync(db.Factory, "F3-ORDINARY-NEGATIVE", clientSaleId);
        await SeedStockAsync(db.Factory, barcode, 10);

        await ApplyDirectAndCommitAsync(
            db.Factory,
            NewSale(saleId, clientSaleId, SaleKind.Sale),
            new[] { NewLine(saleId, 151, barcode, -3) },
            clientSaleId);

        var snapshot = await LoadSnapshotAsync(db.Factory, barcode);
        Assert.AreEqual(7L, snapshot.StockByBarcode[barcode]);
        Assert.AreEqual(1, snapshot.Movements.Count);
        AssertMovement(
            snapshot.Movements[0],
            clientSaleId + ":151:sale_decrement",
            saleId,
            151,
            -3,
            "sale_decrement");
    }

    [TestMethod]
    public async Task SaleStockMovementWriter_SkipsBlankZeroAndReservedEconomicLines()
    {
        using var db = TestDb.Create();
        const string barcode = "F3-SKIP-LIVE";
        const string clientSaleId = "f3-skip-sale";
        var saleId = await InsertSaleAsync(db.Factory, "F3-SKIP", clientSaleId);
        await SeedStockAsync(db.Factory, barcode, 10);

        var lines = new[]
        {
            NewLine(saleId, 201, null, 2),
            NewLine(saleId, 202, "   ", 2),
            NewLine(saleId, 203, barcode, 0),
            NewLine(saleId, 204, "DISC:F3-SKIP", 2),
            NewLine(saleId, 205, "TAX:F3-SKIP", 2),
            NewLine(saleId, 206, "MANUAL:F3-SKIP", 2),
            NewLine(saleId, 207, " " + barcode + " ", 2)
        };

        await ApplyDirectAndCommitAsync(
            db.Factory,
            NewSale(saleId, clientSaleId, SaleKind.Sale),
            lines,
            clientSaleId);

        var snapshot = await LoadSnapshotAsync(db.Factory, barcode);
        Assert.AreEqual(8L, snapshot.StockByBarcode[barcode]);
        Assert.AreEqual(1, snapshot.Movements.Count);
        AssertMovement(
            snapshot.Movements[0],
            clientSaleId + ":207:sale_decrement",
            saleId,
            207,
            -2,
            "sale_decrement");
    }

    [TestMethod]
    public async Task SaleStockMovementWriter_DuplicateLineIsIdempotentAndStockClampsAtZero()
    {
        using var db = TestDb.Create();
        const string barcode = "F3-DUPLICATE-CLAMP";
        const string clientSaleId = "f3-duplicate-sale";
        var saleId = await InsertSaleAsync(db.Factory, "F3-DUPLICATE", clientSaleId);
        var sale = NewSale(saleId, clientSaleId, SaleKind.Sale);
        var lines = new[] { NewLine(saleId, 301, barcode, 5) };
        await SeedStockAsync(db.Factory, barcode, 2);

        await ApplyDirectAndCommitAsync(db.Factory, sale, lines, clientSaleId);
        await ApplyDirectAndCommitAsync(db.Factory, sale, lines, clientSaleId);

        var snapshot = await LoadSnapshotAsync(db.Factory, barcode);
        Assert.AreEqual(0L, snapshot.StockByBarcode[barcode]);
        Assert.AreEqual(1, snapshot.Movements.Count);
        AssertMovement(
            snapshot.Movements[0],
            clientSaleId + ":301:sale_decrement",
            saleId,
            301,
            -5,
            "sale_decrement");
    }

    [TestMethod]
    public async Task SaleStockMovementWriter_MissingProductMetadataKeepsLedgerOnly()
    {
        using var db = TestDb.Create();
        const string barcode = "F3-LEDGER-ONLY";
        const string clientSaleId = "f3-ledger-only-sale";
        var saleId = await InsertSaleAsync(db.Factory, "F3-LEDGER-ONLY", clientSaleId);

        await ApplyDirectAndCommitAsync(
            db.Factory,
            NewSale(saleId, clientSaleId, SaleKind.Sale),
            new[] { NewLine(saleId, 351, barcode, 2) },
            clientSaleId);

        using var conn = db.Factory.Open();
        Assert.AreEqual(0L, await ScalarAsync(
            conn,
            "SELECT COUNT(1) FROM product_meta WHERE barcode = @barcode;",
            new { barcode }));
        var row = await conn.QuerySingleAsync<MovementRow>(@"
SELECT movement_key AS MovementKey,
       sale_id AS SaleId,
       sale_line_id AS SaleLineId,
       barcode AS Barcode,
       quantity_delta AS QuantityDelta,
       movement_kind AS MovementKind,
       created_at AS CreatedAt
FROM local_stock_movements
WHERE barcode = @barcode;", new { barcode });
        AssertMovement(
            row,
            clientSaleId + ":351:sale_decrement",
            saleId,
            351,
            -2,
            "sale_decrement");
    }

    [TestMethod]
    public async Task SaleStockMovementWriter_ZeroLineIdStoresNullSaleLineId()
    {
        using var db = TestDb.Create();
        const string barcode = "F3-ZERO-LINE-ID";
        const string clientSaleId = "f3-zero-line-id-sale";
        var saleId = await InsertSaleAsync(db.Factory, "F3-ZERO-LINE-ID", clientSaleId);
        await SeedStockAsync(db.Factory, barcode, 3);

        await ApplyDirectAndCommitAsync(
            db.Factory,
            NewSale(saleId, clientSaleId, SaleKind.Sale),
            new[] { NewLine(saleId, 0, barcode, 1) },
            clientSaleId);

        using var conn = db.Factory.Open();
        var row = await conn.QuerySingleAsync<MovementRow>(@"
SELECT movement_key AS MovementKey,
       sale_id AS SaleId,
       sale_line_id AS SaleLineId,
       barcode AS Barcode,
       quantity_delta AS QuantityDelta,
       movement_kind AS MovementKind,
       created_at AS CreatedAt
FROM local_stock_movements
WHERE barcode = @barcode;", new { barcode });
        Assert.AreEqual(clientSaleId + ":0:sale_decrement", row.MovementKey);
        Assert.AreEqual(saleId, row.SaleId);
        Assert.IsNull(row.SaleLineId);
        Assert.AreEqual(barcode, row.Barcode);
        Assert.AreEqual(-1L, row.QuantityDelta);
        Assert.AreEqual("sale_decrement", row.MovementKind);
        Assert.AreEqual(1700000000100L, row.CreatedAt);
        Assert.AreEqual(2L, await ScalarAsync(
            conn,
            "SELECT stock_qty FROM product_meta WHERE barcode = @barcode;",
            new { barcode }));
    }

    [TestMethod]
    public async Task SaleStockMovementWriter_UsesRefundAndVoidIncrementMappings()
    {
        using var db = TestDb.Create();
        const string barcode = "F3-REVERSAL";
        const string refundClientSaleId = "f3-refund-sale";
        const string voidClientSaleId = "f3-void-sale";
        var refundSaleId = await InsertSaleAsync(db.Factory, "F3-REFUND", refundClientSaleId);
        var voidSaleId = await InsertSaleAsync(db.Factory, "F3-VOID", voidClientSaleId);
        await SeedStockAsync(db.Factory, barcode, 1);

        await ApplyDirectAndCommitAsync(
            db.Factory,
            NewSale(refundSaleId, refundClientSaleId, SaleKind.Refund),
            new[] { NewLine(refundSaleId, 401, barcode, -3) },
            refundClientSaleId);
        await ApplyDirectAndCommitAsync(
            db.Factory,
            NewSale(voidSaleId, voidClientSaleId, SaleKind.Void),
            new[] { NewLine(voidSaleId, 402, barcode, 2) },
            voidClientSaleId);

        var snapshot = await LoadSnapshotAsync(db.Factory, barcode);
        Assert.AreEqual(6L, snapshot.StockByBarcode[barcode]);
        Assert.AreEqual(2, snapshot.Movements.Count);
        AssertMovement(
            snapshot.Movements.Single(row => row.MovementKind == "refund_increment"),
            refundClientSaleId + ":401:refund_increment",
            refundSaleId,
            401,
            3,
            "refund_increment");
        AssertMovement(
            snapshot.Movements.Single(row => row.MovementKind == "void_reverse"),
            voidClientSaleId + ":402:void_reverse",
            voidSaleId,
            402,
            2,
            "void_reverse");
    }

    [TestMethod]
    public async Task SaleStockMovementWriter_CallerTransactionRollbackLeavesNoLedgerOrStockMutation()
    {
        using var db = TestDb.Create();
        const string barcode = "F3-ROLLBACK";
        const string clientSaleId = "f3-rollback-sale";
        var saleId = await InsertSaleAsync(db.Factory, "F3-ROLLBACK", clientSaleId);
        await SeedStockAsync(db.Factory, barcode, 9);

        using (var conn = db.Factory.Open())
        using (var tx = conn.BeginTransaction())
        {
            await new SaleStockMovementWriter().ApplyAsync(
                conn,
                tx,
                NewSale(saleId, clientSaleId, SaleKind.Sale),
                new[] { NewLine(saleId, 501, barcode, 4) },
                clientSaleId);

            Assert.AreEqual(1L, await ScalarAsync(
                conn,
                "SELECT COUNT(1) FROM local_stock_movements WHERE barcode = @barcode;",
                new { barcode },
                tx));
            Assert.AreEqual(5L, await ScalarAsync(
                conn,
                "SELECT stock_qty FROM product_meta WHERE barcode = @barcode;",
                new { barcode },
                tx));
            tx.Rollback();
        }

        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await ScalarAsync(
            verify,
            "SELECT COUNT(1) FROM local_stock_movements WHERE barcode = @barcode;",
            new { barcode }));
        Assert.AreEqual(9L, await ScalarAsync(
            verify,
            "SELECT stock_qty FROM product_meta WHERE barcode = @barcode;",
            new { barcode }));
    }

    [TestMethod]
    public async Task SaleFacade_CallerTransactionRollbackLeavesNoLedgerOrStockMutation()
    {
        using var db = TestDb.Create();
        const string barcode = "F3-FACADE-ROLLBACK";
        const string clientSaleId = "f3-facade-rollback-sale";
        var saleId = await InsertSaleAsync(db.Factory, "F3-FACADE-ROLLBACK", clientSaleId);
        await SeedStockAsync(db.Factory, barcode, 9);

        using (var conn = db.Factory.Open())
        using (var tx = conn.BeginTransaction())
        {
            await new SaleRepository(db.Factory).ApplyLocalStockMovementsAsync(
                conn,
                tx,
                NewSale(saleId, clientSaleId, SaleKind.Sale),
                new[] { NewLine(saleId, 551, barcode, 4) });

            Assert.AreEqual(1L, await ScalarAsync(
                conn,
                "SELECT COUNT(1) FROM local_stock_movements WHERE barcode = @barcode;",
                new { barcode },
                tx));
            Assert.AreEqual(5L, await ScalarAsync(
                conn,
                "SELECT stock_qty FROM product_meta WHERE barcode = @barcode;",
                new { barcode },
                tx));
            tx.Rollback();
        }

        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await ScalarAsync(
            verify,
            "SELECT COUNT(1) FROM local_stock_movements WHERE barcode = @barcode;",
            new { barcode }));
        Assert.AreEqual(9L, await ScalarAsync(
            verify,
            "SELECT stock_qty FROM product_meta WHERE barcode = @barcode;",
            new { barcode }));
    }

    [TestMethod]
    public async Task SaleFacade_EmptyLinesAndBlankClientSaleIdReturnsBeforeFallbackOrDelegation()
    {
        using var db = TestDb.Create();
        const string barcode = "F3-EMPTY-LINES";
        var saleId = await InsertSaleAsync(db.Factory, "F3-EMPTY-LINES", null);
        await SeedStockAsync(db.Factory, barcode, 4);
        var sale = NewSale(saleId, null, SaleKind.Sale);

        using (var conn = db.Factory.Open())
        using (var tx = conn.BeginTransaction())
        {
            await new SaleRepository(db.Factory).ApplyLocalStockMovementsAsync(
                conn,
                tx,
                sale,
                Array.Empty<SaleLine>());
            tx.Commit();
        }

        Assert.IsNull(sale.ClientSaleId);
        using var verify = db.Factory.Open();
        Assert.IsNull(await verify.ExecuteScalarAsync<string>(
            "SELECT client_sale_id FROM sales WHERE id = @saleId;",
            new { saleId }));
        Assert.AreEqual(0L, await ScalarAsync(
            verify,
            "SELECT COUNT(1) FROM local_stock_movements WHERE sale_id = @saleId;",
            new { saleId }));
        Assert.AreEqual(4L, await ScalarAsync(
            verify,
            "SELECT stock_qty FROM product_meta WHERE barcode = @barcode;",
            new { barcode }));
    }

    [TestMethod]
    public async Task SaleFacade_BlankClientSaleIdFallsBackBeforeDelegatingToStockMovementWriter()
    {
        using var db = TestDb.Create();
        const string barcode = "F3-FALLBACK";
        var saleId = await InsertSaleAsync(db.Factory, "F3-FALLBACK", null);
        await SeedStockAsync(db.Factory, barcode, 4);
        var sale = NewSale(saleId, null, SaleKind.Sale);

        await ApplyFacadeAndCommitAsync(
            db.Factory,
            sale,
            new[] { NewLine(saleId, 601, barcode, 1) });

        var expectedClientSaleId = "win7pos-sale-" + saleId.ToString(CultureInfo.InvariantCulture);
        Assert.AreEqual(expectedClientSaleId, sale.ClientSaleId);
        using var conn = db.Factory.Open();
        Assert.AreEqual(expectedClientSaleId, await conn.ExecuteScalarAsync<string>(
            "SELECT client_sale_id FROM sales WHERE id = @saleId;",
            new { saleId }));
        var row = await conn.QuerySingleAsync<MovementRow>(@"
SELECT movement_key AS MovementKey,
       sale_id AS SaleId,
       sale_line_id AS SaleLineId,
       barcode AS Barcode,
       quantity_delta AS QuantityDelta,
       movement_kind AS MovementKind,
       created_at AS CreatedAt
FROM local_stock_movements
WHERE sale_id = @saleId;", new { saleId });
        AssertMovement(
            row,
            expectedClientSaleId + ":601:sale_decrement",
            saleId,
            601,
            -1,
            "sale_decrement");
        Assert.AreEqual(3L, await ScalarAsync(
            conn,
            "SELECT stock_qty FROM product_meta WHERE barcode = @barcode;",
            new { barcode }));
    }

    private static Sale NewSale(long saleId, string? clientSaleId, SaleKind kind)
    {
        return new Sale
        {
            Id = saleId,
            ClientSaleId = clientSaleId!,
            Kind = (int)kind,
            CreatedAt = 1700000000100L
        };
    }

    private static SaleLine NewLine(long saleId, long lineId, string? barcode, int quantity)
    {
        return new SaleLine
        {
            Id = lineId,
            SaleId = saleId,
            Barcode = barcode!,
            Name = "F3 stock line",
            Quantity = quantity,
            UnitPrice = 100,
            LineTotal = quantity * 100L
        };
    }

    private static async Task<long> InsertSaleAsync(
        SqliteConnectionFactory factory,
        string code,
        string? clientSaleId)
    {
        using var conn = factory.Open();
        return await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(client_sale_id, code, createdAt, kind, total, paidCash, paidCard, change)
VALUES(@clientSaleId, @code, 1700000000000, 0, 0, 0, 0, 0);
SELECT last_insert_rowid();", new { clientSaleId, code });
    }

    private static async Task SeedStockAsync(SqliteConnectionFactory factory, string barcode, int stockQty)
    {
        using var conn = factory.Open();
        await conn.ExecuteAsync(@"
INSERT INTO product_meta(barcode, stock_qty)
VALUES(@barcode, @stockQty);", new { barcode, stockQty });
    }

    private static async Task ApplyDirectAndCommitAsync(
        SqliteConnectionFactory factory,
        Sale sale,
        IReadOnlyList<SaleLine> lines,
        string clientSaleId)
    {
        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        await new SaleStockMovementWriter()
            .ApplyAsync(conn, tx, sale, lines, clientSaleId);
        tx.Commit();
    }

    private static async Task ApplyFacadeAndCommitAsync(
        SqliteConnectionFactory factory,
        Sale sale,
        IReadOnlyList<SaleLine> lines)
    {
        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        await new SaleRepository(factory)
            .ApplyLocalStockMovementsAsync(conn, tx, sale, lines);
        tx.Commit();
    }

    private static async Task<StockMovementSnapshot> LoadSnapshotAsync(
        SqliteConnectionFactory factory,
        params string[] barcodes)
    {
        using var conn = factory.Open();
        var stockRows = await conn.QueryAsync<StockRow>(@"
SELECT barcode AS Barcode, stock_qty AS StockQty
FROM product_meta
WHERE barcode IN @barcodes
ORDER BY barcode ASC;", new { barcodes });
        var movementRows = await conn.QueryAsync<MovementRow>(@"
SELECT movement_key AS MovementKey,
       sale_id AS SaleId,
       sale_line_id AS SaleLineId,
       barcode AS Barcode,
       quantity_delta AS QuantityDelta,
       movement_kind AS MovementKind,
       created_at AS CreatedAt
FROM local_stock_movements
WHERE barcode IN @barcodes
ORDER BY barcode ASC, id ASC;", new { barcodes });
        return new StockMovementSnapshot
        {
            StockByBarcode = stockRows.ToDictionary(row => row.Barcode, row => row.StockQty),
            Movements = movementRows.ToArray()
        };
    }

    private static async Task<long> ScalarAsync(
        SqliteConnection conn,
        string sql,
        object? parameters = null,
        SqliteTransaction? tx = null)
    {
        var value = await conn.ExecuteScalarAsync(sql, parameters, tx);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void AssertSnapshotEqual(
        StockMovementSnapshot expected,
        StockMovementSnapshot actual)
    {
        Assert.AreEqual(expected.StockByBarcode.Count, actual.StockByBarcode.Count);
        foreach (var item in expected.StockByBarcode)
        {
            Assert.IsTrue(actual.StockByBarcode.TryGetValue(item.Key, out var actualStock));
            Assert.AreEqual(item.Value, actualStock, item.Key);
        }

        Assert.AreEqual(expected.Movements.Count, actual.Movements.Count);
        for (var index = 0; index < expected.Movements.Count; index++)
        {
            var expectedRow = expected.Movements[index];
            var actualRow = actual.Movements[index];
            Assert.AreEqual(expectedRow.MovementKey, actualRow.MovementKey);
            Assert.AreEqual(expectedRow.SaleId, actualRow.SaleId);
            Assert.AreEqual(expectedRow.SaleLineId, actualRow.SaleLineId);
            Assert.AreEqual(expectedRow.Barcode, actualRow.Barcode);
            Assert.AreEqual(expectedRow.QuantityDelta, actualRow.QuantityDelta);
            Assert.AreEqual(expectedRow.MovementKind, actualRow.MovementKind);
            Assert.AreEqual(expectedRow.CreatedAt, actualRow.CreatedAt);
        }
    }

    private static void AssertMovement(
        MovementRow row,
        string movementKey,
        long saleId,
        long saleLineId,
        long quantityDelta,
        string movementKind)
    {
        Assert.AreEqual(movementKey, row.MovementKey);
        Assert.AreEqual(saleId, row.SaleId);
        Assert.AreEqual(saleLineId, row.SaleLineId);
        Assert.AreEqual(quantityDelta, row.QuantityDelta);
        Assert.AreEqual(movementKind, row.MovementKind);
        Assert.AreEqual(1700000000100L, row.CreatedAt);
    }

    private sealed class StockMovementSnapshot
    {
        public IReadOnlyDictionary<string, long> StockByBarcode { get; set; } =
            new Dictionary<string, long>();

        public IReadOnlyList<MovementRow> Movements { get; set; } = Array.Empty<MovementRow>();
    }

    private sealed class StockRow
    {
        public string Barcode { get; set; } = string.Empty;
        public long StockQty { get; set; }
    }

    private sealed class MovementRow
    {
        public string MovementKey { get; set; } = string.Empty;
        public long SaleId { get; set; }
        public long? SaleLineId { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public long QuantityDelta { get; set; }
        public string MovementKind { get; set; } = string.Empty;
        public long CreatedAt { get; set; }
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
            var root = Path.Combine(
                Path.GetTempPath(),
                "win7pos-sale-stock-movement-writer-" + Guid.NewGuid().ToString("N"));
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
