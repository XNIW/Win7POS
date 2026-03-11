using System;
using System.Collections.Generic;
using System.Linq;
using Win7POS.Core.Security;

namespace Win7POS.Wpf.Infrastructure.Security
{
    public sealed class PermissionService : IPermissionService
    {
        private readonly IOperatorSession _session;

        public PermissionService(IOperatorSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public bool Has(string permissionCode)
        {
            if (string.IsNullOrEmpty(permissionCode)) return false;
            var user = _session.CurrentUser;
            if (user == null) return false;
            if (user.IsAdmin) return true;
            var codes = user.PermissionCodes;
            return codes != null && ((IEnumerable<string>)codes).Any(p => string.Equals(p, permissionCode, StringComparison.Ordinal));
        }

        public void Demand(string permissionCode, string operationText)
        {
            if (!Has(permissionCode))
                throw new InvalidOperationException("Permesso negato: " + (operationText ?? permissionCode));
        }

        public bool CanOverride(string permissionCode)
        {
            var user = _session.CurrentUser;
            if (user == null) return false;
            return user.CanOverride && Has(PermissionCodes.SecurityOverride);
        }
    }
}
