using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Reports;

namespace Win7POS.Core.Tests.Reports;

[TestClass]
public sealed class HistoricalSalesReportContractsTests
{
    [TestMethod]
    public void MixedSummary_PreservesSignedEventsCashChangeAndNativeAverage()
    {
        var nativeSale = Entry(SalesReportOrigin.Native, 1, 1000, 0, 0, 1000);
        var nativeVoid = Entry(SalesReportOrigin.Native, 2, -1000, 0, 0, -1000, SalesReportMovementKind.Void);
        var cash = Entry(SalesReportOrigin.Native, 3, 2000, 2500, 500, 0);
        var refund = Entry(SalesReportOrigin.Native, 4, -500, -500, 0, 0, SalesReportMovementKind.Refund);
        var historic = Entry(SalesReportOrigin.Historical, 1, 700, 0, 0, 700);
        var cancellation = Entry(SalesReportOrigin.Historical, 2, -700, 0, 0, -700, SalesReportMovementKind.Cancellation);
        var result = new[] { nativeSale, nativeVoid, cash, refund, historic, cancellation }
            .Aggregate(SalesReportSummary.Empty, (summary, entry) => summary.Add(SalesReportSummary.FromEntry(entry)));
        Assert.AreEqual(3700L, result.GrossPositiveClp);
        Assert.AreEqual(2200L, result.ReversalsClp);
        Assert.AreEqual(1500L, result.BalanceClp);
        Assert.AreEqual(1500L, result.CashClp);
        Assert.AreEqual(2000L, result.CashTenderedClp);
        Assert.AreEqual(500L, result.ChangeClp);
        Assert.AreEqual(0L, result.CardClp);
        Assert.AreEqual(2L, result.NativePositiveReceiptCount);
        Assert.AreEqual(1500L, result.NativeAverageReceiptClp);
        Assert.AreEqual(2L, result.HistoricalMovementCount);
        Assert.IsFalse(historic.CanRefund);
        Assert.IsFalse(historic.CanReprintNativeReceipt);
        Assert.IsFalse(historic.Key.Equals(nativeSale.Key));
    }

    [TestMethod]
    public void NativeAverage_ExcludesImportedAndNegatives_AndAggregatesBeforeDividing()
    {
        var native = SalesReportSummary.FromEntry(Entry(SalesReportOrigin.Native, 1, 1000, 0, 0, 1000));
        var imported = SalesReportSummary.FromEntry(Entry(SalesReportOrigin.Historical, 1, 9000, 0, 0, 9000));
        Assert.AreEqual(1000L, native.Add(imported).NativeAverageReceiptClp);
        Assert.IsNull(imported.NativeAverageReceiptClp);
        Assert.IsNull(SalesReportSummary.FromEntry(Entry(SalesReportOrigin.Native, 2, -500, 0, 0, -500, SalesReportMovementKind.Void)).NativeAverageReceiptClp);
        var secondDay = Enumerable.Range(2, 3).Select(id => Entry(SalesReportOrigin.Native, id, 2000, 0, 0, 2000))
            .Aggregate(SalesReportSummary.Empty, (summary, entry) => summary.Add(SalesReportSummary.FromEntry(entry)));
        Assert.AreEqual(1750L, native.Add(secondDay).NativeAverageReceiptClp);
    }

    [TestMethod]
    public void TimePolicy_GapIsEmptyAndFoldHasBothUtcSegments_WithoutMachineRules()
    {
        var policy = new SalesReportTimePolicy("synthetic", "v1", "independent manifest", new[]
        {
            new SalesReportUtcSegment(0, 4*3600000L, 0),
            new SalesReportUtcSegment(4*3600000L, 8*3600000L, 60),
            new SalesReportUtcSegment(8*3600000L, 24*3600000L, 0)
        });
        var day = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        Assert.AreEqual(0, policy.GetUtcIntervals(day.AddHours(4), day.AddHours(5)).Count);
        var fold = policy.GetUtcIntervals(day.AddHours(8.5), day.AddHours(9));
        Assert.AreEqual(2, fold.Count);
        Assert.AreEqual(27000000L, fold[0].FromUtcMs);
        Assert.AreEqual(28800000L, fold[0].ToUtcMs);
        Assert.AreEqual(30600000L, fold[1].FromUtcMs);
        Assert.AreEqual(32400000L, fold[1].ToUtcMs);
        Assert.AreEqual(day.AddHours(8.5).Ticks, policy.GetBusinessTicks(27000000));
        Assert.AreEqual(day.AddHours(8.5).Ticks, policy.GetBusinessTicks(30600000));
        Assert.ThrowsExactly<InvalidOperationException>(() => policy.GetBusinessTicks(-1));
        Assert.ThrowsExactly<ArgumentException>(() => policy.GetUtcIntervals(DateTime.SpecifyKind(day, DateTimeKind.Local), day.AddDays(1)));
    }

    [TestMethod]
    public void TimePolicy_FractionalTickBoundsSelectIntegerNativeMillisecondsExactly()
    {
        var day = new DateTime(1970, 1, 1);
        var policy = new SalesReportTimePolicy("fixed", "v1", "fixture", new[] { new SalesReportUtcSegment(0, 86400000, 0) });
        var segment = policy.GetUtcIntervals(day.AddTicks(1), day.AddTicks(10001)).Single();
        Assert.AreEqual(1L, segment.FromUtcMs);
        Assert.AreEqual(2L, segment.ToUtcMs);
    }

