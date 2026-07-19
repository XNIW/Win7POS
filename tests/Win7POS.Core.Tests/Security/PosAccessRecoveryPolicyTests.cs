using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Security;

namespace Win7POS.Core.Tests.Security;

[TestClass]
public sealed class PosAccessRecoveryPolicyTests
{
    [TestMethod]
    [DataRow(PosAccessFailureKind.NetworkUnavailable)]
    [DataRow(PosAccessFailureKind.ServerNotConfigured)]
    [DataRow(PosAccessFailureKind.Timeout)]
    [DataRow(PosAccessFailureKind.ServerUnavailable)]
    public void EmptyDatabase_TransientFailure_OffersExplicitLocalRecovery(PosAccessFailureKind failure)
    {
        var decision = PosAccessRecoveryPolicy.Evaluate(EmptyState(), failure);

        Assert.AreEqual(PosAccessNextStep.OfferLocalRecovery, decision.NextStep);
        Assert.IsTrue(decision.CanCreateLocalAdmin);
        Assert.IsFalse(decision.CanUseOfflineMirror);
    }

    [TestMethod]
    [DataRow("invalid_credentials", false)]
    [DataRow("http_401", false)]
    [DataRow("http_403", false)]
    [DataRow("anything", true)]
    public void EmptyDatabase_AuthenticationDenied_NeverOffersRecovery(string code, bool denied)
    {
        var failure = PosAccessRecoveryPolicy.ClassifyOnlineFailure(code, denied);
        var decision = PosAccessRecoveryPolicy.Evaluate(EmptyState(), failure);

        Assert.AreEqual(PosAccessNextStep.Denied, decision.NextStep);
        Assert.IsFalse(decision.CanCreateLocalAdmin);
    }

    [TestMethod]
    [DataRow("device_denied", PosAccessFailureKind.DeviceDenied)]
    [DataRow("policy_denied", PosAccessFailureKind.PolicyDenied)]
    [DataRow("contract_invalid", PosAccessFailureKind.InvalidContract)]
    [DataRow("invalid_response", PosAccessFailureKind.InvalidResponse)]
    public void EmptyDatabase_ValidatedSecurityFailure_NeverOffersRecovery(
        string code,
        PosAccessFailureKind expectedFailure)
    {
        var failure = PosAccessRecoveryPolicy.ClassifyOnlineFailure(code, false);
        var decision = PosAccessRecoveryPolicy.Evaluate(EmptyState(), failure);

        Assert.AreEqual(expectedFailure, failure);
        Assert.AreEqual(PosAccessNextStep.Denied, decision.NextStep);
        Assert.IsFalse(decision.CanCreateLocalAdmin);
    }

    [TestMethod]
    public void ActiveRemoteMirror_NetworkUnavailable_UsesOfflineLoginWithoutAdminCreation()
    {
        var state = new PosUserBootstrapState
        {
            TotalUserRows = 1,
            ActiveLoginableUsers = 1,
            ActiveRemoteMirrors = 1
        };

        var decision = PosAccessRecoveryPolicy.Evaluate(state, PosAccessFailureKind.NetworkUnavailable);

        Assert.AreEqual(PosAccessNextStep.OfflineMirrorLogin, decision.NextStep);
        Assert.IsTrue(decision.CanUseOfflineMirror);
        Assert.IsFalse(decision.CanCreateLocalAdmin);
    }

    [TestMethod]
    public void ExistingDisabledUsers_AreNotTreatedAsFreshInstall()
    {
        var state = new PosUserBootstrapState
        {
            TotalUserRows = 2,
            ActiveLoginableUsers = 0,
            ActiveRemoteMirrors = 0
        };

        var decision = PosAccessRecoveryPolicy.Evaluate(state, PosAccessFailureKind.NetworkUnavailable);

        Assert.AreEqual(PosAccessNextStep.ExistingUsersDisabled, decision.NextStep);
        Assert.IsFalse(decision.CanCreateLocalAdmin);
    }

