using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Data;
using Win7POS.Data.Import;

namespace Win7POS.Core.Tests.Persistence;

public sealed partial class HistoricalLedgerTests
{
    [TestMethod]
    public void PublicationJournal_SchemaAdoptionRejectsChangedStateLiteralInATrigger()
    {
        using var f=new Fixture();using var c=f.Factory.Open();
        var trigger=c.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type='trigger' AND name='historical_preparation_outcome_update'")??throw new AssertFailedException("Missing journal outcome guard.");
        var changed=trigger.Replace("'committed'","'COMMITTED'",StringComparison.Ordinal);
        Assert.AreNotEqual(trigger,changed);
        c.Execute("DROP TRIGGER historical_preparation_outcome_update;");c.Execute(changed);
        Assert.IsFalse(HistoricalPreparationSchema.IsSatisfied(new Win7POS.Data.Migrations.LegacySchemaDetector(c)),"State literals are case sensitive; a restored guard with altered states is not canonical.");
    }

    [TestMethod]
    public async Task PublicationJournal_InterruptedPageStagingSurvivesSchemaValidationAndResumesTheSameIntent()
    {
        using var f=new Fixture();using var draft=await PreparationDraft(f);var checks=0;
        await Assert.ThrowsAsync<OperationCanceledException>(()=>new HistoricalPublicationJournal(f.Factory).SaveIntentAsync(draft,PreparationServerDataset,1,
            ()=>{if(++checks==3)throw new OperationCanceledException("synthetic_preparation_interruption");},default));
        string nonce;using(var c=f.Factory.Open())
        {
            nonce=c.ExecuteScalar<string>("SELECT local_nonce FROM historical_preparation_intent")??throw new AssertFailedException("Missing durable intent nonce.");
            Assert.AreEqual("staging",c.ExecuteScalar<string>("SELECT state FROM historical_preparation_outcome"));
            Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_preparation_page"));
        }
        f.Restart();DbInitializer.EnsureCreated(PosDbOptions.ForPath(f.Factory.DbPath).WithAccessContext(f.Factory.AccessContext));
        var journal=new HistoricalPublicationJournal(f.Factory);
        Assert.IsNull(await journal.ReadPendingDraftAsync(draft.OperationId,PublicationShop,PublicationDevice,PreparationServerDataset,1,()=>{},default));
        Assert.AreEqual(nonce,await journal.SaveIntentAsync(draft,PreparationServerDataset,1,()=>{},default));
        using var recovered=await journal.ReadPendingDraftAsync(draft.OperationId,PublicationShop,PublicationDevice,PreparationServerDataset,1,()=>{},default);
        Assert.IsNotNull(recovered);Assert.AreEqual(draft.ContentDigest,recovered.ContentDigest);
    }

    [TestMethod]
    public async Task PublicationJournal_RestartRecoversOriginalPagesAndNonceBeforeAnyNetworkOrLedgerMutation()
    {
        using var f=new Fixture();var draft=await PreparationDraft(f);var operation=draft.OperationId;
        var expected=draft.Pages(default).Select(p=>(p.Ordinal,p.Json,p.Digest)).ToArray();var root=draft.ContentDigest;
        var journal=new HistoricalPublicationJournal(f.Factory);
        var nonce=await journal.SaveIntentAsync(draft,PreparationServerDataset,1,()=>{},default);
        Assert.AreEqual(nonce,await journal.SaveIntentAsync(draft,PreparationServerDataset,1,()=>{},default));
        draft.Dispose();f.Restart();journal=new HistoricalPublicationJournal(f.Factory);
        using var recovered=await journal.ReadPendingDraftAsync(operation,PublicationShop,PublicationDevice,PreparationServerDataset,1,()=>{},default);
        Assert.IsNotNull(recovered);Assert.AreEqual(root,recovered.ContentDigest);
        CollectionAssert.AreEqual(expected,recovered.Pages(default).Select(p=>(p.Ordinal,p.Json,p.Digest)).ToArray());
        Assert.AreEqual(nonce,await journal.SaveIntentAsync(recovered,PreparationServerDataset,1,()=>{},default));
        using var c=f.Factory.Open();Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation"));
        Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_publication_outbox"));
        Assert.AreEqual("prepared",c.ExecuteScalar<string>("SELECT state FROM historical_preparation_outcome"));
    }

