using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.ImportDb;
using Win7POS.Data;
using Win7POS.Data.Import;
using Win7POS.Data.ImportDb;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class RemoteCatalogReferenceTombstoneTests
{
    [TestMethod]
    public async Task DbInitializer_MigratesLegacyCategoryAndSupplierRowsAsActive()
    {
        var root = CreateTempRoot();
        var dbPath = Path.Combine(root, "legacy.db");
        try
        {
            SQLitePCL.Batteries_V2.Init();
            using (var conn = new SqliteConnection("Data Source=" + dbPath))
            {
                conn.Open();
                await conn.ExecuteAsync(@"
CREATE TABLE categories(id INTEGER PRIMARY KEY, name TEXT NOT NULL);
CREATE TABLE suppliers(id INTEGER PRIMARY KEY, name TEXT NOT NULL);
INSERT INTO categories(id, name) VALUES(7, 'Legacy Category');
INSERT INTO suppliers(id, name) VALUES(9, 'Legacy Supplier');");
            }

            DbInitializer.EnsureCreated(PosDbOptions.ForPath(dbPath));

            using var migrated = new SqliteConnectionFactory(PosDbOptions.ForPath(dbPath)).Open();
            var categoryColumns = (await migrated.QueryAsync<string>(
                "SELECT name FROM pragma_table_info('categories');")).ToList();
            var supplierColumns = (await migrated.QueryAsync<string>(
                "SELECT name FROM pragma_table_info('suppliers');")).ToList();
            CollectionAssert.Contains(categoryColumns, "remote_category_id");
            CollectionAssert.Contains(categoryColumns, "remote_updated_at");
            CollectionAssert.Contains(categoryColumns, "remote_deleted_at");
            CollectionAssert.Contains(categoryColumns, "is_active");
            CollectionAssert.Contains(supplierColumns, "remote_supplier_id");
            CollectionAssert.Contains(supplierColumns, "remote_updated_at");
            CollectionAssert.Contains(supplierColumns, "remote_deleted_at");
            CollectionAssert.Contains(supplierColumns, "is_active");
            Assert.AreEqual(1L, await migrated.ExecuteScalarAsync<long>(
                "SELECT is_active FROM categories WHERE id = 7;"));
            Assert.AreEqual(1L, await migrated.ExecuteScalarAsync<long>(
                "SELECT is_active FROM suppliers WHERE id = 9;"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public async Task RemoteTombstones_AreIdempotentAndDoNotDeactivateReferencingProduct()
    {
        using var db = TestDb.Create();
        var categories = new CategoryRepository(db.Factory);
        var suppliers = new SupplierRepository(db.Factory);
        Assert.IsTrue(await categories.UpsertRemoteAsync(
            "category-1", "Category One", "2026-07-14T10:00:00Z"));
        Assert.IsTrue(await suppliers.UpsertRemoteAsync(
            "supplier-1", "Supplier One", "2026-07-14T10:00:00Z"));

        int categoryId;
        int supplierId;
        using (var conn = db.Factory.Open())
        {
            categoryId = await conn.ExecuteScalarAsync<int>(
                "SELECT id FROM categories WHERE remote_category_id = 'category-1';");
            supplierId = await conn.ExecuteScalarAsync<int>(
                "SELECT id FROM suppliers WHERE remote_supplier_id = 'supplier-1';");
            await conn.ExecuteAsync(@"
INSERT INTO products(barcode, name, unitPrice, remote_product_id, is_active)
VALUES('P-1', 'Product One', 1000, 'product-1', 1);
INSERT INTO product_meta(barcode, supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES('P-1', @supplierId, 'Supplier One', @categoryId, 'Category One', 1);",
                new { categoryId, supplierId });
        }

        Assert.IsTrue(await categories.ApplyRemoteTombstoneAsync(
            "category-1", "2026-07-14T11:00:00Z", "2026-07-14T11:00:00Z"));
        Assert.IsTrue(await suppliers.ApplyRemoteTombstoneAsync(
            "supplier-1", "2026-07-14T11:00:00Z", "2026-07-14T11:00:00Z"));
        Assert.IsTrue(await categories.ApplyRemoteTombstoneAsync(
            "category-1", "2026-07-14T11:00:00Z", "2026-07-14T11:00:00Z"));
        Assert.IsTrue(await suppliers.ApplyRemoteTombstoneAsync(
            "supplier-1", "2026-07-14T11:00:00Z", "2026-07-14T11:00:00Z"));

        Assert.AreEqual(0, (await categories.ListAllAsync()).Count);
        Assert.AreEqual(0, (await suppliers.ListAllAsync()).Count);
        using (var conn = db.Factory.Open())
        {
            Assert.AreEqual(1L, await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM products WHERE barcode = 'P-1' AND is_active = 1;"));
            Assert.AreEqual(categoryId, await conn.ExecuteScalarAsync<int>(
                "SELECT category_id FROM product_meta WHERE barcode = 'P-1';"));
            Assert.AreEqual(supplierId, await conn.ExecuteScalarAsync<int>(
                "SELECT supplier_id FROM product_meta WHERE barcode = 'P-1';"));
        }

        Assert.IsFalse(await categories.UpsertRemoteAsync(
            "category-1", "Stale Category", "2026-07-14T10:30:00Z"));
        Assert.IsFalse(await suppliers.UpsertRemoteAsync(
            "supplier-1", "Stale Supplier", "2026-07-14T10:30:00Z"));
        Assert.IsFalse(await categories.UpsertRemoteAsync(
            "category-1", "Equivalent Timestamp Category", "2026-07-14T12:00:00+01:00"));
        Assert.IsFalse(await suppliers.UpsertRemoteAsync(
            "supplier-1", "Equivalent Timestamp Supplier", "2026-07-14T12:00:00+01:00"));
        Assert.IsTrue(await categories.UpsertRemoteAsync(
            "category-1", "Category Reactivated", "2026-07-14T12:00:00Z"));
        Assert.IsTrue(await suppliers.UpsertRemoteAsync(
            "supplier-1", "Supplier Reactivated", "2026-07-14T12:00:00Z"));
        Assert.AreEqual(1, (await categories.ListAllAsync()).Count);
        Assert.AreEqual(1, (await suppliers.ListAllAsync()).Count);
    }

    [TestMethod]
    public async Task LocalResolver_DoesNotReuseRemoteTombstonedReferenceByName()
    {
        using var db = TestDb.Create();
        var categories = new CategoryRepository(db.Factory);
        var suppliers = new SupplierRepository(db.Factory);
        await categories.UpsertRemoteAsync("category-1", "Shared Name", "2026-07-14T10:00:00Z");
        await suppliers.UpsertRemoteAsync("supplier-1", "Shared Name", "2026-07-14T10:00:00Z");
        await categories.ApplyRemoteTombstoneAsync(
            "category-1", "2026-07-14T11:00:00Z", "2026-07-14T11:00:00Z");
        await suppliers.ApplyRemoteTombstoneAsync(
            "supplier-1", "2026-07-14T11:00:00Z", "2026-07-14T11:00:00Z");

        using var conn = db.Factory.Open();
        using var tx = conn.BeginTransaction();
        var resolver = new CategorySupplierResolver(conn, tx);
        var localCategoryId = await resolver.GetOrCreateCategoryIdAsync("Shared Name");
        var localSupplierId = await resolver.GetOrCreateSupplierIdAsync("Shared Name");
        tx.Commit();

        Assert.IsNotNull(localCategoryId);
        Assert.IsNotNull(localSupplierId);
        Assert.AreEqual(2L, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM categories WHERE name = 'Shared Name';"));
        Assert.AreEqual(2L, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM suppliers WHERE name = 'Shared Name';"));
    }

    [TestMethod]
    public async Task DedicatedLocalRows_CannotOverwriteRemoteIdentityByLocalId()
    {
        using var db = TestDb.Create();
        var categories = new CategoryRepository(db.Factory);
        await categories.UpsertRemoteAsync("category-1", "Remote Category", "2026-07-14T10:00:00Z");
        int remoteLocalId;
        using (var conn = db.Factory.Open())
        {
            remoteLocalId = await conn.ExecuteScalarAsync<int>(
                "SELECT id FROM categories WHERE remote_category_id = 'category-1';");
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new ProductImportApplyService(db.Factory).ApplyAsync(
                Array.Empty<ImportRow>(),
                new ImportApplyOptions(),
                dedicatedCategories: new[]
                {
                    new CategoryRow { Id = remoteLocalId, Name = "Local Override" }
                }));
    }

    [TestMethod]
    public async Task ProductDatabaseImport_DoesNotReactivateOrOverwriteRemoteTombstone()
    {
        using var db = TestDb.Create();
        var categories = new CategoryRepository(db.Factory);
        await categories.UpsertRemoteAsync(
            "category-1", "Remote Category", "2026-07-14T10:00:00Z");
        await categories.ApplyRemoteTombstoneAsync(
            "category-1", "2026-07-14T11:00:00Z", "2026-07-14T11:00:00Z");

        int remoteLocalId;
        using (var conn = db.Factory.Open())
        {
            remoteLocalId = await conn.ExecuteScalarAsync<int>(
                "SELECT id FROM categories WHERE remote_category_id = 'category-1';");
        }

        var result = await new ProductDbImporter(db.Factory).ImportAsync(new ProductDbWorkbook
        {
            Categories = new[]
            {
                new CategoryRow { Id = remoteLocalId, Name = "Local Override" }
            },
            Products = new[]
            {
                new ProductRow
                {
                    Barcode = "P-LOCAL",
                    CategoryId = remoteLocalId,
                    CategoryName = "Local Override",
                    Name = "Local Product",
                    RetailPrice = 1000
                }
            }
        }, dryRun: false);

        Assert.AreEqual(1, result.Errors.Count);
        using var verify = db.Factory.Open();
        Assert.AreEqual("Remote Category", await verify.ExecuteScalarAsync<string>(
            "SELECT name FROM categories WHERE id = @id;", new { id = remoteLocalId }));
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(
            "SELECT is_active FROM categories WHERE id = @id;", new { id = remoteLocalId }));
        Assert.AreEqual("category-1", await verify.ExecuteScalarAsync<string>(
            "SELECT remote_category_id FROM categories WHERE id = @id;", new { id = remoteLocalId }));
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM products WHERE barcode = 'P-LOCAL';"));
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "win7pos-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class TestDb : IDisposable
    {
        private TestDb(string root)
        {
            Root = root;
            var dbPath = Path.Combine(root, "pos.db");
            Factory = new SqliteConnectionFactory(PosDbOptions.ForPath(dbPath));
            DbInitializer.EnsureCreated(PosDbOptions.ForPath(dbPath));
        }

        public SqliteConnectionFactory Factory { get; }
        private string Root { get; }

        public static TestDb Create()
        {
            return new TestDb(CreateTempRoot());
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
