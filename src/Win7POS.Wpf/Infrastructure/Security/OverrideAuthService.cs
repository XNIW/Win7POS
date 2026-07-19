using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Win7POS.Core.Security;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos.Dialogs;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.Infrastructure.Security
{
    public sealed class OverrideAuthService : IOverrideAuthService
    {
        private readonly UserRepository _userRepo;
        private readonly IOperatorSession _session;
        private readonly PosTrustedDeviceStore _trustedDeviceStore = new PosTrustedDeviceStore();

        public OverrideAuthService(UserRepository userRepo, IOperatorSession session)
        {
            _userRepo = userRepo ?? throw new ArgumentNullException(nameof(userRepo));
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public async Task<(bool ok, int? authorizerUserId)> RequestOverrideAsync(string operationText, string requiredPermissionCode)
        {
            if (!EnsureAuthorizationValid())
            {
                return (false, null);
            }

            var authorizer = await ResolveLeaseBoundAuthorizerAsync(
                requiredPermissionCode,
                adminOnly: false).ConfigureAwait(true);
            if (authorizer == null)
            {
                ModernMessageDialog.Show(
                    DialogOwnerHelper.GetSafeOwner(),
                    PosLocalization.T("override.title"),
                    PosLocalization.T("override.noAuthorizedOperator"));
                return (false, null);
            }

            var items = new List<OverrideOperatorItem>
            {
                new OverrideOperatorItem
                {
                    Username = authorizer.Username ?? "",
                    DisplayName = authorizer.DisplayName ?? "",
                    RoleName = authorizer.RoleName ?? ""
                }
            };

            async Task<(bool ok, int? userId)> VerifyAsync(string username, string pin)
            {
                if (!_session.EnsureAuthorizationValid()) return (false, null);
                var expected = await ResolveLeaseBoundAuthorizerAsync(
                    requiredPermissionCode,
                    adminOnly: false).ConfigureAwait(true);
                if (expected == null ||
                    !string.Equals(expected.Username, username, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, null);
                }

                var result = await _userRepo.VerifyPinAsync(username, pin).ConfigureAwait(true);
                var account = result?.User;
                if (account == null || account.Id != expected.Id) return (false, null);
                if (!_session.EnsureAuthorizationValid()) return (false, null);

                var rebound = await ResolveLeaseBoundAuthorizerAsync(
                    requiredPermissionCode,
                    adminOnly: false).ConfigureAwait(true);
                return rebound != null && rebound.Id == account.Id
                    ? (true, (int?)account.Id)
                    : (false, null);
            }

            var dlg = new OverrideAuthorizationDialog(operationText, items, VerifyAsync)
            {
                Owner = DialogOwnerHelper.GetSafeOwner()
            };
            var ok = dlg.ShowDialog() == true;
            return ok ? (true, dlg.AuthorizerUserId) : (false, null);
        }

        public async Task<(bool ok, int? authorizerUserId)> RequestAdminOverrideAsync(string operationText)
        {
            if (!EnsureAuthorizationValid())
            {
                return (false, null);
            }

            var authorizer = await ResolveLeaseBoundAuthorizerAsync(
                requiredPermissionCode: null,
                adminOnly: true).ConfigureAwait(true);
            if (authorizer == null)
            {
                ModernMessageDialog.Show(
                    DialogOwnerHelper.GetSafeOwner(),
                    PosLocalization.T("override.title"),
                    PosLocalization.T("override.noAdminOperator"));
                return (false, null);
            }

            var items = new List<OverrideOperatorItem>
            {
                new OverrideOperatorItem
                {
                    Username = authorizer.Username ?? "",
                    DisplayName = authorizer.DisplayName ?? "",
                    RoleName = authorizer.RoleName ?? ""
                }
            };

            async Task<(bool ok, int? userId)> VerifyAsync(string username, string pin)
            {
                if (!_session.EnsureAuthorizationValid()) return (false, null);
                var expected = await ResolveLeaseBoundAuthorizerAsync(
                    requiredPermissionCode: null,
                    adminOnly: true).ConfigureAwait(true);
                if (expected == null ||
                    !string.Equals(expected.Username, username, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, null);
                }

                var result = await _userRepo.VerifyPinAsync(username, pin).ConfigureAwait(true);
                var account = result?.User;
                if (account == null || account.Id != expected.Id) return (false, null);
                if (!_session.EnsureAuthorizationValid()) return (false, null);

                var rebound = await ResolveLeaseBoundAuthorizerAsync(
                    requiredPermissionCode: null,
                    adminOnly: true).ConfigureAwait(true);
                return rebound != null && rebound.Id == account.Id
                    ? (true, (int?)account.Id)
                    : (false, null);
            }

            var dlg = new OverrideAuthorizationDialog(operationText, items, VerifyAsync, isAdminOnly: true)
            {
                Owner = DialogOwnerHelper.GetSafeOwner()
            };
            var ok = dlg.ShowDialog() == true;
            return ok ? (true, dlg.AuthorizerUserId) : (false, null);
        }

        private async Task<UserAccount> ResolveLeaseBoundAuthorizerAsync(
            string requiredPermissionCode,
            bool adminOnly)
        {
            if (!_trustedDeviceStore.TryRead(out var trustedSession) || trustedSession == null)
            {
                return null;
            }

            var username = await _userRepo
                .FindTrustedRemoteStaffUsernameAsync(
                    trustedSession.ShopId,
                    trustedSession.ShopCode,
                    trustedSession.StaffId,
                    trustedSession.StaffCode,
                    trustedSession.StaffCredentialVersion)
                .ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(username))
            {
                return null;
            }

            var account = await _userRepo.GetByUsernameAsync(username).ConfigureAwait(true);
            if (account == null || !account.IsActive)
            {
                return null;
            }

            if (adminOnly)
            {
                return account.IsAdmin ? account : null;
            }

            var hasPermission = account.IsAdmin ||
                (account.PermissionCodes != null &&
                 (account.PermissionCodes.Any(permission =>
                      string.Equals(permission, requiredPermissionCode, StringComparison.Ordinal)) ||
                  account.PermissionCodes.Any(permission =>
                      string.Equals(permission, PermissionCodes.SecurityOverride, StringComparison.Ordinal))));
            return hasPermission ? account : null;
        }

        private bool EnsureAuthorizationValid()
        {
            if (_session.EnsureAuthorizationValid())
            {
                return true;
            }

            ModernMessageDialog.Show(
                DialogOwnerHelper.GetSafeOwner(),
                PosLocalization.T("override.title"),
                PosLocalization.T("access.login.authorizationExpired"));
            return false;
        }
    }
}
