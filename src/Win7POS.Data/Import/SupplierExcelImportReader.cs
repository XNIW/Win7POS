using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using ClosedXML.Excel;
using ExcelDataReader;
using ExcelNumberFormat;
using Win7POS.Core.Import;

namespace Win7POS.Data.Import
{
    public static class SupplierExcelImportReader
    {
        private static readonly Regex HtmlTableRegex = new Regex(
            @"<table\b[^>]*>(.*?)</table>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex HtmlRowRegex = new Regex(
            @"<tr\b[^>]*>(.*?)</tr>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex HtmlCellRegex = new Regex(
            @"<t[dh]\b([^>]*)>(.*?)</t[dh]>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        public static SupplierExcelRawTable ReadFirstWorksheet(string filePath)
        {
            return ReadFirstWorksheet(filePath, CancellationToken.None);
        }

        public static SupplierExcelRawTable ReadFirstWorksheet(string filePath, CancellationToken cancellationToken)
        {
            var worksheet = ReadFirstWorksheetData(filePath, cancellationToken);
            return SupplierImportAnalyzer.BuildRawTableFromOwnedRows(
                worksheet.SheetName,
                worksheet.Rows,
                cancellationToken);
        }

        internal static SupplierExcelWorksheetData ReadFirstWorksheetData(string filePath)
        {
            return ReadFirstWorksheetData(filePath, CancellationToken.None);
        }

        internal static SupplierExcelWorksheetData ReadFirstWorksheetData(
            string filePath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File Excel mancante.", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("File Excel non trovato.", filePath);

            cancellationToken.ThrowIfCancellationRequested();
            var kind = DetectWorkbookKind(filePath);
            List<List<string>> rows;
            string sheetName;

            try
            {
                if (kind == WorkbookKind.Html)
                {
                    rows = ReadHtmlTable(filePath, cancellationToken, out sheetName);
                }
                else if (kind == WorkbookKind.Xls)
                {
                    rows = ReadWithExcelDataReader(filePath, cancellationToken, out sheetName);
                }
                else if (kind == WorkbookKind.Xlsx)
                {
                    if (FirstWorksheetContainsFormula(filePath, cancellationToken))
                    {
                        rows = ReadWithClosedXml(filePath, cancellationToken, out sheetName);
                    }
                    else
                    {
                        try
                        {
                            rows = ReadWithExcelDataReader(filePath, cancellationToken, out sheetName);
                        }
                        catch (Exception ex) when (ShouldFallbackToClosedXml(ex))
                        {
                            rows = ReadWithClosedXml(filePath, cancellationToken, out sheetName);
                        }
                    }
                }
                else
                {
                    throw CorruptOrUnsupported();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SupplierExcelImportException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw CorruptOrUnsupported(ex);
            }

            return new SupplierExcelWorksheetData(sheetName, rows);
        }

        internal static string DetectWorkbookKindName(string filePath)
        {
            return DetectWorkbookKind(filePath).ToString().ToLowerInvariant();
        }

        public static int CountWorksheets(string filePath)
        {
            return CountWorksheets(filePath, CancellationToken.None);
        }

        public static int CountWorksheets(string filePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return 0;

            cancellationToken.ThrowIfCancellationRequested();
            var kind = DetectWorkbookKind(filePath);
            try
            {
                if (kind == WorkbookKind.Html)
                    return 1;
                if (kind == WorkbookKind.Xlsx)
                {
                    try
                    {
                        return CountWithExcelDataReader(filePath, cancellationToken);
                    }
                    catch (Exception ex) when (ShouldFallbackToClosedXml(ex))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using (var stream = OpenRead(filePath))
                        using (var workbook = new XLWorkbook(stream))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            return workbook.Worksheets.Count;
                        }
                    }
                }
                if (kind == WorkbookKind.Xls)
                {
                    return CountWithExcelDataReader(filePath, cancellationToken);
                }
                return 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SupplierExcelImportException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw CorruptOrUnsupported(ex);
            }
        }

        public static bool IsSupportedWorkbookFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;
            return DetectWorkbookKind(filePath) != WorkbookKind.Unsupported;
        }

