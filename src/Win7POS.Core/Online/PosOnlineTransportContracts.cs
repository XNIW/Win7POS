using System.Runtime.Serialization;

namespace Win7POS.Core.Online
{
    public sealed class PosOnlineResult<T> where T : class
    {
        private PosOnlineResult(
            bool success,
            T value,
            string code,
            string message,
            bool denied,
            string clientRequestId,
            string serverRequestId,
            string cfRay)
        {
            Success = success;
            Value = value;
            Code = code;
            Message = message;
            Denied = denied;
            ClientRequestId = clientRequestId;
            ServerRequestId = serverRequestId;
            CfRay = cfRay;
        }

        public string CfRay { get; }
        public string ClientRequestId { get; }
        public bool Denied { get; }
        public string Code { get; }
        public string Message { get; }
        public string ServerRequestId { get; }
        public bool Success { get; }
        public T Value { get; }

        public static PosOnlineResult<T> Ok(
            T value,
            string clientRequestId = null,
            string serverRequestId = null,
            string cfRay = null)
        {
            return new PosOnlineResult<T>(
                true,
                value,
                "success",
                "",
                false,
                clientRequestId,
                serverRequestId,
                cfRay);
        }

        public static PosOnlineResult<T> Failure(
            string code,
            string message,
            bool denied,
            string clientRequestId = null,
            string serverRequestId = null,
            string cfRay = null)
        {
            return new PosOnlineResult<T>(
                false,
                null,
                code ?? "failure",
                message ?? "Errore POS online.",
                denied,
                clientRequestId,
                serverRequestId,
                cfRay);
        }
    }

    [DataContract]
    public sealed class PosFirstLoginRequest
    {
        [DataMember(Name = "credential")]
        public string Credential { get; set; }

        [DataMember(Name = "device")]
        public PosFirstLoginDevice Device { get; set; }

        [DataMember(Name = "shopCode")]
        public string ShopCode { get; set; }

        [DataMember(Name = "staffCode")]
        public string StaffCode { get; set; }
    }

    [DataContract]
    public sealed class PosFirstLoginDevice
    {
        [DataMember(Name = "appVersion", EmitDefaultValue = false)]
        public string AppVersion { get; set; }

        [DataMember(Name = "deviceIdentifier")]
        public string DeviceIdentifier { get; set; }

        [DataMember(Name = "displayName")]
        public string DisplayName { get; set; }
    }

    [DataContract]
    public sealed class PosHeartbeatRequest
    {
        [DataMember(Name = "appVersion", EmitDefaultValue = false)]
        public string AppVersion { get; set; }

        [DataMember(Name = "deviceToken")]
        public string DeviceToken { get; set; }

        [DataMember(Name = "posSessionId")]
        public string PosSessionId { get; set; }

        [DataMember(Name = "sessionToken")]
        public string SessionToken { get; set; }

