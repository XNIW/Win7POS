namespace Win7POS.Wpf.Printing
{
    public sealed class ReceiptPrintOptions
    {
        public string PrinterName { get; set; } = string.Empty;
        public int Copies { get; set; } = 1;
        public int CharactersPerLine { get; set; } = 42;
        public bool SaveCopyToFile { get; set; }
        public string OutputPath { get; set; } = string.Empty;

        /// <summary>True = scontrino (prima riga nome negozio in grassetto e più grande). False = fiscale/PDF (tutto uniforme).</summary>
        public bool UseReceiptHeaderStyle { get; set; } = true;

        /// <summary>Istruzione ESC/POS per cassetto (es. "27,112,0,60,255"). Vuoto = default 27,112,0,25,25.</summary>
        public string CashDrawerCommand { get; set; } = string.Empty;
    }
}
