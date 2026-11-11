namespace Win7POS.Core.Models
{
    public sealed class SaleLine
    {
        public long Id { get; set; }
        public long SaleId { get; set; }

        public long? ProductId { get; set; }
        // In-process command evidence; never serialized or persisted as authority.
        [System.Runtime.Serialization.IgnoreDataMember]
        public Win7POS.Core.Pos.ManualSaleLineEvidence ManualPriceEvidence { get; set; }
        public string Barcode { get; set; }
        public string Name { get; set; }

        public int Quantity { get; set; }
        public long UnitPrice { get; set; }
        public long LineTotal { get; set; }
        public long? RelatedOriginalLineId { get; set; }
    }
}
