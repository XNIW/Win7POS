using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CatalogProductRevisionCloseoutTests
{
    private const string RemoteId = "80000000-0000-4000-8000-000000000001";
    private const string Original = "2026-10-03T10:00:00.000000Z";
    private const string Deleted = "2026-10-03T10:01:00.000000Z";
    private const string Reactivated = "2026-10-03T10:02:00.000000Z";

    [TestMethod]
    public async Task OlderDeltaCannotResurrectProductAfterTombstone()
    {
        using var db = new Fixture();
        var repository = new RemoteCatalogBatchRepository(db.Factory);
        await repository.ApplyAsync(Batch(Original, "Original"));
        await repository.ApplyAsync(Tombstone(Deleted));
        await repository.ApplyAsync(Batch(Original, "Stale renamed"));
        using var connection = db.Factory.Open();
        Assert.AreEqual(0L, await connection.ExecuteScalarAsync<long>(
            "SELECT is_active FROM products WHERE remote_product_id=@RemoteId", new { RemoteId }));
        Assert.AreEqual(Deleted, await connection.ExecuteScalarAsync<string>(
            "SELECT remote_base_revision FROM products WHERE remote_product_id=@RemoteId", new { RemoteId }));
        Assert.AreEqual(0L, await connection.ExecuteScalarAsync<long>(
            "SELECT is_active FROM article_product_remote_shadow WHERE remote_product_id=@RemoteId", new { RemoteId }));
    }

    [TestMethod]
    public async Task OlderTombstoneCannotDeleteNewerReactivation()
    {
        using var db = new Fixture();
        var repository = new RemoteCatalogBatchRepository(db.Factory);
        await repository.ApplyAsync(Batch(Original, "Original"));
        await repository.ApplyAsync(Tombstone(Deleted));
        await repository.ApplyAsync(Batch(Reactivated, "Reactivated"));
        await repository.ApplyAsync(Tombstone(Deleted));
        using var connection = db.Factory.Open();
        Assert.AreEqual(1L, await connection.ExecuteScalarAsync<long>(
            "SELECT is_active FROM products WHERE remote_product_id=@RemoteId", new { RemoteId }));
        Assert.AreEqual(Reactivated, await connection.ExecuteScalarAsync<string>(
            "SELECT remote_base_revision FROM products WHERE remote_product_id=@RemoteId", new { RemoteId }));
        Assert.AreEqual("00a B条码", await connection.ExecuteScalarAsync<string>(
            "SELECT barcode FROM products WHERE remote_product_id=@RemoteId", new { RemoteId }));
        Assert.AreEqual("00Item a", await connection.ExecuteScalarAsync<string>(
            "SELECT article_code FROM product_meta WHERE barcode='00a B条码'"));
    }

    private static RemoteCatalogBatch Batch(string revision, string name) => new()
    {
        Products = new[] { new RemoteCatalogProductWrite
        {
            RemoteProductId = RemoteId, Barcode = "00a B条码", ArticleCode = "00Item a",
            Name = name, UnitPrice = 150, StockQuantity = 2, RemoteUpdatedAt = revision
        } }
    };

    [TestMethod]
    public async Task SamePageDescendingRevisionsKeepNewestBusinessAndShadow()
    {
        using var db = new Fixture();
        var repository = new RemoteCatalogBatchRepository(db.Factory);
        await repository.ApplyAsync(Batch(Original, "Original"));
        var newest = Batch(Reactivated, "Newest").Products.Single();
        var older = Batch(Deleted, "Older").Products.Single();
        var result = await repository.ApplyAsync(new RemoteCatalogBatch { Products = new[] { newest, older } });
        using var connection = db.Factory.Open();
        Assert.AreEqual("Newest", await connection.ExecuteScalarAsync<string>("SELECT name FROM products WHERE remote_product_id=@RemoteId", new { RemoteId }));
        Assert.AreEqual(Reactivated, await connection.ExecuteScalarAsync<string>("SELECT remote_base_revision FROM products WHERE remote_product_id=@RemoteId", new { RemoteId }));
        Assert.AreEqual("Newest", await connection.ExecuteScalarAsync<string>("SELECT primary_name FROM article_product_remote_shadow WHERE remote_product_id=@RemoteId", new { RemoteId }));
        Assert.AreEqual(1L, result.ProductsSkipped);
    }

    private static RemoteCatalogBatch Tombstone(string revision) => new()
    {
        ProductTombstones = new[] { new RemoteCatalogProductTombstoneWrite
        { RemoteProductId = RemoteId, RemoteUpdatedAt = revision, RemoteDeletedAt = revision } }
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "Win7POS.Closeout.Revision." + Guid.NewGuid().ToString("N"));
        public SqliteConnectionFactory Factory { get; }
        public Fixture()
        {
            Directory.CreateDirectory(root);
            var options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));
            Factory = new SqliteConnectionFactory(options);
            DbInitializer.EnsureCreated(options);
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
