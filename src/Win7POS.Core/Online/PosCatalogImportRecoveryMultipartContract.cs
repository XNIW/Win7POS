using System.Runtime.Serialization;

namespace Win7POS.Core.Online
{
    public static class PosCatalogImportRecoveryMultipartContract
    {
        public const string SchemaVersion = "pos-catalog-import-recovery-multipart-v1";
        public const string PlanSchemaVersion = "pos-catalog-import-recovery-plan-v1";
        public const string BasePath = "/api/pos/catalog/import-recovery/";
        public const int MaximumHttpBytes = 512 * 1024;
        public const int MaximumRawPartBytes = 256 * 1024;
        public const int MaximumUploadParts = 128;
        public const int MaximumRawBytes = 32 * 1024 * 1024;
        public const int MaximumOriginalItems = 60000;
        public const int MaximumReceiptPageItems = 1000;
        public const int MaximumCompatibilityBytes = 4 * 1024 * 1024;
        public const int MaximumPhasedRawBytes = 512 * 1024 * 1024;
        public const int MaximumPhasedUploadParts = 2048;
        public const int MaximumManifestPageParts = 256;
        public const int MaximumPlanChildren = 1024;
    }

    [DataContract]
    public class PosCatalogImportRecoveryMultipartRequest
    {
        [DataMember(Name = "schemaVersion")] public string SchemaVersion { get; set; } = PosCatalogImportRecoveryMultipartContract.SchemaVersion;
        [DataMember(Name = "deviceToken")] public string DeviceToken { get; set; }
        [DataMember(Name = "sessionToken")] public string SessionToken { get; set; }
        [DataMember(Name = "posSessionId")] public string PosSessionId { get; set; }
        [DataMember(Name = "shopDeviceId")] public string ShopDeviceId { get; set; }
        [DataMember(Name = "shopCode")] public string ShopCode { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportRecoveryUploadRequest : PosCatalogImportRecoveryMultipartRequest
    {
        [DataMember(Name = "uploadId")] public string UploadId { get; set; }
        [DataMember(Name = "mode")] public string Mode { get; set; }
        [DataMember(Name = "originalKind", EmitDefaultValue = false)] public string OriginalKind { get; set; }
        [DataMember(Name = "declaredPayloadHash", EmitDefaultValue = false)] public string DeclaredPayloadHash { get; set; }
        [DataMember(Name = "totalByteLength")] public int TotalByteLength { get; set; }
        [DataMember(Name = "rawSha256")] public string RawSha256 { get; set; }
        [DataMember(Name = "parts")] public PosCatalogImportRecoveryUploadPart[] Parts { get; set; }
        [DataMember(Name = "partIndex")] public int PartIndex { get; set; }
        [DataMember(Name = "contentBase64")] public string ContentBase64 { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportRecoveryUploadPart
    {
        [DataMember(Name = "index")] public int Index { get; set; }
        [DataMember(Name = "byteLength")] public int ByteLength { get; set; }
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
    }

    // Separate shapes leave the compatibility serializer byte-for-byte intact.
    [DataContract]
    public sealed class PosCatalogImportRecoveryManifestRequest : PosCatalogImportRecoveryMultipartRequest
    {
        [DataMember(Name = "phase")] public string Phase { get; set; } = "manifest";
        [DataMember(Name = "uploadId")] public string UploadId { get; set; }
        [DataMember(Name = "mode")] public string Mode { get; set; }
        [DataMember(Name = "originalKind", EmitDefaultValue = false)] public string OriginalKind { get; set; }
        [DataMember(Name = "declaredPayloadHash", EmitDefaultValue = false)] public string DeclaredPayloadHash { get; set; }
        [DataMember(Name = "verifiedOriginalId", EmitDefaultValue = false)] public string VerifiedOriginalId { get; set; }
        [DataMember(Name = "totalByteLength")] public int TotalByteLength { get; set; }
        [DataMember(Name = "rawSha256")] public string RawSha256 { get; set; }
        [DataMember(Name = "partCount")] public int PartCount { get; set; }
        [DataMember(Name = "manifestSha256")] public string ManifestSha256 { get; set; }
        [DataMember(Name = "offset")] public int Offset { get; set; }
        [DataMember(Name = "parts")] public PosCatalogImportRecoveryUploadPart[] Parts { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportRecoverySealRequest : PosCatalogImportRecoveryMultipartRequest
    {
        [DataMember(Name = "phase")] public string Phase { get; set; } = "seal";
        [DataMember(Name = "uploadId")] public string UploadId { get; set; }
        [DataMember(Name = "manifestSha256")] public string ManifestSha256 { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportRecoveryBytesRequest : PosCatalogImportRecoveryMultipartRequest
    {
        [DataMember(Name = "phase")] public string Phase { get; set; } = "bytes";
        [DataMember(Name = "uploadId")] public string UploadId { get; set; }
        [DataMember(Name = "manifestSha256")] public string ManifestSha256 { get; set; }
        [DataMember(Name = "partIndex")] public int PartIndex { get; set; }
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
        [DataMember(Name = "contentBase64")] public string ContentBase64 { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportRecoveryPhaseRequest : PosCatalogImportRecoveryMultipartRequest
    {
        [DataMember(Name = "phase")] public string Phase { get; set; }
        [DataMember(Name = "uploadId")] public string UploadId { get; set; }
        [DataMember(Name = "rawSha256", EmitDefaultValue = false)] public string RawSha256 { get; set; }
        [DataMember(Name = "cursor", EmitDefaultValue = false)] public int? Cursor { get; set; }
        [DataMember(Name = "stage", EmitDefaultValue = false)] public string Stage { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportRecoveryHandleRequest : PosCatalogImportRecoveryMultipartRequest
    {
        [DataMember(Name = "uploadId", EmitDefaultValue = false)] public string UploadId { get; set; }
        [DataMember(Name = "verifiedOriginalId", EmitDefaultValue = false)] public string VerifiedOriginalId { get; set; }
        [DataMember(Name = "planId", EmitDefaultValue = false)] public string PlanId { get; set; }
        [DataMember(Name = "partIndex", EmitDefaultValue = false)] public int? PartIndex { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportRecoveryApplyRequest : PosCatalogImportRecoveryMultipartRequest
    {
        [DataMember(Name = "planId")] public string PlanId { get; set; }
        [DataMember(Name = "partIndex")] public int PartIndex { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportRecoveryReceiptPageRequest : PosCatalogImportRecoveryMultipartRequest
    {
        [DataMember(Name = "verifiedOriginalId", EmitDefaultValue = false)] public string VerifiedOriginalId { get; set; }
        [DataMember(Name = "planId", EmitDefaultValue = false)] public string PlanId { get; set; }
        [DataMember(Name = "partIndex", EmitDefaultValue = false)] public int? PartIndex { get; set; }
        [DataMember(Name = "receiptSha256")] public string ReceiptSha256 { get; set; }
        [DataMember(Name = "offset")] public int Offset { get; set; }
        [DataMember(Name = "limit")] public int Limit { get; set; }
    }

    // Nullable scalar members keep absent protocol evidence distinct from zero
    // and false. An HTTP 200 or one page alone is not a completed receipt.
    [DataContract]
    public sealed class PosCatalogImportRecoveryMultipartResponse
    {
        [DataMember(Name = "ok")] public bool Ok { get; set; }
        [DataMember(Name = "code")] public string Code { get; set; }
        [DataMember(Name = "schemaVersion")] public string SchemaVersion { get; set; }
        [DataMember(Name = "shopId")] public string ShopId { get; set; }
        [DataMember(Name = "shopDeviceId")] public string ShopDeviceId { get; set; }
        [DataMember(Name = "status")] public string Status { get; set; }
        [DataMember(Name = "phase", EmitDefaultValue = false)] public string Phase { get; set; }
        [DataMember(Name = "stage", EmitDefaultValue = false)] public string Stage { get; set; }
        [DataMember(Name = "nextCursor", EmitDefaultValue = false)] public int? NextCursor { get; set; }
        [DataMember(Name = "nextOffset", EmitDefaultValue = false)] public int? NextOffset { get; set; }
        [DataMember(Name = "totalByteLength", EmitDefaultValue = false)] public int? TotalByteLength { get; set; }
        [DataMember(Name = "totalCoverageCount", EmitDefaultValue = false)] public int? TotalCoverageCount { get; set; }
        [DataMember(Name = "reason", EmitDefaultValue = false)] public string Reason { get; set; }
        [DataMember(Name = "uploadId", EmitDefaultValue = false)] public string UploadId { get; set; }
        [DataMember(Name = "partIndex", EmitDefaultValue = false)] public int? PartIndex { get; set; }
        [DataMember(Name = "manifestSha256", EmitDefaultValue = false)] public string ManifestSha256 { get; set; }
        [DataMember(Name = "verifiedOriginalId", EmitDefaultValue = false)] public string VerifiedOriginalId { get; set; }
        [DataMember(Name = "originalSchemaVersion", EmitDefaultValue = false)] public string OriginalSchemaVersion { get; set; }
        [DataMember(Name = "clientImportId", EmitDefaultValue = false)] public string ClientImportId { get; set; }
        [DataMember(Name = "idempotencyKey", EmitDefaultValue = false)] public string IdempotencyKey { get; set; }
        [DataMember(Name = "payloadHash", EmitDefaultValue = false)] public string PayloadHash { get; set; }
        [DataMember(Name = "canonicalPayloadHash", EmitDefaultValue = false)] public string CanonicalPayloadHash { get; set; }
        [DataMember(Name = "rawSha256", EmitDefaultValue = false)] public string RawSha256 { get; set; }
        [DataMember(Name = "itemCount", EmitDefaultValue = false)] public int? ItemCount { get; set; }
        [DataMember(Name = "snapshotOnly", EmitDefaultValue = false)] public bool? SnapshotOnly { get; set; }
        [DataMember(Name = "replacementAllowed", EmitDefaultValue = false)] public bool? ReplacementAllowed { get; set; }
        [DataMember(Name = "oldIdentityBlocked", EmitDefaultValue = false)] public bool? OldIdentityBlocked { get; set; }
        [DataMember(Name = "retiredAt", EmitDefaultValue = false)] public string RetiredAt { get; set; }
        [DataMember(Name = "planId", EmitDefaultValue = false)] public string PlanId { get; set; }
        [DataMember(Name = "planCanonicalHash", EmitDefaultValue = false)] public string PlanCanonicalHash { get; set; }
        [DataMember(Name = "partCount", EmitDefaultValue = false)] public int? PartCount { get; set; }
        [DataMember(Name = "parts", EmitDefaultValue = false)] public PosCatalogImportRecoveryPlannedPart[] Parts { get; set; }
        [DataMember(Name = "parentStatus", EmitDefaultValue = false)] public string ParentStatus { get; set; }
        [DataMember(Name = "acceptedPartCount", EmitDefaultValue = false)] public int? AcceptedPartCount { get; set; }
        [DataMember(Name = "receiptSha256", EmitDefaultValue = false)] public string ReceiptSha256 { get; set; }
        [DataMember(Name = "receiptEncoding", EmitDefaultValue = false)] public string ReceiptEncoding { get; set; }
        [DataMember(Name = "totalItemCount", EmitDefaultValue = false)] public int? TotalItemCount { get; set; }
        [DataMember(Name = "offset", EmitDefaultValue = false)] public int? Offset { get; set; }
        [DataMember(Name = "limit", EmitDefaultValue = false)] public int? Limit { get; set; }
        [DataMember(Name = "complete", EmitDefaultValue = false)] public bool? Complete { get; set; }
        [DataMember(Name = "receipt", EmitDefaultValue = false)] public PosCatalogImportPersistedAck Receipt { get; set; }
        [DataMember(Name = "currentProductSnapshots", EmitDefaultValue = false)] public PosCatalogImportProductSnapshot[] CurrentProductSnapshots { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportRecoveryPlannedPart
    {
        [DataMember(Name = "index")] public int Index { get; set; }
        [DataMember(Name = "kind")] public string Kind { get; set; }
        [DataMember(Name = "clientImportId")] public string ClientImportId { get; set; }
        [DataMember(Name = "idempotencyKey")] public string IdempotencyKey { get; set; }
        [DataMember(Name = "payloadHash")] public string PayloadHash { get; set; }
        [DataMember(Name = "declaredPayloadHash")] public string DeclaredPayloadHash { get; set; }
        [DataMember(Name = "createdAt")] public string CreatedAt { get; set; }
        [DataMember(Name = "itemCount")] public int ItemCount { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportRecoveryPlanDocument
    {
        [DataMember(Name = "schemaVersion")] public string SchemaVersion { get; set; } = PosCatalogImportRecoveryMultipartContract.PlanSchemaVersion;
        [DataMember(Name = "planId")] public string PlanId { get; set; }
        [DataMember(Name = "verifiedOriginalId")] public string VerifiedOriginalId { get; set; }
        [DataMember(Name = "mode")] public string Mode { get; set; }
        [DataMember(Name = "parts")] public PosCatalogImportRecoveryPlanPart[] Parts { get; set; }
        [DataMember(Name = "coverage")] public PosCatalogImportRecoveryCoverage[] Coverage { get; set; }
        [DataMember(Name = "supersedes", EmitDefaultValue = false)] public PosCatalogImportRecoverySupersedes Supersedes { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportRecoveryPlanPart
    {
        [DataMember(Name = "index")] public int Index { get; set; }
        [DataMember(Name = "request")] public PosCatalogImportRecoveryPlanChildRequest Request { get; set; }
    }

    // A typed wire union avoids serializer-specific polymorphic type markers.
    // Ordinary children use the ordinary members; correction children use only
    // recoveryOf/correction. No current credentials enter the immutable plan.
    [DataContract]
    public sealed class PosCatalogImportRecoveryPlanChildRequest
    {
        [DataMember(Name = "schemaVersion")] public string SchemaVersion { get; set; }
        [DataMember(Name = "appVersion", EmitDefaultValue = false)] public string AppVersion { get; set; }
        [DataMember(Name = "source", EmitDefaultValue = false)] public string Source { get; set; }
        [DataMember(Name = "batch", EmitDefaultValue = false)] public PosCatalogImportBatchRequest Batch { get; set; }
        [DataMember(Name = "summary", EmitDefaultValue = false)] public PosCatalogImportSummaryRequest Summary { get; set; }
        [DataMember(Name = "items", EmitDefaultValue = false)] public PosCatalogImportItemRequest[] Items { get; set; }
        [DataMember(Name = "payloadHash", EmitDefaultValue = false)] public string PayloadHash { get; set; }
        [DataMember(Name = "recoveryOf", EmitDefaultValue = false)] public PosCatalogImportRecoveryReference RecoveryOf { get; set; }
        [DataMember(Name = "correction", EmitDefaultValue = false)] public PosCatalogImportCorrectionOperation Correction { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportRecoveryReference
    {
        [DataMember(Name = "verifiedOriginalId")] public string VerifiedOriginalId { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportRecoveryCoverage
    {
        [DataMember(Name = "clientItemId")] public string ClientItemId { get; set; }
        [DataMember(Name = "kind")] public string Kind { get; set; }
        [DataMember(Name = "partIndex", EmitDefaultValue = false)] public int? PartIndex { get; set; }
        [DataMember(Name = "childClientItemId", EmitDefaultValue = false)] public string ChildClientItemId { get; set; }
        [DataMember(Name = "verifiedContributorId", EmitDefaultValue = false)] public string VerifiedContributorId { get; set; }
        [DataMember(Name = "contributorClientItemId", EmitDefaultValue = false)] public string ContributorClientItemId { get; set; }
        [DataMember(Name = "contributorPartIndex", EmitDefaultValue = false)] public int? ContributorPartIndex { get; set; }
        [DataMember(Name = "contributorPlanId", EmitDefaultValue = false)] public string ContributorPlanId { get; set; }
        [DataMember(Name = "desiredItem", EmitDefaultValue = false)] public PosCatalogImportItemRequest DesiredItem { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportRecoverySupersedes
    {
        [DataMember(Name = "planId")] public string PlanId { get; set; }
        [DataMember(Name = "retiredChildren")] public PosCatalogImportRecoveryRetiredChild[] RetiredChildren { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportRecoveryRetiredChild
    {
        [DataMember(Name = "partIndex")] public int PartIndex { get; set; }
        [DataMember(Name = "canonicalPayloadHash")] public string CanonicalPayloadHash { get; set; }
    }
}
