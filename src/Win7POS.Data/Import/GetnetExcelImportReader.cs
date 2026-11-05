using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using Win7POS.Core.Import;

namespace Win7POS.Data.Import
{
    public static class GetnetExcelImportLimits
    {
        public const int MaximumFiles = 32;
        public const long MaximumFileBytes = 32L * 1024 * 1024;
        public const long MaximumSelectionBytes = 128L * 1024 * 1024;
        public const int MaximumWorksheetRows = 60001;
        public const int MaximumSelectionRows = 100000;
        public const int MaximumColumns = 256;
        public const int MaximumWorksheetCells = 1000000;
        public const int MaximumCellCharacters = 32767;
        public const int MaximumSharedStrings = 200000;
        public const long MaximumSharedStringCharacters = 8000000;
        public const int MaximumDiagnostics = 2000;
        public const long MaximumExpandedZipBytes = 256L * 1024 * 1024;
        public const long MaximumWorksheetXmlBytes = 128L * 1024 * 1024;
        public const long MaximumMetadataXmlBytes = 4L * 1024 * 1024;
        public const long MaximumSharedStringsXmlBytes = 32L * 1024 * 1024;
    }

    internal sealed class GetnetImportException : Exception
    {
        internal GetnetImportException(string code, string field = "", int row = 0, int? column = null)
            : base(code) { Code = code; Field = field; Row = row; Column = column; }
        internal string Code { get; }
        internal string Field { get; }
        internal int Row { get; }
        internal int? Column { get; }
    }

    internal sealed class GetnetCell
    {
        internal string Value = string.Empty;
        internal string Type = "n";
        internal string NumberFormat = "General";
        internal bool IsDate;
        internal bool IsNumeric => Type == "n";
    }

    internal sealed class GetnetWorksheetRow
    {
        internal int Number;
        internal bool HasContent;
        internal Dictionary<int, GetnetCell> Cells = new Dictionary<int, GetnetCell>();
    }

    internal sealed class GetnetReadResult
    {
        internal HistoricalSalesImportFile File;
        internal readonly List<HistoricalSalesTransaction> Transactions = new List<HistoricalSalesTransaction>();
        internal readonly List<HistoricalSalesRejectedRow> Rejected = new List<HistoricalSalesRejectedRow>();
        internal readonly List<HistoricalSalesImportDiagnostic> Diagnostics = new List<HistoricalSalesImportDiagnostic>();
    }

    /// <summary>Bounded OOXML reader. Only one physical row and the compact accepted fields are materialized.</summary>
    internal static class GetnetExcelImportReader
    {
        [ThreadStatic] private static char[] _textBuffer;
        private sealed class Sheet { internal string Name; internal string Entry; }
        private sealed class Style { internal string Format = "General"; internal bool Date; }
        private sealed class Candidate
        {
            internal Sheet Sheet;
            internal GetnetHeaderMap Header;
            internal GetnetProfileAdapter Adapter;
        }

