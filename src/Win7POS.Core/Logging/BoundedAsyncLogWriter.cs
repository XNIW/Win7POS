using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;

namespace Win7POS.Core.Logging
{
    public enum LogLevel
    {
        Info = 0,
        Warning = 1,
        Error = 2
    }

    /// <summary>
    /// Infrastructure-owned sink. Every supplied string is immutable, already redacted,
    /// length-bounded, and terminated with Environment.NewLine.
    /// </summary>
    public interface ILogBatchSink
    {
        void WriteBatch(IReadOnlyList<string> lines);
    }

    public interface ILogClock
    {
        DateTime Now { get; }
    }

    public sealed class SystemLogClock : ILogClock
    {
        public static readonly SystemLogClock Instance = new SystemLogClock();

        private SystemLogClock()
        {
        }

        public DateTime Now => DateTime.Now;
    }

    public sealed class LogWriterMetrics
    {
        internal LogWriterMetrics(
            long acceptedInfo,
            long acceptedWarning,
            long acceptedError,
            long writtenInfo,
            long writtenWarning,
            long writtenError,
            long droppedInfo,
            long droppedWarning,
            long droppedError,
            long writerFailures,
            int highWaterMark,
            int currentPending,
            bool isAccepting,
            bool isWriterAlive,
            bool isFaulted)
        {
            AcceptedInfo = acceptedInfo;
            AcceptedWarning = acceptedWarning;
            AcceptedError = acceptedError;
            WrittenInfo = writtenInfo;
            WrittenWarning = writtenWarning;
            WrittenError = writtenError;
            DroppedInfo = droppedInfo;
            DroppedWarning = droppedWarning;
            DroppedError = droppedError;
            WriterFailures = writerFailures;
            HighWaterMark = highWaterMark;
            CurrentPending = currentPending;
            IsAccepting = isAccepting;
            IsWriterAlive = isWriterAlive;
            IsFaulted = isFaulted;
        }

        public long AcceptedInfo { get; }
        public long AcceptedWarning { get; }
        public long AcceptedError { get; }
        public long WrittenInfo { get; }
        public long WrittenWarning { get; }
        public long WrittenError { get; }
        public long DroppedInfo { get; }
        public long DroppedWarning { get; }
        public long DroppedError { get; }
        public long WriterFailures { get; }
        public int HighWaterMark { get; }
        public int CurrentPending { get; }
        public bool IsAccepting { get; }
        public bool IsWriterAlive { get; }
        public bool IsFaulted { get; }

        public long AcceptedTotal => AcceptedInfo + AcceptedWarning + AcceptedError;
        public long WrittenTotal => WrittenInfo + WrittenWarning + WrittenError;
        public long DroppedTotal => DroppedInfo + DroppedWarning + DroppedError;
    }

    /// <summary>
    /// A non-blocking producer facade over one bounded process queue and one background
    /// writer. INFO is rejected at infoAdmissionLimit, WARN at warningAdmissionLimit,
    /// and ERROR only at total capacity, reserving progressively more important slots.
    /// </summary>
    public sealed class BoundedAsyncLogWriter : IDisposable
    {
        private const int DefaultRetryDelayMilliseconds = 25;

        private readonly object _gate = new object();
        private readonly Queue<ImmutableLogEntry> _queue;
        private readonly AutoResetEvent _workAvailable;
        private readonly ILogBatchSink _sink;
        private readonly ILogClock _clock;
        private readonly int _capacity;
        private readonly int _infoAdmissionLimit;
        private readonly int _warningAdmissionLimit;
        private readonly int _batchSize;
        private readonly int _maxMessageLength;
        private readonly int _maxSourceLength;
        private readonly int _retryDelayMilliseconds;
        private readonly int _maxConsecutiveWriterFailures;
        private readonly Thread _writerThread;

        private bool _accepting = true;
        private bool _stopRequested;
        private int _pendingCount;
        private int _highWaterMark;
        private long _acceptedInfo;
        private long _acceptedWarning;
        private long _acceptedError;
        private long _writtenInfo;
        private long _writtenWarning;
        private long _writtenError;
        private long _droppedInfo;
        private long _droppedWarning;
        private long _droppedError;
        private long _writerFailures;
        private int _consecutiveWriterFailures;
        private bool _faulted;

