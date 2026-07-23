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
public sealed class SaleReadRepositoryTests
{
    [TestMethod]
    public async Task SaleReadRepository_AndSaleFacade_KeepReadParity()
    {
        using var db = TestDb.Create();
        var day = new DateTime(2026, 2, 3);
        var from = ToUnixMilliseconds(day);
        var afterNextDay = ToUnixMilliseconds(day.AddDays(2));
        var firstId = await InsertSaleAsync(
            db,
            "F1-PARITY-001",
            ToUnixMilliseconds(day.AddHours(9)),
            100,
            100,
            0);
        var secondId = await InsertSaleAsync(
            db,
            "F1-PARITY-002",
            ToUnixMilliseconds(day.AddHours(13)),
            200,
            0,
            200,
            operatorId: 7,
            pdfPrinted: true,
            snapshot: "{\"shop\":\"parity\"}");
        await InsertSaleAsync(
            db,
            "F1-PARITY-003",
            ToUnixMilliseconds(day.AddDays(1).AddHours(10)),
            -35,
            -35,
            0,
            kind: SaleKind.Refund,
            relatedSaleId: firstId,
            reason: "parity refund");

        var reader = new SaleReadRepository(db.Factory);
        var facade = new SaleRepository(db.Factory);

        AssertSalesEqual(
            await reader.LastSalesAsync(2),
            await facade.LastSalesAsync(2));
        AssertSalesEqual(
            await reader.GetSalesBetweenAsync(from, afterNextDay, operatorId: null, includeFiscalPrinted: false),
            await facade.GetSalesBetweenAsync(from, afterNextDay, operatorId: null, includeFiscalPrinted: false));
        AssertSummaryEqual(
            await reader.GetDailySummaryAsync(day, includeFiscalPrinted: false),
            await facade.GetDailySummaryAsync(day, includeFiscalPrinted: false));
        AssertSummaryRowsEqual(
            await reader.GetDailySummariesAsync(day, day.AddDays(1), includeFiscalPrinted: false),
            await facade.GetDailySummariesAsync(day, day.AddDays(1), includeFiscalPrinted: false));
        AssertSummaryRowsEqual(
            await reader.GetDailySummariesRangeAsync(day, day.AddDays(1), includeFiscalPrinted: false),
            await facade.GetDailySummariesRangeAsync(day, day.AddDays(1), includeFiscalPrinted: false));
        AssertSalesEqual(
            await reader.GetSalesForDateAsync(day, includeFiscalPrinted: false),
            await facade.GetSalesForDateAsync(day, includeFiscalPrinted: false));
        CollectionAssert.AreEqual(
            (await reader.GetHourlySalesAsync(day, includeFiscalPrinted: false)).ToArray(),
            (await facade.GetHourlySalesAsync(day, includeFiscalPrinted: false)).ToArray());
        AssertSaleEqual(
            await reader.GetByIdAsync(secondId),
            await facade.GetByIdAsync(secondId));
        AssertSalesEqual(
            await reader.GetByCodeLikeAsync("PARITY", includeFiscalPrinted: false),
            await facade.GetByCodeLikeAsync("PARITY", includeFiscalPrinted: false));
    }

