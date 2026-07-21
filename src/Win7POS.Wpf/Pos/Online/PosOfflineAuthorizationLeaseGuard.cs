using System;
using System.Threading.Tasks;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;

namespace Win7POS.Wpf.Pos.Online
{
    internal sealed class PosOfflineAuthorizationLeaseGuard
    {
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly Func<PosTrustedDeviceSession, Task<bool>> _generationIsActive;
        private readonly PosTrustedDeviceStore _store;
        private readonly object _sync = new object();
        private DateTimeOffset? _estimatedServerHighWater;
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
                })
        {
        }

        internal PosOfflineAuthorizationLeaseGuard(
            PosTrustedDeviceStore store,
            Func<DateTimeOffset> utcNow)
            : this(store, utcNow, _ => Task.FromResult(true))
        {
        }

        internal PosOfflineAuthorizationLeaseGuard(
            PosTrustedDeviceStore store,
            Func<DateTimeOffset> utcNow,
            Func<PosTrustedDeviceSession, Task<bool>> generationIsActive)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            _generationIsActive = generationIsActive ??
                throw new ArgumentNullException(nameof(generationIsActive));
        }

        public PosOfflineAuthorizationLeaseDecision Evaluate()
        {
            PosTrustedDeviceSession ignoredSession;
            return Evaluate(out ignoredSession);
        }

        public PosOfflineAuthorizationLeaseDecision Evaluate(out PosTrustedDeviceSession trustedSession)
        {
            lock (_sync)
            {
                trustedSession = null;
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
                var scopedHighWater = generationCreated &&
                    _validatedAuthorizationEpoch == authorizationEpoch &&
                    string.Equals(
                        _validatedGenerationFingerprint,
                        generation.Fingerprint,
                        StringComparison.Ordinal)
                    ? _estimatedServerHighWater
                    : null;
                var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(
                    session,
                    _utcNow(),
                    scopedHighWater);
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

                if (!_estimatedServerHighWater.HasValue ||
                    decision.EstimatedServerNow > _estimatedServerHighWater)
                {
                    _estimatedServerHighWater = decision.EstimatedServerNow;
                }

                if (!PosOnlineSyncRevocationLatch.IsAuthorizationEpochCurrent(
                        authorizationEpoch))
                {
                    trustedSession = null;
                    return PosOfflineAuthorizationLeaseDecision.Deny(
                        "sync_generation_inactive");
                }
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

                var generationCreated = PosOnlineSyncSupervisorHost.TryCreateGeneration(
                    session,
                    out generation);
                var scopedHighWater = generationCreated &&
                    _validatedAuthorizationEpoch == authorizationEpoch &&
                    string.Equals(
                        _validatedGenerationFingerprint,
                        generation.Fingerprint,
                        StringComparison.Ordinal)
                    ? _estimatedServerHighWater
                    : null;
                decision = PosOfflineAuthorizationLeasePolicy.Evaluate(
                    session,
                    _utcNow(),
                    scopedHighWater);
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

                var scopedHighWater =
                    _validatedAuthorizationEpoch == authorizationEpoch &&
                    string.Equals(
                        _validatedGenerationFingerprint,
                        generation.Fingerprint,
                        StringComparison.Ordinal)
                    ? _estimatedServerHighWater
                    : null;
                decision = PosOfflineAuthorizationLeasePolicy.Evaluate(
                    session,
                    _utcNow(),
                    scopedHighWater);
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
                        generation.Fingerprint));
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

                var scopedHighWater =
                    _validatedAuthorizationEpoch == first.Token.AuthorizationEpoch &&
                    string.Equals(
                        _validatedGenerationFingerprint,
                        generation.Fingerprint,
                        StringComparison.Ordinal)
                        ? _estimatedServerHighWater
                        : null;
                scopedHighWater = Later(
                    scopedHighWater,
                    first.Decision.EstimatedServerNow,
                    second.Decision.EstimatedServerNow);
                var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(
                    currentSession,
                    _utcNow(),
                    scopedHighWater);
                if (!decision.Allowed)
                {
                    return new PosOfflineAuthorizationLeaseEvaluation(decision, null, null);
                }

                var previousEpoch = _validatedAuthorizationEpoch;
                var previousFingerprint = _validatedGenerationFingerprint;
                var previousHighWater = _estimatedServerHighWater;
                _validatedAuthorizationEpoch = first.Token.AuthorizationEpoch;
                _validatedGenerationFingerprint = generation.Fingerprint;
                _estimatedServerHighWater = Later(scopedHighWater, decision.EstimatedServerNow);

                if (!PosOnlineSyncRevocationLatch.IsAuthorizationEpochCurrent(
                        first.Token.AuthorizationEpoch) ||
                    PosOnlineSyncRevocationLatch.IsRevoked(generation))
                {
                    _validatedAuthorizationEpoch = previousEpoch;
                    _validatedGenerationFingerprint = previousFingerprint;
                    _estimatedServerHighWater = previousHighWater;
                    return Denied("sync_generation_inactive");
                }

                return new PosOfflineAuthorizationLeaseEvaluation(
                    decision,
                    currentSession,
                    first.Token);
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

    internal sealed class PosOfflineAuthorizationLeaseToken
    {
        public PosOfflineAuthorizationLeaseToken(
            long authorizationEpoch,
            string generationFingerprint)
        {
            AuthorizationEpoch = authorizationEpoch;
            GenerationFingerprint = generationFingerprint ?? string.Empty;
        }

        public long AuthorizationEpoch { get; }
        public string GenerationFingerprint { get; }
    }
}
