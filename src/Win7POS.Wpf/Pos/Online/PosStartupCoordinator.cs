using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure.Security;

namespace Win7POS.Wpf.Pos.Online
{
    internal sealed class PosStartupCoordinatorRuntimeState
    {
        public PosStartupCoordinatorRuntimeState(
            bool isSafeStart,
            bool isRecoveryMode,
            PosAuthenticatedAccessMode accessMode)
        {
            IsSafeStart = isSafeStart;
            IsRecoveryMode = isRecoveryMode;
            AccessMode = accessMode;
        }

        public PosAuthenticatedAccessMode AccessMode { get; }
        public bool IsRecoveryMode { get; }
        public bool IsSafeStart { get; }
    }

    /// <summary>
    /// UI-neutral result of accepting a POS access dialog. The shell renders the
    /// selected mode, while this coordinator remains the authority that derived it.
    /// </summary>
    internal sealed class PosStartupAccessResult
    {
        public PosStartupAccessResult(
            IOperatorSession session,
            PosShellMode shellMode,
            bool catalogSaleSafe)
        {
            Session = session;
            ShellMode = shellMode;
            CatalogSaleSafe = catalogSaleSafe;
        }

        public bool CatalogSaleSafe { get; }
        public IOperatorSession Session { get; }
        public PosShellMode ShellMode { get; }
    }

    /// <summary>
    /// UI-neutral recovery-exit decision. The shell can show its existing
    /// localized message without owning catalog-safety or access-state checks.
    /// </summary>
    internal sealed class PosRecoveryExitValidation
    {
        private PosRecoveryExitValidation(bool canExit, string code)
        {
            CanExit = canExit;
            Code = code;
        }

        public bool CanExit { get; }
        public string Code { get; }

        public static PosRecoveryExitValidation CatalogUnsafe()
        {
            return new PosRecoveryExitValidation(false, "catalog_still_unsafe");
        }

        public static PosRecoveryExitValidation NormalAccessRequired()
        {
            return new PosRecoveryExitValidation(false, "normal_online_access_required");
        }

        public static PosRecoveryExitValidation Ready()
        {
            return new PosRecoveryExitValidation(true, "ready");
        }
    }

    /// <summary>
    /// Owns the non-visual POS startup/runtime lifecycle for one shell. The shell
    /// renders dialogs, navigation and status; database/session/access/recovery
    /// state and online-supervisor sequencing stay here.
    /// </summary>
    internal sealed class PosStartupCoordinator : IDisposable
    {
        private readonly Func<bool> _isSafeStart;
        private readonly Action<string> _logInfo;
        private SqliteConnectionFactory _factory;
        private PosOnlineSyncSupervisorHost _host;
        private SqliteConnectionFactory _schedulerFactory;
        private SqliteConnectionFactory _recoveryFactory;
        private IDisposable _signalRegistration;
        private int _accessMode = (int)PosAuthenticatedAccessMode.Normal;
        private int _disposed;
        private int _fullCatalogRepairInProgress;
        private int _maintenanceResumeRequested;
        private int _recoveryMode;

        public PosStartupCoordinator(
            Func<bool> isSafeStart,
            Action<string> logInfo)
        {
            _isSafeStart = isSafeStart ?? throw new ArgumentNullException(nameof(isSafeStart));
            _logInfo = logInfo;
        }

        public PosAuthenticatedAccessMode AccessMode =>
            (PosAuthenticatedAccessMode)Volatile.Read(ref _accessMode);

        public SqliteConnectionFactory Factory => _schedulerFactory ?? _factory;

        public PosOnlineSyncSupervisorHost Host => _host;

        public bool IsRecoveryMode => Volatile.Read(ref _recoveryMode) != 0;

        public IOperatorSession Session => OperatorSessionHolder.Current;

        public bool IsFullCatalogRepairInProgress =>
            Volatile.Read(ref _fullCatalogRepairInProgress) > 0 ||
            _host?.IsFullCatalogSyncInProgress == true;

