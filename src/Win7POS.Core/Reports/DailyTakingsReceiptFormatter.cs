using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Win7POS.Core.Receipt;

namespace Win7POS.Core.Reports
{
    /// <summary>Authoritative renderer for 32/42-column daily-close output.</summary>
    public static class DailyCloseReceiptTextRenderer
    {
        public static string Render(
            DailyTakingsReceiptModel model,
            ReceiptShopInfo shop = null,
            ReceiptOptions options = null,
            DailyCloseReceiptLabels closeLabels = null)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            shop = shop ?? new ReceiptShopInfo();
            ReceiptShopMetadataPolicy.EnsureValidReceiptShop(shop);
            options = options ?? ReceiptOptions.Default42Clp();
            closeLabels = closeLabels ?? DailyCloseReceiptLabels.English;
            var labels = options.Labels ?? ReceiptLabels.English;
            var culture = CultureInfo.GetCultureInfo(options.CultureName ?? "en-US");
            var width = ReceiptTextLayout.NormalizeColumns(options.Width);
            var lines = new List<string>();

            AddCenteredWrapped(lines, shop.Name, width, upper: true);
            AddCenteredWrapped(lines, labels.Receipt, width);
            lines.Add(ReceiptTextLayout.Separator(width, '='));
            AddWrapped(lines, shop.Address, width);
            AddWrapped(lines, shop.City, width);
            if (!string.IsNullOrWhiteSpace(shop.Rut)) AddWrapped(lines, "RUT: " + shop.Rut, width);
            lines.Add(ReceiptTextLayout.Separator(width));

            AddPair(lines, closeLabels.BusinessDate, model.Date.ToString("yyyy-MM-dd", culture), width);
            if (model.PeriodStart.HasValue && model.PeriodEnd.HasValue)
            {
                AddPair(lines, closeLabels.Period,
                    model.PeriodStart.Value.ToString("yyyy-MM-dd", culture) + " / " +
                    model.PeriodEnd.Value.ToString("yyyy-MM-dd", culture), width);
            }
            if (!string.IsNullOrWhiteSpace(model.OperatorName))
                AddPair(lines, closeLabels.Operator, model.OperatorName, width);

            lines.Add(ReceiptTextLayout.Separator(width));
            AddPair(lines, labels.SalesCountShort, model.SalesCount.ToString(CultureInfo.InvariantCulture), width);
            AddPair(lines, labels.Gross, Money(model.GrossSalesAmount, options.Currency, culture), width);
            AddOptionalMoney(lines, closeLabels.Discounts, model.DiscountsAmount, options, culture, width);
            AddOptionalMoney(lines, closeLabels.Tax, model.TaxAmount, options, culture, width);
            AddPair(lines, labels.Refunds, Money(model.RefundsAmount, options.Currency, culture), width);
            AddOptionalMoney(lines, closeLabels.Voids, model.VoidsAmount, options, culture, width);
            AddPair(lines, labels.Net, Money(model.NetAmount, options.Currency, culture), width);

            lines.Add(ReceiptTextLayout.Separator(width));
            AddPair(lines, labels.Cash, Money(model.CashAmount, options.Currency, culture), width);
            AddPair(lines, labels.Card, Money(model.CardAmount, options.Currency, culture), width);
            if (model.MixedSalesCount.HasValue)
                AddPair(lines, closeLabels.Mixed, model.MixedSalesCount.Value.ToString(CultureInfo.InvariantCulture), width);
            AddOptionalMoney(lines, labels.Change, model.ChangeAmount, options, culture, width);

            AddOptionalMoney(lines, closeLabels.OpeningAmount, model.OpeningAmount, options, culture, width);
            AddOptionalMoney(lines, closeLabels.ClosingAmount, model.ClosingAmount, options, culture, width);
            AddOptionalMoney(lines, closeLabels.ExpectedCash, model.ExpectedCashAmount, options, culture, width);
            AddOptionalMoney(lines, closeLabels.Difference, model.DifferenceAmount, options, culture, width);

            if (model.PendingSyncCount.HasValue || model.RetrySyncCount.HasValue || model.BlockedSyncCount.HasValue)
            {
                lines.Add(ReceiptTextLayout.Separator(width));
                AddOptionalCount(lines, closeLabels.PendingSync, model.PendingSyncCount, width);
                AddOptionalCount(lines, closeLabels.RetrySync, model.RetrySyncCount, width);
                AddOptionalCount(lines, closeLabels.BlockedSync, model.BlockedSyncCount, width);
            }

