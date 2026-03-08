using System;
using System.Collections.Generic;
using System.Globalization;
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
            var culture = CultureInfo.GetCultureInfo(options.CultureName ?? "it-IT");

            var width = options.Width < 16 ? 16 : options.Width;
            var result = new List<string>();

            // Nome negozio in evidenza (maiuscolo = effetto "bold" in testo)
            var shopName = (shop.Name ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(shopName))
            {
                AddCentered(result, width, shopName.ToUpperInvariant());
                AddLine(result, width, new string('=', width));
            }
            else
                AddLine(result, width, string.Empty);

            // Indirizzo + città su una riga (se ci stanno), altrimenti wrapped
            var addr = (shop.Address ?? string.Empty).Trim();
            var cityRaw = (shop.City ?? string.Empty).Trim();
            var city = string.IsNullOrWhiteSpace(cityRaw)
                ? string.Empty
                : culture.TextInfo.ToTitleCase(cityRaw.Trim().ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(addr) && !string.IsNullOrWhiteSpace(city))
                AddWrappedLine(result, width, $"{addr} - {city}");
            else
            {
                if (!string.IsNullOrWhiteSpace(addr)) AddLine(result, width, addr);
                if (!string.IsNullOrWhiteSpace(city)) AddLine(result, width, city);
            }

            if (!string.IsNullOrWhiteSpace(shop.Rut)) AddLine(result, width, "RUT: " + shop.Rut.Trim());
            if (!string.IsNullOrWhiteSpace(shop.Phone)) AddLine(result, width, "Tel: " + (shop.Phone ?? "").Trim());
            AddLine(result, width, new string('-', width));
            AddLine(result, width, $"Scontrino: {sale.Code}");
            var when = DateTimeOffset.FromUnixTimeMilliseconds(sale.CreatedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            AddLine(result, width, $"Data/Ora: {when}");
            AddLine(result, width, new string('-', width));

            if (lines != null)
            {
                foreach (var x in lines)
                {
                    var lineTotalFormatted = FormatAmount(x.LineTotal, options.Currency, culture);
                    if (x.LineTotal < 0)
                    {
                        // Riga sconto: nome in evidenza, poi "Sconto" + importo negativo
                        AddWrappedLine(result, width, x.Name ?? "Sconto");
                        AddLeftRight(result, width, "  Sconto", lineTotalFormatted);
                    }
                    else
                    {
                        var qty = x.Quantity < 0 ? 0 : x.Quantity;
                        var unit = FormatAmount(x.UnitPrice, options.Currency, culture);
                        AddWrappedLine(result, width, x.Name ?? "-");
                        AddLeftRight(result, width, $"  {qty} x {unit}", lineTotalFormatted);
                    }
                }
            }

            AddLine(result, width, new string('-', width));
            var itemCount = lines?.Count ?? 0;
            AddLine(result, width, TrimToWidth("Articoli: " + itemCount, width));
            AddLeftRight(result, width, "Totale", FormatAmount(sale.Total, options.Currency, culture));
            if (sale.PaidCash > 0)
                AddLeftRight(result, width, "Contanti", FormatAmount(sale.PaidCash, options.Currency, culture));
            if (sale.PaidCard > 0)
                AddLeftRight(result, width, "Carta", FormatAmount(sale.PaidCard, options.Currency, culture));
            AddLeftRight(result, width, "Resto", FormatAmount(sale.Change, options.Currency, culture));
            AddLine(result, width, new string('-', width));
            AddCentered(result, width, shop.Footer ?? "");
            return result;
        }

        private static string FormatAmount(int amountMinor, string currency, CultureInfo culture)
        {
            return FormatAmount((long)amountMinor, currency, culture);
        }

        private static string FormatAmount(long amountMinor, string currency, CultureInfo culture)
        {
            if (string.Equals(currency, "CLP", StringComparison.OrdinalIgnoreCase))
                return amountMinor.ToString("N0", culture); // es. 3.300
            var value = amountMinor / 100.0m;
            return $"{value.ToString("N2", culture)} {currency}";
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

        private static void AddWrappedLine(List<string> lines, int width, string text)
        {
            var input = text ?? string.Empty;
            if (input.Length == 0)
            {
                AddLine(lines, width, string.Empty);
                return;
            }

            var start = 0;
            while (start < input.Length)
            {
                var len = input.Length - start;
                if (len > width) len = width;
                AddLine(lines, width, input.Substring(start, len));
                start += len;
            }
        }

        private static string TrimToWidth(string value, int width)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= width ? value : value.Substring(0, width);
        }
    }
}
