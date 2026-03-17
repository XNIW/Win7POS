using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Win7POS.Core.Security;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Pos.Dialogs;

namespace Win7POS.Wpf.Infrastructure.Security
{
    public sealed class OverrideAuthService : IOverrideAuthService
    {
        private readonly UserRepository _userRepo;

        public OverrideAuthService(UserRepository userRepo)
        {
            _userRepo = userRepo ?? throw new ArgumentNullException(nameof(userRepo));
        }

        public async Task<(bool ok, int? authorizerUserId)> RequestOverrideAsync(string operationText, string requiredPermissionCode)
        {
            var authorizableUsers = await _userRepo.ListUsersWithPermissionAsync(requiredPermissionCode).ConfigureAwait(true);
            if (authorizableUsers == null || authorizableUsers.Count == 0)
            {
                ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), "Autorizzazione",
                    "Nessun operatore con permessi adeguati configurato. Impossibile proseguire.");
                return (false, null);
            }

            var items = new List<OverrideOperatorItem>();
            foreach (var u in authorizableUsers)
            {
                items.Add(new OverrideOperatorItem
                {
                    Username = u.Username ?? "",
                    DisplayName = u.DisplayName ?? "",
                    RoleName = u.RoleName ?? ""
                });
            }

            async Task<(bool ok, int? userId)> VerifyAsync(string username, string pin)
            {
                var result = await _userRepo.VerifyPinAsync(username, pin).ConfigureAwait(true);
                var account = result?.User;
                if (account == null) return (false, null);
                var hasPermission = account.IsAdmin ||
                    (account.PermissionCodes != null &&
                     (account.PermissionCodes.Any(p => p == requiredPermissionCode) || account.PermissionCodes.Any(p => p == PermissionCodes.SecurityOverride)));
                return hasPermission ? (true, (int?)account.Id) : (false, null);
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
            var authorizableUsers = await _userRepo.ListAdminUsersAsync().ConfigureAwait(true);
            if (authorizableUsers == null || authorizableUsers.Count == 0)
            {
                ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), "Autorizzazione",
                    "Nessun amministratore configurato. Impossibile proseguire.");
                return (false, null);
            }

            var items = new List<OverrideOperatorItem>();
            foreach (var u in authorizableUsers)
            {
                items.Add(new OverrideOperatorItem
                {
                    Username = u.Username ?? "",
                    DisplayName = u.DisplayName ?? "",
                    RoleName = u.RoleName ?? ""
                });
            }

            async Task<(bool ok, int? userId)> VerifyAsync(string username, string pin)
            {
                var result = await _userRepo.VerifyPinAsync(username, pin).ConfigureAwait(true);
                var account = result?.User;
                if (account == null) return (false, null);
                return account.IsAdmin ? (true, (int?)account.Id) : (false, null);
            }

            var dlg = new OverrideAuthorizationDialog(operationText, items, VerifyAsync, isAdminOnly: true)
            {
                Owner = DialogOwnerHelper.GetSafeOwner()
            };
            var ok = dlg.ShowDialog() == true;
            return ok ? (true, dlg.AuthorizerUserId) : (false, null);
        }
    }
}
