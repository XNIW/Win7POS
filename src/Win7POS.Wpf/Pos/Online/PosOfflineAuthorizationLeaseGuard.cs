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

        public async Task<PosOfflineAuthorizationLeaseEvaluation> EvaluateAsync()
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
                        null);
                }
                if (!_store.TryRead(out session))
                {
                    return new PosOfflineAuthorizationLeaseEvaluation(
                        PosOfflineAuthorizationLeasePolicy.Evaluate(null, _utcNow()),
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
                        null);
                }
            }
            catch
            {
                return new PosOfflineAuthorizationLeaseEvaluation(
                    PosOfflineAuthorizationLeaseDecision.Deny(
                        "sync_generation_check_failed"),
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
                    return new PosOfflineAuthorizationLeaseEvaluation(decision, null);
                }

                var sameValidatedGeneration =
                    _validatedAuthorizationEpoch == authorizationEpoch &&
                    string.Equals(
                        _validatedGenerationFingerprint,
                        generation.Fingerprint,
                        StringComparison.Ordinal);
                _validatedAuthorizationEpoch = authorizationEpoch;
                _validatedGenerationFingerprint = generation.Fingerprint;
                if (!sameValidatedGeneration ||
                    !_estimatedServerHighWater.HasValue ||
                    decision.EstimatedServerNow > _estimatedServerHighWater)
                {
                    _estimatedServerHighWater = decision.EstimatedServerNow;
                }
                if (!PosOnlineSyncRevocationLatch.IsAuthorizationEpochCurrent(
                        authorizationEpoch))
                {
                    return new PosOfflineAuthorizationLeaseEvaluation(
                        PosOfflineAuthorizationLeaseDecision.Deny(
                            "sync_generation_inactive"),
                        null);
                }
                return new PosOfflineAuthorizationLeaseEvaluation(decision, session);
            }
        }
    }

    internal sealed class PosOfflineAuthorizationLeaseEvaluation
    {
        public PosOfflineAuthorizationLeaseEvaluation(
            PosOfflineAuthorizationLeaseDecision decision,
            PosTrustedDeviceSession trustedSession)
        {
            Decision = decision ?? throw new ArgumentNullException(nameof(decision));
            TrustedSession = trustedSession;
        }

        public PosOfflineAuthorizationLeaseDecision Decision { get; }
        public PosTrustedDeviceSession TrustedSession { get; }
    }
}
