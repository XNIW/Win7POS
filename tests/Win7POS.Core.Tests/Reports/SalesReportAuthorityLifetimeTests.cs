using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Reports;
using Win7POS.Data;
using Win7POS.Data.Backup;
using Win7POS.Data.Online;
using Win7POS.Data.Reports;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Reports;

[TestClass]
public sealed class SalesReportAuthorityLifetimeTests
{
    [TestMethod]
    public async Task EpochChange_InvalidatesCachedTokenWithoutAnotherRead_AndOldObserveCannotReviveIt()
    {
        using var fixture = new Fixture();
        var snapshot = await fixture.OpenAsync();
        Assert.IsTrue(fixture.Coordinator.IsCurrent(snapshot.Token));
        fixture.Factory.RefreshAuthorityStamp();
        Assert.IsTrue(fixture.Coordinator.IsCurrent(snapshot.Token), "Reading the same epoch must preserve the current snapshot.");
        fixture.Owner.SetRestoreEpoch(fixture.Factory.AuthorityStamp.RestoreEpoch + 1);
        RejectWithoutFreshRead(fixture.Coordinator, snapshot.Token);
    }

    [TestMethod]
    public async Task DisposeAndReacquireSameSlot_RejectsOldTokenBeforeAnyNewQuery()
    {
        using var fixture = new Fixture();
        var snapshot = await fixture.OpenAsync();
        var oldFactory = fixture.Factory;
        var fixtureToken = fixture.Owner.FixtureToken;
        fixture.Owner.Dispose();
        RejectWithoutFreshRead(fixture.Coordinator, snapshot.Token);
        fixture.Reacquire(fixtureToken);
        RejectWithoutFreshRead(fixture.Coordinator, snapshot.Token);
        Assert.ThrowsExactly<PosDatabaseAuthorityException>(() => oldFactory.Open());
        var current = await fixture.OpenAsync();
        Assert.AreEqual(snapshot.Token.DatasetId, current.Token.DatasetId);
        Assert.AreEqual(snapshot.Token.DataRevision, current.Token.DataRevision);
        Assert.AreNotEqual(snapshot.Token.AuthorityInstanceEpoch, current.Token.AuthorityInstanceEpoch);
        Assert.IsTrue(fixture.Coordinator.IsCurrent(current.Token));
    }

    [TestMethod]
    public async Task RealRestoreBetweenValidationAndPublication_RejectsOldTokenWithoutFreshOpen()
    {
        using var fixture = new Fixture();
        fixture.Marker("snapshot");
        var snapshotPath = fixture.Path("snapshot.db");
        await new SqliteOnlineBackup(fixture.Factory).CreateVerifiedAsync(snapshotPath);
        fixture.Marker("live-before-restore");
        var preRestorePath = fixture.Path("before.db");
        await new SqliteOnlineBackup(fixture.Factory).CreateVerifiedAsync(preRestorePath);
        var report = await fixture.OpenAsync();
        await fixture.Query.ValidateAsync(report.Token, fixture.Filter, null, CancellationToken.None);
        var postSwapReached = false;
        await new AtomicRestoreInstaller().InstallAsync(snapshotPath, fixture.Factory.DbPath, preRestorePath, () =>
        {
            postSwapReached = true;
            RejectWithoutFreshRead(fixture.Coordinator, report.Token);
            return Task.CompletedTask;
        });
        Assert.IsTrue(postSwapReached);
        // Simulate fresh final authorization: the callback is willing to issue a
        // permit, but the shared boundary must reject the old report first.
        RejectWithoutFreshRead(fixture.Coordinator, report.Token);
        Assert.AreEqual("snapshot", fixture.Marker());
        var current = await fixture.OpenAsync();
        Assert.AreEqual(report.Token.DatasetId, current.Token.DatasetId);
        Assert.AreEqual(report.Token.DataRevision, current.Token.DataRevision);
        Assert.AreNotEqual(report.Token.AuthorityInstanceEpoch, current.Token.AuthorityInstanceEpoch);
        Assert.IsTrue(fixture.Coordinator.IsCurrent(current.Token));
    }

