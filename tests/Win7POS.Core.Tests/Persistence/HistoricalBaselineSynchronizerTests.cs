using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Import;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Persistence;

public sealed partial class HistoricalBaselineStoreTests
{
    // Controlled response transport only. No claim of HTTP, actual server
    // worker, SQL migration, browser or distributed WPF qualification.
    sealed class BaselineTransport
    {
        internal readonly HistoricalCloudExportTests.Fixture F;
        internal OnlineSyncGeneration G;
        internal bool LoseUpload,LoseApply,LoseRecovery,StaleApply,Verified,ReviewAllowed=true,UploadVerified=true,ChangeUpload,RevokeOnUpload,AlreadyUploaded;
        internal int PrepareCalls,UploadCalls,AdmissionCreates,ApplyCalls,StartCalls;
        internal readonly List<(string Operation,HistoricalCloudPayload Payload)> Requests=new();
        internal readonly string Upload=Guid.NewGuid().ToString("D"),Run=Guid.NewGuid().ToString("D");
        internal string? Ack;
        internal string ContextEpoch="1",ContextAuthority="local",ContextSnapshot="80000000-0000-4000-8000-000000003019";
        internal string RunState="review_ready";
        internal bool RetryPending,RetryDenied;
        internal readonly Dictionary<string,string> RecoveryAcks=new();internal int RecoveryCalls;
        internal BaselineTransport(HistoricalCloudExportTests.Fixture f,OnlineSyncGeneration g){F=f;G=g;}
        internal HistoricalBaselineSynchronizer Client()=>new(F.Db.Factory,G,()=>{},Request,async(_,state,ct)=>{
            PrepareCalls++;var guard=await F.Guard();var export=await new HistoricalCloudExporter(F.Db.Factory).ExportBaselineAsync(F.Archive,F.Backup,guard,Guid.Parse(state.RestoreLineageId).ToString("D"),ct);
            return new(export,guard);
        },(_,_,_)=>{UploadCalls++;return Task.CompletedTask;});
        internal async Task<HistoricalCloudResult> Request(string operation,HistoricalCloudPayload p,CancellationToken ct)
        {
            Requests.Add((operation,p));var flow=await Store(F,G).ReadAsync(Server,1,ct);
            if(operation=="baseline_context")return new(){Baseline=new(){State=Verified?"verified":"missing",DatasetId=Server,WriterEpoch=ContextEpoch,WriterAuthority=ContextAuthority,CanPrepare=ContextAuthority=="local",
                SourceSnapshotId=Verified?ContextSnapshot:null,BaselineRevision=Verified?flow?.LocalRevision.ToString():null,CanonicalRevision=Verified?"1":null}};
            if(operation=="baseline_upload_create")
            {
                if(AdmissionCreates==0)AdmissionCreates++;
                Assert.AreEqual(flow!.FlowId,p.RequestKey);
                if(LoseUpload){LoseUpload=false;throw new HistoricalCloudRequestException("history_timeout",false,true);}
                if(RevokeOnUpload)await new OnlineSyncGenerationRepository(F.Db.Factory).ActivateAndRecoverAsync(Generation(),2);
                var id=ChangeUpload?Guid.NewGuid().ToString("D"):Upload;
                return new(){UploadId=id,ObjectKey="history/"+flow.ShopId+"/"+id,UploadRequired=!AlreadyUploaded,SignedUrl=AlreadyUploaded?null:"synthetic-not-a-real-url"};
            }
            if(operation=="baseline_upload_ready"||operation=="baseline_upload_status")return new(){UploadId=Upload,State=UploadVerified?"verified":"uploaded",
                FileName=Path.GetFileName(flow!.ArchivePath),Sha256=flow.ArchiveSha256,ByteLength=new FileInfo(flow.ArchivePath).Length.ToString()};
            if(operation=="baseline_start"){StartCalls++;Assert.AreEqual(flow!.StartRequestId,p.RequestKey);return new(){RunId=Run,State="queued"};}
            if(operation=="baseline_status")return new(){RunId=Run,State=RunState};
            if(operation=="baseline_review")
            {Assert.AreEqual(G.PosSessionId,flow!.RunSessionId);return new(){RunId=Run,CanApply=ReviewAllowed,ReviewDigest=new string(RecoveryAcks.Count==0?'a':'b',64)};}
            if(operation=="baseline_receipt"){Assert.AreEqual(flow!.ApplyRequestId,p.RequestKey);return new(){BaselineReceiptJson=Ack};}
            if(operation=="baseline_recover")
            {
                RecoveryCalls++;var pending=(p.RetryFailed==true?await Store(F,G).ContinuePendingFailedRetryAsync(flow!.FlowId,ct):await Store(F,G).ReadPendingRecoveryAsync(flow!.FlowId,ct))!;
                Assert.AreEqual(pending.RequestId,p.RecoveryRequestKey);Assert.AreEqual(flow.ApplyRequestId,p.ApplyRequestKey);Assert.AreEqual(pending.PreviousReviewDigest,p.PreviousReviewDigest);
                if(p.RetryFailed==true){Assert.AreEqual(pending.PreviousRecoveryRequestId,p.PreviousRecoveryRequestKey);if(RetryPending)throw new HistoricalCloudRequestException("history_baseline_retry_pending",false,true);if(RetryDenied)throw new HistoricalCloudRequestException("history_baseline_retry_conflict",false,false);RunState="review_ready";}
                if(Verified)return new(){BaselineRecoveryAckJson=RecoveryAck(flow,pending,G,true),BaselineReceiptJson=Ack};
                if(!RecoveryAcks.TryGetValue(pending.RequestId,out var raw)){raw=RecoveryAck(flow,pending,G);RecoveryAcks.Add(pending.RequestId,raw);}
                if(LoseRecovery){LoseRecovery=false;throw new HistoricalCloudRequestException("history_timeout",false,true);}
                return new(){BaselineRecoveryAckJson=raw};
            }
            if(operation=="baseline_apply")
            {
                Assert.AreEqual(G.PosSessionId,flow!.RunSessionId);Assert.AreEqual(flow.ApplyRequestId,p.RequestKey);Assert.AreEqual(flow.ReviewDigest,p.ReviewDigest);
                if(StaleApply){StaleApply=false;throw new HistoricalCloudRequestException("history_baseline_review_stale",false,false);}
                ApplyCalls++;Verified=true;Ack=Receipt(flow);
                if(LoseApply){LoseApply=false;throw new HistoricalCloudRequestException("history_timeout",false,true);}
                return new(){BaselineReceiptJson=Ack};
            }
            throw new AssertFailedException("Unexpected synthetic operation.");
        }
    }
    [TestMethod]
    public async Task BaselineSynchronizer_AppliedOriginalProofSurvivesWriterFreezeWithoutNewIntentOrReceipt()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(g,1);
        var t=new BaselineTransport(f,g);for(var i=0;i<6&&!t.Verified;i++)await t.Client().RunAsync(default);
        var original=(await Store(f,g).ReadAsync(Server,1,default))!;Assert.AreEqual("applied",original.State);
        t.ContextEpoch="2";t.ContextAuthority="frozen";Assert.IsFalse(await t.Client().RunAsync(default));
        t.ContextAuthority="web";Assert.IsFalse(await t.Client().RunAsync(default));
        var saved=(await Store(f,g).ReadAsync(Server,1,default))!;Assert.AreEqual(original.ReceiptJson,saved.ReceiptJson);Assert.AreEqual(original.FlowId,saved.FlowId);
        Assert.IsNull(await Store(f,g).ReadAsync(Server,2,default));Assert.AreEqual(1,t.PrepareCalls);Assert.AreEqual(1,t.ApplyCalls);Assert.AreEqual(1,t.UploadCalls);
    }
    [TestMethod]
    public async Task BaselineSynchronizer_LaterWriterEpochCannotAdoptUnknownSnapshotFromAnAppliedProof()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(g,1);
        var t=new BaselineTransport(f,g);for(var i=0;i<6&&!t.Verified;i++)await t.Client().RunAsync(default);
        t.ContextEpoch="2";t.ContextAuthority="frozen";t.ContextSnapshot=Guid.NewGuid().ToString("D");
        var error=await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>t.Client().RunAsync(default));Assert.AreEqual("history_baseline_intent_missing",error.Code);Assert.AreEqual(1,t.PrepareCalls);
        Assert.IsNull(await Store(f,g).ReadAsync(Server,2,default));
    }
    [TestMethod]
    public async Task BaselineSynchronizer_LostUploadAndApplyResponsesRecoverOriginalIntentAfterClientRestart()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var facts=f.ReadFacts();var g=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(g,1);
        var t=new BaselineTransport(f,g){LoseUpload=true,LoseApply=true};Assert.IsTrue(await t.Client().RunAsync(default));
        var original=(await Store(f,g).ReadAsync(Server,1,default))!;
        await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>t.Client().RunAsync(default));
        Assert.AreEqual("prepared",(await Store(f,g).ReadAsync(Server,1,default))!.State);
        for(var step=0;step<4;step++)Assert.IsTrue(await t.Client().RunAsync(default));
        await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>t.Client().RunAsync(default));
        Assert.AreEqual("run_started",(await Store(f,g).ReadAsync(Server,1,default))!.State);
        t.G=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(t.G,2);
        Assert.IsTrue(await t.Client().RunAsync(default));Assert.IsFalse(await t.Client().RunAsync(default));
        var applied=(await Store(f,t.G).ReadAsync(Server,1,default))!;Assert.AreEqual("applied",applied.State);Assert.AreEqual(t.Ack,applied.ReceiptJson);
        Assert.AreEqual(original.FlowId,applied.FlowId);Assert.AreEqual(original.ApplyRequestId,applied.ApplyRequestId);Assert.AreEqual(original.StartRequestId,applied.StartRequestId);
        Assert.AreEqual(1,t.AdmissionCreates);Assert.AreEqual(1,t.ApplyCalls);Assert.AreEqual(1,t.PrepareCalls);Assert.AreEqual(1,t.UploadCalls);Assert.AreEqual(1,t.StartCalls);
        Assert.AreEqual(facts,f.ReadFacts());
    }
    [TestMethod]
    public async Task BaselineSynchronizer_RevocationDuringResponseCannotCommitLocalUploadAcknowledgement()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();await Prepare(f,g);var t=new BaselineTransport(f,g){RevokeOnUpload=true};
        await Assert.ThrowsAsync<OnlineSyncGenerationChangedException>(()=>t.Client().RunAsync(default));
        using var c=f.Db.Factory.Open();Assert.AreEqual("prepared",c.ExecuteScalar<string>("SELECT state FROM historical_baseline_flow"));Assert.AreEqual(0,t.UploadCalls);
    }
    [TestMethod]
    public async Task BaselineSynchronizer_ChangedUploadIdentityCannotUploadOrAdvanceExistingFlow()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();await Prepare(f,g);var t=new BaselineTransport(f,g);
        await t.Client().RunAsync(default);t.ChangeUpload=true;await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>t.Client().RunAsync(default));
        Assert.AreEqual("upload_created",(await Store(f,g).ReadAsync(Server,1,default))!.State);Assert.AreEqual(0,t.UploadCalls);
    }
    [TestMethod]
    public async Task BaselineSynchronizer_UnverifiedWorkerSourceDoesNotStartAndDeniedReviewNeedsAttention()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();await Prepare(f,g);var t=new BaselineTransport(f,g){UploadVerified=false,ReviewAllowed=false};
        await t.Client().RunAsync(default);await t.Client().RunAsync(default);Assert.IsFalse(await t.Client().RunAsync(default));Assert.AreEqual(0,t.StartCalls);
        t.UploadVerified=true;Assert.IsTrue(await t.Client().RunAsync(default));Assert.IsFalse(await t.Client().RunAsync(default));
        Assert.AreEqual("needs_attention",(await Store(f,g).ReadAsync(Server,1,default))!.State);Assert.AreEqual(0,t.ApplyCalls);
        Assert.AreEqual("history_baseline_attention_required",(await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>t.Client().RunAsync(default))).Code);
    }
    [TestMethod]
    public async Task BaselineSynchronizer_UnknownServerBaselineCannotAdoptMissingLocalIntentOrExportAgain()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(g,1);
        var prepared=false;
        var client=new HistoricalBaselineSynchronizer(f.Db.Factory,g,()=>{},(_,_,_)=>Task.FromResult(new HistoricalCloudResult{Baseline=new(){State="verified",DatasetId=Server,WriterEpoch="1",WriterAuthority="local",CanPrepare=true,
            SourceSnapshotId=Guid.NewGuid().ToString("D"),BaselineRevision="0",CanonicalRevision="1"}}),(_,_,_)=>{prepared=true;throw new AssertFailedException();},(_,_,_)=>throw new AssertFailedException());
        var error=await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>client.RunAsync(default));Assert.AreEqual("history_baseline_intent_missing",error.Code);Assert.IsFalse(prepared);
        using var c=f.Db.Factory.Open();Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_baseline_flow"));
    }
    [TestMethod]
    public async Task BaselineSynchronizer_NewSessionBeforeReviewUsesBoundResumeBeforeReviewOrApply()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var original=await Prepare(f,g);var t=new BaselineTransport(f,g);
        for(var i=0;i<3;i++)Assert.IsTrue(await t.Client().RunAsync(default));
        Assert.IsNull((await Store(f,g).ReadAsync(Server,1,default))!.ReviewDigest);
        t.G=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(t.G,2);
        for(var i=0;i<4;i++)Assert.IsTrue(await t.Client().RunAsync(default));
        var applied=(await Store(f,t.G).ReadAsync(Server,1,default))!;Assert.AreEqual("applied",applied.State);Assert.AreEqual(t.G.PosSessionId,applied.RunSessionId);
        Assert.AreEqual(original.ApplyRequestId,applied.ApplyRequestId);Assert.AreEqual(1,t.RecoveryCalls);Assert.AreEqual(1,t.ApplyCalls);
    }
    [TestMethod]
    public async Task BaselineSynchronizer_LostRecoveryAckReplaysItsOriginalIntentAndPreservesThePriorReview()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var original=await Prepare(f,g);var t=new BaselineTransport(f,g){LoseRecovery=true};
        for(var i=0;i<4;i++)await t.Client().RunAsync(default);
        t.G=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(t.G,2);
        await t.Client().RunAsync(default);await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>t.Client().RunAsync(default));
        Assert.AreEqual(new string('a',64),(await Store(f,t.G).ReadAsync(Server,1,default))!.ReviewDigest);
        for(var i=0;i<3;i++)Assert.IsTrue(await t.Client().RunAsync(default));
        var applied=(await Store(f,t.G).ReadAsync(Server,1,default))!;Assert.AreEqual("applied",applied.State);Assert.AreEqual(original.ApplyRequestId,applied.ApplyRequestId);
        Assert.AreEqual(2,t.RecoveryCalls);Assert.AreEqual(1,t.RecoveryAcks.Count);Assert.AreEqual(1,t.ApplyCalls);
        using var c=f.Db.Factory.Open();Assert.AreEqual(new string('a',64),c.ExecuteScalar<string>("SELECT previous_review_digest FROM historical_baseline_recovery"));
        Assert.AreEqual("consumed",c.ExecuteScalar<string>("SELECT state FROM historical_baseline_recovery"));
    }
    [TestMethod]
    public async Task BaselineSynchronizer_StaleReviewPreparesRecoveryBeforeAnotherEconomicAttemptWithTheSameApplyKey()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var original=await Prepare(f,g);var t=new BaselineTransport(f,g){StaleApply=true};
        for(var i=0;i<5;i++)Assert.IsTrue(await t.Client().RunAsync(default));
        Assert.AreEqual(0,t.ApplyCalls);Assert.IsNotNull(await Store(f,g).ReadPendingRecoveryAsync(original.FlowId,default));
        for(var i=0;i<3;i++)Assert.IsTrue(await t.Client().RunAsync(default));
        Assert.AreEqual(1,t.ApplyCalls);Assert.AreEqual(1,t.RecoveryCalls);
        var attempts=t.Requests.Where(x=>x.Operation=="baseline_apply").ToArray();Assert.AreEqual(2,attempts.Length);
        Assert.IsTrue(attempts.All(x=>x.Payload.RequestKey==original.ApplyRequestId));Assert.AreNotEqual(attempts[0].Payload.ReviewDigest,attempts[1].Payload.ReviewDigest);
    }
    [TestMethod]
    public async Task BaselineSynchronizer_OriginalCommitRacingPendingRecoveryReturnsReceiptWithoutAnotherApply()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var original=await Prepare(f,g);var t=new BaselineTransport(f,g);
        for(var i=0;i<4;i++)await t.Client().RunAsync(default);
        t.G=Generation();await new OnlineSyncGenerationRepository(f.Db.Factory).ActivateAndRecoverAsync(t.G,2);await t.Client().RunAsync(default);
        var flow=(await Store(f,t.G).ReadAsync(Server,1,default))!;
        // Controlled response injection; actual job-lock races require SQL service E2E.
        t.Verified=true;t.Ack=Receipt(flow);Assert.IsTrue(await t.Client().RunAsync(default));
        var applied=(await Store(f,t.G).ReadAsync(Server,1,default))!;Assert.AreEqual("applied",applied.State);Assert.AreEqual(g.PosSessionId,applied.RunSessionId);
        Assert.AreEqual(original.ApplyRequestId,applied.ApplyRequestId);Assert.AreEqual(t.Ack,applied.ReceiptJson);Assert.IsFalse(t.Requests.Any(x=>x.Operation=="baseline_apply"));
    }
    [TestMethod]
    public async Task BaselineSynchronizer_PutAckLostUsesOwnedExistingObjectButStillRequiresWorkerVerification()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();await Prepare(f,g);var t=new BaselineTransport(f,g){AlreadyUploaded=true,UploadVerified=false};
        Assert.IsTrue(await t.Client().RunAsync(default));Assert.IsTrue(await t.Client().RunAsync(default));Assert.IsFalse(await t.Client().RunAsync(default));
        Assert.AreEqual(0,t.UploadCalls);Assert.AreEqual(0,t.StartCalls);Assert.AreEqual("upload_ready",(await Store(f,g).ReadAsync(Server,1,default))!.State);
        t.UploadVerified=true;for(var i=0;i<3;i++)Assert.IsTrue(await t.Client().RunAsync(default));Assert.AreEqual(1,t.ApplyCalls);Assert.AreEqual(0,t.UploadCalls);
    }
}
