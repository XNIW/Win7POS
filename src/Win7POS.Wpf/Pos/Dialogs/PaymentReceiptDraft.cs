using System.Collections.Generic;

namespace Win7POS.Wpf.Pos.Dialogs
{
    /// <summary>Dati per anteprima scontrino nel PaymentDialog (stesso formato della stampa).</summary>
    public sealed class PaymentReceiptDraft
    {
        public string SaleCode { get; set; }
        public long CreatedAtMs { get; set; }
        public IReadOnlyList<PaymentReceiptDraftLine> CartLines { get; set; }
        public bool UseReceipt42 { get; set; }
        public bool DefaultPrint { get; set; }
    }

    public sealed class PaymentReceiptDraftLine
    {
        public string Barcode { get; set; }
        public string Name { get; set; }
        public int Quantity { get; set; }
        public int UnitPrice { get; set; }
        public int LineTotal { get; set; }
    }
}
