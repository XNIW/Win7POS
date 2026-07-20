using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class PriorityOnlineRequestGateTests
{
    [TestMethod]
    public async Task HeartbeatOvertakesQueuedWork_WhileOtherLanesRemainFifo()
    {
        using var gate = new PriorityOnlineRequestGate(1);
        var active = await gate.EnterAsync(
            OnlineSyncLane.CatalogDelta,
            CancellationToken.None);

        var sales = gate.EnterAsync(
            OnlineSyncLane.SalesOutbox,
            CancellationToken.None);
        var heartbeat = gate.EnterAsync(
            OnlineSyncLane.Heartbeat,
            CancellationToken.None);
        var catalogImport = gate.EnterAsync(
            OnlineSyncLane.CatalogImportOutbox,
            CancellationToken.None);

        active.Dispose();

        Assert.IsTrue(heartbeat.IsCompletedSuccessfully);
        Assert.IsFalse(sales.IsCompleted);
        Assert.IsFalse(catalogImport.IsCompleted);

        var heartbeatLease = await heartbeat;
        heartbeatLease.Dispose();
        Assert.IsTrue(sales.IsCompletedSuccessfully);
        Assert.IsFalse(catalogImport.IsCompleted);

        var salesLease = await sales;
        salesLease.Dispose();
        Assert.IsTrue(catalogImport.IsCompletedSuccessfully);
        (await catalogImport).Dispose();
    }

    [TestMethod]
    public async Task StopRejectsQueuedAndFutureRequests()
    {
        using var gate = new PriorityOnlineRequestGate(1);
        var active = await gate.EnterAsync(
            OnlineSyncLane.SalesOutbox,
            CancellationToken.None);
        var queued = gate.EnterAsync(
            OnlineSyncLane.CatalogDelta,
            CancellationToken.None);

        gate.Stop();

        await Assert.ThrowsExactlyAsync<OnlineSyncGenerationChangedException>(
            async () => (await queued).Dispose());
        Assert.ThrowsExactly<OnlineSyncGenerationChangedException>(() =>
            gate.EnterAsync(OnlineSyncLane.Heartbeat, CancellationToken.None));

        active.Dispose();
    }

    [TestMethod]
    public async Task CancelledWaiterIsRemoved_AndNextWaiterCanProceed()
    {
        using var gate = new PriorityOnlineRequestGate(1);
        var active = await gate.EnterAsync(
            OnlineSyncLane.CatalogDelta,
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelled = gate.EnterAsync(
            OnlineSyncLane.SalesOutbox,
            cancellation.Token);
        var next = gate.EnterAsync(
            OnlineSyncLane.CatalogImportOutbox,
            CancellationToken.None);

        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            async () => (await cancelled).Dispose());

        active.Dispose();
        Assert.IsTrue(next.IsCompletedSuccessfully);
        (await next).Dispose();
    }
}
