using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class CatalogHeartbeatPolicyTests
{
    [TestMethod]
    public void MatchingCommittedRevisionAndExplicitFalse_CanSkip()
    {
        var decision = Evaluate(" revision-7 ", false, "revision-7");

        Assert.IsTrue(decision.ShouldSkipCatalogPull);
        Assert.AreEqual("revision-7", decision.ObservedRevision);
        Assert.AreEqual("catalog_unchanged_at_committed_revision", decision.Code);
    }

    [TestMethod]
    public void MismatchOverridesFalseHint()
    {
        var decision = Evaluate("revision-8", false, "revision-7");

        Assert.IsFalse(decision.ShouldSkipCatalogPull);
        Assert.AreEqual("catalog_revision_mismatch", decision.Code);
    }

    [TestMethod]
    public void MissingTrueAndMalformedHintsAlwaysPull()
    {
        Assert.IsFalse(Evaluate(null, null, "revision-7").ShouldSkipCatalogPull);
        Assert.IsFalse(Evaluate("revision-7", true, "revision-7").ShouldSkipCatalogPull);
        Assert.IsFalse(Evaluate("bad\u0001revision", false, "bad\u0001revision").ShouldSkipCatalogPull);
        Assert.IsFalse(Evaluate("bad\uD800revision", false, "bad\uD800revision").ShouldSkipCatalogPull);
        Assert.IsFalse(Evaluate("bad\uDC00revision", false, "bad\uDC00revision").ShouldSkipCatalogPull);
        Assert.IsFalse(Evaluate(new string('a', 129), false, new string('a', 129)).ShouldSkipCatalogPull);
    }

    [TestMethod]
    public void FullPartialManualAndImportAckOverrideMatchingFalseHint()
    {
        Assert.IsFalse(Evaluate("r", false, "r", full: true).ShouldSkipCatalogPull);
        Assert.IsFalse(Evaluate("r", false, "r", partial: true).ShouldSkipCatalogPull);
        Assert.IsFalse(Evaluate("r", false, "r", manual: true).ShouldSkipCatalogPull);
        Assert.IsFalse(Evaluate("r", false, "r", importAck: true).ShouldSkipCatalogPull);
    }

    [TestMethod]
    public void PollHint_UsesFallbackForNonPositiveAndClampsToFiveThroughThreeHundred()
    {
        Assert.IsNull(CatalogHeartbeatPolicy.NormalizePollSeconds(null));
        Assert.IsNull(CatalogHeartbeatPolicy.NormalizePollSeconds(-1));
        Assert.IsNull(CatalogHeartbeatPolicy.NormalizePollSeconds(0));
        Assert.AreEqual(5, CatalogHeartbeatPolicy.NormalizePollSeconds(1));
        Assert.AreEqual(5, CatalogHeartbeatPolicy.NormalizePollSeconds(5));
        Assert.AreEqual(300, CatalogHeartbeatPolicy.NormalizePollSeconds(300));
        Assert.AreEqual(300, CatalogHeartbeatPolicy.NormalizePollSeconds(301));
        Assert.AreEqual(300, CatalogHeartbeatPolicy.NormalizePollSeconds(int.MaxValue));
    }

    private static CatalogHeartbeatDecision Evaluate(
        string? observed,
        bool? changes,
        string? committed,
        bool full = false,
        bool partial = false,
        bool manual = false,
        bool importAck = false)
    {
        return CatalogHeartbeatPolicy.Evaluate(
            observed,
            changes,
            30,
            committed,
            full,
            partial,
            manual,
            importAck);
    }
}
