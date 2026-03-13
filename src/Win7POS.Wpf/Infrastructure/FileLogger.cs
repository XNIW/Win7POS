using System;
using System.IO;
using System.Text;
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
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + prefix + " " + message + Environment.NewLine;
                lock (_writeLock)
                {
                    File.AppendAllText(AppPaths.LogPath, line, Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never break POS flow.
            }
        }
    }
}
