using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CatalogRunContextPerformanceTests
{
    [TestMethod]
    public async Task RunContextReusesPreparedCommandsAndReferenceMapsAcrossPages()
    {
        using var db = TestDb.Create();
        var repository = new RemoteCatalogBatchRepository(db.Factory);
        using var run = repository.CreateRunContext();

        var first = await run.ApplyAsync(new RemoteCatalogBatch
        {
            Categories = new[]
            {
                new RemoteCatalogCategoryWrite
                {
                    RemoteCategoryId = "category-run",
                    Name = "Run Category"
                }
            },
            Suppliers = new[]
            {
                new RemoteCatalogSupplierWrite
                {
                    RemoteSupplierId = "supplier-run",
                    Name = "Run Supplier"
                }
            },
            Products = new[] { Product("product-run-1", "RUN-001", 1) }
        });
        var second = await run.ApplyAsync(new RemoteCatalogBatch
        {
            Products = new[] { Product("product-run-2", "RUN-002", 2) }
        });
        var third = await run.ApplyAsync(new RemoteCatalogBatch
        {
            Products = new[] { Product("product-run-1", "RUN-001", 3) }
        });

        Assert.AreEqual(1, first.ProductsApplied);
        Assert.AreEqual(1, second.ProductsApplied);
        Assert.AreEqual(1, third.ProductsApplied);
        Assert.AreEqual(3, run.Diagnostics.PagesApplied);
        Assert.AreEqual(8, run.Diagnostics.PreparedCommandCount);
        Assert.AreEqual(2, run.Diagnostics.ReferenceMapRefreshQueryCount);
        Assert.AreEqual(3, run.Diagnostics.ProductIdentityQueryCount);
        Assert.AreEqual(3, run.Diagnostics.PendingStockQueryCount);
        Assert.AreEqual(8, run.Diagnostics.ScopeSqlQueryCount);
        Assert.AreEqual(18, run.Diagnostics.LegacyScopeSqlQueryEstimate);
        Assert.AreEqual(1L, run.Diagnostics.ProductIdentityRowsLoaded);
        Assert.AreEqual(3L, run.Diagnostics.StagedProductIdentityCount);

        using var verify = db.Factory.Open();
        Assert.AreEqual(2L, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM products WHERE COALESCE(is_active, 1) = 1;"));
        Assert.AreEqual(2L, await verify.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM product_meta m
JOIN categories c ON c.id = m.category_id
JOIN suppliers s ON s.id = m.supplier_id
WHERE c.remote_category_id = 'category-run'
  AND s.remote_supplier_id = 'supplier-run';"));
    }

    [TestMethod]
    public async Task InvalidBatchDoesNotPublishRunDiagnosticsOrDurableRows()
    {
        using var db = TestDb.Create();
        using var run = new RemoteCatalogBatchRepository(db.Factory).CreateRunContext();
        var oversized = Product("product-must-not-write", "INVALID-RUN-001", 1);
        oversized.Name = new string('x', 500_000);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => run.ApplyAsync(
            new RemoteCatalogBatch
            {
                Categories = new[]
                {
                    new RemoteCatalogCategoryWrite
                    {
                        RemoteCategoryId = "category-must-not-write",
                        Name = "Must not write"
                    }
                },
                Products = new[] { oversized }
            }));

        Assert.AreEqual(0, run.Diagnostics.PagesApplied);
        Assert.AreEqual(0, run.Diagnostics.ReferenceMapRefreshQueryCount);
        Assert.AreEqual(0, run.Diagnostics.ProductIdentityQueryCount);
        Assert.AreEqual(0, run.Diagnostics.PendingStockQueryCount);
        Assert.AreEqual(0, run.Diagnostics.ScopeSqlQueryCount);
        Assert.AreEqual(0L, run.Diagnostics.StagedProductIdentityCount);

        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM categories WHERE remote_category_id = 'category-must-not-write';"));
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM products WHERE remote_product_id = 'product-must-not-write';"));
    }

    [TestMethod]
    public async Task FailedPageDoesNotPublishRolledBackReferenceCache()
    {
        using var db = TestDb.Create();
        using var run = new RemoteCatalogBatchRepository(db.Factory).CreateRunContext();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => run.ApplyAsync(
            new RemoteCatalogBatch
            {
                Categories = new[]
                {
                    new RemoteCatalogCategoryWrite
                    {
                        RemoteCategoryId = "category-rolled-back",
                        Name = "Rolled Back"
                    }
                },
                Products = new[]
                {
                    Product("product-good", "ROLLBACK-RUN-001", 1),
                    Product("product-invalid", "DISC:ROLLBACK-RUN", 1)
                }
            }));

        var productWithoutReference = Product("product-after-rollback", "ROLLBACK-RUN-002", 2);
        productWithoutReference.RemoteCategoryId = "category-rolled-back";
        productWithoutReference.CategoryName = string.Empty;
        productWithoutReference.RemoteSupplierId = string.Empty;
        productWithoutReference.SupplierName = string.Empty;
        await run.ApplyAsync(new RemoteCatalogBatch
        {
            Products = new[] { productWithoutReference }
        });

        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM categories WHERE remote_category_id = 'category-rolled-back';"));
        Assert.IsNull(await verify.ExecuteScalarAsync<long?>(@"
SELECT m.category_id
FROM product_meta m
WHERE m.barcode = 'ROLLBACK-RUN-002';"));
        Assert.AreEqual(1, run.Diagnostics.PagesApplied);
    }

    [TestMethod]
    public async Task ReferenceTombstoneIsRemovedFromTheRunCache()
    {
        using var db = TestDb.Create();
        using var run = new RemoteCatalogBatchRepository(db.Factory).CreateRunContext();
        await run.ApplyAsync(new RemoteCatalogBatch
        {
            Categories = new[]
            {
                new RemoteCatalogCategoryWrite
                {
                    RemoteCategoryId = "category-run",
                    Name = "Run Category"
                }
            },
            Suppliers = new[]
            {
                new RemoteCatalogSupplierWrite
                {
                    RemoteSupplierId = "supplier-run",
                    Name = "Run Supplier"
                }
            },
            Products = new[] { Product("product-before-tombstone", "RUN-TOMB-001", 1) }
        });
        await run.ApplyAsync(new RemoteCatalogBatch
        {
            CategoryTombstones = new[]
            {
                new RemoteCatalogCategoryTombstoneWrite
                {
                    RemoteCategoryId = "category-run",
                    RemoteDeletedAt = "2026-07-20T01:00:00Z",
                    RemoteUpdatedAt = "2026-07-20T01:00:00Z"
                }
            },
            SupplierTombstones = new[]
            {
                new RemoteCatalogSupplierTombstoneWrite
                {
                    RemoteSupplierId = "supplier-run",
                    RemoteDeletedAt = "2026-07-20T01:00:00Z",
                    RemoteUpdatedAt = "2026-07-20T01:00:00Z"
                }
            }
        });

        var after = Product("product-after-tombstone", "RUN-TOMB-002", 2);
        after.CategoryName = string.Empty;
        after.SupplierName = string.Empty;
        await run.ApplyAsync(new RemoteCatalogBatch
        {
            Products = new[] { after }
        });

        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await verify.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM categories
WHERE remote_category_id = 'category-run' AND is_active = 0;"));
        Assert.AreEqual(1L, await verify.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM suppliers
WHERE remote_supplier_id = 'supplier-run' AND is_active = 0;"));
        Assert.IsNull(await verify.ExecuteScalarAsync<long?>(@"
SELECT category_id FROM product_meta WHERE barcode = 'RUN-TOMB-002';"));
        Assert.IsNull(await verify.ExecuteScalarAsync<long?>(@"
SELECT supplier_id FROM product_meta WHERE barcode = 'RUN-TOMB-002';"));
    }

    private static RemoteCatalogProductWrite Product(
        string remoteProductId,
        string barcode,
        int stockQuantity)
    {
        return new RemoteCatalogProductWrite
        {
            ArticleCode = "ITEM-" + barcode,
            Barcode = barcode,
            CategoryName = "Run Category",
            Name = "Product " + barcode,
            PurchasePrice = 50,
            RemoteCategoryId = "category-run",
            RemoteProductId = remoteProductId,
            RemoteSupplierId = "supplier-run",
            SecondName = string.Empty,
            StockQuantity = stockQuantity,
            SupplierName = "Run Supplier",
            UnitPrice = 100
        };
    }

    private sealed class TestDb : IDisposable
    {
        private TestDb(string root)
        {
            Root = root;
            var options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));
            Factory = new SqliteConnectionFactory(options);
            DbInitializer.EnsureCreated(options);
        }

        internal SqliteConnectionFactory Factory { get; }
        private string Root { get; }

        internal static TestDb Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "win7pos-catalog-run-context-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestDb(root);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
