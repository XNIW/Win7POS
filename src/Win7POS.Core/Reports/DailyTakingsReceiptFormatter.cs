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
        public static IReadOnlyList<string> Format(DailyTakingsReceiptModel model, ReceiptShopInfo shop = null)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            shop = shop ?? new ReceiptShopInfo();
            var culture = CultureInfo.GetCultureInfo("es-CL");
            var width = 42;

            var result = new List<string>();
            AddCentered(result, width, "CHIUSURA CASSA");
            AddLine(result, width, "Data: " + model.Date.ToString("yyyy-MM-dd", culture));
            AddLine(result, width, new string('-', width));

            AddLeftRight(result, width, "N. vendite", model.SalesCount.ToString(CultureInfo.InvariantCulture));
            AddLeftRight(result, width, "Totale", FormatClp(model.TotalAmount));
            AddLeftRight(result, width, "Contanti", FormatClp(model.CashAmount));
            AddLeftRight(result, width, "Carta", FormatClp(model.CardAmount));
            AddLeftRight(result, width, "Lorde", FormatClp(model.GrossSalesAmount));
            AddLeftRight(result, width, "Resi", FormatClp(model.RefundsAmount));
            AddLine(result, width, new string('-', width));
            AddLeftRight(result, width, "Netto", FormatClp(model.NetAmount));
            AddLine(result, width, new string('-', width));
            AddCentered(result, width, string.IsNullOrWhiteSpace(shop.Footer) ? "Grazie" : shop.Footer.Trim());

            return result;
        }

        private static string FormatClp(long pesos)
        {
            return pesos.ToString("N0", CultureInfo.GetCultureInfo("es-CL"));
        }

        private static void AddCentered(List<string> lines, int width, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                AddLine(lines, width, string.Empty);
                return;
            }
            var safe = TrimToWidth(text.Trim(), width);
            var left = (width - safe.Length) / 2;
            if (left < 0) left = 0;
            AddLine(lines, width, new string(' ', left) + safe);
        }

        private static void AddLeftRight(List<string> lines, int width, string left, string right)
        {
            left = left ?? string.Empty;
            right = right ?? string.Empty;
            var room = width - right.Length;
            var l = room <= 1 ? string.Empty : TrimToWidth(left, room - 1);
            var spaces = width - l.Length - right.Length;
            if (spaces < 1) spaces = 1;
            AddLine(lines, width, l + new string(' ', spaces) + right);
        }

        private static void AddLine(List<string> lines, int width, string text)
        {
            lines.Add(TrimToWidth(text ?? string.Empty, width));
        }

        private static string TrimToWidth(string value, int width)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= width ? value : value.Substring(0, width);
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
