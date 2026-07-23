using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class SqliteConnectionPolicyTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void NewAndLegacyDatabases_PreservePolicyRollbackAndIntegrityAfterReconnect()
    {
        SQLitePCL.Batteries_V2.Init();
        foreach (var legacy in new[] { false, true })
        {
            var root = CreateTemporaryRoot();
            var dbPath = Path.Combine(root, "pos.db");
            try
            {
                if (legacy)
                    SeedLegacyProbe(dbPath);

                var options = PosDbOptions.ForPath(dbPath);
                DbInitializer.EnsureCreated(options);
                DbInitializer.EnsureCreated(options);
                var factory = new SqliteConnectionFactory(options);

                using (var first = factory.Open())
                using (var second = factory.Open())
                {
                    AssertConnectionPolicy(first);
                    AssertConnectionPolicy(second);

                    using (var transaction = first.BeginTransaction())
                    {
                        try
                        {
                            Execute(
                                first,
                                "INSERT INTO audit_log(ts, action, details) VALUES(1, 'rollback-probe', 'probe');",
                                transaction);
                            throw new InvalidOperationException("Injected rollback probe.");
                        }
                        catch (InvalidOperationException)
                        {
                            transaction.Rollback();
                        }
                    }

                    Assert.AreEqual(
                        0L,
                        ScalarLong(first, "SELECT COUNT(1) FROM audit_log WHERE action='rollback-probe';"),
                        "The injected failure must not leave a partial transaction.");
                    WriteRuntimeEvidence(first, legacy ? "legacy" : "new", dbPath);
                }

                SqliteConnectionFactory.ClearAllPools();
                using (var reopened = factory.Open())
                {
                    AssertConnectionPolicy(reopened);
                    Assert.AreEqual("ok", ScalarString(reopened, "PRAGMA integrity_check;"));
                    AssertNoForeignKeyViolations(reopened);
                    if (legacy)
                        Assert.AreEqual(1L, ScalarLong(reopened, "SELECT COUNT(1) FROM legacy_probe;"));
                }

                TestContext.WriteLine(
                    $"SQLITE_OBSERVED database={(legacy ? "legacy" : "new")} " +
                    "journal_mode=delete synchronous=2 foreign_keys=1 busy_timeout=5000 " +
                    "temp_store=1 cache_size=-2048 integrity=ok foreign_key_check=ok");
            }
            finally
            {
                SqliteConnectionFactory.ClearAllPools();
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void TwoConnections_EnforceBusyTimeoutAndRecoverAfterRollback()
    {
        SQLitePCL.Batteries_V2.Init();
        var root = CreateTemporaryRoot();
        var dbPath = Path.Combine(root, "pos.db");
        try
        {
            var options = PosDbOptions.ForPath(dbPath);
            DbInitializer.EnsureCreated(options);
            var factory = new SqliteConnectionFactory(options);
            using var first = factory.Open();
            using var second = factory.Open();
            AssertConnectionPolicy(first);
            AssertConnectionPolicy(second);

            using var transaction = first.BeginTransaction();
            Execute(
                first,
                "INSERT INTO audit_log(ts, action, details) VALUES(2, 'busy-owner', 'probe');",
                transaction);

            SqliteException? busyException = null;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                Execute(second, "INSERT INTO audit_log(ts, action, details) VALUES(3, 'busy-waiter', 'probe');");
            }
            catch (SqliteException ex)
            {
                busyException = ex;
            }
            stopwatch.Stop();

            Assert.IsNotNull(busyException, "The competing writer must time out while the first transaction owns the lock.");
            Assert.AreEqual(5, busyException.SqliteErrorCode, "Expected SQLITE_BUSY from the competing writer.");
            Assert.IsTrue(
                stopwatch.ElapsedMilliseconds >= 4000 && stopwatch.ElapsedMilliseconds <= 8000,
                $"busy_timeout=5000 waited an unexpected {stopwatch.ElapsedMilliseconds} ms.");

            transaction.Rollback();
            Execute(second, "INSERT INTO audit_log(ts, action, details) VALUES(4, 'busy-recovered', 'probe');");
            Assert.AreEqual(1L, ScalarLong(second, "SELECT COUNT(1) FROM audit_log WHERE action='busy-recovered';"));
            TestContext.WriteLine($"SQLITE_BUSY_TIMEOUT elapsed_ms={stopwatch.ElapsedMilliseconds} recovered=true");
        }
        finally
        {
            SqliteConnectionFactory.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Factory_ReassertsDeletePolicyWhenAReopenedDatabaseWasChangedToWal()
    {
        SQLitePCL.Batteries_V2.Init();
        var root = CreateTemporaryRoot();
        var dbPath = Path.Combine(root, "pos.db");
        try
        {
            var options = PosDbOptions.ForPath(dbPath);
            DbInitializer.EnsureCreated(options);
            var factory = new SqliteConnectionFactory(options);

            using (var external = new SqliteConnection($"Data Source={dbPath}"))
            {
                external.Open();
                Assert.AreEqual("wal", ScalarString(external, "PRAGMA journal_mode=WAL;").ToLowerInvariant());
            }

            SqliteConnectionFactory.ClearAllPools();
            using (var reopened = factory.Open())
            {
                AssertConnectionPolicy(reopened);
                Assert.AreEqual("ok", ScalarString(reopened, "PRAGMA integrity_check;"));
                AssertNoForeignKeyViolations(reopened);
            }
        }
        finally
        {
            SqliteConnectionFactory.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RuntimePolicyVerification_FailsClosedForAnUnverifiedConnection()
    {
        SQLitePCL.Batteries_V2.Init();
        var root = CreateTemporaryRoot();
        var dbPath = Path.Combine(root, "pos.db");
        try
        {
            var options = PosDbOptions.ForPath(dbPath);
            DbInitializer.EnsureCreated(options);

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=DELETE;");
            Execute(connection, "PRAGMA synchronous=FULL;");
            Execute(connection, "PRAGMA foreign_keys=ON;");
            Execute(connection, "PRAGMA busy_timeout=5000;");
            Execute(connection, "PRAGMA temp_store=FILE;");
            Execute(connection, "PRAGMA cache_size=-1024;");

            var error = Assert.ThrowsExactly<InvalidOperationException>(
                () => SqliteConnectionFactory.VerifyRuntimePolicy(connection));
            StringAssert.Contains(error.Message, "cache_size");
            StringAssert.Contains(error.Message, "-2048");
        }
        finally
        {
            SqliteConnectionFactory.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task OpenAsync_AppliesAndVerifiesTheSameRuntimePolicy()
    {
        SQLitePCL.Batteries_V2.Init();
        var root = CreateTemporaryRoot();
        var dbPath = Path.Combine(root, "pos.db");
        try
        {
            var options = PosDbOptions.ForPath(dbPath);
            DbInitializer.EnsureCreated(options);
            var factory = new SqliteConnectionFactory(options);

            using var connection = await factory.OpenAsync();
            AssertConnectionPolicy(connection);
            Assert.AreEqual("ok", ScalarString(connection, "PRAGMA integrity_check;"));
            AssertNoForeignKeyViolations(connection);
        }
        finally
        {
            SqliteConnectionFactory.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExclusiveMaintenance_DrainsActiveConnectionsBlocksNewOpenAndAllowsOwnerReentry()
    {
        SQLitePCL.Batteries_V2.Init();
        var root = CreateTemporaryRoot();
        var dbPath = Path.Combine(root, "pos.db");
        try
        {
            var options = PosDbOptions.ForPath(dbPath);
            DbInitializer.EnsureCreated(options);
            var factory = new SqliteConnectionFactory(options);
            var active = factory.Open();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var maintenance = SqliteConnectionFactory.RunExclusiveMaintenanceAsync(async () =>
            {
                await SqliteConnectionFactory.RunExclusiveMaintenanceAsync(async () =>
                {
                    using var ownerConnection = factory.Open();
                    AssertConnectionPolicy(ownerConnection);
                    entered.TrySetResult(true);
                    await release.Task;
                });
            });

            await Task.Delay(100);
            Assert.IsFalse(entered.Task.IsCompleted, "Maintenance must wait for the active connection to drain.");

            active.Dispose();
            await AwaitWithTimeout(entered.Task, "Maintenance did not enter after the active connection closed.");

            var blockedOpen = Task.Run(() => factory.Open());
            await Task.Delay(100);
            Assert.IsFalse(
                blockedOpen.IsCompleted,
                "A non-owner connection opened while the maintenance owner held an active connection.");

            release.TrySetResult(true);
            await AwaitWithTimeout(maintenance, "Exclusive maintenance did not complete.");
            using var reopened = await AwaitWithTimeout(
                blockedOpen,
                "A blocked connection did not resume after exclusive maintenance.");
            AssertConnectionPolicy(reopened);
        }
        finally
        {
            SqliteConnectionFactory.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExclusiveMaintenance_AllowsConnectionNeededToCompleteDrainWithoutDeadlock()
    {
        SQLitePCL.Batteries_V2.Init();
        var root = CreateTemporaryRoot();
        var dbPath = Path.Combine(root, "pos.db");
        try
        {
            var options = PosDbOptions.ForPath(dbPath);
            DbInitializer.EnsureCreated(options);
            var factory = new SqliteConnectionFactory(options);
            var active = factory.Open();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var maintenance = SqliteConnectionFactory.RunExclusiveMaintenanceAsync(async () =>
            {
                entered.TrySetResult(true);
                await release.Task;
            });

            Assert.IsFalse(entered.Task.IsCompleted, "Maintenance must wait for the original connection.");

            var neededOpen = Task.Run(() => factory.Open());
            using (var needed = await AwaitWithTimeout(
                neededOpen,
                "A connection needed to finish active work deadlocked behind the pending drain."))
            {
                AssertConnectionPolicy(needed);
            }

            active.Dispose();
            await AwaitWithTimeout(entered.Task, "Maintenance did not enter after both connections drained.");
            release.TrySetResult(true);
            await AwaitWithTimeout(maintenance, "Exclusive maintenance did not complete.");
        }
        finally
        {
            SqliteConnectionFactory.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExclusiveMaintenance_DrainTimeoutAbortsBeforeActionAndReleasesFence()
    {
        SQLitePCL.Batteries_V2.Init();
        var root = CreateTemporaryRoot();
        var dbPath = Path.Combine(root, "pos.db");
        SqliteConnection? active = null;
        try
        {
            var options = PosDbOptions.ForPath(dbPath);
            DbInitializer.EnsureCreated(options);
            var factory = new SqliteConnectionFactory(options);
            active = factory.Open();
            var actionInvoked = false;

            var error = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
                SqliteConnectionFactory.RunExclusiveMaintenanceAsync(
                    () =>
                    {
                        actionInvoked = true;
                        return Task.CompletedTask;
                    },
                    TimeSpan.FromMilliseconds(100)));

            StringAssert.Contains(error.Message, "active SQLite connections to drain");
            Assert.IsFalse(actionInvoked, "Timed-out maintenance must abort before its action can swap the database.");
            using var reopened = factory.Open();
            AssertConnectionPolicy(reopened);
        }
        finally
        {
            active?.Dispose();
            SqliteConnectionFactory.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExclusiveMaintenance_LeakedOwnerConnectionFailsButReleasesGlobalFence()
    {
        SQLitePCL.Batteries_V2.Init();
        var root = CreateTemporaryRoot();
        var dbPath = Path.Combine(root, "pos.db");
        SqliteConnection? leaked = null;
        try
        {
            var options = PosDbOptions.ForPath(dbPath);
            DbInitializer.EnsureCreated(options);
            var factory = new SqliteConnectionFactory(options);

            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                SqliteConnectionFactory.RunExclusiveMaintenanceAsync(() =>
                {
                    leaked = factory.Open();
                    return Task.CompletedTask;
                }));
            StringAssert.Contains(error.Message, "global fence was released");

            var reopenedTask = Task.Run(() => factory.Open());
            using var reopened = await AwaitWithTimeout(
                reopenedTask,
                "The global fence remained blocked after a leaked owner connection.");
            AssertConnectionPolicy(reopened);
        }
        finally
        {
            leaked?.Dispose();
            SqliteConnectionFactory.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertConnectionPolicy(SqliteConnection connection)
    {
        Assert.AreEqual("delete", ScalarString(connection, "PRAGMA journal_mode;").ToLowerInvariant());
        Assert.AreEqual(2L, ScalarLong(connection, "PRAGMA synchronous;"));
        Assert.AreEqual(1L, ScalarLong(connection, "PRAGMA foreign_keys;"));
        Assert.AreEqual(5000L, ScalarLong(connection, "PRAGMA busy_timeout;"));
        Assert.AreEqual(1L, ScalarLong(connection, "PRAGMA temp_store;"));
        Assert.AreEqual(-2048L, ScalarLong(connection, "PRAGMA cache_size;"));
    }

    private static void AssertNoForeignKeyViolations(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        Assert.IsFalse(reader.Read(), "foreign_key_check reported an integrity violation.");
    }

    private void WriteRuntimeEvidence(SqliteConnection connection, string databaseKind, string dbPath)
    {
        var pageSize = ScalarLong(connection, "PRAGMA page_size;");
        var pageCount = ScalarLong(connection, "PRAGMA page_count;");
        var cacheSize = ScalarLong(connection, "PRAGMA cache_size;");
        var databaseBytes = new FileInfo(dbPath).Length;
        TestContext.WriteLine(
            $"SQLITE_RUNTIME_POLICY database={databaseKind} journal_mode=delete synchronous=2 " +
            $"temp_store=1 cache_size_kib={cacheSize} page_size={pageSize} " +
            $"page_count={pageCount} database_bytes={databaseBytes}");
    }

    private static void SeedLegacyProbe(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        Execute(connection, "CREATE TABLE legacy_probe(id INTEGER PRIMARY KEY, value TEXT NOT NULL);");
        Execute(connection, "INSERT INTO legacy_probe(id, value) VALUES(1, 'preserve-me');");
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "Win7POS.SqlitePolicy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task AwaitWithTimeout(Task task, string message)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.AreSame(task, completed, message);
        await task;
    }

    private static async Task<T> AwaitWithTimeout<T>(Task<T> task, string message)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.AreSame(task, completed, message);
        return await task;
    }

    private static void Execute(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }
}
