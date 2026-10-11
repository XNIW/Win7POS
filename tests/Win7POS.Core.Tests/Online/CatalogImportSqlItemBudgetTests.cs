using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class CatalogImportSqlItemBudgetTests
{
    [TestMethod]
    public void ActualDense523Child_HttpFitsButSqlDoesNot_NewPlanSplitsWithoutChangingSavedOperation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "catalog-import-sql-budget-v1", "dense523.child-projection.json.gz");
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, new UTF8Encoding(false, true));
        var projection = reader.ReadToEnd();
        Assert.AreEqual("ca2080cf288a7570eeadae7db4b8df6fd0be0607f5e619d8a4649877562d1dc7", CatalogImportOutboxPayloadBuilder.Sha256Hex(projection));
        var request = CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(projection);
        request.PayloadHash = null; request.Batch.AttemptCount = 0;
        var original = Entry(request);
        Assert.AreEqual(523, request.Items.Length);
        Assert.AreEqual("bcc3a3774ec2c52a00557590dbd341240911e3ddea9fa3dd8f7a499f3b8429f2", original.PayloadHash);
        var savedJson = original.PayloadJson; var savedHash = original.PayloadHash; var savedId = original.ClientImportId;
        Assert.IsTrue(CatalogImportPlanBuilder.Measure(original) <= 524288, "The real failed child already satisfied the HTTP bound.");
        Assert.IsTrue(CatalogImportPlanBuilder.MeasureSqlItems(original) >= 572424, "The conservative bound must cover observed PostgreSQL size.");
        var plan = CatalogImportPlanBuilder.Split(original);
        Assert.IsTrue(plan.Entries.Count > 1, "HTTP-only planning reproduced the SQL rejection.");
        AssertPlan(plan, request);
        Assert.AreEqual(savedJson, original.PayloadJson); Assert.AreEqual(savedHash, original.PayloadHash); Assert.AreEqual(savedId, original.ClientImportId);
        var repeated = CatalogImportPlanBuilder.Split(original);
        CollectionAssert.AreEqual(plan.Entries.Select(part => part.PayloadJson).ToArray(), repeated.Entries.Select(part => part.PayloadJson).ToArray());
    }

    [TestMethod]
    [DataRow("nullable")] [DataRow("fallback")] [DataRow("nfc_expansion")] [DataRow("supplementary")]
    public void NullableFallbackUnicodeAndNumericExtremes_KeepAllRowsInBothBudgets(string shape)
    {
        var items = Enumerable.Range(0, 1000).Select(index => new PosCatalogImportItemRequest
        {
            RowNumber = int.MaxValue - index, Barcode = "SQL-BUDGET-" + index, ClientItemId = "source-row-" + index,
            ChangeKind = "new", Operation = "upsert_product", ProductName = "ASCII",
            PurchasePrice = "0", RetailPrice = "999999999", Quantity = "999999999"
        }).ToArray();
        foreach (var item in items)
        {
            if (shape == "fallback") { item.ProductName = null; item.SecondProductName = new string('x', 240); item.ItemNumber = new string('i', 120); }
            if (shape == "nfc_expansion") item.ProductName = new string('\u0344', 120); // NFC expands to 240 UTF-16 code units.
            if (shape == "supplementary") { item.ProductName = string.Concat(Enumerable.Repeat("😀", 120)); item.Quantity = "0.00000000000000000000000000000000000001"; }
        }
        if (shape == "nfc_expansion") Assert.IsTrue(Encoding.UTF8.GetByteCount(items[0].ProductName.Normalize()) > Encoding.UTF8.GetByteCount(items[0].ProductName));
        var request = Request(items);
        var entry = Entry(request); var saved = entry.PayloadJson;
        var plan = CatalogImportPlanBuilder.Split(entry);
        AssertPlan(plan, request);
        Assert.AreEqual(saved, entry.PayloadJson);
        Assert.AreEqual(items[0].Barcode, request.Items[0].Barcode, "Sizing must preserve identity text.");
        if (shape == "nullable") Assert.AreEqual(1, plan.Entries.Count, "Compact nullable imports still permit 1000 rows.");
        else Assert.IsTrue(plan.Entries.Count > 1, "Normalized fallback or Unicode storage must participate in partitioning.");
    }

    [TestMethod]
    public void CompactLiveSuccession_StillProduces1000And1Rows()
    {
        const string prefix = "U2-0123456789abcdef-S-";
        var items = Enumerable.Range(0, 1001).Select(index => new PosCatalogImportItemRequest
        {
            RowNumber = index + 2, Barcode = prefix + index.ToString("D5"), ClientItemId = "original-row-" + index,
            ChangeKind = "updated", Operation = "upsert_product", DiffSummary = "legacy_recovery",
            ProductName = "U2 successor " + index, SecondProductName = "U2 succession " + index,
            ItemNumber = prefix + index, PurchasePrice = "900", RetailPrice = "1200", Quantity = "1.25"
        }).ToArray();
        var plan = CatalogImportPlanBuilder.Split(Entry(Request(items)));
        CollectionAssert.AreEqual(new[] { 1000, 1 }, plan.Entries.Select(CatalogImportPlanBuilder.CountRows).ToArray());
        Assert.IsTrue(plan.Entries.All(entry => CatalogImportPlanBuilder.MeasureSqlItems(entry) <= 524288));
    }

    [TestMethod]
    public void ShortNonAsciiName_ExplicitlyReservesAcceptedLimitWithoutChangingText()
    {
        var item = new PosCatalogImportItemRequest { RowNumber = 2, Barcode = "unicode-reserve", ClientItemId = "source-row-2",
            ChangeKind = "new", Operation = "upsert_product", ProductName = "A" };
        var ascii = CatalogImportSqlItemBudget.MeasureOrdinary(new[] { item });
        item.ProductName = "漢";
        var shortUnicode = CatalogImportSqlItemBudget.MeasureOrdinary(new[] { item });
        Assert.AreEqual(719L, shortUnicode - ascii, "A one-character non-ASCII name reserves 720 bytes instead of one ASCII byte.");
        item.ProductName = new string('漢', 240);
        Assert.AreEqual(shortUnicode, CatalogImportSqlItemBudget.MeasureOrdinary(new[] { item }), "The reserve is the accepted UTF-16 field cap, not a safety ratio.");
        Assert.AreEqual(new string('漢', 240), item.ProductName, "Measuring cannot normalize or truncate immutable text.");
    }

    [TestMethod]
    public void CorrectionNestedMaskAndDecimalSnapshot_BudgetIncludesContainersAndNullableValues()
    {
        var items = Enumerable.Range(0, 1000).Select(index => new PosCatalogImportCorrectionItem
        {
            ClientItemId = new string('a', 190) + index, RemoteProductId = "10000000-0000-4000-8000-000000000001",
            BaseRevision = "2026-10-11T21:00:00.000000Z", FieldMask = new[] { "purchasePrice", "retailPrice", "quantityDelta" },
            BaseSnapshot = new PosCatalogImportCorrectionBaseSnapshot { PurchasePrice = decimal.MaxValue, RetailPrice = null, StockQuantity = decimal.MinValue },
            Changes = new PosCatalogImportCorrectionChanges { PurchasePrice = 999999999m, RetailPrice = 0m, QuantityDelta = -1000000000m }
        }).ToArray();
        Assert.IsTrue(CatalogImportSqlItemBudget.MeasureCorrection(items) > 524288, "All nested objects, entries and numeric storage count toward the unchanged RPC bound.");
        Assert.IsTrue(CatalogImportSqlItemBudget.MeasureCorrection(items.Take(500).ToArray()) <= 524288);
        Assert.AreEqual(decimal.MaxValue, items[0].BaseSnapshot.PurchasePrice);
        Assert.IsNull(items[0].BaseSnapshot.RetailPrice);
    }

    private static PosCatalogImportRequest Request(PosCatalogImportItemRequest[] items) => new()
    {
        Source = "supplier_excel", SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion,
        Batch = new PosCatalogImportBatchRequest { ClientImportId = "win7pos-source-sql-budget", IdempotencyKey = "win7pos-source-sql-budget:pos-catalog-import-v1", CreatedAt = "2026-10-11T21:00:00.000Z", PreviewFingerprint = "sql-budget-regression" },
        Items = items, Summary = new PosCatalogImportSummaryRequest { NewProducts = items.Count(item => item.ChangeKind == "new"), UpdatedProducts = items.Count(item => item.ChangeKind == "updated") }
    };

    private static CatalogImportOutboxEntry Entry(PosCatalogImportRequest request)
    {
        var json = CatalogImportRecoveryService.Serialize(request);
        return new CatalogImportOutboxEntry { ClientImportId = request.Batch.ClientImportId, IdempotencyKey = request.Batch.IdempotencyKey,
            PayloadJson = json, PayloadHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(json), SchemaVersion = request.SchemaVersion, Source = request.Source };
    }

    private static void AssertPlan(CatalogImportOutboxPlan plan, PosCatalogImportRequest source)
    {
        Assert.AreEqual(source.Items.Length, plan.TotalRows);
        Assert.IsTrue(plan.Entries.All(part => CatalogImportPlanBuilder.CountRows(part) <= 1000 &&
            CatalogImportPlanBuilder.Measure(part) <= 524288 && CatalogImportPlanBuilder.MeasureSqlItems(part) <= 524288));
        var originalByBarcode = source.Items.ToDictionary(item => item.Barcode, StringComparer.Ordinal);
        var actual = plan.Entries.SelectMany(part => CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(part.PayloadJson).Items).ToArray();
        Assert.AreEqual(source.Items.Length, actual.Select(item => item.Barcode).Distinct().Count());
        Assert.IsTrue(actual.All(item => CatalogImportOutboxPayloadBuilder.SameIntent(item, originalByBarcode[item.Barcode]) &&
            item.RowNumber == originalByBarcode[item.Barcode].RowNumber && item.Operation == originalByBarcode[item.Barcode].Operation));
    }
}
