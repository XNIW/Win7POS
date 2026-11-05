using System;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Import;
using Win7POS.Core.Security;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.Infrastructure.Security
{
    public sealed partial class OperatorSession
    {
        // Read-only preflight does not hold the authorization-use semaphore while
        // opening a database or verifying source files. It cannot commit a mutation.
        internal HistoricalReadAuthority CaptureHistoricalReadAuthority(HistoricalImportOperationKind kind, string expectedShopId)
        {
            if (kind != HistoricalImportOperationKind.Apply && kind != HistoricalImportOperationKind.Undo) throw new ArgumentOutOfRangeException(nameof(kind));
            var permission = kind == HistoricalImportOperationKind.Apply ? PermissionCodes.RegisterImportHistory : PermissionCodes.RegisterCancelHistory;
            if (string.IsNullOrWhiteSpace(expectedShopId)) throw new InvalidOperationException("historical_authorization_binding_mismatch");
            return CaptureHistoricalReadAuthorityCore(permission, expectedShopId);
        }

        // File analysis can precede the DB shop binding; the trusted generation
        // and requested permission remain mandatory. Passive checks reuse this
        // one frozen expiry guard, never the lease/store evaluator.
        internal HistoricalReadAuthority CaptureHistoricalUiReadAuthority(string permission)
        {
            if (permission != PermissionCodes.RegisterImportHistory && permission != PermissionCodes.RegisterCancelHistory && permission != PermissionCodes.RegisterViewAll)
                throw new InvalidOperationException("historical_permission_invalid");
            return CaptureHistoricalReadAuthorityCore(permission, null);
        }

        private HistoricalReadAuthority CaptureHistoricalReadAuthorityCore(string permission, string expectedShopId)
        {
            var version = Interlocked.Read(ref _operatorAuthorityVersion);
            var user = _currentUser;
            var decision = _authorizationLeaseGuard.EvaluateAuthorizationUse(out var session, out var expiry);
            if (!decision.Allowed || session == null || expiry == null || user == null ||
                !PosOnlineSyncSupervisorHost.TryCreateGeneration(session, out var generation) ||
                !PosOnlineSyncRevocationLatch.TryCaptureAuthorizationEpoch(out var epoch) ||
                (expectedShopId != null && !string.Equals(generation.ShopId, expectedShopId, StringComparison.Ordinal)))
                throw new InvalidOperationException("historical_authorization_binding_mismatch");
            Action demand = () => DemandAuthorizationUseStillValid(user.Id, permission, permission, epoch, generation, expiry, version, TimeSpan.Zero, null);
            demand();
            var stamp = PosOnlineSyncRevocationLatch.ReadAuthorizationStamp();
            if (stamp.Epoch != epoch) throw new InvalidOperationException("historical_authorization_binding_mismatch");
            return new HistoricalReadAuthority(demand, () =>
            {
                var current = Volatile.Read(ref _currentUser);
                return PosOnlineSyncRevocationLatch.IsAuthorizationReadStampCurrent(stamp) && expiry.Evaluate(TimeSpan.Zero).Allowed &&
                    Interlocked.Read(ref _operatorAuthorityVersion) == version && Volatile.Read(ref _currentUserCanUsePosAuthorization) &&
                    current != null && current.IsActive && current.Id == user.Id && HasPermission(current, permission) &&
                    PosOnlineSyncRevocationLatch.IsAuthorizationReadStampCurrent(stamp);
            }, publish =>
            {
                demand();
                if (!PosOnlineSyncRevocationLatch.CommitIfAuthorizationCurrent(epoch, generation.Fingerprint, () =>
                {
                    DemandCommitExpiryStillValid(expiry, TimeSpan.Zero);
                    DemandOperatorAuthorityStillCurrent(user.Id, permission, permission, version, null);
                }, publish)) throw new InvalidOperationException("historical_authorization_denied");
            });
        }

        internal sealed class HistoricalReadAuthority
        {
            private readonly Action _demand;
            private readonly Action<Action> _publish;
            private readonly Func<bool> _isCurrentInMemory;
            internal HistoricalReadAuthority(Action demand, Func<bool> isCurrentInMemory, Action<Action> publish)
            { _demand = demand; _isCurrentInMemory = isCurrentInMemory; _publish = publish; }
            internal bool IsCurrentInMemory => _isCurrentInMemory();
            internal void DemandStillValid() => _demand();
            internal void Publish(Action captureOnly) => _publish(captureOnly);
        }

        internal async Task<HistoricalAuthorizationUseLease> BeginHistoricalAuthorizationUseAsync(
            HistoricalImportOperationKind kind, string operationId, string requestFingerprint,
            string datasetId, long restoreEpoch, Action demandDatabaseAuthority, CancellationToken cancellationToken)
        {
            if (demandDatabaseAuthority == null) throw new ArgumentNullException(nameof(demandDatabaseAuthority));
            var version = Interlocked.Read(ref _operatorAuthorityVersion);
            var latch = await PosOnlineSyncRevocationLatch.EnterAuthorizationUseAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var permission = kind == HistoricalImportOperationKind.Apply ? PermissionCodes.RegisterImportHistory : PermissionCodes.RegisterCancelHistory;
                var decision = _authorizationLeaseGuard.EvaluateAuthorizationUse(out var session, out var expiry);
                SetAuthorizationDecision(decision);
                if (!decision.Allowed || expiry == null || session == null ||
                    !PosOnlineSyncSupervisorHost.TryCreateGeneration(session, out var generation) ||
                    !PosOnlineSyncRevocationLatch.TryCaptureAuthorizationEpoch(out var epoch))
                    throw new InvalidOperationException("historical_authorization_unavailable");
                var user = _currentUser;
                if (user == null || !_currentUserCanUsePosAuthorization || !HasPermission(user, permission))
                    throw new InvalidOperationException("historical_permission_denied");
                var lease = new HistoricalAuthorizationUseLease(latch);
                Action demand = () =>
                {
                    lease.DemandNotDisposed();
                    cancellationToken.ThrowIfCancellationRequested();
                    demandDatabaseAuthority();
                    DemandAuthorizationUseStillValid(user.Id, permission, permission, epoch, generation, expiry, version, TimeSpan.Zero, null);
                };
                lease.Guard = new HistoricalImportAuthorizationCommitGuard(kind, operationId, requestFingerprint,
                    generation.ShopId, generation.ShopCode, generation.ShopDeviceId, user.Id, datasetId, restoreEpoch,
                    epoch, generation.Fingerprint, generation.StaffId, generation.StaffCredentialVersion, demand,
                    (remaining, commit) =>
                    {
                        demand();
                        CommitAuthorizationUseStillValid(user.Id, permission, permission, epoch, generation, expiry,
                            version, remaining, 0, () => { cancellationToken.ThrowIfCancellationRequested(); commit(); }, null);
                    });
                lease.Guard.DemandStillValid();
                return lease;
            }
            catch { latch.Dispose(); throw; }
        }

        internal sealed class HistoricalAuthorizationUseLease : IDisposable
        {
            private IDisposable _latch;
            internal HistoricalAuthorizationUseLease(IDisposable latch) { _latch = latch; }
            internal HistoricalImportAuthorizationCommitGuard Guard { get; set; }
            internal void DemandNotDisposed() { if (_latch == null) throw new ObjectDisposedException(nameof(HistoricalAuthorizationUseLease)); }
            public void Dispose() => Interlocked.Exchange(ref _latch, null)?.Dispose();
        }
    }
}
