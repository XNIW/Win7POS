namespace Win7POS.Core.Import
{
    public sealed class ImportDiffItem
    {
        public string Barcode { get; set; } = string.Empty;
        public string IncomingName { get; set; } = string.Empty;
        public int IncomingPrice { get; set; }
        public string ExistingName { get; set; } = string.Empty;
        public int? ExistingPrice { get; set; }
        public ImportDiffKind Kind { get; set; }
    }
}
