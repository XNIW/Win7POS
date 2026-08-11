using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Models;

namespace Win7POS.Core.Tests.Import;

[TestClass]
public sealed class SupplierImportCurrentAndroidParityTests
{
    private const string AndroidMain = "4b2b4a93dd5d4db7d1cfb83e897aa5cbac40366e";

    [TestMethod]
    public void GoldenCorpus_CurrentAndroidNormalizedOutputsRemainStable()
    {
        using var corpus = LoadCorpus();
        Assert.AreEqual(AndroidMain, corpus.RootElement.GetProperty("canonicalAndroidMain").GetString());
        var cases = corpus.RootElement.GetProperty("cases").EnumerateArray().ToList();
        Assert.AreEqual(16, cases.Count);

        foreach (var fixture in cases)
        {
            var id = fixture.GetProperty("id").GetString() ?? string.Empty;
            var rows = ReadRows(fixture.GetProperty("rows"));
            var actual = JsonSerializer.SerializeToElement(BuildExpected(id, rows));
            var expected = fixture.GetProperty("expected");
            Assert.IsTrue(
                JsonElement.DeepEquals(expected, actual),
                $"Current-Android parity changed for {id}." + Environment.NewLine +
                "Expected: " + expected.GetRawText() + Environment.NewLine +
                "Actual: " + actual.GetRawText());
        }
    }

    [TestMethod]
    public void CompoundHeaders_UseExactFragmentsWithoutUnsafeSubstringMatching()
    {
        var table = BuildTable(
            "exact-fragments",
            new[] { "mybarcodevalue", "Nombre del producto", "Precio de compra", "Precio de venta" },
            new[] { "12345678", "Producto Uno", "100", "180" },
            new[] { "23456789", "Producto Dos", "110", "190" });

        var first = table.Columns.Single(column => column.OriginalHeader == "mybarcodevalue");
        Assert.AreNotEqual("alias", first.HeaderSource);
        Assert.IsTrue(table.Columns.Any(column => column.CanonicalKey == AndroidImportKeys.Barcode));

        var compound = GetCaseTable("chinese_spanish_compound_header");
        CollectionAssert.AreEqual(
            new[]
            {
                AndroidImportKeys.Barcode,
                AndroidImportKeys.ProductName,
                AndroidImportKeys.PurchasePrice,
                AndroidImportKeys.RetailPrice,
                AndroidImportKeys.Quantity
            },
            compound.Columns.Select(column => column.CanonicalKey).ToArray());
        Assert.IsTrue(compound.Columns.All(column => column.HeaderSource == "alias"));
    }

    [TestMethod]
    public void TwoRowHeaders_MergeAtMostTwoNonDataRowsAndPreserveSourceRows()
    {
        var table = GetCaseTable("metadata_then_two_row_header");

        Assert.AreEqual("combined-lookback", table.DetectionTrace.HeaderMode);
        CollectionAssert.AreEqual(new[] { 2, 3 }, table.DetectionTrace.HeaderRows.ToArray());
        Assert.AreEqual(4, table.DataRowIndex);
        CollectionAssert.AreEqual(new[] { 5, 6 }, table.Rows.Select(row => row.RowNumber).ToArray());
        Assert.AreEqual("ARTICULO", table.Columns[2].OriginalHeader);
        Assert.AreEqual(AndroidImportKeys.ProductName, table.Columns[2].CanonicalKey);
    }

