using System;

namespace Win7POS.Core.Reports
{
    public sealed class SalesReportCsvRow
    {
        private SalesReportCsvRow(SalesReportReadToken token, SalesReportCoverage coverage, SalesReportSummary summary, SalesReportEntry entry)
        {
            Token = token ?? throw new ArgumentNullException(nameof(token)); Coverage = coverage ?? throw new ArgumentNullException(nameof(coverage));
            if (!coverage.CanPublish) throw new InvalidOperationException("report_coverage_not_publishable");
            Summary = summary; Entry = entry;
        }
        public SalesReportReadToken Token { get; }
        public SalesReportCoverage Coverage { get; }
        public SalesReportSummary Summary { get; }
        public SalesReportEntry Entry { get; }
        public bool IsMetadata { get { return Entry == null; } }
        public static SalesReportCsvRow Metadata(SalesReportSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            return new SalesReportCsvRow(snapshot.Token, snapshot.Coverage, snapshot.Summary, null);
        }
        public static SalesReportCsvRow Movement(SalesReportReadToken token, SalesReportCoverage coverage, SalesReportEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            return new SalesReportCsvRow(token, coverage, null, entry);
        }
    }
}
