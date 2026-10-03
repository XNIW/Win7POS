using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Win7POS.Core;
using Win7POS.Core.Backup;
using Win7POS.Core.Operations;
using Win7POS.Data;
using Win7POS.Data.Operations;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Pos.Online;
using System.Linq;
using System.Text;
using Win7POS.Data.Backup;
using Win7POS.Core.Security;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class DbMaintenanceViewModel : INotifyPropertyChanged
    {
        private readonly Pos.PosWorkflowService _service;
        private readonly Func<Task<bool>> _demandRestorePermission;
        private readonly Func<bool> _hasBackupPermission;
        private readonly Func<bool> _hasCatalogImportPermission;
        private readonly Func<bool> _hasMaintenancePermission;
        private readonly FileLogger _logger = new FileLogger("DbMaintenanceViewModel");

        private string _outputLog = string.Empty;
        private bool _isBusy;
        private BackupScheduleMode _backupSchedule;
        private string _backupTime = "02:00";
        private DayOfWeek _backupWeeklyDay = DayOfWeek.Sunday;
        private bool _backupCatchUp = true;
        private string _backupRetentionCount = "14";
        private string _backupRetentionDays = "30";
        private string _backupDestinationKind = "local";
        private string _backupDestinationPath = string.Empty;
        private string _lastBackupResult = string.Empty;
        private bool _backupSettingsLoaded;
        private SettingsOperationsService _pendingProfileService;
        private SettingsProfilePreview _pendingProfile;
        private string _profilePreview = string.Empty;
        private SettingsDefaultsScope _defaultsScope = SettingsDefaultsScope.Hardware;
        private string _effectiveBackupsDirectory = AppPaths.BackupsDirectory;
        private static readonly string[] BackupResultCodes = { "backup_verified", "backup_verified_retention_warning",
            "access_denied", "network_unavailable", "cancelled", "backup_failed", "configuration_invalid",
            "published_identity_invalid", "backup_finalize_pending", "live_database_missing", "busy" };

        public DbMaintenanceViewModel(
            Pos.PosWorkflowService service,
            Func<Task<bool>> demandRestorePermission,
            Func<bool> hasBackupPermission,
            Func<bool> hasCatalogImportPermission,
            Func<bool> hasMaintenancePermission)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _demandRestorePermission = demandRestorePermission ?? (() => Task.FromResult(false));
            _hasBackupPermission = hasBackupPermission ?? (() => false);
            _hasCatalogImportPermission = hasCatalogImportPermission ?? (() => false);
            _hasMaintenancePermission = hasMaintenancePermission ?? (() => false);
            BackupNowCommand = new AsyncRelayCommand(BackupNowAsync, _ => !IsBusy);
            RestoreBackupCommand = new AsyncRelayCommand(RestoreBackupAsync, _ => !IsBusy);
            IntegrityCheckCommand = new AsyncRelayCommand(IntegrityCheckAsync, _ => !IsBusy);
            CompleteRestoreReviewCommand = new AsyncRelayCommand(CompleteRestoreReviewAsync, _ => !IsBusy);
            VacuumCommand = new AsyncRelayCommand(VacuumAsync, _ => !IsBusy);
            SupplierExcelImportCommand = new RelayCommand(_ => OpenSupplierExcelImport(), _ => !IsBusy);
            OpenFolderCommand = new RelayCommand(_ => OpenFolder(), _ => !IsBusy);
            SaveBackupSettingsCommand = new AsyncRelayCommand(SaveBackupSettingsAsync,
                _ => !IsBusy && BackupSettingsLoaded && _hasBackupPermission());
            ExportProfileCommand = new AsyncRelayCommand(ExportProfileAsync, _ => !IsBusy && _hasBackupPermission());
            PreviewProfileCommand = new AsyncRelayCommand(PreviewProfileAsync, _ => !IsBusy && _hasMaintenancePermission());
            ApplyProfileCommand = new AsyncRelayCommand(ApplyProfileAsync, _ => !IsBusy && _pendingProfile != null && _hasMaintenancePermission());
            RestoreDefaultsCommand = new AsyncRelayCommand(RestoreDefaultsAsync, _ => !IsBusy && _hasMaintenancePermission());
            ViewSettingsAuditCommand = new AsyncRelayCommand(ViewSettingsAuditAsync, _ => !IsBusy && _hasMaintenancePermission());
        }

        public string DbPath => _service.DbPath;
        public string BackupsDirectory => _effectiveBackupsDirectory;
        public string ExportsDirectory => AppPaths.ExportsDirectory;

        public IReadOnlyList<BackupChoice> BackupSchedules { get; } = new[]
        {
            new BackupChoice(BackupScheduleMode.Disabled, "backupAutomation.disabled"),
            new BackupChoice(BackupScheduleMode.Daily, "backupAutomation.daily"),
            new BackupChoice(BackupScheduleMode.Weekly, "backupAutomation.weekly")
        };
        public IReadOnlyList<BackupChoice> BackupDestinations { get; } = new[]
        {
            new BackupChoice("local", "backupAutomation.local"),
            new BackupChoice("custom_local", "backupAutomation.customLocal"),
            new BackupChoice("network_share", "backupAutomation.networkShare")
        };
        public IReadOnlyList<BackupChoice> BackupWeekdays { get; } = new[]
        {
            new BackupChoice(DayOfWeek.Monday, "backupAutomation.Monday"),
            new BackupChoice(DayOfWeek.Tuesday, "backupAutomation.Tuesday"),
            new BackupChoice(DayOfWeek.Wednesday, "backupAutomation.Wednesday"),
            new BackupChoice(DayOfWeek.Thursday, "backupAutomation.Thursday"),
            new BackupChoice(DayOfWeek.Friday, "backupAutomation.Friday"),
            new BackupChoice(DayOfWeek.Saturday, "backupAutomation.Saturday"),
            new BackupChoice(DayOfWeek.Sunday, "backupAutomation.Sunday")
        };
        public BackupScheduleMode BackupSchedule { get => _backupSchedule; set { _backupSchedule = value; OnPropertyChanged(); } }
        public string BackupTime { get => _backupTime; set { _backupTime = value; OnPropertyChanged(); } }
        public DayOfWeek BackupWeeklyDay { get => _backupWeeklyDay; set { _backupWeeklyDay = value; OnPropertyChanged(); } }
        public bool BackupCatchUp { get => _backupCatchUp; set { _backupCatchUp = value; OnPropertyChanged(); } }
        public string BackupRetentionCount { get => _backupRetentionCount; set { _backupRetentionCount = value; OnPropertyChanged(); } }
        public string BackupRetentionDays { get => _backupRetentionDays; set { _backupRetentionDays = value; OnPropertyChanged(); } }
        public string BackupDestinationKind { get => _backupDestinationKind; set { _backupDestinationKind = value; OnPropertyChanged(); } }
        public string BackupDestinationPath { get => _backupDestinationPath; set { _backupDestinationPath = value; OnPropertyChanged(); } }
        public string LastBackupResult { get => _lastBackupResult; private set { _lastBackupResult = value; OnPropertyChanged(); } }
        public bool BackupSettingsLoaded { get => _backupSettingsLoaded; private set { _backupSettingsLoaded = value; OnPropertyChanged(); RaiseCanExecuteChanged(); } }

        public sealed class BackupChoice
        {
            private readonly string _key;
            public BackupChoice(object value, string key) { Value = value; _key = key; }
            public object Value { get; }
            public string Label => PosLocalization.T(_key);
        }

        public string OutputLog
        {
            get => _outputLog;
            set
            {
                _outputLog = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasOutputLog));
            }
        }

        public bool HasOutputLog => !string.IsNullOrWhiteSpace(OutputLog);

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public ICommand BackupNowCommand { get; }
        public ICommand RestoreBackupCommand { get; }
        public ICommand IntegrityCheckCommand { get; }
        public ICommand CompleteRestoreReviewCommand { get; }
        public ICommand VacuumCommand { get; }
        public ICommand SupplierExcelImportCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand SaveBackupSettingsCommand { get; }
        public ICommand ExportProfileCommand { get; }
        public ICommand PreviewProfileCommand { get; }
        public ICommand ApplyProfileCommand { get; }
        public ICommand RestoreDefaultsCommand { get; }
        public ICommand ViewSettingsAuditCommand { get; }
        public string ProfilePreview { get => _profilePreview; private set { _profilePreview = value; OnPropertyChanged(); } }
        public SettingsDefaultsScope DefaultsScope { get => _defaultsScope; set { _defaultsScope = value; OnPropertyChanged(); } }
        public IReadOnlyList<BackupChoice> DefaultsScopes { get; } = new[]
        {
            new BackupChoice(SettingsDefaultsScope.Hardware, "settingsProfile.scope.hardware"),
            new BackupChoice(SettingsDefaultsScope.Backup, "settingsProfile.scope.backup"),
            new BackupChoice(SettingsDefaultsScope.CustomerDisplay, "settingsProfile.scope.display"),
            new BackupChoice(SettingsDefaultsScope.Language, "settingsProfile.scope.language"),
            new BackupChoice(SettingsDefaultsScope.AllPortable, "settingsProfile.scope.all")
        };

        private static string CurrentActor => (OperatorSessionHolder.Current?.CurrentUser?.Id ?? 0).ToString(CultureInfo.InvariantCulture);
        private SettingsOperationsService CreateProfileService()
        {
            var actor = CurrentActor;
            return new SettingsOperationsService(new SqliteConnectionFactory(PosDbOptions.ForPath(DbPath)), AppPaths.BackupsDirectory,
                permission => actor == CurrentActor &&
                    (permission == PermissionCodes.DbBackup ? _hasBackupPermission() : permission == PermissionCodes.DbMaintenance && _hasMaintenancePermission()),
                actor, _service.BackupAutomation);
        }
        private async Task ExportProfileAsync()
        {
            if (IsBusy || !_hasBackupPermission()) return;
            var dialog = new SaveFileDialog { Title = PosLocalization.T("settingsProfile.export"), Filter = PosLocalization.T("settingsProfile.filter"),
                FileName = "settings.win7pos-settings.json", AddExtension = true, DefaultExt = ".win7pos-settings.json" };
            if (dialog.ShowDialog(OwnerWindow ?? DialogOwnerHelper.GetSafeOwner()) != true) return;
            IsBusy = true;
            try
            {
                var service = CreateProfileService();
                var bytes = await Task.Run(() => service.ExportAsync(PosApplicationVersion.GetCurrent())).ConfigureAwait(true);
                // The user chose this file; overwrite is confirmed by the standard SaveFileDialog.
                await Task.Run(() => File.WriteAllBytes(dialog.FileName, bytes)).ConfigureAwait(true);
                Append(PosLocalization.T("settingsProfile.exported"));
            }
            catch { Append(PosLocalization.T("settingsProfile.failed")); }
            finally { IsBusy = false; }
        }
        private async Task PreviewProfileAsync()
        {
            if (IsBusy || !_hasMaintenancePermission()) return;
            var dialog = new OpenFileDialog { Title = PosLocalization.T("settingsProfile.preview"), Filter = PosLocalization.T("settingsProfile.filter"), CheckFileExists = true, Multiselect = false };
            if (dialog.ShowDialog(OwnerWindow ?? DialogOwnerHelper.GetSafeOwner()) != true) return;
            IsBusy = true;
            _pendingProfile = null;
            _pendingProfileService = null;
            ProfilePreview = string.Empty;
            try
            {
                var service = CreateProfileService();
                var preview = await Task.Run(async () =>
                {
                    using (var stream = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (stream.Length < 1 || stream.Length > PortableSettingsPolicy.MaximumFileBytes) throw new ArgumentException("Profile size is invalid.");
                        var bytes = new byte[(int)stream.Length];
                        var read = 0;
                        while (read < bytes.Length)
                        {
                            var count = stream.Read(bytes, read, bytes.Length - read);
                            if (count == 0) throw new IOException("Incomplete profile.");
                            read += count;
                        }
                        if (stream.ReadByte() != -1) throw new ArgumentException("Profile size changed.");
                        return await service.PreviewImportAsync(bytes).ConfigureAwait(false);
                    }
                }).ConfigureAwait(true);
                _pendingProfileService = service;
                _pendingProfile = preview;
                ProfilePreview = PosLocalization.F("settingsProfile.diffCount", preview.Differences.Count) + Environment.NewLine +
                    string.Join(Environment.NewLine, preview.Differences.Select(x => x.Key + ": " + x.Before + " → " + x.After)) + Environment.NewLine +
                    string.Join(Environment.NewLine, preview.SafetyAdjustments.Select(x => PosLocalization.T("settingsProfile.safety." + x)));
            }
            catch { Append(PosLocalization.T("settingsProfile.invalid")); }
            finally { IsBusy = false; }
        }
        private async Task ApplyProfileAsync()
        {
            if (IsBusy || _pendingProfile == null || !_hasMaintenancePermission()) return;
            if (!ApplyConfirmDialog.ShowConfirm(OwnerWindow ?? DialogOwnerHelper.GetSafeOwner(), PosLocalization.T("settingsProfile.apply"), PosLocalization.T("settingsProfile.confirmImport"))) return;
            IsBusy = true;
            try
            {
                var service = _pendingProfileService;
                var preview = _pendingProfile;
                await Task.Run(() => service.ImportAsync(preview, true)).ConfigureAwait(true);
                _pendingProfile = null;
                _pendingProfileService = null;
                ProfilePreview = string.Empty;
                await RefreshImportedPreferencesAsync().ConfigureAwait(true);
                Append(PosLocalization.T("settingsProfile.applied"));
            }
            catch { Append(PosLocalization.T("settingsProfile.applyFailed")); }
            finally { IsBusy = false; }
        }
        private async Task RestoreDefaultsAsync()
        {
            if (IsBusy || !_hasMaintenancePermission()) return;
            if (!ApplyConfirmDialog.ShowConfirm(OwnerWindow ?? DialogOwnerHelper.GetSafeOwner(), PosLocalization.T("settingsProfile.reset"), PosLocalization.T("settingsProfile.confirmReset"))) return;
            IsBusy = true;
            try
            {
                var service = CreateProfileService();
                var scope = DefaultsScope;
                await Task.Run(() => service.RestoreDefaultsAsync(scope, true)).ConfigureAwait(true);
                _pendingProfile = null;
                _pendingProfileService = null;
                ProfilePreview = string.Empty;
                await RefreshImportedPreferencesAsync().ConfigureAwait(true);
                Append(PosLocalization.T("settingsProfile.resetDone"));
            }
            catch { Append(PosLocalization.T("settingsProfile.failed")); }
            finally { IsBusy = false; }
        }
        private async Task RefreshImportedPreferencesAsync()
        {
            await PosLocalization.Current.LoadAsync(new SettingsRepository(new SqliteConnectionFactory(PosDbOptions.ForPath(DbPath)))).ConfigureAwait(true);
            await InitializeBackupSettingsAsync().ConfigureAwait(true);
        }
        private async Task ViewSettingsAuditAsync()
        {
            if (IsBusy || !_hasMaintenancePermission()) return;
            IsBusy = true;
            try
            {
                var service = CreateProfileService();
                var rows = await Task.Run(() => service.GetAuditAsync()).ConfigureAwait(true);
                Append(PosLocalization.T("settingsProfile.audit") + Environment.NewLine + string.Join(Environment.NewLine,
                    rows.Select(x => x.CreatedUtc + " | " + x.Event + " | " + x.Actor + " | " + x.Source + " | " + x.Result + " | " + x.KeyCount.ToString(CultureInfo.InvariantCulture) + " | " + x.KeyNames)));
            }
            catch { Append(PosLocalization.T("settingsProfile.failed")); }
            finally { IsBusy = false; }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        internal Window OwnerWindow { get; set; }

        internal async Task InitializeBackupSettingsAsync()
        {
            if (!_hasBackupPermission()) return;
            try
            {
                var options = await Task.Run(() => _service.BackupAutomation.GetOptionsAsync()).ConfigureAwait(true);
                BackupSchedule = options.Schedule.Mode;
                BackupTime = options.Schedule.LocalTime;
                BackupWeeklyDay = options.Schedule.WeeklyDay;
                BackupCatchUp = options.Schedule.CatchUpOnStartup;
                BackupRetentionCount = options.RetentionMaxCount.ToString(CultureInfo.InvariantCulture);
                BackupRetentionDays = options.RetentionMaxAgeDays.ToString(CultureInfo.InvariantCulture);
                BackupDestinationKind = options.DestinationKind;
                BackupDestinationPath = options.DestinationPath;
                UpdateBackupDirectory(options);
                BackupSettingsLoaded = true;
                await RefreshBackupResultAsync().ConfigureAwait(true);
            }
            catch
            {
                BackupSettingsLoaded = false;
                Append(PosLocalization.T("backupAutomation.loadFailed"));
                _logger.LogError(null, "Backup policy load failed");
            }
        }

        private async Task SaveBackupSettingsAsync()
        {
            if (!_hasBackupPermission() || !BackupSettingsLoaded) return;
            if (!int.TryParse(BackupRetentionCount, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                !int.TryParse(BackupRetentionDays, NumberStyles.None, CultureInfo.InvariantCulture, out var days))
            {
                Append(PosLocalization.T("backupAutomation.invalidPolicy"));
                return;
            }
            IsBusy = true;
            try
            {
                var options = new BackupAutomationOptions
                {
                    Schedule = new BackupScheduleOptions
                    {
                        Mode = BackupSchedule, LocalTime = BackupTime,
                        WeeklyDay = BackupWeeklyDay, CatchUpOnStartup = BackupCatchUp
                    },
                    RetentionMaxCount = count, RetentionMaxAgeDays = days,
                    DestinationKind = BackupDestinationKind, DestinationPath = BackupDestinationPath
                };
                var actor = (OperatorSessionHolder.Current?.CurrentUser?.Id ?? 0).ToString(CultureInfo.InvariantCulture);
                // Permission is checked again immediately before the settings transaction.
                if (!_hasBackupPermission()) return;
                await Task.Run(() => _service.BackupAutomation.SaveOptionsAsync(options, actor, () =>
                {
                    if (!_hasBackupPermission() || actor != CurrentActor) throw new UnauthorizedAccessException();
                })).ConfigureAwait(true);
                UpdateBackupDirectory(options);
                Append(PosLocalization.T("backupAutomation.saved"));
                await RefreshBackupResultAsync().ConfigureAwait(true);
            }
            catch (ArgumentException)
            {
                Append(PosLocalization.T("backupAutomation.invalidPolicy"));
            }
            catch
            {
                Append(PosLocalization.T("backupAutomation.saveFailed"));
                _logger.LogError(null, "Backup policy save failed");
            }
            finally { IsBusy = false; }
        }

        private void UpdateBackupDirectory(BackupAutomationOptions options)
        {
            _effectiveBackupsDirectory = options.DestinationKind == "local"
                ? AppPaths.BackupsDirectory : options.DestinationPath.Trim();
            OnPropertyChanged(nameof(BackupsDirectory));
        }

        internal async Task RefreshBackupResultAsync()
        {
            if (!_hasBackupPermission()) return;
            try
            {
                var result = await Task.Run(() => _service.BackupAutomation.GetLastResultAsync()).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(result))
                {
                    LastBackupResult = PosLocalization.T("backupAutomation.noResult");
                    return;
                }
                var parts = result.Split('|');
                var key = Array.IndexOf(BackupResultCodes, parts[0]) >= 0 ? parts[0] : "backup_failed";
                LastBackupResult = PosLocalization.T("backupAutomation.result." + key) +
                    (parts.Length > 1 ? Environment.NewLine + parts[1] : string.Empty) +
                    (parts.Length > 2 && parts[2].Length > 0 ? Environment.NewLine + Path.GetFileName(parts[2]) : string.Empty);
            }
            catch { LastBackupResult = PosLocalization.T("backupAutomation.loadFailed"); }
        }

        private async Task BackupNowAsync()
        {
            if (!_hasBackupPermission())
            {
                Append(PosLocalization.F(
                    "common.permissionDeniedOperation",
                    PosLocalization.T("operations.dbBackup")));
                return;
            }

            IsBusy = true;
            try
            {
                var path = await _service.BackupDbAsync().ConfigureAwait(true);
                Append(PosLocalization.F("dbMaintenance.backupCreated", path));
            }
            catch (Exception ex)
            {
                var detail = (string)null;
                foreach (var code in BackupResultCodes)
                {
                    if (ex is InvalidOperationException && ex.Message == "Backup result=" + code)
                    {
                        detail = PosLocalization.T("backupAutomation.result." + code);
                        break;
                    }
                }
                AppendFailure("Database backup failed", "dbMaintenance.backupFailed", ex, detail);
            }
            finally
            {
                IsBusy = false;
                await RefreshBackupResultAsync().ConfigureAwait(true);
            }
        }

        private async Task RestoreBackupAsync()
        {
            var dlg = new OpenFileDialog
            {
                Title = PosLocalization.T("dbMaintenance.selectBackupTitle"),
                Filter = PosLocalization.T("dbMaintenance.databaseFileFilter"),
                CheckFileExists = true,
                Multiselect = false,
                InitialDirectory = BackupsDirectory
            };
            var owner = DialogOwnerHelper.GetSafeOwner(OwnerWindow);
            if (owner == null)
                throw new InvalidOperationException(PosLocalization.T("dbMaintenance.restoreOwnerMissing"));

            owner.Activate();
            if (dlg.ShowDialog(owner) != true) return;

            if (!(await _demandRestorePermission().ConfigureAwait(true)))
            {
                Append(PosLocalization.T("dbMaintenance.restorePermissionDenied"));
                return;
            }

            IsBusy = true;
            try
            {
                var result = await _service.RestoreDbAsync(dlg.FileName).ConfigureAwait(true);
                OperatorSessionHolder.Current?.LogSecurityEvent(SecurityEventCodes.DbRestore, "backupFile=" + (Path.GetFileName(dlg.FileName) ?? ""));
                OperatorSessionHolder.Current?.LogoutForced();
                Append(PosLocalization.F("dbMaintenance.restoreCompletedFrom", Path.GetFileName(dlg.FileName) ?? "backup.db"));
                Append(PosLocalization.F("dbMaintenance.preRestoreBackup", Path.GetFileName(result.PreRestoreBackupPath) ?? "n/a"));
                Append(PosLocalization.F("dbMaintenance.integrityCheckResult", result.IntegrityCheck));
                Append(PosLocalization.T("dbMaintenance.restoreSyncReview"));
                Win7POS.Wpf.Import.ModernMessageDialog.Show(
                    OwnerWindow ?? DialogOwnerHelper.GetSafeOwner(),
                    PosLocalization.T("dbMaintenance.title"),
                    PosLocalization.T("dbMaintenance.restoreCompletedMessage"));
            }
            catch (Exception ex)
            {
                AppendFailure(
                    "Database restore failed",
                    "dbMaintenance.restoreFailed",
                    ex,
                    GetSafeRestoreFailureDetail(ex));
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task IntegrityCheckAsync()
        {
            IsBusy = true;
            try
            {
                var text = await _service.IntegrityCheckAsync().ConfigureAwait(true);
                Append(PosLocalization.F("dbMaintenance.integrityCheckBlock", text));
            }
            catch (Exception ex)
            {
                AppendFailure("Database integrity check failed", "dbMaintenance.integrityCheckFailed", ex);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task CompleteRestoreReviewAsync()
        {
            if (!_hasMaintenancePermission())
            {
                Append(PosLocalization.F(
                    "common.permissionDeniedOperation",
                    PosLocalization.T("operations.dbMaintenance")));
                return;
            }

            IsBusy = true;
            try
            {
                await _service.CompleteRestoreSyncReviewAsync().ConfigureAwait(true);
                Append(PosLocalization.T("dbMaintenance.restoreReviewCompleted"));
            }
            catch (Exception ex)
            {
                AppendFailure("Restore review completion failed", "dbMaintenance.restoreReviewFailed", ex);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task VacuumAsync()
        {
            if (!_hasMaintenancePermission())
            {
                Append(PosLocalization.F(
                    "common.permissionDeniedOperation",
                    PosLocalization.T("operations.dbMaintenance")));
                return;
            }

            IsBusy = true;
            try
            {
                await _service.VacuumAsync().ConfigureAwait(true);
                Append(PosLocalization.T("dbMaintenance.vacuumCompleted"));
            }
            catch (Exception ex)
            {
                AppendFailure("Database VACUUM failed", "dbMaintenance.vacuumFailed", ex);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void OpenFolder()
        {
            try
            {
                AppPaths.EnsureCreated();
                var path = AppPaths.DataDirectory;
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    Process.Start("explorer.exe", path);
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                AppendFailure("Open data folder failed", "dbMaintenance.openFolderFailed", ex);
            }
        }

        private void OpenSupplierExcelImport()
        {
            if (!_hasCatalogImportPermission())
            {
                Append(PosLocalization.F(
                    "common.permissionDeniedOperation",
                    PosLocalization.T("products.operationImportCatalog")));
                return;
            }

            try
            {
                var applied = SupplierExcelImportDialog.ShowDialog(
                    OwnerWindow ?? DialogOwnerHelper.GetSafeOwner(),
                    _hasCatalogImportPermission);
                if (applied)
                {
                    Win7POS.Wpf.Infrastructure.CatalogEvents.RaiseCatalogChanged(null);
                    Append(PosLocalization.T("supplierExcelImport.completed"));
                }
                else
                {
                    Append(PosLocalization.T("supplierExcelImport.cancelled"));
                }
            }
            catch (Exception ex)
            {
                AppendFailure("Supplier Excel import dialog failed", "supplierExcelImport.failed", ex);
            }
        }

        private void AppendFailure(string logContext, string displayKey, Exception ex, string safeDetail = null)
        {
            _logger.LogError(ex, logContext);
            Append(PosLocalization.F(
                displayKey,
                string.IsNullOrWhiteSpace(safeDetail)
                    ? PosLocalization.T("dbMaintenance.operationFailedFriendly")
                    : safeDetail));
        }

        private static string GetSafeRestoreFailureDetail(Exception ex)
        {
            if (!(ex is InvalidOperationException))
            {
                return null;
            }

            var safeKeys = new[]
            {
                "dbMaintenance.restoreTrustedShopRequired",
                "dbMaintenance.restoreBlockedUnresolvedSales",
                "dbMaintenance.restoreBlockedUnresolvedCatalogImports",
                "dbMaintenance.restoreBlockedUnresolvedArticleMutations",
                "dbMaintenance.restoreBlockedUnresolvedProductImages"
            };
            foreach (var key in safeKeys)
            {
                var safeMessage = PosLocalization.T(key);
                if (string.Equals(ex.Message, safeMessage, StringComparison.Ordinal))
                {
                    return safeMessage;
                }
            }

            return null;
        }

        private void Append(string line)
        {
            OutputLog = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + Environment.NewLine + OutputLog;
        }

        private void RaiseCanExecuteChanged()
        {
            (BackupNowCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RestoreBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (IntegrityCheckCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (CompleteRestoreReviewCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (VacuumCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (SupplierExcelImportCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SaveBackupSettingsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ExportProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (PreviewProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ApplyProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RestoreDefaultsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ViewSettingsAuditCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private sealed class AsyncRelayCommand : ICommand
        {
            private readonly Func<Task> _executeAsync;
            private readonly Func<object, bool> _canExecute;

            public AsyncRelayCommand(Func<Task> executeAsync, Func<object, bool> canExecute = null)
            {
                _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
                _canExecute = canExecute;
            }

            public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);

            public async void Execute(object parameter)
            {
                if (!CanExecute(parameter)) return;
                try
                {
                    await _executeAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Win7POS.Wpf.Infrastructure.UiErrorHandler.Handle(ex, null, "DbMaintenance AsyncRelayCommand failed");
                }
            }
            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }

        private sealed class RelayCommand : ICommand
        {
            private readonly Action<object> _execute;
            private readonly Func<object, bool> _canExecute;

            public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);
            public void Execute(object parameter) => _execute(parameter);
            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
