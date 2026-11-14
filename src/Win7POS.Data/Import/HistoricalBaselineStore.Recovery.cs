using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Dapper;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Data.Import
{
    public sealed class HistoricalBaselineRecovery
    {
        public string RequestId{get;set;} public string FlowId{get;set;} public string SessionId{get;set;} public int CredentialVersion{get;set;}
        public string PreviousReviewDigest{get;set;} public string State{get;set;} public string AckJson{get;set;}
        public string PreviousRecoveryRequestId{get;set;}
    }
    public sealed partial class HistoricalBaselineStore
    {
        private const string RecoverySelect=@"SELECT request_id RequestId,flow_id FlowId,session_id SessionId,credential_version CredentialVersion,
previous_review_digest PreviousReviewDigest,state State,ack_json AckJson FROM historical_baseline_recovery ";
        public async Task<HistoricalBaselineRecovery> ReadPendingRecoveryAsync(string id,CancellationToken ct)
        {
            _demand();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction(deferred:true);
            await DemandGeneration(c,t,ct).ConfigureAwait(false);var f=ReadFlow(c,t,id);DemandFlow(c,t,f,false);
            var r=c.QuerySingleOrDefault<HistoricalBaselineRecovery>(RecoverySelect+"WHERE flow_id=@id AND session_id=@session AND credential_version=@version AND state='prepared'",
                new{id,session=_generation.PosSessionId,version=_generation.StaffCredentialVersion},t);
            _demand();t.Commit();return r;
        }
        public async Task<HistoricalBaselineRecovery> PrepareRecoveryAsync(string id,CancellationToken ct)
        {
            _demand();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            await DemandGeneration(c,t,ct).ConfigureAwait(false);var f=ReadFlow(c,t,id);DemandFlow(c,t,f,true);
            if(f.State!="run_started")throw new InvalidOperationException("history_baseline_phase_invalid");
            var r=c.QuerySingleOrDefault<HistoricalBaselineRecovery>(RecoverySelect+@"WHERE flow_id=@id AND session_id=@session AND credential_version=@version
AND previous_review_digest IS @previous",new{id,session=_generation.PosSessionId,version=_generation.StaffCredentialVersion,previous=f.ReviewDigest},t);
            if(r!=null){if(r.State!="prepared")throw new InvalidOperationException("history_baseline_recovery_repeat");_demand();ct.ThrowIfCancellationRequested();t.Commit();return r;}
            r=new HistoricalBaselineRecovery{RequestId=Guid.NewGuid().ToString("D"),FlowId=f.FlowId,SessionId=_generation.PosSessionId,CredentialVersion=_generation.StaffCredentialVersion,
                PreviousReviewDigest=f.ReviewDigest,State="prepared"};
            c.Execute(@"INSERT INTO historical_baseline_recovery(request_id,flow_id,session_id,credential_version,previous_review_digest,state)
VALUES(@RequestId,@FlowId,@SessionId,@CredentialVersion,@PreviousReviewDigest,'prepared')",r,t);
            _demand();ct.ThrowIfCancellationRequested();t.Commit();return r;
        }
        public async Task ResolveRecoveryAsync(string request,string ackJson,string receiptJson,CancellationToken ct)
        {
            HistoricalReplicaProtocol.DemandId(request);_demand();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            await DemandGeneration(c,t,ct).ConfigureAwait(false);
            var r=c.QuerySingleOrDefault<HistoricalBaselineRecovery>(RecoverySelect+"WHERE request_id=@request",new{request},t)??throw new InvalidOperationException("history_baseline_recovery_missing");
            var f=ReadFlow(c,t,r.FlowId);DemandFlow(c,t,f,false);
            if(r.SessionId!=_generation.PosSessionId||r.CredentialVersion!=_generation.StaffCredentialVersion)throw new OnlineSyncGenerationChangedException();
            var committed=ReadRecoveryAck(ackJson,receiptJson,f,r,_generation);
            if(r.State=="consumed")
            {if(r.AckJson!=ackJson||(committed&&f.ReceiptJson!=receiptJson))throw new InvalidOperationException("history_baseline_recovery_changed");_demand();ct.ThrowIfCancellationRequested();return;}
            if(r.State!="prepared"||f.State!="run_started"||r.PreviousReviewDigest!=f.ReviewDigest)throw new InvalidOperationException("history_baseline_recovery_changed");
            c.Execute("UPDATE historical_baseline_recovery SET state='acknowledged',ack_json=@ackJson,ack_sha256=@hash WHERE request_id=@request",new{request,ackJson,hash=Hash(System.Text.Encoding.UTF8.GetBytes(ackJson))},t);
            if(committed)ApplyReceipt(c,t,f,receiptJson);
            else c.Execute("UPDATE historical_baseline_flow SET review_digest=NULL,run_session_id=@SessionId,run_credential_version=@CredentialVersion WHERE flow_id=@FlowId",r,t);
            c.Execute("UPDATE historical_baseline_recovery SET state='consumed' WHERE request_id=@request",new{request},t);
            c.Execute("UPDATE historical_baseline_recovery SET state='superseded' WHERE flow_id=@FlowId AND state='prepared'",r,t);
            _demand();ct.ThrowIfCancellationRequested();t.Commit();
        }
        private static bool ReadRecoveryAck(string json,string receiptJson,HistoricalBaselineFlow f,HistoricalBaselineRecovery r,OnlineSyncGeneration g)
        {
            try
            {
                var p=HistoricalBaselineReceipt.Closed(HistoricalBaselineReceipt.ReadRoot(json,16384),"schema_version","state","shop_id","device_id","staff_id","session_id","credential_version",
                    "recovery_request_key","run_id","apply_request_key","previous_review_digest");
                if((string)p["schema_version"].Attribute("type")!="number"||p["schema_version"].Value!="1"||
                    (string)p["credential_version"].Attribute("type")!="number"||p["credential_version"].Value!=g.StaffCredentialVersion.ToString(CultureInfo.InvariantCulture))throw InvalidRecovery();
                foreach(var pair in new[]{("shop_id",f.ShopId),("device_id",f.DeviceId),("staff_id",f.StaffId),("session_id",r.SessionId),
                    ("recovery_request_key",r.RequestId),("run_id",f.RunId),("apply_request_key",f.ApplyRequestId)})
                    if(HistoricalBaselineReceipt.Text(p,pair.Item1)!=pair.Item2)throw InvalidRecovery();
                if(r.PreviousReviewDigest==null)
                {if((string)p["previous_review_digest"].Attribute("type")!="null"||p["previous_review_digest"].HasElements)throw InvalidRecovery();}
                else if(HistoricalBaselineReceipt.Text(p,"previous_review_digest")!=r.PreviousReviewDigest)throw InvalidRecovery();
                var state=HistoricalBaselineReceipt.Text(p,"state");
                if(state=="committed")
                {if(receiptJson==null)throw InvalidRecovery();HistoricalBaselineReceipt.Read(receiptJson,f);return true;}
                if(state!="uncommitted"||receiptJson!=null)throw InvalidRecovery();return false;
            }
            catch(Exception e) when(e is XmlException||e is ArgumentException||e is InvalidOperationException||e is FormatException)
            {throw InvalidRecovery();}
        }
        private static InvalidOperationException InvalidRecovery()=>new InvalidOperationException("history_baseline_recovery_invalid");
    }
}
