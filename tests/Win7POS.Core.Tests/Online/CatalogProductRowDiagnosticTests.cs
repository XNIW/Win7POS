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

    [TestMethod]
    public void InvalidUpdatedAt_IsClassifiedWithoutRenderingTimestamp()
    {
        var result = CatalogProductRowDiagnostic.Describe(56, new PosCatalogProductResponse
        {
            ProductId = "remote-product",
            Barcode = "safe-barcode",
            RetailPrice = 100,
            UpdatedAt = "not-a-timestamp"
        });

        Assert.AreEqual("invalid_updated_at", result.Reason);
        Assert.AreEqual(15, result.UpdatedAtLength);
    }

    [TestMethod]
    public void InvalidPurchasePrice_IsClassifiedWithoutRenderingPrice()
    {
        var result = CatalogProductRowDiagnostic.Describe(56, new PosCatalogProductResponse
        {
            ProductId = "remote-product",
            Barcode = "safe-barcode",
            RetailPrice = 100,
            PurchasePrice = -1
        });

        Assert.AreEqual("invalid_purchase_price", result.Reason);
        Assert.AreEqual("negative", result.PurchasePriceClass);
    }

    [TestMethod]
    public void RecoverableProductName_IsNotClassifiedAsABlockingRow()
    {
        var result = CatalogProductRowDiagnostic.Describe(56, new PosCatalogProductResponse
        {
            ProductId = "remote-product",
            Barcode = "safe-barcode",
            ProductName = "sanitized fixture\nwith control",
            RetailPrice = 100
        });

        Assert.AreEqual("valid", result.Reason);
        Assert.AreEqual(30, result.ProductNameLength);
    }
}
