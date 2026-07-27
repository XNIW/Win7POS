using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Win7POS.Core.Online
{
    /// <summary>
    /// Recovers presentation-only catalog text on a private copy of the transport
    /// payload. Identity, price and relationship fields are deliberately never
    /// changed here.
    /// </summary>
    public static class CatalogDisplayRecoveryPolicy
    {
        public const int DisplayTextMaximumLength = 512;

        public static CatalogCompatibilityAssessment Recover(PosCatalogPullResponse response)
        {
            if (response == null)
            {
                return CatalogCompatibilityAssessment.Blocked("catalog_response_missing");
            }

            var summary = new CatalogWarningSummary();
            var recovered = CopyResponse(response);
            var catalog = recovered.Catalog;
            if (catalog == null)
            {
                return new CatalogCompatibilityAssessment(recovered, string.Empty, summary);
            }

            recovered.Catalog.Categories = RecoverCategories(catalog.Categories, summary);
            recovered.Catalog.Suppliers = RecoverSuppliers(catalog.Suppliers, summary);
            recovered.Catalog.Products = RecoverProducts(catalog.Products, summary);
            return new CatalogCompatibilityAssessment(recovered, string.Empty, summary);
        }

        public static CatalogDisplayRecoveryResult RecoverDisplayText(string value, int maximumLength)
        {
            var source = value ?? string.Empty;
            var builder = new StringBuilder(source.Length);
            var pendingSpace = false;
            var controlRemoved = false;
            var replacementUsed = false;
            var changed = false;

            for (var index = 0; index < source.Length; index++)
            {
                var current = source[index];
                if (char.IsHighSurrogate(current))
                {
                    if (index + 1 < source.Length && char.IsLowSurrogate(source[index + 1]))
                    {
                        AppendCharacter(builder, ref pendingSpace, current);
                        AppendCharacter(builder, ref pendingSpace, source[++index]);
                    }
                    else
                    {
                        AppendCharacter(builder, ref pendingSpace, '\uFFFD');
                        replacementUsed = true;
                        changed = true;
                    }

                    continue;
                }

                if (char.IsLowSurrogate(current))
                {
                    AppendCharacter(builder, ref pendingSpace, '\uFFFD');
                    replacementUsed = true;
                    changed = true;
                    continue;
                }

                if (IsCanonicalizedAsciiWhitespace(current))
                {
                    pendingSpace = builder.Length > 0;
                    if (current != ' ') changed = true;
                    continue;
                }

                if (IsRemovedControl(current))
                {
                    controlRemoved = true;
                    changed = true;
                    continue;
                }

                if (IsSpaceSeparator(current))
                {
                    pendingSpace = builder.Length > 0;
                    if (current != ' ') changed = true;
                    continue;
                }

                AppendCharacter(builder, ref pendingSpace, current);
            }

            var canonical = builder.ToString();
            var nfc = canonical.Normalize(NormalizationForm.FormC);
            if (!string.Equals(canonical, nfc, StringComparison.Ordinal))
            {
                canonical = nfc;
                changed = true;
            }

            if (!string.Equals(source, canonical, StringComparison.Ordinal))
            {
                changed = true;
            }

            var warnings = new List<CatalogDataQualityWarning>();
            if (changed)
            {
                warnings.Add(new CatalogDataQualityWarning("catalog_display_text_normalized"));
            }
            if (controlRemoved)
            {
                warnings.Add(new CatalogDataQualityWarning("catalog_display_text_control_removed"));
            }
            if (replacementUsed)
            {
                warnings.Add(new CatalogDataQualityWarning("catalog_display_text_replacement_used"));
            }
            if (maximumLength < 0 || canonical.Length > maximumLength)
            {
                warnings.Add(new CatalogDataQualityWarning("catalog_display_text_over_limit_fallback"));
                return new CatalogDisplayRecoveryResult(string.Empty, false, warnings);
            }

            return new CatalogDisplayRecoveryResult(canonical, true, warnings);
        }

        private static PosCatalogCategoryResponse[] RecoverCategories(
            PosCatalogCategoryResponse[] rows,
            CatalogWarningSummary summary)
        {
            if (rows == null) return null;
            var result = new PosCatalogCategoryResponse[rows.Length];
            for (var index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                if (row == null) continue;
                var text = RecoverDisplayText(row.Name, DisplayTextMaximumLength);
                var warnings = AddFallbackIfNeeded(text, !string.IsNullOrWhiteSpace(row.CategoryId));
                summary.RecordCategory(warnings);
                result[index] = new PosCatalogCategoryResponse
                {
                    CategoryId = row.CategoryId,
                    Name = text.IsUsable && text.Value.Length > 0
                        ? text.Value
                        : row.CategoryId ?? string.Empty,
                    UpdatedAt = row.UpdatedAt
                };
            }

            return result;
        }

        private static PosCatalogSupplierResponse[] RecoverSuppliers(
            PosCatalogSupplierResponse[] rows,
            CatalogWarningSummary summary)
        {
            if (rows == null) return null;
            var result = new PosCatalogSupplierResponse[rows.Length];
            for (var index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                if (row == null) continue;
                var text = RecoverDisplayText(row.Name, DisplayTextMaximumLength);
                var warnings = AddFallbackIfNeeded(text, !string.IsNullOrWhiteSpace(row.SupplierId));
                summary.RecordSupplier(warnings);
                result[index] = new PosCatalogSupplierResponse
                {
                    Name = text.IsUsable && text.Value.Length > 0
                        ? text.Value
                        : row.SupplierId ?? string.Empty,
                    SupplierId = row.SupplierId,
                    UpdatedAt = row.UpdatedAt
                };
            }

            return result;
        }

        private static PosCatalogProductResponse[] RecoverProducts(
            PosCatalogProductResponse[] rows,
            CatalogWarningSummary summary)
        {
            if (rows == null) return null;
            var result = new PosCatalogProductResponse[rows.Length];
            for (var index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                if (row == null) continue;
                var primary = RecoverDisplayText(row.ProductName, DisplayTextMaximumLength);
                var secondary = RecoverDisplayText(row.SecondProductName, DisplayTextMaximumLength);
                var primaryUsable = primary.IsUsable && primary.Value.Length > 0;
                var secondaryUsable = secondary.IsUsable && secondary.Value.Length > 0;
                var fallback = (primary.Warnings.Count > 0 || secondary.Warnings.Count > 0) &&
                    !primaryUsable &&
                    (secondaryUsable || !string.IsNullOrWhiteSpace(row.Barcode));
                var warnings = CombineWarnings(primary, secondary, fallback);
                summary.RecordProduct(warnings);
                result[index] = new PosCatalogProductResponse
                {
                    Barcode = row.Barcode,
                    CategoryId = row.CategoryId,
                    ItemNumber = row.ItemNumber,
                    ProductId = row.ProductId,
                    ProductName = primary.IsUsable ? primary.Value : string.Empty,
                    PurchasePrice = row.PurchasePrice,
                    RetailPrice = row.RetailPrice,
                    SecondProductName = secondary.IsUsable ? secondary.Value : string.Empty,
                    StockQuantity = row.StockQuantity,
                    SupplierId = row.SupplierId,
                    UpdatedAt = row.UpdatedAt
                };
            }

            return result;
        }

        private static IReadOnlyList<CatalogDataQualityWarning> AddFallbackIfNeeded(
            CatalogDisplayRecoveryResult text,
            bool hasIdentityFallback)
        {
            var warnings = new List<CatalogDataQualityWarning>(text.Warnings);
            if ((!text.IsUsable || text.Value.Length == 0) && hasIdentityFallback)
            {
                warnings.Add(new CatalogDataQualityWarning("catalog_display_text_fallback_used"));
            }

            return warnings;
        }

        private static IReadOnlyList<CatalogDataQualityWarning> CombineWarnings(
            CatalogDisplayRecoveryResult primary,
            CatalogDisplayRecoveryResult secondary,
            bool fallback)
        {
            var warnings = new List<CatalogDataQualityWarning>(primary.Warnings.Count + secondary.Warnings.Count + 1);
            warnings.AddRange(primary.Warnings);
            warnings.AddRange(secondary.Warnings);
            if (fallback)
            {
                warnings.Add(new CatalogDataQualityWarning("catalog_display_text_fallback_used"));
            }

            return warnings;
        }

        private static bool IsSpaceSeparator(char value)
        {
            return char.IsWhiteSpace(value) ||
                char.GetUnicodeCategory(value) == UnicodeCategory.SpaceSeparator;
        }

        private static bool IsCanonicalizedAsciiWhitespace(char value)
        {
            return value == '\r' || value == '\n' || value == '\t' || value == ' ';
        }

        private static bool IsRemovedControl(char value)
        {
            if (value <= 0x001F || (value >= 0x007F && value <= 0x009F))
            {
                return true;
            }

            // U+200D is part of valid emoji sequences; removing it would change
            // human-readable content rather than merely removing hidden formatting.
            return value != '\u200D' &&
                char.GetUnicodeCategory(value) == UnicodeCategory.Format;
        }

        private static void AppendCharacter(StringBuilder builder, ref bool pendingSpace, char value)
        {
            if (pendingSpace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            builder.Append(value);
        }

        private static PosCatalogPullResponse CopyResponse(PosCatalogPullResponse response)
        {
            return new PosCatalogPullResponse
            {
                Catalog = response.Catalog == null
                    ? null
                    : new PosCatalogPayload
                    {
                        Categories = response.Catalog.Categories,
                        Prices = response.Catalog.Prices,
                        Products = response.Catalog.Products,
                        Suppliers = response.Catalog.Suppliers,
                        Tombstones = response.Catalog.Tombstones
                    },
                CatalogSummary = response.CatalogSummary,
                CatalogVersion = response.CatalogVersion,
                Code = response.Code,
                GeneratedAt = response.GeneratedAt,
                HasMore = response.HasMore,
                Ok = response.Ok,
                Policy = response.Policy,
                SchemaVersion = response.SchemaVersion,
                ServerTime = response.ServerTime,
                Shop = response.Shop,
                SyncCursor = response.SyncCursor,
                SyncMode = response.SyncMode
            };
        }
    }

    public sealed class CatalogCompatibilityAssessment
    {
        internal CatalogCompatibilityAssessment(
            PosCatalogPullResponse recoveredResponse,
            string blockingCode,
            CatalogWarningSummary warningSummary)
        {
            RecoveredResponse = recoveredResponse;
            BlockingCode = blockingCode ?? string.Empty;
            WarningSummary = warningSummary ?? new CatalogWarningSummary();
        }

        public string BlockingCode { get; }
        public bool CanContinue => string.IsNullOrEmpty(BlockingCode);
        public PosCatalogPullResponse RecoveredResponse { get; }
        public bool SaleSafeCandidate => CanContinue;
        public CatalogWarningSummary WarningSummary { get; }

        public static CatalogCompatibilityAssessment Blocked(string code)
        {
            return new CatalogCompatibilityAssessment(null, code, new CatalogWarningSummary());
        }

        public CatalogCompatibilityAssessment WithBlockingCode(string code)
        {
            return new CatalogCompatibilityAssessment(
                RecoveredResponse,
                code,
                WarningSummary);
        }
    }

    public sealed class CatalogDisplayRecoveryResult
    {
        internal CatalogDisplayRecoveryResult(
            string value,
            bool isUsable,
            IReadOnlyList<CatalogDataQualityWarning> warnings)
        {
            Value = value ?? string.Empty;
            IsUsable = isUsable;
            Warnings = warnings ?? Array.Empty<CatalogDataQualityWarning>();
        }

        public bool IsUsable { get; }
        public string Value { get; }
        public IReadOnlyList<CatalogDataQualityWarning> Warnings { get; }
    }

    public sealed class CatalogDataQualityWarning
    {
        internal CatalogDataQualityWarning(string code)
        {
            Code = code ?? string.Empty;
        }

        public string Code { get; }
    }

    public sealed class CatalogWarningSummary
    {
        public int CategoriesAffected { get; private set; }
        public int FallbackCount { get; private set; }
        public int NormalizedCount { get; private set; }
        public int ProductsAffected { get; private set; }
        public int RemovedControlCount { get; private set; }
        public int ReplacementCharacterCount { get; private set; }
        public int SuppliersAffected { get; private set; }
        public int WarningCount { get; private set; }

        public bool HasWarnings => WarningCount > 0;

        internal void RecordCategory(IReadOnlyList<CatalogDataQualityWarning> warnings)
        {
            Record(warnings, () => CategoriesAffected++);
        }

        internal void RecordProduct(IReadOnlyList<CatalogDataQualityWarning> warnings)
        {
            Record(warnings, () => ProductsAffected++);
        }

        internal void RecordSupplier(IReadOnlyList<CatalogDataQualityWarning> warnings)
        {
            Record(warnings, () => SuppliersAffected++);
        }

        public void Add(CatalogWarningSummary value)
        {
            if (value == null) return;
            CategoriesAffected += value.CategoriesAffected;
            FallbackCount += value.FallbackCount;
            NormalizedCount += value.NormalizedCount;
            ProductsAffected += value.ProductsAffected;
            RemovedControlCount += value.RemovedControlCount;
            ReplacementCharacterCount += value.ReplacementCharacterCount;
            SuppliersAffected += value.SuppliersAffected;
            WarningCount += value.WarningCount;
        }

        private void Record(IReadOnlyList<CatalogDataQualityWarning> warnings, Action recordAffected)
        {
            if (warnings == null || warnings.Count == 0) return;
            recordAffected();
            foreach (var warning in warnings)
            {
                var code = warning?.Code ?? string.Empty;
                WarningCount++;
                if (string.Equals(code, "catalog_display_text_normalized", StringComparison.Ordinal))
                    NormalizedCount++;
                else if (string.Equals(code, "catalog_display_text_control_removed", StringComparison.Ordinal))
                    RemovedControlCount++;
                else if (string.Equals(code, "catalog_display_text_replacement_used", StringComparison.Ordinal))
                    ReplacementCharacterCount++;
                else if (string.Equals(code, "catalog_display_text_fallback_used", StringComparison.Ordinal) ||
                         string.Equals(code, "catalog_display_text_over_limit_fallback", StringComparison.Ordinal))
                    FallbackCount++;
            }
        }
    }
}
