using System;

namespace Win7POS.Core.Online
{
    public sealed class CatalogRevisionState
    {
        public CatalogRevisionState(
            string observedRevision,
            string committedRevision,
            string observedAt,
            string committedAt,
            long importAckGeneration = 0,
            long reconciledImportAckGeneration = 0,
            bool importAckStateValid = true)
        {
            ObservedRevision = observedRevision ?? string.Empty;
            CommittedRevision = committedRevision ?? string.Empty;
            ObservedAt = observedAt ?? string.Empty;
            CommittedAt = committedAt ?? string.Empty;
            ImportAckGeneration = Math.Max(0, importAckGeneration);
            ReconciledImportAckGeneration = Math.Max(0, reconciledImportAckGeneration);
            ImportAckStateValid = importAckStateValid &&
                importAckGeneration >= 0 &&
                reconciledImportAckGeneration >= 0 &&
                reconciledImportAckGeneration <= importAckGeneration;
        }

        public string CommittedAt { get; }
        public string CommittedRevision { get; }
        public long ImportAckGeneration { get; }
        public bool ImportAckReconciliationPending => !ImportAckStateValid ||
            ImportAckGeneration > ReconciledImportAckGeneration;
        public bool ImportAckStateValid { get; }
        public bool IsMatch => ObservedRevision.Length > 0 &&
            CommittedRevision.Length > 0 &&
            string.Equals(ObservedRevision, CommittedRevision, StringComparison.Ordinal);
        public string ObservedAt { get; }
        public string ObservedRevision { get; }
        public long ReconciledImportAckGeneration { get; }
    }

    public sealed class CatalogHeartbeatDecision
    {
        internal CatalogHeartbeatDecision(
            string observedRevision,
            int? nextPollAfterSeconds,
            bool shouldSkipCatalogPull,
            string code)
        {
            ObservedRevision = observedRevision ?? string.Empty;
            NextPollAfterSeconds = nextPollAfterSeconds;
            ShouldSkipCatalogPull = shouldSkipCatalogPull;
            Code = code ?? string.Empty;
        }

        public string Code { get; }
        public int? NextPollAfterSeconds { get; }
        public string ObservedRevision { get; }
        public bool ShouldSkipCatalogPull { get; }
    }

    public static class CatalogHeartbeatPolicy
    {
        public const int MaximumPollSeconds = 300;
        public const int MaximumRevisionLength = 128;
        public const int MinimumPollSeconds = 5;

        public static CatalogHeartbeatDecision Evaluate(
            string observedRevision,
            bool? catalogChangesAvailable,
            int? nextPollAfterSeconds,
            string committedRevision,
            bool fullOrRepairRequired,
            bool partialCursorPending,
            bool manualTrigger,
            bool catalogImportAckPending)
        {
            var observed = NormalizeRevision(observedRevision);
            var committed = NormalizeRevision(committedRevision);
            var normalizedPoll = NormalizePollSeconds(nextPollAfterSeconds);

            var revisionsMatch = observed.Length > 0 &&
                committed.Length > 0 &&
                string.Equals(observed, committed, StringComparison.Ordinal);
            var overrideRequired = fullOrRepairRequired ||
                partialCursorPending ||
                manualTrigger ||
                catalogImportAckPending;
            var skip = !overrideRequired &&
                catalogChangesAvailable == false &&
                revisionsMatch;

            string code;
            if (skip)
            {
                code = "catalog_unchanged_at_committed_revision";
            }
            else if (overrideRequired)
            {
                code = "catalog_pull_override_required";
            }
            else if (catalogChangesAvailable == true)
            {
                code = "catalog_changes_available";
            }
            else if (catalogChangesAvailable == false && !revisionsMatch)
            {
                code = "catalog_revision_mismatch";
            }
            else
            {
                code = "catalog_hint_unavailable";
            }

            return new CatalogHeartbeatDecision(observed, normalizedPoll, skip, code);
        }

        public static int? NormalizePollSeconds(int? value)
        {
            if (!value.HasValue || value.Value <= 0)
            {
                return null;
            }

            return Math.Max(
                MinimumPollSeconds,
                Math.Min(MaximumPollSeconds, value.Value));
        }

        public static string NormalizeRevision(string value)
        {
            var raw = value ?? string.Empty;
            if (raw.Length == 0 || raw.Length > MaximumRevisionLength)
            {
                return string.Empty;
            }

            for (var index = 0; index < raw.Length; index++)
            {
                var character = raw[index];
                if (char.IsControl(character))
                {
                    return string.Empty;
                }
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= raw.Length || !char.IsLowSurrogate(raw[index + 1]))
                    {
                        return string.Empty;
                    }
                    index++;
                }
                else if (char.IsLowSurrogate(character))
                {
                    return string.Empty;
                }
            }

            var normalized = raw.Trim();
            if (normalized.Length == 0)
            {
                return string.Empty;
            }
            return normalized;
        }
    }
}
