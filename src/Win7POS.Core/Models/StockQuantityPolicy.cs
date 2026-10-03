using System;
using System.Globalization;

namespace Win7POS.Core.Models
{
    public static class StockQuantityPolicy
    {
        // Admin catalog-import-sync's nonNegativeNumber bound; article mutation has a wider limit.
        public const decimal MaximumImportQuantity = 999999999m;
        // Local writes use the existing stock magnitude and the mutation contract's three-digit scale.
        // Remote transport reads are separate: never silently round server quantities to this write policy.
        public static bool IsValid(decimal value) => value >= 0 && value <= int.MaxValue && decimal.Round(value, 3) == value;

        public static decimal FromTransport(double? value)
        {
            if (!value.HasValue) return 0;
            if (double.IsNaN(value.Value) || double.IsInfinity(value.Value) ||
                value.Value < 0 || value.Value > int.MaxValue)
                throw new ArgumentException("Invalid stock quantity.");
            return Convert.ToDecimal(value.Value, CultureInfo.InvariantCulture);
        }

        public static bool TryParse(string text, out decimal value)
        {
            const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint |
                NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
            return (decimal.TryParse(text, styles, CultureInfo.CurrentCulture, out value) ||
                    decimal.TryParse(text, styles, CultureInfo.InvariantCulture, out value)) && IsValid(value);
        }

        public static bool TryParseImport(string text, out decimal value)
        {
            // Quantity is a decimal field, unlike CLP prices. A single comma or period is the
            // decimal separator; grouped/mixed separators are rejected instead of guessed.
            var canonical = (text ?? string.Empty).Trim().Replace(',', '.');
            return decimal.TryParse(canonical, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out value) && IsValid(value) && value <= MaximumImportQuantity;
        }
    }
}
