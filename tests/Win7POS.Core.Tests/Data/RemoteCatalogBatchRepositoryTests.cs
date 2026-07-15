using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class RemoteCatalogBatchRepositoryTests
{
    [TestMethod]
    public async Task ApplyAsync_RetrySamePageIsIdempotent()
    {
        using var db = TestDb.Create();
        var repository = new RemoteCatalogBatchRepository(db.Factory);
        var batch = new RemoteCatalogBatch
        {
            Categories = new[]
            {
                new RemoteCatalogCategoryWrite
                {
                    RemoteCategoryId = "category-1",
                    Name = "Category 1",
                    RemoteUpdatedAt = "2026-07-14T10:00:00Z"
                }
            },
            Suppliers = new[]
            {
                new RemoteCatalogSupplierWrite
                {
                    RemoteSupplierId = "supplier-1",
                    Name = "Supplier 1",
                    RemoteUpdatedAt = "2026-07-14T10:00:00Z"
                }
            },
            Products = new[] { Product("product-1", "BATCH-001", 5) },
            Prices = new[]
            {
                new RemoteCatalogPriceWrite
                {
                    RemotePriceId = "price-1",
                    RemoteProductId = "product-1",
                    Type = "retail",
                    Price = 125,
                    EffectiveAt = "2026-07-14T10:00:00Z",
                    Source = "catalog_pull"
                }
            }
        };

        var first = await repository.ApplyAsync(batch);
        var retry = await repository.ApplyAsync(batch);

        Assert.AreEqual(1, first.ProductsApplied);
        Assert.AreEqual(1, retry.ProductsApplied);
        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await ScalarAsync(verify,
            "SELECT COUNT(1) FROM products WHERE remote_product_id = 'product-1' AND is_active = 1;"));
        Assert.AreEqual(1L, await ScalarAsync(verify,
            "SELECT COUNT(1) FROM categories WHERE remote_category_id = 'category-1';"));
        Assert.AreEqual(1L, await ScalarAsync(verify,
            "SELECT COUNT(1) FROM suppliers WHERE remote_supplier_id = 'supplier-1';"));
        Assert.AreEqual(1L, await ScalarAsync(verify,
            "SELECT COUNT(1) FROM product_price_history WHERE remote_price_id = 'price-1';"));
        Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM products p
JOIN product_meta m ON m.barcode = p.barcode
WHERE p.remote_product_id = 'product-1'
  AND p.unitPrice = 100
  AND m.purchase_price = 50
  AND m.stock_qty = 5;"));
        Assert.AreEqual(0L, await ScalarAsync(verify,
            "SELECT COUNT(1) FROM remote_catalog_pending_prices;"));
        Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_meta m
JOIN categories c ON c.id = m.category_id
JOIN suppliers s ON s.id = m.supplier_id
WHERE m.barcode = 'BATCH-001'
  AND c.remote_category_id = 'category-1'
  AND s.remote_supplier_id = 'supplier-1';"));
    }

    [TestMethod]
    public async Task ApplyAsync_MidBatchFailureRollsBackAndCleanRetrySucceeds()
    {
        using var db = TestDb.Create();
        var repository = new RemoteCatalogBatchRepository(db.Factory);
        var failingBatch = new RemoteCatalogBatch
        {
            Categories = new[]
            {
                new RemoteCatalogCategoryWrite
                {
                    RemoteCategoryId = "category-rollback",
                    Name = "Rollback Category",
                    RemoteUpdatedAt = "2026-07-14T10:00:00Z"
                }
            },
            Products = new[]
            {
                Product("product-good", "ROLLBACK-001", 2),
                Product("product-reserved", "DISC:ROLLBACK", 3)
            }
        };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => repository.ApplyAsync(failingBatch));

        using (var rolledBack = db.Factory.Open())
        {
            Assert.AreEqual(0L, await ScalarAsync(rolledBack, "SELECT COUNT(1) FROM products;"));
            Assert.AreEqual(0L, await ScalarAsync(rolledBack, "SELECT COUNT(1) FROM product_meta;"));
            Assert.AreEqual(0L, await ScalarAsync(rolledBack,
                "SELECT COUNT(1) FROM categories WHERE remote_category_id = 'category-rollback';"));
        }

        var retry = await repository.ApplyAsync(new RemoteCatalogBatch
        {
            Products = new[] { Product("product-good", "ROLLBACK-001", 2) }
        });

        Assert.AreEqual(1, retry.ProductsApplied);
        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await ScalarAsync(verify, "SELECT COUNT(1) FROM products;"));
        Assert.AreEqual(1L, await ScalarAsync(verify, "SELECT COUNT(1) FROM product_meta;"));
    }

    [TestMethod]
    public async Task ApplyAsync_PreservesPendingLocalStockAcrossRemoteBarcodeChange()
    {
        using var db = TestDb.Create();
        var repository = new RemoteCatalogBatchRepository(db.Factory);
        await repository.ApplyAsync(new RemoteCatalogBatch
        {
            Products = new[] { Product("product-stock", "STOCK-OLD", 10) }
        });

        using (var seed = db.Factory.Open())
        {
            await seed.ExecuteAsync(@"
UPDATE product_meta SET stock_qty = 7 WHERE barcode = 'STOCK-OLD';
INSERT INTO sales(client_sale_id, code, createdAt, total, paidCash, paidCard, change, sync_status)
VALUES('stock-sale', 'STOCK-SALE', 1, 100, 100, 0, 0, 'pending');
INSERT INTO sales_sync_outbox(
  sale_id, client_sale_id, idempotency_key, status, attempt_count,
  next_retry_at, created_at, updated_at)
VALUES(last_insert_rowid(), 'stock-sale', 'stock-sale:pos-sales-ledger-v2', 'pending', 0, 0, 1, 1);
INSERT INTO local_stock_movements(
  movement_key, sale_id, barcode, quantity_delta, movement_kind, created_at)
VALUES('stock-sale:1:sale', (SELECT id FROM sales WHERE client_sale_id = 'stock-sale'),
       'STOCK-OLD', -1, 'sale', 1);");
        }

        await repository.ApplyAsync(new RemoteCatalogBatch
        {
            Products = new[] { Product("product-stock", "STOCK-NEW", 100) }
        });

        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await ScalarAsync(verify,
            "SELECT COUNT(1) FROM products WHERE remote_product_id = 'product-stock';"));
        Assert.AreEqual(1L, await ScalarAsync(verify,
            "SELECT COUNT(1) FROM products WHERE remote_product_id = 'product-stock' AND barcode = 'STOCK-NEW' AND is_active = 1;"));
        Assert.AreEqual(7L, await ScalarAsync(verify,
            "SELECT stock_qty FROM product_meta WHERE barcode = 'STOCK-NEW';"));
        Assert.AreEqual(0L, await ScalarAsync(verify,
            "SELECT COUNT(1) FROM product_meta WHERE barcode = 'STOCK-OLD';"));
    }

    [TestMethod]
    public async Task ApplyAsync_ReplaysPendingPriceWhenProductArrives()
    {
        using var db = TestDb.Create();
        var repository = new RemoteCatalogBatchRepository(db.Factory);
        var queued = await repository.ApplyAsync(new RemoteCatalogBatch
        {
            Prices = new[]
            {
                new RemoteCatalogPriceWrite
                {
                    RemotePriceId = "price-pending",
                    RemoteProductId = "product-pending",
                    Type = "retail",
                    Price = 450,
                    EffectiveAt = "2026-07-14T10:00:00Z",
                    Source = "catalog_pull"
                }
            }
        });

        Assert.AreEqual(1, queued.PricesQueued);
        var replayed = await repository.ApplyAsync(new RemoteCatalogBatch
        {
            Products = new[] { Product("product-pending", "PENDING-001", 1) }
        });

        Assert.AreEqual(1, replayed.PendingPricesApplied);
        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await ScalarAsync(verify,
            "SELECT COUNT(1) FROM remote_catalog_pending_prices;"));
        Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_price_history
