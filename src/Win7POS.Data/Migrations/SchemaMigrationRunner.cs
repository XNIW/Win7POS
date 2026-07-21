using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Data.Backup;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Migrations
{
    public sealed class SchemaMigrationRunner
    {
        private const string LedgerTable = "schema_migrations";
        private const string LedgerSchemaSql = @"
CREATE TABLE schema_migrations (
  migration_id TEXT PRIMARY KEY,
  checksum TEXT NOT NULL,
  description TEXT NOT NULL,
  applied_at TEXT NOT NULL,
  app_version TEXT NULL
);";

        private readonly SqliteConnectionFactory _factory;
        private readonly IReadOnlyList<SchemaMigration> _migrations;
        private readonly SchemaMigrationRunnerOptions _options;

        public SchemaMigrationRunner(SqliteConnectionFactory factory)
            : this(factory, SchemaMigrationRegistry.All, null)
        {
        }

        public SchemaMigrationRunner(
            SqliteConnectionFactory factory,
            IEnumerable<SchemaMigration> migrations,
            SchemaMigrationRunnerOptions options = null)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            if (migrations == null)
                throw new ArgumentNullException(nameof(migrations));

            var copy = migrations.ToList();
            ValidateRegistry(copy);
            _migrations = new ReadOnlyCollection<SchemaMigration>(copy);
            _options = options ?? new SchemaMigrationRunnerOptions();
        }

        public SchemaMigrationRunResult Run()
        {
            SchemaMigrationRunResult result = null;
            SqliteConnectionFactory.RunExclusiveMaintenanceAsync(() =>
            {
                result = RunCore();
                return Task.CompletedTask;
            }).GetAwaiter().GetResult();

            if (result == null)
                throw new InvalidOperationException("SQLite migration run completed without a result.");
            return result;
        }

        private SchemaMigrationRunResult RunCore()
        {
            var databaseExisted = File.Exists(_factory.DbPath) &&
                new FileInfo(_factory.DbPath).Length > 0;
            var inspection = InspectDatabase();
            var bootstrapped = new List<string>();
            var applied = new List<string>();
            var backupFileName = string.Empty;

            var pending = _migrations.Skip(inspection.AppliedPrefixLength).ToArray();
            var needsBackup = databaseExisted &&
                (!inspection.LedgerExists || pending.Any(migration => migration.RequiresBackup));
            if (needsBackup)
            {
                var backupPath = CreateBackupPath();
                var validation = CreateVerifiedBackup(backupPath);
                if (validation == null || !validation.IsValid)
                {
                    throw new InvalidDataException(
                        "Pre-migration SQLite backup did not pass integrity and foreign-key validation.");
                }

                backupFileName = Path.GetFileName(backupPath);
                SafeLog("verified pre-migration backup created: " + backupFileName);
            }

            if (!inspection.LedgerExists)
            {
                CreateLedgerAndBootstrap(inspection.AppliedPrefixLength, bootstrapped);
            }

            for (var index = inspection.AppliedPrefixLength; index < _migrations.Count; index++)
            {
                ApplyMigration(_migrations[index]);
                applied.Add(_migrations[index].MigrationId);
            }

            return new SchemaMigrationRunResult(
                databaseExisted,
                backupFileName,
                bootstrapped,
                applied,
                _migrations.Count == 0 ? string.Empty : _migrations[_migrations.Count - 1].MigrationId);
        }

        private DatabaseInspection InspectDatabase()
        {
            using (var connection = _factory.Open())
            {
                var detector = new LegacySchemaDetector(connection);
                if (!detector.TableExists(LedgerTable))
                {
                    if (SchemaMigrationRegistry.IsCanonicalRegistry(_migrations))
                    {
                        for (var index = _migrations.Count - 1; index >= 0; index--)
                        {
                            if (_migrations[index].RecognizesLedgerlessBaseline(detector))
                                return new DatabaseInspection(false, index + 1);
                        }
                    }

                    var satisfiedPrefix = 0;
                    foreach (var migration in _migrations)
                    {
                        if (!migration.IsSatisfied(detector))
                            break;
                        satisfiedPrefix++;
                    }

                    return new DatabaseInspection(false, satisfiedPrefix);
                }

                ValidateLedgerShape(detector);
                var rows = connection.Query<MigrationLedgerRow>(@"
SELECT
  migration_id AS MigrationId,
  checksum AS Checksum,
  description AS Description,
  applied_at AS AppliedAt,
  app_version AS AppVersion
FROM schema_migrations;").ToList();

                var registeredById = _migrations.ToDictionary(
                    migration => migration.MigrationId,
                    StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    if (string.IsNullOrWhiteSpace(row.MigrationId) ||
                        !registeredById.ContainsKey(row.MigrationId))
                    {
                        throw new InvalidOperationException(
                            "Database contains migration '" + (row.MigrationId ?? string.Empty) +
                            "' that is unknown to this application. Automatic downgrade is not supported.");
                    }
                }

                var appliedById = rows.ToDictionary(row => row.MigrationId, StringComparer.Ordinal);
                var prefixLength = 0;
                var gapFound = false;
                foreach (var migration in _migrations)
                {
                    if (!appliedById.TryGetValue(migration.MigrationId, out var row))
                    {
                        gapFound = true;
                        continue;
                    }

                    if (gapFound)
                    {
                        throw new InvalidOperationException(
                            "Migration ledger contains a gap before '" + migration.MigrationId +
                            "'. Skipped migrations are not supported.");
                    }
                    if (!string.Equals(row.Checksum, migration.Checksum, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "Checksum mismatch for applied migration '" + migration.MigrationId +
                            "'. Published migrations are immutable.");
                    }
                    if (!string.Equals(row.Description, migration.Description, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Description mismatch for applied migration '" + migration.MigrationId +
                            "'. Published migration metadata is immutable.");
                    }
                    if (string.IsNullOrWhiteSpace(row.AppliedAt))
                    {
                        throw new InvalidOperationException(
                            "Applied migration '" + migration.MigrationId + "' has no timestamp.");
                    }
                    prefixLength++;
                }

                return new DatabaseInspection(true, prefixLength);
            }
        }

        private void CreateLedgerAndBootstrap(int satisfiedPrefixLength, ICollection<string> bootstrapped)
        {
            using (var connection = _factory.Open())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                try
                {
                    connection.Execute(LedgerSchemaSql, transaction: transaction);
                    var detector = new LegacySchemaDetector(connection, transaction);
                    ValidateLedgerShape(detector);

                    var appliedAt = UtcNow().ToString("o");
                    for (var index = 0; index < satisfiedPrefixLength; index++)
                    {
                        ValidateLedgerShape(detector);
                        InsertLedgerRow(
                            connection,
                            transaction,
                            _migrations[index],
                            appliedAt,
                            null);
                        bootstrapped.Add(_migrations[index].MigrationId);
                    }

                    ValidateLedgerShape(detector);
                    transaction.Commit();
                }
                catch
                {
                    TryRollback(transaction);
                    throw;
                }
            }

            if (satisfiedPrefixLength > 0)
                SafeLog("legacy migration ledger bootstrapped: " + satisfiedPrefixLength);
        }

        private void ApplyMigration(SchemaMigration migration)
        {
            SafeLog("applying migration " + migration.MigrationId);
            using (var connection = _factory.Open())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                try
                {
                    var detector = new LegacySchemaDetector(connection, transaction);
                    ValidateLedgerShape(detector);
                    migration.Apply(connection, transaction);
                    ValidateLedgerShape(detector);
                    if (!migration.IsSatisfied(detector))
                    {
                        throw new InvalidDataException(
                            "Migration '" + migration.MigrationId +
                            "' completed without satisfying its declared schema and data invariants.");
                    }

                    ValidateDatabase(connection, transaction);
                    ValidateLedgerShape(detector);
                    InsertLedgerRow(
                        connection,
                        transaction,
                        migration,
                        UtcNow().ToString("o"),
                        ApplicationVersion());
                    ValidateLedgerShape(detector);
                    ValidateDatabase(connection, transaction);
                    transaction.Commit();
                }
                catch
                {
                    TryRollback(transaction);
                    throw;
                }
            }

            SafeLog("migration applied " + migration.MigrationId);
        }

        private void InsertLedgerRow(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SchemaMigration migration,
            string appliedAt,
            string appVersion)
        {
            connection.Execute(@"
INSERT INTO schema_migrations(
  migration_id,
  checksum,
  description,
  applied_at,
  app_version)
VALUES(
  @MigrationId,
  @Checksum,
  @Description,
  @AppliedAt,
  @AppVersion);",
                new
                {
                    migration.MigrationId,
                    migration.Checksum,
                    migration.Description,
                    AppliedAt = appliedAt,
                    AppVersion = appVersion
                },
                transaction);
        }

        private DatabaseValidationResult CreateVerifiedBackup(string destinationPath)
        {
            if (_options.CreateVerifiedBackup != null)
                return _options.CreateVerifiedBackup(destinationPath);

            return new SqliteOnlineBackup(_factory)
                .CreateVerifiedAsync(destinationPath)
                .GetAwaiter()
                .GetResult();
        }

        private string CreateBackupPath()
        {
            var directory = _options.BackupDirectory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(_factory.DbPath));
                if (string.IsNullOrWhiteSpace(databaseDirectory))
                    throw new InvalidOperationException("SQLite database directory is invalid.");
                directory = Path.Combine(databaseDirectory, "backups");
            }

            var fileName = "pos_pre_migration_" +
                UtcNow().UtcDateTime.ToString("yyyyMMdd_HHmmss_fff") + "_" +
                Guid.NewGuid().ToString("N").Substring(0, 8) + ".db";
            return Path.Combine(directory, fileName);
        }

        private string ApplicationVersion()
        {
            if (!string.IsNullOrWhiteSpace(_options.ApplicationVersion))
                return _options.ApplicationVersion.Trim();
            var version = typeof(SchemaMigrationRunner).Assembly.GetName().Version;
            return version == null ? string.Empty : version.ToString();
        }

        private DateTimeOffset UtcNow()
        {
            return _options.UtcNow == null
                ? DateTimeOffset.UtcNow
                : _options.UtcNow().ToUniversalTime();
        }

        private void SafeLog(string message)
        {
            if (_options.Log == null)
                return;
            try { _options.Log(message); } catch { }
        }

        private static void ValidateDatabase(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            var integrity = connection.Query<string>(
                    "PRAGMA integrity_check;",
                    transaction: transaction)
                .ToList();
            if (integrity.Count != 1 ||
                !string.Equals(integrity[0].Trim(), "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "SQLite integrity_check failed during the migration transaction.");
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "PRAGMA foreign_key_check;";
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        throw new InvalidDataException(
                            "SQLite foreign_key_check failed during the migration transaction.");
                    }
                }
            }
        }

        private static void ValidateLedgerShape(LegacySchemaDetector detector)
        {
            if (!detector.HasCanonicalTableDefinitions(LedgerSchemaSql, LedgerTable))
            {
                throw new InvalidDataException(
                    "SQLite migration ledger has an unsupported or unsafe shape.");
            }
        }

        private static void ValidateRegistry(IReadOnlyList<SchemaMigration> migrations)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var previousId = string.Empty;
            foreach (var migration in migrations)
            {
                if (migration == null)
                    throw new InvalidOperationException("Migration registry contains a null entry.");
                if (!ids.Add(migration.MigrationId))
                    throw new InvalidOperationException("Duplicate migration ID: " + migration.MigrationId);
                if (previousId.Length > 0 &&
                    string.CompareOrdinal(previousId, migration.MigrationId) >= 0)
                {
                    throw new InvalidOperationException(
                        "Migration registry must be strictly ordered and append-only.");
                }
                if (migration.Checksum.Length != 64)
                    throw new InvalidOperationException("Invalid migration checksum: " + migration.MigrationId);
                previousId = migration.MigrationId;
            }
        }

        private static void TryRollback(SqliteTransaction transaction)
        {
            try { transaction.Rollback(); } catch { }
        }

        private sealed class DatabaseInspection
        {
            public DatabaseInspection(bool ledgerExists, int appliedPrefixLength)
            {
                LedgerExists = ledgerExists;
                AppliedPrefixLength = appliedPrefixLength;
            }

            public int AppliedPrefixLength { get; }
            public bool LedgerExists { get; }
        }

        private sealed class MigrationLedgerRow
        {
            public string AppVersion { get; set; }
            public string AppliedAt { get; set; }
            public string Checksum { get; set; }
            public string Description { get; set; }
            public string MigrationId { get; set; }
        }
    }

    public sealed class SchemaMigrationRunnerOptions
    {
        public string ApplicationVersion { get; set; }
        public string BackupDirectory { get; set; }
        public Func<string, DatabaseValidationResult> CreateVerifiedBackup { get; set; }
        public Action<string> Log { get; set; }
        public Func<DateTimeOffset> UtcNow { get; set; }
    }

    public sealed class SchemaMigrationRunResult
    {
        internal SchemaMigrationRunResult(
            bool databaseExisted,
            string backupFileName,
            IList<string> bootstrappedMigrationIds,
            IList<string> appliedMigrationIds,
            string latestMigrationId)
        {
            DatabaseExisted = databaseExisted;
            BackupFileName = backupFileName ?? string.Empty;
            BootstrappedMigrationIds = new ReadOnlyCollection<string>(bootstrappedMigrationIds);
            AppliedMigrationIds = new ReadOnlyCollection<string>(appliedMigrationIds);
            LatestMigrationId = latestMigrationId ?? string.Empty;
        }

        public IReadOnlyList<string> AppliedMigrationIds { get; }
        public string BackupFileName { get; }
        public IReadOnlyList<string> BootstrappedMigrationIds { get; }
        public bool DatabaseExisted { get; }
        public string LatestMigrationId { get; }
        public bool WasNoOp =>
            AppliedMigrationIds.Count == 0 && BootstrappedMigrationIds.Count == 0;
    }
}
