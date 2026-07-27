using System;
using Win7POS.Core.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Online
{
    /// <summary>
    /// Bounded, redacted explanation for a catalog product row rejected before a
    /// full-response page can be staged. It deliberately contains no identifiers,
    /// names, barcodes, prices, or transport body.
    /// </summary>
    public sealed class CatalogProductRowDiagnostic
    {
        public int BarcodeLength { get; private set; }
        public int CategoryIdLength { get; private set; }
        public int ItemNumberLength { get; private set; }
        public int ProductIdLength { get; private set; }
        public int ProductNameLength { get; private set; }
        public string PurchasePriceClass { get; private set; } = string.Empty;
        public int Row { get; private set; }
        public string PriceClass { get; private set; } = string.Empty;
        public string Reason { get; private set; } = string.Empty;
        public int SecondProductNameLength { get; private set; }
        public string StockQuantityClass { get; private set; } = string.Empty;
        public int SupplierIdLength { get; private set; }
        public int UpdatedAtLength { get; private set; }

        public static CatalogProductRowDiagnostic FindFirstInvalid(
            PosCatalogProductResponse[] rows)
        {
            var products = rows ?? Array.Empty<PosCatalogProductResponse>();
            for (var index = 0; index < products.Length; index++)
            {
                var product = products[index];
                var recovered = CatalogDisplayRecoveryPolicy.Recover(new PosCatalogPullResponse
                {
                    Catalog = new PosCatalogPayload { Products = new[] { product } }
                }).RecoveredResponse;
                var rowCode = PosOnlineCompatibilityValidator.ValidateCatalogRows(
                    recovered?.Catalog);
                if (!string.IsNullOrWhiteSpace(rowCode))
                {
                    return Describe(index + 1, product);
                }
            }

            return new CatalogProductRowDiagnostic
            {
                PriceClass = "not_evaluated",
                Reason = "unexpected_reason"
            };
        }

        public static CatalogProductRowDiagnostic Describe(
            int row,
            PosCatalogProductResponse value)
        {
            value = CatalogDisplayRecoveryPolicy.Recover(new PosCatalogPullResponse
            {
                Catalog = new PosCatalogPayload { Products = new[] { value } }
            }).RecoveredResponse?.Catalog?.Products?[0];
            var result = new CatalogProductRowDiagnostic
            {
                BarcodeLength = Length(value?.Barcode),
                CategoryIdLength = Length(value?.CategoryId),
                ItemNumberLength = Length(value?.ItemNumber),
                ProductIdLength = Length(value?.ProductId),
                ProductNameLength = Length(value?.ProductName),
                PurchasePriceClass = DescribeOptionalNonNegativeFinite(value?.PurchasePrice, int.MaxValue),
                Row = Math.Max(0, row),
                SecondProductNameLength = Length(value?.SecondProductName),
                PriceClass = DescribePrice(value?.RetailPrice),
                StockQuantityClass = DescribeOptionalNonNegativeFinite(value?.StockQuantity, int.MaxValue),
                SupplierIdLength = Length(value?.SupplierId),
                UpdatedAtLength = Length(value?.UpdatedAt)
            };
            if (value == null) return result.WithReason("null_product");
            if (!RemoteCatalogContentPolicy.IsRequiredText(
                    value.ProductId,
                    RemoteCatalogContentPolicy.RemoteIdMaximumLength))
            {
                return result.WithReason(string.IsNullOrWhiteSpace(value.ProductId)
                    ? "missing_remote_product_id"
                    : "invalid_remote_product_id_text");
            }
            if (!RemoteCatalogContentPolicy.IsRequiredText(
                    value.Barcode,
                    RemoteCatalogContentPolicy.BarcodeMaximumLength))
            {
                return result.WithReason(string.IsNullOrWhiteSpace(value.Barcode)
                    ? "blank_barcode"
                    : "invalid_barcode_text");
            }
            if (!RemoteCatalogContentPolicy.IsOptionalText(
                    value.ProductName,
                    RemoteCatalogContentPolicy.NameMaximumLength))
            {
                return result.WithReason("invalid_product_name_text");
            }
            if (!RemoteCatalogContentPolicy.IsOptionalText(
                    value.SecondProductName,
                    RemoteCatalogContentPolicy.NameMaximumLength))
            {
                return result.WithReason("invalid_second_product_name_text");
            }
            if (!RemoteCatalogContentPolicy.IsOptionalText(
                    value.ItemNumber,
                    RemoteCatalogContentPolicy.ItemNumberMaximumLength))
            {
                return result.WithReason("invalid_item_number_text");
            }
            if (!RemoteCatalogContentPolicy.IsOptionalTimestamp(value.UpdatedAt))
            {
                return result.WithReason("invalid_updated_at");
            }
            if (double.IsNaN(value.RetailPrice.GetValueOrDefault()) ||
                double.IsInfinity(value.RetailPrice.GetValueOrDefault()))
            {
                return result.WithReason("nonfinite_retail_price");
            }
            if (!value.RetailPrice.HasValue || value.RetailPrice.Value <= 0)
                return result.WithReason("nonpositive_retail_price");
            if (value.RetailPrice.Value > long.MaxValue)
                return result.WithReason("retail_price_out_of_range");
            if (!IsOptionalNonNegativeFinite(value.PurchasePrice, int.MaxValue))
            {
                return result.WithReason("invalid_purchase_price");
            }
            if (!IsOptionalNonNegativeFinite(value.StockQuantity, int.MaxValue))
            {
                return result.WithReason("invalid_stock_quantity");
            }
            if (!RemoteCatalogContentPolicy.IsOptionalText(
                    value.CategoryId,
                    RemoteCatalogContentPolicy.RemoteIdMaximumLength))
            {
                return result.WithReason("invalid_category_id_text");
            }
            if (!RemoteCatalogContentPolicy.IsOptionalText(
                    value.SupplierId,
                    RemoteCatalogContentPolicy.RemoteIdMaximumLength))
            {
                return result.WithReason("invalid_supplier_id_text");
            }
            return result.WithReason(string.IsNullOrEmpty(
                PosOnlineCompatibilityValidator.ValidateCatalogRows(
                    new PosCatalogPayload { Products = new[] { value } }))
                ? "valid"
                : "unexpected_reason");
        }

        private CatalogProductRowDiagnostic WithReason(string reason)
        {
            Reason = reason ?? "unexpected_reason";
            return this;
        }

        private static string DescribePrice(double? price)
        {
            if (!price.HasValue) return "missing";
            if (double.IsNaN(price.Value) || double.IsInfinity(price.Value)) return "nonfinite";
            if (price.Value <= 0) return "nonpositive";
            if (price.Value >= long.MaxValue) return "conversion_saturates_long_max";
            if (RemoteCatalogBatchMapper.ToLong(price) <= 0) return "positive_rounds_to_zero";
            return "positive_converts_to_long";
        }

        private static bool IsOptionalNonNegativeFinite(double? value, double maximum)
        {
            return !value.HasValue ||
                (!double.IsNaN(value.Value) &&
                 !double.IsInfinity(value.Value) &&
                 value.Value >= 0 &&
                 value.Value <= maximum);
        }

        private static string DescribeOptionalNonNegativeFinite(double? value, double maximum)
        {
            if (!value.HasValue) return "missing";
            if (double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return "nonfinite";
            if (value.Value < 0) return "negative";
            if (value.Value > maximum) return "out_of_range";
            return "nonnegative_in_range";
        }

        private static int Length(string value)
        {
            return value?.Length ?? 0;
        }
    }
}
