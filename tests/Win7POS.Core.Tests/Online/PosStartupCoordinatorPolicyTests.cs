using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Core.Security;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class PosStartupCoordinatorPolicyTests
{
    [TestMethod]
    public void VerifiedEmpty_AdmitsAuthenticatedShellWithoutSaleOrHistoryPermission()
    {
        var decision = PosStartupCoordinatorPolicy.EvaluateAdmission(PosAuthenticatedAccessMode.Normal,
            isLoggedIn: true, authorizationAllowed: true, catalogSaleSafe: false, catalogAuthoritativelyEmpty: true);
        Assert.AreEqual(PosShellReadiness.AuthenticatedEmpty, decision.Readiness);
        Assert.AreEqual(PosShellMode.Pos, decision.ShellMode);
        Assert.IsTrue(decision.CanUsePosWorkspace);
        Assert.IsTrue(decision.CanEnterShell);
        Assert.IsTrue(decision.CanUseNormalNavigation);
        Assert.IsFalse(decision.CanSell);
    }

    [TestMethod]
    public void SaleReady_StillRequiresValidNormalAuthorityAndExplicitSaleSafety()
    {
        var decision = PosStartupCoordinatorPolicy.EvaluateAdmission(PosAuthenticatedAccessMode.Normal, true, true, true, false);
        Assert.AreEqual(PosShellReadiness.SaleReady, decision.Readiness);
        Assert.AreEqual(PosShellMode.Pos, decision.ShellMode);
        Assert.IsTrue(decision.CanSell);
        Assert.IsTrue(decision.CanUseNormalNavigation);
    }

    [TestMethod]
    [DataRow(false, true, false, true)]
    [DataRow(true, false, false, true)]
    [DataRow(false, true, true, false)]
    [DataRow(true, false, true, false)]
    public void MissingOrExpiredAuthority_DoesNotAdmitShell(bool loggedIn, bool authorized, bool saleSafe, bool empty)
    {
        var decision = PosStartupCoordinatorPolicy.EvaluateAdmission(PosAuthenticatedAccessMode.Normal, loggedIn, authorized, saleSafe, empty);
        Assert.AreEqual(PosShellReadiness.Blocked, decision.Readiness);
        Assert.IsFalse(decision.CanEnterShell);
        Assert.IsFalse(decision.CanUseNormalNavigation);
        Assert.IsFalse(decision.CanSell);
    }

    [TestMethod]
    [DataRow(true, true, false, false)]
    [DataRow(true, false, true, false)]
    [DataRow(false, true, false, true)]
    public void PartialConflictingOrRecoveryState_DoesNotBecomeNormalShell(bool saleSafe, bool empty, bool safeStart, bool recovery)
    {
        var decision = PosStartupCoordinatorPolicy.EvaluateAdmission(PosAuthenticatedAccessMode.Normal, true, true, saleSafe, empty, safeStart, recovery);
        Assert.AreEqual(PosShellReadiness.Recovery, decision.Readiness);
        Assert.IsFalse(decision.CanUseNormalNavigation);
        Assert.IsFalse(decision.CanSell);
    }

    [TestMethod]
    public void LocalRecovery_RemainsRestrictedAndDoesNotBorrowNormalShellAuthority()
    {
        var decision = PosStartupCoordinatorPolicy.EvaluateAdmission(PosAuthenticatedAccessMode.LocalRecovery, true, false, true, false);
        Assert.AreEqual(PosShellReadiness.Recovery, decision.Readiness);
        Assert.IsFalse(decision.CanUseNormalNavigation);
        Assert.IsFalse(decision.CanSell);
        Assert.IsFalse(PosStartupCoordinatorPolicy.EvaluateAdmission((PosAuthenticatedAccessMode)99, true, true, true, false).CanEnterShell);
    }

    [TestMethod]
    public void ShellMode_RequiresNormalAccessWithoutWaitingForCatalog()
    {
        Assert.AreEqual(
            PosShellMode.Pos,
            PosStartupCoordinatorPolicy.DetermineShellMode(
                PosAuthenticatedAccessMode.Normal,
                catalogSaleSafe: true));
        Assert.AreEqual(
            PosShellMode.Pos,
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
    public void FirstDownloadPending_AdmitsRealWorkspaceWithoutRemoteSaleAuthority()
    {
        var decision = PosStartupCoordinatorPolicy.EvaluateAdmission(PosAuthenticatedAccessMode.Normal, true, true, false, false);
        Assert.AreEqual(PosShellReadiness.AuthenticatedPending, decision.Readiness);
        Assert.IsTrue(decision.CanUsePosWorkspace);
        Assert.IsFalse(decision.CanSell);
    }

    [TestMethod]
    [DataRow(CatalogSaleSafetyPolicy.NotSaleSafeCode,true)]
    [DataRow(CatalogSaleSafetyPolicy.ExactnessMissingCode,true)]
    [DataRow(PosCatalogOperationPolicy.FullRefreshRequiredAfterEmptyCode,true)]
    [DataRow(CatalogSaleSafetyPolicy.ShopMismatchCode,false)]
    [DataRow(CatalogSaleSafetyPolicy.RepairRequiredCode,false)]
    [DataRow(CatalogSaleSafetyPolicy.BindingPartialCode,false)]
    [DataRow(CatalogSaleSafetyPolicy.ExactnessNotVerifiedCode,false)]
    public void VerifiedLocalOperations_NeverBorrowIdentityOrRepairAuthority(string reason,bool expected)
    {
        Assert.AreEqual(expected,PosCatalogOperationPolicy.AllowsVerifiedLocalLine(reason));
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