        private static bool ShouldFallbackToClosedXml(Exception ex)
        {
            if (ex is OperationCanceledException || ex is SupplierExcelImportException)
                return false;
            return ex is ArgumentException ||
                ex is InvalidDataException ||
                ex is InvalidOperationException ||
                ex is NotSupportedException ||
                ex.GetType().FullName.IndexOf("ExcelDataReader", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool FirstWorksheetContainsFormula(string filePath, CancellationToken cancellationToken)
        {
            try
            {
                using (var stream = OpenRead(filePath))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
                {
                    var workbookEntry = archive.GetEntry("xl/workbook.xml");
                    var relationshipsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
                    if (workbookEntry == null || relationshipsEntry == null)
                        return false;
                    ValidateOoxmlMetadataEntry(workbookEntry);
                    ValidateOoxmlMetadataEntry(relationshipsEntry);

                    var relationshipId = ReadFirstWorksheetRelationshipId(workbookEntry, cancellationToken);
                    if (string.IsNullOrWhiteSpace(relationshipId))
                        return false;
                    var target = ReadRelationshipTarget(relationshipsEntry, relationshipId, cancellationToken);
                    if (string.IsNullOrWhiteSpace(target))
                        return false;

                    var baseUri = new Uri("http://win7pos.local/xl/workbook.xml", UriKind.Absolute);
                    var worksheetUri = new Uri(baseUri, target.Replace('\\', '/'));
                    var worksheetPath = Uri.UnescapeDataString(worksheetUri.AbsolutePath.TrimStart('/'));
                    var worksheetEntry = archive.GetEntry(worksheetPath);
                    if (worksheetEntry == null)
                        return false;
                    if (worksheetEntry.Length > SupplierExcelImportLimits.MaximumFirstWorksheetXmlBytes)
                        throw CellLimitExceeded();

                    using (var worksheet = worksheetEntry.Open())
                    using (var xml = XmlReader.Create(
                        worksheet,
                        SafeXmlReaderSettings(SupplierExcelImportLimits.MaximumFirstWorksheetXmlBytes)))
                    {
                        var nodes = 0;
                        while (xml.Read())
                        {
                            if ((++nodes & 4095) == 0)
                                cancellationToken.ThrowIfCancellationRequested();
                            if (xml.NodeType == XmlNodeType.Element &&
                                string.Equals(xml.LocalName, "f", StringComparison.Ordinal))
                                return true;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SupplierExcelImportException)
            {
                throw;
            }
            catch (Exception)
            {
                // Compatibility probing must not prevent ExcelDataReader from attempting the file.
            }
            return false;
        }

        private static string ReadFirstWorksheetRelationshipId(
            ZipArchiveEntry workbookEntry,
            CancellationToken cancellationToken)
        {
            using (var stream = workbookEntry.Open())
            using (var xml = XmlReader.Create(
                stream,
                SafeXmlReaderSettings(SupplierExcelImportLimits.MaximumOoxmlMetadataXmlBytes)))
            {
                while (xml.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (xml.NodeType != XmlNodeType.Element || !string.Equals(xml.LocalName, "sheet", StringComparison.Ordinal))
                        continue;
                    if (!xml.HasAttributes)
                        return string.Empty;
                    while (xml.MoveToNextAttribute())
                    {
                        if (string.Equals(xml.LocalName, "id", StringComparison.Ordinal) &&
                            xml.NamespaceURI.IndexOf("relationships", StringComparison.OrdinalIgnoreCase) >= 0)
                            return xml.Value ?? string.Empty;
                    }
                    return string.Empty;
                }
            }
            return string.Empty;
        }

        private static string ReadRelationshipTarget(
            ZipArchiveEntry relationshipsEntry,
            string relationshipId,
            CancellationToken cancellationToken)
        {
            using (var stream = relationshipsEntry.Open())
            using (var xml = XmlReader.Create(
                stream,
                SafeXmlReaderSettings(SupplierExcelImportLimits.MaximumOoxmlMetadataXmlBytes)))
            {
                while (xml.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (xml.NodeType != XmlNodeType.Element || !string.Equals(xml.LocalName, "Relationship", StringComparison.Ordinal))
                        continue;
                    if (!string.Equals(xml.GetAttribute("Id"), relationshipId, StringComparison.Ordinal))
                        continue;
                    return xml.GetAttribute("Target") ?? string.Empty;
                }
            }
            return string.Empty;
        }

        private static void ValidateOoxmlMetadataEntry(ZipArchiveEntry entry)
        {
            if (entry.Length > SupplierExcelImportLimits.MaximumOoxmlMetadataXmlBytes)
                throw CellLimitExceeded();
        }

        private static XmlReaderSettings SafeXmlReaderSettings(long maximumCharactersInDocument)
        {
            return new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                MaxCharactersInDocument = maximumCharactersInDocument,
                XmlResolver = null
            };
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
            using (var stream = OpenRead(filePath))
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

        private static int CountWithExcelDataReader(string filePath, CancellationToken cancellationToken)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            using (var stream = OpenRead(filePath))
            using (var reader = ExcelReaderFactory.CreateReader(stream))
            {
                var count = 0;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    count++;
                }
                while (reader.NextResult());
                return count;
            }
        }

        private static Stream OpenRead(string filePath)
        {
            var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    4096,
                    FileOptions.SequentialScan);
            try
            {
                var length = stream.Length;
                if (length > SupplierExcelImportLimits.MaximumInputFileBytes)
                    throw FileTooLarge();
                return new LengthBoundReadStream(stream, length);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private static bool LooksLikeExcelHtml(string filePath)
        {
            var buffer = new byte[4096];
            int read;
            using (var stream = OpenRead(filePath))
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

        private static List<List<string>> ReadHtmlTable(
            string filePath,
            CancellationToken cancellationToken,
            out string sheetName)
        {
            sheetName = Path.GetFileNameWithoutExtension(filePath) ?? string.Empty;
            var htmlBuilder = new StringBuilder();
            using (var stream = OpenRead(filePath))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false))
            {
                var buffer = new char[8192];
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    htmlBuilder.Append(buffer, 0, read);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var html = htmlBuilder.ToString();

            var tableMatch = SelectBestHtmlTable(html, cancellationToken);
            var tableStart = tableMatch == null ? 0 : tableMatch.Index;
            var tableEnd = tableMatch == null ? html.Length : tableMatch.Index + tableMatch.Length;
            var rows = new List<List<string>>();
            var carry = new Dictionary<int, HtmlCarry>();
            var limits = new WorksheetLimitTracker();

            var rowMatch = HtmlRowRegex.Match(html, tableStart);
            while (rowMatch.Success && rowMatch.Index + rowMatch.Length <= tableEnd)
            {
                var row = new List<string>();
                var col = 0;

                FillCarriedCells(row, carry, ref col);

                var rowContent = rowMatch.Groups[1];
                var rowContentEnd = rowContent.Index + rowContent.Length;
                var cellMatch = HtmlCellRegex.Match(html, rowContent.Index);
                while (cellMatch.Success && cellMatch.Index + cellMatch.Length <= rowContentEnd)
                {
                    FillCarriedCells(row, carry, ref col);

                    var attrs = cellMatch.Groups[1].Value;
                    var text = HtmlCellText(cellMatch.Groups[2].Value);
                    var colSpan = ParsePositiveInt(HtmlAttribute(attrs, "colspan"), 1);
                    var rowSpan = ParsePositiveInt(HtmlAttribute(attrs, "rowspan"), 1);
                    if (col > SupplierExcelImportLimits.MaximumWorksheetColumns - colSpan)
                        throw ColumnLimitExceeded();

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
                    cellMatch = cellMatch.NextMatch();
                }

                FillCarriedCells(row, carry, ref col);
                limits.AddRow(row);
                rows.Add(row);
                if (rows.Count % SupplierExcelImportLimits.CancellationCheckRowInterval == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                rowMatch = rowMatch.NextMatch();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return NormalizeRowsInPlace(rows);
        }

        private static Match SelectBestHtmlTable(string html, CancellationToken cancellationToken)
        {
            Match best = null;
            long bestScore = long.MinValue;
            var candidates = 0;
            var current = HtmlTableRegex.Match(html ?? string.Empty);
            while (current.Success)
            {
                candidates++;
                if (candidates > SupplierExcelImportLimits.MaximumHtmlTableCandidates)
                    throw CellLimitExceeded();
                if ((candidates % SupplierExcelImportLimits.CancellationCheckRowInterval) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                var end = current.Index + current.Length;
                var rows = CountHtmlMatches(HtmlRowRegex, html, current.Index, end, cancellationToken);
                var cells = CountHtmlMatches(HtmlCellRegex, html, current.Index, end, cancellationToken);
                var score = (long)rows * Math.Max(1, cells);
                if (best == null || score > bestScore)
                {
                    best = current;
                    bestScore = score;
                }
                current = current.NextMatch();
            }
            cancellationToken.ThrowIfCancellationRequested();
            return best;
        }

        private static int CountHtmlMatches(
            Regex regex,
            string value,
            int start,
            int end,
            CancellationToken cancellationToken)
        {
            var count = 0;
            if (string.IsNullOrEmpty(value) || start >= end)
                return count;
            cancellationToken.ThrowIfCancellationRequested();
            var match = regex.Match(value, start, end - start);
            while (match.Success && match.Index + match.Length <= end)
            {
                count++;
                if ((count % SupplierExcelImportLimits.CancellationCheckRowInterval) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                match = match.NextMatch();
            }
            return count;
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

        private static List<List<string>> ReadWithClosedXml(
            string filePath,
            CancellationToken cancellationToken,
            out string sheetName)
        {
            using (var stream = OpenRead(filePath))
            using (var workbook = new XLWorkbook(stream))
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                var limits = new WorksheetLimitTracker();
                limits.ValidateDimensions(lastRow - firstRow + 1, lastCol - firstCol + 1);

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

                    limits.AddRow(values);
                    result.Add(values);
                    if (result.Count % SupplierExcelImportLimits.CancellationCheckRowInterval == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                }

                cancellationToken.ThrowIfCancellationRequested();
                return NormalizeRowsInPlace(result);
            }
        }

        private static List<List<string>> ReadWithExcelDataReader(
            string filePath,
            CancellationToken cancellationToken,
            out string sheetName)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            using (var stream = OpenRead(filePath))
            using (var reader = ExcelReaderFactory.CreateReader(stream))
            {
                sheetName = reader.Name ?? string.Empty;
                var rows = new List<List<string>>();
                var formats = new Dictionary<string, NumberFormat>(StringComparer.Ordinal);
                var limits = new WorksheetLimitTracker();
                limits.ValidateDimensions(reader.RowCount, reader.FieldCount);
                while (reader.Read())
                {
                    if (rows.Count % SupplierExcelImportLimits.CancellationCheckRowInterval == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    var fieldCount = reader.FieldCount;
                    if (fieldCount > SupplierExcelImportLimits.MaximumWorksheetColumns)
                        throw ColumnLimitExceeded();
                    var values = new List<string>(fieldCount);
                    for (var column = 0; column < fieldCount; column++)
                        values.Add(CellToString(reader, column, formats));
                    limits.AddRow(values);
                    rows.Add(values);
                }
                cancellationToken.ThrowIfCancellationRequested();
                return NormalizeRowsInPlace(rows);
            }
        }

        private static string CellToString(
            IExcelDataReader reader,
            int column,
            IDictionary<string, NumberFormat> formats)
        {
            var cell = reader.GetValue(column);
            if (cell == null || cell == DBNull.Value) return string.Empty;
            if (cell is string) return ((string)cell).Trim();
            if (cell is bool) return (bool)cell ? "TRUE" : "FALSE";

            var formatString = reader.GetNumberFormatString(column);
            if (!string.IsNullOrWhiteSpace(formatString))
            {
                try
                {
                    NumberFormat format;
                    if (!formats.TryGetValue(formatString, out format))
                    {
                        format = new NumberFormat(formatString);
                        formats[formatString] = format;
                    }
                    if (format.IsValid)
                        return (format.Format(cell, CultureInfo.CurrentCulture, false) ?? string.Empty).Trim();
                }
                catch (Exception)
                {
                    // Preserve the previous invariant conversion when a vendor format is invalid.
                }
            }

            return CellToString(cell);
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

        private static List<List<string>> NormalizeRowsInPlace(List<List<string>> rows)
        {
            rows = rows ?? new List<List<string>>();
            for (var rowIndex = rows.Count - 1; rowIndex >= 0; rowIndex--)
            {
                var row = rows[rowIndex] ?? new List<string>();
                rows[rowIndex] = row;
                for (var column = 0; column < row.Count; column++)
                    row[column] = (row[column] ?? string.Empty).Trim();
                while (row.Count > 0 && string.IsNullOrWhiteSpace(row[row.Count - 1]))
                    row.RemoveAt(row.Count - 1);
                if (!row.Any(value => !string.IsNullOrWhiteSpace(value)))
                    rows.RemoveAt(rowIndex);
            }

            if (rows.Count == 0)
                return rows;

            var firstUsedColumn = rows
                .Select(row => row.FindIndex(value => !string.IsNullOrWhiteSpace(value)))
                .Where(index => index >= 0)
                .DefaultIfEmpty(0)
                .Min();
            if (firstUsedColumn > 0)
            {
                foreach (var row in rows)
                {
                    var remove = Math.Min(firstUsedColumn, row.Count);
                    if (remove > 0)
                        row.RemoveRange(0, remove);
                    while (row.Count > 0 && string.IsNullOrWhiteSpace(row[row.Count - 1]))
                        row.RemoveAt(row.Count - 1);
                }
            }
            return rows;
        }

        private static SupplierExcelImportException CorruptOrUnsupported(Exception innerException = null)
        {
            const string message = "Il file Excel non e supportato oppure e danneggiato.";
            return innerException == null
                ? new SupplierExcelImportException(SupplierExcelImportErrorCodes.CorruptOrUnsupported, message)
                : new SupplierExcelImportException(SupplierExcelImportErrorCodes.CorruptOrUnsupported, message, innerException);
        }

        private static SupplierExcelImportException FileTooLarge()
        {
            return new SupplierExcelImportException(
                SupplierExcelImportErrorCodes.FileTooLarge,
                "Il file Excel supera il limite di dimensione supportato.");
        }

        private static SupplierExcelImportException ColumnLimitExceeded()
        {
            return new SupplierExcelImportException(
                SupplierExcelImportErrorCodes.ColumnLimitExceeded,
                "Il foglio Excel supera il numero massimo di colonne supportato.");
        }

        private static SupplierExcelImportException RowLimitExceeded()
        {
            return new SupplierExcelImportException(
                SupplierExcelImportErrorCodes.RowLimitExceeded,
                "Il foglio Excel supera il numero massimo di righe supportato.");
        }

        private static SupplierExcelImportException CellLimitExceeded()
        {
            return new SupplierExcelImportException(
                SupplierExcelImportErrorCodes.CellLimitExceeded,
                "Il foglio Excel supera il limite complessivo di celle o testo supportato.");
        }

        private static SupplierExcelImportException CellTextTooLarge()
        {
            return new SupplierExcelImportException(
                SupplierExcelImportErrorCodes.CellTextTooLarge,
                "Una cella Excel supera la lunghezza massima supportata.");
        }

        private sealed class WorksheetLimitTracker
        {
            private long _aggregateCharacters;
            private long _cells;
            private int _rows;

            internal void ValidateDimensions(int rows, int columns)
            {
                if (rows > SupplierExcelImportLimits.MaximumWorksheetRows)
                    throw RowLimitExceeded();
                if (columns > SupplierExcelImportLimits.MaximumWorksheetColumns)
                    throw ColumnLimitExceeded();
                if (rows > 0 && columns > 0 &&
                    (long)rows * columns > SupplierExcelImportLimits.MaximumWorksheetCells)
                    throw CellLimitExceeded();
            }

            internal void AddRow(IReadOnlyList<string> row)
            {
                _rows++;
                if (_rows > SupplierExcelImportLimits.MaximumWorksheetRows)
                    throw RowLimitExceeded();
                var columns = row == null ? 0 : row.Count;
                if (columns > SupplierExcelImportLimits.MaximumWorksheetColumns)
                    throw ColumnLimitExceeded();
                _cells += columns;
                if (_cells > SupplierExcelImportLimits.MaximumWorksheetCells)
                    throw CellLimitExceeded();
                if (row == null)
                    return;
                for (var column = 0; column < row.Count; column++)
                {
                    var value = row[column] ?? string.Empty;
                    if (value.Length > SupplierExcelImportLimits.MaximumCellCharacters)
                        throw CellTextTooLarge();
                    _aggregateCharacters += value.Length;
                    if (_aggregateCharacters > SupplierExcelImportLimits.MaximumAggregateRetainedCharacters)
                        throw CellLimitExceeded();
                }
            }
        }

        private sealed class LengthBoundReadStream : Stream
        {
            private readonly FileStream _inner;
            private readonly long _length;

            internal LengthBoundReadStream(FileStream inner, long length)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _length = length;
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => false;
            public override long Length => _length;

            public override long Position
            {
                get => _inner.Position;
                set
                {
                    if (value < 0 || value > _length)
                        throw new IOException("Posizione di lettura non valida.");
                    _inner.Position = value;
                }
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var remaining = _length - _inner.Position;
                if (remaining <= 0)
                    return 0;
                return _inner.Read(buffer, offset, (int)Math.Min(count, remaining));
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                long target;
                switch (origin)
                {
                    case SeekOrigin.Begin:
                        target = offset;
                        break;
                    case SeekOrigin.Current:
                        target = checked(_inner.Position + offset);
                        break;
                    case SeekOrigin.End:
                        target = checked(_length + offset);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(origin));
                }
                Position = target;
                return target;
            }

            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _inner.Dispose();
                base.Dispose(disposing);
            }
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

    internal sealed class SupplierExcelWorksheetData
    {
        internal SupplierExcelWorksheetData(
            string sheetName,
            List<List<string>> rows)
        {
            SheetName = sheetName ?? string.Empty;
            Rows = rows ?? new List<List<string>>();
        }

        internal string SheetName { get; }
        internal List<List<string>> Rows { get; }
    }
}
