using System;
using System.Linq;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public static class PosOnlineCompatibilityValidator
    {
        public static string ValidateCatalogPull(PosCatalogPullResponse response)
        {
            if (response == null || response.SchemaVersion != PosOnlineContract.CatalogPullSchemaVersion)
            {
                return "catalog_schema_not_supported";
            }

            var syncMode = (response.SyncMode ?? string.Empty).Trim();
            if (!string.Equals(syncMode, "delta", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(syncMode, "full_refresh", StringComparison.OrdinalIgnoreCase))
            {
                return "catalog_sync_mode_not_supported";
            }

            return ValidatePolicy(response.Policy);
        }

        public static string ValidatePolicy(PosPolicyResponse policy)
        {
            if (policy == null)
            {
                return "policy_missing";
            }

            if (!string.Equals(
                (policy.ContractVersion ?? string.Empty).Trim(),
                PosOnlineContract.PolicyContractVersion,
                StringComparison.OrdinalIgnoreCase))
            {
                return "policy_contract_not_supported";
            }

            if (policy.Capabilities == null ||
                !string.Equals(
                    (policy.Capabilities.CatalogPull ?? string.Empty).Trim(),
                    PosOnlineContract.CatalogCapabilityVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "catalog_capability_not_supported";
            }

            if (!string.Equals(
                    (policy.Capabilities.SalesSync ?? string.Empty).Trim(),
                    PosOnlineContract.SalesSchemaVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "sales_sync_contract_not_supported";
            }

            if (!policy.Capabilities.OfflineSales)
            {
                return "offline_sales_disabled_by_server_policy";
            }

            if (policy.PaymentPolicy != null &&
                !string.Equals(policy.PaymentPolicy.Currency, "CLP", StringComparison.OrdinalIgnoreCase))
            {
                return "unsupported_currency";
            }

            if ((policy.PaymentPolicy?.SupportedMethods ?? Array.Empty<string>()).Any(method =>
                string.Equals(method, PosOnlineContract.PaymentTransfer, StringComparison.OrdinalIgnoreCase)))
            {
                return "transfer_payment_not_supported_by_win7pos";
            }

            return string.Empty;
        }
    }
}
