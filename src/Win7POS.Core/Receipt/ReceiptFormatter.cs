using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;

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
            return Format(SalesReceiptRenderModel.Create(sale, lines, shop), options);
        }

        public static IReadOnlyList<string> Format(
            SalesReceiptRenderModel input,
            ReceiptOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            options = options ?? ReceiptOptions.Default42();
            var labels = options.Labels ?? ReceiptLabels.English;
            var culture = CultureInfo.GetCultureInfo(options.CultureName ?? "en-US");
            var sale = input.Sale;
            var lines = input.Lines;
            var shop = input.Shop;

            var width = ReceiptTextLayout.NormalizeColumns(options.Width);
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
            AddLine(result, width, $"{labels.Receipt}: {sale.Code}");
            var when = DateTimeOffset.FromUnixTimeMilliseconds(sale.CreatedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            AddLine(result, width, $"{labels.DateTime}: {when}");
            AddLine(result, width, new string('-', width));

            if (lines != null && lines.Count > 0)
            {
                var discountByProduct = new Dictionary<string, SalesReceiptRenderModel.LineSnapshot>(StringComparer.Ordinal);
                foreach (var d in lines)
                {
                    if (d?.Barcode == null || !d.Barcode.StartsWith("DISC:LINE:", StringComparison.Ordinal)) continue;
                    var (key, _) = DiscountKeys.ParseLinePct(d.Barcode);
                    if (key != null && !discountByProduct.ContainsKey(key))
                        discountByProduct[key] = d;
                }

                foreach (var x in lines)
                {
                    if (x.LineTotal < 0 && (x.Barcode?.StartsWith("DISC:CART:", StringComparison.Ordinal) == true))
                        continue; // sconto carrello lo mettiamo nel riepilogo
                    if (x.Barcode != null && x.Barcode.StartsWith("DISC:LINE:", StringComparison.Ordinal))
                        continue; // sconto riga lo abbiamo già mostrato con il prodotto

                    var qty = x.Quantity < 0 ? 0 : x.Quantity;
                    var unit = FormatAmount(x.UnitPrice, options.Currency, culture);
                    var lineTotalFormatted = FormatAmount(x.LineTotal, options.Currency, culture);
                    AddWrappedLine(result, width, x.Name ?? "-");

                    if (discountByProduct.TryGetValue(x.Barcode ?? "", out var discLine))
                    {
                        var origTotal = FormatAmount(x.LineTotal, options.Currency, culture);
                        var discAmount = discLine.LineTotal < 0 ? -discLine.LineTotal : 0L;
                        var discFormatted = FormatAmount(-discAmount, options.Currency, culture);
                        var netTotal = x.LineTotal + discLine.LineTotal;
                        var netFormatted = FormatAmount(netTotal, options.Currency, culture);
                        var (_, pct) = DiscountKeys.ParseLinePct(discLine.Barcode ?? "");
                        var pctStr = pct.HasValue ? pct.Value + "%" : "";
                        AddLeftRight(result, width, $"  {qty} x {unit}", origTotal);
                        AddLeftRight(result, width, labels.Discount + " " + pctStr, discFormatted);
                        AddLeftRight(result, width, labels.Line, netFormatted);
                    }
                    else
                    {
                        AddLeftRight(result, width, $"  {qty} x {unit}", lineTotalFormatted);
                    }
                }

                // Sconti carrello (non associati a una riga)
                foreach (var d in lines)
                {
                    if (d?.LineTotal >= 0) continue;
                    if (d.Barcode != null && d.Barcode.StartsWith("DISC:CART:", StringComparison.Ordinal))
                    {
                        AddWrappedLine(result, width, d.Name ?? labels.CartDiscount);
                        AddLeftRight(result, width, labels.Discount, FormatAmount(d.LineTotal, options.Currency, culture));
                    }
                }
            }

            AddLine(result, width, new string('-', width));
            var itemCount = lines?.Count(x => x.Barcode == null || !x.Barcode.StartsWith("DISC:", StringComparison.Ordinal)) ?? 0;
            AddLine(result, width, labels.Items + ": " + itemCount);

            long subtotale = 0;
            long scontiTotali = 0;
            if (lines != null)
            {
                foreach (var x in lines)
                {
                    if (x.Barcode == null || !x.Barcode.StartsWith("DISC:", StringComparison.Ordinal))
                        subtotale += x.LineTotal;
                    else if (x.LineTotal < 0)
                        scontiTotali += -x.LineTotal;
                }
            }
            AddLeftRight(result, width, labels.Subtotal, FormatAmount(subtotale, options.Currency, culture));
            if (scontiTotali > 0)
                AddLeftRight(result, width, labels.TotalDiscounts, "-" + FormatAmount(scontiTotali, options.Currency, culture));
            AddLeftRight(result, width, labels.Total, FormatAmount(sale.Total, options.Currency, culture));
            if (sale.PaidCash != 0)
                AddLeftRight(result, width, labels.Cash, FormatAmount(sale.PaidCash, options.Currency, culture));
            if (sale.PaidCard != 0)
                AddLeftRight(result, width, labels.Card, FormatAmount(sale.PaidCard, options.Currency, culture));
            AddLeftRight(result, width, labels.Change, FormatAmount(sale.Change, options.Currency, culture));
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

            foreach (var part in ReceiptTextLayout.WrapText(text, width))
                lines.Add(ReceiptTextLayout.Center(part, width));
        }

        private static void AddLeftRight(List<string> lines, int width, string left, string right)
        {
            lines.AddRange(ReceiptTextLayout.TwoColumnLine(left, right, width));
        }

        private static void AddLine(List<string> lines, int width, string text)
        {
            lines.AddRange(ReceiptTextLayout.WrapText(text, width));
        }

        private static void AddWrappedLine(List<string> lines, int width, string text)
        {
            lines.AddRange(ReceiptTextLayout.WrapText(text, width));
        }
    }
}
