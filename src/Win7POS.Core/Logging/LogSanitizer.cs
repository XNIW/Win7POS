using System;
using System.Text.RegularExpressions;

namespace Win7POS.Core.Logging
{
    /// <summary>
    /// Produces one bounded, redacted log-line field before it enters the process queue.
    /// Input is bounded before regular-expression processing so producer cost cannot grow
    /// with an untrusted response body or exception message.
    /// </summary>
    public static class LogSanitizer
    {
        public const int MaxStoredChars = 4 * 1024;
        public const int MaxInputChars = 16 * 1024;
        public const int DefaultMaxMessageLength = MaxStoredChars;
        public const int DefaultMaxSourceLength = 128;
        private const string TruncationMarker = "[truncated]";

        private const RegexOptions CommonOptions =
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

        // Once a sensitive structured key is seen, retain no remainder of that logical
        // line. This deliberately over-redacts malformed JSON, escaped quotes, nested
        // values, and subsequent fields instead of trying to recover an unsafe boundary.
        private static readonly Regex StructuredSecret = new Regex(
            @"((?<![A-Za-z0-9_-])""?(?:session[_-]?token|device[_-]?token|trusted[_-]?device[_-]?token|access[_-]?token|refresh[_-]?token|client[_-]?secret|api[_-]?key|apikey|token|pin|password|credential|pwd|db_password|database password)""?\s*:\s*)[^\r\n]*",
            CommonOptions);

        private static readonly Regex ConnectionStringSecret = new Regex(
            @"((?<![A-Za-z0-9_-]);?\s*(?:Pwd|Password|DB[_ ]Password|Database[_ ]Password)\s*=\s*)[^;\r\n]*",
            CommonOptions);

        private static readonly Regex KeyValueSecret = new Regex(
            @"((?<![A-Za-z0-9_-])(?:session[_-]?token|device[_-]?token|trusted[_-]?device[_-]?token|access[_-]?token|refresh[_-]?token|client[_-]?secret|api[_-]?key|apikey|token|pin|credential)\s*[=:]\s*)[^;|\r\n]*",
            CommonOptions);

        private static readonly Regex BearerToken = new Regex(
            @"(Authorization\s*:\s*Bearer\s+)[A-Za-z0-9._~+/-]+=*",
            CommonOptions);

        private static readonly Regex McPosToken = new Regex(
            @"mcpos_(device|session)_[A-Za-z0-9_-]+",
            CommonOptions);

        private static readonly Regex ProviderSecret = new Regex(
            @"\b(?:sk[-_]|sb_secret_)[A-Za-z0-9_-]{12,}\b",
            CommonOptions);

        private static readonly Regex Jwt = new Regex(
            @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex PrivateKey = new Regex(
            @"-----BEGIN (?:RSA |OPENSSH |EC )?PRIVATE KEY-----.*?(?:-----END (?:RSA |OPENSSH |EC )?PRIVATE KEY-----|\z)",
            CommonOptions | RegexOptions.Singleline);

        private static readonly Regex WindowsPersonalPath = new Regex(
            @"[A-Za-z]:\\[^\r\n|;]*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex UnixPersonalPath = new Regex(
            @"/(?:Users|private|tmp|var)/[^\r\n|;]*",
            CommonOptions);

        private static readonly Regex ControlCharacters = new Regex(
            @"[\x00-\x1F\x7F]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string Sanitize(string value)
        {
            return Sanitize(value, DefaultMaxMessageLength);
        }

        public static string Sanitize(string value, int maxLength)
        {
            if (maxLength < 0 || maxLength > MaxStoredChars)
            {
                throw new ArgumentOutOfRangeException(nameof(maxLength));
            }

            if (maxLength == 0 || string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            // Work is independently bounded. Processing up to four retained lengths lets
            // redaction complete before the final storage bound without allowing an HTTP
            // response body or exception graph to create unbounded producer work.
            var scaledWorkLimit = maxLength > int.MaxValue / 4 ? int.MaxValue : maxLength * 4;
            var workLimit = Math.Min(MaxInputChars, scaledWorkLimit);
            if (workLimit < maxLength)
            {
                workLimit = maxLength;
            }

            var inputWasTruncated = value.Length > workLimit;
            var sanitized = inputWasTruncated
                ? value.Substring(0, workLimit)
                : value;

            // Normalize every logical separator before matching. Sensitive values cannot
            // escape a matcher by continuing after CR/LF or another control character.
            sanitized = sanitized.Replace("\r", " ").Replace("\n", " ");
            sanitized = ControlCharacters.Replace(sanitized, " ");

            sanitized = StructuredSecret.Replace(sanitized, "$1[redacted]");
            sanitized = ConnectionStringSecret.Replace(sanitized, "$1[redacted]");
            sanitized = KeyValueSecret.Replace(sanitized, "$1[redacted]");
            sanitized = BearerToken.Replace(sanitized, "$1[redacted]");
            sanitized = McPosToken.Replace(sanitized, "mcpos_$1_[redacted]");
            sanitized = ProviderSecret.Replace(sanitized, "[secret-redacted]");
            sanitized = Jwt.Replace(sanitized, "[jwt-redacted]");
            sanitized = PrivateKey.Replace(sanitized, "[private-key-redacted]");
            sanitized = WindowsPersonalPath.Replace(sanitized, "[path]");
            sanitized = UnixPersonalPath.Replace(sanitized, "[path]");

            // A cut token can be arbitrarily long and therefore cannot be made safe with
            // a fixed tail window. Deliberately discard the complete message whenever
            // either bounded-work or bounded-storage processing required truncation.
            if (inputWasTruncated || sanitized.Length > maxLength)
            {
                return BoundedTruncationMarker(maxLength);
            }

            return sanitized;
        }

        public static string SanitizeSource(string source, int maxLength = DefaultMaxSourceLength)
        {
            if (maxLength < 0 || maxLength > DefaultMaxSourceLength)
            {
                throw new ArgumentOutOfRangeException(nameof(maxLength));
            }

            var sanitized = Sanitize(source, maxLength);
            return sanitized.Replace('[', '_').Replace(']', '_');
        }

        private static string BoundedTruncationMarker(int maxLength)
        {
            return maxLength >= TruncationMarker.Length
                ? TruncationMarker
                : TruncationMarker.Substring(0, maxLength);
        }
    }
}
