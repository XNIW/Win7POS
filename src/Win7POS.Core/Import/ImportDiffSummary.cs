namespace Win7POS.Core.Import
{
    public sealed class ImportDiffSummary
    {
        public int NewProduct { get; set; }
        public int UpdatePrice { get; set; }
        public int UpdateName { get; set; }
        public int UpdateBoth { get; set; }
        public int NoChange { get; set; }
        public int InvalidRow { get; set; }
    }
}
