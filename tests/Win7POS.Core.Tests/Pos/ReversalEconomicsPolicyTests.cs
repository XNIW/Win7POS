using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Pos;

namespace Win7POS.Core.Tests.Pos;

[TestClass]
public sealed class ReversalEconomicsPolicyTests
{
    [TestMethod]
    public void Calculate_AllocatesDiscountAndTaxFromCumulativeGross()
    {
        var result = ReversalEconomicsPolicy.Calculate(
            Snapshot(originalGross: 100, originalDiscount: 10, originalTax: 5),
            currentGrossClp: 100);

        Assert.AreEqual(100, result.GrossClp);
        Assert.AreEqual(10, result.DiscountClp);
        Assert.AreEqual(5, result.TaxClp);
        Assert.AreEqual(-95, result.NetClp);
    }

    [TestMethod]
    public void Calculate_UsesPostgresHalfAwayFromZeroRounding()
    {
        var result = ReversalEconomicsPolicy.Calculate(
            Snapshot(originalGross: 2, originalDiscount: 1),
            currentGrossClp: 1);

        Assert.AreEqual(1, result.DiscountClp);
        Assert.AreEqual(0, result.NetClp);
    }

    [TestMethod]
    public void Calculate_SuccessivePartialsPreserveCumulativeResidual()
    {
        var first = ReversalEconomicsPolicy.Calculate(
            Snapshot(originalGross: 6, originalDiscount: 1),
            currentGrossClp: 2);
        var second = ReversalEconomicsPolicy.Calculate(
            Snapshot(
                originalGross: 6,
                originalDiscount: 1,
                priorGross: 2,
                actualPriorDiscount: first.DiscountClp),
            currentGrossClp: 2);
        var final = ReversalEconomicsPolicy.Calculate(
            Snapshot(
                originalGross: 6,
                originalDiscount: 1,
                priorGross: 4,
                actualPriorDiscount: first.DiscountClp + second.DiscountClp),
            currentGrossClp: 2);

        Assert.AreEqual(0, first.DiscountClp);
        Assert.AreEqual(-2, first.NetClp);
        Assert.AreEqual(1, second.DiscountClp);
        Assert.AreEqual(-1, second.NetClp);
        Assert.AreEqual(0, final.DiscountClp);
        Assert.AreEqual(-2, final.NetClp);
        Assert.AreEqual(1, first.DiscountClp + second.DiscountClp + final.DiscountClp);
    }

    [TestMethod]
    public void Calculate_NoDiscountOrTaxRefundsGross()
    {
        var result = ReversalEconomicsPolicy.Calculate(
            Snapshot(originalGross: 100),
            currentGrossClp: 40);

        Assert.AreEqual(-40, result.NetClp);
        Assert.AreEqual(0, result.DiscountClp);
        Assert.AreEqual(0, result.TaxClp);
    }

    [TestMethod]
    public void Calculate_FullVoidAllocatesRemainingDiscount()
    {
        var result = ReversalEconomicsPolicy.Calculate(
            Snapshot(
                originalGross: 1000,
                originalDiscount: 101,
                priorGross: 333,
                actualPriorDiscount: 34),
            currentGrossClp: 667);

        Assert.AreEqual(67, result.DiscountClp);
        Assert.AreEqual(-600, result.NetClp);
    }

    [TestMethod]
    public void Calculate_FailsClosedForIncoherentPriorAllocation()
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            ReversalEconomicsPolicy.Calculate(
                Snapshot(
                    originalGross: 6,
                    originalDiscount: 1,
                    priorGross: 2,
                    actualPriorDiscount: 1),
                currentGrossClp: 2));

        Assert.AreEqual(ReversalEconomicsPolicy.InvalidHistoryCode, error.Message);
    }

    private static ReversalEconomicsSnapshot Snapshot(
        long originalGross,
        long originalDiscount = 0,
        long originalTax = 0,
        long priorGross = 0,
        long actualPriorDiscount = 0,
        long actualPriorTax = 0)
    {
        return new ReversalEconomicsSnapshot
        {
            OriginalGrossClp = originalGross,
            OriginalDiscountClp = originalDiscount,
            OriginalTaxClp = originalTax,
            OriginalNetClp = originalGross - originalDiscount + originalTax,
            PriorGrossClp = priorGross,
            ActualPriorDiscountClp = actualPriorDiscount,
            ActualPriorTaxClp = actualPriorTax
        };
    }
}
