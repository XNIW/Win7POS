using System;
using Win7POS.Core.Receipt;
using Win7POS.Core.Reports;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos
{
    /// <summary>Localized WPF entry point for the dedicated daily-close renderer.</summary>
    internal static class DailyCloseReceiptTextRenderer
    {
        internal static string Render(
            DateTime date,
            DailySalesSummary summary,
            ReceiptShopInfo shop,
            bool use42Columns)
        {
            if (summary == null) throw new ArgumentNullException(nameof(summary));
            var model = new DailyTakingsReceiptModel
            {
                Date = date,
                PeriodStart = date.Date,
                PeriodEnd = date.Date,
                GeneratedAt = DateTimeOffset.Now,
                SalesCount = summary.SalesCount,
                TotalAmount = summary.TotalAmount,
                CashAmount = summary.CashAmount,
                CardAmount = summary.CardAmount,
                GrossSalesAmount = summary.GrossSalesAmount,
                RefundsAmount = summary.RefundsAmount,
                NetAmount = summary.NetAmount
            };

            return Win7POS.Core.Reports.DailyCloseReceiptTextRenderer.Render(
                model,
                shop ?? new ReceiptShopInfo(),
                PosLocalization.CreateReceiptOptions(use42Columns, "reports.closeTitle"),
                CreateLabels());
        }

        private static DailyCloseReceiptLabels CreateLabels()
        {
            return new DailyCloseReceiptLabels
            {
                BusinessDate = PosLocalization.T("reports.receiptBusinessDate"),
                Period = PosLocalization.T("reports.receiptPeriod"),
                Operator = PosLocalization.T("common.operator"),
                Discounts = PosLocalization.T("receipt.totalDiscounts"),
                Tax = PosLocalization.T("reports.receiptTax"),
                Mixed = PosLocalization.T("reports.receiptMixed"),
                Voids = PosLocalization.T("sales.kind.void"),
                ExpectedCash = PosLocalization.T("reports.receiptExpectedCash"),
                OpeningAmount = PosLocalization.T("reports.receiptOpening"),
                ClosingAmount = PosLocalization.T("reports.receiptClosing"),
                Difference = PosLocalization.T("reports.receiptDifference"),
                PendingSync = PosLocalization.T("reports.receiptPendingSync"),
                RetrySync = PosLocalization.T("reports.receiptRetrySync"),
                BlockedSync = PosLocalization.T("reports.receiptBlockedSync"),
                Generated = PosLocalization.T("reports.receiptGenerated")
            };
        }
    }
}