        public SqliteConnectionFactory Initialize()
        {
            ThrowIfDisposed();
            if (_factory != null)
            {
                return _factory;
            }

            _logInfo?.Invoke("DbInitializer start");
            var options = PosDbOptions.Default();
            DbInitializer.EnsureCreated(options);
            _logInfo?.Invoke("DbInitializer done");

            var factory = new SqliteConnectionFactory(options);
            var host = new PosOnlineSyncSupervisorHost(factory);
            IDisposable registration = null;
            try
            {
                var registeredHost = host;
                registration = PosOnlineSyncSignalBus.Register(
                    registeredHost.Signal,
                    (lane, trigger, token) => registeredHost.TriggerAsync(lane, trigger, token),
                    () => StopForMaintenanceAsync(registeredHost),
                    token => ResumeAfterMaintenanceAsync(registeredHost, token));

                EnsureSession(factory);
                _factory = factory;
                _schedulerFactory = factory;
                _host = host;
                _signalRegistration = registration;
                return factory;
            }
            catch
            {
                registration?.Dispose();
                host.Dispose();
                throw;
            }
        }

        public IOperatorSession EnsureSession()
        {
            return EnsureSession(RequireFactory());
        }

        public void SetAuthenticatedAccessMode(PosAuthenticatedAccessMode accessMode)
        {
            Interlocked.Exchange(ref _accessMode, (int)accessMode);
        }

        public async Task<PosStartupAccessResult> AcceptAuthenticatedAccessAsync(
            PosAuthenticatedAccessMode accessMode,
            CancellationToken cancellationToken = default)
        {
            var factory = RequireFactory();
            SetAuthenticatedAccessMode(accessMode);
            if (!IsSafeStart && accessMode == PosAuthenticatedAccessMode.Normal)
            {
                await AttachCurrentTrustAsync(cancellationToken).ConfigureAwait(false);
            }

            var session = EnsureSession();
            var catalogSaleSafe = await PosCatalogPullService
                .IsCatalogSaleSafeAsync(factory)
                .ConfigureAwait(false);
            return new PosStartupAccessResult(
                session,
                PosStartupCoordinatorPolicy.DetermineShellMode(accessMode, catalogSaleSafe),
                catalogSaleSafe);
        }

        public async Task EnterRecoveryAsync(SqliteConnectionFactory factory = null)
        {
            _recoveryFactory = factory ?? RequireFactory();
            Interlocked.Exchange(ref _recoveryMode, 1);
            await StopAsync().ConfigureAwait(false);
        }

