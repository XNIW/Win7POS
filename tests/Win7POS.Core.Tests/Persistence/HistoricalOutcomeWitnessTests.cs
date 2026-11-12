using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Data.Import;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Persistence;

public sealed partial class HistoricalLedgerTests
{
    [TestMethod]
    public async Task OutcomeWitness_CommitBindsOriginalReceiptProofSequenceAndSurvivesRestartByteExactly()
    {
        using var f=new Fixture();var g=PublicationGeneration();await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(g,1);
        var applied=await CreatePublication(f,g,"outcome-positive");var journal=new HistoricalPublicationJournal(f.Factory);
        var witness=await journal.ReadOutcomeWitnessAsync(applied.Plan.OperationId,PublicationShop,PublicationDevice,()=>{},default);
        Assert.IsNotNull(witness);Assert.AreEqual("committed",witness.Outcome);Assert.AreEqual(applied.Proof.OperationId,witness.OperationId);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(witness.Json))),witness.Sha256);
        using(var c=f.Factory.Open())
        using(var json=JsonDocument.Parse(witness.Json))
        {
            var root=json.RootElement;Assert.AreEqual(25,root.EnumerateObject().Count());Assert.AreEqual("Apply",root.GetProperty("operation_kind").GetString());
            Assert.AreEqual(c.ExecuteScalar<string>("SELECT local_nonce FROM historical_preparation_intent"),root.GetProperty("local_nonce").GetString());
            Assert.AreEqual(applied.Proof.GrantId,root.GetProperty("grant_id").GetString());
            Assert.AreEqual(c.ExecuteScalar<long>("SELECT local_revision FROM historical_publication_outbox").ToString(),root.GetProperty("local_revision").GetString());
            Assert.AreEqual(c.ExecuteScalar<long>("SELECT sequence FROM historical_publication_outbox").ToString(),root.GetProperty("local_sequence").GetString());
            foreach(var (field,column) in new[]{("local_receipt_sha256","receipt_json"),("proof_sha256","proof_json")})
                Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(c.ExecuteScalar<string>("SELECT "+column+" FROM historical_publication_outbox")!))),root.GetProperty(field).GetString());
            Assert.AreEqual(10000L,root.GetProperty("minimum_remaining_milliseconds").GetInt64());
            Assert.IsTrue(root.GetProperty("remaining_milliseconds").GetInt64()-root.GetProperty("request_elapsed_milliseconds").GetInt64()
                -root.GetProperty("elapsed_milliseconds").GetInt64()-10000>=10000);
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>c.Execute("UPDATE historical_commit_witness SET witness_json='{}'"));
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>c.Execute("DELETE FROM historical_commit_witness"));
        }
        f.Restart();journal=new HistoricalPublicationJournal(f.Factory);
        var replay=await journal.ReadOutcomeWitnessAsync(applied.Plan.OperationId,PublicationShop,PublicationDevice,()=>{},default);
        Assert.AreEqual(witness.Json,replay.Json);Assert.AreEqual(witness.Sha256,replay.Sha256);
        var outbox=new HistoricalPublicationOutbox(f.Factory);var attempt=await outbox.ClaimAsync(g,()=>{},1000,default);
        var bad=ServerReceipt(f,applied);bad.ContentDigest=new string('e',64);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>outbox.ConfirmAsync(attempt,bad,g,()=>{},1001,default));
        using(var c=f.Factory.Open())Assert.IsNull(c.ExecuteScalar<string>("SELECT server_resolution FROM historical_commit_witness"));
        Assert.IsTrue(await outbox.ConfirmAsync(attempt,ServerReceipt(f,applied),g,()=>{},1002,default));
        using(var c=f.Factory.Open())
        {
            Assert.AreEqual("committed",c.ExecuteScalar<string>("SELECT server_resolution FROM historical_commit_witness"));
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>c.Execute("UPDATE historical_commit_witness SET server_resolution=NULL,resolved_at_utc_ms=NULL"));
        }
    }

    [TestMethod]
    public async Task OutcomeWitness_NegativeRequiresActualNoCommitAndOriginalTombstoneBeforeNetwork()
    {
        using var f=new Fixture();using var draft=await PreparationDraft(f);var journal=new HistoricalPublicationJournal(f.Factory);
        var nonce=await journal.SaveIntentAsync(draft,PreparationServerDataset,1,()=>{},default);
        await journal.AbortWithoutCommitAsync(draft.OperationId,nonce,PublicationShop,PublicationDevice,()=>{},default);
        var witness=await journal.ReadOutcomeWitnessAsync(draft.OperationId,PublicationShop,PublicationDevice,()=>{},default);
        Assert.IsNotNull(witness);Assert.AreEqual("aborted",witness.Outcome);
        using(var json=JsonDocument.Parse(witness.Json))
        {
            var root=json.RootElement;Assert.AreEqual(nonce,root.GetProperty("local_nonce").GetString());
            Assert.AreEqual(draft.ContentDigest,root.GetProperty("content_digest").GetString());
            Assert.AreEqual(draft.BaseRevision.ToString(),root.GetProperty("local_revision").GetString());
            foreach(var field in new[]{"grant_id","local_receipt_sha256","proof_sha256","admission_id","remaining_milliseconds","request_elapsed_milliseconds","elapsed_milliseconds","minimum_remaining_milliseconds"})
                Assert.AreEqual(JsonValueKind.Null,root.GetProperty(field).ValueKind);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(()=>journal.SaveIntentAsync(draft,PreparationServerDataset,1,()=>{},default));
        f.Restart();journal=new HistoricalPublicationJournal(f.Factory);
        await journal.AbortWithoutCommitAsync(draft.OperationId,nonce,PublicationShop,PublicationDevice,()=>{},default);
        Assert.AreEqual(witness.Json,(await journal.ReadOutcomeWitnessAsync(draft.OperationId,PublicationShop,PublicationDevice,()=>{},default)).Json);
        using var c=f.Factory.Open();Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation"));
        Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_publication_outbox"));
        c.Execute("UPDATE app_settings SET value=@lineage WHERE key='pos.history.restore_lineage_id'",new{lineage=Guid.NewGuid().ToString("D")});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>journal.ReadOutcomeWitnessAsync(draft.OperationId,PublicationShop,PublicationDevice,()=>{},default));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OutcomeWitness_ExpiredOrMissingLiveWindowAtFinalCommitRollsBackLedgerReceiptOutboxAndWitnessTogether(bool bindLiveWindow)
    {
        using var f=new Fixture();var seed=await f.Plan();
        var plan=new HistoricalSalesImportPlan(seed.OperationId,PublicationShop,seed.DatasetId,seed.DataRevision,seed.RestoreEpoch,seed.Analysis,seed.Assignments,seed.NativeStartBusinessDateTime,seed.OverlapEvidenceReference);
        using var draft=HistoricalPublicationDraft.ForApply(plan,PublicationDevice,(await f.Service.ReadDatasetAsync(default)).RestoreLineageId);
        var preview=await f.Service.PreviewAsync(plan,default);
        var proof=new HistoricalPublicationProof(Guid.NewGuid().ToString("D"),Guid.Parse(plan.OperationId).ToString("D"),PublicationShop,PublicationDevice,plan.DatasetId,PreparationServerDataset,draft.RestoreLineageId,plan.RestoreEpoch,1,draft.ContentDigest,plan.RequestFingerprint,"content_authorized_v2",
            new HistoricalPublicationLocalDecision(preview.NewCount,preview.DuplicateCount,plan.Analysis.RejectedRows.Count,0,0,preview.NewGrossClp,preview.NewCount),new HistoricalPublicationCanonicalDecision(Guid.NewGuid().ToString("D"),1,preview.NewCount,preview.DuplicateCount,0,preview.NewGrossClp));
        await PersistProofIntent(f,draft,proof);long clock=1000;
        var grant=new HistoricalLocalWriteGrant(proof.OperationId,"1",60000,0,()=>{},()=>clock,1000,proof);
        var guard=PublicationGuard(HistoricalImportOperationKind.Apply,plan.OperationId,plan.RequestFingerprint,plan.DatasetId,plan.RestoreEpoch);
        if(bindLiveWindow)guard.AttachPublicationGrant(grant);
        f.Hooks.AtPhase=(phase,_,_)=>{if(phase=="before_commit")clock=41001;};
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.ApplyAsync(plan,guard,f.Backup(),default,publicationDraft:draft,publicationProof:proof));
        Assert.AreEqual(bindLiveWindow?"history_local_write_grant_expired":"history_publication_live_admission_required",error.Message);
        using var c=f.Factory.Open();
        foreach(var table in new[]{"historical_transactions","historical_import_operation","historical_publication_outbox","historical_commit_witness"})Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM "+table),table);
        Assert.AreEqual("authorized",c.ExecuteScalar<string>("SELECT state FROM historical_preparation_outcome"));
        Assert.IsTrue(c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_preparation_page")>0);
    }
}