    [TestMethod]
    public async Task AuthorityChange_WaitsForCurrentPublicationThenInvalidatesBeforeNextPublication()
    {
        using var fixture = new Fixture();
        var snapshot = await fixture.OpenAsync();
        using var publication = await fixture.Coordinator.EnterPublicationAsync(CancellationToken.None);
        using var entered = new ManualResetEventSlim();
        var transition = Task.Run(() => { entered.Set(); fixture.Owner.SetRestoreEpoch(snapshot.Token.RestoreEpoch + 1); });
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            Assert.AreNotSame(transition, await Task.WhenAny(transition, Task.Delay(50)), "Authority changed inside the existing publication boundary.");
            Assert.IsTrue(fixture.Coordinator.IsCurrent(snapshot.Token));
        }
        finally { publication.Dispose(); }
        await transition.WaitAsync(TimeSpan.FromSeconds(2));
        RejectWithoutFreshRead(fixture.Coordinator, snapshot.Token);
    }

    [TestMethod]
    public async Task Commit_UsesCombinedRevisionAndNotifiesOnlyAfterReleasingPublication()
    {
        using var fixture = new Fixture();
        var initial = await fixture.OpenAsync();
        var token = initial.Token;
        var expected = new SalesReportReadToken(token.DatasetId, token.RestoreEpoch, token.AuthorityInstanceEpoch,
            token.DataRevision + 8, token.ScopeHash, token.OwnershipPolicyHash, token.NativeTimePolicyHash);
        var notified = 0;
        var released = false;
        EventHandler broken = (_, _) => throw new InvalidOperationException("synthetic_subscriber_failure");
        EventHandler observer = (_, _) =>
        {
            notified++;
            released = fixture.Coordinator.TryUseSnapshot(expected, () => { });
        };
        fixture.Coordinator.SnapshotChanged += broken;
        fixture.Coordinator.SnapshotChanged += observer;
        try
        {
            using var c = fixture.Factory.Open();
            using var t = c.BeginTransaction();
            c.Execute("UPDATE historical_import_dataset SET data_revision=data_revision+3; UPDATE historical_cloud_cache_revision SET revision=revision+5", transaction: t);
            using (await fixture.Coordinator.EnterPublicationAsync(CancellationToken.None))
            {
                fixture.Coordinator.CommitAndPublishReportRevision(c,t,t.Commit,fixture.Factory.AuthorityStamp);
                Assert.AreEqual(0,notified,"Notification ran inside the publication gate.");
                Assert.IsFalse(fixture.Coordinator.IsCurrent(initial.Token));
                Assert.IsTrue(fixture.Coordinator.IsCurrent(expected));
            }
            Assert.AreEqual(1,notified);
            Assert.IsTrue(released,"The observer could not use the committed snapshot after publication.");
            Assert.AreEqual(expected.DataRevision,(await fixture.OpenAsync()).Token.DataRevision);
            using var same = c.BeginTransaction();
            using (await fixture.Coordinator.EnterPublicationAsync(CancellationToken.None))
                fixture.Coordinator.CommitAndPublishReportRevision(c,same,same.Commit,fixture.Factory.AuthorityStamp);
            Assert.AreEqual(1,notified,"An unchanged stamp caused a reload loop.");
        }
        finally
        {
            fixture.Coordinator.SnapshotChanged -= broken;
            fixture.Coordinator.SnapshotChanged -= observer;
        }
    }

    private static void RejectWithoutFreshRead(SalesReportSnapshotCoordinator coordinator, SalesReportReadToken token)
    {
        Assert.IsFalse(coordinator.IsCurrent(token));
        var issued = false;
        Assert.IsFalse(coordinator.TryUseSnapshot(token, () => issued = true));
        Assert.IsFalse(issued, "A fresh permit callback was invoked for an old database authority.");
        Assert.ThrowsExactly<ReportSnapshotStaleException>(() => coordinator.ObserveRead(token));
        Assert.IsFalse(coordinator.IsCurrent(token));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "W7R." + Guid.NewGuid().ToString("N"));
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            var options = PosDbOptions.ForPath(Path("pos.db"));
            Owner = PosDatabaseAuthorityLease.AcquireSyntheticNew(options);
            Factory = new SqliteConnectionFactory(options.WithAccessContext(Owner.AccessContext));
            DbInitializer.EnsureCreated(options.WithAccessContext(Owner.AccessContext));
            Coordinator = SalesReportSnapshotCoordinator.ForDatabase(Factory.DbPath);
            Query = CreateQuery();
        }
        internal PosDatabaseAuthorityLease Owner { get; private set; }
        internal SqliteConnectionFactory Factory { get; private set; }
        internal SalesReportSnapshotCoordinator Coordinator { get; }
        internal SalesReportQuery Query { get; private set; }
        internal SalesReportFilter Filter { get; } = new("synthetic-shop", new DateTime(2000, 1, 2), new DateTime(2000, 1, 3), SalesReportOrigins.Historical, true, null, NativeSalesOwnershipRepository.PolicyHash, "");
        internal string Path(string name) => System.IO.Path.Combine(_root, name);
        internal Task<SalesReportSnapshot> OpenAsync() => Query.OpenAsync(Filter, null, 200, CancellationToken.None);
        private SalesReportQuery CreateQuery() => new(Factory, () => Factory.AuthorityStamp, Coordinator);
        internal void Reacquire(string token)
        {
            var options = PosDbOptions.ForPath(Path("pos.db"));
            Owner = PosDatabaseAuthorityLease.AcquireSyntheticExisting(options, token);
            Factory = new SqliteConnectionFactory(options.WithAccessContext(Owner.AccessContext)); Query = CreateQuery();
        }
        internal void Marker(string value)
        {
            using var connection = Factory.Open();
            connection.Execute("INSERT INTO app_settings(key,value) VALUES('qa.authority.marker',@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", new { value });
        }
        internal string Marker() { using var connection = Factory.Open(); return connection.ExecuteScalar<string>("SELECT value FROM app_settings WHERE key='qa.authority.marker'") ?? throw new InvalidOperationException("qa_authority_marker_missing"); }
        public void Dispose() { SqliteConnectionFactory.ClearAllPools(); Owner.Dispose(); Directory.Delete(_root, true); }
    }
}
