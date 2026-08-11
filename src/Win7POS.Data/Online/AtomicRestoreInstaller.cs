using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Data.Backup;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Online
{
    public sealed class AtomicRestoreInstaller
    {
        private const string MarkerSuffix = ".restore-in-progress";
        private const string MarkerVersion = "1";
        private const string PhasePrepared = "prepared";
        private const string PhaseCommitted = "committed";

        private readonly Action<BackupRestoreDiagnostic> _diagnostics;
        private readonly SqliteSourceInspection _inspection;
        private readonly string _operationId;
        private readonly BackupRestoreTestHooks _testHooks;

        public AtomicRestoreInstaller()
            : this(null, null, string.Empty, null)
        {
        }

        internal AtomicRestoreInstaller(
            Action<BackupRestoreDiagnostic> diagnostics,
            BackupRestoreTestHooks testHooks,
            string operationId,
            SqliteSourceInspection inspection)
        {
            _diagnostics = diagnostics;
            _testHooks = testHooks;
            _operationId = string.IsNullOrWhiteSpace(operationId)
                ? Guid.NewGuid().ToString("N").Substring(0, 12)
                : operationId;
            _inspection = inspection ?? new SqliteSourceInspection();
        }

        internal async Task InstallAsync(
            string validatedRestorePath,
            string liveDatabasePath,
            string verifiedPreRestoreBackupPath,
            Func<Task> postSwapValidationAndCommit)
        {
            ValidateLegacyArguments(
                validatedRestorePath,
                liveDatabasePath,
                verifiedPreRestoreBackupPath,
                postSwapValidationAndCommit);

            var fullLivePath = Path.GetFullPath(liveDatabasePath);
            var candidatePath = AllocateLegacyCandidatePath(fullLivePath);
            SealedRestoreCandidate candidate = null;
            try
            {
                CopyDurable(Path.GetFullPath(validatedRestorePath), candidatePath);
                var seal = new FileStream(
                    candidatePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.SequentialScan);
                candidate = new SealedRestoreCandidate(
                    candidatePath,
                    seal,
                    seal.Length,
                    File.GetLastWriteTimeUtc(candidatePath));
                await InstallAsync(
                        candidate,
                        fullLivePath,
                        Path.GetFullPath(verifiedPreRestoreBackupPath),
                        _ => postSwapValidationAndCommit(),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                candidate?.Dispose();
            }
        }

        internal Task<DatabaseValidationResult> InstallAsync(
            SealedRestoreCandidate candidate,
            string liveDatabasePath,
            string verifiedPreRestoreBackupPath,
            Func<DatabaseValidationResult, Task> postSwapCommit,
            CancellationToken cancellationToken)
        {
            ValidateInstallArguments(
                candidate,
                liveDatabasePath,
                verifiedPreRestoreBackupPath,
                postSwapCommit);
            cancellationToken.ThrowIfCancellationRequested();

            return InstallWithinMaintenanceAsync(
                candidate,
                Path.GetFullPath(liveDatabasePath),
                Path.GetFullPath(verifiedPreRestoreBackupPath),
                postSwapCommit,
                cancellationToken);
        }

        private async Task<DatabaseValidationResult> InstallWithinMaintenanceAsync(
            SealedRestoreCandidate candidate,
            string liveDatabasePath,
            string verifiedPreRestoreBackupPath,
            Func<DatabaseValidationResult, Task> postSwapCommit,
            CancellationToken cancellationToken)
        {
            DatabaseValidationResult result = null;
            await SqliteConnectionFactory.RunExclusiveMaintenanceAsync(async () =>
            {
                result = await InstallCoreAsync(
                        candidate,
                        liveDatabasePath,
                        verifiedPreRestoreBackupPath,
                        postSwapCommit,
                        cancellationToken)
                    .ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            return result ?? throw new InvalidOperationException("Atomic restore did not produce validation evidence.");
        }

        public Task RecoverInterruptedInstallAsync(string liveDatabasePath)
        {
            return RecoverInterruptedInstallAsync(liveDatabasePath, CancellationToken.None);
        }

        public Task RecoverInterruptedInstallAsync(
            string liveDatabasePath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(liveDatabasePath))
                throw new ArgumentException("Live database path is required.", nameof(liveDatabasePath));

            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(liveDatabasePath);
            return SqliteConnectionFactory.RunExclusiveMaintenanceAsync(async () =>
            {
                _testHooks?.AtRestore(RestoreFailurePoint.StartupRecovery);
                await RecoverInterruptedInstallCoreAsync(fullPath).ConfigureAwait(false);
            }, cancellationToken);
        }

        private async Task<DatabaseValidationResult> InstallCoreAsync(
            SealedRestoreCandidate candidate,
            string liveDatabasePath,
            string verifiedPreRestoreBackupPath,
            Func<DatabaseValidationResult, Task> postSwapCommit,
            CancellationToken cancellationToken)
        {
            await RecoverInterruptedInstallCoreAsync(liveDatabasePath).ConfigureAwait(false);
            if (File.Exists(GetMarkerPath(liveDatabasePath)) || GetMarkerTemporaryFiles(liveDatabasePath).Length > 0)
            {
                throw new IOException(
                    "A previous restore is valid but its durable cleanup is still incomplete.");
            }
            if (!File.Exists(liveDatabasePath))
                throw new FileNotFoundException("Live database file was not found.");
            if (!File.Exists(verifiedPreRestoreBackupPath))
                throw new FileNotFoundException("Verified pre-restore backup file was not found.");

            cancellationToken.ThrowIfCancellationRequested();
            var liveDirectory = Path.GetDirectoryName(liveDatabasePath);
            if (string.IsNullOrWhiteSpace(liveDirectory))
                throw new InvalidOperationException("Live database directory is invalid.");

            var token = Guid.NewGuid().ToString("N").Substring(0, 8);
            var rollbackFileName = "r-" + token + ".old";
            var rollbackPath = Path.Combine(liveDirectory, rollbackFileName);
            if (File.Exists(rollbackPath))
                throw new IOException("Atomic rollback name collision.");
            var candidateFileName = candidate.FileName;
            var candidatePath = Path.Combine(liveDirectory, candidateFileName);
            var markerPath = GetMarkerPath(liveDatabasePath);
            var marker = new RestoreMarker
            {
                CandidateFileName = candidateFileName,
                Phase = PhasePrepared,
                RollbackFileName = rollbackFileName
            };
            var simulatedCrash = false;
            var committed = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var preparedWatch = Stopwatch.StartNew();
                WriteMarker(markerPath, marker);
                preparedWatch.Stop();
                Report(
                    "prepared_marker",
                    Path.GetFileName(liveDatabasePath),
                    preparedWatch.Elapsed.TotalMilliseconds,
                    cancellationToken,
                    "ok",
                    string.Empty);
                _testHooks?.AtRestore(RestoreFailurePoint.AfterPreparedBeforeSwap);

                var replaceWatch = Stopwatch.StartNew();
                candidatePath = candidate.ConsumeForAtomicReplace(liveDatabasePath);
                SqliteConnectionFactory.ClearAllPools();
                DeleteSqliteSidecars(liveDatabasePath);
                File.Replace(candidatePath, liveDatabasePath, rollbackPath);
                SqliteConnectionFactory.ClearAllPools();
                replaceWatch.Stop();
                Report(
                    "replace",
                    Path.GetFileName(liveDatabasePath),
                    replaceWatch.Elapsed.TotalMilliseconds,
                    cancellationToken,
                    "ok",
                    string.Empty);
                _testHooks?.AtRestore(RestoreFailurePoint.ImmediatelyAfterReplace);

                var migrationWatch = Stopwatch.StartNew();
                DbInitializer.EnsureCreated(PosDbOptions.ForPath(liveDatabasePath));
                migrationWatch.Stop();
                _testHooks?.AtRestore(RestoreFailurePoint.DuringPostMigration);
                Report(
                    "post_swap_migration",
                    Path.GetFileName(liveDatabasePath),
                    migrationWatch.Elapsed.TotalMilliseconds,
                    cancellationToken,
                    "ok",
                    string.Empty);

                var validationWatch = Stopwatch.StartNew();
                var liveValidation = await new DbMaintenanceRepository(
                        new SqliteConnectionFactory(PosDbOptions.ForPath(liveDatabasePath)))
                    .ValidateAsync(
                        phase =>
                        {
                            if (phase == DatabaseValidationPhase.Integrity)
                                _testHooks?.AtRestore(RestoreFailurePoint.DuringPostIntegrity);
                            else
                                _testHooks?.AtRestore(RestoreFailurePoint.DuringPostForeignKey);
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                validationWatch.Stop();
                if (!liveValidation.IsValid)
                    throw new InvalidDataException("Post-swap database failed integrity or foreign-key validation.");
                Report(
                    "post_swap_integrity_fk",
                    Path.GetFileName(liveDatabasePath),
                    validationWatch.Elapsed.TotalMilliseconds,
                    cancellationToken,
                    "ok",
                    string.Empty);

                await postSwapCommit(liveValidation).ConfigureAwait(false);
                _testHooks?.AtRestore(RestoreFailurePoint.BeforeCommitted);
                marker.Phase = PhaseCommitted;
                var committedWatch = Stopwatch.StartNew();
                WriteMarker(markerPath, marker);
                committed = true;
                committedWatch.Stop();
                Report(
                    "committed_marker",
                    Path.GetFileName(liveDatabasePath),
                    committedWatch.Elapsed.TotalMilliseconds,
                    cancellationToken,
                    "ok",
                    string.Empty);
                _testHooks?.AtRestore(RestoreFailurePoint.AfterCommittedBeforeCleanup);

                var cleanupWatch = Stopwatch.StartNew();
                var cleanupComplete = TryCleanupCommittedRestore(
                        markerPath,
                        candidatePath,
                        rollbackPath,
                        injectFailure: true);
                if (!cleanupComplete)
                {
                    cleanupComplete = TryCleanupCommittedRestore(
                        markerPath,
                        candidatePath,
                        rollbackPath,
                        injectFailure: false);
                }
                cleanupWatch.Stop();
                Report(
                    "committed_cleanup",
                    Path.GetFileName(liveDatabasePath),
                    cleanupWatch.Elapsed.TotalMilliseconds,
                    cancellationToken,
                    cleanupComplete ? "ok" : "cleanup_deferred",
                    cleanupComplete ? string.Empty : "startup_retry");

                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                return liveValidation;
            }
            catch (RestoreCrashSimulationException)
            {
                simulatedCrash = true;
                throw;
            }
            catch (Exception installException)
            {
                try
                {
                    candidate.ReleaseSealForRecovery();
                    SqliteConnectionFactory.ClearAllPools();
                    await RecoverInterruptedInstallCoreAsync(liveDatabasePath).ConfigureAwait(false);
                }
                catch (Exception recoveryException)
                {
                    throw new AggregateException(
                        "Restore failed and the pre-restore database could not be reinstated atomically.",
                        installException,
                        recoveryException);
                }

                throw;
            }
            finally
            {
                if (!simulatedCrash)
                    TryDeleteSqliteFiles(candidatePath);
                if (committed)
                    SqliteConnectionFactory.ClearAllPools();
            }
        }

        private async Task RecoverInterruptedInstallCoreAsync(string liveDatabasePath)
        {
            var markerPath = GetMarkerPath(liveDatabasePath);
            var markerTemporaries = GetMarkerTemporaryFiles(liveDatabasePath);
            if (!File.Exists(markerPath))
            {
                if (markerTemporaries.Length > 0)
                {
                    throw new InvalidDataException(
                        "A truncated restore marker exists without an authoritative durable marker.");
                }
                return;
            }

            var marker = ReadMarker(markerPath);
            var liveDirectory = Path.GetDirectoryName(liveDatabasePath);
            if (string.IsNullOrWhiteSpace(liveDirectory))
                throw new InvalidOperationException("Live database directory is invalid.");

            var liveFileName = Path.GetFileName(liveDatabasePath);
            if (!IsManagedCandidateFileName(liveFileName, marker.CandidateFileName) ||
                !IsManagedRollbackFileName(liveFileName, marker.RollbackFileName) ||
                string.Equals(
                    marker.CandidateFileName,
                    marker.RollbackFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Restore marker references a file outside the managed restore namespace.");
            }

            var candidatePath = ResolveMarkerFile(liveDirectory, marker.CandidateFileName);
            var rollbackPath = ResolveMarkerFile(liveDirectory, marker.RollbackFileName);
            SqliteConnectionFactory.ClearAllPools();

            if (string.Equals(marker.Phase, PhaseCommitted, StringComparison.Ordinal))
            {
                var liveValid = File.Exists(liveDatabasePath) &&
                    await IsDatabaseValidAsync(liveDatabasePath).ConfigureAwait(false);
                if (!liveValid)
                {
                    if (!File.Exists(rollbackPath))
                    {
                        throw new InvalidDataException(
                            "Committed restore marker exists but live is invalid and no rollback is available.");
                    }

                    await RestoreRollbackAsync(rollbackPath, liveDatabasePath).ConfigureAwait(false);
                    ReportRecovery(liveDatabasePath, "committed_corrupt_rollback", "recovered");
                }
                else
                {
                    ReportRecovery(liveDatabasePath, "committed_valid_cleanup", "recovered");
                }

                DeleteMarkerTemporaries(markerTemporaries);
                if (!TryCleanupCommittedRestore(
                        markerPath,
                        candidatePath,
                        rollbackPath,
                        injectFailure: false))
                {
                    return;
                }
                return;
            }

            if (!string.Equals(marker.Phase, PhasePrepared, StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported restore marker phase.");

            if (File.Exists(rollbackPath))
            {
                await RestoreRollbackAsync(rollbackPath, liveDatabasePath).ConfigureAwait(false);
                ReportRecovery(liveDatabasePath, "prepared_rollback", "recovered");
            }
            else if (!File.Exists(liveDatabasePath))
            {
                throw new InvalidDataException(
                    "Prepared restore marker has neither a live database nor an atomic rollback.");
            }
            else if (!await IsDatabaseValidAsync(liveDatabasePath).ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    "Prepared restore marker has no rollback and the live database is invalid.");
            }
            else
            {
                ReportRecovery(liveDatabasePath, "prepared_before_swap_cleanup", "recovered");
            }

            DeleteIfPresent(candidatePath);
            DeleteSqliteSidecars(candidatePath);
            DeleteSqliteSidecars(rollbackPath);
            DeleteMarkerTemporaries(markerTemporaries);
            DeleteIfPresent(markerPath);
        }

        private static async Task RestoreRollbackAsync(string rollbackPath, string liveDatabasePath)
        {
            if (!await IsDatabaseValidAsync(rollbackPath).ConfigureAwait(false))
                throw new InvalidDataException("Atomic rollback failed integrity or foreign-key validation.");

            SqliteConnectionFactory.ClearAllPools();
            DeleteSqliteSidecars(liveDatabasePath);
            if (File.Exists(liveDatabasePath))
                File.Replace(rollbackPath, liveDatabasePath, null);
            else
                File.Move(rollbackPath, liveDatabasePath);
            SqliteConnectionFactory.ClearAllPools();
            DeleteSqliteSidecars(liveDatabasePath);

            if (!await IsDatabaseValidAsync(liveDatabasePath).ConfigureAwait(false))
                throw new InvalidDataException("Reinstated rollback database failed validation.");
            DeleteSqliteSidecars(liveDatabasePath);
        }

        private static async Task<bool> IsDatabaseValidAsync(string databasePath)
        {
            try
            {
                var factory = new SqliteConnectionFactory(PosDbOptions.ForPath(databasePath));
                var validation = await new DbMaintenanceRepository(factory)
                    .ValidateAsync()
                    .ConfigureAwait(false);
                return validation.IsValid;
            }
            catch
            {
                return false;
            }
            finally
            {
                SqliteConnectionFactory.ClearAllPools();
            }
        }

        private static void ValidateInstallArguments(
            SealedRestoreCandidate candidate,
            string liveDatabasePath,
            string verifiedPreRestoreBackupPath,
            Func<DatabaseValidationResult, Task> postSwapCommit)
        {
            if (candidate == null)
                throw new ArgumentNullException(nameof(candidate));
            if (string.IsNullOrWhiteSpace(liveDatabasePath))
                throw new ArgumentException("Live database path is required.", nameof(liveDatabasePath));
            if (string.IsNullOrWhiteSpace(verifiedPreRestoreBackupPath))
                throw new ArgumentException("Verified pre-restore backup path is required.", nameof(verifiedPreRestoreBackupPath));
            if (postSwapCommit == null)
                throw new ArgumentNullException(nameof(postSwapCommit));
            if (!File.Exists(verifiedPreRestoreBackupPath))
                throw new FileNotFoundException("Verified pre-restore backup file was not found.");
        }

        private static void ValidateLegacyArguments(
            string validatedRestorePath,
            string liveDatabasePath,
            string verifiedPreRestoreBackupPath,
            Func<Task> postSwapValidationAndCommit)
        {
            if (string.IsNullOrWhiteSpace(validatedRestorePath))
                throw new ArgumentException("Validated restore path is required.", nameof(validatedRestorePath));
            if (!File.Exists(validatedRestorePath))
                throw new FileNotFoundException("Validated restore file was not found.");
            if (postSwapValidationAndCommit == null)
                throw new ArgumentNullException(nameof(postSwapValidationAndCommit));
            if (string.IsNullOrWhiteSpace(liveDatabasePath) ||
                string.IsNullOrWhiteSpace(verifiedPreRestoreBackupPath))
            {
                throw new ArgumentException("Live and verified backup paths are required.");
            }
            if (!File.Exists(verifiedPreRestoreBackupPath))
                throw new FileNotFoundException("Verified pre-restore backup file was not found.");
        }

        private static string GetMarkerPath(string liveDatabasePath)
        {
            return liveDatabasePath + MarkerSuffix;
        }

        private static string[] GetMarkerTemporaryFiles(string liveDatabasePath)
        {
            var directory = Path.GetDirectoryName(liveDatabasePath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return Array.Empty<string>();
            return Directory.GetFiles(
                directory,
                Path.GetFileName(GetMarkerPath(liveDatabasePath)) + ".tmp-*");
        }

        private static string ResolveMarkerFile(string directory, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) ||
                Path.IsPathRooted(fileName) ||
                !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
                string.Equals(fileName, ".", StringComparison.Ordinal) ||
                string.Equals(fileName, "..", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Restore marker contains an unsafe file name.");
            }

            var path = Path.GetFullPath(Path.Combine(directory, fileName));
            if (!string.Equals(
                    Path.GetDirectoryName(path),
                    Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Restore marker resolves outside the live database directory.");
            }
            return path;
        }

        private static bool IsManagedCandidateFileName(string liveFileName, string fileName)
        {
            return IsShortManagedName(fileName, ".db") ||
                IsLegacyManagedName(liveFileName, fileName, ".new");
        }

        private static bool IsManagedRollbackFileName(string liveFileName, string fileName)
        {
            return IsShortManagedName(fileName, ".old") ||
                IsLegacyManagedName(liveFileName, fileName, ".old");
        }

        private static bool IsShortManagedName(string fileName, string suffix)
        {
            if (string.IsNullOrWhiteSpace(fileName) ||
                !fileName.StartsWith("r-", StringComparison.Ordinal) ||
                !fileName.EndsWith(suffix, StringComparison.Ordinal))
            {
                return false;
            }

            var tokenLength = fileName.Length - 2 - suffix.Length;
            return tokenLength >= 8 && tokenLength <= 20 &&
                IsLowerHex(fileName, 2, tokenLength);
        }

        private static bool IsLegacyManagedName(
            string liveFileName,
            string fileName,
            string suffix)
        {
            var prefix = (liveFileName ?? string.Empty) + ".restore-";
            if (string.IsNullOrWhiteSpace(fileName) ||
                !fileName.StartsWith(prefix, StringComparison.Ordinal) ||
                !fileName.EndsWith(suffix, StringComparison.Ordinal))
            {
                return false;
            }

            var tokenLength = fileName.Length - prefix.Length - suffix.Length;
            return tokenLength == 8 && IsLowerHex(fileName, prefix.Length, tokenLength);
        }

        private static bool IsLowerHex(string value, int offset, int length)
        {
            for (var index = offset; index < offset + length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        private static RestoreMarker ReadMarker(string markerPath)
        {
            var markerInfo = new FileInfo(markerPath);
            if (markerInfo.Length <= 0 || markerInfo.Length > 4096)
                throw new InvalidDataException("Restore marker is truncated or oversized.");

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(markerPath))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0)
                    throw new InvalidDataException("Restore marker is malformed.");
                var key = line.Substring(0, separator);
                if (values.ContainsKey(key))
                    throw new InvalidDataException("Restore marker contains a duplicate field.");
                values[key] = line.Substring(separator + 1);
            }

            if (values.Count != 4 ||
                !values.TryGetValue("version", out var version) ||
                !string.Equals(version, MarkerVersion, StringComparison.Ordinal) ||
                !values.TryGetValue("phase", out var phase) ||
                !values.TryGetValue("candidate", out var candidate) ||
                !values.TryGetValue("rollback", out var rollback))
            {
                throw new InvalidDataException("Restore marker is incomplete or unsupported.");
            }

            return new RestoreMarker
            {
                CandidateFileName = candidate,
                Phase = phase,
                RollbackFileName = rollback
            };
        }

        private static void WriteMarker(string markerPath, RestoreMarker marker)
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
                    writer.WriteLine("version=" + MarkerVersion);
                    writer.WriteLine("phase=" + marker.Phase);
                    writer.WriteLine("candidate=" + marker.CandidateFileName);
                    writer.WriteLine("rollback=" + marker.RollbackFileName);
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
                DeleteIfPresent(temporaryPath);
            }
        }

        private bool TryCleanupCommittedRestore(
            string markerPath,
            string candidatePath,
            string rollbackPath,
            bool injectFailure)
        {
            try
            {
                DeleteIfPresent(candidatePath);
                DeleteSqliteSidecars(candidatePath);
                if (injectFailure)
                    _testHooks?.AtRestore(RestoreFailurePoint.PartialCleanupFailure);
                DeleteIfPresent(rollbackPath);
                DeleteSqliteSidecars(rollbackPath);
                DeleteIfPresent(markerPath);
                return true;
            }
            catch
            {
                // The committed marker intentionally remains whenever cleanup cannot
                // finish. Startup recovery validates live before retrying this cleanup.
                return false;
            }
        }

        private static string AllocateLegacyCandidatePath(string liveDatabasePath)
        {
            var directory = Path.GetDirectoryName(liveDatabasePath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Live database directory is invalid.");
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var path = Path.Combine(
                    directory,
                    "r-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".db");
                if (!File.Exists(path))
                    return path;
            }
            throw new IOException("Unable to allocate a legacy restore candidate.");
        }

        private static void CopyDurable(string sourcePath, string destinationPath)
        {
            using (var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                65536,
                FileOptions.SequentialScan))
            using (var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                65536,
                FileOptions.WriteThrough))
            {
                source.CopyTo(destination);
                destination.Flush(true);
            }
        }

        private static void DeleteSqliteSidecars(string databasePath)
        {
            DeleteIfPresent(databasePath + "-journal");
            DeleteIfPresent(databasePath + "-wal");
            DeleteIfPresent(databasePath + "-shm");
        }

        private static void TryDeleteSqliteFiles(string databasePath)
        {
            try
            {
                DeleteIfPresent(databasePath);
                DeleteSqliteSidecars(databasePath);
            }
            catch
            {
            }
        }

        private static void DeleteMarkerTemporaries(IEnumerable<string> paths)
        {
            foreach (var path in paths ?? Enumerable.Empty<string>())
                DeleteIfPresent(path);
        }

        private static void DeleteIfPresent(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }

        private void Report(
            string phase,
            string fileName,
            double elapsedMilliseconds,
            CancellationToken cancellationToken,
            string resultCode,
            string recoveryAction)
        {
            try
            {
                _diagnostics?.Invoke(new BackupRestoreDiagnostic
                {
                    CancellationRequested = cancellationToken.IsCancellationRequested,
                    DatabaseBytes = _inspection.DatabaseBytes,
                    ElapsedMilliseconds = elapsedMilliseconds,
                    FileName = fileName,
                    Operation = "restore",
                    OperationId = _operationId,
                    Phase = phase,
                    RecoveryAction = recoveryAction,
                    ResultCode = resultCode,
                    ShmBytes = _inspection.ShmBytes,
                    ShmPresent = _inspection.ShmPresent,
                    SourceKind = _inspection.Kind,
                    WalBytes = _inspection.WalBytes,
                    WalPresent = _inspection.WalPresent
                });
            }
            catch
            {
                // Diagnostics are observational and cannot control recovery.
            }
        }

        private void ReportRecovery(string liveDatabasePath, string action, string resultCode)
        {
            Report(
                "startup_recovery",
                Path.GetFileName(liveDatabasePath),
                0,
                CancellationToken.None,
                resultCode,
                action);
        }

        private sealed class RestoreMarker
        {
            public string CandidateFileName { get; set; } = string.Empty;
            public string Phase { get; set; } = string.Empty;
            public string RollbackFileName { get; set; } = string.Empty;
        }
    }
}
