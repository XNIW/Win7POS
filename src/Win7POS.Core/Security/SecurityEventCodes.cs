namespace Win7POS.Core.Security
{
    /// <summary>Nomi standard eventi audit sicurezza. Un solo nome per azione per evitare log confusi.</summary>
    public static class SecurityEventCodes
    {
        public const string LoginSuccess = "login_success";
        public const string LoginFailed = "login_failed";
        public const string LoginLocked = "login_locked";
        public const string Logout = "logout";
        public const string ForcedLogout = "forced_logout";
        public const string RequirePinChangeEnforced = "require_pin_change_enforced";
        public const string PinChanged = "pin_changed";
        public const string PinReset = "pin_reset";
        public const string UserCreated = "user_created";
        public const string FirstRunAdminCreated = "first_run_admin_created";
        public const string UserUpdated = "user_updated";
        public const string UserEnabled = "user_enabled";
        public const string UserDisabled = "user_disabled";
        public const string RoleChanged = "role_changed";
        public const string RoleCreated = "role_created";
        public const string RoleDuplicated = "role_duplicated";
        public const string RoleRenamed = "role_renamed";
        public const string RoleDeleted = "role_deleted";
        public const string RolePermissionsChanged = "role_permissions_changed";
        public const string Override = "override";
        public const string OverrideRequested = "override_requested";
        public const string OverrideGranted = "override_granted";
        public const string OverrideDenied = "override_denied";
        public const string OverrideFailed = "override_failed";
        public const string DbBackup = "db_backup";
        public const string DbRestore = "db_restore";
        public const string Refund = "refund";
    }
}
