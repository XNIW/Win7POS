using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Win7POS.Core.Backup;
using Win7POS.Core.Operations;
using Win7POS.Core.Security;
using Win7POS.Data.Backup;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Operations
{
    public sealed class SettingsProfilePreview
    {
        public IReadOnlyList<SettingsProfileDifference> Differences { get; }
        public IReadOnlyList<string> SafetyAdjustments { get; }
        internal IReadOnlyDictionary<string, string> Values { get; }
        internal string PreviousHash { get; }
        internal IReadOnlyDictionary<string, string> PreviousValues { get; }
        internal string OperationId { get; } = Guid.NewGuid().ToString("N");
        internal SettingsOperationsService Owner { get; }
        internal SettingsProfilePreview(SettingsOperationsService owner, IReadOnlyDictionary<string, string> before,
            IReadOnlyDictionary<string, string> values, IEnumerable<string> adjustments)
        {
            Owner = owner;
            PreviousHash = PortableSettingsPolicy.SafeHash(before);
            PreviousValues = new ReadOnlyDictionary<string, string>(before.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
            Values = new ReadOnlyDictionary<string, string>(values.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
            SafetyAdjustments = adjustments.ToArray();
            var canonicalBefore = PortableSettingsPolicy.Snapshot(before);
            Differences = values.Where(x => PortableSettingsPolicy.IsPortable(x.Key) && canonicalBefore[x.Key] != x.Value)
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new SettingsProfileDifference(x.Key, canonicalBefore[x.Key], x.Value)).ToArray();
        }
    }
    public sealed class SettingsProfileDifference
    {
        public string Key { get; }
        public string Before { get; }
        public string After { get; }
        internal SettingsProfileDifference(string key, string before, string after) { Key = key; Before = before; After = after; }
    }

    /// <summary>Permission checks are repeated in the commit transaction, independent of the UI.</summary>
    public sealed class SettingsOperationsService
    {
        private readonly SettingsRepository _settings;
        private readonly BackupAutomationService _backup;
        private readonly Func<string, bool> _hasPermission;
        private readonly string _actor;
        private readonly string _defaultBackupDirectory;
        private readonly string _dbPath;
        public SettingsOperationsService(SqliteConnectionFactory factory, string defaultBackupDirectory,
            Func<string, bool> hasPermission, string actor, BackupAutomationService backup = null)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _settings = new SettingsRepository(factory);
            _hasPermission = hasPermission ?? throw new ArgumentNullException(nameof(hasPermission));
            _actor = actor;
            _defaultBackupDirectory = defaultBackupDirectory ?? throw new ArgumentNullException(nameof(defaultBackupDirectory));
            _dbPath = factory.DbPath;
            _backup = backup ?? BackupAutomationService.GetOrCreate(factory, defaultBackupDirectory);
        }
        private void Demand(string permission)
        {
            if (!_hasPermission(permission)) throw new UnauthorizedAccessException("Settings operation permission denied.");
        }
        public async Task<byte[]> ExportAsync(string applicationVersion)
        {
            Demand(PermissionCodes.DbBackup);
            PortableSettingsProfile profile = null;
            await _settings.UpdateStringsAuditedAsync(current =>
            {
                profile = PortableSettingsProfile.Create(applicationVersion, DateTime.UtcNow, PortableSettingsPolicy.Snapshot(current));
                return new Dictionary<string, string>();
            }, "SettingsProfileExport", _actor, "operations", () => Demand(PermissionCodes.DbBackup), auditKeys: PortableSettingsPolicy.Keys).ConfigureAwait(false);
            return new System.Text.UTF8Encoding(false).GetBytes(profile.ToJson());
        }
        public async Task<SettingsProfilePreview> PreviewImportAsync(byte[] bytes)
        {
            Demand(PermissionCodes.DbMaintenance);
            var profile = PortableSettingsProfile.Parse(bytes);
            var before = await _settings.GetPortableSourceAsync().ConfigureAwait(false);
            Demand(PermissionCodes.DbMaintenance);
            var merged = PortableSettingsPolicy.Snapshot(before).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            foreach (var pair in profile.Settings) merged[pair.Key] = pair.Value;
            PortableSettingsPolicy.Validate(merged);
            var adjustments = ApplySafety(before, merged);
            var values = AddAliases(merged);
            return new SettingsProfilePreview(this, before, values, adjustments);
        }
        public async Task<bool> ImportAsync(SettingsProfilePreview preview, bool confirmed)
        {
            Demand(PermissionCodes.DbMaintenance);
            if (!confirmed) throw new InvalidOperationException("Profile confirmation is required.");
            if (preview == null || !ReferenceEquals(preview.Owner, this)) throw new ArgumentException("Profile preview is invalid.");
            return await CommitAsync(preview.Values, "SettingsProfileImport", preview.OperationId, preview.PreviousValues).ConfigureAwait(false);
        }
        public async Task<bool> RestoreDefaultsAsync(SettingsDefaultsScope scope, bool confirmed)
        {
            Demand(PermissionCodes.DbMaintenance);
            if (!confirmed) throw new InvalidOperationException("Defaults confirmation is required.");
            var defaults = PortableSettingsPolicy.Defaults(scope).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            var values = AddAliases(defaults);
            if (scope == SettingsDefaultsScope.CustomerDisplay || scope == SettingsDefaultsScope.AllPortable)
            {
                values["pos.customer_display.enabled"] = "false";
                values["pos.customer_display.auto_open"] = "false";
            }
            return await CommitAsync(values, "SettingsDefaultsRestored", null, null).ConfigureAwait(false);
        }
        public Task<IReadOnlyList<SettingsAuditEntry>> GetAuditAsync(int limit = 100) =>
            _settings.GetSettingsAuditAsync(() => Demand(PermissionCodes.DbMaintenance), limit);

        private async Task<bool> CommitAsync(IReadOnlyDictionary<string, string> values, string eventName, string operationId, IReadOnlyDictionary<string, string> expectedValues)
        {
            var backupChanges = values.Keys.Any(x => x.StartsWith("pos.operations.backup.", StringComparison.Ordinal));
            if (expectedValues != null)
            {
                // File/share checks stay outside SQLite's writer transaction.
                var safe = values.Where(x => PortableSettingsPolicy.IsPortable(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
                ApplySafety(expectedValues, safe);
                if (safe.Any(x => values[x.Key] != x.Value)) throw new InvalidOperationException("Local target became unavailable. Preview the profile again.");
            }
            // Share backup/restore's gate and file lease; no separate scheduler/store is introduced.
            using (await _backup.EnterMaintenanceAsync().ConfigureAwait(false))
            {
                if (backupChanges) _backup.EnsurePortablePolicyState();
                return await _settings.UpdateStringsAuditedAsync(before =>
                {
                    // Compare nonportable local targets privately as values, never as published digests.
                    if (expectedValues != null && (before.Count != expectedValues.Count || before.Any(x => !expectedValues.TryGetValue(x.Key, out var value) || value != x.Value)))
                        throw new InvalidOperationException("Settings changed after the preview. Preview the profile again.");
                    return values;
                }, eventName, _actor, "operations", () => Demand(PermissionCodes.DbMaintenance), operationId,
                backupChanges ? (Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>>)
                    ((conn, tx, before, after) => BackupAutomationStore.ApplyPortablePolicyInTransaction(conn, tx, before, after, _actor, DateTime.Now.Ticks, DateTime.UtcNow.Ticks)) : null).ConfigureAwait(false);
            }
        }
        private IReadOnlyList<string> ApplySafety(IReadOnlyDictionary<string, string> local, Dictionary<string, string> values)
        {
            var adjustments = new List<string>();
            // Profiles do not arm receipt/drawer output. Local hardware activation is explicit.
            values["pos.printer.receipt.enabled"] = "false";
            values["pos.printer.receipt.auto_print_after_sale"] = "false";
            values["pos.printer.receipt.allow_windows_default"] = "false";
            values["pos.cashdrawer.mode"] = "disabled";
            adjustments.Add("hardware_activation_local");
            if (values["pos.operations.backup.schedule"] != "disabled")
            {
                try
                {
                    var options = new BackupAutomationOptions
                    {
                        Schedule = new BackupScheduleOptions { Mode = (BackupScheduleMode)Enum.Parse(typeof(BackupScheduleMode), values["pos.operations.backup.schedule"], true),
                            LocalTime = values["pos.operations.backup.local_time"], WeeklyDay = (DayOfWeek)Enum.Parse(typeof(DayOfWeek), values["pos.operations.backup.weekly_day"]),
                            CatchUpOnStartup = values["pos.operations.backup.catch_up_on_startup"] == "true" },
                        RetentionMaxCount = int.Parse(values["pos.operations.backup.retention.max_count"], CultureInfo.InvariantCulture),
                        RetentionMaxAgeDays = int.Parse(values["pos.operations.backup.retention.max_age_days"], CultureInfo.InvariantCulture),
                        DestinationKind = local.TryGetValue("pos.operations.backup.destination.kind", out var kind) ? kind : "local",
                        DestinationPath = local.TryGetValue("pos.operations.backup.destination.path", out var path) ? path : ""
                    };
                    var resolved = BackupAutomationDestination.Resolve(options, _defaultBackupDirectory, _dbPath);
                    BackupAutomationDestination.RejectReparseAncestors(resolved);
                    if (!Directory.Exists(resolved)) throw new IOException();
                }
                catch (Exception exception) when (exception is ArgumentException || exception is IOException || exception is UnauthorizedAccessException)
                {
                    values["pos.operations.backup.schedule"] = "disabled";
                    adjustments.Add("backup_target_invalid");
                }
            }
            PortableSettingsPolicy.Validate(values);
            return adjustments;
        }
        private static bool IsTrue(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var value) && (value == "1" || value == "true");
        private static Dictionary<string, string> AddAliases(IReadOnlyDictionary<string, string> values)
        {
            var result = values.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            if (values.TryGetValue("pos.printer.receipt.profile", out var profile)) result["pos.useReceipt42"] = profile == "thermal_80mm_42col" ? "1" : "0";
            if (values.TryGetValue("pos.printer.receipt.copies", out var copies)) result["printer.copies"] = copies;
            if (values.TryGetValue("pos.printer.receipt.auto_print_after_sale", out var auto)) result["pos.autoPrint"] = auto == "true" ? "1" : "0";
            if (values.TryGetValue("pos.cashdrawer.mode", out var drawer)) result["pos.cashdrawer.enabled"] = drawer == "printer_kick" ? "1" : "0";
            if (values.TryGetValue("pos.customer_display.privacy.barcode_mode", out var barcodeMode)) result["pos.customer_display.show_barcode"] = barcodeMode == "hidden" ? "false" : "true";
            return result;
        }
        public static async Task<bool> ObserveApplicationVersionAsync(SqliteConnectionFactory factory, string version)
        {
            // Validate the same invariant application metadata as portable profiles.
            PortableSettingsProfile.Create(version, DateTime.UtcNow, new Dictionary<string, string>());
            var repository = new SettingsRepository(factory);
            var previous = await repository.GetStringAsync(PortableSettingsPolicy.LastSeenVersionKey).ConfigureAwait(false);
            if (previous == version) return false;
            var source = string.IsNullOrEmpty(previous) ? "first_observation" :
                Version.TryParse(previous, out var old) && Version.TryParse(version, out var current) && current < old ? "downgrade" : "upgrade";
            return await repository.SetStringsAuditedAsync(new Dictionary<string, string> { [PortableSettingsPolicy.LastSeenVersionKey] = version },
                "ApplicationVersionObserved", "runtime", source, () => { }).ConfigureAwait(false);
        }
    }
}
