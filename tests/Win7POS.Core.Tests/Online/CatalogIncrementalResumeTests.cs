using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class CatalogIncrementalResumeTests
{
    [TestMethod]
    public void RestartWithValidCheckpoint_ResumesWithoutFull()
    {
        var persistedAfterRestart = new CatalogSyncState(
            persistedCursor: "opaque-checkpoint",
            bootstrapCompleted: true,
            hasShopBinding: true,
            hasPartialCheckpoint: true);

        var decision = CatalogSyncPolicy.Evaluate(
            CatalogSyncTrigger.PartialResume,
            persistedAfterRestart);

        Assert.AreEqual(CatalogSyncMode.ResumeIncremental, decision.Mode);
        Assert.AreEqual(CatalogFullSyncReason.None, decision.FullReason);
        Assert.AreEqual("opaque-checkpoint", decision.ResumeCursor);
        Assert.IsTrue(decision.PreserveExistingSaleSafe);
    }

    [TestMethod]
    public void ImportAckReconnectAndManualRemainIncremental()
    {
        foreach (var trigger in new[]
        {
            CatalogSyncTrigger.CatalogImportAcked,
            CatalogSyncTrigger.NetworkRecovered,
            CatalogSyncTrigger.Manual
        })
        {
            var decision = CatalogSyncPolicy.Evaluate(
                trigger,
                new CatalogSyncState("cursor-current"));
            Assert.AreEqual(CatalogSyncMode.Incremental, decision.Mode, trigger.ToString());
            Assert.AreEqual(CatalogFullSyncReason.None, decision.FullReason, trigger.ToString());
        }
    }
}
