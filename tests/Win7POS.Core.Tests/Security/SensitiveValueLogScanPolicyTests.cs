using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Security;

namespace Win7POS.Core.Tests.Security;

[TestClass]
public sealed class SensitiveValueLogScanPolicyTests
{
    private const string SyntheticShortCode = "4729";

    [TestMethod]
    public void ShortNumericSubstringInsideCounter_IsNotASecretMatch()
    {
        Assert.IsFalse(SensitiveValueLogScanPolicy.ContainsSensitiveValue(
            "page=512, bytes=88472917, hasMore=True",
            SyntheticShortCode));
    }

    [TestMethod]
    [DataRow("staff=4729")]
    [DataRow("{\"staffCode\":\"4729\"}")]
    [DataRow("?staff=4729&mode=qa")]
    [DataRow("X-Staff-Code: 4729")]
    [DataRow("pin:4729;")]
    public void ShortNumericStandaloneOrDelimitedLeak_IsDetected(string logLine)
    {
        Assert.IsTrue(SensitiveValueLogScanPolicy.ContainsSensitiveValue(
            logLine,
            SyntheticShortCode));
    }

    [TestMethod]
    public void LongOrHighEntropySecret_RemainsExactSubstringProtected()
    {
        var syntheticCredential = string.Concat(
            "qa_",
            "H7vJ9zN4",
            "pQ2x");
        Assert.IsTrue(SensitiveValueLogScanPolicy.ContainsSensitiveValue(
            "authorization=" + syntheticCredential + "-suffix",
            syntheticCredential));
        Assert.IsFalse(SensitiveValueLogScanPolicy.ContainsSensitiveValue(
            "authorization=[redacted]",
            syntheticCredential));
    }
}
