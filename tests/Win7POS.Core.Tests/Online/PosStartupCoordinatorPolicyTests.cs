using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Core.Security;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class PosStartupCoordinatorPolicyTests
{
    [TestMethod]
    public void ShellMode_RequiresNormalAccessAndSaleSafeCatalogForPos()
    {
        Assert.AreEqual(
            PosShellMode.Pos,
            PosStartupCoordinatorPolicy.DetermineShellMode(
                PosAuthenticatedAccessMode.Normal,
                catalogSaleSafe: true));
        Assert.AreEqual(
            PosShellMode.Recovery,
            PosStartupCoordinatorPolicy.DetermineShellMode(
                PosAuthenticatedAccessMode.Normal,
                catalogSaleSafe: false));
        Assert.AreEqual(
            PosShellMode.Recovery,
            PosStartupCoordinatorPolicy.DetermineShellMode(
                PosAuthenticatedAccessMode.LocalRecovery,
                catalogSaleSafe: true));
    }

    [TestMethod]
    public void BackgroundStart_IsBlockedBySafeStartAndRecovery()
    {
        Assert.IsTrue(PosStartupCoordinatorPolicy.CanStartBackground(false, false));
        Assert.IsFalse(PosStartupCoordinatorPolicy.CanStartBackground(true, false));
        Assert.IsFalse(PosStartupCoordinatorPolicy.CanStartBackground(false, true));
    }

    [TestMethod]
    public void MaintenanceResume_RequiresExistingRequestAndNormalLiveState()
    {
        Assert.IsTrue(PosStartupCoordinatorPolicy.CanResumeAfterMaintenance(
            isSafeStart: false,
            isRecoveryMode: false,
            accessMode: PosAuthenticatedAccessMode.Normal,
            resumeRequested: true));
        Assert.IsFalse(PosStartupCoordinatorPolicy.CanResumeAfterMaintenance(
            isSafeStart: true,
            isRecoveryMode: false,
            accessMode: PosAuthenticatedAccessMode.Normal,
            resumeRequested: true));
        Assert.IsFalse(PosStartupCoordinatorPolicy.CanResumeAfterMaintenance(
            isSafeStart: false,
            isRecoveryMode: true,
            accessMode: PosAuthenticatedAccessMode.Normal,
            resumeRequested: true));
        Assert.IsFalse(PosStartupCoordinatorPolicy.CanResumeAfterMaintenance(
            isSafeStart: false,
            isRecoveryMode: false,
            accessMode: PosAuthenticatedAccessMode.LocalRecovery,
            resumeRequested: true));
        Assert.IsFalse(PosStartupCoordinatorPolicy.CanResumeAfterMaintenance(
            isSafeStart: false,
            isRecoveryMode: false,
            accessMode: PosAuthenticatedAccessMode.Normal,
            resumeRequested: false));
    }

    [TestMethod]
    public void RecoveryExit_RequiresNormalLoggedInAuthorizedSession()
    {
        Assert.IsTrue(PosStartupCoordinatorPolicy.CanCompleteRecoveryExit(
            PosAuthenticatedAccessMode.Normal,
            isLoggedIn: true,
            authorizationAllowed: true));
        Assert.IsFalse(PosStartupCoordinatorPolicy.CanCompleteRecoveryExit(
            PosAuthenticatedAccessMode.LocalRecovery,
            isLoggedIn: true,
            authorizationAllowed: true));
        Assert.IsFalse(PosStartupCoordinatorPolicy.CanCompleteRecoveryExit(
            PosAuthenticatedAccessMode.Normal,
            isLoggedIn: false,
            authorizationAllowed: true));
        Assert.IsFalse(PosStartupCoordinatorPolicy.CanCompleteRecoveryExit(
            PosAuthenticatedAccessMode.Normal,
            isLoggedIn: true,
            authorizationAllowed: false));
    }
}
