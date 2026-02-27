namespace Win7POS.Core.Reports
{
    public sealed class DailyTakings
    {
        public int TotalSalesCount { get; set; }
        public int GrossTotal { get; set; }
        public int CashTotal { get; set; }
        public int CardTotal { get; set; }
        public int ChangeTotal { get; set; }
    }
}
