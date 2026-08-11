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
            return Evaluate(
                session,
                localNow,
                minimumEstimatedServerNow,
                null);
        }

        public static PosOfflineAuthorizationLeaseDecision Evaluate(
            PosTrustedDeviceSession session,
            DateTimeOffset localNow,
            DateTimeOffset? minimumEstimatedServerNow,
            DateTimeOffset? minimumTrustedServerNow)
        {
            return EvaluateInternal(
                session,
                localNow,
                minimumEstimatedServerNow,
                minimumTrustedServerNow,
                requireAuthoritativeOfflineExpiry: true);
        }

        public static PosOfflineAuthorizationLeaseDecision ValidateOnlineReceipt(
            PosTrustedDeviceSession session,
            DateTimeOffset localNow)
        {
            return EvaluateInternal(
                session,
                localNow,
                null,
                null,
                requireAuthoritativeOfflineExpiry: false);
        }

        private static PosOfflineAuthorizationLeaseDecision EvaluateInternal(
            PosTrustedDeviceSession session,
            DateTimeOffset localNow,
            DateTimeOffset? minimumEstimatedServerNow,
            DateTimeOffset? minimumTrustedServerNow,
            bool requireAuthoritativeOfflineExpiry)
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

            DateTimeOffset authoritativeOfflineExpiry = default;
            if (!session.OfflineAuthorizationAttested)
            {
                if (requireAuthoritativeOfflineExpiry)
                {
                    return PosOfflineAuthorizationLeaseDecision.Deny(
                        "offline_attestation_required");
                }
            }
            else if (!TryParseUtc(
                session.EffectiveOfflineAuthorizationExpiresAt,
                out authoritativeOfflineExpiry) ||
                authoritativeOfflineExpiry <= lastServerAt)
            {
                return PosOfflineAuthorizationLeaseDecision.Deny(
                    "offline_attestation_invalid");
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
                    estimatedServerNow,
                    estimatedServerNow);
            }

            var trustedServerNow = estimatedServerNow;
            if (minimumTrustedServerNow.HasValue &&
                minimumTrustedServerNow.Value > trustedServerNow)
            {
                trustedServerNow = minimumTrustedServerNow.Value;
            }

            var effectiveExpiry = sessionExpiresAt <= maximumOfflineExpiry
                ? sessionExpiresAt
                : maximumOfflineExpiry;
            if (session.OfflineAuthorizationAttested &&
                authoritativeOfflineExpiry < effectiveExpiry)
            {
                effectiveExpiry = authoritativeOfflineExpiry;
            }
            if (trustedServerNow >= effectiveExpiry)
            {
                return PosOfflineAuthorizationLeaseDecision.Deny(
                    "offline_lease_expired",
                    effectiveExpiry,
                    trustedServerNow,
                    estimatedServerNow);
            }

            return PosOfflineAuthorizationLeaseDecision.Allow(
                effectiveExpiry,
                trustedServerNow,
                estimatedServerNow);
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
            DateTimeOffset? estimatedServerNow,
            DateTimeOffset? wallEstimatedServerNow)
        {
            Allowed = allowed;
            Code = code ?? string.Empty;
            EffectiveExpiresAt = effectiveExpiresAt;
            EstimatedServerNow = estimatedServerNow;
            WallEstimatedServerNow = wallEstimatedServerNow ?? estimatedServerNow;
        }

        public bool Allowed { get; }
        public string Code { get; }
        public DateTimeOffset? EffectiveExpiresAt { get; }
        public DateTimeOffset? EstimatedServerNow { get; }
        public DateTimeOffset? WallEstimatedServerNow { get; }

        internal static PosOfflineAuthorizationLeaseDecision Allow(
            DateTimeOffset effectiveExpiresAt,
            DateTimeOffset estimatedServerNow,
            DateTimeOffset wallEstimatedServerNow)
        {
            return new PosOfflineAuthorizationLeaseDecision(
                true,
                "ok",
                effectiveExpiresAt,
                estimatedServerNow,
                wallEstimatedServerNow);
        }

        public static PosOfflineAuthorizationLeaseDecision Deny(
            string code,
            DateTimeOffset? effectiveExpiresAt = null,
            DateTimeOffset? estimatedServerNow = null,
            DateTimeOffset? wallEstimatedServerNow = null)
        {
            return new PosOfflineAuthorizationLeaseDecision(
                false,
                code,
                effectiveExpiresAt,
                estimatedServerNow,
                wallEstimatedServerNow);
        }
    }
}
