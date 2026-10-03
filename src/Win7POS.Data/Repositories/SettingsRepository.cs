using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Online;
using Win7POS.Data.Online;
using Win7POS.Core.Operations;
using Win7POS.Data.Backup;
using Microsoft.Data.Sqlite;

namespace Win7POS.Data.Repositories
{
    public sealed class SettingsRepository
    {
        public const string PosLoginLastShopCodeKey = "pos.login.last_shop_code";

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> MonotonicReservationGates =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        private readonly SqliteConnectionFactory _factory;
        private readonly SemaphoreSlim _monotonicReservationGate;

        /// <summary>Committed group notifications; no values or queue/device names leave the repository.</summary>
        public static event EventHandler<SettingsCommittedEventArgs> SettingsChanged;

        public Task<bool> SetStringsAuditedAsync(IReadOnlyDictionary<string, string> values,
            string eventName, string actor, string source, Action demandPermission, string operationId = null)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var copy = values.ToDictionary(x => x.Key, x => x.Value ?? string.Empty, StringComparer.Ordinal);
            return UpdateStringsAuditedAsync(_ => copy, eventName, actor, source, demandPermission, operationId);
        }

        internal async Task<bool> UpdateStringsAuditedAsync(
            Func<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>> prepare,
            string eventName, string actor, string source, Action demandPermission, string operationId = null,
            Action<SqliteConnection, SqliteTransaction, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>> transactionHook = null,
            IEnumerable<string> auditKeys = null)
        {
            if (demandPermission == null) throw new ArgumentNullException(nameof(demandPermission));
            demandPermission();
            var id = operationId ?? Guid.NewGuid().ToString("N");
            if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid settings operation identifier.");
            if (eventName == null || eventName.Length > 64 || eventName.Any(c => !char.IsLetterOrDigit(c))) throw new ArgumentException("Invalid settings event.");
            if (source == null || source.Length > 48 || source.Any(c => !char.IsLetterOrDigit(c) && c != '_')) throw new ArgumentException("Invalid settings source.");
            string[] changedKeys;
            using (var conn = _factory.Open())
            {
                await EnsureSettingsAuditAsync(conn).ConfigureAwait(false);
                using (var tx = conn.BeginTransaction(deferred: false))
                {
                    demandPermission();
                    if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM settings_audit WHERE operation_id=@id", new { id }, tx).ConfigureAwait(false) != 0)
                        return false;
                    var requested = PortableSourceKeys();
                    var beforeRows = await conn.QueryAsync<SettingValue>("SELECT key AS Key,value AS Value FROM app_settings WHERE key IN @requested", new { requested }, tx).ConfigureAwait(false);
                    var before = beforeRows.ToDictionary(x => x.Key, x => x.Value ?? string.Empty, StringComparer.Ordinal);
                    var values = prepare(before) ?? throw new InvalidOperationException("Settings preparation failed.");
                    if (values.Keys.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("key is empty");
                    if (eventName == "ApplicationVersionObserved" && values.All(x => before.TryGetValue(x.Key, out var existing) && existing == x.Value))
                        return false;
                    changedKeys = values.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                    // Fetch nonportable values only to preserve the local setting group. They are never hashed.
                    var after = new Dictionary<string, string>(before, StringComparer.Ordinal);
                    foreach (var pair in values) after[pair.Key] = pair.Value ?? string.Empty;
                    await conn.ExecuteAsync(@"
INSERT INTO app_settings(key,value) VALUES(@key,@value)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                        values.Select(x => new { key = x.Key, value = x.Value ?? string.Empty }), tx).ConfigureAwait(false);
                    transactionHook?.Invoke(conn, tx, before, after);
                    var names = (auditKeys ?? changedKeys).Where(x => x != null && x.Length <= 160 && x.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '_'))
                        .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                    AppendSettingsAudit(conn, tx, id, eventName, actor, source, names, before, after, DateTime.UtcNow);
                    demandPermission();
                    tx.Commit();
                }
            }
            if (changedKeys.Length > 0)
            {
                var notification = new SettingsCommittedEventArgs(Path.GetFullPath(_factory.DbPath), changedKeys);
                foreach (EventHandler<SettingsCommittedEventArgs> handler in SettingsChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
                {
                    try { handler(this, notification); } catch { /* A view cannot undo a committed transaction. */ }
                }
            }
            return true;
        }

        public Task<IReadOnlyDictionary<string, string>> GetPortableSourceAsync() => GetStringsAsync(PortableSourceKeys());

        public async Task<IReadOnlyList<SettingsAuditEntry>> GetSettingsAuditAsync(Action demandPermission, int limit = 100)
        {
            if (demandPermission == null) throw new ArgumentNullException(nameof(demandPermission));
            demandPermission();
            if (limit < 1 || limit > 500) throw new ArgumentOutOfRangeException(nameof(limit));
            using (var conn = _factory.Open())
            {
                await EnsureSettingsAuditAsync(conn).ConfigureAwait(false);
                demandPermission();
                var rows = await conn.QueryAsync<SettingsAuditEntry>(@"
SELECT id AS Id,event AS Event,actor AS Actor,source AS Source,key_names AS KeyNames,key_count AS KeyCount,
 before_hash AS BeforeHash,after_hash AS AfterHash,result AS Result,created_utc AS CreatedUtc
FROM settings_audit ORDER BY id DESC LIMIT @limit", new { limit }).ConfigureAwait(false);
                demandPermission();
                return rows.ToArray();
            }
        }

        internal static string[] PortableSourceKeys() => PortableSettingsPolicy.Keys.Concat(new[]
        {
            "printer.copies", "pos.useReceipt42", "pos.printer.receipt.name", "printer.name", "pos.cashdrawer.printer_name",
            "pos.cashdrawer.command", "pos.cashdrawer.enabled", "pos.autoPrint", "pos.customer_display.enabled", "pos.customer_display.auto_open", "pos.customer_display.show_barcode",
            "pos.operations.backup.destination.kind", "pos.operations.backup.destination.path", PortableSettingsPolicy.LastSeenVersionKey
        }).Distinct(StringComparer.Ordinal).ToArray();

        private const string SettingsAuditSchema = @"
CREATE TABLE IF NOT EXISTS settings_audit(
 id INTEGER PRIMARY KEY,operation_id TEXT NOT NULL UNIQUE,event TEXT NOT NULL,actor TEXT NOT NULL,source TEXT NOT NULL,
 key_names TEXT NOT NULL,key_count INTEGER NOT NULL,before_hash TEXT NOT NULL,after_hash TEXT NOT NULL,result TEXT NOT NULL,created_utc TEXT NOT NULL);";
        private static Task EnsureSettingsAuditAsync(SqliteConnection conn) => conn.ExecuteAsync(SettingsAuditSchema);
        internal static void EnsureSettingsAudit(SqliteConnection conn, SqliteTransaction transaction) => conn.Execute(SettingsAuditSchema, transaction: transaction);
        internal static void AppendSettingsAudit(SqliteConnection conn, SqliteTransaction tx, string id, string eventName, string actor, string source,
            IEnumerable<string> keys, IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after, DateTime utc)
        {
            var names = keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            conn.Execute(@"
INSERT INTO settings_audit(operation_id,event,actor,source,key_names,key_count,before_hash,after_hash,result,created_utc)
VALUES(@id,@eventName,@actor,@source,@names,@count,@beforeHash,@afterHash,'committed',@utc);",
                new { id, eventName, actor = BackupAutomationDestination.SafeActor(actor), source,
                    names = string.Join(",", names), count = names.Length,
                    beforeHash = PortableSettingsPolicy.SafeHash(before), afterHash = PortableSettingsPolicy.SafeHash(after),
                    utc = utc.ToString("O", CultureInfo.InvariantCulture) }, tx);
        }

        public SettingsRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            var normalizedDbPath = Path.GetFullPath(_factory.DbPath);
            _monotonicReservationGate = MonotonicReservationGates.GetOrAdd(
                normalizedDbPath,
                _ => new SemaphoreSlim(1, 1));
        }

        public async Task<string> GetStringAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is empty");
            using var conn = _factory.Open();
            return await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT value FROM app_settings WHERE key = @key",
                new { key }).ConfigureAwait(false);
        }

        public async Task SetStringAsync(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is empty");
            using var conn = _factory.Open();
            await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES(@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new { key, value = value ?? string.Empty }).ConfigureAwait(false);
        }

        /// <summary>Validate and commit a complete settings group, including legacy aliases.</summary>
        public async Task SetStringsAsync(IReadOnlyDictionary<string, string> values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var rows = values.Select(pair => new { key = pair.Key, value = pair.Value ?? string.Empty }).ToArray();
            if (rows.Any(row => string.IsNullOrWhiteSpace(row.key)))
                throw new ArgumentException("key is empty", nameof(values));
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES(@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;", rows, tx).ConfigureAwait(false);
            tx.Commit();
        }

        /// <summary>A single SQLite read snapshot, so readers cannot mix settings generations.</summary>
        public async Task<IReadOnlyDictionary<string, string>> GetStringsAsync(IEnumerable<string> keys)
        {
            var requested = (keys ?? throw new ArgumentNullException(nameof(keys))).Distinct(StringComparer.Ordinal).ToArray();
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<SettingValue>(
                "SELECT key AS Key, value AS Value FROM app_settings WHERE key IN @requested", new { requested }).ConfigureAwait(false);
            return rows.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
        }

        private sealed class SettingValue
        {
            public string Key { get; set; }
            public string Value { get; set; }
        }

        public async Task<bool> SetStringIfGenerationCurrentAsync(
            string key,
            string value,
            OnlineSyncGeneration generation)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is empty");
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction(deferred: false);
            var permitted = generation != null
                ? await OnlineSyncGenerationRepository.IsCurrentAndActiveAsync(
                    conn,
                    tx,
                    generation).ConfigureAwait(false)
                : await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM pos_sync_session_generation
WHERE singleton_id = 1 AND active = 1;",
                    transaction: tx).ConfigureAwait(false) == 0;
            if (!permitted)
            {
                tx.Rollback();
                return false;
            }

            await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES(@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new { key, value = value ?? string.Empty },
                tx).ConfigureAwait(false);
            tx.Commit();
            return true;
        }

        public async Task<bool?> GetBoolAsync(string key)
        {
            var raw = await GetStringAsync(key).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return null;

            if (string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase))
                return false;

            return null;
        }

        public Task SetBoolAsync(string key, bool value)
        {
            return SetStringAsync(key, value ? "1" : "0");
        }

        public Task<bool> SetBoolIfGenerationCurrentAsync(
            string key,
            bool value,
            OnlineSyncGeneration generation)
        {
            return SetStringIfGenerationCurrentAsync(key, value ? "1" : "0", generation);
        }

        public async Task<int?> GetIntAsync(string key)
        {
            var raw = await GetStringAsync(key).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return null;
            return value;
        }

        public Task SetIntAsync(string key, int value)
        {
            return SetStringAsync(key, value.ToString(CultureInfo.InvariantCulture));
        }

        public Task<bool> SetIntIfGenerationCurrentAsync(
            string key,
            int value,
            OnlineSyncGeneration generation)
        {
            return SetStringIfGenerationCurrentAsync(
                key,
                value.ToString(CultureInfo.InvariantCulture),
                generation);
        }

        /// <summary>
        /// Atomically reserves a positive integer that never moves backwards.
        /// If <paramref name="requested"/> is not greater than the stored value,
        /// the next integer is reserved instead. Corrupt or exhausted state fails
        /// closed so callers cannot accidentally reuse an identifier.
        /// </summary>
        public async Task<int> ReserveMonotonicIntAsync(string key, int requested)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is empty");
            if (requested <= 0) throw new ArgumentOutOfRangeException(nameof(requested));

            await _monotonicReservationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var conn = _factory.Open();
                using var tx = conn.BeginTransaction(deferred: false);
                try
                {
                    var raw = await conn.QuerySingleOrDefaultAsync<string>(
                        "SELECT value FROM app_settings WHERE key = @key",
                        new { key },
                        tx).ConfigureAwait(false);

                    var current = 0;
                    if (raw != null &&
                        (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out current) ||
                         current < 0))
                    {
                        throw new InvalidOperationException(
                            "The monotonic integer setting is invalid and cannot be reserved safely.");
                    }

                    int reserved;
                    if (requested > current)
                    {
                        reserved = requested;
                    }
                    else
                    {
                        if (current == int.MaxValue)
                            throw new InvalidOperationException(
                                "The monotonic integer setting is exhausted and cannot be reserved safely.");
                        reserved = checked(current + 1);
                    }

                    await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES(@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                        new
                        {
                            key,
                            value = reserved.ToString(CultureInfo.InvariantCulture)
                        },
                        tx).ConfigureAwait(false);
                    tx.Commit();
                    return reserved;
                }
                catch
                {
                    try { tx.Rollback(); }
                    catch { }
                    throw;
                }
            }
            finally
            {
                _monotonicReservationGate.Release();
            }
        }

        public Task<string> GetLastPosLoginShopCodeAsync()
        {
            return GetStringAsync(PosLoginLastShopCodeKey);
        }

        public Task SetLastPosLoginShopCodeAsync(string shopCode)
        {
            return SetStringAsync(PosLoginLastShopCodeKey, NormalizeShopCode(shopCode));
        }

        private static string NormalizeShopCode(string shopCode)
        {
            return (shopCode ?? string.Empty).Trim().ToUpperInvariant();
        }
    }
}
