using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Migrations;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CustomerOrderInboxMigrationTests
{
    [TestMethod]
    public void UpgradeFrom0011_IsAdditiveCanonicalAndPreservesFiscalRows()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "win7pos-order-inbox-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));
        var factory = new SqliteConnectionFactory(options);
        try
        {
            new SchemaMigrationRunner(
                factory,
                SchemaMigrationRegistry.All.Take(11)).Run();
            using (var connection = factory.Open())
            {
                connection.Execute(@"
INSERT INTO sales(code, createdAt, total, paidCash, paidCard, change)
VALUES('TASK030-MIGRATION-PROBE', 1, 1900, 1900, 0, 0);");
            }

            var result = new SchemaMigrationRunner(
                factory,
                SchemaMigrationRegistry.All).Run();

            CollectionAssert.AreEqual(
                new[] { "0012-customer-order-inbox" },
                result.AppliedMigrationIds.ToArray());
            using var verify = factory.Open();
            var detector = new LegacySchemaDetector(verify);
            Assert.IsTrue(detector.HasCanonicalTableDefinitions(
                DbInitializer.CustomerOrderInboxSchemaSql,
                "customer_order_inbox"));
            Assert.IsTrue(detector.HasAllIndexDefinitions(
                DbInitializer.CustomerOrderInboxIndexSql));
            Assert.IsTrue(
                SchemaMigrationRegistry.IsCurrentSchemaStructurallyValid(detector));
            Assert.AreEqual(
                "TASK030-MIGRATION-PROBE|1900",
                verify.ExecuteScalar<string>(@"
SELECT code || '|' || total
FROM sales
WHERE code = 'TASK030-MIGRATION-PROBE';"));
            Assert.AreEqual(
                "1f2cb3c5895989825e77a8438c22879a6709907758efdadc760038fe5661e2f3",
                SchemaMigrationRegistry.Latest.Checksum);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
