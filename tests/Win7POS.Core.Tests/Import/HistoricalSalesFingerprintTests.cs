using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data.Import;

namespace Win7POS.Core.Tests.Import;

[TestClass]
public sealed class HistoricalSalesFingerprintTests
{
    [TestMethod]
    public void StreamedHashPreservesLiteralUtf16LengthUtf8Protocol()
    {
        Verify(Array.Empty<string?>(), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        Verify(new string?[] { null, "", "abc", "00123", "x:y" }, "ef190b0a9da337a1b484e512e0bfb750e03a6dd36a62695dd86fb11162b31554");
        Verify(new string?[] { "á中😀", null, "e\u0301", "☕", "a\0b" }, "af8601f39d3439e9f1377741013a499b1694889d79debff5184377c664497daa");
    }

    private static void Verify(string?[] fields, string literalExpected)
    {
        // Independent reference is the previous whole-string protocol. The fixed
        // digests were calculated from its literal framed strings, not Hash().
        var old = new StringBuilder();
        foreach (var field in fields) { var value = field ?? ""; old.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value); }
        Assert.AreEqual(literalExpected, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(old.ToString()))));
        Assert.AreEqual(literalExpected, HistoricalSalesImportPlan.Hash(fields.Select(f => f!)));
    }
}
