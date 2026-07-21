using System;
using System.Linq;
using Win7POS.Core.Online;
using Win7POS.Core.Receipt;

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

            if (!response.Ok)
            {
                return "catalog_response_not_ok";
            }

            try
            {
                ReceiptShopMetadataPolicy.EnsureValidRemoteShop(response.Shop);
            }
            catch (ReceiptContentValidationException)
            {
                return "catalog_shop_metadata_invalid";
            }

            if (!RemoteCatalogContentPolicy.IsOptionalTimestamp(response.GeneratedAt))
            {
                return "catalog_generated_at_invalid";
            }

            var rawSyncMode = response.SyncMode ?? string.Empty;
            var syncMode = rawSyncMode.Trim();
            if (!string.Equals(rawSyncMode, syncMode, StringComparison.Ordinal))
            {
                return "catalog_sync_mode_not_supported";
            }
            if (!string.Equals(syncMode, "delta", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(syncMode, "full_refresh", StringComparison.OrdinalIgnoreCase))
            {
                return "catalog_sync_mode_not_supported";
            }

            var syncCursor = response.SyncCursor ?? string.Empty;
            if (!RemoteCatalogContentPolicy.IsRequiredCanonicalText(
                syncCursor,
                RemoteCatalogContentPolicy.SyncCursorMaximumLength))
            {
                return "catalog_sync_cursor_invalid";
            }

            var catalogVersionError = ValidateCatalogVersion(response.CatalogVersion);
            if (!string.IsNullOrWhiteSpace(catalogVersionError))
            {
                return catalogVersionError;
            }

            var rowsError = ValidateCatalogRows(response.Catalog);
            if (!string.IsNullOrWhiteSpace(rowsError))
            {
                return rowsError;
            }

            var summaryError = ValidateCatalogSummary(response.CatalogSummary);
            if (!string.IsNullOrWhiteSpace(summaryError))
            {
                return summaryError;
            }

            return ValidatePolicy(response.Policy);
        }

        public static string ValidateCatalogVersion(string catalogVersion)
        {
            var value = catalogVersion ?? string.Empty;
            if (!RemoteCatalogContentPolicy.IsRequiredCanonicalText(
                value,
                RemoteCatalogContentPolicy.CatalogVersionMaximumLength))
            {
                return "catalog_version_invalid";
            }

            return string.Empty;
        }

        public static string ValidateCatalogRows(PosCatalogPayload catalog)
        {
            if (catalog == null)
            {
                return "catalog_payload_missing";
            }

            foreach (var row in catalog.Categories ?? Array.Empty<PosCatalogCategoryResponse>())
            {
                if (row == null ||
                    !RemoteCatalogContentPolicy.IsRequiredText(row.CategoryId, RemoteCatalogContentPolicy.RemoteIdMaximumLength) ||
                    !RemoteCatalogContentPolicy.IsRequiredText(row.Name, RemoteCatalogContentPolicy.NameMaximumLength) ||
                    !RemoteCatalogContentPolicy.IsOptionalTimestamp(row.UpdatedAt))
                {
                    return "catalog_category_row_invalid";
                }
            }

            if (HasDuplicateCatalogIds(
                catalog.Categories,
                row => row?.CategoryId))
            {
                return "catalog_duplicate_category_ids";
            }

            foreach (var row in catalog.Suppliers ?? Array.Empty<PosCatalogSupplierResponse>())
            {
                if (row == null ||
                    !RemoteCatalogContentPolicy.IsRequiredText(row.SupplierId, RemoteCatalogContentPolicy.RemoteIdMaximumLength) ||
                    !RemoteCatalogContentPolicy.IsRequiredText(row.Name, RemoteCatalogContentPolicy.NameMaximumLength) ||
                    !RemoteCatalogContentPolicy.IsOptionalTimestamp(row.UpdatedAt))
                {
                    return "catalog_supplier_row_invalid";
                }
            }

            if (HasDuplicateCatalogIds(
                catalog.Suppliers,
                row => row?.SupplierId))
            {
                return "catalog_duplicate_supplier_ids";
            }

            foreach (var row in catalog.Products ?? Array.Empty<PosCatalogProductResponse>())
            {
                if (row == null ||
                    !RemoteCatalogContentPolicy.IsRequiredText(row.ProductId, RemoteCatalogContentPolicy.RemoteIdMaximumLength) ||
                    !RemoteCatalogContentPolicy.IsRequiredText(row.Barcode, RemoteCatalogContentPolicy.BarcodeMaximumLength) ||
                    !RemoteCatalogContentPolicy.IsOptionalText(row.ProductName, RemoteCatalogContentPolicy.NameMaximumLength) ||
                    !RemoteCatalogContentPolicy.IsOptionalText(row.SecondProductName, RemoteCatalogContentPolicy.NameMaximumLength) ||
                    !RemoteCatalogContentPolicy.IsOptionalText(row.ItemNumber, RemoteCatalogContentPolicy.ItemNumberMaximumLength) ||
                    !RemoteCatalogContentPolicy.IsOptionalTimestamp(row.UpdatedAt) ||
                    !IsPositiveFinite(row.RetailPrice, long.MaxValue) ||
                    !IsOptionalNonNegativeFinite(row.PurchasePrice, int.MaxValue) ||
                    !IsOptionalNonNegativeFinite(row.StockQuantity, int.MaxValue) ||
                    !RemoteCatalogContentPolicy.IsOptionalText(row.CategoryId, RemoteCatalogContentPolicy.RemoteIdMaximumLength) ||
                    !RemoteCatalogContentPolicy.IsOptionalText(row.SupplierId, RemoteCatalogContentPolicy.RemoteIdMaximumLength))
                {
                    return "catalog_product_row_invalid";
                }
            }

            if (HasDuplicateCatalogIds(
                catalog.Products,
                row => row?.ProductId))
            {
                return "catalog_duplicate_product_ids";
            }

            foreach (var row in catalog.Prices ?? Array.Empty<PosCatalogPriceResponse>())
            {
                if (row == null ||
                    !RemoteCatalogContentPolicy.IsRequiredText(row.PriceId, RemoteCatalogContentPolicy.RemoteIdMaximumLength) ||
                    !RemoteCatalogContentPolicy.IsRequiredText(row.ProductId, RemoteCatalogContentPolicy.RemoteIdMaximumLength) ||
                    !RemoteCatalogContentPolicy.IsRequiredText(row.Type, RemoteCatalogContentPolicy.TypeMaximumLength) ||
                    !RemoteCatalogContentPolicy.IsOptionalTimestamp(row.EffectiveAt) ||
                    !RemoteCatalogContentPolicy.IsOptionalText(row.Source, RemoteCatalogContentPolicy.SourceMaximumLength) ||
                    double.IsNaN(row.Price) ||
                    double.IsInfinity(row.Price) ||
                    row.Price < 0 ||
                    row.Price > int.MaxValue)
                {
                    return "catalog_price_row_invalid";
                }
            }

            foreach (var row in catalog.Tombstones?.Products ?? Array.Empty<PosCatalogProductTombstoneResponse>())
            {
                if (row == null ||
                    !RemoteCatalogContentPolicy.IsRequiredText(row.ProductId, RemoteCatalogContentPolicy.RemoteIdMaximumLength) ||
                    !RemoteCatalogContentPolicy.IsOptionalTimestamp(row.DeletedAt) ||
                    !RemoteCatalogContentPolicy.IsOptionalTimestamp(row.UpdatedAt))
                {
                    return "catalog_product_tombstone_invalid";
                }
            }

            foreach (var row in catalog.Tombstones?.Categories ?? Array.Empty<PosCatalogCategoryTombstoneResponse>())
            {
                if (row == null ||
                    !RemoteCatalogContentPolicy.IsRequiredText(row.CategoryId, RemoteCatalogContentPolicy.RemoteIdMaximumLength) ||
                    !RemoteCatalogContentPolicy.IsOptionalTimestamp(row.DeletedAt) ||
                    !RemoteCatalogContentPolicy.IsOptionalTimestamp(row.UpdatedAt))
                {
                    return "catalog_category_tombstone_invalid";
                }
            }

            foreach (var row in catalog.Tombstones?.Suppliers ?? Array.Empty<PosCatalogSupplierTombstoneResponse>())
            {
                if (row == null ||
                    !RemoteCatalogContentPolicy.IsRequiredText(row.SupplierId, RemoteCatalogContentPolicy.RemoteIdMaximumLength) ||
                    !RemoteCatalogContentPolicy.IsOptionalTimestamp(row.DeletedAt) ||
                    !RemoteCatalogContentPolicy.IsOptionalTimestamp(row.UpdatedAt))
                {
                    return "catalog_supplier_tombstone_invalid";
                }
            }

            return string.Empty;
        }

        private static bool HasDuplicateCatalogIds<T>(
            T[] rows,
            Func<T, string> selectId)
        {
            // This check is deliberately page-scoped. A delta stream may contain
            // multiple ordered events for the same identity on different pages, so
            // carrying IDs across delta cursors would reject valid updates. Full-refresh
            // uniqueness across pages is enforced by CatalogFullRefreshReconciler.
            return (rows ?? Array.Empty<T>())
                .Where(row => row != null)
                .Select(row => (selectId(row) ?? string.Empty).Trim())
                .Where(value => value.Length > 0)
                .GroupBy(value => value, StringComparer.Ordinal)
                .Any(group => group.Skip(1).Any());
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

            var hasChecksum = !string.IsNullOrWhiteSpace(summary.Checksum);
            var hasChecksumAlgorithm = !string.IsNullOrWhiteSpace(summary.ChecksumAlgorithm);
            if (!IsSafeSummaryText(summary.Checksum, 256) ||
                !IsSafeSummaryText(summary.ChecksumAlgorithm, 64) ||
                hasChecksum != hasChecksumAlgorithm)
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

        private static bool IsOptionalNonNegativeFinite(double? value, double maximum)
        {
            return !value.HasValue ||
                (!double.IsNaN(value.Value) &&
                 !double.IsInfinity(value.Value) &&
                 value.Value >= 0 &&
                 value.Value <= maximum);
        }

        private static bool IsPositiveFinite(double? value, double maximum)
        {
            return value.HasValue &&
                !double.IsNaN(value.Value) &&
                !double.IsInfinity(value.Value) &&
                value.Value > 0 &&
                value.Value <= maximum;
        }

        private static bool IsSafeSummaryText(string value, int maximumLength)
        {
            return RemoteCatalogContentPolicy.IsOptionalText(value, maximumLength);
        }
    }
}
