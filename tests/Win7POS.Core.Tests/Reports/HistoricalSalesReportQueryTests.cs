using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Reports;
using Win7POS.Core.Tests.Data;
using Win7POS.Data.Reports;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Reports;

[TestClass]
public sealed class HistoricalSalesReportQueryTests
{
    [TestMethod]
    public async Task MixedQuery_UsesVerifiedShopSubsetSignedAmountsAndOriginSafeKeyset()
    {
        using var fixture = await Fixture.CreateAsync();
        var filter = fixture.Filter(SalesReportOrigins.All);
        var snapshot = await fixture.Query.OpenAsync(filter, fixture.Policy, 1, CancellationToken.None);
        Assert.AreEqual(SalesReportCoverageStatus.VerifiedSubset, snapshot.Coverage.Status);
        Assert.AreEqual(10000L, snapshot.Summary.BalanceClp);
        Assert.AreEqual(10000L, snapshot.Summary.CardClp);
        Assert.AreEqual(1000L, snapshot.Summary.NativeAverageReceiptClp);
        Assert.AreEqual(1L, snapshot.Summary.NativePositiveReceiptCount);
        Assert.AreEqual(1L, snapshot.Summary.HistoricalMovementCount);
        Assert.AreEqual(10000L, snapshot.HourlyAmounts[10]);
        Assert.AreEqual(SalesReportOrigin.Native, snapshot.FirstPage.Entries.Single().Key.Origin);
        Assert.IsTrue(snapshot.FirstPage.HasMore);
        var second = await fixture.Query.GetPageAsync(snapshot.Token, filter, fixture.Policy, snapshot.FirstPage.NextCursor, 1, CancellationToken.None);
        Assert.AreEqual(SalesReportOrigin.Historical, second.Entries.Single().Key.Origin);
        Assert.AreEqual(1L, second.Entries.Single().Key.InternalId);
        Assert.IsFalse(second.HasMore);
        Assert.IsNull(await fixture.Query.GetEntryAsync(snapshot.Token, filter, fixture.Policy, new SalesReportEntryKey(SalesReportOrigin.Native, 2), CancellationToken.None));
        Assert.IsFalse(snapshot.FirstPage.Entries.Any(row => row.Reference == "PRIVATE-UNATTRIBUTED"));
    }

    [TestMethod]
    public async Task Query_HistoricalOnlyNeedsNoNativePolicy_AndPersonalScopeExcludesUnknownCashier()
    {
        using var fixture = await Fixture.CreateAsync();
        var historical = await fixture.Query.OpenAsync(fixture.Filter(SalesReportOrigins.Historical, nativePolicyHash: ""), null, 200, CancellationToken.None);
        Assert.AreEqual(9000L, historical.Summary.BalanceClp);
        Assert.IsNull(historical.Summary.NativeAverageReceiptClp);
        var personal = await fixture.Query.OpenAsync(fixture.Filter(SalesReportOrigins.All, 7), fixture.Policy, 200, CancellationToken.None);
        Assert.AreEqual(1000L, personal.Summary.BalanceClp);
        Assert.AreEqual(0L, personal.Summary.HistoricalMovementCount);
        Assert.AreEqual(SalesReportCoverageStatus.VerifiedForScope, personal.Coverage.Status);
    }

    [TestMethod]
    public async Task Query_PendingProofOrPolicyCannotMasqueradeAsEmpty_AndRevisionInvalidatesCursor()
    {
        using var fixture = await Fixture.CreateAsync();
        var filter = fixture.Filter(SalesReportOrigins.All);
        var initial = await fixture.Query.OpenAsync(filter, fixture.Policy, 1, CancellationToken.None);
        using (var connection = fixture.Db.Factory.Open())
            await connection.ExecuteAsync("UPDATE sales_report_native_ownership SET state='Dirty' WHERE sale_id=2; UPDATE historical_import_dataset SET data_revision=data_revision+1;");
        await Assert.ThrowsExactlyAsync<ReportSnapshotStaleException>(() => fixture.Query.GetPageAsync(initial.Token, filter, fixture.Policy, initial.FirstPage.NextCursor, 1, CancellationToken.None));
        var pending = await fixture.Query.OpenAsync(filter, fixture.Policy, 200, CancellationToken.None);
        Assert.AreEqual(SalesReportCoverageStatus.PendingOwnershipVerification, pending.Coverage.Status);
        Assert.IsFalse(pending.Coverage.CanPublish);
        Assert.AreEqual(0, pending.FirstPage.Entries.Count);
        Assert.AreEqual(0, pending.Days.Count);
        var policyMissing = await fixture.Query.OpenAsync(fixture.Filter(SalesReportOrigins.All, nativePolicyHash: ""), null, 200, CancellationToken.None);
        Assert.AreEqual(SalesReportCoverageStatus.PolicyRequired, policyMissing.Coverage.Status);
        Assert.IsFalse(policyMissing.Coverage.CanPublish);
    }

