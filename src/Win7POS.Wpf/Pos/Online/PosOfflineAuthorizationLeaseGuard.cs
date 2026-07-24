using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;

namespace Win7POS.Wpf.Pos.Online
{
    internal sealed class PosOfflineAuthorizationLeaseGuard
    {
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly Func<long> _monotonicTimestamp;
        private readonly long _monotonicFrequency;
        private readonly Func<PosTrustedDeviceSession, Task<bool>> _generationIsActive;
        private readonly PosTrustedDeviceStore _store;
        private readonly object _sync = new object();
        private DateTimeOffset? _wallEstimatedServerHighWater;
        private DateTimeOffset? _trustedServerAnchor;
        private long _trustedServerAnchorTimestamp;
        private long _validatedAuthorizationEpoch = long.MinValue;
        private string _validatedGenerationFingerprint = string.Empty;

        internal PosOfflineAuthorizationLeaseGuard()
            : this(
                new PosTrustedDeviceStore(),
                () => DateTimeOffset.UtcNow,
                async session =>
                {
                    if (!PosOnlineSyncSupervisorHost.TryCreateGeneration(
                            session,
                            out var generation))
                    {
                        return false;
                    }
                    var factory = new SqliteConnectionFactory(PosDbOptions.Default());
                    return await new OnlineSyncGenerationRepository(factory)
                        .IsCurrentAndActiveAsync(generation)
                        .ConfigureAwait(false);
                },
                Stopwatch.GetTimestamp,
                Stopwatch.Frequency)
        {
        }

        internal PosOfflineAuthorizationLeaseGuard(
            PosTrustedDeviceStore store,
            Func<DateTimeOffset> utcNow)
            : this(
                store,
                utcNow,
                _ => Task.FromResult(true),
                Stopwatch.GetTimestamp,
                Stopwatch.Frequency)
        {
        }

        internal PosOfflineAuthorizationLeaseGuard(
            PosTrustedDeviceStore store,
            Func<DateTimeOffset> utcNow,
            Func<PosTrustedDeviceSession, Task<bool>> generationIsActive)
            : this(
                store,
                utcNow,
                generationIsActive,
                Stopwatch.GetTimestamp,
                Stopwatch.Frequency)
        {
        }

        internal PosOfflineAuthorizationLeaseGuard(
            PosTrustedDeviceStore store,
            Func<DateTimeOffset> utcNow,
            Func<long> monotonicTimestamp,
            long monotonicFrequency)
            : this(
                store,
                utcNow,
                _ => Task.FromResult(true),
                monotonicTimestamp,
                monotonicFrequency)
        {
        }

        internal PosOfflineAuthorizationLeaseGuard(
            PosTrustedDeviceStore store,
            Func<DateTimeOffset> utcNow,
            Func<PosTrustedDeviceSession, Task<bool>> generationIsActive,
            Func<long> monotonicTimestamp,
            long monotonicFrequency)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            _generationIsActive = generationIsActive ??
                throw new ArgumentNullException(nameof(generationIsActive));
            _monotonicTimestamp = monotonicTimestamp ??
                throw new ArgumentNullException(nameof(monotonicTimestamp));
            _monotonicFrequency = monotonicFrequency;
        }

        public PosOfflineAuthorizationLeaseDecision Evaluate()
        {
            PosTrustedDeviceSession ignoredSession;
            return Evaluate(out ignoredSession);
        }

        public PosOfflineAuthorizationLeaseDecision Evaluate(out PosTrustedDeviceSession trustedSession)
        {
            PosAuthorizationCommitExpiryGuard ignoredCommitExpiryGuard;
            return EvaluateAuthorizationUse(
                out trustedSession,
                out ignoredCommitExpiryGuard);
        }

