using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Win7POS.Data
{
    public sealed class SqliteConnectionFactory
    {
        internal const string ExpectedJournalMode = "delete";
        internal const long ExpectedSynchronous = 2;
        internal const long ExpectedForeignKeys = 1;
        internal const long ExpectedBusyTimeoutMilliseconds = 5000;
        internal const long ExpectedTempStore = 1;
        internal const long ExpectedCacheSizeKiB = -2048;

        private static readonly object MaintenanceSync = new object();
        private static readonly SemaphoreSlim MaintenanceGate = new SemaphoreSlim(1, 1);
        private static readonly AsyncLocal<MaintenanceContext> CurrentMaintenance = new AsyncLocal<MaintenanceContext>();
        private static readonly TimeSpan DefaultMaintenanceDrainTimeout = TimeSpan.FromSeconds(30);
        private static TaskCompletionSource<bool> _maintenanceReleased = CompletedSignal();
        private static TaskCompletionSource<bool> _activeConnectionsDrained = CompletedSignal();
        private static string _maintenanceOwnerToken = string.Empty;
        private static bool _maintenanceEntered;
        private static bool _maintenancePending;
        private static int _activeConnections;

        private readonly PosDbOptions _opt;

        public SqliteConnectionFactory(PosDbOptions opt) => _opt = opt;

        public string DbPath => _opt.DbPath;

        private static string BuildConnectionString(string dbPath)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                ForeignKeys = true
            }.ToString();
            return cs + ";Default Timeout=5";
        }

        public SqliteConnection Open()
        {
            var connectionLease = RegisterConnection();
            var conn = new SqliteConnection(BuildConnectionString(_opt.DbPath));
            AttachConnectionLease(conn, connectionLease);
            try
            {
                conn.Open();
                ApplyAndVerifyRuntimePolicy(conn);
                return conn;
            }
            catch
            {
                connectionLease.Release();
                conn.Dispose();
                throw;
            }
        }

        public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var connectionLease = RegisterConnection();
            var conn = new SqliteConnection(BuildConnectionString(_opt.DbPath));
            AttachConnectionLease(conn, connectionLease);
            try
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await ApplyAndVerifyRuntimePolicyAsync(conn, ct).ConfigureAwait(false);
                return conn;
            }
            catch
            {
                connectionLease.Release();
                conn.Dispose();
                throw;
            }
        }

        public static Task RunExclusiveMaintenanceAsync(Func<Task> maintenanceAction)
        {
            return RunExclusiveMaintenanceAsync(maintenanceAction, DefaultMaintenanceDrainTimeout);
        }

        public static async Task RunExclusiveMaintenanceAsync(
            Func<Task> maintenanceAction,
            TimeSpan drainTimeout)
        {
            if (maintenanceAction == null)
                throw new ArgumentNullException(nameof(maintenanceAction));
            if (drainTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(drainTimeout));

            var current = CurrentMaintenance.Value;
            var isReentrant = false;
            lock (MaintenanceSync)
            {
                if (current != null &&
                    _maintenancePending &&
                    string.Equals(current.Token, _maintenanceOwnerToken, StringComparison.Ordinal))
                {
                    current.Depth++;
                    isReentrant = true;
                }
            }

            if (isReentrant)
            {
                try
                {
                    await maintenanceAction().ConfigureAwait(false);
                }
                finally
                {
                    ReleaseMaintenance(current, ownsGate: false);
                }
                return;
            }

            await MaintenanceGate.WaitAsync().ConfigureAwait(false);
            var context = new MaintenanceContext
            {
                Previous = CurrentMaintenance.Value,
                Token = Guid.NewGuid().ToString("N"),
                Depth = 1
            };

            Task drained;
            lock (MaintenanceSync)
            {
                _maintenancePending = true;
                _maintenanceEntered = false;
                _maintenanceOwnerToken = context.Token;
                _maintenanceReleased = PendingSignal();
                _activeConnectionsDrained = _activeConnections == 0
                    ? CompletedSignal()
                    : PendingSignal();
                drained = _activeConnectionsDrained.Task;
            }

            try
            {
                var drainCompleted = await Task.WhenAny(drained, Task.Delay(drainTimeout)).ConfigureAwait(false);
                if (!ReferenceEquals(drainCompleted, drained) && !drained.IsCompleted)
                {
                    throw new TimeoutException(
                        "Timed out waiting for active SQLite connections to drain before exclusive maintenance.");
                }

                await drained.ConfigureAwait(false);
                lock (MaintenanceSync)
                {
                    context.Entered = true;
                    _maintenanceEntered = true;
                }
                CurrentMaintenance.Value = context;
                await maintenanceAction().ConfigureAwait(false);
            }
            finally
            {
                ReleaseMaintenance(context, ownsGate: true);
            }
        }

        public static void ClearAllPools()
        {
            SqliteConnection.ClearAllPools();
        }

        internal static void VerifyRuntimePolicy(SqliteConnection connection)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (connection.State != ConnectionState.Open)
                throw new InvalidOperationException("SQLite runtime policy cannot be verified on a closed connection.");

            VerifyPolicyValue("journal_mode", ExpectedJournalMode, ReadPragmaString(connection, "journal_mode"));
            VerifyPolicyValue("synchronous", ExpectedSynchronous, ReadPragmaLong(connection, "synchronous"));
            VerifyPolicyValue("foreign_keys", ExpectedForeignKeys, ReadPragmaLong(connection, "foreign_keys"));
            VerifyPolicyValue(
                "busy_timeout",
                ExpectedBusyTimeoutMilliseconds,
                ReadPragmaLong(connection, "busy_timeout"));
            VerifyPolicyValue("temp_store", ExpectedTempStore, ReadPragmaLong(connection, "temp_store"));
            VerifyPolicyValue("cache_size", ExpectedCacheSizeKiB, ReadPragmaLong(connection, "cache_size"));
        }

        private static void ApplyAndVerifyRuntimePolicy(SqliteConnection connection)
        {
            // These are connection-local settings and must be established again when a
            // pooled physical connection is handed out.
            ExecutePragma(connection, "foreign_keys=ON");
            ExecutePragma(connection, "busy_timeout=" + ExpectedBusyTimeoutMilliseconds);
            ExecutePragma(connection, "temp_store=FILE");
            ExecutePragma(connection, "cache_size=" + ExpectedCacheSizeKiB);
            EnsureDeleteJournalMode(connection);
            ExecutePragma(connection, "synchronous=FULL");
            VerifyRuntimePolicy(connection);
        }

        private static async Task ApplyAndVerifyRuntimePolicyAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
        {
            await ExecutePragmaAsync(connection, "foreign_keys=ON", cancellationToken).ConfigureAwait(false);
            await ExecutePragmaAsync(
                    connection,
                    "busy_timeout=" + ExpectedBusyTimeoutMilliseconds,
                    cancellationToken)
                .ConfigureAwait(false);
            await ExecutePragmaAsync(connection, "temp_store=FILE", cancellationToken).ConfigureAwait(false);
            await ExecutePragmaAsync(
                    connection,
                    "cache_size=" + ExpectedCacheSizeKiB,
                    cancellationToken)
                .ConfigureAwait(false);

            var journalMode = await ReadPragmaStringAsync(connection, "journal_mode", cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(journalMode, ExpectedJournalMode, StringComparison.OrdinalIgnoreCase))
            {
                await ExecutePragmaAsync(connection, "journal_mode=DELETE", cancellationToken).ConfigureAwait(false);
            }

            await ExecutePragmaAsync(connection, "synchronous=FULL", cancellationToken).ConfigureAwait(false);
            await VerifyRuntimePolicyAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        private static void EnsureDeleteJournalMode(SqliteConnection connection)
        {
            var journalMode = ReadPragmaString(connection, "journal_mode");
            if (!string.Equals(journalMode, ExpectedJournalMode, StringComparison.OrdinalIgnoreCase))
                ExecutePragma(connection, "journal_mode=DELETE");
        }

        private static async Task VerifyRuntimePolicyAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
        {
            VerifyPolicyValue(
                "journal_mode",
                ExpectedJournalMode,
                await ReadPragmaStringAsync(connection, "journal_mode", cancellationToken).ConfigureAwait(false));
            VerifyPolicyValue(
                "synchronous",
                ExpectedSynchronous,
                await ReadPragmaLongAsync(connection, "synchronous", cancellationToken).ConfigureAwait(false));
            VerifyPolicyValue(
                "foreign_keys",
                ExpectedForeignKeys,
                await ReadPragmaLongAsync(connection, "foreign_keys", cancellationToken).ConfigureAwait(false));
            VerifyPolicyValue(
                "busy_timeout",
                ExpectedBusyTimeoutMilliseconds,
                await ReadPragmaLongAsync(connection, "busy_timeout", cancellationToken).ConfigureAwait(false));
            VerifyPolicyValue(
                "temp_store",
                ExpectedTempStore,
                await ReadPragmaLongAsync(connection, "temp_store", cancellationToken).ConfigureAwait(false));
            VerifyPolicyValue(
                "cache_size",
                ExpectedCacheSizeKiB,
                await ReadPragmaLongAsync(connection, "cache_size", cancellationToken).ConfigureAwait(false));
        }

        private static void ExecutePragma(SqliteConnection connection, string pragma)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA " + pragma + ";";
                command.ExecuteNonQuery();
            }
        }

        private static async Task ExecutePragmaAsync(
            SqliteConnection connection,
            string pragma,
            CancellationToken cancellationToken)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA " + pragma + ";";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private static string ReadPragmaString(SqliteConnection connection, string pragma)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA " + pragma + ";";
                return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
            }
        }

        private static long ReadPragmaLong(SqliteConnection connection, string pragma)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA " + pragma + ";";
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static async Task<string> ReadPragmaStringAsync(
            SqliteConnection connection,
            string pragma,
            CancellationToken cancellationToken)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA " + pragma + ";";
                var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return Convert.ToString(value) ?? string.Empty;
            }
        }

        private static async Task<long> ReadPragmaLongAsync(
            SqliteConnection connection,
            string pragma,
            CancellationToken cancellationToken)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA " + pragma + ";";
                var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return Convert.ToInt64(value);
            }
        }

        private static void VerifyPolicyValue(string pragma, string expected, string actual)
        {
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "SQLite runtime policy verification failed for " + pragma +
                    ". Expected '" + expected + "' but observed '" + actual + "'.");
            }
        }

        private static void VerifyPolicyValue(string pragma, long expected, long actual)
        {
            if (expected != actual)
            {
                throw new InvalidOperationException(
                    "SQLite runtime policy verification failed for " + pragma +
                    ". Expected " + expected + " but observed " + actual + ".");
            }
        }

        private static ActiveConnectionLease RegisterConnection()
        {
            while (true)
            {
                Task maintenanceReleased;
                lock (MaintenanceSync)
                {
                    var current = CurrentMaintenance.Value;
                    var isOwner = current != null &&
                        string.Equals(current.Token, _maintenanceOwnerToken, StringComparison.Ordinal);
                    // A flow that already contributes to the drain may need another
                    // connection before it can release its current one. Admit opens
                    // until the active count first reaches zero; that zero boundary
                    // remains reserved for pending maintenance, so later non-owner
                    // opens wait until the fence is released.
                    if (!_maintenancePending ||
                        isOwner ||
                        (!_maintenanceEntered && _activeConnections > 0))
                    {
                        _activeConnections++;
                        return new ActiveConnectionLease();
                    }

                    maintenanceReleased = _maintenanceReleased.Task;
                }

                maintenanceReleased.GetAwaiter().GetResult();
            }
        }

        private static void AttachConnectionLease(
            SqliteConnection connection,
            ActiveConnectionLease connectionLease)
        {
            connection.StateChange += (sender, args) =>
            {
                if (args.CurrentState == ConnectionState.Closed)
                    connectionLease.Release();
            };
            connection.Disposed += (sender, args) => connectionLease.Release();
        }

        private static void ReleaseConnection()
        {
            lock (MaintenanceSync)
            {
                if (_activeConnections <= 0)
                    return;

                _activeConnections--;
                if (_activeConnections == 0)
                    _activeConnectionsDrained.TrySetResult(true);
            }
        }

        private static void ReleaseMaintenance(MaintenanceContext context, bool ownsGate)
        {
            var releaseGate = false;
            var leakedConnections = false;
            lock (MaintenanceSync)
            {
                if (context.Depth <= 0)
                    return;

                context.Depth--;
                if (!ownsGate || context.Depth > 0)
                    return;

                leakedConnections = context.Entered && _activeConnections != 0;
                CurrentMaintenance.Value = context.Previous;
                _maintenanceOwnerToken = string.Empty;
                _maintenanceEntered = false;
                _maintenancePending = false;
                _maintenanceReleased.TrySetResult(true);
                Monitor.PulseAll(MaintenanceSync);
                releaseGate = true;
            }

            if (releaseGate)
                MaintenanceGate.Release();

            if (leakedConnections)
            {
                throw new InvalidOperationException(
                    "Exclusive SQLite maintenance ended while tracked owner connections remained open; " +
                    "the global fence was released to keep subsequent database access recoverable.");
            }
        }

        private static TaskCompletionSource<bool> CompletedSignal()
        {
            var signal = PendingSignal();
            signal.SetResult(true);
            return signal;
        }

        private static TaskCompletionSource<bool> PendingSignal()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class ActiveConnectionLease
        {
            private int _released;

            public void Release()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                    ReleaseConnection();
            }
        }

        private sealed class MaintenanceContext
        {
            public int Depth { get; set; }
            public bool Entered { get; set; }
            public MaintenanceContext Previous { get; set; }
            public string Token { get; set; } = string.Empty;
        }

    }
}
