namespace Win7POS.Core.Import
{
    public sealed class ImportRow
    {
        public string Barcode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int UnitPrice { get; set; }
        public int? Cost { get; set; }
        public int? Stock { get; set; }
    }
}
