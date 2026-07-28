using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Migrations;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class ArticleMutationMigrationTests
{
    [TestMethod]
    public void ArticleMutationMigration_CreatesCanonicalAdditiveSchema()
    {
        SQLitePCL.Batteries_V2.Init();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var transaction = connection.BeginTransaction())
        {
            DbInitializer.CreateBaseTables(connection, transaction);
            DbInitializer.EnsureMigrations(connection, transaction);
            DbInitializer.CreateDependentTables(connection, transaction);
            DbInitializer.BackfillLegacyOutboxBindings(connection, transaction);
            DbInitializer.EnsureIndexes(connection, transaction);
            DbInitializer.SeedSecurity(connection, transaction);
            DbInitializer.EnsureReceiptShopSnapshot(connection, transaction);
            DbInitializer.EnsureOnlineSyncGenerationSchema(connection, transaction);
            DbInitializer.EnsureCatalogAuthoritativeIdStageSchema(
                connection,
                transaction);
            DbInitializer.EnsureArticleMutationSchema(connection, transaction);
            transaction.Commit();
        }

        var detector = new LegacySchemaDetector(connection);
        using (var expected = new SqliteConnection("Data Source=:memory:"))
        {
            expected.Open();
            using var expectedCommand = expected.CreateCommand();
            expectedCommand.CommandText =
                DbInitializer.ArticleMutationTableSql + "\n" +
                DbInitializer.ArticleMutationOwnedIndexSql;
            expectedCommand.ExecuteNonQuery();
            CollectionAssert.AreEqual(
                ReadShape(expected, "article_mutation_outbox"),
                ReadShape(connection, "article_mutation_outbox"));
        }
        Assert.IsTrue(
            detector.HasAllColumnDefinitions(DbInitializer.ArticleMutationColumns),
            "Article mutation additive columns are not canonical.");
        foreach (var table in new[]
        {
            "article_mutation_outbox",
            "article_mutation_attempts",
            "article_manual_stock_adjustments",
            "article_product_remote_shadow"
        })
        {
            Assert.IsTrue(
                detector.HasCanonicalTableDefinitions(
                    DbInitializer.ArticleMutationTableSql + "\n" +
                    DbInitializer.ArticleMutationOwnedIndexSql,
                    table),
                table + " is not canonical.");
        }
        Assert.IsTrue(
            detector.HasAllIndexDefinitions(DbInitializer.ArticleMutationIndexSql),
            "Article mutation indexes are not canonical.");
        Assert.IsTrue(
            SchemaMigrationRegistry.IsCurrentSchemaStructurallyValid(detector),
            "Fresh schema does not satisfy the current migration invariant.");
        Assert.AreEqual(
            "0010-article-mutation-outbox",
            SchemaMigrationRegistry.Latest.MigrationId);
    }

    private static string[] ReadShape(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(" + table + ");";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join(
                "|",
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? "<null>" : reader.GetValue(4),
                reader.GetInt64(5)));
        }
        return rows.ToArray();
    }
}
