using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Win7POS.Data.Backup;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Online
{
    public sealed class RestoreOperationResult
    {
        public DatabaseValidationResult LiveValidation { get; set; } = new DatabaseValidationResult();
        public long SourceDatabaseBytes { get; set; }
        public BackupRestoreSourceKind SourceKind { get; set; }
    }

    public sealed class SqliteRestoreCoordinator
    {
        private const int NativeSnapshotMaximumAttempts = 5;
        private const int NativeSnapshotRetryDelayMilliseconds = 25;
        private const int NativeSnapshotRetryWindowMilliseconds = 5000;

        private readonly Action<BackupRestoreDiagnostic> _diagnostics;
        private readonly SqliteConnectionFactory _liveFactory;
        private readonly SqliteOnlineBackup _onlineBackup;
        private readonly BackupRestoreTestHooks _testHooks;

        public SqliteRestoreCoordinator(
            SqliteConnectionFactory liveFactory,
            SqliteOnlineBackup onlineBackup,
            Action<BackupRestoreDiagnostic> diagnostics = null)
            : this(liveFactory, onlineBackup, diagnostics, null)
        {
        }

        internal SqliteRestoreCoordinator(
            SqliteConnectionFactory liveFactory,
            SqliteOnlineBackup onlineBackup,
            Action<BackupRestoreDiagnostic> diagnostics,
            BackupRestoreTestHooks testHooks)
        {
            _liveFactory = liveFactory ?? throw new ArgumentNullException(nameof(liveFactory));
            _onlineBackup = onlineBackup ?? throw new ArgumentNullException(nameof(onlineBackup));
            _diagnostics = diagnostics;
            _testHooks = testHooks;
        }

        public async Task<RestoreOperationResult> RestoreAsync(
            string sourceDatabasePath,
            string verifiedPreRestoreBackupPath,
            string expectedShopId,
            string expectedShopCode,
            long expectedCatalogEpoch,
            Func<DatabaseValidationResult, Task> postSwapCommit,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ValidateArguments(
                sourceDatabasePath,
                verifiedPreRestoreBackupPath,
                expectedShopId,
                expectedShopCode,
                postSwapCommit);
            cancellationToken.ThrowIfCancellationRequested();

            var livePath = Path.GetFullPath(_liveFactory.DbPath);
            var sourcePath = Path.GetFullPath(sourceDatabasePath);
            var preBackupPath = Path.GetFullPath(verifiedPreRestoreBackupPath);
            if (PathsEqual(sourcePath, livePath))
                throw new InvalidOperationException("Restore source must differ from the live database.");
            if (PathsEqual(preBackupPath, livePath) || PathsEqual(preBackupPath, sourcePath))
                throw new InvalidOperationException("Pre-restore backup destination must be distinct.");
            if (File.Exists(preBackupPath))
                throw new IOException("Pre-restore backup destination already exists: " + Path.GetFileName(preBackupPath));

            var operationId = Guid.NewGuid().ToString("N").Substring(0, 12);
            var inspection = new SqliteSourceInspection();
            var total = Stopwatch.StartNew();
            RestoreCandidatePreparation preparation = null;
            SealedRestoreCandidate sealedCandidate = null;
            try
            {
                // Acquire the no-delete identity guard before inspecting the header
                // and sidecars. It stays held until the native SQLite snapshot has
                // completed, so inspection and source open refer to one file identity.
                using (var identityGuard = OpenSourceIdentityGuard(sourcePath))
                {
                    inspection = SqliteSourceInspector.Inspect(sourcePath, requireWalSidecars: true);
                    preparation = await StageAndValidateCandidateAsync(
                            operationId,
                            sourcePath,
                            livePath,
                            expectedShopId,
                            expectedShopCode,
                            inspection,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                _testHooks?.AtRestore(RestoreFailurePoint.WhileFenceWait);
                await (_testHooks?.PauseRestoreAsync(
                        RestoreFailurePoint.WhileFenceWait,
                        cancellationToken) ?? Task.CompletedTask)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                DatabaseValidationResult liveValidation = null;
                var fenceWatch = Stopwatch.StartNew();
                await SqliteConnectionFactory.RunExclusiveMaintenanceAsync(async () =>
                {
                    fenceWatch.Stop();
                    Report(
                        operationId,
                        "fence_wait",
                        sourcePath,
                        inspection,
                        fenceWatch.Elapsed.TotalMilliseconds,
                        cancellationToken,
                        "ok");
                    cancellationToken.ThrowIfCancellationRequested();

                    var liveSafetyWatch = Stopwatch.StartNew();
                    var liveSafety = await new RestoreShopSafetyRepository(_liveFactory)
                        .ValidateLivePreSwapAsync(
                            expectedShopId,
                            expectedShopCode,
                            expectedCatalogEpoch)
                        .ConfigureAwait(false);
                    liveSafetyWatch.Stop();
                    if (!liveSafety.IsValid)
                        throw new InvalidOperationException(liveSafety.Code);
                    _testHooks?.AtRestore(RestoreFailurePoint.AfterFencedLiveRevalidation);
                    Report(
                        operationId,
                        "fenced_live_revalidation",
                        sourcePath,
                        inspection,
                        liveSafetyWatch.Elapsed.TotalMilliseconds,
                        cancellationToken,
                        "ok");
                    cancellationToken.ThrowIfCancellationRequested();

                    var candidateWatch = Stopwatch.StartNew();
                    sealedCandidate = await preparation
                        .ValidateAndSealAsync(cancellationToken)
                        .ConfigureAwait(false);
                    candidateWatch.Stop();
                    _testHooks?.AtRestore(RestoreFailurePoint.AfterFencedCandidateRevalidation);
                    Report(
                        operationId,
                        "fenced_candidate_revalidation",
                        sourcePath,
                        inspection,
                        candidateWatch.Elapsed.TotalMilliseconds,
                        cancellationToken,
                        "ok");
                    cancellationToken.ThrowIfCancellationRequested();

                    _testHooks?.AtRestore(RestoreFailurePoint.DuringVerifiedPreBackup);
                    var preBackupWatch = Stopwatch.StartNew();
                    var preBackupValidation = await _onlineBackup
                        .CreateVerifiedAsync(preBackupPath, cancellationToken)
                        .ConfigureAwait(false);
                    preBackupWatch.Stop();
                    if (!preBackupValidation.IsValid)
                        throw new InvalidDataException("Verified pre-restore backup failed validation.");
                    Report(
                        operationId,
                        "verified_prebackup",
                        sourcePath,
                        inspection,
                        preBackupWatch.Elapsed.TotalMilliseconds,
                        cancellationToken,
                        "ok");
                    _testHooks?.AtRestore(RestoreFailurePoint.AfterPreBackupBeforePrepared);
                    cancellationToken.ThrowIfCancellationRequested();

                    var installer = new AtomicRestoreInstaller(
                        _diagnostics,
                        _testHooks,
                        operationId,
                        inspection);
                    liveValidation = await installer.InstallAsync(
                            sealedCandidate,
                            livePath,
                            preBackupPath,
                            postSwapCommit,
                            cancellationToken)
                        .ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);

                total.Stop();
                Report(
                    operationId,
                    "complete",
                    sourcePath,
                    inspection,
                    total.Elapsed.TotalMilliseconds,
                    cancellationToken,
                    "restore_committed");
                return new RestoreOperationResult
                {
                    LiveValidation = liveValidation ?? new DatabaseValidationResult(),
                    SourceDatabaseBytes = inspection.DatabaseBytes,
                    SourceKind = inspection.Kind
                };
            }
            catch
            {
                total.Stop();
                Report(
                    operationId,
                    "complete",
                    sourcePath,
                    inspection,
                    total.Elapsed.TotalMilliseconds,
                    cancellationToken,
                    "restore_failed");
                throw;
            }
            finally
            {
                sealedCandidate?.Dispose();
                preparation?.Dispose();
            }
        }

        private async Task<RestoreCandidatePreparation> StageAndValidateCandidateAsync(
            string operationId,
            string sourcePath,
            string livePath,
            string expectedShopId,
            string expectedShopCode,
            SqliteSourceInspection inspection,
            CancellationToken cancellationToken)
        {
            var candidatePath = AllocateCandidatePath(livePath);
            try
            {
                var snapshotWatch = Stopwatch.StartNew();
                var busyRetries = await Task.Run(
                        () => CreateReadOnlySnapshot(
                            sourcePath,
                            candidatePath,
                            cancellationToken))
                    .ConfigureAwait(false);
                snapshotWatch.Stop();
                _testHooks?.AtRestore(RestoreFailurePoint.AfterSourceSnapshot);
                Report(
                    operationId,
                    "snapshot",
                    sourcePath,
                    inspection,
                    snapshotWatch.Elapsed.TotalMilliseconds,
                    cancellationToken,
                    busyRetries == 0 ? "ok" : "ok_after_busy_retry");

                // BackupDatabase is synchronous and deliberately not abandoned.
                cancellationToken.ThrowIfCancellationRequested();
                var migrationWatch = Stopwatch.StartNew();
                DbInitializer.EnsureCreated(PosDbOptions.ForPath(candidatePath));
                migrationWatch.Stop();
                _testHooks?.AtRestore(RestoreFailurePoint.AfterCandidateMigration);
                Report(
                    operationId,
                    "candidate_migration",
                    sourcePath,
                    inspection,
                    migrationWatch.Elapsed.TotalMilliseconds,
                    cancellationToken,
                    "ok");
                cancellationToken.ThrowIfCancellationRequested();

                var candidateFactory = new SqliteConnectionFactory(PosDbOptions.ForPath(candidatePath));
                var integrityWatch = Stopwatch.StartNew();
                var validation = await new DbMaintenanceRepository(candidateFactory)
                    .ValidateAsync(cancellationToken)
                    .ConfigureAwait(false);
                integrityWatch.Stop();
                if (!validation.IsValid)
                {
                    throw new InvalidDataException(
                        "Restore candidate failed integrity or foreign-key validation.");
                }
                _testHooks?.AtRestore(RestoreFailurePoint.AfterCandidateIntegrityForeignKey);
                Report(
                    operationId,
                    "candidate_integrity_fk",
                    sourcePath,
                    inspection,
                    integrityWatch.Elapsed.TotalMilliseconds,
                    cancellationToken,
                    "ok");
                cancellationToken.ThrowIfCancellationRequested();

                var shopWatch = Stopwatch.StartNew();
                var safety = await new RestoreShopSafetyRepository(candidateFactory)
                    .ValidateCandidateAsync(expectedShopId, expectedShopCode)
                    .ConfigureAwait(false);
                shopWatch.Stop();
                if (!safety.IsValid)
                    throw new InvalidOperationException(safety.Code);
                _testHooks?.AtRestore(RestoreFailurePoint.AfterPreliminaryShopValidation);
                Report(
                    operationId,
                    "preliminary_shop_validation",
                    sourcePath,
                    inspection,
                    shopWatch.Elapsed.TotalMilliseconds,
                    cancellationToken,
                    "ok");
                cancellationToken.ThrowIfCancellationRequested();

                return new RestoreCandidatePreparation(
                    candidatePath,
                    candidateFactory,
                    expectedShopId,
                    expectedShopCode);
            }
            catch
            {
                SqliteConnectionFactory.ClearAllPools();
                DeleteSqliteFiles(candidatePath);
                throw;
            }
        }

        private string AllocateCandidatePath(string livePath)
        {
            var directory = Path.GetDirectoryName(livePath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Live database directory is invalid.");

            for (var attempt = 0; attempt < 16; attempt++)
            {
                var token = _testHooks == null
                    ? Guid.NewGuid().ToString("N").Substring(0, 8)
                    : _testHooks.NextCandidateToken();
                var path = Path.Combine(directory, "r-" + token + ".db");
                if (!File.Exists(path) &&
                    !File.Exists(path + "-wal") &&
                    !File.Exists(path + "-shm") &&
                    !File.Exists(path + "-journal"))
                {
                    return path;
                }
            }

            throw new IOException("Unable to allocate a unique same-directory restore candidate.");
        }

        private int CreateReadOnlySnapshot(
            string sourcePath,
            string candidatePath,
            CancellationToken cancellationToken)
        {
            var retryWindow = Stopwatch.StartNew();
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    _testHooks?.AtRestore(RestoreFailurePoint.DuringSourceSnapshotIdentityGuard);
                    using (var source = new SqliteConnection(BuildSourceConnectionString(sourcePath)))
                    using (var destination = new SqliteConnection(BuildCandidateConnectionString(candidatePath)))
                    {
                        source.Open();
                        destination.Open();
                        source.BackupDatabase(destination);
                    }

                    return attempt - 1;
                }
                catch (SqliteException exception) when (
                    IsNativeSnapshotBusy(exception) &&
                    attempt < NativeSnapshotMaximumAttempts &&
                    retryWindow.ElapsedMilliseconds < NativeSnapshotRetryWindowMilliseconds)
                {
                    DeleteSqliteFiles(candidatePath);
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

        private static string BuildCandidateConnectionString(string path)
        {
            return new SqliteConnectionStringBuilder
            {
                Cache = SqliteCacheMode.Private,
                DataSource = path,
                ForeignKeys = true,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();
        }

        private void Report(
            string operationId,
            string phase,
            string sourcePath,
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
                    FileName = Path.GetFileName(sourcePath),
                    Operation = "restore",
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
                // Diagnostics are observational and must not alter restore state.
            }
        }

        private static void ValidateArguments(
            string sourceDatabasePath,
            string verifiedPreRestoreBackupPath,
            string expectedShopId,
            string expectedShopCode,
            Func<DatabaseValidationResult, Task> postSwapCommit)
        {
            if (string.IsNullOrWhiteSpace(sourceDatabasePath))
                throw new ArgumentException("Restore source path is required.", nameof(sourceDatabasePath));
            if (!File.Exists(sourceDatabasePath))
                throw new FileNotFoundException("Restore source database was not found.");
            if (string.IsNullOrWhiteSpace(verifiedPreRestoreBackupPath))
                throw new ArgumentException("Pre-restore backup path is required.", nameof(verifiedPreRestoreBackupPath));
            if (string.IsNullOrWhiteSpace(expectedShopId) || string.IsNullOrWhiteSpace(expectedShopCode))
                throw new InvalidOperationException("Trusted shop identity is required for restore.");
            if (postSwapCommit == null)
                throw new ArgumentNullException(nameof(postSwapCommit));
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static void DeleteSqliteFiles(string path)
        {
            DeleteIfPresent(path);
            DeleteIfPresent(path + "-journal");
            DeleteIfPresent(path + "-wal");
            DeleteIfPresent(path + "-shm");
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    internal sealed class RestoreCandidatePreparation : IDisposable
    {
        private readonly SqliteConnectionFactory _candidateFactory;
        private readonly string _expectedShopCode;
        private readonly string _expectedShopId;
        private string _candidatePath;

        public RestoreCandidatePreparation(
            string candidatePath,
            SqliteConnectionFactory candidateFactory,
            string expectedShopId,
            string expectedShopCode)
        {
            _candidatePath = candidatePath ?? throw new ArgumentNullException(nameof(candidatePath));
            _candidateFactory = candidateFactory ?? throw new ArgumentNullException(nameof(candidateFactory));
            _expectedShopId = expectedShopId;
            _expectedShopCode = expectedShopCode;
        }

        public async Task<SealedRestoreCandidate> ValidateAndSealAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = RequirePath();
            var safety = await new RestoreShopSafetyRepository(_candidateFactory)
                .ValidateCandidateAsync(_expectedShopId, _expectedShopCode)
                .ConfigureAwait(false);
            if (!safety.IsValid)
                throw new InvalidOperationException(safety.Code);
            cancellationToken.ThrowIfCancellationRequested();

            SqliteConnectionFactory.ClearAllPools();
            DeleteIfPresent(path + "-journal");
            DeleteIfPresent(path + "-wal");
            DeleteIfPresent(path + "-shm");
            FlushDurable(path);
            var seal = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
            var candidate = new SealedRestoreCandidate(
                path,
                seal,
                seal.Length,
                File.GetLastWriteTimeUtc(path));
            _candidatePath = null;
            return candidate;
        }

        public void Dispose()
        {
            var path = _candidatePath;
            _candidatePath = null;
            if (string.IsNullOrWhiteSpace(path))
                return;

            SqliteConnectionFactory.ClearAllPools();
            TryDelete(path);
            TryDelete(path + "-journal");
            TryDelete(path + "-wal");
            TryDelete(path + "-shm");
        }

        private string RequirePath()
        {
            if (string.IsNullOrWhiteSpace(_candidatePath))
                throw new ObjectDisposedException(nameof(RestoreCandidatePreparation));
            return _candidatePath;
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

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        private static void TryDelete(string path)
        {
            try
            {
                DeleteIfPresent(path);
            }
            catch
            {
            }
        }
    }

    internal sealed class SealedRestoreCandidate : IDisposable
    {
        private readonly long _expectedLength;
        private readonly DateTime _expectedLastWriteUtc;
        private string _path;
        private FileStream _seal;
        private bool _consumed;

        public SealedRestoreCandidate(
            string path,
            FileStream seal,
            long expectedLength,
            DateTime expectedLastWriteUtc)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _seal = seal ?? throw new ArgumentNullException(nameof(seal));
            _expectedLength = expectedLength;
            _expectedLastWriteUtc = expectedLastWriteUtc;
        }

        public string FileName
        {
            get
            {
                if (_consumed || string.IsNullOrWhiteSpace(_path))
                    throw new InvalidOperationException("Restore candidate was already consumed.");
                return Path.GetFileName(_path);
            }
        }

        public string ConsumeForAtomicReplace(string liveDatabasePath)
        {
            if (_consumed || string.IsNullOrWhiteSpace(_path) || _seal == null)
                throw new InvalidOperationException("Restore candidate was already consumed.");
            var candidateDirectory = Path.GetDirectoryName(Path.GetFullPath(_path));
            var liveDirectory = Path.GetDirectoryName(Path.GetFullPath(liveDatabasePath));
            if (!string.Equals(candidateDirectory, liveDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Restore candidate is not on the live database volume and directory.");
            if (_seal.Length != _expectedLength ||
                File.GetLastWriteTimeUtc(_path) != _expectedLastWriteUtc ||
                File.Exists(_path + "-journal") ||
                File.Exists(_path + "-wal") ||
                File.Exists(_path + "-shm"))
            {
                throw new InvalidDataException("Sealed restore candidate changed after validation.");
            }

            _seal.Dispose();
            _seal = null;
            _consumed = true;
            return _path;
        }

        public void ReleaseSealForRecovery()
        {
            _seal?.Dispose();
            _seal = null;
        }

        public void Dispose()
        {
            _seal?.Dispose();
            _seal = null;
            if (_consumed || string.IsNullOrWhiteSpace(_path))
                return;
            TryDelete(_path);
            TryDelete(_path + "-journal");
            TryDelete(_path + "-wal");
            TryDelete(_path + "-shm");
            _path = null;
        }

        private static void TryDelete(string path)
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
    }
}
