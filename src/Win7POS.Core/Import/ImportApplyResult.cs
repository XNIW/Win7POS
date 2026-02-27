using System.Collections.Generic;

namespace Win7POS.Core.Import
{
    public sealed class ImportApplyResult
    {
        public int AppliedInserted { get; set; }
        public int AppliedUpdated { get; set; }
        public int NoChange { get; set; }
        public int Skipped { get; set; }
        public int ErrorsCount { get; set; }
        public List<string> ChangedBarcodes { get; } = new List<string>();
    }
}
