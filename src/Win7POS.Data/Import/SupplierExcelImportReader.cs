using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using ExcelDataReader;
using Win7POS.Core.Import;

namespace Win7POS.Data.Import
{
    public static class SupplierExcelImportReader
    {
        public static SupplierExcelRawTable ReadFirstWorksheet(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File Excel mancante.", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("File Excel non trovato.", filePath);

            var kind = DetectWorkbookKind(filePath);
            List<List<string>> rows;
            string sheetName;

            if (kind == WorkbookKind.Html)
            {
                rows = ReadHtmlTable(filePath, out sheetName);
            }
            else if (kind == WorkbookKind.Xls)
            {
                rows = ReadWithExcelDataReader(filePath, out sheetName);
            }
            else if (kind == WorkbookKind.Xlsx)
            {
                try
                {
                    rows = ReadWithClosedXml(filePath, out sheetName);
                }
                catch (Exception ex) when (ShouldFallbackToExcelDataReader(ex))
                {
                    rows = ReadWithExcelDataReader(filePath, out sheetName);
                }
            }
            else
            {
                throw new NotSupportedException("Formato non supportato. Usa un file Excel .xls, .xlsx o HTML Excel.");
            }

            return SupplierImportAnalyzer.BuildRawTable(sheetName, rows);
        }

        public static int CountWorksheets(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return 0;

            var kind = DetectWorkbookKind(filePath);
            if (kind == WorkbookKind.Html)
                return 1;
            if (kind == WorkbookKind.Xlsx)
            {
                try
                {
                    using (var workbook = new XLWorkbook(filePath))
                    {
                        return workbook.Worksheets.Count;
                    }
                }
                catch (Exception ex) when (ShouldFallbackToExcelDataReader(ex))
                {
                    return CountWithExcelDataReader(filePath);
                }
            }
            if (kind == WorkbookKind.Xls)
            {
                return CountWithExcelDataReader(filePath);
            }
            return 0;
        }

        public static bool IsSupportedWorkbookFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;
            return DetectWorkbookKind(filePath) != WorkbookKind.Unsupported;
        }

        private static bool ShouldFallbackToExcelDataReader(Exception ex)
        {
            return ex is ArgumentException ||
                ex is InvalidOperationException ||
                ex is NullReferenceException ||
                ex.GetType().FullName.IndexOf("DocumentFormat.OpenXml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                ex.GetType().FullName.IndexOf("ClosedXML", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static WorkbookKind DetectWorkbookKind(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".xlsx")
                return WorkbookKind.Xlsx;
            if (ext == ".xls")
                return LooksLikeExcelHtml(filePath) ? WorkbookKind.Html : WorkbookKind.Xls;
            if (LooksLikeExcelHtml(filePath))
                return WorkbookKind.Html;

            var header = new byte[8];
            int read;
            using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                read = stream.Read(header, 0, header.Length);
            }

            if (read >= 4 && header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04)
                return WorkbookKind.Xlsx;
            if (read >= 8 &&
                header[0] == 0xD0 && header[1] == 0xCF && header[2] == 0x11 && header[3] == 0xE0 &&
                header[4] == 0xA1 && header[5] == 0xB1 && header[6] == 0x1A && header[7] == 0xE1)
                return WorkbookKind.Xls;
            return WorkbookKind.Unsupported;
        }

