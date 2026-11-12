using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Import;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Persistence;

public sealed partial class HistoricalLedgerTests
{
    [TestMethod]
    public async Task OutcomeSender_LiveOwnerBlocksAbsenceAndLostAckReplaysTheSameDurableTombstoneAfterRestart()
    {
        using var f=new Fixture();var g=PublicationGeneration();await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(g,1);
        using var draft=await PreparationDraft(f);var journal=new HistoricalPublicationJournal(f.Factory);
        using(var owner=HistoricalPublicationOperationUse.Begin(f.Factory,draft.OperationId))
        {
            await journal.SaveIntentAsync(draft,PreparationServerDataset,1,()=>{},default);
            var unexpected=new HistoricalPublicationOutcomeSender(journal,g,()=>{},(_,_,_)=>throw new AssertFailedException("Live caller must prevent HTTP."));
            Assert.IsFalse(await unexpected.RunAsync(default));
        }
        string? original=null;
        Task<HistoricalCloudResult> Lost(string op,HistoricalCloudPayload p,CancellationToken ct)
        {
            Assert.AreEqual("local_abort",op);Assert.IsNull(p.LocalProofJson);original=p.WitnessJson;
            using var c=f.Factory.Open();using var transaction=c.BeginTransaction();
            Assert.AreEqual("aborted",c.ExecuteScalar<string>("SELECT state FROM historical_preparation_outcome",transaction:transaction));
            Assert.AreEqual(p.WitnessJson,c.ExecuteScalar<string>("SELECT witness_json FROM historical_commit_witness",transaction:transaction));
            throw new HistoricalCloudRequestException("synthetic_ack_lost",false,true);
        }
        await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>new HistoricalPublicationOutcomeSender(journal,g,()=>{},Lost).RunAsync(default));
        using(var c=f.Factory.Open())Assert.IsNull(c.ExecuteScalar<string>("SELECT server_resolution FROM historical_commit_witness"));
        f.Restart();journal=new HistoricalPublicationJournal(f.Factory);var calls=0;
        Task<HistoricalCloudResult> Recovered(string op,HistoricalCloudPayload p,CancellationToken ct)
        {
            calls++;Assert.AreEqual("local_abort",op);Assert.AreEqual(original,p.WitnessJson);
            var digest=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(p.WitnessJson)));
            return Task.FromResult(new HistoricalCloudResult{State="aborted",OperationId=p.OperationId,ContentDigest=p.ContentDigest,WitnessSha256=digest});
        }
        var sender=new HistoricalPublicationOutcomeSender(journal,g,()=>{},Recovered,()=>1000);
        Assert.IsTrue(await sender.RunAsync(default));Assert.IsFalse(await sender.RunAsync(default));Assert.AreEqual(1,calls);
        using var db=f.Factory.Open();Assert.AreEqual("aborted",db.ExecuteScalar<string>("SELECT server_resolution FROM historical_commit_witness"));
        foreach(var table in new[]{"historical_import_operation","historical_publication_outbox","sales","sales_sync_outbox"})Assert.AreEqual(0L,db.ExecuteScalar<long>("SELECT COUNT(*) FROM "+table),table);
    }

    [TestMethod]
    [DataRow("sha")][DataRow("operation")][DataRow("root")][DataRow("state")]
    public async Task OutcomeSender_UnknownAckCannotResolveTheOriginalNegativeEvidence(string changed)
    {
        using var f=new Fixture();var g=PublicationGeneration();await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(g,1);
        using var draft=await PreparationDraft(f);var journal=new HistoricalPublicationJournal(f.Factory);await journal.SaveIntentAsync(draft,PreparationServerDataset,1,()=>{},default);
        Task<HistoricalCloudResult> Request(string op,HistoricalCloudPayload p,CancellationToken ct)
        {
            var sha=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(p.WitnessJson)));
            return Task.FromResult(new HistoricalCloudResult{State=changed=="state"?"committed":"aborted",OperationId=changed=="operation"?Guid.NewGuid().ToString("D"):p.OperationId,
                ContentDigest=changed=="root"?new string('e',64):p.ContentDigest,WitnessSha256=changed=="sha"?new string('e',64):sha});
        }
        var error=await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>new HistoricalPublicationOutcomeSender(journal,g,()=>{},Request).RunAsync(default));
        Assert.AreEqual("history_publication_abort_ack_invalid",error.Code);
        using var c=f.Factory.Open();Assert.IsNull(c.ExecuteScalar<string>("SELECT server_resolution FROM historical_commit_witness"));
        Assert.AreEqual("aborted",c.ExecuteScalar<string>("SELECT state FROM historical_preparation_outcome"));
    }

    [TestMethod]
    public async Task OutcomeSender_ChangedGenerationBeforeAckPreservesTheUnresolvedTombstone()
    {
        using var f=new Fixture();var g=PublicationGeneration();await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(g,1);
        using var draft=await PreparationDraft(f);var journal=new HistoricalPublicationJournal(f.Factory);await journal.SaveIntentAsync(draft,PreparationServerDataset,1,()=>{},default);
        async Task<HistoricalCloudResult> Request(string op,HistoricalCloudPayload p,CancellationToken ct)
        {
            await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(PublicationGeneration(),2);
            var sha=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(p.WitnessJson)));
            return new(){State="aborted",OperationId=p.OperationId,ContentDigest=p.ContentDigest,WitnessSha256=sha};
        }
        await Assert.ThrowsAsync<OnlineSyncGenerationChangedException>(()=>new HistoricalPublicationOutcomeSender(journal,g,()=>{},Request).RunAsync(default));
        using var c=f.Factory.Open();Assert.IsNull(c.ExecuteScalar<string>("SELECT server_resolution FROM historical_commit_witness"));
    }

    [TestMethod]
    public async Task OutcomeSender_LatePositiveUsesOriginalWitnessAndNeverRequestsAnotherGrant()
    {
        using var f=new Fixture();var g=PublicationGeneration();await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(g,1);
        var applied=await CreatePublication(f,g,"late-positive");var journal=new HistoricalPublicationJournal(f.Factory);
        var original=await journal.ReadOutcomeWitnessAsync(applied.Plan.OperationId,g.ShopId,g.ShopDeviceId,()=>{},default);var calls=new List<string>();
        Task<HistoricalCloudResult> Request(string op,HistoricalCloudPayload p,CancellationToken ct)
        {
            calls.Add(op);if(op=="local_receipt")return Task.FromResult(new HistoricalCloudResult{State="reconcile_required"});
            Assert.AreEqual("local_reconcile",op);Assert.AreEqual(original.Json,p.WitnessJson);Assert.AreEqual(original.ProofJson,p.LocalProofJson);
            using var c=f.Factory.Open();using var t=c.BeginTransaction();
            return Task.FromResult(new HistoricalCloudResult{State="committed",PublicationReceipt=ServerReceipt(f,applied)});
        }
        await new HistoricalPublicationSender(new HistoricalPublicationOutbox(f.Factory),g,()=>{},Request,()=>1000).RunAsync(default);
        CollectionAssert.AreEqual(new[]{"local_receipt","local_reconcile"},calls);
        using var db=f.Factory.Open();Assert.AreEqual("synchronized",db.ExecuteScalar<string>("SELECT status FROM historical_publication_delivery"));
        Assert.AreEqual("committed",db.ExecuteScalar<string>("SELECT server_resolution FROM historical_commit_witness"));
    }

    [TestMethod]
    public async Task OutcomeSender_PriorUnacknowledgedPositiveDelaysAbsenceUntilItsSequenceIsConfirmed()
    {
        using var f=new Fixture();var g=PublicationGeneration();await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(g,1);
        var applied=await CreatePublication(f,g,"ordered-positive");using var draft=await PreparationDraft(f);
        var journal=new HistoricalPublicationJournal(f.Factory);await journal.SaveIntentAsync(draft,PreparationServerDataset,1,()=>{},default);
        Assert.IsNull(await journal.ReadPendingNegativeOutcomeAsync(g,()=>{},default));
        using(var c=f.Factory.Open())Assert.AreEqual("prepared",c.ExecuteScalar<string>("SELECT state FROM historical_preparation_outcome WHERE operation_id=@op",new{op=draft.OperationId}));
        var outbox=new HistoricalPublicationOutbox(f.Factory);var attempt=await outbox.ClaimAsync(g,()=>{},1000,default);
        await outbox.ConfirmAsync(attempt,ServerReceipt(f,applied),g,()=>{},1001,default);
        var witness=await journal.ReadPendingNegativeOutcomeAsync(g,()=>{},default);
        Assert.IsNotNull(witness);using var json=System.Text.Json.JsonDocument.Parse(witness.Json);
        Assert.AreEqual(attempt.Sequence.ToString(),json.RootElement.GetProperty("local_sequence").GetString());
    }

    [TestMethod]
    public async Task OutcomeSender_OldOfficialLineageCannotInferAbsenceOrSendAnUnknownIntent()
    {
        using var f=new Fixture();var g=PublicationGeneration();await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(g,1);
        using var draft=await PreparationDraft(f);var journal=new HistoricalPublicationJournal(f.Factory);await journal.SaveIntentAsync(draft,PreparationServerDataset,1,()=>{},default);
        using(var c=f.Factory.Open())c.Execute("UPDATE app_settings SET value=@lineage WHERE key='pos.history.restore_lineage_id'",new{lineage=Guid.NewGuid().ToString("D")});
        var sender=new HistoricalPublicationOutcomeSender(journal,g,()=>{},(_,_,_)=>throw new AssertFailedException("Old lineage must not publish absence."));
        Assert.IsFalse(await sender.RunAsync(default));
        using var db=f.Factory.Open();Assert.AreEqual(0L,db.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_commit_witness"));
        Assert.AreEqual("prepared",db.ExecuteScalar<string>("SELECT state FROM historical_preparation_outcome"));
    }
}
