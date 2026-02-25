using System;
using System.Linq;

namespace Win7POS.Core.Util
{
    public static class SaleCodeGenerator
    {
        private static readonly Random _rng = new Random();

        public static string NewCode(string prefix = "V")
        {
            var ts = ToBase36(UnixTime.NowMs()).ToUpperInvariant();
            var rnd = _rng.Next(0, 36 * 36 * 36);
            var rndStr = ToBase36(rnd).ToUpperInvariant().PadLeft(3, '0');
            return $"{prefix}{ts}{rndStr}";
        }

        private static string ToBase36(long value)
        {
            const string chars = "0123456789abcdefghijklmnopqrstuvwxyz";
            if (value == 0) return "0";
            var v = Math.Abs(value);
            var result = "";
            while (v > 0)
            {
                result = chars[(int)(v % 36)] + result;
                v /= 36;
            }
            return result;
        }
    }
}
