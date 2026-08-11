using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;

namespace Win7POS.Wpf.Pos.Online
{
    /// <summary>
    /// Process-local fail-closed latch. Durable DB/file invalidation is still
    /// attempted, but an I/O failure must not re-authorize the same generation in
    /// the running process.
    /// </summary>
    internal static class PosOnlineSyncRevocationLatch
    {
        private const int MaximumRevokedFingerprints = 1024;
        private static readonly object Gate = new object();
        private static readonly HashSet<string> RevokedFingerprints =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly SemaphoreSlim AuthorizationUseGate =
            new SemaphoreSlim(1, 1);
        private static long _authorizationEpoch;
        private static int _authorizationMaintenanceDepth;
        private static bool _revocationHistoryOverflowed;

        public static bool IsAuthorizationMaintenanceActive
        {
            get
            {
                lock (Gate)
                    return _authorizationMaintenanceDepth > 0;
            }
        }

        public static async Task<IDisposable>
            EnterAuthorizationMaintenanceAsync()
        {
            await AuthorizationUseGate.WaitAsync().ConfigureAwait(false);
            lock (Gate)
                _authorizationMaintenanceDepth++;
            return new AuthorizationMaintenanceLease();
        }

        public static async Task<IDisposable> EnterAuthorizationUseAsync()
        {
            await AuthorizationUseGate.WaitAsync().ConfigureAwait(false);
            lock (Gate)
            {
                if (_authorizationMaintenanceDepth == 0)
                    return new AuthorizationUseLease();
            }

            AuthorizationUseGate.Release();
            throw new InvalidOperationException(
                "Authorization maintenance is active.");
        }

        public static bool TryCaptureAuthorizationEpoch(out long epoch)
        {
            lock (Gate)
            {
                epoch = _authorizationEpoch;
                return _authorizationMaintenanceDepth == 0;
            }
        }

        public static bool IsAuthorizationEpochCurrent(long epoch)
        {
            lock (Gate)
            {
                return _authorizationMaintenanceDepth == 0 &&
                    _authorizationEpoch == epoch;
            }
        }

        internal static bool CommitIfAuthorizationCurrent(
            long authorizationEpoch,
            string generationFingerprint,
            Action demandFinalAuthority,
            Action commit)
        {
            if (demandFinalAuthority == null)
                throw new ArgumentNullException(nameof(demandFinalAuthority));
            if (commit == null)
                throw new ArgumentNullException(nameof(commit));

            // This monitor is the linearization boundary shared with every
            // revocation/epoch publication. Lease/store validation happens
            // before entering it to preserve the global _sync -> Gate order.
            // A revocation that returns first fails these in-lock checks;
            // otherwise the sale commits first
            // and the revocation cannot return until this section completes.
            lock (Gate)
            {
                var normalized =
                    (generationFingerprint ?? string.Empty).Trim();
                if (_authorizationMaintenanceDepth > 0 ||
                    _authorizationEpoch != authorizationEpoch ||
                    normalized.Length == 0 ||
                    _revocationHistoryOverflowed ||
                    RevokedFingerprints.Contains(normalized))
                {
                    return false;
                }
                demandFinalAuthority();
                commit();
                return true;
            }
        }

        internal static void ChangeOperatorAuthority(
            Action mutation,
            bool invalidateAuthorization)
        {
            if (mutation == null)
                throw new ArgumentNullException(nameof(mutation));
            lock (Gate)
            {
                if (invalidateAuthorization)
                    _authorizationEpoch++;
                mutation();
            }
        }

        internal static bool TryChangeOperatorAuthority(
            long authorizationEpoch,
            string generationFingerprint,
            Action mutation)
        {
            if (mutation == null)
                throw new ArgumentNullException(nameof(mutation));

            lock (Gate)
            {
                var normalized =
                    (generationFingerprint ?? string.Empty).Trim();
                if (_authorizationMaintenanceDepth > 0 ||
                    _authorizationEpoch != authorizationEpoch ||
                    normalized.Length == 0 ||
                    _revocationHistoryOverflowed ||
                    RevokedFingerprints.Contains(normalized))
                {
                    return false;
                }

                mutation();
                return true;
            }
        }

