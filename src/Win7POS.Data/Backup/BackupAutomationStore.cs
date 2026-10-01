using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Backup;

namespace Win7POS.Data.Backup
{
    internal sealed class BackupAutomationState
    {
        public string DbKey { get; set; } = string.Empty;
        public long ConsideredLocal { get; set; }
        public long ActivatedLocal { get; set; }
        public long MaxUtc { get; set; }
        public long RetryUtc { get; set; }
        public string LastResult { get; set; } = string.Empty;
        public string PendingId { get; set; } = string.Empty;
        public string PendingPath { get; set; } = string.Empty;
        public string PendingDestination { get; set; } = string.Empty;
        public long PendingLocal { get; set; }
        public long PendingUtc { get; set; }

        internal BackupAutomationState Copy() => (BackupAutomationState)MemberwiseClone();

        internal void ClearPending()
        {
            PendingId = PendingPath = PendingDestination = string.Empty;
            PendingLocal = PendingUtc = 0;
            RetryUtc = 0;
        }
    }

    internal sealed class BackupAutomationManagedFile
    {
        public string Path { get; set; } = string.Empty;
        public string Destination { get; set; } = string.Empty;
        public string DbKey { get; set; } = string.Empty;
        public long CreatedUtc { get; set; }
        public long Length { get; set; }
        public string Hash { get; set; } = string.Empty;
        public string OperationId { get; set; } = string.Empty;
    }

    internal sealed class BackupAutomationStore
    {
        private const string Prefix = "pos.operations.backup.";
        private readonly SqliteConnectionFactory _factory;
        private readonly string _dbKey;

        internal BackupAutomationStore(SqliteConnectionFactory factory, string dbKey)
        {
            _factory = factory;
            _dbKey = dbKey;
        }

