using Dapper;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Backup;
using Win7POS.Data;
using Win7POS.Data.Backup;
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

[TestClass]
public sealed class BackupSchedulePolicyTests
{
    [TestMethod]
    public void Disabled_HasNoSlotOrDueBackup()
    {
        var clock = new FakeClock(At(2026, 10, 1, 12));
        var options = new BackupScheduleOptions();

        Assert.IsNull(BackupSchedulePolicy.GetLatestSlot(options, clock.LocalNow));
        Assert.IsNull(BackupSchedulePolicy.GetDueSlot(options, clock.LocalNow, null, null, startup: true));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("2:00")]
    [DataRow("02:0")]
    [DataRow("24:00")]
    [DataRow("23:60")]
    [DataRow("-1:00")]
    [DataRow("02:00 ")]
    [DataRow(" 02:00")]
    [DataRow("02:00:00")]
    [DataRow("02.00")]
    [DataRow("٠٢:٠٠")]
    public void LocalTime_RejectsNonInvariantOrOutOfRangeValues(string? value)
    {
        var options = Daily();
        options.LocalTime = value!;

        Assert.IsFalse(BackupSchedulePolicy.TryParseLocalTime(value!, out _));
        Assert.ThrowsExactly<ArgumentException>(() => BackupSchedulePolicy.Validate(options));
    }

    [TestMethod]
    [DataRow("00:00", 0, 0)]
    [DataRow("02:00", 2, 0)]
    [DataRow("23:59", 23, 59)]
    public void LocalTime_AcceptsExactBoundaries(string value, int hours, int minutes)
    {
        Assert.IsTrue(BackupSchedulePolicy.TryParseLocalTime(value, out var time));
        Assert.AreEqual(new TimeSpan(hours, minutes, 0), time);
    }

