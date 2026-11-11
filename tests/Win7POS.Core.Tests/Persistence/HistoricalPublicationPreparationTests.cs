using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Import;

namespace Win7POS.Core.Tests.Persistence;

public sealed partial class HistoricalLedgerTests
{
    const string PreparationServerDataset="c0000000-0000-4000-8000-000000000019";
    static HistoricalCloudResult PreparationAuthority()=>new(){DatasetId=PreparationServerDataset,WriterEpoch="1",WriterAuthority="local",ProtocolVersion=1};
    static async Task<HistoricalPublicationDraft> PreparationDraft(Fixture f)
    {
        var seed=await f.Plan();var state=await f.Service.ReadDatasetAsync(default);
        return HistoricalPublicationDraft.ForApply(new HistoricalSalesImportPlan(seed.OperationId,PublicationShop,seed.DatasetId,seed.DataRevision,seed.RestoreEpoch,
            seed.Analysis,seed.Assignments,seed.NativeStartBusinessDateTime,seed.OverlapEvidenceReference),PublicationDevice,state.RestoreLineageId);
    }
    static HistoricalCloudResult PreparationProgress(HistoricalPublicationDraft draft,int next)
        =>new(){State="preparing",OperationId=Guid.Parse(draft.OperationId).ToString("D"),ContentDigest=draft.ContentDigest,NextOrdinal=next,PublicationPageCount=draft.PageCount};
    static HistoricalCloudResult PreparationGrant(HistoricalPublicationDraft draft,string mismatch="")
    {
        var grant=Guid.NewGuid().ToString("D");var op=Guid.Parse(draft.OperationId).ToString("D");
        var proof=new HistoricalPublicationProof(grant,op,mismatch=="shop"?Guid.NewGuid().ToString("D"):draft.ShopId,
            mismatch=="device"?Guid.NewGuid().ToString("D"):draft.DeviceId,mismatch=="dataset"?"other-dataset":draft.DatasetId,
            mismatch=="server"?Guid.NewGuid().ToString("D"):PreparationServerDataset,
            mismatch=="lineage"?Guid.NewGuid().ToString("D"):draft.RestoreLineageId,draft.RestoreEpoch,mismatch=="epoch"?2:1,
            mismatch=="root"?new string('f',64):draft.ContentDigest,mismatch=="request"?new string('e',64):draft.RequestFingerprint,"content_authorized_v2",
            new HistoricalPublicationLocalDecision(0,0,0,0,0,0,0),new HistoricalPublicationCanonicalDecision(Guid.NewGuid().ToString("D"),1,0,0,0,0));
        return new(){State="authorized",OperationId=op,ContentDigest=draft.ContentDigest,GrantId=grant,WriterEpoch="1",RemainingMilliseconds=60000,PublicationProof=proof};
    }

    [TestMethod]
    public async Task PublicationPreparation_StagesBeforeFreshGrantWithoutRetainingSpoolLockOrWritingLedger()
    {
        using var f=new Fixture();using var draft=await PreparationDraft(f);var calls=new List<string>();long clock=0;
        async Task<HistoricalCloudResult> Request(string op,HistoricalCloudPayload payload,CancellationToken ct)
        {
            calls.Add(op);
            if(op=="local_prepare")return PreparationProgress(draft,1);
            if(op=="local_stage")
            {
                Assert.AreEqual(draft.Pages(default).Single().Digest,payload.PageSha256);
                Assert.AreEqual(draft.PageCount,await Task.Run(()=>draft.Pages(default).Count()).WaitAsync(TimeSpan.FromSeconds(2)));
                clock=600000;return PreparationProgress(draft,payload.Ordinal!.Value+1);
            }
            Assert.AreEqual("local_authorize",op);clock+=7;return PreparationGrant(draft);
        }
        var admission=await new HistoricalPublicationPreparer(f.Factory,()=>{},Request,()=>clock).PrepareAsync(draft,PreparationAuthority(),default);
        Assert.AreEqual(7L,admission.GrantRequestElapsedMilliseconds);Assert.AreEqual(60000,admission.RemainingMilliseconds);
        CollectionAssert.AreEqual(new[]{"local_prepare","local_stage","local_authorize"},calls);
        using var c=f.Factory.Open();Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation"));
        Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_publication_outbox"));
    }

