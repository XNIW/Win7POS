using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Win7POS.Data.Backup
{
    public enum BackupRestoreSourceKind
    {
        Unknown,
        Standalone,
        Delete,
        Wal
    }

    public sealed class BackupRestoreDiagnostic
    {
        public bool CancellationRequested { get; set; }
        public long DatabaseBytes { get; set; }
        public double ElapsedMilliseconds { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public string OperationId { get; set; } = string.Empty;
        public string Phase { get; set; } = string.Empty;
        public string RecoveryAction { get; set; } = string.Empty;
        public string ResultCode { get; set; } = string.Empty;
        public bool ShmPresent { get; set; }
        public long ShmBytes { get; set; }
        public BackupRestoreSourceKind SourceKind { get; set; }
        public bool WalPresent { get; set; }
        public long WalBytes { get; set; }

        public string ToSafeLogLine()
        {
            return "operation=" + SafeToken(Operation) +
                " operation_id=" + SafeToken(OperationId) +
                " phase=" + SafeToken(Phase) +
                " source_kind=" + SourceKind.ToString().ToLowerInvariant() +
                " file=" + SafeFileName(FileName) +
                " db_bytes=" + DatabaseBytes.ToString(CultureInfo.InvariantCulture) +
                " wal_present=" + Bool(WalPresent) +
                " wal_bytes=" + WalBytes.ToString(CultureInfo.InvariantCulture) +
                " shm_present=" + Bool(ShmPresent) +
                " shm_bytes=" + ShmBytes.ToString(CultureInfo.InvariantCulture) +
                " elapsed_ms=" + ElapsedMilliseconds.ToString("0.000", CultureInfo.InvariantCulture) +
                " cancel_requested=" + Bool(CancellationRequested) +
                " recovery_action=" + SafeToken(RecoveryAction) +
                " result=" + SafeToken(ResultCode);
        }

        private static string SafeFileName(string value)
        {
            return SafeToken(Path.GetFileName(value ?? string.Empty));
        }

        private static string SafeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "none";

            var chars = value.ToCharArray();
            for (var index = 0; index < chars.Length; index++)
            {
                var character = chars[index];
                if (!char.IsLetterOrDigit(character) &&
                    character != '_' &&
                    character != '-' &&
                    character != '.')
                {
                    chars[index] = '_';
                }
            }

            return new string(chars);
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }
    }

    internal enum BackupFailurePoint
    {
        BeforeSourceOpen,
        AfterTemporarySnapshotCreation,
        AfterSnapshotBeforeValidation,
        AfterIntegrityForeignKey,
        BeforePublish,
        PublishError,
        CleanupFailure,
        Collision,
        UnwritableDestination,
        SourceRemovedOrLocked
    }

    internal enum RestoreFailurePoint
    {
        DuringSourceSnapshotIdentityGuard,
        AfterSourceSnapshot,
        AfterCandidateMigration,
        AfterCandidateIntegrityForeignKey,
        AfterPreliminaryShopValidation,
        WhileFenceWait,
        AfterFencedLiveRevalidation,
        AfterFencedCandidateRevalidation,
        DuringVerifiedPreBackup,
        AfterPreBackupBeforePrepared,
        AfterPreparedBeforeSwap,
        ImmediatelyAfterReplace,
        DuringPostMigration,
        DuringPostIntegrity,
        DuringPostForeignKey,
        BeforeCommitted,
        AfterCommittedBeforeCleanup,
        PartialCleanupFailure,
        StartupRecovery
    }

    internal sealed class BackupRestoreTestHooks
    {
        public Action<BackupFailurePoint> BackupFault { get; set; }
        public Func<string> CandidateTokenFactory { get; set; }
        public Action<RestoreFailurePoint> RestoreFault { get; set; }
        public Func<RestoreFailurePoint, CancellationToken, Task> RestorePauseAsync { get; set; }
        public Func<string> TemporaryTokenFactory { get; set; }

        public void AtBackup(BackupFailurePoint point)
        {
            BackupFault?.Invoke(point);
        }

        public void AtRestore(RestoreFailurePoint point)
        {
            RestoreFault?.Invoke(point);
        }

        public Task PauseRestoreAsync(RestoreFailurePoint point, CancellationToken cancellationToken)
        {
            return RestorePauseAsync == null
                ? Task.CompletedTask
                : RestorePauseAsync(point, cancellationToken);
        }

        public string NextCandidateToken()
        {
            return NormalizeToken(CandidateTokenFactory?.Invoke());
        }

        public string NextTemporaryToken()
        {
            return NormalizeToken(TemporaryTokenFactory?.Invoke());
        }

        private static string NormalizeToken(string value)
        {
            var token = string.IsNullOrWhiteSpace(value)
                ? Guid.NewGuid().ToString("N").Substring(0, 12)
                : value.Trim();
            if (token.Length > 20)
                token = token.Substring(0, 20);
            foreach (var character in token)
            {
                if (!char.IsLetterOrDigit(character))
                    throw new InvalidOperationException("A test hook returned an unsafe file token.");
            }
            return token;
        }
    }

    internal sealed class RestoreCrashSimulationException : IOException
    {
        public RestoreCrashSimulationException(string phase)
            : base("Deterministic restore crash simulation at " + (phase ?? "unknown") + ".")
        {
        }
    }
}
