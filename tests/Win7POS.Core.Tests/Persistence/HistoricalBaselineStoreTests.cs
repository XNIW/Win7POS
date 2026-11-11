using System.Text.Json;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Import;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Persistence;

[TestClass]
public sealed class HistoricalBaselineStoreTests
{
    const string Server="90000000-0000-4000-8000-000000003019",Staff="30000000-0000-4000-8000-000000003019";
    static OnlineSyncGeneration Generation(string? staff=null)=>new(Guid.NewGuid().ToString("D"),Guid.NewGuid().ToString("D"),
        HistoricalCloudExportTests.Fixture.Device,HistoricalCloudExportTests.Fixture.Shop,"synthetic",staff??Staff,7);
    static HistoricalBaselineStore Store(HistoricalCloudExportTests.Fixture f,OnlineSyncGeneration g)=>new(f.Db.Factory,g,()=>{});
    static async Task<HistoricalBaselineFlow> Prepare(HistoricalCloudExportTests.Fixture f,OnlineSyncGeneration g)
    {
        await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(g,1);
        var state=await f.Service.ReadDatasetAsync(default);var guard=await f.Guard();
        var receipt=await new HistoricalCloudExporter(f.Db.Factory).ExportBaselineAsync(f.Archive,f.Backup,guard,Guid.Parse(state.RestoreLineageId).ToString("D"),default);
        return await Store(f,g).SavePreparedAsync(receipt,Server,1,guard,default);
    }
    static string Receipt(HistoricalBaselineFlow f)=>JsonSerializer.Serialize(new {
        operation_id="70000000-0000-4000-8000-000000003019",kind="migrate",source_run_id=f.RunId,
        source_snapshot_id="80000000-0000-4000-8000-000000003019",dataset_id=f.ServerDatasetId,revision="1",writer_epoch=f.WriterEpoch.ToString(),
        new_active=5,new_inactive=0,duplicates=0,new_batches=1,original_evidence_level="legacy_attested",
        baseline_context=new{state="baseline_verified",source_snapshot_id="80000000-0000-4000-8000-000000003019",local_dataset_id=f.LocalDatasetId,
            local_revision=f.LocalRevision.ToString(),restore_epoch=f.RestoreEpoch.ToString(),restore_lineage_id=f.RestoreLineageId,writer_epoch=f.WriterEpoch.ToString()}});
    static async Task<HistoricalBaselineFlow> RunStarted(HistoricalCloudExportTests.Fixture f,OnlineSyncGeneration g,HistoricalBaselineFlow flow)
    {
        await Store(f,g).SaveUploadAsync(flow.FlowId,Guid.NewGuid().ToString("D"),default);
        await Store(f,g).SaveReadyAsync(flow.FlowId,default);
        await Store(f,g).SaveRunAsync(flow.FlowId,Guid.NewGuid().ToString("D"),default);
        await Store(f,g).SealReviewAsync(flow.FlowId,new string('a',64),default);
        return (await Store(f,g).ReadAsync(Server,1,default))!;
    }
    [TestMethod]
    public async Task BaselineStore_RestartRecoversOriginalIntentAndReviewAndOriginalReceiptWithoutLedgerWrites()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var facts=f.ReadFacts();var g=Generation();
        var original=await Prepare(f,g);var prepared=await Store(f,g).ReadAsync(Server,1,default);
        Assert.AreEqual(original.FlowId,prepared!.FlowId);Assert.AreEqual(original.StartRequestId,prepared.StartRequestId);Assert.AreEqual(original.ApplyRequestId,prepared.ApplyRequestId);
        var started=await RunStarted(f,g,prepared);var json=Receipt(started);
        await Store(f,g).SealReviewAsync(started.FlowId,new string('a',64),default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Store(f,g).SealReviewAsync(started.FlowId,new string('b',64),default));
        await Store(f,g).SaveReceiptAsync(started.FlowId,json,default);await Store(f,g).SaveReceiptAsync(started.FlowId,json,default);
        var applied=await Store(f,g).ReadAsync(Server,1,default);Assert.AreEqual("applied",applied!.State);Assert.AreEqual(json,applied.ReceiptJson);
        using var c=f.Db.Factory.Open();Assert.AreEqual(1L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_baseline_flow"));
        Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_baseline_read_mode"),"Receipt alone must not switch reports before a verified complete replica.");
        Assert.AreEqual(facts,f.ReadFacts());
    }
    [TestMethod]
    public async Task BaselineStore_UnknownReceiptCannotAdvancePhaseOrSelectCanonicalReports()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var flow=await RunStarted(f,g,await Prepare(f,g));
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>Store(f,g).SaveReceiptAsync(flow.FlowId,Receipt(flow).Replace(Server,Guid.NewGuid().ToString("D")),default));
        Assert.AreEqual("history_baseline_receipt_invalid",error.Message);
        Assert.AreEqual("run_started",(await Store(f,g).ReadAsync(Server,1,default))!.State);
        using var c=f.Db.Factory.Open();Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_baseline_read_mode"));
    }
    [TestMethod]
    public async Task BaselineStore_CurrentRenewedOriginalActorCanRecoverButPeerAndRevokedGenerationCannotAdoptIntent()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var flow=await Prepare(f,g);var original=Store(f,g);
        var peer=Generation(Guid.NewGuid().ToString("D"));await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(peer,2);
        await Assert.ThrowsAsync<OnlineSyncGenerationChangedException>(()=>original.SaveUploadAsync(flow.FlowId,Guid.NewGuid().ToString("D"),default));
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>Store(f,peer).ReadAsync(Server,1,default));Assert.AreEqual("history_baseline_original_actor_required",error.Message);
        var renewed=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(renewed,3);
        Assert.AreEqual(flow.FlowId,(await Store(f,renewed).ReadAsync(Server,1,default))!.FlowId);
        await Store(f,renewed).SaveUploadAsync(flow.FlowId,Guid.NewGuid().ToString("D"),default);
    }
    [TestMethod]
    public async Task BaselineStore_ChangedArchiveAndOfficialLineageCannotBeResumed()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var flow=await Prepare(f,g);
        HistoricalBaselineStore.VerifyFiles(flow,default);File.AppendAllText(flow.ArchivePath,"changed");
        Assert.Throws<InvalidOperationException>(()=>HistoricalBaselineStore.VerifyFiles(flow,default));
        using(var c=f.Db.Factory.Open())c.Execute("UPDATE app_settings SET value=@lineage WHERE key='pos.history.restore_lineage_id'",new{lineage=Guid.NewGuid().ToString("D")});
        Assert.IsNull(await Store(f,g).ReadAsync(Server,1,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Store(f,g).SaveUploadAsync(flow.FlowId,Guid.NewGuid().ToString("D"),default));
    }
    [TestMethod]
    [DataRow("duplicate")][DataRow("extra")][DataRow("number")][DataRow("snapshot")][DataRow("negative")][DataRow("nested-extra")][DataRow("zero")]
    public void BaselineReceipt_RejectsAmbiguousOrUnboundAcknowledgements(string mutation)
    {
        var f=new HistoricalBaselineFlow{RunId="60000000-0000-4000-8000-000000003019",ServerDatasetId=Server,WriterEpoch=1,
            LocalDatasetId="synthetic-dataset",LocalRevision=9007199254740993,RestoreEpoch=0,RestoreLineageId="f0000000-0000-4000-8000-000000003019"};
        var json=Receipt(f);Assert.AreEqual(1L,HistoricalBaselineReceipt.Read(json,f).Revision);
        json=mutation switch {
            "duplicate"=>json.Insert(1,"\"kind\":\"migrate\","),"extra"=>json.Insert(1,"\"unexpected\":true,"),
            "number"=>json.Replace("\"9007199254740993\"","9007199254740993"),
            "snapshot"=>json.Replace("\"source_snapshot_id\":\"80000000-0000-4000-8000-000000003019\",\"local_dataset_id\"","\"source_snapshot_id\":\"80000000-0000-4000-8000-000000003020\",\"local_dataset_id\""),
            "negative"=>json.Replace("\"new_active\":5","\"new_active\":-1"),
            "nested-extra"=>json.Replace("\"baseline_context\":{","\"baseline_context\":{\"unexpected\":true,"),
            "zero"=>json.Replace("70000000-0000-4000-8000-000000003019",Guid.Empty.ToString("D")),_=>throw new AssertFailedException()};
        var error=Assert.Throws<InvalidOperationException>(()=>HistoricalBaselineReceipt.Read(json,f));Assert.AreEqual("history_baseline_receipt_invalid",error.Message);
    }
}
