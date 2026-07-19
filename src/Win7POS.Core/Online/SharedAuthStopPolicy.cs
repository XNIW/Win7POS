using System;

namespace Win7POS.Core.Online
{
    public static class SharedAuthStopPolicy
    {
        public static bool IsAuthenticationDenied(string code)
        {
            var normalized = (code ?? string.Empty).Trim();
            return string.Equals(normalized, "auth_denied", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "unauthorized", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "forbidden", StringComparison.OrdinalIgnoreCase);
        }
    }
}