            lines.Add(ReceiptTextLayout.Separator(width));
            var generatedAt = model.GeneratedAt == default ? DateTimeOffset.Now : model.GeneratedAt;
            AddPair(lines, closeLabels.Generated,
                generatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", culture), width);
            AddCenteredWrapped(lines,
                string.IsNullOrWhiteSpace(shop.Footer) ? labels.Thanks : shop.Footer,
                width);
            var receipt = string.Join(Environment.NewLine, lines);
            ReceiptDocumentPolicy.EnsureValidDocument(receipt);
            return receipt;
        }

        private static void AddOptionalMoney(List<string> lines, string label, long? value, ReceiptOptions options, CultureInfo culture, int width)
        {
            if (value.HasValue) AddPair(lines, label, Money(value.Value, options.Currency, culture), width);
        }

        private static void AddOptionalCount(List<string> lines, string label, int? value, int width)
        {
            if (value.HasValue) AddPair(lines, label, value.Value.ToString(CultureInfo.InvariantCulture), width);
        }

        private static void AddPair(List<string> lines, string left, string right, int width)
            => lines.AddRange(ReceiptTextLayout.TwoColumnLine(left, right, width));

        private static void AddWrapped(List<string> lines, string value, int width)
        {
            if (!string.IsNullOrWhiteSpace(value)) lines.AddRange(ReceiptTextLayout.WrapText(value, width));
        }

        private static void AddCenteredWrapped(List<string> lines, string value, int width, bool upper = false)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var text = upper ? value.ToUpperInvariant() : value;
            foreach (var part in ReceiptTextLayout.WrapText(text, width)) lines.Add(ReceiptTextLayout.Center(part, width));
        }

        private static string Money(long amountMinor, string currency, CultureInfo culture)
        {
            if (string.Equals(currency, "CLP", StringComparison.OrdinalIgnoreCase))
                return amountMinor.ToString("N0", culture);
            return (amountMinor / 100m).ToString("N2", culture) + " " + currency;
        }
    }

    /// <summary>Compatibility surface; new code should render the exact string.</summary>
    public static class DailyTakingsReceiptFormatter
    {
        public static IReadOnlyList<string> Format(
            DailyTakingsReceiptModel model,
            ReceiptShopInfo shop = null,
            ReceiptOptions options = null)
        {
            return DailyCloseReceiptTextRenderer.Render(model, shop, options)
                .Replace("\r\n", "\n")
                .Split('\n')
                .ToList();
        }
    }

    public sealed class DailyTakingsReceiptModel
    {
        public DateTime Date { get; set; }
        public DateTime? PeriodStart { get; set; }
        public DateTime? PeriodEnd { get; set; }
        public string OperatorName { get; set; } = string.Empty;
        public DateTimeOffset GeneratedAt { get; set; }
        public int SalesCount { get; set; }
        public long TotalAmount { get; set; }
        public long CashAmount { get; set; }
        public long CardAmount { get; set; }
        public long GrossSalesAmount { get; set; }
        public long RefundsAmount { get; set; }
        public long NetAmount { get; set; }
        public long? DiscountsAmount { get; set; }
        public long? TaxAmount { get; set; }
        public int? MixedSalesCount { get; set; }
        public long? VoidsAmount { get; set; }
        public long? ChangeAmount { get; set; }
        public long? ExpectedCashAmount { get; set; }
        public long? OpeningAmount { get; set; }
        public long? ClosingAmount { get; set; }
        public long? DifferenceAmount { get; set; }
        public int? PendingSyncCount { get; set; }
        public int? RetrySyncCount { get; set; }
        public int? BlockedSyncCount { get; set; }
    }

    public sealed class DailyCloseReceiptLabels
    {
        public static DailyCloseReceiptLabels English => new DailyCloseReceiptLabels();
        public string BusinessDate { get; set; } = "Business date";
        public string Period { get; set; } = "Period";
        public string Operator { get; set; } = "Operator";
        public string Discounts { get; set; } = "Discounts";
        public string Tax { get; set; } = "Tax";
        public string Mixed { get; set; } = "Mixed payments";
        public string Voids { get; set; } = "Voids";
        public string ExpectedCash { get; set; } = "Expected cash";
        public string OpeningAmount { get; set; } = "Opening amount";
        public string ClosingAmount { get; set; } = "Closing amount";
        public string Difference { get; set; } = "Difference";
        public string PendingSync { get; set; } = "Pending sync";
        public string RetrySync { get; set; } = "Retry sync";
        public string BlockedSync { get; set; } = "Blocked sync";
        public string Generated { get; set; } = "Generated";
    }
}
