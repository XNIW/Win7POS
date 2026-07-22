using Dapper;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Migrations;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class MigrationBackupRestoreTests
{
    [TestMethod]
    public void MigrationFailureLogSanitizer_RedactsWindowsPathsContainingSpaces()
    {
        var sanitizer = typeof(DbInitializer).GetMethod(
            "SanitizeLogMessage",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(sanitizer);

        var sanitized = sanitizer.Invoke(
            null,
            new object[] { @"backup failed: C:\Users\Fixture User\POS Data\legacy.db" }) as string;

        Assert.IsNotNull(sanitized);
        Assert.IsFalse(sanitized.Contains(@"C:\Users", StringComparison.Ordinal));
        Assert.IsFalse(sanitized.Contains("Fixture User", StringComparison.Ordinal));
        StringAssert.Contains(sanitized, "[path]");
    }

    [TestMethod]
    public async Task ExistingDatabase_CreatesVerifiedBackupBeforeLedgerAndLogsNoFullPath()
    {
        using var database = MigrationFiles.Create();
        WriteLegacyProbe(database.LivePath, "before-migration");
        var messages = new List<string>();
        var runner = CreateRunner(database, messages.Add);

        var result = runner.Run();

        Assert.IsTrue(result.DatabaseExisted);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.BackupFileName));
        Assert.AreEqual(Path.GetFileName(result.BackupFileName), result.BackupFileName);
        var backupPath = Path.Combine(database.BackupDirectory, result.BackupFileName);
        Assert.IsTrue(File.Exists(backupPath));
        Assert.IsFalse(TableExists(backupPath, "schema_migrations"),
            "The verified snapshot must be created before the migration ledger.");
        Assert.IsTrue(TableExists(database.LivePath, "schema_migrations"));
        Assert.AreEqual("before-migration", ReadLegacyProbe(backupPath));
        Assert.AreEqual("before-migration", ReadLegacyProbe(database.LivePath));
        await AssertDatabaseValidAsync(backupPath);
        await AssertDatabaseValidAsync(database.LivePath);

        Assert.IsTrue(messages.Any(message => message.Contains(result.BackupFileName, StringComparison.Ordinal)));
        Assert.IsFalse(messages.Any(message =>
                message.Contains(database.Root, StringComparison.OrdinalIgnoreCase) ||
                message.Contains(database.LivePath, StringComparison.OrdinalIgnoreCase)),
            "Migration logs must contain a backup filename, never a local absolute path.");
    }

    [TestMethod]
    public void BackupFailure_LeavesExistingDatabaseAndLedgerUntouched()
    {
        using var database = MigrationFiles.Create();
        WriteLegacyProbe(database.LivePath, "backup-failure");
        var factory = new SqliteConnectionFactory(PosDbOptions.ForPath(database.LivePath));
        var runner = new SchemaMigrationRunner(
            factory,
            SchemaMigrationRegistry.All,
            new SchemaMigrationRunnerOptions
            {
                BackupDirectory = database.BackupDirectory,
                CreateVerifiedBackup = _ => throw new IOException("synthetic backup failure")
            });

        var exception = Assert.ThrowsExactly<IOException>(() => runner.Run());

        StringAssert.Contains(exception.Message, "synthetic backup failure");
        Assert.IsFalse(TableExists(database.LivePath, "schema_migrations"));
        Assert.AreEqual("backup-failure", ReadLegacyProbe(database.LivePath));
    }

    [TestMethod]
    public void BackupIntegrityValidationFailure_LeavesLedgerUntouched()
    {
        using var database = MigrationFiles.Create();
        WriteLegacyProbe(database.LivePath, "invalid-backup-result");
        var factory = new SqliteConnectionFactory(PosDbOptions.ForPath(database.LivePath));
        var runner = new SchemaMigrationRunner(
            factory,
            SchemaMigrationRegistry.All,
            new SchemaMigrationRunnerOptions
            {
                BackupDirectory = database.BackupDirectory,
                CreateVerifiedBackup = _ => new DatabaseValidationResult
                {
                    IntegrityCheck = "synthetic-corruption",
                    ForeignKeyCheck = "ok"
                }
            });

        var error = Assert.ThrowsExactly<InvalidDataException>(() => runner.Run());

        StringAssert.Contains(error.Message, "did not pass integrity and foreign-key validation");
        Assert.IsFalse(TableExists(database.LivePath, "schema_migrations"));
        Assert.AreEqual("invalid-backup-result", ReadLegacyProbe(database.LivePath));
    }

    [TestMethod]
    public void InvalidForeignKeySource_IsRejectedBeforeLedgerMutation()
    {
        using var database = MigrationFiles.Create();
        WriteForeignKeyViolation(database.LivePath);
        var runner = CreateRunner(database);

        Assert.ThrowsExactly<InvalidDataException>(() => runner.Run());

        Assert.IsFalse(TableExists(database.LivePath, "schema_migrations"));
        using var connection = Open(database.LivePath, foreignKeys: false);
        Assert.AreEqual(1L, connection.ExecuteScalar<long>("SELECT COUNT(1) FROM legacy_child;"));
        Assert.AreEqual(0, Directory.Exists(database.BackupDirectory)
            ? Directory.GetFiles(database.BackupDirectory, "*.db").Length
            : 0);
    }

    [TestMethod]
    public async Task VerifiedPreMigrationBackup_CanBeRestoredAndUpgradedToLatest()
    {
        using var database = MigrationFiles.Create();
        WriteLegacyProbe(database.LivePath, "preserved-before-migration");
        var firstRun = CreateRunner(database).Run();
        var preMigrationBackup = Path.Combine(database.BackupDirectory, firstRun.BackupFileName);
        Assert.IsTrue(File.Exists(preMigrationBackup));
        Assert.IsFalse(TableExists(preMigrationBackup, "schema_migrations"));

        using (var connection = Open(database.LivePath))
        {
            connection.Execute(
                "UPDATE legacy_probe SET value='changed-after-migration' WHERE id=1;");
        }
        SqliteConnectionFactory.ClearAllPools();
        File.Copy(database.LivePath, database.DeclaredRollbackPath);

        await new AtomicRestoreInstaller().InstallAsync(
            preMigrationBackup,
            database.LivePath,
            database.DeclaredRollbackPath,
            () =>
            {
                DbInitializer.EnsureCreated(PosDbOptions.ForPath(database.LivePath));
                return Task.CompletedTask;
            });

        Assert.AreEqual("preserved-before-migration", ReadLegacyProbe(database.LivePath));
        AssertLatestLedger(database.LivePath);
        AssertAuthoritativeStageSchema(database.LivePath);
        await AssertDatabaseValidAsync(database.LivePath);
        Assert.IsFalse(File.Exists(database.LivePath + ".restore-in-progress"));
    }

    [TestMethod]
    public async Task RestoreWithAuthenticLatestLedgerButMissingReceiptColumn_RollsBackLiveDatabase()
    {
        using var database = MigrationFiles.Create();
        DbInitializer.EnsureCreated(PosDbOptions.ForPath(database.LivePath));
        using (var live = Open(database.LivePath))
        {
            live.Execute(@"
INSERT INTO sales(code, createdAt, total, paidCash, paidCard, change, receipt_shop_snapshot)
VALUES('LIVE-SNAPSHOT', 3, 1200, 1200, 0, 0, '{""shop"":""live""}');");
        }

        SqliteConnectionFactory.ClearAllPools();
        File.Copy(database.LivePath, database.DeclaredRollbackPath);

        var candidateFactory = new SqliteConnectionFactory(
            PosDbOptions.ForPath(database.CandidatePath));
        new SchemaMigrationRunner(
            candidateFactory,
            SchemaMigrationRegistry.All.Take(6)).Run();
        var missingMigrations = SchemaMigrationRegistry.All.Skip(6).ToArray();
        using (var candidate = candidateFactory.Open())
        {
            foreach (var migration in missingMigrations)
            {
                candidate.Execute(@"
INSERT INTO schema_migrations(
  migration_id, checksum, description, applied_at, app_version)
VALUES(
  @MigrationId, @Checksum, @Description,
  '2026-07-19T00:00:00.0000000+00:00', '1.0.0');",
                    new
                    {
                        migration.MigrationId,
                        migration.Checksum,
                        migration.Description
                    });
            }
        }
        SqliteConnectionFactory.ClearAllPools();

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new AtomicRestoreInstaller().InstallAsync(
                database.CandidatePath,
                database.LivePath,
                database.DeclaredRollbackPath,
                () =>
                {
                    DbInitializer.EnsureCreated(PosDbOptions.ForPath(database.LivePath));
                    return Task.CompletedTask;
                }));

        StringAssert.Contains(error.Message, "latest published migration state");
        using var verify = Open(database.LivePath);
        Assert.AreEqual(
            "{\"shop\":\"live\"}",
            verify.ExecuteScalar<string>(@"
SELECT receipt_shop_snapshot
FROM sales
WHERE code = 'LIVE-SNAPSHOT';"));
        AssertLatestLedger(database.LivePath);
        AssertAuthoritativeStageSchema(database.LivePath);
        Assert.IsFalse(File.Exists(database.LivePath + ".restore-in-progress"));
        await AssertDatabaseValidAsync(database.LivePath);
    }

    private static SchemaMigrationRunner CreateRunner(
        MigrationFiles database,
        Action<string>? log = null)
    {
        var factory = new SqliteConnectionFactory(PosDbOptions.ForPath(database.LivePath));
        return new SchemaMigrationRunner(
            factory,
            SchemaMigrationRegistry.All,
            new SchemaMigrationRunnerOptions
            {
                ApplicationVersion = "migration-tests",
                BackupDirectory = database.BackupDirectory,
                Log = log,
                UtcNow = () => new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero)
            });
    }

    private static void AssertLatestLedger(string databasePath)
    {
        using var connection = Open(databasePath);
        CollectionAssert.AreEqual(
            SchemaMigrationRegistry.All.Select(item => item.MigrationId).ToArray(),
            connection.Query<string>(@"
SELECT migration_id
FROM schema_migrations
ORDER BY migration_id;").ToArray());
    }

    private static void AssertAuthoritativeStageSchema(string databasePath)
    {
        using var connection = Open(databasePath);
        var detector = new LegacySchemaDetector(connection);
        Assert.IsTrue(detector.HasCanonicalTableDefinitions(
            DbInitializer.CatalogAuthoritativeIdStageSchemaSql,
            "catalog_authoritative_stage_scope",
            "catalog_authoritative_id_stage"));
        Assert.IsTrue(detector.IndexMatchesDefinition(@"
CREATE UNIQUE INDEX IF NOT EXISTS idx_catalog_authoritative_stage_page_identity
ON catalog_authoritative_id_stage(
  scope_id,
  page_number,
  entity_kind,
  remote_id,
  content_fingerprint,
  category_remote_id,
  supplier_remote_id,
  product_remote_id
);"));
        Assert.IsTrue(detector.IndexMatchesDefinition(@"
CREATE UNIQUE INDEX IF NOT EXISTS idx_catalog_authoritative_stage_scope_identity
ON catalog_authoritative_stage_scope(
  shop_id,
  shop_code,
  transition_epoch,
  generation_id,
  generation_fingerprint,
  full_run_id
);"));
        Assert.IsTrue(detector.IndexMatchesDefinition(@"
CREATE INDEX IF NOT EXISTS idx_catalog_authoritative_stage_scope_cleanup
ON catalog_authoritative_stage_scope(shop_id, shop_code, full_run_id, scope_id);"));
        Assert.IsTrue(detector.IndexMatchesDefinition(@"
CREATE INDEX IF NOT EXISTS idx_catalog_authoritative_stage_reconcile
ON catalog_authoritative_id_stage(
  scope_id,
  entity_kind,
  remote_id
);"));
        Assert.IsTrue(detector.IndexMatchesDefinition(@"
CREATE INDEX IF NOT EXISTS idx_catalog_authoritative_stage_cleanup
ON catalog_authoritative_id_stage(scope_id, stage_id);"));
    }

    private static async Task AssertDatabaseValidAsync(string databasePath)
    {
        var factory = new SqliteConnectionFactory(PosDbOptions.ForPath(databasePath));
        var validation = await new DbMaintenanceRepository(factory).ValidateAsync();
        Assert.IsTrue(
            validation.IsValid,
            "Invalid database: integrity=" + validation.IntegrityCheck +
            " foreignKeys=" + validation.ForeignKeyCheck);
    }

    private static bool TableExists(string databasePath, string table)
    {
        using var connection = Open(databasePath, foreignKeys: false);
        return connection.ExecuteScalar<long>(@"
SELECT COUNT(1)
FROM sqlite_master
WHERE type='table' AND name=@table;", new { table }) == 1;
    }

    private static string ReadLegacyProbe(string databasePath)
    {
        using var connection = Open(databasePath);
        return connection.ExecuteScalar<string>("SELECT value FROM legacy_probe WHERE id=1;") ?? string.Empty;
    }

    private static void WriteLegacyProbe(string databasePath, string value)
    {
        using var connection = Open(databasePath);
        connection.Execute(@"
CREATE TABLE legacy_probe(id INTEGER PRIMARY KEY, value TEXT NOT NULL);
INSERT INTO legacy_probe(id, value) VALUES(1, @value);", new { value });
    }

    private static void WriteForeignKeyViolation(string databasePath)
    {
        using var connection = Open(databasePath, foreignKeys: false);
        connection.Execute(@"
CREATE TABLE legacy_parent(id INTEGER PRIMARY KEY);
CREATE TABLE legacy_child(
  id INTEGER PRIMARY KEY,
  parent_id INTEGER NOT NULL REFERENCES legacy_parent(id));
INSERT INTO legacy_child(id, parent_id) VALUES(1, 999);");
    }

    private static SqliteConnection Open(string databasePath, bool foreignKeys = true)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = foreignKeys,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class MigrationFiles : IDisposable
    {
        private MigrationFiles(string root)
        {
            Root = root;
            LivePath = Path.Combine(root, "live.db");
            CandidatePath = Path.Combine(root, "candidate.db");
            BackupDirectory = Path.Combine(root, "backups");
            DeclaredRollbackPath = Path.Combine(root, "declared-rollback.db");
        }

        public string BackupDirectory { get; }
        public string CandidatePath { get; }
        public string DeclaredRollbackPath { get; }
        public string LivePath { get; }
        public string Root { get; }

        public static MigrationFiles Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "Win7POS.MigrationBackupRestore",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new MigrationFiles(root);
        }

        public void Dispose()
        {
            SqliteConnectionFactory.ClearAllPools();
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
