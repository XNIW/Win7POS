using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Pos;

namespace Win7POS.Core.Tests.Pos;

[TestClass]
public sealed class CustomerDisplayLayoutPolicyTests
{
    [TestMethod]
    [DataRow(800, 600, CustomerDisplayLayoutMode.Compact)]
    [DataRow(1024, 600, CustomerDisplayLayoutMode.Compact)]
    [DataRow(1024, 768, CustomerDisplayLayoutMode.Standard)]
    [DataRow(1366, 768, CustomerDisplayLayoutMode.Standard)]
    [DataRow(1920, 1080, CustomerDisplayLayoutMode.Large)]
    [DataRow(768, 1366, CustomerDisplayLayoutMode.Portrait)]
    [DataRow(3440, 1440, CustomerDisplayLayoutMode.Large)]
    public void DeterminesExpectedMode(int width, int height, CustomerDisplayLayoutMode expected)
    {
        Assert.AreEqual(expected, CustomerDisplayLayoutPolicy.Determine(width, height).Mode);
    }

    [TestMethod]
    public void SystemDpiScale_ReducesDipScaleWithoutChangingMode()
    {
        var normal = CustomerDisplayLayoutPolicy.Determine(1366, 768, 1.0, 1.0);
        var scaled = CustomerDisplayLayoutPolicy.Determine(1366, 768, 1.25, 1.25);
        Assert.AreEqual(normal.Mode, scaled.Mode);
        Assert.IsTrue(scaled.FontScale < normal.FontScale);
    }
}
