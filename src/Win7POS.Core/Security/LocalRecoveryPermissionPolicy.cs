using System;
using System.Linq;

namespace Win7POS.Core.Security
{
    /// <summary>
    /// Limita una sessione di recovery locale alle sole operazioni necessarie
    /// per rendere nuovamente utilizzabile il catalogo o ripristinare i dati.
    /// L'allowlist non concede permessi: resta sempre necessario che il ruolo
    /// locale dell'operatore possieda il permesso richiesto.
    /// </summary>
    public static class LocalRecoveryPermissionPolicy
    {
        public static bool IsAllowed(string permissionCode)
        {
            return string.Equals(permissionCode, PermissionCodes.CatalogView, StringComparison.Ordinal) ||
                string.Equals(permissionCode, PermissionCodes.CatalogEdit, StringComparison.Ordinal) ||
                string.Equals(permissionCode, PermissionCodes.CatalogImport, StringComparison.Ordinal) ||
                string.Equals(permissionCode, PermissionCodes.CatalogPriceEdit, StringComparison.Ordinal) ||
                string.Equals(permissionCode, PermissionCodes.DbMaintenance, StringComparison.Ordinal) ||
                string.Equals(permissionCode, PermissionCodes.DbBackup, StringComparison.Ordinal) ||
                string.Equals(permissionCode, PermissionCodes.DbRestore, StringComparison.Ordinal);
        }

        public static bool IsGranted(UserAccount user, string permissionCode)
        {
            if (user == null || !user.IsActive || !IsAllowed(permissionCode))
            {
                return false;
            }

            if (user.IsAdmin)
            {
                return true;
            }

            return user.PermissionCodes?.Any(code =>
                string.Equals(code, permissionCode, StringComparison.Ordinal)) == true;
        }
    }
}
