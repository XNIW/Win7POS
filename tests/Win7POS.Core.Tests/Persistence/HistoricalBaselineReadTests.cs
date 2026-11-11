using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Core.Reports;
using Win7POS.Data.Import;
using Win7POS.Data.Reports;

namespace Win7POS.Core.Tests.Persistence;

public sealed partial class HistoricalReplicaTests
{
    [TestMethod]
    public async Task BaselineRead_ActivatesOnCompleteReplicaAndOriginalReceiptIncludingEmptyDeltaWithoutEcho()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var facts=f.ReadFacts();var store=new HistoricalReplicaStore(f.Db.Factory,()=>{});
        var rows=RowsWithOtherPos(f);await store.ObserveAuthorityAsync(Shop,Authority("local"),Ct);
        var flow=await SeedBaselineFlow(f);var m=Manifest(rows);var g=await store.BeginAsync(Shop,m,Ct);
        await store.StageAsync(g,Page(m,rows,Cursor,null),Ct);await store.PublishAsync(g,Ct);
        Assert.AreEqual(3380L,(await Report(f)).Summary.BalanceClp,"A replica alone must not adopt an unconfirmed baseline.");
        Assert.AreEqual(0,(await store.ReadAsync(Shop,Ct)).CanonicalReadEnabled);
        CompleteBaselineFlow(f,flow);var local=await Report(f);var delta=Manifest(rows,mode:"delta");
        var candidate=await store.BeginAsync(Shop,delta,Ct);await store.StageAsync(candidate,Page(delta,Array.Empty<HistoricalReplicaEntity>(),Cursor,null),Ct);
        await store.PublishAsync(candidate,Ct);
        var state=await store.ReadAsync(Shop,Ct);Assert.AreEqual(0,state.EverWeb);Assert.AreEqual("local",state.WriterAuthority);Assert.AreEqual(1,state.CanonicalReadEnabled);
        var report=await Report(f);Assert.AreEqual(3457L,report.Summary.BalanceClp);Assert.AreEqual(6L,report.Summary.HistoricalMovementCount);
        Assert.AreEqual(SalesReportCoverageStatus.CloudReplicaCached,report.Coverage.Status);
        var coordinator=SalesReportSnapshotCoordinator.ForDatabase(f.Db.Factory.DbPath);Assert.IsFalse(coordinator.IsCurrent(local.Token));
        var stable=report.Token;candidate=await store.BeginAsync(Shop,delta,Ct);await store.StageAsync(candidate,Page(delta,Array.Empty<HistoricalReplicaEntity>(),Cursor,null),Ct);await store.PublishAsync(candidate,Ct);
        Assert.IsTrue(coordinator.IsCurrent(stable),"An already activated unchanged read source must not reload open reports.");
        Assert.AreEqual(facts,f.ReadFacts(),"No local bank/native facts or original operation receipt changed.");
    }

    [TestMethod]
    public async Task BaselineRead_PartialReplicaCannotActivateAndTheMarkerIsPermanentWithinItsOriginalScope()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var store=new HistoricalReplicaStore(f.Db.Factory,()=>{});
        await store.ObserveAuthorityAsync(Shop,Authority("local"),Ct);var flow=await SeedBaselineFlow(f);CompleteBaselineFlow(f,flow);
        using(var c=f.Db.Factory.Open())Assert.Throws<SqliteException>(()=>c.Execute(@"INSERT INTO historical_baseline_read_mode
SELECT shop_id,local_dataset_id,restore_lineage_id,server_dataset_id,flow_id FROM historical_baseline_flow WHERE flow_id=@flow",new{flow}));
        var rows=RowsWithOtherPos(f);var m=Manifest(rows);var g=await store.BeginAsync(Shop,m,Ct);var next=Guid.NewGuid().ToString("D");
        await store.StageAsync(g,Page(m,rows.Take(2).ToArray(),Cursor,next),Ct);
        Assert.AreEqual(3380L,(await Report(f)).Summary.BalanceClp);Assert.AreEqual(0,(await store.ReadAsync(Shop,Ct)).CanonicalReadEnabled);
        await store.StageAsync(g,Page(m,rows.Skip(2).ToArray(),next,null),Ct);await store.PublishAsync(g,Ct);
        using(var c=f.Db.Factory.Open())
        {
            Assert.Throws<SqliteException>(()=>c.Execute("DELETE FROM historical_baseline_read_mode"));
            Assert.Throws<SqliteException>(()=>c.Execute("UPDATE historical_baseline_read_mode SET server_dataset_id=@other",new{other=Guid.NewGuid().ToString("D")}));
            Assert.Throws<SqliteException>(()=>c.Execute("UPDATE historical_baseline_flow SET state='prepared'"));
        }
        var changed=Authority("local",revision:"1");changed.DatasetId=Guid.NewGuid().ToString("D");await store.ObserveAuthorityAsync(Shop,changed,Ct);
        var report=await Report(f);Assert.AreEqual(SalesReportCoverageStatus.CloudReplicaRequired,report.Coverage.Status);
        Assert.AreEqual(0L,report.Summary.HistoricalMovementCount,"Server dataset change cannot silently restore the old local totals.");
    }

    [TestMethod]
    public async Task BaselineRead_CorruptReceiptBytesRollBackReadActivationAndReplicaPointerTogether()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var store=new HistoricalReplicaStore(f.Db.Factory,()=>{});
        await store.ObserveAuthorityAsync(Shop,Authority("local"),Ct);var flow=await SeedBaselineFlow(f);CompleteBaselineFlow(f,flow,badHash:true);
        var rows=Entities(f);var m=Manifest(rows);var g=await store.BeginAsync(Shop,m,Ct);await store.StageAsync(g,Page(m,rows,Cursor,null),Ct);
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>store.PublishAsync(g,Ct));Assert.AreEqual("history_baseline_receipt_invalid",error.Message);
        Assert.IsNull((await store.ReadAsync(Shop,Ct)).GenerationId);
        using var c=f.Db.Factory.Open();Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_baseline_read_mode"));
        Assert.AreEqual(3380L,(await Report(f)).Summary.BalanceClp);
    }

    // SQL metadata fixture only; no fabricated claim of authenticated baseline E2E.
    static async Task<string> SeedBaselineFlow(HistoricalCloudExportTests.Fixture f)
    {
        var state=await f.Service.ReadDatasetAsync(Ct);var flow=Guid.NewGuid().ToString("D");
        using var c=f.Db.Factory.Open();c.Execute(@"INSERT INTO historical_baseline_flow(flow_id,shop_id,device_id,staff_id,local_dataset_id,restore_lineage_id,restore_epoch,
local_revision,server_dataset_id,writer_epoch,archive_path,backup_path,archive_sha256,manifest_sha256,backup_sha256,manifest_json,start_request_id,apply_request_id,state)
VALUES(@flow,@Shop,@device,'30000000-0000-4000-8000-000000000019',@dataset,@lineage,0,@revision,@Dataset,2,'synthetic-archive','synthetic-backup',@hash,@hash,@hash,'{}',@start,@apply,'prepared')",
            new{flow,Shop,device=HistoricalCloudExportTests.Fixture.Device,dataset=state.DatasetId,lineage=Guid.Parse(state.RestoreLineageId).ToString("D"),revision=state.DataRevision,Dataset,hash=new string('a',64),start=Guid.NewGuid().ToString("D"),apply=Guid.NewGuid().ToString("D")});
        return flow;
    }
    static void CompleteBaselineFlow(HistoricalCloudExportTests.Fixture f,string flow,bool badHash=false)
    {
        const string receipt="{\"synthetic_sql_receipt\":true}";using var c=f.Db.Factory.Open();using var t=c.BeginTransaction();
        c.Execute("UPDATE historical_baseline_flow SET state='upload_created',upload_id=@id WHERE flow_id=@flow",new{flow,id=Guid.NewGuid().ToString("D")},t);
        c.Execute("UPDATE historical_baseline_flow SET state='upload_ready' WHERE flow_id=@flow",new{flow},t);
        c.Execute("UPDATE historical_baseline_flow SET state='run_started',run_id=@id WHERE flow_id=@flow",new{flow,id=Guid.NewGuid().ToString("D")},t);
        c.Execute("UPDATE historical_baseline_flow SET review_digest=@hash WHERE flow_id=@flow",new{flow,hash=new string('a',64)},t);
        c.Execute("UPDATE historical_baseline_flow SET state='applied',receipt_json=@receipt,receipt_sha256=@hash,source_snapshot_id=@id,server_revision=1 WHERE flow_id=@flow",
            new{flow,receipt,hash=badHash?new string('e',64):Hash(receipt),id=Guid.NewGuid().ToString("D")},t);t.Commit();
    }
    static HistoricalReplicaEntity[] RowsWithOtherPos(HistoricalCloudExportTests.Fixture f)
    {
        var rows=Entities(f).ToList();var original=rows.First(x=>x.Kind=="movement");
        var extra=System.Text.Json.JsonSerializer.Deserialize<HistoricalReplicaEntity>(System.Text.Json.JsonSerializer.Serialize(original))!;
        var p=extra.Payload;extra.Id=p.Id=Guid.NewGuid().ToString("D");var batch=Guid.NewGuid().ToString("D");p.BatchId=batch;
        p.ExternalId="SYNTHETIC-OTHER-POS";p.IdentityKey=Hash(Shop+"\u001fgetnet\u001f"+p.MerchantId+"\u001f"+p.ExternalNamespace+"\u001f"+p.ExternalId);
        p.GrossClp=p.NetClp="77";p.FeeClp=p.FeeVatClp="0";p.MovementKind="Sale";p.TransactionState="ABONADO";
        p.EconomicDigest=new HistoricalSalesEconomicValue(long.Parse(p.BusinessTicks,CultureInfo.InvariantCulture),77,0,0,77,p.MovementKind,p.TransactionState).Fingerprint;
        extra.Digest=HistoricalReplicaProtocol.EntityDigest(extra);rows.Add(extra);
        var b=new HistoricalReplicaEntity{Kind="batch",Id=batch,Payload=new(){Id=batch,Origin="local_migration",State="active",ApplyOperationId=Guid.NewGuid().ToString("D"),CreatedRevision="1",CreatedUtcMs="1706745600000"}};
        b.Digest=HistoricalReplicaProtocol.EntityDigest(b);rows.Add(b);return rows.ToArray();
    }
}
