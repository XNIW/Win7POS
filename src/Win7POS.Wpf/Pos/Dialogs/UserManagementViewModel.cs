using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.ViewModels;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class UserManagementViewModel : INotifyPropertyChanged
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly UserRepository _userRepo;
        private readonly RoleRepository _roleRepo;
        private readonly SecurityRepository _securityRepo;
        private UserAccount _selectedUser;
        private UserRole _selectedRole;
        private string _displayName = "";
        private int _selectedRoleId;
        private bool _isActive = true;
        private string _newPin = "";
        private string _status = "";
        private bool _isBusy;
        private string _filter = "";
        private bool _permissionEditMode; // true = editing role permissions, false = viewing user's role (read-only)
        private string _currentOperatorDisplay = "—";
        private string _currentOperatorUsername = "";
        private bool _canManageRoles;
        private bool _isDirty;

        internal Window OwnerWindow { get; set; }

        public UserManagementViewModel()
        {
            var options = PosDbOptions.Default();
            _factory = new SqliteConnectionFactory(options);
            _userRepo = new UserRepository(_factory);
            _roleRepo = new RoleRepository(_factory);
            _securityRepo = new SecurityRepository(_factory);

            Users = new ObservableCollection<UserAccount>();
            FilteredUsers = new ObservableCollection<UserAccount>();
            Roles = new ObservableCollection<UserRole>();
            PermissionItems = new ObservableCollection<PermissionItem>();

            LoadCommand = new RelayCommand(() => _ = LoadAsync(), () => !IsBusy);
            SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => !IsBusy && SelectedUser != null);
            SaveUserCommand = SaveCommand;
            CancelUserChangesCommand = new RelayCommand(() => _ = CancelUserChangesAsync(), () => !IsBusy && SelectedUser != null);
            NewUserCommand = new RelayCommand(() => _ = NewUserAsync(), () => !IsBusy);
            ResetPinCommand = new RelayCommand(() => _ = ResetPinAsync(), () => !IsBusy && SelectedUser != null && !string.IsNullOrWhiteSpace(NewPin));
            SaveRolePermissionsCommand = new RelayCommand(() => _ = SaveSelectedRolePermissionsAsync(), () => !IsBusy && CanSaveSelectedRolePermissions);
            NewRoleCommand = new RelayCommand(() => _ = NewRoleAsync(), () => !IsBusy && CanManageRoles);
            DuplicateRoleCommand = new RelayCommand(() => _ = DuplicateRoleAsync(), () => !IsBusy && SelectedRole != null && CanManageRoles);
            RenameRoleCommand = new RelayCommand(() => _ = RenameRoleAsync(), () => !IsBusy && CanManageSelectedRole);
            DeleteRoleCommand = new RelayCommand(() => _ = DeleteRoleAsync(), () => !IsBusy && CanManageSelectedRole);
            ClearFilterCommand = new RelayCommand(ClearFilter, () => !string.IsNullOrWhiteSpace(Filter));
            ReloadCommand = new RelayCommand(() => _ = ReloadWithConfirmAsync(), () => !IsBusy);
            CancelCommand = CancelUserChangesCommand;
            _ = LoadAsync();
        }

        private void ClearFilter() { Filter = ""; }

        private void SetDirty() { IsDirty = true; }

        private async Task ReloadWithConfirmAsync()
        {
            if (IsDirty && !Win7POS.Wpf.Import.ApplyConfirmDialog.ShowConfirm(
                    OwnerWindow ?? DialogOwnerHelper.GetSafeOwner(),
                    PosLocalization.T("users.confirmReloadTitle"),
                    PosLocalization.T("users.confirmReloadUnsaved")))
                return;
            await LoadAsync().ConfigureAwait(true);
        }

        private static string GetRoleOperativeDescription(UserRole role)
        {
            if (role == null) return "";
            var code = (role.Code ?? "").Trim().ToLowerInvariant();
            switch (code)
            {
                case "admin": return PosLocalization.T("users.roleAdminDescription");
                case "manager": return PosLocalization.T("users.roleManagerDescription");
                case "supervisor": return PosLocalization.T("users.roleSupervisorDescription");
                case "cashier": return PosLocalization.T("users.roleCashierDescription");
                default: return PosLocalization.T(role.IsSystem ? "users.roleSystemDescription" : "users.roleCustomDescription");
            }
        }

        public ObservableCollection<UserAccount> Users { get; }
        public ObservableCollection<UserAccount> FilteredUsers { get; }
        public ObservableCollection<UserRole> Roles { get; }
        public ObservableCollection<PermissionItem> PermissionItems { get; }

        public UserAccount SelectedUser
        {
            get => _selectedUser;
            set { _selectedUser = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsUserSelected)); OnPropertyChanged(nameof(HasSelectedUser)); OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(DetailUserTitle)); OnPropertyChanged(nameof(EditingUserDisplay)); OnPropertyChanged(nameof(SelectedUserRoleDisplay)); OnPropertyChanged(nameof(SelectedUsername)); (SaveUserCommand as RelayCommand)?.RaiseCanExecuteChanged(); (CancelUserChangesCommand as RelayCommand)?.RaiseCanExecuteChanged(); _ = LoadSelectedUserAsync(); }
        }

        /// <summary>Username dell'utente selezionato (sola lettura).</summary>
        public string SelectedUsername => SelectedUser?.Username ?? "";

        public bool IsUserSelected => SelectedUser != null;

        /// <summary>True quando c'è un utente selezionato; usato per alternare form / placeholder senza sovrapposizioni.</summary>
        public bool HasSelectedUser => SelectedUser != null;

        public string DisplayName { get => _displayName; set { _displayName = value ?? ""; OnPropertyChanged(); SetDirty(); } }
        public int SelectedRoleId { get => _selectedRoleId; set { _selectedRoleId = value; OnPropertyChanged(); SetDirty(); } }
        public bool IsActive { get => _isActive; set { _isActive = value; OnPropertyChanged(); OnPropertyChanged(nameof(AccountStatusText)); SetDirty(); } }
        public string NewPin { get => _newPin; set { _newPin = value ?? ""; OnPropertyChanged(); (ResetPinCommand as RelayCommand)?.RaiseCanExecuteChanged(); SetDirty(); } }
        public string Status { get => _status; set { _status = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(StatusMessage)); } }
        public bool IsBusy { get => _isBusy; set { _isBusy = value; OnPropertyChanged(); (LoadCommand as RelayCommand)?.RaiseCanExecuteChanged(); (ReloadCommand as RelayCommand)?.RaiseCanExecuteChanged(); (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged(); (CancelUserChangesCommand as RelayCommand)?.RaiseCanExecuteChanged(); (NewUserCommand as RelayCommand)?.RaiseCanExecuteChanged(); (ResetPinCommand as RelayCommand)?.RaiseCanExecuteChanged(); (SaveRolePermissionsCommand as RelayCommand)?.RaiseCanExecuteChanged(); (NewRoleCommand as RelayCommand)?.RaiseCanExecuteChanged(); (DuplicateRoleCommand as RelayCommand)?.RaiseCanExecuteChanged(); (RenameRoleCommand as RelayCommand)?.RaiseCanExecuteChanged(); (DeleteRoleCommand as RelayCommand)?.RaiseCanExecuteChanged(); } }
        public string Filter { get => _filter; set { _filter = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(IsFilterActive)); (ClearFilterCommand as RelayCommand)?.RaiseCanExecuteChanged(); RefreshFilteredUsers(); } }

        /// <summary>Testo mostrato in dialog: operatore e ruolo correnti (es. "Operatore: Mario Rossi (Cassiere)").</summary>
        public string CurrentOperatorDisplay { get => _currentOperatorDisplay; set { _currentOperatorDisplay = value ?? "—"; OnPropertyChanged(); } }

        /// <summary>Username dell'operatore loggato, per evidenziare "utente attuale" in lista.</summary>
        public string CurrentOperatorUsername { get => _currentOperatorUsername; set { _currentOperatorUsername = value ?? ""; OnPropertyChanged(); } }
        public bool CanManageRoles
        {
            get => _canManageRoles;
            set
            {
                if (_canManageRoles == value) return;
                _canManageRoles = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanManageSelectedRole));
                OnPropertyChanged(nameof(CanSaveSelectedRolePermissions));
                OnPropertyChanged(nameof(PermissionsSectionTitle));
                OnPropertyChanged(nameof(SaveRolePermissionsToolTip));
                OnPropertyChanged(nameof(RenameRoleToolTip));
                OnPropertyChanged(nameof(DeleteRoleToolTip));
                UpdatePermissionItemEditability();
                RaiseRoleCommandsCanExecuteChanged();
            }
        }

        public bool IsFilterActive => !string.IsNullOrWhiteSpace(Filter?.Trim());
        public bool IsUsersListEmpty => FilteredUsers.Count == 0;
        public string EmptyUsersMessage => PosLocalization.T(IsFilterActive ? "users.noUserFound" : "users.noUsers");
        public string SelectedUserRoleDisplay => SelectedUser?.RoleName ?? "";
        public string SelectedRoleSystemInfo => SelectedRole == null ? "" : PosLocalization.T(SelectedRole.IsSystem ? "users.systemRoleInfo" : "users.customRoleInfo");
        public string DetailUserTitle => PosLocalization.T("users.detailUserTitle");
        public string EditingUserDisplay => SelectedUser != null ? PosLocalization.F("users.editingUser", SelectedUser.Username) : "";
        public string PermissionsRoleTitle => SelectedRole != null ? PosLocalization.F("users.permissionsForRole", SelectedRole.Name) : PosLocalization.T("users.rolePermissions");
        /// <summary>Messaggio quando il ruolo selezionato è di sistema (permessi non modificabili).</summary>
        public bool IsSelectedRoleSystem => SelectedRole != null && SelectedRole.IsSystem;
        public string PermessiRoleSystemMessage => SelectedRole != null && SelectedRole.IsSystem ? PosLocalization.T("users.systemRoleReadonlyPermissions") : "";

        /// <summary>Visibility per il messaggio permessi ruolo di sistema (Visible se ruolo sistema).</summary>
        public Visibility PermessiRoleSystemMessageVisibility => string.IsNullOrEmpty(PermessiRoleSystemMessage) ? Visibility.Collapsed : Visibility.Visible;
        public string AccountStatusText => PosLocalization.T(IsActive ? "users.accountCanAccess" : "users.accountBlocked");

        /// <summary>Modifiche utente non salvate (nome, ruolo, stato, PIN).</summary>
        public bool IsDirty { get => _isDirty; private set { _isDirty = value; OnPropertyChanged(); } }

        /// <summary>Ruolo selezionato: nome e tipo (sistema/personalizzato).</summary>
        public string SelectedRoleDescription => SelectedRole == null ? "" : PosLocalization.F(
            "users.roleDescription",
            SelectedRole.Name,
            PosLocalization.T(SelectedRole.IsSystem ? "users.systemRoleLower" : "users.customRoleLower"));

        /// <summary>Descrizione operativa del ruolo (es. "accesso completo", "POS e stampa").</summary>
        public string SelectedRoleOperativeDescription => GetRoleOperativeDescription(SelectedRole);

        /// <summary>Titolo pagina (Utenti e ruoli).</summary>
        public string PageTitle => PosLocalization.T("users.title");

        /// <summary>Messaggio di stato per footer (alias di Status).</summary>
        public string StatusMessage => Status;

        public ICommand ClearFilterCommand { get; }
        public ICommand SaveUserCommand { get; }
        public ICommand CancelUserChangesCommand { get; }
        public ICommand ReloadCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand LoadCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand NewUserCommand { get; }
        public ICommand ResetPinCommand { get; }
        public ICommand SaveRolePermissionsCommand { get; }
        public ICommand NewRoleCommand { get; }
        public ICommand DuplicateRoleCommand { get; }
        public ICommand RenameRoleCommand { get; }
        public ICommand DeleteRoleCommand { get; }

        /// <summary>Ruolo selezionato per modifica permessi. Se non di sistema, i permessi sono editabili.</summary>
        public UserRole SelectedRole
        {
            get => _selectedRole;
            set
            {
                _selectedRole = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsRoleSelected));
                OnPropertyChanged(nameof(CanEditSelectedRole));
                OnPropertyChanged(nameof(CanManageSelectedRole));
                OnPropertyChanged(nameof(CanSaveSelectedRolePermissions));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedRoleSystemInfo));
                OnPropertyChanged(nameof(PermissionsRoleTitle));
                (SaveRolePermissionsCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (DuplicateRoleCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (RenameRoleCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (DeleteRoleCommand as RelayCommand)?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(SaveRolePermissionsToolTip));
                OnPropertyChanged(nameof(RenameRoleToolTip));
                OnPropertyChanged(nameof(DeleteRoleToolTip));
                OnPropertyChanged(nameof(SelectedRoleDescription));
                OnPropertyChanged(nameof(SelectedRoleOperativeDescription));
                OnPropertyChanged(nameof(IsSelectedRoleSystem));
                OnPropertyChanged(nameof(PermessiRoleSystemMessage));
                OnPropertyChanged(nameof(PermessiRoleSystemMessageVisibility));
                UpdatePermissionItemEditability();
                _ = LoadSelectedRolePermissionsAsync();
            }
        }

        public bool IsRoleSelected => SelectedRole != null;
        public bool CanEditSelectedRole => SelectedRole != null && !SelectedRole.IsSystem;
        public bool CanManageSelectedRole => CanManageRoles && CanEditSelectedRole;
        public bool CanSaveSelectedRolePermissions => _permissionEditMode && CanManageSelectedRole;
        public bool PermissionEditMode => _permissionEditMode;
        public bool HasSelection => IsUserSelected || IsRoleSelected;
        public string PermissionsSectionTitle => _permissionEditMode
            ? PosLocalization.T(CanManageRoles ? "users.permissionsRoleEditable" : "users.permissionsRoleReadonly")
            : PosLocalization.T("users.permissionsFromRoleReadonly");
        public string SaveRolePermissionsToolTip => !CanManageRoles
            ? PosLocalization.T("users.manageRolesDeniedTooltip")
            : CanEditSelectedRole
                ? PosLocalization.T("users.saveRolePermissionsTooltip")
                : (SelectedRole != null && SelectedRole.IsSystem ? PosLocalization.T("users.systemRolesReadOnly") : PosLocalization.T("users.selectRoleForPermissionsTooltip"));
        public string RenameRoleToolTip => !CanManageRoles
            ? PosLocalization.T("users.manageRolesDeniedTooltip")
            : CanEditSelectedRole
                ? PosLocalization.T("users.renameRoleTooltip")
                : PosLocalization.T("users.selectCustomRoleRenameTooltip");
        public string DeleteRoleToolTip => !CanManageRoles
            ? PosLocalization.T("users.manageRolesDeniedTooltip")
            : CanEditSelectedRole
                ? PosLocalization.T("users.deleteRoleTooltip")
                : PosLocalization.T("users.selectCustomRoleDeleteTooltip");

