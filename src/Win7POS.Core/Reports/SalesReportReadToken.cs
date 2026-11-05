using System;
using System.Threading;
using System.Threading.Tasks;

namespace Win7POS.Core.Reports
{
    public sealed class SalesReportAuthorityStamp
    {
        public SalesReportAuthorityStamp(long restoreEpoch, string authorityInstanceEpoch)
        {
            if (restoreEpoch < 0 || string.IsNullOrWhiteSpace(authorityInstanceEpoch)) throw new ArgumentException("An active database authority is required.");
            RestoreEpoch = restoreEpoch; AuthorityInstanceEpoch = authorityInstanceEpoch;
        }
        public long RestoreEpoch { get; }
        public string AuthorityInstanceEpoch { get; }
    }

    public sealed class SalesReportReadToken : IEquatable<SalesReportReadToken>
    {
        public SalesReportReadToken(string datasetId, long restoreEpoch, string authorityInstanceEpoch, long dataRevision,
            string scopeHash, string ownershipPolicyHash, string nativeTimePolicyHash)
        {
            if (string.IsNullOrWhiteSpace(datasetId) || restoreEpoch < 0 || dataRevision < 0 || string.IsNullOrWhiteSpace(authorityInstanceEpoch) || string.IsNullOrWhiteSpace(scopeHash) || string.IsNullOrWhiteSpace(ownershipPolicyHash))
                throw new ArgumentException("A complete report token is required.");
            DatasetId = datasetId; RestoreEpoch = restoreEpoch; AuthorityInstanceEpoch = authorityInstanceEpoch; DataRevision = dataRevision;
            ScopeHash = scopeHash; OwnershipPolicyHash = ownershipPolicyHash; NativeTimePolicyHash = nativeTimePolicyHash ?? string.Empty;
            Fingerprint = SalesReportHash.Compute(datasetId, restoreEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture), authorityInstanceEpoch,
                dataRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), scopeHash, ownershipPolicyHash, NativeTimePolicyHash);
        }
        public string DatasetId { get; }
        public long RestoreEpoch { get; }
        public string AuthorityInstanceEpoch { get; }
        public long DataRevision { get; }
        public string ScopeHash { get; }
        public string OwnershipPolicyHash { get; }
        public string NativeTimePolicyHash { get; }
        public string Fingerprint { get; }
        public bool Equals(SalesReportReadToken other) { return other != null && string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal); }
        public override bool Equals(object obj) { return Equals(obj as SalesReportReadToken); }
        public override int GetHashCode() { return Fingerprint.GetHashCode(); }
    }

    public sealed class ReportSnapshotStaleException : InvalidOperationException
    {
        public ReportSnapshotStaleException() : base("report_snapshot_stale") { }
    }

    public interface ISalesReportQuery
    {
        Task<SalesReportSnapshot> OpenAsync(SalesReportFilter filter, SalesReportTimePolicy nativeTimePolicy, int pageSize, CancellationToken cancellationToken);
        Task<SalesReportPage> GetPageAsync(SalesReportReadToken token, SalesReportFilter filter, SalesReportTimePolicy nativeTimePolicy, SalesReportCursor cursor, int pageSize, CancellationToken cancellationToken);
        Task<SalesReportEntry> GetEntryAsync(SalesReportReadToken token, SalesReportFilter filter, SalesReportTimePolicy nativeTimePolicy, SalesReportEntryKey key, CancellationToken cancellationToken);
        Task ValidateAsync(SalesReportReadToken token, SalesReportFilter filter, SalesReportTimePolicy nativeTimePolicy, CancellationToken cancellationToken);
    }
}
