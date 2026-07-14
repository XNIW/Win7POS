using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Pos;

namespace Win7POS.Core.Tests.Pos;

[TestClass]
public sealed class PosNoticePolicyTests
{
    [TestMethod]
    public void SeverityPolicy_IsLanguageIndependentAndUsesRequiredDurations()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(3), PosNoticePolicy.GetAutoDismissDelay(PosNoticeSeverity.Success));
        Assert.AreEqual(TimeSpan.FromSeconds(4), PosNoticePolicy.GetAutoDismissDelay(PosNoticeSeverity.Info));
        Assert.AreEqual(TimeSpan.FromSeconds(8), PosNoticePolicy.GetAutoDismissDelay(PosNoticeSeverity.Warning));
        Assert.IsNull(PosNoticePolicy.GetAutoDismissDelay(PosNoticeSeverity.Error));
    }

    [TestMethod]
    public void OnlyWarningsAndErrorsExposeManualDismiss()
    {
        Assert.IsFalse(PosNoticePolicy.CanDismissManually(PosNoticeSeverity.Info));
        Assert.IsFalse(PosNoticePolicy.CanDismissManually(PosNoticeSeverity.Success));
        Assert.IsTrue(PosNoticePolicy.CanDismissManually(PosNoticeSeverity.Warning));
        Assert.IsTrue(PosNoticePolicy.CanDismissManually(PosNoticeSeverity.Error));
    }
}
