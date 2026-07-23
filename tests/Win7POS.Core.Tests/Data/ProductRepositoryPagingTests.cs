using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Products;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class ProductRepositoryPagingTests
{
    [TestMethod]
    public async Task ForwardKeyset_PreservesExactBarcodePrecedenceAndHasNoDuplicates()
    {
        using var fixture = new Fixture();
        fixture.Seed(
            (1, "900", "milk long-life"),
            (2, "milk", "exact product"),
            (3, "100", "milk fresh"),
            (4, "ZZZ", "milk powder"));

        var filter = new ProductPageFilter("milk", null, null, 2, 1);
        var coordinator = new ProductPagingCoordinator();
        var first = await LoadAndAcceptAsync(fixture.Repository, coordinator, filter, 1);

        CollectionAssert.AreEqual(new[] { "milk", "100" }, first.Items.Select(item => item.Barcode).ToArray());
        var nextPlan = coordinator.Plan(filter, 2);
        Assert.AreEqual(ProductPageQueryKind.Forward, nextPlan.Kind);
        Assert.IsFalse(nextPlan.UsedOffsetFallback);

        var second = await LoadAndAcceptAsync(fixture.Repository, coordinator, filter, 2);
        CollectionAssert.AreEqual(new[] { "900", "ZZZ" }, second.Items.Select(item => item.Barcode).ToArray());
        Assert.AreEqual(4, first.Items.Concat(second.Items).Select(item => item.Id).Distinct().Count());
    }

    [TestMethod]
    public async Task Keyset_InsertAndDeleteBetweenPages_DoesNotRepeatAcceptedRows()
    {
        using var fixture = new Fixture();
        fixture.Seed(
            (1, "A", "A"),
            (2, "B", "B"),
            (3, "C", "C"),
            (4, "D", "D"),
            (5, "E", "E"));

        var filter = new ProductPageFilter(string.Empty, null, null, 2, 1);
        var coordinator = new ProductPagingCoordinator();
        var first = await LoadAndAcceptAsync(fixture.Repository, coordinator, filter, 1);
        fixture.Insert(6, "BB", "inserted after cursor");
        fixture.Delete("D");

        var second = await LoadAndAcceptAsync(fixture.Repository, coordinator, filter, 2);

        CollectionAssert.AreEqual(new[] { "A", "B" }, first.Items.Select(item => item.Barcode).ToArray());
        CollectionAssert.AreEqual(new[] { "BB", "C" }, second.Items.Select(item => item.Barcode).ToArray());
        Assert.AreEqual(
            first.Items.Count + second.Items.Count,
            first.Items.Concat(second.Items).Select(item => item.Id).Distinct().Count());

        var changedRevision = new ProductPageFilter(string.Empty, null, null, 2, 2);
        var reset = coordinator.Plan(changedRevision, 3);
        Assert.AreEqual(ProductPageQueryKind.First, reset.Kind);
        Assert.AreEqual(1, reset.TargetPage);
    }

    [TestMethod]
    public async Task Keyset_TraversesMutatedResultWithoutSkipOrDuplicateAfterCursor()
    {
        using var fixture = new Fixture();
        fixture.Seed(Enumerable.Range(1, 8)
            .Select(index => ((long)index, ((char)('A' + index - 1)).ToString(), $"Product {index}"))
            .ToArray());

        var filter = new ProductPageFilter(string.Empty, null, null, 2, 1);
        var coordinator = new ProductPagingCoordinator();
        var observed = new List<string>();
        var first = await LoadAndAcceptAsync(fixture.Repository, coordinator, filter, 1);
        observed.AddRange(first.Items.Select(item => item.Barcode));

        fixture.Insert(9, "BB", "inserted after accepted cursor");
        fixture.Delete("E");
        for (var page = 2; observed.Count < 8; page++)
        {
            var snapshot = await LoadAndAcceptAsync(fixture.Repository, coordinator, filter, page);
            if (snapshot.Items.Count == 0)
                break;
            observed.AddRange(snapshot.Items.Select(item => item.Barcode));
        }

        CollectionAssert.AreEqual(
            new[] { "A", "B", "BB", "C", "D", "F", "G", "H" },
            observed.ToArray());
        Assert.AreEqual(observed.Count, observed.Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    public async Task SqliteBinaryUnicodeOrder_MatchesPublishedCursorOrder()
    {
        using var fixture = new Fixture();
        fixture.Seed(
            (1, "\U00010000", "supplementary"),
            (2, "\uE000", "private use"),
            (3, "Z", "ascii"));

        var filter = new ProductPageFilter(string.Empty, null, null, 2, 1);
        var coordinator = new ProductPagingCoordinator();
        var first = await LoadAndAcceptAsync(fixture.Repository, coordinator, filter, 1);
        var second = await LoadAndAcceptAsync(fixture.Repository, coordinator, filter, 2);

        CollectionAssert.AreEqual(
            new[] { "Z", "\uE000", "\U00010000" },
            first.Items.Concat(second.Items).Select(item => item.Barcode).ToArray());
    }

    [TestMethod]
    public async Task PreviousAfterAnchorEviction_UsesReverseKeysetAndRestoresStablePage()
    {
        using var fixture = new Fixture();
        fixture.Seed(Enumerable.Range(1, 8)
            .Select(index => ((long)index, $"B{index:D2}", $"Product {index:D2}"))
            .ToArray());

        var filter = new ProductPageFilter(string.Empty, null, null, 2, 1);
        var coordinator = new ProductPagingCoordinator(maximumAnchors: 1);
        await LoadAndAcceptAsync(fixture.Repository, coordinator, filter, 1);
        var expectedSecond = await LoadAndAcceptAsync(fixture.Repository, coordinator, filter, 2);
        await LoadAndAcceptAsync(fixture.Repository, coordinator, filter, 3);

        var previousPlan = coordinator.Plan(filter, 2);
        Assert.AreEqual(ProductPageQueryKind.Reverse, previousPlan.Kind);
        var previous = await fixture.Repository.SearchDetailsPageAsync(filter, previousPlan);

        CollectionAssert.AreEqual(
            expectedSecond.Items.Select(item => item.Id).ToArray(),
            previous.Items.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public async Task ArbitraryJump_UsesExplicitOffsetFallbackOnlyOnceThenNextIsKeyset()
    {
        using var fixture = new Fixture();
        fixture.Seed(Enumerable.Range(1, 12)
            .Select(index => ((long)index, $"B{index:D2}", $"Product {index:D2}"))
            .ToArray());

        var filter = new ProductPageFilter(string.Empty, null, null, 2, 1);
        var coordinator = new ProductPagingCoordinator();
        await LoadAndAcceptAsync(fixture.Repository, coordinator, filter, 1);

        var jumpPlan = coordinator.Plan(filter, 5);
        Assert.AreEqual(ProductPageQueryKind.OffsetFallback, jumpPlan.Kind);
        Assert.AreEqual(8, jumpPlan.Offset);
        var fifth = await LoadAndAcceptAsync(fixture.Repository, coordinator, filter, 5);
        CollectionAssert.AreEqual(new[] { "B09", "B10" }, fifth.Items.Select(item => item.Barcode).ToArray());

        var next = coordinator.Plan(filter, 6);
        Assert.AreEqual(ProductPageQueryKind.Forward, next.Kind);
        Assert.IsFalse(next.UsedOffsetFallback);
        var sixth = await LoadAndAcceptAsync(fixture.Repository, coordinator, filter, 6);
        CollectionAssert.AreEqual(new[] { "B11", "B12" }, sixth.Items.Select(item => item.Barcode).ToArray());
    }

    [TestMethod]
    public async Task QueryRepository_AndPublicFacade_ReturnEquivalentKeysetSnapshot()
    {
        using var fixture = new Fixture();
        fixture.Seed(
            (1, "900", "milk long-life"),
            (2, "milk", "exact product"),
            (3, "100", "milk fresh"),
            (4, "ZZZ", "milk powder"));

        var filter = new ProductPageFilter("milk", null, null, 2, 1);
        var plan = new ProductPagingCoordinator().Plan(filter, 1);

        var facade = await fixture.Repository.SearchDetailsPageAsync(filter, plan);
        var query = await fixture.Queries.SearchDetailsPageAsync(filter, plan);

        Assert.AreEqual(facade.TotalCount, query.TotalCount);
        CollectionAssert.AreEqual(
            facade.Items.Select(item => item.Id).ToArray(),
            query.Items.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { "milk", "100" },
            query.Items.Select(item => item.Barcode).ToArray());
    }

    [TestMethod]
    public async Task QueryRepository_RejectsPagingPlanForDifferentFilter()
    {
        using var fixture = new Fixture();
        fixture.Seed((1, "A", "alpha"));

        var requestedFilter = new ProductPageFilter("alpha", null, null, 25, 1);
        var otherFilter = new ProductPageFilter("beta", null, null, 25, 1);
        var plan = new ProductPagingCoordinator().Plan(otherFilter, 1);

        try
        {
            await fixture.Queries.SearchDetailsPageAsync(requestedFilter, plan);
            Assert.Fail("A paging plan bound to another filter must be rejected.");
        }
        catch (InvalidOperationException)
        {
        }
    }

    [TestMethod]
    public async Task QueryRepository_SupportsParallelReadsWithoutSharedQueryState()
    {
        using var fixture = new Fixture();
        fixture.Seed(Enumerable.Range(1, 12)
            .Select(index => ((long)index, $"B{index:D2}", $"Product {index:D2}"))
            .ToArray());

        var filter = new ProductPageFilter(string.Empty, null, null, 3, 1);
        var plan = new ProductPagingCoordinator().Plan(filter, 1);
        var snapshots = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(_ => fixture.Queries.SearchDetailsPageAsync(filter, plan)));

        foreach (var snapshot in snapshots)
        {
            Assert.AreEqual(12, snapshot.TotalCount);
            CollectionAssert.AreEqual(
                new[] { "B01", "B02", "B03" },
                snapshot.Items.Select(item => item.Barcode).ToArray());
        }
    }

    [TestMethod]
    public async Task QueryRepository_BatchLookup_TrimsDeduplicatesAndCrossesSqliteParameterBoundary()
    {
        using var fixture = new Fixture();
        fixture.Seed(Enumerable.Range(1, 901)
            .Select(index => ((long)index, $"B{index:D4}", $"Product {index:D4}"))
            .ToArray());

        var requested = Enumerable.Range(1, 901)
            .Select(index => $" B{index:D4} ")
            .Concat(new[] { "B0001", string.Empty, null })
            .ToArray();

        var products = await fixture.Queries.GetByBarcodesAsync(requested);

        Assert.AreEqual(901, products.Count);
        Assert.AreEqual("B0001", products["B0001"].Barcode);
        Assert.AreEqual("B0901", products["B0901"].Barcode);
    }

    private static async Task<ProductRepository.ProductDetailsPageSnapshot> LoadAndAcceptAsync(
        ProductRepository repository,
        ProductPagingCoordinator coordinator,
        ProductPageFilter filter,
        int page)
    {
        var plan = coordinator.Plan(filter, page);
        var snapshot = await repository.SearchDetailsPageAsync(filter, plan);
        var first = snapshot.Items.Count == 0
            ? null
            : filter.CreateCursor(snapshot.Items[0].Barcode, snapshot.Items[0].Id);
        var last = snapshot.Items.Count == 0
            ? null
            : filter.CreateCursor(snapshot.Items[snapshot.Items.Count - 1].Barcode, snapshot.Items[snapshot.Items.Count - 1].Id);
        coordinator.Accept(filter, plan, first, last, snapshot.Items.Count, snapshot.TotalCount);
        return snapshot;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly SqliteConnectionFactory _factory;

        internal Fixture()
        {
            SQLitePCL.Batteries_V2.Init();
            _root = Path.Combine(Path.GetTempPath(), "Win7POS.ProductPaging", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            var options = PosDbOptions.ForPath(Path.Combine(_root, "pos.db"));
            DbInitializer.EnsureCreated(options);
            _factory = new SqliteConnectionFactory(options);
            Repository = new ProductRepository(_factory);
            Queries = new ProductQueryRepository(_factory);
        }

        internal ProductRepository Repository { get; }

        internal ProductQueryRepository Queries { get; }

        internal void Seed(params (long Id, string Barcode, string Name)[] products)
        {
            using var connection = _factory.Open();
            using var transaction = connection.BeginTransaction();
            foreach (var product in products)
                Insert(connection, transaction, product.Id, product.Barcode, product.Name);
            transaction.Commit();
        }

        internal void Insert(long id, string barcode, string name)
        {
            using var connection = _factory.Open();
            using var transaction = connection.BeginTransaction();
            Insert(connection, transaction, id, barcode, name);
            transaction.Commit();
        }

        internal void Delete(string barcode)
        {
            using var connection = _factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM product_meta WHERE barcode = $barcode; DELETE FROM products WHERE barcode = $barcode;";
            command.Parameters.AddWithValue("$barcode", barcode);
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnectionFactory.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }

        private static void Insert(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long id,
            string barcode,
            string name)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO products(id, barcode, name, unitPrice, is_active)
VALUES($id, $barcode, $name, 100, 1);
INSERT INTO product_meta(barcode, stock_qty)
VALUES($barcode, 1);";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$barcode", barcode);
            command.Parameters.AddWithValue("$name", name);
            command.ExecuteNonQuery();
        }
    }
}
