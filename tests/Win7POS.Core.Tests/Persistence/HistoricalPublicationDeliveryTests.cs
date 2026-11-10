using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Import;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Persistence;

public sealed partial class HistoricalLedgerTests
{
    const string PublicationShop="a0000000-0000-4000-8000-000000000019",PublicationDevice="b0000000-0000-4000-8000-000000000019";
    static OnlineSyncGeneration PublicationGeneration()=>new(generationId:Guid.NewGuid().ToString("N"),
        posSessionId:Guid.NewGuid().ToString("D"),shopDeviceId:PublicationDevice,shopId:PublicationShop,shopCode:"fixture");

    [TestMethod]
    public async Task PublicationSummary_DistinguishesLocalPendingAttentionConfirmationAndOriginalRegistration()
    {
        using var f=new Fixture();var g=PublicationGeneration();await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(g,1);
        var first=await CreatePublication(f,g,"summary-first");var second=await CreatePublication(f,g,"summary-second");
        var outbox=new HistoricalPublicationOutbox(f.Factory);var initial=await outbox.ReadSummaryAsync(PublicationShop,default);
        Assert.AreEqual(2L,initial.LocalOperations);Assert.AreEqual(2L,initial.Pending);Assert.AreEqual(0L,initial.Confirmed);Assert.IsNull(initial.LastConfirmedRevision);
        var claim=await outbox.ClaimAsync(g,()=>{},1000,default);var receipt=ServerReceipt(f,first);receipt.Revision=long.MaxValue.ToString(CultureInfo.InvariantCulture);
        await outbox.ConfirmAsync(claim,receipt,g,()=>{},1001,default);
        var other=await outbox.ClaimAsync(g,()=>{},1002,default);await outbox.FailAsync(other,g,()=>{},false,"content_conflict",1003,default);
        // A pre-publication-schema original operation has no transport intent.
        // Seed its immutable receipt/batch fixture without replaying any sale.
        using(var c=f.Factory.Open())using(var t=c.BeginTransaction())
        {
            var dataset=c.ExecuteScalar<string>("SELECT dataset_id FROM historical_import_dataset WHERE singleton_id=1",transaction:t);
            var op=Guid.NewGuid().ToString("N");var batch=Guid.NewGuid().ToString("N");
            c.Execute(@"INSERT INTO historical_import_operation VALUES(@op,'Apply',@shop,@dataset,@digest,@batch,1,1,'{}',0,0,0);
INSERT INTO historical_import_batch(id,shop_id,dataset_id,apply_operation_id,state,revision,created_at) VALUES(@batch,@shop,@dataset,@op,'Active',1,1);",new{op,batch,shop=PublicationShop,dataset,digest=new string('a',64)},t);
            t.Commit();
        }
        var summary=await outbox.ReadSummaryAsync(PublicationShop,default);
        Assert.AreEqual(3L,summary.LocalOperations);Assert.AreEqual(0L,summary.Pending);Assert.AreEqual(1L,summary.Attention);
        Assert.AreEqual(1L,summary.Unregistered);Assert.AreEqual(1L,summary.Confirmed);Assert.AreEqual(receipt.Revision,summary.LastConfirmedRevision);
        Assert.AreEqual(0L,(await outbox.ReadSummaryAsync(Guid.NewGuid().ToString("D"),default)).LocalOperations);
    }

    [TestMethod]
    public async Task PublicationDeliveryNotification_AfterCommitDoesNotInvalidateEconomicReports()
    {
        using var f=new Fixture();var g=PublicationGeneration();await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(g,1);
        var local=await CreatePublication(f,g,"delivery-event");var outbox=new HistoricalPublicationOutbox(f.Factory);
        var coordinator=Win7POS.Data.Reports.SalesReportSnapshotCoordinator.ForDatabase(f.Factory.DbPath);
        var events=0;var reports=0;var writerAvailable=true;var states=new List<string>();
        EventHandler broken=(_,_)=>throw new InvalidOperationException("synthetic subscriber");
        EventHandler reader=(_,_)=>
        {
            events++;
            using var c=f.Factory.Open();c.DefaultTimeout=0;
            try{using var transaction=c.BeginTransaction();states.Add(c.ExecuteScalar<string>("SELECT status FROM historical_publication_delivery WHERE operation_id=@op",new{op=local.Plan.OperationId},transaction)??"missing");}
            catch(Microsoft.Data.Sqlite.SqliteException){writerAvailable=false;}
        };
        EventHandler report=(_,_)=>reports++;
        coordinator.HistoricalSyncStatusChanged+=broken;coordinator.HistoricalSyncStatusChanged+=reader;coordinator.SnapshotChanged+=report;
        try
        {
            var claim=await outbox.ClaimAsync(g,()=>{},1000,default);
            await outbox.ConfirmAsync(claim,ServerReceipt(f,local),g,()=>{},1001,default);
            Assert.IsNull(await outbox.ClaimAsync(g,()=>{},1002,default));
            Assert.AreEqual(2,events);Assert.AreEqual(0,reports);Assert.IsTrue(writerAvailable);
            CollectionAssert.AreEqual(new[]{"uncertain","synchronized"},states);
        }
        finally{coordinator.HistoricalSyncStatusChanged-=broken;coordinator.HistoricalSyncStatusChanged-=reader;coordinator.SnapshotChanged-=report;}
    }

    [TestMethod]
    public async Task PublicationLostAck_RestartRecoversOriginalReceiptWithoutAnotherDeliveryOrGrant()
    {
        using var f=new Fixture();var g=PublicationGeneration();await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(g,1);
        var local=await CreatePublication(f,g,"ack-lost");var receipt=ServerReceipt(f,local);long now=1000;var acquired=false;var deliveries=0;var lookups=0;
        File.Delete(f.Source);
        Task<HistoricalCloudResult> Request(string op,HistoricalCloudPayload payload,CancellationToken ct)
        {
            Assert.AreEqual(Guid.Parse(local.Plan.OperationId).ToString("D"),payload.OperationId);
            Assert.AreEqual(local.Proof.ContentDigest,payload.ContentDigest);
            if(op=="local_receipt"){lookups++;return Task.FromResult(new HistoricalCloudResult{State=acquired?"committed":"authorized",PublicationReceipt=acquired?receipt:null});}
            Assert.AreEqual("local_deliver",op);deliveries++;acquired=true;
            throw new HistoricalCloudRequestException("synthetic_ack_lost",false,true);
        }
        var first=new HistoricalPublicationSender(new HistoricalPublicationOutbox(f.Factory),g,()=>{},Request,()=>now);
        await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>first.RunAsync(default));
        Assert.AreEqual("uncertain",(await new HistoricalPublicationOutbox(f.Factory).ReadStatusAsync(local.Plan.OperationId,PublicationShop,default)).Status);
        f.Restart();now+=60001;
        var resumed=new HistoricalPublicationSender(new HistoricalPublicationOutbox(f.Factory),g,()=>{},Request,()=>now);
        await resumed.RunAsync(default);
        var confirmed=await new HistoricalPublicationOutbox(f.Factory).ReadStatusAsync(local.Plan.OperationId,PublicationShop,default);
        Assert.AreEqual("synchronized",confirmed.Status);Assert.AreEqual("7",confirmed.ServerRevision);
        Assert.AreEqual(1,deliveries);Assert.AreEqual(2,lookups);
        using var c=f.Factory.Open();
        Assert.AreEqual(1L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation"));
        Assert.AreEqual(1L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_transactions"));
        Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM sales"));
        Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM sales_sync_outbox"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>c.Execute("UPDATE historical_publication_delivery SET confirmed_at=0"));
    }

    [TestMethod]
    public async Task PublicationAttemptCasAndGenerationFence_RejectOldResponses()
    {
        using var f=new Fixture();var g=PublicationGeneration();await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(g,1);
        var local=await CreatePublication(f,g,"cas");var outbox=new HistoricalPublicationOutbox(f.Factory);
        var first=await outbox.ClaimAsync(g,()=>{},1000,default);Assert.IsNotNull(first);
        Assert.IsNull(await outbox.ClaimAsync(g,()=>{},1001,default));
        var second=await outbox.ClaimAsync(g,()=>{},46001,default);Assert.IsNotNull(second);Assert.AreEqual(first.Attempt+1,second.Attempt);
        Assert.IsFalse(await outbox.ConfirmAsync(first,ServerReceipt(f,local),g,()=>{},46002,default));
        using(var c=f.Factory.Open())c.Execute("UPDATE pos_sync_session_generation SET active=0 WHERE singleton_id=1");
        await Assert.ThrowsAsync<OnlineSyncGenerationChangedException>(()=>outbox.ConfirmAsync(second,ServerReceipt(f,local),g,()=>{},46003,default));
        Assert.AreEqual("uncertain",(await outbox.ReadStatusAsync(local.Plan.OperationId,PublicationShop,default)).Status);
    }

    [TestMethod]
    public async Task PublicationTransientFailure_DoesNotStarveTheNextIndependentOperation()
    {
        using var f=new Fixture();var g=PublicationGeneration();await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(g,1);
        var delayed=await CreatePublication(f,g,"delayed");var independent=await CreatePublication(f,g,"independent");
        var delivered=0;
        Task<HistoricalCloudResult> Request(string op,HistoricalCloudPayload payload,CancellationToken ct)
        {
            if(payload.OperationId==delayed.Proof.OperationId)throw new HistoricalCloudRequestException("synthetic_503",false,true);
            Assert.AreEqual("local_receipt",op);delivered++;
            return Task.FromResult(new HistoricalCloudResult{State="committed",PublicationReceipt=ServerReceipt(f,independent)});
        }
        var outbox=new HistoricalPublicationOutbox(f.Factory);
        var sender=new HistoricalPublicationSender(outbox,g,()=>{},Request,()=>1000);
        await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>sender.RunAsync(default));
        Assert.AreEqual(1,delivered);
        Assert.AreEqual("uncertain",(await outbox.ReadStatusAsync(delayed.Plan.OperationId,PublicationShop,default)).Status);
        Assert.AreEqual("synchronized",(await outbox.ReadStatusAsync(independent.Plan.OperationId,PublicationShop,default)).Status);
    }

    [TestMethod]
    public void PublicationProofWire_UsesExactInt64StringsAndRevalidatesAfterDeserialization()
    {
        var proof=new HistoricalPublicationProof(Guid.NewGuid().ToString("D"),Guid.NewGuid().ToString("D"),PublicationShop,PublicationDevice,
            "synthetic-dataset",PublicationShop,PublicationDevice,long.MaxValue,long.MaxValue,new string('a',64),new string('b',64),"content_authorized_v2");
        using var stream=new MemoryStream();
        var serializer=new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(HistoricalPublicationProof));
        serializer.WriteObject(stream,proof);var json=Encoding.UTF8.GetString(stream.ToArray());
        StringAssert.Contains(json,"\"restore_epoch\":\"9223372036854775807\"");
        StringAssert.Contains(json,"\"writer_epoch\":\"9223372036854775807\"");
        stream.Position=0;var read=(HistoricalPublicationProof)serializer.ReadObject(stream)!;read.DemandValid();
        Assert.AreEqual(proof.RestoreEpoch,read.RestoreEpoch);Assert.AreEqual(proof.WriterEpoch,read.WriterEpoch);
    }