        internal PosOfflineAuthorizationLeaseDecision EvaluateAuthorizationUse(
            out PosTrustedDeviceSession trustedSession,
            out PosAuthorizationCommitExpiryGuard commitExpiryGuard)
        {
            lock (_sync)
            {
                trustedSession = null;
                commitExpiryGuard = null;
                if (!PosOnlineSyncRevocationLatch.TryCaptureAuthorizationEpoch(
                        out var authorizationEpoch))
                {
                    return PosOfflineAuthorizationLeaseDecision.Deny(
                        "sync_maintenance_active");
                }
                if (!_store.TryRead(out var session))
                {
                    return PosOfflineAuthorizationLeasePolicy.Evaluate(null, _utcNow());
                }

                var generationCreated = PosOnlineSyncSupervisorHost.TryCreateGeneration(
                    session,
                    out var generation);
                DateTimeOffset? receiptTrustedServerNow = null;
                DateTimeOffset processTrustedServerNow = default;
                if (generationCreated &&
                    session.OfflineAuthorizationAttested &&
                    !_store.TryGetProcessAuthorizationTrustedNow(
                        generation,
                        out processTrustedServerNow))
                {
                    return PosOfflineAuthorizationLeaseDecision.Deny(
                        "trusted_time_continuity_lost");
                }
                if (generationCreated && session.OfflineAuthorizationAttested)
                    receiptTrustedServerNow = processTrustedServerNow;
                var hasValidatedScope = generationCreated &&
                    HasValidatedScope(authorizationEpoch, generation.Fingerprint);
                var scopedHighWater = hasValidatedScope
                    ? _wallEstimatedServerHighWater
                    : null;
                DateTimeOffset? minimumTrustedServerNow = null;
                var monotonicTimestamp = 0L;
                if (hasValidatedScope &&
                    (!TryCaptureMonotonicTimestamp(out monotonicTimestamp) ||
                     !TryAdvanceTrustedAnchor(
                         _trustedServerAnchor,
                         _trustedServerAnchorTimestamp,
                         monotonicTimestamp,
                         out minimumTrustedServerNow)))
                {
                    return PosOfflineAuthorizationLeaseDecision.Deny(
                        "trusted_time_continuity_lost");
                }
                var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(
                    session,
                    _utcNow(),
                    scopedHighWater,
                    Later(
                        receiptTrustedServerNow,
                        minimumTrustedServerNow));
                if (!decision.Allowed)
                {
                    return decision;
                }
                if (!generationCreated ||
                    PosOnlineSyncRevocationLatch.IsRevoked(generation) ||
                    _validatedAuthorizationEpoch != authorizationEpoch ||
                    !PosOnlineSyncRevocationLatch.IsAuthorizationEpochCurrent(
                        authorizationEpoch) ||
                    !string.Equals(
                        _validatedGenerationFingerprint,
                        generation.Fingerprint,
                        StringComparison.Ordinal))
                {
                    return PosOfflineAuthorizationLeaseDecision.Deny(
                        "sync_generation_inactive");
                }

                trustedSession = session;

                if (!_wallEstimatedServerHighWater.HasValue ||
                    decision.WallEstimatedServerNow >
                        _wallEstimatedServerHighWater)
                {
                    _wallEstimatedServerHighWater =
                        decision.WallEstimatedServerNow;
                }
                _trustedServerAnchor = decision.EstimatedServerNow;
                _trustedServerAnchorTimestamp = monotonicTimestamp;

                if (!PosOnlineSyncRevocationLatch.IsAuthorizationEpochCurrent(
                        authorizationEpoch))
                {
                    trustedSession = null;
                    return PosOfflineAuthorizationLeaseDecision.Deny(
                        "sync_generation_inactive");
                }
                commitExpiryGuard =
                    new PosAuthorizationCommitExpiryGuard(
                        session,
                        decision,
                        monotonicTimestamp,
                        _monotonicTimestamp,
                        _monotonicFrequency,
                        _utcNow);
                return decision;
            }
        }

