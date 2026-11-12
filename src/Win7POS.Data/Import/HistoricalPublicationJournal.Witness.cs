using System;
using System.Globalization;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Data.Import
{
    public sealed class HistoricalPublicationWitnessEnvelope
    {
        internal HistoricalPublicationWitnessEnvelope(string operation,string root,string outcome,string json,string digest,string proof)
        {JournalOperationId=operation;OperationId=Guid.Parse(operation).ToString("D");ContentDigest=root;Outcome=outcome;Json=json;Sha256=digest;ProofJson=proof;}
        internal string JournalOperationId{get;}
        public string OperationId{get;}
        public string ContentDigest{get;}
        public string Outcome{get;}
        public string Json{get;}
        public string Sha256{get;}
        public string ProofJson{get;}
    }

    public sealed partial class HistoricalPublicationJournal
    {
        internal static void SealCommitWitness(SqliteConnection c,SqliteTransaction t,HistoricalImportAuthorizationCommitGuard guard,
            HistoricalLocalCommitWindow window)
        {
            var row=c.QuerySingleOrDefault<Intent>(Select+" WHERE i.operation_id=@operation",new{operation=guard.OperationId},t);
            var saved=c.QuerySingleOrDefault<CommittedRow>(@"SELECT local_revision Revision,sequence Sequence,receipt_json Receipt,proof_json Proof
FROM historical_publication_outbox WHERE operation_id=@operation",new{operation=guard.OperationId},t);
            if(row==null||saved==null||row.State!="committed"||row.ShopId!=guard.ShopId||row.DeviceId!=guard.DeviceId||row.DatasetId!=guard.DatasetId
                ||row.RestoreEpoch!=guard.RestoreEpoch||window==null||row.Proof!=saved.Proof
                ||saved.Proof!=HistoricalPublicationOutbox.Serialize(window.Proof)||window.MinimumRemainingMilliseconds<10000
                ||c.ExecuteScalar<long>("SELECT data_revision FROM historical_import_dataset WHERE singleton_id=1",transaction:t)!=saved.Revision)
                throw new InvalidOperationException("history_commit_witness_binding_invalid");
            var witness=CommonWitness(row,"committed",saved.Revision,saved.Sequence);
            witness.GrantId=window.Proof.GrantId;witness.LocalReceiptSha256=WitnessDigest(saved.Receipt);
            witness.ProofSha256=WitnessDigest(saved.Proof);witness.AdmissionId=window.AdmissionId;
            witness.RemainingMilliseconds=window.RemainingMilliseconds;witness.RequestElapsedMilliseconds=window.RequestElapsedMilliseconds;
            witness.ElapsedMilliseconds=window.ElapsedMilliseconds;witness.MinimumRemainingMilliseconds=window.MinimumRemainingMilliseconds;
            SaveWitness(c,t,row.OperationId,witness);
        }

        private void SealNegativeWitness(SqliteConnection c,SqliteTransaction t,Intent row)
        {
            var existing=c.QuerySingleOrDefault<WitnessRow>(WitnessSelect+" WHERE operation_id=@operation",new{operation=row.OperationId},t);
            if(existing!=null){DemandWitnessBytes(existing);if(existing.Outcome!="aborted")throw new InvalidOperationException("history_preparation_commit_exists");return;}
            DemandCurrent(c,t,row.DatasetId,row.RestoreEpoch,row.LineageId,row.BaseRevision,true);
            if(c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation WHERE operation_id=@operation",new{operation=row.OperationId},t)!=0
                ||c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_publication_outbox WHERE operation_id=@operation",new{operation=row.OperationId},t)!=0
                ||c.ExecuteScalar<string>("SELECT state FROM historical_preparation_outcome WHERE operation_id=@operation",new{operation=row.OperationId},t)!="aborted")
                throw new InvalidOperationException("history_preparation_commit_exists");
            var sequence=c.ExecuteScalar<long>(@"SELECT COALESCE(MAX(sequence),0) FROM historical_publication_outbox
WHERE shop_id=@ShopId AND device_id=@DeviceId AND local_dataset_id=@DatasetId AND proof_json IS NOT NULL",new{row.ShopId,row.DeviceId,row.DatasetId},t);
            var witness=CommonWitness(row,"aborted",row.BaseRevision,sequence);
            if(row.Proof!=null)
            {
                var proof=ReadProof(row.Proof);proof.DemandValid();witness.GrantId=proof.GrantId;witness.ProofSha256=WitnessDigest(row.Proof);
            }
            SaveWitness(c,t,row.OperationId,witness);
        }

        public async Task<HistoricalPublicationWitnessEnvelope> ReadOutcomeWitnessAsync(string operation,string shop,string device,Action demand,CancellationToken ct)
        {
            if(demand==null)throw new ArgumentNullException(nameof(demand));demand();var authority=_factory.AuthorityStamp;
            using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            var row=c.QuerySingleOrDefault<Intent>(Select+" WHERE i.operation_id=@operation",new{operation},t);
            var witness=c.QuerySingleOrDefault<WitnessRow>(WitnessSelect+" WHERE operation_id=@operation",new{operation},t);
            if(witness==null)return null;
            if(row==null||row.ShopId!=shop||row.DeviceId!=device||row.State!=witness.Outcome)throw new InvalidOperationException("history_commit_witness_binding_invalid");
            // Sealing absence required the exact base revision. Reading already
            // sealed immutable evidence may recover an acquired ACK later too.
            DemandCurrent(c,t,row.DatasetId,row.RestoreEpoch,row.LineageId,row.BaseRevision);DemandWitnessBytes(witness);
            demand();DemandFactory(authority);ct.ThrowIfCancellationRequested();t.Commit();
            return new HistoricalPublicationWitnessEnvelope(row.OperationId,row.ContentDigest,witness.Outcome,witness.Json,witness.Digest,row.Proof);
        }

        public async Task<HistoricalPublicationWitnessEnvelope> ReadPendingNegativeOutcomeAsync(OnlineSyncGeneration generation,Action demand,CancellationToken ct)
        {
            if(generation==null||demand==null)throw new ArgumentNullException();demand();Intent[] rows;
            using(var c=await _factory.OpenAsync(ct).ConfigureAwait(false))
            using(var t=c.BeginTransaction(deferred:true))
            {
                if(!await OnlineSyncGenerationRepository.IsCurrentAndActiveAsync(c,t,generation).ConfigureAwait(false))throw new OnlineSyncGenerationChangedException();
                rows=System.Linq.Enumerable.ToArray(c.Query<Intent>(Select+@"
LEFT JOIN historical_commit_witness w ON w.operation_id=i.operation_id
WHERE i.shop_id=@shop AND i.device_id=@device AND s.state IN('staging','prepared','authorized','aborted') AND w.server_resolution IS NULL
AND NOT EXISTS(SELECT 1 FROM historical_publication_outbox o LEFT JOIN historical_publication_delivery d ON d.operation_id=o.operation_id
 WHERE o.shop_id=@shop AND o.device_id=@device AND (d.operation_id IS NULL OR d.status<>'synchronized')
 AND NOT EXISTS(SELECT 1 FROM historical_legacy_baseline_confirmed b WHERE b.operation_id=o.operation_id AND b.shop_id=o.shop_id))
ORDER BY i.created_at,i.operation_id LIMIT 4",new{shop=generation.ShopId,device=generation.ShopDeviceId},t));
                demand();ct.ThrowIfCancellationRequested();t.Commit();
            }
            foreach(var row in rows)
            {
                try
                {
                    // Acquires the same live UI ownership and seals a permanent
                    // tombstone before any HTTP. No SQLite transaction crosses it.
                    await AbortWithoutCommitAsync(row.OperationId,row.Nonce,generation.ShopId,generation.ShopDeviceId,demand,ct).ConfigureAwait(false);
                    return await ReadOutcomeWitnessAsync(row.OperationId,generation.ShopId,generation.ShopDeviceId,demand,ct).ConfigureAwait(false);
                }
                catch(InvalidOperationException e) when(e.Message=="history_preparation_in_use"||e.Message=="history_preparation_commit_exists"||e.Message=="history_preparation_context_changed")
                {demand();ct.ThrowIfCancellationRequested();}
            }
            return null;
        }

        public async Task ResolveNegativeOutcomeAsync(HistoricalPublicationWitnessEnvelope envelope,HistoricalCloudResult ack,
            OnlineSyncGeneration generation,Action demand,long now,CancellationToken ct)
        {
            if(envelope==null||generation==null||demand==null)throw new ArgumentNullException();
            if(now<=0||envelope.Outcome!="aborted"||ack?.State!="aborted"||ack.OperationId!=envelope.OperationId||ack.ContentDigest!=envelope.ContentDigest||ack.WitnessSha256!=envelope.Sha256)
                throw new HistoricalCloudRequestException("history_publication_abort_ack_invalid",false,false);
            demand();var authority=_factory.AuthorityStamp;
            using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            if(!await OnlineSyncGenerationRepository.IsCurrentAndActiveAsync(c,t,generation).ConfigureAwait(false))throw new OnlineSyncGenerationChangedException();
            var row=c.QuerySingleOrDefault<Intent>(Select+" WHERE i.operation_id=@operation",new{operation=envelope.JournalOperationId},t);
            var witness=c.QuerySingleOrDefault<WitnessRow>(WitnessSelect+" WHERE operation_id=@operation",new{operation=envelope.JournalOperationId},t);
            if(row==null||witness==null||row.ShopId!=generation.ShopId||row.DeviceId!=generation.ShopDeviceId||row.State!="aborted"||witness.Outcome!="aborted"
                ||witness.Json!=envelope.Json||witness.Digest!=envelope.Sha256||row.ContentDigest!=envelope.ContentDigest)
                throw new InvalidOperationException("history_commit_witness_binding_invalid");
            DemandCurrent(c,t,row.DatasetId,row.RestoreEpoch,row.LineageId,row.BaseRevision);DemandWitnessBytes(witness);
            c.Execute(@"UPDATE historical_commit_witness SET server_resolution='aborted',resolved_at_utc_ms=@now
WHERE operation_id=@operation AND outcome='aborted' AND server_resolution IS NULL",new{operation=envelope.JournalOperationId,now},t);
            demand();DemandFactory(authority);ct.ThrowIfCancellationRequested();t.Commit();
        }

        internal static void ResolveCommittedWitness(SqliteConnection c,SqliteTransaction t,string operation,long now)
            =>c.Execute(@"UPDATE historical_commit_witness SET server_resolution='committed',resolved_at_utc_ms=@now
WHERE operation_id=@operation AND outcome='committed' AND server_resolution IS NULL",new{operation,now},t);

        private static OutcomeWitness CommonWitness(Intent row,string outcome,long revision,long sequence)
            =>new OutcomeWitness{SchemaVersion=1,Outcome=outcome,OperationKind=row.Kind,LocalNonce=row.Nonce,OperationId=Guid.Parse(row.OperationId).ToString("D"),
                ShopId=row.ShopId,DeviceId=row.DeviceId,LocalDatasetId=row.DatasetId,ServerDatasetId=row.ServerDataset,RestoreEpoch=Number(row.RestoreEpoch),
                RestoreLineageId=row.LineageId,WriterEpoch=Number(row.WriterEpoch),ContentDigest=row.ContentDigest,RequestFingerprint=row.Fingerprint,
                BaseRevision=Number(row.BaseRevision),LocalRevision=Number(revision),LocalSequence=Number(sequence)};

        private static void SaveWitness(SqliteConnection c,SqliteTransaction t,string operation,OutcomeWitness value)
        {
            var json=HistoricalPublicationOutbox.Serialize(value);
            if(Encoding.UTF8.GetByteCount(json)>16384)throw new InvalidOperationException("history_commit_witness_capacity");
            var digest=WitnessDigest(json);
            c.Execute("INSERT INTO historical_commit_witness(operation_id,outcome,witness_json,witness_sha256) VALUES(@operation,@outcome,@json,@digest)",new{operation,outcome=value.Outcome,json,digest},t);
        }
        private static void DemandWitnessBytes(WitnessRow row)
        {
            if(row.Json==null||Encoding.UTF8.GetByteCount(row.Json)>16384||WitnessDigest(row.Json)!=row.Digest)
                throw new InvalidOperationException("history_commit_witness_binding_invalid");
        }
        private static string WitnessDigest(string text){using var sha=System.Security.Cryptography.SHA256.Create();return HistoricalPublicationOutbox.Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));}
        private static string Number(long value)=>value.ToString(CultureInfo.InvariantCulture);
        private static HistoricalPublicationProof ReadProof(string json)
        {using var stream=new System.IO.MemoryStream(Encoding.UTF8.GetBytes(json));return (HistoricalPublicationProof)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(HistoricalPublicationProof)).ReadObject(stream);}
        private const string WitnessSelect="SELECT outcome Outcome,witness_json Json,witness_sha256 Digest FROM historical_commit_witness";
        private sealed class CommittedRow{public long Revision{get;set;}public long Sequence{get;set;}public string Receipt{get;set;}public string Proof{get;set;}}
        private sealed class WitnessRow{public string Outcome{get;set;}public string Json{get;set;}public string Digest{get;set;}}

        [DataContract]
        private sealed class OutcomeWitness
        {
            [DataMember(Name="schema_version",Order=0)]public int SchemaVersion{get;set;}
            [DataMember(Name="outcome",Order=1)]public string Outcome{get;set;}
            [DataMember(Name="local_nonce",Order=2)]public string LocalNonce{get;set;}
            [DataMember(Name="grant_id",Order=3)]public string GrantId{get;set;}
            [DataMember(Name="operation_id",Order=4)]public string OperationId{get;set;}
            [DataMember(Name="shop_id",Order=5)]public string ShopId{get;set;}
            [DataMember(Name="device_id",Order=6)]public string DeviceId{get;set;}
            [DataMember(Name="local_dataset_id",Order=7)]public string LocalDatasetId{get;set;}
            [DataMember(Name="server_dataset_id",Order=8)]public string ServerDatasetId{get;set;}
            [DataMember(Name="restore_epoch",Order=9)]public string RestoreEpoch{get;set;}
            [DataMember(Name="restore_lineage_id",Order=10)]public string RestoreLineageId{get;set;}
            [DataMember(Name="writer_epoch",Order=11)]public string WriterEpoch{get;set;}
            [DataMember(Name="content_digest",Order=12)]public string ContentDigest{get;set;}
            [DataMember(Name="request_fingerprint",Order=13)]public string RequestFingerprint{get;set;}
            [DataMember(Name="base_revision",Order=14)]public string BaseRevision{get;set;}
            [DataMember(Name="local_revision",Order=15)]public string LocalRevision{get;set;}
            [DataMember(Name="local_sequence",Order=16)]public string LocalSequence{get;set;}
            [DataMember(Name="local_receipt_sha256",Order=17)]public string LocalReceiptSha256{get;set;}
            [DataMember(Name="proof_sha256",Order=18)]public string ProofSha256{get;set;}
            [DataMember(Name="admission_id",Order=19)]public string AdmissionId{get;set;}
            [DataMember(Name="remaining_milliseconds",Order=20)]public int? RemainingMilliseconds{get;set;}
            [DataMember(Name="request_elapsed_milliseconds",Order=21)]public long? RequestElapsedMilliseconds{get;set;}
            [DataMember(Name="elapsed_milliseconds",Order=22)]public long? ElapsedMilliseconds{get;set;}
            [DataMember(Name="minimum_remaining_milliseconds",Order=23)]public long? MinimumRemainingMilliseconds{get;set;}
            [DataMember(Name="operation_kind",Order=24)]public string OperationKind{get;set;}
        }
    }
}