    [TestMethod]
    public void AmbiguousCandidates_RemainAvailableForManualMapping()
    {
        var barcodeTable = GetCaseTable("almost_equal_barcode_candidates");
        var barcodeDecision = barcodeTable.DetectionTrace.FieldDecisions.Single(
            decision => decision.Field == AndroidImportKeys.Barcode);
        Assert.IsNull(barcodeDecision.SelectedColumnIndex);
        Assert.AreEqual("low-confidence", barcodeDecision.Reason);
        Assert.AreEqual(2, barcodeDecision.Candidates.Count(candidate => candidate.Score == barcodeDecision.Candidates[0].Score));
        Assert.IsFalse(barcodeTable.Columns.Any(column =>
            column.CanonicalKey == AndroidImportKeys.Barcode && !column.IsGenerated));

        var numericTable = GetCaseTable("headerless_ambiguous_numeric_columns");
        var purchaseDecision = numericTable.DetectionTrace.FieldDecisions.Single(
            decision => decision.Field == AndroidImportKeys.PurchasePrice);
        Assert.IsNull(purchaseDecision.SelectedColumnIndex);
        Assert.AreEqual("low-confidence", purchaseDecision.Reason);
        Assert.IsFalse(numericTable.Columns.Any(column =>
            column.CanonicalKey == AndroidImportKeys.PurchasePrice && !column.IsGenerated));
    }

