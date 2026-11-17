using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Data.Import
{
    public sealed class HistoricalBaselineRetryStatus
    {
        public string State{get;set;} public string ReasonCode{get;set;}
        public bool CanRequestRetry{get;set;} public bool RetryPending{get;set;}
    }
    public sealed partial class HistoricalBaselineStore
    {
        private const string RetrySelect=@"SELECT request_id RequestId,flow_id FlowId,session_id SessionId,credential_version CredentialVersion,
previous_review_digest PreviousReviewDigest,state State,ack_json AckJson,supersedes_request_id PreviousRecoveryRequestId FROM historical_baseline_retry ";
        private HistoricalBaselineFlow CurrentFlow(Microsoft.Data.Sqlite.SqliteConnection c,Microsoft.Data.Sqlite.SqliteTransaction t)
            =>c.QuerySingleOrDefault<HistoricalBaselineFlow>(Select+@"WHERE shop_id=@shop AND device_id=@device
AND local_dataset_id=(SELECT dataset_id FROM historical_import_dataset WHERE singleton_id=1)
AND replace(restore_lineage_id,'-','')=(SELECT replace(value,'-','') FROM app_settings WHERE key='pos.history.restore_lineage_id') COLLATE NOCASE
ORDER BY rowid DESC LIMIT 1",new{shop=_generation.ShopId,device=_generation.ShopDeviceId},t);
        private static bool RetryCandidate(HistoricalBaselineFlow f)=>f!=null&&f.State=="needs_attention"&&f.ReasonCode=="baseline_job_failed"&&
            f.ReviewDigest==null&&f.ReceiptJson==null&&f.RunId!=null&&f.UploadId!=null;
        public async Task<HistoricalBaselineRetryStatus> ReadRetryStatusAsync(CancellationToken ct)
        {
            _demand();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction(deferred:true);
            var generationCurrent=await OnlineSyncGenerationRepository.IsCurrentAndActiveAsync(c,t,_generation).ConfigureAwait(false);var f=CurrentFlow(c,t);
            if(f==null){_demand();t.Commit();return new HistoricalBaselineRetryStatus();}
            DemandFlow(c,t,f,false,false);
            var pending=c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_baseline_retry WHERE flow_id=@FlowId AND state='prepared'",f,t)>0;
            var status=new HistoricalBaselineRetryStatus{State=f.State,ReasonCode=f.ReasonCode,RetryPending=pending,
                CanRequestRetry=generationCurrent&&RetryCandidate(f)&&f.StaffId==_generation.StaffId&&!pending};
            _demand();t.Commit();return status;
        }
        // Only the explicit operator command may create an initial retry intent.
        public async Task<HistoricalBaselineRecovery> PrepareFailedRetryAsync(CancellationToken ct)
        {
            HistoricalBaselineFlow original;
            _demand();using(var c=await _factory.OpenAsync(ct).ConfigureAwait(false))using(var t=c.BeginTransaction(deferred:true))
            {
                await DemandGeneration(c,t,ct).ConfigureAwait(false);original=CurrentFlow(c,t);
                if(!RetryCandidate(original))throw new InvalidOperationException("history_baseline_retry_unavailable");
                DemandFlow(c,t,original,true);_demand();t.Commit();
            }
            await Task.Run(()=>VerifyFiles(original,ct),ct).ConfigureAwait(false);
            return await PrepareOrContinueFailedRetryAsync(original.FlowId,true,ct).ConfigureAwait(false);
        }
        // A generation change may continue a previously requested operation, but
        // a background turn never creates an initial operator retry intent.
        internal Task<HistoricalBaselineRecovery> ContinuePendingFailedRetryAsync(string id,CancellationToken ct)
            =>PrepareOrContinueFailedRetryAsync(id,false,ct);
        private async Task<HistoricalBaselineRecovery> PrepareOrContinueFailedRetryAsync(string id,bool explicitRequest,CancellationToken ct)
        {
            _demand();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            await DemandGeneration(c,t,ct).ConfigureAwait(false);var f=ReadFlow(c,t,id);DemandFlow(c,t,f,true);
            if(!RetryCandidate(f))throw new InvalidOperationException("history_baseline_retry_unavailable");
            var pending=c.QuerySingleOrDefault<HistoricalBaselineRecovery>(RetrySelect+@"WHERE flow_id=@id AND session_id=@session AND credential_version=@version AND state='prepared'",
                new{id,session=_generation.PosSessionId,version=_generation.StaffCredentialVersion},t);
            if(pending!=null){_demand();t.Commit();return pending;}
            // Successors refer to the initial explicit request, which may have
            // reached the server; a later deferred local successor may not have.
            var previous=c.QuerySingleOrDefault<HistoricalBaselineRecovery>(RetrySelect+"WHERE flow_id=@id AND state='prepared' ORDER BY rowid LIMIT 1",new{id},t);
            if(!explicitRequest&&previous==null){_demand();t.Commit();return null;}
            var next=new HistoricalBaselineRecovery{RequestId=Guid.NewGuid().ToString("D"),FlowId=f.FlowId,SessionId=_generation.PosSessionId,
                CredentialVersion=_generation.StaffCredentialVersion,State="prepared",PreviousRecoveryRequestId=previous?.RequestId};
            c.Execute(@"INSERT INTO historical_baseline_retry(request_id,flow_id,session_id,credential_version,previous_review_digest,supersedes_request_id,state)
VALUES(@RequestId,@FlowId,@SessionId,@CredentialVersion,NULL,@previous,'prepared')",
                new{next.RequestId,next.FlowId,next.SessionId,next.CredentialVersion,previous=previous?.RequestId},t);
            _demand();ct.ThrowIfCancellationRequested();t.Commit();return next;
        }
        internal async Task ResolveFailedRetryAsync(string request,string ackJson,string receiptJson,CancellationToken ct)
        {
            HistoricalReplicaProtocol.DemandId(request);_demand();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            await DemandGeneration(c,t,ct).ConfigureAwait(false);
            var retry=c.QuerySingleOrDefault<HistoricalBaselineRecovery>(RetrySelect+"WHERE request_id=@request",new{request},t)
                ??throw new InvalidOperationException("history_baseline_retry_missing");
            var f=ReadFlow(c,t,retry.FlowId);DemandFlow(c,t,f,true);
            if(retry.SessionId!=_generation.PosSessionId||retry.CredentialVersion!=_generation.StaffCredentialVersion)throw new OnlineSyncGenerationChangedException();
            // Failed preparation has no economic review: only an uncommitted,
            // original-operation ACK may reopen it. A receipt is never invented.
            if(ReadRecoveryAck(ackJson,receiptJson,f,retry,_generation))throw new InvalidOperationException("history_baseline_retry_invalid");
            if(retry.State=="consumed")
            {if(retry.AckJson!=ackJson)throw new InvalidOperationException("history_baseline_retry_changed");_demand();ct.ThrowIfCancellationRequested();return;}
            if(retry.State!="prepared"||!RetryCandidate(f))throw new InvalidOperationException("history_baseline_retry_changed");
            c.Execute("UPDATE historical_baseline_retry SET state='acknowledged',ack_json=@ackJson,ack_sha256=@hash WHERE request_id=@request",
                new{request,ackJson,hash=Hash(Encoding.UTF8.GetBytes(ackJson))},t);
            c.Execute("UPDATE historical_baseline_flow SET state='run_started',run_session_id=@SessionId,run_credential_version=@CredentialVersion WHERE flow_id=@FlowId",retry,t);
            c.Execute("UPDATE historical_baseline_retry SET state='consumed' WHERE request_id=@request",new{request},t);
            c.Execute("UPDATE historical_baseline_retry SET state='superseded' WHERE flow_id=@FlowId AND state='prepared'",retry,t);
            _demand();ct.ThrowIfCancellationRequested();t.Commit();
        }
        internal async Task RejectFailedRetryAsync(string request,string code,CancellationToken ct)
        {
            HistoricalReplicaProtocol.DemandId(request);
            if(string.IsNullOrEmpty(code)||code.Length>80)throw new ArgumentException(nameof(code));
            _demand();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            await DemandGeneration(c,t,ct).ConfigureAwait(false);
            var retry=c.QuerySingleOrDefault<HistoricalBaselineRecovery>(RetrySelect+"WHERE request_id=@request",new{request},t)
                ??throw new InvalidOperationException("history_baseline_retry_missing");
            var f=ReadFlow(c,t,retry.FlowId);DemandFlow(c,t,f,true);
            if(retry.SessionId!=_generation.PosSessionId||retry.CredentialVersion!=_generation.StaffCredentialVersion)throw new OnlineSyncGenerationChangedException();
            if(retry.State!="prepared")throw new InvalidOperationException("history_baseline_retry_changed");
            c.Execute("UPDATE historical_baseline_retry SET state='rejected',rejection_code=@code WHERE flow_id=@FlowId AND state='prepared'",new{retry.FlowId,code},t);
            _demand();ct.ThrowIfCancellationRequested();t.Commit();
        }
    }
}
