using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Backup;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

public sealed partial class PersistenceFoundationTests
{
    private const string ProtocolShopId = "restore-shop";
    private const string ProtocolShopCode = "RESTORE-SHOP";

    [TestMethod]
    [DataRow(nameof(RestoreFailurePoint.DuringSourceSnapshotIdentityGuard))]
    [DataRow(nameof(RestoreFailurePoint.AfterSourceSnapshot))]
    [DataRow(nameof(RestoreFailurePoint.AfterCandidateMigration))]
    [DataRow(nameof(RestoreFailurePoint.AfterCandidateIntegrityForeignKey))]
    [DataRow(nameof(RestoreFailurePoint.AfterPreliminaryShopValidation))]
    [DataRow(nameof(RestoreFailurePoint.WhileFenceWait))]
    [DataRow(nameof(RestoreFailurePoint.AfterFencedLiveRevalidation))]
    [DataRow(nameof(RestoreFailurePoint.AfterFencedCandidateRevalidation))]
    [DataRow(nameof(RestoreFailurePoint.DuringVerifiedPreBackup))]
    [DataRow(nameof(RestoreFailurePoint.AfterPreBackupBeforePrepared))]
    [DataRow(nameof(RestoreFailurePoint.AfterPreparedBeforeSwap))]
    [DataRow(nameof(RestoreFailurePoint.ImmediatelyAfterReplace))]
    [DataRow(nameof(RestoreFailurePoint.DuringPostMigration))]
    [DataRow(nameof(RestoreFailurePoint.DuringPostIntegrity))]
    [DataRow(nameof(RestoreFailurePoint.DuringPostForeignKey))]
    [DataRow(nameof(RestoreFailurePoint.BeforeCommitted))]
    [DataRow(nameof(RestoreFailurePoint.AfterCommittedBeforeCleanup))]
    [DataRow(nameof(RestoreFailurePoint.PartialCleanupFailure))]
    public async Task RestoreFailureInjection_AlwaysLeavesOldOrNewValidAndRetryable(
        string pointName)
    {
        var point = Enum.Parse<RestoreFailurePoint>(pointName);
        using var files = await RestoreProtocolFiles.CreateAsync();
        var hooks = new BackupRestoreTestHooks
        {
            RestoreFault = observed =>
            {
                if (observed == point)
                    throw new IOException("restore_fault_" + pointName);
            }
        };

        var failure = await CaptureExceptionAsync(() =>
            RestoreWithHooksAsync(files, hooks, "fault"));
        var completesDespiteInjectedCleanupFailure =
            point == RestoreFailurePoint.PartialCleanupFailure;
        if (completesDespiteInjectedCleanupFailure)
            Assert.IsNull(failure, "Partial cleanup must be retried before returning.");
        else
            Assert.IsNotNull(failure, "The selected fault point was not reached.");

        var expectedNew =
            point == RestoreFailurePoint.AfterCommittedBeforeCleanup ||
            point == RestoreFailurePoint.PartialCleanupFailure;
        Assert.AreEqual(expectedNew ? "new-live" : "old-live", ReadProtocolValue(files.Live));
        await AssertDatabaseValidAndDeleteFullAsync(files.Live);
        AssertRestoreResidue(files.Root, expectedCount: 0);
        await AssertExistingPreBackupsValidAsync(files.Root);

        var retry = await RestoreWithHooksAsync(files, null, "retry");
        Assert.IsTrue(retry.LiveValidation.IsValid);
        Assert.AreEqual("new-live", ReadProtocolValue(files.Live));
        await AssertDatabaseValidAndDeleteFullAsync(files.Live);
        AssertRestoreResidue(files.Root, expectedCount: 0);
    }

    [TestMethod]
    [DataRow("before", false)]
    [DataRow(nameof(RestoreFailurePoint.AfterSourceSnapshot), false)]
    [DataRow(nameof(RestoreFailurePoint.AfterPreliminaryShopValidation), false)]
    [DataRow(nameof(RestoreFailurePoint.WhileFenceWait), false)]
    [DataRow(nameof(RestoreFailurePoint.AfterPreBackupBeforePrepared), false)]
    [DataRow(nameof(RestoreFailurePoint.AfterPreparedBeforeSwap), true)]
    [DataRow(nameof(RestoreFailurePoint.ImmediatelyAfterReplace), true)]
    [DataRow(nameof(RestoreFailurePoint.DuringPostIntegrity), true)]
    public async Task RestoreCancellation_StopsBeforePreparedOrFinishesAtomicProtocol(
        string pointName,
        bool expectsNewLive)
    {
        using var files = await RestoreProtocolFiles.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        BackupRestoreTestHooks? hooks = null;
        if (!string.Equals(pointName, "before", StringComparison.Ordinal))
        {
            var point = Enum.Parse<RestoreFailurePoint>(pointName);
            hooks = new BackupRestoreTestHooks
            {
                RestoreFault = observed =>
                {
                    if (observed == point)
                        cancellation.Cancel();
                }
            };
        }
        else
        {
            cancellation.Cancel();
        }

        var error = await CaptureExceptionAsync(() =>
            RestoreWithHooksAsync(files, hooks, "cancel", cancellation.Token));
        Assert.IsNotNull(error);
        Assert.IsInstanceOfType<OperationCanceledException>(error);
        Assert.AreEqual(expectsNewLive ? "new-live" : "old-live", ReadProtocolValue(files.Live));
        await AssertDatabaseValidAndDeleteFullAsync(files.Live);
        AssertRestoreResidue(files.Root, expectedCount: 0);

        var retry = await RestoreWithHooksAsync(files, null, "cancel-retry");
        Assert.IsTrue(retry.LiveValidation.IsValid);
        Assert.AreEqual("new-live", ReadProtocolValue(files.Live));
        AssertRestoreResidue(files.Root, expectedCount: 0);
    }

