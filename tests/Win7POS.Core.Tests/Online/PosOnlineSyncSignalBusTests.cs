using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
[DoNotParallelize]
public sealed class PosOnlineSyncSignalBusTests
{
    [TestMethod]
    public async Task FailedResume_RemainsInMaintenanceAndSecondResumeRetries()
    {
        var signals = 0;
        var stops = 0;
        var resumes = 0;
        var firstResumeStarted = NewSignal();
        var releaseFirstResume = NewSignal();
        using var registration = PosOnlineSyncSignalBus.Register(
            (_, _) => Interlocked.Increment(ref signals),
            (_, _, _) => Task.FromResult(TerminalSuccess()),
            () =>
            {
                Interlocked.Increment(ref stops);
                return Task.CompletedTask;
            },
            async _ =>
            {
                if (Interlocked.Increment(ref resumes) == 1)
                {
                    firstResumeStarted.TrySetResult(true);
                    await releaseFirstResume.Task;
                    throw new InvalidOperationException("injected resume failure");
                }
            });

        try
        {
            await PosOnlineSyncSignalBus.StopAsync();
            Assert.IsTrue(PosOnlineSyncSignalBus.IsMaintenanceActive);
            Assert.AreEqual(1, stops);

            PosOnlineSyncSignalBus.Signal(
                OnlineSyncLane.SalesOutbox,
                OnlineSyncLaneTrigger.LocalCommit);
            var blockedBeforeResume = await PosOnlineSyncSignalBus.TriggerAsync(
                OnlineSyncLane.Heartbeat,
                OnlineSyncLaneTrigger.Periodic);
            Assert.AreEqual(0, signals);
            Assert.AreEqual("sync_maintenance_active", blockedBeforeResume.Code);

            var failedResume = PosOnlineSyncSignalBus.ResumeAsync();
            await firstResumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(PosOnlineSyncSignalBus.IsMaintenanceActive);
            var blockedWhileResuming = await PosOnlineSyncSignalBus.TriggerAsync(
                OnlineSyncLane.CatalogDelta,
                OnlineSyncLaneTrigger.PartialResume);
            Assert.AreEqual("sync_maintenance_active", blockedWhileResuming.Code);
            releaseFirstResume.TrySetResult(true);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await failedResume);
            Assert.IsTrue(PosOnlineSyncSignalBus.IsMaintenanceActive);
            var blockedAfterFailure = await PosOnlineSyncSignalBus.TriggerAsync(
                OnlineSyncLane.CatalogDelta,
                OnlineSyncLaneTrigger.PartialResume);
            Assert.AreEqual("sync_maintenance_active", blockedAfterFailure.Code);

            var retryOne = PosOnlineSyncSignalBus.ResumeAsync();
            var retryTwo = PosOnlineSyncSignalBus.ResumeAsync();
            await Task.WhenAll(retryOne, retryTwo);
            Assert.IsFalse(PosOnlineSyncSignalBus.IsMaintenanceActive);
            Assert.AreEqual(2, resumes);
            Assert.AreEqual(1, stops);

            PosOnlineSyncSignalBus.Signal(
                OnlineSyncLane.SalesOutbox,
                OnlineSyncLaneTrigger.LocalCommit);
            Assert.AreEqual(1, signals);
            Assert.IsTrue((await PosOnlineSyncSignalBus.TriggerAsync(
                OnlineSyncLane.Heartbeat,
                OnlineSyncLaneTrigger.Periodic)).Success);
        }
        finally
        {
            while (PosOnlineSyncSignalBus.IsMaintenanceActive)
                await PosOnlineSyncSignalBus.ExitMaintenanceWithoutResumeAsync();
        }
    }

    [TestMethod]
    public async Task NestedNoResume_SuppressesTheFinalResumeHandler()
    {
        var stops = 0;
        var resumes = 0;
        using var registration = PosOnlineSyncSignalBus.Register(
            (_, _) => { },
            (_, _, _) => Task.FromResult(TerminalSuccess()),
            () =>
            {
                Interlocked.Increment(ref stops);
                return Task.CompletedTask;
            },
            _ =>
            {
                Interlocked.Increment(ref resumes);
                return Task.CompletedTask;
            });

        try
        {
            await PosOnlineSyncSignalBus.StopAsync();
            await PosOnlineSyncSignalBus.StopAsync();
            Assert.AreEqual(1, stops);
            Assert.IsTrue(PosOnlineSyncSignalBus.IsMaintenanceActive);

            await PosOnlineSyncSignalBus.ExitMaintenanceWithoutResumeAsync();
            Assert.IsTrue(PosOnlineSyncSignalBus.IsMaintenanceActive);
            await PosOnlineSyncSignalBus.ResumeAsync();

            Assert.IsFalse(PosOnlineSyncSignalBus.IsMaintenanceActive);
            Assert.AreEqual(0, resumes);
        }
        finally
        {
            while (PosOnlineSyncSignalBus.IsMaintenanceActive)
                await PosOnlineSyncSignalBus.ExitMaintenanceWithoutResumeAsync();
        }
    }

    [TestMethod]
    public async Task FailedResume_ThenSuccessfulRestoreClearsPendingWithoutRestart()
    {
        var stops = 0;
        var resumes = 0;
        using var registration = PosOnlineSyncSignalBus.Register(
            (_, _) => { },
            (_, _, _) => Task.FromResult(TerminalSuccess()),
            () =>
            {
                Interlocked.Increment(ref stops);
                return Task.CompletedTask;
            },
            _ =>
            {
                Interlocked.Increment(ref resumes);
                return Task.FromException(
                    new InvalidOperationException("injected resume failure"));
            });

        try
        {
            await PosOnlineSyncSignalBus.StopAsync();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => PosOnlineSyncSignalBus.ResumeAsync());
            Assert.IsTrue(PosOnlineSyncSignalBus.IsMaintenanceActive);
            Assert.AreEqual(1, stops);

            await PosOnlineSyncSignalBus.StopAsync();
            Assert.AreEqual(2, stops);
            await PosOnlineSyncSignalBus.ExitMaintenanceWithoutResumeAsync();

            Assert.IsFalse(PosOnlineSyncSignalBus.IsMaintenanceActive);
            Assert.AreEqual(1, resumes);
            var outcome = await PosOnlineSyncSignalBus.TriggerAsync(
                OnlineSyncLane.Heartbeat,
                OnlineSyncLaneTrigger.Periodic);
            Assert.IsTrue(outcome.Success);
        }
        finally
        {
            while (PosOnlineSyncSignalBus.IsMaintenanceActive)
                await PosOnlineSyncSignalBus.ExitMaintenanceWithoutResumeAsync();
        }
    }

    [TestMethod]
    public async Task FailedResume_DirectNoResumeExitCancelsPendingRetry()
    {
        var resumes = 0;
        using var registration = PosOnlineSyncSignalBus.Register(
            (_, _) => { },
            (_, _, _) => Task.FromResult(TerminalSuccess()),
            () => Task.CompletedTask,
            _ =>
            {
                Interlocked.Increment(ref resumes);
                return Task.FromException(
                    new InvalidOperationException("injected resume failure"));
            });

        try
        {
            await PosOnlineSyncSignalBus.StopAsync();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => PosOnlineSyncSignalBus.ResumeAsync());
            Assert.IsTrue(PosOnlineSyncSignalBus.IsMaintenanceActive);

            await PosOnlineSyncSignalBus.ExitMaintenanceWithoutResumeAsync();

            Assert.IsFalse(PosOnlineSyncSignalBus.IsMaintenanceActive);
            Assert.AreEqual(1, resumes);
            Assert.IsTrue((await PosOnlineSyncSignalBus.TriggerAsync(
                OnlineSyncLane.Heartbeat,
                OnlineSyncLaneTrigger.Periodic)).Success);
        }
        finally
        {
            while (PosOnlineSyncSignalBus.IsMaintenanceActive)
                await PosOnlineSyncSignalBus.ExitMaintenanceWithoutResumeAsync();
        }
    }

    private static OnlineSyncLaneOutcome TerminalSuccess()
    {
        return new OnlineSyncLaneOutcome(success: true, terminal: true);
    }

    private static TaskCompletionSource<bool> NewSignal()
    {
        return new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
