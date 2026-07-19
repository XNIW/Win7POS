using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public interface ICatalogSyncClock
    {
        DateTimeOffset UtcNow { get; }
        Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    }

    public sealed class SystemCatalogSyncClock : ICatalogSyncClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken);
        }
    }

    public sealed class CatalogSyncRunRequest
    {
        public CatalogSyncRunRequest(
            string shopKey,
            CatalogSyncTrigger trigger,
            CatalogSyncDecision decision)
        {
            ShopKey = shopKey;
            Trigger = trigger;
            Decision = decision;
        }

        public CatalogSyncDecision Decision { get; }
        public string ShopKey { get; }
        public CatalogSyncTrigger Trigger { get; }
    }

    public sealed class CatalogSyncRunResult
    {
        public CatalogSyncRunResult(
            bool success,
            bool authenticationDenied = false,
            bool offline = false,
            bool hasMore = false,
            bool receivedChanges = false,
            int pages = 0,
            int rows = 0,
            long durationMilliseconds = 0,
            string resumeCursor = null,
            string code = null,
            bool outboxWorkRemaining = false,
            int? nextPollAfterSeconds = null,
            int? heartbeatAfterSeconds = null,
            bool catalogPullAttempted = false,
            string catalogPullSkippedCode = null,
            long? nextOutboxRetryAt = null)
        {
            Success = success;
            AuthenticationDenied = authenticationDenied;
            Offline = offline;
            HasMore = hasMore;
            ReceivedChanges = receivedChanges;
            Pages = Math.Max(0, pages);
            Rows = Math.Max(0, rows);
            DurationMilliseconds = Math.Max(0, durationMilliseconds);
            ResumeCursor = resumeCursor ?? string.Empty;
            Code = code ?? string.Empty;
            OutboxWorkRemaining = outboxWorkRemaining;
            NextPollAfterSeconds = CatalogHeartbeatPolicy.NormalizePollSeconds(nextPollAfterSeconds);
            HeartbeatAfterSeconds = NormalizePositiveSeconds(heartbeatAfterSeconds);
            CatalogPullAttempted = catalogPullAttempted;
            CatalogPullSkippedCode = catalogPullSkippedCode ?? string.Empty;
            NextOutboxRetryAt = nextOutboxRetryAt.HasValue && nextOutboxRetryAt.Value >= 0
                ? nextOutboxRetryAt
                : null;
        }

        public bool AuthenticationDenied { get; }
        public string Code { get; }
        public bool CatalogPullAttempted { get; }
        public string CatalogPullSkippedCode { get; }
        public long DurationMilliseconds { get; }
        public bool HasMore { get; }
        public int? HeartbeatAfterSeconds { get; }
        public int? NextPollAfterSeconds { get; }
        public long? NextOutboxRetryAt { get; }
        public bool Offline { get; }
        public bool OutboxWorkRemaining { get; }
        public int Pages { get; }
        public bool ReceivedChanges { get; }
        public string ResumeCursor { get; }
        public int Rows { get; }
        public bool Success { get; }

        private static int? NormalizePositiveSeconds(int? value)
        {
            return value.HasValue && value.Value > 0 ? value : null;
        }

        internal static CatalogSyncRunResult FromDecision(CatalogSyncDecision decision)
        {
            var blocked = decision != null && decision.Mode == CatalogSyncMode.Blocked;
            return new CatalogSyncRunResult(
                success: !blocked,
                authenticationDenied: decision != null &&
                    string.Equals(
                        decision.DiagnosticCode,
                        "catalog_sync_auth_denied",
                        StringComparison.Ordinal),
                code: decision?.DiagnosticCode);
        }
    }

    public interface ICatalogSyncDiagnosticsSink
    {
        Task RecordAsync(
            CatalogSyncRunRequest request,
            CatalogSyncRunResult result,
            DateTimeOffset recordedAt);
    }

    public sealed class CatalogSyncCoordinator
    {
        private readonly object _sync = new object();
        private readonly string _shopKey;
        private readonly Func<CatalogSyncRunRequest, CancellationToken, Task<CatalogSyncRunResult>> _runner;
        private readonly ICatalogSyncDiagnosticsSink _diagnostics;
        private readonly ICatalogSyncClock _clock;

        private Task<CatalogSyncRunResult> _drainTask;
        private CatalogSyncTrigger _pendingTrigger;
        private CatalogSyncState _pendingState;
        private bool _hasPending;
        private bool _authenticationStopped;

        public CatalogSyncCoordinator(
            string shopKey,
            Func<CatalogSyncRunRequest, CancellationToken, Task<CatalogSyncRunResult>> runner,
            ICatalogSyncDiagnosticsSink diagnostics = null,
            ICatalogSyncClock clock = null)
        {
            _shopKey = NormalizeShopKey(shopKey);
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _diagnostics = diagnostics;
            _clock = clock ?? new SystemCatalogSyncClock();
        }

        public bool AuthenticationStopped
        {
            get
            {
                lock (_sync)
                {
                    return _authenticationStopped;
                }
            }
        }

        public Task<CatalogSyncRunResult> TriggerAsync(
            CatalogSyncTrigger trigger,
            CatalogSyncState state,
            CancellationToken cancellationToken = default)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            lock (_sync)
            {
                if (_authenticationStopped)
                {
                    return Task.FromResult(new CatalogSyncRunResult(
                        success: false,
                        authenticationDenied: true,
                        code: "catalog_sync_auth_stopped"));
                }

                CoalescePending(trigger, state);
                if (_drainTask == null || _drainTask.IsCompleted)
                {
                    _drainTask = DrainAsync(cancellationToken);
                }

                return _drainTask;
            }
        }

        public void ResumeAfterRelink()
        {
            lock (_sync)
            {
                _authenticationStopped = false;
            }
        }

        public Task WaitForScheduleAsync(
            CatalogSyncScheduleDecision schedule,
            CancellationToken cancellationToken)
        {
            if (schedule == null) throw new ArgumentNullException(nameof(schedule));
            if (!schedule.ShouldPoll)
            {
                return Task.CompletedTask;
            }

            return _clock.DelayAsync(schedule.Delay, cancellationToken);
        }

        private async Task<CatalogSyncRunResult> DrainAsync(CancellationToken cancellationToken)
        {
            CatalogSyncRunResult lastResult = null;
            var runsThisDrain = 0;
            while (true)
            {
                CatalogSyncTrigger trigger;
                CatalogSyncState state;
                lock (_sync)
                {
                    if (!_hasPending || _authenticationStopped)
                    {
                        _drainTask = null;
                        return lastResult ?? new CatalogSyncRunResult(true, code: "catalog_sync_noop");
                    }

                    trigger = _pendingTrigger;
                    state = _pendingState;
                    _hasPending = false;
                    _pendingState = null;
                }

                cancellationToken.ThrowIfCancellationRequested();
                runsThisDrain += 1;
                var decision = CatalogSyncPolicy.Evaluate(trigger, state);
                var request = new CatalogSyncRunRequest(_shopKey, trigger, decision);
                if (decision.Mode == CatalogSyncMode.NoOp ||
                    decision.Mode == CatalogSyncMode.Blocked)
                {
                    lastResult = CatalogSyncRunResult.FromDecision(decision);
                }
                else
                {
                    lastResult = await _runner(request, cancellationToken).ConfigureAwait(false);
                    if (lastResult == null)
                    {
                        lastResult = new CatalogSyncRunResult(
                            success: false,
                            code: "catalog_sync_runner_returned_null");
                    }
                }

                var shouldYield = false;
                lock (_sync)
                {
                    if (lastResult.AuthenticationDenied)
                    {
                        _authenticationStopped = true;
                        _hasPending = false;
                        _pendingState = null;
                    }
                    else if (lastResult.HasMore && !_hasPending)
                    {
                        var resumeCursor = string.IsNullOrWhiteSpace(lastResult.ResumeCursor)
                            ? state.PersistedCursor
                            : lastResult.ResumeCursor;
                        _pendingTrigger = CatalogSyncTrigger.PartialResume;
                        _pendingState = new CatalogSyncState(
                            persistedCursor: resumeCursor,
                            bootstrapCompleted: state.BootstrapCompleted,
                            hasShopBinding: state.HasShopBinding,
                            hasPartialCheckpoint: true);
                        _hasPending = true;
                    }

                    if (runsThisDrain >= 2 && _hasPending)
                    {
                        _drainTask = null;
                        shouldYield = true;
                    }
                }

                if (_diagnostics != null)
                {
                    try
                    {
                        await _diagnostics.RecordAsync(request, lastResult, _clock.UtcNow)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // Diagnostics must never weaken auth-stop or lane scheduling.
                    }
                }

                if (shouldYield)
                {
                    return lastResult;
                }
            }
        }

        private void CoalescePending(CatalogSyncTrigger trigger, CatalogSyncState state)
        {
            if (!_hasPending || Priority(trigger) >= Priority(_pendingTrigger))
            {
                _pendingTrigger = trigger;
                _pendingState = state;
            }

            _hasPending = true;
        }

        private static int Priority(CatalogSyncTrigger trigger)
        {
            switch (trigger)
            {
                case CatalogSyncTrigger.AdministratorRepair: return 100;
                case CatalogSyncTrigger.RestoreCompleted: return 90;
                case CatalogSyncTrigger.ShopTransition: return 85;
                case CatalogSyncTrigger.CursorRejected: return 80;
                case CatalogSyncTrigger.ServerFullRequired: return 75;
                case CatalogSyncTrigger.ExactnessMismatch: return 70;
                case CatalogSyncTrigger.FirstBootstrap: return 65;
                case CatalogSyncTrigger.PartialResume: return 60;
                case CatalogSyncTrigger.NetworkRecovered: return 50;
                case CatalogSyncTrigger.Manual: return 45;
                case CatalogSyncTrigger.CatalogImportAcked: return 40;
                case CatalogSyncTrigger.StartOfDay: return 35;
                case CatalogSyncTrigger.Foreground: return 30;
                default: return 10;
            }
        }

        private static string NormalizeShopKey(string shopKey)
        {
            var normalized = (shopKey ?? string.Empty).Trim().ToUpperInvariant();
            if (normalized.Length == 0)
            {
                throw new ArgumentException("A shop key is required.", nameof(shopKey));
            }

            return normalized;
        }
    }

    public sealed class CatalogSyncDiagnosticsRepository : ICatalogSyncDiagnosticsSink
    {
        public const string Prefix = "pos.catalog.sync.";
        private readonly SqliteConnectionFactory _factory;

        public CatalogSyncDiagnosticsRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task RecordAsync(
            CatalogSyncRunRequest request,
            CatalogSyncRunResult result,
            DateTimeOffset recordedAt)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (result == null) throw new ArgumentNullException(nameof(result));

            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                var incremental = request.Decision.Mode == CatalogSyncMode.Incremental ||
                    request.Decision.Mode == CatalogSyncMode.ResumeIncremental;
                var full = request.Decision.Mode == CatalogSyncMode.Full;
                if (!result.CatalogPullAttempted &&
                    result.Success &&
                    !string.IsNullOrWhiteSpace(result.CatalogPullSkippedCode))
                {
                    await IncrementAsync(conn, tx, Prefix + "heartbeat_skip_count").ConfigureAwait(false);
                    await SetAsync(
                        conn,
                        tx,
                        Prefix + "last_skip_code",
                        result.CatalogPullSkippedCode).ConfigureAwait(false);
                }
                else if (result.CatalogPullAttempted && incremental)
                {
                    await IncrementAsync(conn, tx, Prefix + "total_incremental_runs").ConfigureAwait(false);
                }
                else if (result.CatalogPullAttempted && full)
                {
                    await IncrementAsync(conn, tx, Prefix + "total_full_runs").ConfigureAwait(false);
                }

                if (result.CatalogPullAttempted &&
                    (request.Decision.Mode == CatalogSyncMode.ResumeIncremental ||
                     request.Trigger == CatalogSyncTrigger.PartialResume))
                {
                    await IncrementAsync(conn, tx, Prefix + "partial_resume_count").ConfigureAwait(false);
                }

                await SetAsync(conn, tx, Prefix + "last_mode", request.Decision.Mode.ToString()).ConfigureAwait(false);
                await SetAsync(conn, tx, Prefix + "last_trigger", request.Trigger.ToString()).ConfigureAwait(false);
                await SetAsync(
                    conn,
                    tx,
                    Prefix + "last_full_reason",
                    request.Decision.FullReason == CatalogFullSyncReason.None
                        ? string.Empty
                        : request.Decision.FullReason.ToString()).ConfigureAwait(false);
                await SetAsync(conn, tx, Prefix + "pages", result.Pages.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                await SetAsync(conn, tx, Prefix + "rows", result.Rows.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                await SetAsync(conn, tx, Prefix + "duration_ms", result.DurationMilliseconds.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);

                var recorded = recordedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                if (result.Success)
                {
                    await SetAsync(conn, tx, Prefix + "last_success_at", recorded).ConfigureAwait(false);
                    if (result.CatalogPullAttempted && incremental)
                    {
                        await SetAsync(conn, tx, Prefix + "last_incremental_at", recorded).ConfigureAwait(false);
                    }
                    else if (result.CatalogPullAttempted && full)
                    {
                        await SetAsync(conn, tx, Prefix + "last_full_at", recorded).ConfigureAwait(false);
                    }
                }

                var incrementalRuns = await ReadLongAsync(
                    conn,
                    tx,
                    Prefix + "total_incremental_runs").ConfigureAwait(false);
                var fullRuns = await ReadLongAsync(
                    conn,
                    tx,
                    Prefix + "total_full_runs").ConfigureAwait(false);
                var totalRuns = incrementalRuns + fullRuns;
                var ratio = totalRuns == 0
                    ? 0m
                    : decimal.Round(100m * fullRuns / totalRuns, 3, MidpointRounding.AwayFromZero);
                await SetAsync(
                    conn,
                    tx,
                    Prefix + "full_ratio_percent",
                    ratio.ToString("0.###", CultureInfo.InvariantCulture)).ConfigureAwait(false);
                tx.Commit();
            }
        }

        private static Task<int> IncrementAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string key)
        {
            return conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value)
VALUES(@key, '1')
ON CONFLICT(key) DO UPDATE SET value =
  CAST(COALESCE(CAST(app_settings.value AS INTEGER), 0) + 1 AS TEXT);",
                new { key },
                tx);
        }

        private static Task<long> ReadLongAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string key)
        {
            return conn.ExecuteScalarAsync<long>(
                "SELECT COALESCE(CAST(value AS INTEGER), 0) FROM app_settings WHERE key = @key;",
                new { key },
                tx);
        }

        private static Task<int> SetAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string key,
            string value)
        {
            return conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value)
VALUES(@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new { key, value = value ?? string.Empty },
                tx);
        }
    }
}
