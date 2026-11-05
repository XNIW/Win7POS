using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Win7POS.Core.Import
{
    public enum HistoricalSalesProfile { GetnetLegacy, GetnetModern }
    public enum HistoricalSalesMovementKind { Sale, Cancellation }
    public enum HistoricalSalesCardSubtype { Unknown, Debit, Credit, Prepaid }
    public enum HistoricalSalesModernTimeInterpretation { Unresolved, IsoUtc, ExportedLocal }
    public enum HistoricalSalesDiagnosticSeverity { Warning, Error }

    public sealed class HistoricalSalesImportOptions
    {
        public HistoricalSalesModernTimeInterpretation ModernTimeInterpretation { get; set; }
        public string TemporalPolicyVersion { get; set; } = "getnet-commercial-time-v1";
        public string TemporalEvidenceReference { get; set; } = string.Empty;
        public bool LegacyClpConfirmed { get; set; }

        public HistoricalSalesImportOptions Snapshot()
        {
            if (!Enum.IsDefined(typeof(HistoricalSalesModernTimeInterpretation), ModernTimeInterpretation))
                throw new ArgumentOutOfRangeException(nameof(ModernTimeInterpretation));
            if (string.IsNullOrWhiteSpace(TemporalPolicyVersion) || TemporalPolicyVersion.Length > 160 ||
                (TemporalEvidenceReference ?? string.Empty).Length > 2048)
                throw new ArgumentException("historical_import_policy_invalid");
            return new HistoricalSalesImportOptions
            {
                ModernTimeInterpretation = ModernTimeInterpretation,
                TemporalPolicyVersion = TemporalPolicyVersion,
                TemporalEvidenceReference = TemporalEvidenceReference ?? string.Empty,
                LegacyClpConfirmed = LegacyClpConfirmed
            };
        }
    }

    public sealed class HistoricalSalesSource
    {
        public HistoricalSalesSource(string fileId, string sheetName, int rowNumber)
        {
            FileId = fileId ?? throw new ArgumentNullException(nameof(fileId));
            SheetName = sheetName ?? string.Empty;
            RowNumber = rowNumber;
        }
        public string FileId { get; }
        public string SheetName { get; }
        public int RowNumber { get; }
    }

    public sealed class HistoricalSalesMerchant
    {
        public HistoricalSalesMerchant(string local, string seller, string sellerId, string localNumber, string rut)
        {
            Local = local ?? string.Empty; Seller = seller ?? string.Empty;
            SellerId = sellerId ?? string.Empty; LocalNumber = localNumber ?? string.Empty; Rut = rut ?? string.Empty;
        }
        public string Local { get; }
        public string Seller { get; }
        public string SellerId { get; }
        public string LocalNumber { get; }
        public string Rut { get; }
        public bool HasEvidence => Local.Length + Seller.Length + SellerId.Length + LocalNumber.Length + Rut.Length > 0;
    }

    public sealed class HistoricalSalesTimestamp
    {
        public HistoricalSalesTimestamp(string rawTimestamp, string rawOffset, int? offsetMinutes,
            DateTime? businessDateTime, DateTime? isoBusinessDateTime, DateTime? exportedLocalDateTime,
            DateTimeOffset? occurredAtUtc, HistoricalSalesModernTimeInterpretation interpretation,
            string policyVersion, string evidenceReference, bool needsEvidence)
        {
            RawTimestamp = rawTimestamp ?? string.Empty; RawOffset = rawOffset ?? string.Empty;
            OffsetMinutes = offsetMinutes;
            BusinessDateTime = Unspecified(businessDateTime);
            IsoBusinessDateTime = Unspecified(isoBusinessDateTime);
            ExportedLocalDateTime = Unspecified(exportedLocalDateTime);
            OccurredAtUtc = occurredAtUtc?.ToUniversalTime(); Interpretation = interpretation;
            PolicyVersion = policyVersion ?? string.Empty; EvidenceReference = evidenceReference ?? string.Empty;
            NeedsEvidence = needsEvidence;
        }
        private static DateTime? Unspecified(DateTime? value) => value.HasValue
            ? DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified) : (DateTime?)null;
        public string RawTimestamp { get; }
        public string RawOffset { get; }
        public int? OffsetMinutes { get; }
        public DateTime? BusinessDateTime { get; }
        public DateTime? IsoBusinessDateTime { get; }
        public DateTime? ExportedLocalDateTime { get; }
        public DateTimeOffset? OccurredAtUtc { get; }
        public HistoricalSalesModernTimeInterpretation Interpretation { get; }
        public string PolicyVersion { get; }
        public string EvidenceReference { get; }
        public bool NeedsEvidence { get; }
    }

    public sealed class HistoricalSalesTransaction
    {
        public HistoricalSalesTransaction(HistoricalSalesSource source, HistoricalSalesProfile profile,
            string externalNamespace, string externalId, string paymentId, string authorizationCode,
            HistoricalSalesMerchant merchant, HistoricalSalesTimestamp timestamp,
            HistoricalSalesMovementKind movementKind, string transactionStatus, string settlementStatus,
            long grossClp, long? commissionClp, long? commissionVatClp, long? netClp,
            HistoricalSalesCardSubtype cardSubtype, string originalCardType, string originalPrepaid,
            string terminal, string rawSettlementDate)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source)); Profile = profile;
            ExternalNamespace = externalNamespace ?? throw new ArgumentNullException(nameof(externalNamespace));
            ExternalId = externalId ?? throw new ArgumentNullException(nameof(externalId));
            PaymentId = paymentId ?? string.Empty; AuthorizationCode = authorizationCode ?? string.Empty;
            Merchant = merchant ?? throw new ArgumentNullException(nameof(merchant));
            Timestamp = timestamp ?? throw new ArgumentNullException(nameof(timestamp));
            MovementKind = movementKind; TransactionStatus = transactionStatus ?? string.Empty;
            SettlementStatus = settlementStatus ?? string.Empty; GrossClp = grossClp;
            CommissionClp = commissionClp; CommissionVatClp = commissionVatClp; NetClp = netClp;
            CardSubtype = cardSubtype; OriginalCardType = originalCardType ?? string.Empty;
            OriginalPrepaid = originalPrepaid ?? string.Empty; Terminal = terminal ?? string.Empty;
            RawSettlementDate = rawSettlementDate ?? string.Empty;
        }
        public HistoricalSalesSource Source { get; }
        public HistoricalSalesProfile Profile { get; }
        public string ProfileVersion => Profile == HistoricalSalesProfile.GetnetLegacy ? "getnet-legacy-v1" : "getnet-modern-v1";
        public string ParserVersion => "getnet-ooxml-v1";
        public string Provider => "getnet";
        public string Currency => "CLP";
        public string PaymentMethod => "card";
        public string ExternalNamespace { get; }
        public string ExternalId { get; }
        public string PaymentId { get; }
        public string AuthorizationCode { get; }
        public HistoricalSalesMerchant Merchant { get; }
        public HistoricalSalesTimestamp Timestamp { get; }
        public DateTime? BusinessDateTime => Timestamp.BusinessDateTime;
        public HistoricalSalesMovementKind MovementKind { get; }
        public string TransactionStatus { get; }
        public string SettlementStatus { get; }
        public long GrossClp { get; }
        public long? CommissionClp { get; }
        public long? CommissionVatClp { get; }
        public long? NetClp { get; }
        public HistoricalSalesCardSubtype CardSubtype { get; }
        public string OriginalCardType { get; }
        public string OriginalPrepaid { get; }
        public string Terminal { get; }
        public string RawSettlementDate { get; }
        public int? OriginalCashierId => null;
    }

    public sealed class HistoricalSalesRejectedRow
    {
        public HistoricalSalesRejectedRow(HistoricalSalesSource source, string transactionStatus, long? requestedClp)
        { Source = source; TransactionStatus = transactionStatus; RequestedClp = requestedClp; }
        public HistoricalSalesSource Source { get; }
        public string TransactionStatus { get; }
        public long? RequestedClp { get; }
    }

    public sealed class HistoricalSalesImportDiagnostic
    {
        public HistoricalSalesImportDiagnostic(string code, HistoricalSalesDiagnosticSeverity severity,
            HistoricalSalesSource source, string field = "", int? columnNumber = null)
        { Code = code; Severity = severity; Source = source; Field = field ?? string.Empty; ColumnNumber = columnNumber; }
        public string Code { get; }
        public HistoricalSalesDiagnosticSeverity Severity { get; }
        public HistoricalSalesSource Source { get; }
        public string Field { get; }
        public int? ColumnNumber { get; }
    }

    public sealed class HistoricalSalesImportFile
    {
        public HistoricalSalesImportFile(string fileId, string path, string sha256, long bytes,
            string sheetName, HistoricalSalesProfile? profile, int sourceRows)
        { FileId = fileId; Path = path; Sha256 = sha256; Bytes = bytes; SheetName = sheetName; Profile = profile; SourceRows = sourceRows; }
        public string FileId { get; }
        // Local provenance only. These values must never be emitted by public logging/QA DTOs.
        public string Path { get; }
        public string Sha256 { get; }
        public long Bytes { get; }
        public string SheetName { get; }
        public HistoricalSalesProfile? Profile { get; }
        public int SourceRows { get; }
    }

    public sealed class HistoricalSalesImportSummary
    {
        public HistoricalSalesImportSummary(int sourceRows, int accepted, int sales, int cancellations, int rejected,
            long? grossClp, long? positiveGrossClp, long? cancellationGrossClp, long? knownNetClp,
            int knownNetCount, long? rejectedRequestedClp, int knownRejectedRequestedCount,
            DateTime? fromBusinessDateTime, DateTime? toBusinessDateTime,
            DateTime? fromIsoCandidateDateTime = null, DateTime? toIsoCandidateDateTime = null,
            DateTime? fromExportedLocalCandidateDateTime = null, DateTime? toExportedLocalCandidateDateTime = null)
        {
            SourceRows = sourceRows; AcceptedCount = accepted; SaleCount = sales; CancellationCount = cancellations;
            RejectedCount = rejected; GrossClp = grossClp; PositiveGrossClp = positiveGrossClp;
            CancellationGrossClp = cancellationGrossClp; KnownNetClp = knownNetClp; KnownNetCount = knownNetCount;
            RejectedRequestedClp = rejectedRequestedClp; KnownRejectedRequestedCount = knownRejectedRequestedCount;
            FromBusinessDateTime = fromBusinessDateTime; ToBusinessDateTime = toBusinessDateTime;
            FromIsoCandidateDateTime = fromIsoCandidateDateTime; ToIsoCandidateDateTime = toIsoCandidateDateTime;
            FromExportedLocalCandidateDateTime = fromExportedLocalCandidateDateTime; ToExportedLocalCandidateDateTime = toExportedLocalCandidateDateTime;
        }
        public int SourceRows { get; }
        public int AcceptedCount { get; }
        public int SaleCount { get; }
        public int CancellationCount { get; }
        public int RejectedCount { get; }
        public long? GrossClp { get; }
        public long? PositiveGrossClp { get; }
        public long? CancellationGrossClp { get; }
        public long? KnownNetClp { get; }
        public int KnownNetCount { get; }
        public bool NetCoverageComplete => AcceptedCount == KnownNetCount;
        public long? RejectedRequestedClp { get; }
        public int KnownRejectedRequestedCount { get; }
        public DateTime? FromBusinessDateTime { get; }
        public DateTime? ToBusinessDateTime { get; }
        public DateTime? FromIsoCandidateDateTime { get; }
        public DateTime? ToIsoCandidateDateTime { get; }
        public DateTime? FromExportedLocalCandidateDateTime { get; }
        public DateTime? ToExportedLocalCandidateDateTime { get; }
    }

    public sealed class HistoricalSalesImportAnalysis
    {
        public HistoricalSalesImportAnalysis(IEnumerable<HistoricalSalesImportFile> files,
            IEnumerable<HistoricalSalesTransaction> acceptedTransactions, IEnumerable<HistoricalSalesRejectedRow> rejectedRows,
            IEnumerable<HistoricalSalesImportDiagnostic> diagnostics, HistoricalSalesImportSummary summary,
            bool needsEvidence)
        {
            Files = Freeze(files); AcceptedTransactions = Freeze(acceptedTransactions); RejectedRows = Freeze(rejectedRows);
            Diagnostics = Freeze(diagnostics); Summary = summary; NeedsEvidence = needsEvidence;
        }
        private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => new ReadOnlyCollection<T>(values.ToArray());
        public IReadOnlyList<HistoricalSalesImportFile> Files { get; }
        public IReadOnlyList<HistoricalSalesTransaction> AcceptedTransactions { get; }
        public IReadOnlyList<HistoricalSalesRejectedRow> RejectedRows { get; }
        public IReadOnlyList<HistoricalSalesImportDiagnostic> Diagnostics { get; }
        public HistoricalSalesImportSummary Summary { get; }
        public bool NeedsEvidence { get; }
        public bool HasErrors => Diagnostics.Any(x => x.Severity == HistoricalSalesDiagnosticSeverity.Error);
        // Additional shop, merchant, overlap, authorization and DB checks are required by the applier.
        public bool CanApply => AcceptedTransactions.Count > 0 && !HasErrors && !NeedsEvidence;
    }
}