    [TestMethod]
    public void ExistingLocalRecoveryUser_UsesUnifiedLocalLogin()
    {
        var state = new PosUserBootstrapState
        {
            TotalUserRows = 1,
            ActiveLoginableUsers = 1,
            ActiveRemoteMirrors = 0,
            ActiveLocalRecoveryUsers = 1
        };

        var decision = PosAccessRecoveryPolicy.Evaluate(state, PosAccessFailureKind.None);

        Assert.AreEqual(PosAccessNextStep.LocalRecoveryLogin, decision.NextStep);
        Assert.IsTrue(decision.CanUseLocalRecoveryLogin);
        Assert.IsFalse(decision.CanCreateLocalAdmin);
    }

    [TestMethod]
    public void ExistingLocalRecoveryUser_AuthenticationDenied_KeepsSeparateLocalLoginAvailable()
    {
        var state = new PosUserBootstrapState
        {
            TotalUserRows = 1,
            ActiveLoginableUsers = 1,
            ActiveRemoteMirrors = 0,
            ActiveLocalRecoveryUsers = 1
        };

        var decision = PosAccessRecoveryPolicy.Evaluate(state, PosAccessFailureKind.AuthenticationDenied);

        Assert.AreEqual(PosAccessNextStep.LocalRecoveryLogin, decision.NextStep);
        Assert.IsTrue(decision.CanUseLocalRecoveryLogin);
        Assert.IsFalse(decision.CanUseOfflineMirror);
        Assert.IsFalse(decision.CanCreateLocalAdmin);
    }

    [TestMethod]
    public void RemoteMirrorOnly_AuthenticationDenied_RemainsDenied()
    {
        var state = new PosUserBootstrapState
        {
            TotalUserRows = 1,
            ActiveLoginableUsers = 1,
            ActiveRemoteMirrors = 1
        };

        var decision = PosAccessRecoveryPolicy.Evaluate(state, PosAccessFailureKind.AuthenticationDenied);

        Assert.AreEqual(PosAccessNextStep.Denied, decision.NextStep);
        Assert.IsFalse(decision.CanUseLocalRecoveryLogin);
    }

    [TestMethod]
    public void UnclassifiedActiveUser_AuthenticationDenied_DoesNotBecomeLocalRecovery()
    {
        var state = new PosUserBootstrapState
        {
            TotalUserRows = 1,
            ActiveLoginableUsers = 1,
            ActiveRemoteMirrors = 0,
            ActiveLocalRecoveryUsers = 0
        };

        var decision = PosAccessRecoveryPolicy.Evaluate(state, PosAccessFailureKind.AuthenticationDenied);

        Assert.AreEqual(PosAccessNextStep.Denied, decision.NextStep);
        Assert.IsFalse(decision.CanUseLocalRecoveryLogin);
    }

    [TestMethod]
    public void MixedRemoteAndLocalUsers_AuthenticationDenied_OffersOnlySeparateLocalLogin()
    {
        var state = new PosUserBootstrapState
        {
            TotalUserRows = 2,
            ActiveLoginableUsers = 2,
            ActiveRemoteMirrors = 1,
            ActiveLocalRecoveryUsers = 1
        };

        var decision = PosAccessRecoveryPolicy.Evaluate(state, PosAccessFailureKind.AuthenticationDenied);

        Assert.AreEqual(PosAccessNextStep.LocalRecoveryLogin, decision.NextStep);
        Assert.IsTrue(decision.CanUseLocalRecoveryLogin);
        Assert.IsFalse(decision.CanUseOfflineMirror);
        Assert.IsFalse(decision.CanCreateLocalAdmin);
    }

