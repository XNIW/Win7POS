using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Win7POS.Data
{
    public sealed class SqliteConnectionFactory
    {
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
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA busy_timeout=5000;";
                    cmd.ExecuteNonQuery();
                }
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
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA busy_timeout=5000;";
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
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
