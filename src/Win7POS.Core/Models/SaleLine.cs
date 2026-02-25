namespace Win7POS.Core.Models
{
    public sealed class SaleLine
    {
        public long Id { get; set; }
        public long SaleId { get; set; }

        public long? ProductId { get; set; }
        public string Barcode { get; set; }
        public string Name { get; set; }

        public int Quantity { get; set; }
        public int UnitPrice { get; set; }
        public int LineTotal { get; set; }
    }
}