    [TestMethod]
    public async Task PublicationPreparation_ResumesAlreadyStagedContentWithoutSendingPagesAgain()
    {
        using var f=new Fixture();using var draft=await PreparationDraft(f);var calls=new List<string>();
        Task<HistoricalCloudResult> Request(string op,HistoricalCloudPayload _,CancellationToken ct)
        {calls.Add(op);return Task.FromResult(op=="local_prepare"?PreparationProgress(draft,draft.PageCount+1):PreparationGrant(draft));}
        await new HistoricalPublicationPreparer(f.Factory,()=>{},Request).PrepareAsync(draft,PreparationAuthority(),default);
        CollectionAssert.AreEqual(new[]{"local_prepare","local_authorize"},calls);
    }

    [TestMethod]
    public async Task PublicationPreparation_LostAuthorizationResponseDoesNotRenewOrFallbackToUnboundGrant()
    {
        using var f=new Fixture();using var draft=await PreparationDraft(f);var calls=new List<string>();
        Task<HistoricalCloudResult> Request(string op,HistoricalCloudPayload _,CancellationToken ct)
        {calls.Add(op);if(op=="local_authorize")throw new HistoricalCloudRequestException("synthetic_lost_ack",false,true);return Task.FromResult(PreparationProgress(draft,draft.PageCount+1));}
        await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>new HistoricalPublicationPreparer(f.Factory,()=>{},Request).PrepareAsync(draft,PreparationAuthority(),default));
        CollectionAssert.AreEqual(new[]{"local_prepare","local_authorize"},calls);
    }

    [TestMethod]
    [DataRow("shop")][DataRow("device")][DataRow("dataset")][DataRow("server")]
    [DataRow("lineage")][DataRow("epoch")][DataRow("root")][DataRow("request")]
    public async Task PublicationPreparation_RejectsMismatchedContentAuthorization(string mismatch)
    {
        using var f=new Fixture();using var draft=await PreparationDraft(f);
        Task<HistoricalCloudResult> Request(string op,HistoricalCloudPayload _,CancellationToken ct)
            =>Task.FromResult(op=="local_prepare"?PreparationProgress(draft,draft.PageCount+1):PreparationGrant(draft,mismatch));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new HistoricalPublicationPreparer(f.Factory,()=>{},Request).PrepareAsync(draft,PreparationAuthority(),default));
    }

    [TestMethod]
    public async Task PublicationPreparation_InvalidProgressStopsBeforeGrant()
    {
        using var f=new Fixture();using var draft=await PreparationDraft(f);var calls=0;
        Task<HistoricalCloudResult> Request(string op,HistoricalCloudPayload _,CancellationToken ct)
        {calls++;var result=PreparationProgress(draft,1);result.ContentDigest=new string('f',64);return Task.FromResult(result);}
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new HistoricalPublicationPreparer(f.Factory,()=>{},Request).PrepareAsync(draft,PreparationAuthority(),default));
        Assert.AreEqual(1,calls);
    }

    [TestMethod]
    public async Task PublicationPreparation_CancellationDuringStagingCannotRequestGrant()
    {
        using var f=new Fixture();using var draft=await PreparationDraft(f);using var cancel=new CancellationTokenSource();var calls=new List<string>();
        Task<HistoricalCloudResult> Request(string op,HistoricalCloudPayload _,CancellationToken ct)
        {calls.Add(op);if(op=="local_stage")cancel.Cancel();return Task.FromResult(PreparationProgress(draft,op=="local_prepare"?1:2));}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>new HistoricalPublicationPreparer(f.Factory,()=>{},Request).PrepareAsync(draft,PreparationAuthority(),cancel.Token));
        CollectionAssert.AreEqual(new[]{"local_prepare","local_stage"},calls);
    }

    [TestMethod]
    public async Task PublicationPreparation_TransportTimeCannotExtendTheExistingGrantSafetyMargin()
    {
        using var f=new Fixture();using var draft=await PreparationDraft(f);long clock=0;
        Task<HistoricalCloudResult> Request(string op,HistoricalCloudPayload _,CancellationToken ct)
        {if(op=="local_prepare")return Task.FromResult(PreparationProgress(draft,draft.PageCount+1));clock=50000;return Task.FromResult(PreparationGrant(draft));}
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new HistoricalPublicationPreparer(f.Factory,()=>{},Request,()=>clock).PrepareAsync(draft,PreparationAuthority(),default));
    }

    [TestMethod]
    [DataRow("frozen")][DataRow("web")]
    public async Task PublicationPreparation_ClosedWriterCannotStageOrObtainAnotherGrant(string authority)
    {
        using var f=new Fixture();using var draft=await PreparationDraft(f);var calls=0;var state=PreparationAuthority();state.WriterAuthority=authority;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new HistoricalPublicationPreparer(f.Factory,()=>{},(_,_,_)=>
        {calls++;return Task.FromResult(PreparationGrant(draft));}).PrepareAsync(draft,state,default));
        Assert.AreEqual(0,calls);
    }

    [TestMethod]
    public async Task PublicationPreparation_UndoReadsTheExistingApplyAndExactLocalRevisionBeforeAdmission()
    {
        using var f=new Fixture();var plan=await f.Plan();var apply=await f.Service.ApplyAsync(plan,f.Guard(plan),f.Backup(),default);
        var guard=f.UndoGuard(apply.BatchId,1);var state=await f.Service.ReadDatasetAsync(default);
        using var draft=await f.Service.PrepareUndoPublicationDraftAsync(guard.OperationId,guard.ShopId,guard.DeviceId,guard.DatasetId,guard.RestoreEpoch,apply.BatchId,1,default);
        Assert.AreEqual(state.DataRevision,draft.BaseRevision);Assert.AreEqual("Undo",draft.Kind);
        StringAssert.Contains(draft.ReadPage(1,default).Json,plan.OperationId);
        var receipt=await f.Service.UndoAsync(apply.BatchId,1,guard,f.Backup(),default,publicationDraft:draft);
        Assert.AreEqual("Undone",receipt.CurrentBatchState);
        using var c=f.Factory.Open();Assert.AreEqual(plan.OperationId,c.ExecuteScalar<string>("SELECT depends_on_operation_id FROM historical_publication_outbox WHERE operation_id=@id",new{id=guard.OperationId}));
        Assert.AreEqual(draft.ContentDigest,c.ExecuteScalar<string>("SELECT content_digest FROM historical_publication_outbox WHERE operation_id=@id",new{id=guard.OperationId}));
    }

    [TestMethod]
    public async Task PublicationPreparation_UndoCannotCommitAfterAnotherLocalHistoricalRevision()
    {
        using var f=new Fixture();var plan=await f.Plan();var apply=await f.Service.ApplyAsync(plan,f.Guard(plan),f.Backup(),default);
        var guard=f.UndoGuard(apply.BatchId,1);
        using var draft=await f.Service.PrepareUndoPublicationDraftAsync(guard.OperationId,guard.ShopId,guard.DeviceId,guard.DatasetId,guard.RestoreEpoch,apply.BatchId,1,default);
        var other=await f.Plan(id:"other");await f.Service.ApplyAsync(other,f.Guard(other),f.Backup(),default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.UndoAsync(apply.BatchId,1,guard,f.Backup(),default,publicationDraft:draft));
        using var c=f.Factory.Open();Assert.AreEqual("Active",c.ExecuteScalar<string>("SELECT state FROM historical_import_batch WHERE id=@id",new{id=apply.BatchId}));
        Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT count(*) FROM historical_import_operation WHERE operation_id=@id",new{id=guard.OperationId}));
        Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT count(*) FROM historical_publication_outbox WHERE operation_id=@id",new{id=guard.OperationId}));
    }

    [TestMethod]
    public async Task PublicationPreparation_UndoRejectsForeignOrStaleBatchesBeforeStaging()
    {
        using var f=new Fixture();var plan=await f.Plan();var apply=await f.Service.ApplyAsync(plan,f.Guard(plan),f.Backup(),default);
        var guard=f.UndoGuard(apply.BatchId,1);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.PrepareUndoPublicationDraftAsync(guard.OperationId,"other-shop",guard.DeviceId,guard.DatasetId,guard.RestoreEpoch,apply.BatchId,1,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.PrepareUndoPublicationDraftAsync(guard.OperationId,guard.ShopId,guard.DeviceId,guard.DatasetId,guard.RestoreEpoch,apply.BatchId,2,default));
        using var c=f.Factory.Open();Assert.AreEqual(1L,c.ExecuteScalar<long>("SELECT count(*) FROM historical_publication_outbox"));
    }
}
