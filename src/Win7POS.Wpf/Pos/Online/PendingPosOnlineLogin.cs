using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Wpf.Infrastructure.Security;

namespace Win7POS.Wpf.Pos.Online
{
    // A short-lived continuation of one verified online sign-in, never a new
    // authorization capability. Only the ordinary mirror login may commit the
    // operator; no credential is written to disk, logs or process environment.
    internal sealed class PendingPosOnlineLogin : IDisposable
    {
        private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(5);
        private readonly SqliteConnectionFactory _factory;
        private readonly PosTrustedDeviceStore _store;
        private readonly PosAuthorizationCommitExpiryGuard _expiry;
        private readonly long _authorizationEpoch;
        private readonly long _startedAt;
        private SecureString _credential;
        private readonly object _sync = new object();

        private PendingPosOnlineLogin(SqliteConnectionFactory factory, PosTrustedDeviceStore store,
            OnlineSyncGeneration generation, string staffCode, SecureString credential,
            PosAuthorizationCommitExpiryGuard expiry, long authorizationEpoch)
        {
            _factory = factory; _store = store; Generation = generation; StaffCode = staffCode;
            _credential = credential.Copy(); _credential.MakeReadOnly();
            _expiry = expiry; _authorizationEpoch = authorizationEpoch; _startedAt = Stopwatch.GetTimestamp();
        }

        internal OnlineSyncGeneration Generation { get; }
        internal string StaffCode { get; }
        internal bool IsCurrentInMemory
        {
            get
            {
                lock (_sync)
                {
                    var elapsed = Stopwatch.GetTimestamp() - _startedAt;
                    return _credential != null && elapsed >= 0 &&
                        elapsed < MaximumLifetime.TotalSeconds * Stopwatch.Frequency &&
                        PosOnlineSyncRevocationLatch.IsAuthorizationEpochCurrent(_authorizationEpoch) &&
                        !PosOnlineSyncRevocationLatch.IsRevoked(Generation) &&
                        _expiry.Evaluate(TimeSpan.Zero).Allowed;
                }
            }
        }

        internal static async Task<PendingPosOnlineLogin> CreateAsync(SqliteConnectionFactory factory,
            PosTrustedDeviceStore store, OnlineSyncGeneration generation, OperatorSession issuer,
            SecureString credential, CancellationToken cancellationToken)
        {
            if (factory == null || store == null || generation == null || issuer == null || credential == null || credential.Length == 0)
                throw new InvalidOperationException("pending_online_login_unavailable");
            var proof = await issuer.CapturePendingOnlineLoginProofAsync(generation, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var pending = new PendingPosOnlineLogin(factory, store, generation, proof.StaffCode,
                credential, proof.Expiry, proof.AuthorizationEpoch);
            try { await pending.DemandCurrentAsync(cancellationToken).ConfigureAwait(false); return pending; }
            catch { pending.Dispose(); throw; }
        }

        internal async Task DemandCurrentAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentInMemory) throw new InvalidOperationException("pending_online_login_expired");
            var active = await Task.Run(async () => _store.TryReadGeneration(Generation, out _, out _) &&
                await new OnlineSyncGenerationRepository(_factory).IsCurrentAndActiveAsync(Generation).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!active || !IsCurrentInMemory) throw new InvalidOperationException("pending_online_login_authority_changed");
        }

        internal string ReadCredentialForMirrorLogin()
        {
            lock (_sync)
            {
                if (!IsCurrentInMemory) throw new InvalidOperationException("pending_online_login_expired");
                var buffer = Marshal.SecureStringToBSTR(_credential);
                try { return Marshal.PtrToStringBSTR(buffer); }
                finally { Marshal.ZeroFreeBSTR(buffer); }
            }
        }

        public void Dispose()
        {
            lock (_sync) { _credential?.Dispose(); _credential = null; }
        }
    }
}