    [TestMethod]
    public void InvalidModeAndWeekday_AreRejected()
    {
        var options = Daily();
        options.Mode = (BackupScheduleMode)99;
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BackupSchedulePolicy.Validate(options));
        options.Mode = BackupScheduleMode.Weekly;
        options.WeeklyDay = (DayOfWeek)7;
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BackupSchedulePolicy.Validate(options));
        Assert.ThrowsExactly<ArgumentNullException>(() => BackupSchedulePolicy.Validate(null!));
    }

    [TestMethod]
    public void Daily_IsDueAtExactTimeAndReturnsOnlyLatestMissedSlot()
    {
        var clock = new FakeClock(At(2026, 10, 1, 1, 59));
        var options = Daily();

        Assert.AreEqual(At(2026, 9, 30, 2), BackupSchedulePolicy.GetLatestSlot(options, clock.LocalNow));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(At(2026, 10, 1, 2), Due(options, clock));
        clock.Advance(TimeSpan.FromDays(20));
        Assert.AreEqual(At(2026, 10, 21, 2), Due(options, clock));
    }

    [TestMethod]
    public void Weekly_UsesInvariantWeekdayAcrossWeekBoundary()
    {
        var options = new BackupScheduleOptions
        {
            Mode = BackupScheduleMode.Weekly,
            LocalTime = "03:30",
            WeeklyDay = DayOfWeek.Sunday
        };
        var clock = new FakeClock(At(2026, 10, 4, 3, 29));

        Assert.AreEqual(At(2026, 9, 27, 3, 30), Due(options, clock));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(At(2026, 10, 4, 3, 30), Due(options, clock));
        clock.Advance(TimeSpan.FromDays(6));
        Assert.AreEqual(At(2026, 10, 4, 3, 30), Due(options, clock));
    }

    [TestMethod]
    public void RestartCatchUp_OffersOneSlotAndDurableCompletionPreventsDuplicate()
    {
        var options = Daily();
        var clock = new FakeClock(At(2026, 10, 20, 14));
        var completed = At(2026, 10, 1, 2);

        var due = Due(options, clock, completed, startup: true);
        Assert.AreEqual(At(2026, 10, 20, 2), due);
        Assert.IsNull(Due(options, clock, due, startup: true));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.IsNull(Due(options, clock, due));
    }

    [TestMethod]
    public void FirstActivation_DoesNotBackfillBeforePolicyWasEnabled()
    {
        var options = Daily();
        var clock = new FakeClock(At(2026, 10, 1, 12));
        var activated = clock.LocalNow;

        Assert.IsNull(Due(options, clock, activatedAt: activated, startup: true));
        clock.Advance(TimeSpan.FromHours(14));
        Assert.AreEqual(At(2026, 10, 2, 2), Due(options, clock, activatedAt: activated));
    }

    [TestMethod]
    public void CatchUpDisabled_SkippedStartupSlotRemainsSkippedOnLaterTickAndRestart()
    {
        var options = Daily();
        options.CatchUpOnStartup = false;
        var clock = new FakeClock(At(2026, 10, 1, 12));

        Assert.IsNull(Due(options, clock, startup: true));
        var consideredThrough = BackupSchedulePolicy.GetLatestSlot(options, clock.LocalNow);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.IsNull(Due(options, clock, consideredThrough));
        Assert.IsNull(Due(options, clock, consideredThrough, startup: true));
        clock.LocalNow = At(2026, 10, 2, 2);
        Assert.AreEqual(clock.LocalNow, Due(options, clock, consideredThrough));
    }

    [TestMethod]
    public void CatchUpDisabled_ExactStartupSlotIsDue()
    {
        var options = Daily();
        options.CatchUpOnStartup = false;
        var clock = new FakeClock(At(2026, 10, 1, 2));

        Assert.AreEqual(clock.LocalNow, Due(options, clock, startup: true));
    }

    [TestMethod]
    public void ClockRollback_DoesNotRepeatCompletedOrOlderSlots()
    {
        var options = Daily();
        var completed = At(2026, 10, 10, 2);
        var clock = new FakeClock(At(2026, 10, 8, 14));

        Assert.IsNull(Due(options, clock, completed, startup: true));
        clock.LocalNow = At(2026, 10, 10, 2);
        Assert.IsNull(Due(options, clock, completed));
        clock.LocalNow = At(2026, 10, 11, 2);
        Assert.AreEqual(clock.LocalNow, Due(options, clock, completed));
    }

    [TestMethod]
    public void BackwardDstOrTimezoneChange_UsesOneWallClockSlotIdentity()
    {
        var options = Daily();
        options.LocalTime = "01:30";
        var clock = new FakeClock(At(2026, 11, 1, 1, 45));
        var completed = Due(options, clock);

        clock.LocalNow = At(2026, 11, 1, 1, 15);
        Assert.IsNull(Due(options, clock, completed));
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.IsNull(Due(options, clock, completed));
        Assert.AreEqual(DateTimeKind.Unspecified, completed!.Value.Kind);
    }

    [TestMethod]
    public void ForwardDstOrTimezoneChange_CatchesSkippedLocalTimeOnce()
    {
        var options = Daily();
        var clock = new FakeClock(At(2026, 3, 8, 1, 59));
        var completed = At(2026, 3, 7, 2);
        Assert.IsNull(Due(options, clock, completed));

        clock.LocalNow = At(2026, 3, 8, 3);
        var due = Due(options, clock, completed);
        Assert.AreEqual(At(2026, 3, 8, 2), due);
        Assert.IsNull(Due(options, clock, due));
    }

    [TestMethod]
    public void DateTimeKind_DoesNotConvertLocalWallClockAndMinimumDateDoesNotUnderflow()
    {
        var options = Daily();
        var now = DateTime.SpecifyKind(At(2026, 10, 1, 2), DateTimeKind.Utc);

        Assert.AreEqual(At(2026, 10, 1, 2), BackupSchedulePolicy.GetLatestSlot(options, now));
        Assert.IsNull(BackupSchedulePolicy.GetLatestSlot(options, DateTime.MinValue));
        options.Mode = BackupScheduleMode.Weekly;
        Assert.IsNull(BackupSchedulePolicy.GetLatestSlot(options, DateTime.MinValue));
    }

    private static BackupScheduleOptions Daily() => new BackupScheduleOptions
    {
        Mode = BackupScheduleMode.Daily,
        LocalTime = "02:00"
    };

    private static DateTime? Due(
        BackupScheduleOptions options,
        FakeClock clock,
        DateTime? consideredThrough = null,
        DateTime? activatedAt = null,
        bool startup = false) =>
        BackupSchedulePolicy.GetDueSlot(options, clock.LocalNow, consideredThrough, activatedAt, startup);

    private static DateTime At(int year, int month, int day, int hour, int minute = 0) =>
        new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);

    private sealed class FakeClock
    {
        public FakeClock(DateTime localNow) => LocalNow = localNow;
        public DateTime LocalNow { get; set; }
        public void Advance(TimeSpan elapsed) => LocalNow = LocalNow.Add(elapsed);
    }
}

