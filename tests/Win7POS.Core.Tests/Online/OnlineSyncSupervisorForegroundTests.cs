using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Online;

public sealed partial class OnlineSyncSupervisorTests
{
    static OnlineSyncRequestCredentials ForegroundCredentials(OnlineSyncGeneration generation,string stamp="one")
        =>new(generation,"synthetic-device-token","synthetic-session-token",stamp);

    [TestMethod]
    public async Task ForegroundHistory_UsesTheSameNetworkGateAndFreshCredentialsAfterWaiting()
    {
        var generation=Generation("foreground-gate");var entered=NewSignal();var release=NewSignal();
        var credentialStamp="old";var foregroundEntered=false;
        using var supervisor=new OnlineSyncSupervisor(generation,(context,_,ct)=>context.ExecuteRequestAsync(async token=>
        {entered.TrySetResult(true);await release.Task.WaitAsync(token);return TerminalSuccess();},ct),
            _=>Task.FromResult(true),(_,_)=>Task.CompletedTask,networkConcurrency:1,
            credentialProvider:g=>Task.FromResult(ForegroundCredentials(g,credentialStamp)));
        var background=supervisor.TriggerAsync(OnlineSyncLane.SalesOutbox,OnlineSyncLaneTrigger.LocalCommit);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var foreground=supervisor.ExecuteCredentialedRequestAsync(generation,OnlineSyncLane.HistoricalPayments,(credentials,_)=>
        {foregroundEntered=true;return Task.FromResult(credentials.CredentialStamp);},_=>"",default);
        await Task.Delay(30);Assert.IsFalse(foregroundEntered);
        credentialStamp="rotated";release.TrySetResult(true);
        Assert.AreEqual("rotated",await foreground.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue((await background).Success);await supervisor.WhenIdleAsync();
    }

    [TestMethod]
    public async Task ForegroundCallerCancellation_DoesNotCancelAnIndependentBackgroundRequest()
    {
        var generation=Generation("foreground-cancel");var entered=NewSignal();var release=NewSignal();var calls=0;
        using var supervisor=new OnlineSyncSupervisor(generation,(context,_,ct)=>context.ExecuteRequestAsync(async token=>
        {entered.TrySetResult(true);await release.Task.WaitAsync(token);return TerminalSuccess();},ct),
            _=>Task.FromResult(true),(_,_)=>Task.CompletedTask,networkConcurrency:1,
            credentialProvider:g=>Task.FromResult(ForegroundCredentials(g)));
        var background=supervisor.TriggerAsync(OnlineSyncLane.CatalogDelta,OnlineSyncLaneTrigger.Manual);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));using var caller=new CancellationTokenSource();
        var foreground=supervisor.ExecuteCredentialedRequestAsync(generation,OnlineSyncLane.HistoricalPayments,(_,_)=>
        {calls++;return Task.FromResult(1);},_=>"",caller.Token);
        caller.Cancel();await Assert.ThrowsAsync<OperationCanceledException>(()=>foreground);
        Assert.AreEqual(0,calls);Assert.IsFalse(background.IsCompleted);
        release.TrySetResult(true);Assert.IsTrue((await background).Success);
        Assert.AreEqual(2,await supervisor.ExecuteCredentialedRequestAsync(generation,OnlineSyncLane.HistoricalPayments,(_,_)=>Task.FromResult(2),_=>"",default));
    }

    [TestMethod]
    public async Task SupervisorStop_CancelsAndDrainsForegroundTransportCleanup()
    {
        var generation=Generation("foreground-stop");var entered=NewSignal();var cleanup=NewSignal();var releaseCleanup=NewSignal();
        using var supervisor=new OnlineSyncSupervisor(generation,(_,_,_)=>Task.FromResult(TerminalSuccess()),
            _=>Task.FromResult(true),(_,_)=>Task.CompletedTask,
            credentialProvider:g=>Task.FromResult(ForegroundCredentials(g)));
        var request=supervisor.ExecuteCredentialedRequestAsync(generation,OnlineSyncLane.HistoricalPayments,async (_,ct)=>
        {
            entered.TrySetResult(true);
            try{await Task.Delay(Timeout.Infinite,ct);return 1;}
            finally{cleanup.TrySetResult(true);await releaseCleanup.Task;}
        },_=>"",default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var stop=supervisor.StopAsync();
        await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.IsFalse(stop.IsCompleted);
        releaseCleanup.TrySetResult(true);await Assert.ThrowsAsync<OperationCanceledException>(()=>request);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ForegroundRequest_RejectsAnotherExpectedGenerationWithoutHttp()
    {
        var generation=Generation("foreground-current");var calls=0;
        using var supervisor=new OnlineSyncSupervisor(generation,(_,_,_)=>Task.FromResult(TerminalSuccess()),
            _=>Task.FromResult(true),(_,_)=>Task.CompletedTask,
            credentialProvider:g=>Task.FromResult(ForegroundCredentials(g)));
        await Assert.ThrowsAsync<OnlineSyncGenerationChangedException>(()=>supervisor.ExecuteCredentialedRequestAsync(
            Generation("foreground-obsolete"),OnlineSyncLane.HistoricalPayments,(_,_)=>{calls++;return Task.FromResult(1);},_=>"",default));
        Assert.AreEqual(0,calls);
    }

    [TestMethod]
    [DataRow("history_cloud_access_denied",0)]
    [DataRow("auth_denied",1)]
    public async Task ForegroundHistory_SeparatesDomainDenialFromSharedRevocation(string code,int expectedStops)
    {
        var generation=Generation("foreground-auth");var stops=0;
        using var supervisor=new OnlineSyncSupervisor(generation,(_,_,_)=>Task.FromResult(TerminalSuccess()),
            _=>Task.FromResult(true),(_,_)=>{Interlocked.Increment(ref stops);return Task.CompletedTask;},
            credentialProvider:g=>Task.FromResult(ForegroundCredentials(g)));
        var request=supervisor.ExecuteCredentialedRequestAsync(generation,OnlineSyncLane.HistoricalPayments,
            (_,_)=>Task.FromResult(code),value=>SharedAuthStopPolicy.IsAuthenticationDenied(value)?value:"",default);
        if(expectedStops==1)await Assert.ThrowsAsync<OperationCanceledException>(()=>request);
        else Assert.AreEqual(code,await request);
        Assert.AreEqual(expectedStops,stops);
        var sales=await supervisor.TriggerAsync(OnlineSyncLane.SalesOutbox,OnlineSyncLaneTrigger.LocalCommit);
        Assert.AreEqual(expectedStops==1,sales.AuthenticationDenied);
    }

    [TestMethod]
    public async Task ForegroundOldTokenDenial_CannotRevokeRotatedCredentials()
    {
        var generation=Generation("foreground-rotation");var reads=0;var stops=0;
        using var supervisor=new OnlineSyncSupervisor(generation,(_,_,_)=>Task.FromResult(TerminalSuccess()),
            _=>Task.FromResult(true),(_,_)=>{stops++;return Task.CompletedTask;},
            credentialProvider:g=>Task.FromResult(ForegroundCredentials(g,(++reads).ToString())));
        await Assert.ThrowsAsync<OnlineSyncCredentialsChangedException>(()=>supervisor.ExecuteCredentialedRequestAsync(
            generation,OnlineSyncLane.HistoricalPayments,(_,_)=>Task.FromResult("auth_denied"),value=>value,default));
        Assert.AreEqual(0,stops);Assert.IsTrue((await supervisor.TriggerAsync(OnlineSyncLane.SalesOutbox,OnlineSyncLaneTrigger.LocalCommit)).Success);
    }

    [TestMethod]
    public async Task ForegroundTransportFault_IsObservedByCallerAndDoesNotBreakOwnedShutdown()
    {
        var generation=Generation("foreground-fault");
        using var supervisor=new OnlineSyncSupervisor(generation,(_,_,_)=>Task.FromResult(TerminalSuccess()),
            _=>Task.FromResult(true),(_,_)=>Task.CompletedTask,
            credentialProvider:g=>Task.FromResult(ForegroundCredentials(g)));
        await Assert.ThrowsAsync<IOException>(()=>supervisor.ExecuteCredentialedRequestAsync<int>(generation,
            OnlineSyncLane.HistoricalPayments,(_,_)=>throw new IOException("synthetic transport"),_=>"",default));
        await supervisor.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ForegroundResult_AfterGenerationChangeCannotBePublished()
    {
        var generation=Generation("foreground-late-result");var current=true;
        using var supervisor=new OnlineSyncSupervisor(generation,(_,_,_)=>Task.FromResult(TerminalSuccess()),
            _=>Task.FromResult(current),(_,_)=>Task.CompletedTask,
            credentialProvider:g=>Task.FromResult(ForegroundCredentials(g)));
        await Assert.ThrowsAsync<OnlineSyncGenerationChangedException>(()=>supervisor.ExecuteCredentialedRequestAsync(
            generation,OnlineSyncLane.HistoricalPayments,(_,_)=>{current=false;return Task.FromResult(1);},_=>"",default));
        await supervisor.WhenIdleAsync();
    }
}
