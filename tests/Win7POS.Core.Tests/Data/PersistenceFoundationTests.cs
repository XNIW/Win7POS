using System.Diagnostics;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Backup;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class PersistenceFoundationTests
{
    [TestMethod]
    public async Task OnlineBackup_ProducesValidatedSnapshotWhileWriterContinues()
    {
        using var files = PersistenceFiles.Create();
        var options = PosDbOptions.ForPath(files.Live);
        DbInitializer.EnsureCreated(options);
        var factory = new SqliteConnectionFactory(options);
        using (var seed = factory.Open())
        {
            await seed.ExecuteAsync(
                "INSERT INTO audit_log(ts, action, details) VALUES(1, 'backup-seed', 'seed');" +
                "CREATE TABLE backup_payload(id INTEGER PRIMARY KEY, payload BLOB NOT NULL);" +
                "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 512) " +
                "INSERT INTO backup_payload(id, payload) SELECT x, zeroblob(65536) FROM n;");
        }

        var backupActive = 0;
        var overlappingAttempts = 0;
        var overlappingSuccesses = 0;
        var maximumOverlappingWriteMs = 0L;
        var stop = 0;
        var writes = 0;
        Exception? writerFailure = null;
        var writer = Task.Run(async () =>
        {
            try
            {
                using var connection = factory.Open();
                while (Volatile.Read(ref stop) == 0)
                {
                    var sequence = Interlocked.Increment(ref writes);
                    var overlapsBackup = Volatile.Read(ref backupActive) == 1;
                    if (overlapsBackup)
                        Interlocked.Increment(ref overlappingAttempts);
                    var stopwatch = Stopwatch.StartNew();
                    await connection.ExecuteAsync(
                        "INSERT INTO audit_log(ts, action, details) VALUES(@ts, @action, 'writer');",
                        new { ts = sequence + 1L, action = "backup-writer-" + sequence });
                    stopwatch.Stop();
                    if (overlapsBackup)
                    {
                        Interlocked.Increment(ref overlappingSuccesses);
                        UpdateMaximum(ref maximumOverlappingWriteMs, stopwatch.ElapsedMilliseconds);
                    }
                    await Task.Delay(1);
                }
            }
            catch (Exception ex)
            {
                writerFailure = ex;
            }
        });

        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref writes) >= 20, "Concurrent writer did not start.");
            Interlocked.Exchange(ref backupActive, 1);
            try
            {
                var validation = await new SqliteOnlineBackup(factory).CreateVerifiedAsync(files.Backup);
                Assert.IsTrue(validation.IsValid, "Online backup must pass integrity and foreign-key checks.");
            }
            finally
            {
                Interlocked.Exchange(ref backupActive, 0);
            }
        }
        finally
        {
            Interlocked.Exchange(ref stop, 1);
            await writer;
        }

        Assert.IsNull(writerFailure, "Concurrent writer failed during online backup: " + writerFailure);
        Assert.IsTrue(overlappingAttempts > 0, "Writer never attempted a commit while online backup was active.");
        Assert.IsTrue(overlappingSuccesses > 0, "Writer made no successful commit while online backup was active.");
        Assert.IsTrue(maximumOverlappingWriteMs < 5000, "An overlapping writer exceeded busy_timeout.");
        Assert.IsTrue(File.Exists(files.Backup));

        var backupFactory = new SqliteConnectionFactory(PosDbOptions.ForPath(files.Backup));
        var backupValidation = await new DbMaintenanceRepository(backupFactory).ValidateAsync();
        Assert.IsTrue(backupValidation.IsValid);
        using var backup = backupFactory.Open();
        using var live = factory.Open();
        var backupRows = await backup.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM audit_log;");
        var liveRows = await live.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM audit_log;");
        Assert.IsTrue(backupRows >= 1, "Backup lost the committed seed row.");
        Assert.IsTrue(backupRows <= liveRows, "Backup contains rows that were not committed in the live database.");
    }

    [TestMethod]
    public async Task CandidateForeignKeyViolation_IsRejectedBeforeLiveSwap()
    {
        using var files = PersistenceFiles.Create();
        WriteProbeDatabase(files.Live, "live-before");
        WriteForeignKeyViolationDatabase(files.Candidate);
        var liveHashBefore = Hash(files.Live);

        var candidateFactory = new SqliteConnectionFactory(PosDbOptions.ForPath(files.Candidate));
        var validation = await new DbMaintenanceRepository(candidateFactory).ValidateAsync();

        Assert.IsFalse(validation.IsValid);
        Assert.AreEqual("ok", validation.IntegrityCheck.Trim().ToLowerInvariant());
        Assert.AreNotEqual("ok", validation.ForeignKeyCheck.Trim().ToLowerInvariant());
        CollectionAssert.AreEqual(liveHashBefore, Hash(files.Live), "Rejected candidate changed the live database.");
        Assert.AreEqual("live-before", ReadProbeValue(files.Live));
    }

    [TestMethod]
    public async Task Recovery_PreparedBeforeSwapKeepsOldLiveAndRemovesPartialCandidate()
    {
        using var files = PersistenceFiles.Create();
        WriteProbeDatabase(files.Live, "old-live");
        WriteProbeDatabase(files.Candidate, "partial-candidate");
        WriteMarker(files.Live, "prepared", Path.GetFileName(files.Candidate), Path.GetFileName(files.Rollback));

        await new AtomicRestoreInstaller().RecoverInterruptedInstallAsync(files.Live);
        await new AtomicRestoreInstaller().RecoverInterruptedInstallAsync(files.Live);

        Assert.AreEqual("old-live", ReadProbeValue(files.Live));
        Assert.IsFalse(File.Exists(files.Candidate));
        Assert.IsFalse(File.Exists(files.Live + ".restore-in-progress"));
        await AssertDatabaseValidAsync(files.Live);
    }

    [TestMethod]
    public async Task Recovery_PreparedAfterAtomicSwapRestoresOldLive()
    {
        using var files = PersistenceFiles.Create();
        WriteProbeDatabase(files.Live, "new-live");
        WriteProbeDatabase(files.Rollback, "old-live");
        File.WriteAllBytes(files.Live + "-journal", new byte[] { 1, 2, 3, 4 });
        WriteMarker(files.Live, "prepared", Path.GetFileName(files.Candidate), Path.GetFileName(files.Rollback));

        await new AtomicRestoreInstaller().RecoverInterruptedInstallAsync(files.Live);
        await new AtomicRestoreInstaller().RecoverInterruptedInstallAsync(files.Live);

        Assert.AreEqual("old-live", ReadProbeValue(files.Live));
        Assert.IsFalse(File.Exists(files.Rollback));
        Assert.IsFalse(File.Exists(files.Live + ".restore-in-progress"));
        Assert.IsFalse(File.Exists(files.Live + "-journal"));
        await AssertDatabaseValidAsync(files.Live);
    }

    [TestMethod]
    public async Task Recovery_CommittedAfterSwapKeepsNewLiveAndCleansRollback()
    {
        using var files = PersistenceFiles.Create();
        WriteProbeDatabase(files.Live, "new-live");
        WriteProbeDatabase(files.Rollback, "old-live");
        WriteMarker(files.Live, "committed", Path.GetFileName(files.Candidate), Path.GetFileName(files.Rollback));

        await new AtomicRestoreInstaller().RecoverInterruptedInstallAsync(files.Live);
        await new AtomicRestoreInstaller().RecoverInterruptedInstallAsync(files.Live);

        Assert.AreEqual("new-live", ReadProbeValue(files.Live));
        Assert.IsFalse(File.Exists(files.Rollback));
        Assert.IsFalse(File.Exists(files.Live + ".restore-in-progress"));
        await AssertDatabaseValidAsync(files.Live);
    }

    [TestMethod]
    public async Task Recovery_CommittedCorruptLiveRestoresValidatedRollback()
    {
        using var files = PersistenceFiles.Create();
        File.WriteAllBytes(files.Live, new byte[] { 0, 1, 2, 3, 4, 5 });
        WriteProbeDatabase(files.Rollback, "old-live");
        WriteMarker(files.Live, "committed", Path.GetFileName(files.Candidate), Path.GetFileName(files.Rollback));

        await new AtomicRestoreInstaller().RecoverInterruptedInstallAsync(files.Live);
        await new AtomicRestoreInstaller().RecoverInterruptedInstallAsync(files.Live);

        Assert.AreEqual("old-live", ReadProbeValue(files.Live));
        Assert.IsFalse(File.Exists(files.Rollback));
        Assert.IsFalse(File.Exists(files.Live + ".restore-in-progress"));
        await AssertDatabaseValidAsync(files.Live);
    }

    private static async Task AssertDatabaseValidAsync(string path)
    {
        var factory = new SqliteConnectionFactory(PosDbOptions.ForPath(path));
        var validation = await new DbMaintenanceRepository(factory).ValidateAsync();
        Assert.IsTrue(
            validation.IsValid,
            "Recovered database is invalid: integrity=" + validation.IntegrityCheck +
            " foreignKeys=" + validation.ForeignKeyCheck);
    }

    private static byte[] Hash(string path)
    {
        SqliteConnectionFactory.ClearAllPools();
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return sha.ComputeHash(stream);
    }

    private static string ReadProbeValue(string path)
    {
        using var connection = new SqliteConnection("Data Source=" + path);
        connection.Open();
        return connection.ExecuteScalar<string>("SELECT value FROM restore_probe WHERE id=1;") ?? string.Empty;
    }

    private static void WriteForeignKeyViolationDatabase(string path)
    {
        using var connection = new SqliteConnection("Data Source=" + path);
        connection.Open();
        connection.Execute(@"
PRAGMA foreign_keys=OFF;
CREATE TABLE parent(id INTEGER PRIMARY KEY);
CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL REFERENCES parent(id));
INSERT INTO child(id, parent_id) VALUES(1, 999);");
    }

    private static void WriteMarker(
        string livePath,
        string phase,
        string candidateFileName,
        string rollbackFileName)
    {
        File.WriteAllLines(
            livePath + ".restore-in-progress",
            new[]
            {
                "version=1",
                "phase=" + phase,
                "candidate=" + candidateFileName,
                "rollback=" + rollbackFileName
            });
    }

    private static void WriteProbeDatabase(string path, string value)
    {
        using var connection = new SqliteConnection("Data Source=" + path);
        connection.Open();
        connection.Execute(@"
CREATE TABLE restore_probe(id INTEGER PRIMARY KEY, value TEXT NOT NULL);
INSERT INTO restore_probe(id, value) VALUES(1, @value);", new { value });
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.IsTrue(predicate(), message);
    }

    private static void UpdateMaximum(ref long target, long value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }

    private sealed class PersistenceFiles : IDisposable
    {
        private PersistenceFiles(string root)
        {
            Root = root;
            Backup = Path.Combine(root, "backup.db");
            Candidate = Path.Combine(root, "live.db.restore-crash.new");
            Live = Path.Combine(root, "live.db");
            Rollback = Path.Combine(root, "live.db.restore-crash.old");
        }

        public string Backup { get; }
        public string Candidate { get; }
        public string Live { get; }
        public string Rollback { get; }
        private string Root { get; }

        public static PersistenceFiles Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "Win7POS.Persistence", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new PersistenceFiles(root);
        }

        public void Dispose()
        {
            SqliteConnectionFactory.ClearAllPools();
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
