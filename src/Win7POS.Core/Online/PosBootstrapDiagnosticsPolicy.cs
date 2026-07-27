using System;

namespace Win7POS.Core.Online
{
    /// <summary>
    /// Maps bounded transport and first-login facts to the diagnostic vocabulary
    /// used by the bootstrap acceptance harness. It deliberately never accepts a
    /// response body or an exception message.
    /// </summary>
    public static class PosBootstrapDiagnosticsPolicy
    {
        public static string GetFailureStage(
            string rootCode,
            int? httpStatus,
            bool requestReachedServer)
        {
            var normalized = NormalizeCode(rootCode);
            if ((normalized.IndexOf("app_version", StringComparison.Ordinal) >= 0 ||
                 normalized.IndexOf("contract_version", StringComparison.Ordinal) >= 0) &&
                (normalized.IndexOf("unsupported", StringComparison.Ordinal) >= 0 ||
                 normalized.IndexOf("rejected", StringComparison.Ordinal) >= 0 ||
                 normalized.IndexOf("invalid", StringComparison.Ordinal) >= 0))
            {
                return "first_login_contract";
            }
            if (normalized.IndexOf("device", StringComparison.Ordinal) >= 0 &&
                (normalized.IndexOf("pending", StringComparison.Ordinal) >= 0 ||
                 normalized.IndexOf("approval", StringComparison.Ordinal) >= 0))
            {
                return "device_pending_approval";
            }
            if (normalized.IndexOf("device", StringComparison.Ordinal) >= 0 &&
                (normalized.IndexOf("denied", StringComparison.Ordinal) >= 0 ||
                 normalized.IndexOf("revoked", StringComparison.Ordinal) >= 0))
            {
                return "device_denied";
            }
            if (normalized.IndexOf("staff", StringComparison.Ordinal) >= 0 &&
                normalized.IndexOf("denied", StringComparison.Ordinal) >= 0)
            {
                return "staff_denied";
            }

            switch (normalized)
            {
                case "validation_failed":
                    return "profile_validation";
                case "invalid_options":
                case "invalid_operation":
                    return "request_build";
                case "invalid_response":
                case "response_too_large":
                    return "invalid_response";
                case "first_login_contract":
                case "shop_metadata_invalid":
                    return "first_login_contract";
                case "device_pending_approval":
                    return "device_pending_approval";
                case "device_denied":
                case "device_revoked":
                    return "device_denied";
                case "staff_denied":
                    return "staff_denied";
                case "sync_maintenance_active":
                case "trusted_time_continuity_lost":
                    return "trusted_session_persistence";
                case "shop_transition_blocked":
                    return "shop_transition";
            }

            if (httpStatus.HasValue || requestReachedServer)
            {
                return "server_response";
            }

            switch (NormalizeCode(rootCode))
            {
                case "dns":
                    return "dns";
                case "tls":
                    return "tls";
                case "timeout":
                    return "timeout";
                case "network":
                case "network_error":
                case "io_error":
                    return "network";
                default:
                    return "server_response";
            }
        }

        public static string GetRootCode(string code, int? httpStatus)
        {
            var normalized = NormalizeCode(code);
            if (!string.IsNullOrWhiteSpace(normalized) && normalized != "failure")
            {
                return normalized;
            }

            if (httpStatus == 401 || httpStatus == 403 || httpStatus == 409)
            {
                return "http_" + httpStatus.Value.ToString();
            }

            if (httpStatus.HasValue && httpStatus.Value >= 500 && httpStatus.Value <= 599)
            {
                return "http_5xx";
            }

            return "unknown";
        }

        public static string GetDeviceApprovalState(string rootCode, string deviceStatus)
        {
            var status = NormalizeCode(deviceStatus);
            if (status == "active" || status == "approved")
            {
                return "approved";
            }
            if (status == "pending" || status == "pending_approval")
            {
                return "pending";
            }
            if (status == "denied" || status == "revoked")
            {
                return "denied";
            }

            var code = NormalizeCode(rootCode);
            if (code.IndexOf("pending", StringComparison.Ordinal) >= 0 ||
                code.IndexOf("approval", StringComparison.Ordinal) >= 0)
            {
                return "pending";
            }
            if (code.IndexOf("device", StringComparison.Ordinal) >= 0 &&
                (code.IndexOf("denied", StringComparison.Ordinal) >= 0 ||
                 code.IndexOf("revoked", StringComparison.Ordinal) >= 0))
            {
                return "denied";
            }

            return "unknown";
        }

        public static bool IsRetryable(string rootCode, int? httpStatus, bool authenticationDenied)
        {
            if (authenticationDenied)
            {
                return false;
            }
            if (httpStatus.HasValue)
            {
                return httpStatus.Value >= 500 && httpStatus.Value <= 599;
            }

            switch (NormalizeCode(rootCode))
            {
                case "dns":
                case "tls":
                case "network":
                case "network_error":
                case "io_error":
                case "timeout":
                    return true;
                default:
                    return false;
            }
        }

        private static string NormalizeCode(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }
    }
}
