using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Models;

namespace Win7POS.Core.Tests.Import;

[TestClass]
public sealed class SupplierImportInputBoundsTests
{
    [TestMethod]
    [DataRow("purchasePrice", "2147483648")]
    [DataRow("purchasePrice", "2147483647.1")]
    [DataRow("retailPrice", "9223372036854775808")]
    [DataRow("retailPrice", "9223372036854775807.1")]
    [DataRow("purchasePrice", "NaN")]
    [DataRow("retailPrice", "Infinity")]
    [DataRow("purchasePrice", "-Infinity")]
    [DataRow("retailPrice", "1e999")]
    [DataRow("purchasePrice", "abc")]
    [DataRow("purchasePrice", "-1")]
    [DataRow("purchasePrice", "0.00000000000000000000000000011")]
    [DataRow("purchasePrice", "1e-99")]
    [DataRow("retailPrice", "9223372036854775807.00000000001")]
    public void InvalidPrice_PreviewKeepsCellAndBlocksWithoutThrowing(string field, string value)
    {
        var row = ValidRow();
        if (field == "purchasePrice") row.PurchasePrice = value;
        else row.RetailPrice = value;

        var preview = SupplierImportAnalyzer.BuildSyncPreview(new[] { row }, Array.Empty<ProductDetailsRow>());

        Assert.IsFalse(preview.CanApply);
        Assert.IsTrue(preview.Errors.Any(error => error.RowIndex == 17 && error.Barcode == row.Barcode && error.Message.Contains(field)));
        Assert.AreEqual(value, field == "purchasePrice" ? preview.FinalRows[0].PurchasePrice : preview.FinalRows[0].RetailPrice);
        row.PurchasePrice = "100";
        row.RetailPrice = "200";
        Assert.IsTrue(SupplierImportAnalyzer.BuildSyncPreview(new[] { row }, Array.Empty<ProductDetailsRow>()).CanApply);
    }

    [TestMethod]
    public void ExactStorageBounds_ArePreservedWithoutDoubleRounding()
    {
        var row = ValidRow();
        row.PurchasePrice = int.MaxValue.ToString();
        row.RetailPrice = long.MaxValue.ToString();
        var preview = SupplierImportAnalyzer.BuildSyncPreview(new[] { row }, Array.Empty<ProductDetailsRow>());
        Assert.IsTrue(preview.CanApply);
        Assert.AreEqual("2147483647", preview.NewProducts.Single().PurchasePrice);
        Assert.AreEqual("9223372036854775807", preview.NewProducts.Single().RetailPrice);
    }

    [TestMethod]
    [DataRow("12.5", "12")]
    [DataRow("1.234,56", "1235")]
    [DataRow("1,234.56", "1235")]
    public void ExistingPriceRoundingContract_IsPreserved(string value, string expected)
    {
        var row = ValidRow();
        row.PurchasePrice = value;
        row.RetailPrice = value;
        var preview = SupplierImportAnalyzer.BuildSyncPreview(new[] { row }, Array.Empty<ProductDetailsRow>());
        Assert.IsTrue(preview.CanApply);
        Assert.AreEqual(expected, preview.NewProducts.Single().PurchasePrice);
        Assert.AreEqual(expected, preview.NewProducts.Single().RetailPrice);
    }

    [TestMethod]
    [DataRow("barcode", 81)]
    [DataRow("itemNumber", 121)]
    [DataRow("productName", 241)]
    [DataRow("secondProductName", 241)]
    [DataRow("supplier", 121)]
    [DataRow("category", 121)]
    public void TextBeyondImportPayloadContract_IsReportedWithoutTruncation(string field, int length)
    {
        var row = ValidRow();
        var value = new string('x', length);
        switch (field)
        {
            case "barcode": row.Barcode = value; break;
            case "itemNumber": row.ItemNumber = value; break;
            case "productName": row.ProductName = value; break;
            case "secondProductName": row.SecondProductName = value; break;
            case "supplier": row.Supplier = value; break;
            case "category": row.Category = value; break;
        }
        var preview = SupplierImportAnalyzer.BuildSyncPreview(new[] { row }, Array.Empty<ProductDetailsRow>());
        Assert.IsFalse(preview.CanApply);
        Assert.IsTrue(preview.Errors.Any(error => error.RowIndex == 17 && error.Message.Contains(field)));
        Assert.AreSame(row, preview.FinalRows.Single());
    }

