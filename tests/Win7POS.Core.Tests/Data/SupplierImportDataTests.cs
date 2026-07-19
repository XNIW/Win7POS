using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class SupplierImportDataTests
{
    [TestMethod]
    public async Task ProductRepository_UpsertAsync_ReactivatesSoftDeletedBarcode()
    {
        using var db = TestDb.Create();
        var products = new ProductRepository(db.Factory);
        var id = await products.UpsertAsync(new Product { Barcode = "SOFT-001", Name = "Old", UnitPrice = 100 });
        Assert.IsTrue(await products.DeleteByBarcodeAsync("SOFT-001"));
        Assert.IsNull(await products.GetByBarcodeAsync("SOFT-001"));

        var updatedId = await products.UpsertAsync(new Product { Barcode = "SOFT-001", Name = "New", UnitPrice = 200 });

        Assert.AreEqual(id, updatedId);
        Assert.AreEqual(1, await ScalarLongAsync(db.Factory, "SELECT COUNT(1) FROM products WHERE barcode = 'SOFT-001'"));
        Assert.AreEqual(1, await ScalarLongAsync(db.Factory, "SELECT COALESCE(is_active, 1) FROM products WHERE barcode = 'SOFT-001'"));
        Assert.AreEqual("", await ScalarStringAsync(db.Factory, "SELECT COALESCE(remote_deleted_at, '') FROM products WHERE barcode = 'SOFT-001'"));
        var product = await products.GetByBarcodeAsync("SOFT-001");
        Assert.AreEqual("New", product!.Name);
        Assert.AreEqual(200, product.UnitPrice);
    }

    [TestMethod]
    public async Task SupplierExcelImportApplier_Apply_ReactivatesSoftDeletedExistingProduct()
    {
        using var db = TestDb.Create();
        var products = new ProductRepository(db.Factory);
        await products.UpsertProductAndMetaInTransactionAsync(
            new Product { Barcode = "SOFT-APPLY", Name = "Old", UnitPrice = 100 },
            "OLD",
            "Old second",
            50,
            null,
            "Old supplier",
            null,
            "Old category",
            1);
        Assert.IsTrue(await products.DeleteByBarcodeAsync("SOFT-APPLY"));

        var existing = await products.ListDetailsByBarcodesAsync(new[] { "SOFT-APPLY" });
        Assert.AreEqual(1, existing.Count);
        Assert.IsFalse(existing[0].IsActive);

        var row = new SupplierImportEditableRow
        {
            RowNumber = 2,
            Barcode = "SOFT-APPLY",
            ItemNumber = "NEW",
            ProductName = "Reactivated",
            SecondProductName = "New second",
            PurchasePrice = "80",
            RetailPrice = "180",
            Quantity = "3"
        };
        var preview = SupplierImportAnalyzer.BuildSyncPreview(new[] { row }, existing);
        Assert.AreEqual(1, preview.UpdatedProducts.Count);
        Assert.AreEqual(0, preview.NewProducts.Count);

        var result = await new SupplierExcelImportApplier(db.Factory).ApplyAsync(
            preview,
            new SupplierExcelImportApplyOptions { InsertNew = true });

        Assert.AreEqual(0, result.Errors);
        Assert.AreEqual(0, result.Inserted);
        Assert.AreEqual(1, result.Updated);
        Assert.AreEqual(1, await ScalarLongAsync(db.Factory, "SELECT COALESCE(is_active, 1) FROM products WHERE barcode = 'SOFT-APPLY'"));
        Assert.AreEqual("", await ScalarStringAsync(db.Factory, "SELECT COALESCE(remote_deleted_at, '') FROM products WHERE barcode = 'SOFT-APPLY'"));
        Assert.AreEqual(1, await ScalarLongAsync(db.Factory, "SELECT COUNT(1) FROM products WHERE barcode = 'SOFT-APPLY'"));
        Assert.IsTrue(await ScalarLongAsync(db.Factory, "SELECT COUNT(1) FROM product_price_history WHERE barcode = 'SOFT-APPLY'") >= 2);
    }

    [TestMethod]
    public async Task CatalogImportOutboxRepository_EnqueueAsync_DuplicateSameEntryReturnsSameId_ConflictFails()
    {
        using var db = TestDb.Create();
        var repository = new CatalogImportOutboxRepository(db.Factory);
        var entry = BuildCatalogEntry("same", 1);

        var first = await repository.EnqueueAsync(entry);
        var second = await repository.EnqueueAsync(entry);

        Assert.AreEqual(first, second);
        Assert.AreEqual(1, await ScalarLongAsync(db.Factory, "SELECT COUNT(1) FROM catalog_import_outbox"));

        var conflict = BuildCatalogEntry("different", 2);
        conflict.ClientImportId = entry.ClientImportId;
        conflict.IdempotencyKey = entry.IdempotencyKey;
        await AssertThrowsInvalidOperationAsync(() => repository.EnqueueAsync(conflict));
    }

    [TestMethod]
    public async Task CatalogImportOutboxRepository_ByIdClaimFindsRowsBeyondPendingPageLimit()
    {
        using var db = TestDb.Create();
        var repository = new CatalogImportOutboxRepository(db.Factory);
        long targetId = 0;
        for (var index = 1; index <= 51; index++)
        {
            targetId = await repository.EnqueueAsync(BuildCatalogEntry(
                "by-id-" + index.ToString(CultureInfo.InvariantCulture),
                100 + index));
        }

        Assert.IsTrue(await repository.MarkOriginBlockedAsync(
            targetId,
            "origin_shop_mismatch",
            1767225600000L));
        Assert.AreEqual("failed_blocked", await ScalarStringAsync(
            db.Factory,
            "SELECT status FROM catalog_import_outbox WHERE id = " +
            targetId.ToString(CultureInfo.InvariantCulture)));
        Assert.AreEqual(50, await ScalarLongAsync(
            db.Factory,
            "SELECT COUNT(1) FROM catalog_import_outbox WHERE status = 'pending'"));
    }

    [TestMethod]
    public async Task CatalogImportOutboxRepository_PrepareAttemptAsync_LeasesInProgressRows()
    {
        using var db = TestDb.Create();
        var repository = new CatalogImportOutboxRepository(db.Factory);
        var nowMs = 1767225600000L;
        var id = await repository.EnqueueAsync(BuildCatalogEntry("lease", 3));

        Assert.IsTrue(await repository.PrepareAttemptAsync(id, nowMs));
        Assert.IsFalse(await repository.PrepareAttemptAsync(id, nowMs + 1000));

        var immediate = await repository.GetPendingAsync(10, nowMs + 2000);
        Assert.IsFalse(immediate.Any(row => row.Id == id), "Fresh in_progress rows must stay leased.");

        var afterLease = await repository.GetPendingAsync(10, nowMs + (16 * 60 * 1000));
        Assert.IsTrue(afterLease.Any(row => row.Id == id), "Stale in_progress rows must be recoverable.");

        var recoveredAt = nowMs + (17 * 60 * 1000);
        Assert.IsTrue(await repository.PrepareAttemptAsync(id, recoveredAt));
        Assert.AreEqual(2, await ScalarLongAsync(db.Factory, "SELECT attempt_count FROM catalog_import_outbox WHERE id = " + id.ToString(CultureInfo.InvariantCulture)));
        Assert.IsFalse(await repository.MarkAckedAsync(id, "late-first-attempt", "server-request-late", recoveredAt + 1000, expectedAttemptCount: 1));
        Assert.IsTrue(await repository.MarkAckedAsync(id, "server-acked", "server-request-acked", recoveredAt + 2000, expectedAttemptCount: 2));
        Assert.AreEqual("acked", await ScalarStringAsync(db.Factory, "SELECT status FROM catalog_import_outbox WHERE id = " + id.ToString(CultureInfo.InvariantCulture)));
    }

    [TestMethod]
    public async Task CatalogImportOutboxRepository_Transitions_DoNotOverwriteAckedRows()
    {
        using var db = TestDb.Create();
        var repository = new CatalogImportOutboxRepository(db.Factory);
        var nowMs = 1767225600000L;
        var id = await repository.EnqueueAsync(BuildCatalogEntry("acked-race", 4));

        Assert.IsTrue(await repository.PrepareAttemptAsync(id, nowMs));
        Assert.IsTrue(await repository.MarkAckedAsync(id, "server-acked", "server-request-acked", nowMs + 1000, expectedAttemptCount: 1));
        Assert.AreEqual(1, await ScalarLongAsync(
            db.Factory,
            "SELECT CAST(value AS INTEGER) FROM app_settings WHERE key = '" +
            CatalogShopStateRepository.ImportAckGenerationKey + "'"));
        Assert.IsFalse(await repository.MarkAckedAsync(
            id,
            "server-duplicate",
            "server-request-duplicate",
            nowMs + 1500,
            expectedAttemptCount: 1));
        Assert.AreEqual(1, await ScalarLongAsync(
            db.Factory,
            "SELECT CAST(value AS INTEGER) FROM app_settings WHERE key = '" +
            CatalogShopStateRepository.ImportAckGenerationKey + "'"));
        Assert.IsFalse(await repository.MarkRetryAsync(id, "timeout", nowMs + 30000, nowMs + 2000, expectedAttemptCount: 1));
        Assert.IsFalse(await repository.MarkBlockedAsync(id, "late_block", nowMs + 3000, expectedAttemptCount: 1));

        Assert.AreEqual("acked", await ScalarStringAsync(db.Factory, "SELECT status FROM catalog_import_outbox WHERE id = " + id.ToString(CultureInfo.InvariantCulture)));
    }

    [TestMethod]
    public async Task CatalogImportCancelledClaim_ReleasesAttemptTwelveWithoutBlocking()
    {
        using var db = TestDb.Create();
        var repository = new CatalogImportOutboxRepository(db.Factory);
        const long nowMs = 1767225600000L;
        var id = await repository.EnqueueAsync(BuildCatalogEntry("cancel-at-limit", 41));
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
UPDATE catalog_import_outbox
SET status = 'retry', attempt_count = 11, next_retry_at = 0, updated_at = @updatedAt
WHERE id = @id;",
                new { id, updatedAt = nowMs - 1 });
        }

        Assert.IsTrue(await repository.PrepareAttemptAsync(id, nowMs));
        Assert.IsTrue(await repository.ReleaseAttemptAsync(
            id,
            "cancelled",
            nowMs,
            nowMs,
            expectedAttemptCount: 12));
        Assert.IsFalse(await repository.ReleaseAttemptAsync(
            id,
            "cancelled",
            nowMs,
            nowMs,
            expectedAttemptCount: 12));
        Assert.AreEqual("retry", await ScalarStringAsync(
            db.Factory,
            "SELECT status FROM catalog_import_outbox WHERE id = " + id.ToString(CultureInfo.InvariantCulture)));
        Assert.AreEqual(11, await ScalarLongAsync(
            db.Factory,
            "SELECT attempt_count FROM catalog_import_outbox WHERE id = " + id.ToString(CultureInfo.InvariantCulture)));
    }

    [TestMethod]
    public async Task CatalogImportOutboxRepository_AckAttemptMatch_AppliesRemoteIds()
    {
        using var db = TestDb.Create();
        var repository = new CatalogImportOutboxRepository(db.Factory);
        var products = new ProductRepository(db.Factory);
        var nowMs = 1767225600000L;
        var id = await repository.EnqueueAsync(BuildCatalogEntry("remote-ids", 5));
        await products.UpsertAsync(new Product { Barcode = "CAT-5", Name = "Catalog Remote", UnitPrice = 180 });
        await products.InsertPriceHistoryAsync("CAT-5", "retail", 180, "IMPORT");

        Assert.IsTrue(await repository.PrepareAttemptAsync(id, nowMs));
        var ack = new CatalogImportAckResult
        {
            ServerImportId = "server-import-remote",
            ServerRequestId = "server-request-remote",
            RemoteProductIds = new[]
            {
                new CatalogImportRemoteProductId
                {
                    Barcode = "CAT-5",
                    ClientItemId = "test-import-remote-ids-item",
                    RemoteProductId = "remote-product-5"
                }
            },
            RemotePriceIds = new[]
            {
                new CatalogImportRemotePriceId
                {
                    Barcode = "CAT-5",
                    ClientItemId = "test-import-remote-ids-item",
                    PriceType = "retail",
                    RemotePriceId = "remote-price-5"
                }
            }
        };

        Assert.IsFalse(await repository.MarkAckedAsync(id, ack, nowMs + 1000, expectedAttemptCount: 2));
        Assert.AreEqual("", await ScalarStringAsync(db.Factory, "SELECT COALESCE(remote_product_id, '') FROM products WHERE barcode = 'CAT-5'"));
        Assert.AreEqual("", await ScalarStringAsync(db.Factory, "SELECT COALESCE(remote_price_id, '') FROM product_price_history WHERE barcode = 'CAT-5' ORDER BY id DESC LIMIT 1"));

        Assert.IsTrue(await repository.MarkAckedAsync(id, ack, nowMs + 2000, expectedAttemptCount: 1));
        Assert.AreEqual("acked", await ScalarStringAsync(db.Factory, "SELECT status FROM catalog_import_outbox WHERE id = " + id.ToString(CultureInfo.InvariantCulture)));
        Assert.AreEqual("server-request-remote", await ScalarStringAsync(db.Factory, "SELECT COALESCE(server_request_id, '') FROM catalog_import_outbox WHERE id = " + id.ToString(CultureInfo.InvariantCulture)));
        Assert.AreEqual("remote-product-5", await ScalarStringAsync(db.Factory, "SELECT COALESCE(remote_product_id, '') FROM products WHERE barcode = 'CAT-5'"));
        Assert.AreEqual("remote-price-5", await ScalarStringAsync(db.Factory, "SELECT COALESCE(remote_price_id, '') FROM product_price_history WHERE barcode = 'CAT-5' ORDER BY id DESC LIMIT 1"));
        Assert.AreEqual("remote-product-5", await ScalarStringAsync(db.Factory, @"
SELECT remote_product_id
FROM remote_catalog_price_ownership
WHERE remote_price_id = 'remote-price-5';"));

        var effectiveAt = await ScalarStringAsync(db.Factory, @"
SELECT timestamp
FROM product_price_history
WHERE remote_price_id = 'remote-price-5';");
        var replay = await products.UpsertOrQueueRemotePriceHistoryAsync(
            "remote-product-5",
            "remote-price-5",
            "retail",
            180,
            effectiveAt,
            "IMPORT");
        Assert.IsTrue(replay.Applied);
        Assert.IsFalse(replay.Queued);
        Assert.AreEqual(1, await ScalarLongAsync(db.Factory, @"
SELECT COUNT(1)
FROM product_price_history
WHERE remote_price_id = 'remote-price-5';"));
    }

    [TestMethod]
    public async Task CatalogImportOutboxRepository_AckRemotePriceIds_UsesClientItemIdentityForLateAck()
    {
        using var db = TestDb.Create();
        var repository = new CatalogImportOutboxRepository(db.Factory);
        var nowMs = 1767225600000L;
        var entry = BuildCatalogEntry("late-price-ack", 6);
        var outboxId = await repository.EnqueueAsync(entry);
        await new ProductRepository(db.Factory).UpsertAsync(new Product
        {
            Barcode = "CAT-6",
            Name = "Late ACK product",
            UnitPrice = 180
        });
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
INSERT INTO product_price_history(
  barcode, timestamp, type, old_price, new_price, source,
  catalog_import_client_item_id, catalog_import_idempotency_key)
VALUES(
  'CAT-6', '2026-01-01T00:00:01Z', 'retail', NULL, 180, 'IMPORT',
  @clientItemId, @idempotencyKey);

INSERT INTO product_price_history(barcode, timestamp, type, old_price, new_price, source)
VALUES('CAT-6', '2026-01-01T00:00:02Z', 'retail', 180, 190, 'IMPORT');",
                new
                {
                    clientItemId = "test-import-late-price-ack-item",
                    idempotencyKey = entry.IdempotencyKey
                });
        }

        Assert.IsTrue(await repository.PrepareAttemptAsync(outboxId, nowMs));
        var ack = new CatalogImportAckResult
        {
            ServerImportId = "server-import-late-price",
            ServerRequestId = "server-request-late-price",
            RemoteProductIds = new[]
            {
                new CatalogImportRemoteProductId
                {
                    Barcode = "CAT-6",
                    ClientItemId = "test-import-late-price-ack-item",
                    RemoteProductId = "remote-product-late-price"
                }
            },
            RemotePriceIds = new[]
            {
                new CatalogImportRemotePriceId
                {
                    Barcode = "CAT-6",
                    ClientItemId = "test-import-late-price-ack-item",
                    PriceType = "retail",
                    RemotePriceId = "remote-price-original"
                }
            }
        };

        Assert.IsTrue(await repository.MarkAckedAsync(outboxId, ack, nowMs + 1000, expectedAttemptCount: 1));
        Assert.AreEqual(
            "remote-price-original",
            await ScalarStringAsync(db.Factory, "SELECT COALESCE(remote_price_id, '') FROM product_price_history WHERE barcode = 'CAT-6' AND catalog_import_client_item_id = 'test-import-late-price-ack-item'"));
        Assert.AreEqual(
            "",
            await ScalarStringAsync(db.Factory, "SELECT COALESCE(remote_price_id, '') FROM product_price_history WHERE barcode = 'CAT-6' AND catalog_import_client_item_id IS NULL ORDER BY id DESC LIMIT 1"));
        Assert.AreEqual(
            "remote-product-late-price",
            await ScalarStringAsync(db.Factory, @"
SELECT remote_product_id
FROM remote_catalog_price_ownership
WHERE remote_price_id = 'remote-price-original';"));
    }

    [TestMethod]
    public async Task CatalogImportOutboxRepository_AckRemotePriceWithoutOwnerRollsBackAckAndMapping()
    {
        using var db = TestDb.Create();
        var repository = new CatalogImportOutboxRepository(db.Factory);
        var nowMs = 1767225600000L;
        var entry = BuildCatalogEntry("missing-price-owner", 7);
        var outboxId = await repository.EnqueueAsync(entry);
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
INSERT INTO product_price_history(
  barcode, timestamp, type, old_price, new_price, source,
  catalog_import_client_item_id, catalog_import_idempotency_key)
VALUES(
  'CAT-7', '2026-01-01T00:00:01Z', 'retail', NULL, 280, 'IMPORT',
  @clientItemId, @idempotencyKey);",
                new
                {
                    clientItemId = "test-import-missing-price-owner-item",
                    idempotencyKey = entry.IdempotencyKey
                });
        }

        Assert.IsTrue(await repository.PrepareAttemptAsync(outboxId, nowMs));
        var ack = new CatalogImportAckResult
        {
            ServerImportId = "server-import-missing-owner",
            ServerRequestId = "server-request-missing-owner",
            RemotePriceIds = new[]
            {
                new CatalogImportRemotePriceId
                {
                    Barcode = "CAT-7",
                    ClientItemId = "test-import-missing-price-owner-item",
                    PriceType = "retail",
                    RemotePriceId = "remote-price-missing-owner"
                }
            }
        };

        await AssertThrowsInvalidOperationAsync(() =>
            repository.MarkAckedAsync(outboxId, ack, nowMs + 1000, expectedAttemptCount: 1));
        Assert.AreEqual("in_progress", await ScalarStringAsync(db.Factory,
            "SELECT status FROM catalog_import_outbox WHERE id = " + outboxId.ToString(CultureInfo.InvariantCulture)));
        Assert.AreEqual("", await ScalarStringAsync(db.Factory, @"
SELECT COALESCE(remote_price_id, '')
FROM product_price_history
WHERE barcode = 'CAT-7';"));
        Assert.AreEqual(0, await ScalarLongAsync(db.Factory, @"
SELECT COUNT(1)
FROM remote_catalog_price_ownership
WHERE remote_price_id = 'remote-price-missing-owner';"));
    }

    [TestMethod]
    public async Task CatalogImportOutboxRepository_UnmatchedRemotePriceMappingRollsBackAck()
    {
        using var db = TestDb.Create();
        var repository = new CatalogImportOutboxRepository(db.Factory);
        var nowMs = 1767225600000L;
        var entry = BuildCatalogEntry("unmatched-price", 8);
        var outboxId = await repository.EnqueueAsync(entry);
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
INSERT INTO products(barcode, name, unitPrice, remote_product_id, is_active)
VALUES('CAT-8', 'Unmatched mapping product', 380, 'remote-product-8', 1);");
        }

        Assert.IsTrue(await repository.PrepareAttemptAsync(outboxId, nowMs));
        var ack = new CatalogImportAckResult
        {
            ServerImportId = "server-import-unmatched",
            ServerRequestId = "server-request-unmatched",
            RemotePriceIds = new[]
            {
                new CatalogImportRemotePriceId
                {
                    Barcode = "CAT-8",
                    ClientItemId = "test-import-unmatched-price-item",
                    PriceType = "retail",
                    RemotePriceId = "remote-price-unmatched"
                }
            }
        };

        await AssertThrowsInvalidOperationAsync(() =>
            repository.MarkAckedAsync(outboxId, ack, nowMs + 1000, expectedAttemptCount: 1));
        Assert.AreEqual("in_progress", await ScalarStringAsync(db.Factory,
            "SELECT status FROM catalog_import_outbox WHERE id = " + outboxId.ToString(CultureInfo.InvariantCulture)));
        Assert.AreEqual(0, await ScalarLongAsync(db.Factory, @"
SELECT COUNT(1)
FROM remote_catalog_price_ownership
WHERE remote_price_id = 'remote-price-unmatched';"));
    }

    [TestMethod]
    public async Task CatalogImportOutboxRepository_ConflictingExistingRemotePriceMappingRollsBackAck()
    {
        using var db = TestDb.Create();
        var repository = new CatalogImportOutboxRepository(db.Factory);
        var nowMs = 1767225600000L;
        var entry = BuildCatalogEntry("conflicting-price", 9);
        var outboxId = await repository.EnqueueAsync(entry);
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
INSERT INTO products(barcode, name, unitPrice, remote_product_id, is_active)
VALUES('CAT-9', 'Conflicting mapping product', 480, 'remote-product-9', 1);

INSERT INTO product_price_history(
  barcode, timestamp, type, old_price, new_price, source,
  catalog_import_client_item_id, catalog_import_idempotency_key)
VALUES(
  'CAT-9', '2026-01-01T00:00:01Z', 'retail', NULL, 480, 'IMPORT',
  @clientItemId, @idempotencyKey);

INSERT INTO product_price_history(
  barcode, timestamp, type, old_price, new_price, source, remote_price_id)
VALUES(
  'CAT-9', '2026-01-01T00:00:02Z', 'PURCHASE', NULL, 280, 'IMPORT',
  'remote-price-conflicting');

INSERT INTO remote_catalog_price_ownership(remote_price_id, remote_product_id)
VALUES('remote-price-conflicting', 'remote-product-9');",
                new
                {
                    clientItemId = "test-import-conflicting-price-item",
                    idempotencyKey = entry.IdempotencyKey
                });
        }

        Assert.IsTrue(await repository.PrepareAttemptAsync(outboxId, nowMs));
        var ack = new CatalogImportAckResult
        {
            ServerImportId = "server-import-conflicting",
            ServerRequestId = "server-request-conflicting",
            RemotePriceIds = new[]
            {
                new CatalogImportRemotePriceId
                {
                    Barcode = "CAT-9",
                    ClientItemId = "test-import-conflicting-price-item",
                    PriceType = "retail",
                    RemotePriceId = "remote-price-conflicting"
                }
            }
        };

        await AssertThrowsInvalidOperationAsync(() =>
            repository.MarkAckedAsync(outboxId, ack, nowMs + 1000, expectedAttemptCount: 1));
        Assert.AreEqual("in_progress", await ScalarStringAsync(db.Factory,
            "SELECT status FROM catalog_import_outbox WHERE id = " + outboxId.ToString(CultureInfo.InvariantCulture)));
        Assert.AreEqual("", await ScalarStringAsync(db.Factory, @"
SELECT COALESCE(remote_price_id, '')
FROM product_price_history
WHERE catalog_import_client_item_id = 'test-import-conflicting-price-item';"));
        Assert.AreEqual(1, await ScalarLongAsync(db.Factory, @"
SELECT COUNT(1)
FROM product_price_history
WHERE remote_price_id = 'remote-price-conflicting'
  AND type = 'PURCHASE';"));
    }

    [TestMethod]
    public void CatalogImportSyncService_AckMapping_PrefersLocalClientItemBarcode()
    {
        var request = new PosCatalogImportRequest
        {
            Items = new[]
            {
                new PosCatalogImportItemRequest
                {
                    Barcode = "CAT-LOCAL",
                    ClientItemId = "client-item-1",
                    Operation = "upsert_product",
                    RowNumber = 2
                }
            }
        };
        var remote = new PosCatalogImportResponse
        {
            Batch = new PosCatalogImportBatchResponse
            {
                ClientImportId = "client-import",
                IdempotencyKey = "idempotency-key",
                PosCatalogImportBatchId = "server-import"
            },
            RemoteProductIds = new[]
            {
                new PosCatalogImportRemoteProductIdAck
                {
                    Barcode = "CAT-WRONG",
                    ClientItemId = "client-item-1",
                    RemoteProductId = "remote-product"
                }
            },
            RemotePriceIds = new[]
            {
                new PosCatalogImportRemotePriceIdAck
                {
                    Barcode = "CAT-WRONG",
                    ClientItemId = "client-item-1",
                    PriceType = "retail",
                    RemotePriceId = "remote-price"
                }
            },
            ServerRequestId = "server-request"
        };
        var item = new CatalogImportOutboxItem
        {
            ClientImportId = "client-import",
            IdempotencyKey = "idempotency-key",
            PayloadHash = "payload-hash"
        };

        var ack = InvokeBuildAckResult(item, request, remote);

        Assert.AreEqual("CAT-LOCAL", ack.RemoteProductIds.Single().Barcode);
        Assert.AreEqual("CAT-LOCAL", ack.RemotePriceIds.Single().Barcode);
    }

    [TestMethod]
    public async Task CatalogImportSyncService_ExplicitPriceMapWinsLegacyItemAndPersistsOwner()
    {
        using var db = TestDb.Create();
        var repository = new CatalogImportOutboxRepository(db.Factory);
        var products = new ProductRepository(db.Factory);
        var nowMs = 1767225600000L;
        var entry = BuildCatalogEntry("explicit-price-precedence", 10);
        var outboxId = await repository.EnqueueAsync(entry);
        await products.UpsertAsync(new Product
        {
            Barcode = "CAT-10",
            Name = "Explicit price precedence",
            UnitPrice = 580
        });
        await products.InsertPriceHistoryAsync("CAT-10", "retail", 580, "IMPORT");
        Assert.IsTrue(await repository.PrepareAttemptAsync(outboxId, nowMs));

        const string clientItemId = "test-import-explicit-price-precedence-item";
        var request = new PosCatalogImportRequest
        {
            Items = new[]
            {
                new PosCatalogImportItemRequest
                {
                    Barcode = "CAT-10",
                    ClientItemId = clientItemId
                }
            }
        };
        var remote = new PosCatalogImportResponse
        {
            Batch = new PosCatalogImportBatchResponse(),
            RemoteProductIds = new[]
            {
                new PosCatalogImportRemoteProductIdAck
                {
                    Barcode = "CAT-10",
                    ClientItemId = clientItemId,
                    RemoteProductId = "remote-product-explicit"
                }
            },
            RemotePriceIds = new[]
            {
                new PosCatalogImportRemotePriceIdAck
                {
                    Barcode = "CAT-10",
                    ClientItemId = clientItemId,
                    RemotePriceId = "remote-price-explicit"
                }
            },
            Items = new[]
            {
                new PosCatalogImportItemAck
                {
                    Barcode = "CAT-10",
                    ClientItemId = clientItemId,
                    PriceType = "retail",
                    RemotePriceId = "remote-price-legacy"
                }
            },
            ServerImportId = "server-import-explicit",
            ServerRequestId = "server-request-explicit"
        };
        var ack = InvokeBuildAckResult(
            new CatalogImportOutboxItem
            {
                ClientImportId = entry.ClientImportId,
                IdempotencyKey = entry.IdempotencyKey,
                PayloadHash = entry.PayloadHash
            },
            request,
            remote);

        Assert.AreEqual(1, ack.RemotePriceIds.Count);
        Assert.AreEqual("remote-price-explicit", ack.RemotePriceIds.Single().RemotePriceId);
        Assert.IsTrue(await repository.MarkAckedAsync(
            outboxId,
            ack,
            nowMs + 1000,
            expectedAttemptCount: 1));
        Assert.AreEqual("acked", await ScalarStringAsync(db.Factory,
            "SELECT status FROM catalog_import_outbox WHERE id = " + outboxId.ToString(CultureInfo.InvariantCulture)));
        Assert.AreEqual("remote-price-explicit", await ScalarStringAsync(db.Factory, @"
SELECT COALESCE(remote_price_id, '')
FROM product_price_history
WHERE barcode = 'CAT-10'
ORDER BY id DESC
LIMIT 1;"));
        Assert.AreEqual(0, await ScalarLongAsync(db.Factory, @"
SELECT COUNT(1)
FROM product_price_history
WHERE remote_price_id = 'remote-price-legacy';"));
        Assert.AreEqual("remote-product-explicit", await ScalarStringAsync(db.Factory, @"
SELECT remote_product_id
FROM remote_catalog_price_ownership
WHERE remote_price_id = 'remote-price-explicit';"));
    }

    [TestMethod]
    public void CatalogImportSyncService_ExplicitTypedPriceMapWinsLegacyWildcardItem()
    {
        const string clientItemId = "test-import-explicit-typed-price-item";
        var request = new PosCatalogImportRequest
        {
            Items = new[]
            {
                new PosCatalogImportItemRequest
                {
                    Barcode = "CAT-11",
                    ClientItemId = clientItemId
                }
            }
        };
        var remote = new PosCatalogImportResponse
        {
            Batch = new PosCatalogImportBatchResponse(),
            RemotePriceIds = new[]
            {
                new PosCatalogImportRemotePriceIdAck
                {
                    Barcode = "CAT-11",
                    ClientItemId = clientItemId,
                    PriceType = "retail",
                    RemotePriceId = "remote-price-explicit-typed"
                }
            },
            Items = new[]
            {
                new PosCatalogImportItemAck
                {
                    Barcode = "CAT-11",
                    ClientItemId = clientItemId,
                    RemotePriceId = "remote-price-legacy-wildcard"
                }
            }
        };

        var ack = InvokeBuildAckResult(
            new CatalogImportOutboxItem
            {
                ClientImportId = "client-import",
                IdempotencyKey = "idempotency-key",
                PayloadHash = "payload-hash"
            },
            request,
            remote);

        Assert.AreEqual(1, ack.RemotePriceIds.Count);
        Assert.AreEqual("remote-price-explicit-typed", ack.RemotePriceIds.Single().RemotePriceId);
        Assert.AreEqual("retail", ack.RemotePriceIds.Single().PriceType);
    }

    [TestMethod]
    public void CatalogImportSyncService_RemoteBatchValidation_RequiresPayloadHashAndAttempt()
    {
        var item = new CatalogImportOutboxItem
        {
            AttemptCount = 0,
            ClientImportId = "client-import",
            IdempotencyKey = "idempotency-key",
            PayloadHash = "expected-hash"
        };

        Assert.AreEqual(
            "payload_hash_mismatch",
            InvokeRemoteBatchMismatchCode(item, new PosCatalogImportBatchResponse
            {
                ClientImportId = "client-import",
                IdempotencyKey = "idempotency-key"
            }));
        Assert.AreEqual(
            "attempt_count_mismatch",
            InvokeRemoteBatchMismatchCode(item, new PosCatalogImportBatchResponse
            {
                ClientImportId = "client-import",
                IdempotencyKey = "idempotency-key",
                PayloadHash = "expected-hash"
            }));
        Assert.AreEqual(
            "",
            InvokeRemoteBatchMismatchCode(item, new PosCatalogImportBatchResponse
            {
                AttemptCount = 1,
                ClientImportId = "client-import",
                IdempotencyKey = "idempotency-key",
                PayloadHash = "expected-hash"
            }));
        Assert.AreEqual(
            "attempt_count_mismatch",
            InvokeRemoteBatchMismatchCode(item, new PosCatalogImportBatchResponse
            {
                AttemptCount = 2,
                ClientImportId = "client-import",
                IdempotencyKey = "idempotency-key",
                PayloadHash = "expected-hash"
            }));
        Assert.AreEqual(
            "payload_hash_mismatch",
            InvokeRemoteBatchMismatchCode(item, new PosCatalogImportBatchResponse
            {
                AttemptCount = 1,
                ClientImportId = "client-import",
                IdempotencyKey = "idempotency-key",
                PayloadHash = "wrong-hash"
            }));
    }

    [TestMethod]
    public void CatalogImportSyncService_ResponseShopMustMatchImmutableOrigin()
    {
        var item = new CatalogImportOutboxItem
        {
            OriginShopId = "shop-a",
            OriginShopCode = "SHOP-A"
        };
        Assert.AreEqual(string.Empty, InvokeResponseShopMismatchCode(item, new PosCatalogImportResponse
        {
            Shop = new PosShopResponse { ShopId = "shop-a", ShopCode = "shop-a" }
        }));
        Assert.AreEqual("response_shop_mismatch", InvokeResponseShopMismatchCode(item, new PosCatalogImportResponse
        {
            Shop = new PosShopResponse { ShopId = "shop-b", ShopCode = "SHOP-B" }
        }));
        Assert.AreEqual("response_shop_mismatch", InvokeResponseShopMismatchCode(item, new PosCatalogImportResponse()));
    }

    private static CatalogImportOutboxEntry BuildCatalogEntry(string name, int rowNumber)
    {
        var request = new PosCatalogImportRequest
        {
            Batch = new PosCatalogImportBatchRequest
            {
                ClientImportId = "test-import-" + name,
                CreatedAt = "2026-01-01T00:00:00.0000000Z",
                IdempotencyKey = "test-import-" + name + ":pos-catalog-import-v1",
                PreviewFingerprint = "fingerprint-" + name,
                SourceFileName = "supplier.xlsx"
            },
            Items = new[]
            {
                new PosCatalogImportItemRequest
                {
                    Barcode = "CAT-" + rowNumber.ToString(CultureInfo.InvariantCulture),
                    ChangeKind = "new",
                    ClientItemId = "test-import-" + name + "-item",
                    Operation = "upsert_product",
                    ProductName = "Catalog " + name,
                    PurchasePrice = "100",
                    RetailPrice = "180",
                    RowNumber = rowNumber
                }
            },
            SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion,
            Source = "supplier_excel",
            Summary = new PosCatalogImportSummaryRequest { NewProducts = 1 }
        };
        var payload = Serialize(request);
        return new CatalogImportOutboxEntry
        {
            ClientImportId = request.Batch.ClientImportId,
            CreatedAt = 1767225600000,
            IdempotencyKey = request.Batch.IdempotencyKey,
            PayloadHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(payload),
            PayloadJson = payload,
            SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion,
            Source = "supplier_excel"
        };
    }

    private static string Serialize<T>(T value)
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static CatalogImportAckResult InvokeBuildAckResult(
        CatalogImportOutboxItem item,
        PosCatalogImportRequest request,
        PosCatalogImportResponse response)
    {
        var method = typeof(CatalogImportSyncService).GetMethod(
            "BuildAckResult",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        return (CatalogImportAckResult)method!.Invoke(null, new object[] { item, request, response, "transport-request" })!;
    }

    private static string InvokeRemoteBatchMismatchCode(
        CatalogImportOutboxItem item,
        PosCatalogImportBatchResponse batch)
    {
        var method = typeof(CatalogImportSyncService).GetMethod(
            "GetRemoteBatchMismatchCode",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        return (string)method!.Invoke(null, new object[] { item, batch })!;
    }

    private static string InvokeResponseShopMismatchCode(
        CatalogImportOutboxItem item,
        PosCatalogImportResponse response)
    {
        var method = typeof(CatalogImportSyncService).GetMethod(
            "GetResponseShopMismatchCode",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        return (string)method!.Invoke(null, new object[] { item, response })!;
    }

    private static async Task AssertThrowsInvalidOperationAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        Assert.Fail("Expected InvalidOperationException.");
    }

    private static async Task<long> ScalarLongAsync(SqliteConnectionFactory factory, string sql)
    {
        using var conn = factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(SqliteConnectionFactory factory, string sql)
    {
        using var conn = factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync();
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private sealed class TestDb : IDisposable
    {
        private TestDb(string root)
        {
            Root = root;
            Factory = new SqliteConnectionFactory(PosDbOptions.ForPath(Path.Combine(root, "pos.db")));
            DbInitializer.EnsureCreated(PosDbOptions.ForPath(Path.Combine(root, "pos.db")));
            using var conn = Factory.Open();
            conn.Execute(@"
INSERT INTO app_settings(key, value) VALUES(@codeKey, 'TEST-SHOP')
ON CONFLICT(key) DO UPDATE SET value = excluded.value;
INSERT INTO app_settings(key, value) VALUES(@idKey, 'test-shop-id')
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new
                {
                    codeKey = OutboxShopBinding.OfficialShopCodeKey,
                    idKey = OutboxShopBinding.OfficialShopIdKey
                });
        }

        public SqliteConnectionFactory Factory { get; }
        private string Root { get; }

        public static TestDb Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "win7pos-tests-" + Guid.NewGuid().ToString("N"));
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
