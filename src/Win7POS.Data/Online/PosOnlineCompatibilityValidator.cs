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

            var summaryError = ValidateCatalogSummary(response.CatalogSummary);
            if (!string.IsNullOrWhiteSpace(summaryError))
            {
                return summaryError;
            }

            return ValidatePolicy(response.Policy);
        }

        public static string ValidateCatalogSummary(PosCatalogSummaryResponse summary)
        {
            // Older catalog-v2 servers do not emit catalogSummary. Absence is therefore
            // compatible, but it can only produce an Unverified exactness result.
            if (summary == null)
            {
                return string.Empty;
            }

            if (IsNegative(summary.Products) ||
                IsNegative(summary.ActiveProducts) ||
                IsNegative(summary.Categories) ||
                IsNegative(summary.Suppliers) ||
                IsNegative(summary.Prices))
            {
                return "catalog_summary_count_invalid";
            }

            if (summary.Products.HasValue &&
                summary.ActiveProducts.HasValue &&
                summary.ActiveProducts.Value > summary.Products.Value)
            {
                return "catalog_summary_relationship_invalid";
            }

            if (!IsSafeSummaryText(summary.Checksum, 256) ||
                !IsSafeSummaryText(summary.ChecksumAlgorithm, 64) ||
                (!string.IsNullOrWhiteSpace(summary.ChecksumAlgorithm) &&
                 string.IsNullOrWhiteSpace(summary.Checksum)))
            {
                return "catalog_summary_checksum_invalid";
            }

            return string.Empty;
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

        private static bool IsNegative(long? value)
        {
            return value.HasValue && value.Value < 0;
        }

        private static bool IsSafeSummaryText(string value, int maximumLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return true;
            }

            return value.Length <= maximumLength && !value.Any(char.IsControl);
        }
    }
}