        [DataMember(Name = "shopDeviceId")]
        public string ShopDeviceId { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogPullRequest
    {
        [DataMember(Name = "appVersion", EmitDefaultValue = false)]
        public string AppVersion { get; set; }

        [DataMember(Name = "deviceToken")]
        public string DeviceToken { get; set; }

        [DataMember(Name = "posSessionId")]
        public string PosSessionId { get; set; }

        [DataMember(Name = "sessionToken")]
        public string SessionToken { get; set; }

        [DataMember(Name = "shopDeviceId")]
        public string ShopDeviceId { get; set; }

        [DataMember(Name = "limit", EmitDefaultValue = false)]
        public int Limit { get; set; }

        [DataMember(Name = "syncCursor", EmitDefaultValue = false)]
        public string SyncCursor { get; set; }

        [DataMember(Name = "updated_since", EmitDefaultValue = false)]
        public string UpdatedSince { get; set; }
    }

    [DataContract]
    public sealed class PosSalesSyncRequest
    {
        [DataMember(Name = "appVersion", EmitDefaultValue = false)]
        public string AppVersion { get; set; }

        [DataMember(Name = "batch")]
        public PosSalesSyncBatchRequest Batch { get; set; }

        [DataMember(Name = "deviceToken")]
        public string DeviceToken { get; set; }

        [DataMember(Name = "posSessionId")]
        public string PosSessionId { get; set; }

        [DataMember(Name = "sales")]
        public PosSalesSyncSaleRequest[] Sales { get; set; }

        [DataMember(Name = "schemaVersion")]
        public string SchemaVersion { get; set; }

        [DataMember(Name = "sessionToken")]
        public string SessionToken { get; set; }

        [DataMember(Name = "shopCode", EmitDefaultValue = false)]
        public string ShopCode { get; set; }

        [DataMember(Name = "shopDeviceId")]
        public string ShopDeviceId { get; set; }
    }

    [DataContract]
    public sealed class PosSalesSyncBatchRequest
    {
        [DataMember(Name = "clientBatchId")]
        public string ClientBatchId { get; set; }

        [DataMember(Name = "idempotencyKey")]
        public string IdempotencyKey { get; set; }
    }

    [DataContract]
    public sealed class PosSalesSyncSaleRequest
    {
        [DataMember(Name = "amounts")]
        public PosSalesSyncAmounts Amounts { get; set; }

        [DataMember(Name = "businessDate")]
        public string BusinessDate { get; set; }

        [DataMember(Name = "clientOriginalSaleId", EmitDefaultValue = false)]
        public string ClientOriginalSaleId { get; set; }

        [DataMember(Name = "clientSaleId")]
        public string ClientSaleId { get; set; }

        [DataMember(Name = "currency")]
        public string Currency { get; set; }

        [DataMember(Name = "fiscal")]
        public PosSalesSyncFiscal Fiscal { get; set; }

        [DataMember(Name = "idempotencyKey")]
        public string IdempotencyKey { get; set; }

        [DataMember(Name = "kind")]
        public string Kind { get; set; }

        [DataMember(Name = "lines")]
        public PosSalesSyncLine[] Lines { get; set; }

        [DataMember(Name = "occurredAt")]
        public string OccurredAt { get; set; }

        [DataMember(Name = "payments")]
        public PosSalesSyncPayment[] Payments { get; set; }

        [DataMember(Name = "reversalReason", EmitDefaultValue = false)]
        public string ReversalReason { get; set; }

        [DataMember(Name = "saleNumber")]
        public string SaleNumber { get; set; }
    }

    [DataContract]
    public sealed class PosSalesSyncAmounts
    {
        [DataMember(Name = "changeClp")]
        public long ChangeClp { get; set; }

        [DataMember(Name = "discountClp")]
        public long DiscountClp { get; set; }

        [DataMember(Name = "grossClp")]
        public long GrossClp { get; set; }

        [DataMember(Name = "netClp")]
        public long NetClp { get; set; }

        [DataMember(Name = "paidClp")]
        public long PaidClp { get; set; }

        [DataMember(Name = "taxClp")]
        public long TaxClp { get; set; }
    }

    [DataContract]
    public sealed class PosSalesSyncLine
    {
        [DataMember(Name = "amountClp")]
        public long AmountClp { get; set; }

        [DataMember(Name = "barcode", EmitDefaultValue = false)]
        public string Barcode { get; set; }

        [DataMember(Name = "clientLineId")]
        public string ClientLineId { get; set; }

        [DataMember(Name = "clientOriginalLineId", EmitDefaultValue = false)]
        public string ClientOriginalLineId { get; set; }

        [DataMember(Name = "linePosition")]
        public int LinePosition { get; set; }

        [DataMember(Name = "lineType")]
        public string LineType { get; set; }

        [DataMember(Name = "localProductId", EmitDefaultValue = false)]
        public string LocalProductId { get; set; }

        [DataMember(Name = "productId", EmitDefaultValue = false)]
        public string ProductId { get; set; }

        [DataMember(Name = "productName", EmitDefaultValue = false)]
        public string ProductName { get; set; }

        [DataMember(Name = "quantity")]
        public int Quantity { get; set; }

        [DataMember(Name = "stockQuantityDelta")]
        public int StockQuantityDelta { get; set; }

        [DataMember(Name = "unitAmountClp")]
        public long UnitAmountClp { get; set; }
    }

    [DataContract]
    public sealed class PosSalesSyncPayment
    {
        [DataMember(Name = "amountClp")]
        public long AmountClp { get; set; }

        [DataMember(Name = "changeClp")]
        public long ChangeClp { get; set; }

        [DataMember(Name = "clientPaymentId")]
        public string ClientPaymentId { get; set; }

        [DataMember(Name = "method")]
        public string Method { get; set; }
    }

    [DataContract]
    public sealed class PosSalesSyncFiscal
    {
        [DataMember(Name = "documentNumber", EmitDefaultValue = false)]
        public string DocumentNumber { get; set; }

        [DataMember(Name = "documentType")]
        public string DocumentType { get; set; }

        [DataMember(Name = "printedAt")]
        public string PrintedAt { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }
    }

    [DataContract]
    public sealed class PosFirstLoginResponse
    {
        [DataMember(Name = "device")]
        public PosTrustedDeviceResponse Device { get; set; }

        [DataMember(Name = "ok")]
        public bool Ok { get; set; }

        [DataMember(Name = "policy", EmitDefaultValue = false)]
        public PosPolicyResponse Policy { get; set; }

        [DataMember(Name = "serverTime")]
        public string ServerTime { get; set; }

        [DataMember(Name = "session")]
        public PosSessionResponse Session { get; set; }

        [DataMember(Name = "shop")]
        public PosShopResponse Shop { get; set; }

        [DataMember(Name = "staff")]
        public PosStaffResponse Staff { get; set; }

        [DataMember(Name = "trustedDeviceToken")]
        public string TrustedDeviceToken { get; set; }
    }

    [DataContract]
    public sealed class PosHeartbeatResponse
    {
        [DataMember(Name = "ok")]
        public bool Ok { get; set; }

        [DataMember(Name = "serverTime")]
        public string ServerTime { get; set; }

        [DataMember(Name = "session")]
        public PosSessionResponse Session { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogPullResponse
    {
        [DataMember(Name = "catalog")]
        public PosCatalogPayload Catalog { get; set; }

        [DataMember(Name = "catalogSummary", EmitDefaultValue = false)]
        public PosCatalogSummaryResponse CatalogSummary { get; set; }

        [DataMember(Name = "catalogVersion")]
        public string CatalogVersion { get; set; }

        [DataMember(Name = "code")]
        public string Code { get; set; }

        [DataMember(Name = "generatedAt")]
        public string GeneratedAt { get; set; }

        [DataMember(Name = "hasMore")]
        public bool HasMore { get; set; }

        [DataMember(Name = "ok")]
        public bool Ok { get; set; }

        [DataMember(Name = "policy", EmitDefaultValue = false)]
        public PosPolicyResponse Policy { get; set; }

        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "serverTime")]
        public string ServerTime { get; set; }

        [DataMember(Name = "shop")]
        public PosShopResponse Shop { get; set; }

        [DataMember(Name = "syncCursor")]
        public string SyncCursor { get; set; }

        [DataMember(Name = "syncMode")]
        public string SyncMode { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogSummaryResponse
    {
        [DataMember(Name = "activeProducts", EmitDefaultValue = false)]
        public long? ActiveProducts { get; set; }

        [DataMember(Name = "categories", EmitDefaultValue = false)]
        public long? Categories { get; set; }

        [DataMember(Name = "checksum", EmitDefaultValue = false)]
        public string Checksum { get; set; }

        [DataMember(Name = "checksumAlgorithm", EmitDefaultValue = false)]
        public string ChecksumAlgorithm { get; set; }

        [DataMember(Name = "prices", EmitDefaultValue = false)]
        public long? Prices { get; set; }

        [DataMember(Name = "products", EmitDefaultValue = false)]
        public long? Products { get; set; }

        [DataMember(Name = "suppliers", EmitDefaultValue = false)]
        public long? Suppliers { get; set; }
    }

    [DataContract]
    public sealed class PosPolicyResponse
    {
        [DataMember(Name = "capabilities", EmitDefaultValue = false)]
        public PosPolicyCapabilitiesResponse Capabilities { get; set; }

        [DataMember(Name = "contractVersion", EmitDefaultValue = false)]
        public string ContractVersion { get; set; }

        [DataMember(Name = "limitations", EmitDefaultValue = false)]
        public string[] Limitations { get; set; }

        [DataMember(Name = "offlinePolicy", EmitDefaultValue = false)]
        public PosOfflinePolicyResponse OfflinePolicy { get; set; }

        [DataMember(Name = "paymentPolicy", EmitDefaultValue = false)]
        public PosPaymentPolicyResponse PaymentPolicy { get; set; }

        [DataMember(Name = "staffPolicy", EmitDefaultValue = false)]
        public PosStaffPolicyResponse StaffPolicy { get; set; }

        [DataMember(Name = "taxPolicy", EmitDefaultValue = false)]
        public PosTaxPolicyResponse TaxPolicy { get; set; }
    }

    [DataContract]
    public sealed class PosPolicyCapabilitiesResponse
    {
        [DataMember(Name = "catalogPull", EmitDefaultValue = false)]
        public string CatalogPull { get; set; }

        [DataMember(Name = "fiscalDocumentMode", EmitDefaultValue = false)]
        public string FiscalDocumentMode { get; set; }

        [DataMember(Name = "localReceiptPrinting", EmitDefaultValue = false)]
        public bool LocalReceiptPrinting { get; set; }

        [DataMember(Name = "localStaffMirror", EmitDefaultValue = false)]
        public string LocalStaffMirror { get; set; }

        [DataMember(Name = "offlineSales", EmitDefaultValue = false)]
        public bool OfflineSales { get; set; }

        [DataMember(Name = "paymentMethods", EmitDefaultValue = false)]
        public string[] PaymentMethods { get; set; }

        [DataMember(Name = "salesSync", EmitDefaultValue = false)]
        public string SalesSync { get; set; }
    }

    [DataContract]
    public sealed class PosOfflinePolicyResponse
    {
        [DataMember(Name = "firstActivationRequiresOnline", EmitDefaultValue = false)]
        public bool FirstActivationRequiresOnline { get; set; }

        [DataMember(Name = "mode", EmitDefaultValue = false)]
        public string Mode { get; set; }

        [DataMember(Name = "pendingSalesRetention", EmitDefaultValue = false)]
        public string PendingSalesRetention { get; set; }

        [DataMember(Name = "revocationEnforcement", EmitDefaultValue = false)]
        public string RevocationEnforcement { get; set; }
    }

    [DataContract]
    public sealed class PosPaymentPolicyResponse
    {
        [DataMember(Name = "currency", EmitDefaultValue = false)]
        public string Currency { get; set; }

        [DataMember(Name = "fallbackMethod", EmitDefaultValue = false)]
        public string FallbackMethod { get; set; }

        [DataMember(Name = "supportedMethods", EmitDefaultValue = false)]
        public string[] SupportedMethods { get; set; }

        [DataMember(Name = "unsupportedMethods", EmitDefaultValue = false)]
        public string[] UnsupportedMethods { get; set; }
    }

    [DataContract]
    public sealed class PosStaffPolicyResponse
    {
        [DataMember(Name = "credentialMaterial", EmitDefaultValue = false)]
        public string CredentialMaterial { get; set; }

        [DataMember(Name = "mustChangeCredential", EmitDefaultValue = false)]
        public string MustChangeCredential { get; set; }

        [DataMember(Name = "offlineMirror", EmitDefaultValue = false)]
        public string OfflineMirror { get; set; }
    }

    [DataContract]
    public sealed class PosTaxPolicyResponse
    {
        [DataMember(Name = "defaultTaxClp", EmitDefaultValue = false)]
        public int DefaultTaxClp { get; set; }

        [DataMember(Name = "fiscalAuthorityIntegration", EmitDefaultValue = false)]
        public string FiscalAuthorityIntegration { get; set; }

        [DataMember(Name = "status", EmitDefaultValue = false)]
        public string Status { get; set; }
    }

    [DataContract]
    public sealed class PosSalesSyncResponse
    {
        [DataMember(Name = "batch")]
        public PosSalesSyncBatchResponse Batch { get; set; }

        [DataMember(Name = "code")]
        public string Code { get; set; }

        [DataMember(Name = "ok")]
        public bool Ok { get; set; }

        [DataMember(Name = "sales")]
        public PosSalesSyncSaleAck[] Sales { get; set; }

        [DataMember(Name = "serverTime")]
        public string ServerTime { get; set; }

        [DataMember(Name = "shop")]
        public PosShopResponse Shop { get; set; }
    }

    [DataContract]
    public sealed class PosSalesSyncBatchResponse
    {
        [DataMember(Name = "clientBatchId")]
        public string ClientBatchId { get; set; }

        [DataMember(Name = "posSalesSyncBatchId")]
        public string PosSalesSyncBatchId { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }
    }

    [DataContract]
    public sealed class PosSalesSyncSaleAck
    {
        [DataMember(Name = "clientSaleId")]
        public string ClientSaleId { get; set; }

        [DataMember(Name = "posSaleId")]
        public string PosSaleId { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogPayload
    {
        [DataMember(Name = "categories")]
        public PosCatalogCategoryResponse[] Categories { get; set; }

        [DataMember(Name = "prices")]
        public PosCatalogPriceResponse[] Prices { get; set; }

        [DataMember(Name = "products")]
        public PosCatalogProductResponse[] Products { get; set; }

        [DataMember(Name = "suppliers")]
        public PosCatalogSupplierResponse[] Suppliers { get; set; }

        [DataMember(Name = "tombstones")]
        public PosCatalogTombstonesResponse Tombstones { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogTombstonesResponse
    {
        [DataMember(Name = "categories")]
        public PosCatalogCategoryTombstoneResponse[] Categories { get; set; }

        [DataMember(Name = "products")]
        public PosCatalogProductTombstoneResponse[] Products { get; set; }

        [DataMember(Name = "suppliers")]
        public PosCatalogSupplierTombstoneResponse[] Suppliers { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogProductTombstoneResponse
    {
        [DataMember(Name = "deletedAt")]
        public string DeletedAt { get; set; }

        [DataMember(Name = "productId")]
        public string ProductId { get; set; }

        [DataMember(Name = "updatedAt")]
        public string UpdatedAt { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogCategoryTombstoneResponse
    {
        [DataMember(Name = "categoryId")]
        public string CategoryId { get; set; }

        [DataMember(Name = "deletedAt")]
        public string DeletedAt { get; set; }

        [DataMember(Name = "updatedAt")]
        public string UpdatedAt { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogSupplierTombstoneResponse
    {
        [DataMember(Name = "deletedAt")]
        public string DeletedAt { get; set; }

        [DataMember(Name = "supplierId")]
        public string SupplierId { get; set; }

        [DataMember(Name = "updatedAt")]
        public string UpdatedAt { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogProductResponse
    {
        [DataMember(Name = "barcode")]
        public string Barcode { get; set; }

        [DataMember(Name = "categoryId")]
        public string CategoryId { get; set; }

        [DataMember(Name = "itemNumber")]
        public string ItemNumber { get; set; }

        [DataMember(Name = "productId")]
        public string ProductId { get; set; }

        [DataMember(Name = "productName")]
        public string ProductName { get; set; }

        [DataMember(Name = "purchasePrice")]
        public double? PurchasePrice { get; set; }

        [DataMember(Name = "retailPrice")]
        public double? RetailPrice { get; set; }

        [DataMember(Name = "secondProductName")]
        public string SecondProductName { get; set; }

        [DataMember(Name = "stockQuantity")]
        public double? StockQuantity { get; set; }

        [DataMember(Name = "supplierId")]
        public string SupplierId { get; set; }

        [DataMember(Name = "updatedAt")]
        public string UpdatedAt { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogCategoryResponse
    {
        [DataMember(Name = "categoryId")]
        public string CategoryId { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "updatedAt")]
        public string UpdatedAt { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogSupplierResponse
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "supplierId")]
        public string SupplierId { get; set; }

        [DataMember(Name = "updatedAt")]
        public string UpdatedAt { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogPriceResponse
    {
        [DataMember(Name = "effectiveAt")]
        public string EffectiveAt { get; set; }

        [DataMember(Name = "price")]
        public double Price { get; set; }

        [DataMember(Name = "priceId")]
        public string PriceId { get; set; }

        [DataMember(Name = "productId")]
        public string ProductId { get; set; }

        [DataMember(Name = "source")]
        public string Source { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }
    }

    [DataContract]
    public sealed class PosTrustedDeviceResponse
    {
        [DataMember(Name = "shopDeviceId")]
        public string ShopDeviceId { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "trusted")]
        public bool Trusted { get; set; }
    }

    [DataContract]
    public sealed class PosSessionResponse
    {
        [DataMember(Name = "expiresAt")]
        public string ExpiresAt { get; set; }

        [DataMember(Name = "heartbeatAfterSeconds")]
        public int HeartbeatAfterSeconds { get; set; }

        [DataMember(Name = "posSessionId")]
        public string PosSessionId { get; set; }

        [DataMember(Name = "sessionToken", EmitDefaultValue = false)]
        public string SessionToken { get; set; }
    }

    [DataContract]
    public sealed class PosShopResponse
    {
        [DataMember(Name = "businessAddress", EmitDefaultValue = false)]
        public string BusinessAddress { get; set; }

        [DataMember(Name = "businessCity", EmitDefaultValue = false)]
        public string BusinessCity { get; set; }

        [DataMember(Name = "businessGiro", EmitDefaultValue = false)]
        public string BusinessGiro { get; set; }

        [DataMember(Name = "companyRut", EmitDefaultValue = false)]
        public string CompanyRut { get; set; }

        [DataMember(Name = "fiscalIdentityLockedByPlatform", EmitDefaultValue = false)]
        public bool FiscalIdentityLockedByPlatform { get; set; }

        [DataMember(Name = "legalRepresentativeRut", EmitDefaultValue = false)]
        public string LegalRepresentativeRut { get; set; }

        [DataMember(Name = "shopCode")]
        public string ShopCode { get; set; }

        [DataMember(Name = "shopId")]
        public string ShopId { get; set; }

        [DataMember(Name = "shopName")]
        public string ShopName { get; set; }

        [DataMember(Name = "shopStatus", EmitDefaultValue = false)]
        public string ShopStatus { get; set; }

        [DataMember(Name = "source", EmitDefaultValue = false)]
        public string Source { get; set; }

        [DataMember(Name = "updatedAt", EmitDefaultValue = false)]
        public string UpdatedAt { get; set; }
    }

    [DataContract]
    public sealed class PosStaffResponse
    {
        [DataMember(Name = "credentialVersion")]
        public int CredentialVersion { get; set; }

        [DataMember(Name = "displayName")]
        public string DisplayName { get; set; }

        [DataMember(Name = "roleKey")]
        public string RoleKey { get; set; }

        [DataMember(Name = "staffCode")]
        public string StaffCode { get; set; }

        [DataMember(Name = "staffId")]
        public string StaffId { get; set; }
    }

    [DataContract]
    public sealed class PosErrorResponse
    {
        [DataMember(Name = "clientRequestId", EmitDefaultValue = false)]
        public string ClientRequestId { get; set; }

        [DataMember(Name = "code")]
        public string Code { get; set; }

        [DataMember(Name = "message")]
        public string Message { get; set; }

        [DataMember(Name = "ok")]
        public bool Ok { get; set; }

        [DataMember(Name = "requestId", EmitDefaultValue = false)]
        public string RequestId { get; set; }
    }
}
