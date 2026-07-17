using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Pos;

namespace Win7POS.Core.Tests.Pos;

[TestClass]
public sealed class CustomerDisplayProjectionTests
{
    [TestMethod]
    public void EmptyCart_IsIdleWithoutStaleTotals()
    {
        var snapshot = CustomerDisplayProjection.Cart(Array.Empty<CustomerDisplayProjectionLine>(), 0, 0, "Shop", null, false, DateTimeOffset.UtcNow);
        Assert.AreEqual(CustomerDisplayState.Idle, snapshot.State);
        Assert.AreEqual(0, snapshot.Total);
        Assert.AreEqual(0, snapshot.Lines.Count);
    }

    [TestMethod]
    public void Item_CopiesAuthoritativeValuesAndOrder()
    {
        var snapshot = CustomerDisplayProjection.Cart(new[]
        {
            Line("a", "Apple", "111", 2, 700, 1400),
            Line("b", "Bread", "222", 1, 900, 900)
        }, 2300, 2300, "Shop", "b", true, DateTimeOffset.UtcNow);

        Assert.AreEqual(CustomerDisplayState.CartActive, snapshot.State);
        CollectionAssert.AreEqual(new[] { "Apple", "Bread" }, snapshot.Lines.Select(x => x.Name).ToArray());
        Assert.AreEqual(3, snapshot.ItemCount);
        Assert.AreEqual(2300, snapshot.Total);
        Assert.AreEqual("b", snapshot.LastChangedLineKey);
    }

    [TestMethod]
    public void Discount_UsesAuthoritativeTotalsInsteadOfSummingLines()
    {
        var snapshot = CustomerDisplayProjection.Cart(
            new[] { Line("a", "Item", "111", 3, 333, 999) },
            999,
            899,
            string.Empty,
            "a",
            false,
            DateTimeOffset.UtcNow);
        Assert.AreEqual(100, snapshot.Discount);
        Assert.AreEqual(899, snapshot.Total);
        Assert.AreNotEqual(snapshot.Lines.Sum(x => x.LineTotal), snapshot.Total);
    }

    [TestMethod]
    public void ManualAndReservedBarcodes_AreNeverExposed()
    {
        var snapshot = CustomerDisplayProjection.Cart(new[]
        {
            Line("manual", "Manual item", "MANUAL:1000", 1, 1000, 1000),
            Line("discount", "Internal", "DISC:CART:10", 1, -100, -100, CustomerDisplayLineKind.Discount)
        }, 1000, 900, string.Empty, null, true, DateTimeOffset.UtcNow);
        Assert.IsTrue(snapshot.Lines.All(x => string.IsNullOrEmpty(x.Barcode)));
        Assert.AreEqual("Discount", snapshot.Lines[1].Name);
    }

    [TestMethod]
    public void Completed_CopiesSafePaidAndChange()
    {
        var cart = CustomerDisplayProjection.Cart(new[] { Line("a", "Item", "1", 1, 1000, 1000) }, 1000, 1000, "Shop", "a", false, DateTimeOffset.UtcNow);
        var completed = CustomerDisplayProjection.Completed(cart, 1000, 2000, 1000, DateTimeOffset.UtcNow);
        Assert.AreEqual(CustomerDisplayState.Completed, completed.State);
        Assert.AreEqual(2000, completed.Paid);
        Assert.AreEqual(1000, completed.Change);
    }

    [TestMethod]
    public void PublicSnapshotTypes_ContainNoSensitiveFieldNames()
    {
        var forbidden = new[] { "Stock", "Cost", "Margin", "Supplier", "Operator", "Token", "RemoteId", "ProductId" };
        var names = typeof(CustomerDisplaySnapshot).GetProperties().Select(x => x.Name)
            .Concat(typeof(CustomerDisplayLine).GetProperties().Select(x => x.Name)).ToArray();
        foreach (var word in forbidden)
            Assert.IsFalse(names.Any(x => x.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0), word);
    }

    private static CustomerDisplayProjectionLine Line(string key, string name, string barcode, int qty, long unit, long total, CustomerDisplayLineKind kind = CustomerDisplayLineKind.Item) =>
        new() { StableKey = key, Name = name, Barcode = barcode, Quantity = qty, UnitPrice = unit, LineTotal = total, LineKind = kind };
}
