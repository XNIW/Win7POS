using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class StartOfDaySalesDrainPolicyTests
{
    [TestMethod]
    [DataRow(0, StartOfDaySalesDrainDecision.Complete)]
    [DataRow(25, StartOfDaySalesDrainDecision.ContinueBackground)]
    [DataRow(26, StartOfDaySalesDrainDecision.ContinueBackground)]
    [DataRow(60, StartOfDaySalesDrainDecision.ContinueBackground)]
    public void PendingSales_UseRealBacklogState(
        int pendingSales,
        StartOfDaySalesDrainDecision expected)
    {
        Assert.AreEqual(
            expected,
            StartOfDaySalesDrainPolicy.Evaluate(pendingSales, 0, 0, 0));
    }

    [TestMethod]
    public void RetrySales_ContinueInBackgroundWithoutBlockingPos()
    {
        Assert.AreEqual(
            StartOfDaySalesDrainDecision.ContinueBackground,
            StartOfDaySalesDrainPolicy.Evaluate(0, 1, 0, 0));
    }

    [TestMethod]
    public void ActiveInProgressSales_RemainUnresolved()
    {
        Assert.AreEqual(
            StartOfDaySalesDrainDecision.ContinueBackground,
            StartOfDaySalesDrainPolicy.Evaluate(0, 0, 1, 0));
    }

    [TestMethod]
    public void StaleInProgressSales_RemainUnresolvedUntilReclaimed()
    {
        Assert.AreEqual(
            StartOfDaySalesDrainDecision.ContinueBackground,
            StartOfDaySalesDrainPolicy.Evaluate(0, 0, 1, 0));
    }

    [TestMethod]
    public void BlockedSales_TakePriorityOverOrdinaryBacklog()
    {
        Assert.AreEqual(
            StartOfDaySalesDrainDecision.Blocked,
            StartOfDaySalesDrainPolicy.Evaluate(60, 1, 1, 1));
    }

    [TestMethod]
    public void SixtyPendingSales_DrainAcrossThreeBoundedRuns()
    {
        const int salesPerRun = 25;
        var pendingSales = 60;
        var remainingAfterRuns = new List<int>();

        while (pendingSales > 0)
        {
            Assert.AreEqual(
                StartOfDaySalesDrainDecision.ContinueBackground,
                StartOfDaySalesDrainPolicy.Evaluate(pendingSales, 0, 0, 0));
            pendingSales = Math.Max(0, pendingSales - salesPerRun);
            remainingAfterRuns.Add(pendingSales);
        }

        CollectionAssert.AreEqual(new[] { 35, 10, 0 }, remainingAfterRuns);
        Assert.AreEqual(
            StartOfDaySalesDrainDecision.Complete,
            StartOfDaySalesDrainPolicy.Evaluate(pendingSales, 0, 0, 0));
    }

    [TestMethod]
    public void IdleSchedulerPolling_RemainsBetweenTwentyFourAndThirtySixSeconds()
    {
        var successfulIdleRun = new CatalogSyncRunResult(success: true);
        var lowerBound = CatalogSyncSchedulerPolicy.Evaluate(successfulIdleRun, 0, 0);
        var upperBound = CatalogSyncSchedulerPolicy.Evaluate(successfulIdleRun, 0, 1);

        Assert.IsTrue(lowerBound.ShouldPoll);
        Assert.IsTrue(upperBound.ShouldPoll);
        Assert.AreEqual(CatalogSyncScheduleKind.IdleOnline, lowerBound.Kind);
        Assert.AreEqual(CatalogSyncScheduleKind.IdleOnline, upperBound.Kind);
        Assert.AreEqual(24d, lowerBound.Delay.TotalSeconds);
        Assert.AreEqual(36d, upperBound.Delay.TotalSeconds);
    }

    [TestMethod]
    public void HealthyPeriodicPolling_NeverSelectsFullCatalog()
    {
        var decision = CatalogSyncPolicy.Evaluate(
            CatalogSyncTrigger.Periodic,
            new CatalogSyncState(persistedCursor: "cursor-periodic"));

        Assert.AreEqual(CatalogSyncMode.Incremental, decision.Mode);
        Assert.AreEqual(CatalogFullSyncReason.None, decision.FullReason);
    }
}
