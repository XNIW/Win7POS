using System;
using System.Threading;

namespace Win7POS.Core.Reports
{
    /// <summary>One publication already authorized at the shared authority linearization point.</summary>
    public sealed class SalesReportPublicationPermit
    {
        private int _consumed;
        internal SalesReportPublicationPermit(string publicationId, SalesReportReadToken token, string temporaryPath,
            string destinationPath, string contentSha256, long contentBytes, bool overwriteAllowed,
            int operatorId, long authorizationEpoch, long operatorAuthorityVersion, DateTimeOffset authorizedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(publicationId) || token == null || string.IsNullOrWhiteSpace(temporaryPath) || string.IsNullOrWhiteSpace(destinationPath) ||
                string.IsNullOrWhiteSpace(contentSha256) || contentSha256.Length != 64 || contentBytes <= 0 || operatorId <= 0 || authorizationEpoch < 0 || operatorAuthorityVersion < 0)
                throw new ArgumentException("A complete report publication authorization is required.");
            PublicationId = publicationId; Token = token; TemporaryPath = temporaryPath; DestinationPath = destinationPath;
            ContentSha256 = contentSha256; ContentBytes = contentBytes; OverwriteAllowed = overwriteAllowed;
            OperatorId = operatorId; AuthorizationEpoch = authorizationEpoch; OperatorAuthorityVersion = operatorAuthorityVersion;
            AuthorizedAtUtc = authorizedAtUtc.ToUniversalTime();
        }
        public string PublicationId { get; }
        public SalesReportReadToken Token { get; }
        public string TemporaryPath { get; }
        public string DestinationPath { get; }
        public string ContentSha256 { get; }
        public long ContentBytes { get; }
        public bool OverwriteAllowed { get; }
        public int OperatorId { get; }
        public long AuthorizationEpoch { get; }
        public long OperatorAuthorityVersion { get; }
        public DateTimeOffset AuthorizedAtUtc { get; }
        public bool TryConsume() { return Interlocked.CompareExchange(ref _consumed, 1, 0) == 0; }
    }
}
