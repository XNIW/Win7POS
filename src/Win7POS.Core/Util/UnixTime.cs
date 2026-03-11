using System;

namespace Win7POS.Core.Util
{
    public static class UnixTime
    {
        public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        /// <summary>Secondi da epoch (UTC).</summary>
        public static long NowSeconds() => NowMs() / 1000;
    }
}
