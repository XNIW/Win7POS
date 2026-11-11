using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Import;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Persistence;

public sealed partial class HistoricalBaselineStoreTests
{
    static string RecoveryAck(HistoricalBaselineFlow f,HistoricalBaselineRecovery r,OnlineSyncGeneration g,bool committed=false)=>JsonSerializer.Serialize(new {
        schema_version=1,state=committed?"committed":"uncommitted",shop_id=f.ShopId,device_id=f.DeviceId,staff_id=f.StaffId,session_id=g.PosSessionId,
        credential_version=g.StaffCredentialVersion,recovery_request_key=r.RequestId,run_id=f.RunId,apply_request_key=f.ApplyRequestId,previous_review_digest=r.PreviousReviewDigest});

    [TestMethod]
    public async Task BaselineRecovery_OnlyAuthenticatedOriginalUncommittedAckCanClearSessionReviewAndKeepItsHistory()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var facts=f.ReadFacts();var original=Generation();var flow=await RunStarted(f,original,await Prepare(f,original));
        var current=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(current,2);var store=Store(f,current);
        var pending=await store.PrepareRecoveryAsync(flow.FlowId,default);Assert.AreEqual(pending.RequestId,(await Store(f,current).PrepareRecoveryAsync(flow.FlowId,default)).RequestId);
        Assert.AreEqual(flow.ReviewDigest,(await store.ReadAsync(Server,1,default))!.ReviewDigest,"Preparing recovery cannot discard the original authorization.");
        var ack=RecoveryAck(flow,pending,current);await Store(f,current).ResolveRecoveryAsync(pending.RequestId,ack,null!,default);
        await Store(f,current).ResolveRecoveryAsync(pending.RequestId,ack,null!,default);
        var resumed=(await store.ReadAsync(Server,1,default))!;Assert.IsNull(resumed.ReviewDigest);Assert.AreEqual(current.PosSessionId,resumed.RunSessionId);Assert.AreEqual(flow.ApplyRequestId,resumed.ApplyRequestId);
        await store.SealReviewAsync(flow.FlowId,new string('b',64),default);
        using var c=f.Db.Factory.Open();Assert.AreEqual(flow.ReviewDigest,c.ExecuteScalar<string>("SELECT previous_review_digest FROM historical_baseline_recovery"));
        Assert.AreEqual("consumed",c.ExecuteScalar<string>("SELECT state FROM historical_baseline_recovery"));
        Assert.Throws<SqliteException>(()=>c.Execute("DELETE FROM historical_baseline_recovery"));
        Assert.Throws<SqliteException>(()=>c.Execute("UPDATE historical_baseline_recovery SET previous_review_digest=NULL"));
        Assert.AreEqual(facts,f.ReadFacts());
    }
    [TestMethod]
    [DataRow("session")][DataRow("request")][DataRow("outcome")][DataRow("extra")][DataRow("duplicate")][DataRow("previous")][DataRow("number")][DataRow("mixed")]
    public async Task BaselineRecovery_RejectsAmbiguousOrUnboundProofWithoutClearingTheOldReview(string mutation)
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var flow=await RunStarted(f,g,await Prepare(f,g));
        var current=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(current,2);var store=Store(f,current);
        var r=await store.PrepareRecoveryAsync(flow.FlowId,default);var ack=RecoveryAck(flow,r,current);string? receipt=null;
        ack=mutation switch {
            "session"=>ack.Replace(current.PosSessionId,Guid.NewGuid().ToString("D")),"request"=>ack.Replace(r.RequestId,Guid.NewGuid().ToString("D")),
            "outcome"=>ack.Replace("uncommitted","committed"),"extra"=>ack.Insert(1,"\"unknown\":true,"),"duplicate"=>ack.Insert(1,"\"state\":\"uncommitted\","),
            "previous"=>ack.Replace(new string('a',64),new string('b',64)),"number"=>ack.Replace("\"credential_version\":7","\"credential_version\":7.0"),
            "mixed"=>ack,_=>throw new AssertFailedException()};
        if(mutation=="mixed")receipt=Receipt(flow);
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>store.ResolveRecoveryAsync(r.RequestId,ack,receipt!,default));Assert.AreEqual("history_baseline_recovery_invalid",error.Message);
        var unchanged=(await store.ReadAsync(Server,1,default))!;Assert.AreEqual(flow.ReviewDigest,unchanged.ReviewDigest);Assert.AreEqual(flow.RunSessionId,unchanged.RunSessionId);
        using var c=f.Db.Factory.Open();Assert.AreEqual("prepared",c.ExecuteScalar<string>("SELECT state FROM historical_baseline_recovery"));Assert.IsNull(c.ExecuteScalar<string>("SELECT ack_json FROM historical_baseline_recovery"));
    }
    [TestMethod]
    public async Task BaselineRecovery_CommittedOriginalReceiptWinsAndKeepsEconomicActorThroughRestart()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var flow=await RunStarted(f,g,await Prepare(f,g));
        var current=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(current,2);var pending=await Store(f,current).PrepareRecoveryAsync(flow.FlowId,default);
        var ack=RecoveryAck(flow,pending,current,true);var receipt=Receipt(flow);
        await Store(f,current).ResolveRecoveryAsync(pending.RequestId,ack,receipt,default);
        var applied=(await Store(f,current).ReadAsync(Server,1,default))!;Assert.AreEqual("applied",applied.State);Assert.AreEqual(receipt,applied.ReceiptJson);Assert.AreEqual(g.PosSessionId,applied.RunSessionId);
        Assert.AreEqual(flow.ApplyRequestId,applied.ApplyRequestId);Assert.AreEqual(flow.ReviewDigest,applied.ReviewDigest);
        using var c=f.Db.Factory.Open();Assert.AreEqual("consumed",c.ExecuteScalar<string>("SELECT state FROM historical_baseline_recovery"));Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_baseline_read_mode"));
    }
    [TestMethod]
    public async Task BaselineRecovery_LaterSessionSupersedesUncertainTechnicalAckOnlyAfterItsOwnBoundProof()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var flow=await RunStarted(f,g,await Prepare(f,g));
        var second=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(second,2);var old=await Store(f,second).PrepareRecoveryAsync(flow.FlowId,default);
        var third=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(third,3);var next=await Store(f,third).PrepareRecoveryAsync(flow.FlowId,default);
        Assert.AreNotEqual(old.RequestId,next.RequestId);await Store(f,third).ResolveRecoveryAsync(next.RequestId,RecoveryAck(flow,next,third),null!,default);
        using var c=f.Db.Factory.Open();Assert.AreEqual("superseded",c.ExecuteScalar<string>("SELECT state FROM historical_baseline_recovery WHERE request_id=@id",new{id=old.RequestId}));
        Assert.AreEqual("consumed",c.ExecuteScalar<string>("SELECT state FROM historical_baseline_recovery WHERE request_id=@id",new{id=next.RequestId}));
        await Assert.ThrowsAsync<OnlineSyncGenerationChangedException>(()=>Store(f,second).ResolveRecoveryAsync(old.RequestId,RecoveryAck(flow,old,second),null!,default));
        Assert.AreEqual(third.PosSessionId,(await Store(f,third).ReadAsync(Server,1,default))!.RunSessionId);
    }
    [TestMethod]
    public async Task BaselineRecovery_MetadataCannotRelabelAnOldReviewWithoutServerAcknowledgement()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var flow=await RunStarted(f,g,await Prepare(f,g));
        var current=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(current,2);await Store(f,current).PrepareRecoveryAsync(flow.FlowId,default);
        using var c=f.Db.Factory.Open();Assert.Throws<SqliteException>(()=>c.Execute("UPDATE historical_baseline_flow SET review_digest=NULL,run_session_id=@session",new{session=current.PosSessionId}));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Store(f,current).SealReviewAsync(flow.FlowId,flow.ReviewDigest,default));
        Assert.AreEqual(flow.ReviewDigest,(await Store(f,current).ReadAsync(Server,1,default))!.ReviewDigest);
    }
}
