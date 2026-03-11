namespace Win7POS.Core.Security
{
    /// <summary>Snapshot della sessione operatore corrente (utente + permessi).</summary>
    public sealed class CurrentSession
    {
        public UserAccount User { get; set; }

        public bool IsLoggedIn => User != null && User.IsActive;
        public bool IsAdmin => User?.IsAdmin ?? false;
        public string DisplayName => User?.DisplayName ?? "";
        public string RoleName => User?.RoleName ?? "";
    }
}