    [TestMethod]
    public void DiagnosticTrace_IsBoundedAndContainsNoWorksheetValues()
    {
        var table = GetCaseTable("headerless_clear_dataset");
        var trace = table.DetectionTrace;

        Assert.AreEqual(AndroidImportKeys.AllKeys.Length, trace.FieldDecisions.Count);
        Assert.IsTrue(trace.HeaderRows.Count <= 2);
        Assert.IsTrue(trace.SampleSize <= 40);
        Assert.IsTrue(trace.FieldDecisions.All(decision => decision.Candidates.Count <= 3));
        Assert.IsTrue(trace.FieldDecisions.SelectMany(decision => decision.Candidates)
            .All(candidate => candidate.Reasons.Count <= 4));
        var diagnosticText = string.Join("|", trace.FieldDecisions.SelectMany(decision =>
            new[] { decision.Field, decision.Confidence, decision.Reason }
                .Concat(decision.Candidates.SelectMany(candidate => candidate.Reasons))));
        Assert.IsFalse(diagnosticText.Contains("Dream Item", StringComparison.Ordinal));
        Assert.IsFalse(diagnosticText.Contains("687112820", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ReceivingOnlyFields_AreClassifiedWithoutChangingCatalogFieldRules()
    {
        var realQuantityTable = GetCaseTable("real_quantity_plus_quantity");
        var realQuantityAnalysis = SupplierImportAnalyzer.Analyze(
            realQuantityTable,
            Array.Empty<ProductDetailsRow>());
        Assert.AreEqual("9", realQuantityAnalysis.EditableRows.Single().Quantity,
            "Current Android realQuantity precedence must remain explicit in the parity matrix.");

        var receivingTable = GetCaseTable("receiving_only_keys_excluded");
        var receivingAnalysis = SupplierImportAnalyzer.Analyze(
            receivingTable,
            Array.Empty<ProductDetailsRow>());
        var row = receivingAnalysis.EditableRows.Single();
        Assert.AreEqual("100", row.PurchasePrice, "discountedPrice must not replace catalog purchasePrice in Win7 apply rows.");
        Assert.AreEqual("5", row.Quantity);
        Assert.AreEqual("180", row.RetailPrice);
        Assert.IsFalse(receivingTable.Columns.Any(column => column.CanonicalKey == AndroidImportKeys.Complete));
    }

    private static object BuildExpected(string id, string[][] rows)
    {
        var table = BuildTable(id, rows);
        var analysis = SupplierImportAnalyzer.Analyze(table, Array.Empty<ProductDetailsRow>());
        foreach (var row in analysis.EditableRows)
        {
            if (!row.Exists && string.IsNullOrWhiteSpace(row.RetailPrice) &&
                !string.IsNullOrWhiteSpace(row.Barcode) &&
                (!string.IsNullOrWhiteSpace(row.ProductName) ||
                 !string.IsNullOrWhiteSpace(row.SecondProductName) ||
                 !string.IsNullOrWhiteSpace(row.ItemNumber)))
            {
                row.RetailPrice = "200";
            }
        }
        var preview = SupplierImportAnalyzer.BuildSyncPreview(analysis.EditableRows, Array.Empty<ProductDetailsRow>());
        return new
        {
            dataRowIndex = table.DataRowIndex,
            hasHeader = table.HasHeader,
            headerRows = table.DetectionTrace.HeaderRows,
            headerMode = table.DetectionTrace.HeaderMode,
            mergedHeaders = table.Columns.Select(column => column.OriginalHeader).ToArray(),
            columns = table.Columns.Select(column =>
                column.ColumnIndex.ToString(CultureInfo.InvariantCulture) + ":" +
                column.CanonicalKey + ":" + column.HeaderSource + ":" +
                column.Confidence + ":" + (column.IsGenerated ? "generated" : "source")).ToArray(),
            selectedCandidates = table.DetectionTrace.FieldDecisions
                .Where(decision => decision.SelectedColumnIndex.HasValue)
                .ToDictionary(decision => decision.Field, decision => decision.SelectedColumnIndex.GetValueOrDefault()),
            rejectedAmbiguousCandidates = table.DetectionTrace.FieldDecisions
                .Where(decision => decision.Reason == "low-confidence" || decision.Reason == "header-alias-rejected")
                .Select(decision => decision.Field).ToArray(),
            candidateTrace = table.DetectionTrace.FieldDecisions
                .Where(decision => decision.Candidates.Count > 0)
                .ToDictionary(
                    decision => decision.Field,
                    decision => decision.Candidates.Select(candidate =>
                        candidate.ColumnIndex.ToString(CultureInfo.InvariantCulture) + ":" +
                        candidate.Score.ToString("0.000", CultureInfo.InvariantCulture) + ":" +
                        string.Join("+", candidate.Reasons)).ToArray()),
            sourceRowNumbers = table.Rows.Select(row => row.RowNumber).ToArray(),
            droppedSummaryRows = table.DroppedSummaryRows,
            warnings = analysis.Warnings.Select(WarningCode).ToArray(),
            editableRows = analysis.EditableRows.Select(row => string.Join("|", new[]
            {
                row.RowNumber.ToString(CultureInfo.InvariantCulture),
                row.Barcode,
                row.ItemNumber,
                row.ProductName,
                row.SecondProductName,
                row.PurchasePrice,
                row.RetailPrice,
                row.Quantity,
                row.Supplier,
                row.Category
            })).ToArray(),
            previewFingerprint = Sha256(preview.Fingerprint)
        };
    }

    private static SupplierExcelRawTable GetCaseTable(string id)
    {
        using var corpus = LoadCorpus();
        var fixture = corpus.RootElement.GetProperty("cases").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == id);
        return BuildTable(id, ReadRows(fixture.GetProperty("rows")));
    }

    private static SupplierExcelRawTable BuildTable(string name, params string[][] rows)
    {
        return SupplierImportAnalyzer.BuildRawTable(
            name,
            rows.Select(row => (IReadOnlyList<string>)row).ToList());
    }

    private static string[][] ReadRows(JsonElement rows)
    {
        return rows.EnumerateArray()
            .Select(row => row.EnumerateArray().Select(cell => cell.GetString() ?? string.Empty).ToArray())
            .ToArray();
    }

    private static JsonDocument LoadCorpus()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "supplier-import",
            "current-android-parity-corpus.json");
        return JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    private static string WarningCode(SupplierImportWarning warning)
    {
        var message = warning.Message ?? string.Empty;
        var code = message.Contains("duplicato", StringComparison.OrdinalIgnoreCase) ? "duplicate-last-wins" :
            message.Contains("Barcode mancante", StringComparison.OrdinalIgnoreCase) ? "missing-barcode" :
            message.Contains("Prezzo vendita", StringComparison.OrdinalIgnoreCase) ? "missing-retail-price" :
            message.Contains("Nuovo prodotto", StringComparison.OrdinalIgnoreCase) ? "missing-new-product-identity" :
            "other";
        return code + ":" + string.Join(",", warning.Rows);
    }

    private static string Sha256(string value)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();
    }
}
