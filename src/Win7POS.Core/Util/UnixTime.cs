using System;

namespace Win7POS.Core.Util
{
    public static class UnixTime
    {
        public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
