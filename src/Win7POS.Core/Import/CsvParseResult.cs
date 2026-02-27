using System.Collections.Generic;

namespace Win7POS.Core.Import
{
    public sealed class CsvParseResult
    {
        public List<ImportRow> Rows { get; } = new List<ImportRow>();
        public List<ImportParseError> Errors { get; } = new List<ImportParseError>();
        public int TotalRows { get; set; }
    }
}
