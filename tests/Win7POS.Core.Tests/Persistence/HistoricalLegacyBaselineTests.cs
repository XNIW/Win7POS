using System.IO.Compression;
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
    [TestMethod]
    public async Task BaselineAdmission_RequiresOriginalAppliedReceiptBeforeAnyNewPreparationIntent()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var flow=await Prepare(f,g);var store=Store(f,g);
        Assert.IsFalse(await store.IsReadyForPublicationAsync(Server,1,default));
        flow=await RunStarted(f,g,flow);Assert.IsFalse(await store.IsReadyForPublicationAsync(Server,1,default));
        await store.SaveReceiptAsync(flow.FlowId,Receipt(flow),default);
        Assert.IsTrue(await store.IsReadyForPublicationAsync(Server,1,default));
        Assert.IsFalse(await store.IsReadyForPublicationAsync(Server,2,default));
        Assert.IsFalse(await store.IsReadyForPublicationAsync(Guid.NewGuid().ToString("D"),1,default));
        using var c=f.Db.Factory.Open();Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_preparation_intent"));
        Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_baseline_read_mode"),"Publication preflight does not select reports before the compatible replica arrives.");
        var next=Generation(Guid.NewGuid().ToString("D"));await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(next,2);
        Assert.IsTrue(await Store(f,next).IsReadyForPublicationAsync(Server,1,default),"A later authorized staff member may use the confirmed device baseline without adopting its original intent.");
        var original=await Store(f,next).ReadAsync(Server,1,default);Assert.AreEqual(flow.FlowId,original!.FlowId);Assert.AreEqual(g.StaffId,original.StaffId);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Store(f,next).SaveReceiptAsync(flow.FlowId,Receipt(flow),default));
        await Assert.ThrowsAsync<OnlineSyncGenerationChangedException>(()=>store.IsReadyForPublicationAsync(Server,1,default));
        c.Execute("UPDATE app_settings SET value=@lineage WHERE key='pos.history.restore_lineage_id'",new{lineage=Guid.NewGuid().ToString("D")});
        Assert.IsFalse(await Store(f,next).IsReadyForPublicationAsync(Server,1,default));
    }

    [TestMethod]
    public async Task LegacyBaseline_CapturesOnlyOriginalImmutableOperationsActuallyPresentInTheVerifiedArchive()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var flow=await Prepare(f,g);
        using var c=f.Db.Factory.Open();var operation=c.ExecuteScalar<string>("SELECT operation_id FROM historical_import_operation");
        Assert.AreEqual(operation,c.ExecuteScalar<string>("SELECT operation_id FROM historical_legacy_baseline_operation WHERE flow_id=@FlowId",flow));
        using(var zip=ZipFile.OpenRead(flow.ArchivePath))using(var reader=new StreamReader(zip.GetEntry("operations.ndjson")!.Open()))
        {using var row=JsonDocument.Parse((await reader.ReadLineAsync())!);Assert.AreEqual(operation,row.RootElement.GetProperty("operation_id").GetString());Assert.AreEqual(f.OriginalReceipt,row.RootElement.GetProperty("receipt_json").GetString());}
        var outbox=new HistoricalPublicationOutbox(f.Db.Factory);Assert.AreEqual(0L,(await outbox.ReadSummaryAsync(flow.ShopId,default)).BaselineCovered);
        flow=await RunStarted(f,g,flow);await Store(f,g).SaveReceiptAsync(flow.FlowId,Receipt(flow),default);
        Assert.AreEqual(0L,(await outbox.ReadSummaryAsync(flow.ShopId,default)).BaselineCovered,"Canonical publication also requires the complete compatible replica marker.");
        Assert.Throws<SqliteException>(()=>c.Execute("DELETE FROM historical_legacy_baseline_operation"));
        Assert.Throws<SqliteException>(()=>c.Execute("UPDATE historical_legacy_baseline_operation SET operation_id=operation_id"));
        Assert.AreEqual(f.OriginalReceipt,c.ExecuteScalar<string>("SELECT receipt_json FROM historical_import_operation"));
    }
    [TestMethod]
    public async Task LegacyBaseline_RegisteredUnknownOutcomeStillBlocksSourcePreparation()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var state=await f.Service.ReadDatasetAsync(default);
        using var c=f.Db.Factory.Open();var batch=c.QuerySingle("SELECT id,revision,apply_operation_id FROM historical_import_batch");
        using var draft=HistoricalPublicationDraft.ForUndo(Guid.NewGuid().ToString("N"),HistoricalCloudExportTests.Fixture.Shop,HistoricalCloudExportTests.Fixture.Device,
            state.DatasetId,0,state.DataRevision,(string)batch.id,(long)batch.revision,(string)batch.apply_operation_id,state.RestoreLineageId);
        await new HistoricalPublicationJournal(f.Db.Factory).SaveIntentAsync(draft,Server,1,()=>{},default);
        var root=Path.Combine(Path.GetTempPath(),"s-"+Guid.NewGuid().ToString("N"));
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>new HistoricalBaselineSource(f.Db.Factory,root).PrepareAsync(f.Guard().GetAwaiter().GetResult(),Guid.Parse(state.RestoreLineageId).ToString("D"),default));
        Assert.AreEqual("history_cloud_history_sync_required",error.Message);Assert.AreEqual(0,Directory.EnumerateDirectories(root).Count());
    }
}

