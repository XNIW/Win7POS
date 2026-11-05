using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Reports;

namespace Win7POS.Wpf.Pos
{
    public sealed class SalesReportPublicationRequest
    {
        internal SalesReportPublicationRequest(SalesReportReadToken token, string temporaryPath, string destinationPath, string hash, long bytes, bool overwrite)
        { Token = token; TemporaryPath = temporaryPath; DestinationPath = destinationPath; ContentSha256 = hash; ContentBytes = bytes; OverwriteAllowed = overwrite; }
        public SalesReportReadToken Token { get; }
        public string TemporaryPath { get; }
        public string DestinationPath { get; }
        public string ContentSha256 { get; }
        public long ContentBytes { get; }
        public bool OverwriteAllowed { get; }
    }
    public enum SalesReportPublicationOutcome { Published, FailedBeforePublication, PublicationOutcomeUnknown }
    public sealed class SalesReportExportResult
    {
        internal SalesReportExportResult(SalesReportPublicationOutcome outcome, SalesReportReadToken token, TimeSpan elapsed, string errorCode)
        { Outcome = outcome; Token = token; DestinationIoElapsed = elapsed; ErrorCode = errorCode; }
        public SalesReportPublicationOutcome Outcome { get; }
        public SalesReportReadToken Token { get; }
        public TimeSpan DestinationIoElapsed { get; }
        public bool FixtureThresholdPassed { get { return DestinationIoElapsed <= TimeSpan.FromSeconds(1); } }
        public string ErrorCode { get; }
    }

