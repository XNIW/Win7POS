using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Backup
{
    public sealed class SqliteOnlineBackup
    {
        private const int NativeSnapshotMaximumAttempts = 5;
        private const int NativeSnapshotRetryDelayMilliseconds = 25;
        private const int NativeSnapshotRetryWindowMilliseconds = 5000;

        private readonly Action<BackupRestoreDiagnostic> _diagnostics;
        private readonly SqliteConnectionFactory _sourceFactory;
        private readonly BackupRestoreTestHooks _testHooks;

        public SqliteOnlineBackup(SqliteConnectionFactory sourceFactory)
            : this(sourceFactory, null, null)
        {
        }

        public SqliteOnlineBackup(
            SqliteConnectionFactory sourceFactory,
            Action<BackupRestoreDiagnostic> diagnostics)
            : this(sourceFactory, diagnostics, null)
        {
        }

        internal SqliteOnlineBackup(
            SqliteConnectionFactory sourceFactory,
            Action<BackupRestoreDiagnostic> diagnostics,
            BackupRestoreTestHooks testHooks)
        {
            _sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
            _diagnostics = diagnostics;
            _testHooks = testHooks;
        }

        public Task<DatabaseValidationResult> CreateVerifiedAsync(string destinationPath)
        {
            return CreateVerifiedAsync(destinationPath, CancellationToken.None);
        }

        public async Task<DatabaseValidationResult> CreateVerifiedAsync(
            string destinationPath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("Backup destination path is required.", nameof(destinationPath));

            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = Path.GetFullPath(_sourceFactory.DbPath);
            var operationId = Guid.NewGuid().ToString("N").Substring(0, 12);
            var sourceContext = _sourceFactory.AccessContext.ForOperation(operationId);
            sourceContext.DemandPath(sourcePath);
            var finalPath = Path.GetFullPath(destinationPath);
            if (string.Equals(sourcePath, finalPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Backup destination must differ from the live database path.");
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("Current POS database not found.");
            EnsureDestinationIsPublishable(finalPath);

            var directory = Path.GetDirectoryName(finalPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Backup destination directory is invalid.");
            _testHooks?.AtBackup(BackupFailurePoint.UnwritableDestination);
            Directory.CreateDirectory(directory);
            using var publishContext = PosDatabaseAccessContext.ReservePublishTarget(finalPath, sourceContext);

            var inspection = new SqliteSourceInspection();
            var temporaryPath = string.Empty;
            PosDatabaseAccessContext temporaryContext = null;
            var total = Stopwatch.StartNew();
            try
            {
                // The identity guard is acquired before sidecar inspection and held
                // until the SQLite snapshot finishes, closing the inspect/open race
                // while still allowing committed writes.
                using (var identityGuard = OpenSourceIdentityGuard(sourcePath))
                {
                    inspection = SqliteSourceInspector.Inspect(sourcePath, requireWalSidecars: true);
                    temporaryPath = AllocateTemporaryPath(finalPath);
                    temporaryContext = PosDatabaseAccessContext.Allocate(temporaryPath, PosDatabaseAccessRole.BackupPartial, sourceContext);
                    _testHooks?.AtBackup(BackupFailurePoint.BeforeSourceOpen);
                    cancellationToken.ThrowIfCancellationRequested();
                    var snapshot = Stopwatch.StartNew();
                    var busyRetries = await Task.Run(
                            () => CreateNativeSnapshot(
                                sourceContext,
                                temporaryContext,
                                operationId,
                                cancellationToken))
                        .ConfigureAwait(false);
                    snapshot.Stop();
                    Report(
                        operationId,
                        "snapshot",
                        Path.GetFileName(finalPath),
                        inspection,
                        snapshot.Elapsed.TotalMilliseconds,
                        cancellationToken,
                        busyRetries == 0 ? "ok" : "ok_after_busy_retry");
                    _testHooks?.AtBackup(BackupFailurePoint.AfterTemporarySnapshotCreation);

                    // SQLite's native backup call is synchronous. Cancellation is observed
                    // only after it returns so no abandoned worker can keep writing a file.
                    cancellationToken.ThrowIfCancellationRequested();
                    _testHooks?.AtBackup(BackupFailurePoint.AfterSnapshotBeforeValidation);
                }
                // Native backup can copy the WAL header. Normalize only the owned snapshot;
                // the source remains read-only and the historical contents/ledger are untouched.
                using (var preparation = new SqliteConnectionFactory(PosDbOptions.ForPath(temporaryPath)
                    .WithAccessContext(temporaryContext)).Open()) { }
                var validationWatch = Stopwatch.StartNew();
                var validationFactory = new SqliteConnectionFactory(PosDbOptions.ForPath(temporaryPath)
                    .WithAccessContext(temporaryContext.Narrow(PosDatabaseAccessRole.BackupValidation)));
                var validation = await new DbMaintenanceRepository(validationFactory)
                    .ValidateAsync(cancellationToken)
                    .ConfigureAwait(false);
                validationWatch.Stop();
                if (!validation.IsValid)
                {
                    throw new InvalidDataException(
                        "SQLite online backup validation failed. integrity=" + validation.IntegrityCheck +
                        " foreignKeys=" + validation.ForeignKeyCheck);
                }

                _testHooks?.AtBackup(BackupFailurePoint.AfterIntegrityForeignKey);
                Report(
                    operationId,
                    "integrity_fk",
                    Path.GetFileName(finalPath),
                    inspection,
                    validationWatch.Elapsed.TotalMilliseconds,
                    cancellationToken,
                    "ok");
                cancellationToken.ThrowIfCancellationRequested();

                SqliteConnectionFactory.ClearAllPools();
                DeleteSqliteSidecars(temporaryPath);
                FlushDurable(temporaryPath);
                _testHooks?.AtBackup(BackupFailurePoint.BeforePublish);
                cancellationToken.ThrowIfCancellationRequested();
                var publish = Stopwatch.StartNew();
                _testHooks?.AtBackup(BackupFailurePoint.PublishError);
                EnsureDestinationIsPublishable(finalPath);
                publishContext.DemandOperation(operationId, finalPath, PosDatabaseAccessRole.PublishTarget);
                File.Move(temporaryPath, finalPath);
                publishContext.BindCreatedFile();
                publish.Stop();
                Report(
                    operationId,
                    "publish",
                    Path.GetFileName(finalPath),
                    inspection,
                    publish.Elapsed.TotalMilliseconds,
                    cancellationToken,
                    "ok");

                total.Stop();
                Report(
                    operationId,
                    "complete",
                    Path.GetFileName(finalPath),
                    inspection,
                    total.Elapsed.TotalMilliseconds,
                    cancellationToken,
                    "backup_verified");
                return validation;
            }
            catch
            {
                SqliteConnectionFactory.ClearAllPools();
                try
                {
                    _testHooks?.AtBackup(BackupFailurePoint.CleanupFailure);
                }
                catch
                {
                    // Deterministic cleanup failure injection exercises the retry below.
                }
                DeleteIfPresent(temporaryPath);
                DeleteSqliteSidecars(temporaryPath);
                total.Stop();
                Report(
                    operationId,
                    "complete",
                    Path.GetFileName(finalPath),
                    inspection,
                    total.Elapsed.TotalMilliseconds,
                    cancellationToken,
                    "backup_failed");
                throw;
            }
            finally { temporaryContext?.Dispose(); }
        }

        private int CreateNativeSnapshot(
            PosDatabaseAccessContext sourceContext,
            PosDatabaseAccessContext destinationContext,
            string operationId,
            CancellationToken cancellationToken)
        {
            var sourcePath = sourceContext.DbPath;
            var temporaryPath = destinationContext.DbPath;
            var retryWindow = Stopwatch.StartNew();
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    sourceContext.DemandOperation(operationId, sourcePath, PosDatabaseAccessRole.Live, PosDatabaseAccessRole.SyntheticFixture, PosDatabaseAccessRole.CandidateMigration);
                    destinationContext.DemandOperation(operationId, temporaryPath, PosDatabaseAccessRole.BackupPartial);
                    _testHooks?.AtBackup(BackupFailurePoint.SourceRemovedOrLocked);
                    using (var sourceBorrow = sourceContext.EnterConnection())
                    using (var destinationBorrow = destinationContext.EnterConnection())
                    using (var source = new SqliteConnection(BuildSourceConnectionString(sourcePath)))
                    using (var destination = new SqliteConnection(BuildDestinationConnectionString(temporaryPath)))
                    {
                        source.Open();
                        destination.Open();
                        if (_testHooks?.NativeSnapshotRunner == null)
                            source.BackupDatabase(destination);
                        else
                            _testHooks.NativeSnapshotRunner(() => source.BackupDatabase(destination));
                    }

                    return attempt - 1;
                }
                catch (SqliteException exception) when (
                    IsNativeSnapshotBusy(exception) &&
                    attempt < NativeSnapshotMaximumAttempts &&
                    retryWindow.ElapsedMilliseconds < NativeSnapshotRetryWindowMilliseconds)
                {
                    DeleteIfPresent(temporaryPath);
                    DeleteSqliteSidecars(temporaryPath);
                    destinationContext.RecreateOwnedPartial();
                    cancellationToken.ThrowIfCancellationRequested();
                    var remaining = NativeSnapshotRetryWindowMilliseconds -
                        (int)retryWindow.ElapsedMilliseconds;
                    if (remaining > 0)
                    {
                        Thread.Sleep(Math.Min(
                            NativeSnapshotRetryDelayMilliseconds,
                            remaining));
                    }
                }
            }
        }

        private static bool IsNativeSnapshotBusy(SqliteException exception)
        {
            return exception.SqliteErrorCode == 5 || exception.SqliteErrorCode == 6;
        }

        private static FileStream OpenSourceIdentityGuard(string sourcePath)
        {
            return new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                4096,
                FileOptions.SequentialScan);
        }

        private string AllocateTemporaryPath(string finalPath)
        {
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var token = _testHooks == null
                    ? Guid.NewGuid().ToString("N").Substring(0, 12)
                    : _testHooks.NextTemporaryToken();
                var path = finalPath + ".partial-" + token;
                if (!File.Exists(path) &&
                    !File.Exists(path + "-wal") &&
                    !File.Exists(path + "-shm") &&
                    !File.Exists(path + "-journal"))
                {
                    return path;
                }

                _testHooks?.AtBackup(BackupFailurePoint.Collision);
            }

            throw new IOException("Unable to allocate a unique non-final backup snapshot name.");
        }

        private void Report(
            string operationId,
            string phase,
            string fileName,
            SqliteSourceInspection inspection,
            double elapsedMilliseconds,
            CancellationToken cancellationToken,
            string resultCode)
        {
            try
            {
                _diagnostics?.Invoke(new BackupRestoreDiagnostic
                {
                    CancellationRequested = cancellationToken.IsCancellationRequested,
                    DatabaseBytes = inspection.DatabaseBytes,
                    ElapsedMilliseconds = elapsedMilliseconds,
                    FileName = fileName,
                    Operation = "backup",
                    OperationId = operationId,
                    Phase = phase,
                    ResultCode = resultCode,
                    ShmBytes = inspection.ShmBytes,
                    ShmPresent = inspection.ShmPresent,
                    SourceKind = inspection.Kind,
                    WalBytes = inspection.WalBytes,
                    WalPresent = inspection.WalPresent
                });
            }
            catch
            {
                // Diagnostics are observational and must never alter publication.
            }
        }

        private static string BuildDestinationConnectionString(string path)
        {
            return new SqliteConnectionStringBuilder
            {
                Cache = SqliteCacheMode.Private,
                DataSource = path,
                ForeignKeys = true,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString();
        }

        private static string BuildSourceConnectionString(string path)
        {
            return new SqliteConnectionStringBuilder
            {
                Cache = SqliteCacheMode.Private,
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString() + ";Default Timeout=5";
        }

        private static void FlushDurable(string path)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Flush(true);
            }
        }

        private static void DeleteSqliteSidecars(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            DeleteIfPresent(path + "-journal");
            DeleteIfPresent(path + "-wal");
            DeleteIfPresent(path + "-shm");
        }

        private static void EnsureDestinationIsPublishable(string path)
        {
            if (File.Exists(path) ||
                File.Exists(path + "-journal") ||
                File.Exists(path + "-wal") ||
                File.Exists(path + "-shm"))
            {
                throw new IOException(
                    "Backup destination or SQLite sidecar already exists: " +
                    Path.GetFileName(path));
            }
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
