using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Import;
using Win7POS.Core.Online;

namespace Win7POS.Data.Import
{
    public sealed class HistoricalPublicationStatus
    {
        public string OperationId{get;set;} public string Status{get;set;} public string ReasonCode{get;set;}
        public string ServerRevision{get;set;} public long? ConfirmedAt{get;set;}
    }
    public sealed class HistoricalPublicationSummary
    {
        public long LocalOperations{get;set;} public long Pending{get;set;}
        public long Attention{get;set;} public long Confirmed{get;set;} public long Unregistered{get;set;}
        public string LastConfirmedRevision{get;set;}
    }
    /// <summary>Historical facts only. Never writes sales, stock, fiscal or native outbox tables.</summary>
    public sealed partial class HistoricalPublicationOutbox
    {
        private readonly SqliteConnectionFactory _factory;
        public HistoricalPublicationOutbox(SqliteConnectionFactory factory)=>_factory=factory??throw new ArgumentNullException(nameof(factory));

        internal static void Enqueue(SqliteConnection c,SqliteTransaction t,HistoricalImportAuthorizationCommitGuard guard,
            HistoricalPublicationDraft draft,HistoricalPublicationProof proof,HistoricalSalesOperationReceipt receipt,string dependsOn,long now,CancellationToken ct)
        {
            draft.DemandMatches(guard);
            proof?.DemandValid();
            if(draft.RestoreLineageId!=HistoricalPublicationDraft.NormalizeLineage(c.ExecuteScalar<string>("SELECT value FROM app_settings WHERE key='pos.history.restore_lineage_id'",transaction:t)))
                throw new InvalidOperationException("history_restore_lineage_changed");
            if(proof!=null&&(proof.OperationId!=Guid.Parse(guard.OperationId).ToString("D")||proof.ShopId!=guard.ShopId||proof.DeviceId!=guard.DeviceId||
                proof.DatasetId!=guard.DatasetId||proof.RestoreLineageId!=draft.RestoreLineageId||proof.RestoreEpoch!=guard.RestoreEpoch||proof.ContentDigest!=draft.ContentDigest||proof.RequestFingerprint!=guard.RequestFingerprint||proof.Decision!="content_authorized_v2"))
                throw new InvalidOperationException("history_publication_proof_mismatch");
            var revision=c.ExecuteScalar<long>("SELECT data_revision FROM historical_import_dataset WHERE singleton_id=1",transaction:t);
            if(proof!=null)
            {
                var decision=proof.LocalDecision;var apply=guard.Kind==HistoricalImportOperationKind.Apply;
                if(receipt==null||receipt.OperationId!=guard.OperationId||receipt.OperationKind!=guard.Kind.ToString()||
                    decision.Inserted!=(apply?receipt.InsertedCount:0)||decision.Deactivated!=(apply?0:receipt.InsertedCount)||
                    decision.Duplicates!=receipt.DuplicateCount||decision.Rejected!=receipt.RejectedCount||decision.Corrections!=receipt.CorrectionCount||
                    HistoricalReplicaProtocol.Integer(decision.SignedGrossClp)!=receipt.SignedAmountClp||
                    HistoricalReplicaProtocol.Integer(decision.RevisionDelta)!=checked(revision-draft.BaseRevision))
                    throw new InvalidOperationException("history_publication_local_decision_mismatch");
            }
            var receiptJson=Serialize(receipt);
            c.Execute(@"INSERT INTO historical_publication_outbox(operation_id,shop_id,device_id,local_dataset_id,restore_epoch,local_revision,operation_kind,request_fingerprint,content_digest,protocol_version,writer_epoch,grant_id,proof_json,depends_on_operation_id,receipt_json,page_count,created_at)
VALUES(@OperationId,@ShopId,@DeviceId,@DatasetId,@RestoreEpoch,@revision,@kind,@RequestFingerprint,@digest,2,@epoch,@grant,@proof,@dependsOn,@receiptJson,@pages,@now)",
                new{guard.OperationId,guard.ShopId,guard.DeviceId,guard.DatasetId,guard.RestoreEpoch,revision,kind=guard.Kind.ToString(),guard.RequestFingerprint,
                    digest=draft.ContentDigest,epoch=(long?)proof?.WriterEpoch,grant=proof?.GrantId,proof=proof==null?null:Serialize(proof),dependsOn,receiptJson,pages=draft.PageCount,now},t);
            var pages=0;using var digest=SHA256.Create();
            foreach(var page in draft.Pages(ct))
            {
                c.Execute("INSERT INTO historical_publication_page(operation_id,ordinal,page_json,page_sha256) VALUES(@OperationId,@ordinal,@json,@sha)",new{guard.OperationId,ordinal=page.Ordinal,json=page.Json,sha=page.Digest},t);
                var line=Encoding.ASCII.GetBytes(page.Digest+"\n");digest.TransformBlock(line,0,line.Length,line,0);pages++;
            }
            digest.TransformFinalBlock(Array.Empty<byte>(),0,0);
            if(pages!=draft.PageCount||Hex(digest.Hash)!=draft.ContentDigest)throw new InvalidOperationException("history_publication_content_changed");
            c.Execute(@"INSERT INTO historical_publication_delivery(operation_id,status,reason_code) VALUES(@OperationId,@status,@reason)",
                new{guard.OperationId,status=proof==null?"needs_attention":"pending",reason=proof==null?"history_publication_proof_missing":null},t);
            guard.DemandStillValid();
        }
        public async Task<HistoricalPublicationStatus> ReadStatusAsync(string operation,string shop,CancellationToken ct)
        {
            using var c=await _factory.OpenExistingForRecoveryAsync(ct).ConfigureAwait(false);
            return await c.QuerySingleOrDefaultAsync<HistoricalPublicationStatus>(new CommandDefinition(@"SELECT o.operation_id OperationId,d.status Status,d.reason_code ReasonCode,d.server_revision ServerRevision,d.confirmed_at ConfirmedAt
FROM historical_publication_outbox o JOIN historical_publication_delivery d ON d.operation_id=o.operation_id WHERE o.operation_id=@operation AND o.shop_id=@shop",new{operation,shop},cancellationToken:ct)).ConfigureAwait(false);
        }
        public async Task<HistoricalPublicationSummary> ReadSummaryAsync(string shop,CancellationToken ct)
        {
            using var c=await _factory.OpenExistingForRecoveryAsync(ct).ConfigureAwait(false);
            return await c.QuerySingleAsync<HistoricalPublicationSummary>(new CommandDefinition(@"SELECT COUNT(*) LocalOperations,
COALESCE(SUM(CASE WHEN d.status IN('pending','uncertain') THEN 1 ELSE 0 END),0) Pending,
COALESCE(SUM(CASE WHEN d.status='needs_attention' THEN 1 ELSE 0 END),0) Attention,
COALESCE(SUM(CASE WHEN d.status='synchronized' THEN 1 ELSE 0 END),0) Confirmed,
COALESCE(SUM(CASE WHEN d.operation_id IS NULL THEN 1 ELSE 0 END),0) Unregistered,
(SELECT server_revision FROM historical_publication_delivery d2 JOIN historical_publication_outbox o2 ON o2.operation_id=d2.operation_id
 JOIN historical_import_dataset h2 ON h2.singleton_id=1 AND h2.dataset_id=o2.local_dataset_id
 WHERE o2.shop_id=@shop AND d2.status='synchronized' ORDER BY CAST(server_revision AS INTEGER) DESC LIMIT 1) LastConfirmedRevision
FROM historical_import_operation op JOIN historical_import_dataset h ON h.singleton_id=1 AND h.dataset_id=op.dataset_id
LEFT JOIN historical_publication_delivery d ON d.operation_id=op.operation_id WHERE op.shop_id=@shop",new{shop},cancellationToken:ct)).ConfigureAwait(false);
        }
        internal static string Serialize<T>(T value){using var stream=new MemoryStream();new DataContractJsonSerializer(typeof(T)).WriteObject(stream,value);return Encoding.UTF8.GetString(stream.ToArray());}
        internal static string Hex(byte[] value)=>BitConverter.ToString(value).Replace("-","").ToLowerInvariant();
    }
}
