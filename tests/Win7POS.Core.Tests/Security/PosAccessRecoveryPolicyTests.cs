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
            ActiveRemoteMirrors = 0
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
            ActiveRemoteMirrors = 0
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
    public void MixedRemoteAndLocalUsers_AuthenticationDenied_OffersOnlySeparateLocalLogin()
    {
        var state = new PosUserBootstrapState
        {
            TotalUserRows = 2,
            ActiveLoginableUsers = 2,
            ActiveRemoteMirrors = 1
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
            ActiveRemoteMirrors = 0
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
    public void SaleSafeCatalog_ExitsRecoveryShell()
    {
        Assert.AreEqual(
            PosShellMode.Pos,
            PosShellStartupPolicy.Determine(PosAuthenticatedAccessMode.LocalRecovery, catalogSaleSafe: true));
    }

    private static PosUserBootstrapState EmptyState()
    {
        return new PosUserBootstrapState();
    }
}
