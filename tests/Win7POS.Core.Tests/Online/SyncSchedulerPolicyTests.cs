using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class SyncSchedulerPolicyTests
{
    [TestMethod]
    public void IdleJitter_IsBoundedToTwentyPercent()
    {
        var result = new CatalogSyncRunResult(success: true);

        Assert.AreEqual(24d, CatalogSyncSchedulerPolicy.Evaluate(result, 0, 0).Delay.TotalSeconds);
        Assert.AreEqual(30d, CatalogSyncSchedulerPolicy.Evaluate(result, 0, 0.5).Delay.TotalSeconds);
        Assert.AreEqual(36d, CatalogSyncSchedulerPolicy.Evaluate(result, 0, 1).Delay.TotalSeconds);
    }

    [TestMethod]
    public void RetryBackoff_GrowsCapsAndResetsAfterSuccess()
    {
        var failure = new CatalogSyncRunResult(success: false);
        var expected = new[] { 5d, 15d, 30d, 60d, 120d, 300d, 300d };
        var failureCount = 0;
        foreach (var seconds in expected)
        {
            var decision = CatalogSyncSchedulerPolicy.Evaluate(failure, failureCount, 0.5);
            Assert.AreEqual(seconds, decision.Delay.TotalSeconds);
            failureCount = decision.FailureCount;
        }

        var reset = CatalogSyncSchedulerPolicy.Evaluate(
            new CatalogSyncRunResult(success: true),
            failureCount,
            0.5);
        Assert.AreEqual(0, reset.FailureCount);
        Assert.AreEqual(CatalogSyncScheduleKind.IdleOnline, reset.Kind);
    }

    [TestMethod]
    public void RetryJitter_RemainsBoundedAndNeverExceedsCap()
    {
        var failure = new CatalogSyncRunResult(success: false);
        var low = CatalogSyncSchedulerPolicy.Evaluate(failure, 0, 0);
        var high = CatalogSyncSchedulerPolicy.Evaluate(failure, 0, 1);
        var capped = CatalogSyncSchedulerPolicy.Evaluate(failure, 99, 1);

        Assert.AreEqual(4d, low.Delay.TotalSeconds);
        Assert.AreEqual(6d, high.Delay.TotalSeconds);
        Assert.AreEqual(300d, capped.Delay.TotalSeconds);
    }

    [TestMethod]
    public void HasMoreOrChanges_UsesFastCatchUp()
    {
        foreach (var result in new[]
        {
            new CatalogSyncRunResult(success: true, hasMore: true),
            new CatalogSyncRunResult(success: true, receivedChanges: true)
        })
        {
            var decision = CatalogSyncSchedulerPolicy.Evaluate(result, 4, 0.2);
            Assert.AreEqual(CatalogSyncScheduleKind.FastCatchUp, decision.Kind);
            Assert.AreEqual(5d, decision.Delay.TotalSeconds);
            Assert.AreEqual(0, decision.FailureCount);
        }
    }

    [TestMethod]
    public void AuthDeniedStopsWhileOfflineRetainsQuietPolling()
    {
        var auth = CatalogSyncSchedulerPolicy.Evaluate(
            new CatalogSyncRunResult(false, authenticationDenied: true),
            3,
            0.5);
        var offline = CatalogSyncSchedulerPolicy.Evaluate(
            new CatalogSyncRunResult(false, offline: true),
            3,
            0.5);

        Assert.IsFalse(auth.ShouldPoll);
        Assert.AreEqual(CatalogSyncScheduleKind.AuthenticationStopped, auth.Kind);
        Assert.IsTrue(offline.ShouldPoll);
        Assert.AreEqual(CatalogSyncScheduleKind.OfflineQuiet, offline.Kind);
        Assert.AreEqual(60d, offline.Delay.TotalSeconds);
        Assert.AreEqual(4, offline.FailureCount);
    }

    [TestMethod]
    public void ServerSideRecoveryWithoutNicTransition_ResumesAndResetsBackoff()
    {
        var offline = CatalogSyncSchedulerPolicy.Evaluate(
            new CatalogSyncRunResult(false, offline: true, code: "network_error"),
            0,
            0.5);
        var recovered = CatalogSyncSchedulerPolicy.Evaluate(
            new CatalogSyncRunResult(success: true),
            offline.FailureCount,
            0.5);

        Assert.IsTrue(offline.ShouldPoll);
        Assert.AreEqual(CatalogSyncScheduleKind.OfflineQuiet, offline.Kind);
        Assert.AreEqual(5d, offline.Delay.TotalSeconds);
        Assert.AreEqual(1, offline.FailureCount);
        Assert.IsTrue(recovered.ShouldPoll);
        Assert.AreEqual(CatalogSyncScheduleKind.IdleOnline, recovered.Kind);
        Assert.AreEqual(30d, recovered.Delay.TotalSeconds);
        Assert.AreEqual(0, recovered.FailureCount);
    }

    [TestMethod]
    public async Task TwentyConcurrentTriggers_CoalesceToOneRunAndOneRerun()
    {
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var coordinator = new CatalogSyncCoordinator(
            "shop-scheduler",
            async (_, _) =>
            {
                var current = Interlocked.Increment(ref runs);
                if (current == 1)
                {
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task;
                }

                return new CatalogSyncRunResult(success: true);
            });

        var first = coordinator.TriggerAsync(
            CatalogSyncTrigger.Periodic,
            new CatalogSyncState("cursor"));
        await firstStarted.Task;
        var concurrent = Enumerable.Range(0, 19)
            .Select(_ => coordinator.TriggerAsync(
                CatalogSyncTrigger.Manual,
                new CatalogSyncState("cursor")))
            .ToArray();
        releaseFirst.TrySetResult(true);

        await Task.WhenAll(concurrent.Append(first));
        Assert.AreEqual(2, runs);
    }

    [TestMethod]
    public async Task PartialResult_RerunsImmediatelyFromCheckpoint()
    {
        var requests = new List<CatalogSyncRunRequest>();
        var coordinator = new CatalogSyncCoordinator(
            "shop-resume",
            (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(requests.Count == 1
                    ? new CatalogSyncRunResult(
                        success: true,
                        hasMore: true,
                        resumeCursor: "cursor-partial")
                    : new CatalogSyncRunResult(success: true));
            });

        await coordinator.TriggerAsync(
            CatalogSyncTrigger.StartOfDay,
            new CatalogSyncState("cursor-start"));

        Assert.AreEqual(2, requests.Count);
        Assert.AreEqual(CatalogSyncTrigger.PartialResume, requests[1].Trigger);
        Assert.AreEqual(CatalogSyncMode.ResumeIncremental, requests[1].Decision.Mode);
        Assert.AreEqual("cursor-partial", requests[1].Decision.ResumeCursor);
    }

    [TestMethod]
    public async Task AuthDeniedStopsRunsUntilExplicitRelink()
    {
        var runs = 0;
        var coordinator = new CatalogSyncCoordinator(
            "shop-auth",
            (_, _) =>
            {
                Interlocked.Increment(ref runs);
                return Task.FromResult(new CatalogSyncRunResult(
                    success: false,
                    authenticationDenied: true));
            });

        await coordinator.TriggerAsync(
            CatalogSyncTrigger.Periodic,
            new CatalogSyncState("cursor"));
        var stopped = await coordinator.TriggerAsync(
            CatalogSyncTrigger.Manual,
            new CatalogSyncState("cursor"));

        Assert.AreEqual(1, runs);
        Assert.IsTrue(coordinator.AuthenticationStopped);
        Assert.IsTrue(stopped.AuthenticationDenied);

        coordinator.ResumeAfterRelink();
        await coordinator.TriggerAsync(
            CatalogSyncTrigger.NetworkRecovered,
            new CatalogSyncState("cursor"));
        Assert.AreEqual(2, runs);
    }

    [TestMethod]
    public async Task Diagnostics_OneFullAmongOneHundredOneRunsKeepsRatioAtOrBelowOnePercent()
    {
        using var db = TestDb.Create();
        var diagnostics = new CatalogSyncDiagnosticsRepository(db.Factory);
        var at = DateTimeOffset.Parse("2026-07-16T20:00:00Z");
        for (var index = 0; index < 100; index++)
        {
            var decision = CatalogSyncPolicy.Evaluate(
                CatalogSyncTrigger.Periodic,
                new CatalogSyncState("cursor-" + index));
            await diagnostics.RecordAsync(
                Request(CatalogSyncTrigger.Periodic, decision),
                new CatalogSyncRunResult(true, pages: 1, rows: 10, durationMilliseconds: 5),
                at.AddSeconds(index));
        }

        var fullDecision = CatalogSyncPolicy.Evaluate(
            CatalogSyncTrigger.AdministratorRepair,
            new CatalogSyncState(
                "cursor",
                administratorRepairAuthorized: true));
        await diagnostics.RecordAsync(
            Request(CatalogSyncTrigger.AdministratorRepair, fullDecision),
            new CatalogSyncRunResult(true, pages: 20, rows: 1000, durationMilliseconds: 200),
            at.AddMinutes(2));

        using var verify = db.Factory.Open();
        var ratio = await verify.ExecuteScalarAsync<decimal>(
            "SELECT CAST(value AS REAL) FROM app_settings WHERE key = @key;",
            new { key = CatalogSyncDiagnosticsRepository.Prefix + "full_ratio_percent" });
        Assert.IsTrue(ratio <= 1m, ratio.ToString());
        Assert.AreEqual(100L, await ReadLongAsync(verify, "total_incremental_runs"));
        Assert.AreEqual(1L, await ReadLongAsync(verify, "total_full_runs"));
    }

    private static CatalogSyncRunRequest Request(
        CatalogSyncTrigger trigger,
        CatalogSyncDecision decision)
    {
        return new CatalogSyncRunRequest("SHOP-DIAGNOSTICS", trigger, decision);
    }

    private static Task<long> ReadLongAsync(Microsoft.Data.Sqlite.SqliteConnection conn, string suffix)
    {
        return conn.ExecuteScalarAsync<long>(
            "SELECT CAST(value AS INTEGER) FROM app_settings WHERE key = @key;",
            new { key = CatalogSyncDiagnosticsRepository.Prefix + suffix });
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
            var root = Path.Combine(Path.GetTempPath(), "Win7POS.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestDb(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