        internal static bool TryChangeOperatorAuthorityIf(
            Func<bool> isExpectedAuthority,
            Action mutation,
            bool invalidateAuthorization)
        {
            if (isExpectedAuthority == null)
                throw new ArgumentNullException(
                    nameof(isExpectedAuthority));
            if (mutation == null)
                throw new ArgumentNullException(nameof(mutation));

            lock (Gate)
            {
                if (!isExpectedAuthority())
                    return false;
                if (invalidateAuthorization)
                    _authorizationEpoch++;
                mutation();
                return true;
            }
        }

        internal static bool TryReadOperatorAuthorityIf(
            long authorizationEpoch,
            Func<bool> isExpectedAuthority,
            Action capture)
        {
            if (isExpectedAuthority == null)
                throw new ArgumentNullException(
                    nameof(isExpectedAuthority));
            if (capture == null)
                throw new ArgumentNullException(nameof(capture));

            lock (Gate)
            {
                if (_authorizationMaintenanceDepth > 0 ||
                    _authorizationEpoch != authorizationEpoch ||
                    !isExpectedAuthority())
                    return false;
                capture();
                return true;
            }
        }

        public static void InvalidateAuthorizationState()
        {
            // Publish first. Active authorization-use leases recheck this epoch
            // inside their SQLite transaction and must be able to observe a
            // revocation before COMMIT. Waiting for their semaphore here would
            // hide the revocation until after the sale had committed.
            lock (Gate)
                _authorizationEpoch++;
        }

        public static bool TryInvalidateAuthorizationState(long expectedEpoch)
        {
            lock (Gate)
            {
                if (_authorizationMaintenanceDepth > 0 ||
                    _authorizationEpoch != expectedEpoch)
                {
                    return false;
                }
                _authorizationEpoch++;
                return true;
            }
        }

        internal static void InvalidateAuthorizationStateWhileMaintenanceHeld()
        {
            lock (Gate)
            {
                if (_authorizationMaintenanceDepth <= 0)
                {
                    throw new InvalidOperationException(
                        "Authorization maintenance is not active.");
                }
                _authorizationEpoch++;
            }
        }

        public static bool IsRevoked(OnlineSyncGeneration generation)
        {
            if (generation == null) return true;
            return IsRevokedFingerprint(generation.Fingerprint);
        }

        public static bool IsRevokedFingerprint(string fingerprint)
        {
            var normalized = (fingerprint ?? string.Empty).Trim();
            if (normalized.Length == 0)
                return true;
            lock (Gate)
            {
                return _revocationHistoryOverflowed ||
                    RevokedFingerprints.Contains(normalized);
            }
        }

        public static void Revoke(OnlineSyncGeneration generation)
        {
            RevokeFingerprint(generation?.Fingerprint);
        }

        public static void RevokeFingerprint(string fingerprint)
        {
            // As with epoch invalidation, revocation is published without
            // waiting for an active use lease. Maintenance still drains active
            // leases through AuthorizationUseGate.
            RevokeFingerprintCore(fingerprint);
        }

        internal static void RevokeWhileMaintenanceHeld(
            OnlineSyncGeneration generation)
        {
            lock (Gate)
            {
                if (_authorizationMaintenanceDepth <= 0)
                {
                    throw new InvalidOperationException(
                        "Authorization maintenance is not active.");
                }
            }
            RevokeFingerprintCore(generation?.Fingerprint);
        }

        private static void RevokeFingerprintCore(string fingerprint)
        {
            lock (Gate)
            {
                _authorizationEpoch++;
                var normalized = (fingerprint ?? string.Empty).Trim();
                if (normalized.Length > 0)
                {
                    if (RevokedFingerprints.Count <
                        MaximumRevokedFingerprints)
                    {
                        RevokedFingerprints.Add(normalized);
                    }
                    else if (!RevokedFingerprints.Contains(normalized))
                    {
                        // Never forget an old revocation merely to cap memory.
                        // A pathological amount of generation churn therefore
                        // fails closed for the remainder of this process.
                        _revocationHistoryOverflowed = true;
                    }
                }
            }
        }

        private sealed class AuthorizationMaintenanceLease : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                lock (Gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    if (_authorizationMaintenanceDepth > 0)
                        _authorizationMaintenanceDepth--;
                }
                AuthorizationUseGate.Release();
            }
        }

        private sealed class AuthorizationUseLease : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    AuthorizationUseGate.Release();
            }
        }
    }
}
