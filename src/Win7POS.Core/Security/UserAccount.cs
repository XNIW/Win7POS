using System.Collections.Generic;

namespace Win7POS.Core.Security
{
    public sealed class UserAccount
    {
        public int Id { get; set; }
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int RoleId { get; set; }
        public string RoleCode { get; set; } = "";
        public string RoleName { get; set; } = "";
        public bool IsActive { get; set; }
        public bool RequirePinChange { get; set; }
        public int MaxDiscountPercent { get; set; }
        public bool CanOverride { get; set; }

        /// <summary>Elenco codici permesso per questo utente (dal ruolo).</summary>
        public IReadOnlyList<string> PermissionCodes { get; set; } = new List<string>();

        public bool IsAdmin => string.Equals(RoleCode, "admin", System.StringComparison.OrdinalIgnoreCase);
    }
}
