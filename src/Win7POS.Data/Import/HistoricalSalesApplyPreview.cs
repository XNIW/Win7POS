using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Serialization;
using Win7POS.Core.Import;

namespace Win7POS.Data.Import
{
    [DataContract]
    public sealed class HistoricalSalesEconomicValue
    {
        public HistoricalSalesEconomicValue(long businessTicks, long grossClp, long? feeClp, long? feeVatClp, long? netClp, string movementKind, string transactionState)
        { BusinessTicks = businessTicks; GrossClp = grossClp; FeeClp = feeClp; FeeVatClp = feeVatClp; NetClp = netClp; MovementKind = movementKind ?? ""; TransactionState = HistoricalSalesValueRules.NormalizeHeader(transactionState); }
        [DataMember] public long BusinessTicks { get; private set; }
        [DataMember] public long GrossClp { get; private set; }
        [DataMember] public long? FeeClp { get; private set; }
        [DataMember] public long? FeeVatClp { get; private set; }
        [DataMember] public long? NetClp { get; private set; }
        [DataMember] public string MovementKind { get; private set; }
        [DataMember] public string TransactionState { get; private set; }
        public string Fingerprint => HistoricalSalesImportPlan.Hash(new[] { "historical-economics-v1", BusinessTicks.ToString(CultureInfo.InvariantCulture), GrossClp.ToString(CultureInfo.InvariantCulture),
            FeeClp?.ToString(CultureInfo.InvariantCulture) ?? "null", FeeVatClp?.ToString(CultureInfo.InvariantCulture) ?? "null", NetClp?.ToString(CultureInfo.InvariantCulture) ?? "null", MovementKind, TransactionState });
        internal bool ConflictsWith(HistoricalSalesEconomicValue other) => other == null || BusinessTicks != other.BusinessTicks || GrossClp != other.GrossClp ||
            MovementKind != other.MovementKind || TransactionState != other.TransactionState || KnownDiffers(FeeClp, other.FeeClp) || KnownDiffers(FeeVatClp, other.FeeVatClp) || KnownDiffers(NetClp, other.NetClp);
        private static bool KnownDiffers(long? a, long? b) => a.HasValue && b.HasValue && a.Value != b.Value;
    }

    public sealed class HistoricalSalesCorrectionCandidate
    {
        internal HistoricalSalesCorrectionCandidate(long previousId, HistoricalSalesEconomicValue previous, HistoricalSalesEconomicValue proposed, string fileId, string sheet, int row, string context)
        { PreviousTransactionId = previousId; Previous = previous; Proposed = proposed; SourceFileId = fileId; SourceSheet = sheet; SourceRow = row; ContextFingerprint = context; }
        public long PreviousTransactionId { get; }
        public HistoricalSalesEconomicValue Previous { get; }
        public HistoricalSalesEconomicValue Proposed { get; }
        public string SourceFileId { get; }
        public string SourceSheet { get; }
        public int SourceRow { get; }
        public string ContextFingerprint { get; }
        public HistoricalSalesCorrectionApproval Approve(string evidenceReference) => new HistoricalSalesCorrectionApproval(this, evidenceReference);
        internal bool Matches(HistoricalSalesCorrectionApproval a) => a != null && a.PreviousTransactionId == PreviousTransactionId && a.SourceFileId == SourceFileId &&
            a.SourceSheet == SourceSheet && a.SourceRow == SourceRow && a.ContextFingerprint == ContextFingerprint && a.Previous.Fingerprint == Previous.Fingerprint && a.Proposed.Fingerprint == Proposed.Fingerprint;
    }

    [DataContract]
    public sealed class HistoricalSalesCorrectionApproval
    {
        internal HistoricalSalesCorrectionApproval(HistoricalSalesCorrectionCandidate candidate, string evidenceReference)
        {
            PreviousTransactionId = candidate.PreviousTransactionId; Previous = candidate.Previous; Proposed = candidate.Proposed;
            SourceFileId = candidate.SourceFileId; SourceSheet = candidate.SourceSheet; SourceRow = candidate.SourceRow;
            ContextFingerprint = candidate.ContextFingerprint;
            EvidenceReference = HistoricalMerchantAssignment.Required(evidenceReference);
        }
        [DataMember] public long PreviousTransactionId { get; private set; }
        [DataMember] public HistoricalSalesEconomicValue Previous { get; private set; }
        [DataMember] public HistoricalSalesEconomicValue Proposed { get; private set; }
        [DataMember] public string SourceFileId { get; private set; }
        [DataMember] public string SourceSheet { get; private set; }
        [DataMember] public int SourceRow { get; private set; }
        [DataMember] public string ContextFingerprint { get; private set; }
        [DataMember] public string EvidenceReference { get; private set; }
        internal string Fingerprint => HistoricalSalesImportPlan.Hash(new[] { PreviousTransactionId.ToString(CultureInfo.InvariantCulture), Previous.Fingerprint, Proposed.Fingerprint,
            SourceFileId, SourceSheet, SourceRow.ToString(CultureInfo.InvariantCulture), ContextFingerprint, EvidenceReference });
    }

    public sealed class HistoricalSalesApplyPreview
    {
        public const int MaxDisplayedCorrections = 200;
        private readonly List<HistoricalSalesCorrectionCandidate> _corrections = new List<HistoricalSalesCorrectionCandidate>();
        public IReadOnlyList<HistoricalSalesCorrectionCandidate> Corrections => new ReadOnlyCollection<HistoricalSalesCorrectionCandidate>(_corrections);
        public int CorrectionCount { get; internal set; }
        public int UnapprovedCorrectionCount { get; internal set; }
        public bool CorrectionsTruncated => CorrectionCount > _corrections.Count;
        public bool CanApply => ConflictCount == 0 && UnapprovedCorrectionCount == 0 && !CorrectionsTruncated;
        internal void AddCorrection(HistoricalSalesCorrectionCandidate candidate) { CorrectionCount++; if (_corrections.Count < MaxDisplayedCorrections) _corrections.Add(candidate); }
        public int NewCount { get; internal set; }
        public int DuplicateCount { get; internal set; }
        public int ConflictCount { get; internal set; }
        public long NewGrossClp { get; internal set; }
        public long KnownNewNetClp { get; internal set; }
        public int KnownNewNetCount { get; internal set; }
    }
}
