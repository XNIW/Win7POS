namespace Win7POS.Core.Import
{
    public sealed class ImportAnalysis
    {
        public int TotalRows { get; set; }
        public int ValidRows { get; set; }
        public int Duplicates { get; set; }
        public int MissingBarcode { get; set; }
        public int InvalidPrice { get; set; }
        public int ErrorRows { get; set; }
    }
}
