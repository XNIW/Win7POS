using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Data.Import;

namespace Win7POS.Core.Tests.Import;

[TestClass]
public sealed class GetnetImportTests
{
    private static HistoricalSalesImportOptions Approved() => new()
    {
        LegacyClpConfirmed = true,
        ModernTimeInterpretation = HistoricalSalesModernTimeInterpretation.IsoUtc,
        TemporalEvidenceReference = "synthetic-provider-evidence-v1"
    };

    [TestMethod]
    public void DistributedSyntheticCorpus_MatchesIndependentFixedOracle()
    {
        var directory = System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "historical-sales-getnet-v1");
        var files = new[] { "synthetic-legacy.xlsx", "synthetic-modern.xlsx" }.Select(x => System.IO.Path.Combine(directory, x)).ToArray();
        Assert.IsTrue(files.All(File.Exists), "Synthetic fixtures must be distributed to the consumer output.");
        var result = new HistoricalSalesImportAnalyzer().Analyze(files, Approved());
        Assert.IsTrue(result.CanApply, string.Join(",", result.Diagnostics.Select(x => x.Code)));
        Assert.AreEqual(6, result.Summary.SourceRows);
        Assert.AreEqual(5, result.Summary.AcceptedCount);
        Assert.AreEqual(4, result.Summary.SaleCount);
        Assert.AreEqual(1, result.Summary.CancellationCount);
        Assert.AreEqual(1, result.Summary.RejectedCount);
        Assert.AreEqual(3380L, result.Summary.GrossClp);
        Assert.AreEqual(3320L, result.Summary.KnownNetClp);
        Assert.AreEqual(888L, result.Summary.RejectedRequestedClp);
    }

    [TestMethod]
    public void RejectedMissingFinancialId_IsClassifiedBeforeAcceptedRequirements()
    {
        using var workbook = GetnetTestWorkbook.Modern(new[]
        {
            GetnetTestWorkbook.ModernRow("-", "Rechazada", "Venta", "888", "", "", "", "")
        });
        var analysis = new HistoricalSalesImportAnalyzer().Analyze(new[] { workbook.Path }, Approved());
        Assert.IsFalse(analysis.HasErrors);
        Assert.AreEqual(0, analysis.AcceptedTransactions.Count);
        Assert.AreEqual(1, analysis.RejectedRows.Count);
        Assert.AreEqual(888L, analysis.Summary.RejectedRequestedClp);
        Assert.AreEqual(0L, analysis.Summary.GrossClp);
    }

    [TestMethod]
    [DataRow("USD")]
    [DataRow("")]
    public void RejectedRequestsWithUnprovenCurrencyNeverContributeToClpDiagnostics(string currency)
    {
        var row = GetnetTestWorkbook.ModernRow("-", "Rechazada", "Venta", "888", "", "", "", "");
        row["MONEDA"] = currency;
        using var workbook = GetnetTestWorkbook.Modern(new[] { row });
        var analysis = new HistoricalSalesImportAnalyzer().Analyze(new[] { workbook.Path }, Approved());
        Assert.IsTrue(analysis.HasErrors);
        Assert.IsTrue(analysis.Diagnostics.Any(d => d.Code == "getnet_currency_unsupported" && d.Field == "MONEDA"));
        Assert.IsFalse(analysis.Diagnostics.Any(d => d.Code.Contains("identifier", StringComparison.Ordinal)));
        Assert.AreEqual(0, analysis.AcceptedTransactions.Count);
        Assert.AreEqual(0, analysis.Summary.KnownRejectedRequestedCount);
        Assert.AreEqual(0L, analysis.Summary.RejectedRequestedClp);
    }

    [TestMethod]
    public void SaleAndCancellation_AreTwoSignedMovementsWithoutAuthorizationHeuristics()
    {
        var sale = GetnetTestWorkbook.ModernRow("SYN-EVENT-001", "Confirmada", "Venta", "900", "900", "-15", "-3", "882");
        var cancellation = GetnetTestWorkbook.ModernRow("SYN-EVENT-002", "Confirmada", "Anulación", "-900", "-900", "15", "3", "-882");
        sale["COD.AUT"] = cancellation["COD.AUT"] = "000AB7";
        using var workbook = GetnetTestWorkbook.Modern(new[] { sale, cancellation });
        var result = new HistoricalSalesImportAnalyzer().Analyze(new[] { workbook.Path }, Approved());
        Assert.IsTrue(result.CanApply, string.Join(",", result.Diagnostics.Select(x => x.Code)));
        Assert.AreEqual(2, result.AcceptedTransactions.Count);
        Assert.AreEqual(1, result.Summary.CancellationCount);
        Assert.AreEqual(0L, result.Summary.GrossClp);
        Assert.AreEqual(0L, result.Summary.KnownNetClp);
        Assert.AreEqual("000AB7", result.AcceptedTransactions[0].AuthorizationCode);
    }

    [TestMethod]
    public void IdenticalTimeAndAmount_DistinctStringIdsStayDistinct()
    {
        using var workbook = GetnetTestWorkbook.Legacy(new[]
        {
            GetnetTestWorkbook.LegacyRow("5000000001", "1200", "20", "1180"),
            GetnetTestWorkbook.LegacyRow("5000000002", "1200", "20", "1180")
        }, reverseColumns: true);
        var result = new HistoricalSalesImportAnalyzer().Analyze(new[] { workbook.Path }, Approved());
        Assert.IsTrue(result.CanApply);
        Assert.AreEqual(2, result.AcceptedTransactions.Count);
        Assert.AreEqual("5000000001", result.AcceptedTransactions[0].ExternalId);
        Assert.AreEqual("5000000002", result.AcceptedTransactions[1].ExternalId);
        Assert.AreEqual(2400L, result.Summary.GrossClp);
    }

    [TestMethod]
    public void UnresolvedTime_HasTwoCandidatesAndBlocksApplicationWithoutLosingEconomics()
    {
        using var workbook = GetnetTestWorkbook.Modern(new[] { GetnetTestWorkbook.ModernRow() });
        var result = new HistoricalSalesImportAnalyzer().Analyze(new[] { workbook.Path });
        Assert.IsFalse(result.HasErrors);
        Assert.IsTrue(result.NeedsEvidence);
        Assert.IsFalse(result.CanApply);
        var timestamp = result.AcceptedTransactions.Single().Timestamp;
        Assert.IsNull(timestamp.BusinessDateTime);
        Assert.IsNull(timestamp.OccurredAtUtc);
        Assert.AreEqual(new DateTime(2024, 1, 31, 16, 12, 13).AddTicks(4567000), timestamp.IsoBusinessDateTime);
        Assert.AreEqual(new DateTime(2024, 1, 31, 20, 12, 13).AddTicks(4567000), timestamp.ExportedLocalDateTime);
        Assert.AreEqual(1450L, result.Summary.GrossClp);
        Assert.IsNull(result.Summary.FromBusinessDateTime);
        Assert.IsNotNull(result.Summary.FromIsoCandidateDateTime);
        Assert.IsNotNull(result.Summary.FromExportedLocalCandidateDateTime);
    }

    [TestMethod]
    public void ConfirmedUnsettledSale_ContributesGrossAndUnknownNetCoverage()
    {
        var row = GetnetTestWorkbook.ModernRow();
        row["ESTADO DEL ABONO"] = "Pendiente";
        row["COMISION"] = row["IVA COMISION"] = row["VALOR NETO"] = "";
        row["PREPAGO"] = "Si"; row["TIPO"] = "Débito";
        using var workbook = GetnetTestWorkbook.Modern(new[] { row });
        var result = new HistoricalSalesImportAnalyzer().Analyze(new[] { workbook.Path }, Approved());
        Assert.IsTrue(result.CanApply);
        Assert.AreEqual(1450L, result.Summary.GrossClp);
        Assert.AreEqual(0, result.Summary.KnownNetCount);
        Assert.IsFalse(result.Summary.NetCoverageComplete);
        Assert.IsNull(result.AcceptedTransactions.Single().NetClp);
        Assert.AreEqual(HistoricalSalesCardSubtype.Prepaid, result.AcceptedTransactions.Single().CardSubtype);
    }

    [TestMethod]
    public void DuplicateAndMissingHeaders_AreExplicitBlockingDiagnostics()
    {
        using var duplicate = GetnetTestWorkbook.Legacy(new[] { GetnetTestWorkbook.LegacyRow() }, extraHeader: "  comisión  ");
        var result = new HistoricalSalesImportAnalyzer().Analyze(new[] { duplicate.Path }, Approved());
        Assert.IsTrue(result.Diagnostics.Any(x => x.Code == "getnet_header_duplicate" && x.Field == "COMISION"));
        using var missing = GetnetTestWorkbook.Legacy(new[] { GetnetTestWorkbook.LegacyRow() }, omitHeader: "ID TRANSACCIÓN");
        result = new HistoricalSalesImportAnalyzer().Analyze(new[] { missing.Path }, Approved());
        Assert.IsTrue(result.Diagnostics.Any(x => x.Code == "getnet_header_missing" && x.Field == "ID TRANSACCION"));
        Assert.IsFalse(result.CanApply);
    }

    [TestMethod]
    [DataRow("PROPINA", "1", "getnet_tip_or_cashback_unsupported")]
    [DataRow("MONEDA", "USD", "getnet_currency_unsupported")]
    [DataRow("ESTADO TRX", "Desconocida", "getnet_transaction_state_unknown")]
    [DataRow("VALOR VENTA", "2", "getnet_amount_alias_mismatch")]
    [DataRow("COMISION", "25", "getnet_fee_sign_invalid")]
    [DataRow("VALOR NETO", "1", "getnet_financial_reconciliation_mismatch")]
    public void EconomicAnomalies_BlockTheWholeSelection(string field, string value, string expectedCode)
    {
        var row = GetnetTestWorkbook.ModernRow(); row[field] = value;
        using var workbook = GetnetTestWorkbook.Modern(new[] { row });
        var result = new HistoricalSalesImportAnalyzer().Analyze(new[] { workbook.Path }, Approved());
        Assert.IsFalse(result.CanApply);
        Assert.IsTrue(result.Diagnostics.Any(x => x.Code == expectedCode && x.Source.RowNumber == 2));
    }

    [TestMethod]
    public void FormulaAndExternalRelationship_AreRejectedWithoutEvaluation()
    {
        using var formula = GetnetTestWorkbook.Legacy(new[] { GetnetTestWorkbook.LegacyRow() }, formula: true);
        var result = new HistoricalSalesImportAnalyzer().Analyze(new[] { formula.Path }, Approved());
        Assert.IsTrue(result.Diagnostics.Any(x => x.Code == "getnet_formula_not_allowed"));
        using var external = GetnetTestWorkbook.Legacy(new[] { GetnetTestWorkbook.LegacyRow() }, external: true);
        result = new HistoricalSalesImportAnalyzer().Analyze(new[] { external.Path }, Approved());
        Assert.IsTrue(result.Diagnostics.Any(x => x.Code == "getnet_external_or_active_content"));
    }

    [TestMethod]
    public void CancellationAndFileSizeLimit_ExitWithoutPartialSuccess()
    {
        using var workbook = GetnetTestWorkbook.Legacy(new[] { GetnetTestWorkbook.LegacyRow() });
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => new HistoricalSalesImportAnalyzer().Analyze(new[] { workbook.Path }, Approved(), cancellation.Token));
        using (var stream = new FileStream(workbook.Path, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.SetLength(GetnetExcelImportLimits.MaximumFileBytes + 1);
        var result = new HistoricalSalesImportAnalyzer().Analyze(new[] { workbook.Path }, Approved());
        Assert.IsTrue(result.Diagnostics.Any(x => x.Code == "getnet_file_limit"));
        Assert.AreEqual(0, result.AcceptedTransactions.Count);
    }
}

