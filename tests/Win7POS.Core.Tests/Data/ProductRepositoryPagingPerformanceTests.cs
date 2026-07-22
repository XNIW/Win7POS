using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Products;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class ProductRepositoryPagingPerformanceTests
{
    private const int RowCount = 100_000;
    private const int PageSize = 200;
    private const int SampleCount = 20;
    private const double MaximumP95Milliseconds = 500d;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(180_000)]
    public async Task LastPageForwardKeyset_RepositoryEndToEndP95_IsBoundedAt100000Rows()
    {
        using var fixture = new Fixture(RowCount);
        var filter = new ProductPageFilter(string.Empty, null, null, PageSize, catalogRevision: 1);
        var plan = CreateLastPageForwardPlan(filter);
        var offsetPlan = CreateLastPageOffsetPlan(filter);

        // Warm the SQLite page cache and Dapper's materializer before collecting
        // same-process steady-state samples. Each measured call still opens its own
        // connection and executes the transaction, COUNT, page query and commit.
        await AssertLastPageAsync(fixture.Repository, filter, plan);
        await AssertLastPageAsync(fixture.Repository, filter, plan);
        await AssertLastPageAsync(fixture.Repository, filter, offsetPlan);

        var keysetSamples = new double[SampleCount];
        var offsetSamples = new double[SampleCount];
        for (var index = 0; index < keysetSamples.Length; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            await AssertLastPageAsync(fixture.Repository, filter, plan);
            stopwatch.Stop();
            keysetSamples[index] = stopwatch.Elapsed.TotalMilliseconds;

            stopwatch.Restart();
            await AssertLastPageAsync(fixture.Repository, filter, offsetPlan);
            stopwatch.Stop();
            offsetSamples[index] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(keysetSamples);
        Array.Sort(offsetSamples);
        var p95Index = (int)Math.Ceiling(keysetSamples.Length * 0.95d) - 1;
        var p95Milliseconds = keysetSamples[p95Index];
        var medianMilliseconds = keysetSamples[keysetSamples.Length / 2];
        var offsetP95Milliseconds = offsetSamples[p95Index];
        var ratio = offsetP95Milliseconds / Math.Max(p95Milliseconds, 0.001d);

        TestContext.WriteLine(
            $"PRODUCT_REPOSITORY_KEYSET_100K samples={SampleCount} rows={RowCount} " +
            $"page_size={PageSize} median_ms={medianMilliseconds:F3} p95_ms={p95Milliseconds:F3} " +
            $"offset_p95_ms={offsetP95Milliseconds:F3} ratio={ratio:F2}x " +
            "scope=open+transaction+count+dapper+materialize+commit");

        Assert.IsTrue(
            p95Milliseconds <= MaximumP95Milliseconds,
            $"100,000-row repository-level last-page keyset p95 exceeded " +
            $"{MaximumP95Milliseconds:F0} ms: {p95Milliseconds:F3} ms.");
        Assert.IsTrue(
            p95Milliseconds <= offsetP95Milliseconds * 1.15d,
            $"Keyset repository p95 regressed more than 15% versus the same-host OFFSET control: " +
            $"keyset={p95Milliseconds:F3} ms, offset={offsetP95Milliseconds:F3} ms.");
    }

    private static ProductPagePlan CreateLastPageForwardPlan(ProductPageFilter filter)
    {
        var coordinator = new ProductPagingCoordinator();
        var firstPlan = coordinator.Plan(filter, 1);
        coordinator.Accept(
            filter,
            firstPlan,
            filter.CreateCursor("BC-00000001", 1),
            filter.CreateCursor("BC-00000200", 200),
            PageSize,
            RowCount);

        const int penultimatePage = RowCount / PageSize - 1;
        var penultimatePlan = coordinator.Plan(filter, penultimatePage);
        Assert.AreEqual(ProductPageQueryKind.OffsetFallback, penultimatePlan.Kind);
        coordinator.Accept(
            filter,
            penultimatePlan,
            filter.CreateCursor("BC-00099601", 99_601),
            filter.CreateCursor("BC-00099800", 99_800),
            PageSize,
            RowCount);

        var lastPagePlan = coordinator.Plan(filter, RowCount / PageSize);
        Assert.AreEqual(ProductPageQueryKind.Forward, lastPagePlan.Kind);
        Assert.IsFalse(lastPagePlan.UsedOffsetFallback);
        return lastPagePlan;
    }

    private static ProductPagePlan CreateLastPageOffsetPlan(ProductPageFilter filter)
    {
        var coordinator = new ProductPagingCoordinator();
        var firstPlan = coordinator.Plan(filter, 1);
        coordinator.Accept(
            filter,
            firstPlan,
            filter.CreateCursor("BC-00000001", 1),
            filter.CreateCursor("BC-00000200", 200),
            PageSize,
            RowCount);

        var lastPagePlan = coordinator.Plan(filter, RowCount / PageSize);
        Assert.AreEqual(ProductPageQueryKind.OffsetFallback, lastPagePlan.Kind);
        return lastPagePlan;
    }

    private static async Task AssertLastPageAsync(
        ProductRepository repository,
        ProductPageFilter filter,
        ProductPagePlan plan)
    {
        var snapshot = await repository.SearchDetailsPageAsync(filter, plan);
        Assert.AreEqual(RowCount, snapshot.TotalCount);
        Assert.AreEqual(PageSize, snapshot.Items.Count);
        Assert.AreEqual(99_801L, snapshot.Items[0].Id);
        Assert.AreEqual(RowCount, snapshot.Items[snapshot.Items.Count - 1].Id);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly SqliteConnectionFactory _factory;

        internal Fixture(int rows)
        {
            SQLitePCL.Batteries_V2.Init();
            _root = Path.Combine(
                Path.GetTempPath(),
                "Win7POS.ProductRepositoryPagingPerformance",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            var options = PosDbOptions.ForPath(Path.Combine(_root, "pos.db"));
            DbInitializer.EnsureCreated(options);
            _factory = new SqliteConnectionFactory(options);
            Repository = new ProductRepository(_factory);
            Seed(rows);
        }

        internal ProductRepository Repository { get; }

        public void Dispose()
        {
            SqliteConnectionFactory.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }

        private void Seed(int rows)
        {
            using var connection = _factory.Open();
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
    }
}
