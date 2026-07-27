using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class CatalogProductRowDiagnosticTests
{
    [TestMethod]
    public void BlankBarcode_IsClassifiedWithoutRenderingTheValue()
    {
        var result = CatalogProductRowDiagnostic.Describe(7, new PosCatalogProductResponse
        {
            ProductId = "remote-product",
            Barcode = " ",
            ProductName = "Product",
            RetailPrice = 100
        });

        Assert.AreEqual("blank_barcode", result.Reason);
        Assert.AreEqual(7, result.Row);
        Assert.AreEqual(1, result.BarcodeLength);
        Assert.AreEqual("positive_converts_to_long", result.PriceClass);
    }

    [TestMethod]
    public void NonfinitePrice_IsClassifiedAsSuch()
    {
        var result = CatalogProductRowDiagnostic.Describe(1, new PosCatalogProductResponse
        {
            ProductId = "remote-product",
            Barcode = "safe-barcode",
            RetailPrice = double.NaN
        });

        Assert.AreEqual("nonfinite_retail_price", result.Reason);
        Assert.AreEqual("nonfinite", result.PriceClass);
    }
}
