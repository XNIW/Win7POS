using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Backup;

namespace Win7POS.Core.Tests.Data;

public sealed partial class PersistenceFoundationTests
{
    [TestMethod]
    public async Task OnlineBackup_RealExclusiveLockReleasedAtRetry_PublishesVerifiedSnapshot()
    {
        using var files = await RestoreProtocolFiles.CreateAsync();
        await using var writer = await ControlledExclusiveWriter.StartAsync(files.Live);
        var attempts = 0;
        var diagnostics = new List<BackupRestoreDiagnostic>();
        var hooks = new BackupRestoreTestHooks
        {
            BackupFault = point =>
            {
                if (point != BackupFailurePoint.SourceRemovedOrLocked)
                    return;
                if (Interlocked.Increment(ref attempts) == 2)
                    writer.ReleaseAsync().GetAwaiter().GetResult();
            }
        };
        var destination = Path.Combine(files.Root, "released-lock.db");

        var result = await new SqliteOnlineBackup(files.LiveFactory, diagnostics.Add, hooks)
            .CreateVerifiedAsync(destination).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(2, attempts, "The first attempt must observe the real held lock; the second must recover after release.");
        Assert.AreEqual("committed-concurrent-writer", ReadProtocolValue(destination));
        Assert.IsTrue(diagnostics.Any(item => item.Phase == "snapshot" && item.ResultCode == "ok_after_busy_retry"));
        Assert.AreEqual(0, Directory.GetFiles(files.Root, "*.partial-*").Length);
        await AssertDatabaseValidAndDeleteFullAsync(destination);
    }

    [TestMethod]
    public async Task OnlineBackup_RealPersistentExclusiveLock_FailsWithinAttemptBudgetAndCleansUp()
    {
        using var files = await RestoreProtocolFiles.CreateAsync();
        await using var writer = await ControlledExclusiveWriter.StartAsync(files.Live);
        var attempts = 0;
        var hooks = new BackupRestoreTestHooks
        {
            BackupFault = point =>
            {
                if (point == BackupFailurePoint.SourceRemovedOrLocked)
                    Interlocked.Increment(ref attempts);
            }
        };
        var destination = Path.Combine(files.Root, "persistent-lock.db");
        var elapsed = Stopwatch.StartNew();

        var error = await Assert.ThrowsExactlyAsync<SqliteException>(() =>
            new SqliteOnlineBackup(files.LiveFactory, null, hooks)
                .CreateVerifiedAsync(destination).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.AreEqual(5, error.SqliteErrorCode);
        Assert.AreEqual(5, attempts, "Persistent native BUSY must exhaust exactly the finite attempt budget.");
        Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(5), "The actual provider returned BUSY without using its command timeout.");
        Assert.IsFalse(File.Exists(destination));
        Assert.AreEqual(0, Directory.GetFiles(files.Root, "*.partial-*").Length);
        await writer.ReleaseAsync();
        var retry = await new SqliteOnlineBackup(files.LiveFactory).CreateVerifiedAsync(destination);
        Assert.IsTrue(retry.IsValid);
        Assert.AreEqual("committed-concurrent-writer", ReadProtocolValue(destination));
    }

    [TestMethod]
    public async Task OnlineBackup_CancelDuringRealLockAttempt_DoesNotRetryOrPublish()
    {
        using var files = await RestoreProtocolFiles.CreateAsync();
        await using var writer = await ControlledExclusiveWriter.StartAsync(files.Live);
        using var cancellation = new CancellationTokenSource();
        using var continueAttempt = new ManualResetEventSlim();
        var attemptEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var hooks = new BackupRestoreTestHooks
        {
            BackupFault = point =>
            {
                if (point != BackupFailurePoint.SourceRemovedOrLocked)
                    return;
                Interlocked.Increment(ref attempts);
                attemptEntered.TrySetResult(true);
                Assert.IsTrue(continueAttempt.Wait(TimeSpan.FromSeconds(10)), "Cancellation controller did not release the snapshot attempt.");
            }
        };
        var destination = Path.Combine(files.Root, "cancel-real-lock.db");
        var backup = new SqliteOnlineBackup(files.LiveFactory, null, hooks)
            .CreateVerifiedAsync(destination, cancellation.Token);
        try
        {
            await attemptEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
        }
        finally
        {
            continueAttempt.Set();
        }

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => backup.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(1, attempts);
        Assert.IsFalse(File.Exists(destination));
        Assert.AreEqual(0, Directory.GetFiles(files.Root, "*.partial-*").Length);
    }

