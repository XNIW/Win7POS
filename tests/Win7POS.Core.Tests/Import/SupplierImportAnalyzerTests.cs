using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Models;

namespace Win7POS.Core.Tests.Import;

[TestClass]
public sealed class SupplierImportAnalyzerTests
{
    [TestMethod]
    public void Analyze_SecondProductNameOnlyNewProduct_UsesSecondNameAsIdentity()
    {
        var table = SupplierTable(
            new[] { "barcode", "secondProductName", "purchasePrice", "retailPrice" },
            new[] { "990000000001", "Second Name Only", "100", "180" });

        var analysis = SupplierImportAnalyzer.Analyze(table, Array.Empty<ProductDetailsRow>());

        Assert.AreEqual(0, analysis.Errors.Count);
        Assert.AreEqual(1, analysis.NewProducts.Count);
        Assert.AreEqual("Second Name Only", analysis.NewProducts[0].ProductName);
    }

    [TestMethod]
    public void Analyze_DuplicateBarcode_KeepsLastOccurrence()
    {
        var table = SupplierTable(
            new[] { "barcode", "productName", "purchasePrice", "retailPrice" },
            new[] { "990000000002", "First", "100", "180" },
            new[] { "990000000002", "Last", "100", "190" });

        var analysis = SupplierImportAnalyzer.Analyze(table, Array.Empty<ProductDetailsRow>());

        Assert.AreEqual(0, analysis.Errors.Count);
        Assert.AreEqual(1, analysis.NewProducts.Count);
        Assert.AreEqual("Last", analysis.NewProducts[0].ProductName);
        CollectionAssert.AreEqual(new[] { 2, 3 }, analysis.Warnings.Single().Rows.ToArray());
    }

    [TestMethod]
    public void BuildSyncPreview_NewProductMissingRetailPrice_BlocksApply()
    {
        var rows = new[]
        {
            new SupplierImportEditableRow
            {
                RowNumber = 10,
                Barcode = "990000000003",
                ItemNumber = "MISSING-RETAIL",
                ProductName = "Missing Retail",
                PurchasePrice = "100",
                RetailPrice = ""
            }
        };

        var preview = SupplierImportAnalyzer.BuildSyncPreview(rows, Array.Empty<ProductDetailsRow>());

        Assert.IsFalse(preview.CanApply);
        Assert.IsTrue(preview.Errors.Any(error => error.Message.Contains("retailPrice", StringComparison.OrdinalIgnoreCase)));
    }

    private static SupplierExcelRawTable SupplierTable(params string[][] rows)
    {
        return SupplierImportAnalyzer.BuildRawTable(
            "test",
            rows.Select(row => (IReadOnlyList<string>)row.ToList()).ToList());
    }
}
