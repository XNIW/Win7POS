namespace Win7POS.Core.Models
{
    public sealed class Sale
    {
        public long Id { get; set; }
        public string Code { get; set; }
        public long CreatedAt { get; set; }   // unix ms
        public int Kind { get; set; } = (int)SaleKind.Sale;
        public long? RelatedSaleId { get; set; }
        public long? VoidedBySaleId { get; set; }
        public long? VoidedAt { get; set; }
        public string Reason { get; set; }
        public int Total { get; set; }
        public int PaidCash { get; set; }
        public int PaidCard { get; set; }
        public int Change { get; set; }
    }
}
