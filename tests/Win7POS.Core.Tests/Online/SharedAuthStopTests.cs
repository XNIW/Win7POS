using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class SharedAuthStopTests
{
    [TestMethod]
    [DataRow("auth_denied")]
    [DataRow("unauthorized")]
    [DataRow("forbidden")]
    [DataRow(" AUTH_DENIED ")]
    public void AuthenticationCodes_AreClassifiedForSharedStop(string code)
    {
        Assert.IsTrue(SharedAuthStopPolicy.IsAuthenticationDenied(code));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("timeout")]
    [DataRow("network_error")]
    [DataRow("validation_failed")]
    public void RetryableOrPermanentNonAuthCodes_DoNotTriggerGlobalStop(string code)
    {
        Assert.IsFalse(SharedAuthStopPolicy.IsAuthenticationDenied(code));
    }
}
