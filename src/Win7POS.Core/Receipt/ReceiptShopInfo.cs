namespace Win7POS.Core.Receipt
{
    public sealed class ReceiptShopInfo
    {
        public string Name { get; set; } = "Win7 POS Store";
        public string Address { get; set; }
        public string BusinessGiro { get; set; }
        public string City { get; set; }
        public string LegalRepresentativeRut { get; set; }
        public string Rut { get; set; }
        public string Phone { get; set; }
        public string Footer { get; set; } = "Thanks";
        public string ShopCode { get; set; }
        public string ShopStatus { get; set; }
        public string Source { get; set; }
        public string SyncedAtUtc { get; set; }
    }
}