    [TestMethod]
    public async Task SaleReadRepository_DateBoundariesOperatorAndFiscalCompatibilityFlagKeepReadParity()
    {
        using var db = TestDb.Create();
        var day = new DateTime(2026, 2, 3);
        var from = ToUnixMilliseconds(day);
        var to = ToUnixMilliseconds(day.AddDays(1));
        var beforeId = await InsertSaleAsync(db, "F1-BEFORE", from - 1, 10, 10, 0);
        var midnightId = await InsertSaleAsync(
            db, "F1-MIDNIGHT", from, 100, 100, 0, operatorId: 7);
        var fiscalPrintedId = await InsertSaleAsync(
            db, "F1-FISCAL", ToUnixMilliseconds(day.AddHours(13)), 200, 0, 200, pdfPrinted: true);
        var refundId = await InsertSaleAsync(
            db,
            "F1-REFUND",
            ToUnixMilliseconds(day.AddHours(14)),
            -40,
            -40,
            0,
            kind: SaleKind.Refund,
            relatedSaleId: midnightId,
            reason: "date boundary refund");
        var otherOperatorId = await InsertSaleAsync(
            db, "F1-OPERATOR-8", ToUnixMilliseconds(day.AddHours(15)), 50, 50, 0, operatorId: 8);
        var nextDayId = await InsertSaleAsync(db, "F1-NEXT", to, 300, 300, 0);

        var reader = new SaleReadRepository(db.Factory);
        var facade = new SaleRepository(db.Factory);

        var directRange = await reader.GetSalesBetweenAsync(from, to, includeFiscalPrinted: false);
        var facadeRange = await facade.GetSalesBetweenAsync(from, to, includeFiscalPrinted: false);
        AssertSalesEqual(directRange, facadeRange);
        CollectionAssert.AreEqual(
            new[] { midnightId, fiscalPrintedId, refundId, otherOperatorId },
            directRange.Select(row => row.Id).ToArray());
        Assert.IsTrue(directRange.Any(row => row.Id == fiscalPrintedId && row.PdfPrinted));
        Assert.IsFalse(directRange.Any(row => row.Id == beforeId || row.Id == nextDayId));

        var directIncludingFiscal = await reader.GetSalesBetweenAsync(from, to, includeFiscalPrinted: true);
        AssertSalesEqual(directRange, directIncludingFiscal);
        AssertSalesEqual(
            directIncludingFiscal,
            await facade.GetSalesBetweenAsync(from, to, includeFiscalPrinted: true));

        var directOperatorRows = await reader.GetSalesBetweenAsync(
            from,
            to,
            operatorId: 7,
            includeFiscalPrinted: false);
        AssertSalesEqual(
            directOperatorRows,
            await facade.GetSalesBetweenAsync(from, to, operatorId: 7, includeFiscalPrinted: false));
        CollectionAssert.AreEqual(
            new[] { midnightId, fiscalPrintedId, refundId },
            directOperatorRows.Select(row => row.Id).ToArray());

        AssertSalesEqual(
            directRange,
            await reader.GetSalesForDateAsync(day, includeFiscalPrinted: false));
        AssertSalesEqual(
            directRange,
            await facade.GetSalesForDateAsync(day, includeFiscalPrinted: false));

        var directSummary = await reader.GetDailySummaryAsync(day, includeFiscalPrinted: false);
        AssertSummaryEqual(directSummary, await facade.GetDailySummaryAsync(day, includeFiscalPrinted: false));
        Assert.AreEqual(day, directSummary.Date);
        Assert.AreEqual(3, directSummary.SalesCount);
        Assert.AreEqual(310L, directSummary.TotalAmount);
        Assert.AreEqual(110L, directSummary.CashAmount);
        Assert.AreEqual(200L, directSummary.CardAmount);
        Assert.AreEqual(350L, directSummary.GrossSalesAmount);
        Assert.AreEqual(40L, directSummary.RefundsAmount);
        Assert.AreEqual(310L, directSummary.NetAmount);

        var directHourly = await reader.GetHourlySalesAsync(day, includeFiscalPrinted: false);
        CollectionAssert.AreEqual(
            directHourly.ToArray(),
            (await facade.GetHourlySalesAsync(day, includeFiscalPrinted: false)).ToArray());
        Assert.AreEqual(100L, directHourly[0]);
        Assert.AreEqual(200L, directHourly[13]);
        Assert.AreEqual(0L, directHourly[14]);
        Assert.AreEqual(50L, directHourly[15]);

        AssertSummaryRowsEqual(
            await reader.GetDailySummariesRangeAsync(day.AddDays(-1), day.AddDays(1), includeFiscalPrinted: false),
            await facade.GetDailySummariesRangeAsync(day.AddDays(-1), day.AddDays(1), includeFiscalPrinted: false));
    }