public sealed partial class HistoricalReplicaTests
{
    [TestMethod]
    public async Task LegacyBaseline_VerifiedCoverageDrainsFrozenExportWithoutRetiringAnyUnresolvedV2Intent()
    {
        // Metadata fixture; this exercises the actual product drain, not
        // authenticated HTTP delivery or authority to activate a web writer.
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var flow=await SeedBaselineFlow(f);CaptureLegacyForSqlMetadataFixture(f,flow);
        CompleteBaselineFlow(f,flow);var prepared=new HistoricalPreparedExportStore(f.Db.Factory);var guard=await f.Guard();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>prepared.DemandHistoricalPublicationsDrainedAsync(guard,Ct));
        var replica=new HistoricalReplicaStore(f.Db.Factory,()=>{});await replica.ObserveAuthorityAsync(Shop,Authority("local"),Ct);
        var rows=Entities(f);var manifest=Manifest(rows);var stage=await replica.BeginAsync(Shop,manifest,Ct);
        await replica.StageAsync(stage,Page(manifest,rows,Cursor,null),Ct);await replica.PublishAsync(stage,Ct);
        await prepared.DemandHistoricalPublicationsDrainedAsync(guard,Ct);
        var state=await f.Service.ReadDatasetAsync(Ct);using var c=f.Db.Factory.Open();var batch=c.QuerySingle("SELECT id,revision,apply_operation_id FROM historical_import_batch");
        using var draft=HistoricalPublicationDraft.ForUndo(Guid.NewGuid().ToString("N"),Shop,HistoricalCloudExportTests.Fixture.Device,state.DatasetId,0,state.DataRevision,
            (string)batch.id,(long)batch.revision,(string)batch.apply_operation_id,state.RestoreLineageId);
        await new HistoricalPublicationJournal(f.Db.Factory).SaveIntentAsync(draft,Dataset,2,()=>{},Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>prepared.DemandHistoricalPublicationsDrainedAsync(guard,Ct));
    }

    static void CaptureLegacyForSqlMetadataFixture(HistoricalCloudExportTests.Fixture f,string flow)
    {
        using var c=f.Db.Factory.Open();c.Execute("INSERT INTO historical_legacy_baseline_operation SELECT @flow,operation_id FROM historical_import_operation",new{flow});
    }
    [TestMethod]
    public async Task LegacyBaseline_ReportsInitialCanonicalCoverageSeparatelyFromIncrementalAcknowledgements()
    {
        // Metadata fixture only; authenticated baseline delivery is qualified separately.
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var facts=f.ReadFacts();var flow=await SeedBaselineFlow(f);CaptureLegacyForSqlMetadataFixture(f,flow);
        CompleteBaselineFlow(f,flow);var outbox=new HistoricalPublicationOutbox(f.Db.Factory);Assert.AreEqual(0L,(await outbox.ReadSummaryAsync(Shop,Ct)).BaselineCovered);
        var store=new HistoricalReplicaStore(f.Db.Factory,()=>{});await store.ObserveAuthorityAsync(Shop,Authority("local"),Ct);
        var rows=Entities(f);var m=Manifest(rows);var stage=await store.BeginAsync(Shop,m,Ct);await store.StageAsync(stage,Page(m,rows,Cursor,null),Ct);await store.PublishAsync(stage,Ct);
        var summary=await outbox.ReadSummaryAsync(Shop,Ct);Assert.AreEqual(1L,summary.LocalOperations);Assert.AreEqual(1L,summary.BaselineCovered);Assert.AreEqual(0L,summary.Confirmed);
        Assert.AreEqual(0L,summary.Pending+summary.Attention+summary.Unregistered);Assert.AreEqual("1",summary.LastConfirmedRevision);
        using(var c=f.Db.Factory.Open())
        {var op=c.ExecuteScalar<string>("SELECT operation_id FROM historical_import_operation");Assert.AreEqual("baseline_covered",(await outbox.ReadStatusAsync(op,Shop,Ct)).Status);
         Assert.AreEqual("needs_attention",c.ExecuteScalar<string>("SELECT status FROM historical_publication_delivery"),"Original incremental delivery audit must not be relabelled synchronized.");}
        var changed=Authority("local");changed.DatasetId=Guid.NewGuid().ToString("D");await store.ObserveAuthorityAsync(Shop,changed,Ct);
        Assert.AreEqual(0L,(await outbox.ReadSummaryAsync(Shop,Ct)).BaselineCovered,"An unrelated server dataset cannot inherit a previous baseline claim.");Assert.AreEqual(facts,f.ReadFacts());
    }
    [TestMethod]
    public async Task LegacyBaseline_FirstAbortedV2IntentUsesOnlyAdmittedPositiveSequenceAndWaitsForVerifiedCoverage()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=new OnlineSyncGeneration(Guid.NewGuid().ToString("D"),Guid.NewGuid().ToString("D"),HistoricalCloudExportTests.Fixture.Device,Shop,"synthetic","30000000-0000-4000-8000-000000003019",7);
        await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(g,1);
        var flow=await SeedBaselineFlow(f);CaptureLegacyForSqlMetadataFixture(f,flow);CompleteBaselineFlow(f,flow);
        var state=await f.Service.ReadDatasetAsync(Ct);using var c=f.Db.Factory.Open();var b=c.QuerySingle("SELECT id,revision,apply_operation_id FROM historical_import_batch");
        using var draft=HistoricalPublicationDraft.ForUndo(Guid.NewGuid().ToString("N"),Shop,g.ShopDeviceId,state.DatasetId,0,state.DataRevision,(string)b.id,(long)b.revision,(string)b.apply_operation_id,state.RestoreLineageId);
        var journal=new HistoricalPublicationJournal(f.Db.Factory);await journal.SaveIntentAsync(draft,Dataset,2,()=>{},Ct);
        Assert.IsNull(await journal.ReadPendingNegativeOutcomeAsync(g,()=>{},Ct));
        var store=new HistoricalReplicaStore(f.Db.Factory,()=>{});await store.ObserveAuthorityAsync(Shop,Authority("local"),Ct);
        var rows=Entities(f);var m=Manifest(rows);var stage=await store.BeginAsync(Shop,m,Ct);await store.StageAsync(stage,Page(m,rows,Cursor,null),Ct);await store.PublishAsync(stage,Ct);
        var witness=await journal.ReadPendingNegativeOutcomeAsync(g,()=>{},Ct);Assert.IsNotNull(witness);Assert.AreEqual("aborted",witness.Outcome);
        using var json=JsonDocument.Parse(witness.Json);Assert.AreEqual("0",json.RootElement.GetProperty("local_sequence").GetString());
        Assert.AreEqual(1L,c.ExecuteScalar<long>("SELECT MAX(sequence) FROM historical_publication_outbox"),"Legacy physical outbox sequence is preserved, not rewritten into an incremental delivery.");
    }
}
