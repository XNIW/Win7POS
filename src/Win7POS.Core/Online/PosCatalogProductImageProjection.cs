using System;

namespace Win7POS.Core.Online
{
    public sealed class PosCatalogProductImageProjection
    {
        private PosCatalogProductImageProjection() { }

        public bool Apply { get; private set; }
        public string PrimaryImageUpdatedAt { get; private set; }
        public string PrimaryImageVersionId { get; private set; }
        public string WarningCode { get; private set; }

        public static PosCatalogProductImageProjection Normalize(
            string primaryImageVersionId,
            string primaryImageUpdatedAt)
        {
            return Normalize(
                true,
                primaryImageVersionId,
                true,
                primaryImageUpdatedAt);
        }

        public static PosCatalogProductImageProjection Normalize(
            bool primaryImageVersionIdPresent,
            string primaryImageVersionId,
            bool primaryImageUpdatedAtPresent,
            string primaryImageUpdatedAt)
        {
            if (!primaryImageVersionIdPresent || !primaryImageUpdatedAtPresent)
            {
                return Warning("image_fields_missing");
            }
            var version = EmptyToNull(primaryImageVersionId);
            var updatedAt = EmptyToNull(primaryImageUpdatedAt);
            if (version != null && !PosProductImageContractV1.IsCanonicalUuid(version))
            {
                return Warning("image_version_invalid");
            }
            if (updatedAt != null && !PosProductImageContractV1.IsCanonicalTimestamp(updatedAt))
            {
                return Warning("image_timestamp_invalid");
            }
            if (version != null && updatedAt == null)
            {
                return Warning("image_timestamp_missing");
            }
            return new PosCatalogProductImageProjection
            {
                Apply = true,
                PrimaryImageVersionId = version,
                PrimaryImageUpdatedAt = updatedAt
            };
        }

        private static PosCatalogProductImageProjection Warning(string code) =>
            new PosCatalogProductImageProjection
            {
                Apply = false,
                WarningCode = code
            };

        private static string EmptyToNull(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return normalized.Length == 0 ? null : normalized;
        }
    }
}