#pragma warning disable CS0067 // Usato da UserManagementDialog che si sottoscrive a RequestClose
        public event Action<bool> RequestClose;
#pragma warning restore CS0067
        public event PropertyChangedEventHandler PropertyChanged;

        private async Task LoadAsync()
        {
            IsBusy = true;
            try
            {
                var users = await _userRepo.ListAsync().ConfigureAwait(true);
                var roles = await _roleRepo.GetAllAsync().ConfigureAwait(true);
                Users.Clear();
                foreach (var u in users)
                    Users.Add(u);
                RefreshFilteredUsers();
                Roles.Clear();
                foreach (var r in roles)
                    Roles.Add(r);
                if (SelectedUser != null)
                {
                    var again = Users.FirstOrDefault(x => x.Id == SelectedUser.Id);
                    SelectedUser = again;
                }
                Status = PosLocalization.T("users.loaded");
                IsDirty = false;
            }
            catch (Exception ex)
            {
                Status = PosLocalization.F("common.errorWithMessage", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void RefreshFilteredUsers()
        {
            var filter = (Filter ?? "").Trim().ToLowerInvariant();
            FilteredUsers.Clear();
            foreach (var u in Users)
            {
                if (string.IsNullOrEmpty(filter) ||
                    (u.DisplayName ?? "").ToLowerInvariant().Contains(filter) ||
                    (u.Username ?? "").ToLowerInvariant().Contains(filter))
                    FilteredUsers.Add(u);
            }
            OnPropertyChanged(nameof(IsUsersListEmpty));
            OnPropertyChanged(nameof(EmptyUsersMessage));
        }

        private async Task LoadSelectedUserAsync()
        {
            if (SelectedUser == null)
            {
                DisplayName = "";
                SelectedRoleId = 0;
                IsActive = true;
                NewPin = "";
                PermissionItems.Clear();
                _permissionEditMode = false;
                IsDirty = false;
                OnPropertyChanged(nameof(PermissionEditMode));
                OnPropertyChanged(nameof(PermissionsSectionTitle));
                OnPropertyChanged(nameof(CanSaveSelectedRolePermissions));
                OnPropertyChanged(nameof(HasSelection));
                RaiseRoleCommandsCanExecuteChanged();
                return;
            }
            OnPropertyChanged(nameof(HasSelection));
            DisplayName = SelectedUser.DisplayName;
            SelectedRoleId = SelectedUser.RoleId;
            IsActive = SelectedUser.IsActive;
            NewPin = "";
            IsDirty = false;
            _permissionEditMode = false;
            OnPropertyChanged(nameof(PermissionEditMode));
            OnPropertyChanged(nameof(PermissionsSectionTitle));
            OnPropertyChanged(nameof(CanSaveSelectedRolePermissions));
            RaiseRoleCommandsCanExecuteChanged();
            await LoadRolePermissionsAsync(SelectedUser.RoleId, isEditable: false).ConfigureAwait(true);
        }

        private async Task CancelUserChangesAsync()
        {
            if (SelectedUser == null) return;
            var id = SelectedUser.Id;
            var refreshed = await _userRepo.GetByIdAsync(id).ConfigureAwait(true);
            if (refreshed == null) return;
            var idx = Users.TakeWhile(u => u.Id != id).Count();
            if (idx < Users.Count) Users[idx] = refreshed;
            RefreshFilteredUsers();
            SelectedUser = refreshed;
            Status = PosLocalization.T("users.changesCancelled");
        }

        private async Task LoadSelectedRolePermissionsAsync()
        {
            if (SelectedRole == null)
                return;
            _permissionEditMode = true;
            await LoadRolePermissionsAsync(SelectedRole.Id, CanManageSelectedRole).ConfigureAwait(true);
            OnPropertyChanged(nameof(PermissionEditMode));
            OnPropertyChanged(nameof(PermissionsSectionTitle));
            OnPropertyChanged(nameof(CanSaveSelectedRolePermissions));
            RaiseRoleCommandsCanExecuteChanged();
        }

        private void RaiseRoleCommandsCanExecuteChanged()
        {
            (SaveRolePermissionsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (NewRoleCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DuplicateRoleCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RenameRoleCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteRoleCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void UpdatePermissionItemEditability()
        {
            var isEditable = _permissionEditMode && CanManageSelectedRole;
            foreach (var item in PermissionItems)
                item.IsEditable = isEditable;
        }

        private async Task LoadRolePermissionsAsync(int roleId, bool isEditable)
        {
            var codes = await _roleRepo.GetPermissionCodesAsync(roleId).ConfigureAwait(true);
            var allCodes = GetAllPermissionCodes();
            PermissionItems.Clear();
            foreach (var code in allCodes)
            {
                PermissionItems.Add(new PermissionItem
                {
                    Code = code,
                    Label = CodeToLabel(code),
                    Section = GetPermissionSection(code),
                    IsChecked = codes.Contains(code),
                    RoleId = roleId,
                    IsEditable = isEditable,
                    ToolTip = GetPermissionToolTip(code)
                });
            }
        }

        private static string GetPermissionToolTip(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            if (code == PermissionCodes.SecurityOverride) return PosLocalization.T("users.permissionSecurityOverride");
            if (code == PermissionCodes.DbRestore) return PosLocalization.T("users.permissionDbRestore");
            if (code == PermissionCodes.PosVoidSale) return PosLocalization.T("users.permissionVoidSale");
            if (code == PermissionCodes.PosRefund) return PosLocalization.T("users.permissionRefund");
            if (code == PermissionCodes.PosDiscountOverLimit) return PosLocalization.T("users.permissionDiscountOverLimit");
            if (code == PermissionCodes.RegisterViewAll) return PosLocalization.T("users.permissionRegisterViewAll");
            return null;
        }

        private static IReadOnlyList<string> GetAllPermissionCodes()
        {
            return new[]
            {
                PermissionCodes.PosSell, PermissionCodes.PosPay, PermissionCodes.PosSuspendCart, PermissionCodes.PosRecoverCart,
                PermissionCodes.PosDiscount, PermissionCodes.PosDiscountOverLimit, PermissionCodes.PosRefund, PermissionCodes.PosVoidSale,
                PermissionCodes.PosReprintReceipt,
                PermissionCodes.CatalogView, PermissionCodes.CatalogEdit, PermissionCodes.CatalogImport, PermissionCodes.CatalogPriceEdit,
                PermissionCodes.RegisterView, PermissionCodes.RegisterViewAll,
                PermissionCodes.DailyCloseView, PermissionCodes.DailyCloseRun, PermissionCodes.DailyClosePrint,
                PermissionCodes.SettingsShop, PermissionCodes.SettingsPrinter,
                PermissionCodes.DbBackup, PermissionCodes.DbRestore, PermissionCodes.DbMaintenance,
                PermissionCodes.UsersManage, PermissionCodes.RolesManage, PermissionCodes.SecurityOverride
            };
        }

        private static string CodeToLabel(string code)
        {
            if (string.IsNullOrEmpty(code)) return code;
            switch (code)
            {
                case PermissionCodes.UsersManage: return PosLocalization.T("users.permissionUsersManage");
                case PermissionCodes.RolesManage: return PosLocalization.T("users.permissionRolesManage");
                case PermissionCodes.SecurityOverride: return PosLocalization.T("users.permissionSecurityOverride");
                case PermissionCodes.RegisterViewAll: return PosLocalization.T("users.permissionRegisterViewAll");
                case PermissionCodes.CatalogPriceEdit: return PosLocalization.T("users.permissionCatalogPriceEdit");
                case PermissionCodes.PosReprintReceipt: return PosLocalization.T("users.permissionReprintReceipt");
                case PermissionCodes.PosVoidSale: return PosLocalization.T("users.permissionVoidSale");
                case PermissionCodes.PosRefund: return PosLocalization.T("users.permissionRefund");
                case PermissionCodes.PosDiscountOverLimit: return PosLocalization.T("users.permissionDiscountOverLimit");
                case PermissionCodes.PosSell: return PosLocalization.T("users.permissionSell");
                case PermissionCodes.PosPay: return PosLocalization.T("users.permissionPayment");
                case PermissionCodes.PosSuspendCart: return PosLocalization.T("users.permissionSuspendCart");
                case PermissionCodes.PosRecoverCart: return PosLocalization.T("users.permissionRecoverCart");
                case PermissionCodes.PosDiscount: return PosLocalization.T("users.permissionDiscount");
                case PermissionCodes.CatalogView: return PosLocalization.T("users.permissionCatalogView");
                case PermissionCodes.CatalogEdit: return PosLocalization.T("users.permissionCatalogEdit");
                case PermissionCodes.CatalogImport: return PosLocalization.T("users.permissionCatalogImport");
                case PermissionCodes.RegisterView: return PosLocalization.T("users.permissionRegisterView");
                case PermissionCodes.DailyCloseView: return PosLocalization.T("users.permissionDailyCloseView");
                case PermissionCodes.DailyCloseRun: return PosLocalization.T("users.permissionDailyCloseRun");
                case PermissionCodes.DailyClosePrint: return PosLocalization.T("users.permissionDailyClosePrint");
                case PermissionCodes.SettingsShop: return PosLocalization.T("users.permissionSettingsShop");
                case PermissionCodes.SettingsPrinter: return PosLocalization.T("users.permissionSettingsPrinter");
                case PermissionCodes.DbBackup: return PosLocalization.T("users.permissionDbBackup");
                case PermissionCodes.DbRestore: return PosLocalization.T("users.permissionDbRestore");
                case PermissionCodes.DbMaintenance: return PosLocalization.T("users.permissionDbMaintenance");
                default:
                    var parts = code.Split('.');
                    return parts.Length >= 2 ? parts[1] : code;
            }
        }

        private static string GetPermissionSection(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            var prefix = code.Split('.')[0];
            switch (prefix)
            {
                case "pos": return "POS";
                case "catalog": return PosLocalization.T("users.sectionCatalog");
                case "register": return PosLocalization.T("users.sectionRegister");
                case "daily_close": return PosLocalization.T("users.sectionDailyClose");
                case "settings": return PosLocalization.T("users.sectionSettings");
                case "db": return "Database";
                case "users":
                case "roles":
                case "security": return PosLocalization.T("users.sectionSecurity");
                default: return PosLocalization.T("users.sectionOther");
            }
        }

        private async Task SaveAsync()
        {
            if (SelectedUser == null) return;
            var wasActive = SelectedUser.IsActive;
            IsBusy = true;
            try
            {
                await _userRepo.UpdateAsync(SelectedUser.Id, DisplayName, SelectedRoleId, IsActive, SelectedUser.MaxDiscountPercent, SelectedUser.RequirePinChange).ConfigureAwait(true);
                await _securityRepo.LogEventAsync(SecurityEventCodes.UserUpdated, "userId=" + SelectedUser.Id).ConfigureAwait(false);
                if (SelectedRoleId != SelectedUser.RoleId)
                    await _securityRepo.LogEventAsync(SecurityEventCodes.RoleChanged, "userId=" + SelectedUser.Id + ", newRoleId=" + SelectedRoleId).ConfigureAwait(false);
                if (wasActive && !IsActive)
                    await _securityRepo.LogEventAsync(SecurityEventCodes.UserDisabled, "userId=" + SelectedUser.Id).ConfigureAwait(false);
                if (!wasActive && IsActive)
                    await _securityRepo.LogEventAsync(SecurityEventCodes.UserEnabled, "userId=" + SelectedUser.Id).ConfigureAwait(false);
                Status = PosLocalization.T("users.saved");
                IsDirty = false;
                await LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Status = PosLocalization.F("common.errorWithMessage", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static void EnsureMinimalPermissionsForRole(UserRole role, IReadOnlyList<string> enabled)
        {
            if (role == null) return;
            var set = new HashSet<string>(enabled ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var code = (role.Code ?? "").Trim().ToLowerInvariant();

            if (code == "cashier")
            {
                Require(set, PermissionCodes.PosSell, PosLocalization.T("users.roleCashierName"));
                Require(set, PermissionCodes.PosPay, PosLocalization.T("users.roleCashierName"));
                Require(set, PermissionCodes.RegisterView, PosLocalization.T("users.roleCashierName"));
            }
            else if (code == "supervisor")
            {
                Require(set, PermissionCodes.RegisterView, PosLocalization.T("users.roleSupervisorName"));
                Require(set, PermissionCodes.PosRefund, PosLocalization.T("users.roleSupervisorName"));
            }
            else if (code == "manager")
            {
                Require(set, PermissionCodes.RegisterViewAll, PosLocalization.T("users.roleManagerName"));
            }
        }

        private static void Require(HashSet<string> set, string permCode, string roleName)
        {
            if (!set.Contains(permCode))
                throw new InvalidOperationException(PosLocalization.F("users.roleMustIncludePermission", roleName, permCode));
        }

        private async Task SaveSelectedRolePermissionsAsync()
        {
            if (!CanManageRoles)
            {
                Status = PosLocalization.T("users.roleManageDenied");
                return;
            }
            if (SelectedRole == null || SelectedRole.IsSystem)
            {
                Status = PosLocalization.T("users.systemRolesReadOnly");
                return;
            }
            IsBusy = true;
            try
            {
                var enabled = PermissionItems.Where(x => x.IsChecked).Select(x => x.Code).ToList();
                EnsureMinimalPermissionsForRole(SelectedRole, enabled);
                await _roleRepo.SetPermissionsAsync(SelectedRole.Id, enabled).ConfigureAwait(true);
                await _securityRepo.LogEventAsync(SecurityEventCodes.RolePermissionsChanged, "roleId=" + SelectedRole.Id + ", roleCode=" + SelectedRole.Code).ConfigureAwait(false);
                Status = PosLocalization.T("users.rolePermissionsSaved");
                await LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Status = PosLocalization.F("common.errorWithMessage", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task NewRoleAsync()
        {
            if (!CanManageRoles)
            {
                Status = PosLocalization.T("users.roleManageDenied");
                return;
            }
            var dlg = new RoleEditDialog(PosLocalization.T("users.roleNewTitle"), "", "")
            {
                Owner = OwnerWindow ?? DialogOwnerHelper.GetSafeOwner(),
                ValidateCode = ValidateUniqueRoleCode
            };
            if (dlg.ShowDialog() != true) return;
            var code = dlg.RoleCode.Trim().ToLowerInvariant();
            var name = dlg.RoleName.Trim();
            IsBusy = true;
            try
            {
                await _roleRepo.CreateAsync(code, name).ConfigureAwait(true);
                await _securityRepo.LogEventAsync(SecurityEventCodes.RoleCreated, "code=" + code + ", name=" + name).ConfigureAwait(false);
                Status = PosLocalization.T("users.roleCreated");
                await LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Status = GetRoleMutationErrorMessage(ex);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task<string> GenerateUniqueRoleCodeAsync(string baseCode)
        {
            var normalizedBaseCode = (baseCode ?? "").Trim().ToLowerInvariant();
            var candidate = normalizedBaseCode + "_copia";
            if (await _roleRepo.GetByCodeAsync(candidate).ConfigureAwait(true) == null)
                return candidate;

            for (int i = 2; i <= 99; i++)
            {
                var numbered = candidate + "_" + i;
                if (await _roleRepo.GetByCodeAsync(numbered).ConfigureAwait(true) == null)
                    return numbered;
            }

            // Safety net rara oltre _99: un suffisso a tick evita di bloccare il flusso.
            return candidate + "_" + DateTime.Now.Ticks;
        }

        private async Task DuplicateRoleAsync()
        {
            if (!CanManageRoles)
            {
                Status = PosLocalization.T("users.roleManageDenied");
                return;
            }
            if (SelectedRole == null) return;
            var originalCode = (SelectedRole.Code ?? "").Trim().ToLowerInvariant();
            var suggestedCode = await GenerateUniqueRoleCodeAsync(originalCode).ConfigureAwait(true);
            var suggestedName = PosLocalization.F("users.copyOf", (SelectedRole.Name ?? "").Trim());
            var dlg = new RoleEditDialog(PosLocalization.T("users.roleDuplicateTitle"), suggestedCode, suggestedName)
            {
                Owner = OwnerWindow ?? DialogOwnerHelper.GetSafeOwner(),
                ValidateCode = ValidateUniqueRoleCode
            };
            if (dlg.ShowDialog() != true) return;
            var code = dlg.RoleCode.Trim().ToLowerInvariant();
            var name = dlg.RoleName.Trim();
            IsBusy = true;
            try
            {
                await _roleRepo.DuplicateAsync(SelectedRole.Id, code, name).ConfigureAwait(true);
                await _securityRepo.LogEventAsync(SecurityEventCodes.RoleDuplicated, "sourceId=" + SelectedRole.Id + ", newCode=" + code).ConfigureAwait(false);
                Status = PosLocalization.T("users.roleDuplicated");
                await LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Status = GetRoleMutationErrorMessage(ex);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RenameRoleAsync()
        {
            if (!CanManageRoles)
            {
                Status = PosLocalization.T("users.roleManageDenied");
                return;
            }
            if (SelectedRole == null || SelectedRole.IsSystem) return;
            var dlg = new RoleEditDialog(PosLocalization.T("users.roleRenameTitle"), SelectedRole.Code, SelectedRole.Name, codeReadOnly: true) { Owner = OwnerWindow ?? DialogOwnerHelper.GetSafeOwner() };
            if (dlg.ShowDialog() != true) return;
            var name = dlg.RoleName.Trim();
            IsBusy = true;
            try
            {
                await _roleRepo.UpdateNameAsync(SelectedRole.Id, name).ConfigureAwait(true);
                await _securityRepo.LogEventAsync(SecurityEventCodes.RoleRenamed, "roleId=" + SelectedRole.Id + ", newName=" + name).ConfigureAwait(false);
                Status = PosLocalization.T("users.roleRenamed");
                await LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Status = PosLocalization.F("common.errorWithMessage", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private string ValidateUniqueRoleCode(string code)
        {
            var normalizedCode = (code ?? "").Trim();
            return Roles.Any(r => string.Equals((r.Code ?? "").Trim(), normalizedCode, StringComparison.OrdinalIgnoreCase))
                ? PosLocalization.F("users.roleCodeInUse", normalizedCode)
                : null;
        }

        private static string GetRoleMutationErrorMessage(Exception ex)
        {
            var message = ex?.Message ?? "";
            if (message.IndexOf("UNIQUE", StringComparison.OrdinalIgnoreCase) >= 0)
                return PosLocalization.T("users.roleCodeUniqueError");
            return PosLocalization.F("common.errorWithMessage", message);
        }

        private async Task DeleteRoleAsync()
        {
            if (!CanManageRoles)
            {
                Status = PosLocalization.T("users.roleManageDenied");
                return;
            }
            if (SelectedRole == null || SelectedRole.IsSystem) return;
            if (!Win7POS.Wpf.Import.ApplyConfirmDialog.ShowConfirm(
                    OwnerWindow ?? DialogOwnerHelper.GetSafeOwner(),
                    PosLocalization.T("users.confirmDeleteRoleTitle"),
                    PosLocalization.F("users.confirmDeleteRoleMessage", SelectedRole.Name)))
                return;
            IsBusy = true;
            try
            {
                await _roleRepo.DeleteAsync(SelectedRole.Id).ConfigureAwait(true);
                await _securityRepo.LogEventAsync(SecurityEventCodes.RoleDeleted, "roleId=" + SelectedRole.Id + ", code=" + SelectedRole.Code).ConfigureAwait(false);
                SelectedRole = null;
                Status = PosLocalization.T("users.roleDeleted");
                await LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Status = PosLocalization.F("common.errorWithMessage", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task NewUserAsync()
        {
            var dlg = new NewUserDialog { Owner = OwnerWindow ?? DialogOwnerHelper.GetSafeOwner() };
            if (dlg.ShowDialog() != true) return;
            IsBusy = true;
            try
            {
                var salt = PinHelper.GenerateSalt();
                var hash = PinHelper.HashPin(dlg.Pin, salt);
                await _userRepo.CreateAsync(dlg.Username, dlg.DisplayName, hash, salt, dlg.RoleId, 0, true).ConfigureAwait(true);
                await _securityRepo.LogEventAsync(SecurityEventCodes.UserCreated, "username=" + dlg.Username).ConfigureAwait(false);
                Status = PosLocalization.T("users.userCreated");
                await LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Status = PosLocalization.F("common.errorWithMessage", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ResetPinAsync()
        {
            if (SelectedUser == null || string.IsNullOrWhiteSpace(NewPin)) return;
            if (NewPin.Length < 4 || NewPin.Length > 6 || !NewPin.All(char.IsDigit)) { Status = PosLocalization.T("pin.invalidDigits"); return; }
            IsBusy = true;
            try
            {
                var salt = PinHelper.GenerateSalt();
                var hash = PinHelper.HashPin(NewPin, salt);
                await _userRepo.UpdatePinAsync(SelectedUser.Id, hash, salt, true).ConfigureAwait(true);
                await _securityRepo.LogEventAsync(SecurityEventCodes.PinReset, "userId=" + SelectedUser.Id).ConfigureAwait(false);
                NewPin = "";
                Status = PosLocalization.T("users.pinUpdated");
            }
            catch (Exception ex)
            {
                Status = PosLocalization.F("common.errorWithMessage", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public sealed class PermissionItem : INotifyPropertyChanged
        {
            public string Code { get; set; }
            public string Label { get; set; }
            public string Section { get; set; }
            public int RoleId { get; set; }
            public string ToolTip { get; set; }
            private bool _isChecked;
            public bool IsChecked { get => _isChecked; set { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); } }
            private bool _isEditable;
            public bool IsEditable { get => _isEditable; set { _isEditable = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditable))); } }
            public event PropertyChangedEventHandler PropertyChanged;
        }
    }
}
