using System.Collections.Generic;

namespace Win7POS.Core.Import
{
    public static class ImportAnalyzer
    {
        public static ImportAnalysis Analyze(CsvParseResult parseResult)
        {
            var result = new ImportAnalysis();
            if (parseResult == null) return result;

            result.TotalRows = parseResult.TotalRows;
            result.ErrorRows = parseResult.Errors.Count;
            foreach (var e in parseResult.Errors)
            {
                if (e.Message.StartsWith("MissingBarcode")) result.MissingBarcode += 1;
                if (e.Message.StartsWith("InvalidPrice")) result.InvalidPrice += 1;
            }

            var seen = new HashSet<string>();
            foreach (var row in parseResult.Rows)
            {
                if (seen.Contains(row.Barcode))
                {
                    result.Duplicates += 1;
                    continue;
                }

                seen.Add(row.Barcode);
                result.ValidRows += 1;
            }

            return result;
        }
    }
}
