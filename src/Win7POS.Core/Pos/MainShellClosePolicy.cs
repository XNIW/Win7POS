using System;

namespace Win7POS.Core.Pos
{
    [Flags]
    public enum MainShellCloseState
    {
        Idle = 0,
        CartNotEmpty = 1,
        PaymentInProgress = 2,
        CriticalDatabaseOperation = 4,
        FullCatalogRepairInProgress = 8,
        IncrementalSyncInProgress = 16,
        SessionEnding = 32,
        ProgrammaticClose = 64
    }

    public enum MainShellCloseIntent
    {
        Close,
        Minimize
    }

    public enum MainShellCloseDecision
    {
        Allow,
        RequireConfirmation,
        RequireCartWarning,
        BlockUntilOperationCompletes,
        BypassForSystemShutdown
    }

    public static class MainShellClosePolicy
    {
        public static MainShellCloseDecision Decide(
            MainShellCloseState state,
            MainShellCloseIntent intent = MainShellCloseIntent.Close)
        {
            if (intent == MainShellCloseIntent.Minimize)
            {
                return MainShellCloseDecision.Allow;
            }

            if ((state & MainShellCloseState.SessionEnding) != 0)
            {
                return MainShellCloseDecision.BypassForSystemShutdown;
            }

            if ((state & MainShellCloseState.ProgrammaticClose) != 0)
            {
                return MainShellCloseDecision.Allow;
            }

            var critical = MainShellCloseState.PaymentInProgress |
                           MainShellCloseState.CriticalDatabaseOperation |
                           MainShellCloseState.FullCatalogRepairInProgress;
            if ((state & critical) != 0)
            {
                return MainShellCloseDecision.BlockUntilOperationCompletes;
            }

            if ((state & MainShellCloseState.CartNotEmpty) != 0)
            {
                return MainShellCloseDecision.RequireCartWarning;
            }

            return MainShellCloseDecision.RequireConfirmation;
        }
    }
}
