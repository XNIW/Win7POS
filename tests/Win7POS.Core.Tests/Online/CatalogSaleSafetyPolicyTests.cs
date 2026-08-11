using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class CatalogSaleSafetyPolicyTests
{
    [TestMethod]
    public void Evaluate_AllowsLegacyUnboundOnlyForOrdinarySale()
    {
        var snapshot = Snapshot(boundShopId: string.Empty, boundShopCode: string.Empty);

        var ordinarySale = CatalogSaleSafetyPolicy.Evaluate(snapshot, allowLegacyUnbound: true);
        var officialReadiness = CatalogSaleSafetyPolicy.Evaluate(snapshot, allowLegacyUnbound: false);

        Assert.IsTrue(ordinarySale.IsSaleSafe);
        Assert.IsFalse(ordinarySale.IsCatalogBound);
        Assert.AreEqual(CatalogSaleSafetyPolicy.LegacyUnboundSafeCode, ordinarySale.ReasonCode);
        Assert.IsFalse(officialReadiness.IsSaleSafe);
        Assert.IsFalse(officialReadiness.IsCatalogBound);
        Assert.AreEqual(CatalogSaleSafetyPolicy.NotBoundCode, officialReadiness.ReasonCode);
    }

    [TestMethod]
    public void Evaluate_FailsClosedForEachUnsafeCatalogState()
    {
        var cases = new[]
        {
            new EvaluationCase(
                "partial catalog binding",
                Snapshot(boundShopCode: string.Empty),
                CatalogSaleSafetyPolicy.BindingPartialCode),
            new EvaluationCase(
                "partial official binding",
                Snapshot(officialShopCode: string.Empty),
                CatalogSaleSafetyPolicy.OfficialShopPartialCode),
            new EvaluationCase(
                "official binding mismatch",
                Snapshot(officialShopCode: "SHOP-OTHER"),
                CatalogSaleSafetyPolicy.ShopMismatchCode),
            new EvaluationCase(
                "invalid repair flag",
                Snapshot(repairRequired: "true"),
                CatalogSaleSafetyPolicy.RepairStateInvalidCode),
            new EvaluationCase(
                "required repair",
                Snapshot(repairRequired: "1"),
                CatalogSaleSafetyPolicy.RepairRequiredCode),
            new EvaluationCase(
                "missing sale-safe marker",
                Snapshot(saleSafeAt: " "),
                CatalogSaleSafetyPolicy.NotSaleSafeCode),
            new EvaluationCase(
                "missing exactness",
                Snapshot(exactnessStatus: " "),
                CatalogSaleSafetyPolicy.ExactnessMissingCode),
            new EvaluationCase(
                "invalid exactness",
                Snapshot(exactnessStatus: "not-an-exactness-status"),
                CatalogSaleSafetyPolicy.ExactnessNotVerifiedCode),
            new EvaluationCase(
                "unverified exactness",
                Snapshot(exactnessStatus: "Unverified"),
                CatalogSaleSafetyPolicy.ExactnessNotVerifiedCode),
            new EvaluationCase(
                "partial exactness binding",
                Snapshot(exactnessShopCode: string.Empty),
                CatalogSaleSafetyPolicy.ExactnessBindingPartialCode),
            new EvaluationCase(
                "exactness binding mismatch",
                Snapshot(exactnessShopCode: "SHOP-OTHER"),
                CatalogSaleSafetyPolicy.ExactnessShopMismatchCode)
        };

        foreach (var testCase in cases)
        {
            var decision = CatalogSaleSafetyPolicy.Evaluate(
                testCase.Snapshot,
                allowLegacyUnbound: false);

            Assert.IsFalse(decision.IsSaleSafe, testCase.Name);
            Assert.IsTrue(decision.IsCatalogBound, testCase.Name);
            Assert.AreEqual(testCase.ExpectedCode, decision.ReasonCode, testCase.Name);
        }
    }

    [TestMethod]
    public void Evaluate_PreservesEnumParsingAndShopComparisonSemantics()
    {
        var snapshot = Snapshot(
            boundShopId: " shop-1 ",
            boundShopCode: " shop-safe ",
            officialShopId: "SHOP-1",
            officialShopCode: "SHOP-SAFE",
            exactnessStatus: "1",
            exactnessShopId: "SHOP-1",
            exactnessShopCode: "SHOP-SAFE");

        var decision = CatalogSaleSafetyPolicy.Evaluate(snapshot, allowLegacyUnbound: false);

        Assert.IsTrue(decision.IsSaleSafe);
        Assert.IsTrue(decision.IsCatalogBound);
        Assert.AreEqual(CatalogSaleSafetyPolicy.SafeCode, decision.ReasonCode);
    }

    [TestMethod]
    public void Evaluate_RejectsWhitespaceWrappedRepairFlag()
    {
        var decision = CatalogSaleSafetyPolicy.Evaluate(
            Snapshot(repairRequired: " 1 "),
            allowLegacyUnbound: false);

        Assert.IsFalse(decision.IsSaleSafe);
        Assert.AreEqual(CatalogSaleSafetyPolicy.RepairStateInvalidCode, decision.ReasonCode);
    }

    private static CatalogSaleSafetySnapshot Snapshot(
        string boundShopId = "shop-1",
        string boundShopCode = "SHOP-SAFE",
        string officialShopId = "shop-1",
        string officialShopCode = "SHOP-SAFE",
        string repairRequired = "0",
        string saleSafeAt = "2026-07-22T00:00:00Z",
        string exactnessStatus = "Verified",
        string exactnessShopId = "shop-1",
        string exactnessShopCode = "SHOP-SAFE")
    {
        return new CatalogSaleSafetySnapshot(
            boundShopId,
            boundShopCode,
            officialShopId,
            officialShopCode,
            repairRequired,
            saleSafeAt,
            exactnessStatus,
            exactnessShopId,
            exactnessShopCode);
    }

    private sealed class EvaluationCase
    {
        public EvaluationCase(
            string name,
            CatalogSaleSafetySnapshot snapshot,
            string expectedCode)
        {
            Name = name;
            Snapshot = snapshot;
            ExpectedCode = expectedCode;
        }

        public string ExpectedCode { get; }
        public string Name { get; }
        public CatalogSaleSafetySnapshot Snapshot { get; }
    }
}
