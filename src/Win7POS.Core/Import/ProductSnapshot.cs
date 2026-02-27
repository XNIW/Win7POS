namespace Win7POS.Core.Import
{
    public sealed class ProductSnapshot
    {
        public string Barcode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int UnitPrice { get; set; }
    }
}
