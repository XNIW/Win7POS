using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class PriorityOnlineRequestGateTests
{
    [TestMethod]
    public async Task WaitingRequests_PrioritizeHeartbeatSalesCatalogThenArticles()
    {
        using var gate = new PriorityOnlineRequestGate(1);
        var active = await gate.EnterAsync(
            OnlineSyncLane.ArticleMutationOutbox,
            CancellationToken.None);
        var article = gate.EnterAsync(
            OnlineSyncLane.ArticleMutationOutbox,
            CancellationToken.None);
        var catalog = gate.EnterAsync(
            OnlineSyncLane.CatalogDelta,
            CancellationToken.None);
        var sales = gate.EnterAsync(
            OnlineSyncLane.SalesOutbox,
            CancellationToken.None);
        var heartbeat = gate.EnterAsync(
            OnlineSyncLane.Heartbeat,
            CancellationToken.None);

        active.Dispose();
        var heartbeatLease = await AssertNextAsync(
            heartbeat,
            article,
            catalog,
            sales);
        heartbeatLease.Dispose();
        var salesLease = await AssertNextAsync(
            sales,
            article,
            catalog);
        salesLease.Dispose();
        var catalogLease = await AssertNextAsync(catalog, article);
        catalogLease.Dispose();
        (await article).Dispose();
    }

    private static async Task<IDisposable> AssertNextAsync(
        Task<IDisposable> expected,
        params Task<IDisposable>[] stillWaiting)
    {
        var completed = await Task.WhenAny(
            new[] { expected }.Concat(stillWaiting));
        Assert.AreSame(expected, completed);
        foreach (var pending in stillWaiting)
            Assert.IsFalse(pending.IsCompleted);
        return await expected;
    }
}
