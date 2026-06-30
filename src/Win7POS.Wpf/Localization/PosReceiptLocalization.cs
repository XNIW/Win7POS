using Win7POS.Core.Receipt;

namespace Win7POS.Wpf.Localization
{
    public sealed partial class PosLocalization
    {
        public static ReceiptOptions CreateReceiptOptions(bool use42Columns, string receiptTitleKey)
        {
            var options = use42Columns ? ReceiptOptions.Default42Clp() : ReceiptOptions.Default32Clp();
            options.CultureName = CultureNameForLanguage(Current.CurrentLanguage);
            options.Labels = CreateReceiptLabels(receiptTitleKey);
            return options;
        }

        public static ReceiptLabels CreateReceiptLabels(string receiptTitleKey)
        {
            return new ReceiptLabels
            {
                Card = T("common.card"),
                CartDiscount = T("receipt.cartDiscount"),
                Cash = T("common.cash"),
                Change = T("receipt.change"),
                DateTime = T("receipt.dateTime"),
                Discount = T("receipt.discount"),
                Gross = T("common.gross"),
                Items = T("receipt.items"),
                Line = T("receipt.line"),
                Net = T("common.net"),
                Receipt = T(string.IsNullOrWhiteSpace(receiptTitleKey) ? "receipt.title" : receiptTitleKey),
                Refunds = T("common.returns"),
                SalesCountShort = T("common.receipts"),
                Subtotal = T("receipt.subtotal"),
                Thanks = T("receipt.thanks"),
                Total = T("common.total"),
                TotalDiscounts = T("receipt.totalDiscounts")
            };
        }
    }
}
