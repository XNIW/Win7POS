using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class OutboxDrainResultTests
{
    [TestMethod]
    public void Result_ExposesImmutablePostRunState()
    {
        var result = new OutboxDrainResult(
            attempted: 25,
            acked: 20,
            retried: 3,
            blocked: 2,
            remainingDue: 35,
            nextRetryAt: 123456,
            failureKind: SyncFailureKind.RetryableRemote,
            diagnosticCode: "server_busy");

        Assert.AreEqual(25, result.Attempted);
        Assert.AreEqual(20, result.Acked);
        Assert.AreEqual(3, result.Retried);
        Assert.AreEqual(2, result.Blocked);
        Assert.AreEqual(35L, result.RemainingDue);
        Assert.AreEqual(123456L, result.NextRetryAt);
        Assert.IsTrue(result.HasImmediateMore);
        Assert.AreEqual("server_busy", result.DiagnosticCode);
    }

    [TestMethod]
    public void Result_RejectsImpossibleTransitionCounts()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new OutboxDrainResult(
            attempted: 1,
            acked: 1,
            retried: 1,
            blocked: 0,
            remainingDue: 0));
    }

    [TestMethod]
    public void AuthenticationDenied_SuppressesImmediateMore()
    {
        var result = OutboxDrainResult.Empty(
            remainingDue: 9,
            failureKind: SyncFailureKind.AuthenticationDenied,
            diagnosticCode: "authorization_denied");

        Assert.IsTrue(result.AuthenticationDenied);
        Assert.IsFalse(result.HasImmediateMore);
    }

    [TestMethod]
    public void DiagnosticCode_IsRedactedWhenUnsafe()
    {
        var result = OutboxDrainResult.Empty(
            failureKind: SyncFailureKind.Unexpected,
            diagnosticCode: "token=super-secret");

        Assert.AreEqual("outbox_failure", result.DiagnosticCode);
    }
}
