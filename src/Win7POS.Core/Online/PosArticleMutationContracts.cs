using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.Serialization;

namespace Win7POS.Core.Online
{
    public static class PosArticleMutationContract
    {
        public const string EndpointPath = "/api/pos/catalog/article-mutations";
        public const string SchemaVersion = "pos-article-mutation-v1";
        public const int MaximumBatchCount = 25;
        public const int MaximumEncodedRequestBytes = 256 * 1024;
        public const int MaximumSafeIdLength = 120;
        public const int MaximumSecretLength = 512;
    }

    public static class PosArticleMutationKinds
    {
        public const string ProductCreate = "product_create";
        public const string ProductDuplicate = "product_duplicate";
        public const string ProductUpdate = "product_update";
        public const string ProductActivate = "product_activate";
        public const string ProductDeactivate = "product_deactivate";
        public const string ProductRetailPriceChange = "product_retail_price_change";
        public const string ProductPurchasePriceChange = "product_purchase_price_change";
        public const string ProductManualStockAdjustment = "product_manual_stock_adjustment";
    }

    public static class PosArticleMutationFields
    {
        public const string Barcode = "barcode";
        public const string CategoryId = "categoryId";
        public const string ItemNumber = "itemNumber";
        public const string PrimaryName = "primaryName";
        public const string SecondaryName = "secondaryName";
        public const string SupplierId = "supplierId";
        public const string PurchasePrice = "purchasePrice";
        public const string RetailPrice = "retailPrice";
        public const string StockQuantity = "stockQuantity";
        public const string Price = "price";
        public const string QuantityDelta = "quantityDelta";
        public const string Reason = "reason";
    }

    public sealed class PosArticleMutationEnvelope
    {
        public string AppVersion { get; set; }
        public string ShopId { get; set; }
        public string ShopDeviceId { get; set; }
        public string StaffId { get; set; }
        public int StaffCredentialVersion { get; set; }
        public string PosSessionId { get; set; }
        public string DeviceToken { get; set; }
        public string SessionToken { get; set; }
        public IReadOnlyList<PosArticleMutationRequest> Mutations { get; set; }
    }

    public sealed class PosArticleMutationRequest
    {
        public PosArticleMutationIntent Intent { get; set; }
        public string PayloadHash { get; set; }
        public string AttemptToken { get; set; }
    }

    public sealed class PosArticleMutationIntent
    {
        public PosArticleMutationIntent(
            string baseRevision,
            IDictionary<string, object> changes,
            string clientProductId,
            string createdAt,
            IEnumerable<string> fieldMask,
            string idempotencyKey,
            long localSequence,
            string mutationId,
            string mutationKind,
            string occurredAt,
            string remoteProductId)
        {
            BaseRevision = baseRevision;
            Changes = new ReadOnlyDictionary<string, object>(
                new Dictionary<string, object>(
                    changes ?? new Dictionary<string, object>(),
                    StringComparer.Ordinal));
            ClientProductId = clientProductId;
            CreatedAt = createdAt;
            FieldMask = new ReadOnlyCollection<string>(
                new List<string>(fieldMask ?? Array.Empty<string>()));
            IdempotencyKey = idempotencyKey;
            LocalSequence = localSequence;
            MutationId = mutationId;
            MutationKind = mutationKind;
            OccurredAt = occurredAt;
            RemoteProductId = remoteProductId;
        }

        public string BaseRevision { get; }
        public IReadOnlyDictionary<string, object> Changes { get; }
        public string ClientProductId { get; }
        public string CreatedAt { get; }
        public IReadOnlyList<string> FieldMask { get; }
        public string IdempotencyKey { get; }
        public long LocalSequence { get; }
        public string MutationId { get; }
        public string MutationKind { get; }
        public string OccurredAt { get; }
        public string RemoteProductId { get; }
    }

    [DataContract]
    public sealed class PosArticleMutationResponse
    {
        [DataMember(Name = "code")]
        public string Code { get; set; }

        [DataMember(Name = "ok")]
        public bool Ok { get; set; }

        [DataMember(Name = "results")]
        public PosArticleMutationResult[] Results { get; set; }

        [DataMember(Name = "schemaVersion")]
        public string SchemaVersion { get; set; }

        [DataMember(Name = "serverTime")]
        public string ServerTime { get; set; }
    }

    [DataContract]
    public sealed class PosArticleMutationResult
    {
        [DataMember(Name = "ack")]
        public PosArticleMutationAck Ack { get; set; }

        [DataMember(Name = "deliveryStatus")]
        public string DeliveryStatus { get; set; }
    }

    [DataContract]
    public sealed class PosArticleMutationAck
    {
        [DataMember(Name = "attemptToken")]
        public string AttemptToken { get; set; }

        [DataMember(Name = "authoritativeRevision")]
        public string AuthoritativeRevision { get; set; }

        [DataMember(Name = "catalogRevision")]
        public string CatalogRevision { get; set; }

        [DataMember(Name = "code")]
        public string Code { get; set; }

        [DataMember(Name = "idempotencyKey")]
        public string IdempotencyKey { get; set; }

        [DataMember(Name = "mutationId")]
        public string MutationId { get; set; }

        [DataMember(Name = "payloadHash")]
        public string PayloadHash { get; set; }

        [DataMember(Name = "priceHistoryId")]
        public string PriceHistoryId { get; set; }

        [DataMember(Name = "remoteProductId")]
        public string RemoteProductId { get; set; }

        [DataMember(Name = "retryable")]
        public bool Retryable { get; set; }

        [DataMember(Name = "schemaVersion")]
        public string SchemaVersion { get; set; }

        [DataMember(Name = "serverTimestamp")]
        public string ServerTimestamp { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "stockMovementId")]
        public string StockMovementId { get; set; }

        [DataMember(Name = "terminal")]
        public bool Terminal { get; set; }
    }

    public sealed class PosArticleMutationResponseValidation
    {
        private PosArticleMutationResponseValidation(
            bool valid,
            string code,
            IReadOnlyDictionary<string, PosArticleMutationResult> results)
        {
            IsValid = valid;
            Code = code ?? string.Empty;
            ResultsByMutationId = results ??
                new ReadOnlyDictionary<string, PosArticleMutationResult>(
                    new Dictionary<string, PosArticleMutationResult>());
        }

        public bool IsValid { get; }
        public string Code { get; }
        public IReadOnlyDictionary<string, PosArticleMutationResult> ResultsByMutationId { get; }

        public static PosArticleMutationResponseValidation Invalid(string code)
        {
            return new PosArticleMutationResponseValidation(false, code, null);
        }

        public static PosArticleMutationResponseValidation Valid(
            IDictionary<string, PosArticleMutationResult> results)
        {
            return new PosArticleMutationResponseValidation(
                true,
                "success",
                new ReadOnlyDictionary<string, PosArticleMutationResult>(
                    new Dictionary<string, PosArticleMutationResult>(
                        results,
                        StringComparer.Ordinal)));
        }
    }
}
