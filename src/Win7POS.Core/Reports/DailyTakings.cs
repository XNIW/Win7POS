namespace Win7POS.Core.Reports
{
    public sealed class DailyTakings
    {
        public int TotalSalesCount { get; set; }
        public long GrossTotal { get; set; }
        public long CashTotal { get; set; }
        public long CardTotal { get; set; }
        public long ChangeTotal { get; set; }
    }
}
