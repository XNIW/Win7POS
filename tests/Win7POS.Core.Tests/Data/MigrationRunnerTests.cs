using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Migrations;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class MigrationRunnerTests
{
    private static readonly SchemaColumnDefinition ReceiptSnapshotDefinition =
        new("sales", "receipt_shop_snapshot", "TEXT", false, "", "TEXT NULL");

    [TestMethod]
    public void Registry_IsOrderedUniqueAndChecksummed()
    {
        var migrations = SchemaMigrationRegistry.All;
        var expectedChecksums = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["0001-core-pos-schema"] = "bd7f3e733cdf867b40816757687e34a654ceee39a2d60ea6923dda6cb98591c6",
            ["0002-supported-legacy-columns"] = "93008b229176205ed7c8d9c631739fb78e2166504012b4b9f277e1338d125d47",
            ["0003-outbox-catalog-evidence"] = "dbc5dae94d81d82fd9043020712471731cb34c1e1d961e00348fcc5cec29eacd",
            ["0004-shop-bound-outbox-backfill"] = "649f49fbe75acf86ecfd354269df305fcece6b81a21e45a5de224f2377992a66",
            ["0005-canonical-query-indexes"] = "44afcce1cee8d87f0d68f1de472c18f0b5fb6ca474ee94c592d43cf71234da1a",
            ["0006-system-role-permissions"] = "ade7405f309f563d6734bf5eaafd36df1f2ef6da8bd42ac9b910d1c51b783b8e",
            ["0007-receipt-shop-snapshot"] = "a1d12cca8bbfeb57872ee854e18cc32bf98258937d1f7be4be91d925f2ef6462",
            ["0008-online-sync-generation"] = "a951929521bdb7a73d82fcc308bd2e800ccb4888b6c16c829f51c2b93f49a488"
        };

        Assert.AreEqual(8, migrations.Count);
        Assert.AreEqual("0008-online-sync-generation", SchemaMigrationRegistry.Latest.MigrationId);
        CollectionAssert.AreEqual(
            migrations.Select(item => item.MigrationId).OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            migrations.Select(item => item.MigrationId).ToArray());
        Assert.AreEqual(
            migrations.Count,
            migrations.Select(item => item.MigrationId).Distinct(StringComparer.Ordinal).Count());

        foreach (var migration in migrations)
        {
            Assert.IsTrue(Regex.IsMatch(migration.MigrationId, "^[0-9]{4}-[a-z0-9-]+$"));
            Assert.IsTrue(Regex.IsMatch(migration.Checksum, "^[0-9a-f]{64}$"));
            Assert.AreEqual(expectedChecksums[migration.MigrationId], migration.Checksum);
            Assert.IsTrue(migration.RequiresBackup);
            Assert.IsFalse(string.IsNullOrWhiteSpace(migration.MinimumApplicationVersion));
            Assert.IsFalse(string.IsNullOrWhiteSpace(migration.RollbackCompatibility));
        }
    }

    [TestMethod]
    public void NewDatabase_AppliesAllMigrationsAndRepeatedReopenIsNoOp()
    {
        using var database = MigrationDatabase.Create();
        var backupCalls = 0;
        var runner = new SchemaMigrationRunner(
            database.Factory,
            SchemaMigrationRegistry.All,
            OptionsThatCountBackups(() => backupCalls++));

        var first = runner.Run();
        var timestamps = ReadLedgerTimestamps(database.Factory);
        var second = runner.Run();

        Assert.IsFalse(first.DatabaseExisted);
        Assert.AreEqual(0, first.BootstrappedMigrationIds.Count);
        Assert.AreEqual(SchemaMigrationRegistry.All.Count, first.AppliedMigrationIds.Count);
        Assert.AreEqual(SchemaMigrationRegistry.Latest.MigrationId, first.LatestMigrationId);
        Assert.IsFalse(first.WasNoOp);
        Assert.IsTrue(second.WasNoOp);
        Assert.AreEqual(0, backupCalls, "A new database or latest reopen must not create a pre-migration backup.");
        CollectionAssert.AreEqual(timestamps, ReadLedgerTimestamps(database.Factory));
        AssertLatestLedger(database.Factory);
    }

    [TestMethod]
    public void LedgerlessLatestDatabase_BootstrapsOnlyDetectedPrefixAndBacksUpFirst()
    {
        using var database = MigrationDatabase.Create();
        DbInitializer.EnsureCreated(database.Options);
        using (var connection = database.Factory.Open())
        {
            connection.Execute(@"
INSERT INTO sales(code, createdAt, total, paidCash, paidCard, change, receipt_shop_snapshot)
VALUES('SNAPSHOT-BASELINE', 1, 1000, 1000, 0, 0, '{""shop"":""QA""}');
DROP TABLE schema_migrations;");
        }

        var backupCalls = 0;
        var runner = new SchemaMigrationRunner(
            database.Factory,
            SchemaMigrationRegistry.All,
            OptionsThatCountBackups(() => backupCalls++));

        var result = runner.Run();

        Assert.AreEqual(1, backupCalls);
        Assert.AreEqual(SchemaMigrationRegistry.All.Count, result.BootstrappedMigrationIds.Count);
        Assert.AreEqual(0, result.AppliedMigrationIds.Count);
        AssertLatestLedger(database.Factory);
        using var verify = database.Factory.Open();
        Assert.AreEqual(
            0L,
            verify.ExecuteScalar<long>(
                "SELECT COUNT(1) FROM schema_migrations WHERE app_version IS NOT NULL;"),
            "Bootstrapped historical rows must remain distinguishable from applied migrations.");
        Assert.AreEqual(
            "{\"shop\":\"QA\"}",
            verify.ExecuteScalar<string>(@"
SELECT receipt_shop_snapshot
FROM sales
WHERE code = 'SNAPSHOT-BASELINE';"),
            "Ledger bootstrap must preserve the immutable receipt snapshot.");
    }

    [TestMethod]
    public void LedgerThrough0006_AppliesOnlyReceiptSnapshotMigrationAndBacksUpFirst()
    {
        using var database = MigrationDatabase.Create();
        new SchemaMigrationRunner(
            database.Factory,
            SchemaMigrationRegistry.All.Take(6)).Run();
        using (var connection = database.Factory.Open())
        {
            connection.Execute(@"
INSERT INTO sales(code, createdAt, total, paidCash, paidCard, change)
VALUES('PRE-0007', 2, 750, 750, 0, 0);");
        }

        var backupCalls = 0;
        var result = new SchemaMigrationRunner(
            database.Factory,
            SchemaMigrationRegistry.All,
            OptionsThatCountBackups(() => backupCalls++)).Run();

        Assert.AreEqual(1, backupCalls);
        Assert.AreEqual(0, result.BootstrappedMigrationIds.Count);
        CollectionAssert.AreEqual(
            new[]
            {
                "0007-receipt-shop-snapshot",
                "0008-online-sync-generation"
            },
            result.AppliedMigrationIds.ToArray());
        using var verify = database.Factory.Open();
        Assert.IsTrue(new LegacySchemaDetector(verify).ColumnMatchesDefinition(
            ReceiptSnapshotDefinition));
        Assert.AreEqual(
            "PRE-0007|750|",
            verify.ExecuteScalar<string>(@"
SELECT code || '|' || total || '|' || COALESCE(receipt_shop_snapshot, '')
FROM sales
WHERE code = 'PRE-0007';"));
        AssertLatestLedger(database.Factory);
    }

    [TestMethod]
    public void OnlineSyncGenerationMigration_ReleasesLegacyClaimsExactlyOnce()
    {
        using var database = MigrationDatabase.Create();
        new SchemaMigrationRunner(
            database.Factory,
            SchemaMigrationRegistry.All.Take(7)).Run();
        using (var connection = database.Factory.Open())
        {
            connection.Execute(@"
INSERT INTO sales(
  code, createdAt, total, paidCash, paidCard, change, client_sale_id, sync_status)
VALUES(
  'SYNC2-UPGRADE-SALE', 1, 1000, 1000, 0, 0, 'sale-upgrade', 'in_progress');
INSERT INTO sales_sync_outbox(
  sale_id, client_sale_id, client_batch_id, idempotency_key,
  origin_shop_id, origin_shop_code, payload_json, payload_hash,
  status, attempt_count, next_retry_at, last_attempt_at,
  created_at, updated_at)
VALUES(
  last_insert_rowid(), 'sale-upgrade', 'batch-upgrade', 'sale-idem-upgrade',
  'shop-upgrade', 'SHOP-UPGRADE', '{}', 'sale-hash-upgrade',
  'in_progress', 3, 999, 777, 1, 1);
INSERT INTO catalog_import_outbox(
  client_import_id, idempotency_key, origin_shop_id, origin_shop_code,
  payload_json, payload_hash, status, attempt_count,
  next_retry_at, last_attempt_at, created_at, updated_at)
VALUES(
  'import-upgrade', 'import-idem-upgrade', 'shop-upgrade', 'SHOP-UPGRADE',
  '{}', 'import-hash-upgrade', 'in_progress', 2,
  999, 888, 1, 1);");
        }

        var backupCalls = 0;
        var runner = new SchemaMigrationRunner(
            database.Factory,
            SchemaMigrationRegistry.All,
            OptionsThatCountBackups(() => backupCalls++));
        var first = runner.Run();
        var second = runner.Run();

        CollectionAssert.AreEqual(
            new[] { "0008-online-sync-generation" },
            first.AppliedMigrationIds.ToArray());
        Assert.IsTrue(second.WasNoOp);
        Assert.AreEqual(1, backupCalls);
        using var verify = database.Factory.Open();
        var salesOutbox = verify.QuerySingle<GenerationUpgradeOutboxProjection>(@"
SELECT
  status AS Status,
  attempt_count AS AttemptCount,
  next_retry_at AS NextRetryAt,
  last_attempt_at AS LastAttemptAt,
  last_error_code AS LastErrorCode,
  last_error_at AS LastErrorAt,
  claim_generation_id AS ClaimGenerationId,
  claim_token AS ClaimToken
FROM sales_sync_outbox
WHERE client_sale_id = 'sale-upgrade';");
        var catalogOutbox = verify.QuerySingle<GenerationUpgradeOutboxProjection>(@"
SELECT
  status AS Status,
  attempt_count AS AttemptCount,
  next_retry_at AS NextRetryAt,
  last_attempt_at AS LastAttemptAt,
  last_error_code AS LastErrorCode,
  last_error_at AS LastErrorAt,
  claim_generation_id AS ClaimGenerationId,
  claim_token AS ClaimToken
FROM catalog_import_outbox
WHERE client_import_id = 'import-upgrade';");

        AssertReleasedGenerationClaim(salesOutbox, expectedAttemptCount: 2);
        AssertReleasedGenerationClaim(catalogOutbox, expectedAttemptCount: 1);
        Assert.AreEqual(
            "retry",
            verify.ExecuteScalar<string>(@"
SELECT sync_status
FROM sales
WHERE client_sale_id = 'sale-upgrade';"));
    }

    [TestMethod]
    public void LedgerlessPostPr7WithUnknownColumn_FailsClosedWithoutLedgerRows()
    {
        using var database = MigrationDatabase.Create();
        DbInitializer.EnsureCreated(database.Options);
        using (var connection = database.Factory.Open())
        {
            connection.Execute(@"
DROP TABLE schema_migrations;
ALTER TABLE sales ADD COLUMN unknown_future_state TEXT NULL;");
        }

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new SchemaMigrationRunner(
                database.Factory,
                SchemaMigrationRegistry.All,
                OptionsThatCountBackups(() => { })).Run());

        using var verify = database.Factory.Open();
        Assert.AreEqual(
            0L,
            verify.ExecuteScalar<long>("SELECT COUNT(1) FROM schema_migrations;"));
    }

    [TestMethod]
    public void LedgerlessPostPr7WithMissingOwnershipBackfill_IsNotFalselyBootstrapped()
    {
        using var database = MigrationDatabase.Create();
        DbInitializer.EnsureCreated(database.Options);
        using (var connection = database.Factory.Open())
        {
            connection.Execute(@"
INSERT INTO remote_catalog_pending_prices(
  remote_price_id, remote_product_id, type, price, effective_at, source, created_at)
VALUES(
  'PRICE-UNOWNED', 'PRODUCT-UNOWNED', 'retail', 900,
  '2026-07-19T00:00:00Z', 'qa', '2026-07-19T00:00:00Z');
DROP TABLE schema_migrations;");
        }

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new SchemaMigrationRunner(
                database.Factory,
                SchemaMigrationRegistry.All,
                OptionsThatCountBackups(() => { })).Run());

        using var verify = database.Factory.Open();
        Assert.AreEqual(
            0L,
            verify.ExecuteScalar<long>("SELECT COUNT(1) FROM schema_migrations;"),
            "Unsatisfied 0003 ownership evidence must never be ledgered as historical.");
        Assert.AreEqual(
            0L,
            verify.ExecuteScalar<long>(
                "SELECT COUNT(1) FROM remote_catalog_price_ownership WHERE remote_price_id='PRICE-UNOWNED';"));
    }

    [TestMethod]
    public void AuthenticLatestLedgerWithoutReceiptSnapshotColumn_FailsCurrentSchemaValidation()
    {
        using var database = MigrationDatabase.Create();
        new SchemaMigrationRunner(
            database.Factory,
            SchemaMigrationRegistry.All.Take(6)).Run();
        var missingMigrations = SchemaMigrationRegistry.All.Skip(6).ToArray();
        using (var connection = database.Factory.Open())
        {
            connection.Execute(@"
DELETE FROM role_permissions
WHERE role_id = (SELECT id FROM roles WHERE code = 'cashier')
  AND permission_code = 'pos.sell';");
            foreach (var migration in missingMigrations)
            {
                connection.Execute(@"
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

        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            DbInitializer.EnsureCreated(database.Options));

        StringAssert.Contains(error.Message, "latest published migration state");
        using var verify = database.Factory.Open();
        Assert.IsFalse(new LegacySchemaDetector(verify).ColumnMatchesDefinition(
            ReceiptSnapshotDefinition));
        Assert.AreEqual(
            0L,
            verify.ExecuteScalar<long>(@"
SELECT COUNT(1)
FROM role_permissions
WHERE role_id = (SELECT id FROM roles WHERE code = 'cashier')
  AND permission_code = 'pos.sell';"),
            "Structural rejection must happen before mutable security reconciliation.");
    }

    [TestMethod]
    public void CanonicalBaselineRecognizer_CannotBootstrapUnsatisfiedCustomPredecessor()
    {
        using var database = MigrationDatabase.Create();
        DbInitializer.EnsureCreated(database.Options);
        using (var connection = database.Factory.Open())
            connection.Execute("DROP TABLE schema_migrations;");

        var probe = ProbeMigration(
            "custom-predecessor-v1",
            (connection, transaction) =>
                connection.Execute(
                    "CREATE TABLE custom_predecessor(id INTEGER PRIMARY KEY);",
                    transaction: transaction),
            "0000-custom-predecessor",
            "custom_predecessor");
        var result = new SchemaMigrationRunner(
            database.Factory,
            new[] { probe, SchemaMigrationRegistry.Latest },
            OptionsThatCountBackups(() => { })).Run();

        Assert.AreEqual(0, result.BootstrappedMigrationIds.Count);
        CollectionAssert.AreEqual(
            new[] { "0000-custom-predecessor", "0008-online-sync-generation" },
            result.AppliedMigrationIds.ToArray());
        using var verify = database.Factory.Open();
        Assert.IsTrue(new LegacySchemaDetector(verify).TableExists("custom_predecessor"));
    }

    [TestMethod]
    public void MalformedSchemaWithLedgerThrough0006_DoesNotCommit0007()
    {
        using var database = MigrationDatabase.Create();
        new SchemaMigrationRunner(
            database.Factory,
            SchemaMigrationRegistry.All.Take(6)).Run();
        using (var connection = database.Factory.Open())
            connection.Execute("ALTER TABLE sales ADD COLUMN unknown_future_state TEXT NULL;");

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new SchemaMigrationRunner(
                database.Factory,
                SchemaMigrationRegistry.All,
                OptionsThatCountBackups(() => { })).Run());

        using var verify = database.Factory.Open();
        Assert.AreEqual(
            6L,
            verify.ExecuteScalar<long>("SELECT COUNT(1) FROM schema_migrations;"));
        Assert.AreEqual(
            0L,
            verify.ExecuteScalar<long>(@"
SELECT COUNT(1)
FROM pragma_table_info('sales')
WHERE name = 'receipt_shop_snapshot';"),
            "0007 DDL and ledger row must roll back together on unsupported prior schema.");
    }

    [TestMethod]
    public async Task ConcurrentEnsureCreated_RegistersEveryMigrationExactlyOnce()
    {
        using var database = MigrationDatabase.Create();

        await Task.WhenAll(
            Task.Run(() => DbInitializer.EnsureCreated(database.Options)),
            Task.Run(() => DbInitializer.EnsureCreated(database.Options)));

        AssertLatestLedger(database.Factory);
        using var connection = database.Factory.Open();
        Assert.AreEqual(
            SchemaMigrationRegistry.All.Count,
            connection.ExecuteScalar<int>("SELECT COUNT(1) FROM schema_migrations;"));
        Assert.AreEqual(
            SchemaMigrationRegistry.All.Count,
            connection.ExecuteScalar<int>("SELECT COUNT(DISTINCT migration_id) FROM schema_migrations;"));
    }

    [TestMethod]
    public void FailureBeforeDdl_RollsBackAndLeavesMigrationPending()
    {
        AssertInjectedFailureRollsBack((connection, transaction) =>
            throw new InvalidOperationException("failure-before-ddl"));
    }

    [TestMethod]
    public void FailureAfterDdl_RollsBackAndLeavesMigrationPending()
    {
        AssertInjectedFailureRollsBack((connection, transaction) =>
        {
            connection.Execute("CREATE TABLE failure_probe(id INTEGER PRIMARY KEY);", transaction: transaction);
            throw new InvalidOperationException("failure-after-ddl");
        });
    }

    [TestMethod]
    public void FailureDuringBackfill_RollsBackSchemaDataAndLedger()
    {
        AssertInjectedFailureRollsBack((connection, transaction) =>
        {
            connection.Execute(@"
CREATE TABLE failure_probe(id INTEGER PRIMARY KEY, value TEXT NOT NULL);
INSERT INTO failure_probe(id, value) VALUES(1, 'backfill');",
                transaction: transaction);
            throw new InvalidOperationException("failure-during-backfill");
        });
    }

    [TestMethod]
    public void AppliedMigration_IsNeverAppliedTwice()
    {
        using var database = MigrationDatabase.Create();
        var applyCalls = 0;
        var firstMigration = ProbeMigration(
            "probe-definition-v1",
            (connection, transaction) =>
            {
                applyCalls++;
                connection.Execute("CREATE TABLE probe(id INTEGER PRIMARY KEY);", transaction: transaction);
            });
        new SchemaMigrationRunner(database.Factory, new[] { firstMigration }).Run();

        var samePublishedMigration = ProbeMigration(
            "probe-definition-v1",
            (connection, transaction) => throw new InvalidOperationException("must-not-run-twice"));
        var second = new SchemaMigrationRunner(database.Factory, new[] { samePublishedMigration }).Run();

        Assert.AreEqual(1, applyCalls);
        Assert.IsTrue(second.WasNoOp);
    }

    [TestMethod]
    public void ChecksumMismatch_BlocksStartupWithoutReapplyingMigration()
    {
        using var database = MigrationDatabase.Create();
        new SchemaMigrationRunner(
            database.Factory,
            new[]
            {
                ProbeMigration(
                    "probe-definition-v1",
                    (connection, transaction) =>
                        connection.Execute("CREATE TABLE probe(id INTEGER PRIMARY KEY);", transaction: transaction))
            }).Run();

        var changed = ProbeMigration(
            "probe-definition-v2-mutated",
            (connection, transaction) => throw new InvalidOperationException("must-not-apply"));
        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            new SchemaMigrationRunner(database.Factory, new[] { changed }).Run());

        StringAssert.Contains(error.Message, "Checksum mismatch");
        using var verify = database.Factory.Open();
        Assert.AreEqual(1L, verify.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='probe';"));
    }

    [TestMethod]
    public void LedgerGapAndUnknownMigration_BlockAutomaticDowngrade()
    {
        using var gapDatabase = MigrationDatabase.Create();
        var migrations = new[]
        {
            ProbeMigration("first-v1", (connection, transaction) =>
                connection.Execute("CREATE TABLE probe(id INTEGER PRIMARY KEY);", transaction: transaction), "0001-probe"),
            ProbeMigration("second-v1", (connection, transaction) =>
                connection.Execute("CREATE TABLE probe_second(id INTEGER PRIMARY KEY);", transaction: transaction), "0002-probe")
        };
        new SchemaMigrationRunner(gapDatabase.Factory, migrations).Run();
        using (var connection = gapDatabase.Factory.Open())
            connection.Execute("DELETE FROM schema_migrations WHERE migration_id='0001-probe';");
        var gap = Assert.ThrowsExactly<InvalidOperationException>(() =>
            new SchemaMigrationRunner(gapDatabase.Factory, migrations).Run());
        StringAssert.Contains(gap.Message, "gap");

        using var unknownDatabase = MigrationDatabase.Create();
        new SchemaMigrationRunner(unknownDatabase.Factory, new[] { migrations[0] }).Run();
        using (var connection = unknownDatabase.Factory.Open())
        {
            connection.Execute(@"
INSERT INTO schema_migrations(migration_id, checksum, description, applied_at, app_version)
VALUES('9999-future', @checksum, 'future', '2026-07-17T00:00:00.0000000+00:00', '99.0');",
                new { checksum = new string('a', 64) });
        }
        var unknown = Assert.ThrowsExactly<InvalidOperationException>(() =>
            new SchemaMigrationRunner(unknownDatabase.Factory, new[] { migrations[0] }).Run());
        StringAssert.Contains(unknown.Message, "Automatic downgrade is not supported");
    }

    [TestMethod]
    public void PartialHistoricalState_RegistersContiguousPrefixThenAppliesRemainder()
    {
        using var database = MigrationDatabase.Create();
        using (var connection = database.Factory.Open())
            connection.Execute("CREATE TABLE probe(id INTEGER PRIMARY KEY);");

        var migrations = new[]
        {
            ProbeMigration("first-v1", (connection, transaction) => { }, "0001-probe"),
            ProbeMigration(
                "second-v1",
                (connection, transaction) =>
                    connection.Execute("CREATE TABLE probe_second(id INTEGER PRIMARY KEY);", transaction: transaction),
                "0002-probe",
                "probe_second")
        };
        var backupCalls = 0;
        var result = new SchemaMigrationRunner(
            database.Factory,
            migrations,
            OptionsThatCountBackups(() => backupCalls++)).Run();

        CollectionAssert.AreEqual(new[] { "0001-probe" }, result.BootstrappedMigrationIds.ToArray());
        CollectionAssert.AreEqual(new[] { "0002-probe" }, result.AppliedMigrationIds.ToArray());
        Assert.AreEqual(1, backupCalls);
    }

    [TestMethod]
    public void SameNamedWrongIndexDefinition_IsNotLedgeredAsCanonical()
    {
        using var database = MigrationDatabase.Create();
        using (var connection = database.Factory.Open())
        {
            connection.Execute(@"
CREATE TABLE probe(id INTEGER PRIMARY KEY, value TEXT NOT NULL);
CREATE INDEX idx_probe_value ON probe(id);");
        }

        const string canonicalSql =
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_probe_value ON probe(value);";
        var migration = new SchemaMigration(
            "0001-probe-index",
            "Create exact probe index.",
            canonicalSql,
            "1.0.0",
            "Test-only additive migration.",
            true,
            (connection, transaction) => connection.Execute(canonicalSql, transaction: transaction),
            detector => detector.IndexMatchesDefinition(canonicalSql));

        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            new SchemaMigrationRunner(
                database.Factory,
                new[] { migration },
                OptionsThatCountBackups(() => { })).Run());

        StringAssert.Contains(error.Message, "declared schema and data invariants");
        using var verify = database.Factory.Open();
        Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM schema_migrations;"));
        StringAssert.Contains(
            verify.ExecuteScalar<string>(
                "SELECT sql FROM sqlite_master WHERE type='index' AND name='idx_probe_value';") ?? string.Empty,
            "probe(id)");
    }

    [TestMethod]
    public void CanonicalIndexBatch_IgnoresTrailingWhitespace()
    {
        using var database = MigrationDatabase.Create();
        using (var connection = database.Factory.Open())
        {
            connection.Execute(@"
CREATE TABLE probe(id INTEGER PRIMARY KEY, value TEXT NOT NULL);
CREATE INDEX idx_probe_value ON probe(value);");
            var detector = new LegacySchemaDetector(connection);
            Assert.IsTrue(detector.HasAllIndexDefinitions(@"
CREATE INDEX IF NOT EXISTS idx_probe_value ON probe(value);

"));
        }
    }

    [TestMethod]
    public void WrongColumnTypeNullabilityOrDefault_IsNotLedgeredAsCanonical()
    {
        using var database = MigrationDatabase.Create();
        using (var connection = database.Factory.Open())
        {
            connection.Execute(@"
CREATE TABLE probe(
  id INTEGER PRIMARY KEY,
  wrong_type INTEGER NOT NULL DEFAULT 'pending',
  wrong_null TEXT NULL DEFAULT 'pending',
  wrong_default TEXT NOT NULL DEFAULT 'wrong'
);");
        }

        var expected = new[]
        {
            new SchemaColumnDefinition(
                "probe", "wrong_type", "TEXT", true, "'pending'", "TEXT NOT NULL DEFAULT 'pending'"),
            new SchemaColumnDefinition(
                "probe", "wrong_null", "TEXT", true, "'pending'", "TEXT NOT NULL DEFAULT 'pending'"),
            new SchemaColumnDefinition(
                "probe", "wrong_default", "TEXT", true, "'pending'", "TEXT NOT NULL DEFAULT 'pending'")
        };
        using (var connection = database.Factory.Open())
        {
            var detector = new LegacySchemaDetector(connection);
            foreach (var definition in expected)
                Assert.IsFalse(detector.ColumnMatchesDefinition(definition));
        }
        var migration = new SchemaMigration(
            "0001-probe-column",
            "Validate exact probe column.",
            string.Join("\n", expected.Select(item => item.ToCanonicalMaterial())),
            "1.0.0",
            "Test-only additive migration.",
            true,
            (connection, transaction) => { },
            detector => detector.HasAllColumnDefinitions(expected));

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new SchemaMigrationRunner(
                database.Factory,
                new[] { migration },
                OptionsThatCountBackups(() => { })).Run());

        using var verify = database.Factory.Open();
        Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM schema_migrations;"));
    }

    [TestMethod]
    public void WrongPrimaryKeyOrUniqueConstraint_IsNotCanonical()
    {
        using var database = MigrationDatabase.Create();
        using (var connection = database.Factory.Open())
        {
            connection.Execute(@"
CREATE TABLE probe(
  id INTEGER NOT NULL,
  code TEXT NOT NULL
);");

            var detector = new LegacySchemaDetector(connection);
            Assert.IsFalse(detector.HasCanonicalTableDefinitions(@"
CREATE TABLE probe(
  id INTEGER PRIMARY KEY NOT NULL,
  code TEXT NOT NULL UNIQUE
);", "probe"));
        }
    }

    [TestMethod]
    public void UnknownColumnOrUniqueCollation_IsNotInKnownSchema()
    {
        using var database = MigrationDatabase.Create();
        using (var connection = database.Factory.Open())
        {
            connection.Execute(@"
CREATE TABLE probe(
  id INTEGER PRIMARY KEY NOT NULL,
  code TEXT COLLATE NOCASE NOT NULL UNIQUE,
  poison TEXT NOT NULL DEFAULT 'x'
);");

            var detector = new LegacySchemaDetector(connection);
            Assert.IsFalse(detector.HasKnownTableDefinitions(
                @"CREATE TABLE probe(
  id INTEGER PRIMARY KEY NOT NULL,
  code TEXT NOT NULL UNIQUE
);",
                @"CREATE TABLE probe(
  id INTEGER PRIMARY KEY NOT NULL,
  code TEXT NOT NULL UNIQUE,
  known_optional TEXT NULL
);",
                "probe"));
        }
    }

    [TestMethod]
    public void UnknownCheckTriggerForeignKeyOrIndex_IsNotInKnownSchema()
    {
        using var database = MigrationDatabase.Create();
        using (var connection = database.Factory.Open())
        {
            connection.Execute(@"
CREATE TABLE parent(code TEXT PRIMARY KEY NOT NULL);
CREATE TABLE probe(
  id INTEGER PRIMARY KEY NOT NULL,
  code TEXT NOT NULL UNIQUE REFERENCES parent(code) CHECK(length(code) > 0)
);
CREATE INDEX idx_probe_code ON probe(code);
CREATE TRIGGER trg_probe_abort
BEFORE INSERT ON probe
BEGIN
  SELECT RAISE(ABORT, 'blocked');
END;");

            const string canonical = @"
CREATE TABLE parent(code TEXT PRIMARY KEY NOT NULL);
CREATE TABLE probe(
  id INTEGER PRIMARY KEY NOT NULL,
  code TEXT NOT NULL UNIQUE
);";
            var detector = new LegacySchemaDetector(connection);
            Assert.IsFalse(detector.HasKnownTableDefinitions(
                canonical,
                canonical,
                "probe"));
        }
    }

    [TestMethod]
    public void CommentSeparatedCheckConstraint_IsNotInKnownSchema()
    {
        using var database = MigrationDatabase.Create();
        using (var connection = database.Factory.Open())
        {
            connection.Execute(@"
CREATE TABLE probe(
  id INTEGER PRIMARY KEY NOT NULL,
  code TEXT NOT NULL UNIQUE CHECK/**/(0)
);");

            const string canonical = @"
CREATE TABLE probe(
  id INTEGER PRIMARY KEY NOT NULL,
  code TEXT NOT NULL UNIQUE
);";
            var detector = new LegacySchemaDetector(connection);
            Assert.IsFalse(detector.HasKnownTableDefinitions(
                canonical,
                canonical,
                "probe"));
        }
    }

    private static void AssertInjectedFailureRollsBack(Action<SqliteConnection, SqliteTransaction> apply)
    {
        using var database = MigrationDatabase.Create();
        var migration = ProbeMigration("failure-probe-v1", apply);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new SchemaMigrationRunner(database.Factory, new[] { migration }).Run());

        using var verify = database.Factory.Open();
        Assert.AreEqual(0L, verify.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='failure_probe';"));
        Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM schema_migrations;"));
    }

    private static SchemaMigration ProbeMigration(
        string checksumMaterial,
        Action<SqliteConnection, SqliteTransaction> apply,
        string id = "0001-probe",
        string tableName = "probe")
    {
        return new SchemaMigration(
            id,
            "Probe migration",
            checksumMaterial,
            "1.0.0",
            "Test-only additive migration.",
            true,
            apply,
            detector => detector.TableExists(tableName));
    }

    private static SchemaMigrationRunnerOptions OptionsThatCountBackups(Action onBackup)
    {
        return new SchemaMigrationRunnerOptions
        {
            CreateVerifiedBackup = path =>
            {
                onBackup();
                return new DatabaseValidationResult
                {
                    IntegrityCheck = "ok",
                    ForeignKeyCheck = "ok"
                };
            }
        };
    }

    private static string[] ReadLedgerTimestamps(SqliteConnectionFactory factory)
    {
        using var connection = factory.Open();
        return connection.Query<string>(
                "SELECT applied_at FROM schema_migrations ORDER BY migration_id;")
            .ToArray();
    }

    private static void AssertReleasedGenerationClaim(
        GenerationUpgradeOutboxProjection row,
        int expectedAttemptCount)
    {
        Assert.AreEqual("retry", row.Status);
        Assert.AreEqual(expectedAttemptCount, row.AttemptCount);
        Assert.AreEqual(0L, row.NextRetryAt);
        Assert.IsNull(row.LastAttemptAt);
        Assert.AreEqual("session_generation_upgrade", row.LastErrorCode);
        Assert.IsTrue(row.LastErrorAt.HasValue && row.LastErrorAt.Value > 0);
        Assert.IsNull(row.ClaimGenerationId);
        Assert.IsNull(row.ClaimToken);
    }

    private static void AssertLatestLedger(SqliteConnectionFactory factory)
    {
        using var connection = factory.Open();
        var rows = connection.Query<LedgerProjection>(@"
SELECT migration_id AS MigrationId, checksum AS Checksum
FROM schema_migrations
ORDER BY migration_id;").ToArray();
        CollectionAssert.AreEqual(
            SchemaMigrationRegistry.All.Select(item => item.MigrationId).ToArray(),
            rows.Select(item => item.MigrationId).ToArray());
        CollectionAssert.AreEqual(
            SchemaMigrationRegistry.All.Select(item => item.Checksum).ToArray(),
            rows.Select(item => item.Checksum).ToArray());
    }

    private sealed class LedgerProjection
    {
        public string MigrationId { get; set; } = string.Empty;

        public string Checksum { get; set; } = string.Empty;
    }

    private sealed class GenerationUpgradeOutboxProjection
    {
        public int AttemptCount { get; set; }
        public string? ClaimGenerationId { get; set; }
        public string? ClaimToken { get; set; }
        public long? LastAttemptAt { get; set; }
        public string LastErrorCode { get; set; } = string.Empty;
        public long? LastErrorAt { get; set; }
        public long NextRetryAt { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    private sealed class MigrationDatabase : IDisposable
    {
        private MigrationDatabase(string root)
        {
            Root = root;
            Options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));
            Factory = new SqliteConnectionFactory(Options);
        }

        public SqliteConnectionFactory Factory { get; }
        public PosDbOptions Options { get; }
        private string Root { get; }

        public static MigrationDatabase Create()
        {
            SQLitePCL.Batteries_V2.Init();
            var root = Path.Combine(Path.GetTempPath(), "Win7POS.Migrations", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new MigrationDatabase(root);
        }

        public void Dispose()
        {
            SqliteConnectionFactory.ClearAllPools();
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