        internal BackupAutomationState LoadState(long activationLocal = 0)
        {
            using (var connection = OpenExisting())
            {
                EnsureSchema(connection);
                var state = connection.QuerySingle<BackupAutomationState>(@"
SELECT db_key AS DbKey, considered_local AS ConsideredLocal, activated_local AS ActivatedLocal,
 max_utc AS MaxUtc, retry_utc AS RetryUtc, last_result AS LastResult,
 pending_id AS PendingId, pending_path AS PendingPath, pending_destination AS PendingDestination,
 pending_local AS PendingLocal, pending_utc AS PendingUtc
FROM backup_automation_state WHERE singleton_id=1;");
                if (!string.Equals(state.DbKey, _dbKey, StringComparison.Ordinal))
                {
                    // A restore from another POS must never grant ownership of its external files.
                    using (var transaction = connection.BeginTransaction(deferred: false))
                    {
                        try
                        {
                            connection.Execute(@"
DELETE FROM backup_automation_files;
UPDATE backup_automation_state SET db_key=@dbKey,considered_local=0,activated_local=@activationLocal,
 max_utc=0,retry_utc=0,last_result='',pending_id='',pending_path='',pending_destination='',
 pending_local=0,pending_utc=0 WHERE singleton_id=1;", new { dbKey = _dbKey, activationLocal }, transaction);
                            InsertAudit(connection, transaction, "identity_reset", "automation", 0,
                                string.Empty, string.Empty, "ownership_disowned", string.Empty, 0, 0);
                            transaction.Commit();
                        }
                        catch
                        {
                            try { transaction.Rollback(); } catch { }
                            throw;
                        }
                    }
                    state = new BackupAutomationState { DbKey = _dbKey, ActivatedLocal = activationLocal };
                }
                return state;
            }
        }

        internal BackupAutomationOptions LoadOptions()
        {
            using (var connection = OpenExisting())
                return ReadOptions(connection, null);
        }

        internal void SaveOptions(BackupAutomationOptions options, string actor, long localTicks, long utcTicks)
        {
            using (var connection = OpenExisting())
            {
                EnsureSchema(connection);
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    try
                    {
                        var before = ReadOptions(connection, transaction);
                        var rows = OptionRows(options);
                        connection.Execute(@"
INSERT INTO app_settings(key,value) VALUES(@Key,@Value)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;", rows.Select(row => new { row.Key, row.Value }), transaction);
                        if (ScheduleSignature(before) != ScheduleSignature(options))
                        {
                            connection.Execute(@"
UPDATE backup_automation_state SET activated_local=@localTicks WHERE singleton_id=1;",
                                new { localTicks }, transaction);
                        }
                        InsertAudit(connection, transaction, "settings_saved", actor, rows.Count,
                            OptionsHash(before), OptionsHash(options), "saved", string.Empty, 0, utcTicks,
                            string.Join(",", rows.Keys.OrderBy(key => key, StringComparer.Ordinal)));
                        transaction.Commit();
                    }
                    catch
                    {
                        try { transaction.Rollback(); } catch { }
                        throw;
                    }
                }
            }
        }

        internal void SaveState(BackupAutomationState state, string code = null, string fileName = "", int deletedCount = 0,
            BackupAutomationManagedFile managed = null, string removedPath = null)
        {
            using (var connection = OpenExisting())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                try
                {
                    connection.Execute(@"
UPDATE backup_automation_state SET considered_local=@ConsideredLocal, activated_local=@ActivatedLocal,
 max_utc=@MaxUtc, retry_utc=@RetryUtc, last_result=@LastResult,
 pending_id=@PendingId, pending_path=@PendingPath, pending_destination=@PendingDestination,
 pending_local=@PendingLocal, pending_utc=@PendingUtc WHERE singleton_id=1 AND db_key=@DbKey;", state, transaction);
                    if (managed != null)
                    {
                        connection.Execute(@"
INSERT INTO backup_automation_files(path,destination,db_key,created_utc,length,hash,operation_id)
VALUES(@Path,@Destination,@DbKey,@CreatedUtc,@Length,@Hash,@OperationId)
ON CONFLICT(path) DO NOTHING;", managed, transaction);
                    }
                    if (removedPath != null)
                        connection.Execute("DELETE FROM backup_automation_files WHERE path=@removedPath AND db_key=@DbKey;",
                            new { removedPath, state.DbKey }, transaction);
                    if (code != null)
                        InsertAudit(connection, transaction, "backup_result", "automation", 0, string.Empty, string.Empty,
                            code, System.IO.Path.GetFileName(fileName ?? string.Empty), deletedCount, state.MaxUtc);
                    transaction.Commit();
                }
                catch
                {
                    try { transaction.Rollback(); } catch { }
                    throw;
                }
            }
        }

        internal List<BackupAutomationManagedFile> LoadManaged(string destination)
        {
            using (var connection = OpenExisting())
                return connection.Query<BackupAutomationManagedFile>(@"
SELECT path AS Path,destination AS Destination,db_key AS DbKey,created_utc AS CreatedUtc,
 length AS Length,hash AS Hash,operation_id AS OperationId
FROM backup_automation_files WHERE destination=@destination AND db_key=@dbKey
ORDER BY created_utc DESC, rowid DESC;", new { destination, dbKey = _dbKey }).ToList();
        }

        internal static string HashText(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty);
        }

        private SqliteConnection OpenExisting()
        {
            // Prevent Create-mode factory opens from recreating a source deleted between checks.
            using (var guard = new System.IO.FileStream(_factory.DbPath, System.IO.FileMode.Open,
                System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                return _factory.Open();
        }

        private void EnsureSchema(SqliteConnection connection)
        {
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                try
                {
                    connection.Execute(@"
CREATE TABLE IF NOT EXISTS backup_automation_state(
 singleton_id INTEGER PRIMARY KEY CHECK(singleton_id=1),db_key TEXT NOT NULL,
 considered_local INTEGER NOT NULL DEFAULT 0,activated_local INTEGER NOT NULL DEFAULT 0,
 max_utc INTEGER NOT NULL DEFAULT 0,retry_utc INTEGER NOT NULL DEFAULT 0,last_result TEXT NOT NULL DEFAULT '',
 pending_id TEXT NOT NULL DEFAULT '',pending_path TEXT NOT NULL DEFAULT '',pending_destination TEXT NOT NULL DEFAULT '',
 pending_local INTEGER NOT NULL DEFAULT 0,pending_utc INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS backup_automation_files(
 path TEXT PRIMARY KEY,destination TEXT NOT NULL,db_key TEXT NOT NULL,created_utc INTEGER NOT NULL,
 length INTEGER NOT NULL,hash TEXT NOT NULL,operation_id TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS backup_automation_audit(
 id INTEGER PRIMARY KEY,event TEXT NOT NULL,actor TEXT NOT NULL,key_count INTEGER NOT NULL,key_names TEXT NOT NULL,
 before_hash TEXT NOT NULL,after_hash TEXT NOT NULL,code TEXT NOT NULL,file_name TEXT NOT NULL,
 deleted_count INTEGER NOT NULL,created_utc INTEGER NOT NULL);
INSERT INTO backup_automation_state(singleton_id,db_key) VALUES(1,@dbKey)
ON CONFLICT(singleton_id) DO NOTHING;", new { dbKey = _dbKey }, transaction);
                    transaction.Commit();
                }
                catch
                {
                    try { transaction.Rollback(); } catch { }
                    throw;
                }
            }
        }

        private static void InsertAudit(SqliteConnection connection, SqliteTransaction transaction,
            string eventName, string actor, int keyCount, string beforeHash, string afterHash,
            string code, string fileName, int deletedCount, long utcTicks, string keyNames = "")
        {
            connection.Execute(@"
INSERT INTO backup_automation_audit(event,actor,key_count,key_names,before_hash,after_hash,code,file_name,deleted_count,created_utc)
VALUES(@eventName,@actor,@keyCount,@keyNames,@beforeHash,@afterHash,@code,@fileName,@deletedCount,@utcTicks);",
                new { eventName, actor = BackupAutomationDestination.SafeActor(actor), keyCount, beforeHash, afterHash,
                    code, fileName, deletedCount, utcTicks, keyNames }, transaction);
        }

        private static BackupAutomationOptions ReadOptions(SqliteConnection connection, SqliteTransaction transaction)
        {
            var rows = connection.Query<SettingRow>("SELECT key AS Key,value AS Value FROM app_settings WHERE key LIKE @prefix;",
                new { prefix = Prefix + "%" }, transaction).ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
            var options = new BackupAutomationOptions();
            var mode = Read(rows, "schedule", "disabled");
            switch (mode)
            {
                case "disabled": options.Schedule.Mode = BackupScheduleMode.Disabled; break;
                case "daily": options.Schedule.Mode = BackupScheduleMode.Daily; break;
                case "weekly": options.Schedule.Mode = BackupScheduleMode.Weekly; break;
                default: throw new InvalidOperationException("Backup schedule setting is invalid.");
            }
            options.Schedule.LocalTime = Read(rows, "local_time", options.Schedule.LocalTime);
            var weeklyDay = Read(rows, "weekly_day", options.Schedule.WeeklyDay.ToString());
            if (!Enum.TryParse(weeklyDay, false, out DayOfWeek day) ||
                !Enum.IsDefined(typeof(DayOfWeek), day) || weeklyDay != day.ToString())
                throw new InvalidOperationException("Backup weekly-day setting is invalid.");
            options.Schedule.WeeklyDay = day;
            var catchUp = Read(rows, "catch_up_on_startup", "true");
            if (catchUp != "false" && catchUp != "true")
                throw new InvalidOperationException("Backup catch-up setting is invalid.");
            options.Schedule.CatchUpOnStartup = catchUp == "true";
            options.RetentionMaxCount = ReadInt(rows, "retention.max_count", options.RetentionMaxCount);
            options.RetentionMaxAgeDays = ReadInt(rows, "retention.max_age_days", options.RetentionMaxAgeDays);
            options.DestinationKind = Read(rows, "destination.kind", options.DestinationKind);
            options.DestinationPath = Read(rows, "destination.path", options.DestinationPath);
            return options;
        }

        private static string Read(Dictionary<string, string> rows, string key, string defaultValue)
        {
            return rows.TryGetValue(Prefix + key, out var value) ? value : defaultValue;
        }

        private static int ReadInt(Dictionary<string, string> rows, string key, int defaultValue)
        {
            var text = Read(rows, key, defaultValue.ToString(CultureInfo.InvariantCulture));
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                throw new InvalidOperationException("Backup integer setting is invalid.");
            return value;
        }

        private static Dictionary<string, string> OptionRows(BackupAutomationOptions options)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Prefix + "schedule"] = options.Schedule.Mode.ToString().ToLowerInvariant(),
                [Prefix + "local_time"] = options.Schedule.LocalTime,
                [Prefix + "weekly_day"] = options.Schedule.WeeklyDay.ToString(),
                [Prefix + "catch_up_on_startup"] = options.Schedule.CatchUpOnStartup ? "true" : "false",
                [Prefix + "retention.max_count"] = options.RetentionMaxCount.ToString(CultureInfo.InvariantCulture),
                [Prefix + "retention.max_age_days"] = options.RetentionMaxAgeDays.ToString(CultureInfo.InvariantCulture),
                [Prefix + "destination.kind"] = options.DestinationKind,
                [Prefix + "destination.path"] = options.DestinationPath ?? string.Empty
            };
        }

        private static string OptionsHash(BackupAutomationOptions options)
        {
            return HashText(string.Join("\n", OptionRows(options).OrderBy(row => row.Key, StringComparer.Ordinal)
                .Select(row => row.Key + "=" + row.Value)));
        }

        private static string ScheduleSignature(BackupAutomationOptions options)
        {
            return options.Schedule.Mode + "|" + options.Schedule.LocalTime + "|" + options.Schedule.WeeklyDay;
        }

        private sealed class SettingRow
        {
            public string Key { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
        }
    }
}
