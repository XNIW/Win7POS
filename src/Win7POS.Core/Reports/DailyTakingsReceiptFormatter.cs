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
        private const int Width = 42;

        public static IReadOnlyList<string> Format(DailyTakingsReceiptModel model, ReceiptShopInfo shop = null)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            shop = shop ?? new ReceiptShopInfo();
            var culture = CultureInfo.GetCultureInfo("es-CL");

            var lines = new List<string>();
            lines.Add("CHIUSURA CASSA");
            lines.Add("Data: " + model.Date.ToString("yyyy-MM-dd", culture));
            lines.Add(new string('-', Width));
            lines.Add(Row("N. vendite", model.SalesCount.ToString(CultureInfo.InvariantCulture)));
            lines.Add(Row("Totale", Money(model.TotalAmount)));
            lines.Add(Row("Contanti", Money(model.CashAmount)));
            lines.Add(Row("Carta", Money(model.CardAmount)));
            lines.Add(Row("Lorde", Money(model.GrossSalesAmount)));
            lines.Add(Row("Resi", Money(model.RefundsAmount)));
            lines.Add(new string('-', Width));
            lines.Add(Row("Netto", Money(model.NetAmount)));
            lines.Add(new string('-', Width));
            lines.Add(string.IsNullOrWhiteSpace(shop.Footer) ? "Grazie e arrivederci" : Fit(shop.Footer.Trim(), Width));

            return lines;
        }

        private static string Fit(string text, int max)
        {
            text = text ?? string.Empty;
            return text.Length <= max ? text : text.Substring(0, max);
        }

        private static string Row(string label, string value, int width = Width)
        {
            label = Fit(label, 14);
            value = value ?? "0";
            var spaces = width - label.Length - value.Length;
            if (spaces < 1) spaces = 1;
            return label + new string(' ', spaces) + value;
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
