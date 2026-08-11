using System.Runtime.Serialization;

namespace Win7POS.Core.Online
{
    [DataContract]
    public sealed class PosCustomerOrderClaimRequest
    {
        [DataMember(Name = "appVersion", EmitDefaultValue = false)]
        public string AppVersion { get; set; }

        [DataMember(Name = "deviceToken")]
        public string DeviceToken { get; set; }

        [DataMember(Name = "limit")]
        public int Limit { get; set; }

        [DataMember(Name = "posSessionId")]
        public string PosSessionId { get; set; }

        [DataMember(Name = "schemaVersion")]
        public string SchemaVersion { get; set; }

        [DataMember(Name = "sessionToken")]
        public string SessionToken { get; set; }

        [DataMember(Name = "shopDeviceId")]
        public string ShopDeviceId { get; set; }
    }

    [DataContract]
    public sealed class PosCustomerOrderClaimResponse
    {
        [DataMember(Name = "code")]
        public string Code { get; set; }

        [DataMember(Name = "handoffs")]
        public PosCustomerOrderHandoff[] Handoffs { get; set; }

        [DataMember(Name = "ok")]
        public bool Ok { get; set; }

        [DataMember(Name = "schemaVersion")]
        public string SchemaVersion { get; set; }

        [DataMember(Name = "serverTime")]
        public string ServerTime { get; set; }
    }

    [DataContract]
    public sealed class PosCustomerOrderHandoff
    {
        [DataMember(Name = "attemptCount")]
        public int AttemptCount { get; set; }

        [DataMember(Name = "correlationId")]
        public string CorrelationId { get; set; }

        [DataMember(Name = "eventIdempotencyKey")]
        public string EventIdempotencyKey { get; set; }

        [DataMember(Name = "eventType")]
        public string EventType { get; set; }

        [DataMember(Name = "handoffId")]
        public string HandoffId { get; set; }

        [DataMember(Name = "leaseExpiresAt")]
        public string LeaseExpiresAt { get; set; }

        [DataMember(Name = "leaseToken")]
        public string LeaseToken { get; set; }

        [DataMember(Name = "order")]
        public PosCustomerOrderSnapshot Order { get; set; }

        [DataMember(Name = "schemaVersion")]
        public string SchemaVersion { get; set; }
    }

    [DataContract]
    public sealed class PosCustomerOrderSnapshot
    {
        [DataMember(Name = "currencyCode")]
        public string CurrencyCode { get; set; }

        [DataMember(Name = "currentStatusVersion")]
        public long CurrentStatusVersion { get; set; }

        [DataMember(Name = "deliveryFeeClp")]
        public long DeliveryFeeClp { get; set; }

        [DataMember(Name = "documentKind")]
        public string DocumentKind { get; set; }

        [DataMember(Name = "fiscalStatus")]
        public string FiscalStatus { get; set; }

        [DataMember(Name = "fulfillment")]
        public PosCustomerOrderFulfillment Fulfillment { get; set; }

        [DataMember(Name = "fulfillmentMode")]
        public string FulfillmentMode { get; set; }

        [DataMember(Name = "items")]
        public PosCustomerOrderItem[] Items { get; set; }

        [DataMember(Name = "orderCode")]
        public string OrderCode { get; set; }

        [DataMember(Name = "orderId")]
        public string OrderId { get; set; }

        [DataMember(Name = "placedAt")]
        public string PlacedAt { get; set; }

        [DataMember(Name = "shopId")]
        public string ShopId { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "statusVersion")]
        public long StatusVersion { get; set; }

        [DataMember(Name = "subtotalClp")]
        public long SubtotalClp { get; set; }

        [DataMember(Name = "totalClp")]
        public long TotalClp { get; set; }

