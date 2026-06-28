using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Win7POS.Core;

namespace Win7POS.Wpf.Infrastructure
{
    /// <summary>
    /// Logger su file per Win7POS. Formato riga per facile debug:
    /// yyyy-MM-dd HH:mm:ss.fff [LEVEL] [Source.Method] Operazione | Dettaglio
    /// Per errori: include Exception type, Message, StackTrace e InnerException chain.
    /// Cercare in log: [ERROR], [WARN], nome metodo, messaggio eccezione.
    /// </summary>
    public sealed class FileLogger
    {
        private const long MaxLogBytes = 5L * 1024L * 1024L;
        private const int RetainedLogFiles = 5;
        private static readonly object _writeLock = new object();
        private readonly string _source;

        public FileLogger(string source = null)
        {
            _source = source ?? "App";
        }

        public void LogInfo(string message)
        {
            WriteLine("INFO", _source, message ?? string.Empty, null);
        }

        public void LogWarning(string message, Exception ex = null)
        {
            var detail = message ?? string.Empty;
            if (ex != null)
                detail += " | " + FormatExceptionFull(ex);
            WriteLine("WARN", _source, detail, null);
        }

        /// <summary>Log con contesto operazione. Per debug: cerca [Source.Method] o operazione nel log.</summary>
        public void LogError(Exception ex, string operation)
        {
            var detail = (operation ?? string.Empty);
            if (ex != null)
                detail += " | " + FormatExceptionFull(ex);
            WriteLine("ERROR", _source, detail, ex);
        }

        /// <summary>Formato eccezione completo: tipo, messaggio, stack, inner/aggregate exceptions.</summary>
        public static string FormatExceptionFull(Exception ex)
        {
            if (ex == null) return "";
            var sb = new StringBuilder();
            AppendException(sb, ex, 0, 10);
            return sb.ToString();
        }

        private static void AppendException(StringBuilder sb, Exception ex, int depth, int maxDepth)
        {
            if (ex == null || depth >= maxDepth) return;
            if (depth > 0) sb.Append(" ---INNER--- ");
            sb.Append(ex.GetType().FullName ?? "Exception");
            sb.Append(": ");
            sb.Append(ex.Message ?? "");
            sb.Append(" | StackTrace: ");
            sb.Append(ex.StackTrace ?? "(nessuno)");

            if (ex is AggregateException agg && agg.InnerExceptions?.Count > 0)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    if (depth + 1 >= maxDepth) break;
                    sb.Append(" ---AGGREGATE--- ");
                    AppendException(sb, inner, depth + 1, maxDepth);
                }
            }
            else if (ex.InnerException != null)
            {
                AppendException(sb, ex.InnerException, depth + 1, maxDepth);
            }
        }

        private static void WriteLine(string level, string source, string message, Exception ex)
        {
            try
            {
                AppPaths.EnsureDataDirectories();
                var prefix = string.IsNullOrEmpty(source)
                    ? $"[{level}]"
                    : $"[{level}][{source}]";
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + prefix + " " + Sanitize(message) + Environment.NewLine;
                lock (_writeLock)
                {
                    RotateIfNeeded();
                    File.AppendAllText(AppPaths.LogPath, line, Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never break POS flow.
            }
        }

        private static string Sanitize(string value)
        {
            var sanitized = value ?? string.Empty;
            sanitized = Regex.Replace(
                sanitized,
                @"(?i)(sessionToken|deviceToken|trustedDeviceToken|pin|password|credential|pwd|db_password|database password)\s*[:=]\s*\S+",
                "$1=[redacted]");
            sanitized = Regex.Replace(
                sanitized,
                @"(?i)(""?(sessionToken|deviceToken|trustedDeviceToken|pin|password|credential|pwd|db_password|database password)""?\s*:\s*"")[^""]+("")",
                "$1[redacted]$3");
            sanitized = Regex.Replace(
                sanitized,
                @"(?i)(;?\s*(?:Pwd|Password|DB Password|Database Password)\s*=\s*)[^;|\s]+",
                "$1[redacted]");
            sanitized = Regex.Replace(
                sanitized,
                @"(?i)(Authorization\s*:\s*Bearer\s+)[A-Za-z0-9._~+/-]+=*",
                "$1[redacted]");
            sanitized = Regex.Replace(
                sanitized,
                @"(?i)mcpos_(device|session)_[A-Za-z0-9_-]+",
                "mcpos_$1_[redacted]");
            sanitized = Regex.Replace(sanitized, @"[A-Za-z]:\\[^\s|]+", "[path]");
            sanitized = Regex.Replace(sanitized, @"/(?:Users|private|tmp|var)/[^\s|]+", "[path]");
            return sanitized;
        }

        private static void RotateIfNeeded()
        {
            var logPath = AppPaths.LogPath;
            if (!File.Exists(logPath))
            {
                return;
            }

            var info = new FileInfo(logPath);
            if (info.Length < MaxLogBytes)
            {
                return;
            }

            for (var i = RetainedLogFiles - 1; i >= 1; i--)
            {
                var source = RotatedPath(i);
                var target = RotatedPath(i + 1);

                if (!File.Exists(source))
                {
                    continue;
                }

                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Move(source, target);
            }

            var firstArchive = RotatedPath(1);
            if (File.Exists(firstArchive))
            {
                File.Delete(firstArchive);
            }

            File.Move(logPath, firstArchive);
        }

        private static string RotatedPath(int index)
        {
            return AppPaths.LogPath + "." + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
