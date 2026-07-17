using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Pos;

namespace Win7POS.Core.Tests.Pos;

[TestClass]
public sealed class MainShellClosePolicyTests
{
    [TestMethod]
    public void IdleClose_RequiresConfirmation() =>
        Assert.AreEqual(MainShellCloseDecision.RequireConfirmation, MainShellClosePolicy.Decide(MainShellCloseState.Idle));

    [TestMethod]
    public void CartNotEmpty_RequiresCartWarning() =>
        Assert.AreEqual(MainShellCloseDecision.RequireCartWarning, MainShellClosePolicy.Decide(MainShellCloseState.CartNotEmpty));

    [TestMethod]
    [DataRow(MainShellCloseState.PaymentInProgress)]
    [DataRow(MainShellCloseState.CriticalDatabaseOperation)]
    [DataRow(MainShellCloseState.FullCatalogRepairInProgress)]
    public void CriticalState_Blocks(MainShellCloseState state) =>
        Assert.AreEqual(MainShellCloseDecision.BlockUntilOperationCompletes, MainShellClosePolicy.Decide(state));

    [TestMethod]
    public void IncrementalSync_AllowsCloseAfterConfirmation() =>
        Assert.AreEqual(MainShellCloseDecision.RequireConfirmation, MainShellClosePolicy.Decide(MainShellCloseState.IncrementalSyncInProgress));

    [TestMethod]
    public void ProgrammaticClose_BypassesPrompt() =>
        Assert.AreEqual(MainShellCloseDecision.Allow, MainShellClosePolicy.Decide(MainShellCloseState.ProgrammaticClose));

    [TestMethod]
    public void SessionEnding_BypassesForSystemShutdown() =>
        Assert.AreEqual(MainShellCloseDecision.BypassForSystemShutdown, MainShellClosePolicy.Decide(MainShellCloseState.SessionEnding | MainShellCloseState.CartNotEmpty));

    [TestMethod]
    public void Minimize_NeverPrompts() =>
        Assert.AreEqual(MainShellCloseDecision.Allow, MainShellClosePolicy.Decide(MainShellCloseState.CartNotEmpty, MainShellCloseIntent.Minimize));
}
