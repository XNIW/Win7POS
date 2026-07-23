using System;

namespace Win7POS.Core.Online
{
    public sealed class CatalogSaleSafetySnapshot
    {
        public CatalogSaleSafetySnapshot(
            string boundShopId,
            string boundShopCode,
            string officialShopId,
            string officialShopCode,
            string repairRequired,
            string saleSafeAt,
            string exactnessStatus,
            string exactnessShopId,
            string exactnessShopCode)
        {
            BoundShopId = boundShopId ?? string.Empty;
            BoundShopCode = boundShopCode ?? string.Empty;
            OfficialShopId = officialShopId ?? string.Empty;
            OfficialShopCode = officialShopCode ?? string.Empty;
            RepairRequired = repairRequired ?? string.Empty;
            SaleSafeAt = saleSafeAt ?? string.Empty;
            ExactnessStatus = exactnessStatus ?? string.Empty;
            ExactnessShopId = exactnessShopId ?? string.Empty;
            ExactnessShopCode = exactnessShopCode ?? string.Empty;
        }

        public string BoundShopCode { get; }
        public string BoundShopId { get; }
        public string ExactnessShopCode { get; }
        public string ExactnessShopId { get; }
        public string ExactnessStatus { get; }
        public string OfficialShopCode { get; }
        public string OfficialShopId { get; }
        public string RepairRequired { get; }
        public string SaleSafeAt { get; }
    }

    public sealed class CatalogSaleSafetyDecision
    {
        internal CatalogSaleSafetyDecision(
            bool isSaleSafe,
            bool isCatalogBound,
            string reasonCode)
        {
            IsSaleSafe = isSaleSafe;
            IsCatalogBound = isCatalogBound;
            ReasonCode = reasonCode ?? string.Empty;
        }

        public bool IsCatalogBound { get; }
        public bool IsSaleSafe { get; }
        public string ReasonCode { get; }
    }

    public static class CatalogSaleSafetyPolicy
    {
        public const string ExactnessBindingPartialCode = "catalog_sale_blocked_exactness_binding_partial";
        public const string ExactnessMissingCode = "catalog_sale_blocked_exactness_missing";
        public const string ExactnessNotVerifiedCode = "catalog_sale_blocked_exactness_not_verified";
        public const string ExactnessShopMismatchCode = "catalog_sale_blocked_exactness_shop_mismatch";
        public const string LegacyUnboundSafeCode = "catalog_sale_safe_legacy_unbound";
        public const string BindingPartialCode = "catalog_sale_blocked_binding_partial";
        public const string NotBoundCode = "catalog_sale_blocked_not_bound";
        public const string NotSaleSafeCode = "catalog_sale_blocked_not_sale_safe";
        public const string OfficialShopPartialCode = "catalog_sale_blocked_official_shop_partial";
        public const string RepairRequiredCode = "catalog_sale_blocked_repair_required";
        public const string RepairStateInvalidCode = "catalog_sale_blocked_repair_state_invalid";
        public const string SafeCode = "catalog_sale_safe";
        public const string ShopMismatchCode = "catalog_sale_blocked_shop_mismatch";

        public static CatalogSaleSafetyDecision Evaluate(
            CatalogSaleSafetySnapshot snapshot,
            bool allowLegacyUnbound)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var boundShopId = NormalizeId(snapshot.BoundShopId);
            var boundShopCode = NormalizeCode(snapshot.BoundShopCode);
            var hasBoundShopId = boundShopId.Length > 0;
            var hasBoundShopCode = boundShopCode.Length > 0;

            // Databases that have never been linked retain their legacy/local sale
            // behavior. Official-catalog readiness remains false for that same state.
            if (!hasBoundShopId && !hasBoundShopCode)
            {
                return allowLegacyUnbound
                    ? Safe(isCatalogBound: false, LegacyUnboundSafeCode)
                    : Blocked(isCatalogBound: false, NotBoundCode);
            }

            if (!hasBoundShopId || !hasBoundShopCode)
            {
                return Blocked(isCatalogBound: true, BindingPartialCode);
            }

            var officialShopId = NormalizeId(snapshot.OfficialShopId);
            var officialShopCode = NormalizeCode(snapshot.OfficialShopCode);
            if (officialShopId.Length == 0 || officialShopCode.Length == 0)
            {
                return Blocked(isCatalogBound: true, OfficialShopPartialCode);
            }

            if (HasMismatch(boundShopId, boundShopCode, officialShopId, officialShopCode))
            {
                return Blocked(isCatalogBound: true, ShopMismatchCode);
            }

            if (!TryParseOptionalBinaryFlag(snapshot.RepairRequired, out var repairRequired))
            {
                return Blocked(isCatalogBound: true, RepairStateInvalidCode);
            }

            if (repairRequired)
            {
                return Blocked(isCatalogBound: true, RepairRequiredCode);
            }

            if (string.IsNullOrWhiteSpace(snapshot.SaleSafeAt))
            {
                return Blocked(isCatalogBound: true, NotSaleSafeCode);
            }

            if (string.IsNullOrWhiteSpace(snapshot.ExactnessStatus))
            {
                return Blocked(isCatalogBound: true, ExactnessMissingCode);
            }

            if (!Enum.TryParse(snapshot.ExactnessStatus, true, out ExactnessStatus exactnessStatus) ||
                exactnessStatus != ExactnessStatus.Verified)
            {
                return Blocked(isCatalogBound: true, ExactnessNotVerifiedCode);
            }

            var exactnessShopId = NormalizeId(snapshot.ExactnessShopId);
            var exactnessShopCode = NormalizeCode(snapshot.ExactnessShopCode);
            if (exactnessShopId.Length == 0 || exactnessShopCode.Length == 0)
            {
                return Blocked(isCatalogBound: true, ExactnessBindingPartialCode);
            }

            if (HasMismatch(exactnessShopId, exactnessShopCode, officialShopId, officialShopCode))
            {
                return Blocked(isCatalogBound: true, ExactnessShopMismatchCode);
            }

            return Safe(isCatalogBound: true, SafeCode);
        }

        private static CatalogSaleSafetyDecision Blocked(bool isCatalogBound, string reasonCode)
        {
            return new CatalogSaleSafetyDecision(false, isCatalogBound, reasonCode);
        }

        private static bool HasMismatch(
            string originShopId,
            string originShopCode,
            string trustedShopId,
            string trustedShopCode)
        {
            if (originShopCode.Length == 0 || trustedShopCode.Length == 0)
            {
                return true;
            }

            return !string.Equals(originShopCode, trustedShopCode, StringComparison.Ordinal) ||
                (originShopId.Length > 0 &&
                 (trustedShopId.Length == 0 ||
                  !string.Equals(originShopId, trustedShopId, StringComparison.OrdinalIgnoreCase)));
        }

        private static string NormalizeCode(string value)
        {
            return (value ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static string NormalizeId(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static CatalogSaleSafetyDecision Safe(bool isCatalogBound, string reasonCode)
        {
            return new CatalogSaleSafetyDecision(true, isCatalogBound, reasonCode);
        }

        private static bool TryParseOptionalBinaryFlag(string value, out bool parsed)
        {
            if (string.IsNullOrEmpty(value) || string.Equals(value, "0", StringComparison.Ordinal))
            {
                parsed = false;
                return true;
            }

            if (string.Equals(value, "1", StringComparison.Ordinal))
            {
                parsed = true;
                return true;
            }

            parsed = false;
            return false;
        }

        private enum ExactnessStatus
        {
            Unverified = 0,
            Verified = 1,
            Mismatch = 2
        }
    }
}
