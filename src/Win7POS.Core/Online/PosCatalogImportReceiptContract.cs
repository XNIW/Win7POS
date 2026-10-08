using System.Runtime.Serialization;

namespace Win7POS.Core.Online
{
    public static class PosCatalogImportReceiptContract
    {
        public const string SchemaVersion = "pos-catalog-import-receipt-v1";
        public const string RetirementSchemaVersion = "pos-catalog-import-retirement-v1";
        public const string EndpointPath = "/api/pos/catalog/import-receipt";
        public const string RetirementEndpointPath = "/api/pos/catalog/import-retire";
    }

    [DataContract]
    public sealed class PosCatalogImportReceiptRequest
    {
        [DataMember(Name = "schemaVersion")] public string SchemaVersion { get; set; } = PosCatalogImportReceiptContract.SchemaVersion;
        [DataMember(Name = "clientImportId")] public string ClientImportId { get; set; }
        [DataMember(Name = "idempotencyKey")] public string IdempotencyKey { get; set; }
        [DataMember(Name = "payloadHash")] public string PayloadHash { get; set; }
        [DataMember(Name = "originalRequest")] public PosCatalogImportRequest OriginalRequest { get; set; }
        [DataMember(Name = "deviceToken", EmitDefaultValue = false)] public string DeviceToken { get; set; }
        [DataMember(Name = "sessionToken", EmitDefaultValue = false)] public string SessionToken { get; set; }
        [DataMember(Name = "posSessionId", EmitDefaultValue = false)] public string PosSessionId { get; set; }
        [DataMember(Name = "shopDeviceId", EmitDefaultValue = false)] public string ShopDeviceId { get; set; }
        [DataMember(Name = "shopCode", EmitDefaultValue = false)] public string ShopCode { get; set; }
    }

