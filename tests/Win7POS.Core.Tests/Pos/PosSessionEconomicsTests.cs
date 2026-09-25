using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;

namespace Win7POS.Core.Tests.Pos;

[TestClass]
public sealed class PosSessionEconomicsTests
{
    private sealed class Lookup : IProductLookup
    {
        public long Price = 1000;
        public Task<Product> GetByBarcodeAsync(string barcode) => Task.FromResult(
            new Product { Id = 1, Barcode = barcode, Name = barcode, UnitPrice = Price });
    }

    [TestMethod]
    [DataRow("new")]
    [DataRow("existing")]
    [DataRow("manual")]
    [DataRow("manual-existing")]
    public async Task CartPercent_RecalculatesForEveryAddition(string kind)
    {
        var session = new PosSession(new Lookup());
        if (kind == "manual-existing") await session.AddManualPriceAsync(1000);
        else await session.AddByBarcodeAsync("A");
        session.ApplyCartDiscountPercent(10);
        if (kind.StartsWith("manual")) await session.AddManualPriceAsync(1000);
        else await session.AddByBarcodeAsync(kind == "existing" ? "A" : "B");
        Assert.AreEqual(1800L, session.Total);
    }

    [TestMethod]
    public async Task FixedDiscount_ClampsAfterQuantityAndPriceReduction()
    {
        var session = new PosSession(new Lookup());
        await session.AddByBarcodeAsync("A");
        session.SetQuantity("A", 2);
        session.ApplyLineDiscountAmount("A", 1500);
        session.SetQuantity("A", 1);
        Assert.AreEqual(0L, session.Total);
        session.SetLineUnitPrice("A", 100);
        Assert.AreEqual(0L, session.Total);
        session.RemoveLine("A");
        Assert.AreEqual(0, session.Lines.Count);
    }

    [TestMethod]
    public async Task FinalUnitPrice_RemainsUnitaryWhenQuantityChanges()
    {
        var session = new PosSession(new Lookup());
        await session.AddByBarcodeAsync("A");
        session.ApplyLineDiscountByFinalUnitPrice("A", 650);
        session.SetQuantity("A", 3);
        Assert.AreEqual(1950L, session.Total);
        session.SetLineUnitPrice("A", 1200);
        Assert.AreEqual(1950L, session.Total);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ScanAtQuantityLimit_RejectsWithoutMutating(bool manual)
    {
        var session = new PosSession(new Lookup());
        if (manual) await session.AddManualPriceAsync(1000);
        else await session.AddByBarcodeAsync("A");
        var barcode = session.Lines[0].Barcode;
        session.SetQuantity(barcode, 100000);
        await Assert.ThrowsExactlyAsync<PosException>(() => manual
            ? session.AddManualPriceAsync(1000) : session.AddByBarcodeAsync("A"));
        Assert.AreEqual(100000, session.Lines[0].Quantity);
    }

    [TestMethod]
    public async Task Percent_UsesExactIntegerClpAndHalfAwayRounding()
    {
        var lookup = new Lookup { Price = 101 };
        var session = new PosSession(lookup);
        await session.AddByBarcodeAsync("A");
        session.ApplyLineDiscountPercent("A", 50);
        Assert.AreEqual(50L, session.Total);
        session.Clear();
        lookup.Price = 9007199254740993;
        await session.AddByBarcodeAsync("A");
        session.ApplyCartDiscountPercent(100);
        Assert.AreEqual(0L, session.Total);
    }

    [TestMethod]
    public async Task Overflow_LeavesCartUnchanged()
    {
        var session = new PosSession(new Lookup { Price = long.MaxValue });
        await session.AddByBarcodeAsync("A");
        Assert.ThrowsExactly<OverflowException>(() => session.SetQuantity("A", 2));
        Assert.AreEqual(1, session.Lines[0].Quantity);
        Assert.AreEqual(long.MaxValue, session.Total);
    }

    [TestMethod]
    public async Task DiscountsRemainAdditiveAndAreBoundedByGross_AndRemovalClearsThem()
    {
        var session = new PosSession(new Lookup());
        await session.AddByBarcodeAsync("A");
        session.ApplyLineDiscountPercent("A", 20);
        session.ApplyCartDiscountPercent(10);
        Assert.AreEqual(700L, session.Total, "The existing policy is additive on gross, not sequential compounding.");
        session.ApplyCartDiscountPercent(100);
        Assert.AreEqual(0L, session.Total);
        session.ApplyLineDiscountPercent("A", 0);
        Assert.AreEqual(0L, session.Total);
        session.RemoveLine("A");
        Assert.HasCount(0, session.Lines);
    }

    [TestMethod]
    public async Task RestoreRecalculatesAndInvalidRestorePreservesActiveCart()
    {
        var session = new PosSession(new Lookup());
        await session.AddByBarcodeAsync("A");
        session.ApplyLineDiscountByFinalUnitPrice("A", 650);
        var held = session.Lines.Select(x => new RestoredLine { Barcode = x.Barcode, Name = x.Name, UnitPrice = x.UnitPrice, Quantity = x.Quantity }).ToList();
        held[0].Quantity = 3;
        session.ReplaceWithLines(held);
        Assert.AreEqual(1950L, session.Total);
        held[0].Quantity = 100001;
        Assert.ThrowsExactly<PosException>(() => session.ReplaceWithLines(held));
        Assert.AreEqual(1950L, session.Total);
    }
}
