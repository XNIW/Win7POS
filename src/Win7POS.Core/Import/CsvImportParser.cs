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

                // New format (>=6 cols): A=0 barcode, B=1 article_code, C=2 name, D=3 name2, E=4 unitPrice, F=5 purchasePrice, I=8 supplier, J=9 category, K=10 stock
                // Legacy (3-5 cols): 0=barcode, 1=name, 2=unitPrice, 3=cost, 4=stock
                var barcode = cols[0].Trim();
                string articleCode, name, name2, unitPriceText;
                if (cols.Length >= 6)
                {
                    articleCode = cols[1].Trim();
                    name = cols[2].Trim();
                    name2 = cols[3].Trim();
                    unitPriceText = cols[4].Trim();
                }
                else
                {
                    articleCode = string.Empty;
                    name = cols.Length > 1 ? cols[1].Trim() : string.Empty;
                    name2 = string.Empty;
                    unitPriceText = cols[2].Trim();
                }

                if (barcode.Length == 0)
                {
                    result.Errors.Add(new ImportParseError { LineNumber = lineNumber, Message = "MissingBarcode: barcode is empty." });
                    continue;
                }

                if (!long.TryParse(unitPriceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unitPrice) || unitPrice < 0)
                {
                    result.Errors.Add(new ImportParseError { LineNumber = lineNumber, Message = "InvalidPrice: unit price must be non-negative int minor." });
                    continue;
                }

                int? cost = null;
                int? stock = null;
                var supplierName = cols.Length > 8 ? cols[8].Trim() : string.Empty;
                var categoryName = cols.Length > 9 ? cols[9].Trim() : string.Empty;
                if (cols.Length >= 6 && int.TryParse(cols[5].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)) cost = c;
                else if (cols.Length >= 4 && int.TryParse(cols[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c3)) cost = c3;
                if (cols.Length > 10 && int.TryParse(cols[10].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) stock = s;
                else if (cols.Length == 5 && int.TryParse(cols[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s4)) stock = s4; // legacy: 5th = stock
                else if (cols.Length >= 4 && cols.Length < 6 && int.TryParse(cols[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s4b)) stock = s4b;

                result.Rows.Add(new ImportRow
                {
                    Barcode = barcode,
                    ArticleCode = articleCode,
                    Name = name,
                    Name2 = name2,
                    UnitPrice = unitPrice,
                    Cost = cost,
                    Stock = stock,
                    SupplierName = supplierName,
                    CategoryName = categoryName
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
