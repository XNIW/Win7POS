using System;
using Win7POS.Core.Online;

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
            return Evaluate(
                result,
                currentFailureCount,
                jitterSample,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public static CatalogSyncScheduleDecision Evaluate(
            CatalogSyncRunResult result,
            int currentFailureCount,
            double jitterSample,
            long nowMilliseconds)
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

            if (result.Success &&
                (result.HasMore || result.ReceivedChanges || result.OutboxWorkRemaining))
            {
                var fastDelayMilliseconds = 5000d;
                if (result.NextOutboxRetryAt.HasValue)
                {
                    fastDelayMilliseconds = Math.Min(
                        fastDelayMilliseconds,
                        Math.Max(1000d, result.NextOutboxRetryAt.Value - nowMilliseconds));
                }
                return new CatalogSyncScheduleDecision(
                    CatalogSyncScheduleKind.FastCatchUp,
                    TimeSpan.FromMilliseconds(fastDelayMilliseconds),
                    0,
                    true);
            }

            if (result.Success)
            {
                double idleSeconds;
                if (result.NextPollAfterSeconds.HasValue)
                {
                    var serverSeconds = Math.Max(
                        CatalogHeartbeatPolicy.MinimumPollSeconds,
                        Math.Min(
                            CatalogHeartbeatPolicy.MaximumPollSeconds,
                            result.NextPollAfterSeconds.Value));
                    idleSeconds = Math.Max(
                        CatalogHeartbeatPolicy.MinimumPollSeconds,
                        Math.Min(
                            CatalogHeartbeatPolicy.MaximumPollSeconds,
                            serverSeconds * (0.8d + (0.4d * boundedJitter))));
                }
                else
                {
                    // Preserve the legacy bounded idle interval when the optional hint is absent.
                    idleSeconds = 24d + (12d * boundedJitter);
                }

                if (result.HeartbeatAfterSeconds.HasValue)
                {
                    idleSeconds = Math.Min(idleSeconds, result.HeartbeatAfterSeconds.Value);
                }
                var idleMilliseconds = idleSeconds * 1000d;
                if (result.NextOutboxRetryAt.HasValue)
                {
                    var retryDelayMilliseconds = result.NextOutboxRetryAt.Value - nowMilliseconds;
                    // A retry that became due between aggregation and scheduling should
                    // resume promptly without creating a zero-delay spin.
                    idleMilliseconds = Math.Min(
                        idleMilliseconds,
                        Math.Max(1000d, retryDelayMilliseconds));
                }
                return new CatalogSyncScheduleDecision(
                    CatalogSyncScheduleKind.IdleOnline,
                    TimeSpan.FromMilliseconds(idleMilliseconds),
                    0,
                    true);
            }

            var nextFailureCount = Math.Max(0, currentFailureCount) + 1;
            var index = Math.Min(nextFailureCount, RetrySeconds.Length) - 1;
            var retrySeconds = Math.Min(
                300d,
                RetrySeconds[index] * (0.8d + (0.4d * boundedJitter)));
            var retryMilliseconds = retrySeconds * 1000d;
            if (result.NextOutboxRetryAt.HasValue)
            {
                retryMilliseconds = Math.Min(
                    retryMilliseconds,
                    Math.Max(1000d, result.NextOutboxRetryAt.Value - nowMilliseconds));
            }
            var retryKind = result.Offline
                ? CatalogSyncScheduleKind.OfflineQuiet
                : CatalogSyncScheduleKind.Retry;
            return new CatalogSyncScheduleDecision(
                retryKind,
                TimeSpan.FromMilliseconds(retryMilliseconds),
                nextFailureCount,
                true);
        }
    }
}