    [TestMethod]
    public async Task PublicationFreezeAdmission_RejectsPendingUncertainAndAttentionUntilOriginalReceiptIsConfirmed()
    {
        using var f=new Fixture();var g=PublicationGeneration();await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(g,1);
        var local=await CreatePublication(f,g,"freeze");var state=await f.Service.ReadDatasetAsync(default);
        var guard=new HistoricalCloudExportGuard(PublicationShop,PublicationDevice,state.DatasetId,state.DataRevision,f.Epoch,()=>{});
        var prepared=new HistoricalPreparedExportStore(f.Factory);
        var failure=await Assert.ThrowsAsync<InvalidOperationException>(()=>prepared.DemandHistoricalPublicationsDrainedAsync(guard,default));
        Assert.AreEqual("history_cloud_history_sync_required",failure.Message);
        var outbox=new HistoricalPublicationOutbox(f.Factory);var claim=await outbox.ClaimAsync(g,()=>{},1000,default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>prepared.DemandHistoricalPublicationsDrainedAsync(guard,default));
        await outbox.ConfirmAsync(claim,ServerReceipt(f,local),g,()=>{},1001,default);
        await prepared.DemandHistoricalPublicationsDrainedAsync(guard,default);
        var other=await CreatePublication(f,g,"attention");var second=await outbox.ClaimAsync(g,()=>{},1002,default);
        await outbox.FailAsync(second,g,()=>{},false,"conflict",1003,default);
        var current=await f.Service.ReadDatasetAsync(default);
        var newGuard=new HistoricalCloudExportGuard(PublicationShop,PublicationDevice,current.DatasetId,current.DataRevision,f.Epoch,()=>{});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>prepared.DemandHistoricalPublicationsDrainedAsync(newGuard,default));
        Assert.AreEqual("needs_attention",(await outbox.ReadStatusAsync(other.Plan.OperationId,PublicationShop,default)).Status);
    }

    [TestMethod]
    public async Task PublicationDraft_CorrectionCarriesPriorProposedContextAndEvidenceInTheAuthorizedDigest()
    {
        using var f=new Fixture();var first=await f.Plan(amount:1000);
        var applied=await f.Service.ApplyAsync(first,f.Guard(first),f.Backup(),default);
        await f.Service.UndoAsync(applied.BatchId,1,f.UndoGuard(applied.BatchId,1),f.Backup(),default);
        var proposed=await f.Plan(amount:9000);var preview=await f.Service.PreviewAsync(proposed,default);
        var approval=preview.Corrections.Single().Approve("synthetic-reviewed-correction");
        var plan=WithApprovals(proposed,new[]{approval});
        var lineage=(await f.Service.ReadDatasetAsync(default)).RestoreLineageId;
        Assert.Throws<InvalidOperationException>(()=>HistoricalPublicationDraft.ForApply(plan,PublicationDevice,lineage));
        using var draft=await f.Service.PreparePublicationDraftAsync(plan,PublicationDevice,default);
        var seen=false;
        foreach(var page in draft.Pages(default))
        {
            using var parsed=System.Text.Json.JsonDocument.Parse(page.Json);
            foreach(var record in parsed.RootElement.EnumerateArray())
            {
                if(record.GetProperty("kind").GetString()!="correction")continue;
                var fields=record.GetProperty("fields");seen=true;
                Assert.AreEqual(approval.PreviousTransactionId.ToString(CultureInfo.InvariantCulture),fields.GetProperty("previous_transaction_id").GetString());
                Assert.AreEqual(applied.BatchId,fields.GetProperty("previous_local_batch_id").GetString());
                Assert.AreEqual(Guid.Parse(applied.OperationId).ToString("D"),fields.GetProperty("previous_apply_operation_id").GetString());
                Assert.AreEqual(approval.Previous.Fingerprint,fields.GetProperty("previous_economic_digest").GetString());
                Assert.AreEqual(approval.Proposed.Fingerprint,fields.GetProperty("proposed_economic_digest").GetString());
                Assert.AreEqual(approval.ContextFingerprint,fields.GetProperty("context_fingerprint").GetString());
                Assert.AreEqual(approval.EvidenceReference,fields.GetProperty("evidence_reference").GetString());
                Assert.AreEqual(approval.Fingerprint,fields.GetProperty("approval_fingerprint").GetString());
            }
        }
        Assert.IsTrue(seen);
    }

    [TestMethod]
    public async Task PublicationDraft_OwnerSnapshotRejectsStaleRevisionAndCancellationWithoutCreatingAnIntent()
    {
        using var f=new Fixture();var plan=await f.Plan();
        long CountIntents(){using var c=f.Factory.Open();return c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_publication_outbox");}
        var before=CountIntents();
        using(var cancelled=new CancellationTokenSource())
        {
            cancelled.Cancel();await Assert.ThrowsAsync<OperationCanceledException>(()=>f.Service.PreparePublicationDraftAsync(plan,PublicationDevice,cancelled.Token));
        }
        await f.Service.ApplyAsync(plan,f.Guard(plan),f.Backup(),default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.PreparePublicationDraftAsync(plan,PublicationDevice,default));
        Assert.AreEqual(before+1,CountIntents());
    }

    [TestMethod]
    public async Task PublicationUndo_WaitsOnlyForItsOwnApplyWhileIndependentBatchCanProceed()
    {
        using var f=new Fixture();var g=PublicationGeneration();await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(g,1);
        var applied=await CreatePublication(f,g,"dependency");var state=await f.Service.ReadDatasetAsync(default);var undo=Guid.NewGuid().ToString("N");
        var fp=HistoricalSalesImportPlan.UndoFingerprint(undo,PublicationShop,state.DatasetId,applied.Local.BatchId,1,f.Epoch);
        using var draft=HistoricalPublicationDraft.ForUndo(undo,PublicationShop,PublicationDevice,state.DatasetId,f.Epoch,state.DataRevision,applied.Local.BatchId,1,applied.Plan.OperationId,state.RestoreLineageId);
        var proof=new HistoricalPublicationProof(Guid.NewGuid().ToString("D"),Guid.Parse(undo).ToString("D"),PublicationShop,PublicationDevice,state.DatasetId,applied.Proof.ServerDatasetId,draft.RestoreLineageId,f.Epoch,1,draft.ContentDigest,fp,"content_authorized_v2");
        var guard=PublicationGuard(HistoricalImportOperationKind.Undo,undo,fp,state.DatasetId,f.Epoch);
        await f.Service.UndoAsync(applied.Local.BatchId,1,guard,f.Backup(),default,publicationDraft:draft,publicationProof:proof);
        var independent=await CreatePublication(f,g,"independent");var outbox=new HistoricalPublicationOutbox(f.Factory);
        var first=await outbox.ClaimAsync(g,()=>{},1000,default);Assert.AreEqual(applied.Plan.OperationId,first.OperationId);
        await outbox.FailAsync(first,g,()=>{},false,"content_conflict",1001,default);
        var other=await outbox.ClaimAsync(g,()=>{},1002,default);Assert.AreEqual(independent.Plan.OperationId,other.OperationId);
        await outbox.ConfirmAsync(other,ServerReceipt(f,independent),g,()=>{},1003,default);
        Assert.IsNull(await outbox.ClaimAsync(g,()=>{},1004,default));
        Assert.AreEqual("pending",(await outbox.ReadStatusAsync(undo,PublicationShop,default)).Status);
    }

    [TestMethod]
    public async Task PublicationRestoreLineage_RequiresReconciliationAndDoesNotStopFreshIndependentContent()
    {
        using var f=new Fixture();var g=PublicationGeneration();await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(g,1);
        var old=await CreatePublication(f,g,"old-lineage");
        using(var c=f.Factory.Open())c.Execute("UPDATE app_settings SET value=@value WHERE key='pos.history.restore_lineage_id'",new{value=Guid.NewGuid().ToString("N")});
        var current=await CreatePublication(f,g,"current-lineage");var outbox=new HistoricalPublicationOutbox(f.Factory);var deliveries=0;
        Task<HistoricalCloudResult> Request(string op,HistoricalCloudPayload p,CancellationToken ct)
        {
            if(op=="local_deliver")deliveries++;
            return Task.FromResult(p.OperationId==old.Proof.OperationId?new HistoricalCloudResult{State="authorized"}:
                new HistoricalCloudResult{State="committed",PublicationReceipt=ServerReceipt(f,current)});
        }
        await new HistoricalPublicationSender(outbox,g,()=>{},Request,()=>1000).RunAsync(default);
        Assert.AreEqual(0,deliveries,"An old-lineage operation was first-delivered after restore.");
        var status=await outbox.ReadStatusAsync(old.Plan.OperationId,PublicationShop,default);
        Assert.AreEqual("needs_attention",status.Status);Assert.AreEqual("history_publication_restore_reconciliation",status.ReasonCode);
        Assert.AreEqual("synchronized",(await outbox.ReadStatusAsync(current.Plan.OperationId,PublicationShop,default)).Status);
    }

    [TestMethod]
    public async Task PublicationRestoreLineage_CanRecoverAnAlreadyAcquiredReceiptWithoutFirstDelivery()
    {
        using var f=new Fixture();var g=PublicationGeneration();await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(g,1);
        var local=await CreatePublication(f,g,"already-acquired");var receipt=ServerReceipt(f,local);
        using(var c=f.Factory.Open())c.Execute("UPDATE app_settings SET value=@value WHERE key='pos.history.restore_lineage_id'",new{value=Guid.NewGuid().ToString("N")});
        var lookups=0;
        Task<HistoricalCloudResult> Request(string op,HistoricalCloudPayload p,CancellationToken ct)
        {Assert.AreEqual("local_receipt",op);lookups++;return Task.FromResult(new HistoricalCloudResult{State="committed",PublicationReceipt=receipt});}
        var outbox=new HistoricalPublicationOutbox(f.Factory);
        await new HistoricalPublicationSender(outbox,g,()=>{},Request,()=>1000).RunAsync(default);
        Assert.AreEqual(1,lookups);Assert.AreEqual("synchronized",(await outbox.ReadStatusAsync(local.Plan.OperationId,PublicationShop,default)).Status);
    }

    [TestMethod]
    [DataRow("shop")][DataRow("device")][DataRow("dataset")][DataRow("grant")][DataRow("digest")][DataRow("receipt")]
    [DataRow("lineage")][DataRow("revision")][DataRow("batch")]
    public async Task PublicationReceiptScopeOrContentMismatch_IsExplicitAttention(string mismatch)
    {
        using var f=new Fixture();var g=PublicationGeneration();await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(g,1);
        var local=await CreatePublication(f,g,"mismatch");var receipt=ServerReceipt(f,local);
        switch(mismatch)
        {
            case "shop":receipt.ShopId=Guid.NewGuid().ToString("D");break;
            case "device":receipt.DeviceId=Guid.NewGuid().ToString("D");break;
            case "dataset":receipt.DatasetId=Guid.NewGuid().ToString("D");break;
            case "grant":receipt.GrantId=Guid.NewGuid().ToString("D");break;
            case "digest":receipt.ContentDigest=new string('f',64);break;
            case "lineage":receipt.RestoreLineageId=Guid.NewGuid().ToString("D");break;
            case "revision":receipt.Revision="01";break;
            case "batch":receipt.BatchId="invalid";break;
            default:receipt.LocalReceiptSha256=new string('f',64);break;
        }
        var sender=new HistoricalPublicationSender(new HistoricalPublicationOutbox(f.Factory),g,()=>{},(_,_,_)=>Task.FromResult(new HistoricalCloudResult{State="committed",PublicationReceipt=receipt}),()=>1000);
        await sender.RunAsync(default);
        var state=await new HistoricalPublicationOutbox(f.Factory).ReadStatusAsync(local.Plan.OperationId,PublicationShop,default);
        Assert.AreEqual("needs_attention",state.Status);Assert.AreEqual("history_publication_receipt_mismatch",state.ReasonCode);
    }

    static HistoricalImportAuthorizationCommitGuard PublicationGuard(HistoricalImportOperationKind kind,string op,string fp,string dataset,long epoch)
        =>new(kind,op,fp,PublicationShop,"SYNTHETIC",PublicationDevice,1,dataset,epoch,1,"generation","staff",1,()=>{},(_,commit)=>commit());
    static async Task<(HistoricalSalesImportPlan Plan,HistoricalSalesOperationReceipt Local,HistoricalPublicationProof Proof)> CreatePublication(Fixture f,OnlineSyncGeneration generation,string identity)
    {
        var seed=await f.Plan(id:identity);
        var plan=new HistoricalSalesImportPlan(seed.OperationId,generation.ShopId,seed.DatasetId,seed.DataRevision,seed.RestoreEpoch,seed.Analysis,seed.Assignments,seed.NativeStartBusinessDateTime,seed.OverlapEvidenceReference);
        using var draft=HistoricalPublicationDraft.ForApply(plan,generation.ShopDeviceId,(await f.Service.ReadDatasetAsync(default)).RestoreLineageId);
        var proof=new HistoricalPublicationProof(Guid.NewGuid().ToString("D"),Guid.Parse(plan.OperationId).ToString("D"),PublicationShop,PublicationDevice,plan.DatasetId,"c0000000-0000-4000-8000-000000000019",draft.RestoreLineageId,plan.RestoreEpoch,1,draft.ContentDigest,plan.RequestFingerprint,"content_authorized_v2");
        var receipt=await f.Service.ApplyAsync(plan,PublicationGuard(HistoricalImportOperationKind.Apply,plan.OperationId,plan.RequestFingerprint,plan.DatasetId,plan.RestoreEpoch),f.Backup(),default,publicationDraft:draft,publicationProof:proof);
        return(plan,receipt,proof);
    }
    static HistoricalPublicationServerReceipt ServerReceipt(Fixture f,(HistoricalSalesImportPlan Plan,HistoricalSalesOperationReceipt Local,HistoricalPublicationProof Proof) source)
    {
        using var c=f.Factory.Open();var json=c.ExecuteScalar<string>("SELECT receipt_json FROM historical_publication_outbox WHERE operation_id=@op",new{op=source.Plan.OperationId})??throw new InvalidOperationException("synthetic_receipt_missing");
        return new(){OperationId=source.Proof.OperationId,GrantId=source.Proof.GrantId,ShopId=source.Proof.ShopId,DeviceId=source.Proof.DeviceId,
            DatasetId=source.Proof.ServerDatasetId,LocalDatasetId=source.Proof.DatasetId,RestoreEpoch=source.Proof.RestoreEpoch.ToString(CultureInfo.InvariantCulture),
            RestoreLineageId=source.Proof.RestoreLineageId,
            WriterEpoch=source.Proof.WriterEpoch.ToString(CultureInfo.InvariantCulture),Revision="7",ContentDigest=source.Proof.ContentDigest,
            LocalReceiptSha256=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))),BatchId=Guid.Parse(source.Local.BatchId).ToString("D"),
            Kind="Apply",Inserted=source.Local.InsertedCount,Duplicates=source.Local.DuplicateCount,Deactivated=0,SignedGrossClp=source.Local.SignedAmountClp.ToString(CultureInfo.InvariantCulture)};
    }
}
