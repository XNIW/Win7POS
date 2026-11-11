using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Import;
using Win7POS.Core.Online;

namespace Win7POS.Data.Import
{
    public sealed class HistoricalBaselinePreparation
    {
        public HistoricalBaselinePreparation(HistoricalCloudExportReceipt receipt,HistoricalCloudExportGuard guard)
        {Receipt=receipt??throw new ArgumentNullException(nameof(receipt));Guard=guard??throw new ArgumentNullException(nameof(guard));}
        public HistoricalCloudExportReceipt Receipt{get;} public HistoricalCloudExportGuard Guard{get;}
    }
    /// <summary>One recoverable bounded baseline step in the existing history lane.</summary>
    public sealed class HistoricalBaselineSynchronizer
    {
        private readonly SqliteConnectionFactory _factory;private readonly HistoricalBaselineStore _store;private readonly Action _demand;private readonly OnlineSyncGeneration _generation;
        private readonly Func<string,HistoricalCloudPayload,CancellationToken,Task<HistoricalCloudResult>> _request;
        private readonly Func<HistoricalBaselineContext,HistoricalSalesDatasetState,CancellationToken,Task<HistoricalBaselinePreparation>> _prepare;
        private readonly Func<string,HistoricalBaselineFlow,CancellationToken,Task> _upload;
        public HistoricalBaselineSynchronizer(SqliteConnectionFactory factory,OnlineSyncGeneration generation,Action demand,
            Func<string,HistoricalCloudPayload,CancellationToken,Task<HistoricalCloudResult>> request,
            Func<HistoricalBaselineContext,HistoricalSalesDatasetState,CancellationToken,Task<HistoricalBaselinePreparation>> prepare,
            Func<string,HistoricalBaselineFlow,CancellationToken,Task> upload)
        {_factory=factory??throw new ArgumentNullException(nameof(factory));_store=new HistoricalBaselineStore(factory,generation,demand);_generation=generation;_demand=demand??throw new ArgumentNullException(nameof(demand));
         _request=request??throw new ArgumentNullException(nameof(request));_prepare=prepare??throw new ArgumentNullException(nameof(prepare));_upload=upload??throw new ArgumentNullException(nameof(upload));}

