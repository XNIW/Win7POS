namespace Win7POS.Core.Online
{
    // Data verifies local provenance. Identity, repair and integrity failures
    // never acquire an exemption from a UI flag.
    public static class PosCatalogOperationPolicy
    {
        public const string FullRefreshRequiredAfterEmptyCode = "catalog_full_refresh_required_after_empty";
        public static bool AllowsVerifiedLocalLine(string remoteReadinessReason) =>
            remoteReadinessReason == CatalogSaleSafetyPolicy.NotSaleSafeCode ||
            remoteReadinessReason == CatalogSaleSafetyPolicy.ExactnessMissingCode ||
            remoteReadinessReason == FullRefreshRequiredAfterEmptyCode;
    }
}
