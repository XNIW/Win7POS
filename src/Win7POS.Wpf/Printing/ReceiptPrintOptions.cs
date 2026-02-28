namespace Win7POS.Wpf.Printing
{
    public sealed class ReceiptPrintOptions
    {
        public string PrinterName { get; set; } = string.Empty;
        public int Copies { get; set; } = 1;
        public int CharactersPerLine { get; set; } = 42;
        public bool SaveCopyToFile { get; set; }
        public string OutputPath { get; set; } = string.Empty;
    }
}