        /// <summary>
        /// Performs the asynchronous lease checks without changing the reusable
        /// authorization cache. Authentication failures must never prime a new
        /// generation for later permission checks.
        /// </summary>
        public async Task<PosOfflineAuthorizationLeaseEvaluation> PreflightAsync()
        {
            PosTrustedDeviceSession session;
            PosOfflineAuthorizationLeaseDecision decision;
            OnlineSyncGeneration generation;
            long authorizationEpoch;
            long firstMonotonicTimestamp;
            lock (_sync)
            {
                if (!PosOnlineSyncRevocationLatch.TryCaptureAuthorizationEpoch(
                        out authorizationEpoch))
                {
                    return new PosOfflineAuthorizationLeaseEvaluation(
                        PosOfflineAuthorizationLeaseDecision.Deny(
                            "sync_maintenance_active"),
                        null,
                        null);
                }
                if (!_store.TryRead(out session))
                {
                    return new PosOfflineAuthorizationLeaseEvaluation(
                        PosOfflineAuthorizationLeasePolicy.Evaluate(null, _utcNow()),
                        null,
                        null);
                }
                if (!TryCaptureMonotonicTimestamp(out firstMonotonicTimestamp))
                {
                    return Denied("trusted_time_continuity_lost");
                }

                var generationCreated = PosOnlineSyncSupervisorHost.TryCreateGeneration(
                    session,
                    out generation);
                DateTimeOffset? receiptTrustedServerNow = null;
                DateTimeOffset processTrustedServerNow = default;
                if (generationCreated &&
                    session.OfflineAuthorizationAttested &&
                    !_store.TryGetProcessAuthorizationTrustedNow(
                        generation,
                        out processTrustedServerNow))
                {
                    return Denied("trusted_time_continuity_lost");
                }
                if (generationCreated && session.OfflineAuthorizationAttested)
                    receiptTrustedServerNow = processTrustedServerNow;
                var hasValidatedScope = generationCreated &&
                    HasValidatedScope(authorizationEpoch, generation.Fingerprint);
                var scopedHighWater = hasValidatedScope
                    ? _wallEstimatedServerHighWater
                    : null;
                DateTimeOffset? minimumTrustedServerNow = null;
                if (hasValidatedScope &&
                    !TryAdvanceTrustedAnchor(
                        _trustedServerAnchor,
                        _trustedServerAnchorTimestamp,
                        firstMonotonicTimestamp,
                        out minimumTrustedServerNow))
                {
                    return Denied("trusted_time_continuity_lost");
                }
                decision = PosOfflineAuthorizationLeasePolicy.Evaluate(
                    session,
                    _utcNow(),
                    scopedHighWater,
                    Later(
                        receiptTrustedServerNow,
                        minimumTrustedServerNow));
                if (!decision.Allowed ||
                    !generationCreated ||
                    PosOnlineSyncRevocationLatch.IsRevoked(generation))
                {
                    return new PosOfflineAuthorizationLeaseEvaluation(
                        decision.Allowed
                            ? PosOfflineAuthorizationLeaseDecision.Deny(
                                "sync_generation_inactive")
                            : decision,
                        null,
                        null);
                }
            }

            try
            {
                if (!await _generationIsActive(session).ConfigureAwait(false))
                {
                    return new PosOfflineAuthorizationLeaseEvaluation(
                        PosOfflineAuthorizationLeaseDecision.Deny(
                            "sync_generation_inactive"),
                        null,
                        null);
                }
            }
            catch
            {
                return new PosOfflineAuthorizationLeaseEvaluation(
                    PosOfflineAuthorizationLeaseDecision.Deny(
                        "sync_generation_check_failed"),
                    null,
                    null);
            }

            lock (_sync)
            {
                if (!PosOnlineSyncRevocationLatch.IsAuthorizationEpochCurrent(
                        authorizationEpoch) ||
                    PosOnlineSyncRevocationLatch.IsRevoked(generation) ||
                    !_store.TryReadGeneration(generation, out session, out _))
                {
                    return new PosOfflineAuthorizationLeaseEvaluation(
                        PosOfflineAuthorizationLeaseDecision.Deny(
                            "sync_generation_inactive"),
                        null,
                        null);
                }

                if (!TryCaptureMonotonicTimestamp(out var secondMonotonicTimestamp) ||
                    !TryAdvanceTrustedAnchor(
                        decision.EstimatedServerNow,
                        firstMonotonicTimestamp,
                        secondMonotonicTimestamp,
                        out var preflightLowerBound))
                {
                    return Denied("trusted_time_continuity_lost");
                }
                DateTimeOffset receiptTrustedServerNow = default;
                if (session.OfflineAuthorizationAttested &&
                    !_store.TryGetProcessAuthorizationTrustedNow(
                        generation,
                        out receiptTrustedServerNow))
                {
                    return Denied("trusted_time_continuity_lost");
                }

                var hasValidatedScope =
                    HasValidatedScope(authorizationEpoch, generation.Fingerprint);
                var scopedHighWater = hasValidatedScope
                    ? _wallEstimatedServerHighWater
                    : null;
                DateTimeOffset? scopedLowerBound = null;
                if (hasValidatedScope &&
                    !TryAdvanceTrustedAnchor(
                        _trustedServerAnchor,
                        _trustedServerAnchorTimestamp,
                        secondMonotonicTimestamp,
                        out scopedLowerBound))
                {
                    return Denied("trusted_time_continuity_lost");
                }
                decision = PosOfflineAuthorizationLeasePolicy.Evaluate(
                    session,
                    _utcNow(),
                    scopedHighWater,
                    Later(
                        preflightLowerBound,
                        scopedLowerBound,
                        session.OfflineAuthorizationAttested
                            ? (DateTimeOffset?)receiptTrustedServerNow
                            : null));
                if (!decision.Allowed)
                {
                    return new PosOfflineAuthorizationLeaseEvaluation(decision, null, null);
                }

                if (!PosOnlineSyncRevocationLatch.IsAuthorizationEpochCurrent(
                        authorizationEpoch))
                {
                    return new PosOfflineAuthorizationLeaseEvaluation(
                        PosOfflineAuthorizationLeaseDecision.Deny(
                            "sync_generation_inactive"),
                        null,
                        null);
                }
                return new PosOfflineAuthorizationLeaseEvaluation(
                    decision,
                    session,
                    new PosOfflineAuthorizationLeaseToken(
                        authorizationEpoch,
                        generation.Fingerprint,
                        secondMonotonicTimestamp));
            }
        }

