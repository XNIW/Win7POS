namespace Win7POS.Core.Pos
{
    public sealed class PosLine
    {
        public long? ProductId { get; set; }
        public string Barcode { get; set; }
        public string Name { get; set; }
        public int Quantity { get; set; }
        public long UnitPrice { get; set; }
        public long LineTotal => Quantity * UnitPrice;
    }
}