    [TestMethod]
    [DataRow(nameof(RestoreFailurePoint.AfterPreparedBeforeSwap), "old-live")]
    [DataRow(nameof(RestoreFailurePoint.ImmediatelyAfterReplace), "old-live")]
    [DataRow(nameof(RestoreFailurePoint.AfterCommittedBeforeCleanup), "new-live")]
    public async Task RestoreCrashMarkers_RecoverIdempotently(
        string pointName,
        string expectedAfterRecovery)
    {
        var point = Enum.Parse<RestoreFailurePoint>(pointName);
        using var files = await RestoreProtocolFiles.CreateAsync();
        var hooks = new BackupRestoreTestHooks
        {
            RestoreFault = observed =>
            {
                if (observed == point)
                    throw new RestoreCrashSimulationException(pointName);
            }
        };

        var error = await CaptureExceptionAsync(() =>
            RestoreWithHooksAsync(files, hooks, "crash"));
        Assert.IsInstanceOfType<RestoreCrashSimulationException>(error);
        Assert.IsTrue(File.Exists(files.Live + ".restore-in-progress"));

        var installer = new AtomicRestoreInstaller();
        await installer.RecoverInterruptedInstallAsync(files.Live);
        await installer.RecoverInterruptedInstallAsync(files.Live);

        Assert.AreEqual(expectedAfterRecovery, ReadProtocolValue(files.Live));
        await AssertDatabaseValidAndDeleteFullAsync(files.Live);
        AssertRestoreResidue(files.Root, expectedCount: 0);
    }

