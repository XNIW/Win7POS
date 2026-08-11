using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Data.Import;

namespace Win7POS.Core.Tests.Import;

[TestClass]
[DoNotParallelize]
public sealed class SupplierExcelImportReaderBoundsTests
{
    [TestMethod]
    public void CountWorksheets_LargeSecondSheet_DoesNotApplyCellMaterializationLimits()
    {
        var path = TempPath(".xlsx");
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var first = workbook.Worksheets.Add("First");
                first.Cell(1, 1).Value = AndroidImportKeys.Barcode;
                first.Cell(1, 2).Value = AndroidImportKeys.ProductName;
                first.Cell(1, 3).Value = AndroidImportKeys.PurchasePrice;
                first.Cell(1, 4).Value = AndroidImportKeys.RetailPrice;
                first.Cell(2, 1).Value = "00123456";
                first.Cell(2, 2).Value = "First only";
                first.Cell(2, 3).Value = 100;
                first.Cell(2, 4).Value = 180;

                var second = workbook.Worksheets.Add("Oversized second");
                for (var row = 1; row <= SupplierExcelImportLimits.MaximumWorksheetRows + 1; row++)
                    second.Cell(row, 1).Value = row;
                workbook.SaveAs(path);
            }

            Assert.AreEqual(2, SupplierExcelImportReader.CountWorksheets(path));
            var table = SupplierExcelImportReader.ReadFirstWorksheet(path);
            Assert.AreEqual("First", table.SheetName);
            Assert.AreEqual(1, table.Rows.Count);
            Assert.AreEqual("00123456", table.Rows[0].Values[0]);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void ReadFirstWorksheet_FileSizeLimit_FailsBeforeWorkbookMaterialization()
    {
        var path = TempPath(".xlsx");
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
                stream.SetLength(SupplierExcelImportLimits.MaximumInputFileBytes + 1);

            AssertCode(
                SupplierExcelImportErrorCodes.FileTooLarge,
                () => SupplierExcelImportReader.ReadFirstWorksheet(path));
            AssertCode(
                SupplierExcelImportErrorCodes.FileTooLarge,
                () => SupplierExcelImportReader.CountWorksheets(path));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void ReadFirstWorksheet_OoxmlMetadataExpansionLimit_FailsBeforeInflation()
    {
        var path = TempPath(".xlsx");
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add("First");
                worksheet.Cell(1, 1).Value = AndroidImportKeys.Barcode;
                worksheet.Cell(2, 1).Value = "00123456";
                workbook.SaveAs(path);
            }

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, false))
            {
                var existing = archive.GetEntry("xl/_rels/workbook.xml.rels");
                Assert.IsNotNull(existing);
                existing.Delete();
                var oversized = archive.CreateEntry(
                    "xl/_rels/workbook.xml.rels",
                    CompressionLevel.Optimal);
                using (var output = oversized.Open())
                {
                    var buffer = new byte[8192];
                    for (var index = 0; index < buffer.Length; index++)
                        buffer[index] = (byte)'x';
                    var remaining = SupplierExcelImportLimits.MaximumOoxmlMetadataXmlBytes + 1;
                    while (remaining > 0)
                    {
                        var count = (int)Math.Min(buffer.Length, remaining);
                        output.Write(buffer, 0, count);
                        remaining -= count;
                    }
                }
            }

            AssertCode(
                SupplierExcelImportErrorCodes.CellLimitExceeded,
                () => SupplierExcelImportReader.ReadFirstWorksheet(path));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void ReadFirstWorksheet_HtmlTableCandidateLimit_FailsBeforeGlobalMatchMaterialization()
    {
        var path = TempPath(".xls");
        try
        {
            var html = new StringBuilder("<html>");
            for (var table = 0; table <= SupplierExcelImportLimits.MaximumHtmlTableCandidates; table++)
                html.Append("<table></table>");
            html.Append("</html>");
            File.WriteAllText(path, html.ToString(), new UTF8Encoding(false));

            AssertCode(
                SupplierExcelImportErrorCodes.CellLimitExceeded,
                () => SupplierExcelImportReader.ReadFirstWorksheet(path));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void ReadFirstWorksheet_HtmlCandidateScoring_StaysInsideEachTableSpan()
    {
        var path = TempPath(".xls");
        try
        {
            var html = new StringBuilder("<html>");
            for (var candidate = 1; candidate < SupplierExcelImportLimits.MaximumHtmlTableCandidates; candidate++)
                html.Append("<table></table>");
            html.Append('x', 1024 * 1024);
            html.Append("<table><tr><th>barcode</th><th>productName</th><th>purchasePrice</th><th>retailPrice</th></tr>")
                .Append("<tr><td>00123456</td><td>Bounded</td><td>100</td><td>180</td></tr></table></html>");
            File.WriteAllText(path, html.ToString(), new UTF8Encoding(false));

            var table = SupplierExcelImportReader.ReadFirstWorksheet(path);
            Assert.AreEqual(1, table.Rows.Count);
            Assert.AreEqual("00123456", table.Rows[0].Values[0]);
            Assert.AreEqual("Bounded", table.Rows[0].Values[1]);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    [DataRow("rows", SupplierExcelImportErrorCodes.RowLimitExceeded)]
    [DataRow("columns", SupplierExcelImportErrorCodes.ColumnLimitExceeded)]
    [DataRow("cells", SupplierExcelImportErrorCodes.CellLimitExceeded)]
    [DataRow("cell_text", SupplierExcelImportErrorCodes.CellTextTooLarge)]
    [DataRow("aggregate_text", SupplierExcelImportErrorCodes.CellLimitExceeded)]
    public void ReadFirstWorksheet_HtmlLimits_ReturnStableCodes(string scenario, string expectedCode)
    {
        var path = TempPath(".xls");
        try
        {
            File.WriteAllText(path, BuildLimitHtml(scenario), new UTF8Encoding(false));
            AssertCode(expectedCode, () => SupplierExcelImportReader.ReadFirstWorksheet(path));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void ReadFirstWorksheet_Cancellation_ReleasesFileAndAllowsNextAnalysis()
    {
        var path = TempPath(".xls");
        var movedPath = path + ".moved.xls";
        var nextPath = TempPath(".xls");
        try
        {
            File.WriteAllText(path, BuildCancellableHtml(50000), new UTF8Encoding(false));
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.CancelAfter(1);
                Assert.ThrowsExactly<OperationCanceledException>(() =>
                    SupplierExcelImportReader.ReadFirstWorksheet(path, cancellation.Token));
            }

            File.Move(path, movedPath);
            File.Delete(movedPath);
            Assert.IsFalse(File.Exists(movedPath), "Cancellation retained an input file handle.");

            File.WriteAllText(
                nextPath,
                "<html><table><tr><th>barcode</th><th>productName</th><th>purchasePrice</th><th>retailPrice</th></tr>" +
                "<tr><td>12345678</td><td>Next Product</td><td>100</td><td>180</td></tr></table></html>",
                new UTF8Encoding(false));
            var next = SupplierExcelImportReader.ReadFirstWorksheet(nextPath);
            Assert.AreEqual(1, next.Rows.Count);
            Assert.AreEqual("Next Product", next.Rows[0].Values[1]);
        }
        finally
        {
            TryDelete(path);
            TryDelete(movedPath);
            TryDelete(nextPath);
        }
    }

    private static string BuildLimitHtml(string scenario)
    {
        var html = new StringBuilder();
        html.Append("<html><table>");
        switch (scenario)
        {
            case "rows":
                for (var row = 0; row <= SupplierExcelImportLimits.MaximumWorksheetRows; row++)
                    html.Append("<tr><td>x</td></tr>");
                break;
            case "columns":
                html.Append("<tr><td colspan=\"")
                    .Append(SupplierExcelImportLimits.MaximumWorksheetColumns + 1)
                    .Append("\">x</td></tr>");
                break;
            case "cells":
                var rows = (int)(SupplierExcelImportLimits.MaximumWorksheetCells /
                    SupplierExcelImportLimits.MaximumWorksheetColumns) + 2;
                for (var row = 0; row < rows; row++)
                    html.Append("<tr><td colspan=\"256\">x</td></tr>");
                break;
            case "cell_text":
                html.Append("<tr><td>")
                    .Append('x', SupplierExcelImportLimits.MaximumCellCharacters + 1)
                    .Append("</td></tr>");
                break;
            case "aggregate_text":
                var value = new string('x', SupplierExcelImportLimits.MaximumCellCharacters);
                var aggregateRows = (int)(SupplierExcelImportLimits.MaximumAggregateRetainedCharacters /
                    SupplierExcelImportLimits.MaximumCellCharacters) + 1;
                for (var row = 0; row < aggregateRows; row++)
                    html.Append("<tr><td>").Append(value).Append("</td></tr>");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        return html.Append("</table></html>").ToString();
    }

    private static string BuildCancellableHtml(int rows)
    {
        var html = new StringBuilder(rows * 128);
        html.Append("<html><table><tr><th>barcode</th><th>productName</th><th>purchasePrice</th></tr>");
        for (var row = 0; row < rows; row++)
        {
            html.Append("<tr><td>")
                .Append((10000000 + row).ToString())
                .Append("</td><td>Product ")
                .Append(row)
                .Append("</td><td>100</td></tr>");
        }
        return html.Append("</table></html>").ToString();
    }

    private static void AssertCode(string expectedCode, Action action)
    {
        var exception = Assert.ThrowsExactly<SupplierExcelImportException>(action);
        Assert.AreEqual(expectedCode, exception.Code);
        Assert.IsFalse(string.IsNullOrWhiteSpace(exception.Message));
    }

    private static string TempPath(string extension) =>
        Path.Combine(Path.GetTempPath(), "win7pos-supplier-bounds-" + Guid.NewGuid().ToString("N") + extension);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
