using System;
using System.Collections.Generic;
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
        private static readonly object Gate = new object();
        private static readonly HashSet<string> RevokedFingerprints =
            new HashSet<string>(StringComparer.Ordinal);
        private static long _authorizationEpoch;
        private static int _authorizationMaintenanceDepth;

        public static bool IsAuthorizationMaintenanceActive
        {
            get
            {
                lock (Gate)
                    return _authorizationMaintenanceDepth > 0;
            }
        }

        public static IDisposable EnterAuthorizationMaintenance()
        {
            lock (Gate)
                _authorizationMaintenanceDepth++;
            return new AuthorizationMaintenanceLease();
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

        public static void InvalidateAuthorizationState()
        {
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

        public static bool IsRevoked(OnlineSyncGeneration generation)
        {
            if (generation == null) return true;
            lock (Gate)
                return RevokedFingerprints.Contains(generation.Fingerprint);
        }

        public static void Revoke(OnlineSyncGeneration generation)
        {
            RevokeFingerprint(generation?.Fingerprint);
        }

        public static void RevokeFingerprint(string fingerprint)
        {
            lock (Gate)
            {
                _authorizationEpoch++;
                var normalized = (fingerprint ?? string.Empty).Trim();
                if (normalized.Length > 0)
                    RevokedFingerprints.Add(normalized);
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
            }
        }
    }
}
