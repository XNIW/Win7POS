using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Models;
using Win7POS.Core.Receipt;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class SaleLineReadRepositoryTests
{
    [TestMethod]
    public async Task SaleLineReadRepository_AndSaleFacade_KeepOrderedFullLineParity()
    {
        using var db = TestDb.Create();
        var saleId = await InsertSaleAsync(db, "F2-LINES-PARITY");
        var firstId = await InsertLineAsync(
            db,
            saleId,
            productId: 71,
            barcode: "F2-ONE",
            name: "First line",
            quantity: 2,
            unitPrice: 1250,
            lineTotal: 2500,
            relatedOriginalLineId: null);
        var secondId = await InsertLineAsync(
            db,
            saleId,
            productId: null,
            barcode: "F2-SECOND",
            name: "Second linea Ω",
            quantity: 1,
            unitPrice: 99,
            lineTotal: 99,
            relatedOriginalLineId: firstId);
        var thirdId = await InsertLineAsync(
            db,
            saleId,
            productId: 73,
            barcode: "F2-THIRD",
            name: "Third line",
            quantity: -1,
            unitPrice: -50,
            lineTotal: 50,
            relatedOriginalLineId: secondId);

        var reader = new SaleLineReadRepository(db.Factory);
        var facade = new SaleRepository(db.Factory);
        var direct = await reader.GetLinesBySaleIdAsync(saleId);
        var throughFacade = await facade.GetLinesBySaleIdAsync(saleId);

        AssertSaleLinesEqual(direct, throughFacade);
        CollectionAssert.AreEqual(
            new[] { firstId, secondId, thirdId },
            direct.Select(line => line.Id).ToArray());
        Assert.AreEqual(saleId, direct[0].SaleId);
        Assert.AreEqual(71L, direct[0].ProductId);
        Assert.AreEqual("F2-ONE", direct[0].Barcode);
        Assert.AreEqual("First line", direct[0].Name);
        Assert.AreEqual(2, direct[0].Quantity);
        Assert.AreEqual(1250L, direct[0].UnitPrice);
        Assert.AreEqual(2500L, direct[0].LineTotal);
        Assert.IsNull(direct[0].RelatedOriginalLineId);
        Assert.IsNull(direct[1].ProductId);
        Assert.AreEqual("F2-SECOND", direct[1].Barcode);
        Assert.AreEqual("Second linea Ω", direct[1].Name);
        Assert.AreEqual(1, direct[1].Quantity);
        Assert.AreEqual(99L, direct[1].UnitPrice);
        Assert.AreEqual(99L, direct[1].LineTotal);
        Assert.AreEqual(firstId, direct[1].RelatedOriginalLineId);
        Assert.AreEqual(73L, direct[2].ProductId);
        Assert.AreEqual("F2-THIRD", direct[2].Barcode);
        Assert.AreEqual("Third line", direct[2].Name);
        Assert.AreEqual(-1, direct[2].Quantity);
        Assert.AreEqual(-50L, direct[2].UnitPrice);
        Assert.AreEqual(50L, direct[2].LineTotal);
        Assert.AreEqual(secondId, direct[2].RelatedOriginalLineId);
    }

    [TestMethod]
    public async Task SaleLineReadRepository_AndSaleFacade_KeepEmptyResultParity()
    {
        using var db = TestDb.Create();
        var saleId = await InsertSaleAsync(db, "F2-LINES-EMPTY");
        var reader = new SaleLineReadRepository(db.Factory);
        var facade = new SaleRepository(db.Factory);

        var direct = await reader.GetLinesBySaleIdAsync(saleId);
        var throughFacade = await facade.GetLinesBySaleIdAsync(saleId);

        AssertSaleLinesEqual(direct, throughFacade);
        Assert.AreEqual(0, direct.Count);
    }

    [TestMethod]
    public async Task SaleLineReadRepository_RejectsOversizedAggregateAndUtf8HistoricalBudgetParity()
    {
        using var db = TestDb.Create();
        var fieldOverflowSaleId = await InsertSaleAsync(db, "F2-LINES-FIELD-OVERFLOW");
        await InsertLineAsync(
            db,
            fieldOverflowSaleId,
            productId: null,
            barcode: "F2-OVERSIZED",
            name: new string('x', SalesReceiptContentPolicy.MaxSaleLineNameCharacters + 1),
            quantity: 1,
            unitPrice: 1,
            lineTotal: 1,
            relatedOriginalLineId: null);

        var aggregateOverflowSaleId = await InsertSaleAsync(db, "F2-LINES-AGGREGATE-OVERFLOW");
        for (var index = 0; index < 17; index++)
        {
            await InsertLineAsync(
                db,
                aggregateOverflowSaleId,
                productId: null,
                barcode: "F2-AGG-" + index,
                name: new string('a', SalesReceiptContentPolicy.MaxSaleLineNameCharacters),
                quantity: 1,
                unitPrice: 1,
                lineTotal: 1,
                relatedOriginalLineId: null);
        }

        var barcodeOverflowSaleId = await InsertSaleAsync(db, "F2-LINES-BARCODE-OVERFLOW");
        await InsertLineAsync(
            db,
            barcodeOverflowSaleId,
            productId: null,
            barcode: new string('b', SalesReceiptContentPolicy.MaxSaleLineBarcodeCharacters + 1),
            name: "Valid barcode-overflow name",
            quantity: 1,
            unitPrice: 1,
            lineTotal: 1,
            relatedOriginalLineId: null);

        var utf8OverflowSaleId = await InsertSaleAsync(db, "F2-LINES-UTF8-OVERFLOW");
        for (var index = 0; index < 11; index++)
        {
            await InsertLineAsync(
                db,
                utf8OverflowSaleId,
                productId: null,
                barcode: "F2-UTF8-" + index,
                name: new string('\u754c', SalesReceiptContentPolicy.MaxSaleLineNameCharacters),
                quantity: 1,
                unitPrice: 1,
                lineTotal: 1,
                relatedOriginalLineId: null);
        }

        var lineCountOverflowSaleId = await InsertSaleAsync(db, "F2-LINES-COUNT-OVERFLOW");
        await InsertRepeatedLinesAsync(
            db,
            lineCountOverflowSaleId,
            SalesReceiptContentPolicy.MaxSaleLines + 1,
            "F2-COUNT-",
            "Count-budget line");

        var reader = new SaleLineReadRepository(db.Factory);
        var facade = new SaleRepository(db.Factory);

        var nameException = await AssertBudgetFailureParityAsync(
            reader,
            facade,
            fieldOverflowSaleId,
            "receipt_sale_line_field_too_large");
        Assert.AreEqual("sale.lines", nameException.Field);
        Assert.AreEqual(SalesReceiptContentPolicy.MaxSaleLineNameCharacters + 1, nameException.Characters);
        Assert.AreEqual(-1, nameException.Utf8Bytes);

        var aggregateException = await AssertBudgetFailureParityAsync(
            reader,
            facade,
            aggregateOverflowSaleId,
            "receipt_sale_line_budget_exceeded");
        Assert.AreEqual("sale.lines.name.aggregate", aggregateException.Field);
        Assert.AreEqual(17 * SalesReceiptContentPolicy.MaxSaleLineNameCharacters, aggregateException.Characters);
        Assert.AreEqual(17 * SalesReceiptContentPolicy.MaxSaleLineNameCharacters, aggregateException.Utf8Bytes);

        var barcodeException = await AssertBudgetFailureParityAsync(
            reader,
            facade,
            barcodeOverflowSaleId,
            "receipt_sale_line_field_too_large");
        Assert.AreEqual("sale.lines", barcodeException.Field);
        Assert.AreEqual(SalesReceiptContentPolicy.MaxSaleLineBarcodeCharacters + 1, barcodeException.Characters);
        Assert.AreEqual(-1, barcodeException.Utf8Bytes);

        var utf8Exception = await AssertBudgetFailureParityAsync(
            reader,
            facade,
            utf8OverflowSaleId,
            "receipt_sale_line_budget_exceeded");
        Assert.AreEqual("sale.lines.name.aggregate", utf8Exception.Field);
        Assert.AreEqual(11 * SalesReceiptContentPolicy.MaxSaleLineNameCharacters, utf8Exception.Characters);
        Assert.AreEqual(11 * SalesReceiptContentPolicy.MaxSaleLineNameCharacters * 3, utf8Exception.Utf8Bytes);

        var countException = await AssertBudgetFailureParityAsync(
            reader,
            facade,
            lineCountOverflowSaleId,
            "receipt_sale_line_count_exceeded");
        Assert.AreEqual("sale.lines", countException.Field);
        Assert.AreEqual(SalesReceiptContentPolicy.MaxSaleLines + 1, countException.Characters);
        Assert.AreEqual(-1, countException.Utf8Bytes);
    }

    [TestMethod]
    public async Task SaleLineReadRepository_ParallelReadsLeavePersistentStateUnchanged()
    {
        using var db = TestDb.Create();
        var saleId = await InsertSaleAsync(db, "F2-LINES-PARALLEL");
        for (var index = 0; index < 8; index++)
        {
            await InsertLineAsync(
                db,
                saleId,
                productId: index % 2 == 0 ? index + 1 : (long?)null,
                barcode: "F2-PARALLEL-" + index,
                name: "Parallel line " + index,
                quantity: index + 1,
                unitPrice: 100 + index,
                lineTotal: (index + 1) * (100 + index),
                relatedOriginalLineId: null);
        }

        var reader = new SaleLineReadRepository(db.Factory);
        var facade = new SaleRepository(db.Factory);
        var before = await LoadPersistentStateAsync(db.Factory);
        var reads = Enumerable.Range(0, 32)
            .Select(index => index % 2 == 0
                ? (Task)reader.GetLinesBySaleIdAsync(saleId)
                : facade.GetLinesBySaleIdAsync(saleId))
            .ToArray();

        await Task.WhenAll(reads);

        AssertPersistentStateEqual(before, await LoadPersistentStateAsync(db.Factory));
    }

    [TestMethod]
    public async Task SaleLineReadRepository_StoredBudgetGuardUsesCallerTransactionWithoutCommit()
    {
        using var db = TestDb.Create();
        var saleId = await InsertSaleAsync(db, "F2-LINES-CALLER-TX");

        using (var conn = db.Factory.Open())
        using (var tx = conn.BeginTransaction())
        {
            await conn.ExecuteAsync(@"
INSERT INTO sale_lines(saleId, barcode, name, quantity, unitPrice, lineTotal)
VALUES(@saleId, 'F2-CALLER-TX', 'Visible only in caller transaction', 1, 10, 10);",
                new { saleId }, tx);

            var budget = await SaleLineReadRepository
                .EnsureStoredLineBudgetAsync(conn, tx, saleId);
            Assert.AreEqual(1L, budget.LineCount);
            Assert.AreEqual("Visible only in caller transaction".Length, budget.AggregateNameCharacters);

            tx.Rollback();
        }

        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM sale_lines WHERE saleId = @saleId;",
            new { saleId }));
    }

    private static async Task<ReceiptContentValidationException> AssertBudgetFailureParityAsync(
        SaleLineReadRepository reader,
        SaleRepository facade,
        long saleId,
        string expectedCode)
    {
        var directException = await Assert.ThrowsExactlyAsync<ReceiptContentValidationException>(
            () => reader.GetLinesBySaleIdAsync(saleId));
        var facadeException = await Assert.ThrowsExactlyAsync<ReceiptContentValidationException>(
            () => facade.GetLinesBySaleIdAsync(saleId));

        Assert.AreEqual(expectedCode, directException.Code);
        Assert.AreEqual(directException.Code, facadeException.Code);
        Assert.AreEqual(directException.Field, facadeException.Field);
        Assert.AreEqual(directException.Characters, facadeException.Characters);
        Assert.AreEqual(directException.Utf8Bytes, facadeException.Utf8Bytes);
        return directException;
    }

    private static async Task<long> InsertSaleAsync(TestDb db, string code)
    {
        using var conn = db.Factory.Open();
        return await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(code, createdAt, kind, total, paidCash, paidCard, change)
VALUES(@code, 1, 0, 0, 0, 0, 0);
SELECT last_insert_rowid();",
            new { code });
    }

    private static async Task<long> InsertLineAsync(
        TestDb db,
        long saleId,
        long? productId,
        string barcode,
        string name,
        int quantity,
        long unitPrice,
        long lineTotal,
        long? relatedOriginalLineId)
    {
        using var conn = db.Factory.Open();
        return await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sale_lines(
  saleId, productId, barcode, name, quantity, unitPrice, lineTotal, related_original_line_id)
VALUES(
  @saleId, @productId, @barcode, @name, @quantity, @unitPrice, @lineTotal, @relatedOriginalLineId);
SELECT last_insert_rowid();",
            new
            {
                saleId,
                productId,
                barcode,
                name,
                quantity,
                unitPrice,
                lineTotal,
                relatedOriginalLineId
            });
    }

    private static async Task InsertRepeatedLinesAsync(
        TestDb db,
        long saleId,
        int count,
        string barcodePrefix,
        string name)
    {
        using var conn = db.Factory.Open();
        using var tx = conn.BeginTransaction();
        var rows = Enumerable.Range(0, count)
            .Select(index => new
            {
                saleId,
                barcode = barcodePrefix + index,
                name,
                quantity = 1,
                unitPrice = 1,
                lineTotal = 1
            })
            .ToArray();
        await conn.ExecuteAsync(@"
INSERT INTO sale_lines(saleId, barcode, name, quantity, unitPrice, lineTotal)
VALUES(@saleId, @barcode, @name, @quantity, @unitPrice, @lineTotal);",
            rows,
            tx);
        tx.Commit();
    }

    private static async Task<PersistentState> LoadPersistentStateAsync(SqliteConnectionFactory factory)
    {
        using var conn = factory.Open();
        return await conn.QuerySingleAsync<PersistentState>(@"
SELECT COUNT(1) AS LineCount,
       COALESCE(SUM(quantity), 0) AS QuantityTotal,
       COALESCE(SUM(lineTotal), 0) AS LineTotal,
       COALESCE(MIN(id), 0) AS FirstLineId,
       COALESCE(MAX(id), 0) AS LastLineId
FROM sale_lines;");
    }

    private static void AssertSaleLinesEqual(
        IReadOnlyList<SaleLine> expected,
        IReadOnlyList<SaleLine> actual)
    {
        Assert.AreEqual(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            AssertSaleLineEqual(expected[index], actual[index]);
        }
    }

    private static void AssertSaleLineEqual(SaleLine expected, SaleLine actual)
    {
        Assert.IsNotNull(expected);
        Assert.IsNotNull(actual);
        Assert.AreEqual(expected.Id, actual.Id);
        Assert.AreEqual(expected.SaleId, actual.SaleId);
        Assert.AreEqual(expected.ProductId, actual.ProductId);
        Assert.AreEqual(expected.Barcode, actual.Barcode);
        Assert.AreEqual(expected.Name, actual.Name);
        Assert.AreEqual(expected.Quantity, actual.Quantity);
        Assert.AreEqual(expected.UnitPrice, actual.UnitPrice);
        Assert.AreEqual(expected.LineTotal, actual.LineTotal);
        Assert.AreEqual(expected.RelatedOriginalLineId, actual.RelatedOriginalLineId);
    }

    private static void AssertPersistentStateEqual(PersistentState expected, PersistentState actual)
    {
        Assert.AreEqual(expected.LineCount, actual.LineCount);
        Assert.AreEqual(expected.QuantityTotal, actual.QuantityTotal);
        Assert.AreEqual(expected.LineTotal, actual.LineTotal);
        Assert.AreEqual(expected.FirstLineId, actual.FirstLineId);
        Assert.AreEqual(expected.LastLineId, actual.LastLineId);
    }

    private sealed class PersistentState
    {
        public long FirstLineId { get; set; }
        public long LastLineId { get; set; }
        public long LineCount { get; set; }
        public long LineTotal { get; set; }
        public long QuantityTotal { get; set; }
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
            var root = Path.Combine(Path.GetTempPath(), "win7pos-sale-line-read-repository-" + Guid.NewGuid().ToString("N"));
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