[TestClass]
public sealed class BackupAutomationDestinationTests
{
    private const string DefaultDirectory = @"C:\Win7POS\backups";
    private const string LivePath = @"C:\Win7POS\pos.db";

    [TestMethod]
    public void Local_UsesApplicationDirectoryWithoutExaminingCustomDestination()
    {
        var options = new BackupAutomationOptions { DestinationPath = @"\\offline-server\unused-share" };

        Assert.AreEqual(Full(DefaultDirectory), Resolve(options));
    }

    [TestMethod]
    public void CustomLocal_PreservesSelectedAbsoluteDirectory()
    {
        var options = Destination("custom_local", @"D:\POS archives\daily");

        Assert.AreEqual(Full(options.DestinationPath), Resolve(options));
    }

    [TestMethod]
    [DataRow(@"\\offline-server\pos-archive")]
    [DataRow(@"\\offline-server\pos-archive\daily")]
    public void NetworkShare_ResolvesConfiguredUncWithoutLocalFallbackOrNetworkIo(string path)
    {
        var options = Destination("network_share", path);

        Assert.AreEqual(Full(path), Resolve(options));
        Assert.AreNotEqual(Full(DefaultDirectory), Resolve(options));
    }

    [TestMethod]
    [DataRow("custom_local", "")]
    [DataRow("custom_local", "backups")]
    [DataRow("custom_local", @"..\backups")]
    [DataRow("custom_local", @"C:backups")]
    [DataRow("custom_local", @"\backups")]
    [DataRow("custom_local", @"\\server\share")]
    [DataRow("network_share", @"C:\backups")]
    [DataRow("network_share", @"\\server")]
    [DataRow("network_share", @"\\server\")]
    [DataRow("network_share", "//server/share")]
    public void Destination_RejectsRelativeOrWrongKindPaths(string kind, string path)
    {
        Assert.ThrowsExactly<ArgumentException>(() => Resolve(Destination(kind, path)));
    }

    [TestMethod]
    [DataRow(@"C:\POS\..\backups")]
    [DataRow(@"C:\POS\.\backups")]
    [DataRow(@"C:\POS\backups.")]
    [DataRow(@"C:\POS \backups")]
    [DataRow(@"C:\PROGRA~1\backups")]
    [DataRow(@"C:\POS\backup:stream")]
    [DataRow(@"C:\POS\backup?name")]
    [DataRow(@"C:\POS\backup*name")]
    [DataRow(@"C:\POS\backup|name")]
    public void Destination_RejectsTraversalAliasesAndInvalidComponents(string path)
    {
        Assert.ThrowsExactly<ArgumentException>(() => Resolve(Destination("custom_local", path)));
    }

    [TestMethod]
    [DataRow("custom_local", @"\\?\C:\backups")]
    [DataRow("custom_local", @"\\.\C:\backups")]
    [DataRow("network_share", @"\\?\UNC\server\share")]
    [DataRow("network_share", @"\\.\UNC\server\share")]
    public void Destination_RejectsExtendedAndDevicePaths(string kind, string path)
    {
        Assert.ThrowsExactly<ArgumentException>(() => Resolve(Destination(kind, path)));
    }

    [TestMethod]
    [DataRow(@"\\user:password@server\share")]
    [DataRow(@"\\user:password\share")]
    [DataRow(@"\\server\share@password")]
    [DataRow("smb://user:password@server/share")]
    public void NetworkShare_RejectsCredentialSyntax(string path)
    {
        Assert.ThrowsExactly<ArgumentException>(() => Resolve(Destination("network_share", path)));
    }

    [TestMethod]
    public void Destination_RejectsLiveDatabaseAsDirectoryCaseInsensitively()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            Resolve(Destination("custom_local", @"c:\WIN7POS\POS.DB")));
    }

