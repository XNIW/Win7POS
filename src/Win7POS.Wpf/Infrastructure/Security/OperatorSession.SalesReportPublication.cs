using System;
using System.Threading;
using Win7POS.Core.Reports;
using Win7POS.Core.Security;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.Infrastructure.Security
{
    internal sealed class SalesReportReadAuthority
    {
        internal SalesReportReadAuthority(SalesReportFilter filter, string permission, int userId, long version, PosOnlineSyncRevocationLatch.AuthorizationReadStamp stamp, PosAuthorizationCommitExpiryGuard expiry)
        { Filter = filter; Permission = permission; UserId = userId; Version = version; Stamp = stamp; Expiry = expiry; }
        internal SalesReportFilter Filter { get; }
        internal string Permission { get; }
        internal int UserId { get; }
        internal long Version { get; }
        internal PosOnlineSyncRevocationLatch.AuthorizationReadStamp Stamp { get; }
        internal PosAuthorizationCommitExpiryGuard Expiry { get; }
    }

    public sealed partial class OperatorSession
    {
        internal SalesReportReadAuthority CaptureSalesReportReadAuthority(SalesReportFilter filter, string permissionCode)
        {
            if (filter == null) throw new ArgumentNullException(nameof(filter));
            var version = Interlocked.Read(ref _operatorAuthorityVersion);
            var user = _currentUser;
            var decision = _authorizationLeaseGuard.EvaluateAuthorizationUse(out var session, out var expiry);
            if (!decision.Allowed || session == null || expiry == null || user == null || !_currentUserCanUsePosAuthorization ||
                !HasPermission(user, permissionCode) || !PosOnlineSyncSupervisorHost.TryCreateGeneration(session, out var generation) ||
                !string.Equals(filter.ShopId, generation.ShopId, StringComparison.Ordinal) ||
                !PosOnlineSyncRevocationLatch.TryCaptureAuthorizationEpoch(out var epoch))
                throw new InvalidOperationException("report_authorization_denied");
            DemandAuthorizationUseStillValid(user.Id, permissionCode, permissionCode, epoch, generation, expiry, version, TimeSpan.Zero, null);
            var stamp = PosOnlineSyncRevocationLatch.ReadAuthorizationStamp();
            if (stamp.Epoch != epoch) throw new InvalidOperationException("report_operator_scope_changed");
            var authority = new SalesReportReadAuthority(filter, permissionCode, user.Id, version, stamp, expiry);
            if (!IsSalesReportReadAuthorityCurrent(authority, filter)) throw new InvalidOperationException("report_operator_scope_changed");
            return authority;
        }

        internal bool IsSalesReportReadAuthorityCurrent(SalesReportReadAuthority authority, SalesReportFilter filter)
        {
            if (authority == null || filter == null ||
                !string.Equals(authority.Filter.Fingerprint, filter.Fingerprint, StringComparison.Ordinal) ||
                !PosOnlineSyncRevocationLatch.IsAuthorizationReadStampCurrent(authority.Stamp)) return false;
            var user = Volatile.Read(ref _currentUser);
            return authority.Expiry.Evaluate(TimeSpan.Zero).Allowed &&
                Interlocked.Read(ref _operatorAuthorityVersion) == authority.Version && Volatile.Read(ref _currentUserCanUsePosAuthorization) &&
                user != null && user.IsActive && user.Id == authority.UserId && HasPermission(user, authority.Permission) &&
                (filter.CanViewAllOperators ? HasPermission(user, PermissionCodes.RegisterViewAll) : filter.OperatorId == user.Id) &&
                PosOnlineSyncRevocationLatch.IsAuthorizationReadStampCurrent(authority.Stamp);
        }

        // UI reads never take the authorization latch. The caller's report
        // TryEnter protects data revision; these before/after memory checks and
        // independent getter checks reject a concurrent authority transition.
        internal bool TryUseSalesReportReadAuthority(SalesReportReadAuthority authority, SalesReportFilter filter, Action captureOnly)
        {
            if (captureOnly == null || !IsSalesReportReadAuthorityCurrent(authority, filter)) return false;
            captureOnly();
            return IsSalesReportReadAuthorityCurrent(authority, filter);
        }

        // Performs lease/store IO before the caller takes the report publication gate.
        // The returned issuer performs only final in-memory checks at the shared latch.
        internal Func<SalesReportPublicationPermit> PrepareSalesReportPublication(SalesReportReadToken token, SalesReportFilter filter,
            string temporaryPath, string destinationPath, string sha256, long bytes, bool overwrite,
            string permissionCode, CancellationToken cancellationToken)
        {
            if (token == null) throw new ArgumentNullException(nameof(token));
            if (filter == null || !string.Equals(token.ScopeHash, filter.Fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("report_scope_invalid");
            if (permissionCode != PermissionCodes.RegisterView && permissionCode != PermissionCodes.RegisterViewAll && permissionCode != PermissionCodes.DailyCloseView)
                throw new InvalidOperationException("report_permission_invalid");
            cancellationToken.ThrowIfCancellationRequested();
            var version = Interlocked.Read(ref _operatorAuthorityVersion);
            var user = _currentUser;
            var decision = _authorizationLeaseGuard.EvaluateAuthorizationUse(out var session, out var expiry);
            if (!decision.Allowed || session == null || expiry == null || user == null || !_currentUserCanUsePosAuthorization ||
                !HasPermission(user, permissionCode) || !PosOnlineSyncSupervisorHost.TryCreateGeneration(session, out var generation) ||
                !PosOnlineSyncRevocationLatch.TryCaptureAuthorizationEpoch(out var epoch))
                throw new InvalidOperationException("report_authorization_denied");
            DemandAuthorizationUseStillValid(user.Id, permissionCode, permissionCode, epoch, generation, expiry, version, TimeSpan.Zero, null);
            if (!string.Equals(filter.ShopId, generation.ShopId, StringComparison.Ordinal) ||
                (filter.CanViewAllOperators ? !HasPermission(user, PermissionCodes.RegisterViewAll) : filter.OperatorId != user.Id))
                throw new InvalidOperationException("report_operator_scope_changed");
            var issued = 0;
            return () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Interlocked.CompareExchange(ref issued, 1, 0) != 0) throw new InvalidOperationException("report_permit_already_issued");
                SalesReportPublicationPermit permit = null;
                var approved = PosOnlineSyncRevocationLatch.CommitIfAuthorizationCurrent(epoch, generation.Fingerprint, () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DemandCommitExpiryStillValid(expiry, TimeSpan.Zero);
                    DemandOperatorAuthorityStillCurrent(user.Id, permissionCode, permissionCode, version, null);
                    var current = _currentUser;
                    if (!string.Equals(token.ScopeHash, filter.Fingerprint, StringComparison.Ordinal) ||
                        !string.Equals(filter.ShopId, generation.ShopId, StringComparison.Ordinal) ||
                        (filter.CanViewAllOperators ? !HasPermission(current, PermissionCodes.RegisterViewAll) : filter.OperatorId != current?.Id))
                        throw new InvalidOperationException("report_operator_scope_changed");
                }, () => permit = new SalesReportPublicationPermit(Guid.NewGuid().ToString("N"), token, temporaryPath,
                    destinationPath, sha256, bytes, overwrite, user.Id, epoch, version, DateTimeOffset.UtcNow));
                if (!approved) throw new InvalidOperationException("report_authorization_denied");
                return permit;
            };
        }
    }
}
