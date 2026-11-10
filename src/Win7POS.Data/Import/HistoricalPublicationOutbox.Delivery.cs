using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Online;
using Win7POS.Data.Reports;

namespace Win7POS.Data.Import
{
    public sealed class HistoricalPublicationAttempt
    {
        internal HistoricalPublicationAttempt() { }
        public string OperationId { get; internal set; }
        public long Sequence { get; internal set; }
        public long LocalRevision { get; internal set; }
        public string Kind { get; internal set; }
        public string ReceiptJson { get; internal set; }
        public string ContentDigest { get; internal set; }
        public string ProofJson { get; internal set; }
        public int Attempt { get; internal set; }
        public HistoricalPublicationProof Proof { get; internal set; }
        public bool ReceiptOnly { get; internal set; }
        internal string ClaimLineageId { get; set; }
        public string WireOperationId => Guid.Parse(OperationId).ToString("D");
    }

    public sealed partial class HistoricalPublicationOutbox
    {
        private async Task DemandDeliveryAsync(SqliteConnection c,SqliteTransaction t,OnlineSyncGeneration generation,Action demand,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();demand();
            if(!await OnlineSyncGenerationRepository.IsCurrentAndActiveAsync(c,t,generation).ConfigureAwait(false))
                throw new OnlineSyncGenerationChangedException();
        }
        /// <summary>Persist uncertainty before HTTP; no database lease survives the return.</summary>
        public async Task<HistoricalPublicationAttempt> ClaimAsync(OnlineSyncGeneration generation,Action demand,long now,CancellationToken ct)
        {
            if(generation==null||demand==null)throw new ArgumentNullException();
            demand();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            await DemandDeliveryAsync(c,t,generation,demand,ct).ConfigureAwait(false);
            HistoricalPublicationAttempt row=null;var changed=false;
            for(var skipped=0;skipped<8;skipped++)
            {
                row=await c.QuerySingleOrDefaultAsync<HistoricalPublicationAttempt>(new CommandDefinition(@"SELECT o.operation_id OperationId,o.sequence Sequence,o.local_revision LocalRevision,o.operation_kind Kind,o.receipt_json ReceiptJson,o.content_digest ContentDigest,o.proof_json ProofJson,d.attempt_count Attempt
FROM historical_publication_outbox o JOIN historical_publication_delivery d ON d.operation_id=o.operation_id
JOIN historical_import_dataset h ON h.singleton_id=1 AND h.dataset_id=o.local_dataset_id
WHERE o.shop_id=@shop AND o.device_id=@device AND d.status IN('pending','uncertain') AND COALESCE(d.next_attempt_at,0)<=@now
AND (o.depends_on_operation_id IS NULL OR EXISTS(SELECT 1 FROM historical_publication_delivery dependency WHERE dependency.operation_id=o.depends_on_operation_id AND dependency.status='synchronized'))
ORDER BY o.sequence LIMIT 1",new{shop=generation.ShopId,device=generation.ShopDeviceId,now},t,cancellationToken:ct)).ConfigureAwait(false);
                if(row==null)break;
                string reason=null;
                try
                {
                    row.Proof=Deserialize<HistoricalPublicationProof>(row.ProofJson);
                    if(row.Proof==null)throw new InvalidOperationException("history_publication_proof_invalid");
                    row.Proof.DemandValid();
                    if(row.Proof.OperationId!=row.WireOperationId||row.Proof.ShopId!=generation.ShopId||row.Proof.DeviceId!=generation.ShopDeviceId||row.Proof.ContentDigest!=row.ContentDigest)
                        reason="history_publication_proof_mismatch";
                    var lineage=await c.ExecuteScalarAsync<string>(new CommandDefinition("SELECT value FROM app_settings WHERE key='pos.history.restore_lineage_id'",transaction:t,cancellationToken:ct)).ConfigureAwait(false);
                    row.ClaimLineageId=HistoricalPublicationDraft.NormalizeLineage(lineage);
                    row.ReceiptOnly=row.Proof.RestoreLineageId!=row.ClaimLineageId;
                }
                catch(Exception error) when(error is InvalidOperationException||error is ArgumentException||error is System.Runtime.Serialization.SerializationException)
                {reason="history_publication_proof_invalid";}
                if(reason==null)break;
                await c.ExecuteAsync(new CommandDefinition("UPDATE historical_publication_delivery SET status='needs_attention',reason_code=@reason,next_attempt_at=NULL WHERE operation_id=@op",new{reason,op=row.OperationId},t,cancellationToken:ct)).ConfigureAwait(false);
                changed=true;
                row=null;
            }
            if(row!=null)
            {
                changed=true;
                row.Attempt=checked(row.Attempt+1);
                await c.ExecuteAsync(new CommandDefinition(@"UPDATE historical_publication_delivery SET status='uncertain',reason_code='history_publication_in_flight',attempt_count=@attempt,next_attempt_at=@next WHERE operation_id=@op",
                    new{attempt=row.Attempt,next=checked(now+45000),op=row.OperationId},t,cancellationToken:ct)).ConfigureAwait(false);
            }
            await DemandDeliveryAsync(c,t,generation,demand,ct).ConfigureAwait(false);t.Commit();
            if(changed)SalesReportSnapshotCoordinator.ForDatabase(_factory.DbPath).NotifyHistoricalSyncStatusCommitted();
            return row;
        }
        public async Task<bool> ConfirmAsync(HistoricalPublicationAttempt attempt,HistoricalPublicationServerReceipt receipt,
            OnlineSyncGeneration generation,Action demand,long now,CancellationToken ct)
        {
            DemandReceipt(attempt,receipt);
            demand();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            await DemandDeliveryAsync(c,t,generation,demand,ct).ConfigureAwait(false);
            var lineage=await c.ExecuteScalarAsync<string>(new CommandDefinition("SELECT value FROM app_settings WHERE key='pos.history.restore_lineage_id'",transaction:t,cancellationToken:ct)).ConfigureAwait(false);
            if(attempt.ClaimLineageId!=HistoricalPublicationDraft.NormalizeLineage(lineage))throw new OnlineSyncGenerationChangedException();
            var changed=await c.ExecuteAsync(new CommandDefinition(@"UPDATE historical_publication_delivery SET status='synchronized',reason_code=NULL,next_attempt_at=NULL,server_receipt_json=@receipt,server_revision=@revision,confirmed_at=@now
WHERE operation_id=@op AND status='uncertain' AND attempt_count=@attempt",new{receipt=Serialize(receipt),revision=receipt.Revision,now,op=attempt.OperationId,attempt=attempt.Attempt},t,cancellationToken:ct)).ConfigureAwait(false);
            await DemandDeliveryAsync(c,t,generation,demand,ct).ConfigureAwait(false);t.Commit();
            if(changed==1)SalesReportSnapshotCoordinator.ForDatabase(_factory.DbPath).NotifyHistoricalSyncStatusCommitted();
            return changed==1;
        }
        public async Task<bool> FailAsync(HistoricalPublicationAttempt attempt,OnlineSyncGeneration generation,Action demand,bool retryable,string reason,long now,CancellationToken ct)
        {
            // Keep the reason a typed local status, never arbitrary HTTP text.
            var code=retryable?"history_publication_retry_pending":reason=="history_publication_receipt_mismatch"||reason=="history_publication_restore_reconciliation"?reason:"history_publication_reconciliation_required";
            demand();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            await DemandDeliveryAsync(c,t,generation,demand,ct).ConfigureAwait(false);
            var delay=Math.Min(300000L,1000L*(1L<<Math.Min(attempt.Attempt,8)))+(attempt.Sequence%997);
            var changed=await c.ExecuteAsync(new CommandDefinition(@"UPDATE historical_publication_delivery SET status=@status,reason_code=@code,next_attempt_at=@next
WHERE operation_id=@op AND status='uncertain' AND attempt_count=@attempt",new{status=retryable?"uncertain":"needs_attention",code,next=retryable?(long?)checked(now+delay):null,op=attempt.OperationId,attempt=attempt.Attempt},t,cancellationToken:ct)).ConfigureAwait(false);
            await DemandDeliveryAsync(c,t,generation,demand,ct).ConfigureAwait(false);t.Commit();
            if(changed==1)SalesReportSnapshotCoordinator.ForDatabase(_factory.DbPath).NotifyHistoricalSyncStatusCommitted();
            return changed==1;
        }
        public static void DemandReceipt(HistoricalPublicationAttempt attempt,HistoricalPublicationServerReceipt r)
        {
            try { DemandReceiptCore(attempt,r); }
            catch(Exception failure) when(failure is InvalidOperationException||failure is ArgumentException||failure is FormatException||failure is OverflowException)
            { throw new InvalidOperationException("history_publication_receipt_mismatch"); }
        }
        private static void DemandReceiptCore(HistoricalPublicationAttempt attempt,HistoricalPublicationServerReceipt r)
        {
            var p=attempt?.Proof;
            if(p==null||r==null)throw new InvalidOperationException("history_publication_receipt_mismatch");
            p.DemandValid();
            if(r.OperationId!=attempt.WireOperationId||r.GrantId!=p.GrantId||r.ShopId!=p.ShopId||r.DeviceId!=p.DeviceId||
                r.DatasetId!=p.ServerDatasetId||r.LocalDatasetId!=p.DatasetId||r.RestoreLineageId!=p.RestoreLineageId||r.RestoreEpoch!=p.RestoreEpoch.ToString(CultureInfo.InvariantCulture)||
                r.WriterEpoch!=p.WriterEpoch.ToString(CultureInfo.InvariantCulture)||r.ContentDigest!=p.ContentDigest||r.Kind!=attempt.Kind||
                r.LocalReceiptSha256!=Digest(attempt.ReceiptJson)||r.Inserted<0||r.Duplicates<0||r.Deactivated<0||
                r.Inserted>100000||r.Duplicates>100000||r.Deactivated>100000)
                throw new InvalidOperationException("history_publication_receipt_mismatch");
            HistoricalReplicaProtocol.DemandId(r.BatchId);
            if(HistoricalReplicaProtocol.Integer(r.Revision)<0)throw new InvalidOperationException("history_publication_receipt_mismatch");
            HistoricalReplicaProtocol.Integer(r.SignedGrossClp);
        }
        private static string Digest(string text){using var sha=System.Security.Cryptography.SHA256.Create();return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));}
        private static T Deserialize<T>(string text){if(string.IsNullOrEmpty(text))throw new InvalidOperationException("history_publication_proof_missing");using var stream=new MemoryStream(Encoding.UTF8.GetBytes(text));return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);}
    }
}
