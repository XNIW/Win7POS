using System;
using System.Collections.Generic;
using System.Globalization;

namespace Win7POS.Core.Import
{
    public static class CsvImportParser
    {
        public static CsvParseResult Parse(string content)
        {
            var lines = (content ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            return ParseLines(lines);
        }

        public static CsvParseResult ParseLines(IEnumerable<string> lines)
        {
            var result = new CsvParseResult();
            var lineNumber = 0;
            var isFirst = true;

            foreach (var rawLine in lines)
            {
                lineNumber += 1;
                var line = (rawLine ?? string.Empty).Trim();
                if (line.Length == 0) continue;

                if (isFirst && IsHeader(line))
                {
                    isFirst = false;
                    continue;
                }
                isFirst = false;
                result.TotalRows += 1;

                var delimiter = DetectDelimiter(line);
                var cols = line.Split(delimiter);
                if (cols.Length < 3)
                {
                    result.Errors.Add(new ImportParseError { LineNumber = lineNumber, Message = "InvalidColumns: at least 3 columns required." });
                    continue;
                }

                var barcode = cols[0].Trim();
                var name = cols[1].Trim();
                var unitPriceText = cols[2].Trim();

                if (barcode.Length == 0)
                {
                    result.Errors.Add(new ImportParseError { LineNumber = lineNumber, Message = "MissingBarcode: barcode is empty." });
                    continue;
                }

                if (!int.TryParse(unitPriceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unitPrice) || unitPrice < 0)
                {
                    result.Errors.Add(new ImportParseError { LineNumber = lineNumber, Message = "InvalidPrice: unit price must be non-negative int minor." });
                    continue;
                }

                int? cost = null;
                int? stock = null;
                if (cols.Length >= 4 && int.TryParse(cols[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)) cost = c;
                if (cols.Length >= 5 && int.TryParse(cols[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) stock = s;

                result.Rows.Add(new ImportRow
                {
                    Barcode = barcode,
                    Name = name,
                    UnitPrice = unitPrice,
                    Cost = cost,
                    Stock = stock
                });
            }

            return result;
        }

        private static char DetectDelimiter(string line) => line.IndexOf(';') >= 0 ? ';' : ',';

        private static bool IsHeader(string line)
        {
            var lower = line.ToLowerInvariant();
            return lower.Contains("barcode") && lower.Contains("name");
        }
    }
}
