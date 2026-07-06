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
                @"(?i)(sessionToken|deviceToken|trustedDeviceToken|pin|password|credential)\s*[:=]\s*\S+",
                "$1=[redacted]");
            sanitized = Regex.Replace(sanitized, @"[A-Za-z]:\\[^\s|]+", "[path]");
            sanitized = Regex.Replace(sanitized, @"/(?:Users|private|tmp|var)/[^\s|]+", "[path]");
            return sanitized.Length > 500 ? sanitized.Substring(0, 500) : sanitized;
        }
    }
}
