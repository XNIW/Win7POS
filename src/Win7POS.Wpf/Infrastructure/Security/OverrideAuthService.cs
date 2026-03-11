using System;
using System.Linq;
using System.Windows;
using Win7POS.Core.Security;
using Win7POS.Data.Repositories;
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

        public bool RequestOverride(string operationText, string requiredPermissionCode, out int? authorizerUserId)
        {
            authorizerUserId = null;

            (bool ok, int? userId) Verify(string username, string pin)
            {
                var result = _userRepo.VerifyPinAsync(username, pin).GetAwaiter().GetResult();
                var account = result?.User;
                if (account == null) return (false, null);
                var hasPermission = account.IsAdmin ||
                    (account.PermissionCodes != null &&
                     (account.PermissionCodes.Any(p => p == requiredPermissionCode) || account.PermissionCodes.Any(p => p == PermissionCodes.SecurityOverride)));
                return hasPermission ? (true, (int?)account.Id) : (false, null);
            }

            var dlg = new OverrideAuthorizationDialog(operationText, requiredPermissionCode, Verify)
            {
                Owner = Application.Current?.MainWindow
            };
            var result = dlg.ShowDialog() == true;
            if (result)
                authorizerUserId = dlg.AuthorizerUserId;
            return result;
        }
    }
}
