using Win7POS.Core.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class PosBootstrapDiagnosticsPolicyTests
{
    [TestMethod]
    public void FailureStage_IsBoundedAndDeterministic()
    {
        Assert.AreEqual("dns", PosBootstrapDiagnosticsPolicy.GetFailureStage("dns", null, false));
        Assert.AreEqual("tls", PosBootstrapDiagnosticsPolicy.GetFailureStage("tls", null, false));
        Assert.AreEqual("network", PosBootstrapDiagnosticsPolicy.GetFailureStage("network_error", null, false));
        Assert.AreEqual("timeout", PosBootstrapDiagnosticsPolicy.GetFailureStage("timeout", null, false));
        Assert.AreEqual("server_response", PosBootstrapDiagnosticsPolicy.GetFailureStage("http_403", 403, true));
        Assert.AreEqual("invalid_response", PosBootstrapDiagnosticsPolicy.GetFailureStage("invalid_response", 200, true));
        Assert.AreEqual("first_login_contract", PosBootstrapDiagnosticsPolicy.GetFailureStage("unsupported_app_version", 409, true));
        Assert.AreEqual("device_pending_approval", PosBootstrapDiagnosticsPolicy.GetFailureStage("device_pending", 403, true));
        Assert.AreEqual("device_denied", PosBootstrapDiagnosticsPolicy.GetFailureStage("device_revoked", 403, true));
        Assert.AreEqual("staff_denied", PosBootstrapDiagnosticsPolicy.GetFailureStage("staff_denied", 403, true));
    }

    [TestMethod]
    public void RootCode_UsesApplicationCodeOrBoundedHttpFallback()
    {
        Assert.AreEqual("http_401", PosBootstrapDiagnosticsPolicy.GetRootCode(null, 401));
        Assert.AreEqual("http_403", PosBootstrapDiagnosticsPolicy.GetRootCode(null, 403));
        Assert.AreEqual("http_409", PosBootstrapDiagnosticsPolicy.GetRootCode(null, 409));
        Assert.AreEqual("http_5xx", PosBootstrapDiagnosticsPolicy.GetRootCode(null, 503));
        Assert.AreEqual("device_pending_approval", PosBootstrapDiagnosticsPolicy.GetRootCode("device_pending_approval", 403));
    }

    [TestMethod]
    public void DeviceApprovalState_IsNeverInferredFromASecret()
    {
        Assert.AreEqual("pending", PosBootstrapDiagnosticsPolicy.GetDeviceApprovalState("device_pending_approval", null));
        Assert.AreEqual("denied", PosBootstrapDiagnosticsPolicy.GetDeviceApprovalState("device_revoked", null));
        Assert.AreEqual("approved", PosBootstrapDiagnosticsPolicy.GetDeviceApprovalState(null, "active"));
        Assert.AreEqual("unknown", PosBootstrapDiagnosticsPolicy.GetDeviceApprovalState(null, null));
    }

    [TestMethod]
    public void Retryability_IsBoundedByRootAndAuthentication()
    {
        Assert.IsTrue(PosBootstrapDiagnosticsPolicy.IsRetryable("timeout", null, false));
        Assert.IsTrue(PosBootstrapDiagnosticsPolicy.IsRetryable("http_5xx", 502, false));
        Assert.IsFalse(PosBootstrapDiagnosticsPolicy.IsRetryable("http_403", 403, true));
        Assert.IsFalse(PosBootstrapDiagnosticsPolicy.IsRetryable("invalid_response", 200, false));
    }
}