    [TestMethod]
    public async Task SaleReadRepository_InvalidRangesAndBlankCodeKeepReadParity()
    {
        using var db = TestDb.Create();
        var reader = new SaleReadRepository(db.Factory);
        var facade = new SaleRepository(db.Factory);
        var day = new DateTime(2026, 2, 3);

        AssertSalesEqual(
            await reader.GetSalesBetweenAsync(50, 40, includeFiscalPrinted: false),
            await facade.GetSalesBetweenAsync(50, 40, includeFiscalPrinted: false));
        Assert.AreEqual(0, (await reader.GetSalesBetweenAsync(50, 40, includeFiscalPrinted: false)).Count);
        AssertSalesEqual(
            await reader.GetSalesBetweenAsync(-100, 0, includeFiscalPrinted: false),
            await facade.GetSalesBetweenAsync(-100, 0, includeFiscalPrinted: false));
        Assert.AreEqual(0, (await reader.GetSalesBetweenAsync(-100, 0, includeFiscalPrinted: false)).Count);
        AssertSummaryRowsEqual(
            await reader.GetDailySummariesRangeAsync(day.AddDays(1), day, includeFiscalPrinted: false),
            await facade.GetDailySummariesRangeAsync(day.AddDays(1), day, includeFiscalPrinted: false));
        Assert.AreEqual(0, (await reader.GetDailySummariesAsync(day.AddDays(1), day, includeFiscalPrinted: false)).Count);
        AssertSalesEqual(
            await reader.GetByCodeLikeAsync("   ", includeFiscalPrinted: false),
            await facade.GetByCodeLikeAsync("   ", includeFiscalPrinted: false));
        Assert.AreEqual(0, (await reader.GetByCodeLikeAsync("   ", includeFiscalPrinted: false)).Count);
        Assert.IsNull(await reader.GetByIdAsync(987654321));
        Assert.IsNull(await facade.GetByIdAsync(987654321));
    }

