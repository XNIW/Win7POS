using System.Collections.Generic;

namespace Win7POS.Core.Import
{
    public sealed class ImportApplyResult
    {
        public int Inserted { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public int ErrorsCount { get; set; }
        public List<string> AppliedBarcodes { get; } = new List<string>();
    }
}
