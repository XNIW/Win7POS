using Win7POS.Core.Security;

namespace Win7POS.Core.Online
{
    /// <summary>
    /// Pure state rules shared by the WPF startup coordinator. Keeping these
    /// decisions in Core makes safe/recovery/access regressions executable
    /// without constructing a WPF shell or a live supervisor.
    /// </summary>
    public static class PosStartupCoordinatorPolicy
    {
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
