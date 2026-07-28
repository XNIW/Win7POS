using System;

namespace Win7POS.Core.Security
{
    public static class SensitiveValueLogScanPolicy
    {
        private const int ShortNumericMaximumLength = 8;

        public static bool ContainsSensitiveValue(string text, string sensitiveValue)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(sensitiveValue))
            {
                return false;
            }

            if (!IsShortNumeric(sensitiveValue))
            {
                return text.IndexOf(sensitiveValue, StringComparison.Ordinal) >= 0;
            }

            var start = 0;
            while (start <= text.Length - sensitiveValue.Length)
            {
                var match = text.IndexOf(
                    sensitiveValue,
                    start,
                    StringComparison.Ordinal);
                if (match < 0)
                {
                    return false;
                }

                var beforeIsDigit = match > 0 && char.IsDigit(text[match - 1]);
                var afterIndex = match + sensitiveValue.Length;
                var afterIsDigit = afterIndex < text.Length && char.IsDigit(text[afterIndex]);
                if (!beforeIsDigit && !afterIsDigit)
                {
                    return true;
                }

                start = match + 1;
            }

            return false;
        }

        private static bool IsShortNumeric(string value)
        {
            if (value.Length == 0 || value.Length > ShortNumericMaximumLength)
            {
                return false;
            }

            for (var index = 0; index < value.Length; index++)
            {
                if (!char.IsDigit(value[index]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
