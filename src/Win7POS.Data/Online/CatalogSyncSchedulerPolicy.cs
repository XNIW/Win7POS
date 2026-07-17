using System;

namespace Win7POS.Data.Online
{
    public enum CatalogSyncScheduleKind
    {
        FastCatchUp = 0,
        IdleOnline = 1,
        Retry = 2,
        OfflineQuiet = 3,
        AuthenticationStopped = 4
    }

    public sealed class CatalogSyncScheduleDecision
    {
        internal CatalogSyncScheduleDecision(
            CatalogSyncScheduleKind kind,
            TimeSpan delay,
            int failureCount,
            bool shouldPoll)
        {
            Kind = kind;
            Delay = delay;
            FailureCount = failureCount;
            ShouldPoll = shouldPoll;
        }

        public CatalogSyncScheduleKind Kind { get; }
        public TimeSpan Delay { get; }
        public int FailureCount { get; }
        public bool ShouldPoll { get; }
    }

    public static class CatalogSyncSchedulerPolicy
    {
        private static readonly int[] RetrySeconds = { 5, 15, 30, 60, 120, 300 };

        public static CatalogSyncScheduleDecision Evaluate(
            CatalogSyncRunResult result,
            int currentFailureCount,
            double jitterSample)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (double.IsNaN(jitterSample) || double.IsInfinity(jitterSample))
            {
                throw new ArgumentOutOfRangeException(nameof(jitterSample));
            }

            var boundedJitter = Math.Max(0d, Math.Min(1d, jitterSample));
            if (result.AuthenticationDenied)
            {
                return new CatalogSyncScheduleDecision(
                    CatalogSyncScheduleKind.AuthenticationStopped,
                    TimeSpan.Zero,
                    0,
                    false);
            }

            if (result.Offline)
            {
                return new CatalogSyncScheduleDecision(
                    CatalogSyncScheduleKind.OfflineQuiet,
                    TimeSpan.Zero,
                    0,
                    false);
            }

            if (result.Success && (result.HasMore || result.ReceivedChanges))
            {
                return new CatalogSyncScheduleDecision(
                    CatalogSyncScheduleKind.FastCatchUp,
                    TimeSpan.FromSeconds(5),
                    0,
                    true);
            }

            if (result.Success)
            {
                // Map [0,1] to the bounded idle interval [24,36] seconds.
                var idleSeconds = 24d + (12d * boundedJitter);
                return new CatalogSyncScheduleDecision(
                    CatalogSyncScheduleKind.IdleOnline,
                    TimeSpan.FromSeconds(idleSeconds),
                    0,
                    true);
            }

            var nextFailureCount = Math.Max(0, currentFailureCount) + 1;
            var index = Math.Min(nextFailureCount, RetrySeconds.Length) - 1;
            var retrySeconds = Math.Min(
                300d,
                RetrySeconds[index] * (0.8d + (0.4d * boundedJitter)));
            return new CatalogSyncScheduleDecision(
                CatalogSyncScheduleKind.Retry,
                TimeSpan.FromSeconds(retrySeconds),
                nextFailureCount,
                true);
        }
    }
}
