using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Migrations;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class LegacyMigrationFixtureTests
{
    private static readonly string[] CanonicalIndexes =
    {
        "idx_sale_lines_saleId",
        "idx_sale_lines_barcode",
        "idx_sales_createdAt",
        "idx_sales_client_sale_id",
        "idx_sales_client_sale_id_unique",
        "idx_sales_sync_status",
        "idx_local_stock_movements_sale",
        "idx_local_stock_movements_barcode",
        "idx_sales_sync_outbox_status_next",
        "idx_sales_sync_outbox_sale",
        "idx_sales_sync_outbox_last_attempt",
        "idx_catalog_import_outbox_client_import",
        "idx_catalog_import_outbox_idempotency",
        "idx_catalog_import_outbox_status_next",
        "idx_catalog_import_outbox_last_attempt",
        "idx_audit_log_ts",
        "idx_price_history_unique",
        "idx_price_history_remote_price_id",
        "idx_price_history_catalog_import_item",
        "idx_pending_remote_price_id",
        "idx_pending_remote_price_fallback",
        "idx_pending_remote_price_product",
        "idx_remote_price_ownership_product",
        "idx_remote_price_quarantine_remote_id",
        "idx_remote_product_refs_category",
        "idx_remote_product_refs_supplier",
        "idx_held_cart_lines_holdId",
        "idx_security_events_ts",
        "idx_products_remote_product_id",
        "idx_products_active_barcode",
        "idx_products_active_remote_product_id",
        "idx_categories_remote_category_id",
        "idx_categories_active_name",
        "idx_suppliers_remote_supplier_id",
        "idx_suppliers_active_name",
        "idx_users_remote_staff_id",
        "idx_users_remote_shop_staff"
    };

    [TestMethod]
    [DataRow("legacy_initial_minimal.sql")]
    [DataRow("legacy_pre_refund_void.sql")]
    [DataRow("legacy_pre_outbox.sql")]
    [DataRow("legacy_pre_shop_binding.sql")]
    [DataRow("legacy_pre_catalog_exactness.sql")]
    [DataRow("legacy_current_main_unversioned.sql")]
    [DataRow("legacy_post_pr7_unversioned.sql")]
    public async Task SanitizedLegacyFixture_UpgradesToLatestWithoutDataLossAndReopensAsNoOp(
        string fixtureFileName)
    {
        using var database = FixtureDatabase.Create();
        ExecuteFixture(database.LivePath, fixtureFileName);
        var probesBefore = ReadFixtureProbes(database.LivePath);
        var domainEvidenceBefore = ReadFixtureDomainEvidence(database.LivePath);
        var immutableOutboxBefore = ReadImmutableOutboxEvidence(database.LivePath);

        DbInitializer.EnsureCreated(PosDbOptions.ForPath(database.FreshPath));
        var expectedSchema = ReadSemanticSchema(database.FreshPath);

        DbInitializer.EnsureCreated(PosDbOptions.ForPath(database.LivePath));

        await AssertDatabaseValidAsync(database.LivePath);
        AssertLatestLedger(database.LivePath);
        CollectionAssert.AreEquivalent(
            probesBefore,
            ReadFixtureProbes(database.LivePath),
            "Fixture evidence changed during migration.");
        CollectionAssert.AreEqual(
            immutableOutboxBefore,
            ReadImmutableOutboxEvidence(database.LivePath),
            "Migration changed immutable outbox payload or hash evidence.");
        var domainEvidenceAfter = ReadFixtureDomainEvidence(database.LivePath)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var evidence in domainEvidenceBefore)
        {
            Assert.IsTrue(
                domainEvidenceAfter.Contains(evidence),
                "Migration lost or changed fixture domain evidence: " + evidence);
        }
        CollectionAssert.AreEqual(
            expectedSchema,
            ReadSemanticSchema(database.LivePath),
            "Upgraded schema differs from a freshly initialized database.");
        AssertCanonicalIndexes(database.LivePath, database.FreshPath);
        AssertCanonicalForeignKeys(database.LivePath);

        var backups = Directory.GetFiles(database.BackupDirectory, "pos_pre_migration_*.db");
        Assert.HasCount(1, backups, "An existing unversioned database requires one verified backup.");
        Assert.IsFalse(TableExists(backups[0], "schema_migrations"),
            "The pre-migration backup must precede every ledger mutation.");
        await AssertDatabaseValidAsync(backups[0]);

        if (string.Equals(fixtureFileName, "legacy_pre_shop_binding.sql", StringComparison.Ordinal))
            AssertAmbiguousLegacyOutboxIsBlocked(database.LivePath);
        if (string.Equals(fixtureFileName, "legacy_current_main_unversioned.sql", StringComparison.Ordinal))
            AssertPrePr7MainAppliedOnlyReceiptSnapshot(database.LivePath);
        if (string.Equals(fixtureFileName, "legacy_post_pr7_unversioned.sql", StringComparison.Ordinal))
            AssertPostPr7MainWasBootstrappedWithoutReapplying(database.LivePath);

        var ledgerBefore = ReadLedgerTimestamps(database.LivePath);
        var blockedOutboxBefore = ReadBlockedOutboxTimestamps(database.LivePath);
        DbInitializer.EnsureCreated(PosDbOptions.ForPath(database.LivePath));

        CollectionAssert.AreEqual(
            ledgerBefore,
            ReadLedgerTimestamps(database.LivePath),
            "A reopen must not rewrite applied migration rows.");
        Assert.HasCount(
            1,
            Directory.GetFiles(database.BackupDirectory, "pos_pre_migration_*.db"),
            "A no-op reopen must not create another backup.");
        CollectionAssert.AreEquivalent(probesBefore, ReadFixtureProbes(database.LivePath));
        CollectionAssert.AreEqual(
            blockedOutboxBefore,
            ReadBlockedOutboxTimestamps(database.LivePath),
            "A no-op reopen must not churn canonical blocked-outbox timestamps.");
    }

    private static void AssertLatestLedger(string databasePath)
    {
        using var connection = Open(databasePath);
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

    private static void AssertPrePr7MainAppliedOnlyReceiptSnapshot(string databasePath)
    {
        using var connection = Open(databasePath);
        var appliedIds = connection.Query<string>(@"
SELECT migration_id
FROM schema_migrations
WHERE app_version IS NOT NULL
ORDER BY migration_id;").ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "0007-receipt-shop-snapshot",
                "0008-online-sync-generation"
            },
            appliedIds,
            "The historical pre-PR7 schema must bootstrap 0001-0006 and apply 0007-0008.");
    }

    private static void AssertPostPr7MainWasBootstrappedWithoutReapplying(string databasePath)
    {
        using var connection = Open(databasePath);
        var rowsWithApplicationVersion = connection.ExecuteScalar<long>(@"
SELECT COUNT(1)
FROM schema_migrations
WHERE app_version IS NOT NULL;");
        Assert.AreEqual(1L, rowsWithApplicationVersion,
            "The exact post-PR7 schema must bootstrap through 0007 and apply only 0008.");
        Assert.AreEqual(
            "0008-online-sync-generation",
            connection.ExecuteScalar<string>(@"
SELECT migration_id
FROM schema_migrations
WHERE app_version IS NOT NULL;"));
        Assert.AreEqual(
            "{\"shopName\":\"Negozio QA Ñ\",\"address\":\"Via Unicode 7\"}",
            connection.ExecuteScalar<string>(@"
SELECT receipt_shop_snapshot
FROM sales
WHERE code = 'POST-PR7-SNAPSHOT';"),
            "The immutable Unicode receipt snapshot must survive ledger bootstrap unchanged.");
    }

    private static void AssertAmbiguousLegacyOutboxIsBlocked(string databasePath)
    {
        using var connection = Open(databasePath);
        Assert.AreEqual(
            "failed_blocked|legacy_origin_ambiguous",
            connection.ExecuteScalar<string>(@"
SELECT status || '|' || last_error_code
FROM sales_sync_outbox
WHERE id = 1;"));
        Assert.AreEqual(
            "failed_blocked|legacy_origin_ambiguous",
            connection.ExecuteScalar<string>(@"
SELECT status || '|' || last_error_code
FROM catalog_import_outbox
WHERE id = 1;"));
        Assert.AreEqual(
            "failed_blocked|legacy_contract_mismatch",
            connection.ExecuteScalar<string>(@"
SELECT status || '|' || last_error_code
FROM catalog_import_outbox
WHERE id = 2;"));
    }

    private static void AssertCanonicalForeignKeys(string databasePath)
    {
        using var connection = Open(databasePath);
        AssertForeignKey(connection, "sale_lines", "saleId", "sales", "id", "CASCADE");
        AssertForeignKey(connection, "held_cart_lines", "holdId", "held_carts", "holdId", "CASCADE");
        AssertForeignKey(connection, "users", "role_id", "roles", "id", "NO ACTION");
        AssertForeignKey(connection, "role_permissions", "role_id", "roles", "id", "CASCADE");
        AssertForeignKey(connection, "sales_sync_outbox", "sale_id", "sales", "id", "CASCADE");
    }

    private static void AssertForeignKey(
        SqliteConnection connection,
        string table,
        string from,
        string parent,
        string to,
        string onDelete)
    {
        var rows = ReadForeignKeys(connection, table);
        Assert.IsTrue(rows.Any(row =>
                string.Equals(row.From, from, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.Table, parent, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.To, to, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.OnDelete, onDelete, StringComparison.OrdinalIgnoreCase)),
            "Missing canonical foreign key on " + table + "." + from + ".");
    }

    private static void AssertCanonicalIndexes(string databasePath, string freshDatabasePath)
    {
        using var connection = Open(databasePath);
        using var fresh = Open(freshDatabasePath);
        var actual = connection.Query<IndexProjection>(@"
SELECT name AS Name, sql AS Sql
FROM sqlite_master
WHERE type = 'index' AND sql IS NOT NULL;")
            .ToDictionary(item => item.Name, StringComparer.Ordinal);
        var expected = fresh.Query<IndexProjection>(@"
SELECT name AS Name, sql AS Sql
FROM sqlite_master
WHERE type = 'index' AND sql IS NOT NULL;")
            .ToDictionary(item => item.Name, StringComparer.Ordinal);
        foreach (var index in CanonicalIndexes)
        {
            Assert.IsTrue(actual.TryGetValue(index, out var actualIndex),
                "Missing canonical index: " + index);
            Assert.IsTrue(expected.TryGetValue(index, out var expectedIndex),
                "Fresh database is missing canonical index: " + index);
            Assert.AreEqual(
                NormalizeIndexSql(expectedIndex.Sql),
                NormalizeIndexSql(actualIndex.Sql),
                "Canonical index definition differs: " + index);
        }
    }

    private static string NormalizeIndexSql(string? value)
    {
        var withoutGuard = Regex.Replace(
            value ?? string.Empty,
            @"\bIF\s+NOT\s+EXISTS\b",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(
                withoutGuard.Trim().TrimEnd(';'),
                @"\s+",
                string.Empty,
                RegexOptions.CultureInvariant)
            .ToLowerInvariant();
    }

    private static async Task AssertDatabaseValidAsync(string path)
    {
        var factory = new SqliteConnectionFactory(PosDbOptions.ForPath(path));
        var result = await new DbMaintenanceRepository(factory).ValidateAsync();
        Assert.IsTrue(
            result.IsValid,
            "Database validation failed: integrity=" + result.IntegrityCheck +
            " foreignKeys=" + result.ForeignKeyCheck);
    }

    private static string[] ReadSemanticSchema(string databasePath)
    {
        using var connection = Open(databasePath);
        var tables = connection.Query<string>(@"
SELECT name
FROM sqlite_master
WHERE type = 'table'
  AND name NOT LIKE 'sqlite_%'
  AND name NOT IN ('schema_migrations', 'fixture_probe')
ORDER BY name;").ToArray();
        var result = new List<string>();
        foreach (var table in tables)
        {
            result.Add("table|" + table);
            foreach (var column in connection.Query<ColumnProjection>(@"
SELECT
  name AS Name,
  type AS Type,
  ""notnull"" AS IsNotNullValue,
  dflt_value AS DefaultValue,
  pk AS PrimaryKey
FROM pragma_table_info(@table);", new { table })
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(string.Join("|", new[]
                {
                    "column",
                    table,
                    column.Name ?? string.Empty,
                    (column.Type ?? string.Empty).ToUpperInvariant(),
                    column.IsNotNullValue.ToString(),
                    NormalizeDefault(column.DefaultValue),
                    column.PrimaryKey.ToString()
                }));
            }

            foreach (var foreignKey in ReadForeignKeys(connection, table)
                     .OrderBy(item => item.Id)
                     .ThenBy(item => item.Sequence))
            {
                result.Add(string.Join("|", new[]
                {
                    "fk",
                    table,
                    foreignKey.From ?? string.Empty,
                    foreignKey.Table ?? string.Empty,
                    foreignKey.To ?? string.Empty,
                    foreignKey.OnUpdate ?? string.Empty,
                    foreignKey.OnDelete ?? string.Empty
                }));
            }
        }

        return result.ToArray();
    }

    private static IEnumerable<ForeignKeyProjection> ReadForeignKeys(
        SqliteConnection connection,
        string table)
    {
        return connection.Query<ForeignKeyProjection>(@"
SELECT
  id AS Id,
  seq AS Sequence,
  ""table"" AS [Table],
  ""from"" AS [From],
  ""to"" AS [To],
  on_update AS OnUpdate,
  on_delete AS OnDelete
FROM pragma_foreign_key_list(@table);", new { table });
    }

    private static string NormalizeDefault(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var normalized = value.Trim();
        while (normalized.Length >= 2 && normalized[0] == '(' && normalized[^1] == ')')
            normalized = normalized.Substring(1, normalized.Length - 2).Trim();
        return normalized;
    }

    private static string[] ReadFixtureProbes(string databasePath)
    {
        using var connection = Open(databasePath);
        return connection.Query<string>(@"
SELECT fixture_name || '=' || fixture_value
FROM fixture_probe
ORDER BY fixture_name;").ToArray();
    }

    private static string[] ReadFixtureDomainEvidence(string databasePath)
    {
        using var connection = Open(databasePath);
        var evidence = new List<string>();
        evidence.AddRange(connection.Query<string>(@"
SELECT 'product|' || id || '|' || barcode || '|' || name || '|' || unitPrice
FROM products WHERE barcode='FIXTURE-0001';"));
        evidence.AddRange(connection.Query<string>(@"
SELECT 'sale|' || id || '|' || code || '|' || createdAt || '|' || total ||
       '|' || paidCash || '|' || paidCard || '|' || change
FROM sales WHERE code='FIXTURE-SALE-0001';"));
        evidence.AddRange(connection.Query<string>(@"
SELECT 'line|' || id || '|' || saleId || '|' || COALESCE(productId, 0) ||
       '|' || barcode || '|' || name || '|' || quantity || '|' || unitPrice ||
       '|' || lineTotal
FROM sale_lines WHERE id=1;"));

        if (TableExists(connection, "app_settings"))
        {
            evidence.AddRange(connection.Query<string>(@"
SELECT 'setting|' || key || '|' || value
FROM app_settings WHERE key LIKE 'fixture.%';"));
        }
        if (TableExists(connection, "suppliers"))
        {
            evidence.AddRange(connection.Query<string>(@"
SELECT 'supplier|' || id || '|' || name || '|' || COALESCE(remote_supplier_id, '')
FROM suppliers WHERE id=1;"));
            evidence.AddRange(connection.Query<string>(@"
SELECT 'category|' || id || '|' || name || '|' || COALESCE(remote_category_id, '')
FROM categories WHERE id=1;"));
            evidence.AddRange(connection.Query<string>(@"
SELECT 'meta|' || barcode || '|' || COALESCE(article_code, '') ||
       '|' || purchase_price || '|' || stock_qty
FROM product_meta WHERE barcode='FIXTURE-0001';"));
            evidence.AddRange(connection.Query<string>(@"
SELECT 'history|' || id || '|' || barcode || '|' || timestamp || '|' || type ||
       '|' || COALESCE(old_price, 0) || '|' || new_price || '|' || COALESCE(source, '')
FROM product_price_history WHERE barcode='FIXTURE-0001';"));
            evidence.AddRange(connection.Query<string>(@"
SELECT 'cart|' || holdId || '|' || createdAtMs || '|' || totalMinor
FROM held_carts WHERE holdId='fixture-hold';"));
            evidence.AddRange(connection.Query<string>(@"
SELECT 'cart-line|' || id || '|' || holdId || '|' || barcode || '|' ||
       name || '|' || unitPrice || '|' || qty
FROM held_cart_lines WHERE holdId='fixture-hold';"));
            evidence.AddRange(connection.Query<string>(@"
SELECT 'role|' || id || '|' || code || '|' || name || '|' || is_system
FROM roles WHERE code='fixture_role';"));
            evidence.AddRange(connection.Query<string>(@"
SELECT 'user|' || id || '|' || username || '|' || display_name || '|' ||
       pin_hash || '|' || pin_salt || '|' || role_id || '|' || is_active
FROM users WHERE username='fixture_user';"));
        }
        if (TableExists(connection, "local_stock_movements"))
        {
            evidence.AddRange(connection.Query<string>(@"
SELECT 'movement|' || id || '|' || movement_key || '|' || sale_id || '|' ||
       COALESCE(sale_line_id, 0) || '|' || barcode || '|' || quantity_delta ||
       '|' || movement_kind || '|' || created_at
FROM local_stock_movements WHERE movement_key='fixture-movement';"));
        }
        if (TableExists(connection, "remote_catalog_pending_prices"))
        {
            evidence.AddRange(connection.Query<string>(@"
SELECT 'pending-price|' || id || '|' || COALESCE(remote_price_id, '') || '|' ||
       remote_product_id || '|' || type || '|' || price || '|' || effective_at ||
       '|' || COALESCE(source, '') || '|' || created_at
FROM remote_catalog_pending_prices WHERE remote_price_id='fixture-price-id';"));
        }
        if (TableExists(connection, "remote_catalog_product_references"))
        {
            evidence.AddRange(connection.Query<string>(@"
SELECT 'reference|' || remote_product_id || '|' || COALESCE(remote_category_id, '') ||
       '|' || COALESCE(remote_supplier_id, '')
FROM remote_catalog_product_references WHERE remote_product_id='fixture-product-id';"));
        }
        if (TableExists(connection, "remote_catalog_price_ownership"))
        {
            evidence.AddRange(connection.Query<string>(@"
SELECT 'ownership|' || remote_price_id || '|' || remote_product_id
FROM remote_catalog_price_ownership WHERE remote_price_id='fixture-price-id';"));
        }

        return evidence.OrderBy(item => item, StringComparer.Ordinal).ToArray();
    }

    private static string[] ReadImmutableOutboxEvidence(string databasePath)
    {
        using var connection = Open(databasePath);
        var result = new List<string>();
        if (TableExists(connection, "sales_sync_outbox"))
        {
            result.AddRange(connection.Query<string>(@"
SELECT 'sales|' || id || '|' || COALESCE(payload_json, '') || '|' || COALESCE(payload_hash, '')
FROM sales_sync_outbox
ORDER BY id;"));
        }
        if (TableExists(connection, "catalog_import_outbox"))
        {
            result.AddRange(connection.Query<string>(@"
SELECT 'catalog|' || id || '|' || COALESCE(payload_json, '') || '|' || COALESCE(payload_hash, '')
FROM catalog_import_outbox
ORDER BY id;"));
        }
        return result.ToArray();
    }

    private static string[] ReadLedgerTimestamps(string databasePath)
    {
        using var connection = Open(databasePath);
        return connection.Query<string>(@"
SELECT migration_id || '=' || applied_at
FROM schema_migrations
ORDER BY migration_id;").ToArray();
    }

    private static string[] ReadBlockedOutboxTimestamps(string databasePath)
    {
        using var connection = Open(databasePath);
        var result = new List<string>();
        if (TableExists(connection, "sales_sync_outbox"))
        {
            result.AddRange(connection.Query<string>(@"
SELECT 'sales|' || id || '|' || status || '|' || COALESCE(last_error_code, '') ||
       '|' || COALESCE(last_error_at, 0) || '|' || updated_at
FROM sales_sync_outbox
WHERE status = 'failed_blocked'
ORDER BY id;"));
        }
        if (TableExists(connection, "catalog_import_outbox"))
        {
            result.AddRange(connection.Query<string>(@"
SELECT 'catalog|' || id || '|' || status || '|' || COALESCE(last_error_code, '') ||
       '|' || COALESCE(last_error_at, 0) || '|' || updated_at
FROM catalog_import_outbox
WHERE status = 'failed_blocked'
ORDER BY id;"));
        }
        return result.ToArray();
    }

    private static bool TableExists(string databasePath, string table)
    {
        using var connection = Open(databasePath);
        return TableExists(connection, table);
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        return connection.ExecuteScalar<long>(@"
SELECT COUNT(1)
FROM sqlite_master
WHERE type = 'table' AND name = @table;", new { table }) == 1;
    }

    private static void ExecuteFixture(string databasePath, string fixtureFileName)
    {
        var fixtureDirectory = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "migrations");
        var script = LoadFixtureScript(fixtureDirectory, fixtureFileName, new HashSet<string>(StringComparer.Ordinal));
        using var connection = Open(databasePath);
        connection.Execute(script);
    }

    private static string LoadFixtureScript(
        string fixtureDirectory,
        string fixtureFileName,
        ISet<string> includeStack)
    {
        if (!string.Equals(fixtureFileName, Path.GetFileName(fixtureFileName), StringComparison.Ordinal) ||
            !fixtureFileName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Unsafe migration fixture include: " + fixtureFileName);
        }
        if (!includeStack.Add(fixtureFileName))
            throw new InvalidDataException("Cyclic migration fixture include: " + fixtureFileName);

        try
        {
            var path = Path.Combine(fixtureDirectory, fixtureFileName);
            if (!File.Exists(path))
                throw new FileNotFoundException("Migration fixture was not found.", path);

            var script = new StringBuilder();
            foreach (var line in File.ReadLines(path))
            {
                const string includePrefix = "-- include:";
                if (line.TrimStart().StartsWith(includePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var include = line.Trim().Substring(includePrefix.Length).Trim();
                    script.AppendLine(LoadFixtureScript(fixtureDirectory, include, includeStack));
                }
                else
                {
                    script.AppendLine(line);
                }
            }
            return script.ToString();
        }
        finally
        {
            includeStack.Remove(fixtureFileName);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Win7POS.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the Win7POS repository root.");
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class ColumnProjection
    {
        public string? DefaultValue { get; set; }
        public string? Name { get; set; }
        public int IsNotNullValue { get; set; }
        public int PrimaryKey { get; set; }
        public string? Type { get; set; }
    }

    private sealed class ForeignKeyProjection
    {
        public string? From { get; set; }
        public int Id { get; set; }
        public string? OnDelete { get; set; }
        public string? OnUpdate { get; set; }
        public int Sequence { get; set; }
        public string? Table { get; set; }
        public string? To { get; set; }
    }

    private sealed class LedgerProjection
    {
        public string MigrationId { get; set; } = string.Empty;
        public string Checksum { get; set; } = string.Empty;
    }

    private sealed class IndexProjection
    {
        public string Name { get; set; } = string.Empty;
        public string Sql { get; set; } = string.Empty;
    }

    private sealed class FixtureDatabase : IDisposable
    {
        private FixtureDatabase(string root)
        {
            Root = root;
            LivePath = Path.Combine(root, "legacy.db");
            FreshPath = Path.Combine(root, "fresh.db");
            BackupDirectory = Path.Combine(root, "backups");
        }

        public string BackupDirectory { get; }
        public string FreshPath { get; }
        public string LivePath { get; }
        private string Root { get; }

        public static FixtureDatabase Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "Win7POS.LegacyMigrations",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new FixtureDatabase(root);
        }

        public void Dispose()
        {
            SqliteConnectionFactory.ClearAllPools();
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