        /// <summary>
        /// Atomically commits a successful authentication only when both
        /// preflights still describe the same active lease generation.
        /// </summary>
        public async Task<PosOfflineAuthorizationLeaseEvaluation> CommitAuthenticationAsync(
            PosOfflineAuthorizationLeaseEvaluation first,
            PosOfflineAuthorizationLeaseEvaluation second)
        {
            if (HasMonotonicTokenRegression(first, second))
            {
                return Denied("trusted_time_continuity_lost");
            }
            if (!CanCommit(first, second, out var candidateSession))
            {
                return Denied("sync_generation_changed");
            }

            try
            {
                if (!await _generationIsActive(candidateSession).ConfigureAwait(false))
                {
                    return Denied("sync_generation_inactive");
                }
            }
            catch
            {
                return Denied("sync_generation_check_failed");
            }

            lock (_sync)
            {
                if (!CanCommit(first, second, out candidateSession) ||
                    !PosOnlineSyncSupervisorHost.TryCreateGeneration(
                        candidateSession,
                        out var generation) ||
                    PosOnlineSyncRevocationLatch.IsRevoked(generation) ||
                    !PosOnlineSyncRevocationLatch.IsAuthorizationEpochCurrent(
                        first.Token.AuthorizationEpoch) ||
                    !_store.TryReadGeneration(generation, out var currentSession, out _))
                {
                    return Denied("sync_generation_inactive");
                }
                DateTimeOffset receiptTrustedServerNow = default;
                if (currentSession.OfflineAuthorizationAttested &&
                    !_store.TryGetProcessAuthorizationTrustedNow(
                        generation,
                        out receiptTrustedServerNow))
                {
                    return Denied("trusted_time_continuity_lost");
                }

                if (!TryCaptureMonotonicTimestamp(out var monotonicTimestamp) ||
                    !TryAdvanceTrustedAnchor(
                        first.Decision.EstimatedServerNow,
                        first.Token.MonotonicTimestamp,
                        monotonicTimestamp,
                        out var firstLowerBound) ||
                    !TryAdvanceTrustedAnchor(
                        second.Decision.EstimatedServerNow,
                        second.Token.MonotonicTimestamp,
                        monotonicTimestamp,
                        out var secondLowerBound))
                {
                    return Denied("trusted_time_continuity_lost");
                }

                var hasValidatedScope = HasValidatedScope(
                    first.Token.AuthorizationEpoch,
                    generation.Fingerprint);
                var scopedHighWater = hasValidatedScope
                        ? _wallEstimatedServerHighWater
                        : null;
                scopedHighWater = Later(
                    scopedHighWater,
                    first.Decision.WallEstimatedServerNow,
                    second.Decision.WallEstimatedServerNow);
                DateTimeOffset? scopedLowerBound = null;
                if (hasValidatedScope &&
                    !TryAdvanceTrustedAnchor(
                        _trustedServerAnchor,
                        _trustedServerAnchorTimestamp,
                        monotonicTimestamp,
                        out scopedLowerBound))
                {
                    return Denied("trusted_time_continuity_lost");
                }
                var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(
                    currentSession,
                    _utcNow(),
                    scopedHighWater,
                    Later(
                        firstLowerBound,
                        secondLowerBound,
                        scopedLowerBound,
                        currentSession.OfflineAuthorizationAttested
                            ? (DateTimeOffset?)receiptTrustedServerNow
                            : null));
                if (!decision.Allowed)
                {
                    return new PosOfflineAuthorizationLeaseEvaluation(decision, null, null);
                }

                var previousEpoch = _validatedAuthorizationEpoch;
                var previousFingerprint = _validatedGenerationFingerprint;
                var previousHighWater = _wallEstimatedServerHighWater;
                var previousTrustedServerAnchor = _trustedServerAnchor;
                var previousTrustedServerAnchorTimestamp =
                    _trustedServerAnchorTimestamp;
                _validatedAuthorizationEpoch = first.Token.AuthorizationEpoch;
                _validatedGenerationFingerprint = generation.Fingerprint;
                _wallEstimatedServerHighWater = Later(
                    scopedHighWater,
                    decision.WallEstimatedServerNow);
                _trustedServerAnchor = decision.EstimatedServerNow;
                _trustedServerAnchorTimestamp = monotonicTimestamp;

                if (!PosOnlineSyncRevocationLatch.IsAuthorizationEpochCurrent(
                        first.Token.AuthorizationEpoch) ||
                    PosOnlineSyncRevocationLatch.IsRevoked(generation))
                {
                    _validatedAuthorizationEpoch = previousEpoch;
                    _validatedGenerationFingerprint = previousFingerprint;
                    _wallEstimatedServerHighWater = previousHighWater;
                    _trustedServerAnchor = previousTrustedServerAnchor;
                    _trustedServerAnchorTimestamp =
                        previousTrustedServerAnchorTimestamp;
                    return Denied("sync_generation_inactive");
                }

                return new PosOfflineAuthorizationLeaseEvaluation(
                    decision,
                    currentSession,
                    new PosOfflineAuthorizationLeaseToken(
                        first.Token.AuthorizationEpoch,
                        generation.Fingerprint,
                        monotonicTimestamp));
            }
        }

