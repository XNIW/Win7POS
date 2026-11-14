using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Win7POS.Core.Reports;
using Win7POS.Core.Import;

namespace Win7POS.Core.Online
{
    [DataContract]
    public sealed class HistoricalCloudRequest
    {
        [DataMember(Name="deviceId")] public string DeviceId { get; set; }
        [DataMember(Name="sessionId")] public string SessionId { get; set; }
        [DataMember(Name="deviceToken")] public string DeviceToken { get; set; }
        [DataMember(Name="sessionToken")] public string SessionToken { get; set; }
        [DataMember(Name="operation")] public string Operation { get; set; }
        [DataMember(Name="payload")] public HistoricalCloudPayload Payload { get; set; }
    }
    [DataContract]
    public sealed partial class HistoricalCloudPayload
    {
        [DataMember(Name="qualification",EmitDefaultValue=false)] public HistoricalCloudQualification Qualification { get; set; }
        [DataMember(Name="request_key",EmitDefaultValue=false)] public string RequestKey { get; set; }
        [DataMember(Name="operation_id",EmitDefaultValue=false)] public string OperationId { get; set; }
        [DataMember(Name="kind",EmitDefaultValue=false)] public string Kind { get; set; }
        [DataMember(Name="writer_epoch",EmitDefaultValue=false)] public string WriterEpoch { get; set; }
        [DataMember(Name="dataset_id",EmitDefaultValue=false)] public string DatasetId { get; set; }
        [DataMember(Name="revision",EmitDefaultValue=false)] public string Revision { get; set; }
        [DataMember(Name="cursor",EmitDefaultValue=false)] public string Cursor { get; set; }
        [DataMember(Name="page_size",EmitDefaultValue=false)] public int? PageSize { get; set; }
        [DataMember(Name="checksum",EmitDefaultValue=false)] public string Checksum { get; set; }
        [DataMember(Name="protocol_version",EmitDefaultValue=false)] public int? ProtocolVersion { get; set; }
        [DataMember(Name="client_build_sha",EmitDefaultValue=false)] public string ClientBuildSha { get; set; }
        [DataMember(Name="local_dataset_id",EmitDefaultValue=false)] public string LocalDatasetId { get; set; }
        [DataMember(Name="data_revision",EmitDefaultValue=false)] public string DataRevision { get; set; }
        [DataMember(Name="restore_epoch",EmitDefaultValue=false)] public string RestoreEpoch { get; set; }
        [DataMember(Name="restore_lineage_id",EmitDefaultValue=false)] public string RestoreLineageId { get; set; }
        [DataMember(Name="archive_sha256",EmitDefaultValue=false)] public string ArchiveSha256 { get; set; }
        [DataMember(Name="manifest_sha256",EmitDefaultValue=false)] public string ManifestSha256 { get; set; }
        [DataMember(Name="backup_sha256",EmitDefaultValue=false)] public string BackupSha256 { get; set; }
        [DataMember(Name="local_writes_drained",EmitDefaultValue=false)] public bool? LocalWritesDrained { get; set; }
        [DataMember(Name="native_outbox_drained",EmitDefaultValue=false)] public bool? NativeOutboxDrained { get; set; }
        [DataMember(Name="content_digest",EmitDefaultValue=false)] public string ContentDigest { get; set; }
        [DataMember(Name="request_fingerprint",EmitDefaultValue=false)] public string RequestFingerprint { get; set; }
        [DataMember(Name="base_revision",EmitDefaultValue=false)] public string BaseRevision { get; set; }
        [DataMember(Name="page_count",EmitDefaultValue=false)] public int? PageCount { get; set; }
        [DataMember(Name="ordinal",EmitDefaultValue=false)] public int? Ordinal { get; set; }
        [DataMember(Name="page_json",EmitDefaultValue=false)] public string PageJson { get; set; }
        [DataMember(Name="page_sha256",EmitDefaultValue=false)] public string PageSha256 { get; set; }
        [DataMember(Name="grant_id",EmitDefaultValue=false)] public string GrantId { get; set; }
        [DataMember(Name="local_revision",EmitDefaultValue=false)] public string LocalRevision { get; set; }
        [DataMember(Name="local_sequence",EmitDefaultValue=false)] public string LocalSequence { get; set; }
        [DataMember(Name="local_receipt_json",EmitDefaultValue=false)] public string LocalReceiptJson { get; set; }
        [DataMember(Name="local_nonce",EmitDefaultValue=false)] public string LocalNonce { get; set; }
        [DataMember(Name="review_token",EmitDefaultValue=false)] public string ReviewToken { get; set; }
        [DataMember(Name="witness_json",EmitDefaultValue=false)] public string WitnessJson { get; set; }
        [DataMember(Name="local_proof_json",EmitDefaultValue=false)] public string LocalProofJson { get; set; }
        [DataMember(Name="upload_id",EmitDefaultValue=false)] public string UploadId { get; set; }
        [DataMember(Name="run_id",EmitDefaultValue=false)] public string RunId { get; set; }
        [DataMember(Name="file_name",EmitDefaultValue=false)] public string FileName { get; set; }
        [DataMember(Name="sha256",EmitDefaultValue=false)] public string Sha256 { get; set; }
        [DataMember(Name="byte_length",EmitDefaultValue=false)] public string ByteLength { get; set; }
        [DataMember(Name="review_digest",EmitDefaultValue=false)] public string ReviewDigest { get; set; }
        [DataMember(Name="recovery_request_key",EmitDefaultValue=false)] public string RecoveryRequestKey { get; set; }
        [DataMember(Name="apply_request_key",EmitDefaultValue=false)] public string ApplyRequestKey { get; set; }
        [DataMember(Name="previous_review_digest",EmitDefaultValue=false)] public string PreviousReviewDigest { get; set; }
        [DataMember(Name="retry_failed",EmitDefaultValue=false)] public bool? RetryFailed { get; set; }
        [DataMember(Name="previous_recovery_request_key",EmitDefaultValue=false)] public string PreviousRecoveryRequestKey { get; set; }
    }
    [DataContract]
    public sealed class HistoricalCloudQualification
    {
        [DataMember(Name="config_digest")] public string ConfigDigest{get;set;}
        [DataMember(Name="resolver_fingerprint")] public string ResolverFingerprint{get;set;}
        [DataMember(Name="evidence_ids")] public string[] EvidenceIds{get;set;}
    }
    [DataContract]
    public sealed class HistoricalCloudResponse
    {
        [DataMember(Name="ok")] public bool Ok { get; set; }
        [DataMember(Name="shop_id")] public string ShopId { get; set; }
        [DataMember(Name="device_id")] public string DeviceId { get; set; }
        [DataMember(Name="session_id")] public string SessionId { get; set; }
        [DataMember(Name="result")] public HistoricalCloudResult Result { get; set; }
    }
    [DataContract]
    public sealed class HistoricalCloudResult
    {
        [DataMember(Name="dataset_id")] public string DatasetId { get; set; }
        [DataMember(Name="revision")] public string Revision { get; set; }
        [DataMember(Name="writer_epoch")] public string WriterEpoch { get; set; }
        [DataMember(Name="writer_authority")] public string WriterAuthority { get; set; }
        [DataMember(Name="inventory_verified")] public bool InventoryVerified { get; set; }
        [DataMember(Name="protocol_version")] public int ProtocolVersion { get; set; }
        [DataMember(Name="grant_id")] public string GrantId { get; set; }
        [DataMember(Name="operation_id")] public string OperationId { get; set; }
        [DataMember(Name="remaining_milliseconds")] public int RemainingMilliseconds { get; set; }
        [DataMember(Name="cursor")] public string Cursor { get; set; }
        [DataMember(Name="next_cursor")] public string NextCursor { get; set; }
        [DataMember(Name="done")] public bool Done { get; set; }
        [DataMember(Name="mode")] public string Mode { get; set; }
        [DataMember(Name="checksum")] public string Checksum { get; set; }
        [DataMember(Name="movements")] public int Movements { get; set; }
        [DataMember(Name="batches")] public int Batches { get; set; }
        [DataMember(Name="acknowledged")] public bool Acknowledged { get; set; }
        [DataMember(Name="entities")] public HistoricalReplicaEntity[] Entities { get; set; }
        [DataMember(Name="state")] public string State { get; set; }
        [DataMember(Name="content_digest")] public string ContentDigest { get; set; }
        [DataMember(Name="next_ordinal")] public int NextOrdinal { get; set; }
        [DataMember(Name="review_token")] public string ReviewToken { get; set; }
        [DataMember(Name="witness_sha256")] public string WitnessSha256 { get; set; }
        [DataMember(Name="page_count")] public int PublicationPageCount { get; set; }
        [DataMember(Name="proof")] public HistoricalPublicationProof PublicationProof { get; set; }
        [DataMember(Name="receipt")] public HistoricalPublicationServerReceipt PublicationReceipt { get; set; }
        [DataMember(Name="baseline")] public HistoricalBaselineContext Baseline { get; set; }
        [DataMember(Name="upload_id")] public string UploadId { get; set; }
        [DataMember(Name="object_key")] public string ObjectKey { get; set; }
        [DataMember(Name="signed_url")] public string SignedUrl { get; set; }
        [DataMember(Name="upload_required")] public bool? UploadRequired { get; set; }
        [DataMember(Name="run_id")] public string RunId { get; set; }
        [DataMember(Name="file_name")] public string FileName { get; set; }
        [DataMember(Name="sha256")] public string Sha256 { get; set; }
        [DataMember(Name="byte_length")] public string ByteLength { get; set; }
        [DataMember(Name="review_digest")] public string ReviewDigest { get; set; }
        [DataMember(Name="can_apply")] public bool? CanApply { get; set; }
        [DataMember(Name="receipt_json")] public string BaselineReceiptJson { get; set; }
        [DataMember(Name="recovery_ack_json")] public string BaselineRecoveryAckJson { get; set; }
        [DataMember(Name="schema_version")] public string QualificationSchemaVersion { get; set; }
        [DataMember(Name="shop_id")] public string QualificationShopId { get; set; }
        [DataMember(Name="config_digest")] public string QualificationConfigDigest { get; set; }
        [DataMember(Name="runtime")] public HistoricalQualificationRuntimeDto QualificationRuntime { get; set; }
        [DataMember(Name="evidence")] public HistoricalQualifiedEvidenceDto[] QualifiedEvidence { get; set; }
        [DataMember(Name="local_scope_bindings")] public HistoricalLocalScopeBindingDto[] QualificationLocalScopeBindings { get; set; }
    }
    [DataContract]
    public sealed class HistoricalLocalScopeBindingDto
    {
        [DataMember(Name="local_scope_id")] public string LocalScopeId { get; set; }
        [DataMember(Name="merchant_id")] public string MerchantId { get; set; }
        [DataMember(Name="source_run_id")] public string SourceRunId { get; set; }
        [DataMember(Name="witness_id")] public string WitnessId { get; set; }
    }
    [DataContract]
    public sealed class HistoricalQualificationRuntimeDto
    {
        [DataMember(Name="dataset_id")] public string DatasetId { get; set; }
        [DataMember(Name="writer_epoch")] public string WriterEpoch { get; set; }
        [DataMember(Name="writer_authority")] public string WriterAuthority { get; set; }
    }
    [DataContract]
    public sealed class HistoricalQualifiedEvidenceDto
    {
        [DataMember(Name="id")] public string Id { get; set; }
        [DataMember(Name="shop_id")] public string ShopId { get; set; }
        [DataMember(Name="kind")] public string Kind { get; set; }
        [DataMember(Name="state")] public string State { get; set; }
        [DataMember(Name="issuer")] public string Issuer { get; set; }
        [DataMember(Name="qualification_version")] public string QualificationVersion { get; set; }
        [DataMember(Name="merchant_id")] public string MerchantId { get; set; }
        [DataMember(Name="alias_key")] public string AliasKey { get; set; }
        [DataMember(Name="profile")] public string Profile { get; set; }
        [DataMember(Name="parser_version")] public string ParserVersion { get; set; }
        [DataMember(Name="files")] public string[] Files { get; set; }
        [DataMember(Name="from_ticks")] public string FromTicks { get; set; }
        [DataMember(Name="to_ticks")] public string ToTicks { get; set; }
        [DataMember(Name="policy_version")] public string PolicyVersion { get; set; }
        [DataMember(Name="reference")] public string Reference { get; set; }
        [DataMember(Name="interpretation")] public string Interpretation { get; set; }
        [DataMember(Name="native_start_ticks")] public string NativeStartTicks { get; set; }
        // Nullable distinguishes a missing protocol field from an explicit false.
        [DataMember(Name="provider_verified")] public bool? ProviderVerified { get; set; }
        [DataMember(Name="document_sha256")] public string DocumentSha256 { get; set; }
        [DataMember(Name="document_upload_id")] public string DocumentUploadId { get; set; }
    }
    [DataContract]
    public sealed class HistoricalBaselineContext
    {
        [DataMember(Name="state")] public string State { get; set; }
        [DataMember(Name="dataset_id")] public string DatasetId { get; set; }
        [DataMember(Name="writer_epoch")] public string WriterEpoch { get; set; }
        [DataMember(Name="writer_authority")] public string WriterAuthority { get; set; }
        [DataMember(Name="source_snapshot_id")] public string SourceSnapshotId { get; set; }
        [DataMember(Name="baseline_revision")] public string BaselineRevision { get; set; }
        [DataMember(Name="canonical_revision")] public string CanonicalRevision { get; set; }
        [DataMember(Name="can_prepare")] public bool? CanPrepare { get; set; }
    }
    [DataContract]
    public sealed class HistoricalPublicationServerReceipt
    {
        [DataMember(Name="operation_id")] public string OperationId { get; set; }
        [DataMember(Name="grant_id")] public string GrantId { get; set; }
        [DataMember(Name="shop_id")] public string ShopId { get; set; }
        [DataMember(Name="device_id")] public string DeviceId { get; set; }
        [DataMember(Name="dataset_id")] public string DatasetId { get; set; }
        [DataMember(Name="local_dataset_id")] public string LocalDatasetId { get; set; }
        [DataMember(Name="restore_epoch")] public string RestoreEpoch { get; set; }
        [DataMember(Name="writer_epoch")] public string WriterEpoch { get; set; }
        [DataMember(Name="revision")] public string Revision { get; set; }
        [DataMember(Name="content_digest")] public string ContentDigest { get; set; }
        [DataMember(Name="local_receipt_sha256")] public string LocalReceiptSha256 { get; set; }
        [DataMember(Name="restore_lineage_id")] public string RestoreLineageId { get; set; }
        [DataMember(Name="batch_id")] public string BatchId { get; set; }
        [DataMember(Name="kind")] public string Kind { get; set; }
        [DataMember(Name="inserted")] public int Inserted { get; set; }
        [DataMember(Name="duplicates")] public int Duplicates { get; set; }
        [DataMember(Name="deactivated")] public int Deactivated { get; set; }
        [DataMember(Name="signed_gross_clp")] public string SignedGrossClp { get; set; }
    }
    [DataContract]
    public sealed class HistoricalReplicaEntity
    {
        [DataMember(Name="kind")] public string Kind { get; set; }
        [DataMember(Name="id")] public string Id { get; set; }
        [DataMember(Name="digest")] public string Digest { get; set; }
        [DataMember(Name="payload")] public HistoricalReplicaValue Payload { get; set; }
    }
    [DataContract]
    public sealed class HistoricalReplicaValue
    {
        [DataMember(Name="id")] public string Id { get; set; }
        [DataMember(Name="origin")] public string Origin { get; set; }
        [DataMember(Name="state")] public string State { get; set; }
        [DataMember(Name="apply_operation_id")] public string ApplyOperationId { get; set; }
        [DataMember(Name="created_revision")] public string CreatedRevision { get; set; }
        [DataMember(Name="cancelled_revision")] public string CancelledRevision { get; set; }
        [DataMember(Name="created_utc_ms")] public string CreatedUtcMs { get; set; }
        [DataMember(Name="merchant_id")] public string MerchantId { get; set; }
        [DataMember(Name="batch_id")] public string BatchId { get; set; }
        [DataMember(Name="identity_key")] public string IdentityKey { get; set; }
        [DataMember(Name="economic_digest")] public string EconomicDigest { get; set; }
        [DataMember(Name="active")] public bool? Active { get; set; }
        [DataMember(Name="business_ticks")] public string BusinessTicks { get; set; }
        [DataMember(Name="gross_clp")] public string GrossClp { get; set; }
        [DataMember(Name="fee_clp")] public string FeeClp { get; set; }
        [DataMember(Name="fee_vat_clp")] public string FeeVatClp { get; set; }
        [DataMember(Name="net_clp")] public string NetClp { get; set; }
        [DataMember(Name="movement_kind")] public string MovementKind { get; set; }
        [DataMember(Name="transaction_state")] public string TransactionState { get; set; }
        [DataMember(Name="external_namespace")] public string ExternalNamespace { get; set; }
        [DataMember(Name="external_id")] public string ExternalId { get; set; }
        [DataMember(Name="source_sheet")] public string SourceSheet { get; set; }
        [DataMember(Name="source_row")] public string SourceRow { get; set; }
        [DataMember(Name="timestamp_raw")] public string TimestampRaw { get; set; }
        [DataMember(Name="offset_minutes")] public string OffsetMinutes { get; set; }
        [DataMember(Name="occurred_at_utc")] public string OccurredAtUtc { get; set; }
        [DataMember(Name="time_interpretation")] public string TimeInterpretation { get; set; }
        [DataMember(Name="time_policy_version")] public string TimePolicyVersion { get; set; }
        [DataMember(Name="time_evidence_reference")] public string TimeEvidenceReference { get; set; }
        [DataMember(Name="card_subtype")] public string CardSubtype { get; set; }
    }
    public static class HistoricalReplicaProtocol
    {
        public const int Version=1;
        public static void DemandId(string value) { if (!Guid.TryParseExact(value,"D",out var id)||id.ToString("D")!=value) throw new InvalidOperationException("history_replica_identity_invalid"); }
        public static void DemandHash(string value) { if(value==null||!Regex.IsMatch(value,"^[0-9a-f]{64}$",RegexOptions.CultureInvariant))throw new InvalidOperationException("history_replica_digest_invalid"); }
        public static long Integer(string value,bool nullable=false)
        {
            if(nullable&&value==null)return 0;
            if(!long.TryParse(value,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var number)||number.ToString(CultureInfo.InvariantCulture)!=value)throw new InvalidOperationException("history_replica_integer_invalid");return number;
        }
        public static string EntityDigest(HistoricalReplicaEntity entity)
        {
            if(entity?.Payload==null)throw new InvalidOperationException("history_replica_entity_invalid");var p=entity.Payload;
            var fields=entity.Kind=="batch"?new[]{p.Id,p.Origin,p.State,p.ApplyOperationId,p.CreatedRevision,p.CancelledRevision,p.CreatedUtcMs}:
                entity.Kind=="movement"?new[]{p.Id,p.MerchantId,p.BatchId,p.IdentityKey,p.EconomicDigest,p.Active.HasValue?(p.Active.Value?"true":"false"):null,p.BusinessTicks,p.GrossClp,p.FeeClp,p.FeeVatClp,p.NetClp,p.MovementKind,p.TransactionState,p.ExternalNamespace,p.ExternalId,p.SourceSheet,p.SourceRow,p.TimestampRaw,p.OffsetMinutes,p.OccurredAtUtc,p.TimeInterpretation,p.TimePolicyVersion,p.TimeEvidenceReference,p.CardSubtype,p.CreatedRevision}:throw new InvalidOperationException("history_replica_entity_invalid");
            return SalesReportHash.Compute(new[]{"history-replica-entity-v1",entity.Kind}.Concat(fields.Select(value=>value??"null")).ToArray());
        }
        public static void Validate(HistoricalReplicaEntity entity,long revision)
        {
            DemandId(entity?.Id);var p=entity.Payload??throw new InvalidOperationException("history_replica_entity_invalid");DemandHash(entity.Digest);
            if(p.Id!=entity.Id||Integer(p.CreatedRevision)<1||Integer(p.CreatedRevision)>revision||EntityDigest(entity)!=entity.Digest)throw new InvalidOperationException("history_replica_entity_invalid");
            if(entity.Kind=="batch")
            {
                DemandId(p.ApplyOperationId);if(p.Origin!="web_import"&&p.Origin!="local_migration"||p.State!="active"&&p.State!="cancelled"||Integer(p.CreatedUtcMs)<0||p.State=="active"&&p.CancelledRevision!=null||p.State=="cancelled"&&(Integer(p.CancelledRevision)<Integer(p.CreatedRevision)||Integer(p.CancelledRevision)>revision))throw new InvalidOperationException("history_replica_batch_invalid");
            }
            else
            {
                DemandId(p.MerchantId);DemandId(p.BatchId);DemandHash(p.IdentityKey);DemandHash(p.EconomicDigest);
                var ticks=Integer(p.BusinessTicks);var gross=Integer(p.GrossClp);Integer(p.FeeClp,true);Integer(p.FeeVatClp,true);Integer(p.NetClp,true);
                if(ticks<0||ticks>DateTime.MaxValue.Ticks||!p.Active.HasValue||!(p.MovementKind=="Sale"&&gross>0||p.MovementKind=="Cancellation"&&gross<0)||p.TransactionState!="ABONADO"&&p.TransactionState!="CONFIRMADA"||p.ExternalNamespace!="getnet.legacy.transaction"&&p.ExternalNamespace!="getnet.financial.movement"||string.IsNullOrEmpty(p.ExternalId)||p.ExternalId.Length>256||p.ExternalId.Any(char.IsControl)||p.SourceSheet==null||p.SourceSheet.Length>256||Integer(p.SourceRow)<1||Integer(p.SourceRow)>1048576||p.TimestampRaw==null||p.TimestampRaw.Length>128||p.TimePolicyVersion==null||p.TimePolicyVersion.Length>160||p.TimeEvidenceReference?.Length>2048||p.CardSubtype!="Credit"&&p.CardSubtype!="Debit"&&p.CardSubtype!="Prepaid"&&p.CardSubtype!="Unknown")throw new InvalidOperationException("history_replica_movement_invalid");
                if(p.OffsetMinutes!=null&&(Integer(p.OffsetMinutes)<-840||Integer(p.OffsetMinutes)>840))throw new InvalidOperationException("history_replica_time_invalid");
                var economics=SalesReportHash.Compute("historical-economics-v1",p.BusinessTicks,p.GrossClp,p.FeeClp??"null",p.FeeVatClp??"null",p.NetClp??"null",p.MovementKind,p.TransactionState);
                if(economics!=p.EconomicDigest)throw new InvalidOperationException("history_replica_economics_invalid");
            }
        }
        public static string SnapshotChecksum(string shop,string dataset,long revision,long epoch,IEnumerable<HistoricalReplicaEntity> entities)
            => SnapshotChecksumOrdered(shop,dataset,revision,epoch,entities.OrderBy(e=>e.Kind,StringComparer.Ordinal).ThenBy(e=>e.Id,StringComparer.Ordinal));
        public static string SnapshotChecksumOrdered(string shop,string dataset,long revision,long epoch,IEnumerable<HistoricalReplicaEntity> entities)
        {
            DemandId(shop);DemandId(dataset);if(revision<0||epoch<1)throw new ArgumentOutOfRangeException(nameof(revision));
            var prefix="history-replica-snapshot-v1\n"+shop+"\n"+dataset+"\n"+revision.ToString(CultureInfo.InvariantCulture)+"\n"+epoch.ToString(CultureInfo.InvariantCulture)+"\n";
            using(var sha=SHA256.Create())
            {
                var header=Encoding.UTF8.GetBytes(prefix);sha.TransformBlock(header,0,header.Length,header,0);
                string previous=null;
                foreach(var e in entities){Validate(e,revision);var key=e.Kind+"|"+e.Id;if(previous!=null&&StringComparer.Ordinal.Compare(previous,key)>=0)throw new InvalidOperationException("history_replica_order_invalid");previous=key;var line=Encoding.UTF8.GetBytes(key+"|"+e.Digest+"\n");sha.TransformBlock(line,0,line.Length,line,0);}
                sha.TransformFinalBlock(Array.Empty<byte>(),0,0);return BitConverter.ToString(sha.Hash).Replace("-","").ToLowerInvariant();
            }
        }
    }
}
