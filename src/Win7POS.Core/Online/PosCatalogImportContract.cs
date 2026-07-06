using System.Runtime.Serialization;

namespace Win7POS.Core.Online
{
    [DataContract]
    public sealed class PosCatalogImportRequest
    {
        [DataMember(Name = "appVersion", EmitDefaultValue = false)]
        public string AppVersion { get; set; }

        [DataMember(Name = "batch")]
        public PosCatalogImportBatchRequest Batch { get; set; }

        [DataMember(Name = "deviceToken", EmitDefaultValue = false)]
        public string DeviceToken { get; set; }

        [DataMember(Name = "items")]
        public PosCatalogImportItemRequest[] Items { get; set; }

        [DataMember(Name = "posSessionId", EmitDefaultValue = false)]
        public string PosSessionId { get; set; }

        [DataMember(Name = "payloadHash", EmitDefaultValue = false)]
        public string PayloadHash { get; set; }

        [DataMember(Name = "schemaVersion")]
        public string SchemaVersion { get; set; }

        [DataMember(Name = "sessionToken", EmitDefaultValue = false)]
        public string SessionToken { get; set; }

        [DataMember(Name = "shopCode", EmitDefaultValue = false)]
        public string ShopCode { get; set; }

        [DataMember(Name = "shopDeviceId", EmitDefaultValue = false)]
        public string ShopDeviceId { get; set; }

        [DataMember(Name = "source")]
        public string Source { get; set; }

        [DataMember(Name = "summary")]
        public PosCatalogImportSummaryRequest Summary { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportBatchRequest
    {
        [DataMember(Name = "attemptCount", EmitDefaultValue = false)]
        public int AttemptCount { get; set; }

        [DataMember(Name = "clientImportId")]
        public string ClientImportId { get; set; }

        [DataMember(Name = "createdAt")]
        public string CreatedAt { get; set; }

        [DataMember(Name = "idempotencyKey")]
        public string IdempotencyKey { get; set; }

        [DataMember(Name = "previewFingerprint")]
        public string PreviewFingerprint { get; set; }

        [DataMember(Name = "sourceFileName", EmitDefaultValue = false)]
        public string SourceFileName { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportSummaryRequest
    {
        [DataMember(Name = "newProducts")]
        public int NewProducts { get; set; }

        [DataMember(Name = "noChangeRows")]
        public int NoChangeRows { get; set; }

        [DataMember(Name = "skippedRows")]
        public int SkippedRows { get; set; }

        [DataMember(Name = "updatedProducts")]
        public int UpdatedProducts { get; set; }

        [DataMember(Name = "warningCount")]
        public int WarningCount { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportItemRequest
    {
        [DataMember(Name = "barcode")]
        public string Barcode { get; set; }

        [DataMember(Name = "category", EmitDefaultValue = false)]
        public string Category { get; set; }

        [DataMember(Name = "changeKind")]
        public string ChangeKind { get; set; }

        [DataMember(Name = "clientItemId")]
        public string ClientItemId { get; set; }

        [DataMember(Name = "diffSummary", EmitDefaultValue = false)]
        public string DiffSummary { get; set; }

        [DataMember(Name = "itemNumber", EmitDefaultValue = false)]
        public string ItemNumber { get; set; }

        [DataMember(Name = "operation")]
        public string Operation { get; set; }

        [DataMember(Name = "productName", EmitDefaultValue = false)]
        public string ProductName { get; set; }

        [DataMember(Name = "purchasePrice", EmitDefaultValue = false)]
        public string PurchasePrice { get; set; }

        [DataMember(Name = "quantity", EmitDefaultValue = false)]
        public string Quantity { get; set; }

        [DataMember(Name = "retailPrice", EmitDefaultValue = false)]
        public string RetailPrice { get; set; }

        [DataMember(Name = "rowNumber")]
        public int RowNumber { get; set; }

        [DataMember(Name = "secondProductName", EmitDefaultValue = false)]
        public string SecondProductName { get; set; }

        [DataMember(Name = "supplier", EmitDefaultValue = false)]
        public string Supplier { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportResponse
    {
        [DataMember(Name = "batch")]
        public PosCatalogImportBatchResponse Batch { get; set; }

        [DataMember(Name = "code")]
        public string Code { get; set; }

        [DataMember(Name = "items")]
        public PosCatalogImportItemAck[] Items { get; set; }

        [DataMember(Name = "ok")]
        public bool Ok { get; set; }

        [DataMember(Name = "remotePriceIds", EmitDefaultValue = false)]
        public PosCatalogImportRemotePriceIdAck[] RemotePriceIds { get; set; }

        [DataMember(Name = "remoteProductIds", EmitDefaultValue = false)]
        public PosCatalogImportRemoteProductIdAck[] RemoteProductIds { get; set; }

        [DataMember(Name = "serverImportId", EmitDefaultValue = false)]
        public string ServerImportId { get; set; }

        [DataMember(Name = "serverRequestId", EmitDefaultValue = false)]
        public string ServerRequestId { get; set; }

        [DataMember(Name = "serverTime")]
        public string ServerTime { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportBatchResponse
    {
        [DataMember(Name = "attemptCount", EmitDefaultValue = false)]
        public int AttemptCount { get; set; }

        [DataMember(Name = "clientImportId")]
        public string ClientImportId { get; set; }

        [DataMember(Name = "idempotencyKey", EmitDefaultValue = false)]
        public string IdempotencyKey { get; set; }

        [DataMember(Name = "payloadHash", EmitDefaultValue = false)]
        public string PayloadHash { get; set; }

        [DataMember(Name = "posCatalogImportBatchId")]
        public string PosCatalogImportBatchId { get; set; }

        [DataMember(Name = "serverImportId", EmitDefaultValue = false)]
        public string ServerImportId { get; set; }

        [DataMember(Name = "serverRequestId", EmitDefaultValue = false)]
        public string ServerRequestId { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportItemAck
    {
        [DataMember(Name = "barcode", EmitDefaultValue = false)]
        public string Barcode { get; set; }

        [DataMember(Name = "clientItemId")]
        public string ClientItemId { get; set; }

        [DataMember(Name = "code", EmitDefaultValue = false)]
        public string Code { get; set; }

        [DataMember(Name = "message", EmitDefaultValue = false)]
        public string Message { get; set; }

        [DataMember(Name = "priceType", EmitDefaultValue = false)]
        public string PriceType { get; set; }

        [DataMember(Name = "remotePriceId", EmitDefaultValue = false)]
        public string RemotePriceId { get; set; }

        [DataMember(Name = "remoteProductId", EmitDefaultValue = false)]
        public string RemoteProductId { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportRemoteProductIdAck
    {
        [DataMember(Name = "barcode", EmitDefaultValue = false)]
        public string Barcode { get; set; }

        [DataMember(Name = "clientItemId", EmitDefaultValue = false)]
        public string ClientItemId { get; set; }

        [DataMember(Name = "remoteProductId")]
        public string RemoteProductId { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogImportRemotePriceIdAck
    {
        [DataMember(Name = "barcode", EmitDefaultValue = false)]
        public string Barcode { get; set; }

        [DataMember(Name = "clientItemId", EmitDefaultValue = false)]
        public string ClientItemId { get; set; }

        [DataMember(Name = "priceType", EmitDefaultValue = false)]
        public string PriceType { get; set; }

        [DataMember(Name = "remotePriceId")]
        public string RemotePriceId { get; set; }
    }
}
