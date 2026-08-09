using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Data;
using Win7POS.Data.Backup;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

internal enum BackupRestoreSelfTestMode
{
    Functional,
    Failure,
    Performance
}

internal sealed class BackupRestoreSelfTestRequest
{
    public BackupRestoreSelfTestMode Mode { get; set; }
    public IReadOnlyList<int> SizesMiB { get; set; } = new[] { 32, 128, 512 };
    public int Iterations { get; set; } = 5;
    public bool KeepDatabase { get; set; }
}

internal static class BackupRestoreSelfTest
{
    private const string Implementation = "wal_safe_sealed_candidate_v2";
    private const string ShopId = "perf-shop";
    private const string ShopCode = "PERF-SHOP";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static bool TryParse(string[] args, out BackupRestoreSelfTestRequest request)
    {
        request = new BackupRestoreSelfTestRequest();
        var found = false;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--backup-restore-selftest", StringComparison.OrdinalIgnoreCase))
            {
                request.Mode = BackupRestoreSelfTestMode.Functional;
                request.SizesMiB = new[] { 8 };
                request.Iterations = 1;
                found = true;
                continue;
            }

            if (string.Equals(argument, "--backup-restore-failure-selftest", StringComparison.OrdinalIgnoreCase))
            {
                request.Mode = BackupRestoreSelfTestMode.Failure;
                request.SizesMiB = new[] { 8 };
                request.Iterations = 1;
                found = true;
                continue;
            }

            if (string.Equals(argument, "--backup-restore-perf-selftest", StringComparison.OrdinalIgnoreCase))
            {
                request.Mode = BackupRestoreSelfTestMode.Performance;
                found = true;
                continue;
            }

            if (string.Equals(argument, "--sizes-mib", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                    throw new ArgumentException("--sizes-mib requires a comma-separated value.");
                request.SizesMiB = ParseSizes(args[++index]);
                continue;
            }

            if (string.Equals(argument, "--iterations", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length ||
                    !int.TryParse(args[++index], NumberStyles.Integer, Invariant, out var iterations) ||
                    iterations < 1 || iterations > 20)
                {
                    throw new ArgumentException("--iterations must be between 1 and 20.");
                }

                request.Iterations = iterations;
                continue;
            }

            if (string.Equals(argument, "--keepdb", StringComparison.OrdinalIgnoreCase))
                request.KeepDatabase = true;
        }

        return found;
    }

