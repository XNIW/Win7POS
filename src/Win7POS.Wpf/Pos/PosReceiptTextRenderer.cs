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
            if (sale == null) throw new ArgumentNullException(nameof(sale));

            var renderedLines = new List<string>(ReceiptFormatter.Format(
                sale,
                lines,
                PosLocalization.CreateReceiptOptions(use42, "receipt.title"),
                shop ?? new ReceiptShopInfo()));

            // Il codice resta anche in chiaro: la stampa fisica può aggiungere il Code128.
            if (!string.IsNullOrEmpty(sale.Code))
            {
                renderedLines.Add(string.Empty);
                renderedLines.Add(PosLocalization.T("receipt.title") + ": " + sale.Code);
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