        internal static GetnetReadResult Read(string path, string fileId, HistoricalSalesImportOptions options, CancellationToken token)
        {
            if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
                throw new GetnetImportException("getnet_format_unsupported");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
            {
                if (stream.Length > GetnetExcelImportLimits.MaximumFileBytes) throw new GetnetImportException("getnet_file_limit");
                token.ThrowIfCancellationRequested();
                var hash = Hash(stream, token);
                stream.Position = 0;
                var result = new GetnetReadResult();
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, true))
                {
                    ValidateArchive(archive, token);
                    bool date1904;
                    var sheets = ReadSheets(archive, token, out date1904);
                    var strings = ReadSharedStrings(archive, token);
                    var styles = ReadStyles(archive, token);
                    var candidates = new List<Candidate>();
                    foreach (var sheet in sheets)
                    {
                        foreach (var row in ReadRows(archive, sheet.Entry, strings, styles, token))
                        {
                            if (row.Number > 32) break;
                            var header = new GetnetHeaderMap(row);
                            var adapter = GetnetProfileAdapter.Detect(header);
                            if (adapter == null) continue;
                            candidates.Add(new Candidate { Sheet = sheet, Header = header, Adapter = adapter });
                            break;
                        }
                    }
                    if (candidates.Count != 1)
                        throw new GetnetImportException(candidates.Count == 0 ? "getnet_profile_unrecognized" : "getnet_multiple_candidate_sheets");
                    var candidate = candidates[0];
                    candidate.Header.Validate();
                    candidate.Adapter.ValidateHeaders(candidate.Header);
                    var sourceRows = 0;
                    foreach (var row in ReadRows(archive, candidate.Sheet.Entry, strings, styles, token, candidate.Header.RetainedColumns()))
                    {
                        if (row.Number <= candidate.Header.RowNumber || !row.HasContent) continue;
                        token.ThrowIfCancellationRequested();
                        sourceRows++;
                        var source = new HistoricalSalesSource(fileId, candidate.Sheet.Name, row.Number);
                        try
                        {
                            var context = new GetnetRowContext(candidate.Header, row, source, date1904);
                            var transaction = candidate.Adapter.Read(context, options, result.Rejected);
                            if (transaction != null) result.Transactions.Add(transaction);
                        }
                        catch (Exception error) when (error is FormatException || error is OverflowException || error is GetnetImportException || error is ArgumentOutOfRangeException)
                        {
                            var importError = error as GetnetImportException;
                            var code = importError?.Code ?? (error is OverflowException ? "getnet_amount_overflow" :
                                error.Message.StartsWith("getnet_", StringComparison.Ordinal) ? error.Message : "getnet_row_invalid");
                            result.Diagnostics.Add(new HistoricalSalesImportDiagnostic(code, HistoricalSalesDiagnosticSeverity.Error,
                                source, importError?.Field ?? string.Empty, importError?.Column));
                            if (result.Diagnostics.Count >= GetnetExcelImportLimits.MaximumDiagnostics)
                                throw new GetnetImportException("getnet_diagnostic_limit", row: row.Number);
                        }
                    }
                    result.File = new HistoricalSalesImportFile(fileId, path, hash, stream.Length,
                        candidate.Sheet.Name, candidate.Adapter.Profile, sourceRows);
                }
                stream.Position = 0;
                if (!string.Equals(hash, Hash(stream, token), StringComparison.Ordinal)) throw new GetnetImportException("getnet_input_changed");
                return result;
            }
        }

        private static string Hash(Stream stream, CancellationToken token)
        {
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[65536]; int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                { token.ThrowIfCancellationRequested(); sha.TransformBlock(buffer, 0, read, buffer, 0); }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static void ValidateArchive(ZipArchive archive, CancellationToken token)
        {
            if (archive.Entries.Count > 512) throw new GetnetImportException("getnet_zip_entry_limit");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                var name = entry.FullName;
                if (name.StartsWith("/", StringComparison.Ordinal) || name.Contains("\\") || name.Contains(":") ||
                    name.Split('/').Any(x => x == ".." || x == ".") || !names.Add(name))
                    throw new GetnetImportException("getnet_zip_path_invalid");
                expanded = checked(expanded + entry.Length);
                if (expanded > GetnetExcelImportLimits.MaximumExpandedZipBytes) throw new GetnetImportException("getnet_zip_expansion_limit");
                if (name.IndexOf("vbaProject", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("externalLinks/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("embeddings/", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new GetnetImportException("getnet_external_or_active_content");
                if (!name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;
                using (var reader = Xml(entry, GetnetExcelImportLimits.MaximumMetadataXmlBytes))
                    while (reader.Read())
                        if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship" &&
                            string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                            throw new GetnetImportException("getnet_external_or_active_content");
            }
        }

        private static List<Sheet> ReadSheets(ZipArchive archive, CancellationToken token, out bool date1904)
        {
            var relationships = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var reader = Xml(RequiredEntry(archive, "xl/_rels/workbook.xml.rels"), GetnetExcelImportLimits.MaximumMetadataXmlBytes))
                while (reader.Read())
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship")
                    {
                        var id = reader.GetAttribute("Id"); var target = reader.GetAttribute("Target");
                        if (id == null || target == null || relationships.ContainsKey(id)) throw new GetnetImportException("getnet_workbook_relationship_invalid");
                        if (target.StartsWith("/xl/", StringComparison.Ordinal)) target = target.Substring(1);
                        else target = "xl/" + target;
                        if (target.Contains("..") || target.Contains("\\") || target.Contains(":")) throw new GetnetImportException("getnet_workbook_relationship_invalid");
                        relationships.Add(id, target);
                    }
            var sheets = new List<Sheet>(); date1904 = false;
            using (var reader = Xml(RequiredEntry(archive, "xl/workbook.xml"), GetnetExcelImportLimits.MaximumMetadataXmlBytes))
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.LocalName == "workbookPr") date1904 = reader.GetAttribute("date1904") == "1" || reader.GetAttribute("date1904") == "true";
                    if (reader.LocalName != "sheet") continue;
                    var id = reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    string entry;
                    if (id == null || !relationships.TryGetValue(id, out entry) || sheets.Count >= 16) throw new GetnetImportException("getnet_worksheet_limit_or_relationship");
                    sheets.Add(new Sheet { Name = reader.GetAttribute("name") ?? string.Empty, Entry = entry });
                }
            return sheets;
        }

        private static List<string> ReadSharedStrings(ZipArchive archive, CancellationToken token)
        {
            var result = new List<string>(); var entry = archive.GetEntry("xl/sharedStrings.xml"); long characters = 0;
            if (entry == null) return result;
            using (var reader = Xml(entry, GetnetExcelImportLimits.MaximumSharedStringsXmlBytes))
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si") continue;
                    token.ThrowIfCancellationRequested();
                    var value = ContainerText(reader, true);
                    characters = checked(characters + value.Length);
                    if (result.Count >= GetnetExcelImportLimits.MaximumSharedStrings || characters > GetnetExcelImportLimits.MaximumSharedStringCharacters)
                        throw new GetnetImportException("getnet_shared_string_limit");
                    result.Add(value);
                }
            return result;
        }

        private static List<Style> ReadStyles(ZipArchive archive, CancellationToken token)
        {
            var result = new List<Style>(); var formats = new Dictionary<int, string>();
            var entry = archive.GetEntry("xl/styles.xml"); if (entry == null) { result.Add(new Style()); return result; }
            using (var reader = Xml(entry, GetnetExcelImportLimits.MaximumMetadataXmlBytes))
            {
                var cellXfs = false;
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.LocalName == "cellXfs") { cellXfs = reader.NodeType == XmlNodeType.Element; continue; }
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.LocalName == "numFmt")
                    {
                        int id; if (!int.TryParse(reader.GetAttribute("numFmtId"), NumberStyles.None, CultureInfo.InvariantCulture, out id))
                            throw new GetnetImportException("getnet_style_invalid");
                        var format = reader.GetAttribute("formatCode") ?? string.Empty;
                        if (format.Length > GetnetExcelImportLimits.MaximumCellCharacters || formats.Count >= 4096 || formats.ContainsKey(id))
                            throw new GetnetImportException("getnet_style_limit_or_duplicate");
                        formats.Add(id, format);
                    }
                    if (!cellXfs || reader.LocalName != "xf") continue;
                    int formatId; if (!int.TryParse(reader.GetAttribute("numFmtId") ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out formatId) || result.Count >= 4096)
                        throw new GetnetImportException("getnet_style_invalid");
                    string value; if (!formats.TryGetValue(formatId, out value)) value = formatId == 0 ? "General" : formatId == 1 ? "0" : string.Empty;
                    var date = (formatId >= 14 && formatId <= 22) || (formatId >= 45 && formatId <= 47) ||
                        (value.IndexOf('y') >= 0 && value.IndexOf('d') >= 0);
                    result.Add(new Style { Format = value, Date = date });
                }
            }
            if (result.Count == 0) result.Add(new Style());
            return result;
        }

        private static IEnumerable<GetnetWorksheetRow> ReadRows(ZipArchive archive, string entryName,
            List<string> strings, List<Style> styles, CancellationToken token, HashSet<int> retainedColumns = null)
        {
            using (var reader = Xml(RequiredEntry(archive, entryName), GetnetExcelImportLimits.MaximumWorksheetXmlBytes))
            {
                var previous = 0; var cellCount = 0;
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row") continue;
                    token.ThrowIfCancellationRequested();
                    int number;
                    if (!int.TryParse(reader.GetAttribute("r") ?? (previous + 1).ToString(CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out number) ||
                        number <= previous || number > GetnetExcelImportLimits.MaximumWorksheetRows)
                        throw new GetnetImportException("getnet_row_limit_or_order");
                    previous = number;
                    var row = new GetnetWorksheetRow { Number = number };
                    using (var rowReader = reader.ReadSubtree())
                    {
                        var previousColumn = 0;
                        while (rowReader.Read())
                        {
                            if (rowReader.NodeType != XmlNodeType.Element || rowReader.LocalName != "c") continue;
                            token.ThrowIfCancellationRequested();
                            var reference = rowReader.GetAttribute("r");
                            var column = reference == null ? previousColumn + 1 : Column(reference, number);
                            if (column <= previousColumn || column > GetnetExcelImportLimits.MaximumColumns || ++cellCount > GetnetExcelImportLimits.MaximumWorksheetCells)
                                throw new GetnetImportException("getnet_cell_limit_or_order", row: number, column: column);
                            previousColumn = column;
                            var type = rowReader.GetAttribute("t") ?? "n";
                            int styleIndex;
                            if (!int.TryParse(rowReader.GetAttribute("s") ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out styleIndex) || styleIndex < 0 || styleIndex >= styles.Count)
                                throw new GetnetImportException("getnet_style_invalid", row: number, column: column);
                            var value = string.Empty;
                            using (var cellReader = rowReader.ReadSubtree())
                                while (cellReader.Read())
                                {
                                    if (cellReader.NodeType != XmlNodeType.Element) continue;
                                    if (cellReader.LocalName == "f") throw new GetnetImportException("getnet_formula_not_allowed", row: number, column: column);
                                    if (cellReader.LocalName == "v") value = ContainerText(cellReader, false);
                                    if (cellReader.LocalName == "is") value = ContainerText(cellReader, true);
                                }
                            if (type == "s")
                            {
                                int index;
                                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out index) || index < 0 || index >= strings.Count)
                                    throw new GetnetImportException("getnet_shared_string_invalid", row: number, column: column);
                                value = strings[index];
                            }
                            if (!string.IsNullOrWhiteSpace(value)) row.HasContent = true;
                            if (retainedColumns == null || retainedColumns.Contains(column))
                                row.Cells.Add(column, new GetnetCell { Value = value, Type = type, NumberFormat = styles[styleIndex].Format, IsDate = styles[styleIndex].Date });
                        }
                    }
                    yield return row;
                }
            }
        }

        private static int Column(string reference, int expectedRow)
        {
            var index = 0; var column = 0;
            while (index < reference.Length && reference[index] >= 'A' && reference[index] <= 'Z')
            { column = checked(column * 26 + reference[index] - 'A' + 1); index++; if (column > 256) break; }
            int row;
            if (index == 0 || !int.TryParse(reference.Substring(index), NumberStyles.None, CultureInfo.InvariantCulture, out row) || row != expectedRow)
                throw new GetnetImportException("getnet_cell_reference_invalid", row: expectedRow);
            return column;
        }

        private static string ContainerText(XmlReader parent, bool onlyTextElements)
        {
            var builder = new StringBuilder(); var buffer = _textBuffer ?? (_textBuffer = new char[1024]);
            using (var reader = parent.ReadSubtree())
            {
                var inText = !onlyTextElements;
                while (reader.Read())
                {
                    if (onlyTextElements && reader.LocalName == "t") inText = reader.NodeType == XmlNodeType.Element;
                    if (!inText || (reader.NodeType != XmlNodeType.Text && reader.NodeType != XmlNodeType.CDATA &&
                        reader.NodeType != XmlNodeType.SignificantWhitespace && reader.NodeType != XmlNodeType.Whitespace)) continue;
                    int read;
                    while ((read = reader.ReadValueChunk(buffer, 0, buffer.Length)) > 0)
                    {
                        if (builder.Length + read > GetnetExcelImportLimits.MaximumCellCharacters) throw new GetnetImportException("getnet_cell_text_limit");
                        builder.Append(buffer, 0, read);
                    }
                }
            }
            return builder.ToString();
        }

        private static ZipArchiveEntry RequiredEntry(ZipArchive archive, string path) => archive.GetEntry(path) ??
            throw new GetnetImportException("getnet_package_part_missing");

        private static XmlReader Xml(ZipArchiveEntry entry, long maximum)
        {
            if (entry.Length > maximum) throw new GetnetImportException("getnet_xml_size_limit");
            var stream = entry.Open();
            try { return XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, CloseInput = true, IgnoreWhitespace = true, MaxCharactersInDocument = maximum }); }
            catch { stream.Dispose(); throw; }
        }
    }
}
