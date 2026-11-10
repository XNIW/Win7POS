using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Online;

public sealed partial class OnlineSyncSupervisorTests
{
    [TestMethod]
    public async Task Stop_JoinsCancelledScheduleBeforeReleasingOwnedResources()
    {
        var clock=new DeferredCancellationClock();
        using var supervisor=new OnlineSyncSupervisor(Generation("owned-delay"),
            (_,_,_)=>Task.FromResult(new OnlineSyncLaneOutcome(true,nextPollAfterSeconds:30)),
            _=>Task.FromResult(true),(_,_)=>Task.CompletedTask,clock:clock);
        Task stop=Task.CompletedTask;
        try
        {
            supervisor.Signal(OnlineSyncLane.Heartbeat,OnlineSyncLaneTrigger.StartOfDay);
            await clock.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            stop=supervisor.StopAsync();
            await clock.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsFalse(stop.IsCompleted,"Stop must own the cancelled delay's final continuation.");
            clock.Release.TrySetResult(true);
            await stop.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsTrue(clock.FinallyRan);
            Assert.IsTrue(supervisor.GetSnapshot().Lanes.All(x=>!x.InFlight && !x.Pending));
        }
        finally {clock.Release.TrySetResult(true);await supervisor.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));}
    }

    private sealed class DeferredCancellationClock:IOnlineSyncClock
    {
        internal readonly TaskCompletionSource<bool> Started=NewSignal(),Cancelled=NewSignal(),Release=NewSignal();
        internal bool FinallyRan;
        public DateTimeOffset UtcNow=>DateTimeOffset.UtcNow;
        public async Task DelayAsync(TimeSpan delay,CancellationToken token)
        {
            using var registration=token.Register(()=>Cancelled.TrySetResult(true));
            Started.TrySetResult(true);
            try {await Release.Task.ConfigureAwait(false);token.ThrowIfCancellationRequested();}
            finally{FinallyRan=true;}
        }
    }
}