    public static async Task RunAsync(BackupRestoreSelfTestRequest request)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        SQLitePCL.Batteries_V2.Init();
        var root = Path.Combine(
            Path.GetTempPath(),
            "Win7POS.BackupRestore",
            Guid.NewGuid().ToString("N"));
        var previousDataDirectory = Environment.GetEnvironmentVariable("WIN7POS_DATA_DIR");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("WIN7POS_DATA_DIR", root);
        try
        {
            if (request.Mode == BackupRestoreSelfTestMode.Failure)
            {
                var failureSummary = await RunFailureMatrixAsync(root).ConfigureAwait(false);
                Console.WriteLine(
                    "BACKUP_RESTORE_RESULT mode=failure implementation=" + Implementation +
                    " result=pass restore_fault_points=" + failureSummary.RestoreFaultPoints.ToString(Invariant) +
                    " cancellation_points=" + failureSummary.CancellationPoints.ToString(Invariant) +
                    " crash_recovery_points=" + failureSummary.CrashRecoveryPoints.ToString(Invariant) +
                    " backup_fault_points=" + failureSummary.BackupFaultPoints.ToString(Invariant) +
                    " startup_recovery_points=" + failureSummary.StartupRecoveryPoints.ToString(Invariant) +
                    " partial_backup_residue=0 restore_temp_residue=0 retry=pass double_recovery=pass old_or_new_valid=true");
                Console.WriteLine("BACKUP RESTORE FAILURE SELFTEST PASS");
                Console.WriteLine("TEST PASS");
                return;
            }

            var allSamples = new List<IterationSample>();
            foreach (var sizeMiB in request.SizesMiB)
            {
                var profileRoot = Path.Combine(root, "profile-" + sizeMiB.ToString(Invariant));
                Directory.CreateDirectory(profileRoot);
                var seedPath = Path.Combine(profileRoot, "seed.db");
                await CreateSeedAsync(seedPath, sizeMiB).ConfigureAwait(false);
                var seedBytes = new FileInfo(seedPath).Length;
                Console.WriteLine(
                    "BACKUP_RESTORE_PERF kind=profile implementation=" + Implementation +
                    " size_mib=" + sizeMiB.ToString(Invariant) +
                    " db_bytes=" + seedBytes.ToString(Invariant) +
                    " warmup=1 iterations=" + request.Iterations.ToString(Invariant));

                var warmupRoot = Path.Combine(profileRoot, "warmup");
                await RunIterationAsync(seedPath, warmupRoot, sizeMiB, 0, measured: false)
                    .ConfigureAwait(false);
                DeleteDirectoryBestEffort(warmupRoot);

                for (var iteration = 1; iteration <= request.Iterations; iteration++)
                {
                    var iterationRoot = Path.Combine(profileRoot, "iteration-" + iteration.ToString(Invariant));
                    var sample = await RunIterationAsync(
                            seedPath,
                            iterationRoot,
                            sizeMiB,
                            iteration,
                            measured: true)
                        .ConfigureAwait(false);
                    allSamples.Add(sample);
                    if (!request.KeepDatabase)
                        DeleteDirectoryBestEffort(iterationRoot);
                }

                if (request.Mode == BackupRestoreSelfTestMode.Performance ||
                    request.Mode == BackupRestoreSelfTestMode.Functional)
                {
                    var walProbeRoot = Path.Combine(profileRoot, "wal-probe");
                    var walResult = await RunWalCommittedProbeAsync(seedPath, walProbeRoot)
                        .ConfigureAwait(false);
                    Console.WriteLine(
                        "BACKUP_RESTORE_RESULT mode=wal_committed implementation=" + Implementation +
                        " size_mib=" + sizeMiB.ToString(Invariant) +
                        " committed_frame_preserved=" + Bool(walResult.CommittedFramePreserved) +
                        " concurrent_writer_consistent=" + Bool(walResult.ConcurrentWriterConsistent) +
                        " writer_max_latency_ms=" + walResult.WriterMaximumLatencyMilliseconds.ToString(Invariant) +
                        " baseline_gap_closed=" + Bool(
                            walResult.CommittedFramePreserved && walResult.ConcurrentWriterConsistent));
                    if (!request.KeepDatabase)
                        DeleteDirectoryBestEffort(walProbeRoot);
                }

                WriteSummary(allSamples.Where(sample => sample.SizeMiB == sizeMiB).ToArray(), sizeMiB);
                if (!request.KeepDatabase)
                {
                    SqliteConnectionFactory.ClearAllPools();
                    DeleteFileBestEffort(seedPath);
                }
            }

            Console.WriteLine(
                "BACKUP_RESTORE_RESULT mode=" + request.Mode.ToString().ToLowerInvariant() +
                " implementation=" + Implementation +
                " result=pass profiles=" + request.SizesMiB.Count.ToString(Invariant) +
                " samples=" + allSamples.Count.ToString(Invariant));
            Console.WriteLine(request.Mode == BackupRestoreSelfTestMode.Performance
                ? "BACKUP RESTORE PERF SELFTEST PASS"
                : "BACKUP RESTORE SELFTEST PASS");
            Console.WriteLine("TEST PASS");
        }
        finally
        {
            Environment.SetEnvironmentVariable("WIN7POS_DATA_DIR", previousDataDirectory);
            SqliteConnectionFactory.ClearAllPools();
            if (!request.KeepDatabase)
                DeleteDirectoryBestEffort(root);
            else
                Console.WriteLine("BACKUP_RESTORE_RESULT kept=true root_name=" + Path.GetFileName(root));
        }
    }

    private static IReadOnlyList<int> ParseSizes(string value)
    {
        var sizes = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                if (!int.TryParse(part.Trim(), NumberStyles.Integer, Invariant, out var parsed) ||
                    parsed < 4 || parsed > 1024)
                {
                    throw new ArgumentException("Every --sizes-mib value must be between 4 and 1024.");
                }
                return parsed;
            })
            .Distinct()
            .ToArray();
        if (sizes.Length == 0)
            throw new ArgumentException("--sizes-mib must contain at least one size.");
        return sizes;
    }

    private static async Task CreateSeedAsync(string path, int requestedMiB)
    {
        var options = PosDbOptions.ForPath(path);
        DbInitializer.EnsureCreated(options);
        var factory = new SqliteConnectionFactory(options);
        await new ShopOfficialSnapshotRepository(factory).SaveAsync(new OfficialShopSnapshot
        {
            ShopCode = ShopCode,
            ShopId = ShopId,
            ShopName = "Performance fixture",
            Source = "backup_restore_perf"
        }).ConfigureAwait(false);
        await new CatalogShopStateRepository(factory)
            .EnsureAndLoadCursorAsync(ShopId, ShopCode)
            .ConfigureAwait(false);

        using (var connection = factory.Open())
        {
            await connection.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS backup_restore_perf_payload(
  id INTEGER PRIMARY KEY,
  payload BLOB NOT NULL);
CREATE TABLE IF NOT EXISTS backup_restore_perf_commits(
  id INTEGER PRIMARY KEY,
  value TEXT NOT NULL);
INSERT OR IGNORE INTO backup_restore_perf_commits(id, value)
VALUES(1, 'seed');").ConfigureAwait(false);
        }

        var targetBytes = requestedMiB * 1024L * 1024L;
        var nextId = 1L;
        while (new FileInfo(path).Length < targetBytes)
        {
            var remaining = targetBytes - new FileInfo(path).Length;
            var rows = (int)Math.Max(1, Math.Min(256, (remaining + 262143L) / 262144L));
            using (var connection = factory.Open())
            using (var transaction = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO backup_restore_perf_payload(id, payload)
VALUES(@id, zeroblob(262144));";
                var id = command.CreateParameter();
                id.ParameterName = "@id";
                command.Parameters.Add(id);
                for (var row = 0; row < rows; row++)
                {
                    id.Value = nextId++;
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                transaction.Commit();
            }
        }

        SqliteConnectionFactory.ClearAllPools();
        DeleteSqliteSidecars(path);
    }

    private static async Task<IterationSample> RunIterationAsync(
        string seedPath,
        string root,
        int sizeMiB,
        int iteration,
        bool measured)
    {
        Directory.CreateDirectory(root);
        var manualLivePath = Path.Combine(root, "manual-live.db");
        var manualBackupPath = Path.Combine(root, "manual-backup.db");
        CloneSeed(seedPath, manualLivePath);
        var sample = new IterationSample(sizeMiB, iteration);
        var manualFactory = new SqliteConnectionFactory(PosDbOptions.ForPath(manualLivePath));
        var writer = new WriterProbe(manualFactory);
        await writer.StartAsync().ConfigureAwait(false);
        try
        {
            await MeasureAsync(sample, "backup", "snapshot_validate_publish", async () =>
            {
                var validation = await new SqliteOnlineBackup(manualFactory)
                    .CreateVerifiedAsync(manualBackupPath)
                    .ConfigureAwait(false);
                Require(validation.IsValid, "Manual backup validation failed.");
            }).ConfigureAwait(false);
        }
        finally
        {
            await writer.StopAsync().ConfigureAwait(false);
        }
        sample.ConcurrentWriterMaxLatencyMs = writer.MaximumLatencyMilliseconds;
        sample.ManualBackupBytesWritten = File.Exists(manualBackupPath)
            ? new FileInfo(manualBackupPath).Length
            : 0;

        var sourcePath = Path.Combine(root, "restore-source.db");
        var livePath = Path.Combine(root, "live.db");
        var preBackupPath = Path.Combine(root, "pre-restore.db");
        CloneSeed(seedPath, sourcePath);
        CloneSeed(seedPath, livePath);
        var sourceFingerprint = await LogicalFingerprintAsync(sourcePath).ConfigureAwait(false);
        var initialLiveBytes = new FileInfo(livePath).Length;
        var totalAllocationBefore = GC.GetTotalAllocatedBytes(false);
        var totalStopwatch = Stopwatch.StartNew();

        await MeasureAsync(sample, "restore", "source_open_staging", () =>
        {
            Require(File.Exists(sourcePath), "Restore source is missing.");
            Require(!PathsEqual(sourcePath, livePath), "Restore source must differ from live.");
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        var liveFactory = new SqliteConnectionFactory(PosDbOptions.ForPath(livePath));
        var liveCatalogEpoch = await new CatalogShopStateRepository(liveFactory)
            .LoadTransitionEpochAsync()
            .ConfigureAwait(false);
        var phaseAllocationCheckpoint = GC.GetTotalAllocatedBytes(false);
        var coordinator = new SqliteRestoreCoordinator(
            liveFactory,
            new SqliteOnlineBackup(liveFactory),
            diagnostic =>
            {
                if (!string.Equals(diagnostic.Phase, "complete", StringComparison.Ordinal))
                {
                    var allocatedNow = GC.GetTotalAllocatedBytes(false);
                    sample.AddPhase(
                        "restore",
                        diagnostic.Phase,
                        diagnostic.ElapsedMilliseconds,
                        Math.Max(0, allocatedNow - phaseAllocationCheckpoint));
                    phaseAllocationCheckpoint = allocatedNow;
                }
            });
        var outcome = await coordinator.RestoreAsync(
                sourcePath,
                preBackupPath,
                ShopId,
                ShopCode,
                liveCatalogEpoch,
                validation =>
                {
                    Require(validation.IsValid, "Post-swap validation failed.");
                    return Task.CompletedTask;
                })
            .ConfigureAwait(false);
        Require(outcome.LiveValidation.IsValid, "Restore coordinator did not return valid evidence.");
        sample.SourceSnapshotBytes = new FileInfo(sourcePath).Length;
        sample.PreBackupBytes = new FileInfo(preBackupPath).Length;

        totalStopwatch.Stop();
        sample.RestoreTotalMilliseconds = totalStopwatch.Elapsed.TotalMilliseconds;
        sample.RestoreAllocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(false) - totalAllocationBefore);
        sample.FinalLiveBytes = new FileInfo(livePath).Length;
        sample.DatabaseGrowthBytes = sample.FinalLiveBytes - initialLiveBytes;
        sample.FinalWalBytes = LengthIfPresent(livePath + "-wal");
        sample.FinalShmBytes = LengthIfPresent(livePath + "-shm");
        sample.FinalJournalBytes = LengthIfPresent(livePath + "-journal");
        sample.ResultFingerprint = await LogicalFingerprintAsync(livePath).ConfigureAwait(false);
        sample.FingerprintMatch = string.Equals(
            sourceFingerprint,
            sample.ResultFingerprint,
            StringComparison.Ordinal);
        sample.EstimatedBytesWritten = sample.SourceSnapshotBytes + sample.PreBackupBytes;
        sample.EstimatedFullSizeReadPasses = 6;
        sample.EstimatedFullSizeWritePasses = 2;
        sample.CandidateCopyPasses = 1;
        Require(sample.FingerprintMatch, "Restored logical fingerprint differs from the source snapshot.");
        Require(sample.FinalWalBytes == 0 && sample.FinalShmBytes == 0 && sample.FinalJournalBytes == 0,
            "Restored live database retained a SQLite sidecar.");
        await AssertDeleteFullAsync(livePath).ConfigureAwait(false);

        if (measured)
            WriteIteration(sample);
        return sample;
    }

    private static async Task<WalProbeResult> RunWalCommittedProbeAsync(string seedPath, string root)
    {
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "wal-source.db");
        var livePath = Path.Combine(root, "live.db");
        var preBackupPath = Path.Combine(root, "pre-restore.db");
        CloneSeed(seedPath, sourcePath);
        CloneSeed(seedPath, livePath);
        using var source = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath,
                Cache = SqliteCacheMode.Private,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString() + ";Pooling=False");
        source.Open();
        source.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=0;");
        source.Execute("INSERT INTO backup_restore_perf_commits(id, value) VALUES(2, 'wal-committed');");
        source.Execute(@"
CREATE TABLE IF NOT EXISTS backup_restore_perf_wal_writes(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  value TEXT NOT NULL);");
        var stopWriter = 0;
        var writerCount = 0;
        var writerMaximumLatency = 0L;
        Exception? writerFailure = null;
        var writer = Task.Run(async () =>
        {
            try
            {
                using var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        Cache = SqliteCacheMode.Private,
                        DataSource = sourcePath,
                        Mode = SqliteOpenMode.ReadWrite,
                        Pooling = false
                    }.ToString() + ";Default Timeout=5");
                connection.Open();
                while (Volatile.Read(ref stopWriter) == 0)
                {
                    var stopwatch = Stopwatch.StartNew();
                    await connection.ExecuteAsync(
                            "INSERT INTO backup_restore_perf_wal_writes(value) VALUES('writer');")
                        .ConfigureAwait(false);
                    stopwatch.Stop();
                    UpdateMaximumValue(ref writerMaximumLatency, stopwatch.ElapsedMilliseconds);
                    Interlocked.Increment(ref writerCount);
                    await Task.Delay(1).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                writerFailure = ex;
            }
        });
        var writerDeadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref writerCount) < 3 && DateTime.UtcNow < writerDeadline)
            await Task.Delay(10).ConfigureAwait(false);
        Require(Volatile.Read(ref writerCount) >= 3, "WAL concurrent writer did not start.");
        var liveFactory = new SqliteConnectionFactory(PosDbOptions.ForPath(livePath));
        var epoch = await new CatalogShopStateRepository(liveFactory)
            .LoadTransitionEpochAsync()
            .ConfigureAwait(false);
        RestoreOperationResult outcome;
        try
        {
            outcome = await new SqliteRestoreCoordinator(
                    liveFactory,
                    new SqliteOnlineBackup(liveFactory))
                .RestoreAsync(
                    sourcePath,
                    preBackupPath,
                    ShopId,
                    ShopCode,
                    epoch,
                    _ => Task.CompletedTask)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref stopWriter, 1);
            await writer.ConfigureAwait(false);
        }
        Require(outcome.LiveValidation.IsValid, "WAL committed-frame restore was invalid.");
        Require(writerFailure == null, "WAL concurrent writer failed: " + writerFailure);
        Require(writerMaximumLatency < 5000, "WAL concurrent writer exceeded busy_timeout.");
        using var restored = liveFactory.Open();
        var committedFramePreserved = await restored.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM backup_restore_perf_commits WHERE id=2 AND value='wal-committed';")
            .ConfigureAwait(false) == 1;
        var restoredWrites = await restored.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM backup_restore_perf_wal_writes;")
            .ConfigureAwait(false);
        var sourceWrites = source.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM backup_restore_perf_wal_writes;");
        var writerConsistent = restoredWrites >= 1 && restoredWrites <= sourceWrites;
        Require(committedFramePreserved, "WAL committed frame was not preserved.");
        Require(writerConsistent, "WAL concurrent snapshot was inconsistent.");
        await AssertDeleteFullAsync(livePath).ConfigureAwait(false);
        return new WalProbeResult
        {
            CommittedFramePreserved = committedFramePreserved,
            ConcurrentWriterConsistent = writerConsistent,
            WriterMaximumLatencyMilliseconds = writerMaximumLatency
        };
    }

    private static async Task<FailureMatrixSummary> RunFailureMatrixAsync(string root)
    {
        var seedPath = Path.Combine(root, "failure-seed.db");
        await CreateSeedAsync(seedPath, 4).ConfigureAwait(false);
        var summary = new FailureMatrixSummary();
        var restoreFaultPoints = new[]
        {
            RestoreFailurePoint.DuringSourceSnapshotIdentityGuard,
            RestoreFailurePoint.AfterSourceSnapshot,
            RestoreFailurePoint.AfterCandidateMigration,
            RestoreFailurePoint.AfterCandidateIntegrityForeignKey,
            RestoreFailurePoint.AfterPreliminaryShopValidation,
            RestoreFailurePoint.WhileFenceWait,
            RestoreFailurePoint.AfterFencedLiveRevalidation,
            RestoreFailurePoint.AfterFencedCandidateRevalidation,
            RestoreFailurePoint.DuringVerifiedPreBackup,
            RestoreFailurePoint.AfterPreBackupBeforePrepared,
            RestoreFailurePoint.AfterPreparedBeforeSwap,
            RestoreFailurePoint.ImmediatelyAfterReplace,
            RestoreFailurePoint.DuringPostMigration,
            RestoreFailurePoint.DuringPostIntegrity,
            RestoreFailurePoint.DuringPostForeignKey,
            RestoreFailurePoint.BeforeCommitted,
            RestoreFailurePoint.AfterCommittedBeforeCleanup,
            RestoreFailurePoint.PartialCleanupFailure
        };

        foreach (var point in restoreFaultPoints)
        {
            using var fixture = await FailureFixture.CreateAsync(seedPath, root, "restore-" + point)
                .ConfigureAwait(false);
            var hooks = new BackupRestoreTestHooks
            {
                RestoreFault = observed =>
                {
                    if (observed == point)
                        throw new IOException("restore_fault_" + point);
                }
            };
            var error = await CaptureExceptionAsync(() =>
                    RestoreWithHooksAsync(fixture, hooks, "fault"))
                .ConfigureAwait(false);
            var completesDuringCleanup = point == RestoreFailurePoint.PartialCleanupFailure;
            Require(completesDuringCleanup ? error == null : error != null,
                "Restore fault point was not deterministically observed: " + point);
            var expectsNew = point == RestoreFailurePoint.AfterCommittedBeforeCleanup ||
                point == RestoreFailurePoint.PartialCleanupFailure;
            Require(string.Equals(ReadFailureValue(fixture.LivePath), expectsNew ? "new-live" : "old-live", StringComparison.Ordinal),
                "Restore fault left an unexpected live value: " + point);
            await AssertDeleteFullAsync(fixture.LivePath).ConfigureAwait(false);
            AssertRestoreResidue(fixture.RootPath, 0);
            AssertBackupPartialResidue(fixture.RootPath, 0);
            await AssertExistingPreBackupsValidAsync(fixture.RootPath).ConfigureAwait(false);

            var retry = await RestoreWithHooksAsync(fixture, null, "retry").ConfigureAwait(false);
            Require(retry.LiveValidation.IsValid, "Restore retry was invalid: " + point);
            Require(string.Equals(ReadFailureValue(fixture.LivePath), "new-live", StringComparison.Ordinal),
                "Restore retry did not install the source: " + point);
            AssertRestoreResidue(fixture.RootPath, 0);
            summary.RestoreFaultPoints++;
            Console.WriteLine(
                "BACKUP_RESTORE_RESULT mode=failure_case operation=restore point=" + point +
                " result=pass terminal=" + (expectsNew ? "new_valid" : "old_valid") +
                " retry=pass residue=0");
        }

        var cancellationCases = new[]
        {
            new CancellationCase("before", null, false),
            new CancellationCase("after_snapshot", RestoreFailurePoint.AfterSourceSnapshot, false),
            new CancellationCase("after_candidate_validation", RestoreFailurePoint.AfterPreliminaryShopValidation, false),
            new CancellationCase("fence_wait", RestoreFailurePoint.WhileFenceWait, false),
            new CancellationCase("after_prebackup", RestoreFailurePoint.AfterPreBackupBeforePrepared, false),
            new CancellationCase("after_prepared", RestoreFailurePoint.AfterPreparedBeforeSwap, true),
            new CancellationCase("after_swap", RestoreFailurePoint.ImmediatelyAfterReplace, true),
            new CancellationCase("during_post_swap", RestoreFailurePoint.DuringPostIntegrity, true)
        };
        foreach (var cancellationCase in cancellationCases)
        {
            using var fixture = await FailureFixture.CreateAsync(seedPath, root, "cancel-" + cancellationCase.Name)
                .ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            BackupRestoreTestHooks? hooks = null;
            if (cancellationCase.Point.HasValue)
            {
                hooks = new BackupRestoreTestHooks
                {
                    RestoreFault = observed =>
                    {
                        if (observed == cancellationCase.Point.Value)
                            cancellation.Cancel();
                    }
                };
            }
            else
            {
                cancellation.Cancel();
            }

            var error = await CaptureExceptionAsync(() =>
                    RestoreWithHooksAsync(fixture, hooks, "cancel", cancellation.Token))
                .ConfigureAwait(false);
            Require(error is OperationCanceledException,
                "Restore cancellation was not propagated consistently: " + cancellationCase.Name);
            Require(string.Equals(
                    ReadFailureValue(fixture.LivePath),
                    cancellationCase.ExpectsNew ? "new-live" : "old-live",
                    StringComparison.Ordinal),
                "Restore cancellation crossed an invalid atomic boundary: " + cancellationCase.Name);
            await AssertDeleteFullAsync(fixture.LivePath).ConfigureAwait(false);
            AssertRestoreResidue(fixture.RootPath, 0);
            var retry = await RestoreWithHooksAsync(fixture, null, "cancel-retry").ConfigureAwait(false);
            Require(retry.LiveValidation.IsValid, "Cancellation retry failed: " + cancellationCase.Name);
            AssertRestoreResidue(fixture.RootPath, 0);
            summary.CancellationPoints++;
            Console.WriteLine(
                "BACKUP_RESTORE_RESULT mode=cancellation_case operation=restore point=" + cancellationCase.Name +
                " result=pass terminal=" + (cancellationCase.ExpectsNew ? "new_valid" : "old_valid") +
                " retry=pass residue=0");
        }

        var crashCases = new[]
        {
            new CrashCase(RestoreFailurePoint.AfterPreparedBeforeSwap, "old-live"),
            new CrashCase(RestoreFailurePoint.ImmediatelyAfterReplace, "old-live"),
            new CrashCase(RestoreFailurePoint.AfterCommittedBeforeCleanup, "new-live")
        };
        foreach (var crashCase in crashCases)
        {
            using var fixture = await FailureFixture.CreateAsync(seedPath, root, "crash-" + crashCase.Point)
                .ConfigureAwait(false);
            var hooks = new BackupRestoreTestHooks
            {
                RestoreFault = observed =>
                {
                    if (observed == crashCase.Point)
                        throw new RestoreCrashSimulationException(crashCase.Point.ToString());
                }
            };
            var error = await CaptureExceptionAsync(() => RestoreWithHooksAsync(fixture, hooks, "crash"))
                .ConfigureAwait(false);
            Require(error is RestoreCrashSimulationException, "Crash point was not observed: " + crashCase.Point);
            Require(File.Exists(fixture.LivePath + ".restore-in-progress"), "Recoverable marker was not retained.");
            var installer = new AtomicRestoreInstaller();
            await installer.RecoverInterruptedInstallAsync(fixture.LivePath).ConfigureAwait(false);
            await installer.RecoverInterruptedInstallAsync(fixture.LivePath).ConfigureAwait(false);
            Require(string.Equals(ReadFailureValue(fixture.LivePath), crashCase.ExpectedValue, StringComparison.Ordinal),
                "Crash recovery selected the wrong terminal database: " + crashCase.Point);
            await AssertDeleteFullAsync(fixture.LivePath).ConfigureAwait(false);
            AssertRestoreResidue(fixture.RootPath, 0);
            summary.CrashRecoveryPoints++;
            Console.WriteLine(
                "BACKUP_RESTORE_RESULT mode=recovery_case point=" + crashCase.Point +
                " result=pass terminal=" + (crashCase.ExpectedValue == "new-live" ? "new_valid" : "old_valid") +
                " double_recovery=pass residue=0");
        }

        using (var fixture = await FailureFixture.CreateAsync(seedPath, root, "startup-recovery").ConfigureAwait(false))
        {
            WriteMarker(
                fixture.LivePath + ".restore-in-progress",
                "prepared",
                "r-cafebabe.db",
                "r-deadbeef.old");
            var calls = 0;
            var hooks = new BackupRestoreTestHooks
            {
                RestoreFault = observed =>
                {
                    if (observed == RestoreFailurePoint.StartupRecovery)
                        calls++;
                }
            };
            var installer = new AtomicRestoreInstaller(null, hooks, "cli-recovery", null);
            await installer.RecoverInterruptedInstallAsync(fixture.LivePath).ConfigureAwait(false);
            await installer.RecoverInterruptedInstallAsync(fixture.LivePath).ConfigureAwait(false);
            Require(calls == 2, "Startup recovery hook was not invoked idempotently.");
            Require(string.Equals(ReadFailureValue(fixture.LivePath), "old-live", StringComparison.Ordinal),
                "Prepared startup recovery changed a valid old live database.");
            AssertRestoreResidue(fixture.RootPath, 0);
            summary.StartupRecoveryPoints++;
        }

        await RunBackupFailureMatrixAsync(seedPath, root, summary).ConfigureAwait(false);
        return summary;
    }

    private static async Task RunBackupFailureMatrixAsync(
        string seedPath,
        string root,
        FailureMatrixSummary summary)
    {
        var injectedPoints = new[]
        {
            BackupFailurePoint.BeforeSourceOpen,
            BackupFailurePoint.AfterTemporarySnapshotCreation,
            BackupFailurePoint.AfterSnapshotBeforeValidation,
            BackupFailurePoint.AfterIntegrityForeignKey,
            BackupFailurePoint.BeforePublish,
            BackupFailurePoint.PublishError,
            BackupFailurePoint.SourceRemovedOrLocked
        };
        foreach (var point in injectedPoints)
        {
            using var fixture = await FailureFixture.CreateAsync(seedPath, root, "backup-" + point)
                .ConfigureAwait(false);
            var finalPath = Path.Combine(fixture.RootPath, "backup.db");
            var hooks = new BackupRestoreTestHooks
            {
                BackupFault = observed =>
                {
                    if (observed == point)
                        throw new IOException("backup_fault_" + point);
                }
            };
            await AssertThrowsAsync<IOException>(() =>
                    new SqliteOnlineBackup(fixture.LiveFactory, null, hooks)
                        .CreateVerifiedAsync(finalPath))
                .ConfigureAwait(false);
            Require(!File.Exists(finalPath), "Failed backup published a final file: " + point);
            AssertBackupPartialResidue(fixture.RootPath, 0);
            var retryPath = Path.Combine(fixture.RootPath, "backup-retry.db");
            var retry = await new SqliteOnlineBackup(fixture.LiveFactory)
                .CreateVerifiedAsync(retryPath)
                .ConfigureAwait(false);
            Require(retry.IsValid, "Backup retry failed: " + point);
            await AssertDeleteFullAsync(retryPath).ConfigureAwait(false);
            summary.BackupFaultPoints++;
            Console.WriteLine(
                "BACKUP_RESTORE_RESULT mode=failure_case operation=backup point=" + point +
                " result=pass final_published=false retry=pass residue=0");
        }

        using (var fixture = await FailureFixture.CreateAsync(seedPath, root, "backup-collision").ConfigureAwait(false))
        {
            const string token = "fixedtoken";
            var finalPath = Path.Combine(fixture.RootPath, "collision.db");
            var intentionalPartial = finalPath + ".partial-" + token;
            File.WriteAllText(intentionalPartial, "collision");
            var collisions = 0;
            var hooks = new BackupRestoreTestHooks
            {
                TemporaryTokenFactory = () => token,
                BackupFault = point =>
                {
                    if (point == BackupFailurePoint.Collision)
                        collisions++;
                }
            };
            await AssertThrowsAsync<IOException>(() =>
                    new SqliteOnlineBackup(fixture.LiveFactory, null, hooks).CreateVerifiedAsync(finalPath))
                .ConfigureAwait(false);
            Require(collisions == 16 && !File.Exists(finalPath), "Backup collision policy was not fail-closed.");
            File.Delete(intentionalPartial);
            AssertBackupPartialResidue(fixture.RootPath, 0);
            summary.BackupFaultPoints++;
        }

        using (var fixture = await FailureFixture.CreateAsync(seedPath, root, "backup-unwritable").ConfigureAwait(false))
        {
            var finalPath = Path.Combine(fixture.RootPath, "denied", "backup.db");
            var hooks = new BackupRestoreTestHooks
            {
                BackupFault = point =>
                {
                    if (point == BackupFailurePoint.UnwritableDestination)
                        throw new UnauthorizedAccessException("denied-test");
                }
            };
            await AssertThrowsAsync<UnauthorizedAccessException>(() =>
                    new SqliteOnlineBackup(fixture.LiveFactory, null, hooks).CreateVerifiedAsync(finalPath))
                .ConfigureAwait(false);
            Require(!File.Exists(finalPath), "Unwritable destination published a file.");
            summary.BackupFaultPoints++;
        }

        using (var fixture = await FailureFixture.CreateAsync(seedPath, root, "backup-cleanup").ConfigureAwait(false))
        {
            var finalPath = Path.Combine(fixture.RootPath, "cleanup.db");
            var hooks = new BackupRestoreTestHooks
            {
                BackupFault = point =>
                {
                    if (point == BackupFailurePoint.AfterSnapshotBeforeValidation ||
                        point == BackupFailurePoint.CleanupFailure)
                    {
                        throw new IOException("cleanup-test");
                    }
                }
            };
            await AssertThrowsAsync<IOException>(() =>
                    new SqliteOnlineBackup(fixture.LiveFactory, null, hooks).CreateVerifiedAsync(finalPath))
                .ConfigureAwait(false);
            Require(!File.Exists(finalPath), "Cleanup fault published a final backup.");
            AssertBackupPartialResidue(fixture.RootPath, 0);
            summary.BackupFaultPoints++;
        }

        using (var fixture = await FailureFixture.CreateAsync(seedPath, root, "backup-cancel").ConfigureAwait(false))
        using (var cancellation = new CancellationTokenSource())
        {
            var finalPath = Path.Combine(fixture.RootPath, "cancel.db");
            var hooks = new BackupRestoreTestHooks
            {
                BackupFault = point =>
                {
                    if (point == BackupFailurePoint.AfterTemporarySnapshotCreation)
                        cancellation.Cancel();
                }
            };
            await AssertThrowsAsync<OperationCanceledException>(() =>
                    new SqliteOnlineBackup(fixture.LiveFactory, null, hooks)
                        .CreateVerifiedAsync(finalPath, cancellation.Token))
                .ConfigureAwait(false);
            Require(!File.Exists(finalPath), "Cancelled backup published a final file.");
            AssertBackupPartialResidue(fixture.RootPath, 0);
            summary.CancellationPoints++;
        }

        using (var fixture = await FailureFixture.CreateAsync(seedPath, root, "backup-destinations").ConfigureAwait(false))
        {
            await AssertThrowsAsync<InvalidOperationException>(() =>
                    new SqliteOnlineBackup(fixture.LiveFactory).CreateVerifiedAsync(fixture.LivePath))
                .ConfigureAwait(false);
            var existing = Path.Combine(fixture.RootPath, "existing.db");
            File.Copy(fixture.LivePath, existing);
            await AssertThrowsAsync<IOException>(() =>
                    new SqliteOnlineBackup(fixture.LiveFactory).CreateVerifiedAsync(existing))
                .ConfigureAwait(false);
        }
    }

    private static async Task<RestoreOperationResult> RestoreWithHooksAsync(
        FailureFixture fixture,
        BackupRestoreTestHooks? hooks,
        string suffix,
        CancellationToken cancellationToken = default(CancellationToken))
    {
        var preBackupPath = Path.Combine(
            fixture.RootPath,
            "pre-" + suffix + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".db");
        var backup = hooks == null
            ? new SqliteOnlineBackup(fixture.LiveFactory)
            : new SqliteOnlineBackup(fixture.LiveFactory, null, hooks);
        var coordinator = hooks == null
            ? new SqliteRestoreCoordinator(fixture.LiveFactory, backup)
            : new SqliteRestoreCoordinator(fixture.LiveFactory, backup, null, hooks);
        return await coordinator.RestoreAsync(
                fixture.SourcePath,
                preBackupPath,
                ShopId,
                ShopCode,
                fixture.LiveEpoch,
                validation =>
                {
                    Require(validation.IsValid, "Post-swap validation was invalid.");
                    return Task.CompletedTask;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Exception?> CaptureExceptionAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task AssertExistingPreBackupsValidAsync(string root)
    {
        foreach (var path in Directory.GetFiles(root, "pre-*.db"))
            await AssertDeleteFullAsync(path).ConfigureAwait(false);
    }

    private static void AssertRestoreResidue(string root, int expectedCount)
    {
        var residue = Directory.GetFiles(root)
            .Where(path =>
                Path.GetFileName(path).StartsWith("r-", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".restore-in-progress", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Contains(".restore-in-progress.tmp-", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Require(residue.Length == expectedCount, "Unexpected restore residue: " +
            string.Join(",", residue.Select(Path.GetFileName)));
    }

    private static void AssertBackupPartialResidue(string root, int expectedCount)
    {
        var residue = Directory.GetFiles(root, "*.partial-*");
        Require(residue.Length == expectedCount, "Unexpected backup partial residue: " +
            string.Join(",", residue.Select(Path.GetFileName)));
    }

    private static string ReadFailureValue(string path)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                Cache = SqliteCacheMode.Private,
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
        connection.Open();
        return connection.ExecuteScalar<string>(
            "SELECT value FROM backup_restore_perf_commits WHERE id=1;") ?? string.Empty;
    }

    private static async Task AssertThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException("Expected exception " + typeof(T).Name + " was not thrown.");
    }

    private static async Task MeasureAsync(
        IterationSample sample,
        string operation,
        string phase,
        Func<Task> action)
    {
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var stopwatch = Stopwatch.StartNew();
        await action().ConfigureAwait(false);
        stopwatch.Stop();
        sample.AddPhase(
            operation,
            phase,
            stopwatch.Elapsed.TotalMilliseconds,
            Math.Max(0, GC.GetTotalAllocatedBytes(false) - allocatedBefore));
    }

    private static void WriteIteration(IterationSample sample)
    {
        foreach (var phase in sample.Phases)
        {
            Console.WriteLine(
                "BACKUP_RESTORE_PHASE implementation=" + Implementation +
                " size_mib=" + sample.SizeMiB.ToString(Invariant) +
                " iteration=" + sample.Iteration.ToString(Invariant) +
                " operation=" + phase.Operation +
                " phase=" + phase.Name +
                " elapsed_ms=" + Format(phase.ElapsedMilliseconds) +
                " allocated_bytes=" + phase.AllocatedBytes.ToString(Invariant));
        }

        Console.WriteLine(
            "BACKUP_RESTORE_RESULT mode=iteration implementation=" + Implementation +
            " size_mib=" + sample.SizeMiB.ToString(Invariant) +
            " iteration=" + sample.Iteration.ToString(Invariant) +
            " restore_total_ms=" + Format(sample.RestoreTotalMilliseconds) +
            " restore_allocated_bytes=" + sample.RestoreAllocatedBytes.ToString(Invariant) +
            " backup_bytes_written=" + sample.ManualBackupBytesWritten.ToString(Invariant) +
            " restore_bytes_written_estimated=" + sample.EstimatedBytesWritten.ToString(Invariant) +
            " read_passes_estimated=" + sample.EstimatedFullSizeReadPasses.ToString(Invariant) +
            " write_passes_estimated=" + sample.EstimatedFullSizeWritePasses.ToString(Invariant) +
            " candidate_copy_passes=" + sample.CandidateCopyPasses.ToString(Invariant) +
            " writer_max_latency_ms=" + sample.ConcurrentWriterMaxLatencyMs.ToString(Invariant) +
            " db_growth_bytes=" + sample.DatabaseGrowthBytes.ToString(Invariant) +
            " journal_bytes=" + sample.FinalJournalBytes.ToString(Invariant) +
            " wal_bytes=" + sample.FinalWalBytes.ToString(Invariant) +
            " shm_bytes=" + sample.FinalShmBytes.ToString(Invariant) +
            " fingerprint=" + sample.ResultFingerprint +
            " fingerprint_match=" + Bool(sample.FingerprintMatch));
    }

    private static void WriteSummary(IReadOnlyList<IterationSample> samples, int sizeMiB)
    {
        foreach (var group in samples.SelectMany(sample => sample.Phases)
                     .GroupBy(phase => phase.Operation + "/" + phase.Name, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var first = group.First();
            Console.WriteLine(
                "BACKUP_RESTORE_PERF kind=phase_median implementation=" + Implementation +
                " size_mib=" + sizeMiB.ToString(Invariant) +
                " operation=" + first.Operation +
                " phase=" + first.Name +
                " median_ms=" + Format(Median(group.Select(value => value.ElapsedMilliseconds))) +
                " median_allocated_bytes=" + Median(group.Select(value => value.AllocatedBytes)).ToString(Invariant));
        }

        Console.WriteLine(
            "BACKUP_RESTORE_PERF kind=profile_result implementation=" + Implementation +
            " size_mib=" + sizeMiB.ToString(Invariant) +
            " backup_total_median_ms=" + Format(Median(samples.Select(sample =>
                sample.Phases.Where(phase => phase.Operation == "backup").Sum(phase => phase.ElapsedMilliseconds)))) +
            " restore_total_median_ms=" + Format(Median(samples.Select(sample => sample.RestoreTotalMilliseconds))) +
            " restore_allocated_median_bytes=" + Median(samples.Select(sample => sample.RestoreAllocatedBytes)).ToString(Invariant) +
            " restore_bytes_written_median=" + Median(samples.Select(sample => sample.EstimatedBytesWritten)).ToString(Invariant) +
            " writer_max_latency_ms=" + samples.Max(sample => sample.ConcurrentWriterMaxLatencyMs).ToString(Invariant) +
            " fingerprint_match=" + Bool(samples.All(sample => sample.FingerprintMatch)));
    }

    private static async Task<string> LogicalFingerprintAsync(string path)
    {
        using var connection = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        var payloadRows = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM backup_restore_perf_payload;").ConfigureAwait(false);
        var payloadBytes = await connection.ExecuteScalarAsync<long>(
            "SELECT COALESCE(SUM(length(payload)), 0) FROM backup_restore_perf_payload;").ConfigureAwait(false);
        var committedRows = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM backup_restore_perf_commits;").ConfigureAwait(false);
        var value = payloadRows.ToString(Invariant) + ":" +
            payloadBytes.ToString(Invariant) + ":" + committedRows.ToString(Invariant);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)))
            .Replace("-", string.Empty)
            .ToLowerInvariant();
    }

    private static async Task AssertDeleteFullAsync(string path)
    {
        var factory = new SqliteConnectionFactory(PosDbOptions.ForPath(path));
        using var connection = factory.Open();
        var journal = await connection.ExecuteScalarAsync<string>("PRAGMA journal_mode;").ConfigureAwait(false);
        var synchronous = await connection.ExecuteScalarAsync<long>("PRAGMA synchronous;").ConfigureAwait(false);
        Require(string.Equals(journal, "delete", StringComparison.OrdinalIgnoreCase), "journal_mode is not DELETE.");
        Require(synchronous == 2, "synchronous is not FULL.");
    }

    private static void CloneSeed(string sourcePath, string destinationPath)
    {
        SqliteConnectionFactory.ClearAllPools();
        File.Copy(sourcePath, destinationPath, true);
        DeleteSqliteSidecars(destinationPath);
    }

    private static void CopyDurable(string sourcePath, string destinationPath)
    {
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65536,
            FileOptions.SequentialScan);
        using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            65536,
            FileOptions.WriteThrough);
        source.CopyTo(destination);
        destination.Flush(true);
    }

    private static void WriteMarker(
        string markerPath,
        string phase,
        string candidateFileName,
        string rollbackFileName)
    {
        var temporaryPath = markerPath + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.WriteLine("version=1");
                writer.WriteLine("phase=" + phase);
                writer.WriteLine("candidate=" + candidateFileName);
                writer.WriteLine("rollback=" + rollbackFileName);
                writer.Flush();
                stream.Flush(true);
            }

            if (File.Exists(markerPath))
                File.Replace(temporaryPath, markerPath, null);
            else
                File.Move(temporaryPath, markerPath);
        }
        finally
        {
            DeleteFileBestEffort(temporaryPath);
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static long LengthIfPresent(string path)
    {
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    private static void DeleteSqliteSidecars(string path)
    {
        DeleteFileBestEffort(path + "-journal");
        DeleteFileBestEffort(path + "-wal");
        DeleteFileBestEffort(path + "-shm");
    }

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        SqliteConnectionFactory.ClearAllPools();
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Format(double value) => value.ToString("0.000", Invariant);

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
            return 0;
        var midpoint = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[midpoint - 1] + ordered[midpoint]) / 2.0
            : ordered[midpoint];
    }

    private static long Median(IEnumerable<long> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
            return 0;
        var midpoint = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[midpoint - 1] + ordered[midpoint]) / 2
            : ordered[midpoint];
    }

    private static void UpdateMaximumValue(ref long target, long value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }

    private sealed class WalProbeResult
    {
        public bool CommittedFramePreserved { get; set; }
        public bool ConcurrentWriterConsistent { get; set; }
        public long WriterMaximumLatencyMilliseconds { get; set; }
    }

    private sealed class FailureMatrixSummary
    {
        public int BackupFaultPoints { get; set; }
        public int CancellationPoints { get; set; }
        public int CrashRecoveryPoints { get; set; }
        public int RestoreFaultPoints { get; set; }
        public int StartupRecoveryPoints { get; set; }
    }

    private sealed class CancellationCase
    {
        public CancellationCase(
            string name,
            RestoreFailurePoint? point,
            bool expectsNew)
        {
            Name = name;
            Point = point;
            ExpectsNew = expectsNew;
        }

        public bool ExpectsNew { get; }
        public string Name { get; }
        public RestoreFailurePoint? Point { get; }
    }

    private sealed class CrashCase
    {
        public CrashCase(RestoreFailurePoint point, string expectedValue)
        {
            Point = point;
            ExpectedValue = expectedValue;
        }

        public string ExpectedValue { get; }
        public RestoreFailurePoint Point { get; }
    }

    private sealed class FailureFixture : IDisposable
    {
        private FailureFixture(string rootPath)
        {
            RootPath = rootPath;
            LivePath = Path.Combine(rootPath, "live.db");
            SourcePath = Path.Combine(rootPath, "source.db");
            LiveFactory = new SqliteConnectionFactory(PosDbOptions.ForPath(LivePath));
        }

        public SqliteConnectionFactory LiveFactory { get; }
        public long LiveEpoch { get; private set; }
        public string LivePath { get; }
        public string RootPath { get; }
        public string SourcePath { get; }

        public static async Task<FailureFixture> CreateAsync(
            string seedPath,
            string parentRoot,
            string name)
        {
            var safeName = new string((name ?? "case")
                .Select(character => char.IsLetterOrDigit(character) ? character : '-')
                .ToArray());
            var rootPath = Path.Combine(
                parentRoot,
                safeName + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(rootPath);
            var fixture = new FailureFixture(rootPath);
            CloneSeed(seedPath, fixture.LivePath);
            CloneSeed(seedPath, fixture.SourcePath);
            using (var live = fixture.LiveFactory.Open())
            {
                await live.ExecuteAsync(
                        "UPDATE backup_restore_perf_commits SET value='old-live' WHERE id=1;")
                    .ConfigureAwait(false);
            }
            using (var source = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    Cache = SqliteCacheMode.Private,
                    DataSource = fixture.SourcePath,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false
                }.ToString()))
            {
                source.Open();
                await source.ExecuteAsync(
                        "UPDATE backup_restore_perf_commits SET value='new-live' WHERE id=1;")
                    .ConfigureAwait(false);
            }
            fixture.LiveEpoch = await new CatalogShopStateRepository(fixture.LiveFactory)
                .LoadTransitionEpochAsync()
                .ConfigureAwait(false);
            SqliteConnectionFactory.ClearAllPools();
            return fixture;
        }

        public void Dispose()
        {
            SqliteConnectionFactory.ClearAllPools();
            DeleteDirectoryBestEffort(RootPath);
        }
    }

    private sealed class WriterProbe
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private Task _task = Task.CompletedTask;
        private long _maximumLatencyMilliseconds;
        private int _startedWrites;
        private Exception? _failure;

        public WriterProbe(SqliteConnectionFactory factory)
        {
            _factory = factory;
        }

        public long MaximumLatencyMilliseconds => Volatile.Read(ref _maximumLatencyMilliseconds);

        public async Task StartAsync()
        {
            _task = Task.Run(async () =>
            {
                try
                {
                    using var connection = _factory.Open();
                    while (!_stop.IsCancellationRequested)
                    {
                        var sequence = Interlocked.Increment(ref _startedWrites);
                        var stopwatch = Stopwatch.StartNew();
                        await connection.ExecuteAsync(
                            "INSERT INTO audit_log(ts, action, details) VALUES(@ts, @action, 'perf-writer');",
                            new { ts = sequence + 1000L, action = "backup-perf-writer-" + sequence.ToString(Invariant) })
                            .ConfigureAwait(false);
                        stopwatch.Stop();
                        UpdateMaximum(ref _maximumLatencyMilliseconds, stopwatch.ElapsedMilliseconds);
                        await Task.Delay(1).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _failure = ex;
                }
            });

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref _startedWrites) < 3 && DateTime.UtcNow < deadline)
                await Task.Delay(10).ConfigureAwait(false);
            Require(Volatile.Read(ref _startedWrites) >= 3, "Concurrent writer did not start.");
        }

        public async Task StopAsync()
        {
            _stop.Cancel();
            await _task.ConfigureAwait(false);
            if (_failure != null)
                throw new InvalidOperationException("Concurrent writer failed.", _failure);
            Require(MaximumLatencyMilliseconds < 5000, "Concurrent writer exceeded busy_timeout.");
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
    }

    private sealed class IterationSample
    {
        public IterationSample(int sizeMiB, int iteration)
        {
            SizeMiB = sizeMiB;
            Iteration = iteration;
        }

        public int SizeMiB { get; }
        public int Iteration { get; }
        public List<PhaseSample> Phases { get; } = new List<PhaseSample>();
        public long CandidateDurableCopyBytes { get; set; }
        public int CandidateCopyPasses { get; set; }
        public long ConcurrentWriterMaxLatencyMs { get; set; }
        public long DatabaseGrowthBytes { get; set; }
        public long EstimatedBytesWritten { get; set; }
        public int EstimatedFullSizeReadPasses { get; set; }
        public int EstimatedFullSizeWritePasses { get; set; }
        public long FinalJournalBytes { get; set; }
        public long FinalLiveBytes { get; set; }
        public long FinalShmBytes { get; set; }
        public long FinalWalBytes { get; set; }
        public bool FingerprintMatch { get; set; }
        public long ManualBackupBytesWritten { get; set; }
        public long PreBackupBytes { get; set; }
        public string ResultFingerprint { get; set; } = string.Empty;
        public long RestoreAllocatedBytes { get; set; }
        public double RestoreTotalMilliseconds { get; set; }
        public long SourceSnapshotBytes { get; set; }

        public void AddPhase(
            string operation,
            string name,
            double elapsedMilliseconds,
            long allocatedBytes)
        {
            Phases.Add(new PhaseSample
            {
                AllocatedBytes = allocatedBytes,
                ElapsedMilliseconds = elapsedMilliseconds,
                Name = name,
                Operation = operation
            });
        }
    }

    private sealed class PhaseSample
    {
        public long AllocatedBytes { get; set; }
        public double ElapsedMilliseconds { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
    }
}
