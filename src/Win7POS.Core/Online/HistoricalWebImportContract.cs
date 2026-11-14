using System.Runtime.Serialization;

namespace Win7POS.Core.Online
{
    public sealed partial class HistoricalCloudPayload
    {
        [DataMember(Name="upload_ids",EmitDefaultValue=false)] public string[] WebUploadIds { get; set; }
        [DataMember(Name="configuration",EmitDefaultValue=false)] public HistoricalWebParserConfiguration WebConfiguration { get; set; }
        [DataMember(Name="job_id",EmitDefaultValue=false)] public string WebJobId { get; set; }
        [DataMember(Name="evidence_ids",EmitDefaultValue=false)] public string[] WebEvidenceIds { get; set; }
        // The device contract accepts no corrections in this protocol version.
        [DataMember(Name="corrections",EmitDefaultValue=false)] public string[] WebCorrections { get; set; }
    }
    [DataContract]
    public sealed class HistoricalWebParserConfiguration
    {
        [DataMember(Name="legacyAmountsAreClp")] public bool LegacyAmountsAreClp { get; set; }
        [DataMember(Name="modernTimeInterpretation")] public string ModernTimeInterpretation { get; set; }
        [DataMember(Name="temporalPolicyVersion")] public string TemporalPolicyVersion { get; set; }
        [DataMember(Name="temporalEvidenceReference")] public string TemporalEvidenceReference { get; set; }
    }
    [DataContract]
    public sealed class HistoricalWebCloudResponse
    {
        [DataMember(Name="ok")] public bool Ok { get; set; }
        [DataMember(Name="shop_id")] public string ShopId { get; set; }
        [DataMember(Name="device_id")] public string DeviceId { get; set; }
        [DataMember(Name="session_id")] public string SessionId { get; set; }
        [DataMember(Name="result")] public HistoricalWebResultDto Result { get; set; }
    }
    [DataContract]
    public sealed class HistoricalWebResultDto
    {
        [DataMember(Name="upload_id")] public string UploadId { get; set; }
        [DataMember(Name="object_key")] public string ObjectKey { get; set; }
        [DataMember(Name="upload_required")] public bool? UploadRequired { get; set; }
        [DataMember(Name="signed_url")] public string UploadUrl { get; set; }
        [DataMember(Name="file_name")] public string FileName { get; set; }
        [DataMember(Name="sha256")] public string Sha256 { get; set; }
        [DataMember(Name="byte_length")] public string ByteLength { get; set; }
        [DataMember(Name="job_id")] public string JobId { get; set; }
        [DataMember(Name="state")] public string State { get; set; }
        [DataMember(Name="processed_rows")] public int? ProcessedRows { get; set; }
        [DataMember(Name="summary")] public HistoricalWebSummaryDto Summary { get; set; }
        [DataMember(Name="diagnostics")] public HistoricalWebDiagnosticDto[] Diagnostics { get; set; }
        [DataMember(Name="error_code")] public string ErrorCode { get; set; }
        [DataMember(Name="merchants")] public HistoricalWebMerchantDto[] Merchants { get; set; }
        [DataMember(Name="shop_id")] public string ShopId { get; set; }
        [DataMember(Name="dataset_id")] public string DatasetId { get; set; }
        [DataMember(Name="revision")] public string Revision { get; set; }
        [DataMember(Name="writer_epoch")] public string WriterEpoch { get; set; }
        [DataMember(Name="writer_ready")] public bool? WriterReady { get; set; }
        [DataMember(Name="can_commit")] public bool? CanCommit { get; set; }
        [DataMember(Name="reasons")] public string[] Reasons { get; set; }
        [DataMember(Name="review_digest")] public string ReviewDigest { get; set; }
        [DataMember(Name="operation_id")] public string OperationId { get; set; }
        [DataMember(Name="batch_id")] public string BatchId { get; set; }
        [DataMember(Name="kind")] public string Kind { get; set; }
        [DataMember(Name="receipt")] public HistoricalWebReceiptDto Receipt { get; set; }
    }
    [DataContract]
    public sealed class HistoricalWebReceiptDto
    {
        [DataMember(Name="operation_id")] public string OperationId { get; set; }
        [DataMember(Name="batch_id")] public string BatchId { get; set; }
        [DataMember(Name="kind")] public string Kind { get; set; }
        [DataMember(Name="dataset_id")] public string DatasetId { get; set; }
        [DataMember(Name="revision")] public string Revision { get; set; }
        [DataMember(Name="writer_epoch")] public string WriterEpoch { get; set; }
        [DataMember(Name="summary")] public HistoricalWebSummaryDto Summary { get; set; }
    }
    // The one wire field "summary" is a closed union of parser and review
    // shapes. The operation-specific mapper rejects the other shape's fields.
    [DataContract]
    public sealed class HistoricalWebSummaryDto
    {
        [DataMember(Name="total")] public int? Total { get; set; }
        [DataMember(Name="new_count")] public int? NewCount { get; set; }
        [DataMember(Name="duplicate_count")] public int? DuplicateCount { get; set; }
        [DataMember(Name="conflicts")] public int? Conflicts { get; set; }
        [DataMember(Name="blocked")] public int? Blocked { get; set; }
        [DataMember(Name="new_gross_clp")] public string NewGrossClp { get; set; }
        [DataMember(Name="known_new_net_clp")] public string KnownNewNetClp { get; set; }
        [DataMember(Name="known_new_net_count")] public int? KnownNewNetCount { get; set; }
        [DataMember(Name="sourceRows")] public int? SourceRows { get; set; }
        [DataMember(Name="accepted")] public int? Accepted { get; set; }
        [DataMember(Name="sales")] public int? Sales { get; set; }
        [DataMember(Name="cancellations")] public int? Cancellations { get; set; }
        [DataMember(Name="rejected")] public int? Rejected { get; set; }
        [DataMember(Name="signedGrossClp")] public string SignedGrossClp { get; set; }
        [DataMember(Name="positiveGrossClp")] public string PositiveGrossClp { get; set; }
        [DataMember(Name="cancellationGrossClp")] public string CancellationGrossClp { get; set; }
        [DataMember(Name="knownNetClp")] public string KnownNetClp { get; set; }
        [DataMember(Name="knownNetCount")] public int? KnownNetCount { get; set; }
        [DataMember(Name="rejectedRequestedClp")] public string RejectedRequestedClp { get; set; }
        [DataMember(Name="knownRejectedRequestedCount")] public int? KnownRejectedRequestedCount { get; set; }
        [DataMember(Name="fromBusinessDateTime")] public string FromBusinessDateTime { get; set; }
        [DataMember(Name="toBusinessDateTime")] public string ToBusinessDateTime { get; set; }
        [DataMember(Name="fromIsoCandidateDateTime")] public string FromIsoCandidateDateTime { get; set; }
        [DataMember(Name="toIsoCandidateDateTime")] public string ToIsoCandidateDateTime { get; set; }
        [DataMember(Name="fromExportedLocalCandidateDateTime")] public string FromExportedLocalCandidateDateTime { get; set; }
        [DataMember(Name="toExportedLocalCandidateDateTime")] public string ToExportedLocalCandidateDateTime { get; set; }
        [DataMember(Name="sourceAccepted")] public int? SourceAccepted { get; set; }
        [DataMember(Name="candidates")] public HistoricalWebTimeCandidatesDto Candidates { get; set; }
        [DataMember(Name="needsTimeDecision")] public bool? NeedsTimeDecision { get; set; }
    }
    [DataContract]
    public sealed class HistoricalWebTimeCandidatesDto
    {
        [DataMember(Name="isoFrom")] public string IsoFrom { get; set; }
        [DataMember(Name="isoTo")] public string IsoTo { get; set; }
        [DataMember(Name="localFrom")] public string LocalFrom { get; set; }
        [DataMember(Name="localTo")] public string LocalTo { get; set; }
    }
    [DataContract]
    public sealed class HistoricalWebDiagnosticDto
    {
        [DataMember(Name="code")] public string Code { get; set; }
        [DataMember(Name="severity")] public string Severity { get; set; }
        [DataMember(Name="source")] public HistoricalWebDiagnosticSourceDto Source { get; set; }
        [DataMember(Name="field")] public string Field { get; set; }
        [DataMember(Name="column")] public int? Column { get; set; }
    }
    [DataContract]
    public sealed class HistoricalWebDiagnosticSourceDto
    {
        [DataMember(Name="fileId")] public string FileId { get; set; }
        [DataMember(Name="fileSha256")] public string FileSha256 { get; set; }
        [DataMember(Name="sheetName")] public string SheetName { get; set; }
        [DataMember(Name="rowNumber")] public int? RowNumber { get; set; }
    }
    [DataContract]
    public sealed class HistoricalWebMerchantDto
    {
        [DataMember(Name="alias_key")] public string AliasKey { get; set; }
        [DataMember(Name="source_identity")] public HistoricalWebMerchantSourceDto SourceIdentity { get; set; }
    }
    [DataContract]
    public sealed class HistoricalWebMerchantSourceDto
    {
        [DataMember(Name="local")] public string Local { get; set; }
        [DataMember(Name="seller")] public string Seller { get; set; }
        [DataMember(Name="sellerId")] public string SellerId { get; set; }
        [DataMember(Name="localNumber")] public string LocalNumber { get; set; }
        [DataMember(Name="rut")] public string Rut { get; set; }
        [DataMember(Name="hasEvidence")] public bool? HasEvidence { get; set; }
    }
}