        public async Task<PosRecoveryExitValidation> ValidateRecoveryExitAsync(
            IOperatorSession session,
            CancellationToken cancellationToken = default)
        {
            if (!HasNormalAuthorizedAccess(session))
            {
                return PosRecoveryExitValidation.NormalAccessRequired();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var catalogSaleSafe = await PosCatalogPullService
                .IsCatalogSaleSafeAsync(GetRecoveryFactory())
                .ConfigureAwait(false);
            return catalogSaleSafe
                ? PosRecoveryExitValidation.Ready()
                : PosRecoveryExitValidation.CatalogUnsafe();
        }

        public async Task<PosRecoveryExitValidation> CompleteRecoveryExitAsync(
            IOperatorSession session)
        {
            if (!HasNormalAuthorizedAccess(session))
            {
                await StopAsync().ConfigureAwait(false);
                return PosRecoveryExitValidation.NormalAccessRequired();
            }

            Interlocked.Exchange(ref _recoveryMode, 0);
            _recoveryFactory = null;
            return PosRecoveryExitValidation.Ready();
        }

        public SqliteConnectionFactory GetRecoveryFactory()
        {
            return _recoveryFactory ?? RequireFactory();
        }

        public bool HasNormalAuthorizedAccess(IOperatorSession session)
        {
            if (session == null || !session.IsLoggedIn)
            {
                return false;
            }

            return PosStartupCoordinatorPolicy.CanCompleteRecoveryExit(
                AccessMode,
                isLoggedIn: true,
                authorizationAllowed: session.EvaluateAuthorizationLease().Allowed);
        }

        public bool HasLeaseFreeLocalRecoveryAccess()
        {
            return PosAccessRecoveryPolicy.IsLeaseFreeLocalRecovery(
                IsRecoveryMode ? PosShellMode.Recovery : PosShellMode.Pos,
                AccessMode);
        }

        public async Task<bool> TryApproveLocalCatalogAsync(
            int userId,
            CancellationToken cancellationToken = default)
        {
            if (userId <= 0)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await new CatalogRecoveryRepository(GetRecoveryFactory())
                .TryApproveLocalCatalogAsync(userId)
                .ConfigureAwait(false);
        }

        public OnlineSyncSupervisorSnapshot GetSnapshot()
        {
            var host = _host;
            return IsDisposed || host == null ? null : host.GetSnapshot();
        }

        public async Task<bool> AttachCurrentTrustAsync(
            CancellationToken cancellationToken = default)
        {
            var host = _host;
            if (IsDisposed || host == null)
            {
                return false;
            }

            return await host.AttachCurrentTrustAsync(cancellationToken)
                .ConfigureAwait(false) != null;
        }

        public void StartBackground()
        {
            var state = CurrentState();
            var host = _host;
            if (IsDisposed || host == null ||
                !PosStartupCoordinatorPolicy.CanStartBackground(
                    state.IsSafeStart,
                    state.IsRecoveryMode))
            {
                return;
            }

            host.StartContinuous();
        }

        public void StartAdaptive(
            SqliteConnectionFactory factory,
            CatalogSyncTrigger initialTrigger)
        {
            var state = CurrentState();
            var host = _host;
            if (factory == null || IsDisposed || host == null ||
                !PosStartupCoordinatorPolicy.CanStartBackground(
                    state.IsSafeStart,
                    state.IsRecoveryMode))
            {
                return;
            }

            _schedulerFactory = factory;
            host.StartContinuous();
            var trigger = MapOnlineSyncTrigger(initialTrigger);
            host.Signal(OnlineSyncLane.Heartbeat, trigger);
            host.Signal(OnlineSyncLane.SalesOutbox, trigger);
            host.Signal(OnlineSyncLane.CatalogImportOutbox, trigger);
        }

        public async Task<CatalogSyncRunResult> TriggerAdaptiveOnlineRefreshAsync(
            CatalogSyncTrigger requestedTrigger,
            CancellationToken cancellationToken,
            bool administratorRepairAuthorized = false,
            bool allowFullDecision = true)
        {
            var timer = Stopwatch.StartNew();
            var state = CurrentState();
            var host = _host;
            if (IsDisposed || host == null || state.IsSafeStart ||
                state.AccessMode == PosAuthenticatedAccessMode.LocalRecovery)
            {
                _logInfo?.Invoke(
                    "category=catalog.sync result=skipped reason=safe_or_recovery_mode");
                return new CatalogSyncRunResult(
                    success: false,
                    code: "sync_disabled_safe_mode");
            }

            var operatorSession = EnsureSession();
            if (operatorSession == null || !operatorSession.EnsureAuthorizationValid())
            {
                return new CatalogSyncRunResult(
                    success: false,
                    authenticationDenied: true,
                    code: "authorization_lease_denied");
            }

            if (requestedTrigger == CatalogSyncTrigger.AdministratorRepair &&
                !administratorRepairAuthorized)
            {
                return new CatalogSyncRunResult(
                    success: false,
                    code: "catalog_sync_administrator_repair_denied");
            }

            if (await host.AttachCurrentTrustAsync(cancellationToken)
                    .ConfigureAwait(false) == null)
            {
                return new CatalogSyncRunResult(
                    success: false,
                    authenticationDenied: true,
                    code: "trusted_session_missing");
            }

            var trigger = MapOnlineSyncTrigger(requestedTrigger);
            var previewDecision = await host.EvaluateCatalogDecisionAsync(
                trigger,
                administratorRepairAuthorized,
                cancellationToken).ConfigureAwait(false);
            if (previewDecision == null)
            {
                return new CatalogSyncRunResult(
                    false,
                    authenticationDenied: true,
                    code: "trusted_generation_changed");
            }
            if (!allowFullDecision && previewDecision.Mode == CatalogSyncMode.Full)
            {
                return new CatalogSyncRunResult(
                    false,
                    code: "catalog_sync_full_repair_required");
            }

            var heartbeatTask = host.TriggerAsync(
                OnlineSyncLane.Heartbeat,
                trigger,
                cancellationToken);
            var salesTask = host.TriggerAsync(
                OnlineSyncLane.SalesOutbox,
                trigger,
                cancellationToken);
            var catalogImportTask = host.TriggerAsync(
                OnlineSyncLane.CatalogImportOutbox,
                trigger,
                cancellationToken);
            await Task.WhenAll(heartbeatTask, salesTask, catalogImportTask).ConfigureAwait(false);

            var heartbeat = heartbeatTask.Result;
            var sales = salesTask.Result;
            var catalogImport = catalogImportTask.Result;
            var authenticationDenied = heartbeat.AuthenticationDenied ||
                sales.AuthenticationDenied ||
                catalogImport.AuthenticationDenied;
            if (authenticationDenied)
            {
                return new CatalogSyncRunResult(
                    false,
                    authenticationDenied: true,
                    durationMilliseconds: timer.ElapsedMilliseconds,
                    code: FirstNonEmpty(
                        heartbeat.AuthenticationDenied ? heartbeat.Code : null,
                        sales.AuthenticationDenied ? sales.Code : null,
                        catalogImport.Code,
                        "auth_denied"));
            }

            var catalogRequested = heartbeat.RequestCatalogNow ||
                catalogImport.RequestCatalogNow ||
                IsExplicitCatalogTrigger(requestedTrigger);
            OnlineSyncLaneOutcome catalog = null;
            var tracksFullRepair = requestedTrigger == CatalogSyncTrigger.AdministratorRepair;
            if (tracksFullRepair) Interlocked.Increment(ref _fullCatalogRepairInProgress);
            try
            {
                if (catalogRequested)
                {
                    catalog = await host.TriggerAsync(
                        OnlineSyncLane.CatalogDelta,
                        trigger,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                if (tracksFullRepair) Interlocked.Decrement(ref _fullCatalogRepairInProgress);
            }

            var offline = heartbeat.Offline || sales.Offline ||
                catalogImport.Offline || catalog?.Offline == true;
            var outboxWorkRemaining = sales.HasImmediateMore ||
                sales.NextRetryAt.HasValue ||
                catalogImport.HasImmediateMore ||
                catalogImport.NextRetryAt.HasValue;
            return new CatalogSyncRunResult(
                success: heartbeat.Success &&
                    sales.Success &&
                    catalogImport.Success &&
                    (!catalogRequested || catalog?.Success == true),
                authenticationDenied: catalog?.AuthenticationDenied == true,
                offline: offline,
                hasMore: catalog?.CatalogHasMore == true,
                receivedChanges: (catalog?.CatalogRowsApplied ?? 0) > 0,
                pages: catalog?.CatalogPagesProcessed ?? 0,
                rows: catalog?.CatalogRowsApplied ?? 0,
                durationMilliseconds: timer.ElapsedMilliseconds,
                code: catalogRequested
                    ? FirstNonEmpty(catalog?.Code, "catalog_sync_failed")
                    : FirstNonEmpty(heartbeat.Code, "catalog_pull_not_requested"),
                outboxWorkRemaining: outboxWorkRemaining,
                nextPollAfterSeconds: heartbeat.NextPollAfterSeconds,
                catalogPullAttempted: catalogRequested,
                catalogPullSkippedCode: catalogRequested ? string.Empty : heartbeat.Code,
                nextOutboxRetryAt: MinimumRetryAt(
                    sales.NextRetryAt,
                    catalogImport.NextRetryAt));
        }

        public Task StopAsync()
        {
            var host = _host;
            return IsDisposed || host == null ? Task.CompletedTask : host.StopAsync();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _maintenanceResumeRequested, 0);
            try
            {
                _signalRegistration?.Dispose();
                _signalRegistration = null;
            }
            finally
            {
                _host?.Dispose();
                _host = null;
                _recoveryFactory = null;
            }
        }

        private PosStartupCoordinatorRuntimeState CurrentState()
        {
            return new PosStartupCoordinatorRuntimeState(
                IsSafeStart,
                IsRecoveryMode,
                AccessMode);
        }

        private static IOperatorSession EnsureSession(SqliteConnectionFactory factory)
        {
            if (OperatorSessionHolder.Current == null)
            {
                OperatorSessionHolder.Current = new OperatorSession(
                    new UserRepository(factory),
                    new SecurityRepository(factory));
            }

            return OperatorSessionHolder.Current;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static bool IsExplicitCatalogTrigger(CatalogSyncTrigger trigger)
        {
            switch (trigger)
            {
                case CatalogSyncTrigger.FirstBootstrap:
                case CatalogSyncTrigger.StartOfDay:
                case CatalogSyncTrigger.Manual:
                case CatalogSyncTrigger.CatalogImportAcked:
                case CatalogSyncTrigger.PartialResume:
                case CatalogSyncTrigger.CursorRejected:
                case CatalogSyncTrigger.ServerFullRequired:
                case CatalogSyncTrigger.ShopTransition:
                case CatalogSyncTrigger.RestoreCompleted:
                case CatalogSyncTrigger.ExactnessMismatch:
                case CatalogSyncTrigger.AdministratorRepair:
                    return true;
                default:
                    return false;
            }
        }

        private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        private bool IsSafeStart
        {
            get
            {
                try
                {
                    return _isSafeStart();
                }
                catch
                {
                    return true;
                }
            }
        }

        private static OnlineSyncLaneTrigger MapOnlineSyncTrigger(CatalogSyncTrigger trigger)
        {
            switch (trigger)
            {
                case CatalogSyncTrigger.StartOfDay:
                case CatalogSyncTrigger.FirstBootstrap:
                case CatalogSyncTrigger.RestoreCompleted:
                case CatalogSyncTrigger.ExactnessMismatch:
                    return OnlineSyncLaneTrigger.StartOfDay;
                case CatalogSyncTrigger.CatalogImportAcked:
                    return OnlineSyncLaneTrigger.ImportAcknowledged;
                case CatalogSyncTrigger.NetworkRecovered:
                    return OnlineSyncLaneTrigger.NetworkRecovered;
                case CatalogSyncTrigger.PartialResume:
                    return OnlineSyncLaneTrigger.PartialResume;
                case CatalogSyncTrigger.Foreground:
                    return OnlineSyncLaneTrigger.Foreground;
                case CatalogSyncTrigger.Manual:
                    return OnlineSyncLaneTrigger.Manual;
                case CatalogSyncTrigger.AdministratorRepair:
                    return OnlineSyncLaneTrigger.AdministratorRepair;
                default:
                    return OnlineSyncLaneTrigger.Periodic;
            }
        }

        private static long? MinimumRetryAt(long? first, long? second)
        {
            if (!first.HasValue) return second;
            if (!second.HasValue) return first;
            return Math.Min(first.Value, second.Value);
        }

        private async Task ResumeAfterMaintenanceAsync(
            PosOnlineSyncSupervisorHost registeredHost,
            CancellationToken cancellationToken)
        {
            var shouldResume = Interlocked.CompareExchange(
                ref _maintenanceResumeRequested,
                1,
                1) == 1;
            var state = CurrentState();
            if (!PosStartupCoordinatorPolicy.CanResumeAfterMaintenance(
                    state.IsSafeStart,
                    state.IsRecoveryMode,
                    state.AccessMode,
                    shouldResume))
            {
                Interlocked.Exchange(ref _maintenanceResumeRequested, 0);
                return;
            }

            try
            {
                if (await registeredHost.AttachCurrentTrustAsync(cancellationToken)
                        .ConfigureAwait(false) == null)
                {
                    throw new InvalidOperationException(
                        "The trusted sync generation could not be resumed.");
                }
                registeredHost.StartContinuous();
                Interlocked.Exchange(ref _maintenanceResumeRequested, 0);
            }
            catch
            {
                Interlocked.Exchange(ref _maintenanceResumeRequested, 1);
                throw;
            }
        }

        private SqliteConnectionFactory RequireFactory()
        {
            var factory = Factory;
            if (factory == null)
            {
                throw new InvalidOperationException(
                    "The POS startup coordinator has not been initialized.");
            }

            return factory;
        }

        private async Task StopForMaintenanceAsync(PosOnlineSyncSupervisorHost registeredHost)
        {
            var state = CurrentState();
            var resumeAllowed = !state.IsSafeStart &&
                !state.IsRecoveryMode &&
                state.AccessMode == PosAuthenticatedAccessMode.Normal;
            var stoppedState = await registeredHost.StopAsync().ConfigureAwait(false);
            var shouldResume = resumeAllowed &&
                stoppedState.HadGeneration &&
                stoppedState.WasContinuous;
            Interlocked.Exchange(ref _maintenanceResumeRequested, shouldResume ? 1 : 0);
        }

        private void ThrowIfDisposed()
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(PosStartupCoordinator));
            }
        }
    }
}