        public BoundedAsyncLogWriter(
            ILogBatchSink sink,
            int capacity = 1024,
            int infoAdmissionLimit = 768,
            int warningAdmissionLimit = 960,
            int batchSize = 64,
            int maxMessageLength = LogSanitizer.DefaultMaxMessageLength,
            int maxSourceLength = LogSanitizer.DefaultMaxSourceLength,
            ILogClock clock = null,
            int retryDelayMilliseconds = DefaultRetryDelayMilliseconds,
            int maxConsecutiveWriterFailures = 3)
        {
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (infoAdmissionLimit <= 0 || infoAdmissionLimit > capacity)
                throw new ArgumentOutOfRangeException(nameof(infoAdmissionLimit));
            if (warningAdmissionLimit < infoAdmissionLimit || warningAdmissionLimit > capacity)
                throw new ArgumentOutOfRangeException(nameof(warningAdmissionLimit));
            if (batchSize <= 0 || batchSize > capacity)
                throw new ArgumentOutOfRangeException(nameof(batchSize));
            if (maxMessageLength <= 0 || maxMessageLength > LogSanitizer.MaxStoredChars)
                throw new ArgumentOutOfRangeException(nameof(maxMessageLength));
            if (maxSourceLength <= 0 || maxSourceLength > LogSanitizer.DefaultMaxSourceLength)
                throw new ArgumentOutOfRangeException(nameof(maxSourceLength));
            if (retryDelayMilliseconds < 1)
                throw new ArgumentOutOfRangeException(nameof(retryDelayMilliseconds));
            if (maxConsecutiveWriterFailures < 1)
                throw new ArgumentOutOfRangeException(nameof(maxConsecutiveWriterFailures));

            _sink = sink;
            _capacity = capacity;
            _infoAdmissionLimit = infoAdmissionLimit;
            _warningAdmissionLimit = warningAdmissionLimit;
            _batchSize = batchSize;
            _maxMessageLength = maxMessageLength;
            _maxSourceLength = maxSourceLength;
            _clock = clock ?? SystemLogClock.Instance;
            _retryDelayMilliseconds = retryDelayMilliseconds;
            _maxConsecutiveWriterFailures = maxConsecutiveWriterFailures;
            _queue = new Queue<ImmutableLogEntry>(Math.Min(capacity, 1024));
            _workAvailable = new AutoResetEvent(false);
            _writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "Win7POS bounded log writer"
            };
            _writerThread.Start();
        }

        /// <summary>
        /// Formats and redacts before admission. Returns false on saturation, shutdown,
        /// invalid level, or any producer-side formatting failure; it never propagates.
        /// </summary>
        public bool TryWrite(LogLevel level, string source, string message)
        {
            if (!IsKnownLevel(level))
            {
                return false;
            }

            string line;
            try
            {
                var safeSource = LogSanitizer.SanitizeSource(source, _maxSourceLength);
                var safeMessage = LogSanitizer.Sanitize(message, _maxMessageLength);
                var prefix = safeSource.Length == 0
                    ? "[" + LevelText(level) + "]"
                    : "[" + LevelText(level) + "][" + safeSource + "]";
                line = _clock.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + " " + prefix + " " + safeMessage + Environment.NewLine;
            }
            catch
            {
                lock (_gate)
                {
                    IncrementDropped(level);
                }

                return false;
            }

            var immutableEntry = new ImmutableLogEntry(level, line);
            lock (_gate)
            {
                if (!_accepting || _pendingCount >= AdmissionLimit(level))
                {
                    IncrementDropped(level);
                    return false;
                }

                _queue.Enqueue(immutableEntry);
                _pendingCount++;
                IncrementAccepted(level);
                if (_pendingCount > _highWaterMark)
                {
                    _highWaterMark = _pendingCount;
                }
            }

            _workAvailable.Set();
            return true;
        }

        public LogWriterMetrics GetMetrics()
        {
            lock (_gate)
            {
                return new LogWriterMetrics(
                    _acceptedInfo,
                    _acceptedWarning,
                    _acceptedError,
                    _writtenInfo,
                    _writtenWarning,
                    _writtenError,
                    _droppedInfo,
                    _droppedWarning,
                    _droppedError,
                    _writerFailures,
                    _highWaterMark,
                    _pendingCount,
                    _accepting,
                    _writerThread.IsAlive,
                    _faulted);
            }
        }

