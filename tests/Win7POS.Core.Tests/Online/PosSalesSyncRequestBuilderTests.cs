using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Pos;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class PosSalesSyncRequestBuilderTests
{
    [TestMethod]
    public void BuildCanonical_EmitsClientOriginalLineIdForRefundLine()
    {
        var request = PosSalesSyncRequestBuilder.BuildCanonical(
            Item("refund"),
            Sale(SaleKind.Refund),
            new[] { Line(relatedOriginalLineId: 44) },
            new Dictionary<long, string>(),
            Economics(gross: 1000, net: -1000));

        var line = request.Sales.Single().Lines.Single();
        Assert.AreEqual("line-44", line.ClientOriginalLineId);
        StringAssert.Contains(
            PosSalesSyncRequestBuilder.SerializeCanonical(request),
            "\"clientOriginalLineId\":\"line-44\"");
    }

    [TestMethod]
    public void BuildCanonical_FailsClosedForLegacyReversalWithoutOriginalLine()
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            PosSalesSyncRequestBuilder.BuildCanonical(
                Item("void"),
                Sale(SaleKind.Void),
                new[] { Line(relatedOriginalLineId: null) },
                new Dictionary<long, string>()));

        Assert.AreEqual("reversal_original_line_missing", error.Message);
    }

    [TestMethod]
    public void BuildCanonical_OmitsClientOriginalLineIdForNormalSale()
    {
        var request = PosSalesSyncRequestBuilder.BuildCanonical(
            Item("sale"),
            Sale(SaleKind.Sale),
            new[] { Line(relatedOriginalLineId: null) },
            new Dictionary<long, string>());

        Assert.IsNull(request.Sales.Single().Lines.Single().ClientOriginalLineId);
        Assert.IsFalse(
            PosSalesSyncRequestBuilder.SerializeCanonical(request).Contains(
                "clientOriginalLineId",
                StringComparison.Ordinal));
    }

    [TestMethod]
    public void HasCompleteReversalBindings_RejectsLegacyPersistedPayload()
    {
        var legacy = PosSalesSyncRequestBuilder.BuildCanonical(
            Item("sale"),
            Sale(SaleKind.Sale),
            new[] { Line(relatedOriginalLineId: null) },
            new Dictionary<long, string>());
        legacy.Sales.Single().Kind = "refund";
        legacy.Sales.Single().ClientOriginalSaleId = "win7pos-sale-1";

        Assert.IsFalse(PosSalesSyncRequestBuilder.HasCompleteReversalBindings(legacy));
    }

    [TestMethod]
    public void BuildCanonical_ReversalIsItemOnlyWithAllocatedHeaderAndExactPayment()
    {
        var sale = Sale(SaleKind.Refund);
        sale.Total = -950;
        sale.PaidCash = -600;
        sale.PaidCard = -350;

        var request = PosSalesSyncRequestBuilder.BuildCanonical(
            Item("refund"),
            sale,
            new[] { Line(relatedOriginalLineId: 44) },
            new Dictionary<long, string>(),
            Economics(gross: 1000, discount: 100, tax: 50, net: -950));

        var syncedSale = request.Sales.Single();
        Assert.IsTrue(syncedSale.Lines.All(line => line.LineType == "item"));
        Assert.AreEqual(1000, syncedSale.Amounts.GrossClp);
        Assert.AreEqual(100, syncedSale.Amounts.DiscountClp);
        Assert.AreEqual(50, syncedSale.Amounts.TaxClp);
        Assert.AreEqual(-950, syncedSale.Amounts.NetClp);
        Assert.AreEqual(-950, syncedSale.Amounts.PaidClp);
        Assert.AreEqual(-950, syncedSale.Payments.Sum(payment => payment.AmountClp));
        Assert.IsTrue(PosSalesSyncRequestBuilder.HasExpectedReversalEconomics(
            request,
            Economics(gross: 1000, discount: 100, tax: 50, net: -950)));
    }

    [TestMethod]
    public void BuildCanonical_RejectsPseudoAdjustmentInReversal()
    {
        var line = Line(relatedOriginalLineId: 44);
        line.Barcode = DiscountKeys.Prefix + "CART";

        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            PosSalesSyncRequestBuilder.BuildCanonical(
                Item("refund"),
                Sale(SaleKind.Refund),
                new[] { line },
                new Dictionary<long, string>(),
                Economics(gross: 1000, net: -1000)));

        Assert.AreEqual(ReversalEconomicsPolicy.MismatchCode, error.Message);
    }

    [TestMethod]
    public void BuildCanonical_RejectsReversalPaymentDifferentFromNet()
    {
        var sale = Sale(SaleKind.Void);
        sale.PaidCash = -999;

        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            PosSalesSyncRequestBuilder.BuildCanonical(
                Item("void"),
                sale,
                new[] { Line(relatedOriginalLineId: 44) },
                new Dictionary<long, string>(),
                Economics(gross: 1000, net: -1000)));

        Assert.AreEqual(ReversalEconomicsPolicy.MismatchCode, error.Message);
    }

    [TestMethod]
    public void BuildCanonical_NormalSaleCarriesDiscountAndTaxHeader()
    {
        var sale = Sale(SaleKind.Sale);
        sale.Total = 950;
        sale.PaidCash = 950;
        var discount = Line(relatedOriginalLineId: null);
        discount.Barcode = DiscountKeys.CartPrefix + "TEST";
        discount.UnitPrice = -100;
        discount.LineTotal = -100;
        var tax = Line(relatedOriginalLineId: null);
        tax.Barcode = DiscountKeys.TaxPrefix + "TEST";
        tax.UnitPrice = 50;
        tax.LineTotal = 50;

        var request = PosSalesSyncRequestBuilder.BuildCanonical(
            Item("sale"),
            sale,
            new[] { Line(relatedOriginalLineId: null), discount, tax },
            new Dictionary<long, string>());

        var amounts = request.Sales.Single().Amounts;
        Assert.AreEqual(1000, amounts.GrossClp);
        Assert.AreEqual(100, amounts.DiscountClp);
        Assert.AreEqual(50, amounts.TaxClp);
        Assert.AreEqual(950, amounts.NetClp);
    }

    private static SalesSyncOutboxItem Item(string operation)
    {
        return new SalesSyncOutboxItem
        {
            ClientSaleId = "win7pos-sale-2",
            IdempotencyKey = "win7pos-sale-2:" + PosOnlineContract.SalesSchemaVersion,
            OperationType = operation,
            OriginShopCode = "SHOP-1",
            OriginShopId = "shop-1",
            SchemaVersion = PosOnlineContract.SalesSchemaVersion
        };
    }

    private static Sale Sale(SaleKind kind)
    {
        return new Sale
        {
            ClientSaleId = "win7pos-sale-2",
            Code = "TEST-2",
            CreatedAt = DateTimeOffset.Parse("2026-07-15T12:00:00Z").ToUnixTimeMilliseconds(),
            Kind = (int)kind,
            PaidCash = kind == SaleKind.Sale ? 1000 : -1000,
            RelatedSaleId = kind == SaleKind.Sale ? null : 1,
            Total = kind == SaleKind.Sale ? 1000 : -1000
        };
    }

    private static SaleLine Line(long? relatedOriginalLineId)
    {
        return new SaleLine
        {
            Barcode = "TEST-001",
            Id = 91,
            LineTotal = 1000,
            Name = "Test",
            Quantity = 1,
            RelatedOriginalLineId = relatedOriginalLineId,
            UnitPrice = 1000
        };
    }

    private static ReversalEconomicsResult Economics(
        long gross,
        long discount = 0,
        long tax = 0,
        long net = 0)
    {
        return new ReversalEconomicsResult
        {
            GrossClp = gross,
            DiscountClp = discount,
            TaxClp = tax,
            NetClp = net
        };
    }
}
