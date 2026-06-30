namespace Win7POS.Core.Receipt
{
    public sealed class ReceiptOptions
    {
        public int Width { get; set; } = 42;
        public string Currency { get; set; } = "CLP";
        public string CultureName { get; set; } = "en-US";
        public ReceiptLabels Labels { get; set; } = ReceiptLabels.English;

        public static ReceiptOptions Default42() => new ReceiptOptions { Width = 42 };
        public static ReceiptOptions Default32() => new ReceiptOptions { Width = 32 };
        public static ReceiptOptions Default42Clp() => new ReceiptOptions { Width = 42, Currency = "CLP" };
        public static ReceiptOptions Default32Clp() => new ReceiptOptions { Width = 32, Currency = "CLP" };
    }

    public sealed class ReceiptLabels
    {
        public static ReceiptLabels English => new ReceiptLabels();

        public string Card { get; set; } = "Card";
        public string CartDiscount { get; set; } = "Cart discount";
        public string Cash { get; set; } = "Cash";
        public string Change { get; set; } = "Change";
        public string DateTime { get; set; } = "Date/time";
        public string Discount { get; set; } = "Discount";
        public string Gross { get; set; } = "Gross";
        public string Items { get; set; } = "Items";
        public string Line { get; set; } = "Line";
        public string Net { get; set; } = "Net";
        public string Receipt { get; set; } = "Receipt";
        public string Refunds { get; set; } = "Returns";
        public string SalesCountShort { get; set; } = "Receipts";
        public string Subtotal { get; set; } = "Subtotal";
        public string Thanks { get; set; } = "Thanks";
        public string Total { get; set; } = "Total";
        public string TotalDiscounts { get; set; } = "Total discounts";
    }
}
