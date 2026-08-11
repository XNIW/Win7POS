using System;

namespace Win7POS.Core.Logging
{
    /// <summary>
    /// Process-wide owner of the one bounded application-log queue and writer thread.
    /// All entry points fail closed so logging can never break a POS operation or startup.
    /// </summary>
    public static class ProcessFileLog
    {
        private static readonly TimeSpan ProcessExitFlushTimeout =
            TimeSpan.FromMilliseconds(500);
        private static readonly Lazy<BoundedAsyncLogWriter> Writer =
            new Lazy<BoundedAsyncLogWriter>(CreateWriter, true);

        static ProcessFileLog()
        {
            try
            {
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            }
            catch
            {
                // Process-exit registration is best-effort; App.OnExit also flushes.
            }
        }

        public static bool TryWrite(LogLevel level, string source, string message)
        {
            try
            {
                return Writer.Value.TryWrite(level, source, message);
            }
            catch
            {
                return false;
            }
        }

        public static LogWriterMetrics GetMetrics()
        {
            try
            {
                return Writer.Value.GetMetrics();
            }
            catch
            {
                return UnavailableMetrics();
            }
        }

        public static bool Shutdown(TimeSpan timeout)
        {
            try
            {
                return !Writer.IsValueCreated || Writer.Value.Shutdown(timeout);
            }
            catch
            {
                return false;
            }
        }

        private static BoundedAsyncLogWriter CreateWriter()
        {
            return new BoundedAsyncLogWriter(
                new RotatingFileLogSink(AppPaths.LogPath));
        }

        private static LogWriterMetrics UnavailableMetrics()
        {
            return new LogWriterMetrics(
                acceptedInfo: 0,
                acceptedWarning: 0,
                acceptedError: 0,
                writtenInfo: 0,
                writtenWarning: 0,
                writtenError: 0,
                droppedInfo: 0,
                droppedWarning: 0,
                droppedError: 0,
                writerFailures: 1,
                highWaterMark: 0,
                currentPending: 0,
                isAccepting: false,
                isWriterAlive: false,
                isFaulted: true);
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            Shutdown(ProcessExitFlushTimeout);
        }
    }
}
