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
        private bool _isDirty;

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
            SaveRolePermissionsCommand = new RelayCommand(() => _ = SaveSelectedRolePermissionsAsync(), () => !IsBusy && CanEditSelectedRole);
            NewRoleCommand = new RelayCommand(() => _ = NewRoleAsync(), () => !IsBusy);
            DuplicateRoleCommand = new RelayCommand(() => _ = DuplicateRoleAsync(), () => !IsBusy && SelectedRole != null);
            RenameRoleCommand = new RelayCommand(() => _ = RenameRoleAsync(), () => !IsBusy && CanEditSelectedRole);
            DeleteRoleCommand = new RelayCommand(() => _ = DeleteRoleAsync(), () => !IsBusy && CanEditSelectedRole);
            ClearFilterCommand = new RelayCommand(ClearFilter, () => !string.IsNullOrWhiteSpace(Filter));
            ReloadCommand = new RelayCommand(() => _ = ReloadWithConfirmAsync(), () => !IsBusy);
            CancelCommand = CancelUserChangesCommand;
            _ = LoadAsync();
        }

        private void ClearFilter() { Filter = ""; }

        private void SetDirty() { IsDirty = true; }

        private async Task ReloadWithConfirmAsync()
        {
            if (IsDirty && !Win7POS.Wpf.Import.ApplyConfirmDialog.ShowConfirm(System.Windows.Application.Current?.MainWindow, "Utenti e ruoli", "Modifiche non salvate. Ricaricare comunque?"))
                return;
            await LoadAsync().ConfigureAwait(true);
        }

        private static string GetRoleOperativeDescription(UserRole role)
        {
            if (role == null) return "";
            var code = (role.Code ?? "").Trim().ToLowerInvariant();
            switch (code)
            {
                case "admin": return "Accesso completo.";
                case "manager": return "Vendite, report, configurazioni limitate.";
                case "supervisor": return "Vendite e resi, chiusura cassa.";
                case "cashier": return "POS e stampa, niente impostazioni.";
                default: return role.IsSystem ? "Ruolo di sistema." : "Ruolo personalizzato, modificabile.";
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

        public bool IsFilterActive => !string.IsNullOrWhiteSpace(Filter?.Trim());
        public bool IsUsersListEmpty => FilteredUsers.Count == 0;
        public string EmptyUsersMessage => IsFilterActive ? "Nessun utente trovato." : "Nessun utente.";
        public string SelectedUserRoleDisplay => SelectedUser?.RoleName ?? "";
        public string SelectedRoleSystemInfo => SelectedRole == null ? "" : (SelectedRole.IsSystem ? "Ruolo di sistema: alcune azioni non sono disponibili." : "Ruolo personalizzato: modificabile.");
        public string DetailUserTitle => "Dettaglio utente selezionato";
        public string EditingUserDisplay => SelectedUser != null ? "Stai modificando: " + SelectedUser.Username : "";
        public string PermissionsRoleTitle => SelectedRole != null ? "Permessi del ruolo: " + SelectedRole.Name : "Permessi ruolo";
        /// <summary>Messaggio quando il ruolo selezionato è di sistema (permessi non modificabili).</summary>
        public bool IsSelectedRoleSystem => SelectedRole != null && SelectedRole.IsSystem;
        public string PermessiRoleSystemMessage => SelectedRole != null && SelectedRole.IsSystem ? "Ruolo predefinito di sistema: i permessi non sono modificabili." : "";

        /// <summary>Visibility per il messaggio permessi ruolo di sistema (Visible se ruolo sistema).</summary>
        public Visibility PermessiRoleSystemMessageVisibility => string.IsNullOrEmpty(PermessiRoleSystemMessage) ? Visibility.Collapsed : Visibility.Visible;
        public string AccountStatusText => IsActive ? "Può accedere al sistema" : "Stato: accesso bloccato";

        /// <summary>Modifiche utente non salvate (nome, ruolo, stato, PIN).</summary>
        public bool IsDirty { get => _isDirty; private set { _isDirty = value; OnPropertyChanged(); } }

        /// <summary>Ruolo selezionato: nome e tipo (sistema/personalizzato).</summary>
        public string SelectedRoleDescription => SelectedRole == null ? "" : SelectedRole.Name + " — " + (SelectedRole.IsSystem ? "ruolo di sistema" : "personalizzato");

        /// <summary>Descrizione operativa del ruolo (es. "accesso completo", "POS e stampa").</summary>
        public string SelectedRoleOperativeDescription => GetRoleOperativeDescription(SelectedRole);

        /// <summary>Titolo pagina (Utenti e ruoli).</summary>
        public string PageTitle => "Utenti e ruoli";

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
                _ = LoadSelectedRolePermissionsAsync();
            }
        }

        public bool IsRoleSelected => SelectedRole != null;
        public bool CanEditSelectedRole => SelectedRole != null && !SelectedRole.IsSystem;
        public bool PermissionEditMode => _permissionEditMode;
        public bool HasSelection => IsUserSelected || IsRoleSelected;
        public string PermissionsSectionTitle => _permissionEditMode ? "Permessi ruolo (modificabili)" : "Permessi (dal ruolo, sola lettura)";
        public string SaveRolePermissionsToolTip => CanEditSelectedRole ? "Salva le modifiche ai permessi del ruolo selezionato." : (SelectedRole != null && SelectedRole.IsSystem ? "I ruoli di sistema non sono modificabili." : "Seleziona un ruolo per modificare i permessi.");
        public string RenameRoleToolTip => CanEditSelectedRole ? "Rinomina il ruolo selezionato." : "Seleziona un ruolo custom (non di sistema) per rinominarlo.";
        public string DeleteRoleToolTip => CanEditSelectedRole ? "Elimina il ruolo selezionato (nessun utente deve averlo assegnato)." : "Seleziona un ruolo custom non assegnato per eliminarlo.";

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
                Status = "Caricato.";
                IsDirty = false;
            }
            catch (Exception ex)
            {
                Status = "Errore: " + ex.Message;
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
                OnPropertyChanged(nameof(HasSelection));
                return;
            }
            OnPropertyChanged(nameof(HasSelection));
            DisplayName = SelectedUser.DisplayName;
            SelectedRoleId = SelectedUser.RoleId;
            IsActive = SelectedUser.IsActive;
            NewPin = "";
            IsDirty = false;
            await LoadRolePermissionsAsync(SelectedUser.RoleId, isEditable: false).ConfigureAwait(true);
            _permissionEditMode = false;
            OnPropertyChanged(nameof(PermissionEditMode));
            OnPropertyChanged(nameof(PermissionsSectionTitle));
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
            Status = "Modifiche annullate.";
        }

        private async Task LoadSelectedRolePermissionsAsync()
        {
            if (SelectedRole == null)
                return;
            await LoadRolePermissionsAsync(SelectedRole.Id, CanEditSelectedRole).ConfigureAwait(true);
            _permissionEditMode = true;
            OnPropertyChanged(nameof(PermissionEditMode));
            OnPropertyChanged(nameof(PermissionsSectionTitle));
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
            if (code == PermissionCodes.SecurityOverride) return "Autorizza operazioni riservate ad altri ruoli (override).";
            if (code == PermissionCodes.DbRestore) return "Operazione sensibile: ripristino database.";
            if (code == PermissionCodes.PosVoidSale) return "Storno vendita: richiede supervisore o override.";
            if (code == PermissionCodes.PosRefund) return "Resi: richiede permesso o override.";
            if (code == PermissionCodes.PosDiscountOverLimit) return "Sconti oltre soglia: richiede supervisore.";
            if (code == PermissionCodes.RegisterViewAll) return "Visualizza vendite di tutti gli operatori.";
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
                case PermissionCodes.UsersManage: return "Gestione utenti";
                case PermissionCodes.RolesManage: return "Gestione ruoli";
                case PermissionCodes.SecurityOverride: return "Autorizzazioni straordinarie";
                case PermissionCodes.RegisterViewAll: return "Vedi vendite di tutti";
                case PermissionCodes.CatalogPriceEdit: return "Modifica prezzi";
                case PermissionCodes.PosReprintReceipt: return "Ristampa scontrino";
                case PermissionCodes.PosVoidSale: return "Storno vendita";
                case PermissionCodes.PosRefund: return "Reso";
                case PermissionCodes.PosDiscountOverLimit: return "Sconto oltre limite";
                case PermissionCodes.PosSell: return "Vendita";
                case PermissionCodes.PosPay: return "Pagamento";
                case PermissionCodes.PosSuspendCart: return "Sospendi carrello";
                case PermissionCodes.PosRecoverCart: return "Recupera carrello";
                case PermissionCodes.PosDiscount: return "Sconto";
                case PermissionCodes.CatalogView: return "Visualizza catalogo";
                case PermissionCodes.CatalogEdit: return "Modifica catalogo";
                case PermissionCodes.CatalogImport: return "Importa catalogo";
                case PermissionCodes.RegisterView: return "Registro vendite";
                case PermissionCodes.DailyCloseView: return "Visualizza chiusura cassa";
                case PermissionCodes.DailyCloseRun: return "Esegui chiusura cassa";
                case PermissionCodes.DailyClosePrint: return "Stampa chiusura cassa";
                case PermissionCodes.SettingsShop: return "Impostazioni negozio";
                case PermissionCodes.SettingsPrinter: return "Impostazioni stampante";
                case PermissionCodes.DbBackup: return "Backup database";
                case PermissionCodes.DbRestore: return "Ripristino database";
                case PermissionCodes.DbMaintenance: return "Manutenzione database";
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
                case "catalog": return "Catalogo";
                case "register": return "Registro vendite";
                case "daily_close": return "Chiusura cassa";
                case "settings": return "Impostazioni";
                case "db": return "Database";
                case "users":
                case "roles":
                case "security": return "Sicurezza / Utenti";
                default: return "Altro";
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
                Status = "Salvato.";
                IsDirty = false;
                await LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Status = "Errore: " + ex.Message;
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
                Require(set, PermissionCodes.PosSell, "Cassiere");
                Require(set, PermissionCodes.PosPay, "Cassiere");
                Require(set, PermissionCodes.RegisterView, "Cassiere");
            }
            else if (code == "supervisor")
            {
                Require(set, PermissionCodes.RegisterView, "Supervisore");
                Require(set, PermissionCodes.PosRefund, "Supervisore");
            }
            else if (code == "manager")
            {
                Require(set, PermissionCodes.RegisterViewAll, "Manager");
            }
        }

        private static void Require(HashSet<string> set, string permCode, string roleName)
        {
            if (!set.Contains(permCode))
                throw new InvalidOperationException("Il ruolo " + roleName + " deve includere il permesso " + permCode + ".");
        }

        private async Task SaveSelectedRolePermissionsAsync()
        {
            if (SelectedRole == null || SelectedRole.IsSystem)
            {
                Status = "I ruoli di sistema non sono modificabili.";
                return;
            }
            IsBusy = true;
            try
            {
                var enabled = PermissionItems.Where(x => x.IsChecked).Select(x => x.Code).ToList();
                EnsureMinimalPermissionsForRole(SelectedRole, enabled);
                await _roleRepo.SetPermissionsAsync(SelectedRole.Id, enabled).ConfigureAwait(true);
                await _securityRepo.LogEventAsync(SecurityEventCodes.RolePermissionsChanged, "roleId=" + SelectedRole.Id + ", roleCode=" + SelectedRole.Code).ConfigureAwait(false);
                Status = "Permessi ruolo salvati.";
                await LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Status = "Errore: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task NewRoleAsync()
        {
            var dlg = new RoleEditDialog("Nuovo ruolo", "", "") { Owner = System.Windows.Application.Current?.MainWindow };
            if (dlg.ShowDialog() != true) return;
            var code = dlg.RoleCode.Trim().ToLowerInvariant();
            var name = dlg.RoleName.Trim();
            IsBusy = true;
            try
            {
                await _roleRepo.CreateAsync(code, name).ConfigureAwait(true);
                await _securityRepo.LogEventAsync(SecurityEventCodes.RoleCreated, "code=" + code + ", name=" + name).ConfigureAwait(false);
                Status = "Ruolo creato. Assegna i permessi e salva.";
                await LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Status = "Errore: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task DuplicateRoleAsync()
        {
            if (SelectedRole == null) return;
            var suggestedCode = "copy_" + (SelectedRole.Code ?? "").Trim().ToLowerInvariant();
            var suggestedName = "Copia di " + (SelectedRole.Name ?? "").Trim();
            var dlg = new RoleEditDialog("Duplica ruolo", suggestedCode, suggestedName) { Owner = System.Windows.Application.Current?.MainWindow };
            if (dlg.ShowDialog() != true) return;
            var code = dlg.RoleCode.Trim().ToLowerInvariant();
            var name = dlg.RoleName.Trim();
            IsBusy = true;
            try
            {
                await _roleRepo.DuplicateAsync(SelectedRole.Id, code, name).ConfigureAwait(true);
                await _securityRepo.LogEventAsync(SecurityEventCodes.RoleDuplicated, "sourceId=" + SelectedRole.Id + ", newCode=" + code).ConfigureAwait(false);
                Status = "Ruolo duplicato. Verifica i permessi e salva se necessario.";
                await LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Status = "Errore: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RenameRoleAsync()
        {
            if (SelectedRole == null || SelectedRole.IsSystem) return;
            var dlg = new RoleEditDialog("Rinomina ruolo", SelectedRole.Code, SelectedRole.Name, codeReadOnly: true) { Owner = System.Windows.Application.Current?.MainWindow };
            if (dlg.ShowDialog() != true) return;
            var name = dlg.RoleName.Trim();
            IsBusy = true;
            try
            {
                await _roleRepo.UpdateNameAsync(SelectedRole.Id, name).ConfigureAwait(true);
                await _securityRepo.LogEventAsync(SecurityEventCodes.RoleRenamed, "roleId=" + SelectedRole.Id + ", newName=" + name).ConfigureAwait(false);
                Status = "Ruolo rinominato.";
                await LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Status = "Errore: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task DeleteRoleAsync()
        {
            if (SelectedRole == null || SelectedRole.IsSystem) return;
            if (!Win7POS.Wpf.Import.ApplyConfirmDialog.ShowConfirm(System.Windows.Application.Current?.MainWindow, "Conferma eliminazione", "Eliminare il ruolo \"" + SelectedRole.Name + "\"? Gli utenti non devono averlo assegnato."))
                return;
            IsBusy = true;
            try
            {
                await _roleRepo.DeleteAsync(SelectedRole.Id).ConfigureAwait(true);
                await _securityRepo.LogEventAsync(SecurityEventCodes.RoleDeleted, "roleId=" + SelectedRole.Id + ", code=" + SelectedRole.Code).ConfigureAwait(false);
                SelectedRole = null;
                Status = "Ruolo eliminato.";
                await LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Status = "Errore: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task NewUserAsync()
        {
            var dlg = new NewUserDialog { Owner = System.Windows.Application.Current?.MainWindow };
            if (dlg.ShowDialog() != true) return;
            IsBusy = true;
            try
            {
                var salt = PinHelper.GenerateSalt();
                var hash = PinHelper.HashPin(dlg.Pin, salt);
                await _userRepo.CreateAsync(dlg.Username, dlg.DisplayName, hash, salt, dlg.RoleId, 0, true).ConfigureAwait(true);
                await _securityRepo.LogEventAsync(SecurityEventCodes.UserCreated, "username=" + dlg.Username).ConfigureAwait(false);
                Status = "Utente creato.";
                await LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Status = "Errore: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ResetPinAsync()
        {
            if (SelectedUser == null || string.IsNullOrWhiteSpace(NewPin)) return;
            if (NewPin.Length < 4 || NewPin.Length > 6) { Status = "PIN 4-6 cifre."; return; }
            IsBusy = true;
            try
            {
                var salt = PinHelper.GenerateSalt();
                var hash = PinHelper.HashPin(NewPin, salt);
                await _userRepo.UpdatePinAsync(SelectedUser.Id, hash, salt, true).ConfigureAwait(true);
                await _securityRepo.LogEventAsync(SecurityEventCodes.PinReset, "userId=" + SelectedUser.Id).ConfigureAwait(false);
                NewPin = "";
                Status = "PIN aggiornato. Cambio obbligatorio al prossimo accesso.";
            }
            catch (Exception ex)
            {
                Status = "Errore: " + ex.Message;
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
