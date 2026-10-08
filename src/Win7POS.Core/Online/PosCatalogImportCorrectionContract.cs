using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Win7POS.Core.Online
{
    public static class PosCatalogImportCorrectionContract
    {
        public const string SchemaVersion = "pos-catalog-import-correction-v1";
        public const string EndpointPath = "/api/pos/catalog/import-correction";
    }

    [DataContract]
    public sealed class PosCatalogImportCorrectionRequest
    {
        [DataMember(Name = "schemaVersion")] public string SchemaVersion { get; set; } = PosCatalogImportCorrectionContract.SchemaVersion;
        [DataMember(Name = "deviceToken", EmitDefaultValue = false)] public string DeviceToken { get; set; }
        [DataMember(Name = "sessionToken", EmitDefaultValue = false)] public string SessionToken { get; set; }
        [DataMember(Name = "posSessionId", EmitDefaultValue = false)] public string PosSessionId { get; set; }
        [DataMember(Name = "shopDeviceId", EmitDefaultValue = false)] public string ShopDeviceId { get; set; }
        [DataMember(Name = "shopCode", EmitDefaultValue = false)] public string ShopCode { get; set; }
        [DataMember(Name = "recoveryOf")] public PosCatalogImportRecoveryOf RecoveryOf { get; set; }
        [DataMember(Name = "correction")] public PosCatalogImportCorrectionOperation Correction { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportRecoveryOf
    {
        [DataMember(Name = "clientImportId")] public string ClientImportId { get; set; }
        [DataMember(Name = "idempotencyKey")] public string IdempotencyKey { get; set; }
        [DataMember(Name = "payloadHash")] public string PayloadHash { get; set; }
        [DataMember(Name = "originalRequest")] public PosCatalogImportRequest OriginalRequest { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportCorrectionOperation
    {
        [DataMember(Name = "clientImportId")] public string ClientImportId { get; set; }
        [DataMember(Name = "idempotencyKey")] public string IdempotencyKey { get; set; }
        [DataMember(Name = "payloadHash", EmitDefaultValue = false)] public string PayloadHash { get; set; }
        [DataMember(Name = "createdAt")] public string CreatedAt { get; set; }
        [DataMember(Name = "items")] public PosCatalogImportCorrectionItem[] Items { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportCorrectionItem
    {
        [DataMember(Name = "clientItemId")] public string ClientItemId { get; set; }
        [DataMember(Name = "remoteProductId")] public string RemoteProductId { get; set; }
        [DataMember(Name = "baseRevision")] public string BaseRevision { get; set; }
        [DataMember(Name = "baseSnapshot")] public PosCatalogImportCorrectionBaseSnapshot BaseSnapshot { get; set; }
        [DataMember(Name = "fieldMask")] public string[] FieldMask { get; set; }
        [DataMember(Name = "changes")] public PosCatalogImportCorrectionChanges Changes { get; set; }
    }

    [CollectionDataContract]
    public sealed class PosCatalogImportCorrectionBaseSnapshot : Dictionary<string, decimal?>
    {
        // Dictionary presence distinguishes a masked null from an unmasked field.
        public decimal? RetailPrice { get => Read("retailPrice"); set => this["retailPrice"] = value; }
        public decimal? PurchasePrice { get => Read("purchasePrice"); set => this["purchasePrice"] = value; }
        public decimal? StockQuantity { get => Read("stockQuantity"); set => this["stockQuantity"] = value; }
        private decimal? Read(string field) => TryGetValue(field, out var value) ? value : null;
    }

    [DataContract]
    public sealed class PosCatalogImportCorrectionChanges
    {
        [DataMember(Name = "retailPrice", EmitDefaultValue = false)] public decimal? RetailPrice { get; set; }
        [DataMember(Name = "purchasePrice", EmitDefaultValue = false)] public decimal? PurchasePrice { get; set; }
        [DataMember(Name = "quantityDelta", EmitDefaultValue = false)] public decimal? QuantityDelta { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportCorrectionResponse
    {
        [DataMember(Name = "ok")] public bool Ok { get; set; }
        [DataMember(Name = "code")] public string Code { get; set; }
        [DataMember(Name = "schemaVersion")] public string SchemaVersion { get; set; }
        [DataMember(Name = "status")] public string Status { get; set; }
        [DataMember(Name = "shopId")] public string ShopId { get; set; }
        [DataMember(Name = "shopDeviceId")] public string ShopDeviceId { get; set; }
        [DataMember(Name = "clientImportId")] public string ClientImportId { get; set; }
        [DataMember(Name = "idempotencyKey")] public string IdempotencyKey { get; set; }
        [DataMember(Name = "payloadHash")] public string PayloadHash { get; set; }
        [DataMember(Name = "canonicalPayloadHash")] public string CanonicalPayloadHash { get; set; }
        [DataMember(Name = "reason", EmitDefaultValue = false)] public string Reason { get; set; }
        [DataMember(Name = "receipt", EmitDefaultValue = false)] public PosCatalogImportPersistedAck Receipt { get; set; }
    }
}
