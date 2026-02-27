using System;
using System.Collections.Generic;
using Win7POS.Core.Models;

namespace Win7POS.Core.Receipt
{
    public static class ReceiptFormatter
    {
        public static IReadOnlyList<string> Format(
            Sale sale,
            IReadOnlyList<SaleLine> lines,
            ReceiptOptions options = null,
            ReceiptShopInfo shop = null)
        {
            if (sale == null) throw new ArgumentNullException(nameof(sale));
            options = options ?? ReceiptOptions.Default42();
            shop = shop ?? new ReceiptShopInfo();

            var width = options.Width < 16 ? 16 : options.Width;
            var result = new List<string>();
            AddCentered(result, width, shop.Name);
            if (!string.IsNullOrWhiteSpace(shop.Address)) AddLine(result, width, shop.Address);
            if (!string.IsNullOrWhiteSpace(shop.Phone)) AddLine(result, width, $"Tel: {shop.Phone}");
            AddLine(result, width, new string('-', width));
            AddLine(result, width, $"Sale: {sale.Code}");
            var when = DateTimeOffset.FromUnixTimeMilliseconds(sale.CreatedAt).ToString("yyyy-MM-dd HH:mm:ss");
            AddLine(result, width, $"Time: {when}");
            AddLine(result, width, new string('-', width));

            if (lines != null)
            {
                foreach (var x in lines)
                {
                    var qty = x.Quantity < 0 ? 0 : x.Quantity;
                    var unit = FormatAmount(x.UnitPrice, options.Currency);
                    var lineTotal = FormatAmount(x.LineTotal, options.Currency);
                    AddLine(result, width, TrimToWidth(x.Name ?? "-", width));
                    AddLeftRight(result, width, $"  {qty} x {unit}", lineTotal);
                }
            }

            AddLine(result, width, new string('-', width));
            AddLeftRight(result, width, "Totale", FormatAmount(sale.Total, options.Currency));
            AddLeftRight(result, width, "Cash", FormatAmount(sale.PaidCash, options.Currency));
            AddLeftRight(result, width, "Card", FormatAmount(sale.PaidCard, options.Currency));
            AddLeftRight(result, width, "Change", FormatAmount(sale.Change, options.Currency));
            AddLine(result, width, new string('-', width));
            AddCentered(result, width, shop.Footer);
            return result;
        }

        private static string FormatAmount(int amountMinor, string currency)
        {
            var value = amountMinor / 100.0m;
            return $"{value:0.00} {currency}";
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
}