        private static int CountWithExcelDataReader(string filePath)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = ExcelReaderFactory.CreateReader(stream))
            {
                var conf = new ExcelDataSetConfiguration
                {
                    ConfigureDataTable = _ => new ExcelDataTableConfiguration
                    {
                        UseHeaderRow = false
                    }
                };
                var dataSet = reader.AsDataSet(conf);
                return dataSet == null ? 0 : dataSet.Tables.Count;
            }
        }

        private static bool LooksLikeExcelHtml(string filePath)
        {
            var buffer = new byte[4096];
            int read;
            using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                read = stream.Read(buffer, 0, buffer.Length);
            }

            if (read <= 0) return false;
            var head = Encoding.GetEncoding("ISO-8859-1")
                .GetString(buffer, 0, read)
                .ToLowerInvariant();
            return head.Contains("<html") ||
                head.Contains("mso-application") ||
                head.Contains("office:excel") ||
                head.Contains("<table");
        }

        private static List<List<string>> ReadHtmlTable(string filePath, out string sheetName)
        {
            sheetName = Path.GetFileNameWithoutExtension(filePath) ?? string.Empty;
            string html;
            using (var reader = new StreamReader(filePath, Encoding.UTF8, true))
            {
                html = reader.ReadToEnd();
            }

            var tableMatch = Regex.Matches(
                    html,
                    @"<table\b[^>]*>(.*?)</table>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline)
                .Cast<Match>()
                .OrderByDescending(match =>
                    Regex.Matches(match.Value, @"<tr\b", RegexOptions.IgnoreCase).Count *
                    Math.Max(1, Regex.Matches(match.Value, @"<t[dh]\b", RegexOptions.IgnoreCase).Count))
                .FirstOrDefault();
            var tableHtml = tableMatch == null ? html : tableMatch.Value;
            var rows = new List<List<string>>();
            var carry = new Dictionary<int, HtmlCarry>();

            foreach (Match rowMatch in Regex.Matches(
                tableHtml,
                @"<tr\b[^>]*>(.*?)</tr>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var row = new List<string>();
                var col = 0;

                FillCarriedCells(row, carry, ref col);

                foreach (Match cellMatch in Regex.Matches(
                    rowMatch.Groups[1].Value,
                    @"<t[dh]\b([^>]*)>(.*?)</t[dh]>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline))
                {
                    FillCarriedCells(row, carry, ref col);

                    var attrs = cellMatch.Groups[1].Value;
                    var text = HtmlCellText(cellMatch.Groups[2].Value);
                    var colSpan = ParsePositiveInt(HtmlAttribute(attrs, "colspan"), 1);
                    var rowSpan = ParsePositiveInt(HtmlAttribute(attrs, "rowspan"), 1);

                    for (var offset = 0; offset < colSpan; offset++)
                    {
                        row.Add(text);
                        if (rowSpan > 1)
                        {
                            carry[col + offset] = new HtmlCarry
                            {
                                RemainingRows = rowSpan - 1,
                                Text = text
                            };
                        }
                    }
                    col += colSpan;
                }

                FillCarriedCells(row, carry, ref col);
                rows.Add(row);
            }

            var maxCols = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
            foreach (var row in rows)
            {
                while (row.Count < maxCols) row.Add(string.Empty);
            }
            return NormalizeRows(rows);
        }

        private static void FillCarriedCells(List<string> row, IDictionary<int, HtmlCarry> carry, ref int col)
        {
            while (carry.ContainsKey(col))
            {
                var item = carry[col];
                row.Add(item.Text);
                if (item.RemainingRows <= 1)
                    carry.Remove(col);
                else
                    carry[col] = new HtmlCarry { RemainingRows = item.RemainingRows - 1, Text = item.Text };
                col++;
            }
        }

        private static string HtmlAttribute(string attrs, string name)
        {
            var match = Regex.Match(
                attrs ?? string.Empty,
                @"\b" + Regex.Escape(name) + @"\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success) return string.Empty;
            return match.Groups[1].Success
                ? match.Groups[1].Value
                : match.Groups[2].Success
                    ? match.Groups[2].Value
                    : match.Groups[3].Value;
        }

        private static int ParsePositiveInt(string value, int fallback)
        {
            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0
                ? parsed
                : fallback;
        }

        private static string HtmlCellText(string value)
        {
            var text = Regex.Replace(value ?? string.Empty, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<[^>]+>", " ", RegexOptions.Singleline);
            text = WebUtility.HtmlDecode(text) ?? string.Empty;
            return Regex.Replace(text.Replace('\u00A0', ' '), @"\s+", " ").Trim();
        }

        private static List<List<string>> ReadWithClosedXml(string filePath, out string sheetName)
        {
            using (var workbook = new XLWorkbook(filePath))
            {
                var worksheet = workbook.Worksheets.FirstOrDefault();
                if (worksheet == null)
                {
                    sheetName = string.Empty;
                    return new List<List<string>>();
                }

                sheetName = worksheet.Name ?? string.Empty;
                var used = worksheet.RangeUsed();
                if (used == null) return new List<List<string>>();

                var result = new List<List<string>>();
                var firstRow = used.RangeAddress.FirstAddress.RowNumber;
                var lastRow = used.RangeAddress.LastAddress.RowNumber;
                var firstCol = used.RangeAddress.FirstAddress.ColumnNumber;
                var lastCol = used.RangeAddress.LastAddress.ColumnNumber;

                for (var r = firstRow; r <= lastRow; r++)
                {
                    var values = new List<string>();
                    for (var c = firstCol; c <= lastCol; c++)
                    {
                        var cell = worksheet.Cell(r, c);
                        var text = string.Empty;
                        try
                        {
                            text = cell.GetFormattedString();
                        }
                        catch
                        {
                            text = cell.GetString();
                        }

                        values.Add((text ?? string.Empty).Trim());
                    }

                    result.Add(values);
                }

                return NormalizeRows(result);
            }
        }

        private static List<List<string>> ReadWithExcelDataReader(string filePath, out string sheetName)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = ExcelReaderFactory.CreateReader(stream))
            {
                sheetName = reader.Name ?? string.Empty;
                var conf = new ExcelDataSetConfiguration
                {
                    ConfigureDataTable = _ => new ExcelDataTableConfiguration
                    {
                        UseHeaderRow = false
                    }
                };
                var dataSet = reader.AsDataSet(conf);
                if (dataSet == null || dataSet.Tables.Count == 0)
                    return new List<List<string>>();

                var table = dataSet.Tables[0];
                sheetName = table.TableName ?? sheetName;
                var rows = new List<List<string>>();
                foreach (DataRow row in table.Rows)
                {
                    var values = new List<string>();
                    foreach (var cell in row.ItemArray)
                    {
                        values.Add(CellToString(cell));
                    }
                    rows.Add(values);
                }
                return NormalizeRows(rows);
            }
        }

        private static string CellToString(object cell)
        {
            if (cell == null || cell == DBNull.Value) return string.Empty;
            if (cell is double)
            {
                var d = (double)cell;
                return Math.Abs(d - Math.Round(d)) < 0.0000001
                    ? Math.Round(d).ToString(CultureInfo.InvariantCulture)
                    : d.ToString(CultureInfo.InvariantCulture);
            }
            if (cell is decimal)
            {
                var d = (decimal)cell;
                return d == decimal.Round(d)
                    ? decimal.Round(d).ToString(CultureInfo.InvariantCulture)
                    : d.ToString(CultureInfo.InvariantCulture);
            }
            if (cell is DateTime)
            {
                return ((DateTime)cell).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            return Convert.ToString(cell, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        }

        private static List<List<string>> NormalizeRows(IEnumerable<List<string>> rows)
        {
            return (rows ?? Enumerable.Empty<List<string>>())
                .Select(row => (row ?? new List<string>())
                    .Select(value => (value ?? string.Empty).Trim())
                    .ToList())
                .Select(row =>
                {
                    while (row.Count > 0 && string.IsNullOrWhiteSpace(row[row.Count - 1]))
                        row.RemoveAt(row.Count - 1);
                    return row;
                })
                .Where(row => row.Any(value => !string.IsNullOrWhiteSpace(value)))
                .ToList();
        }

        private sealed class HtmlCarry
        {
            public int RemainingRows { get; set; }
            public string Text { get; set; } = string.Empty;
        }

        private enum WorkbookKind
        {
            Unsupported,
            Xls,
            Xlsx,
            Html
        }
    }
}
