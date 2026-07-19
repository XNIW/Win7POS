using System;
using Win7POS.Core.Security;

namespace Win7POS.Wpf.Infrastructure.Security
{
    /// <summary>
    /// RBAC ristretto per la shell recovery. Non richiede una authorization
    /// lease, ma non puo concedere operazioni esterne all'allowlist recovery.
    /// </summary>
    public sealed class LocalRecoveryPermissionService : IPermissionService
    {
        private readonly IOperatorSession _session;

        public LocalRecoveryPermissionService(IOperatorSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public bool Has(string permissionCode)
        {
            if (!_session.IsLoggedIn)
            {
                return false;
            }

            return LocalRecoveryPermissionPolicy.IsGranted(
                _session.CurrentUser,
                permissionCode);
        }

        public void Demand(string permissionCode, string operationText)
        {
            if (!Has(permissionCode))
            {
                throw new InvalidOperationException(
                    "Permesso recovery negato: " + (operationText ?? permissionCode));
            }
        }

        public bool CanOverride(string permissionCode)
        {
            return false;
        }
    }
}
