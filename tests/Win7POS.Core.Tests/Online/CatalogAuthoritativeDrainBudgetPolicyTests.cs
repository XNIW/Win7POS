using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class CatalogAuthoritativeDrainBudgetPolicyTests
{
    [TestMethod]
    public void CurrentManifest_UsesSequentialLaneCapacities()
    {
        var decision = Calculate(71, 102, 19763, 41228);

        Assert.IsTrue(decision.Allowed);
        Assert.AreEqual(676L, decision.ActivePageBudget);
        Assert.AreEqual(1L + 1L + 330L + 344L, decision.ActivePageBudget);
        Assert.IsTrue(decision.ActivePageBudget > 512L);
    }

    [TestMethod]
    public void EmptyInvalidAndIncompleteSummaries_FailClosed()
    {
        Assert.AreEqual(
            CatalogAuthoritativeDrainBudgetPolicy.SummaryInvalidCode,
            CatalogAuthoritativeDrainBudgetPolicy.Calculate(null).Code);

        var incomplete = Summary(1, 1, 1, 1);
        incomplete.Prices = null;
        Assert.AreEqual(
            CatalogAuthoritativeDrainBudgetPolicy.SummaryInvalidCode,
            CatalogAuthoritativeDrainBudgetPolicy.Calculate(incomplete).Code);

        var invalid = Summary(1, 1, 1, 1);
        invalid.ActiveProducts = 2;
        Assert.AreEqual(
            CatalogAuthoritativeDrainBudgetPolicy.SummaryInvalidCode,
            CatalogAuthoritativeDrainBudgetPolicy.Calculate(invalid).Code);

        var empty = Calculate(0, 0, 0, 0);
        Assert.IsTrue(empty.Allowed);
        Assert.AreEqual(1L, empty.ActivePageBudget);
    }

    [TestMethod]
    public void CheckedInt64ArithmeticOverflow_FailsWithTypedCode()
    {
        var decision = CatalogAuthoritativeDrainBudgetPolicy.Calculate(
            Summary(long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue),
            new CatalogLanePageCapacity(1, 1, 1, 1));

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual(
            CatalogAuthoritativeDrainBudgetPolicy.NumericOverflowCode,
            decision.Code);
    }

    [TestMethod]
    public void LargeFiniteManifests_HaveNoProductionPageCeiling()
    {
        var oneHundredThousandProducts = Calculate(0, 0, 100000, 0);
        Assert.IsTrue(oneHundredThousandProducts.Allowed);
        Assert.AreEqual(1667L, oneHundredThousandProducts.ActivePageBudget);

        var oneHundredThousandProductsAndPrices =
            Calculate(0, 0, 100000, 100000);
        Assert.IsTrue(oneHundredThousandProductsAndPrices.Allowed);
        Assert.AreEqual(
            2501L,
            oneHundredThousandProductsAndPrices.ActivePageBudget);

        var aboveAdminEnvelope = Calculate(0, 0, 250000, 0);
        Assert.IsTrue(aboveAdminEnvelope.Allowed);
        Assert.AreEqual(4167L, aboveAdminEnvelope.ActivePageBudget);

        var aboveFormerEmergencyCeiling = Calculate(
            100000,
            100000,
            500000,
            500000);
        Assert.IsTrue(aboveFormerEmergencyCeiling.Allowed);
        Assert.IsTrue(aboveFormerEmergencyCeiling.ActivePageBudget > 6047L);

        var veryLargeWithoutRowAllocation = Calculate(
            12_000_000_000L,
            8_000_000_000L,
            90_000_000_000L,
            120_000_000_000L);
        Assert.IsTrue(veryLargeWithoutRowAllocation.Allowed);
        Assert.AreEqual(
            50_000_000L +
            33_333_334L +
            1_500_000_000L +
            1_000_000_000L,
            veryLargeWithoutRowAllocation.ActivePageBudget);
    }

    [TestMethod]
    public void ProgressBudget_DerivesFromActiveManifestAndBoundedPerPageSla()
    {
        var drain = Calculate(71, 102, 19763, 41228);
        var budget =
            CatalogAuthoritativeDrainBudgetPolicy.CalculateProgressBudget(drain);

        Assert.IsTrue(budget.Allowed);
        Assert.AreEqual(676L, budget.ActivePageBudget);
        Assert.AreEqual(
            CatalogAuthoritativeDrainBudgetPolicy
                .NoProgressTimeoutMilliseconds,
            budget.NoProgressTimeoutMilliseconds);
        Assert.AreEqual(
            CatalogAuthoritativeDrainBudgetPolicy
                .OverallSetupAllowanceMilliseconds +
            676L * CatalogAuthoritativeDrainBudgetPolicy
                .PerActivePageSlaMilliseconds,
            budget.OverallTimeoutMilliseconds);
    }

    [TestMethod]
    public void ProgressBudgetOverflow_FailsWithTypedNumericCode()
    {
        var drain = CatalogAuthoritativeDrainBudgetPolicy.Calculate(
            Summary(long.MaxValue - 1, 0, 0, 0),
            new CatalogLanePageCapacity(1, 1, 1, 1));
        Assert.IsTrue(drain.Allowed);

        var budget =
            CatalogAuthoritativeDrainBudgetPolicy.CalculateProgressBudget(drain);

        Assert.IsFalse(budget.Allowed);
        Assert.AreEqual(
            CatalogAuthoritativeDrainBudgetPolicy.NumericOverflowCode,
            budget.Code);
    }

    private static CatalogAuthoritativeDrainDecision Calculate(
        long categories,
        long suppliers,
        long products,
        long prices)
    {
        return CatalogAuthoritativeDrainBudgetPolicy.Calculate(
            Summary(categories, suppliers, products, prices));
    }

    private static PosCatalogSummaryResponse Summary(
        long categories,
        long suppliers,
        long products,
        long prices)
    {
        return new PosCatalogSummaryResponse
        {
            ActiveProducts = products,
            Categories = categories,
            Prices = prices,
            Products = products,
            Suppliers = suppliers
        };
    }
}
