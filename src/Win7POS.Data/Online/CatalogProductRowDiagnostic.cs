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
        public int ProductIdLength { get; private set; }
        public int ProductNameLength { get; private set; }
        public int Row { get; private set; }
        public string PriceClass { get; private set; } = string.Empty;
        public string Reason { get; private set; } = string.Empty;
        public int SecondProductNameLength { get; private set; }

        public static CatalogProductRowDiagnostic FindFirstInvalid(
            PosCatalogProductResponse[] rows)
        {
            var products = rows ?? Array.Empty<PosCatalogProductResponse>();
            for (var index = 0; index < products.Length; index++)
            {
                var product = products[index];
                var rowCode = PosOnlineCompatibilityValidator.ValidateCatalogRows(
                    new PosCatalogPayload { Products = new[] { product } });
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
            var result = new CatalogProductRowDiagnostic
            {
                BarcodeLength = Length(value?.Barcode),
                ProductIdLength = Length(value?.ProductId),
                ProductNameLength = Length(value?.ProductName),
                Row = Math.Max(0, row),
                SecondProductNameLength = Length(value?.SecondProductName),
                PriceClass = DescribePrice(value?.RetailPrice)
            };
            if (value == null) return result.WithReason("null_product");
            if (string.IsNullOrWhiteSpace(value.ProductId)) return result.WithReason("missing_remote_product_id");
            if (string.IsNullOrWhiteSpace(value.Barcode)) return result.WithReason("blank_barcode");
            if (double.IsNaN(value.RetailPrice.GetValueOrDefault()) ||
                double.IsInfinity(value.RetailPrice.GetValueOrDefault()))
            {
                return result.WithReason("nonfinite_retail_price");
            }
            if (!value.RetailPrice.HasValue || value.RetailPrice.Value <= 0)
                return result.WithReason("nonpositive_unit_price_after_conversion");
            if (value.RetailPrice.Value >= long.MaxValue)
                return result.WithReason("conversion_overflow");
            if (ProductIdentityPolicy.IsReservedBarcode(value.Barcode.Trim()))
            {
                return result.WithReason(value.Barcode.Trim().StartsWith("DISC:", StringComparison.Ordinal)
                    ? "reserved_disc_barcode"
                    : "reserved_manual_barcode");
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

        private static int Length(string value)
        {
            return value?.Length ?? 0;
        }
    }
}
