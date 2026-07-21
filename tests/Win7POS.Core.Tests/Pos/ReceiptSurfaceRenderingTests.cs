using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Models;
using Win7POS.Core.Receipt;
using Win7POS.Core.Reports;

namespace Win7POS.Core.Tests.Pos;

[TestClass]
public sealed class ReceiptSurfaceRenderingTests
{
    [TestMethod]
    public void SalesReceiptRenderModel_FreezesPersistedEconomicsAndShopSnapshot()
    {
        var sale = Sale(total: 14691, paidCash: 15000, paidCard: 0, change: 309);
        var line = new SaleLine
        {
            Barcode = "QA-001",
            Name = "Confezione città pingüino niño",
            Quantity = 2,
            UnitPrice = 6173,
            LineTotal = 12346
        };
        var shop = Shop();
        var input = SalesReceiptRenderModel.Create(sale, new[] { line }, shop);

        sale.Total = 1;
        sale.Code = "MUTATED";
        line.Name = "MUTATED PRODUCT";
        line.LineTotal = 1;
        shop.Name = "MUTATED SHOP";

        var rendered = ReceiptFormatter.Format(input, ReceiptOptions.Default42Clp());
        var text = string.Join("\n", rendered);

        StringAssert.Contains(text, "VMRQI-RECEIPT");
        StringAssert.Contains(text, "Confezione città pingüino niño");
        StringAssert.Contains(text, "QA SNAPSHOT SHOP");
        Assert.IsFalse(text.Contains("MUTATED", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SalesReceipt_CashCardMixedDiscountRefundVoidAndLongText_Fit32And42()
    {
        var variants = new[]
        {
            Sale(total: 14691, paidCash: 15000, paidCard: 0, change: 309),
            Sale(total: 14691, paidCash: 0, paidCard: 14691, change: 0),
            Sale(total: 14691, paidCash: 4691, paidCard: 10000, change: 0),
            Sale(total: -12000, paidCash: -12000, paidCard: 0, change: 0, kind: SaleKind.Refund),
            Sale(total: -12000, paidCash: 0, paidCard: -12000, change: 0, kind: SaleKind.Void)
        };
        var lines = new[]
        {
            new SaleLine
            {
                Barcode = "CURRENT-PRICE-MUST-NOT-BE-USED",
                Name = "Prodotto con una descrizione persistita molto lunga 中文 español italiano",
                Quantity = 2,
                UnitPrice = 6173,
                LineTotal = 12346
            },
            new SaleLine
            {
                Barcode = "DISC:CART:10",
                Name = "Sconto carrello persistito",
                Quantity = 1,
                UnitPrice = -1234,
                LineTotal = -1234
            }
        };

        foreach (var width in new[] { 32, 42 })
        foreach (var culture in new[] { "en-US", "es-CL", "it-IT", "zh-CN" })
        foreach (var sale in variants)
        {
            var options = new ReceiptOptions
            {
                Width = width,
                Currency = "CLP",
                CultureName = culture,
                Labels = LongLabels()
            };
            var rendered = ReceiptFormatter.Format(
                SalesReceiptRenderModel.Create(sale, lines, Shop()),
                options);

            Assert.IsTrue(rendered.Count > 10);
            Assert.IsTrue(rendered.All(line => ReceiptTextLayout.VisibleWidth(line) <= width),
                "Receipt overflowed " + width + " columns for " + culture + ":\n" +
                string.Join("\n", rendered));
        }
    }

    [TestMethod]
    public void ReceiptTextLayout_WrapAndTwoColumns_PreserveValuesWithoutOverflow()
    {
        foreach (var width in new[] { 32, 42 })
        {
            var wrapped = ReceiptTextLayout.WrapText(
                "Etichetta estremamente lunga 中文 información città qualità",
                width);
            var paired = ReceiptTextLayout.TwoColumnLine(
                "Differenza di cassa negativa estremamente lunga",
                "-9.876.543.210",
                width);

            Assert.IsTrue(wrapped.All(line => ReceiptTextLayout.VisibleWidth(line) <= width));
            Assert.IsTrue(paired.All(line => ReceiptTextLayout.VisibleWidth(line) <= width));
            StringAssert.Contains(string.Join("\n", paired), "-9.876.543.210");
        }
    }

    [TestMethod]
    public void DailyClose_PreviewAndPrintTextAreDeterministicAndFit32And42()
    {
        var model = DailyModel();
        var shop = Shop();
        shop.Name = "QA 日常营业结算商店名称非常长";
        shop.Address = "Avenida internacional de validación extremadamente larga 12345";
        shop.Footer = "Grazie · Gracias · 谢谢";

        foreach (var width in new[] { 32, 42 })
        foreach (var culture in new[] { "en-US", "es-CL", "it-IT", "zh-CN" })
        {
            var options = new ReceiptOptions
            {
                Width = width,
                Currency = "CLP",
                CultureName = culture,
                Labels = LongLabels()
            };
            var labels = LongDailyLabels();
            var preview = DailyCloseReceiptTextRenderer.Render(model, shop, options, labels);
            var printed = DailyCloseReceiptTextRenderer.Render(model, shop, options, labels);

            Assert.AreEqual(preview, printed);
            Assert.IsTrue(preview.SplitLines().All(line => ReceiptTextLayout.VisibleWidth(line) <= width),
                "Daily close overflowed " + width + " columns for " + culture + ":\n" + preview);
            var digitsOnly = new string(preview.Where(character =>
                char.IsDigit(character) || character == '-').ToArray());
            StringAssert.Contains(digitsOnly, "-2222222");
            StringAssert.Contains(digitsOnly, "9876543210");
        }
    }

    [TestMethod]
    public void FiscalBoleta_PreviewAndPrintTextAreDeterministicCompleteAndFit32And42()
    {
        var createdAtMs = new DateTimeOffset(2020, 6, 15, 12, 30, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
        var expectedDate = DateTimeOffset.FromUnixTimeMilliseconds(createdAtMs)
            .ToLocalTime()
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var shop = Shop();
        shop.Name = "Negozio fiscale estremamente lungo 中文 pingüino città qualità";
        shop.Rut = "RUT 76.123.456-7 verifica";
        shop.BusinessGiro = "Commercio internazionale e servizi de alimentación 中文";
        shop.LegalRepresentativeRut = "12.345.678-9 rappresentante";
        shop.Address = "Avenida internacional de validación extremadamente larga 12345";
        shop.City = "Santiago metropolitana 中文";

        foreach (var width in new[] { 32, 42 })
        {
            var preview = FiscalBoletaTextRenderer.Render(
                shop,
                createdAtMs,
                int.MaxValue,
                long.MaxValue,
                1_472_334_122_581_234_567,
                width);
            var printed = FiscalBoletaTextRenderer.Render(
                shop,
                createdAtMs,
                int.MaxValue,
                long.MaxValue,
                1_472_334_122_581_234_567,
                width);

            Assert.AreEqual(preview, printed);
            Assert.IsTrue(preview.SplitLines().All(line => ReceiptTextLayout.VisibleWidth(line) <= width),
                "Fiscal boleta overflowed " + width + " columns:\n" + preview);
            StringAssert.Contains(preview, expectedDate);
            StringAssert.Contains(preview, "2.147.483.647");
            StringAssert.Contains(preview, "9.223.372.036.854.775.807");
            StringAssert.Contains(preview, "1.472.334.122.581.234.567");
            Assert.AreEqual(
                1,
                preview.SplitLines().Count(line =>
                    string.Equals(line, FiscalBoletaTextRenderer.SiiStampMarker, StringComparison.Ordinal)));
        }
    }

    private static Sale Sale(
        long total,
        long paidCash,
        long paidCard,
        long change,
        SaleKind kind = SaleKind.Sale)
    {
        return new Sale
        {
            Id = 7,
            Code = "VMRQI-RECEIPT",
            CreatedAt = new DateTimeOffset(2026, 7, 17, 20, 15, 0, TimeSpan.Zero)
                .ToUnixTimeMilliseconds(),
            Kind = (int)kind,
            Total = total,
            PaidCash = paidCash,
            PaidCard = paidCard,
            Change = change
        };
    }

    private static ReceiptShopInfo Shop()
    {
        return new ReceiptShopInfo
        {
            Name = "QA Snapshot Shop",
            Address = "Snapshot Avenue 1",
            City = "Santiago",
            Rut = "76.123.456-7",
            Phone = "+56 2 1234 5678",
            Footer = "Grazie · Gracias · 谢谢"
        };
    }

    private static ReceiptLabels LongLabels()
    {
        return new ReceiptLabels
        {
            Receipt = "Scontrino / Recibo / 收据",
            DateTime = "Data e ora / Fecha y hora",
            Items = "Articoli acquistati",
            Subtotal = "Subtotale prima degli sconti",
            TotalDiscounts = "Sconti complessivi applicati",
            Discount = "Sconto",
            CartDiscount = "Sconto carrello",
            Line = "Totale riga",
            Total = "Totale complessivo",
            Cash = "Pagamento in contanti",
            Card = "Pagamento con carta",
            Change = "Resto",
            Gross = "Vendite lorde",
            Refunds = "Resi e storni",
            Net = "Totale netto",
            SalesCountShort = "Numero scontrini",
            Thanks = "Grazie"
        };
    }

    private static DailyTakingsReceiptModel DailyModel()
    {
        return new DailyTakingsReceiptModel
        {
            Date = new DateTime(2026, 7, 17),
            PeriodStart = new DateTime(2026, 7, 17),
            PeriodEnd = new DateTime(2026, 7, 17),
            OperatorName = "Operatore QA con nome volutamente molto lungo",
            GeneratedAt = new DateTimeOffset(2026, 7, 17, 22, 30, 0, TimeSpan.Zero),
            SalesCount = 987654,
            GrossSalesAmount = 9876543210,
            DiscountsAmount = 123456789,
            TaxAmount = 187654321,
            RefundsAmount = 87654321,
            VoidsAmount = 7654321,
            NetAmount = 9543209876,
            CashAmount = 4321098765,
            CardAmount = 5222111111,
            MixedSalesCount = 12345,
            ChangeAmount = 1234567,
            OpeningAmount = 100000,
            ClosingAmount = 4320000000,
            ExpectedCashAmount = 4322222222,
            DifferenceAmount = -2222222,
            PendingSyncCount = 123,
            RetrySyncCount = 45,
            BlockedSyncCount = 6
        };
    }

    private static DailyCloseReceiptLabels LongDailyLabels()
    {
        return new DailyCloseReceiptLabels
        {
            BusinessDate = "Data commerciale / Fecha comercial / 营业日期",
            Period = "Periodo di rendicontazione estremamente lungo",
            Operator = "Operatore responsabile / 操作员",
            Discounts = "Sconti complessivi applicati",
            Tax = "Imposte",
            Mixed = "Pagamenti misti",
            Voids = "Annullamenti",
            ExpectedCash = "Contanti attesi nel cassetto",
            OpeningAmount = "Importo iniziale",
            ClosingAmount = "Importo di chiusura",
            Difference = "Differenza negativa",
            PendingSync = "Sincronizzazioni in attesa",
            RetrySync = "Sincronizzazioni da riprovare",
            BlockedSync = "Sincronizzazioni bloccate",
            Generated = "Generato il"
        };
    }
}

internal static class ReceiptTestStringExtensions
{
    internal static IEnumerable<string> SplitLines(this string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}
