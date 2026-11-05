using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Xml;
using Win7POS.Core.Import;

namespace Win7POS.Data.Import
{
    public sealed class HistoricalSalesImportAnalyzer
    {
        private readonly Action<CancellationToken> _parsedForTesting;
        public HistoricalSalesImportAnalyzer() { }
        internal HistoricalSalesImportAnalyzer(Action<CancellationToken> parsedForTesting) { _parsedForTesting = parsedForTesting; }
        public HistoricalSalesImportAnalysis Analyze(IReadOnlyList<string> filePaths, HistoricalSalesImportOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (filePaths == null) throw new ArgumentNullException(nameof(filePaths));
            if (filePaths.Count == 0 || filePaths.Count > GetnetExcelImportLimits.MaximumFiles) throw new ArgumentException("getnet_selection_file_limit");
            var policy = (options ?? new HistoricalSalesImportOptions()).Snapshot();
            var paths = filePaths.Select(Path.GetFullPath).ToArray();
            var files = new List<HistoricalSalesImportFile>(); var transactions = new List<HistoricalSalesTransaction>();
            var rejected = new List<HistoricalSalesRejectedRow>(); var diagnostics = new List<HistoricalSalesImportDiagnostic>();
            long bytes = 0; var rows = 0;
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileId = Guid.NewGuid().ToString("N");
                try
                {
                    bytes = checked(bytes + new FileInfo(path).Length);
                    if (bytes > GetnetExcelImportLimits.MaximumSelectionBytes) throw new GetnetImportException("getnet_selection_byte_limit");
                    var result = GetnetExcelImportReader.Read(path, fileId, policy, cancellationToken);
                    files.Add(result.File); rows = checked(rows + result.File.SourceRows);
                    if (rows > GetnetExcelImportLimits.MaximumSelectionRows) throw new GetnetImportException("getnet_selection_row_limit");
                    transactions.AddRange(result.Transactions); rejected.AddRange(result.Rejected); diagnostics.AddRange(result.Diagnostics);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is GetnetImportException || error is IOException || error is UnauthorizedAccessException ||
                    error is XmlException || error is InvalidDataException || error is OverflowException)
                {
                    var specific = error as GetnetImportException;
                    diagnostics.Add(new HistoricalSalesImportDiagnostic(specific?.Code ?? (error is UnauthorizedAccessException ? "getnet_file_access_denied" : "getnet_corrupt_or_unreadable"),
                        HistoricalSalesDiagnosticSeverity.Error, new HistoricalSalesSource(fileId, string.Empty, specific?.Row ?? 0), specific?.Field ?? string.Empty, specific?.Column));
                    if (!files.Any(x => x.FileId == fileId)) files.Add(new HistoricalSalesImportFile(fileId, path, string.Empty, 0, string.Empty, null, 0));
                }
                if (diagnostics.Count >= GetnetExcelImportLimits.MaximumDiagnostics) break;
            }
            // Test-only observer runs inside the real analyzer body, after file
            // parsing and before its summary/result. Normal callers have no observer.
            cancellationToken.ThrowIfCancellationRequested();
            _parsedForTesting?.Invoke(cancellationToken);
            var needsEvidence = transactions.Any(x => x.Timestamp.NeedsEvidence ||
                (x.Profile == HistoricalSalesProfile.GetnetLegacy && !policy.LegacyClpConfirmed));
            if (needsEvidence) diagnostics.Add(new HistoricalSalesImportDiagnostic("getnet_policy_needs_evidence", HistoricalSalesDiagnosticSeverity.Warning,
                new HistoricalSalesSource(string.Empty, string.Empty, 0)));
            bool overflow;
            var summary = Summarize(rows, transactions, rejected, out overflow);
            if (overflow) diagnostics.Add(new HistoricalSalesImportDiagnostic("getnet_total_overflow", HistoricalSalesDiagnosticSeverity.Error,
                new HistoricalSalesSource(string.Empty, string.Empty, 0)));
            return new HistoricalSalesImportAnalysis(files, transactions, rejected, diagnostics, summary, needsEvidence);
        }

        private static HistoricalSalesImportSummary Summarize(int rows, List<HistoricalSalesTransaction> transactions,
            List<HistoricalSalesRejectedRow> rejected, out bool overflow)
        {
            overflow = false; long gross = 0, positive = 0, negative = 0, net = 0, requests = 0;
            var sales = 0; var cancellations = 0; var netCount = 0; var requestCount = 0;
            DateTime? from = null, to = null;
            DateTime? isoFrom = null, isoTo = null, localFrom = null, localTo = null;
            var allBusinessKnown = true;
            foreach (var tx in transactions)
            {
                if (tx.MovementKind == HistoricalSalesMovementKind.Sale) sales++; else cancellations++;
                if (tx.NetClp.HasValue) netCount++;
                if (tx.BusinessDateTime.HasValue)
                {
                    var date = tx.BusinessDateTime.Value;
                    if (!from.HasValue || date < from.Value) from = date;
                    if (!to.HasValue || date > to.Value) to = date;
                }
                else allBusinessKnown = false;
                var iso = tx.Timestamp.IsoBusinessDateTime ?? tx.BusinessDateTime;
                var local = tx.Timestamp.ExportedLocalDateTime ?? tx.BusinessDateTime;
                if (iso.HasValue)
                { if (!isoFrom.HasValue || iso.Value < isoFrom.Value) isoFrom = iso; if (!isoTo.HasValue || iso.Value > isoTo.Value) isoTo = iso; }
                if (local.HasValue)
                { if (!localFrom.HasValue || local.Value < localFrom.Value) localFrom = local; if (!localTo.HasValue || local.Value > localTo.Value) localTo = local; }
                try
                {
                    gross = checked(gross + tx.GrossClp);
                    if (tx.MovementKind == HistoricalSalesMovementKind.Sale) positive = checked(positive + tx.GrossClp);
                    else negative = checked(negative + tx.GrossClp);
                    if (tx.NetClp.HasValue) net = checked(net + tx.NetClp.Value);
                }
                catch (OverflowException) { overflow = true; }
            }
            foreach (var row in rejected)
                if (row.RequestedClp.HasValue)
                { requestCount++; try { requests = checked(requests + row.RequestedClp.Value); } catch (OverflowException) { overflow = true; } }
            return new HistoricalSalesImportSummary(rows, transactions.Count, sales, cancellations, rejected.Count,
                overflow ? (long?)null : gross, overflow ? (long?)null : positive, overflow ? (long?)null : negative,
                overflow ? (long?)null : net, netCount, overflow ? (long?)null : requests, requestCount,
                allBusinessKnown ? from : null, allBusinessKnown ? to : null, isoFrom, isoTo, localFrom, localTo);
        }
    }
}
