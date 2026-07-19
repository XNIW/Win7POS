using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Win7POS.Wpf.Infrastructure
{
    public static class StartupTrace
    {
        private static readonly object WriteLock = new object();

        public static void Write(string phase, Exception exception = null)
        {
            try
            {
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                    " " + Sanitize(phase);
                if (exception != null)
                {
                    line += " | " + exception.GetType().FullName + ": " + Sanitize(exception.Message);
                }

                line += Environment.NewLine;

                lock (WriteLock)
                {
                    if (!TryAppendProgramData(line))
                    {
                        TryAppendAppFolder(line);
                    }
                }
            }
            catch
            {
                // Startup tracing must never block process startup.
            }
        }

        private static bool TryAppendProgramData(string line)
        {
            try
            {
                var dataRoot = Environment.GetEnvironmentVariable("WIN7POS_DATA_DIR");
                if (string.IsNullOrWhiteSpace(dataRoot))
                {
                    var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                    if (string.IsNullOrWhiteSpace(programData))
                    {
                        return false;
                    }

                    dataRoot = Path.Combine(programData, "Win7POS");
                }

                var logs = Path.Combine(dataRoot, "logs");
                Directory.CreateDirectory(logs);
                File.AppendAllText(Path.Combine(logs, "startup-trace.log"), line, Encoding.UTF8);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TryAppendAppFolder(string line)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup-trace.log"),
                    line,
                    Encoding.UTF8);
            }
            catch
            {
                // Nothing else is safe this early.
            }
        }

        private static string Sanitize(string value)
        {
            var sanitized = value ?? string.Empty;
            sanitized = Regex.Replace(
                sanitized,
                @"(?i)(session[_-]?token|device[_-]?token|trusted[_-]?device[_-]?token|access[_-]?token|refresh[_-]?token|client[_-]?secret|api[_-]?key|apikey|token|pin|password|credential|pwd|db_password|database password)\s*[:=]\s*\S+",
                "$1=[redacted]");
            sanitized = Regex.Replace(
                sanitized,
                @"(?i)(""?(session[_-]?token|device[_-]?token|trusted[_-]?device[_-]?token|access[_-]?token|refresh[_-]?token|client[_-]?secret|api[_-]?key|apikey|token|pin|password|credential|pwd|db_password|database password)""?\s*:\s*"")[^""]+("")",
                "$1[redacted]$3");
            sanitized = Regex.Replace(
                sanitized,
                @"(?i)(Authorization\s*:\s*Bearer\s+)[A-Za-z0-9._~+/-]+=*",
                "$1[redacted]");
            sanitized = Regex.Replace(
                sanitized,
                @"(?i)mcpos_(device|session)_[A-Za-z0-9_-]+",
                "mcpos_$1_[redacted]");
            sanitized = Regex.Replace(sanitized, @"(?i)\b(?:sk[-_]|sb_secret_)[A-Za-z0-9_-]{12,}\b", "[secret-redacted]");
            sanitized = Regex.Replace(sanitized, @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", "[jwt-redacted]");
            sanitized = Regex.Replace(sanitized, @"(?is)-----BEGIN (?:RSA |OPENSSH |EC )?PRIVATE KEY-----.*?(?:-----END (?:RSA |OPENSSH |EC )?PRIVATE KEY-----|\z)", "[private-key-redacted]");
            sanitized = Regex.Replace(sanitized, @"[A-Za-z]:\\[^\s|]+", "[path]");
            sanitized = Regex.Replace(sanitized, @"/(?:Users|private|tmp|var)/[^\s|]+", "[path]");
            return sanitized.Length > 500 ? sanitized.Substring(0, 500) : sanitized;
        }
    }
}