    [TestMethod]
    [DataRow(PosAccessFailureKind.DeviceDenied)]
    [DataRow(PosAccessFailureKind.PolicyDenied)]
    [DataRow(PosAccessFailureKind.InvalidContract)]
    [DataRow(PosAccessFailureKind.InvalidResponse)]
    public void ExistingLocalRecoveryUser_NonCredentialSecurityDenial_RemainsDenied(
        PosAccessFailureKind failure)
    {
        var state = new PosUserBootstrapState
        {
            TotalUserRows = 1,
            ActiveLoginableUsers = 1,
            ActiveRemoteMirrors = 0,
            ActiveLocalRecoveryUsers = 1
        };

        var decision = PosAccessRecoveryPolicy.Evaluate(state, failure);

        Assert.AreEqual(PosAccessNextStep.Denied, decision.NextStep);
        Assert.IsFalse(decision.CanUseLocalRecoveryLogin);
    }

    [TestMethod]
    public void LocalRecoveryWithUnsafeCatalog_UsesRecoveryShell()
    {
        Assert.AreEqual(
            PosShellMode.Recovery,
            PosShellStartupPolicy.Determine(PosAuthenticatedAccessMode.LocalRecovery, catalogSaleSafe: false));
    }

    [TestMethod]
    public void SaleSafeCatalog_DoesNotElevateLocalRecoveryToPosShell()
    {
        Assert.AreEqual(
            PosShellMode.Recovery,
            PosShellStartupPolicy.Determine(PosAuthenticatedAccessMode.LocalRecovery, catalogSaleSafe: true));
    }

    [TestMethod]
    public void NormalAuthorizedAccessWithSaleSafeCatalog_UsesPosShell()
    {
        Assert.AreEqual(
            PosShellMode.Pos,
            PosShellStartupPolicy.Determine(PosAuthenticatedAccessMode.Normal, catalogSaleSafe: true));
    }

    [TestMethod]
    public void NormalAuthorizedAccessWithUnsafeCatalog_UsesRecoveryShell()
    {
        Assert.AreEqual(
            PosShellMode.Recovery,
            PosShellStartupPolicy.Determine(PosAuthenticatedAccessMode.Normal, catalogSaleSafe: false));
    }

    [TestMethod]
    [DataRow(PermissionCodes.CatalogView, true)]
    [DataRow(PermissionCodes.CatalogEdit, true)]
    [DataRow(PermissionCodes.CatalogImport, true)]
    [DataRow(PermissionCodes.CatalogPriceEdit, true)]
    [DataRow(PermissionCodes.DbMaintenance, true)]
    [DataRow(PermissionCodes.DbBackup, true)]
    [DataRow(PermissionCodes.DbRestore, true)]
    [DataRow(PermissionCodes.PosSell, false)]
    [DataRow(PermissionCodes.DailyCloseRun, false)]
    [DataRow(PermissionCodes.SettingsPrinter, false)]
    [DataRow(PermissionCodes.UsersManage, false)]
    [DataRow(PermissionCodes.SecurityOverride, false)]
    public void LocalRecoveryPermissionPolicy_AdminRemainsInsideExplicitAllowlist(
        string permissionCode,
        bool expected)
    {
        var admin = new UserAccount
        {
            IsActive = true,
            RoleCode = "admin"
        };

        Assert.AreEqual(expected, LocalRecoveryPermissionPolicy.IsGranted(admin, permissionCode));
    }

    [TestMethod]
    public void LocalRecoveryPermissionPolicy_NonAdminStillNeedsRolePermission()
    {
        var user = new UserAccount
        {
            IsActive = true,
            RoleCode = "manager",
            PermissionCodes = new[] { PermissionCodes.CatalogView, PermissionCodes.PosSell }
        };

        Assert.IsTrue(LocalRecoveryPermissionPolicy.IsGranted(user, PermissionCodes.CatalogView));
        Assert.IsFalse(LocalRecoveryPermissionPolicy.IsGranted(user, PermissionCodes.CatalogImport));
        Assert.IsFalse(LocalRecoveryPermissionPolicy.IsGranted(user, PermissionCodes.PosSell));
    }

    private static PosUserBootstrapState EmptyState()
    {
        return new PosUserBootstrapState();
    }
}
