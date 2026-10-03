using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Backup;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Backup
{
    internal enum BackupAutomationFailurePoint { AfterPublish }

    internal sealed class BackupAutomationCrashException : IOException
    {
        internal BackupAutomationCrashException() : base("Deterministic backup automation crash after publish.") { }
    }

    internal sealed class BackupAutomationIdentityException : IOException
    {
        internal BackupAutomationIdentityException(Exception innerException = null)
            : base("Published snapshot identity is invalid.", innerException) { }
    }

    public sealed class BackupAutomationService : IDisposable
    {
        private static readonly ConcurrentDictionary<string, BackupAutomationService> Services =
            new ConcurrentDictionary<string, BackupAutomationService>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Flights =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

        private readonly SqliteConnectionFactory _factory;
        private readonly string _livePath;
        private readonly string _dbKey;
        private readonly string _defaultDirectory;
        private readonly SemaphoreSlim _flight;
        private readonly BackupAutomationStore _store;
        private readonly IBackupAutomationClock _clock;
        private readonly Func<string, CancellationToken, Task<DatabaseValidationResult>> _snapshot;
        private readonly Action<BackupRestoreDiagnostic> _diagnostics;
        private readonly Action<BackupAutomationFailurePoint> _failureHook;
        private readonly Action<string> _destinationProbe;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private Timer _timer;
        private int _started;
        private int _startupPending = 1;
        private DateTime? _logicalUtcBase;
        private TimeSpan _logicalElapsedBase;
        private string _lastUnavailableResult = string.Empty;

        public static BackupAutomationService GetOrCreate(SqliteConnectionFactory factory, string defaultDirectory,
            Action<BackupRestoreDiagnostic> diagnostics = null)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            var path = System.IO.Path.GetFullPath(factory.DbPath);
            return Services.GetOrAdd(path, _ => new BackupAutomationService(factory, defaultDirectory,
                new BackupAutomationClock(), null, diagnostics));
        }

        internal BackupAutomationService(SqliteConnectionFactory factory, string defaultDirectory,
            IBackupAutomationClock clock,
            Func<string, CancellationToken, Task<DatabaseValidationResult>> snapshot = null,
            Action<BackupRestoreDiagnostic> diagnostics = null,
            Action<BackupAutomationFailurePoint> failureHook = null,
            Action<string> destinationProbe = null)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _livePath = System.IO.Path.GetFullPath(factory.DbPath);
            _dbKey = BackupAutomationStore.HashText(_livePath.ToUpperInvariant());
            _defaultDirectory = defaultDirectory ?? throw new ArgumentNullException(nameof(defaultDirectory));
            _flight = Flights.GetOrAdd(_livePath, _ => new SemaphoreSlim(1, 1));
            _store = new BackupAutomationStore(factory, _dbKey);
            _diagnostics = diagnostics;
            _failureHook = failureHook;
            _destinationProbe = destinationProbe;
            var engine = new SqliteOnlineBackup(factory, diagnostics);
            _snapshot = snapshot ?? engine.CreateVerifiedAsync;
        }

        // The caller starts only after restore recovery and all migrations have completed.
        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
                _timer = new Timer(state => { _ = TickAsync(); }, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _timer, null)?.Dispose();
            _stop.Cancel();
        }

        public async Task<BackupAutomationOptions> GetOptionsAsync()
        {
            using (await EnterMaintenanceAsync().ConfigureAwait(false))
                return await Task.Run(() => _store.LoadOptions()).ConfigureAwait(false);
        }

        // The profile service already holds EnterMaintenanceAsync; preserve the same store/schema/ownership.
        internal void EnsurePortablePolicyState() => _store.LoadState(LocalNow.Ticks);

        // Trusted fixtures use the internal overload; production callers must supply current authority.
        internal Task SaveOptionsAsync(BackupAutomationOptions options, string actor) => SaveOptionsAsync(options, actor, () => { });

        public async Task SaveOptionsAsync(BackupAutomationOptions options, string actor, Action demandPermission)
        {
            if (demandPermission == null) throw new ArgumentNullException(nameof(demandPermission));
            demandPermission();
            if (options == null || options.Schedule == null)
                throw new ArgumentException("Backup options are required.");
            var copy = options.Copy();
            copy.DestinationPath = copy.DestinationPath ?? string.Empty;
            var resolved = BackupAutomationDestination.Resolve(copy, _defaultDirectory, _livePath);
            copy.DestinationPath = copy.DestinationKind == "local" ? string.Empty : resolved;
            using (await EnterMaintenanceAsync().ConfigureAwait(false))
            {
                demandPermission();
                await Task.Run(() =>
                {
                    var state = _store.LoadState(LocalNow.Ticks);
                    var utc = EffectiveUtc(state);
                    _store.SaveOptions(copy, actor, LocalNow.Ticks, utc.Ticks, demandPermission);
                }).ConfigureAwait(false);
            }
        }

        public async Task<string> GetLastResultAsync()
        {
            try
            {
                using (await EnterMaintenanceAsync().ConfigureAwait(false))
                    return await Task.Run(() => _store.LoadState(LocalNow.Ticks).LastResult).ConfigureAwait(false);
            }
            catch
            {
                if (_lastUnavailableResult.Length == 0) throw;
                return _lastUnavailableResult;
            }
        }

        public Task<BackupAutomationResult> BackupNowAsync(CancellationToken cancellationToken = default)
        {
            return Task.Run(() => RunAsync(true, false, cancellationToken));
        }

        internal Task<BackupAutomationResult> PollAsync(bool startup = false, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => RunAsync(false, startup, cancellationToken));
        }

        // Restore and settings use this lease; normal backup uses the same gate without waiting.
        public async Task<IDisposable> EnterMaintenanceAsync(CancellationToken cancellationToken = default)
        {
            await _flight.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var processLock = OpenProcessLock();
                        try
                        {
                            if (!FileExistsStrict(_livePath)) throw new FileNotFoundException("Live backup database is missing.");
                            return new MaintenanceLease(_flight, processLock);
                        }
                        catch { processLock.Dispose(); throw; }
                    }
                    catch (IOException exception) when (IsSharingViolation(exception))
                    {
                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                _flight.Release();
                throw;
            }
        }

        private async Task TickAsync()
        {
            try
            {
                var result = await PollAsync(Volatile.Read(ref _startupPending) == 1, _stop.Token).ConfigureAwait(false);
                if (result.Code != "busy") Interlocked.Exchange(ref _startupPending, 0);
            }
            catch (Exception exception)
            {
                Report(FailureCode(exception, false), string.Empty);
            }
        }

        private DateTime LocalNow => DateTime.SpecifyKind(_clock.LocalNow, DateTimeKind.Unspecified);

        private async Task<BackupAutomationResult> RunAsync(bool manual, bool startup, CancellationToken cancellationToken)
        {
            if (!_flight.Wait(0)) return Result("busy");
            BackupAutomationState state = null;
            BackupAutomationOptions options = null;
            FileStream processLock = null;
            var published = false;
            try
            {
                if (!FileExistsStrict(_livePath))
                {
                    _lastUnavailableResult = "live_database_missing";
                    Report("live_database_missing", string.Empty);
                    return Result("live_database_missing");
                }
                try { processLock = OpenProcessLock(); }
                catch (IOException exception) when (IsSharingViolation(exception)) { return Result("busy"); }
                cancellationToken.ThrowIfCancellationRequested();
                state = _store.LoadState(LocalNow.Ticks);
                var utc = EffectiveUtc(state);
                options = _store.LoadOptions();
                var destination = BackupAutomationDestination.Resolve(options, _defaultDirectory, _livePath);
                if (!manual && options.Schedule.Mode == BackupScheduleMode.Disabled)
                {
                    _store.SaveState(state);
                    return Result("disabled");
                }
                if (!manual && startup && !options.Schedule.CatchUpOnStartup)
                {
                    var skipped = BackupSchedulePolicy.GetLatestSlot(options.Schedule, LocalNow);
                    if (skipped.HasValue && skipped.Value < LocalNow)
                        state.ConsideredLocal = Math.Max(state.ConsideredLocal, skipped.Value.Ticks);
                    // Persist before retry backoff. Keep the intent until an existing
                    // publication is reconciled or its absence is established safely.
                    _store.SaveState(state);
                }
                // Back off before probing an unavailable share. A known published result
                // can be reconciled immediately without running another snapshot.
                if (!manual && state.RetryUtc > utc.Ticks &&
                    !state.LastResult.StartsWith("backup_finalize_pending|", StringComparison.Ordinal))
                {
                    _store.SaveState(state);
                    return Result("retry_pending");
                }
                if (state.PendingId.Length != 0) _destinationProbe?.Invoke(state.PendingDestination);
                if (state.PendingId.Length != 0 && FileExistsStrict(state.PendingPath))
                {
                    published = true;
                    return await CompletePublishedAsync(state, options).ConfigureAwait(false);
                }

                if (!manual && state.PendingLocal != 0 && state.PendingLocal <= state.ConsideredLocal)
                    state.ClearPending();

                var due = BackupSchedulePolicy.GetDueSlot(options.Schedule, LocalNow,
                    LocalDate(state.ConsideredLocal), LocalDate(state.ActivatedLocal), startup);
                if (!manual && !due.HasValue && state.PendingId.Length == 0)
                {
                    _store.SaveState(state);
                    return Result("not_due");
                }

                // Keep a failed intent in the same directory. An explicit configuration
                // change may replace an absent intent, never a published snapshot.
                if (state.PendingId.Length != 0 && !string.Equals(state.PendingDestination, destination,
                    StringComparison.OrdinalIgnoreCase))
                    state.ClearPending();
                if (!manual && !due.HasValue && state.PendingId.Length == 0)
                {
                    _store.SaveState(state);
                    return Result("not_due");
                }
                if (state.PendingId.Length == 0)
                {
                    _destinationProbe?.Invoke(destination);
                    state.PendingId = Guid.NewGuid().ToString("N");
                    state.PendingDestination = destination;
                    state.PendingUtc = utc.Ticks;
                    state.PendingLocal = manual ? 0 : due.Value.Ticks;
                    state.PendingPath = System.IO.Path.Combine(destination, "pos_backup_" +
                        utc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" +
                        _dbKey.Substring(0, 8) + "_" + state.PendingId.Substring(0, 12) + ".db");
                    if (FileExistsStrict(state.PendingPath))
                        throw new IOException("Backup destination collision.");
                }
                _store.SaveState(state); // Durable intent is part of the upcoming SQLite snapshot.
                BackupAutomationDestination.RejectReparseAncestors(state.PendingDestination);
                cancellationToken.ThrowIfCancellationRequested();
                var validation = await _snapshot(state.PendingPath, cancellationToken).ConfigureAwait(false);
                if (validation == null || !validation.IsValid)
                    throw new InvalidDataException("Backup snapshot was not verified.");
                published = true;
                _failureHook?.Invoke(BackupAutomationFailurePoint.AfterPublish);
                // Publication is the cancellation boundary: a verified published snapshot
                // must be finalized even if the caller cancels while the engine returns.
                return await CompletePublishedAsync(state, options).ConfigureAwait(false);
            }
            catch (BackupAutomationCrashException) { throw; }
            catch (Exception exception)
            {
                var code = FailureCode(exception, options?.DestinationKind == "network_share");
                if (published && !(exception is BackupAutomationIdentityException)) code = "backup_finalize_pending";
                if (state != null)
                {
                    try
                    {
                        var utc = EffectiveUtc(state);
                        state.RetryUtc = utc.Add(RetryDelay).Ticks;
                        state.LastResult = LastResult(code, utc, state.PendingPath);
                        _store.SaveState(state, code, state.PendingPath);
                    }
                    catch { /* The durable pending intent remains recoverable; diagnostics are safe. */ }
                }
                _lastUnavailableResult = code;
                Report(code, state?.PendingPath);
                return Result(code, state?.PendingPath);
            }
            finally
            {
                try { processLock?.Dispose(); }
                finally { _flight.Release(); }
            }
        }

        private async Task<BackupAutomationResult> CompletePublishedAsync(BackupAutomationState state,
            BackupAutomationOptions options)
        {
            var path = state.PendingPath;
            var utc = EffectiveUtc(state);
            BackupAutomationManagedFile managed;
            // Bind identity validation, hash and ownership registration to the same
            // immutable file. An engine-return/persistence gap must not adopt a replacement.
            using (var publicationGuard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await ValidatePendingSnapshotAsync(state).ConfigureAwait(false);
                managed = new BackupAutomationManagedFile
                {
                    Path = path, Destination = state.PendingDestination, DbKey = _dbKey,
                    CreatedUtc = utc.Ticks, OperationId = state.PendingId,
                    Hash = BackupAutomationRetention.HashFile(path, out var length), Length = length
                };
                // Never clear the caller's durable intent before its completion transaction commits.
                state = state.Copy();
                if (state.PendingLocal != 0)
                {
                    // A successful retry covers current data once, rather than replaying a backlog.
                    var latest = BackupSchedulePolicy.GetLatestSlot(options.Schedule, LocalNow);
                    state.ConsideredLocal = Math.Max(state.ConsideredLocal,
                        Math.Max(state.PendingLocal, latest?.Ticks ?? 0));
                }
                state.LastResult = LastResult("backup_verified", utc, path);
                state.ClearPending();
                _store.SaveState(state, "backup_verified", path, managed: managed);
            }

            var deleted = 0;
            var warning = false;
            var configuredDestination = BackupAutomationDestination.Resolve(options, _defaultDirectory, _livePath);
            try
            {
                if (string.Equals(managed.Destination, configuredDestination, StringComparison.OrdinalIgnoreCase))
                    deleted = ApplyRetention(state, options, managed.Destination, out warning);
            }
            catch { warning = true; }
            var code = warning ? "backup_verified_retention_warning" : "backup_verified";
            if (warning || deleted != 0)
            {
                state.LastResult = LastResult(code, utc, path);
                try { _store.SaveState(state, code, path, deleted); }
                catch { code = "backup_verified_retention_warning"; }
            }
            Report(code, path);
            return new BackupAutomationResult { Path = path, Code = code, IsSuccess = true, DeletedCount = deleted };
        }

        private int ApplyRetention(BackupAutomationState state, BackupAutomationOptions options, string destination,
            out bool warning)
        {
            warning = false;
            var verified = new List<BackupAutomationManagedFile>();
            foreach (var file in _store.LoadManaged(destination))
            {
                try
                {
                    if (BackupAutomationRetention.IsVerifiedIdentity(file)) verified.Add(file);
                    else
                    {
                        warning = true;
                        _store.SaveState(state, removedPath: file.Path); // Disown replaced files, never delete them.
                    }
                }
                catch (FileNotFoundException) { _store.SaveState(state, removedPath: file.Path); }
                catch (DirectoryNotFoundException) { _store.SaveState(state, removedPath: file.Path); }
                catch { warning = true; }
            }
            var deleted = 0;
            var oldestAllowedUtc = new DateTime(state.MaxUtc, DateTimeKind.Utc).AddDays(-options.RetentionMaxAgeDays).Ticks;
            for (var index = 3; index < verified.Count; index++)
            {
                var file = verified[index];
                if (index < options.RetentionMaxCount && file.CreatedUtc >= oldestAllowedUtc) continue;
                try
                {
                    if (BackupAutomationRetention.DeleteVerifiedIdentity(file))
                    {
                        deleted++;
                        _store.SaveState(state, removedPath: file.Path);
                    }
                    else warning = true;
                }
                catch { warning = true; }
            }
            return deleted;
        }

        private async Task ValidatePendingSnapshotAsync(BackupAutomationState state)
        {
            if ((File.GetAttributes(state.PendingPath) & FileAttributes.ReparsePoint) != 0)
                throw new BackupAutomationIdentityException();
            BackupAutomationDestination.RejectReparseAncestors(state.PendingDestination);
            using (var guard = new FileStream(state.PendingPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = state.PendingPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
                Cache = SqliteCacheMode.Private
            }.ToString()))
            {
                try
                {
                    await connection.OpenAsync().ConfigureAwait(false);
                    var identity = await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1) FROM backup_automation_state WHERE singleton_id=1 AND db_key=@DbKey
 AND pending_id=@PendingId AND pending_path=@PendingPath;", state).ConfigureAwait(false);
                    if (identity != 1) throw new BackupAutomationIdentityException();
                    var integrity = await connection.ExecuteScalarAsync<string>("PRAGMA integrity_check;").ConfigureAwait(false);
                    var foreignKeys = await connection.QueryAsync<object>("PRAGMA foreign_key_check;").ConfigureAwait(false);
                    if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase) || foreignKeys.Any())
                        throw new BackupAutomationIdentityException();
                }
                catch (SqliteException exception) when (exception.SqliteErrorCode == 1 ||
                    exception.SqliteErrorCode == 11 || exception.SqliteErrorCode == 26)
                {
                    throw new BackupAutomationIdentityException(exception);
                }
            }
        }

        private DateTime EffectiveUtc(BackupAutomationState state)
        {
            var rawUtc = DateTime.SpecifyKind(_clock.UtcNow, DateTimeKind.Utc);
            if (!_logicalUtcBase.HasValue)
            {
                _logicalUtcBase = new DateTime(Math.Max(rawUtc.Ticks, state.MaxUtc), DateTimeKind.Utc);
                _logicalElapsedBase = _clock.MonotonicElapsed;
            }
            var elapsed = _clock.MonotonicElapsed - _logicalElapsedBase;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            var effective = Math.Max(Math.Max(rawUtc.Ticks, state.MaxUtc), _logicalUtcBase.Value.Add(elapsed).Ticks);
            state.MaxUtc = effective;
            return new DateTime(effective, DateTimeKind.Utc);
        }

        private FileStream OpenProcessLock()
        {
            return new FileStream(_livePath + ".backup-automation.lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }

        private static bool IsSharingViolation(IOException exception)
        {
            var code = exception.HResult & 0xffff;
            return code == 32 || code == 33;
        }

        private static bool FileExistsStrict(string path)
        {
            try { File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException)
            {
                // An unreachable UNC share is not proof that a previously published
                // file is absent. Preserve its intent for later reconciliation.
                if (path.StartsWith(@"\\", StringComparison.Ordinal))
                    File.GetAttributes(System.IO.Path.GetPathRoot(path));
                return false;
            }
        }

        private static DateTime? LocalDate(long ticks)
        {
            return ticks == 0 ? (DateTime?)null : new DateTime(ticks, DateTimeKind.Unspecified);
        }

        private static BackupAutomationResult Result(string code, string path = "")
        {
            return new BackupAutomationResult { Code = code, Path = path ?? string.Empty };
        }

        private static string LastResult(string code, DateTime utc, string path)
        {
            return code + "|" + utc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) + "|" +
                System.IO.Path.GetFileName(path ?? string.Empty);
        }

        private static string FailureCode(Exception exception, bool network)
        {
            if (exception is OperationCanceledException) return "cancelled";
            if (exception is UnauthorizedAccessException) return "access_denied";
            if (exception is ArgumentException) return "configuration_invalid";
            if (exception is BackupAutomationIdentityException) return "published_identity_invalid";
            if (exception is InvalidDataException) return "published_identity_invalid";
            if (network && exception is IOException) return "network_unavailable";
            return "backup_failed";
        }

        private void Report(string code, string path)
        {
            try
            {
                _diagnostics?.Invoke(new BackupRestoreDiagnostic
                {
                    Operation = "backup_automation", Phase = "complete", ResultCode = code,
                    FileName = System.IO.Path.GetFileName(path ?? string.Empty)
                });
            }
            catch { }
        }

        private sealed class MaintenanceLease : IDisposable
        {
            private SemaphoreSlim _gate;
            private FileStream _lock;
            internal MaintenanceLease(SemaphoreSlim gate, FileStream processLock) { _gate = gate; _lock = processLock; }
            public void Dispose()
            {
                var gate = Interlocked.Exchange(ref _gate, null);
                if (gate == null) return;
                try { Interlocked.Exchange(ref _lock, null)?.Dispose(); }
                finally { gate.Release(); }
            }
        }
    }
}