    [TestMethod]
    public async Task PublicationJournal_LostAuthorizeAckLeavesDurableOriginalIntentForReconciliation()
    {
        using var f=new Fixture();using var draft=await PreparationDraft(f);var requests=0;
        async Task<Win7POS.Core.Online.HistoricalCloudResult> Request(string operation,Win7POS.Core.Online.HistoricalCloudPayload payload,CancellationToken ct)
        {
            requests++;
            using(var c=f.Factory.Open())Assert.AreEqual(1L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_preparation_outcome WHERE state='prepared'"));
            if(operation=="local_prepare")return PreparationProgress(draft,1);
            if(operation=="local_stage")return PreparationProgress(draft,payload.Ordinal!.Value+1);
            await Task.Yield();throw new IOException("synthetic_ack_lost");
        }
        await Assert.ThrowsAsync<IOException>(()=>new HistoricalPublicationPreparer(f.Factory,()=>{},Request).PrepareAsync(draft,PreparationAuthority(),default));
        Assert.AreEqual(3,requests);f.Restart();
        using var recovered=await new HistoricalPublicationJournal(f.Factory).ReadPendingDraftAsync(draft.OperationId,PublicationShop,PublicationDevice,PreparationServerDataset,1,()=>{},default);
        Assert.IsNotNull(recovered);Assert.AreEqual(draft.ContentDigest,recovered.ContentDigest);
        using var c=f.Factory.Open();Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation"));
        Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_publication_outbox"));
    }

    [TestMethod]
    public async Task PublicationJournal_TombstonePreventsFutureApplyOfTheSameAuthorizedIntent()
    {
        using var f=new Fixture();var seed=await f.Plan();
        var plan=new HistoricalSalesImportPlan(seed.OperationId,PublicationShop,seed.DatasetId,seed.DataRevision,seed.RestoreEpoch,seed.Analysis,seed.Assignments,seed.NativeStartBusinessDateTime,seed.OverlapEvidenceReference);
        using var draft=HistoricalPublicationDraft.ForApply(plan,PublicationDevice,(await f.Service.ReadDatasetAsync(default)).RestoreLineageId);
        var preview=await f.Service.PreviewAsync(plan,default);
        var proof=new HistoricalPublicationProof(Guid.NewGuid().ToString("D"),Guid.Parse(plan.OperationId).ToString("D"),PublicationShop,PublicationDevice,plan.DatasetId,PreparationServerDataset,draft.RestoreLineageId,plan.RestoreEpoch,1,draft.ContentDigest,plan.RequestFingerprint,"content_authorized_v2",
            new HistoricalPublicationLocalDecision(preview.NewCount,preview.DuplicateCount,plan.Analysis.RejectedRows.Count,0,0,preview.NewGrossClp,preview.NewCount),
            new HistoricalPublicationCanonicalDecision(Guid.NewGuid().ToString("D"),1,preview.NewCount,preview.DuplicateCount,0,preview.NewGrossClp));
        var journal=new HistoricalPublicationJournal(f.Factory);var nonce=await journal.SaveIntentAsync(draft,PreparationServerDataset,1,()=>{},default);
        await journal.SaveGrantAsync(draft.OperationId,nonce,proof,()=>{},default);
        await journal.AbortWithoutCommitAsync(draft.OperationId,nonce,PublicationShop,PublicationDevice,()=>{},default);
        await journal.AbortWithoutCommitAsync(draft.OperationId,nonce,PublicationShop,PublicationDevice,()=>{},default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>journal.SaveIntentAsync(draft,PreparationServerDataset,1,()=>{},default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.ApplyAsync(plan,PublicationGuard(HistoricalImportOperationKind.Apply,plan.OperationId,plan.RequestFingerprint,plan.DatasetId,plan.RestoreEpoch),f.Backup(),default,publicationDraft:draft,publicationProof:proof));
        using var c=f.Factory.Open();Assert.AreEqual("aborted",c.ExecuteScalar<string>("SELECT state FROM historical_preparation_outcome"));
        Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation"));
        Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_transactions"));
        Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_publication_outbox"));
    }

    [TestMethod]
    public async Task PublicationJournal_LocalCommitTransfersPagesAtomicallyAndCannotBeReportedAbsent()
    {
        using var f=new Fixture();var applied=await CreatePublication(f,PublicationGeneration(),"journal-commit");
        string nonce;using(var c=f.Factory.Open())
        {
            nonce=c.ExecuteScalar<string>("SELECT local_nonce FROM historical_preparation_intent")??throw new AssertFailedException("Missing durable intent nonce.");
            Assert.AreEqual("committed",c.ExecuteScalar<string>("SELECT state FROM historical_preparation_outcome"));
            Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_preparation_page"));
            Assert.IsTrue(c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_publication_page")>0);
            Assert.AreEqual(1L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation"));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new HistoricalPublicationJournal(f.Factory).AbortWithoutCommitAsync(applied.Plan.OperationId,nonce,PublicationShop,PublicationDevice,()=>{},default));
    }

    [TestMethod]
    public async Task PublicationJournal_FailedLocalTransactionPreservesAuthorizedIntentAndPages()
    {
        using var f=new Fixture();f.Hooks.AtPhase=(phase,_,_)=>{if(phase=="before_commit")throw new InvalidOperationException("synthetic_local_rollback");};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>CreatePublication(f,PublicationGeneration(),"journal-rollback"));
        using var c=f.Factory.Open();Assert.AreEqual("authorized",c.ExecuteScalar<string>("SELECT state FROM historical_preparation_outcome"));
        Assert.IsTrue(c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_preparation_page")>0);
        Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_publication_page"));
        Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation"));
        Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_transactions"));
    }

    [TestMethod]
    public async Task PublicationJournal_CannotChangeScopeOrDeleteUnresolvedEvidence()
    {
        using var f=new Fixture();using var draft=await PreparationDraft(f);var journal=new HistoricalPublicationJournal(f.Factory);
        var nonce=await journal.SaveIntentAsync(draft,PreparationServerDataset,1,()=>{},default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>journal.SaveIntentAsync(draft,Guid.NewGuid().ToString("D"),1,()=>{},default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>journal.ReadPendingDraftAsync(draft.OperationId,Guid.NewGuid().ToString("D"),PublicationDevice,PreparationServerDataset,1,()=>{},default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>journal.AbortWithoutCommitAsync(draft.OperationId,Guid.NewGuid().ToString("D"),PublicationShop,PublicationDevice,()=>{},default));
        using var c=f.Factory.Open();Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>c.Execute("DELETE FROM historical_preparation_page"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>c.Execute("UPDATE historical_preparation_intent SET content_digest='changed'"));
        Assert.AreEqual(nonce,c.ExecuteScalar<string>("SELECT local_nonce FROM historical_preparation_intent"));
    }
}