    [TestMethod]
    public void Cursor_SeparatesOriginsAndIdsAtIdenticalBusinessTicks()
    {
        var native = Entry(SalesReportOrigin.Native, 42, 1000, 0, 0, 1000);
        var historical = Entry(SalesReportOrigin.Historical, 42, 1000, 0, 0, 1000);
        var cursor = new SalesReportCursor(native.BusinessTicks, native.Key);
        Assert.IsFalse(SalesReportCursor.ComesAfter(native, cursor));
        Assert.IsTrue(SalesReportCursor.ComesAfter(historical, cursor));
        Assert.IsFalse(SalesReportCursor.ComesAfter(native, new SalesReportCursor(historical.BusinessTicks, historical.Key)));
    }

    [TestMethod]
    public void Csv_QuotesTextWithoutLoss_ProtectsOnlyTextAndRetainsTypedNegativeMoney()
    {
        Assert.AreEqual("\"A;B\"\"C\nD\"", SalesReportCsvSerializer.Text("A;B\"C\nD"));
        Assert.AreEqual("\"000123\"", SalesReportCsvSerializer.Text("000123"));
        Assert.AreEqual("\"' \t=1+1\"", SalesReportCsvSerializer.Text(" \t=1+1"));
        Assert.AreEqual("\"'-700\"", SalesReportCsvSerializer.Text("-700"));
        var entry = new SalesReportEntry(new SalesReportEntryKey(SalesReportOrigin.Historical, 1), new DateTime(2026, 8, 1).Ticks,
            SalesReportMovementKind.Cancellation, -700, 0, 0, -700, null, "000123", "Getnet", "financial-id", "4047659322", "batch", "Transactions", 7);
        var line = SalesReportCsvSerializer.Serialize(SalesReportCsvRow.Movement(Token(), new SalesReportCoverage(SalesReportCoverageStatus.VerifiedSubset), entry));
        StringAssert.Contains(line, ";-700;0;0;0;-700;");
        StringAssert.Contains(line, ";\"000123\";\"Getnet\";\"financial-id\";\"4047659322\";");
        Assert.IsFalse(line.Contains("'=", StringComparison.Ordinal));
        Assert.AreEqual(26, ParseCsv(line).Count);
    }

    [TestMethod]
    public void Csv_MetadataCarriesCoverageEvenForEmptySelection_AndPendingCannotPublish()
    {
        var token = Token();
        var snapshot = new SalesReportSnapshot(token, new SalesReportCoverage(SalesReportCoverageStatus.VerifiedSubset), SalesReportSummary.Empty,
            Array.Empty<SalesReportDaySummary>(), new long[24], new SalesReportPage(token, Array.Empty<SalesReportEntry>(), false));
        var cells = ParseCsv(SalesReportCsvSerializer.Serialize(SalesReportCsvRow.Metadata(snapshot)));
        Assert.AreEqual("REPORT", cells[0]);
        Assert.AreEqual("VerifiedSubset", cells[3]);
        Assert.AreEqual(string.Empty, cells[10]);
        Assert.AreEqual(string.Empty, cells[25]);
        Assert.ThrowsExactly<InvalidOperationException>(() => SalesReportCsvRow.Movement(token, new SalesReportCoverage(SalesReportCoverageStatus.PendingOwnershipVerification), Entry(SalesReportOrigin.Native, 1, 1000, 0, 0, 1000)));
    }

    [TestMethod]
    public void SignedGeometry_AllNegativeAndMixedAreNotClampedToZero()
    {
        var maximum = SalesReportHourlyGeometry.MaximumMagnitude(new[] { -500L, 1000L, 0L });
        Assert.AreEqual(1000m, maximum);
        Assert.AreEqual(0.5d, SalesReportHourlyGeometry.NegativeFraction(-500, maximum));
        Assert.AreEqual(1d, SalesReportHourlyGeometry.PositiveFraction(1000, maximum));
        var allNegative = SalesReportHourlyGeometry.MaximumMagnitude(new[] { -500L });
        Assert.AreEqual(1d, SalesReportHourlyGeometry.NegativeFraction(-500, allNegative));
        Assert.AreEqual(9223372036854775808m, SalesReportHourlyGeometry.MaximumMagnitude(new[] { long.MinValue }));
    }

    private static SalesReportEntry Entry(SalesReportOrigin origin, long id, long amount, long cash, long change, long card, SalesReportMovementKind kind = SalesReportMovementKind.Sale)
        => new(new SalesReportEntryKey(origin, id), new DateTime(2026, 2, 3, 10, 0, 0).Ticks, kind, amount, cash, change, card, origin == SalesReportOrigin.Native ? 7 : null, "reference");
    private static SalesReportReadToken Token() => new("dataset", 0, "authority", 1, "scope", "ownership", "timezone");
    private static List<string> ParseCsv(string value)
    {
        var result = new List<string>(); var cell = new System.Text.StringBuilder(); var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current == '"') { if (quoted && index + 1 < value.Length && value[index + 1] == '"') { cell.Append('"'); index++; } else quoted = !quoted; }
            else if (current == ';' && !quoted) { result.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(current);
        }
        Assert.IsFalse(quoted); result.Add(cell.ToString()); return result;
    }
}
