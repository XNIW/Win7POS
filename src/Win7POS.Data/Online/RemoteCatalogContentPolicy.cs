using System;
using System.Globalization;
using System.IO;

namespace Win7POS.Data.Online
{
    internal static class RemoteCatalogContentPolicy
    {
        public const int BarcodeMaximumLength = 128;
        public const int CatalogVersionMaximumLength = 128;
        public const int ItemNumberMaximumLength = 128;
        public const int NameMaximumLength = 512;
        public const int RemoteIdMaximumLength = 256;
        public const int SourceMaximumLength = 256;
        public const int SyncCursorMaximumLength = 512;
        public const int TimestampMaximumLength = 64;
        public const int TypeMaximumLength = 64;

        public static bool IsOptionalText(string value, int maximumLength)
        {
            return string.IsNullOrEmpty(value) || IsSafeText(value, maximumLength);
        }

        public static bool IsRequiredText(string value, int maximumLength)
        {
            return !string.IsNullOrWhiteSpace(value) && IsSafeText(value, maximumLength);
        }

        /// <summary>
        /// Identity values must not contain invisible formatting controls. Unlike
        /// display text, these values are never canonicalized or recovered, so a
        /// bidi/format character must remain a fail-closed transport error.
        /// </summary>
        public static bool IsOptionalIdentityText(string value, int maximumLength)
        {
            return string.IsNullOrEmpty(value) || IsSafeIdentityText(value, maximumLength);
        }

        public static bool IsRequiredIdentityText(string value, int maximumLength)
        {
            return !string.IsNullOrWhiteSpace(value) && IsSafeIdentityText(value, maximumLength);
        }

        public static bool IsOptionalCanonicalText(string value, int maximumLength)
        {
            return string.IsNullOrEmpty(value) ||
                (IsSafeText(value, maximumLength) &&
                 string.Equals(value, value.Trim(), StringComparison.Ordinal));
        }

        public static bool IsRequiredCanonicalText(string value, int maximumLength)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                IsSafeText(value, maximumLength) &&
                string.Equals(value, value.Trim(), StringComparison.Ordinal);
        }

        public static bool IsOptionalTimestamp(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return IsOptionalText(value, TimestampMaximumLength);
            }

            if (!IsSafeText(value, TimestampMaximumLength) ||
                !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            return DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _);
        }

        public static void EnsureOptionalText(string value, int maximumLength, string field)
        {
            if (!IsOptionalText(value, maximumLength))
            {
                throw new InvalidDataException(
                    "Remote catalog field rejected: " + (field ?? string.Empty) + ".");
            }
        }

        public static void EnsureOptionalTimestamp(string value, string field)
        {
            if (!IsOptionalTimestamp(value))
            {
                throw new InvalidDataException(
                    "Remote catalog timestamp rejected: " + (field ?? string.Empty) + ".");
            }
        }

        public static void EnsureOptionalCanonicalText(
            string value,
            int maximumLength,
            string field)
        {
            if (!IsOptionalCanonicalText(value, maximumLength))
            {
                throw new InvalidDataException(
                    "Remote catalog canonical field rejected: " + (field ?? string.Empty) + ".");
            }
        }

        private static bool IsSafeText(string value, int maximumLength)
        {
            if (value == null || maximumLength < 0 || value.Length > maximumLength)
            {
                return false;
            }

            for (var index = 0; index < value.Length; index++)
            {
                var current = value[index];
                if (char.IsControl(current))
                {
                    return false;
                }

                if (char.IsHighSurrogate(current))
                {
                    if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                    {
                        return false;
                    }

                    index++;
                    continue;
                }

                if (char.IsLowSurrogate(current))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsSafeIdentityText(string value, int maximumLength)
        {
            if (!IsSafeText(value, maximumLength))
            {
                return false;
            }

            for (var index = 0; index < value.Length; index++)
            {
                if (char.GetUnicodeCategory(value[index]) == UnicodeCategory.Format)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
