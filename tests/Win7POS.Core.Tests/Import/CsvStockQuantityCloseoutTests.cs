using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;

namespace Win7POS.Core.Tests.Import;

[TestClass]
public sealed class CsvStockQuantityCloseoutTests
{
    [TestMethod]
    public void LegacyAndProductDbCsvPreserveFractionalStockAndFourColumnsRemainOptional()
    {
        var legacy = CsvImportParser.Parse("00a B条码;Product;1000;300;1.234");
        Assert.AreEqual(0, legacy.Errors.Count);
        Assert.AreEqual(1.234m, legacy.Rows.Single().Stock);
        var productDb = CsvImportParser.Parse("00a B条码;00Item a;Product;Second;1000;300;;;Supplier;Category;1.25");
        Assert.AreEqual(0, productDb.Errors.Count);
        Assert.AreEqual(1.25m, productDb.Rows.Single().Stock);
        var four = CsvImportParser.Parse("00a B条码;Product;1000;300");
        Assert.AreEqual(0, four.Errors.Count);
        Assert.IsNull(four.Rows.Single().Stock);
    }

    [TestMethod]
    [DataRow("-1")]
    [DataRow("1.2345")]
    [DataRow("NaN")]
    [DataRow("2147483648")]
    public void InvalidCsvStockIsReportedWithoutApplyingOrReplacingItWithZero(string quantity)
    {
        var parsed = CsvImportParser.Parse("00a;Product;1000;300;" + quantity);
        Assert.AreEqual(0, parsed.Rows.Count);
        Assert.AreEqual(1, parsed.Errors.Count);
        StringAssert.StartsWith(parsed.Errors.Single().Message, "InvalidStock:");
    }
}
