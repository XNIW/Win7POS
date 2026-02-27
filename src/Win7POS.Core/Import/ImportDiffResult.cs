using System.Collections.Generic;

namespace Win7POS.Core.Import
{
    public sealed class ImportDiffResult
    {
        public ImportDiffSummary Summary { get; } = new ImportDiffSummary();
        public List<ImportDiffItem> Items { get; } = new List<ImportDiffItem>();
    }
}