        [DataMember(Name = "updatedAt")]
        public string UpdatedAt { get; set; }
    }

    [DataContract]
    public sealed class PosCustomerOrderItem
    {
        [DataMember(Name = "linePosition")]
        public int LinePosition { get; set; }

        [DataMember(Name = "lineTotalClp")]
        public long LineTotalClp { get; set; }

        [DataMember(Name = "publicName")]
        public string PublicName { get; set; }

        [DataMember(Name = "quantity")]
        public int Quantity { get; set; }

        [DataMember(Name = "unitPriceClp")]
        public long UnitPriceClp { get; set; }
    }

    [DataContract]
    public sealed class PosCustomerOrderFulfillment
    {
        [DataMember(Name = "deliveryZone", EmitDefaultValue = false)]
        public PosCustomerOrderDeliveryZone DeliveryZone { get; set; }

        [DataMember(Name = "mode")]
        public string Mode { get; set; }

        [DataMember(Name = "pickupPoint", EmitDefaultValue = false)]
        public PosCustomerOrderPickupPoint PickupPoint { get; set; }

        [DataMember(Name = "slot", EmitDefaultValue = false)]
        public PosCustomerOrderSlot Slot { get; set; }
    }

    [DataContract]
    public sealed class PosCustomerOrderPickupPoint
    {
        [DataMember(Name = "commune", EmitDefaultValue = false)]
        public string Commune { get; set; }

        [DataMember(Name = "publicName", EmitDefaultValue = false)]
        public string PublicName { get; set; }

        [DataMember(Name = "region", EmitDefaultValue = false)]
        public string Region { get; set; }
    }

    [DataContract]
    public sealed class PosCustomerOrderDeliveryZone
    {
        [DataMember(Name = "feeClp", EmitDefaultValue = false)]
        public long FeeClp { get; set; }

        [DataMember(Name = "name", EmitDefaultValue = false)]
        public string Name { get; set; }

        [DataMember(Name = "region", EmitDefaultValue = false)]
        public string Region { get; set; }
    }

    [DataContract]
    public sealed class PosCustomerOrderSlot
    {
        [DataMember(Name = "endsAt", EmitDefaultValue = false)]
        public string EndsAt { get; set; }

        [DataMember(Name = "label", EmitDefaultValue = false)]
        public string Label { get; set; }

        [DataMember(Name = "startsAt", EmitDefaultValue = false)]
        public string StartsAt { get; set; }
    }

    [DataContract]
    public sealed class PosCustomerOrderAckRequest
    {
        [DataMember(Name = "appVersion", EmitDefaultValue = false)]
        public string AppVersion { get; set; }

        [DataMember(Name = "deviceToken")]
        public string DeviceToken { get; set; }

        [DataMember(Name = "expectedStatusVersion")]
        public long ExpectedStatusVersion { get; set; }

        [DataMember(Name = "handoffId")]
        public string HandoffId { get; set; }

        [DataMember(Name = "idempotencyKey")]
        public string IdempotencyKey { get; set; }

        [DataMember(Name = "leaseToken")]
        public string LeaseToken { get; set; }

        [DataMember(Name = "outcome")]
        public string Outcome { get; set; }

        [DataMember(Name = "posSaleId", EmitDefaultValue = false)]
        public string PosSaleId { get; set; }

        [DataMember(Name = "posSessionId")]
        public string PosSessionId { get; set; }

        [DataMember(Name = "schemaVersion")]
        public string SchemaVersion { get; set; }

        [DataMember(Name = "sessionToken")]
        public string SessionToken { get; set; }

        [DataMember(Name = "shopDeviceId")]
        public string ShopDeviceId { get; set; }
    }

    [DataContract]
    public sealed class PosCustomerOrderAckResponse
    {
        [DataMember(Name = "code")]
        public string Code { get; set; }

        [DataMember(Name = "fiscalStatus")]
        public string FiscalStatus { get; set; }

        [DataMember(Name = "handoffId")]
        public string HandoffId { get; set; }

        [DataMember(Name = "idempotent")]
        public bool Idempotent { get; set; }

        [DataMember(Name = "ok")]
        public bool Ok { get; set; }

        [DataMember(Name = "orderId")]
        public string OrderId { get; set; }

        [DataMember(Name = "orderStatus")]
        public string OrderStatus { get; set; }

        [DataMember(Name = "orderStatusVersion")]
        public long OrderStatusVersion { get; set; }

        [DataMember(Name = "outcome")]
        public string Outcome { get; set; }

        [DataMember(Name = "posSaleId", EmitDefaultValue = false)]
        public string PosSaleId { get; set; }

        [DataMember(Name = "schemaVersion")]
        public string SchemaVersion { get; set; }

        [DataMember(Name = "serverTime")]
        public string ServerTime { get; set; }
    }
}
