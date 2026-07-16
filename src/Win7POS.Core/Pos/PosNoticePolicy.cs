using System;

namespace Win7POS.Core.Pos
{
    public enum PosNoticeSeverity
    {
        Info = 0,
        Success,
        Warning,
        Error
    }

    public static class PosNoticePolicy
    {
        public static TimeSpan? GetAutoDismissDelay(PosNoticeSeverity severity)
        {
            switch (severity)
            {
                case PosNoticeSeverity.Success:
                    return TimeSpan.FromSeconds(3);
                case PosNoticeSeverity.Info:
                    return TimeSpan.FromSeconds(4);
                case PosNoticeSeverity.Warning:
                    return TimeSpan.FromSeconds(8);
                case PosNoticeSeverity.Error:
                    return null;
                default:
                    throw new ArgumentOutOfRangeException(nameof(severity));
            }
        }

        public static bool CanDismissManually(PosNoticeSeverity severity)
        {
            return severity == PosNoticeSeverity.Warning || severity == PosNoticeSeverity.Error;
        }
    }
}
