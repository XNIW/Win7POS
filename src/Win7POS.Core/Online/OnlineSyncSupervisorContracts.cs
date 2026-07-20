using System;
using System.Security.Cryptography;
using System.Text;

namespace Win7POS.Core.Online
{
    public enum OnlineSyncLane
    {
        Heartbeat = 0,
        SalesOutbox = 1,
        CatalogImportOutbox = 2,
        CatalogDelta = 3
    }

    public enum OnlineSyncLaneTrigger
    {
        Periodic = 0,
        StartOfDay = 1,
        LocalCommit = 2,
        ImportAcknowledged = 3,
        NetworkRecovered = 4,
        RevisionChanged = 5,
        PartialResume = 6,
        Foreground = 7,
        Manual = 8,
        AdministratorRepair = 9,
        FirstBootstrap = 10
    }

    /// <summary>
    /// Immutable identity of one trusted-session lifetime. Tokens are deliberately
    /// excluded: a heartbeat may rotate a token without creating a new generation.
    /// </summary>
    public sealed class OnlineSyncGeneration
    {
        public OnlineSyncGeneration(
            string generationId,
            string posSessionId,
            string shopDeviceId,
            string shopId,
            string shopCode,
            string staffId = null,
            int staffCredentialVersion = 0)
        {
            GenerationId = NormalizeRequired(generationId, nameof(generationId), 64);
            PosSessionId = NormalizeRequired(posSessionId, nameof(posSessionId), 160);
            ShopDeviceId = NormalizeRequired(shopDeviceId, nameof(shopDeviceId), 160);
            ShopId = Normalize(shopId, 160);
            ShopCode = Normalize(shopCode, 80).ToUpperInvariant();
            StaffId = Normalize(staffId, 160);
            StaffCredentialVersion = Math.Max(0, staffCredentialVersion);
            if (ShopId.Length == 0 && ShopCode.Length == 0)
            {
                throw new ArgumentException("A shop identity is required.");
            }

            Fingerprint = ComputeFingerprint(
                GenerationId,
                PosSessionId,
                ShopDeviceId,
                ShopId,
                ShopCode,
                StaffId,
                StaffCredentialVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public string Fingerprint { get; }
        public string GenerationId { get; }
        public string PosSessionId { get; }
        public string ShopCode { get; }
        public string ShopDeviceId { get; }
        public string ShopId { get; }
        public int StaffCredentialVersion { get; }
        public string StaffId { get; }

        public static string CreateGenerationId()
        {
            return Guid.NewGuid().ToString("N");
        }

        private static string ComputeFingerprint(params string[] values)
        {
            var material = new StringBuilder("v1|");
            foreach (var value in values ?? Array.Empty<string>())
            {
                var normalized = value ?? string.Empty;
                material.Append(normalized.Length);
                material.Append(':');
                material.Append(normalized);
                material.Append('|');
            }
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(material.ToString()));
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var value in hash)
                {
                    builder.Append(value.ToString("x2"));
                }
                return builder.ToString();
            }
        }

        private static string NormalizeRequired(string value, string name, int maximumLength)
        {
            var normalized = Normalize(value, maximumLength);
            if (normalized.Length == 0)
            {
                throw new ArgumentException("A value is required.", name);
            }
            return normalized;
        }

