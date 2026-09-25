using System;
using System.Threading;

namespace Win7POS.Data
{
    /// <summary>Opt-in per-operation diagnostics. Contains counts only, never SQL, parameters or credentials.</summary>
    public sealed class SqliteWorkMetrics : IDisposable
    {
        private static readonly AsyncLocal<SqliteWorkMetrics> Current = new AsyncLocal<SqliteWorkMetrics>();
        private readonly SqliteWorkMetrics _previous;
        private readonly int _callingThread;
        private int _connections;
        private int _productCommands;
        private int _commandsOnCallingThread;
        private SqliteWorkMetrics()
        {
            _previous = Current.Value;
            _callingThread = Thread.CurrentThread.ManagedThreadId;
            Current.Value = this;
        }
        public static SqliteWorkMetrics Begin() => new SqliteWorkMetrics();
        public int Connections => Volatile.Read(ref _connections);
        public int ProductCommands => Volatile.Read(ref _productCommands);
        public int ProductCommandsOnCallingThread => Volatile.Read(ref _commandsOnCallingThread);
        internal static void ConnectionOpened()
        {
            var metrics = Current.Value;
            if (metrics != null) Interlocked.Increment(ref metrics._connections);
        }
        internal static void ProductCommand()
        {
            var metrics = Current.Value;
            if (metrics == null) return;
            Interlocked.Increment(ref metrics._productCommands);
            if (Thread.CurrentThread.ManagedThreadId == metrics._callingThread)
                Interlocked.Increment(ref metrics._commandsOnCallingThread);
        }
        public void Dispose() => Current.Value = _previous;
    }
}