    [TestMethod]
    public void Destination_ReservesSpaceForWin7SnapshotAndTemporaryNames()
    {
        var tooLong = @"C:\" + new string('b', 133);
        Assert.ThrowsExactly<ArgumentException>(() => Resolve(Destination("custom_local", tooLong)));
        var allowed = @"C:\" + new string('b', 132);
        Assert.AreEqual(Full(allowed), Resolve(Destination("custom_local", allowed)));
    }

    [TestMethod]
    public void Destination_RejectsProgramFilesAndChildren()
    {
        var protectedDirectories = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432")
        }.Where(directory => !string.IsNullOrWhiteSpace(directory)).Distinct().ToArray();
        Assert.IsTrue(protectedDirectories.Length > 0, "Windows must expose its Program Files directories.");
        foreach (var directory in protectedDirectories)
        {
            Assert.ThrowsExactly<ArgumentException>(() => Resolve(Destination("custom_local", directory!)));
            Assert.ThrowsExactly<ArgumentException>(() =>
                Resolve(Destination("custom_local", Path.Combine(directory!, "POS archives"))));
        }
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(366)]
    public void Retention_RejectsOutOfRangeCount(int count)
    {
        var options = new BackupAutomationOptions { RetentionMaxCount = count };

        Assert.ThrowsExactly<ArgumentException>(() => Resolve(options));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(3651)]
    public void Retention_RejectsOutOfRangeAgeDays(int days)
    {
        var options = new BackupAutomationOptions { RetentionMaxAgeDays = days };

        Assert.ThrowsExactly<ArgumentException>(() => Resolve(options));
    }

    [TestMethod]
    [DataRow(3, 1)]
    [DataRow(365, 3650)]
    public void Retention_AcceptsExactBoundaries(int count, int days)
    {
        var options = new BackupAutomationOptions { RetentionMaxCount = count, RetentionMaxAgeDays = days };

        Assert.AreEqual(Full(DefaultDirectory), Resolve(options));
    }

    [TestMethod]
    public void InvalidOptions_ModeTimeWeekdayAndDestinationKindAreRejected()
    {
        var options = new BackupAutomationOptions { DestinationKind = "ftp" };
        Assert.ThrowsExactly<ArgumentException>(() => Resolve(options));
        options.DestinationKind = "local";
        options.Schedule.Mode = (BackupScheduleMode)99;
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Resolve(options));
        options.Schedule.Mode = BackupScheduleMode.Weekly;
        options.Schedule.LocalTime = "2:00";
        Assert.ThrowsExactly<ArgumentException>(() => Resolve(options));
        options.Schedule.LocalTime = "02:00";
        options.Schedule.WeeklyDay = (DayOfWeek)7;
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Resolve(options));
        options.Schedule = null!;
        Assert.ThrowsExactly<ArgumentException>(() => Resolve(options));
        Assert.ThrowsExactly<ArgumentException>(() => BackupAutomationDestination.Resolve(null!, DefaultDirectory, LivePath));
    }

    private static BackupAutomationOptions Destination(string kind, string path) =>
        new BackupAutomationOptions { DestinationKind = kind, DestinationPath = path };

    private static string Resolve(BackupAutomationOptions options) =>
        BackupAutomationDestination.Resolve(options, DefaultDirectory, LivePath);

    private static string Full(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
}