        private static string Normalize(string value, int maximumLength)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length > maximumLength)
            {
                throw new ArgumentException("The value exceeds the supported length.");
            }
            for (var index = 0; index < normalized.Length; index++)
            {
                if (char.IsControl(normalized[index]))
                {
                    throw new ArgumentException("Control characters are not supported.");
                }
            }
            return normalized;
        }
    }

    public sealed class OnlineSyncLaneOutcome
    {
        public OnlineSyncLaneOutcome(
            bool success,
            string code = null,
            bool offline = false,
            bool authenticationDenied = false,
            bool hasImmediateMore = false,
            long? nextRetryAt = null,
            bool catalogHasMore = false,
            bool requestCatalogNow = false,
            int? nextPollAfterSeconds = null,
            bool terminal = false,
            int catalogPagesProcessed = 0,
            int catalogRowsApplied = 0,
            bool catalogSaleSafe = false)
        {
            if (nextRetryAt.HasValue && nextRetryAt.Value < 0)
                throw new ArgumentOutOfRangeException(nameof(nextRetryAt));
            if (catalogPagesProcessed < 0)
                throw new ArgumentOutOfRangeException(nameof(catalogPagesProcessed));
            if (catalogRowsApplied < 0)
                throw new ArgumentOutOfRangeException(nameof(catalogRowsApplied));

            Success = success;
            Code = NormalizeCode(code);
            Offline = offline;
            AuthenticationDenied = authenticationDenied;
            HasImmediateMore = hasImmediateMore;
            NextRetryAt = nextRetryAt;
            CatalogHasMore = catalogHasMore;
            RequestCatalogNow = requestCatalogNow;
            NextPollAfterSeconds = CatalogHeartbeatPolicy.NormalizePollSeconds(
                nextPollAfterSeconds);
            Terminal = terminal;
            CatalogPagesProcessed = catalogPagesProcessed;
            CatalogRowsApplied = catalogRowsApplied;
            CatalogSaleSafe = catalogSaleSafe;
        }

        public bool AuthenticationDenied { get; }
        public bool CatalogHasMore { get; }
        public int CatalogPagesProcessed { get; }
        public int CatalogRowsApplied { get; }
        public bool CatalogSaleSafe { get; }
        public string Code { get; }
        public bool HasImmediateMore { get; }
        public long? NextRetryAt { get; }
        public int? NextPollAfterSeconds { get; }
        public bool Offline { get; }
        public bool RequestCatalogNow { get; }
        public bool Success { get; }
        public bool Terminal { get; }

        public static OnlineSyncLaneOutcome AuthDenied(string code = "auth_denied")
        {
            return new OnlineSyncLaneOutcome(
                success: false,
                code: code,
                authenticationDenied: true);
        }

        private static string NormalizeCode(string value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Length == 0)
                return string.Empty;
            if (normalized.Length > 96)
                return "sync_failure";
            for (var index = 0; index < normalized.Length; index++)
            {
                var character = normalized[index];
                var allowed = (character >= 'a' && character <= 'z') ||
                    (character >= '0' && character <= '9') ||
                    character == '_' || character == '-' || character == '.';
                if (!allowed)
                    return "sync_failure";
            }
            return normalized;
        }
    }

    /// <summary>
    /// Short-lived credentials loaded immediately before one HTTP request. Callers
    /// must never persist or log this object.
    /// </summary>
    public sealed class OnlineSyncRequestCredentials
    {
        public OnlineSyncRequestCredentials(
            OnlineSyncGeneration generation,
            string deviceToken,
            string sessionToken,
            string credentialStamp)
        {
            if (generation == null) throw new ArgumentNullException(nameof(generation));
            GenerationId = generation.GenerationId;
            GenerationFingerprint = generation.Fingerprint;
            PosSessionId = generation.PosSessionId;
            ShopDeviceId = generation.ShopDeviceId;
            DeviceToken = NormalizeRequired(deviceToken, nameof(deviceToken));
            SessionToken = NormalizeRequired(sessionToken, nameof(sessionToken));
            CredentialStamp = NormalizeRequired(
                credentialStamp,
                nameof(credentialStamp));
        }

        public string CredentialStamp { get; }
        public string DeviceToken { get; }
        public string GenerationFingerprint { get; }
        public string GenerationId { get; }
        public string PosSessionId { get; }
        public string SessionToken { get; }
        public string ShopDeviceId { get; }

        public bool Matches(OnlineSyncGeneration generation)
        {
            return generation != null &&
                string.Equals(GenerationId, generation.GenerationId, StringComparison.Ordinal) &&
                string.Equals(
                    GenerationFingerprint,
                    generation.Fingerprint,
                    StringComparison.Ordinal);
        }

        private static string NormalizeRequired(string value, string name)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0)
                throw new ArgumentException("A value is required.", name);
            return normalized;
        }
    }

    public sealed class OnlineSyncLaneScheduleDecision
    {
        internal OnlineSyncLaneScheduleDecision(
            bool shouldSchedule,
            TimeSpan delay,
            int failureCount)
        {
            ShouldSchedule = shouldSchedule;
            Delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
            FailureCount = Math.Max(0, failureCount);
        }

        public TimeSpan Delay { get; }
        public int FailureCount { get; }
        public bool ShouldSchedule { get; }
    }

    public static class OnlineSyncLaneSchedulePolicy
    {
        private static readonly int[] RetrySeconds = { 5, 15, 30, 60, 120, 300 };

        public static OnlineSyncLaneScheduleDecision Evaluate(
            OnlineSyncLane lane,
            OnlineSyncLaneOutcome outcome,
            int currentFailureCount,
            DateTimeOffset now,
            double jitterSample)
        {
            if (outcome == null) throw new ArgumentNullException(nameof(outcome));
            if (double.IsNaN(jitterSample) || double.IsInfinity(jitterSample))
                throw new ArgumentOutOfRangeException(nameof(jitterSample));

            if (outcome.AuthenticationDenied || outcome.Terminal)
                return new OnlineSyncLaneScheduleDecision(false, TimeSpan.Zero, 0);

            var jitter = Math.Max(0d, Math.Min(1d, jitterSample));
            if ((lane == OnlineSyncLane.SalesOutbox ||
                 lane == OnlineSyncLane.CatalogImportOutbox) &&
                outcome.HasImmediateMore)
            {
                return new OnlineSyncLaneScheduleDecision(
                    true,
                    TimeSpan.FromSeconds(1d + (4d * jitter)),
                    outcome.Success
                        ? 0
                        : Math.Max(0, currentFailureCount) + 1);
            }

            if (outcome.Success)
            {
                if (outcome.NextRetryAt.HasValue)
                {
                    var milliseconds = Math.Max(
                        1000d,
                        outcome.NextRetryAt.Value - now.ToUnixTimeMilliseconds());
                    return new OnlineSyncLaneScheduleDecision(
                        true,
                        TimeSpan.FromMilliseconds(milliseconds),
                        0);
                }

                if (lane == OnlineSyncLane.CatalogDelta && outcome.CatalogHasMore)
                {
                    return new OnlineSyncLaneScheduleDecision(
                        true,
                        TimeSpan.FromSeconds(5),
                        0);
                }

                if (lane == OnlineSyncLane.Heartbeat)
                {
                    var seconds = outcome.NextPollAfterSeconds.HasValue
                        ? outcome.NextPollAfterSeconds.Value
                        : 24d + (12d * jitter);
                    return new OnlineSyncLaneScheduleDecision(
                        true,
                        TimeSpan.FromSeconds(seconds),
                        0);
                }

                return new OnlineSyncLaneScheduleDecision(false, TimeSpan.Zero, 0);
            }

            var nextFailureCount = Math.Max(0, currentFailureCount) + 1;
            var index = Math.Min(nextFailureCount, RetrySeconds.Length) - 1;
            var retrySeconds = Math.Min(
                300d,
                RetrySeconds[index] * (0.8d + (0.4d * jitter)));
            if (outcome.NextRetryAt.HasValue)
            {
                retrySeconds = Math.Max(
                    retrySeconds,
                    Math.Max(
                        1d,
                        (outcome.NextRetryAt.Value - now.ToUnixTimeMilliseconds()) / 1000d));
            }
            return new OnlineSyncLaneScheduleDecision(
                true,
                TimeSpan.FromSeconds(retrySeconds),
                nextFailureCount);
        }
    }

    public sealed class OnlineSyncLaneSnapshot
    {
        public OnlineSyncLaneSnapshot(
            OnlineSyncLane lane,
            bool inFlight,
            bool pending,
            DateTimeOffset? nextDueAt,
            int failureCount,
            OnlineSyncLaneOutcome lastOutcome)
        {
            Lane = lane;
            InFlight = inFlight;
            Pending = pending;
            NextDueAt = nextDueAt;
            FailureCount = Math.Max(0, failureCount);
            LastOutcome = lastOutcome;
        }

        public int FailureCount { get; }
        public bool InFlight { get; }
        public OnlineSyncLane Lane { get; }
        public OnlineSyncLaneOutcome LastOutcome { get; }
        public DateTimeOffset? NextDueAt { get; }
        public bool Pending { get; }
    }

    public sealed class OnlineSyncSupervisorSnapshot
    {
        public OnlineSyncSupervisorSnapshot(
            OnlineSyncGeneration generation,
            bool authenticationStopped,
            OnlineSyncLaneSnapshot[] lanes)
        {
            Generation = generation ?? throw new ArgumentNullException(nameof(generation));
            AuthenticationStopped = authenticationStopped;
            Lanes = lanes ?? Array.Empty<OnlineSyncLaneSnapshot>();
        }

        public bool AuthenticationStopped { get; }
        public OnlineSyncGeneration Generation { get; }
        public OnlineSyncLaneSnapshot[] Lanes { get; }
    }
}
