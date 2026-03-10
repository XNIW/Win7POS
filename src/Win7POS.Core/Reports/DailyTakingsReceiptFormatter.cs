using System;
using System.Collections.Generic;
using System.Globalization;
using Win7POS.Core.Receipt;

namespace Win7POS.Core.Reports
{
    /// <summary>
    /// Formatta la ricevuta di chiusura cassa / incasso giornaliero per stampa.
    /// </summary>
    public static class DailyTakingsReceiptFormatter
    {
        /// <summary>Larghezza termica conservativa (Win7 + driver).</summary>
        private const int ReceiptWidth = 32;

        public static IReadOnlyList<string> Format(DailyTakingsReceiptModel model, ReceiptShopInfo shop = null)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            shop = shop ?? new ReceiptShopInfo();
            var culture = CultureInfo.GetCultureInfo("es-CL");

            var lines = new List<string>();
            lines.Add("CHIUSURA CASSA");
            lines.Add("Data: " + model.Date.ToString("yyyy-MM-dd", culture));
            lines.Add(new string('-', ReceiptWidth));
            lines.Add(Line2("Scontr.", model.SalesCount.ToString(CultureInfo.InvariantCulture)));
            lines.Add(Line2("Totale", Money(model.TotalAmount)));
            lines.Add(Line2("Cash", Money(model.CashAmount)));
            lines.Add(Line2("Carta", Money(model.CardAmount)));
            lines.Add(Line2("Lorde", Money(model.GrossSalesAmount)));
            lines.Add(Line2("Resi", Money(model.RefundsAmount)));
            lines.Add(new string('-', ReceiptWidth));
            lines.Add(Line2("Netto", Money(model.NetAmount)));
            lines.Add(new string('-', ReceiptWidth));
            lines.Add(string.IsNullOrWhiteSpace(shop.Footer) ? "Grazie" : Fit(shop.Footer.Trim(), ReceiptWidth));

            return lines;
        }

        /// <summary>Riga con label a sinistra e valore a destra, senza superare ReceiptWidth.</summary>
        private static string Line2(string left, string right)
        {
            left = left ?? "";
            right = right ?? "";
            var maxLeft = Math.Max(1, ReceiptWidth - right.Length - 1);
            if (left.Length > maxLeft)
                left = left.Substring(0, maxLeft);
            return left.PadRight(ReceiptWidth - right.Length) + right;
        }

        private static string Fit(string text, int max)
        {
            text = text ?? string.Empty;
            return text.Length <= max ? text : text.Substring(0, max);
        }

        private static string Money(long pesos)
        {
            return pesos.ToString("N0", CultureInfo.GetCultureInfo("es-CL"));
        }
    }

    public sealed class DailyTakingsReceiptModel
    {
        public DateTime Date { get; set; }
        public int SalesCount { get; set; }
        public long TotalAmount { get; set; }
        public long CashAmount { get; set; }
        public long CardAmount { get; set; }
        public long GrossSalesAmount { get; set; }
        public long RefundsAmount { get; set; }
        public long NetAmount { get; set; }
    }
}
