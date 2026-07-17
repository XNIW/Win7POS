using System;

namespace Win7POS.Core.Online
{
    public enum StartOfDaySalesDrainDecision
    {
        Complete = 0,
        ContinueBackground = 1,
        Blocked = 2
    }

    public static class StartOfDaySalesDrainPolicy
    {
        public static StartOfDaySalesDrainDecision Evaluate(
            long pendingSales,
            long retrySales,
            long inProgressSales,
            long blockedSales)
        {
            EnsureNonNegative(pendingSales, nameof(pendingSales));
            EnsureNonNegative(retrySales, nameof(retrySales));
            EnsureNonNegative(inProgressSales, nameof(inProgressSales));
            EnsureNonNegative(blockedSales, nameof(blockedSales));

            if (blockedSales > 0)
            {
                return StartOfDaySalesDrainDecision.Blocked;
            }

            if (pendingSales > 0 || retrySales > 0 || inProgressSales > 0)
            {
                return StartOfDaySalesDrainDecision.ContinueBackground;
            }

            return StartOfDaySalesDrainDecision.Complete;
        }

        private static void EnsureNonNegative(long value, string parameterName)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }
}
