using System;
using Win7POS.Core.Online;

namespace Win7POS.Wpf.Pos.Online
{
    internal sealed class PosOfflineAuthorizationLeaseGuard
    {
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly PosTrustedDeviceStore _store;
        private readonly object _sync = new object();
        private DateTimeOffset? _estimatedServerHighWater;

        internal PosOfflineAuthorizationLeaseGuard()
            : this(new PosTrustedDeviceStore(), () => DateTimeOffset.UtcNow)
        {
        }

        internal PosOfflineAuthorizationLeaseGuard(
            PosTrustedDeviceStore store,
            Func<DateTimeOffset> utcNow)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
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
                if (!_store.TryRead(out var session))
                {
                    return PosOfflineAuthorizationLeasePolicy.Evaluate(null, _utcNow());
                }

                var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(
                    session,
                    _utcNow(),
                    _estimatedServerHighWater);
                if (!decision.Allowed)
                {
                    return decision;
                }

                trustedSession = session;

                if (!_estimatedServerHighWater.HasValue ||
                    decision.EstimatedServerNow > _estimatedServerHighWater)
                {
                    _estimatedServerHighWater = decision.EstimatedServerNow;
                }

                return decision;
            }
        }
    }
}