        /// <summary>
        /// Stops admission and waits at most timeout. A blocked sink cannot hold up the
        /// caller beyond this bound; the writer thread is background-only.
        /// </summary>
        public bool Shutdown(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            lock (_gate)
            {
                _accepting = false;
                _stopRequested = true;
            }

            _workAvailable.Set();

            if (ReferenceEquals(Thread.CurrentThread, _writerThread))
            {
                return false;
            }

            var timeoutMilliseconds = timeout.TotalMilliseconds >= int.MaxValue
                ? int.MaxValue
                : (int)Math.Ceiling(timeout.TotalMilliseconds);
            return _writerThread.Join(timeoutMilliseconds);
        }

        public void Dispose()
        {
            Shutdown(TimeSpan.FromSeconds(1));
        }

        private void WriterLoop()
        {
            var batch = new List<ImmutableLogEntry>(_batchSize);

            while (true)
            {
                if (batch.Count == 0)
                {
                    lock (_gate)
                    {
                        while (_queue.Count > 0 && batch.Count < _batchSize)
                        {
                            batch.Add(_queue.Dequeue());
                        }

                        if (batch.Count == 0 && _stopRequested)
                        {
                            return;
                        }
                    }

                    if (batch.Count == 0)
                    {
                        _workAvailable.WaitOne(250);
                        continue;
                    }
                }

                try
                {
                    var lines = new List<string>(batch.Count);
                    for (var i = 0; i < batch.Count; i++)
                    {
                        lines.Add(batch[i].Line);
                    }

                    ReadOnlyCollection<string> immutableView = lines.AsReadOnly();
                    _sink.WriteBatch(immutableView);
                }
                catch
                {
                    var abandon = false;
                    lock (_gate)
                    {
                        _writerFailures++;
                        _consecutiveWriterFailures++;
                        abandon = _stopRequested
                            || _consecutiveWriterFailures >= _maxConsecutiveWriterFailures;
                        if (abandon)
                        {
                            _faulted = true;
                            _accepting = false;
                            _stopRequested = true;
                            DropOnWriterFailure(batch);
                        }
                    }

                    if (abandon)
                    {
                        return;
                    }

                    _workAvailable.WaitOne(_retryDelayMilliseconds);
                    continue;
                }

                lock (_gate)
                {
                    _consecutiveWriterFailures = 0;
                    for (var i = 0; i < batch.Count; i++)
                    {
                        IncrementWritten(batch[i].Level);
                    }

                    _pendingCount -= batch.Count;
                }

                batch.Clear();
            }
        }

        private void DropOnWriterFailure(List<ImmutableLogEntry> inFlight)
        {
            for (var i = 0; i < inFlight.Count; i++)
            {
                IncrementDropped(inFlight[i].Level);
            }

            while (_queue.Count > 0)
            {
                IncrementDropped(_queue.Dequeue().Level);
            }

            _pendingCount = 0;
        }

        private int AdmissionLimit(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Info:
                    return _infoAdmissionLimit;
                case LogLevel.Warning:
                    return _warningAdmissionLimit;
                default:
                    return _capacity;
            }
        }

        private static bool IsKnownLevel(LogLevel level)
        {
            return level == LogLevel.Info || level == LogLevel.Warning || level == LogLevel.Error;
        }

        private static string LevelText(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Info:
                    return "INFO";
                case LogLevel.Warning:
                    return "WARN";
                default:
                    return "ERROR";
            }
        }

        private void IncrementAccepted(LogLevel level)
        {
            if (level == LogLevel.Info) _acceptedInfo++;
            else if (level == LogLevel.Warning) _acceptedWarning++;
            else _acceptedError++;
        }

        private void IncrementWritten(LogLevel level)
        {
            if (level == LogLevel.Info) _writtenInfo++;
            else if (level == LogLevel.Warning) _writtenWarning++;
            else _writtenError++;
        }

        private void IncrementDropped(LogLevel level)
        {
            if (level == LogLevel.Info) _droppedInfo++;
            else if (level == LogLevel.Warning) _droppedWarning++;
            else _droppedError++;
        }

        private sealed class ImmutableLogEntry
        {
            public ImmutableLogEntry(LogLevel level, string line)
            {
                Level = level;
                Line = line;
            }

            public LogLevel Level { get; }
            public string Line { get; }
        }
    }
}
