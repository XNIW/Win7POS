using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class CatalogPaginationSafetyPolicyTests
{
    private const int Limit = 1000;

    [TestMethod]
    public void SaturatedTerminalFull_RejectsMissingVersionOrSummary()
    {
        var response = Full(products: Limit, prices: 0, hasMore: false, summaryProducts: null);
        AssertAmbiguous(response);

        response = Full(products: Limit, prices: 0, hasMore: false, summaryProducts: Limit);
        response.CatalogVersion = string.Empty;
        AssertAmbiguous(response);
    }

    [TestMethod]
    public void SaturatedTerminalFull_RejectsContradictoryOrIncompleteSummary()
    {
        AssertAmbiguous(Full(Limit, 0, false, 19763));

        var incomplete = Full(Limit, 0, false, Limit);
        incomplete.CatalogSummary.Prices = null;
        AssertAmbiguous(incomplete);
    }

    [TestMethod]
    public void SaturatedTerminalFull_AcceptsExactAuthoritativeSummary()
    {
        var response = Full(Limit, Limit, false, Limit, Limit);
        var decision = Evaluate(response);

        Assert.IsTrue(decision.Allowed);
    }

    [TestMethod]
    public void GuardUsesIndependentLaneCapacityAndTombstones()
    {
        AssertAmbiguous(Full(Limit, Limit, false, summaryProducts: null));

        var tombstoneResponse = Full(990, 0, false, 990);
        tombstoneResponse.Catalog.Tombstones.Products = Enumerable
            .Range(0, 10)
            .Select(_ => new PosCatalogProductTombstoneResponse())
            .ToArray();
        AssertAmbiguous(tombstoneResponse);
    }

    [TestMethod]
    public void UnsaturatedTerminalFull_AcceptsTombstonesWhenActiveSummaryMatches()
    {
        var response = Full(989, 0, false, 989);
        response.Catalog.Tombstones.Products = Enumerable
            .Range(0, 10)
            .Select(index => new PosCatalogProductTombstoneResponse
            {
                ProductId = "removed-" + index.ToString()
            })
            .ToArray();

        var decision = Evaluate(response);

        Assert.IsTrue(decision.Allowed);
    }

    [TestMethod]
    public void TerminalFullWithoutCompleteSummary_IsRejectedBeforePromotion()
    {
        var terminal = Evaluate(Full(999, 0, false, summaryProducts: null));
        Assert.IsFalse(terminal.Allowed);
        Assert.AreEqual(CatalogPaginationSafetyPolicy.AmbiguousEndCode, terminal.Code);
        Assert.IsTrue(Evaluate(Full(Limit, 0, true, summaryProducts: null)).Allowed);

        var delta = Full(Limit, 0, false, summaryProducts: null);
        delta.SyncMode = "delta";
        Assert.IsTrue(CatalogPaginationSafetyPolicy.EvaluateTerminalPage(
            delta,
            Limit,
            fullSnapshotExpected: false,
            receivedBeforePage: EmptyCounts()).Allowed);
    }

    [TestMethod]
    public void FullHasMore_AllowsActiveSummarySatisfiedButRejectsBelowCumulativeEvidence()
    {
        var alreadyComplete = Full(Limit, 0, true, summaryProducts: Limit);
        Assert.IsTrue(Evaluate(alreadyComplete).Allowed);

        var belowCumulative = Full(500, 0, true, summaryProducts: Limit);
        var decision = CatalogPaginationSafetyPolicy.EvaluateTerminalPage(
            belowCumulative,
            Limit,
            fullSnapshotExpected: true,
            receivedBeforePage: new CatalogPaginationLaneCounts(Limit, 0, 0, 0),
            cumulativeEvidence: new CatalogPaginationLaneCounts(1500, 0, 0, 0));

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual(CatalogPaginationSafetyPolicy.AmbiguousEndCode, decision.Code);
    }

    [TestMethod]
    public void FullHasMore_TombstonesCanContinueBeyondActiveSummaryBudget()
    {
        var firstPage = Full(Limit, 0, true, summaryProducts: Limit);
        var firstEvidence = new CatalogPaginationLaneCounts(
            products: Limit,
            categories: 0,
            suppliers: 0,
            prices: 0);

        var firstDecision = CatalogPaginationSafetyPolicy.EvaluateTerminalPage(
            firstPage,
            Limit,
            fullSnapshotExpected: true,
            receivedBeforePage: EmptyCounts(),
            cumulativeEvidence: firstEvidence);
        var activeOnlyBudget = Budget(Limit, 0).PageBudget;
        var expandedBudget = CatalogPaginationSafetyPolicy
            .ExpandFullPageBudgetForTombstoneContinuation(
                activeOnlyBudget,
                hardCeilingPages: 512,
                fullSnapshot: true,
                hasMore: true,
                cumulativeEvidence: firstEvidence,
                summary: firstPage.CatalogSummary);

        Assert.IsTrue(firstDecision.Allowed);
        Assert.AreEqual(1, activeOnlyBudget);
        Assert.AreEqual(512, expandedBudget);

        var terminalPage = Full(0, 0, false, summaryProducts: Limit);
        terminalPage.Catalog.Tombstones.Products = new[]
        {
            new PosCatalogProductTombstoneResponse { ProductId = "removed-1" }
        };
        var terminalDecision = CatalogPaginationSafetyPolicy.EvaluateTerminalPage(
            terminalPage,
            Limit,
            fullSnapshotExpected: true,
            receivedBeforePage: firstEvidence,
            cumulativeEvidence: new CatalogPaginationLaneCounts(
                products: Limit,
                categories: 0,
                suppliers: 0,
                prices: 0,
                productTombstones: 1),
            pageAfterContinuation: true);

        Assert.IsTrue(terminalDecision.Allowed);

        var unexplainedTerminal = CatalogPaginationSafetyPolicy.EvaluateTerminalPage(
            Full(0, 0, false, summaryProducts: Limit),
            Limit,
            fullSnapshotExpected: true,
            receivedBeforePage: firstEvidence,
            cumulativeEvidence: firstEvidence,
            pageAfterContinuation: true);
        Assert.IsFalse(unexplainedTerminal.Allowed);
        Assert.AreEqual(
            CatalogPaginationSafetyPolicy.AmbiguousEndCode,
            unexplainedTerminal.Code);
    }

    [TestMethod]
    public void TerminalFull_UsesDistinctCumulativeEvidenceForRepeatedReferenceRows()
    {
        var terminal = Full(763, 763, false, summaryProducts: 1763, summaryPrices: 1763);
        terminal.Catalog.Categories = new[] { new PosCatalogCategoryResponse() };
        terminal.Catalog.Suppliers = new[] { new PosCatalogSupplierResponse() };
        terminal.CatalogSummary.Categories = 1;
        terminal.CatalogSummary.Suppliers = 1;

        var decision = CatalogPaginationSafetyPolicy.EvaluateTerminalPage(
            terminal,
            Limit,
            fullSnapshotExpected: true,
            receivedBeforePage: new CatalogPaginationLaneCounts(1000, 1000, 1000, 1000),
            cumulativeEvidence: new CatalogPaginationLaneCounts(1763, 1, 1, 1763));

        Assert.IsTrue(decision.Allowed);
    }

    [TestMethod]
    public void FullLaneEvidence_AllowsIdenticalRepeatedReferencesAndRejectsConflicts()
    {
        var tracker = new CatalogFullLaneEvidenceTracker();
        var first = tracker.Add(References("Category", "Supplier", "2026-07-19T01:00:00Z"));
        var second = tracker.Add(References(" Category ", " Supplier ", " 2026-07-19T01:00:00Z "));

        Assert.AreEqual(1L, first.Categories);
        Assert.AreEqual(1L, second.Categories);
        Assert.AreEqual(1L, second.Suppliers);
        Assert.AreEqual(string.Empty, tracker.ConflictCode);
        CollectionAssert.AreEqual(new[] { "category-1" }, tracker.CategoryIds.ToArray());
        CollectionAssert.AreEqual(new[] { "supplier-1" }, tracker.SupplierIds.ToArray());

        tracker.Add(References("Changed category", "Supplier", "2026-07-19T01:00:00Z"));
        Assert.AreEqual(CatalogFullLaneEvidenceTracker.CategoryConflictCode, tracker.ConflictCode);

        var supplierConflict = new CatalogFullLaneEvidenceTracker();
        supplierConflict.Add(References("Category", "Supplier", "2026-07-19T01:00:00Z"));
        supplierConflict.Add(References("Category", "Changed supplier", "2026-07-19T01:00:00Z"));
        Assert.AreEqual(
            CatalogFullLaneEvidenceTracker.SupplierConflictCode,
            supplierConflict.ConflictCode);
    }

    [TestMethod]
    public void FullLaneEvidence_RejectsActiveTombstoneOverlapBeforePromotion()
    {
        var samePageProduct = new CatalogFullLaneEvidenceTracker();
        samePageProduct.Add(new PosCatalogPayload
        {
            Products = new[]
            {
                new PosCatalogProductResponse { ProductId = "product-1" }
            },
            Tombstones = new PosCatalogTombstonesResponse
            {
                Products = new[]
                {
                    new PosCatalogProductTombstoneResponse { ProductId = "product-1" }
                }
            }
        });
        Assert.AreEqual(
            CatalogFullLaneEvidenceTracker.ProductActiveTombstoneConflictCode,
            samePageProduct.ConflictCode);

        var categoryAcrossPages = new CatalogFullLaneEvidenceTracker();
        categoryAcrossPages.Add(new PosCatalogPayload
        {
            Categories = new[]
            {
                new PosCatalogCategoryResponse { CategoryId = "category-1" }
            }
        });
        categoryAcrossPages.Add(new PosCatalogPayload
        {
            Tombstones = new PosCatalogTombstonesResponse
            {
                Categories = new[]
                {
                    new PosCatalogCategoryTombstoneResponse { CategoryId = "category-1" }
                }
            }
        });
        Assert.AreEqual(
            CatalogFullLaneEvidenceTracker.CategoryActiveTombstoneConflictCode,
            categoryAcrossPages.ConflictCode);

        var supplierAcrossPages = new CatalogFullLaneEvidenceTracker();
        supplierAcrossPages.Add(new PosCatalogPayload
        {
            Tombstones = new PosCatalogTombstonesResponse
            {
                Suppliers = new[]
                {
                    new PosCatalogSupplierTombstoneResponse { SupplierId = "supplier-1" }
                }
            }
        });
        supplierAcrossPages.Add(new PosCatalogPayload
        {
            Suppliers = new[]
            {
                new PosCatalogSupplierResponse { SupplierId = "supplier-1" }
            }
        });
        Assert.AreEqual(
            CatalogFullLaneEvidenceTracker.SupplierActiveTombstoneConflictCode,
            supplierAcrossPages.ConflictCode);
    }

    [TestMethod]
    public void FullLaneEvidence_RejectsChangedTombstoneMetadataAcrossPages()
    {
        var tracker = new CatalogFullLaneEvidenceTracker();
        tracker.Add(new PosCatalogPayload
        {
            Tombstones = new PosCatalogTombstonesResponse
            {
                Products = new[]
                {
                    new PosCatalogProductTombstoneResponse
                    {
                        ProductId = "product-1",
                        DeletedAt = "2026-07-19T01:00:00Z",
                        UpdatedAt = "2026-07-19T01:00:00Z"
                    }
                }
            }
        });
        tracker.Add(new PosCatalogPayload
        {
            Tombstones = new PosCatalogTombstonesResponse
            {
                Products = new[]
                {
                    new PosCatalogProductTombstoneResponse
                    {
                        ProductId = "product-1",
                        DeletedAt = "2026-07-19T02:00:00Z",
                        UpdatedAt = "2026-07-19T02:00:00Z"
                    }
                }
            }
        });

        Assert.AreEqual(
            CatalogFullLaneEvidenceTracker.ProductTombstoneConflictCode,
            tracker.ConflictCode);
    }

    [TestMethod]
    public void BudgetUsesMaximumIndependentLaneAndSupportsOneHundredThousandRows()
    {
        foreach (var item in new[]
        {
            (Count: 0L, Pages: 1),
            (Count: 1L, Pages: 1),
            (Count: 999L, Pages: 1),
            (Count: 1000L, Pages: 1),
            (Count: 1001L, Pages: 2),
            (Count: 19763L, Pages: 20),
            (Count: 100000L, Pages: 100)
        })
        {
            var decision = Budget(item.Count, 0);
            Assert.IsTrue(decision.Allowed);
            Assert.AreEqual(item.Pages, decision.PageBudget, item.Count.ToString());
        }

        Assert.AreEqual(100, Budget(100000, 100000).PageBudget);
        Assert.AreEqual(100, Budget(100000, 1).PageBudget);
    }

    [TestMethod]
    public void BudgetFallsBackForLegacyAndRejectsAboveHardCeiling()
    {
        var legacy = CatalogPaginationSafetyPolicy.CalculatePageBudget(null, Limit, 120, 512);
        Assert.IsTrue(legacy.Allowed);
        Assert.IsFalse(legacy.Authoritative);
        Assert.AreEqual(120, legacy.PageBudget);

        var exceeded = Budget(513000, 0);
        Assert.IsFalse(exceeded.Allowed);
        Assert.AreEqual(CatalogPaginationSafetyPolicy.PageBudgetExceededCode, exceeded.Code);
    }

    private static void AssertAmbiguous(PosCatalogPullResponse response)
    {
        var decision = Evaluate(response);
        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual(CatalogPaginationSafetyPolicy.AmbiguousEndCode, decision.Code);
    }

    private static CatalogPageBudgetDecision Budget(long products, long prices)
    {
        return CatalogPaginationSafetyPolicy.CalculatePageBudget(
            Summary(products, prices),
            Limit,
            legacyFallbackPages: 120,
            hardCeilingPages: 512);
    }

    private static CatalogPaginationSafetyDecision Evaluate(PosCatalogPullResponse response)
    {
        return CatalogPaginationSafetyPolicy.EvaluateTerminalPage(
            response,
            Limit,
            fullSnapshotExpected: true,
            receivedBeforePage: EmptyCounts());
    }

    private static CatalogPaginationLaneCounts EmptyCounts()
    {
        return new CatalogPaginationLaneCounts(0, 0, 0, 0);
    }

    private static PosCatalogPullResponse Full(
        int products,
        int prices,
        bool hasMore,
        long? summaryProducts,
        long? summaryPrices = 0)
    {
        return new PosCatalogPullResponse
        {
            Catalog = new PosCatalogPayload
            {
                Categories = Array.Empty<PosCatalogCategoryResponse>(),
                Suppliers = Array.Empty<PosCatalogSupplierResponse>(),
                Products = Enumerable.Range(0, products)
                    .Select(_ => new PosCatalogProductResponse())
                    .ToArray(),
                Prices = Enumerable.Range(0, prices)
                    .Select(_ => new PosCatalogPriceResponse())
                    .ToArray(),
                Tombstones = new PosCatalogTombstonesResponse
                {
                    Categories = Array.Empty<PosCatalogCategoryTombstoneResponse>(),
                    Suppliers = Array.Empty<PosCatalogSupplierTombstoneResponse>(),
                    Products = Array.Empty<PosCatalogProductTombstoneResponse>()
                }
            },
            CatalogSummary = summaryProducts.HasValue
                ? Summary(summaryProducts.Value, summaryPrices ?? 0)
                : null,
            CatalogVersion = "revision-1",
            HasMore = hasMore,
            Ok = true,
            SyncMode = "full_refresh"
        };
    }

    private static PosCatalogSummaryResponse Summary(long products, long prices)
    {
        return new PosCatalogSummaryResponse
        {
            ActiveProducts = products,
            Categories = 0,
            Prices = prices,
            Products = products,
            Suppliers = 0
        };
    }

    private static PosCatalogPayload References(
        string categoryName,
        string supplierName,
        string updatedAt)
    {
        return new PosCatalogPayload
        {
            Categories = new[]
            {
                new PosCatalogCategoryResponse
                {
                    CategoryId = "category-1",
                    Name = categoryName,
                    UpdatedAt = updatedAt
                }
            },
            Suppliers = new[]
            {
                new PosCatalogSupplierResponse
                {
                    Name = supplierName,
                    SupplierId = "supplier-1",
                    UpdatedAt = updatedAt
                }
            }
        };
    }
}