WHERE remote_price_id = 'price-pending'
  AND barcode = 'PENDING-001'
  AND new_price = 450;"));
    }

    [TestMethod]
    public async Task ApplyAsync_TombstonesRemainSoftDeletedAndCancellationBeforeBoundaryWritesNothing()
    {
        using var db = TestDb.Create();
        var repository = new RemoteCatalogBatchRepository(db.Factory);
        await repository.ApplyAsync(new RemoteCatalogBatch
        {
            Categories = new[]
            {
                new RemoteCatalogCategoryWrite
                {
                    RemoteCategoryId = "category-delete",
                    Name = "Delete Category",
                    RemoteUpdatedAt = "2026-07-14T10:00:00Z"
                }
            },
            Suppliers = new[]
            {
                new RemoteCatalogSupplierWrite
                {
                    RemoteSupplierId = "supplier-delete",
                    Name = "Delete Supplier",
                    RemoteUpdatedAt = "2026-07-14T10:00:00Z"
                }
            },
            Products = new[] { Product("product-delete", "DELETE-001", 1) }
        });

        var deleted = await repository.ApplyAsync(new RemoteCatalogBatch
        {
            ProductTombstones = new[]
            {
                new RemoteCatalogProductTombstoneWrite
                {
                    RemoteProductId = "product-delete",
                    RemoteDeletedAt = "2026-07-14T11:00:00Z"
                }
            },
            CategoryTombstones = new[]
            {
                new RemoteCatalogCategoryTombstoneWrite
                {
                    RemoteCategoryId = "category-delete",
                    RemoteDeletedAt = "2026-07-14T11:00:00Z",
                    RemoteUpdatedAt = "2026-07-14T11:00:00Z"
                }
            },
            SupplierTombstones = new[]
            {
                new RemoteCatalogSupplierTombstoneWrite
                {
                    RemoteSupplierId = "supplier-delete",
                    RemoteDeletedAt = "2026-07-14T11:00:00Z",
                    RemoteUpdatedAt = "2026-07-14T11:00:00Z"
                }
            }
        });

        Assert.AreEqual(3, deleted.TombstonesApplied);
        using (var verify = db.Factory.Open())
        {
            Assert.AreEqual(1L, await ScalarAsync(verify,
                "SELECT COUNT(1) FROM products WHERE remote_product_id = 'product-delete' AND is_active = 0;"));
            Assert.AreEqual(1L, await ScalarAsync(verify,
                "SELECT COUNT(1) FROM categories WHERE remote_category_id = 'category-delete' AND is_active = 0;"));
            Assert.AreEqual(1L, await ScalarAsync(verify,
                "SELECT COUNT(1) FROM suppliers WHERE remote_supplier_id = 'supplier-delete' AND is_active = 0;"));
            Assert.AreEqual(1L, await ScalarAsync(verify,
                "SELECT COUNT(1) FROM product_meta WHERE barcode = 'DELETE-001';"));
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => repository.ApplyAsync(
            new RemoteCatalogBatch
            {
                Products = new[] { Product("product-cancelled", "CANCELLED-001", 1) }
            },
            cancellation.Token));

        using var afterCancellation = db.Factory.Open();
        Assert.AreEqual(0L, await ScalarAsync(afterCancellation,
            "SELECT COUNT(1) FROM products WHERE remote_product_id = 'product-cancelled';"));
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
            CategoryName = "Category 1",
            Name = "Product " + barcode,
            PurchasePrice = 50,
            RemoteProductId = remoteProductId,
            SecondName = string.Empty,
            StockQuantity = stockQuantity,
            SupplierName = "Supplier 1",
            UnitPrice = 100
        };
    }

    private static Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        return connection.ExecuteScalarAsync<long>(sql);
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
            var root = Path.Combine(Path.GetTempPath(), "win7pos-batch-tests-" + Guid.NewGuid().ToString("N"));
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
