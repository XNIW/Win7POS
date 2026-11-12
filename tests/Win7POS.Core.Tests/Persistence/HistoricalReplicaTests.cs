using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Core.Reports;
using Win7POS.Data.Import;
using Win7POS.Data.Reports;
using Win7POS.Data.Repositories;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Persistence;

[TestClass]
public sealed partial class HistoricalReplicaTests
{
    const string Shop=HistoricalCloudExportTests.Fixture.Shop,Dataset="90000000-0000-4000-8000-000000003019",Batch="80000000-0000-4000-8000-000000003019",Merchant="70000000-0000-4000-8000-000000003019",Cursor="60000000-0000-4000-8000-000000003019";
    static readonly CancellationToken Ct=CancellationToken.None;
    [TestMethod]
    public async Task AuthorityObservation_KnownRevisionCannotRegressAndDoesNotInvalidateReadableEconomics()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var store=new HistoricalReplicaStore(f.Db.Factory,()=>{});var rows=Entities(f);
        await store.ObserveAuthorityAsync(Shop,Authority(),Ct);var manifest=Manifest(rows);var g=await store.BeginAsync(Shop,manifest,Ct);
        await store.StageAsync(g,Page(manifest,rows,Cursor,null),Ct);await store.PublishAsync(g,Ct);var report=await Report(f);
        var coordinator=SalesReportSnapshotCoordinator.ForDatabase(f.Db.Factory.DbPath);var status=0;var reports=0;
        EventHandler statusChanged=(_,_)=>status++;EventHandler snapshotChanged=(_,_)=>reports++;
        coordinator.HistoricalSyncStatusChanged+=statusChanged;coordinator.SnapshotChanged+=snapshotChanged;
        try
        {
            await store.ObserveAuthorityAsync(Shop,Authority(revision:"3"),Ct);
            await store.ObserveAuthorityAsync(Shop,Authority(revision:"3"),Ct);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>store.ObserveAuthorityAsync(Shop,Authority(revision:"2"),Ct));
            var state=await store.ReadAsync(Shop,Ct);Assert.AreEqual(3L,state.ServerRevision);Assert.AreEqual(1L,state.PublishedRevision);
            Assert.AreEqual(1,status);Assert.AreEqual(0,reports);Assert.AreEqual(3380L,(await Report(f)).Summary.BalanceClp);
            await Query(f).ValidateAsync(report.Token,Filter(),null,Ct);
        }
        finally{coordinator.HistoricalSyncStatusChanged-=statusChanged;coordinator.SnapshotChanged-=snapshotChanged;}
    }
    [TestMethod]
    public async Task CompleteReplica_PublishesOnceWithoutAddingTheLocalLegacyTotals()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var original=f.ReadFacts();var store=new HistoricalReplicaStore(f.Db.Factory,()=>{});
        var source=Entities(f);await store.ObserveAuthorityAsync(Shop,Authority(),Ct);
        Assert.AreEqual(SalesReportCoverageStatus.CloudReplicaRequired,(await Report(f)).Coverage.Status);
        var manifest=Manifest(source);var generation=await store.BeginAsync(Shop,manifest,Ct);
        var first=Page(manifest,source.Take(2).ToArray(),Cursor,"61000000-0000-4000-8000-000000003019");
        await store.StageAsync(generation,first,Ct);await store.StageAsync(generation,first,Ct);
        var pending=await new HistoricalReplicaStore(f.Db.Factory,()=>{}).ReadPendingAsync(Shop,Ct);
        Assert.AreEqual(generation,pending.GenerationId);Assert.AreEqual(first.NextCursor,pending.Manifest.Cursor);Assert.IsFalse(pending.Complete);
        Assert.IsFalse((await Report(f)).Coverage.CanPublish,"Partial data cannot be reported as empty or combined with the legacy source.");
        await store.StageAsync(generation,Page(manifest,source.Skip(2).ToArray(),first.NextCursor,null),Ct);
        await store.PublishAsync(generation,Ct);
        Assert.IsNull((await store.ReadPendingAsync(Shop,Ct)).GenerationId);
        var report=await Report(f);Assert.AreEqual(3380L,report.Summary.BalanceClp);Assert.AreEqual(5L,report.Summary.HistoricalMovementCount);
        Assert.AreEqual(SalesReportCoverageStatus.CloudReplicaCached,report.Coverage.Status);Assert.IsTrue(report.Coverage.CanPublish);
        Assert.AreEqual(original,f.ReadFacts(),"Replica metadata must not change the retained bank/native ledger or receipt counts.");
        Assert.AreEqual(0L,(await Report(f,1001)).Summary.HistoricalMovementCount,"Unknown original cashiers never become the current operator.");
        await store.MarkStaleAsync(Shop,Ct);Assert.AreEqual(3380L,(await Report(f)).Summary.BalanceClp);
    }
    [TestMethod]
    public async Task UndoDelta_IsAtomicAndRollbackNeverRevealsTheOldLocalTotals()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var store=new HistoricalReplicaStore(f.Db.Factory,()=>{});var source=Entities(f);
        await store.ObserveAuthorityAsync(Shop,Authority(),Ct);var manifest=Manifest(source);var g=await store.BeginAsync(Shop,manifest,Ct);
        await store.StageAsync(g,Page(manifest,source,Cursor,null),Ct);await store.PublishAsync(g,Ct);var before=await Report(f);
        foreach(var entity in source){if(entity.Kind=="movement")entity.Payload.Active=false;else{entity.Payload.State="cancelled";entity.Payload.CancelledRevision="2";}entity.Digest=HistoricalReplicaProtocol.EntityDigest(entity);}
        await store.ObserveAuthorityAsync(Shop,Authority(revision:"2"),Ct);var delta=Manifest(source,revision:"2",mode:"delta");var stage=await store.BeginAsync(Shop,delta,Ct);
        await store.StageAsync(stage,Page(delta,source,Cursor,null),Ct);Assert.AreEqual(3380L,(await Report(f)).Summary.BalanceClp);
        await store.PublishAsync(stage,Ct);Assert.AreEqual(0L,(await Report(f)).Summary.BalanceClp);
        await Assert.ThrowsAsync<ReportSnapshotStaleException>(()=>Query(f).ValidateAsync(before.Token,Filter(),null,Ct));
        await store.ObserveAuthorityAsync(Shop,Authority("frozen","3","2"),Ct);Assert.AreEqual(0L,(await Report(f)).Summary.BalanceClp);
        using var c=f.Db.Factory.Open();Assert.AreEqual(5L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_transactions WHERE is_active=1"));
    }
    [TestMethod]
    public async Task ReplicaVerification_DoesNotReserveTheWriterWhileAnotherCommercialTransactionIsActive()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();
        var verifying=false;var calls=0;var verified=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store=new HistoricalReplicaStore(f.Db.Factory,()=>{if(verifying&&Interlocked.Increment(ref calls)==2)verified.TrySetResult(true);});
        var source=Entities(f);await store.ObserveAuthorityAsync(Shop,Authority(),Ct);var manifest=Manifest(source);
        var generation=await store.BeginAsync(Shop,manifest,Ct);await store.StageAsync(generation,Page(manifest,source,Cursor,null),Ct);
        using var c=f.Db.Factory.Open();using var writer=c.BeginTransaction(deferred:false);
        c.Execute("INSERT INTO app_settings(key,value) VALUES('qa.commercial.active','synthetic')",transaction:writer);
        verifying=true;var publish=Task.Run(()=>store.PublishAsync(generation,Ct));
        var readCompleted=false;
        try
        {
            readCompleted=ReferenceEquals(await Task.WhenAny(verified.Task,Task.Delay(750)),verified.Task);
            Assert.IsFalse(publish.IsCompleted,"The replica pointer committed inside another writer's transaction.");
        }
        finally{writer.Rollback();await publish;}
        Assert.IsTrue(readCompleted,"Replica verification tried to reserve the writer before reading and hashing its candidate.");
        Assert.AreEqual(3380L,(await Report(f)).Summary.BalanceClp);
    }
    [TestMethod]
    public async Task BadChecksumAndRevocation_LeaveTheLastCompleteGenerationUntouched()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var revoked=false;var store=new HistoricalReplicaStore(f.Db.Factory,()=>{if(revoked)throw new InvalidOperationException("revoked");});var source=Entities(f);
        await store.ObserveAuthorityAsync(Shop,Authority(),Ct);var manifest=Manifest(source);var g=await store.BeginAsync(Shop,manifest,Ct);
        await store.StageAsync(g,Page(manifest,source,Cursor,null),Ct);await store.PublishAsync(g,Ct);var prior=(await store.ReadAsync(Shop,Ct)).GenerationId;
        manifest.Checksum=new string('f',64);g=await store.BeginAsync(Shop,manifest,Ct);await store.StageAsync(g,Page(manifest,source,Cursor,null),Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.PublishAsync(g,Ct));Assert.AreEqual(prior,(await store.ReadAsync(Shop,Ct)).GenerationId);
        revoked=true;await Assert.ThrowsAsync<InvalidOperationException>(()=>store.PublishAsync(g,Ct));revoked=false;
        Assert.AreEqual(3380L,(await Report(f)).Summary.BalanceClp);
    }
    [TestMethod]
    public async Task PageScopeAndEpochChanges_CannotPublishACopiedReplica()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var store=new HistoricalReplicaStore(f.Db.Factory,()=>{});var source=Entities(f);await store.ObserveAuthorityAsync(Shop,Authority(),Ct);
        var manifest=Manifest(source);var g=await store.BeginAsync(Shop,manifest,Ct);var page=Page(manifest,source,Cursor,null);page.WriterEpoch="99";
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.StageAsync(g,page,Ct));page.WriterEpoch="2";await store.StageAsync(g,page,Ct);
        await store.ObserveAuthorityAsync(Shop,Authority("frozen","3"),Ct);await Assert.ThrowsAsync<InvalidOperationException>(()=>store.PublishAsync(g,Ct));
        Assert.IsNull((await store.ReadAsync(Shop,Ct)).GenerationId);
    }
    [TestMethod]
    public void LocalGrant_UsesAMonotonicDeadlineAndNeverSurvivesActorRevocation()
    {
        long clock=1000;var revoked=false;var grant=new HistoricalLocalWriteGrant(Cursor,"1",60000,100,()=>{if(revoked)throw new InvalidOperationException("revoked");},()=>clock,1000);
        clock=50899;grant.DemandStillValid();clock=50900;Assert.Throws<InvalidOperationException>(grant.DemandStillValid);
        clock=1000;revoked=true;Assert.Throws<InvalidOperationException>(grant.DemandStillValid);
        Assert.Throws<InvalidOperationException>(()=>new HistoricalLocalWriteGrant(Cursor,"1",10000,100,()=>{}));
    }
    [TestMethod]
    public void ReplicaIntegerProtocol_PreservesInt64AndRejectsRoundedOrNoncanonicalValues()
    {
        Assert.AreEqual(long.MaxValue,HistoricalReplicaProtocol.Integer("9223372036854775807"));
        foreach(var value in new[]{"9223372036854775808","1e3","01","-0","1.0"})Assert.Throws<InvalidOperationException>(()=>HistoricalReplicaProtocol.Integer(value));
    }
    [TestMethod]
    public async Task ReplicaPublication_InvalidatesOpenMemoryTokenAndDoesNotReloadForRepeatedEmptyDeltas()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();
        var store=new HistoricalReplicaStore(f.Db.Factory,()=>{});
        var coordinator=SalesReportSnapshotCoordinator.ForDatabase(f.Db.Factory.DbPath);
        var notifications=0;
        EventHandler changed=(_,_)=>notifications++;
        coordinator.SnapshotChanged+=changed;
        try
        {
            var local=await Report(f);
            await store.ObserveAuthorityAsync(Shop,Authority(),Ct);
            Assert.IsFalse(coordinator.IsCurrent(local.Token),"Authority changed without invalidating an already open report.");
            var rows=Entities(f);var m=Manifest(rows);var g=await store.BeginAsync(Shop,m,Ct);
            await store.StageAsync(g,Page(m,rows,Cursor,null),Ct);await store.PublishAsync(g,Ct);
            var published=await Report(f);var before=notifications;
            Assert.IsTrue(coordinator.IsCurrent(published.Token));
            var delta=Manifest(rows,mode:"delta");var candidate=await store.BeginAsync(Shop,delta,Ct);
            await store.StageAsync(candidate,Page(delta,Array.Empty<HistoricalReplicaEntity>(),Cursor,null),Ct);
            await store.PublishAsync(candidate,Ct);
            Assert.IsTrue(coordinator.IsCurrent(published.Token));Assert.AreEqual(before,notifications);
            await store.MarkStaleAsync(Shop,Ct);
            Assert.IsFalse(coordinator.IsCurrent(published.Token));Assert.AreEqual(before+1,notifications);
            await store.MarkStaleAsync(Shop,Ct);Assert.AreEqual(before+1,notifications);
            var cached=await Report(f);Assert.AreEqual(published.Summary.BalanceClp,cached.Summary.BalanceClp);
        }
        finally{coordinator.SnapshotChanged-=changed;}
    }
    [TestMethod]
    public async Task RepeatedEmptyDeltas_ReusesPublishedRowsAndBoundsDatabasePages()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var store=new HistoricalReplicaStore(f.Db.Factory,()=>{});var rows=Entities(f);
        await store.ObserveAuthorityAsync(Shop,Authority(),Ct);var m=Manifest(rows);var g=await store.BeginAsync(Shop,m,Ct);await store.StageAsync(g,Page(m,rows,Cursor,null),Ct);await store.PublishAsync(g,Ct);
        using var c=f.Db.Factory.Open();var ids=string.Join(",",c.Query<long>("SELECT id FROM historical_cloud_entity ORDER BY id"));var pages=c.ExecuteScalar<long>("PRAGMA page_count");
        for(var i=0;i<40;i++)
        {
            var delta=Manifest(rows,mode:"delta");var candidate=await store.BeginAsync(Shop,delta,Ct);
            Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_cloud_entity WHERE generation_id=@candidate",new{candidate}));
            await store.StageAsync(candidate,Page(delta,Array.Empty<HistoricalReplicaEntity>(),Cursor,null),Ct);await store.PublishAsync(candidate,Ct);
            Assert.AreEqual(g,(await store.ReadAsync(Shop,Ct)).GenerationId);
        }
        Assert.AreEqual(ids,string.Join(",",c.Query<long>("SELECT id FROM historical_cloud_entity ORDER BY id")));
        Assert.AreEqual(1L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_cloud_generation"));
        Assert.IsTrue(c.ExecuteScalar<long>("PRAGMA page_count")<=pages+8,"Repeated unchanged sync must reuse SQLite pages, not grow by dataset size.");
    }
    [TestMethod]
    public async Task InterruptedDelta_ClonesOnlyAtFirstChangeAndCleanupKeepsRecoveryAndPublishedCopy()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var store=new HistoricalReplicaStore(f.Db.Factory,()=>{});var rows=Entities(f);
        await store.ObserveAuthorityAsync(Shop,Authority(),Ct);var m=Manifest(rows);var original=await store.BeginAsync(Shop,m,Ct);await store.StageAsync(original,Page(m,rows,Cursor,null),Ct);await store.PublishAsync(original,Ct);
        var delta=Manifest(rows,revision:"2",mode:"delta");await store.ObserveAuthorityAsync(Shop,Authority(revision:"2"),Ct);var candidate=await store.BeginAsync(Shop,delta,Ct);
        using var c=f.Db.Factory.Open();var next="61000000-0000-4000-8000-000000003019";
        await store.StageAsync(candidate,Page(delta,Array.Empty<HistoricalReplicaEntity>(),Cursor,next),Ct);
        Assert.AreEqual(6L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_cloud_entity"));
        await store.PruneAsync(Shop,Ct);Assert.AreEqual(candidate,(await store.ReadPendingAsync(Shop,Ct)).GenerationId);
        var last="62000000-0000-4000-8000-000000003019";
        await store.StageAsync(candidate,Page(delta,rows.Take(1).ToArray(),next,last),Ct);
        Assert.AreEqual(12L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_cloud_entity"));
        await store.StageAsync(candidate,Page(delta,rows.Skip(1).ToArray(),last,null),Ct);
        Assert.AreEqual(12L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_cloud_entity"));
        Assert.AreEqual(original,(await store.ReadAsync(Shop,Ct)).GenerationId);await store.PublishAsync(candidate,Ct);
        Assert.AreEqual(6L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_cloud_entity"));Assert.AreEqual(1L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_cloud_generation"));
    }
    [TestMethod]
    public async Task ExpiredAbandonedAndIncompatibleStagesAreReclaimed_ButLiveTransfersAreNotEvicted()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var store=new HistoricalReplicaStore(f.Db.Factory,()=>{});var rows=Entities(f);await store.ObserveAuthorityAsync(Shop,Authority(),Ct);var m=Manifest(rows);var published=await store.BeginAsync(Shop,m,Ct);await store.StageAsync(published,Page(m,rows,Cursor,null),Ct);await store.PublishAsync(published,Ct);
        var pending=new List<string>();for(var i=0;i<3;i++)pending.Add(await store.BeginAsync(Shop,Manifest(rows),Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.BeginAsync(Shop,Manifest(rows),Ct));
        using var c=f.Db.Factory.Open();c.Execute("UPDATE historical_cloud_generation SET created_utc_ms=0 WHERE generation_id=@id",new{id=pending[0]});await store.PruneAsync(Shop,Ct);
        Assert.AreEqual(3L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_cloud_generation"));Assert.AreEqual(published,(await store.ReadAsync(Shop,Ct)).GenerationId);
        await store.ObserveAuthorityAsync(Shop,Authority("frozen","3"),Ct);await store.PruneAsync(Shop,Ct);
        Assert.AreEqual(1L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_cloud_generation"));Assert.AreEqual(3380L,(await Report(f)).Summary.BalanceClp);
    }
    [TestMethod]
    public async Task ReusedGeneration_CannotRegressRevisionWhenEmptyCandidatesRace()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var store=new HistoricalReplicaStore(f.Db.Factory,()=>{});var rows=Entities(f);await store.ObserveAuthorityAsync(Shop,Authority(),Ct);var m=Manifest(rows);var g=await store.BeginAsync(Shop,m,Ct);await store.StageAsync(g,Page(m,rows,Cursor,null),Ct);await store.PublishAsync(g,Ct);
        var earlier=Manifest(rows,revision:"2",mode:"delta");var later=Manifest(rows,revision:"3",mode:"delta");var a=await store.BeginAsync(Shop,earlier,Ct);var b=await store.BeginAsync(Shop,later,Ct);
        await store.StageAsync(a,Page(earlier,Array.Empty<HistoricalReplicaEntity>(),Cursor,null),Ct);await store.StageAsync(b,Page(later,Array.Empty<HistoricalReplicaEntity>(),Cursor,null),Ct);await store.PublishAsync(b,Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.PublishAsync(a,Ct));Assert.AreEqual(3L,(await store.ReadAsync(Shop,Ct)).PublishedRevision);Assert.AreEqual(3L,(await store.ReadAsync(Shop,Ct)).ServerRevision);Assert.AreEqual(g,(await store.ReadAsync(Shop,Ct)).GenerationId);
    }
    [TestMethod]
    public async Task Synchronizer_YieldsAfterEightPagesAndResumesWithoutReplacingTheReadableCopy()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();
        var store=new HistoricalReplicaStore(f.Db.Factory,()=>{});var source=Entities(f);
        await store.ObserveAuthorityAsync(Shop,Authority(),Ct);
        var old=Manifest(source);var oldGeneration=await store.BeginAsync(Shop,old,Ct);
        await store.StageAsync(oldGeneration,Page(old,source,Cursor,null),Ct);await store.PublishAsync(oldGeneration,Ct);
        var target=Manifest(source,revision:"2",mode:"delta");
        var requests=0;var pages=0;var ack=0;
        var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HistoricalCloudResult> Request(string op,HistoricalCloudPayload payload,CancellationToken token)
        {
            requests++;
            if(op=="authority"){entered.TrySetResult(true);await release.Task.WaitAsync(token);return Authority(revision:"2");}
            if(op=="delta_start")return target;
            if(op=="page")
            {
                pages++;var last=pages==9;
                return Page(target,last?source:Array.Empty<HistoricalReplicaEntity>(),payload.Cursor,last?null:Guid.NewGuid().ToString("D"));
            }
            if(op=="replica_ack"){ack++;return new(){Acknowledged=true,Revision="2",WriterEpoch="2"};}
            Assert.Fail("Unexpected operation "+op);return null!;
        }
        var sync=new HistoricalReplicaSynchronizer(store,()=>{},Request);
        var first=sync.RunAsync(Shop,new string('a',40),Ct);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(3380L,(await Report(f)).Summary.BalanceClp,"Network wait must retain the old complete read model.");
        Assert.AreEqual(oldGeneration,(await store.ReadAsync(Shop,Ct)).GenerationId);
        release.SetResult(true);Assert.IsTrue(await first);Assert.AreEqual(8,pages);Assert.AreEqual(0,ack);
        Assert.AreEqual(oldGeneration,(await store.ReadAsync(Shop,Ct)).GenerationId);
        Assert.IsFalse(await sync.RunAsync(Shop,new string('a',40),Ct));Assert.AreEqual(9,pages);Assert.AreEqual(1,ack);
        Assert.AreEqual(3380L,(await Report(f)).Summary.BalanceClp);Assert.AreEqual(2L,(await store.ReadAsync(Shop,Ct)).PublishedRevision);
        Assert.AreEqual(13,requests,"Two authority reads, one manifest, nine pages, one ACK; no repeated full download.");
    }

    [TestMethod]
    public async Task ReplicaCommit_RejectsAnInactiveDurableGenerationEvenIfTheCallerCallbackIsStale()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();
        var generation=new OnlineSyncGeneration(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("D"),Guid.NewGuid().ToString("D"),Shop,"fixture");
        await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(generation,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var store=new HistoricalReplicaStore(f.Db.Factory,()=>{},generation);var source=Entities(f);
        await store.ObserveAuthorityAsync(Shop,Authority(),Ct);var manifest=Manifest(source);var pending=await store.BeginAsync(Shop,manifest,Ct);
        await store.StageAsync(pending,Page(manifest,source,Cursor,null),Ct);
        using(var c=f.Db.Factory.Open())c.Execute("UPDATE pos_sync_session_generation SET active=0 WHERE singleton_id=1");
        await Assert.ThrowsAsync<OnlineSyncGenerationChangedException>(()=>store.PublishAsync(pending,Ct));
        Assert.IsNull((await store.ReadAsync(Shop,Ct)).GenerationId);
        using(var c=f.Db.Factory.Open())Assert.AreEqual(5L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_transactions WHERE is_active=1"));
    }

    static HistoricalCloudResult Authority(string writer="web",string epoch="2",string revision="1")=>new(){DatasetId=Dataset,Revision=revision,WriterEpoch=epoch,WriterAuthority=writer,ProtocolVersion=1};
    static HistoricalCloudResult Manifest(HistoricalReplicaEntity[] rows,string revision="1",string mode="snapshot")=>new(){DatasetId=Dataset,Revision=revision,WriterEpoch="2",Mode=mode,Cursor=Cursor,Movements=rows.Count(r=>r.Kind=="movement"),Batches=rows.Count(r=>r.Kind=="batch"),Checksum=HistoricalReplicaProtocol.SnapshotChecksum(Shop,Dataset,long.Parse(revision),2,rows)};
    static HistoricalCloudResult Page(HistoricalCloudResult manifest,HistoricalReplicaEntity[] rows,string cursor,string? next)=>new(){DatasetId=manifest.DatasetId,Revision=manifest.Revision,WriterEpoch=manifest.WriterEpoch,Cursor=cursor,NextCursor=next,Done=next==null,Checksum=manifest.Checksum,Movements=manifest.Movements,Batches=manifest.Batches,Entities=rows};
    static SalesReportFilter Filter(int? cashier=null)=>new(Shop,new DateTime(2024,1,1),new DateTime(2025,1,1),SalesReportOrigins.Historical,!cashier.HasValue,cashier,NativeSalesOwnershipRepository.PolicyHash,"");
    static SalesReportQuery Query(HistoricalCloudExportTests.Fixture f)=>new(f.Db.Factory,()=>f.Db.Factory.AuthorityStamp,SalesReportSnapshotCoordinator.ForDatabase(f.Db.Factory.DbPath));
    static Task<SalesReportSnapshot> Report(HistoricalCloudExportTests.Fixture f,int? cashier=null)=>Query(f).OpenAsync(Filter(cashier),null,200,Ct);
    static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    static HistoricalReplicaEntity[] Entities(HistoricalCloudExportTests.Fixture f)
    {
        var list=new List<HistoricalReplicaEntity>{new(){Kind="batch",Id=Batch,Payload=new(){Id=Batch,Origin="local_migration",State="active",ApplyOperationId="51000000-0000-4000-8000-000000003019",CreatedRevision="1",CreatedUtcMs="1706745600000"}}};
        using var c=f.Db.Factory.Open();var rows=c.Query("SELECT * FROM historical_transactions ORDER BY id").ToArray();var i=0;
        foreach(var r in rows)
        {
            var id=$"40000000-0000-4000-8000-{++i:000000000000}";
            var p=new HistoricalReplicaValue{Id=id,MerchantId=Merchant,BatchId=Batch,IdentityKey=Hash(Shop+"\u001fgetnet\u001f"+Merchant+"\u001f"+(string)r.external_namespace+"\u001f"+(string)r.external_id),Active=true,BusinessTicks=((long)r.business_ticks).ToString(CultureInfo.InvariantCulture),GrossClp=((long)r.signed_amount_clp).ToString(CultureInfo.InvariantCulture),FeeClp=((long?)r.signed_fee_clp)?.ToString(CultureInfo.InvariantCulture),FeeVatClp=((long?)r.signed_fee_vat_clp)?.ToString(CultureInfo.InvariantCulture),NetClp=((long?)r.signed_net_clp)?.ToString(CultureInfo.InvariantCulture),MovementKind=(string)r.movement_kind,TransactionState=HistoricalSalesValueRules.NormalizeHeader((string)r.state),ExternalNamespace=(string)r.external_namespace,ExternalId=(string)r.external_id,SourceSheet=(string)r.source_sheet,SourceRow=((long)r.source_row).ToString(CultureInfo.InvariantCulture),TimestampRaw=(string)r.timestamp_raw,OffsetMinutes=((long?)r.offset_minutes)?.ToString(CultureInfo.InvariantCulture),TimeInterpretation=r.offset_minutes==null?"Unresolved":"IsoUtc",TimePolicyVersion="synthetic-fixed-offset-v1",TimeEvidenceReference="synthetic evidence",CardSubtype=(string)r.card_subtype,CreatedRevision="1"};
            p.EconomicDigest=new HistoricalSalesEconomicValue((long)r.business_ticks,(long)r.signed_amount_clp,(long?)r.signed_fee_clp,(long?)r.signed_fee_vat_clp,(long?)r.signed_net_clp,p.MovementKind,p.TransactionState).Fingerprint;
            list.Add(new(){Kind="movement",Id=id,Payload=p});
        }
        foreach(var entity in list)entity.Digest=HistoricalReplicaProtocol.EntityDigest(entity);return list.ToArray();
    }
}
