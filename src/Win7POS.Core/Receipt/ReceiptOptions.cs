namespace Win7POS.Core.Receipt
{
    public sealed class ReceiptOptions
    {
        public int Width { get; set; } = 42;
        public string Currency { get; set; } = "EUR";

        public static ReceiptOptions Default42() => new ReceiptOptions { Width = 42 };
        public static ReceiptOptions Default32() => new ReceiptOptions { Width = 32 };
    }
}
