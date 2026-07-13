using System;

namespace Win7POS.Core.Security
{
    public enum PosAccessFailureKind
    {
        None = 0,
        NetworkUnavailable,
        ServerNotConfigured,
        Timeout,
        ServerUnavailable,
        AuthenticationDenied,
        DeviceDenied,
        PolicyDenied,
        InvalidContract,
        InvalidResponse,
        InvalidConfiguration,
        Other
    }

    public enum PosAccessNextStep
    {
        RetryOnline = 0,
        OfflineMirrorLogin,
        OfferLocalRecovery,
        LocalRecoveryLogin,
        ExistingUsersDisabled,
        Denied
    }

    public enum PosAuthenticatedAccessMode
    {
        Normal = 0,
        LocalRecovery
    }

    public enum PosShellMode
    {
        Pos = 0,
        Recovery
    }

    public sealed class PosUserBootstrapState
    {
        public int TotalUserRows { get; set; }
        public int ActiveLoginableUsers { get; set; }
        public int ActiveRemoteMirrors { get; set; }

        public bool IsNewDatabase => TotalUserRows == 0;
        public bool HasOnlyDisabledUsers => TotalUserRows > 0 && ActiveLoginableUsers == 0;
        public bool HasActiveLocalRecoveryUsers => ActiveLoginableUsers > 0 && ActiveRemoteMirrors == 0;
    }

    public sealed class PosAccessRecoveryDecision
    {
        public PosAccessRecoveryDecision(PosAccessNextStep nextStep, PosAccessFailureKind failureKind)
        {
            NextStep = nextStep;
            FailureKind = failureKind;
        }

        public bool CanCreateLocalAdmin => NextStep == PosAccessNextStep.OfferLocalRecovery;
        public bool CanUseOfflineMirror => NextStep == PosAccessNextStep.OfflineMirrorLogin;
        public bool CanUseLocalRecoveryLogin => NextStep == PosAccessNextStep.LocalRecoveryLogin;
        public PosAccessFailureKind FailureKind { get; }
        public PosAccessNextStep NextStep { get; }
    }

    public static class PosAccessRecoveryPolicy
    {
        public static PosAccessRecoveryDecision Evaluate(
            PosUserBootstrapState state,
            PosAccessFailureKind failureKind)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            if (IsDenied(failureKind))
            {
                return new PosAccessRecoveryDecision(PosAccessNextStep.Denied, failureKind);
            }

            if (state.HasOnlyDisabledUsers)
            {
                return new PosAccessRecoveryDecision(PosAccessNextStep.ExistingUsersDisabled, failureKind);
            }

            if (failureKind == PosAccessFailureKind.None && state.HasActiveLocalRecoveryUsers)
            {
                return new PosAccessRecoveryDecision(PosAccessNextStep.LocalRecoveryLogin, failureKind);
            }

            if (IsTransientOrMissingServer(failureKind))
            {
                if (state.ActiveRemoteMirrors > 0)
                {
                    return new PosAccessRecoveryDecision(PosAccessNextStep.OfflineMirrorLogin, failureKind);
                }

                if (state.IsNewDatabase)
                {
                    return new PosAccessRecoveryDecision(PosAccessNextStep.OfferLocalRecovery, failureKind);
                }

                if (state.HasActiveLocalRecoveryUsers)
                {
                    return new PosAccessRecoveryDecision(PosAccessNextStep.LocalRecoveryLogin, failureKind);
                }
            }

            return new PosAccessRecoveryDecision(PosAccessNextStep.RetryOnline, failureKind);
        }

        public static PosAccessFailureKind ClassifyOnlineFailure(string code, bool denied)
        {
            if (denied)
            {
                return PosAccessFailureKind.AuthenticationDenied;
            }

            var normalized = (code ?? string.Empty).Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "authorizationfailed":
                case "authorization_failed":
                case "auth_denied":
                case "denied":
                case "forbidden":
                case "http_401":
                case "http_403":
                case "invalid_credential":
                case "invalid_credentials":
                case "revoked":
                case "unauthorized":
                    return PosAccessFailureKind.AuthenticationDenied;
                case "device_denied":
                case "device_revoked":
                    return PosAccessFailureKind.DeviceDenied;
                case "policy_denied":
                case "capability_not_supported":
                    return PosAccessFailureKind.PolicyDenied;
                case "contract_invalid":
                case "unsupported_contract":
                    return PosAccessFailureKind.InvalidContract;
                case "invalid_response":
                    return PosAccessFailureKind.InvalidResponse;
                case "invalid_options":
                case "invalid_operation":
                case "invalid_url":
                    return PosAccessFailureKind.InvalidConfiguration;
                case "server_not_configured":
                case "missing_base_url":
                    return PosAccessFailureKind.ServerNotConfigured;
                case "timeout":
                    return PosAccessFailureKind.Timeout;
                case "network_offline":
                    return PosAccessFailureKind.NetworkUnavailable;
                case "connection_failed":
                case "dns_error":
                case "failure":
                case "io_error":
                case "network_error":
                case "server_unavailable":
                case "tls_error":
                    return PosAccessFailureKind.ServerUnavailable;
                default:
                    return PosAccessFailureKind.Other;
            }
        }

        private static bool IsDenied(PosAccessFailureKind failureKind)
        {
            return failureKind == PosAccessFailureKind.AuthenticationDenied ||
                failureKind == PosAccessFailureKind.DeviceDenied ||
                failureKind == PosAccessFailureKind.PolicyDenied ||
                failureKind == PosAccessFailureKind.InvalidContract ||
                failureKind == PosAccessFailureKind.InvalidResponse;
        }

        private static bool IsTransientOrMissingServer(PosAccessFailureKind failureKind)
        {
            return failureKind == PosAccessFailureKind.NetworkUnavailable ||
                failureKind == PosAccessFailureKind.ServerNotConfigured ||
                failureKind == PosAccessFailureKind.Timeout ||
                failureKind == PosAccessFailureKind.ServerUnavailable;
        }
    }

    public static class PosShellStartupPolicy
    {
        public static PosShellMode Determine(
            PosAuthenticatedAccessMode accessMode,
            bool catalogSaleSafe)
        {
            if (!catalogSaleSafe)
            {
                return PosShellMode.Recovery;
            }

            return PosShellMode.Pos;
        }
    }
}