    [TestMethod]
    public void InternalWhitespaceCountsTowardActualPayloadLength()
    {
        var row = ValidRow();
        row.ProductName = "x" + new string(' ', 239) + "y";
        var preview = SupplierImportAnalyzer.BuildSyncPreview(new[] { row }, Array.Empty<ProductDetailsRow>());
        Assert.IsFalse(preview.CanApply);
        Assert.IsTrue(preview.Errors.Any(error => error.Message.Contains("productName")));
        Assert.AreEqual(241, preview.FinalRows[0].ProductName.Length);
    }

    [TestMethod]
    public void BarcodeInternalWhitespacePreservesDistinctProductIdentity()
    {
        var first = ValidRow();
        first.Barcode = " AB  CD ";
        var second = ValidRow();
        second.RowNumber++;
        second.Barcode = "AB CD";
        var preview = SupplierImportAnalyzer.BuildSyncPreview(new[] { first, second }, Array.Empty<ProductDetailsRow>());
        Assert.IsTrue(preview.CanApply);
        CollectionAssert.AreEquivalent(new[] { "AB  CD", "AB CD" }, preview.NewProducts.Select(row => row.Barcode).ToArray());
        Assert.AreEqual(2, preview.ApplyExpectations.Count);
    }

    [TestMethod]
    [DataRow("itemNumber", 121)]
    [DataRow("productName", 241)]
    [DataRow("secondProductName", 241)]
    [DataRow("supplier", 121)]
    [DataRow("category", 121)]
    public void InheritedLegacyTextIsValidatedBeforePayloadTruncation(string field, int length)
    {
        var row = ValidRow();
        row.ProductName = "";
        var existing = new ProductDetailsRow { Id = 7, Barcode = row.Barcode, Name = "Legacy", UnitPrice = 200, PurchasePrice = 100 };
        var value = new string('x', length);
        switch (field)
        {
            case "itemNumber": existing.ArticleCode = value; break;
            case "productName": existing.Name = value; break;
            case "secondProductName": existing.Name2 = value; break;
            case "supplier": existing.SupplierName = value; break;
            case "category": existing.CategoryName = value; break;
        }
        var preview = SupplierImportAnalyzer.BuildSyncPreview(new[] { row }, new[] { existing });
        Assert.IsFalse(preview.CanApply);
        Assert.IsTrue(preview.Errors.Any(error => error.Message.Contains(field)));
    }

    [TestMethod]
    public void Analyze_InvalidPriceAlsoRetainsDraftForCorrection()
    {
        var table = SupplierImportAnalyzer.BuildRawTable("bounds", new IReadOnlyList<string>[]
        {
            new[] { "barcode", "productName", "purchasePrice", "retailPrice" },
            new[] { "BOUNDS-ANALYSIS", "Bounds", "2147483648", "200" }
        });
        var analysis = SupplierImportAnalyzer.Analyze(table, Array.Empty<ProductDetailsRow>());
        Assert.AreEqual("2147483648", analysis.EditableRows.Single().PurchasePrice);
        Assert.IsFalse(SupplierImportAnalyzer.BuildSyncPreview(analysis.EditableRows, Array.Empty<ProductDetailsRow>()).CanApply);
    }

    [TestMethod]
    [DataRow("NaN")]
    [DataRow("Infinity")]
    [DataRow("-Infinity")]
    [DataRow("1e999")]
    public void ParseNumber_RejectsNonFiniteAcrossRuntimes(string value)
    {
        Assert.IsNull(SupplierImportAnalyzer.ParseNumber(value));
    }

    [TestMethod]
    [DataRow("2147483648", 30.0)]
    [DataRow("100", 1e308)]
    [DataRow("100", double.NaN)]
    [DataRow("100", double.PositiveInfinity)]
    [DataRow("100", -101.0)]
    public void MarkupOutsideSupportedRange_DoesNotThrowOrReplaceDraft(string purchase, double markup)
    {
        var row = ValidRow();
        row.PurchasePrice = purchase;
        row.RetailPrice = "";
        Assert.IsNull(SupplierRetailPriceHelper.CalculateRetailPrice(purchase, markup, 100));
        Assert.AreEqual(0, SupplierRetailPriceHelper.ApplyMarkupToRetailPriceRows(new[] { row }, markup, 100, true));
        Assert.AreEqual("", row.RetailPrice);
        Assert.AreEqual(purchase, row.PurchasePrice);
    }

    private static SupplierImportEditableRow ValidRow() => new()
    {
        RowNumber = 17, Barcode = "BOUNDS-17", ProductName = "Bounds", PurchasePrice = "100", RetailPrice = "200"
    };
}
