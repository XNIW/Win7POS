namespace Win7POS.Wpf.Printing
{
    public sealed class ReceiptPrintOptions
    {
        public const int MinimumCopies = 1;
        public const int MaximumCopies = 3;

        public string PrinterName { get; set; } = string.Empty;
        public int Copies { get; set; } = 1;
        public int CharactersPerLine { get; set; } = 42;

        /// <summary>True = scontrino (prima riga nome negozio in grassetto e più grande). False = fiscale/PDF (tutto uniforme).</summary>
        public bool UseReceiptHeaderStyle { get; set; } = true;

        /// <summary>Stable sale/refund code to render as Code128. Empty = no sale barcode.</summary>
        public string SaleCodeForBarcode { get; set; } = string.Empty;

        /// <summary>Istruzione ESC/POS per cassetto (es. "27,112,0,25,250"). Vuoto o malformato viene rifiutato.</summary>
        public string CashDrawerCommand { get; set; } = string.Empty;

        public static bool IsValidCopyCount(int copies)
        {
            return copies >= MinimumCopies && copies <= MaximumCopies;
        }
    }
}