        private bool HasValidatedScope(
            long authorizationEpoch,
            string generationFingerprint)
        {
            return _validatedAuthorizationEpoch == authorizationEpoch &&
                string.Equals(
                    _validatedGenerationFingerprint,
                    generationFingerprint,
                    StringComparison.Ordinal);
        }

        private bool TryCaptureMonotonicTimestamp(out long timestamp)
        {
            timestamp = 0;
            if (_monotonicFrequency <= 0)
            {
                return false;
            }

            try
            {
                timestamp = _monotonicTimestamp();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryAdvanceTrustedAnchor(
            DateTimeOffset? serverAnchor,
            long anchorTimestamp,
            long currentTimestamp,
            out DateTimeOffset? trustedServerNow)
        {
            trustedServerNow = null;
            if (!serverAnchor.HasValue ||
                _monotonicFrequency <= 0 ||
                currentTimestamp < anchorTimestamp)
            {
                return false;
            }

            try
            {
                var elapsedCounterTicks =
                    (decimal)currentTimestamp - anchorTimestamp;
                var elapsedTimeSpanTicks = decimal.Truncate(
                    elapsedCounterTicks *
                    TimeSpan.TicksPerSecond /
                    _monotonicFrequency);
                if (elapsedTimeSpanTicks < 0 ||
                    elapsedTimeSpanTicks > long.MaxValue)
                {
                    return false;
                }

                trustedServerNow = serverAnchor.Value.AddTicks(
                    decimal.ToInt64(elapsedTimeSpanTicks));
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        private static bool CanCommit(
            PosOfflineAuthorizationLeaseEvaluation first,
            PosOfflineAuthorizationLeaseEvaluation second,
            out PosTrustedDeviceSession candidateSession)
        {
            candidateSession = second?.TrustedSession;
            return first?.Decision?.Allowed == true &&
                second?.Decision?.Allowed == true &&
                first.Token != null &&
                second.Token != null &&
                candidateSession != null &&
                first.Token.AuthorizationEpoch == second.Token.AuthorizationEpoch &&
                string.Equals(
                    first.Token.GenerationFingerprint,
                    second.Token.GenerationFingerprint,
                    StringComparison.Ordinal);
        }

        private static bool HasMonotonicTokenRegression(
            PosOfflineAuthorizationLeaseEvaluation first,
            PosOfflineAuthorizationLeaseEvaluation second)
        {
            return first?.Token != null &&
                second?.Token != null &&
                second.Token.MonotonicTimestamp <
                    first.Token.MonotonicTimestamp;
        }

        private static DateTimeOffset? Later(
            DateTimeOffset? current,
            params DateTimeOffset?[] candidates)
        {
            var result = current;
            foreach (var candidate in candidates)
            {
                if (candidate.HasValue &&
                    (!result.HasValue || candidate.Value > result.Value))
                {
                    result = candidate.Value;
                }
            }
            return result;
        }

        private static PosOfflineAuthorizationLeaseEvaluation Denied(string code)
        {
            return new PosOfflineAuthorizationLeaseEvaluation(
                PosOfflineAuthorizationLeaseDecision.Deny(code),
                null,
                null);
        }
    }

    internal sealed class PosOfflineAuthorizationLeaseEvaluation
    {
        public PosOfflineAuthorizationLeaseEvaluation(
            PosOfflineAuthorizationLeaseDecision decision,
            PosTrustedDeviceSession trustedSession,
            PosOfflineAuthorizationLeaseToken token)
        {
            Decision = decision ?? throw new ArgumentNullException(nameof(decision));
            TrustedSession = trustedSession;
            Token = token;
        }

        public PosOfflineAuthorizationLeaseDecision Decision { get; }
        public PosTrustedDeviceSession TrustedSession { get; }
        internal PosOfflineAuthorizationLeaseToken Token { get; }
    }

    /// <summary>
    /// Immutable, lock-free expiry proof captured when an authorization-use
    /// lease begins. It can be evaluated while the revocation latch is held,
    /// so expiry and revoke share the sale COMMIT linearization boundary
    /// without reversing the lease-guard lock order.
    /// </summary>
    internal sealed class PosAuthorizationCommitExpiryGuard
    {
        private readonly long _anchorTimestamp;
        private readonly PosTrustedDeviceSession _frozenSession;
        private readonly long _monotonicFrequency;
        private readonly Func<long> _monotonicTimestamp;
        private readonly DateTimeOffset _trustedServerAnchor;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly DateTimeOffset _wallEstimatedServerHighWater;

        public PosAuthorizationCommitExpiryGuard(
            PosTrustedDeviceSession session,
            PosOfflineAuthorizationLeaseDecision decision,
            long anchorTimestamp,
            Func<long> monotonicTimestamp,
            long monotonicFrequency,
            Func<DateTimeOffset> utcNow)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));
            if (decision?.Allowed != true ||
                !decision.EstimatedServerNow.HasValue ||
                !decision.WallEstimatedServerNow.HasValue)
            {
                throw new ArgumentException(
                    "An allowed lease decision is required.",
                    nameof(decision));
            }
            _monotonicTimestamp = monotonicTimestamp ??
                throw new ArgumentNullException(nameof(monotonicTimestamp));
            _utcNow = utcNow ??
                throw new ArgumentNullException(nameof(utcNow));
            _monotonicFrequency = monotonicFrequency;
            _anchorTimestamp = anchorTimestamp;
            _trustedServerAnchor = decision.EstimatedServerNow.Value;
            _wallEstimatedServerHighWater =
                decision.WallEstimatedServerNow.Value;
            _frozenSession = new PosTrustedDeviceSession
            {
                EffectiveOfflineAuthorizationExpiresAt =
                    session.EffectiveOfflineAuthorizationExpiresAt,
                LastOkLocalAt = session.LastOkLocalAt,
                LastOkServerAt = session.LastOkServerAt,
                OfflineAuthorizationAttested =
                    session.OfflineAuthorizationAttested,
                SessionExpiresAt = session.SessionExpiresAt
            };
        }

        public PosOfflineAuthorizationLeaseDecision Evaluate()
        {
            if (_monotonicFrequency <= 0)
            {
                return PosOfflineAuthorizationLeaseDecision.Deny(
                    "trusted_time_continuity_lost");
            }

            try
            {
                var currentTimestamp = _monotonicTimestamp();
                if (currentTimestamp < _anchorTimestamp)
                {
                    return PosOfflineAuthorizationLeaseDecision.Deny(
                        "trusted_time_continuity_lost");
                }

                var elapsedTimeSpanTicks =
                    ((decimal)currentTimestamp - _anchorTimestamp) *
                    TimeSpan.TicksPerSecond /
                    _monotonicFrequency;
                if (elapsedTimeSpanTicks < 0m ||
                    elapsedTimeSpanTicks > long.MaxValue)
                {
                    return PosOfflineAuthorizationLeaseDecision.Deny(
                        "trusted_time_continuity_lost");
                }
                var monotonicTrustedServerNow =
                    _trustedServerAnchor.AddTicks(
                        decimal.ToInt64(elapsedTimeSpanTicks));
                return PosOfflineAuthorizationLeasePolicy.Evaluate(
                    _frozenSession,
                    _utcNow(),
                    _wallEstimatedServerHighWater,
                    monotonicTrustedServerNow);
            }
            catch (ArgumentOutOfRangeException)
            {
                return PosOfflineAuthorizationLeaseDecision.Deny(
                    "trusted_time_continuity_lost");
            }
            catch (OverflowException)
            {
                return PosOfflineAuthorizationLeaseDecision.Deny(
                    "trusted_time_continuity_lost");
            }
            catch
            {
                return PosOfflineAuthorizationLeaseDecision.Deny(
                    "trusted_time_continuity_lost");
            }
        }
    }

    internal sealed class PosOfflineAuthorizationLeaseToken
    {
        public PosOfflineAuthorizationLeaseToken(
            long authorizationEpoch,
            string generationFingerprint,
            long monotonicTimestamp)
        {
            AuthorizationEpoch = authorizationEpoch;
            GenerationFingerprint = generationFingerprint ?? string.Empty;
            MonotonicTimestamp = monotonicTimestamp;
        }

        public long AuthorizationEpoch { get; }
        public string GenerationFingerprint { get; }
        public long MonotonicTimestamp { get; }
    }
}
