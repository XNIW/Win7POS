using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class ProductQueryPlanTests
{
    private const string ProductPageSqlPrefix = @"
SELECT p.barcode
FROM products p
LEFT JOIN product_meta m ON m.barcode = p.barcode
WHERE COALESCE(p.is_active, 1) = 1
  AND ($q = '' OR p.barcode = $q OR p.name LIKE $like)";

    private const string ProductPageSqlSuffix = @"
ORDER BY p.barcode ASC
LIMIT 200 OFFSET 0;";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(20000)]
    [DataRow(100000)]
    public void ExplainProductQueries_BeforeAndAfterCandidateIndexes(int rows)
    {
        SQLitePCL.Batteries_V2.Init();
        var root = Path.Combine(Path.GetTempPath(), "Win7POS.ProductQueryPlan", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "pos.db");
        try
        {
            var options = PosDbOptions.ForPath(dbPath);
            DbInitializer.EnsureCreated(options);
            var factory = new SqliteConnectionFactory(options);
            using var connection = factory.Open();
            SeedProducts(connection, rows);

            var target = rows / 2;
            var cases = new[]
            {
                new QueryCase("page", string.Empty, "%", string.Empty, 0, 200),
                new QueryCase("category", string.Empty, "%", " AND m.category_id = $filter_id", 7, 200),
                new QueryCase("supplier", string.Empty, "%", " AND m.supplier_id = $filter_id", 11, 200),
                new QueryCase($"barcode", $"BC-{target:D8}", $"%BC-{target:D8}%", string.Empty, 0, 1),
                new QueryCase($"name_contains", $"Product {target:D8}", $"%Product {target:D8}%", string.Empty, 0, 1)
            };

            var before = cases.ToDictionary(
                item => item.Name,
                item => CaptureEvidence(connection, item),
                StringComparer.Ordinal);

            Execute(connection, @"
CREATE INDEX candidate_product_meta_category_barcode ON product_meta(category_id, barcode);
CREATE INDEX candidate_product_meta_supplier_barcode ON product_meta(supplier_id, barcode);
ANALYZE;");

            foreach (var item in cases)
            {
                var after = CaptureEvidence(connection, item);
                var baseline = before[item.Name];
                Assert.AreEqual(
                    baseline.ResultCount,
                    after.ResultCount,
                    $"Candidate indexes changed query exactness for {item.Name}.");
                Assert.AreEqual(item.ExpectedCount, after.ResultCount, $"Unexpected result count for {item.Name}.");
                Assert.IsTrue(baseline.Plan.Count > 0 && after.Plan.Count > 0, $"Missing query plan for {item.Name}.");
                TestContext.WriteLine(
                    $"PRODUCT_QUERY_PLAN rows={rows} query={item.Name} " +
                    $"before_ms={baseline.MedianMilliseconds:F3} after_ms={after.MedianMilliseconds:F3} " +
                    $"before=[{string.Join(" | ", baseline.Plan)}] after=[{string.Join(" | ", after.Plan)}]");
            }
        }
        finally
        {
            SqliteConnectionFactory.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static QueryEvidence CaptureEvidence(SqliteConnection connection, QueryCase query)
    {
        var plan = ReadPlan(connection, query);
        var resultCount = ExecuteQuery(connection, query);
        var samples = new double[5];
        for (var index = 0; index < samples.Length; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            var sampleCount = ExecuteQuery(connection, query);
            stopwatch.Stop();
            Assert.AreEqual(resultCount, sampleCount, $"Query {query.Name} returned unstable results.");
            samples[index] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        return new QueryEvidence(plan, resultCount, samples[samples.Length / 2]);
    }

    private static IReadOnlyList<string> ReadPlan(SqliteConnection connection, QueryCase query)
    {
        using var command = CreateQueryCommand(connection, query, "EXPLAIN QUERY PLAN " + BuildSql(query));
        using var reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
            details.Add(reader.GetString(3));
        return details;
    }

    private static int ExecuteQuery(SqliteConnection connection, QueryCase query)
    {
        using var command = CreateQueryCommand(connection, query, BuildSql(query));
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
            count++;
        return count;
    }

    private static SqliteCommand CreateQueryCommand(SqliteConnection connection, QueryCase query, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$q", query.Query);
        command.Parameters.AddWithValue("$like", query.Like);
        if (query.FilterSql.Length > 0)
            command.Parameters.AddWithValue("$filter_id", query.FilterId);
        return command;
    }

    private static string BuildSql(QueryCase query) =>
        ProductPageSqlPrefix + query.FilterSql + ProductPageSqlSuffix;

    private static void SeedProducts(SqliteConnection connection, int rows)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO products(id, barcode, name, unitPrice, is_active)
VALUES($id, $barcode, $name, $unit_price, 1);
INSERT INTO product_meta(barcode, supplier_id, category_id, stock_qty)
VALUES($barcode, $supplier_id, $category_id, 1);";
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var barcode = command.Parameters.Add("$barcode", SqliteType.Text);
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var unitPrice = command.Parameters.Add("$unit_price", SqliteType.Integer);
        var supplierId = command.Parameters.Add("$supplier_id", SqliteType.Integer);
        var categoryId = command.Parameters.Add("$category_id", SqliteType.Integer);
        command.Prepare();

        for (var index = 1; index <= rows; index++)
        {
            id.Value = index;
            barcode.Value = $"BC-{index:D8}";
            name.Value = $"Product {index:D8}";
            unitPrice.Value = 100 + index;
            supplierId.Value = index % 40;
            categoryId.Value = index % 40;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class QueryCase
    {
        internal QueryCase(
            string name,
            string query,
            string like,
            string filterSql,
            int filterId,
            int expectedCount)
        {
            Name = name;
            Query = query;
            Like = like;
            FilterSql = filterSql;
            FilterId = filterId;
            ExpectedCount = expectedCount;
        }

        internal string Name { get; }
        internal string Query { get; }
        internal string Like { get; }
        internal string FilterSql { get; }
        internal int FilterId { get; }
        internal int ExpectedCount { get; }
    }

    private sealed class QueryEvidence
    {
        internal QueryEvidence(IReadOnlyList<string> plan, int resultCount, double medianMilliseconds)
        {
            Plan = plan;
            ResultCount = resultCount;
            MedianMilliseconds = medianMilliseconds;
        }

        internal IReadOnlyList<string> Plan { get; }
        internal int ResultCount { get; }
        internal double MedianMilliseconds { get; }
    }
}