    [TestMethod]
    public async Task OnlineBackup_RealInvalidSource_IsNotRetriedAndNeverPublished()
    {
        using var files = PersistenceFiles.Create();
        File.WriteAllBytes(files.Live, new byte[4096]);
        var attempts = 0;
        var hooks = new BackupRestoreTestHooks
        {
            BackupFault = point =>
            {
                if (point == BackupFailurePoint.SourceRemovedOrLocked)
                    Interlocked.Increment(ref attempts);
            }
        };
        var factory = new SqliteConnectionFactory(PosDbOptions.ForPath(files.Live));

        var error = await Assert.ThrowsExactlyAsync<SqliteException>(() =>
            new SqliteOnlineBackup(factory, null, hooks).CreateVerifiedAsync(files.Backup));

        Assert.AreEqual(26, error.SqliteErrorCode, "A malformed database must surface SQLITE_NOTADB.");
        Assert.AreEqual(1, attempts);
        Assert.IsFalse(File.Exists(files.Backup));
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(files.Backup)!, "*.partial-*").Length);
        Assert.AreEqual(4096L, new FileInfo(files.Live).Length);
    }

    [TestMethod]
    [DataRow(8)] // SQLITE_READONLY
    [DataRow(10)] // SQLITE_IOERR
    [DataRow(11)] // SQLITE_CORRUPT
    [DataRow(13)] // SQLITE_FULL
    public async Task OnlineBackup_IrreversibleSqliteFault_IsNotRetriedAndCleansPartial(int errorCode)
    {
        using var files = await RestoreProtocolFiles.CreateAsync();
        var destination = Path.Combine(files.Root, "irreversible.db");
        const string token = "nonretryable";
        var attempts = 0;
        var hooks = new BackupRestoreTestHooks
        {
            TemporaryTokenFactory = () => token,
            BackupFault = point =>
            {
                if (point != BackupFailurePoint.SourceRemovedOrLocked)
                    return;
                Interlocked.Increment(ref attempts);
                File.WriteAllText(destination + ".partial-" + token, "incomplete-fixture");
                throw new SqliteException("injected irreversible snapshot fault", errorCode);
            }
        };

        var error = await Assert.ThrowsExactlyAsync<SqliteException>(() =>
            new SqliteOnlineBackup(files.LiveFactory, null, hooks).CreateVerifiedAsync(destination));

        Assert.AreEqual(errorCode, error.SqliteErrorCode);
        Assert.AreEqual(1, attempts);
        Assert.IsFalse(File.Exists(destination));
        Assert.AreEqual(0, Directory.GetFiles(files.Root, "*.partial-*").Length);
        Assert.AreEqual("old-live", ReadProtocolValue(files.Live));
    }

    private sealed class ControlledExclusiveWriter : IAsyncDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource<bool> _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _worker = Task.CompletedTask;

        public static async Task<ControlledExclusiveWriter> StartAsync(string path)
        {
            var writer = new ControlledExclusiveWriter();
            writer._worker = Task.Factory.StartNew(() =>
            {
                try
                {
                    using var connection = OpenRaw(path, SqliteOpenMode.ReadWrite);
                    connection.Execute("BEGIN EXCLUSIVE; UPDATE restore_protocol_probe SET value='committed-concurrent-writer' WHERE id=1;");
                    writer._held.TrySetResult(true);
                    Assert.IsTrue(writer._release.Wait(TimeSpan.FromSeconds(10)), "Exclusive writer was not released by its test controller.");
                    connection.Execute("COMMIT;");
                }
                catch (Exception error)
                {
                    writer._held.TrySetException(error);
                    throw;
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                await writer._held.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return writer;
            }
            catch
            {
                await writer.DisposeAsync();
                throw;
            }
        }

        public async Task ReleaseAsync()
        {
            _release.Set();
            await _worker.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async ValueTask DisposeAsync()
        {
            await ReleaseAsync();
            _release.Dispose();
        }
    }
}