    [TestMethod]
    [DataRow(SalesReportOrigins.Native, false, false)]
    [DataRow(SalesReportOrigins.Historical, false, false)]
    [DataRow(SalesReportOrigins.All, false, false)]
    [DataRow(SalesReportOrigins.All, true, false)]
    [DataRow(SalesReportOrigins.All, false, true)]
    [DataRow(SalesReportOrigins.All, true, true)]
    public async Task SummaryAndDays_MatchEntriesAcrossOriginsDatesAndReferenceFilters(
        SalesReportOrigins origins, bool selectedDates, bool referenceFilter)
    {
        using var fixture = await Fixture.CreateAsync();
        using (var connection = fixture.Db.Factory.Open())
        {
            await connection.ExecuteAsync(@"INSERT INTO sales VALUES
(3,'client-3','MATCH-CASH',@utc,0,3000,3500,0,500,7,NULL,NULL),
(4,'client-4','MATCH-RETURN',@utc,3,-500,0,-500,0,7,NULL,1),
(5,'client-5','ANOMALY',@utc+86400000,0,200,0,201,0,7,NULL,NULL);
INSERT INTO sales_report_native_ownership SELECT id,'Verified','shop-a','source-'||id,@policy FROM sales WHERE id>=3;
INSERT INTO historical_transactions VALUES
(2,'batch-b','shop-a','Getnet',@ticks,9007199254740993,NULL,'financial-id','MATCH-LARGE','Transactions',3,1,'raw',-240,'Debit'),
(3,'batch-b','shop-a','Getnet',@ticks+1,-700,NULL,'financial-id','MATCH-CANCEL','Transactions',4,1,'raw',-240,'Debit'),
(4,'batch-c','other-shop','Getnet',@ticks,8888,NULL,'financial-id','MATCH-OTHER','Transactions',5,1,'raw',-240,'Debit'),
(5,'batch-d','shop-a','Getnet',@ticks,7777,NULL,'financial-id','MATCH-INACTIVE','Transactions',6,0,'raw',-240,'Debit');",
                new { utc = new DateTimeOffset(2026, 2, 4, 14, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    ticks = new DateTime(2026, 2, 4, 10, 0, 0).Ticks, policy = NativeSalesOwnershipRepository.PolicyHash });
        }
        var filter = new SalesReportFilter("shop-a", new DateTime(2026, 2, 3), new DateTime(2026, 2, 6),
            origins, true, null, NativeSalesOwnershipRepository.PolicyHash, fixture.Policy.Hash,
            referenceFilter ? "MATCH" : null,
            selectedDates ? new[] { new DateTime(2026, 2, 3), new DateTime(2026, 2, 5) } : null);
        var snapshot = await fixture.Query.OpenAsync(filter, fixture.Policy, 200, CancellationToken.None);
        Assert.IsTrue(snapshot.Coverage.CanPublish);
        Assert.IsFalse(snapshot.FirstPage.HasMore);
        var entries = snapshot.FirstPage.Entries;
        AssertSummaryEqual(entries.Select(SalesReportSummary.FromEntry).Aggregate(SalesReportSummary.Empty, (a, b) => a.Add(b)), snapshot.Summary);
        var groups = entries.GroupBy(e => new DateTime(e.BusinessTicks).Date).OrderBy(g => g.Key).ToArray();
        CollectionAssert.AreEqual(groups.Select(g => g.Key).ToArray(), snapshot.Days.Select(d => d.Date).ToArray());
        foreach (var group in groups)
            AssertSummaryEqual(group.Select(SalesReportSummary.FromEntry).Aggregate(SalesReportSummary.Empty, (a, b) => a.Add(b)),
                snapshot.Days.Single(d => d.Date == group.Key).Summary);
        for (var hour = 0; hour < 24; hour++)
            Assert.AreEqual(entries.Where(e => new DateTime(e.BusinessTicks).Hour == hour).Sum(e => e.SignedAmountClp),
                snapshot.HourlyAmounts[hour], "Hourly rollup must match the independently read signed entries.");
        if (origins == SalesReportOrigins.All && !selectedDates && !referenceFilter)
            Assert.AreEqual(9007199254752993L, snapshot.Summary.BalanceClp);
    }

    [TestMethod]
    public async Task EmptyFilteredReport_HasZeroTotalAndNoArtificialCalendarDay()
    {
        using var fixture = await Fixture.CreateAsync();
        var filter = new SalesReportFilter("shop-a", new DateTime(2026, 2, 3), new DateTime(2026, 2, 4),
            SalesReportOrigins.All, true, null, NativeSalesOwnershipRepository.PolicyHash, fixture.Policy.Hash, "absent-reference");
        var snapshot = await fixture.Query.OpenAsync(filter, fixture.Policy, 200, CancellationToken.None);
        Assert.AreEqual(SalesReportCoverageStatus.VerifiedEmpty, snapshot.Coverage.Status);
        AssertSummaryEqual(SalesReportSummary.Empty, snapshot.Summary);
        Assert.AreEqual(0, snapshot.Days.Count);
        Assert.AreEqual(0, snapshot.FirstPage.Entries.Count);
        Assert.IsTrue(snapshot.HourlyAmounts.All(amount => amount == 0));
    }

    private static void AssertSummaryEqual(SalesReportSummary expected, SalesReportSummary actual)
    {
        static long[] Values(SalesReportSummary s) => new[] { s.GrossPositiveClp, s.ReversalsClp, s.BalanceClp,
            s.CashTenderedClp, s.ChangeClp, s.CashClp, s.CardClp, s.NativePositiveSalesClp, s.NativePositiveReceiptCount,
            s.NativeNegativeMovementCount, s.HistoricalMovementCount, s.HistoricalCancellationCount, s.ReconciliationAnomalyCount };
        CollectionAssert.AreEqual(Values(expected), Values(actual));
        Assert.AreEqual(expected.NativeAverageReceiptClp, actual.NativeAverageReceiptClp);
    }

    private sealed class Fixture : IDisposable
    {
        internal OwnedSqliteTestFile Db { get; private set; } = null!;
        internal SalesReportTimePolicy Policy { get; private set; } = null!;
        internal SalesReportQuery Query { get; private set; } = null!;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Win7POS-report-" + Guid.NewGuid().ToString("N"));
        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(); Directory.CreateDirectory(fixture._directory);
            fixture.Db = OwnedSqliteTestFile.Create(Path.Combine(fixture._directory, "report.db"));
            fixture.Policy = new SalesReportTimePolicy("fixture-shop-time", "v1", "literal synthetic manifest", new[]
            {
                new SalesReportUtcSegment(new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero).ToUnixTimeMilliseconds(), new DateTimeOffset(2027,1,1,0,0,0,TimeSpan.Zero).ToUnixTimeMilliseconds(), -240)
            });
            using var connection = fixture.Db.Factory.Open();
            await connection.ExecuteAsync(@"CREATE TABLE historical_import_dataset(singleton_id INTEGER PRIMARY KEY,dataset_id TEXT,data_revision INTEGER);
INSERT INTO historical_import_dataset VALUES(1,'synthetic-dataset',1);
CREATE TABLE sales(id INTEGER PRIMARY KEY,client_sale_id TEXT,code TEXT,createdAt INTEGER,kind INTEGER,total INTEGER,paidCash INTEGER,paidCard INTEGER,change INTEGER,operator_id INTEGER,voided_by_sale_id INTEGER,related_sale_id INTEGER);
CREATE INDEX fixture_sales_time ON sales(createdAt);
CREATE TABLE sales_report_native_ownership(sale_id INTEGER PRIMARY KEY,state TEXT,shop_id TEXT,source_fingerprint TEXT,policy_hash TEXT);
CREATE TABLE historical_transactions(id INTEGER PRIMARY KEY,batch_id TEXT,shop_id TEXT,provider TEXT,business_ticks INTEGER,signed_amount_clp INTEGER,original_cashier_id INTEGER,external_namespace TEXT,external_id TEXT,source_sheet TEXT,source_row INTEGER,is_active INTEGER,timestamp_raw TEXT,offset_minutes INTEGER,card_subtype TEXT);
INSERT INTO sales VALUES(1,'client-1','POS-1',@utc,0,1000,0,1000,0,7,NULL,NULL);
INSERT INTO sales VALUES(2,'client-2','PRIVATE-UNATTRIBUTED',@utc,0,7000,0,7000,0,8,NULL,NULL);
INSERT INTO sales_report_native_ownership VALUES(1,'Verified','shop-a','source-1',@policy);
INSERT INTO sales_report_native_ownership VALUES(2,'Unknown',NULL,'source-2',@policy);
INSERT INTO historical_transactions VALUES(1,'batch-a','shop-a','Getnet',@ticks,9000,NULL,'financial-id','000123','Transactions',2,1,'raw',-240,'Debit');",
                new { utc = new DateTimeOffset(2026, 2, 3, 14, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), ticks = new DateTime(2026, 2, 3, 10, 0, 0).Ticks, policy = NativeSalesOwnershipRepository.PolicyHash });
            connection.Execute(Win7POS.Data.Import.HistoricalCloudSchema.Sql);
            fixture.Query = new SalesReportQuery(fixture.Db.Factory, () => fixture.Db.Factory.AuthorityStamp, SalesReportSnapshotCoordinator.ForDatabase(fixture.Db.Factory.DbPath));
            return fixture;
        }
        internal SalesReportFilter Filter(SalesReportOrigins origins, int? operatorId = null, string? nativePolicyHash = null) => new("shop-a", new DateTime(2026, 2, 3), new DateTime(2026, 2, 4), origins,
            !operatorId.HasValue, operatorId, NativeSalesOwnershipRepository.PolicyHash, nativePolicyHash ?? Policy.Hash);
        public void Dispose() { Db.Dispose(); Directory.Delete(_directory, true); }
    }
}
