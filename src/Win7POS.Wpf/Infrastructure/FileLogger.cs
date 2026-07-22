using System;
using System.Text;
using Win7POS.Core.Logging;

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
        private const int MaxExceptionDepth = 10;
        private const int MaxExceptions = 16;
        private const int MaxExceptionFieldChars = 4096;
        private const int MaxFormattedExceptionChars = 32 * 1024;
        private const int MaxExceptionOperationChars = 1024;
        private const int MaxExceptionLogChars = 3072;
        private readonly string _source;

        public FileLogger(string source = null)
        {
            _source = source ?? "App";
        }

        public void LogInfo(string message)
        {
            WriteLine(LogLevel.Info, _source, message ?? string.Empty);
        }

        public void LogWarning(string message, Exception ex = null)
        {
            try
            {
                WriteLine(
                    LogLevel.Warning,
                    _source,
                    ComposeExceptionDetail(message, ex));
            }
            catch
            {
                // Exception formatting and logging must never break POS flow.
            }
        }

        /// <summary>Log con contesto operazione. Per debug: cerca [Source.Method] o operazione nel log.</summary>
        public void LogError(Exception ex, string operation)
        {
            try
            {
                WriteLine(
                    LogLevel.Error,
                    _source,
                    ComposeExceptionDetail(operation, ex));
            }
            catch
            {
                // Exception formatting and logging must never break POS flow.
            }
        }

        /// <summary>Formato eccezione completo: tipo, messaggio, stack, inner/aggregate exceptions.</summary>
        public static string FormatExceptionFull(Exception ex)
        {
            if (ex == null) return "";
            try
            {
                var sb = new StringBuilder();
                var exceptionCount = 0;
                AppendException(
                    sb,
                    ex,
                    depth: 0,
                    maxDepth: MaxExceptionDepth,
                    ref exceptionCount,
                    maxExceptions: MaxExceptions);
                return sb.ToString();
            }
            catch
            {
                return "[exception-formatting-unavailable]";
            }
        }

        internal static bool Shutdown(TimeSpan timeout)
        {
            return ProcessFileLog.Shutdown(timeout);
        }

        public static LogWriterMetrics GetMetrics()
        {
            return ProcessFileLog.GetMetrics();
        }

        private static string ComposeExceptionDetail(string operation, Exception ex)
        {
            if (ex == null)
            {
                return operation ?? string.Empty;
            }

            var safeOperation = LogSanitizer.Sanitize(
                operation,
                MaxExceptionOperationChars);
            var safeException = LogSanitizer.Sanitize(
                FormatExceptionFull(ex),
                MaxExceptionLogChars);
            return safeOperation + " | " + safeException;
        }

        private static void AppendException(
            StringBuilder sb,
            Exception ex,
            int depth,
            int maxDepth,
            ref int exceptionCount,
            int maxExceptions)
        {
            if (ex == null || depth >= maxDepth || exceptionCount >= maxExceptions ||
                sb.Length >= MaxFormattedExceptionChars)
            {
                return;
            }

            exceptionCount++;
            if (depth > 0)
            {
                AppendBounded(sb, " ---INNER--- ", MaxFormattedExceptionChars);
            }

            AppendBounded(
                sb,
                SafeExceptionType(ex),
                MaxExceptionFieldChars);
            AppendBounded(sb, ": ", MaxFormattedExceptionChars);
            AppendBounded(sb, SafeExceptionMessage(ex), MaxExceptionFieldChars);
            AppendBounded(sb, " | StackTrace: ", MaxFormattedExceptionChars);
            AppendBounded(sb, SafeExceptionStackTrace(ex), MaxExceptionFieldChars);

            var aggregate = ex as AggregateException;
            if (aggregate != null && aggregate.InnerExceptions != null &&
                aggregate.InnerExceptions.Count > 0)
            {
                for (var index = 0;
                     index < aggregate.InnerExceptions.Count &&
                     depth + 1 < maxDepth &&
                     exceptionCount < maxExceptions &&
                     sb.Length < MaxFormattedExceptionChars;
                     index++)
                {
                    AppendBounded(sb, " ---AGGREGATE--- ", MaxFormattedExceptionChars);
                    AppendException(
                        sb,
                        aggregate.InnerExceptions[index],
                        depth + 1,
                        maxDepth,
                        ref exceptionCount,
                        maxExceptions);
                }

                return;
            }

            AppendException(
                sb,
                SafeInnerException(ex),
                depth + 1,
                maxDepth,
                ref exceptionCount,
                maxExceptions);
        }

        private static string SafeExceptionType(Exception ex)
        {
            try
            {
                return ex.GetType().FullName ?? "Exception";
            }
            catch
            {
                return "[exception-type-unavailable]";
            }
        }

        private static string SafeExceptionMessage(Exception ex)
        {
            try
            {
                return ex.Message ?? string.Empty;
            }
            catch
            {
                return "[exception-message-unavailable]";
            }
        }

        private static string SafeExceptionStackTrace(Exception ex)
        {
            try
            {
                return ex.StackTrace ?? "(nessuno)";
            }
            catch
            {
                return "[exception-stack-unavailable]";
            }
        }

        private static Exception SafeInnerException(Exception ex)
        {
            try
            {
                return ex.InnerException;
            }
            catch
            {
                return null;
            }
        }

        private static void AppendBounded(StringBuilder sb, string value, int fieldLimit)
        {
            if (string.IsNullOrEmpty(value) || sb.Length >= MaxFormattedExceptionChars)
            {
                return;
            }

            var remaining = MaxFormattedExceptionChars - sb.Length;
            var take = Math.Min(Math.Min(value.Length, fieldLimit), remaining);
            sb.Append(value, 0, take);
        }

        private static void WriteLine(LogLevel level, string source, string message)
        {
            try
            {
                ProcessFileLog.TryWrite(level, source, message);
            }
            catch
            {
                // Logging must never break POS flow.
            }
        }

        private static string Sanitize(string value)
        {
            return LogSanitizer.Sanitize(value);
        }
    }
}
