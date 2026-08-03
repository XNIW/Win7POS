namespace Win7POS.Core.Online
{
    public static class PosOnlineContract
    {
        public const string CatalogCapabilityVersion = "catalog-v2";
        public const int CatalogPullSchemaVersion = 2;
        public const string CatalogImportSchemaVersion = "pos-catalog-import-v1";
        public const string CustomerOrderAckSchemaVersion =
            "pos-customer-order-ack-v1";
        public const string CustomerOrderHandoffSchemaVersion =
            "pos-customer-order-handoff-v1";
        public const int CustomerOrderMaximumBatchCount = 25;
        public const string ArticleMutationSchemaVersion =
            PosArticleMutationContract.SchemaVersion;
        public const int OfflineAuthorizationMaxAgeSeconds = 12 * 60 * 60;
        public const string PaymentCard = "card";
        public const string PaymentCash = "cash";
        public const string PaymentOther = "other";
        public const string PaymentTransfer = "transfer";
        public const string PolicyContractVersion = "pos-policy-v1";
        public const string SalesSchemaVersion = "pos-sales-ledger-v2";
    }
}
