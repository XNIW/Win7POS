using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace Win7POS.Core.Reports
{
    public sealed class SalesReportUtcSegment
    {
        public SalesReportUtcSegment(long fromUtcMs, long toUtcMs, int offsetMinutes)
        {
            if (fromUtcMs >= toUtcMs || offsetMinutes < -840 || offsetMinutes > 840) throw new ArgumentException("Invalid canonical UTC segment.");
            // Reject ranges that cannot be represented as native millisecond timestamps.
            DateTimeOffset.FromUnixTimeMilliseconds(fromUtcMs);
            DateTimeOffset.FromUnixTimeMilliseconds(checked(toUtcMs - 1));
            FromUtcMs = fromUtcMs; ToUtcMs = toUtcMs; OffsetMinutes = offsetMinutes;
        }
        public long FromUtcMs { get; }
        public long ToUtcMs { get; }
        public int OffsetMinutes { get; }
    }

    /// <summary>Frozen transition data, independent of the machine's current time zone and OS rules.</summary>
    public sealed class SalesReportTimePolicy
    {
        public const long UnixEpochTicks = 621355968000000000L;
        public SalesReportTimePolicy(string policyId, string version, string provenance, IEnumerable<SalesReportUtcSegment> segments)
        {
            if (string.IsNullOrWhiteSpace(policyId) || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(provenance)) throw new ArgumentException("Canonical time policy identity and provenance are required.");
            var frozen = (segments ?? throw new ArgumentNullException(nameof(segments))).ToArray();
            if (frozen.Length == 0 || frozen.Length > 2048) throw new ArgumentException("Canonical time policy segment limit exceeded.");
            foreach (var segment in frozen)
            {
                if (segment == null) throw new ArgumentException("A canonical segment is required.");
                ToLocalTicks(segment.FromUtcMs, segment.OffsetMinutes);
                ToLocalTicks(segment.ToUtcMs, segment.OffsetMinutes);
            }
            for (var i = 1; i < frozen.Length; i++) if (frozen[i - 1].ToUtcMs != frozen[i].FromUtcMs) throw new ArgumentException("UTC segments must be ordered, contiguous and nonoverlapping.");
            PolicyId = policyId; Version = version; Provenance = provenance;
            Segments = new ReadOnlyCollection<SalesReportUtcSegment>(frozen);
            var fields = new List<string> { policyId, version, provenance };
            foreach (var segment in frozen) { fields.Add(segment.FromUtcMs.ToString(CultureInfo.InvariantCulture)); fields.Add(segment.ToUtcMs.ToString(CultureInfo.InvariantCulture)); fields.Add(segment.OffsetMinutes.ToString(CultureInfo.InvariantCulture)); }
            Hash = SalesReportHash.Compute(fields.ToArray());
        }
        public string PolicyId { get; }
        public string Version { get; }
        public string Provenance { get; }
        public string Hash { get; }
        public IReadOnlyList<SalesReportUtcSegment> Segments { get; }
        public long GetBusinessTicks(long utcMs)
        {
            foreach (var segment in Segments)
                if (utcMs >= segment.FromUtcMs && utcMs < segment.ToUtcMs)
                    return ToLocalTicks(utcMs, segment.OffsetMinutes);
            throw new InvalidOperationException("report_time_policy_outside_coverage");
        }
        public IReadOnlyList<SalesReportUtcSegment> GetUtcIntervals(DateTime localFromInclusive, DateTime localToExclusive)
        {
            if (localFromInclusive.Kind != DateTimeKind.Unspecified || localToExclusive.Kind != DateTimeKind.Unspecified || localFromInclusive >= localToExclusive)
                throw new ArgumentException("An unspecified commercial interval is required.");
            var first = Segments[0]; var last = Segments[Segments.Count - 1];
            if (localFromInclusive.Ticks < ToLocalTicks(first.FromUtcMs, first.OffsetMinutes) || localToExclusive.Ticks > ToLocalTicks(last.ToUtcMs, last.OffsetMinutes))
                throw new InvalidOperationException("report_time_policy_outside_coverage");
            var result = new List<SalesReportUtcSegment>();
            foreach (var segment in Segments)
            {
                var offsetTicks = checked((long)segment.OffsetMinutes * TimeSpan.TicksPerMinute);
                var from = Math.Max(segment.FromUtcMs, CeilingDivide(checked(localFromInclusive.Ticks - UnixEpochTicks - offsetTicks), TimeSpan.TicksPerMillisecond));
                var to = Math.Min(segment.ToUtcMs, CeilingDivide(checked(localToExclusive.Ticks - UnixEpochTicks - offsetTicks), TimeSpan.TicksPerMillisecond));
                if (from < to) result.Add(new SalesReportUtcSegment(from, to, segment.OffsetMinutes));
            }
            return new ReadOnlyCollection<SalesReportUtcSegment>(result);
        }
        public static long ToLocalTicks(long utcMs, int offsetMinutes)
        {
            var ticks = checked(UnixEpochTicks + checked(utcMs * TimeSpan.TicksPerMillisecond) + checked((long)offsetMinutes * TimeSpan.TicksPerMinute));
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) throw new ArgumentOutOfRangeException(nameof(utcMs));
            return ticks;
        }
        private static long CeilingDivide(long value, long divisor) { var quotient = value / divisor; return value % divisor > 0 ? checked(quotient + 1) : quotient; }
    }
}
