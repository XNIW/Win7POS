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
    private const string Implementation = "baseline_raw_copy_v1";
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
                await RunBaselineFailureSmokeAsync(root).ConfigureAwait(false);
                Console.WriteLine("BACKUP_RESTORE_RESULT mode=failure implementation=" + Implementation + " result=pass cases=3");
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

                if (request.Mode == BackupRestoreSelfTestMode.Performance)
                {
                    var walProbeRoot = Path.Combine(profileRoot, "wal-probe");
                    var walResult = await RunWalCommittedProbeAsync(seedPath, walProbeRoot)
                        .ConfigureAwait(false);
                    Console.WriteLine(
                        "BACKUP_RESTORE_RESULT mode=wal_committed implementation=" + Implementation +
                        " size_mib=" + sizeMiB.ToString(Invariant) +
                        " committed_frame_preserved=" + Bool(walResult) +
                        " expected_baseline_gap=true");
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
        var validationPath = Path.Combine(root, "pos_restore_validate.db");
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

        await MeasureAsync(sample, "restore", "snapshot", () =>
        {
            File.Copy(sourcePath, validationPath, true);
            sample.SourceSnapshotBytes = new FileInfo(validationPath).Length;
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        await MeasureAsync(sample, "restore", "candidate_migration", () =>
        {
            DbInitializer.EnsureCreated(PosDbOptions.ForPath(validationPath));
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        var candidateFactory = new SqliteConnectionFactory(PosDbOptions.ForPath(validationPath));
        await MeasureAsync(sample, "restore", "candidate_integrity_fk", async () =>
        {
            var validation = await new DbMaintenanceRepository(candidateFactory).ValidateAsync().ConfigureAwait(false);
            Require(validation.IsValid, "Candidate integrity/FK validation failed.");
        }).ConfigureAwait(false);
        await MeasureAsync(sample, "restore", "preliminary_shop_validation", async () =>
        {
            var safety = await new RestoreShopSafetyRepository(candidateFactory)
                .ValidateCandidateAsync(ShopId, ShopCode)
                .ConfigureAwait(false);
            Require(safety.IsValid, "Candidate shop validation failed: " + safety.Code);
        }).ConfigureAwait(false);
        SqliteConnectionFactory.ClearAllPools();

        var liveFactory = new SqliteConnectionFactory(PosDbOptions.ForPath(livePath));
        var liveCatalogEpoch = await new CatalogShopStateRepository(liveFactory)
            .LoadTransitionEpochAsync()
            .ConfigureAwait(false);
        var fenceStart = Stopwatch.StartNew();
        var fenceAllocationBefore = GC.GetTotalAllocatedBytes(false);
        await SqliteConnectionFactory.RunExclusiveMaintenanceAsync(async () =>
        {
            fenceStart.Stop();
            sample.AddPhase(
                "restore",
                "fence_wait",
                fenceStart.Elapsed.TotalMilliseconds,
                Math.Max(0, GC.GetTotalAllocatedBytes(false) - fenceAllocationBefore));
            await MeasureAsync(sample, "restore", "fenced_live_revalidation", async () =>
            {
                var safety = await new RestoreShopSafetyRepository(liveFactory)
                    .ValidateLivePreSwapAsync(ShopId, ShopCode, liveCatalogEpoch)
                    .ConfigureAwait(false);
                Require(safety.IsValid, "Fenced live validation failed: " + safety.Code);
            }).ConfigureAwait(false);
            await MeasureAsync(sample, "restore", "fenced_candidate_revalidation", async () =>
            {
                var safety = await new RestoreShopSafetyRepository(candidateFactory)
                    .ValidateCandidateAsync(ShopId, ShopCode)
                    .ConfigureAwait(false);
                Require(safety.IsValid, "Fenced candidate validation failed: " + safety.Code);
            }).ConfigureAwait(false);
            await MeasureAsync(sample, "restore", "verified_prebackup", async () =>
            {
                var validation = await new SqliteOnlineBackup(liveFactory)
                    .CreateVerifiedAsync(preBackupPath)
                    .ConfigureAwait(false);
                Require(validation.IsValid, "Pre-restore backup validation failed.");
                sample.PreBackupBytes = new FileInfo(preBackupPath).Length;
            }).ConfigureAwait(false);
            await InstallBaselineAsync(sample, validationPath, livePath).ConfigureAwait(false);
        }).ConfigureAwait(false);

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
        sample.EstimatedBytesWritten =
            sample.SourceSnapshotBytes + sample.PreBackupBytes + sample.CandidateDurableCopyBytes;
        sample.EstimatedFullSizeReadPasses = 7;
        sample.EstimatedFullSizeWritePasses = 3;
        sample.CandidateCopyPasses = 2;
        Require(sample.FingerprintMatch, "Restored logical fingerprint differs from the source snapshot.");
        Require(sample.FinalWalBytes == 0 && sample.FinalShmBytes == 0 && sample.FinalJournalBytes == 0,
            "Restored live database retained a SQLite sidecar.");
        await AssertDeleteFullAsync(livePath).ConfigureAwait(false);

        if (measured)
            WriteIteration(sample);
        return sample;
    }

    private static async Task InstallBaselineAsync(
        IterationSample sample,
        string validatedRestorePath,
        string liveDatabasePath)
    {
        var liveDirectory = Path.GetDirectoryName(liveDatabasePath) ??
            throw new InvalidOperationException("Live directory is missing.");
        var token = Guid.NewGuid().ToString("N").Substring(0, 8);
        var liveFileName = Path.GetFileName(liveDatabasePath);
        var candidateFileName = liveFileName + ".restore-" + token + ".new";
        var rollbackFileName = liveFileName + ".restore-" + token + ".old";
        var candidatePath = Path.Combine(liveDirectory, candidateFileName);
        var rollbackPath = Path.Combine(liveDirectory, rollbackFileName);
        var markerPath = liveDatabasePath + ".restore-in-progress";

        await MeasureAsync(sample, "restore", "prepared_marker", () =>
        {
            WriteMarker(markerPath, "prepared", candidateFileName, rollbackFileName);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await MeasureAsync(sample, "restore", "candidate_durable_copy", () =>
        {
            CopyDurable(validatedRestorePath, candidatePath);
            sample.CandidateDurableCopyBytes = new FileInfo(candidatePath).Length;
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await MeasureAsync(sample, "restore", "replace", () =>
        {
            SqliteConnectionFactory.ClearAllPools();
            DeleteSqliteSidecars(liveDatabasePath);
            File.Replace(candidatePath, liveDatabasePath, rollbackPath);
            SqliteConnectionFactory.ClearAllPools();
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await MeasureAsync(sample, "restore", "post_swap_migration", () =>
        {
            DbInitializer.EnsureCreated(PosDbOptions.ForPath(liveDatabasePath));
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await MeasureAsync(sample, "restore", "post_swap_integrity_fk", async () =>
        {
            var validation = await new DbMaintenanceRepository(
                    new SqliteConnectionFactory(PosDbOptions.ForPath(liveDatabasePath)))
                .ValidateAsync()
                .ConfigureAwait(false);
            Require(validation.IsValid, "Post-swap integrity/FK validation failed.");
        }).ConfigureAwait(false);
        await MeasureAsync(sample, "restore", "committed_marker_cleanup", () =>
        {
            WriteMarker(markerPath, "committed", candidateFileName, rollbackFileName);
            DeleteFileBestEffort(candidatePath);
            DeleteSqliteSidecars(candidatePath);
            DeleteFileBestEffort(rollbackPath);
            DeleteSqliteSidecars(rollbackPath);
            DeleteFileBestEffort(markerPath);
            DeleteSqliteSidecars(liveDatabasePath);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        DeleteFileBestEffort(validatedRestorePath);
        DeleteSqliteSidecars(validatedRestorePath);
    }

    private static async Task<bool> RunWalCommittedProbeAsync(string seedPath, string root)
    {
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "wal-source.db");
        var copiedPath = Path.Combine(root, "raw-copy.db");
        CloneSeed(seedPath, sourcePath);
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
        var sourceCount = source.ExecuteScalar<long>("SELECT COUNT(1) FROM backup_restore_perf_commits;");
        File.Copy(sourcePath, copiedPath, true);
        using var copied = new SqliteConnection("Data Source=" + copiedPath + ";Pooling=False");
        copied.Open();
        var copiedCount = await copied.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM backup_restore_perf_commits;").ConfigureAwait(false);
        return copiedCount == sourceCount;
    }

    private static async Task RunBaselineFailureSmokeAsync(string root)
    {
        var livePath = Path.Combine(root, "live.db");
        var destinationPath = Path.Combine(root, "backup.db");
        await CreateSeedAsync(livePath, 8).ConfigureAwait(false);
        var factory = new SqliteConnectionFactory(PosDbOptions.ForPath(livePath));
        await AssertThrowsAsync<InvalidOperationException>(() =>
            new SqliteOnlineBackup(factory).CreateVerifiedAsync(livePath)).ConfigureAwait(false);
        File.Copy(livePath, destinationPath);
        await AssertThrowsAsync<IOException>(() =>
            new SqliteOnlineBackup(factory).CreateVerifiedAsync(destinationPath)).ConfigureAwait(false);
        var corruptPath = Path.Combine(root, "corrupt.db");
        File.WriteAllBytes(corruptPath, new byte[] { 0, 1, 2, 3 });
        await AssertThrowsAsync<Exception>(async () =>
        {
            var corruptFactory = new SqliteConnectionFactory(PosDbOptions.ForPath(corruptPath));
            await new DbMaintenanceRepository(corruptFactory).ValidateAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
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
