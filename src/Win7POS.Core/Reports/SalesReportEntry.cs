using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Win7POS.Core.Reports
{
    public enum SalesReportOrigin { Native = 0, Historical = 1 }
    [Flags]
    public enum SalesReportOrigins { Native = 1, Historical = 2, All = Native | Historical }
    public enum SalesReportMovementKind { Sale, Refund, Void, Cancellation }
    public enum SalesReportCoverageStatus { PolicyRequired, PendingOwnershipVerification, VerifiedSubset, VerifiedForScope, VerifiedEmpty }

    public sealed class SalesReportCoverage
    {
        public SalesReportCoverage(SalesReportCoverageStatus status) { Status = status; }
        public SalesReportCoverageStatus Status { get; }
        public bool CanPublish { get { return Status != SalesReportCoverageStatus.PolicyRequired && Status != SalesReportCoverageStatus.PendingOwnershipVerification; } }
        public bool IsCompleteForSelectedScope { get { return Status == SalesReportCoverageStatus.VerifiedForScope || Status == SalesReportCoverageStatus.VerifiedEmpty; } }
    }

    public sealed class SalesReportEntryKey : IEquatable<SalesReportEntryKey>
    {
        public SalesReportEntryKey(SalesReportOrigin origin, long internalId)
        {
            if (!Enum.IsDefined(typeof(SalesReportOrigin), origin) || internalId <= 0) throw new ArgumentOutOfRangeException(nameof(internalId));
            Origin = origin; InternalId = internalId;
        }
        public SalesReportOrigin Origin { get; }
        public long InternalId { get; }
        public bool Equals(SalesReportEntryKey other) { return other != null && Origin == other.Origin && InternalId == other.InternalId; }
        public override bool Equals(object obj) { return Equals(obj as SalesReportEntryKey); }
        public override int GetHashCode() { return ((int)Origin * 397) ^ InternalId.GetHashCode(); }
        public override string ToString() { return Origin.ToString() + ":" + InternalId.ToString(System.Globalization.CultureInfo.InvariantCulture); }
    }

    public sealed class SalesReportEntry
    {
        public SalesReportEntry(SalesReportEntryKey key, long businessTicks, SalesReportMovementKind movementKind,
            long signedAmountClp, long cashTenderedClp, long changeClp, long cardClp, int? originalCashierId,
            string reference, string provider = null, string externalNamespace = null, string externalId = null,
            string batchId = null, string sourceSheet = null, int? sourceRow = null, bool isVoided = false,
            long? relatedNativeSaleId = null, string rawTimestamp = null, int? offsetMinutes = null, string cardSubtype = null)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            BusinessDateTime = new DateTime(businessTicks, DateTimeKind.Unspecified);
            if (!Enum.IsDefined(typeof(SalesReportMovementKind), movementKind)) throw new ArgumentOutOfRangeException(nameof(movementKind));
            MovementKind = movementKind; SignedAmountClp = signedAmountClp; CashTenderedClp = cashTenderedClp;
            ChangeClp = changeClp; CashClp = checked(cashTenderedClp - changeClp); CardClp = cardClp;
            OriginalCashierId = originalCashierId; Reference = reference ?? string.Empty; Provider = provider ?? string.Empty;
            ExternalNamespace = externalNamespace ?? string.Empty; ExternalId = externalId ?? string.Empty;
            BatchId = batchId ?? string.Empty; SourceSheet = sourceSheet ?? string.Empty; SourceRow = sourceRow;
            IsVoided = isVoided; RelatedNativeSaleId = relatedNativeSaleId; RawTimestamp = rawTimestamp ?? string.Empty;
            OffsetMinutes = offsetMinutes; CardSubtype = cardSubtype ?? string.Empty;
            if (key.Origin == SalesReportOrigin.Historical && (cashTenderedClp != 0 || changeClp != 0 || cardClp != signedAmountClp || relatedNativeSaleId.HasValue))
                throw new ArgumentException("Historical movements must be card-only and cannot reference a native sale.");
        }
        public SalesReportEntryKey Key { get; }
        public DateTime BusinessDateTime { get; }
        public long BusinessTicks { get { return BusinessDateTime.Ticks; } }
        public SalesReportMovementKind MovementKind { get; }
        public long SignedAmountClp { get; }
        public long CashTenderedClp { get; }
        public long ChangeClp { get; }
        public long CashClp { get; }
        public long CardClp { get; }
        public int? OriginalCashierId { get; }
        public string Reference { get; }
        public string Provider { get; }
        public string ExternalNamespace { get; }
        public string ExternalId { get; }
        public string BatchId { get; }
        public string SourceSheet { get; }
        public int? SourceRow { get; }
        public bool IsVoided { get; }
        public long? RelatedNativeSaleId { get; }
        public string RawTimestamp { get; }
        public int? OffsetMinutes { get; }
        public string CardSubtype { get; }
        public bool IsReconciled { get { return checked(CashClp + CardClp) == SignedAmountClp; } }
        public bool HasItemLines { get { return Key.Origin == SalesReportOrigin.Native; } }
        public bool CanRefund { get { return Key.Origin == SalesReportOrigin.Native && MovementKind == SalesReportMovementKind.Sale && !IsVoided; } }
        public bool CanReprintNativeReceipt { get { return Key.Origin == SalesReportOrigin.Native; } }
    }

    public sealed class SalesReportSummary
    {
        public SalesReportSummary(long grossPositiveClp, long reversalsClp, long balanceClp, long cashTenderedClp,
            long changeClp, long cashClp, long cardClp, long nativePositiveSalesClp, long nativePositiveReceiptCount,
            long nativeNegativeMovementCount, long historicalMovementCount, long historicalCancellationCount, long reconciliationAnomalyCount)
        {
            GrossPositiveClp = grossPositiveClp; ReversalsClp = reversalsClp; BalanceClp = balanceClp;
            CashTenderedClp = cashTenderedClp; ChangeClp = changeClp; CashClp = cashClp; CardClp = cardClp;
            NativePositiveSalesClp = nativePositiveSalesClp; NativePositiveReceiptCount = nativePositiveReceiptCount;
            NativeNegativeMovementCount = nativeNegativeMovementCount; HistoricalMovementCount = historicalMovementCount;
            HistoricalCancellationCount = historicalCancellationCount; ReconciliationAnomalyCount = reconciliationAnomalyCount;
        }
        public long GrossPositiveClp { get; }
        public long ReversalsClp { get; }
        public long BalanceClp { get; }
        public long CashTenderedClp { get; }
        public long ChangeClp { get; }
        public long CashClp { get; }
        public long CardClp { get; }
        public long NativePositiveSalesClp { get; }
        public long NativePositiveReceiptCount { get; }
        public long NativeNegativeMovementCount { get; }
        public long HistoricalMovementCount { get; }
        public long HistoricalCancellationCount { get; }
        public long ReconciliationAnomalyCount { get; }
        public long? NativeAverageReceiptClp { get { return NativePositiveReceiptCount == 0 ? (long?)null : checked((long)decimal.Round((decimal)NativePositiveSalesClp / NativePositiveReceiptCount, 0, MidpointRounding.AwayFromZero)); } }
        public static SalesReportSummary Empty { get { return new SalesReportSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0); } }
        public SalesReportSummary Add(SalesReportSummary other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            checked { return new SalesReportSummary(GrossPositiveClp + other.GrossPositiveClp, ReversalsClp + other.ReversalsClp,
                BalanceClp + other.BalanceClp, CashTenderedClp + other.CashTenderedClp, ChangeClp + other.ChangeClp,
                CashClp + other.CashClp, CardClp + other.CardClp, NativePositiveSalesClp + other.NativePositiveSalesClp,
                NativePositiveReceiptCount + other.NativePositiveReceiptCount, NativeNegativeMovementCount + other.NativeNegativeMovementCount,
                HistoricalMovementCount + other.HistoricalMovementCount, HistoricalCancellationCount + other.HistoricalCancellationCount,
                ReconciliationAnomalyCount + other.ReconciliationAnomalyCount); }
        }
        public static SalesReportSummary FromEntry(SalesReportEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            var positiveSale = entry.MovementKind == SalesReportMovementKind.Sale && entry.SignedAmountClp > 0;
            var native = entry.Key.Origin == SalesReportOrigin.Native;
            return new SalesReportSummary(positiveSale ? entry.SignedAmountClp : 0,
                entry.SignedAmountClp < 0 ? checked(-entry.SignedAmountClp) : 0, entry.SignedAmountClp,
                entry.CashTenderedClp, entry.ChangeClp, entry.CashClp, entry.CardClp,
                native && positiveSale ? entry.SignedAmountClp : 0, native && positiveSale ? 1 : 0,
                native && entry.SignedAmountClp < 0 ? 1 : 0, native ? 0 : 1,
                !native && entry.SignedAmountClp < 0 ? 1 : 0, entry.IsReconciled ? 0 : 1);
        }
    }

    public sealed class SalesReportDaySummary
    {
        public SalesReportDaySummary(DateTime date, SalesReportSummary summary) { Date = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified); Summary = summary ?? throw new ArgumentNullException(nameof(summary)); }
        public DateTime Date { get; }
        public SalesReportSummary Summary { get; }
    }

    public sealed class SalesReportCursor
    {
        public SalesReportCursor(long businessTicks, SalesReportEntryKey key) { new DateTime(businessTicks); BusinessTicks = businessTicks; Key = key ?? throw new ArgumentNullException(nameof(key)); }
        public long BusinessTicks { get; }
        public SalesReportEntryKey Key { get; }
        public static bool ComesAfter(SalesReportEntry entry, SalesReportCursor cursor)
        {
            if (cursor == null) return true;
            return entry.BusinessTicks < cursor.BusinessTicks || (entry.BusinessTicks == cursor.BusinessTicks &&
                ((int)entry.Key.Origin > (int)cursor.Key.Origin || (entry.Key.Origin == cursor.Key.Origin && entry.Key.InternalId < cursor.Key.InternalId)));
        }
    }

    public sealed class SalesReportPage
    {
        public SalesReportPage(SalesReportReadToken token, IEnumerable<SalesReportEntry> entries, bool hasMore)
        {
            Token = token ?? throw new ArgumentNullException(nameof(token));
            Entries = new ReadOnlyCollection<SalesReportEntry>((entries ?? throw new ArgumentNullException(nameof(entries))).ToArray());
            HasMore = hasMore;
            if (Entries.Count > 0) { var last = Entries[Entries.Count - 1]; NextCursor = new SalesReportCursor(last.BusinessTicks, last.Key); }
        }
        public SalesReportReadToken Token { get; }
        public IReadOnlyList<SalesReportEntry> Entries { get; }
        public bool HasMore { get; }
        public SalesReportCursor NextCursor { get; }
    }

    public sealed class SalesReportSnapshot
    {
        public SalesReportSnapshot(SalesReportReadToken token, SalesReportCoverage coverage, SalesReportSummary summary,
            IEnumerable<SalesReportDaySummary> days, IEnumerable<long> hourlyAmounts, SalesReportPage firstPage)
        {
            Token = token ?? throw new ArgumentNullException(nameof(token)); Coverage = coverage ?? throw new ArgumentNullException(nameof(coverage));
            Summary = summary ?? throw new ArgumentNullException(nameof(summary));
            Days = new ReadOnlyCollection<SalesReportDaySummary>((days ?? throw new ArgumentNullException(nameof(days))).ToArray());
            var hourly = (hourlyAmounts ?? throw new ArgumentNullException(nameof(hourlyAmounts))).ToArray();
            if (hourly.Length != 24) throw new ArgumentException("Exactly 24 commercial hours are required.");
            HourlyAmounts = new ReadOnlyCollection<long>(hourly); FirstPage = firstPage ?? throw new ArgumentNullException(nameof(firstPage));
            if (!token.Equals(firstPage.Token)) throw new ArgumentException("A snapshot cannot combine different report revisions.");
        }
        public SalesReportReadToken Token { get; }
        public SalesReportCoverage Coverage { get; }
        public SalesReportSummary Summary { get; }
        public IReadOnlyList<SalesReportDaySummary> Days { get; }
        public IReadOnlyList<long> HourlyAmounts { get; }
        public SalesReportPage FirstPage { get; }
    }

    public static class SalesReportHourlyGeometry
    {
        public static decimal MaximumMagnitude(IEnumerable<long> amounts)
        {
            if (amounts == null) throw new ArgumentNullException(nameof(amounts));
            decimal maximum = 0;
            foreach (var amount in amounts) maximum = Math.Max(maximum, Math.Abs((decimal)amount));
            return maximum;
        }
        public static double PositiveFraction(long amount, decimal maximumMagnitude) { return amount > 0 && maximumMagnitude > 0 ? (double)((decimal)amount / maximumMagnitude) : 0; }
        public static double NegativeFraction(long amount, decimal maximumMagnitude) { return amount < 0 && maximumMagnitude > 0 ? (double)(-(decimal)amount / maximumMagnitude) : 0; }
    }
}
