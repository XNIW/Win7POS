using System.Runtime.Serialization.Json;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class CatalogImportOutboxPayloadBuilderTests
{
    [TestMethod]
    public void BuildSupplierExcelEntry_SourcePath_IsRedactedToFileName()
    {
        var preview = new SupplierImportSyncPreview
        {
            Fingerprint = "test-fingerprint"
        };
        preview.NewProducts.Add(new SupplierImportProductRow
        {
            Barcode = "990000000010",
            ProductName = "Redacted Payload",
            PurchasePrice = "100",
            RetailPrice = "180",
            RowNumber = 2
        });
        preview.Summary.NewProducts = 1;
        preview.Summary.NonSkippedRows = 1;
        preview.Summary.TotalRows = 1;

        var entry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(
            preview,
            @"C:\Users\Alice\Documents\Secret Client\supplier.xlsx",
            "test");

        Assert.IsNotNull(entry);
        Assert.IsFalse(entry.PayloadJson.Contains("Alice", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(entry.PayloadJson.Contains("Secret Client", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(entry.PayloadJson.Contains(@"C:\Users", StringComparison.OrdinalIgnoreCase));

        var request = Deserialize<PosCatalogImportRequest>(entry.PayloadJson);
        Assert.AreEqual("supplier.xlsx", request.Batch.SourceFileName);
        Assert.IsTrue(string.IsNullOrWhiteSpace(request.DeviceToken));
        Assert.IsTrue(string.IsNullOrWhiteSpace(request.SessionToken));
    }

    [TestMethod]
    public void BuildSupplierExcelEntry_SameLogicalImport_ProducesStablePayloadHash()
    {
        var first = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(
            CreatePreview(),
            "supplier.xlsx",
            "test");
        System.Threading.Thread.Sleep(20);
        var second = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(
            CreatePreview(),
            "supplier.xlsx",
            "test");

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual(first.ClientImportId, second.ClientImportId);
        Assert.AreEqual(first.IdempotencyKey, second.IdempotencyKey);
        Assert.AreEqual(first.PayloadHash, second.PayloadHash);
        Assert.AreEqual(first.PayloadJson, second.PayloadJson);
    }

    private static SupplierImportSyncPreview CreatePreview()
    {
        var preview = new SupplierImportSyncPreview
        {
            Fingerprint = "stable-fingerprint"
        };
        preview.NewProducts.Add(new SupplierImportProductRow
        {
            Barcode = "990000000020",
            ProductName = "Stable Payload",
            PurchasePrice = "100",
            RetailPrice = "180",
            RowNumber = 2
        });
        preview.Summary.NewProducts = 1;
        preview.Summary.NonSkippedRows = 1;
        preview.Summary.TotalRows = 1;
        return preview;
    }

    private static T Deserialize<T>(string json) where T : class
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (T)serializer.ReadObject(stream)!;
    }
}