    public sealed class SalesReportExportService
    {
        public Task<SalesReportExportResult> ExportAsync(SalesReportContext context, string destinationPath, bool overwriteAllowed,
            Func<SalesReportPublicationRequest, Func<SalesReportPublicationPermit>> prepareAuthorization,
            Func<CancellationToken, Task> demandCurrentScope, CancellationToken cancellationToken, IProgress<string> progress = null)
        {
            if (context == null || prepareAuthorization == null || demandCurrentScope == null) throw new ArgumentNullException();
            if (!context.Snapshot.Coverage.CanPublish) throw new InvalidOperationException("report_coverage_not_publishable");
            return Task.Run(async () =>
            {
                var destination = Path.GetFullPath(destinationPath);
                var directory = Path.GetDirectoryName(destination);
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) throw new IOException("report_destination_unavailable");
                if (File.Exists(destination) && !overwriteAllowed) throw new IOException("report_destination_exists");
                var temporary = Path.Combine(directory, ".win7pos-report-" + Guid.NewGuid().ToString("N") + ".tmp");
                var publicationStarted = false;
                progress?.Report("preparing");
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan))
                    {
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(true), 65536, leaveOpen: true))
                        {
                            writer.WriteLine(SalesReportCsvSerializer.Header);
                            writer.WriteLine(SalesReportCsvSerializer.Serialize(SalesReportCsvRow.Metadata(context.Snapshot)));
                            var page = context.Snapshot.FirstPage;
                            while (true)
                            {
                                if (!page.Token.Equals(context.Snapshot.Token)) throw new ReportSnapshotStaleException();
                                foreach (var entry in page.Entries)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    writer.WriteLine(SalesReportCsvSerializer.Serialize(SalesReportCsvRow.Movement(context.Snapshot.Token, context.Snapshot.Coverage, entry)));
                                }
                                if (!page.HasMore) break;
                                page = await context.Query.GetPageAsync(context.Snapshot.Token, context.Filter, context.NativeTimePolicy, page.NextCursor, 200, cancellationToken).ConfigureAwait(false);
                            }
                            writer.Flush();
                        }
                        stream.Flush(flushToDisk: true);
                    }
                    var bytes = new FileInfo(temporary).Length;
                    var hash = HashFile(temporary, cancellationToken);
                    var previousHash = File.Exists(destination) ? HashFile(destination, cancellationToken) : null;
                    await demandCurrentScope(cancellationToken).ConfigureAwait(false);
                    await context.Query.ValidateAsync(context.Snapshot.Token, context.Filter, context.NativeTimePolicy, cancellationToken).ConfigureAwait(false);
                    var request = new SalesReportPublicationRequest(context.Snapshot.Token, temporary, destination, hash, bytes, overwriteAllowed);
                    // Lease/store/DB preflight occurs before the coordinator, never inside the latch callback.
                    var issuePermit = prepareAuthorization(request) ?? throw new InvalidOperationException("report_publication_authority_unavailable");
                    SalesReportPublicationPermit permit;
                    using (await context.Coordinator.EnterPublicationAsync(cancellationToken).ConfigureAwait(false))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!context.Coordinator.IsCurrent(context.Snapshot.Token)) throw new ReportSnapshotStaleException();
                        permit = issuePermit();
                        if (permit == null || !permit.Token.Equals(request.Token) || permit.ContentSha256 != hash || permit.ContentBytes != bytes ||
                            permit.TemporaryPath != temporary || permit.DestinationPath != destination || permit.OverwriteAllowed != overwriteAllowed || !permit.TryConsume())
                            throw new InvalidOperationException("report_publication_permit_mismatch");
                    }
                    // Non-cancellable destination phase: no DB connection, publication gate or authority monitor remains held.
                    publicationStarted = true;
                    progress?.Report("finalizing");
                    var elapsed = Stopwatch.StartNew();
                    var destinationMutationAttempted = false;
                    try
                    {
                        // The permit covers these exact bytes. Reopen after authorization and deny writes,
                        // while allowing the atomic rename on Windows. Delete sharing does not pin a
                        // pathname against an uncooperative external rename; product code exclusively owns this random temporary.
                        using (var approvedContent = new FileStream(temporary, FileMode.Open, FileAccess.Read,
                            FileShare.Read | FileShare.Delete, 65536, FileOptions.SequentialScan))
                        {
                            if (approvedContent.Length != permit.ContentBytes || HashStream(approvedContent, CancellationToken.None) != permit.ContentSha256)
                            {
                                elapsed.Stop();
                                return new SalesReportExportResult(SalesReportPublicationOutcome.FailedBeforePublication, permit.Token, elapsed.Elapsed, "report_publication_content_changed");
                            }
                            destinationMutationAttempted = true;
                            if (overwriteAllowed && File.Exists(destination)) File.Replace(temporary, destination, null);
                            else File.Move(temporary, destination);
                        }
                        elapsed.Stop();
                        return new SalesReportExportResult(SalesReportPublicationOutcome.Published, permit.Token, elapsed.Elapsed, null);
                    }
                    catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is NotSupportedException)
                    {
                        elapsed.Stop();
                        if (!destinationMutationAttempted)
                            return new SalesReportExportResult(SalesReportPublicationOutcome.FailedBeforePublication, permit.Token, elapsed.Elapsed, "report_publication_content_unavailable");
                        try
                        {
                            var observed = File.Exists(destination) ? HashFile(destination, CancellationToken.None) : null;
                            if (observed == hash && new FileInfo(destination).Length == bytes)
                                return new SalesReportExportResult(SalesReportPublicationOutcome.Published, permit.Token, elapsed.Elapsed, "report_destination_content_verified");
                            if (observed == previousHash)
                                return new SalesReportExportResult(SalesReportPublicationOutcome.FailedBeforePublication, permit.Token, elapsed.Elapsed, "report_destination_write_failed");
                        }
                        catch (Exception reconciliationError) when (reconciliationError is IOException || reconciliationError is UnauthorizedAccessException) { }
                        return new SalesReportExportResult(SalesReportPublicationOutcome.PublicationOutcomeUnknown, permit.Token, elapsed.Elapsed, "report_publication_outcome_unknown");
                    }
                }
                finally
                {
                    // Only this operation's random, exclusively created temporary is eligible for cleanup.
                    // Never delete or undo a destination after a late success or uncertain publication.
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (Exception cleanupError) when (cleanupError is IOException || cleanupError is UnauthorizedAccessException)
                    { if (!publicationStarted) { /* Preserve the primary failure and the private temporary. */ } }
                }
            }, cancellationToken);
        }
        private static string HashFile(string path, CancellationToken cancellationToken)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            return HashStream(stream, cancellationToken);
        }
        private static string HashStream(Stream stream, CancellationToken cancellationToken)
        {
            using var sha = SHA256.Create();
            var buffer = new byte[65536]; int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0) { cancellationToken.ThrowIfCancellationRequested(); sha.TransformBlock(buffer, 0, read, buffer, 0); }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
        }
    }
}
