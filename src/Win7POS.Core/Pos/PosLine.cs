namespace Win7POS.Core.Pos
{
    public sealed class PosLine
    {
        public string LineKey { get; } = System.Guid.NewGuid().ToString("N");
        public long? ProductId { get; set; }
        public ManualSaleLineEvidence ManualPriceEvidence { get; internal set; }
        public string Barcode { get; set; }
        public string Name { get; set; }
        public int Quantity { get; set; }
        public long UnitPrice { get; set; }
        public long LineTotal => checked(Quantity * UnitPrice);
    }
}
