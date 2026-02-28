using System.Collections.Generic;

namespace Win7POS.Core.Models
{
    public sealed class RefundLineRequest
    {
        public long OriginalLineId { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int UnitPriceMinor { get; set; }
        public int QtyToRefund { get; set; }
    }

    public sealed class RefundPaymentInfo
    {
        public int CashMinor { get; set; }
        public int CardMinor { get; set; }
    }

    public sealed class RefundCreateRequest
    {
        public long OriginalSaleId { get; set; }
        public bool IsFullVoid { get; set; }
        public List<RefundLineRequest> Lines { get; set; } = new List<RefundLineRequest>();
        public RefundPaymentInfo Payment { get; set; } = new RefundPaymentInfo();
        public string Reason { get; set; } = string.Empty;
    }

    public sealed class RefundCreateResult
    {
        public long RefundSaleId { get; set; }
        public string RefundSaleCode { get; set; } = string.Empty;
        public string Receipt42 { get; set; } = string.Empty;
        public string Receipt32 { get; set; } = string.Empty;
        public int TotalMinor { get; set; }
    }
}
