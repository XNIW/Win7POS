using System;

namespace Win7POS.Data.Repositories
{
    /// <summary>
    /// Shared product-identity rules for both local and remote catalog writers.
    /// </summary>
    internal static class ProductIdentityPolicy
    {
        internal static bool IsReservedBarcode(string barcode)
        {
            if (string.IsNullOrEmpty(barcode)) return false;
            return barcode.StartsWith("DISC:", StringComparison.Ordinal)
                || barcode.StartsWith("MANUAL:", StringComparison.Ordinal);
        }
    }
}
