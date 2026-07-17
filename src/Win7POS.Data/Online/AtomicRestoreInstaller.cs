using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Win7POS.Data.Online
{
    public sealed class AtomicRestoreInstaller
    {
        private const string MarkerSuffix = ".restore-in-progress";
        private const string MarkerVersion = "1";
        private const string PhasePrepared = "prepared";
        private const string PhaseCommitted = "committed";

        public Task InstallAsync(
            string validatedRestorePath,
            string liveDatabasePath,
            string rollbackDatabasePath,
            Func<Task> postSwapValidationAndCommit)
        {
            ValidateInstallArguments(
                validatedRestorePath,
                liveDatabasePath,
                rollbackDatabasePath,
                postSwapValidationAndCommit);

            return SqliteConnectionFactory.RunExclusiveMaintenanceAsync(() => InstallCoreAsync(
                Path.GetFullPath(validatedRestorePath),
                Path.GetFullPath(liveDatabasePath),
                postSwapValidationAndCommit));
        }

        public Task RecoverInterruptedInstallAsync(string liveDatabasePath)
        {
            if (string.IsNullOrWhiteSpace(liveDatabasePath))
                throw new ArgumentException("Live database path is required.", nameof(liveDatabasePath));

            var fullPath = Path.GetFullPath(liveDatabasePath);
            return SqliteConnectionFactory.RunExclusiveMaintenanceAsync(() =>
                RecoverInterruptedInstallCoreAsync(fullPath));
        }

        private static async Task InstallCoreAsync(
            string validatedRestorePath,
            string liveDatabasePath,
            Func<Task> postSwapValidationAndCommit)
        {
            await RecoverInterruptedInstallCoreAsync(liveDatabasePath).ConfigureAwait(false);
            if (File.Exists(GetMarkerPath(liveDatabasePath)))
            {
                throw new IOException(
                    "A previous committed restore is valid but its deferred cleanup is still incomplete.");
            }
            if (!File.Exists(liveDatabasePath))
                throw new FileNotFoundException("Live database file was not found.", liveDatabasePath);

            var liveDirectory = Path.GetDirectoryName(liveDatabasePath);
            if (string.IsNullOrWhiteSpace(liveDirectory))
                throw new InvalidOperationException("Live database directory is invalid.");

            var token = Guid.NewGuid().ToString("N").Substring(0, 8);
            var liveFileName = Path.GetFileName(liveDatabasePath);
            var candidateFileName = liveFileName + ".restore-" + token + ".new";
            var rollbackFileName = liveFileName + ".restore-" + token + ".old";
            var candidatePath = Path.Combine(liveDirectory, candidateFileName);
            var atomicRollbackPath = Path.Combine(liveDirectory, rollbackFileName);
            var markerPath = GetMarkerPath(liveDatabasePath);
            var marker = new RestoreMarker
            {
                CandidateFileName = candidateFileName,
                Phase = PhasePrepared,
                RollbackFileName = rollbackFileName
            };

            try
            {
                WriteMarker(markerPath, marker);
                CopyDurable(validatedRestorePath, candidatePath);

                SqliteConnectionFactory.ClearAllPools();
                DeleteSqliteSidecars(liveDatabasePath);
                File.Replace(candidatePath, liveDatabasePath, atomicRollbackPath);
                SqliteConnectionFactory.ClearAllPools();

                await postSwapValidationAndCommit().ConfigureAwait(false);

                marker.Phase = PhaseCommitted;
                WriteMarker(markerPath, marker);
                TryCleanupCommittedRestore(markerPath, candidatePath, atomicRollbackPath);
            }
            catch (Exception installException)
            {
                try
                {
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
                TryDeleteSqliteFiles(candidatePath);
            }
        }

        private static async Task RecoverInterruptedInstallCoreAsync(string liveDatabasePath)
        {
            var markerPath = GetMarkerPath(liveDatabasePath);
            if (!File.Exists(markerPath))
                return;

            var marker = ReadMarker(markerPath);
            var liveDirectory = Path.GetDirectoryName(liveDatabasePath);
            if (string.IsNullOrWhiteSpace(liveDirectory))
                throw new InvalidOperationException("Live database directory is invalid.");

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
                            "Committed restore marker exists but the live database is invalid and no rollback is available.");
                    }

                    await RestoreRollbackAsync(rollbackPath, liveDatabasePath).ConfigureAwait(false);
                }

                TryCleanupCommittedRestore(markerPath, candidatePath, rollbackPath);
                return;
            }

            if (!string.Equals(marker.Phase, PhasePrepared, StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported restore marker phase: " + marker.Phase);

            if (File.Exists(rollbackPath))
            {
                await RestoreRollbackAsync(rollbackPath, liveDatabasePath).ConfigureAwait(false);
            }
            else if (!File.Exists(liveDatabasePath))
            {
                throw new InvalidDataException(
                    "Prepared restore marker exists without either a live database or an atomic rollback file.");
            }
            else if (!await IsDatabaseValidAsync(liveDatabasePath).ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    "Prepared restore marker exists without a rollback and the live database is invalid.");
            }

            DeleteIfPresent(candidatePath);
            DeleteSqliteSidecars(candidatePath);
            DeleteSqliteSidecars(rollbackPath);
            DeleteIfPresent(markerPath);
        }

        private static async Task RestoreRollbackAsync(string rollbackPath, string liveDatabasePath)
        {
            if (!await IsDatabaseValidAsync(rollbackPath).ConfigureAwait(false))
                throw new InvalidDataException("The atomic rollback database failed integrity or foreign-key validation.");

            SqliteConnectionFactory.ClearAllPools();
            DeleteSqliteSidecars(liveDatabasePath);
            if (File.Exists(liveDatabasePath))
                File.Replace(rollbackPath, liveDatabasePath, null);
            else
                File.Move(rollbackPath, liveDatabasePath);
            SqliteConnectionFactory.ClearAllPools();
            DeleteSqliteSidecars(liveDatabasePath);

            if (!await IsDatabaseValidAsync(liveDatabasePath).ConfigureAwait(false))
                throw new InvalidDataException("The reinstated rollback database failed validation.");
        }

        private static async Task<bool> IsDatabaseValidAsync(string databasePath)
        {
            try
            {
                var factory = new SqliteConnectionFactory(PosDbOptions.ForPath(databasePath));
                var validation = await new Repositories.DbMaintenanceRepository(factory)
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
            string validatedRestorePath,
            string liveDatabasePath,
            string rollbackDatabasePath,
            Func<Task> postSwapValidationAndCommit)
        {
            if (string.IsNullOrWhiteSpace(validatedRestorePath))
                throw new ArgumentException("Validated restore path is required.", nameof(validatedRestorePath));
            if (string.IsNullOrWhiteSpace(liveDatabasePath))
                throw new ArgumentException("Live database path is required.", nameof(liveDatabasePath));
            if (string.IsNullOrWhiteSpace(rollbackDatabasePath))
                throw new ArgumentException("Rollback database path is required.", nameof(rollbackDatabasePath));
            if (postSwapValidationAndCommit == null)
                throw new ArgumentNullException(nameof(postSwapValidationAndCommit));
            if (!File.Exists(validatedRestorePath))
                throw new FileNotFoundException("Validated restore file was not found.", validatedRestorePath);
            if (!File.Exists(rollbackDatabasePath))
                throw new FileNotFoundException("Rollback database file was not found.", rollbackDatabasePath);
        }

        private static string GetMarkerPath(string liveDatabasePath)
        {
            return liveDatabasePath + MarkerSuffix;
        }

        private static string ResolveMarkerFile(string directory, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) ||
                !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
            {
                throw new InvalidDataException("Restore marker contains an unsafe file name.");
            }

            return Path.Combine(directory, fileName);
        }

        private static RestoreMarker ReadMarker(string markerPath)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(markerPath))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0)
                    throw new InvalidDataException("Restore marker is malformed.");
                values[line.Substring(0, separator)] = line.Substring(separator + 1);
            }

            if (!values.TryGetValue("version", out var version) ||
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

        private static void TryCleanupCommittedRestore(
            string markerPath,
            string candidatePath,
            string rollbackPath)
        {
            try
            {
                DeleteIfPresent(candidatePath);
                DeleteSqliteSidecars(candidatePath);
                DeleteIfPresent(rollbackPath);
                DeleteSqliteSidecars(rollbackPath);
                DeleteIfPresent(markerPath);
            }
            catch
            {
                // The committed marker intentionally remains whenever cleanup cannot finish.
                // Startup recovery validates the live DB before retrying this idempotent cleanup.
            }
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
                // Best-effort cleanup must not change the outcome of a committed restore.
            }
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        private sealed class RestoreMarker
        {
            public string CandidateFileName { get; set; } = string.Empty;
            public string Phase { get; set; } = string.Empty;
            public string RollbackFileName { get; set; } = string.Empty;
        }
    }
}
