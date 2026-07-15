using System;
using System.Globalization;

namespace Win7POS.Core.Online
{
    public static class PosOfflineAuthorizationLeasePolicy
    {
        private static readonly TimeSpan MaximumOfflineAge =
            TimeSpan.FromSeconds(PosOnlineContract.OfflineAuthorizationMaxAgeSeconds);

        public static PosOfflineAuthorizationLeaseDecision Evaluate(
            PosTrustedDeviceSession session,
            DateTimeOffset localNow,
            DateTimeOffset? minimumEstimatedServerNow = null)
        {
            if (session == null)
            {
                return PosOfflineAuthorizationLeaseDecision.Deny("trusted_session_missing");
            }

            if (!TryParseUtc(session.LastOkServerAt, out var lastServerAt))
            {
                return PosOfflineAuthorizationLeaseDecision.Deny("last_server_time_invalid");
            }

            if (!TryParseUtc(session.LastOkLocalAt, out var lastLocalAt))
            {
                return PosOfflineAuthorizationLeaseDecision.Deny("local_receipt_time_invalid");
            }

            if (!TryParseUtc(session.SessionExpiresAt, out var sessionExpiresAt))
            {
                return PosOfflineAuthorizationLeaseDecision.Deny("session_expiry_invalid");
            }

            if (sessionExpiresAt <= lastServerAt)
            {
                return PosOfflineAuthorizationLeaseDecision.Deny("session_window_invalid");
            }

            var normalizedLocalNow = localNow.ToUniversalTime();
            if (normalizedLocalNow < lastLocalAt)
            {
                return PosOfflineAuthorizationLeaseDecision.Deny("clock_rollback");
            }

            DateTimeOffset estimatedServerNow;
            DateTimeOffset maximumOfflineExpiry;
            try
            {
                estimatedServerNow = lastServerAt + (normalizedLocalNow - lastLocalAt);
                maximumOfflineExpiry = lastServerAt + MaximumOfflineAge;
            }
            catch (ArgumentOutOfRangeException)
            {
                return PosOfflineAuthorizationLeaseDecision.Deny("session_window_invalid");
            }

            if (minimumEstimatedServerNow.HasValue &&
                estimatedServerNow < minimumEstimatedServerNow.Value)
            {
                return PosOfflineAuthorizationLeaseDecision.Deny(
                    "clock_rollback",
                    null,
                    estimatedServerNow);
            }

            var effectiveExpiry = sessionExpiresAt <= maximumOfflineExpiry
                ? sessionExpiresAt
                : maximumOfflineExpiry;
            if (estimatedServerNow >= effectiveExpiry)
            {
                return PosOfflineAuthorizationLeaseDecision.Deny(
                    "offline_lease_expired",
                    effectiveExpiry,
                    estimatedServerNow);
            }

            return PosOfflineAuthorizationLeaseDecision.Allow(effectiveExpiry, estimatedServerNow);
        }

        private static bool TryParseUtc(string value, out DateTimeOffset parsed)
        {
            return DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed);
        }
    }

    public sealed class PosOfflineAuthorizationLeaseDecision
    {
        private PosOfflineAuthorizationLeaseDecision(
            bool allowed,
            string code,
            DateTimeOffset? effectiveExpiresAt,
            DateTimeOffset? estimatedServerNow)
        {
            Allowed = allowed;
            Code = code ?? string.Empty;
            EffectiveExpiresAt = effectiveExpiresAt;
            EstimatedServerNow = estimatedServerNow;
        }

        public bool Allowed { get; }
        public string Code { get; }
        public DateTimeOffset? EffectiveExpiresAt { get; }
        public DateTimeOffset? EstimatedServerNow { get; }

        internal static PosOfflineAuthorizationLeaseDecision Allow(
            DateTimeOffset effectiveExpiresAt,
            DateTimeOffset estimatedServerNow)
        {
            return new PosOfflineAuthorizationLeaseDecision(
                true,
                "ok",
                effectiveExpiresAt,
                estimatedServerNow);
        }

        internal static PosOfflineAuthorizationLeaseDecision Deny(
            string code,
            DateTimeOffset? effectiveExpiresAt = null,
            DateTimeOffset? estimatedServerNow = null)
        {
            return new PosOfflineAuthorizationLeaseDecision(
                false,
                code,
                effectiveExpiresAt,
                estimatedServerNow);
        }
    }
}