internal sealed class GetnetTestWorkbook : IDisposable
{
    private GetnetTestWorkbook(string path) => Path = path;
    public string Path { get; }
    public static Dictionary<string, string> LegacyRow(string id = "SYN-L-000001", string gross = "1200", string fee = "20", string net = "1180") => new()
    { ["FECHA VENTA"] = "01/02/2024 11:12:13", ["ID TRANSACCIÓN"] = id, ["VALOR TRANSACCIÓN"] = gross, ["ESTADO"] = "Abonado", ["LOCAL"] = "Synthetic Merchant", ["COMISIÓN"] = fee, ["MONTO ABONO"] = net, ["COD.AUT"] = "000AB7", ["TIPO"] = "Crédito", ["PROPINA"] = "0", ["VUELTO"] = "0" };
    public static Dictionary<string, string> ModernRow(string id = "SYN-M-000001", string status = "Confirmada", string kind = "Venta", string amount = "1450", string gross = "1450", string fee = "-25", string vat = "-5", string net = "1420") => new()
    { ["FECHA DE VENTA"] = "2024-01-31T20:12:13.4567Z", ["ID FINANCIERO DEL MOVIMIENTO"] = id, ["VALOR TRANSACCION"] = amount, ["VALOR VENTA"] = amount, ["MONTO BRUTO"] = gross, ["ESTADO TRX"] = status, ["TIPO MOV."] = kind, ["MONEDA"] = "CLP", ["ZONA HORARIA (UTC)"] = "-4", ["VENDEDOR"] = "Synthetic Merchant", ["COMISION"] = fee, ["IVA COMISION"] = vat, ["VALOR NETO"] = net, ["ESTADO DEL ABONO"] = "Abonado", ["TIPO"] = "Débito", ["PREPAGO"] = "No", ["COD.AUT"] = "000AB7", ["PROPINA"] = "0" };
    public static GetnetTestWorkbook Legacy(IEnumerable<Dictionary<string, string>> rows, bool reverseColumns = false, string? extraHeader = null, string? omitHeader = null, bool formula = false, bool external = false) => Write(rows, reverseColumns, extraHeader, omitHeader, formula, external);
    public static GetnetTestWorkbook Modern(IEnumerable<Dictionary<string, string>> rows) => Write(rows, true, null, null, false, false);
    private static GetnetTestWorkbook Write(IEnumerable<Dictionary<string, string>> source, bool reverse, string? extra, string? omit, bool formula, bool external)
    {
        var rows = source.ToArray(); var headers = rows.First().Keys.Where(x => x != omit).ToList();
        if (reverse) headers.Reverse(); if (extra != null) headers.Add(extra);
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GetnetSynthetic-" + Guid.NewGuid().ToString("N") + ".xlsx");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Part(string name, string content) { using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(content); }
            Part("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Part("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Synthetic\" sheetId=\"1\" r:id=\"s1\"/></sheets></workbook>");
            Part("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"s1\" Target=\"worksheets/sheet1.xml\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\"/>" + (external ? "<Relationship Id=\"external\" Target=\"https://example.invalid/never-open\" TargetMode=\"External\"/>" : "") + "</Relationships>");
            var xml = new StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            void Row(int index, IEnumerable<string> values)
            {
                xml.Append("<row r=\"").Append(index).Append("\">"); var column = 0;
                foreach (var value in values)
                {
                    column++; var letters = string.Empty; for (var c = column; c > 0; c = (c - 1) / 26) letters = (char)('A' + (c - 1) % 26) + letters;
                    xml.Append("<c r=\"").Append(letters).Append(index).Append("\" t=\"inlineStr\">");
                    if (formula && index == 2 && column == 1) xml.Append("<f>1+1</f>");
                    xml.Append("<is><t>").Append(SecurityElement.Escape(value)).Append("</t></is></c>");
                }
                xml.Append("</row>");
            }
            Row(1, headers); for (var index = 0; index < rows.Length; index++) Row(index + 2, headers.Select(x => rows[index].GetValueOrDefault(x, "")));
            xml.Append("</sheetData></worksheet>"); Part("xl/worksheets/sheet1.xml", xml.ToString());
        }
        return new GetnetTestWorkbook(path);
    }
    public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }
}
