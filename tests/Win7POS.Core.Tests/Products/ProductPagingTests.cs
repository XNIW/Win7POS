using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Products;

namespace Win7POS.Core.Tests.Products;

[TestClass]
public sealed class ProductPagingTests
{
    [TestMethod]
    public void Filter_NormalizesValuesAndBindsRevisionToFingerprint()
    {
        var first = new ProductPageFilter("  00123  ", 0, -1, 200, 7);
        var same = new ProductPageFilter("00123", null, null, 200, 7);
        var changedRevision = new ProductPageFilter("00123", null, null, 200, 8);

        Assert.AreEqual("00123", first.Query);
        Assert.IsNull(first.CategoryId);
        Assert.IsNull(first.SupplierId);
        Assert.AreEqual(first.Fingerprint, same.Fingerprint);
        Assert.AreNotEqual(first.Fingerprint, changedRevision.Fingerprint);
        Assert.AreEqual(64, first.Fingerprint.Length);
    }

    [TestMethod]
    public void Cursor_OrdersExactBarcodeThenBinaryBarcodeThenId()
    {
        var filter = new ProductPageFilter("B", null, null, 200, 1);
        var exact = filter.CreateCursor("B", 9);
        var upper = filter.CreateCursor("A", 10);
        var lower = filter.CreateCursor("a", 1);
        var duplicateFirst = filter.CreateCursor("a", 2);
        var duplicateSecond = filter.CreateCursor("a", 3);

        Assert.IsTrue(exact.CompareTo(upper) < 0, "Exact barcode rank must win before barcode ordering.");
        Assert.IsTrue(upper.CompareTo(lower) < 0, "Cursor comparison must use ordinal/BINARY barcode ordering.");
        Assert.IsTrue(duplicateFirst.CompareTo(duplicateSecond) < 0, "Duplicate barcodes must be ordered by id.");
    }

    [TestMethod]
    public void Cursor_UsesSqliteUtf8BinaryOrderForSupplementaryCharacters()
    {
        var filter = new ProductPageFilter(string.Empty, null, null, 200, 1);
        var privateUse = filter.CreateCursor("\uE000", 1);
        var supplementary = filter.CreateCursor("\U00010000", 2);

        Assert.IsTrue(
            privateUse.CompareTo(supplementary) < 0,
            "SQLite UTF-8 BINARY orders U+E000 before U+10000, unlike UTF-16 ordinal comparison.");
    }

    [TestMethod]
    public void Coordinator_UsesForwardAnchorsForOrdinaryNextAndPrevious()
    {
        var coordinator = new ProductPagingCoordinator();
        var filter = new ProductPageFilter(string.Empty, null, null, 2, 4);

        var firstPlan = coordinator.Plan(filter, 1);
        Assert.AreEqual(ProductPageQueryKind.First, firstPlan.Kind);
        coordinator.Accept(filter, firstPlan, Cursor(filter, "A", 1), Cursor(filter, "B", 2), 2, 8);

        var nextPlan = coordinator.Plan(filter, 2);
        Assert.AreEqual(ProductPageQueryKind.Forward, nextPlan.Kind);
        Assert.AreEqual(2L, nextPlan.Cursor.Id);
        coordinator.Accept(filter, nextPlan, Cursor(filter, "C", 3), Cursor(filter, "D", 4), 2, 8);

        var previousPlan = coordinator.Plan(filter, 1);
        Assert.AreEqual(ProductPageQueryKind.First, previousPlan.Kind);
        coordinator.Accept(filter, previousPlan, Cursor(filter, "A", 1), Cursor(filter, "B", 2), 2, 8);

        var cachedSecond = coordinator.Plan(filter, 2);
        Assert.AreEqual(ProductPageQueryKind.Forward, cachedSecond.Kind);
        Assert.AreEqual(2L, cachedSecond.Cursor.Id);
    }

    [TestMethod]
    public void Coordinator_UsesReverseKeysetAfterPreviousAnchorEviction()
    {
        var coordinator = new ProductPagingCoordinator(maximumAnchors: 2);
        var filter = new ProductPageFilter(string.Empty, null, null, 2, 4);

        AcceptPage(coordinator, filter, 1, "A", 1, "B", 2, 10);
        AcceptPage(coordinator, filter, 2, "C", 3, "D", 4, 10);
        AcceptPage(coordinator, filter, 3, "E", 5, "F", 6, 10);

        Assert.AreEqual(2, coordinator.AnchorCount);
        var previousPlan = coordinator.Plan(filter, 2);
        Assert.AreEqual(ProductPageQueryKind.Reverse, previousPlan.Kind);
        Assert.AreEqual(5L, previousPlan.Cursor.Id);
    }