        public async Task<bool> RunAsync(CancellationToken ct)
        {
            _demand();ct.ThrowIfCancellationRequested();HistoricalSalesDatasetState state;
            using(var c=await _factory.OpenAsync(ct).ConfigureAwait(false))state=await c.QuerySingleAsync<HistoricalSalesDatasetState>(new CommandDefinition(
                "SELECT dataset_id DatasetId,data_revision DataRevision,(SELECT value FROM app_settings WHERE key='pos.history.restore_lineage_id') RestoreLineageId FROM historical_import_dataset WHERE singleton_id=1",cancellationToken:ct)).ConfigureAwait(false);
            var context=(await Request("context",new HistoricalCloudPayload{LocalDatasetId=state.DatasetId,DataRevision=S(state.DataRevision),RestoreEpoch=S(_factory.AuthorityStamp.RestoreEpoch),
                RestoreLineageId=HistoricalPublicationDraft.NormalizeLineage(state.RestoreLineageId)},ct).ConfigureAwait(false)).Baseline;
            DemandContext(context);var epoch=HistoricalReplicaProtocol.Integer(context.WriterEpoch);
            var flow=await _store.ReadAsync(context.DatasetId,epoch,ct).ConfigureAwait(false);
            if(flow==null)
            {
                if(context.State=="verified")throw new HistoricalCloudRequestException("history_baseline_intent_missing",false,false);
                if(context.CanPrepare!=true)return false;
                var prepared=await _prepare(context,state,ct).ConfigureAwait(false);_demand();
                await _store.SavePreparedAsync(prepared.Receipt,context.DatasetId,epoch,prepared.Guard,ct).ConfigureAwait(false);return true;
            }
            if(flow.State=="needs_attention")return false;
            if(flow.State=="applied")
            {
                var receipt=HistoricalBaselineReceipt.Read(flow.ReceiptJson,flow);
                if(context.State!="verified"||receipt.Snapshot!=context.SourceSnapshotId||receipt.Revision!=HistoricalReplicaProtocol.Integer(context.CanonicalRevision))
                    throw new HistoricalCloudRequestException("history_baseline_context_changed",false,false);
                return false;
            }
            if(context.State=="verified"&&(flow.State!="run_started"||flow.ReviewDigest==null))
            {await _store.MarkAttentionAsync(flow.FlowId,"baseline_unknown_outcome",ct).ConfigureAwait(false);return false;}
            if(context.CanPrepare!=true)return false;
            if(flow.State=="prepared"||flow.State=="upload_created")
            {
                await Task.Run(()=>HistoricalBaselineStore.VerifyFiles(flow,ct),ct).ConfigureAwait(false);_demand();
                var length=new FileInfo(flow.ArchivePath).Length;
                var upload=await Request("upload_create",new HistoricalCloudPayload{RequestKey=flow.FlowId,FileName=Path.GetFileName(flow.ArchivePath),Sha256=flow.ArchiveSha256,ByteLength=S(length)},ct).ConfigureAwait(false);
                HistoricalReplicaProtocol.DemandId(upload.UploadId);
                if(upload.ObjectKey!="history/"+flow.ShopId+"/"+upload.UploadId||upload.UploadRequired==null||
                    (upload.UploadRequired==true?string.IsNullOrEmpty(upload.SignedUrl):upload.SignedUrl!=null))throw Invalid();
                if(flow.State=="prepared"){await _store.SaveUploadAsync(flow.FlowId,upload.UploadId,ct).ConfigureAwait(false);return true;}
                if(flow.UploadId!=upload.UploadId)throw Invalid();
                if(upload.UploadRequired==true)await _upload(upload.SignedUrl,flow,ct).ConfigureAwait(false);_demand();
                var ready=await Request("upload_ready",new HistoricalCloudPayload{UploadId=flow.UploadId},ct).ConfigureAwait(false);DemandUpload(ready,flow);
                await _store.SaveReadyAsync(flow.FlowId,ct).ConfigureAwait(false);return true;
            }
            if(flow.State=="upload_ready")
            {
                var upload=await Request("upload_status",new HistoricalCloudPayload{UploadId=flow.UploadId},ct).ConfigureAwait(false);DemandUpload(upload,flow);
                if(upload.State=="rejected"||upload.State=="expired"){await _store.MarkAttentionAsync(flow.FlowId,"baseline_upload_rejected",ct).ConfigureAwait(false);return false;}
                if(upload.State!="verified")return false;
                var run=await Request("start",new HistoricalCloudPayload{RequestKey=flow.StartRequestId,UploadId=flow.UploadId,LocalDatasetId=flow.LocalDatasetId,DataRevision=S(flow.LocalRevision),
                    RestoreEpoch=S(flow.RestoreEpoch),RestoreLineageId=flow.RestoreLineageId,WriterEpoch=S(flow.WriterEpoch)},ct).ConfigureAwait(false);
                HistoricalReplicaProtocol.DemandId(run.RunId);if(run.State!="queued"&&run.State!="analysing"&&run.State!="review_ready")throw Invalid();
                await _store.SaveRunAsync(flow.FlowId,run.RunId,ct).ConfigureAwait(false);return true;
            }
            if(flow.State!="run_started")throw Invalid();
            var pending=await _store.ReadPendingRecoveryAsync(flow.FlowId,ct).ConfigureAwait(false);
            if(pending!=null)
            {
                var resolved=await Request("recover",new HistoricalCloudPayload{RecoveryRequestKey=pending.RequestId,RunId=flow.RunId,
                    ApplyRequestKey=flow.ApplyRequestId,PreviousReviewDigest=pending.PreviousReviewDigest},ct).ConfigureAwait(false);
                await _store.ResolveRecoveryAsync(pending.RequestId,resolved.BaselineRecoveryAckJson,resolved.BaselineReceiptJson,ct).ConfigureAwait(false);return true;
            }
            if(flow.RunSessionId!=_generation.PosSessionId||flow.RunCredentialVersion!=_generation.StaffCredentialVersion)
            {
                var original=await Request("receipt",new HistoricalCloudPayload{RequestKey=flow.ApplyRequestId},ct).ConfigureAwait(false);
                if(original.BaselineReceiptJson!=null){await _store.SaveReceiptAsync(flow.FlowId,original.BaselineReceiptJson,ct).ConfigureAwait(false);return true;}
                await _store.PrepareRecoveryAsync(flow.FlowId,ct).ConfigureAwait(false);return true;
            }
            if(flow.ReviewDigest==null)
            {
                var run=await Request("status",new HistoricalCloudPayload{RunId=flow.RunId},ct).ConfigureAwait(false);if(run.RunId!=flow.RunId)throw Invalid();
                if(run.State=="queued"||run.State=="analysing")return false;
                if(run.State!="review_ready"&&run.State!="applied"&&run.State!="failed"&&run.State!="cancelled")throw Invalid();
                if(run.State!="review_ready"){await _store.MarkAttentionAsync(flow.FlowId,run.State=="applied"?"baseline_unknown_outcome":"baseline_job_failed",ct).ConfigureAwait(false);return false;}
                var review=await Request("review",new HistoricalCloudPayload{RunId=flow.RunId},ct).ConfigureAwait(false);
                if(review.RunId!=flow.RunId||review.CanApply==null)throw Invalid();
                if(review.CanApply!=true){await _store.MarkAttentionAsync(flow.FlowId,"baseline_review_denied",ct).ConfigureAwait(false);return false;}
                await _store.SealReviewAsync(flow.FlowId,review.ReviewDigest,ct).ConfigureAwait(false);return true;
            }
            var recovered=await Request("receipt",new HistoricalCloudPayload{RequestKey=flow.ApplyRequestId},ct).ConfigureAwait(false);
            if(recovered.BaselineReceiptJson!=null){await _store.SaveReceiptAsync(flow.FlowId,recovered.BaselineReceiptJson,ct).ConfigureAwait(false);return true;}
            HistoricalCloudResult applied;
            try{applied=await Request("apply",new HistoricalCloudPayload{RunId=flow.RunId,RequestKey=flow.ApplyRequestId,ReviewDigest=flow.ReviewDigest},ct).ConfigureAwait(false);}
            catch(HistoricalCloudRequestException e) when(e.Code=="history_baseline_review_stale")
            {await _store.PrepareRecoveryAsync(flow.FlowId,ct).ConfigureAwait(false);return true;}
            await _store.SaveReceiptAsync(flow.FlowId,applied.BaselineReceiptJson,ct).ConfigureAwait(false);return true;
        }
        private async Task<HistoricalCloudResult> Request(string operation,HistoricalCloudPayload payload,CancellationToken ct)
        {_demand();ct.ThrowIfCancellationRequested();var r=await _request("baseline_"+operation,payload,ct).ConfigureAwait(false);_demand();ct.ThrowIfCancellationRequested();return r??throw Invalid();}
        private static void DemandContext(HistoricalBaselineContext c)
        {
            if(c==null||c.CanPrepare==null||(c.State!="missing"&&c.State!="verified")||(c.WriterAuthority!="local"&&c.WriterAuthority!="frozen"&&c.WriterAuthority!="web")||
                c.CanPrepare==true&&c.WriterAuthority!="local")throw Invalid();
            HistoricalReplicaProtocol.DemandId(c.DatasetId);if(HistoricalReplicaProtocol.Integer(c.WriterEpoch)<1)throw Invalid();
            if(c.State=="missing"){if(c.SourceSnapshotId!=null||c.BaselineRevision!=null||c.CanonicalRevision!=null)throw Invalid();}
            else{HistoricalReplicaProtocol.DemandId(c.SourceSnapshotId);if(HistoricalReplicaProtocol.Integer(c.BaselineRevision)<0||HistoricalReplicaProtocol.Integer(c.CanonicalRevision)<1)throw Invalid();}
        }
        private static void DemandUpload(HistoricalCloudResult r,HistoricalBaselineFlow f)
        {
            if(r.UploadId!=f.UploadId||r.Sha256!=f.ArchiveSha256||r.FileName!=Path.GetFileName(f.ArchivePath)||r.ByteLength!=S(new FileInfo(f.ArchivePath).Length)||
                (r.State!="pending"&&r.State!="uploaded"&&r.State!="verifying"&&r.State!="verified"&&r.State!="rejected"&&r.State!="expired"))throw Invalid();
        }
        private static string S(long n)=>n.ToString(CultureInfo.InvariantCulture);
        private static HistoricalCloudRequestException Invalid()=>new HistoricalCloudRequestException("history_response_invalid",false,false);
    }
}
