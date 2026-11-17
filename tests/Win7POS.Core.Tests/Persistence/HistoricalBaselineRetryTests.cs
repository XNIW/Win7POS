using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Import;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Persistence;
public sealed partial class HistoricalBaselineStoreTests
{
    static async Task<BaselineTransport> FailedPreparation(HistoricalCloudExportTests.Fixture f,OnlineSyncGeneration g)
    {
        await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(g,1);
        var transport=new BaselineTransport(f,g){RunState="failed"};
        for(var n=0;n<4;n++)Assert.IsTrue(await transport.Client().RunAsync(default));
        Assert.IsFalse(await transport.Client().RunAsync(default));
        Assert.AreEqual("needs_attention",(await Store(f,g).ReadAsync(Server,1,default))!.State);
        return transport;
    }
    [TestMethod]
    public async Task BaselineRetry_BackgroundAndNewLoginNeverCreateAnOperatorRetryOrAnotherArchive()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var t=await FailedPreparation(f,g);
        var before=(await Store(f,g).ReadAsync(Server,1,default))!;var facts=f.ReadFacts();
        for(var n=0;n<2;n++)Assert.AreEqual("history_baseline_attention_required",(await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>t.Client().RunAsync(default))).Code);
        t.G=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(t.G,2);
        await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>t.Client().RunAsync(default));
        using var c=f.Db.Factory.Open();Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_baseline_retry"));
        Assert.AreEqual(before.FlowId,(await Store(f,t.G).ReadAsync(Server,1,default))!.FlowId);Assert.AreEqual(1,t.PrepareCalls);Assert.AreEqual(1,t.UploadCalls);Assert.AreEqual(1,t.StartCalls);Assert.AreEqual(0,t.RecoveryCalls);Assert.AreEqual(facts,f.ReadFacts());
    }
    [TestMethod]
    public async Task BaselineRetry_ExplicitRequestLostAckAndRestartPreserveAllOriginalKeysAndFailures()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var t=await FailedPreparation(f,g);var facts=f.ReadFacts();
        var before=(await Store(f,g).ReadAsync(Server,1,default))!;var intent=await Store(f,g).PrepareFailedRetryAsync(default);
        Assert.AreEqual(intent.RequestId,(await Store(f,g).PrepareFailedRetryAsync(default)).RequestId);
        using(var c=f.Db.Factory.Open())Assert.Throws<SqliteException>(()=>c.Execute("UPDATE historical_baseline_flow SET state='run_started'"));
        t.LoseRecovery=true;await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>t.Client().RunAsync(default));
        Assert.AreEqual("needs_attention",(await Store(f,g).ReadAsync(Server,1,default))!.State);
        Assert.IsTrue(await t.Client().RunAsync(default));
        var resumed=(await Store(f,g).ReadAsync(Server,1,default))!;Assert.AreEqual("run_started",resumed.State);Assert.AreEqual("baseline_job_failed",resumed.ReasonCode);
        Assert.AreEqual(before.FlowId,resumed.FlowId);Assert.AreEqual(before.RunId,resumed.RunId);Assert.AreEqual(before.UploadId,resumed.UploadId);Assert.AreEqual(before.ApplyRequestId,resumed.ApplyRequestId);Assert.AreEqual(before.StartRequestId,resumed.StartRequestId);Assert.AreEqual(before.ArchiveSha256,resumed.ArchiveSha256);
        Assert.IsTrue(await t.Client().RunAsync(default));Assert.IsTrue(await t.Client().RunAsync(default));
        Assert.AreEqual("applied",(await Store(f,g).ReadAsync(Server,1,default))!.State);Assert.AreEqual(1,t.ApplyCalls);Assert.AreEqual(1,t.PrepareCalls);Assert.AreEqual(1,t.UploadCalls);Assert.AreEqual(1,t.StartCalls);Assert.AreEqual(facts,f.ReadFacts());
        using var db=f.Db.Factory.Open();Assert.AreEqual("consumed",db.ExecuteScalar<string>("SELECT state FROM historical_baseline_retry"));
        Assert.Throws<SqliteException>(()=>db.Execute("DELETE FROM historical_baseline_retry"));Assert.Throws<SqliteException>(()=>db.Execute("UPDATE historical_baseline_retry SET request_id='changed'"));
        Assert.AreEqual(1,t.RecoveryAcks.Count);Assert.AreEqual(2,t.RecoveryCalls);
    }
    [TestMethod]
    public async Task BaselineRetry_LostAckNewGenerationUsesBoundPredecessorAndWaitsWithoutEnqueueingAnotherIntent()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var t=await FailedPreparation(f,g);
        var initial=await Store(f,g).PrepareFailedRetryAsync(default);t.LoseRecovery=true;
        await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>t.Client().RunAsync(default));
        t.G=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(t.G,2);t.RetryPending=true;
        Assert.AreEqual("history_baseline_retry_pending",(await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>t.Client().RunAsync(default))).Code);
        var successor=await Store(f,t.G).ContinuePendingFailedRetryAsync(initial.FlowId,default);Assert.AreEqual(initial.RequestId,successor.PreviousRecoveryRequestId);
        await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>t.Client().RunAsync(default));
        Assert.AreEqual(successor.RequestId,(await Store(f,t.G).ContinuePendingFailedRetryAsync(initial.FlowId,default)).RequestId);
        t.RetryPending=false;Assert.IsTrue(await t.Client().RunAsync(default));
        using var c=f.Db.Factory.Open();Assert.AreEqual("superseded",c.ExecuteScalar<string>("SELECT state FROM historical_baseline_retry WHERE request_id=@id",new{id=initial.RequestId}));
        Assert.AreEqual("consumed",c.ExecuteScalar<string>("SELECT state FROM historical_baseline_retry WHERE request_id=@id",new{id=successor.RequestId}));Assert.AreEqual(2L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_baseline_retry"));
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BaselineRetry_TwoSuccessiveLoginsKeepOriginalServerResolvableRoot(bool middleAcceptedAckLost)
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var t=await FailedPreparation(f,g);
        var first=await Store(f,g).PrepareFailedRetryAsync(default);t.LoseRecovery=true;
        await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>t.Client().RunAsync(default));
        t.G=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(t.G,2);
        t.RetryPending=!middleAcceptedAckLost;t.LoseRecovery=middleAcceptedAckLost;
        await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>t.Client().RunAsync(default));
        var middle=await Store(f,t.G).ContinuePendingFailedRetryAsync(first.FlowId,default);
        Assert.AreEqual(first.RequestId,middle.PreviousRecoveryRequestId);
        t.G=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(t.G,3);t.RetryPending=false;
        var last=await Store(f,t.G).ContinuePendingFailedRetryAsync(first.FlowId,default);
        Assert.AreEqual(first.RequestId,last.PreviousRecoveryRequestId);Assert.AreNotEqual(middle.RequestId,last.PreviousRecoveryRequestId);
        Assert.IsTrue(await t.Client().RunAsync(default));
        using var c=f.Db.Factory.Open();Assert.AreEqual(1L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_baseline_retry WHERE state='consumed'"));
        Assert.AreEqual(2L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_baseline_retry WHERE state='superseded'"));
        Assert.AreEqual(1,t.PrepareCalls);Assert.AreEqual(1,t.UploadCalls);Assert.AreEqual(1,t.StartCalls);
    }
    [TestMethod]
    public async Task BaselineRetry_PermanentDenialRetiresAllPendingSuccessorsAndNeedsAnotherExplicitRequest()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var t=await FailedPreparation(f,g);
        await Store(f,g).PrepareFailedRetryAsync(default);t.G=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(t.G,2);t.RetryDenied=true;
        Assert.AreEqual("history_baseline_retry_conflict",(await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>t.Client().RunAsync(default))).Code);
        var calls=t.RecoveryCalls;await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>t.Client().RunAsync(default));Assert.AreEqual(calls,t.RecoveryCalls);
        using var c=f.Db.Factory.Open();Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_baseline_retry WHERE state='prepared'"));Assert.AreEqual(2L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_baseline_retry WHERE state='rejected'"));
    }
    [TestMethod]
    [DataRow("shop")][DataRow("device")][DataRow("actor")][DataRow("session")][DataRow("request")][DataRow("run")][DataRow("apply")][DataRow("previous")][DataRow("committed")]
    public async Task BaselineRetry_UnboundAckCannotReopenFailedFlow(string mutation)
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();await FailedPreparation(f,g);var flow=(await Store(f,g).ReadAsync(Server,1,default))!;
        var r=await Store(f,g).PrepareFailedRetryAsync(default);var ack=RecoveryAck(flow,r,g);
        var source=mutation switch{"shop"=>flow.ShopId,"device"=>flow.DeviceId,"actor"=>flow.StaffId,"session"=>g.PosSessionId,"request"=>r.RequestId,"run"=>flow.RunId,"apply"=>flow.ApplyRequestId,_=>""};
        ack=mutation=="previous"?ack.Replace("\"previous_review_digest\":null","\"previous_review_digest\":\""+new string('a',64)+"\""):mutation=="committed"?ack.Replace("uncommitted","committed"):ack.Replace(source,Guid.NewGuid().ToString("D"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Store(f,g).ResolveFailedRetryAsync(r.RequestId,ack,null!,default));
        Assert.AreEqual("needs_attention",(await Store(f,g).ReadAsync(Server,1,default))!.State);
        using var c=f.Db.Factory.Open();Assert.AreEqual("prepared",c.ExecuteScalar<string>("SELECT state FROM historical_baseline_retry"));
    }
    [TestMethod]
    public async Task BaselineRetry_ReadStatusDoesNotBlockLocalHistoryWithoutCurrentOnlineGeneration()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();
        Assert.IsNull((await Store(f,g).ReadRetryStatusAsync(default)).State);
        await FailedPreparation(f,g);await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(Generation(),2);
        var status=await Store(f,g).ReadRetryStatusAsync(default);Assert.AreEqual("needs_attention",status.State);Assert.IsFalse(status.CanRequestRetry);
        await Assert.ThrowsAsync<OnlineSyncGenerationChangedException>(()=>Store(f,g).PrepareFailedRetryAsync(default));
    }
    [TestMethod]
    public async Task BaselineRetry_OriginalActorSourceAndGenerationRemainMandatory()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();await FailedPreparation(f,g);var original=Store(f,g);
        var peer=Generation(Guid.NewGuid().ToString("D"));await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(peer,2);
        Assert.IsFalse((await Store(f,peer).ReadRetryStatusAsync(default)).CanRequestRetry);
        await Assert.ThrowsAsync<OnlineSyncGenerationChangedException>(()=>original.PrepareFailedRetryAsync(default));
        Assert.AreEqual("history_baseline_original_actor_required",(await Assert.ThrowsAsync<InvalidOperationException>(()=>Store(f,peer).PrepareFailedRetryAsync(default))).Message);
        var current=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(current,3);File.AppendAllText(f.Archive,"changed");
        Assert.AreEqual("history_baseline_source_changed",(await Assert.ThrowsAsync<InvalidOperationException>(()=>Store(f,current).PrepareFailedRetryAsync(default))).Message);
        using var c=f.Db.Factory.Open();Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_baseline_retry"));
    }
}
