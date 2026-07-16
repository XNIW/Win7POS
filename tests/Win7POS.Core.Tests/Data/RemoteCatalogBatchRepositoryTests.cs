using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Online;
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
            Assert.AreEqual(0L, await ScalarAsync(rolledBack,
                "SELECT COUNT(1) FROM remote_catalog_product_references;"));
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
        Assert.AreEqual(1L, await ScalarAsync(verify,
            "SELECT COUNT(1) FROM local_stock_movements WHERE barcode = 'STOCK-NEW';"));
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

    [TestMethod]
    public async Task ApplyAsync_ResolvesReferencesByRemoteIdWhenNamesAreDuplicated()
    {
        using var db = TestDb.Create();
        var first = Product("product-ref-a", "REF-A", 1);
        first.CategoryName = "Shared category";
        first.SupplierName = "Shared supplier";
        first.RemoteCategoryId = "category-a";
        first.RemoteSupplierId = "supplier-a";
        var second = Product("product-ref-b", "REF-B", 1);
        second.CategoryName = "Shared category";
        second.SupplierName = "Shared supplier";
        second.RemoteCategoryId = "category-b";
        second.RemoteSupplierId = "supplier-b";

        await new RemoteCatalogBatchRepository(db.Factory).ApplyAsync(new RemoteCatalogBatch
        {
            Categories = new[]
            {
                new RemoteCatalogCategoryWrite { RemoteCategoryId = "category-a", Name = "Shared category" },
                new RemoteCatalogCategoryWrite { RemoteCategoryId = "category-b", Name = "Shared category" }
            },
            Suppliers = new[]
            {
                new RemoteCatalogSupplierWrite { RemoteSupplierId = "supplier-a", Name = "Shared supplier" },
                new RemoteCatalogSupplierWrite { RemoteSupplierId = "supplier-b", Name = "Shared supplier" }
            },
            Products = new[] { first, second }
        });

        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_meta m
JOIN categories c ON c.id = m.category_id
JOIN suppliers s ON s.id = m.supplier_id
WHERE m.barcode = 'REF-A'
  AND c.remote_category_id = 'category-a'
  AND s.remote_supplier_id = 'supplier-a';"));
        Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_meta m
JOIN categories c ON c.id = m.category_id
JOIN suppliers s ON s.id = m.supplier_id
WHERE m.barcode = 'REF-B'
  AND c.remote_category_id = 'category-b'
  AND s.remote_supplier_id = 'supplier-b';"));
    }

    [TestMethod]
    public async Task ApplyAsync_ReportsMalformedPriceRowsAsSkipped()
    {
        using var db = TestDb.Create();
        var result = await new RemoteCatalogBatchRepository(db.Factory).ApplyAsync(new RemoteCatalogBatch
        {
            Prices = new RemoteCatalogPriceWrite[]
            {
                null!,
                new RemoteCatalogPriceWrite
                {
                    RemotePriceId = "invalid-price",
                    RemoteProductId = string.Empty,
                    Type = string.Empty,
                    Price = -1
                }
            }
        });

        Assert.AreEqual(2, result.PricesSkipped);
        Assert.AreEqual(0, result.PricesApplied);
        Assert.AreEqual(0, result.PricesQueued);
        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await ScalarAsync(verify, "SELECT COUNT(1) FROM product_price_history;"));
        Assert.AreEqual(0L, await ScalarAsync(verify, "SELECT COUNT(1) FROM remote_catalog_pending_prices;"));
    }

    [TestMethod]
    public async Task ApplyAsync_RelinksRemoteReferencesThatArriveOnALaterPage()
    {
        using var db = TestDb.Create();
        var repository = new RemoteCatalogBatchRepository(db.Factory);
        var lateProduct = Product("product-late-ref", "LATE-REF", 5);
        lateProduct.RemoteCategoryId = "category-late";
        lateProduct.RemoteSupplierId = "supplier-late";
        var unrelatedProduct = Product("product-unrelated", "UNRELATED-REF", 3);
        unrelatedProduct.RemoteCategoryId = "category-unrelated";
        unrelatedProduct.RemoteSupplierId = "supplier-unrelated";

        await repository.ApplyAsync(new RemoteCatalogBatch
        {
            Categories = new[]
            {
                new RemoteCatalogCategoryWrite
                {
                    RemoteCategoryId = "category-unrelated",
                    Name = "Unrelated Category"
                }
            },
            Suppliers = new[]
            {
                new RemoteCatalogSupplierWrite
                {
                    RemoteSupplierId = "supplier-unrelated",
                    Name = "Unrelated Supplier"
                }
            },
            Products = new[] { lateProduct, unrelatedProduct }
        });

        using (var before = db.Factory.Open())
        {
            Assert.AreEqual(1L, await ScalarAsync(before, @"
SELECT COUNT(1)
FROM product_meta
WHERE barcode = 'LATE-REF'
  AND category_id IS NULL
  AND supplier_id IS NULL;"));
            Assert.AreEqual(1L, await ScalarAsync(before, @"
SELECT COUNT(1)
FROM product_meta m
JOIN categories c ON c.id = m.category_id
JOIN suppliers s ON s.id = m.supplier_id
WHERE m.barcode = 'UNRELATED-REF'
  AND c.remote_category_id = 'category-unrelated'
  AND s.remote_supplier_id = 'supplier-unrelated';"));
            await before.ExecuteAsync(@"
CREATE TABLE product_meta_relink_touch_log(barcode TEXT NOT NULL);
CREATE TRIGGER product_meta_relink_touch
AFTER UPDATE OF category_id, category_name, supplier_id, supplier_name ON product_meta
BEGIN
  INSERT INTO product_meta_relink_touch_log(barcode) VALUES(NEW.barcode);
END;");
        }

        var referencePage = await repository.ApplyAsync(new RemoteCatalogBatch
        {
            Categories = new[]
            {
                new RemoteCatalogCategoryWrite
                {
                    RemoteCategoryId = "category-late",
                    Name = "Late Category"
                }
            },
            Suppliers = new[]
            {
                new RemoteCatalogSupplierWrite
                {
                    RemoteSupplierId = "supplier-late",
                    Name = "Late Supplier"
                }
            }
        });

        Assert.IsTrue(referencePage.ProductReferencesRelinked > 0);
        using (var verify = db.Factory.Open())
        {
            Assert.AreEqual(0L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_meta_relink_touch_log
WHERE barcode = 'UNRELATED-REF';"));
            Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_meta_relink_touch_log
WHERE barcode = 'LATE-REF';"));
            Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_meta m
JOIN categories c ON c.id = m.category_id
JOIN suppliers s ON s.id = m.supplier_id
WHERE m.barcode = 'LATE-REF'
  AND c.remote_category_id = 'category-late'
  AND s.remote_supplier_id = 'supplier-late';"));
        }

        var audit = await new CatalogFullRefreshReconciler(db.Factory).AuditCurrentAsync();
        Assert.AreEqual(0L, audit.RemoteProductsWithoutReferenceMap);
        Assert.AreEqual(0L, audit.InvalidCategoryReferenceMappings);
        Assert.AreEqual(0L, audit.InvalidSupplierReferenceMappings);
        Assert.AreEqual(string.Empty, CatalogExactnessVerifier.FindInvariantError(audit));
    }

    [TestMethod]
    public async Task ApplyAsync_DifferentRemoteProductsCannotCollapseOntoSameBarcodeAcrossPages()
    {
        using var db = TestDb.Create();
        var repository = new RemoteCatalogBatchRepository(db.Factory);

        var first = await repository.ApplyAsync(new RemoteCatalogBatch
        {
            Products = new[] { Product("product-identity-a", "IDENTITY-BAR", 3) }
        });
        var conflictingCaseVariant = await repository.ApplyAsync(new RemoteCatalogBatch
        {
            Products = new[] { Product("product-identity-b", "identity-bar", 4) }
        });

        Assert.AreEqual(1, first.ProductsApplied);
        Assert.AreEqual(0, conflictingCaseVariant.ProductsApplied);
        Assert.AreEqual(1, conflictingCaseVariant.ProductsSkipped);
        Assert.AreEqual(1, conflictingCaseVariant.ProductIdentityConflicts);

        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM products
WHERE remote_product_id = 'product-identity-a'
  AND barcode = 'IDENTITY-BAR'
  AND is_active = 1;"));
        Assert.AreEqual(0L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM products
WHERE remote_product_id = 'product-identity-b';"));
        Assert.AreEqual(0L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM remote_catalog_product_references
WHERE remote_product_id = 'product-identity-b';"));
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
            RemoteCategoryId = "category-1",
            RemoteProductId = remoteProductId,
            RemoteSupplierId = "supplier-1",
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