    [TestMethod]
    public async Task SaleReadRepository_GetByIdKeepsReceiptAndReversalSnapshotParity()
    {
        using var db = TestDb.Create();
        var day = new DateTime(2026, 2, 3);
        var originalSnapshot = "{\"shop\":\"original\",\"version\":1}";
        var reversalSnapshot = "{\"shop\":\"refund\",\"version\":1}";
        var originalId = await InsertSaleAsync(
            db,
            "F1-SNAPSHOT-ORIGINAL",
            ToUnixMilliseconds(day.AddHours(10)),
            175,
            175,
            0,
            snapshot: originalSnapshot);
        var reversalId = await InsertSaleAsync(
            db,
            "F1-SNAPSHOT-REFUND",
            ToUnixMilliseconds(day.AddHours(11)),
            -175,
            -175,
            0,
            kind: SaleKind.Refund,
            relatedSaleId: originalId,
            reason: "customer return",
            snapshot: reversalSnapshot);
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
UPDATE sales
SET voided_by_sale_id = @reversalId,
    voided_at = @voidedAt
WHERE id = @originalId;",
                new
                {
                    originalId,
                    reversalId,
                    voidedAt = ToUnixMilliseconds(day.AddHours(11))
                });
        }

        var reader = new SaleReadRepository(db.Factory);
        var facade = new SaleRepository(db.Factory);
        var directOriginal = await reader.GetByIdAsync(originalId);
        var facadeOriginal = await facade.GetByIdAsync(originalId);
        var directReversal = await reader.GetByIdAsync(reversalId);
        var facadeReversal = await facade.GetByIdAsync(reversalId);

        AssertSaleEqual(directOriginal, facadeOriginal);
        AssertSaleEqual(directReversal, facadeReversal);
        Assert.AreEqual(originalSnapshot, directOriginal.ReceiptShopSnapshotJson);
        Assert.AreEqual(reversalId, directOriginal.VoidedBySaleId);
        Assert.AreEqual(originalId, directReversal.RelatedSaleId);
        Assert.AreEqual((int)SaleKind.Refund, directReversal.Kind);
        Assert.AreEqual("customer return", directReversal.Reason);
        Assert.AreEqual(reversalSnapshot, directReversal.ReceiptShopSnapshotJson);
    }

    [TestMethod]
    public async Task SaleReadRepository_GetByIdRejectsOversizedReceiptSnapshotParity()
    {
        using var db = TestDb.Create();
        var saleId = await InsertSaleAsync(
            db,
            "F1-SNAPSHOT-OVERSIZED",
            ToUnixMilliseconds(new DateTime(2026, 2, 3, 10, 0, 0)),
            100,
            100,
            0,
            snapshot: new string('x', ReceiptDocumentPolicy.MaxSnapshotJsonCharacters + 1));
        var reader = new SaleReadRepository(db.Factory);
        var facade = new SaleRepository(db.Factory);

        var directException = await Assert.ThrowsExactlyAsync<ReceiptContentValidationException>(
            () => reader.GetByIdAsync(saleId));
        var facadeException = await Assert.ThrowsExactlyAsync<ReceiptContentValidationException>(
            () => facade.GetByIdAsync(saleId));

        Assert.AreEqual("receipt_shop_snapshot_too_large", directException.Code);
        Assert.AreEqual(directException.Code, facadeException.Code);
        Assert.AreEqual(directException.Field, facadeException.Field);
        Assert.AreEqual(directException.Characters, facadeException.Characters);
        Assert.AreEqual(directException.Utf8Bytes, facadeException.Utf8Bytes);
    }

    [TestMethod]
    public async Task SaleReadRepository_ParallelReadsLeavePersistentStateUnchanged()
    {
        using var db = TestDb.Create();
        var day = new DateTime(2026, 2, 3);
        var from = ToUnixMilliseconds(day);
        var to = ToUnixMilliseconds(day.AddDays(1));
        for (var index = 0; index < 8; index++)
        {
            await InsertSaleAsync(
                db,
                "F1-PARALLEL-" + index,
                ToUnixMilliseconds(day.AddHours(index + 1)),
                100 + index,
                100 + index,
                0,
                operatorId: index % 2 == 0 ? 7 : (int?)null,
                pdfPrinted: index % 3 == 0);
        }

        var reader = new SaleReadRepository(db.Factory);
        var facade = new SaleRepository(db.Factory);
        var before = await LoadPersistentStateAsync(db.Factory);
        var reads = Enumerable.Range(0, 32)
            .Select(index =>
            {
                switch (index % 4)
                {
                    case 0:
                        return (Task)reader.LastSalesAsync(5);
                    case 1:
                        return facade.GetSalesBetweenAsync(from, to, operatorId: 7, includeFiscalPrinted: false);
                    case 2:
                        return (Task)reader.GetDailySummariesRangeAsync(day, day, includeFiscalPrinted: false);
                    default:
                        return facade.GetByCodeLikeAsync("F1-PARALLEL", includeFiscalPrinted: false);
                }
            })
            .ToArray();

        await Task.WhenAll(reads);

        AssertPersistentStateEqual(before, await LoadPersistentStateAsync(db.Factory));
    }

    private static long ToUnixMilliseconds(DateTime dateTime)
    {
        return new DateTimeOffset(dateTime).ToUnixTimeMilliseconds();
    }

    private static async Task<long> InsertSaleAsync(
        TestDb db,
        string code,
        long createdAt,
        long total,
        long paidCash,
        long paidCard,
        SaleKind kind = SaleKind.Sale,
        long? relatedSaleId = null,
        int? operatorId = null,
        bool pdfPrinted = false,
        string? reason = null,
        string? snapshot = null)
    {
        using var conn = db.Factory.Open();
        return await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(
  client_sale_id, code, createdAt, kind, related_sale_id, reason,
  total, paidCash, paidCard, change, operator_id, pdf_printed,
  sync_status, receipt_shop_snapshot)
VALUES(
  @clientSaleId, @code, @createdAt, @kind, @relatedSaleId, @reason,
  @total, @paidCash, @paidCard, 0, @operatorId, @pdfPrinted,
  'pending', @snapshot);
SELECT last_insert_rowid();",
            new
            {
                clientSaleId = "client-" + code,
                code,
                createdAt,
                kind = (int)kind,
                relatedSaleId,
                reason,
                total,
                paidCash,
                paidCard,
                operatorId,
                pdfPrinted = pdfPrinted ? 1 : 0,
                snapshot
            });
    }

    private static async Task<PersistentState> LoadPersistentStateAsync(SqliteConnectionFactory factory)
    {
        using var conn = factory.Open();
        return await conn.QuerySingleAsync<PersistentState>(@"
SELECT COUNT(1) AS SaleCount,
       COALESCE(SUM(total), 0) AS Total,
       COALESCE(SUM(COALESCE(pdf_printed, 0)), 0) AS PrintedCount,
       COALESCE(SUM(CASE WHEN kind IN (1, 2) THEN 1 ELSE 0 END), 0) AS ReversalCount
FROM sales;");
    }

    private static void AssertSalesEqual(IReadOnlyList<Sale> expected, IReadOnlyList<Sale> actual)
    {
        Assert.AreEqual(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            AssertSaleEqual(expected[index], actual[index]);
        }
    }

    private static void AssertSaleEqual(Sale expected, Sale actual)
    {
        Assert.IsNotNull(expected);
        Assert.IsNotNull(actual);
        Assert.AreEqual(expected.Id, actual.Id);
        Assert.AreEqual(expected.ClientSaleId, actual.ClientSaleId);
        Assert.AreEqual(expected.Code, actual.Code);
        Assert.AreEqual(expected.CreatedAt, actual.CreatedAt);
        Assert.AreEqual(expected.Kind, actual.Kind);
        Assert.AreEqual(expected.RelatedSaleId, actual.RelatedSaleId);
        Assert.AreEqual(expected.VoidedBySaleId, actual.VoidedBySaleId);
        Assert.AreEqual(expected.VoidedAt, actual.VoidedAt);
        Assert.AreEqual(expected.Reason, actual.Reason);
        Assert.AreEqual(expected.Total, actual.Total);
        Assert.AreEqual(expected.PaidCash, actual.PaidCash);
        Assert.AreEqual(expected.PaidCard, actual.PaidCard);
        Assert.AreEqual(expected.Change, actual.Change);
        Assert.AreEqual(expected.OperatorId, actual.OperatorId);
        Assert.AreEqual(expected.PdfPrinted, actual.PdfPrinted);
        Assert.AreEqual(expected.SyncStatus, actual.SyncStatus);
        Assert.AreEqual(expected.ReceiptShopSnapshotJson, actual.ReceiptShopSnapshotJson);
    }

    private static void AssertSummaryRowsEqual(
        IReadOnlyList<DailySalesSummary> expected,
        IReadOnlyList<DailySalesSummary> actual)
    {
        Assert.AreEqual(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            AssertSummaryEqual(expected[index], actual[index]);
        }
    }

    private static void AssertSummaryEqual(DailySalesSummary expected, DailySalesSummary actual)
    {
        Assert.IsNotNull(expected);
        Assert.IsNotNull(actual);
        Assert.AreEqual(expected.Date, actual.Date);
        Assert.AreEqual(expected.SalesCount, actual.SalesCount);
        Assert.AreEqual(expected.TotalAmount, actual.TotalAmount);
        Assert.AreEqual(expected.CashAmount, actual.CashAmount);
        Assert.AreEqual(expected.CardAmount, actual.CardAmount);
        Assert.AreEqual(expected.GrossSalesAmount, actual.GrossSalesAmount);
        Assert.AreEqual(expected.RefundsAmount, actual.RefundsAmount);
        Assert.AreEqual(expected.NetAmount, actual.NetAmount);
    }

    private static void AssertPersistentStateEqual(PersistentState expected, PersistentState actual)
    {
        Assert.AreEqual(expected.SaleCount, actual.SaleCount);
        Assert.AreEqual(expected.Total, actual.Total);
        Assert.AreEqual(expected.PrintedCount, actual.PrintedCount);
        Assert.AreEqual(expected.ReversalCount, actual.ReversalCount);
    }

    private sealed class PersistentState
    {
        public long SaleCount { get; set; }
        public long Total { get; set; }
        public long PrintedCount { get; set; }
        public long ReversalCount { get; set; }
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
            var root = Path.Combine(Path.GetTempPath(), "win7pos-sale-read-repository-" + Guid.NewGuid().ToString("N"));
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