    [TestMethod]
    public void Coordinator_UsesOffsetOnlyForUnanchoredArbitraryJump()
    {
        var coordinator = new ProductPagingCoordinator();
        var filter = new ProductPageFilter(string.Empty, null, null, 200, 2);

        AcceptPage(coordinator, filter, 1, "A", 1, "B", 2, 2000);

        var jump = coordinator.Plan(filter, 8);
        Assert.AreEqual(ProductPageQueryKind.OffsetFallback, jump.Kind);
        Assert.IsTrue(jump.UsedOffsetFallback);
        Assert.AreEqual(1400, jump.Offset);

        var ordinaryNext = coordinator.Plan(filter, 2);
        Assert.AreEqual(ProductPageQueryKind.Forward, ordinaryNext.Kind);
        Assert.IsFalse(ordinaryNext.UsedOffsetFallback);
    }

    [TestMethod]
    public void Coordinator_FilterOrRevisionMismatchResetsToFirstWithoutAdvancingBeforeAccept()
    {
        var coordinator = new ProductPagingCoordinator();
        var filter = new ProductPageFilter("milk", 3, 4, 200, 10);
        AcceptPage(coordinator, filter, 1, "A", 1, "B", 2, 600);
        Assert.AreEqual(1, coordinator.CurrentPage);

        var changedFilter = new ProductPageFilter("milk", 3, 4, 200, 11);
        var plan = coordinator.Plan(changedFilter, 2);

        Assert.AreEqual(ProductPageQueryKind.First, plan.Kind);
        Assert.AreEqual(1, plan.TargetPage);
        Assert.AreEqual(1, coordinator.CurrentPage, "Planning a failed/retried query must not advance accepted state.");
        Assert.IsFalse(filter.CreateCursor("B", 2).Matches(changedFilter));
    }

    [TestMethod]
    public void Coordinator_RejectsMismatchedCursorEvidence()
    {
        var coordinator = new ProductPagingCoordinator();
        var filter = new ProductPageFilter(string.Empty, null, null, 2, 1);
        var other = new ProductPageFilter("other", null, null, 2, 1);
        var plan = coordinator.Plan(filter, 1);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            coordinator.Accept(filter, plan, other.CreateCursor("A", 1), other.CreateCursor("B", 2), 2, 2));
        Assert.AreEqual(0, coordinator.CurrentPage);
    }

    [TestMethod]
    public void Coordinator_RejectsConcurrentPlanAfterAnotherResultAdvancesState()
    {
        var coordinator = new ProductPagingCoordinator();
        var filter = new ProductPageFilter(string.Empty, null, null, 2, 1);
        var firstPlan = coordinator.Plan(filter, 1);
        var staleConcurrentPlan = coordinator.Plan(filter, 1);

        coordinator.Accept(
            filter,
            firstPlan,
            Cursor(filter, "A", 1),
            Cursor(filter, "B", 2),
            itemCount: 2,
            totalCount: 4);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            coordinator.Accept(
                filter,
                staleConcurrentPlan,
                Cursor(filter, "A", 1),
                Cursor(filter, "B", 2),
                itemCount: 2,
                totalCount: 4));
        Assert.AreEqual(1, coordinator.CurrentPage);
    }

    [TestMethod]
    public void Coordinator_RejectsEmptyOrOutOfRangePageAfterCatalogShrink()
    {
        var coordinator = new ProductPagingCoordinator();
        var filter = new ProductPageFilter(string.Empty, null, null, 2, 1);
        AcceptPage(coordinator, filter, 1, "A", 1, "B", 2, 4);
        var secondPage = coordinator.Plan(filter, 2);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            coordinator.Accept(filter, secondPage, null, null, itemCount: 0, totalCount: 2));
        Assert.AreEqual(1, coordinator.CurrentPage);
    }

    private static void AcceptPage(
        ProductPagingCoordinator coordinator,
        ProductPageFilter filter,
        int page,
        string firstBarcode,
        long firstId,
        string lastBarcode,
        long lastId,
        int total)
    {
        var plan = coordinator.Plan(filter, page);
        coordinator.Accept(
            filter,
            plan,
            Cursor(filter, firstBarcode, firstId),
            Cursor(filter, lastBarcode, lastId),
            itemCount: 2,
            totalCount: total);
    }

    private static ProductPageCursor Cursor(ProductPageFilter filter, string barcode, long id) =>
        filter.CreateCursor(barcode, id);
}
