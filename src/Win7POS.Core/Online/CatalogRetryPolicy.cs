using System;

namespace Win7POS.Core.Online
{
    /// <summary>
    /// Catalog contract and exactness failures are deterministic for a catalog
    /// revision. They remain sale-blocking, but must not create periodic traffic
    /// until an explicit new revision/event is observed.
    /// </summary>
    public static class CatalogRetryPolicy
    {
        public static bool IsDeterministicRevisionFailure(string code)
        {
            var normalized = (code ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return false;
            }

            if (string.Equals(normalized, "catalog_product_row_invalid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "catalog_category_row_invalid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "catalog_supplier_row_invalid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "catalog_price_row_invalid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "catalog_v2_page_contract_invalid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "catalog_rows_not_fully_applied", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "response_shop_mismatch", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "catalog_version_changed_mid_pull", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return normalized.StartsWith("catalog_", StringComparison.OrdinalIgnoreCase) &&
                (normalized.EndsWith("_tombstone_invalid", StringComparison.OrdinalIgnoreCase) ||
                 normalized.EndsWith("_conflict", StringComparison.OrdinalIgnoreCase));
        }

        public static bool ShouldOfferManualRetry(string code, bool authenticationDenied)
        {
            return !authenticationDenied && !IsDeterministicRevisionFailure(code);
        }
    }
}
