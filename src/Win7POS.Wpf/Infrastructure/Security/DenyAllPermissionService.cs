using System;

namespace Win7POS.Wpf.Infrastructure.Security
{
    internal sealed class DenyAllPermissionService : IPermissionService
    {
        public static readonly DenyAllPermissionService Instance = new DenyAllPermissionService();

        private DenyAllPermissionService()
        {
        }

        public bool Has(string permissionCode)
        {
            return false;
        }

        public void Demand(string permissionCode, string operationText)
        {
            throw new InvalidOperationException("Permesso negato: " + (operationText ?? permissionCode));
        }

        public bool CanOverride(string permissionCode)
        {
            return false;
        }
    }
}