    // Same authenticated lookup/retirement envelope, with the correction schema
    // retained in originalRequest so the server can fence that exact identity.
    [DataContract]
    public sealed class PosCatalogImportCorrectionReceiptRequest
    {
        [DataMember(Name = "schemaVersion")] public string SchemaVersion { get; set; } = PosCatalogImportReceiptContract.SchemaVersion;
        [DataMember(Name = "clientImportId")] public string ClientImportId { get; set; }
        [DataMember(Name = "idempotencyKey")] public string IdempotencyKey { get; set; }
        [DataMember(Name = "payloadHash")] public string PayloadHash { get; set; }
        [DataMember(Name = "originalRequest")] public PosCatalogImportCorrectionRequest OriginalRequest { get; set; }
        [DataMember(Name = "deviceToken", EmitDefaultValue = false)] public string DeviceToken { get; set; }
        [DataMember(Name = "sessionToken", EmitDefaultValue = false)] public string SessionToken { get; set; }
        [DataMember(Name = "posSessionId", EmitDefaultValue = false)] public string PosSessionId { get; set; }
        [DataMember(Name = "shopDeviceId", EmitDefaultValue = false)] public string ShopDeviceId { get; set; }
        [DataMember(Name = "shopCode", EmitDefaultValue = false)] public string ShopCode { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportReceiptResponse
    {
        [DataMember(Name = "ok")] public bool Ok { get; set; }
        [DataMember(Name = "code")] public string Code { get; set; }
        [DataMember(Name = "schemaVersion")] public string SchemaVersion { get; set; }
        [DataMember(Name = "originalSchemaVersion")] public string OriginalSchemaVersion { get; set; }
        [DataMember(Name = "status")] public string Status { get; set; }
        [DataMember(Name = "shopId")] public string ShopId { get; set; }
        [DataMember(Name = "shopDeviceId")] public string ShopDeviceId { get; set; }
        [DataMember(Name = "clientImportId")] public string ClientImportId { get; set; }
        [DataMember(Name = "idempotencyKey")] public string IdempotencyKey { get; set; }
        [DataMember(Name = "payloadHash")] public string PayloadHash { get; set; }
        [DataMember(Name = "canonicalPayloadHash")] public string CanonicalPayloadHash { get; set; }
        [DataMember(Name = "snapshotOnly", EmitDefaultValue = false)] public bool SnapshotOnly { get; set; }
        [DataMember(Name = "replacementAllowed", EmitDefaultValue = false)] public bool ReplacementAllowed { get; set; }
        [DataMember(Name = "oldIdentityBlocked", EmitDefaultValue = false)] public bool OldIdentityBlocked { get; set; }
        [DataMember(Name = "retiredAt", EmitDefaultValue = false)] public string RetiredAt { get; set; }
        [DataMember(Name = "reason", EmitDefaultValue = false)] public string Reason { get; set; }
        [DataMember(Name = "receipt", EmitDefaultValue = false)] public PosCatalogImportPersistedAck Receipt { get; set; }
        [DataMember(Name = "currentProductSnapshots", EmitDefaultValue = false)] public PosCatalogImportProductSnapshot[] CurrentProductSnapshots { get; set; }
    }

    // The historical server persists its RPC ACK, not the original HTTP response.
    // Do not synthesize shop, attempt, server time or transport metadata here.
    [DataContract]
    public sealed class PosCatalogImportPersistedAck
    {
        [DataMember(Name = "ok")] public bool Ok { get; set; }
        [DataMember(Name = "batchId")] public string BatchId { get; set; }
        [DataMember(Name = "status")] public string Status { get; set; }
        [DataMember(Name = "items")] public PosCatalogImportPersistedItemAck[] Items { get; set; }
        [DataMember(Name = "remoteProductIds")] public PosCatalogImportPersistedProductAck[] RemoteProductIds { get; set; }
        [DataMember(Name = "remotePriceIds")] public PosCatalogImportPersistedPriceAck[] RemotePriceIds { get; set; }
        [DataMember(Name = "summary")] public PosCatalogImportPersistedSummary Summary { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportPersistedItemAck
    {
        [DataMember(Name = "clientItemId")] public string ClientItemId { get; set; }
        [DataMember(Name = "barcode", EmitDefaultValue = false)] public string Barcode { get; set; }
        [DataMember(Name = "remoteProductId")] public string RemoteProductId { get; set; }
        [DataMember(Name = "remotePriceId", EmitDefaultValue = false)] public string RemotePriceId { get; set; }
        [DataMember(Name = "priceType", EmitDefaultValue = false)] public string PriceType { get; set; }
        [DataMember(Name = "status")] public string Status { get; set; }
        [DataMember(Name = "code", EmitDefaultValue = false)] public string Code { get; set; }
        [DataMember(Name = "message", EmitDefaultValue = false)] public string Message { get; set; }
        [DataMember(Name = "authoritativeRevision", EmitDefaultValue = false)] public string AuthoritativeRevision { get; set; }
        [DataMember(Name = "unchangedFields", EmitDefaultValue = false)] public string[] UnchangedFields { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportPersistedProductAck
    {
        [DataMember(Name = "clientItemId")] public string ClientItemId { get; set; }
        [DataMember(Name = "barcode", EmitDefaultValue = false)] public string Barcode { get; set; }
        [DataMember(Name = "remoteProductId")] public string RemoteProductId { get; set; }
        [DataMember(Name = "authoritativeRevision", EmitDefaultValue = false)] public string AuthoritativeRevision { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportPersistedPriceAck
    {
        [DataMember(Name = "clientItemId")] public string ClientItemId { get; set; }
        [DataMember(Name = "barcode", EmitDefaultValue = false)] public string Barcode { get; set; }
        [DataMember(Name = "remoteProductId")] public string RemoteProductId { get; set; }
        [DataMember(Name = "remotePriceId")] public string RemotePriceId { get; set; }
        [DataMember(Name = "priceType")] public string PriceType { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportPersistedSummary
    {
        [DataMember(Name = "acceptedItemCount")] public int AcceptedItemCount { get; set; }
        [DataMember(Name = "duplicateItemCount")] public int DuplicateItemCount { get; set; }
        [DataMember(Name = "productCount")] public int ProductCount { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportProductSnapshot
    {
        [DataMember(Name = "clientItemId")] public string ClientItemId { get; set; }
        [DataMember(Name = "remoteProductId")] public string RemoteProductId { get; set; }
        [DataMember(Name = "snapshotStatus")] public string SnapshotStatus { get; set; }
        [DataMember(Name = "baseRevision")] public string BaseRevision { get; set; }
        [DataMember(Name = "retailPrice")] public decimal? RetailPrice { get; set; }
        [DataMember(Name = "purchasePrice")] public decimal? PurchasePrice { get; set; }
        [DataMember(Name = "stockQuantity")] public decimal? StockQuantity { get; set; }
    }
}
