using Win7POS.Core.Security;

namespace Win7POS.Core.Online
{
    public sealed class PosPublishedCatalogState
    {
        public PosPublishedCatalogState(bool remoteSaleSafe, bool authoritativelyEmpty, bool recoveryRequired)
        { RemoteSaleSafe = remoteSaleSafe; AuthoritativelyEmpty = authoritativelyEmpty; RecoveryRequired = recoveryRequired; }
        public bool RemoteSaleSafe { get; }
        public bool AuthoritativelyEmpty { get; }
        public bool RecoveryRequired { get; }
    }

    public enum PosShellReadiness
    {
        Blocked = 0,
        Recovery,
        AuthenticatedEmpty,
        SaleReady,
        AuthenticatedPending
    }

    public sealed class PosShellAdmission
    {
        internal PosShellAdmission(PosShellReadiness readiness) { Readiness = readiness; }
        public PosShellReadiness Readiness { get; }
        public bool CanEnterShell => Readiness != PosShellReadiness.Blocked;
        public bool CanUseNormalNavigation => CanUsePosWorkspace;
        public bool CanUsePosWorkspace => Readiness == PosShellReadiness.AuthenticatedEmpty ||
            Readiness == PosShellReadiness.AuthenticatedPending || Readiness == PosShellReadiness.SaleReady;
        // Compatibility: this describes the published remote catalog, not permission to pay.
        public bool CanSell => Readiness == PosShellReadiness.SaleReady;
        public PosShellMode ShellMode => CanUsePosWorkspace ? PosShellMode.Pos : PosShellMode.Recovery;
    }

    /// <summary>
    /// Pure state rules shared by the WPF startup coordinator. Keeping these
    /// decisions in Core makes safe/recovery/access regressions executable
    /// without constructing a WPF shell or a live supervisor.
    /// </summary>
    public static class PosStartupCoordinatorPolicy
    {
        // Authentication, shell admission and sale admission are independent.
        // The empty input is a scoped authoritative observation, never a count
        // of zero products or a generic bootstrap Success. History permissions
        // have no role in admitting an authenticated operator to the shell.
        public static PosShellAdmission EvaluateAdmission(
            PosAuthenticatedAccessMode accessMode,
            bool isLoggedIn,
            bool authorizationAllowed,
            bool catalogSaleSafe,
            bool catalogAuthoritativelyEmpty,
            bool isSafeStart = false,
            bool recoveryRequired = false)
        {
            if (!isLoggedIn || (accessMode != PosAuthenticatedAccessMode.Normal && accessMode != PosAuthenticatedAccessMode.LocalRecovery))
                return new PosShellAdmission(PosShellReadiness.Blocked);
            if (accessMode == PosAuthenticatedAccessMode.LocalRecovery)
                return new PosShellAdmission(PosShellReadiness.Recovery);
            if (!authorizationAllowed) return new PosShellAdmission(PosShellReadiness.Blocked);
            if (isSafeStart || recoveryRequired || (catalogSaleSafe && catalogAuthoritativelyEmpty))
                return new PosShellAdmission(PosShellReadiness.Recovery);
            return new PosShellAdmission(catalogSaleSafe ? PosShellReadiness.SaleReady :
                catalogAuthoritativelyEmpty ? PosShellReadiness.AuthenticatedEmpty : PosShellReadiness.AuthenticatedPending);
        }

        public static bool CanCompleteRecoveryExit(
            PosAuthenticatedAccessMode accessMode,
            bool isLoggedIn,
            bool authorizationAllowed)
        {
            return accessMode == PosAuthenticatedAccessMode.Normal &&
                isLoggedIn &&
                authorizationAllowed;
        }

        public static bool CanResumeAfterMaintenance(
            bool isSafeStart,
            bool isRecoveryMode,
            PosAuthenticatedAccessMode accessMode,
            bool resumeRequested)
        {
            return resumeRequested &&
                !isSafeStart &&
                !isRecoveryMode &&
                accessMode == PosAuthenticatedAccessMode.Normal;
        }

        public static bool CanStartBackground(
            bool isSafeStart,
            bool isRecoveryMode)
        {
            return !isSafeStart && !isRecoveryMode;
        }

        public static PosShellMode DetermineShellMode(
            PosAuthenticatedAccessMode accessMode,
            bool catalogSaleSafe)
        {
            return PosShellStartupPolicy.Determine(accessMode, catalogSaleSafe);
        }
    }
}