    [TestMethod]
    public async Task RestoreWalSource_PreservesCommittedFramesWithConcurrentWriter()
    {
        using var files = await RestoreProtocolFiles.CreateAsync();
        using var keeper = OpenRaw(files.Source, SqliteOpenMode.ReadWrite);
        keeper.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=0;");
        keeper.Execute("UPDATE restore_protocol_probe SET value='wal-committed' WHERE id=1;");
        keeper.Execute(@"
CREATE TABLE IF NOT EXISTS restore_protocol_wal_writes(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  value TEXT NOT NULL);");

        var stop = 0;
        var writes = 0;
        var maximumLatency = 0L;
        Exception? writerFailure = null;
        var writer = Task.Run(async () =>
        {
            try
            {
                using var connection = OpenRaw(files.Source, SqliteOpenMode.ReadWrite);
                while (Volatile.Read(ref stop) == 0)
                {
                    var stopwatch = Stopwatch.StartNew();
                    await connection.ExecuteAsync(
                        "INSERT INTO restore_protocol_wal_writes(value) VALUES('writer');");
                    stopwatch.Stop();
                    UpdateMaximum(ref maximumLatency, stopwatch.ElapsedMilliseconds);
                    Interlocked.Increment(ref writes);
                    await Task.Delay(1);
                }
            }
            catch (Exception ex)
            {
                writerFailure = ex;
            }
        });

        await WaitUntilAsync(() => Volatile.Read(ref writes) >= 3, "WAL writer did not start.");
        try
        {
            var result = await RestoreWithHooksAsync(files, null, "wal-writer");
            Assert.IsTrue(result.LiveValidation.IsValid);
        }
        finally
        {
            Interlocked.Exchange(ref stop, 1);
            await writer;
        }

        Assert.IsNull(writerFailure, "WAL writer failed: " + writerFailure);
        Assert.IsTrue(maximumLatency < 5000, "WAL writer exceeded busy_timeout.");
        Assert.AreEqual("wal-committed", ReadProtocolValue(files.Live));
        using (var restored = new SqliteConnectionFactory(PosDbOptions.ForPath(files.Live)).Open())
        {
            var restoredWrites = await restored.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM restore_protocol_wal_writes;");
            var sourceWrites = keeper.ExecuteScalar<long>(
                "SELECT COUNT(1) FROM restore_protocol_wal_writes;");
            Assert.IsTrue(restoredWrites >= 1, "Restore lost every committed concurrent WAL write.");
            Assert.IsTrue(restoredWrites <= sourceWrites, "Restore contains an uncommitted future write.");
        }
        await AssertDatabaseValidAndDeleteFullAsync(files.Live);
        AssertRestoreResidue(files.Root, expectedCount: 0);
    }

    [TestMethod]
    public async Task OnlineBackup_WalSourceIsReadOnlyAndPreservesCommittedFrames()
    {
        using var files = await RestoreProtocolFiles.CreateAsync();
        using var keeper = OpenRaw(files.Live, SqliteOpenMode.ReadWrite);
        keeper.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=0;");
        keeper.Execute("UPDATE restore_protocol_probe SET value='wal-backup-committed' WHERE id=1;");
        Assert.AreEqual("wal", keeper.ExecuteScalar<string>("PRAGMA journal_mode;")?.ToLowerInvariant());

        var destination = Path.Combine(files.Root, "wal-backup.db");
        var result = await new SqliteOnlineBackup(files.LiveFactory)
            .CreateVerifiedAsync(destination);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual("wal", keeper.ExecuteScalar<string>("PRAGMA journal_mode;")?.ToLowerInvariant(),
            "Online backup must not mutate the WAL source policy.");
        Assert.AreEqual("wal-backup-committed", ReadProtocolValue(destination));
        await AssertDatabaseValidAndDeleteFullAsync(destination);
    }

    [TestMethod]
    public async Task OnlineBackup_SourceIdentityGuardBlocksDeleteAndReplace()
    {
        using var files = await RestoreProtocolFiles.CreateAsync();
        var replacement = Path.Combine(files.Root, "backup-replacement.db");
        File.Copy(files.Source, replacement);
        var deleteBlocked = false;
        var replaceBlocked = false;
        var hooks = new BackupRestoreTestHooks
        {
            BackupFault = point =>
            {
                if (point != BackupFailurePoint.SourceRemovedOrLocked)
                    return;
                try
                {
                    File.Delete(files.Live);
                }
                catch (IOException)
                {
                    deleteBlocked = true;
                }
                try
                {
                    File.Replace(replacement, files.Live, null);
                }
                catch (IOException)
                {
                    replaceBlocked = true;
                }
            }
        };
        var destination = Path.Combine(files.Root, "identity-backup.db");
        var result = await new SqliteOnlineBackup(files.LiveFactory, null, hooks)
            .CreateVerifiedAsync(destination);

        Assert.IsTrue(result.IsValid);
        Assert.IsTrue(deleteBlocked, "Backup source deletion was not blocked.");
        Assert.IsTrue(replaceBlocked, "Backup source replacement was not blocked.");
        Assert.AreEqual("old-live", ReadProtocolValue(files.Live));
        Assert.AreEqual(0, Directory.GetFiles(files.Root, "*.partial-*").Length);
    }

    [TestMethod]
    public async Task OnlineBackup_TransientNativeBusy_RetriesFromCleanPartialSnapshot()
    {
        using var files = await RestoreProtocolFiles.CreateAsync();
        var attempts = 0;
        var hooks = new BackupRestoreTestHooks
        {
            BackupFault = point =>
            {
                if (point == BackupFailurePoint.SourceRemovedOrLocked &&
                    Interlocked.Increment(ref attempts) == 1)
                {
                    throw new SqliteException("deterministic snapshot busy", 5);
                }
            }
        };
        var destination = Path.Combine(files.Root, "busy-retry-backup.db");

        var result = await new SqliteOnlineBackup(files.LiveFactory, null, hooks)
            .CreateVerifiedAsync(destination);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(2, attempts, "Transient SQLITE_BUSY must retry exactly once.");
        Assert.AreEqual(0, Directory.GetFiles(files.Root, "*.partial-*").Length);
        await AssertDatabaseValidAndDeleteFullAsync(destination);
    }

    [TestMethod]
    public async Task RestoreSnapshot_TransientNativeBusy_RetriesFromCleanCandidate()
    {
        using var files = await RestoreProtocolFiles.CreateAsync();
        var attempts = 0;
        var hooks = new BackupRestoreTestHooks
        {
            RestoreFault = point =>
            {
                if (point == RestoreFailurePoint.DuringSourceSnapshotIdentityGuard &&
                    Interlocked.Increment(ref attempts) == 1)
                {
                    throw new SqliteException("deterministic restore snapshot busy", 5);
                }
            }
        };

        var result = await RestoreWithHooksAsync(files, hooks, "busy-retry");

        Assert.IsTrue(result.LiveValidation.IsValid);
        Assert.AreEqual(2, attempts, "Transient restore SQLITE_BUSY must retry exactly once.");
        Assert.AreEqual("new-live", ReadProtocolValue(files.Live));
        AssertRestoreResidue(files.Root, expectedCount: 0);
    }

    [TestMethod]
    public async Task NativeSnapshot_PersistentBusyFailsClosedAndCleanRetryPasses()
    {
        using var files = await RestoreProtocolFiles.CreateAsync();
        var backupAttempts = 0;
        var backupDestination = Path.Combine(files.Root, "persistent-busy-backup.db");
        var backupHooks = new BackupRestoreTestHooks
        {
            BackupFault = point =>
            {
                if (point == BackupFailurePoint.SourceRemovedOrLocked)
                {
                    Interlocked.Increment(ref backupAttempts);
                    throw new SqliteException("persistent deterministic backup busy", 5);
                }
            }
        };

        await Assert.ThrowsExactlyAsync<SqliteException>(() =>
            new SqliteOnlineBackup(files.LiveFactory, null, backupHooks)
                .CreateVerifiedAsync(backupDestination));
        Assert.AreEqual(5, backupAttempts, "Backup SQLITE_BUSY retry must be bounded.");
        Assert.IsFalse(File.Exists(backupDestination));
        Assert.AreEqual(0, Directory.GetFiles(files.Root, "*.partial-*").Length);

        var backupRetryDestination = Path.Combine(files.Root, "persistent-busy-backup-retry.db");
        var backupRetry = await new SqliteOnlineBackup(files.LiveFactory)
            .CreateVerifiedAsync(backupRetryDestination);
        Assert.IsTrue(backupRetry.IsValid);
        await AssertDatabaseValidAndDeleteFullAsync(backupRetryDestination);

        var restoreAttempts = 0;
        var restoreHooks = new BackupRestoreTestHooks
        {
            RestoreFault = point =>
            {
                if (point == RestoreFailurePoint.DuringSourceSnapshotIdentityGuard)
                {
                    Interlocked.Increment(ref restoreAttempts);
                    throw new SqliteException("persistent deterministic restore busy", 5);
                }
            }
        };

        await Assert.ThrowsExactlyAsync<SqliteException>(() =>
            RestoreWithHooksAsync(files, restoreHooks, "persistent-busy"));
        Assert.AreEqual(5, restoreAttempts, "Restore SQLITE_BUSY retry must be bounded.");
        Assert.AreEqual("old-live", ReadProtocolValue(files.Live));
        AssertRestoreResidue(files.Root, expectedCount: 0);

        var restoreRetry = await RestoreWithHooksAsync(files, null, "persistent-busy-retry");
        Assert.IsTrue(restoreRetry.LiveValidation.IsValid);
        Assert.AreEqual("new-live", ReadProtocolValue(files.Live));
        AssertRestoreResidue(files.Root, expectedCount: 0);
    }

    [TestMethod]
    public async Task NativeSnapshot_CancellationAfterBusyStopsBeforeRetryAndCleansPartial()
    {
        using var files = await RestoreProtocolFiles.CreateAsync();
        using var backupCancellation = new CancellationTokenSource();
        var backupAttempts = 0;
        var backupDestination = Path.Combine(files.Root, "busy-cancel-backup.db");
        var backupHooks = new BackupRestoreTestHooks
        {
            BackupFault = point =>
            {
                if (point == BackupFailurePoint.SourceRemovedOrLocked)
                {
                    Interlocked.Increment(ref backupAttempts);
                    backupCancellation.Cancel();
                    throw new SqliteException("cancel after backup busy", 5);
                }
            }
        };

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            new SqliteOnlineBackup(files.LiveFactory, null, backupHooks)
                .CreateVerifiedAsync(backupDestination, backupCancellation.Token));
        Assert.AreEqual(1, backupAttempts);
        Assert.IsFalse(File.Exists(backupDestination));
        Assert.AreEqual(0, Directory.GetFiles(files.Root, "*.partial-*").Length);

        using var restoreCancellation = new CancellationTokenSource();
        var restoreAttempts = 0;
        var restoreHooks = new BackupRestoreTestHooks
        {
            RestoreFault = point =>
            {
                if (point == RestoreFailurePoint.DuringSourceSnapshotIdentityGuard)
                {
                    Interlocked.Increment(ref restoreAttempts);
                    restoreCancellation.Cancel();
                    throw new SqliteException("cancel after restore busy", 5);
                }
            }
        };

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            RestoreWithHooksAsync(
                files,
                restoreHooks,
                "busy-cancel",
                restoreCancellation.Token));
        Assert.AreEqual(1, restoreAttempts);
        Assert.AreEqual("old-live", ReadProtocolValue(files.Live));
        AssertRestoreResidue(files.Root, expectedCount: 0);
    }

    [TestMethod]
    public async Task BackupRestore_DiagnosticSinkFailureCannotChangeProtocolOutcome()
    {
        using var files = await RestoreProtocolFiles.CreateAsync();
        var backupPath = Path.Combine(files.Root, "diagnostic-backup.db");
        var backup = await new SqliteOnlineBackup(
                files.LiveFactory,
                _ => throw new InvalidOperationException("diagnostic-sink-test"))
            .CreateVerifiedAsync(backupPath);
        Assert.IsTrue(backup.IsValid);
        await AssertDatabaseValidAndDeleteFullAsync(backupPath);

        var coordinator = new SqliteRestoreCoordinator(
            files.LiveFactory,
            new SqliteOnlineBackup(files.LiveFactory),
            _ => throw new InvalidOperationException("diagnostic-sink-test"));
        var result = await coordinator.RestoreAsync(
            files.Source,
            Path.Combine(files.Root, "diagnostic-pre.db"),
            ProtocolShopId,
            ProtocolShopCode,
            files.LiveEpoch,
            _ => Task.CompletedTask);
        Assert.IsTrue(result.LiveValidation.IsValid);
        Assert.AreEqual("new-live", ReadProtocolValue(files.Live));
        AssertRestoreResidue(files.Root, expectedCount: 0);
    }

    [TestMethod]
    public async Task RestoreSourceSidecarMatrix_IsDeterministicAndFailClosed()
    {
        using (var stale = await RestoreProtocolFiles.CreateAsync())
        {
            File.WriteAllBytes(stale.Source + "-wal", new byte[64]);
            var error = await CaptureExceptionAsync(() =>
                RestoreWithHooksAsync(stale, null, "stale"));
            Assert.IsInstanceOfType<InvalidDataException>(error);
            Assert.AreEqual("old-live", ReadProtocolValue(stale.Live));
            AssertRestoreResidue(stale.Root, expectedCount: 0);

            DeleteIfPresentForTest(stale.Source + "-wal");
            File.WriteAllBytes(stale.Source + "-shm", new byte[64]);
            error = await CaptureExceptionAsync(() =>
                RestoreWithHooksAsync(stale, null, "stale-shm"));
            Assert.IsInstanceOfType<InvalidDataException>(error);
            Assert.AreEqual("old-live", ReadProtocolValue(stale.Live));
            AssertRestoreResidue(stale.Root, expectedCount: 0);
        }

        using (var missing = await RestoreProtocolFiles.CreateAsync())
        {
            using (var connection = OpenRaw(missing.Source, SqliteOpenMode.ReadWrite))
                connection.Execute("PRAGMA journal_mode=WAL;");
            DeleteIfPresentForTest(missing.Source + "-wal");
            DeleteIfPresentForTest(missing.Source + "-shm");
            var error = await CaptureExceptionAsync(() =>
                RestoreWithHooksAsync(missing, null, "missing-wal"));
            Assert.IsInstanceOfType<InvalidDataException>(error);
            Assert.AreEqual("old-live", ReadProtocolValue(missing.Live));
            AssertRestoreResidue(missing.Root, expectedCount: 0);
        }

        using (var mismatch = await RestoreProtocolFiles.CreateAsync())
        {
            using (var sourceHeader = OpenRaw(mismatch.Source, SqliteOpenMode.ReadWrite))
                sourceHeader.Execute("PRAGMA journal_mode=WAL;");
            DeleteIfPresentForTest(mismatch.Source + "-wal");
            DeleteIfPresentForTest(mismatch.Source + "-shm");

            var foreign = Path.Combine(mismatch.Root, "foreign.db");
            using var foreignKeeper = new SqliteConnection(
                "Data Source=" + foreign + ";Mode=ReadWriteCreate;Pooling=False");
            foreignKeeper.Open();
            foreignKeeper.Execute(@"
PRAGMA page_size=8192;
VACUUM;
PRAGMA journal_mode=WAL;
PRAGMA wal_autocheckpoint=0;
CREATE TABLE foreign_probe(id INTEGER PRIMARY KEY, value TEXT);
INSERT INTO foreign_probe(id, value) VALUES(1, 'foreign');");
            File.Copy(foreign + "-wal", mismatch.Source + "-wal", true);
            File.Copy(foreign + "-shm", mismatch.Source + "-shm", true);

            var error = await CaptureExceptionAsync(() =>
                RestoreWithHooksAsync(mismatch, null, "mismatch"));
            Assert.IsNotNull(error, "Mismatched WAL/SHM sidecars must not be accepted.");
            Assert.AreEqual("old-live", ReadProtocolValue(mismatch.Live));
            AssertRestoreResidue(mismatch.Root, expectedCount: 0);
        }
    }

    [TestMethod]
    public async Task RestoreSourceIdentityGuard_BlocksDeleteAndReplaceDuringSnapshot()
    {
        using var files = await RestoreProtocolFiles.CreateAsync();
        var replacement = Path.Combine(files.Root, "replacement.db");
        File.Copy(files.Live, replacement);
        var deleteBlocked = false;
        var replaceBlocked = false;
        var hooks = new BackupRestoreTestHooks
        {
            RestoreFault = point =>
            {
                if (point != RestoreFailurePoint.DuringSourceSnapshotIdentityGuard)
                    return;
                try
                {
                    File.Delete(files.Source);
                }
                catch (IOException)
                {
                    deleteBlocked = true;
                }
                try
                {
                    File.Replace(replacement, files.Source, null);
                }
                catch (IOException)
                {
                    replaceBlocked = true;
                }
            }
        };

        var result = await RestoreWithHooksAsync(files, hooks, "identity");
        Assert.IsTrue(result.LiveValidation.IsValid);
        Assert.IsTrue(deleteBlocked, "Source deletion was not blocked by the identity guard.");
        Assert.IsTrue(replaceBlocked, "Source replacement was not blocked by the identity guard.");
        Assert.AreEqual("new-live", ReadProtocolValue(files.Live));
        AssertRestoreResidue(files.Root, expectedCount: 0);
    }

    [TestMethod]
    public async Task RestoreRejectsSameLiveCorruptForeignKeyAndLockedSources_ThenRetries()
    {
        using var files = await RestoreProtocolFiles.CreateAsync();
        var liveFactory = files.LiveFactory;
        var sameLive = new SqliteRestoreCoordinator(
            liveFactory,
            new SqliteOnlineBackup(liveFactory));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            sameLive.RestoreAsync(
                files.Live,
                Path.Combine(files.Root, "same-pre.db"),
                ProtocolShopId,
                ProtocolShopCode,
                files.LiveEpoch,
                _ => Task.CompletedTask));

        var corrupt = Path.Combine(files.Root, "corrupt.db");
        File.WriteAllBytes(corrupt, new byte[] { 0, 1, 2, 3, 4 });
        Assert.IsNotNull(await CaptureExceptionAsync(() =>
            RestoreSourceAsync(files, corrupt, "corrupt")));
        Assert.AreEqual("old-live", ReadProtocolValue(files.Live));

        using (var source = OpenRaw(files.Source, SqliteOpenMode.ReadWrite))
        {
            source.Execute(@"
PRAGMA foreign_keys=OFF;
CREATE TABLE IF NOT EXISTS restore_fk_parent(id INTEGER PRIMARY KEY);
CREATE TABLE IF NOT EXISTS restore_fk_child(
  id INTEGER PRIMARY KEY,
  parent_id INTEGER NOT NULL REFERENCES restore_fk_parent(id));
INSERT INTO restore_fk_child(id, parent_id) VALUES(1, 999);");
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            RestoreWithHooksAsync(files, null, "fk"));
        Assert.AreEqual("old-live", ReadProtocolValue(files.Live));
        AssertRestoreResidue(files.Root, expectedCount: 0);

        using (var locked = new FileStream(
            files.Source,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            await Assert.ThrowsExactlyAsync<IOException>(() =>
                RestoreWithHooksAsync(files, null, "locked"));
        }

        using (var repair = OpenRaw(files.Source, SqliteOpenMode.ReadWrite))
            repair.Execute("DELETE FROM restore_fk_child;");
        var retry = await RestoreWithHooksAsync(files, null, "source-retry");
        Assert.IsTrue(retry.LiveValidation.IsValid);
        Assert.AreEqual("new-live", ReadProtocolValue(files.Live));
        AssertRestoreResidue(files.Root, expectedCount: 0);
    }

    [TestMethod]
    [DataRow(nameof(BackupFailurePoint.BeforeSourceOpen))]
    [DataRow(nameof(BackupFailurePoint.AfterTemporarySnapshotCreation))]
    [DataRow(nameof(BackupFailurePoint.AfterSnapshotBeforeValidation))]
    [DataRow(nameof(BackupFailurePoint.AfterIntegrityForeignKey))]
    [DataRow(nameof(BackupFailurePoint.BeforePublish))]
    [DataRow(nameof(BackupFailurePoint.PublishError))]
    [DataRow(nameof(BackupFailurePoint.SourceRemovedOrLocked))]
    public async Task BackupFailureInjection_NeverPublishesPartialAndRetryPasses(string pointName)
    {
        var point = Enum.Parse<BackupFailurePoint>(pointName);
        using var files = await RestoreProtocolFiles.CreateAsync();
        var destination = Path.Combine(files.Root, "backup-fault.db");
        var hooks = new BackupRestoreTestHooks
        {
            BackupFault = observed =>
            {
                if (observed == point)
                    throw new IOException("backup_fault_" + pointName);
            }
        };
        var backup = new SqliteOnlineBackup(files.LiveFactory, null, hooks);
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            backup.CreateVerifiedAsync(destination));
        Assert.IsFalse(File.Exists(destination), "A failed backup published a final file.");
        Assert.AreEqual(0, Directory.GetFiles(files.Root, "*.partial-*").Length);

        var retryPath = Path.Combine(files.Root, "backup-retry.db");
        var retry = await new SqliteOnlineBackup(files.LiveFactory)
            .CreateVerifiedAsync(retryPath);
        Assert.IsTrue(retry.IsValid);
        await AssertDatabaseValidAndDeleteFullAsync(retryPath);
    }

    [TestMethod]
    public async Task BackupCollisionCleanupCancellationAndInvalidDestinations_AreSafe()
    {
        using var files = await RestoreProtocolFiles.CreateAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new SqliteOnlineBackup(files.LiveFactory).CreateVerifiedAsync(files.Live));

        var existing = Path.Combine(files.Root, "existing.db");
        File.Copy(files.Live, existing);
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            new SqliteOnlineBackup(files.LiveFactory).CreateVerifiedAsync(existing));

        const string fixedToken = "fixedtoken";
        var collisionFinal = Path.Combine(files.Root, "collision.db");
        var collisionPartial = collisionFinal + ".partial-" + fixedToken;
        File.WriteAllText(collisionPartial, "preexisting");
        var collisionCount = 0;
        var collisionHooks = new BackupRestoreTestHooks
        {
            TemporaryTokenFactory = () => fixedToken,
            BackupFault = point =>
            {
                if (point == BackupFailurePoint.Collision)
                    collisionCount++;
            }
        };
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            new SqliteOnlineBackup(files.LiveFactory, null, collisionHooks)
                .CreateVerifiedAsync(collisionFinal));
        Assert.AreEqual(16, collisionCount);
        Assert.AreEqual("preexisting", File.ReadAllText(collisionPartial));
        File.Delete(collisionPartial);

        var cleanupFinal = Path.Combine(files.Root, "cleanup.db");
        var cleanupHooks = new BackupRestoreTestHooks
        {
            BackupFault = point =>
            {
                if (point == BackupFailurePoint.AfterSnapshotBeforeValidation ||
                    point == BackupFailurePoint.CleanupFailure)
                {
                    throw new IOException("cleanup-injection");
                }
            }
        };
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            new SqliteOnlineBackup(files.LiveFactory, null, cleanupHooks)
                .CreateVerifiedAsync(cleanupFinal));
        Assert.IsFalse(File.Exists(cleanupFinal));
        Assert.AreEqual(0, Directory.GetFiles(files.Root, "*.partial-*").Length);

        using var cancellation = new CancellationTokenSource();
        var cancelFinal = Path.Combine(files.Root, "cancel.db");
        var cancelHooks = new BackupRestoreTestHooks
        {
            BackupFault = point =>
            {
                if (point == BackupFailurePoint.AfterTemporarySnapshotCreation)
                    cancellation.Cancel();
            }
        };
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            new SqliteOnlineBackup(files.LiveFactory, null, cancelHooks)
                .CreateVerifiedAsync(cancelFinal, cancellation.Token));
        Assert.IsFalse(File.Exists(cancelFinal));
        Assert.AreEqual(0, Directory.GetFiles(files.Root, "*.partial-*").Length);

        var deniedFinal = Path.Combine(files.Root, "denied", "backup.db");
        var deniedHooks = new BackupRestoreTestHooks
        {
            BackupFault = point =>
            {
                if (point == BackupFailurePoint.UnwritableDestination)
                    throw new UnauthorizedAccessException("denied-test");
            }
        };
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
            new SqliteOnlineBackup(files.LiveFactory, null, deniedHooks)
                .CreateVerifiedAsync(deniedFinal));
        Assert.IsFalse(File.Exists(deniedFinal));
    }

    [TestMethod]
    [DataRow("-journal")]
    [DataRow("-wal")]
    [DataRow("-shm")]
    public async Task BackupRejectsPreexistingFinalSidecarWithoutPublishingOrDeletingIt(
        string sidecarSuffix)
    {
        using var files = await RestoreProtocolFiles.CreateAsync();
        var destination = Path.Combine(files.Root, "sidecar-collision.db");
        var sidecar = destination + sidecarSuffix;
        File.WriteAllText(sidecar, "preexisting-sidecar");

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            new SqliteOnlineBackup(files.LiveFactory)
                .CreateVerifiedAsync(destination));

        Assert.IsFalse(File.Exists(destination));
        Assert.AreEqual("preexisting-sidecar", File.ReadAllText(sidecar));
        Assert.AreEqual(0, Directory.GetFiles(files.Root, "*.partial-*").Length);

        File.Delete(sidecar);
        var retry = await new SqliteOnlineBackup(files.LiveFactory)
            .CreateVerifiedAsync(destination);
        Assert.IsTrue(retry.IsValid);
        await AssertDatabaseValidAndDeleteFullAsync(destination);
    }

    [TestMethod]
    public async Task BackupRejectsFinalSidecarCreatedImmediatelyBeforePublish()
    {
        using var files = await RestoreProtocolFiles.CreateAsync();
        var destination = Path.Combine(files.Root, "publish-race.db");
        var sidecar = destination + "-wal";
        var hooks = new BackupRestoreTestHooks
        {
            BackupFault = point =>
            {
                if (point == BackupFailurePoint.PublishError)
                    File.WriteAllText(sidecar, "publish-race-sidecar");
            }
        };

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            new SqliteOnlineBackup(files.LiveFactory, null, hooks)
                .CreateVerifiedAsync(destination));

        Assert.IsFalse(File.Exists(destination));
        Assert.AreEqual("publish-race-sidecar", File.ReadAllText(sidecar));
        Assert.AreEqual(0, Directory.GetFiles(files.Root, "*.partial-*").Length);

        File.Delete(sidecar);
        var retry = await new SqliteOnlineBackup(files.LiveFactory)
            .CreateVerifiedAsync(destination);
        Assert.IsTrue(retry.IsValid);
        await AssertDatabaseValidAndDeleteFullAsync(destination);
    }

    [TestMethod]
    public async Task RecoveryMalformedUnsafeAmbiguousAndCorruptRollback_FailsClosed()
    {
        using (var unsafeMarker = await RestoreProtocolFiles.CreateAsync())
        {
            WriteMarker(
                unsafeMarker.Live,
                "prepared",
                "..\\escape.db",
                "r-deadbeef.old");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                new AtomicRestoreInstaller().RecoverInterruptedInstallAsync(unsafeMarker.Live));
            Assert.AreEqual("old-live", ReadProtocolValue(unsafeMarker.Live));
            Assert.IsTrue(File.Exists(unsafeMarker.Live + ".restore-in-progress"));
        }

        using (var truncated = await RestoreProtocolFiles.CreateAsync())
        {
            File.WriteAllText(truncated.Live + ".restore-in-progress.tmp-dead", "version=1\nphase=");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                new AtomicRestoreInstaller().RecoverInterruptedInstallAsync(truncated.Live));
            Assert.AreEqual("old-live", ReadProtocolValue(truncated.Live));
        }

        using (var corruptRollback = await RestoreProtocolFiles.CreateAsync())
        {
            var rollback = Path.Combine(corruptRollback.Root, "r-deadbeef.old");
            File.WriteAllBytes(corruptRollback.Live, new byte[] { 0, 1, 2, 3 });
            File.WriteAllBytes(rollback, new byte[] { 4, 5, 6, 7 });
            WriteMarker(
                corruptRollback.Live,
                "committed",
                "r-cafebabe.db",
                Path.GetFileName(rollback));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                new AtomicRestoreInstaller().RecoverInterruptedInstallAsync(corruptRollback.Live));
            Assert.IsTrue(File.Exists(corruptRollback.Live + ".restore-in-progress"));
            Assert.IsTrue(File.Exists(rollback));
        }

        using (var missing = await RestoreProtocolFiles.CreateAsync())
        {
            File.Delete(missing.Live);
            WriteMarker(missing.Live, "prepared", "r-cafebabe.db", "r-deadbeef.old");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                new AtomicRestoreInstaller().RecoverInterruptedInstallAsync(missing.Live));
            Assert.IsTrue(File.Exists(missing.Live + ".restore-in-progress"));
        }

        using (var liveAlias = await RestoreProtocolFiles.CreateAsync())
        {
            WriteMarker(
                liveAlias.Live,
                "prepared",
                Path.GetFileName(liveAlias.Live),
                "r-deadbeef.old");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                new AtomicRestoreInstaller().RecoverInterruptedInstallAsync(liveAlias.Live));
            Assert.AreEqual("old-live", ReadProtocolValue(liveAlias.Live));
            Assert.IsTrue(File.Exists(liveAlias.Live + ".restore-in-progress"));
        }
    }

    [TestMethod]
    public async Task StartupRecoveryHook_IsInvokedAndSecondRecoveryIsIdempotent()
    {
        using var files = await RestoreProtocolFiles.CreateAsync();
        WriteMarker(files.Live, "prepared", "r-cafebabe.db", "r-deadbeef.old");
        var recoveryCalls = 0;
        var hooks = new BackupRestoreTestHooks
        {
            RestoreFault = point =>
            {
                if (point == RestoreFailurePoint.StartupRecovery)
                    recoveryCalls++;
            }
        };
        var installer = new AtomicRestoreInstaller(null, hooks, "recovery-test", null);
        await installer.RecoverInterruptedInstallAsync(files.Live);
        await installer.RecoverInterruptedInstallAsync(files.Live);
        Assert.AreEqual(2, recoveryCalls);
        Assert.AreEqual("old-live", ReadProtocolValue(files.Live));
        AssertRestoreResidue(files.Root, expectedCount: 0);
    }

    private static async Task<RestoreOperationResult> RestoreWithHooksAsync(
        RestoreProtocolFiles files,
        BackupRestoreTestHooks? hooks,
        string suffix,
        CancellationToken cancellationToken = default)
    {
        return await RestoreSourceAsync(files, files.Source, suffix, hooks, cancellationToken);
    }

    private static async Task<RestoreOperationResult> RestoreSourceAsync(
        RestoreProtocolFiles files,
        string sourcePath,
        string suffix,
        BackupRestoreTestHooks? hooks = null,
        CancellationToken cancellationToken = default)
    {
        var preBackupPath = Path.Combine(
            files.Root,
            "pre-" + suffix + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".db");
        var onlineBackup = hooks == null
            ? new SqliteOnlineBackup(files.LiveFactory)
            : new SqliteOnlineBackup(files.LiveFactory, null, hooks);
        var coordinator = hooks == null
            ? new SqliteRestoreCoordinator(files.LiveFactory, onlineBackup)
            : new SqliteRestoreCoordinator(files.LiveFactory, onlineBackup, null, hooks);
        return await coordinator.RestoreAsync(
            sourcePath,
            preBackupPath,
            ProtocolShopId,
            ProtocolShopCode,
            files.LiveEpoch,
            validation =>
            {
                Assert.IsTrue(validation.IsValid);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    private static async Task<Exception?> CaptureExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task AssertDatabaseValidAndDeleteFullAsync(string path)
    {
        var factory = new SqliteConnectionFactory(PosDbOptions.ForPath(path));
        var validation = await new DbMaintenanceRepository(factory).ValidateAsync();
        Assert.IsTrue(validation.IsValid, validation.IntegrityCheck + " / " + validation.ForeignKeyCheck);
        using var connection = factory.Open();
        Assert.AreEqual("delete", (await connection.ExecuteScalarAsync<string>("PRAGMA journal_mode;"))?.ToLowerInvariant());
        Assert.AreEqual(2L, await connection.ExecuteScalarAsync<long>("PRAGMA synchronous;"));
        connection.Dispose();
        SqliteConnectionFactory.ClearAllPools();
        Assert.IsFalse(File.Exists(path + "-wal"));
        Assert.IsFalse(File.Exists(path + "-shm"));
    }

    private static async Task AssertExistingPreBackupsValidAsync(string root)
    {
        foreach (var path in Directory.GetFiles(root, "pre-*.db"))
            await AssertDatabaseValidAndDeleteFullAsync(path);
    }

    private static void AssertRestoreResidue(string root, int expectedCount)
    {
        var residue = Directory.GetFiles(root)
            .Where(path =>
                Path.GetFileName(path).StartsWith("r-", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".restore-in-progress", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Contains(".restore-in-progress.tmp-", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.AreEqual(expectedCount, residue.Length, "Unexpected restore residue: " +
            string.Join(",", residue.Select(Path.GetFileName)));
    }

    private static string ReadProtocolValue(string path)
    {
        using var connection = OpenRaw(path, SqliteOpenMode.ReadOnly);
        return connection.ExecuteScalar<string>(
            "SELECT value FROM restore_protocol_probe WHERE id=1;") ?? string.Empty;
    }

    private static SqliteConnection OpenRaw(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                Cache = SqliteCacheMode.Private,
                DataSource = path,
                Mode = mode,
                Pooling = false
            }.ToString());
        connection.Open();
        return connection;
    }

    private static void DeleteIfPresentForTest(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private sealed class RestoreProtocolFiles : IDisposable
    {
        private RestoreProtocolFiles(string root)
        {
            Root = root;
            Live = Path.Combine(root, "live.db");
            Source = Path.Combine(root, "source.db");
            LiveFactory = new SqliteConnectionFactory(PosDbOptions.ForPath(Live));
        }

        public string Live { get; }
        public long LiveEpoch { get; private set; }
        public SqliteConnectionFactory LiveFactory { get; }
        public string Root { get; }
        public string Source { get; }

        public static async Task<RestoreProtocolFiles> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "Win7POS.RestoreProtocol",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var files = new RestoreProtocolFiles(root);
            await CreateDatabaseAsync(files.Live, "old-live");
            await CreateDatabaseAsync(files.Source, "new-live");
            files.LiveEpoch = await new CatalogShopStateRepository(files.LiveFactory)
                .LoadTransitionEpochAsync();
            SqliteConnectionFactory.ClearAllPools();
            return files;
        }

        public void Dispose()
        {
            SqliteConnectionFactory.ClearAllPools();
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, true);
            }
            catch
            {
            }
        }

        private static async Task CreateDatabaseAsync(string path, string value)
        {
            var options = PosDbOptions.ForPath(path);
            DbInitializer.EnsureCreated(options);
            var factory = new SqliteConnectionFactory(options);
            await new ShopOfficialSnapshotRepository(factory).SaveAsync(new OfficialShopSnapshot
            {
                ShopCode = ProtocolShopCode,
                ShopId = ProtocolShopId,
                ShopName = "Restore test",
                Source = "test"
            });
            await new CatalogShopStateRepository(factory)
                .EnsureAndLoadCursorAsync(ProtocolShopId, ProtocolShopCode);
            using var connection = factory.Open();
            await connection.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS restore_protocol_probe(
  id INTEGER PRIMARY KEY,
  value TEXT NOT NULL);
INSERT INTO restore_protocol_probe(id, value)
VALUES(1, @value)
ON CONFLICT(id) DO UPDATE SET value=@value;", new { value });
        }
    }
}
