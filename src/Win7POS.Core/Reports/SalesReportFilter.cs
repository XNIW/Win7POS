using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Win7POS.Core.Reports
{
    public sealed class SalesReportFilter
    {
        public SalesReportFilter(string shopId, DateTime fromInclusive, DateTime toExclusive, SalesReportOrigins origins,
            bool canViewAllOperators, int? operatorId, string ownershipPolicyHash, string nativeTimePolicyHash, string referenceSearch = null, IEnumerable<DateTime> selectedDates = null)
        {
            if (string.IsNullOrWhiteSpace(shopId)) throw new ArgumentException("An authoritative shop ID is required.", nameof(shopId));
            if (fromInclusive.Kind != DateTimeKind.Unspecified || toExclusive.Kind != DateTimeKind.Unspecified || fromInclusive >= toExclusive)
                throw new ArgumentException("A valid unspecified commercial interval is required.");
            if (origins == 0 || (origins & ~SalesReportOrigins.All) != 0) throw new ArgumentOutOfRangeException(nameof(origins));
            if ((!canViewAllOperators && !operatorId.HasValue) || (operatorId.HasValue && operatorId.Value <= 0)) throw new ArgumentException("A restricted report requires a specific operator.");
            if (string.IsNullOrWhiteSpace(ownershipPolicyHash)) throw new ArgumentException("Ownership policy is required.");
            ShopId = shopId; FromInclusive = fromInclusive; ToExclusive = toExclusive; Origins = origins;
            CanViewAllOperators = canViewAllOperators; OperatorId = operatorId; OwnershipPolicyHash = ownershipPolicyHash;
            NativeTimePolicyHash = nativeTimePolicyHash ?? string.Empty; ReferenceSearch = (referenceSearch ?? string.Empty).Trim();
            if (ReferenceSearch.Length > 160) throw new ArgumentException("Reference search exceeds its limit.");
            var dates = selectedDates == null ? new DateTime[0] : selectedDates.Select(date => DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified)).Distinct().OrderBy(date => date).ToArray();
            if (dates.Length > 366 || dates.Any(date => date < fromInclusive.Date || date >= toExclusive)) throw new ArgumentException("Selected report dates exceed the supported scope.");
            SelectedDates = new ReadOnlyCollection<DateTime>(dates);
            Fingerprint = SalesReportHash.Compute(shopId, fromInclusive.Ticks.ToString(CultureInfo.InvariantCulture), toExclusive.Ticks.ToString(CultureInfo.InvariantCulture),
                ((int)origins).ToString(CultureInfo.InvariantCulture), canViewAllOperators ? "all" : "own", operatorId.HasValue ? operatorId.Value.ToString(CultureInfo.InvariantCulture) : "", ownershipPolicyHash, NativeTimePolicyHash, ReferenceSearch,
                string.Join(",", dates.Select(date => date.Ticks.ToString(CultureInfo.InvariantCulture))));
        }
        public string ShopId { get; }
        public DateTime FromInclusive { get; }
        public DateTime ToExclusive { get; }
        public SalesReportOrigins Origins { get; }
        public bool CanViewAllOperators { get; }
        public int? OperatorId { get; }
        public bool IncludeUnknownCashier { get { return CanViewAllOperators && !OperatorId.HasValue; } }
        public string OwnershipPolicyHash { get; }
        public string NativeTimePolicyHash { get; }
        public string ReferenceSearch { get; }
        public string Fingerprint { get; }
        public IReadOnlyList<DateTime> SelectedDates { get; }
    }

    internal static class SalesReportHash
    {
        internal static string Compute(params string[] fields)
        {
            var body = new StringBuilder();
            foreach (var field in fields) { var value = field ?? string.Empty; body.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value); }
            using (var sha = SHA256.Create()) { return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(body.ToString()))).Replace("-", "").ToLowerInvariant(); }
        }
    }
}
