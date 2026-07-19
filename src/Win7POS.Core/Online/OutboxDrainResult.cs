using System;

namespace Win7POS.Core.Online
{
    public enum SyncFailureKind
    {
        None = 0,
        ConcurrentDrain = 1,
        Configuration = 2,
        AuthenticationDenied = 3,
        Network = 4,
        Timeout = 5,
        RetryableRemote = 6,
        PermanentRemote = 7,
        LocalValidation = 8,
        LocalPersistence = 9,
        Unexpected = 10
    }

    /// <summary>
    /// Immutable, post-run view of a bounded outbox drain. Transition counters only
    /// include compare-and-swap updates that this run actually committed.
    /// </summary>
    public sealed class OutboxDrainResult
    {
        public OutboxDrainResult(
            int attempted,
            int acked,
            int retried,
            int blocked,
            long remainingDue,
            long? nextRetryAt = null,
            SyncFailureKind failureKind = SyncFailureKind.None,
            string diagnosticCode = null)
        {
            if (attempted < 0) throw new ArgumentOutOfRangeException(nameof(attempted));
            if (acked < 0) throw new ArgumentOutOfRangeException(nameof(acked));
            if (retried < 0) throw new ArgumentOutOfRangeException(nameof(retried));
            if (blocked < 0) throw new ArgumentOutOfRangeException(nameof(blocked));
            if (remainingDue < 0) throw new ArgumentOutOfRangeException(nameof(remainingDue));
            if (nextRetryAt.HasValue && nextRetryAt.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(nextRetryAt));
            }

            var transitioned = (long)acked + retried + blocked;
            if (transitioned > attempted)
            {
                throw new ArgumentException(
                    "Acknowledged, retried, and blocked transitions cannot exceed attempted claims.");
            }

            Attempted = attempted;
            Acked = acked;
            Retried = retried;
            Blocked = blocked;
            RemainingDue = remainingDue;
            NextRetryAt = nextRetryAt;
            FailureKind = failureKind;
            DiagnosticCode = NormalizeDiagnosticCode(diagnosticCode, failureKind);
        }

        public int Acked { get; }
        public int Attempted { get; }
        public bool AuthenticationDenied => FailureKind == SyncFailureKind.AuthenticationDenied;
        public int Blocked { get; }
        public string DiagnosticCode { get; }
        public SyncFailureKind FailureKind { get; }
        public bool HasImmediateMore => RemainingDue > 0 && !AuthenticationDenied;
        public long? NextRetryAt { get; }
        public long RemainingDue { get; }
        public int Retried { get; }

        public static OutboxDrainResult Empty(
            long remainingDue = 0,
            long? nextRetryAt = null,
            SyncFailureKind failureKind = SyncFailureKind.None,
            string diagnosticCode = null)
        {
            return new OutboxDrainResult(
                0,
                0,
                0,
                0,
                remainingDue,
                nextRetryAt,
                failureKind,
                diagnosticCode);
        }

        private static string NormalizeDiagnosticCode(
            string value,
            SyncFailureKind failureKind)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Length == 0)
            {
                return failureKind == SyncFailureKind.None
                    ? string.Empty
                    : "outbox_failure";
            }

            if (normalized.Length > 96)
            {
                return "outbox_failure";
            }

            for (var index = 0; index < normalized.Length; index++)
            {
                var character = normalized[index];
                var allowed = (character >= 'a' && character <= 'z') ||
                    (character >= '0' && character <= '9') ||
                    character == '_' ||
                    character == '-' ||
                    character == '.';
                if (!allowed)
                {
                    return "outbox_failure";
                }
            }

            return normalized;
        }
    }

    public sealed class OutboxDrainState
    {
        public OutboxDrainState(long remainingDue, long? nextRetryAt)
        {
            if (remainingDue < 0) throw new ArgumentOutOfRangeException(nameof(remainingDue));
            if (nextRetryAt.HasValue && nextRetryAt.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(nextRetryAt));
            }

            RemainingDue = remainingDue;
            NextRetryAt = nextRetryAt;
        }

        public long? NextRetryAt { get; }
        public long RemainingDue { get; }
    }
}
