using System;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;
using Win7POS.Core.Receipt;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Online
{
    internal sealed class PosAuthenticatedTrustTransition
    {
        public PosAuthenticatedTrustTransition(
            long attemptId,
            long authorizationEpoch,
            OnlineSyncGenerationPredecessorState expectedCurrentState)
        {
            AttemptId = attemptId;
            AuthorizationEpoch = authorizationEpoch;
            ExpectedCurrentState = expectedCurrentState ??
                throw new ArgumentNullException(nameof(expectedCurrentState));
        }

        public long AttemptId { get; }
        public long AuthorizationEpoch { get; }
        public OnlineSyncGenerationPredecessorState ExpectedCurrentState { get; }
    }

    public sealed class PosOnlineSyncHostStopState
    {
        public PosOnlineSyncHostStopState(
            bool hadGeneration,
            bool wasContinuous)
        {
            HadGeneration = hadGeneration;
            WasContinuous = wasContinuous;
        }

        public bool HadGeneration { get; }
        public bool WasContinuous { get; }
    }

    /// <summary>
    /// Owns the one online-sync supervisor for the currently trusted generation.
    /// Generation transitions are serialized here before a replacement supervisor
    /// can issue requests or commit remote results.
    /// </summary>
    public sealed class PosOnlineSyncSupervisorHost : IDisposable
    {
        private const string CatalogLastErrorSettingKey = "pos.catalog.last_error";
        private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(4);
        private static long _lastActivationTimestamp;
        private long _latestAuthenticationAttempt;
        private readonly object _stateGate = new object();
        private readonly SemaphoreSlim _transitionGate = new SemaphoreSlim(1, 1);
        private readonly SqliteConnectionFactory _factory;
        private readonly FileLogger _logger;
        private readonly PosTrustedDeviceStore _store;
        private OnlineSyncSupervisor _supervisor;
        private OnlineSyncGeneration _generation;
        private int _fullCatalogRuns;
        private bool _continuousStarted;
        private bool _disposed;

        public PosOnlineSyncSupervisorHost(SqliteConnectionFactory factory)
            : this(
                factory,
                new PosTrustedDeviceStore(),
                new FileLogger("PosOnlineSyncSupervisorHost"))
        {
        }

        internal PosOnlineSyncSupervisorHost(
            SqliteConnectionFactory factory,
            PosTrustedDeviceStore store,
            FileLogger logger)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public OnlineSyncGeneration CurrentGeneration
        {
            get
            {
                lock (_stateGate)
                    return _generation;
            }
        }

        public bool IsFullCatalogSyncInProgress =>
            Volatile.Read(ref _fullCatalogRuns) > 0;

        public OnlineSyncSupervisorSnapshot GetSnapshot()
        {
            lock (_stateGate)
                return _supervisor?.GetSnapshot();
        }

        public Task<OnlineSyncGeneration> AttachCurrentTrustAsync(
            CancellationToken cancellationToken = default)
        {
            return AttachCurrentTrustCoreAsync(cancellationToken);
        }

        internal async Task<PosAuthenticatedTrustTransition>
            BeginAuthenticatedTrustTransitionAsync(
                CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (!PosOnlineSyncRevocationLatch.TryCaptureAuthorizationEpoch(
                        out var authorizationEpoch) ||
                    PosOnlineSyncSignalBus.IsMaintenanceActive)
                {
                    return null;
                }

                var attemptId = Interlocked.Increment(ref _latestAuthenticationAttempt);
                cancellationToken.ThrowIfCancellationRequested();
                var generationRepository =
                    new OnlineSyncGenerationRepository(_factory);
                var expectedCurrentState = await generationRepository
                    .ReadCurrentPredecessorAsync().ConfigureAwait(false);
                if (attemptId != Interlocked.Read(ref _latestAuthenticationAttempt) ||
                    !PosOnlineSyncRevocationLatch.IsAuthorizationEpochCurrent(
                        authorizationEpoch) ||
                    PosOnlineSyncSignalBus.IsMaintenanceActive)
                {
                    return null;
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new PosAuthenticatedTrustTransition(
                    attemptId,
                    authorizationEpoch,
                    expectedCurrentState);
            }
            finally
            {
                _transitionGate.Release();
            }
        }

        internal async Task<OnlineSyncGeneration> ActivateAuthenticatedTrustAsync(
            PosFirstLoginResponse response,
            string generationId,
            PosAuthenticatedTrustTransition transition,
            Func<Task<IDisposable>> applyAuthorizedLocalTransitionAsync,
            Func<OnlineSyncGeneration, Task> persistAuthenticatedLocalStateAsync,
            CancellationToken cancellationToken = default)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            ReceiptShopMetadataPolicy.EnsureValidRemoteShop(response.Shop);
            if (persistAuthenticatedLocalStateAsync == null)
            {
                throw new ArgumentNullException(
                    nameof(persistAuthenticatedLocalStateAsync));
            }

            ThrowIfDisposed();
            await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            OnlineSyncGeneration nextGeneration = null;
            OnlineSyncSupervisor candidateSupervisor = null;
            var authenticatedCommitAttempted = false;
            try
            {
                ThrowIfDisposed();
                if (transition == null ||
                    transition.AttemptId !=
                        Interlocked.Read(ref _latestAuthenticationAttempt) ||
                    !PosOnlineSyncRevocationLatch.IsAuthorizationEpochCurrent(
                        transition.AuthorizationEpoch) ||
                    PosOnlineSyncSignalBus.IsMaintenanceActive)
                {
                    return null;
                }
                cancellationToken.ThrowIfCancellationRequested();

                nextGeneration = new OnlineSyncGeneration(
                    generationId,
                    response.Session.PosSessionId,
                    response.Device.ShopDeviceId,
                    response.Shop.ShopId,
                    response.Shop.ShopCode,
                    response.Staff.StaffId,
                    response.Staff.CredentialVersion);
                if (PosOnlineSyncRevocationLatch.IsRevoked(nextGeneration))
                    return null;

                var generationRepository =
                    new OnlineSyncGenerationRepository(_factory);
                // This durable compare-and-swap is the authenticated transition's
                // linearization point. No catalog reset or trusted-file write may
                // happen until the full predecessor state still matches.
                // Invalidate first so a synchronous permission check cannot reuse
                // the predecessor in the narrow interval after the SQLite commit.
                if (!PosOnlineSyncRevocationLatch.TryInvalidateAuthorizationState(
                    transition.AuthorizationEpoch))
                {
                    return null;
                }
                authenticatedCommitAttempted = true;
                await generationRepository.ActivateAndRecoverAsync(
                        nextGeneration,
                        NextActivationTimestamp(),
                        transition.ExpectedCurrentState)
                    .ConfigureAwait(false);

                OnlineSyncSupervisor previous;
                lock (_stateGate)
                {
                    previous = _supervisor;
                    _supervisor = null;
                    _generation = null;
                    _continuousStarted = false;
                }
                if (previous != null)
                {
                    try
                    {
                        await previous.StopAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        previous.Dispose();
                    }
                }

                IDisposable localTransitionLease = null;
                try
                {
                    if (applyAuthorizedLocalTransitionAsync != null)
                    {
                        localTransitionLease = await applyAuthorizedLocalTransitionAsync()
                            .ConfigureAwait(false);
                    }
                    await persistAuthenticatedLocalStateAsync(nextGeneration)
                        .ConfigureAwait(false);
                    // The protected trust file is the final local commit marker.
                    // A crash before this write leaves DB/file generations
                    // mismatched and therefore fails closed on restart.
                    _store.SaveFirstLogin(response, generationId);
                }
                finally
                {
                    localTransitionLease?.Dispose();
                }

                candidateSupervisor = CreateSupervisor(nextGeneration);
                lock (_stateGate)
                {
                    ThrowIfDisposed();
                    _generation = nextGeneration;
                    _supervisor = candidateSupervisor;
                    candidateSupervisor = null;
                }
                return nextGeneration;
            }
            catch (Exception activationFailure)
            {
                if (!authenticatedCommitAttempted || nextGeneration == null)
                    throw;

                var failures = new System.Collections.Generic.List<Exception>
                {
                    activationFailure
                };
                if (candidateSupervisor != null)
                {
                    try { candidateSupervisor.Dispose(); }
                    catch (Exception ex) { failures.Add(ex); }
                }

                var generationRepository =
                    new OnlineSyncGenerationRepository(_factory);
                var preserveResidentSupervisor = false;
                try
                {
                    var durableState = await generationRepository
                        .ReadCurrentPredecessorAsync().ConfigureAwait(false);
                    preserveResidentSupervisor = PredecessorStatesMatch(
                        durableState,
                        transition.ExpectedCurrentState);
                }
                catch (Exception ex)
                {
                    // If durable state cannot be classified after an ambiguous
                    // commit failure, detach the resident supervisor fail-closed.
                    failures.Add(ex);
                }

                if (!preserveResidentSupervisor)
                {
                    OnlineSyncSupervisor rollbackSupervisor;
                    lock (_stateGate)
                    {
                        rollbackSupervisor = _supervisor;
                        _supervisor = null;
                        _generation = null;
                        _continuousStarted = false;
                    }
                    if (rollbackSupervisor != null)
                    {
                        try
                        {
                            await rollbackSupervisor.StopAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            failures.Add(ex);
                        }
                        finally
                        {
                            try { rollbackSupervisor.Dispose(); }
                            catch (Exception ex) { failures.Add(ex); }
                        }
                    }
                }

                PosOnlineSyncRevocationLatch.Revoke(nextGeneration);
                try
                {
                    var stopped = await generationRepository.StopIfCurrentAsync(
                            nextGeneration,
                            "bootstrap_local_persistence_failed",
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                        .ConfigureAwait(false);
                    if (!stopped &&
                        await generationRepository.IsCurrentAndActiveAsync(nextGeneration)
                            .ConfigureAwait(false))
                    {
                        throw new InvalidOperationException(
                            "The failed authenticated generation could not be stopped.");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }

                try
                {
                    var cleared = _store.TryClear(nextGeneration.GenerationId);
                    if (!cleared &&
                        _store.TryReadGeneration(nextGeneration, out _, out _))
                    {
                        throw new InvalidOperationException(
                            "The failed authenticated trusted-session file could not be removed.");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }

                if (failures.Count == 1)
                    throw;
                throw new AggregateException(
                    "Authenticated trust activation and its rollback failed.",
                    failures);
            }
            finally
            {
                _transitionGate.Release();
            }
        }

        private async Task<OnlineSyncGeneration> AttachCurrentTrustCoreAsync(
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (PosOnlineSyncRevocationLatch.IsAuthorizationMaintenanceActive ||
                    PosOnlineSyncSignalBus.IsMaintenanceActive)
                {
                    return null;
                }
                if (!_store.TryRead(out var session) ||
                    !TryCreateGeneration(session, out var nextGeneration))
                {
                    return null;
                }
                if (PosOnlineSyncRevocationLatch.IsRevoked(nextGeneration))
                    return null;

                OnlineSyncSupervisor previous;
                lock (_stateGate)
                {
                    if (_generation != null &&
                        string.Equals(
                            _generation.Fingerprint,
                            nextGeneration.Fingerprint,
                            StringComparison.Ordinal) &&
                        _supervisor != null)
                    {
                        return _generation;
                    }
                }

                var generationRepository = new OnlineSyncGenerationRepository(_factory);
                if (!await generationRepository
                    .AttachOrInitializeCurrentAsync(
                        nextGeneration,
                        NextActivationTimestamp()).ConfigureAwait(false))
                {
                    return null;
                }

                lock (_stateGate)
                {
                    previous = _supervisor;
                    _supervisor = null;
                    _generation = null;
                    _continuousStarted = false;
                }
                if (previous != null)
                {
                    try
                    {
                        await previous.StopAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        previous.Dispose();
                    }
                }

                var supervisor = CreateSupervisor(nextGeneration);
                lock (_stateGate)
                {
                    ThrowIfDisposed();
                    _generation = nextGeneration;
                    _supervisor = supervisor;
                }
                return nextGeneration;
            }
            finally
            {
                _transitionGate.Release();
            }
        }

        public void StartContinuous()
        {
            if (PosOnlineSyncRevocationLatch.IsAuthorizationMaintenanceActive ||
                PosOnlineSyncSignalBus.IsMaintenanceActive)
            {
                return;
            }
            OnlineSyncSupervisor supervisor;
            lock (_stateGate)
            {
                if (_disposed || _supervisor == null || _continuousStarted)
                    return;
                _continuousStarted = true;
                supervisor = _supervisor;
            }
            supervisor.Start();
        }

        public void Signal(OnlineSyncLane lane, OnlineSyncLaneTrigger trigger)
        {
            if (PosOnlineSyncRevocationLatch.IsAuthorizationMaintenanceActive ||
                PosOnlineSyncSignalBus.IsMaintenanceActive)
            {
                return;
            }
            OnlineSyncSupervisor supervisor;
            lock (_stateGate)
                supervisor = _disposed ? null : _supervisor;
            supervisor?.Signal(lane, trigger);
        }

        public Task<OnlineSyncLaneOutcome> TriggerAsync(
            OnlineSyncLane lane,
            OnlineSyncLaneTrigger trigger,
            CancellationToken cancellationToken = default)
        {
            if (PosOnlineSyncRevocationLatch.IsAuthorizationMaintenanceActive ||
                PosOnlineSyncSignalBus.IsMaintenanceActive)
            {
                return Task.FromResult(new OnlineSyncLaneOutcome(
                    false,
                    "sync_maintenance_active",
                    terminal: true));
            }
            OnlineSyncSupervisor supervisor;
            lock (_stateGate)
                supervisor = _disposed ? null : _supervisor;
            return supervisor == null
                ? Task.FromResult(new OnlineSyncLaneOutcome(
                    false,
                    "sync_supervisor_inactive"))
                : supervisor.TriggerAsync(lane, trigger, cancellationToken);
        }

        public async Task<CatalogSyncDecision> EvaluateCatalogDecisionAsync(
            OnlineSyncLaneTrigger trigger,
            bool administratorRepairAuthorized,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnlineSyncGeneration generation;
            lock (_stateGate)
                generation = _disposed ? null : _generation;
            if (generation == null ||
                !_store.TryReadGeneration(generation, out var trustedSession, out _))
            {
                return null;
            }

            var context = await BuildCatalogSyncContextAsync(
                trustedSession,
                generation,
                MapCatalogTrigger(trigger),
                administratorRepairAuthorized).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return context.Decision;
        }

        public async Task<OnlineSyncStartOfDayResult> RunStartOfDayAsync(
            bool catalogRequired,
            CancellationToken cancellationToken)
        {
            OnlineSyncSupervisor supervisor;
            lock (_stateGate)
                supervisor = _disposed ? null : _supervisor;
            if (supervisor == null)
                return null;

            return await supervisor.TriggerStartOfDayAsync(
                catalogRequired,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<PosOnlineSyncHostStopState> StopAsync()
        {
            await _transitionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                OnlineSyncSupervisor supervisor;
                bool hadGeneration;
                bool wasContinuous;
                lock (_stateGate)
                {
                    supervisor = _supervisor;
                    hadGeneration = _generation != null;
                    wasContinuous = _continuousStarted;
                    _supervisor = null;
                    _generation = null;
                    _continuousStarted = false;
                }
                if (supervisor != null)
                {
                    try
                    {
                        await supervisor.StopAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        supervisor.Dispose();
                    }
                }
                return new PosOnlineSyncHostStopState(
                    hadGeneration,
                    wasContinuous);
            }
            finally
            {
                _transitionGate.Release();
            }
        }

        internal async Task<bool> RejectAuthenticatedTrustTransitionAsync(
            PosAuthenticatedTrustTransition transition,
            string reason)
        {
            if (transition == null) return false;
            await _transitionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (transition.AttemptId !=
                        Interlocked.Read(ref _latestAuthenticationAttempt) ||
                    !PosOnlineSyncRevocationLatch.IsAuthorizationEpochCurrent(
                        transition.AuthorizationEpoch) ||
                    PosOnlineSyncSignalBus.IsMaintenanceActive)
                {
                    return false;
                }

                var currentState = await new OnlineSyncGenerationRepository(_factory)
                    .ReadCurrentPredecessorAsync().ConfigureAwait(false);
                if (transition.AttemptId !=
                        Interlocked.Read(ref _latestAuthenticationAttempt) ||
                    !PosOnlineSyncRevocationLatch.IsAuthorizationEpochCurrent(
                        transition.AuthorizationEpoch) ||
                    PosOnlineSyncSignalBus.IsMaintenanceActive ||
                    !PredecessorStatesMatch(
                        currentState,
                        transition.ExpectedCurrentState))
                {
                    return false;
                }

                await RevokeCurrentTrustCoreAsync(
                        reason,
                        transition.ExpectedCurrentState)
                    .ConfigureAwait(false);
                return true;
            }
            finally
            {
                _transitionGate.Release();
            }
        }

        public async Task RevokeCurrentTrustAsync(string reason)
        {
            await _transitionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await RevokeCurrentTrustCoreAsync(reason).ConfigureAwait(false);
            }
            finally
            {
                _transitionGate.Release();
            }
        }

        private async Task RevokeCurrentTrustCoreAsync(
            string reason,
            OnlineSyncGenerationPredecessorState expectedCurrentState = null)
        {
            if (expectedCurrentState != null && !expectedCurrentState.Exists)
            {
                PosOnlineSyncRevocationLatch.RevokeFingerprint(null);
                return;
            }

            OnlineSyncSupervisor supervisor;
            OnlineSyncGeneration generation;
            lock (_stateGate)
            {
                var residentMatchesScope = expectedCurrentState == null ||
                    (_generation != null &&
                     string.Equals(
                         _generation.Fingerprint,
                         expectedCurrentState.Fingerprint,
                         StringComparison.Ordinal));
                if (residentMatchesScope)
                {
                    supervisor = _supervisor;
                    generation = _generation;
                    _supervisor = null;
                    _generation = null;
                    _continuousStarted = false;
                }
                else
                {
                    supervisor = null;
                    generation = null;
                }
            }

            if (generation == null &&
                _store.TryRead(out var storedSession) &&
                TryCreateGeneration(storedSession, out var storedGeneration) &&
                (expectedCurrentState == null ||
                 string.Equals(
                     storedGeneration.Fingerprint,
                     expectedCurrentState.Fingerprint,
                     StringComparison.Ordinal)))
            {
                generation = storedGeneration;
            }

            if (expectedCurrentState == null)
            {
                PosOnlineSyncRevocationLatch.Revoke(generation);
            }
            else
            {
                PosOnlineSyncRevocationLatch.RevokeFingerprint(
                    expectedCurrentState.Fingerprint);
            }
            var failures = new System.Collections.Generic.List<Exception>();
            var supervisorStopTask = Task.CompletedTask;
            if (supervisor != null)
            {
                try
                {
                    // Stop() cancels the shared lifetime synchronously before
                    // returning its drain task. Persist the durable fence while
                    // canceled lanes are still unwinding.
                    supervisorStopTask = supervisor.StopAsync();
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            if (generation != null || expectedCurrentState != null)
            {
                var generationRepository = new OnlineSyncGenerationRepository(_factory);
                try
                {
                    var stoppedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var stopped = expectedCurrentState == null
                        ? await generationRepository.StopIfCurrentAsync(
                                generation,
                                reason,
                                stoppedAt).ConfigureAwait(false)
                        : await generationRepository.StopIfCurrentPredecessorAsync(
                                expectedCurrentState,
                                reason,
                                stoppedAt).ConfigureAwait(false);
                    var remainsCurrent = expectedCurrentState == null
                        ? !stopped && await generationRepository
                            .IsCurrentAndActiveAsync(generation).ConfigureAwait(false)
                        : !stopped && PredecessorStatesMatch(
                            await generationRepository.ReadCurrentPredecessorAsync()
                                .ConfigureAwait(false),
                            expectedCurrentState) &&
                          expectedCurrentState.Active;
                    if (remainsCurrent)
                    {
                        throw new InvalidOperationException(
                            "The current sync generation could not be stopped.");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }

                if (generation != null)
                {
                    try
                    {
                        var cleared = _store.TryClear(generation.GenerationId);
                        if (!cleared &&
                            _store.TryReadGeneration(generation, out _, out _))
                        {
                            throw new InvalidOperationException(
                                "The revoked trusted-session file could not be removed.");
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex);
                    }
                }
            }

            if (supervisor != null)
            {
                try
                {
                    await supervisorStopTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
                finally
                {
                    supervisor.Dispose();
                }
            }

            if (failures.Count == 1)
                throw failures[0];
            if (failures.Count > 1)
                throw new AggregateException(
                    "Trusted-session revocation did not complete cleanly.",
                    failures);
        }

        public void Dispose()
        {
            lock (_stateGate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            Task stopTask;
            try { stopTask = StopAsync(); }
            catch { stopTask = Task.CompletedTask; }
            _ = stopTask.ContinueWith(
                _ => _transitionGate.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private Task<OnlineSyncLaneOutcome> RunLaneAsync(
            OnlineSyncLaneExecutionContext context,
            OnlineSyncLaneTrigger trigger,
            CancellationToken cancellationToken)
        {
            switch (context.Lane)
            {
                case OnlineSyncLane.Heartbeat:
                    return RunHeartbeatAsync(context, trigger, cancellationToken);
                case OnlineSyncLane.SalesOutbox:
                    return RunSalesAsync(context, cancellationToken);
                case OnlineSyncLane.CatalogImportOutbox:
                    return RunCatalogImportAsync(context, cancellationToken);
                case OnlineSyncLane.CatalogDelta:
                    return RunCatalogAsync(context, trigger, cancellationToken);
                default:
                    return Task.FromResult(new OnlineSyncLaneOutcome(
                        false,
                        "sync_lane_unknown"));
            }
        }

        private async Task<OnlineSyncLaneOutcome> RunHeartbeatAsync(
            OnlineSyncLaneExecutionContext context,
            OnlineSyncLaneTrigger trigger,
            CancellationToken cancellationToken)
        {
            if (!PosAdminWebOptions.TryLoad(out var options, out _))
                return new OnlineSyncLaneOutcome(false, "admin_web_config_missing");
            if (!_store.TryReadGeneration(
                context.Generation,
                out var trustedSession,
                out _))
            {
                return new OnlineSyncLaneOutcome(false, "trusted_generation_changed");
            }

            var catalogState = new CatalogShopStateRepository(_factory);
            var binding = await catalogState.EnsureAndLoadCursorAsync(
                trustedSession.ShopId,
                trustedSession.ShopCode,
                context.Generation).ConfigureAwait(false);
            if (!binding.IsValid)
                return new OnlineSyncLaneOutcome(false, binding.Code);
            var revisionState = await catalogState.LoadRevisionStateAsync(
                trustedSession.ShopId,
                trustedSession.ShopCode,
                binding.Epoch).ConfigureAwait(false);

            OnlineSyncRequestCredentials usedCredentials = null;
            PosOnlineResult<PosHeartbeatResponse> heartbeat = null;
            using (var client = new PosAdminWebClient(options))
            {
                for (var credentialAttempt = 0; credentialAttempt < 2; credentialAttempt++)
                {
                    usedCredentials = null;
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken))
                    {
                        timeout.CancelAfter(HeartbeatTimeout);
                        try
                        {
                            heartbeat = await context.ExecuteCredentialedRequestAsync(
                                (credentials, token) =>
                                {
                                    usedCredentials = credentials;
                                    return client.HeartbeatAsync(new PosHeartbeatRequest
                                    {
                                        AppVersion = typeof(PosOnlineSyncSupervisorHost)
                                            .Assembly.GetName().Version?.ToString(),
                                        CatalogRevision = revisionState.CommittedRevision,
                                        DeviceToken = credentials.DeviceToken,
                                        PosSessionId = credentials.PosSessionId,
                                        SessionToken = credentials.SessionToken,
                                        ShopDeviceId = credentials.ShopDeviceId
                                    }, token);
                                },
                                response =>
                                {
                                    var code = FirstNonEmpty(
                                        response?.Value?.Code,
                                        response?.Code,
                                        "auth_denied");
                                    return response != null &&
                                        (response.Denied ||
                                         SharedAuthStopPolicy.IsAuthenticationDenied(code))
                                            ? code
                                            : string.Empty;
                                },
                                timeout.Token).ConfigureAwait(false);
                            break;
                        }
                        catch (OnlineSyncCredentialsChangedException) when (
                            credentialAttempt == 0)
                        {
                            // A concurrent heartbeat committed a rotated token.
                        }
                        catch (OnlineSyncCredentialsChangedException)
                        {
                            return new OnlineSyncLaneOutcome(
                                false,
                                "heartbeat_credentials_changed");
                        }
                        catch (OperationCanceledException) when (
                            !cancellationToken.IsCancellationRequested)
                        {
                            return new OnlineSyncLaneOutcome(
                                false,
                                "heartbeat_timeout",
                                offline: true);
                        }
                    }
                }
            }

            var responseCode = FirstNonEmpty(
                heartbeat?.Value?.Code,
                heartbeat?.Code,
                "heartbeat_not_ok");
            if (heartbeat == null ||
                !heartbeat.Success ||
                heartbeat.Value == null ||
                !heartbeat.Value.Ok ||
                heartbeat.Value.Session == null)
            {
                var denied = heartbeat != null &&
                    (heartbeat.Denied ||
                     SharedAuthStopPolicy.IsAuthenticationDenied(responseCode));
                return denied
                    ? OnlineSyncLaneOutcome.AuthDenied(responseCode)
                    : new OnlineSyncLaneOutcome(
                        false,
                        responseCode,
                        offline: IsOffline(responseCode));
            }

            if (usedCredentials == null ||
                !_store.TryReadGeneration(
                    context.Generation,
                    out var expectedSession,
                    out var currentCredentialStamp) ||
                !string.Equals(
                    usedCredentials.CredentialStamp,
                    currentCredentialStamp,
                    StringComparison.Ordinal) ||
                !await context.IsCurrentAsync().ConfigureAwait(false) ||
                !_store.TrySaveHeartbeat(
                    context.Generation.GenerationId,
                    expectedSession,
                    heartbeat.Value,
                    out trustedSession))
            {
                return new OnlineSyncLaneOutcome(false, "heartbeat_commit_stale");
            }
            if (!await context.IsCurrentAsync().ConfigureAwait(false))
            {
                _store.TryClear(context.Generation.GenerationId);
                return new OnlineSyncLaneOutcome(false, "heartbeat_commit_stale");
            }

            var syncContext = await BuildCatalogSyncContextAsync(
                trustedSession,
                context.Generation,
                MapCatalogTrigger(trigger),
                trigger == OnlineSyncLaneTrigger.AdministratorRepair)
                .ConfigureAwait(false);
            var terminalCatalogBlock = syncContext.Decision.Mode == CatalogSyncMode.Blocked &&
                string.Equals(
                    syncContext.Decision.DiagnosticCode,
                    CatalogPaginationSafetyPolicy.AmbiguousEndCode,
                    StringComparison.OrdinalIgnoreCase);
            var decision = CatalogHeartbeatPolicy.Evaluate(
                heartbeat.Value.CatalogRevision,
                heartbeat.Value.CatalogChangesAvailable,
                heartbeat.Value.NextPollAfterSeconds,
                revisionState.CommittedRevision,
                fullOrRepairRequired: syncContext.Decision.Mode == CatalogSyncMode.Full ||
                    (syncContext.Decision.Mode == CatalogSyncMode.Blocked &&
                     !terminalCatalogBlock),
                partialCursorPending: syncContext.State.HasPartialCheckpoint,
                manualTrigger: trigger == OnlineSyncLaneTrigger.Manual ||
                    trigger == OnlineSyncLaneTrigger.AdministratorRepair,
                catalogImportAckPending: revisionState.ImportAckReconciliationPending);
            if (decision.ObservedRevision.Length > 0)
            {
                await catalogState.StoreObservedRevisionAsync(
                    trustedSession.ShopId,
                    trustedSession.ShopCode,
                    decision.ObservedRevision,
                    DateTimeOffset.UtcNow,
                    binding.Epoch,
                    context.Generation).ConfigureAwait(false);
            }

            var skipConfirmed = false;
            if (decision.ShouldSkipCatalogPull)
            {
                skipConfirmed = await catalogState.TryConfirmCatalogUnchangedAsync(
                    trustedSession.ShopId,
                    trustedSession.ShopCode,
                    binding.Epoch,
                    decision.ObservedRevision,
                    revisionState.CommittedRevision,
                    revisionState.ImportAckGeneration,
                    clearStaleError: true,
                    generation: context.Generation).ConfigureAwait(false);
            }

            var requestCatalog = !terminalCatalogBlock &&
                (!decision.ShouldSkipCatalogPull || !skipConfirmed);
            var nextPoll = MinimumPositivePoll(
                decision.NextPollAfterSeconds,
                heartbeat.Value.Session.HeartbeatAfterSeconds);
            return new OnlineSyncLaneOutcome(
                true,
                terminalCatalogBlock
                    ? CatalogPaginationSafetyPolicy.AmbiguousEndCode
                    : decision.Code,
                requestCatalogNow: requestCatalog,
                nextPollAfterSeconds: nextPoll);
        }

        private async Task<OnlineSyncLaneOutcome> RunSalesAsync(
            OnlineSyncLaneExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (!PosAdminWebOptions.TryLoad(out var options, out _))
                return new OnlineSyncLaneOutcome(false, "admin_web_config_missing");
            var result = await new PosSalesSyncService(_factory)
                .TrySyncPendingAsync(
                    options,
                    context.Generation,
                    context,
                    cancellationToken).ConfigureAwait(false);
            return FromOutbox(result, requestCatalogNow: false);
        }

        private async Task<OnlineSyncLaneOutcome> RunCatalogImportAsync(
            OnlineSyncLaneExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (!PosAdminWebOptions.TryLoad(out var options, out _))
                return new OnlineSyncLaneOutcome(false, "admin_web_config_missing");
            if (!_store.TryReadGeneration(
                context.Generation,
                out var trustedSession,
                out _))
            {
                return new OnlineSyncLaneOutcome(false, "trusted_generation_changed");
            }
            var result = await new CatalogImportSyncService(_factory)
                .SyncPendingAsync(
                    options,
                    trustedSession,
                    context.Generation,
                    context,
                    cancellationToken).ConfigureAwait(false);
            return FromOutbox(result, requestCatalogNow: result.Acked > 0);
        }

        private async Task<OnlineSyncLaneOutcome> RunCatalogAsync(
            OnlineSyncLaneExecutionContext context,
            OnlineSyncLaneTrigger trigger,
            CancellationToken cancellationToken)
        {
            if (!PosAdminWebOptions.TryLoad(out var options, out _))
                return new OnlineSyncLaneOutcome(false, "admin_web_config_missing");
            if (!_store.TryReadGeneration(
                context.Generation,
                out var trustedSession,
                out _))
            {
                return new OnlineSyncLaneOutcome(false, "trusted_generation_changed");
            }
            var syncContext = await BuildCatalogSyncContextAsync(
                trustedSession,
                context.Generation,
                MapCatalogTrigger(trigger),
                trigger == OnlineSyncLaneTrigger.AdministratorRepair)
                .ConfigureAwait(false);
            if (syncContext.Decision.Mode == CatalogSyncMode.NoOp)
            {
                return new OnlineSyncLaneOutcome(
                    true,
                    syncContext.Decision.DiagnosticCode,
                    terminal: true);
            }
            if (syncContext.Decision.Mode == CatalogSyncMode.Blocked)
            {
                return new OnlineSyncLaneOutcome(
                    false,
                    syncContext.Decision.DiagnosticCode,
                    terminal: true);
            }

            var forceFullRepair = syncContext.Decision.Mode == CatalogSyncMode.Full;
            if (forceFullRepair) Interlocked.Increment(ref _fullCatalogRuns);
            try
            {
                var outcome = await new PosCatalogPullService(_factory)
                    .TryPullCatalogForSupervisorAsync(
                        options,
                        trustedSession,
                        context.Generation,
                        context,
                        forceFullRepair,
                        bootstrapRun: forceFullRepair,
                        cancellationToken).ConfigureAwait(false);
                return outcome.AuthDenied
                    ? OnlineSyncLaneOutcome.AuthDenied(outcome.StatusCode)
                    : new OnlineSyncLaneOutcome(
                        outcome.Completed || outcome.HasMore,
                        outcome.StatusCode,
                        offline: IsOffline(outcome.StatusCode),
                        catalogHasMore: outcome.HasMore,
                        terminal: string.Equals(
                            outcome.StatusCode,
                            CatalogPaginationSafetyPolicy.AmbiguousEndCode,
                            StringComparison.OrdinalIgnoreCase),
                        catalogPagesProcessed: outcome.PagesProcessed,
                        catalogRowsApplied: SumCatalogRows(outcome),
                        catalogSaleSafe: outcome.CatalogSaleSafe);
            }
            finally
            {
                if (forceFullRepair) Interlocked.Decrement(ref _fullCatalogRuns);
            }
        }

        private async Task StopAuthenticationAsync(
            OnlineSyncGeneration generation,
            string code)
        {
            PosOnlineSyncRevocationLatch.Revoke(generation);
            Exception persistenceFailure = null;
            try
            {
                var stopped = await new OnlineSyncGenerationRepository(_factory)
                    .StopIfCurrentAsync(
                        generation,
                        code,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    .ConfigureAwait(false);
                if (!stopped &&
                    await new OnlineSyncGenerationRepository(_factory)
                        .IsCurrentAndActiveAsync(generation).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "The current sync generation could not be stopped.");
                }
            }
            catch (Exception ex)
            {
                persistenceFailure = ex;
            }
            finally
            {
                var cleared = _store.TryClear(generation.GenerationId);
                if (!cleared &&
                    _store.TryReadGeneration(generation, out _, out _) &&
                    persistenceFailure == null)
                {
                    persistenceFailure = new InvalidOperationException(
                        "The revoked trusted-session file could not be removed.");
                }
            }

            if (persistenceFailure != null)
                throw persistenceFailure;
        }

        private Task<OnlineSyncRequestCredentials> ReadCredentialsAsync(
            OnlineSyncGeneration generation)
        {
            if (generation == null ||
                PosOnlineSyncRevocationLatch.IsAuthorizationMaintenanceActive ||
                PosOnlineSyncSignalBus.IsMaintenanceActive ||
                PosOnlineSyncRevocationLatch.IsRevoked(generation) ||
                !_store.TryReadGeneration(
                generation,
                out var session,
                out var credentialStamp))
            {
                return Task.FromResult<OnlineSyncRequestCredentials>(null);
            }
            return Task.FromResult(new OnlineSyncRequestCredentials(
                generation,
                session.DeviceToken,
                session.SessionToken,
                credentialStamp));
        }

        private static OnlineSyncLaneOutcome FromOutbox(
            OutboxDrainResult result,
            bool requestCatalogNow)
        {
            result = result ?? OutboxDrainResult.Empty(
                failureKind: SyncFailureKind.Unexpected,
                diagnosticCode: "outbox_result_missing");
            if (result.AuthenticationDenied)
                return OnlineSyncLaneOutcome.AuthDenied(result.DiagnosticCode);
            var blockedOnly = result.Blocked > 0 &&
                result.RemainingDue == 0 &&
                !result.NextRetryAt.HasValue &&
                !result.HasImmediateMore &&
                (result.FailureKind == SyncFailureKind.PermanentRemote ||
                 result.FailureKind == SyncFailureKind.LocalValidation);
            return new OnlineSyncLaneOutcome(
                result.FailureKind == SyncFailureKind.None,
                result.DiagnosticCode,
                offline: result.FailureKind == SyncFailureKind.Network ||
                    result.FailureKind == SyncFailureKind.Timeout,
                hasImmediateMore: result.HasImmediateMore,
                nextRetryAt: result.NextRetryAt,
                requestCatalogNow: requestCatalogNow,
                terminal: blockedOnly);
        }

        private async Task<SupervisorCatalogContext> BuildCatalogSyncContextAsync(
            PosTrustedDeviceSession trustedSession,
            OnlineSyncGeneration generation,
            CatalogSyncTrigger requestedTrigger,
            bool administratorRepairAuthorized)
        {
            var stateRepository = new CatalogShopStateRepository(_factory);
            var binding = await stateRepository.EnsureAndLoadCursorAsync(
                trustedSession.ShopId,
                trustedSession.ShopCode,
                generation).ConfigureAwait(false);
            if (!binding.IsValid)
            {
                var failedState = new CatalogSyncState(
                    failure: CatalogSyncFailure.DatabaseIntegrityFailed);
                return new SupervisorCatalogContext(
                    failedState,
                    CatalogSyncPolicy.Evaluate(requestedTrigger, failedState));
            }

            var settings = new SettingsRepository(_factory);
            var terminalPaginationBlocked =
                !IsExplicitCatalogRetry(requestedTrigger) &&
                string.Equals(
                    await settings.GetStringAsync(CatalogLastErrorSettingKey)
                        .ConfigureAwait(false),
                    CatalogPaginationSafetyPolicy.AmbiguousEndCode,
                    StringComparison.OrdinalIgnoreCase);
            var bootstrapCompleted = !string.IsNullOrWhiteSpace(
                await settings.GetStringAsync(
                    CatalogShopStateRepository.InitialCompletedAtKey)
                    .ConfigureAwait(false));
            var restoreRecovery = await settings.GetBoolAsync(
                RestoreShopSafetyRepository.RestoreNeedsReviewKey)
                .ConfigureAwait(false) == true;
            var exactness = await stateRepository.LoadExactnessAsync()
                .ConfigureAwait(false);
            var delta = await stateRepository.LoadDeltaChainAsync(
                trustedSession.ShopId,
                trustedSession.ShopCode,
                binding.Epoch).ConfigureAwait(false);
            var exactnessRepair = exactness.RepairRequired ||
                exactness.Status != CatalogCompletenessStatus.Verified;

            var trigger = requestedTrigger;
            if (requestedTrigger == CatalogSyncTrigger.AdministratorRepair)
            {
                trigger = CatalogSyncTrigger.AdministratorRepair;
            }
            else if (restoreRecovery)
            {
                trigger = CatalogSyncTrigger.RestoreCompleted;
            }
            else if (exactnessRepair)
            {
                trigger = CatalogSyncTrigger.ExactnessMismatch;
            }
            else if (!bootstrapCompleted)
            {
                trigger = CatalogSyncTrigger.FirstBootstrap;
            }
            else if (delta.IsValid && delta.HasState)
            {
                trigger = CatalogSyncTrigger.PartialResume;
            }

            var state = new CatalogSyncState(
                persistedCursor: binding.Cursor,
                bootstrapCompleted: bootstrapCompleted,
                hasShopBinding: true,
                legacyCursorMissing: bootstrapCompleted &&
                    string.IsNullOrWhiteSpace(binding.Cursor) &&
                    !restoreRecovery &&
                    !exactnessRepair,
                hasPartialCheckpoint: delta.IsValid && delta.HasState,
                restoreRecoveryRequired: restoreRecovery,
                exactnessRepairRequired: exactnessRepair,
                administratorRepairAuthorized: administratorRepairAuthorized,
                failure: !delta.IsValid
                    ? CatalogSyncFailure.DatabaseIntegrityFailed
                    : terminalPaginationBlocked
                        ? CatalogSyncFailure.TerminalPaginationAmbiguous
                        : CatalogSyncFailure.None);
            return new SupervisorCatalogContext(
                state,
                CatalogSyncPolicy.Evaluate(trigger, state));
        }

        private static CatalogSyncTrigger MapCatalogTrigger(
            OnlineSyncLaneTrigger trigger)
        {
            switch (trigger)
            {
                case OnlineSyncLaneTrigger.StartOfDay:
                    return CatalogSyncTrigger.StartOfDay;
                case OnlineSyncLaneTrigger.FirstBootstrap:
                    return CatalogSyncTrigger.FirstBootstrap;
                case OnlineSyncLaneTrigger.ImportAcknowledged:
                    return CatalogSyncTrigger.CatalogImportAcked;
                case OnlineSyncLaneTrigger.NetworkRecovered:
                    return CatalogSyncTrigger.NetworkRecovered;
                case OnlineSyncLaneTrigger.PartialResume:
                    return CatalogSyncTrigger.PartialResume;
                case OnlineSyncLaneTrigger.Foreground:
                case OnlineSyncLaneTrigger.LocalCommit:
                    return CatalogSyncTrigger.Foreground;
                case OnlineSyncLaneTrigger.Manual:
                    return CatalogSyncTrigger.Manual;
                case OnlineSyncLaneTrigger.AdministratorRepair:
                    return CatalogSyncTrigger.AdministratorRepair;
                default:
                    return CatalogSyncTrigger.Periodic;
            }
        }

        private static bool IsExplicitCatalogRetry(CatalogSyncTrigger trigger)
        {
            return trigger == CatalogSyncTrigger.FirstBootstrap ||
                trigger == CatalogSyncTrigger.Manual ||
                trigger == CatalogSyncTrigger.AdministratorRepair;
        }

        private static int? MinimumPositivePoll(int? first, int? second)
        {
            var normalizedFirst = CatalogHeartbeatPolicy.NormalizePollSeconds(first);
            var normalizedSecond = CatalogHeartbeatPolicy.NormalizePollSeconds(second);
            if (!normalizedFirst.HasValue) return normalizedSecond;
            if (!normalizedSecond.HasValue) return normalizedFirst;
            return Math.Min(normalizedFirst.Value, normalizedSecond.Value);
        }

        private static int SumCatalogRows(PosCatalogPullOutcome outcome)
        {
            if (outcome == null) return 0;
            var rows = (long)outcome.ProductsApplied +
                outcome.PricesApplied +
                outcome.PricesQueued +
                outcome.PendingPricesApplied;
            return rows >= int.MaxValue ? int.MaxValue : (int)rows;
        }

        private OnlineSyncSupervisor CreateSupervisor(
            OnlineSyncGeneration generation)
        {
            return new OnlineSyncSupervisor(
                generation,
                RunLaneAsync,
                current => new OnlineSyncGenerationRepository(_factory)
                    .IsCurrentAndActiveAsync(current),
                StopAuthenticationAsync,
                networkConcurrency: 2,
                credentialProvider: ReadCredentialsAsync);
        }

        private static bool PredecessorStatesMatch(
            OnlineSyncGenerationPredecessorState current,
            OnlineSyncGenerationPredecessorState expected)
        {
            if (current == null || expected == null ||
                current.Exists != expected.Exists)
            {
                return false;
            }
            return !current.Exists ||
                (current.Active == expected.Active &&
                 string.Equals(
                     current.Fingerprint,
                     expected.Fingerprint,
                     StringComparison.Ordinal));
        }

        internal static bool TryCreateGeneration(
            PosTrustedDeviceSession session,
            out OnlineSyncGeneration generation)
        {
            generation = null;
            if (session == null)
                return false;
            try
            {
                generation = new OnlineSyncGeneration(
                    session.GenerationId,
                    session.PosSessionId,
                    session.ShopDeviceId,
                    session.ShopId,
                    session.ShopCode,
                    session.StaffId,
                    session.StaffCredentialVersion);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static long NextActivationTimestamp()
        {
            while (true)
            {
                var current = Interlocked.Read(ref _lastActivationTimestamp);
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var next = Math.Max(now, current + 1);
                if (Interlocked.CompareExchange(
                    ref _lastActivationTimestamp,
                    next,
                    current) == current)
                {
                    return next;
                }
            }
        }

        private static bool IsOffline(string code)
        {
            return string.Equals(code, "network_error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "io_error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "timeout", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "heartbeat_timeout", StringComparison.OrdinalIgnoreCase);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return string.Empty;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PosOnlineSyncSupervisorHost));
        }

        private sealed class SupervisorCatalogContext
        {
            public SupervisorCatalogContext(
                CatalogSyncState state,
                CatalogSyncDecision decision)
            {
                State = state ?? throw new ArgumentNullException(nameof(state));
                Decision = decision ?? throw new ArgumentNullException(nameof(decision));
            }

            public CatalogSyncDecision Decision { get; }
            public CatalogSyncState State { get; }
        }
    }
}
