using System;
using System.Collections.Generic;
using Win7POS.Core.Models;
using Win7POS.Core.Receipt;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos
{
    /// <summary>
    /// Unico renderer testuale per anteprima pagamento, stampa vendita e campione stampante.
    /// Mantenere qui ogni modifica alla struttura dello scontrino POS.
    /// </summary>
    internal static class PosReceiptTextRenderer
    {
        internal static string BuildReceipt(
            Sale sale,
            IReadOnlyList<SaleLine> lines,
            bool use42,
            ReceiptShopInfo shop = null)
        {
            return BuildReceipt(SalesReceiptRenderModel.Create(sale, lines, shop), use42);
        }

        internal static string BuildReceipt(SalesReceiptRenderModel input, bool use42)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var columns = use42 ? 42 : 32;

            var renderedLines = new List<string>(ReceiptFormatter.Format(
                input,
                PosLocalization.CreateReceiptOptions(use42, "receipt.title")));

            if (input.Sale.Kind == (int)SaleKind.Refund ||
                input.Sale.Kind == (int)SaleKind.Void)
            {
                renderedLines.InsertRange(
                    0,
                    ReceiptTextLayout.WrapText(
                        PosLocalization.T("refund.receiptHeader"),
                        columns));
            }

            // Il codice resta anche in chiaro: la stampa fisica può aggiungere il Code128.
            if (!string.IsNullOrEmpty(input.Sale.Code))
            {
                renderedLines.Add(string.Empty);
                renderedLines.AddRange(ReceiptTextLayout.WrapText(
                    PosLocalization.T("receipt.title") + ": " + input.Sale.Code,
                    columns));
            }

            return string.Join(Environment.NewLine, renderedLines);
        }

        internal static void SplitPreview(
            string receiptText,
            out string firstLine,
            out string remainingText)
        {
            var normalized = (receiptText ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            var separator = normalized.IndexOf('\n');
            if (separator < 0)
            {
                firstLine = normalized;
                remainingText = string.Empty;
                return;
            }

            firstLine = normalized.Substring(0, separator);
            remainingText = normalized.Substring(separator + 1)
                .Replace("\n", Environment.NewLine);
        }
    }
}
