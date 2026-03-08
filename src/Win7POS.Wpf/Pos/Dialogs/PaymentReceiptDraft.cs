using System.Collections.Generic;
using Win7POS.Core.Receipt;

namespace Win7POS.Wpf.Pos.Dialogs
{
    /// <summary>Dati per anteprima scontrino nella schermata Pagamento (PaymentView, stesso formato della stampa).</summary>
    public sealed class PaymentReceiptDraft
    {
        public string SaleCode { get; set; }
        public long CreatedAtMs { get; set; }
        public IReadOnlyList<PaymentReceiptDraftLine> CartLines { get; set; }
        public bool UseReceipt42 { get; set; }
        public bool DefaultPrint { get; set; }
        public ReceiptShopInfo ShopInfo { get; set; }
        public int NextBoletaNumber { get; set; }
    }

    public sealed class PaymentReceiptDraftLine
    {
        public string Barcode { get; set; }
        public string Name { get; set; }
        public int Quantity { get; set; }
        public long UnitPrice { get; set; }
        public long LineTotal { get; set; }
    }
}
