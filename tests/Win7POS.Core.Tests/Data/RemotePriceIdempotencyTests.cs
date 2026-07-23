using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class RemotePriceIdempotencyTests
{
    private const string EffectiveAt = "2026-07-14T10:00:00Z";

    [TestMethod]
    public async Task ExistingHistory_ExactRemotePriceIdRetryIsAcceptedWithoutDuplicate()
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "product-a", "PRICE-A");
        var repository = new RemotePriceHistoryRepository(db.Factory);

        var first = await repository.UpsertOrQueueRemotePriceHistoryAsync(
            "product-a",
            "price-idempotent",
            "retail",
            125,
            EffectiveAt,
            "catalog_pull");
        var retry = await repository.UpsertOrQueueRemotePriceHistoryAsync(
            "product-a",
            "price-idempotent",
            "retail",
            125,
            EffectiveAt,
            "catalog_pull");

        Assert.IsTrue(first.Applied);
        Assert.IsFalse(first.Queued);
        Assert.IsTrue(retry.Applied);
        Assert.IsFalse(retry.Queued);
        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await ScalarAsync(verify,
            "SELECT COUNT(1) FROM product_price_history WHERE remote_price_id = 'price-idempotent';"));
        Assert.AreEqual(0L, await ScalarAsync(verify,
            "SELECT COUNT(1) FROM remote_catalog_pending_prices;"));
    }

    [TestMethod]
    public async Task RemotePriceHistoryRepository_AndProductFacade_KeepPublicApplyQueueReplayParity()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        await SeedProductAsync(directDb.Factory, "parity-applied", "PARITY-APPLIED");
        await SeedProductAsync(facadeDb.Factory, "parity-applied", "PARITY-APPLIED");

        var direct = new RemotePriceHistoryRepository(directDb.Factory);
        var facade = new ProductRepository(facadeDb.Factory);
        var directApplied = await direct.UpsertRemotePriceHistoryAsync(
            "parity-applied", "retail", 125, EffectiveAt, "catalog_pull");
        var facadeApplied = await facade.UpsertRemotePriceHistoryAsync(
            "parity-applied", "retail", 125, EffectiveAt, "catalog_pull");
        var directQueued = await direct.UpsertOrQueueRemotePriceHistoryAsync(
            "parity-queued", "parity-queued-price", "retail", 450, EffectiveAt, "catalog_pull");
        var facadeQueued = await facade.UpsertOrQueueRemotePriceHistoryAsync(
            "parity-queued", "parity-queued-price", "retail", 450, EffectiveAt, "catalog_pull");

        Assert.AreEqual(directApplied, facadeApplied);
        Assert.AreEqual(directQueued.Applied, facadeQueued.Applied);
        Assert.AreEqual(directQueued.Queued, facadeQueued.Queued);
        await SeedProductAsync(directDb.Factory, "parity-queued", "PARITY-QUEUED");
        await SeedProductAsync(facadeDb.Factory, "parity-queued", "PARITY-QUEUED");

        Assert.AreEqual(
            await direct.ApplyPendingRemotePricesAsync(),
            await facade.ApplyPendingRemotePricesAsync());
        Assert.AreEqual(
            await ScalarAsync(directDb.Factory, "SELECT COUNT(1) FROM product_price_history;"),
            await ScalarAsync(facadeDb.Factory, "SELECT COUNT(1) FROM product_price_history;"));
        Assert.AreEqual(
            await ScalarAsync(directDb.Factory, "SELECT COUNT(1) FROM remote_catalog_pending_prices;"),
            await ScalarAsync(facadeDb.Factory, "SELECT COUNT(1) FROM remote_catalog_pending_prices;"));
        Assert.AreEqual(
            await ScalarAsync(directDb.Factory, "SELECT COUNT(1) FROM remote_catalog_price_ownership;"),
            await ScalarAsync(facadeDb.Factory, "SELECT COUNT(1) FROM remote_catalog_price_ownership;"));
        Assert.AreEqual(
            await ScalarAsync(directDb.Factory, "SELECT COUNT(1) FROM product_price_history WHERE remote_price_id = 'parity-queued-price';"),
            await ScalarAsync(facadeDb.Factory, "SELECT COUNT(1) FROM product_price_history WHERE remote_price_id = 'parity-queued-price';"));
    }

    [TestMethod]
    [DataRow("product-b", "retail", 125, EffectiveAt, "catalog_pull", DisplayName = "different remote product and barcode")]
    [DataRow("product-a", "wholesale", 125, EffectiveAt, "catalog_pull", DisplayName = "different type")]
    [DataRow("product-a", "retail", 126, EffectiveAt, "catalog_pull", DisplayName = "different price")]
    [DataRow("product-a", "retail", 125, "2026-07-14T11:00:00Z", "catalog_pull", DisplayName = "different effective_at")]
    [DataRow("product-a", "retail", 125, EffectiveAt, "catalog_delta", DisplayName = "different source")]
    public async Task ExistingHistory_RemotePriceIdCollisionIsSkipped(
        string retryRemoteProductId,
        string retryType,
        int retryPrice,
        string retryEffectiveAt,
        string retrySource)
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "product-a", "PRICE-A");
        await SeedProductAsync(db.Factory, "product-b", "PRICE-B");
        var repository = new RemotePriceHistoryRepository(db.Factory);
        var first = await repository.UpsertOrQueueRemotePriceHistoryAsync(
            "product-a",
            "price-collision",
            "retail",
            125,
            EffectiveAt,
            "catalog_pull");

        Assert.IsTrue(first.Applied);
        var collision = await repository.UpsertOrQueueRemotePriceHistoryAsync(
            retryRemoteProductId,
            "price-collision",
            retryType,
            retryPrice,
            retryEffectiveAt,
            retrySource);

        AssertSkipped(collision);
        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await ScalarAsync(verify,
            "SELECT COUNT(1) FROM product_price_history WHERE remote_price_id = 'price-collision';"));
        Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_price_history
WHERE remote_price_id = 'price-collision'
  AND barcode = 'PRICE-A'
  AND type = 'RETAIL'
  AND new_price = 125
  AND timestamp = '2026-07-14T10:00:00Z'
  AND source = 'catalog_pull';"));
        Assert.AreEqual(0L, await ScalarAsync(verify,
            "SELECT COUNT(1) FROM remote_catalog_pending_prices;"));
    }

    [TestMethod]
    public async Task ExistingHistory_SameRemoteProductAfterBarcodeChangeIsAcceptedByImmutableOwnership()
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "product-a", "PRICE-A");
        var repository = new RemotePriceHistoryRepository(db.Factory);
        Assert.IsTrue((await repository.UpsertOrQueueRemotePriceHistoryAsync(
            "product-a",
            "price-barcode",
            "retail",
            125,
            EffectiveAt,
            "catalog_pull")).Applied);

        using (var changeBarcode = db.Factory.Open())
        {
            await changeBarcode.ExecuteAsync(
                "UPDATE products SET barcode = 'PRICE-A-NEW' WHERE remote_product_id = 'product-a';");
        }

        var retry = await repository.UpsertOrQueueRemotePriceHistoryAsync(
            "product-a",
            "price-barcode",
            "retail",
            125,
            EffectiveAt,
            "catalog_pull");

        Assert.IsTrue(retry.Applied);
        Assert.IsFalse(retry.Queued);
        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_price_history
WHERE remote_price_id = 'price-barcode'
  AND barcode = 'PRICE-A';"));
        Assert.AreEqual("product-a", await verify.ExecuteScalarAsync<string>(@"
SELECT remote_product_id
FROM remote_catalog_price_ownership
WHERE remote_price_id = 'price-barcode';"));
    }

    [TestMethod]
    public async Task ExistingHistory_DifferentRemoteProductReusingBarcodeIsRejectedByImmutableOwnership()
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "product-a", "REUSED-BARCODE");
        var repository = new RemotePriceHistoryRepository(db.Factory);
        Assert.IsTrue((await repository.UpsertOrQueueRemotePriceHistoryAsync(
            "product-a",
            "price-reused-barcode",
            "retail",
            125,
            EffectiveAt,
            "catalog_pull")).Applied);

        using (var reuseBarcode = db.Factory.Open())
        {
            await reuseBarcode.ExecuteAsync(@"
UPDATE products
SET remote_product_id = 'product-b'
WHERE barcode = 'REUSED-BARCODE';");
        }

        var collision = await repository.UpsertOrQueueRemotePriceHistoryAsync(
            "product-b",
            "price-reused-barcode",
            "retail",
            125,
            EffectiveAt,
            "catalog_pull");

        AssertSkipped(collision);
        using var verify = db.Factory.Open();
        Assert.AreEqual("product-a", await verify.ExecuteScalarAsync<string>(@"
SELECT remote_product_id
FROM remote_catalog_price_ownership
WHERE remote_price_id = 'price-reused-barcode';"));
        Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_price_history
WHERE remote_price_id = 'price-reused-barcode'
  AND barcode = 'REUSED-BARCODE';"));
    }

    [TestMethod]
    public async Task LegacyHistoryWithoutOwnership_DoesNotInferOwnerFromCurrentBarcode()
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "product-current", "LEGACY-REUSED");
        using (var seedLegacyHistory = db.Factory.Open())
        {
            await seedLegacyHistory.ExecuteAsync(@"
INSERT INTO product_price_history(
  barcode,
  timestamp,
  type,
  old_price,
  new_price,
  source,
  remote_price_id)
VALUES(
  'LEGACY-REUSED',
  @effectiveAt,
  'RETAIL',
  NULL,
  125,
  'catalog_pull',
  'legacy-price-owner-unknown');",
                new { effectiveAt = EffectiveAt });
        }

        DbInitializer.EnsureCreated(PosDbOptions.ForPath(db.Factory.DbPath));
        using (var afterMigration = db.Factory.Open())
        {
            Assert.AreEqual(0L, await ScalarAsync(afterMigration, @"
SELECT COUNT(1)
FROM remote_catalog_price_ownership
WHERE remote_price_id = 'legacy-price-owner-unknown';"));
        }

        var retry = await new RemotePriceHistoryRepository(db.Factory).UpsertOrQueueRemotePriceHistoryAsync(
            "product-current",
            "legacy-price-owner-unknown",
            "retail",
            125,
            EffectiveAt,
            "catalog_pull");

        AssertSkipped(retry);
        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM remote_catalog_price_ownership
WHERE remote_price_id = 'legacy-price-owner-unknown';"));
        Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_price_history
WHERE remote_price_id = 'legacy-price-owner-unknown';"));
    }

    [TestMethod]
    public async Task PendingPrice_ExactRetryStaysQueuedAndReplaysWhenProductArrives()
    {
        using var db = TestDb.Create();
        var repository = new RemotePriceHistoryRepository(db.Factory);

        var first = await repository.UpsertOrQueueRemotePriceHistoryAsync(
            "product-pending",
            "price-pending-idempotent",
            "retail",
            450,
            string.Empty,
            "catalog_pull");
        using (var agePending = db.Factory.Open())
        {
            await agePending.ExecuteAsync(@"
UPDATE remote_catalog_pending_prices
SET effective_at = @effectiveAt
WHERE remote_price_id = 'price-pending-idempotent';",
                new { effectiveAt = EffectiveAt });
        }

        var retry = await repository.UpsertOrQueueRemotePriceHistoryAsync(
            "product-pending",
            "price-pending-idempotent",
            "retail",
            450,
            string.Empty,
            "catalog_pull");

        Assert.IsFalse(first.Applied);
        Assert.IsTrue(first.Queued);
        Assert.IsFalse(retry.Applied);
        Assert.IsTrue(retry.Queued);
        string queuedEffectiveAt;
        using (var pending = db.Factory.Open())
        {
            Assert.AreEqual(1L, await ScalarAsync(pending,
                "SELECT COUNT(1) FROM remote_catalog_pending_prices WHERE remote_price_id = 'price-pending-idempotent';"));
            queuedEffectiveAt = await pending.ExecuteScalarAsync<string>(
                "SELECT effective_at FROM remote_catalog_pending_prices WHERE remote_price_id = 'price-pending-idempotent';")
                ?? string.Empty;
            Assert.IsFalse(string.IsNullOrWhiteSpace(queuedEffectiveAt));
        }

        var collision = await repository.UpsertOrQueueRemotePriceHistoryAsync(
            "different-product",
            "price-pending-idempotent",
            "retail",
            450,
            string.Empty,
            "catalog_pull");
        AssertSkipped(collision);

        await SeedProductAsync(db.Factory, "product-pending", "PENDING-PRICE");
        var replay = await repository.UpsertOrQueueRemotePriceHistoryAsync(
            "product-pending",
            "price-pending-idempotent",
            "retail",
            450,
            string.Empty,
            "catalog_pull");

        Assert.IsTrue(replay.Applied);
        Assert.IsFalse(replay.Queued);
        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await ScalarAsync(verify,
            "SELECT COUNT(1) FROM remote_catalog_pending_prices;"));
        Assert.AreEqual(1L, await verify.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM product_price_history
WHERE remote_price_id = 'price-pending-idempotent'
  AND barcode = 'PENDING-PRICE'
  AND timestamp = @effectiveAt
  AND new_price = 450;", new { effectiveAt = queuedEffectiveAt }));
    }

    [TestMethod]
    public async Task BatchApply_RemotePriceIdCollisionIsCountedAsSkipped()
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "product-a", "PRICE-A");
        await SeedProductAsync(db.Factory, "product-b", "PRICE-B");
        var repository = new RemoteCatalogBatchRepository(db.Factory);

        var first = await repository.ApplyAsync(new RemoteCatalogBatch
        {
            Prices = new[]
            {
                Price("product-a", 125)
            }
        });
        var collision = await repository.ApplyAsync(new RemoteCatalogBatch
        {
            Prices = new[]
            {
                Price("product-b", 125)
            }
        });

        Assert.AreEqual(1, first.PricesApplied);
        Assert.AreEqual(0, first.PricesSkipped);
        Assert.AreEqual(0, collision.PricesApplied);
        Assert.AreEqual(0, collision.PricesQueued);
        Assert.AreEqual(1, collision.PricesSkipped);
    }

    [TestMethod]
    public async Task PendingReplay_RemotePriceIdCollisionRemainsPendingAndDoesNotOverwriteHistory()
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "product-a", "PRICE-A");
        await SeedProductAsync(db.Factory, "product-b", "PRICE-B");
        var repository = new RemotePriceHistoryRepository(db.Factory);
        Assert.IsTrue((await repository.UpsertOrQueueRemotePriceHistoryAsync(
            "product-a",
            "price-replay-collision",
            "retail",
            125,
            EffectiveAt,
            "catalog_pull")).Applied);

        using (var seedCollision = db.Factory.Open())
        {
            await seedCollision.ExecuteAsync(@"
INSERT INTO remote_catalog_pending_prices(
  remote_price_id,
  remote_product_id,
  type,
  price,
  effective_at,
  source,
  created_at)
VALUES(
  'price-replay-collision',
  'product-b',
  'RETAIL',
  125,
  @effectiveAt,
  'catalog_pull',
  @effectiveAt);",
                new { effectiveAt = EffectiveAt });
        }

        var applied = await repository.ApplyPendingRemotePricesAsync();

        Assert.AreEqual(0, applied);
        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await ScalarAsync(verify,
            "SELECT COUNT(1) FROM remote_catalog_pending_prices WHERE remote_price_id = 'price-replay-collision';"));
        Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_price_history
WHERE remote_price_id = 'price-replay-collision'
  AND barcode = 'PRICE-A';"));
    }

    [TestMethod]
    public async Task BatchApply_PendingReplayCollisionPropagatesToRowsSkippedOnce()
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "product-a", "PRICE-A");
        await SeedProductAsync(db.Factory, "product-b", "PRICE-B");
        var prices = new RemotePriceHistoryRepository(db.Factory);
        Assert.IsTrue((await prices.UpsertOrQueueRemotePriceHistoryAsync(
            "product-a",
            "price-batch-replay-collision",
            "retail",
            125,
            EffectiveAt,
            "catalog_pull")).Applied);

        using (var seedCollision = db.Factory.Open())
        {
            await seedCollision.ExecuteAsync(@"
INSERT INTO remote_catalog_pending_prices(
  remote_price_id,
  remote_product_id,
  type,
  price,
  effective_at,
  source,
  created_at)
VALUES(
  'price-batch-replay-collision',
  'product-b',
  'RETAIL',
  125,
  @effectiveAt,
  'catalog_pull',
  @effectiveAt);",
                new { effectiveAt = EffectiveAt });
        }

        var result = await new RemoteCatalogBatchRepository(db.Factory)
            .ApplyAsync(new RemoteCatalogBatch());

        Assert.AreEqual(0, result.PendingPricesApplied);
        Assert.AreEqual(1, result.PricesSkipped);
        Assert.AreEqual(1, result.RowsSkipped);
        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM remote_catalog_pending_prices
WHERE remote_price_id = 'price-batch-replay-collision';"));
    }

    [TestMethod]
    public async Task LegacyHistoryOnly_AuthoritativeFullRefreshQuarantinesAndConverges()
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "product-repair", "REPAIR-CURRENT");
        using (var seedLegacy = db.Factory.Open())
        {
            await seedLegacy.ExecuteAsync(@"
INSERT INTO product_price_history(
  barcode, timestamp, type, old_price, new_price, source, remote_price_id)
VALUES(
  'REPAIR-LEGACY', @effectiveAt, 'RETAIL', NULL, 725, 'catalog_pull',
  'price-authoritative-repair');",
                new { effectiveAt = EffectiveAt });
        }

        DbInitializer.EnsureCreated(PosDbOptions.ForPath(db.Factory.DbPath));
        var ordinaryRetry = await new RemotePriceHistoryRepository(db.Factory)
            .UpsertOrQueueRemotePriceHistoryAsync(
                "product-repair",
                "price-authoritative-repair",
                "retail",
                725,
                EffectiveAt,
                "catalog_pull");
        AssertSkipped(ordinaryRetry);

        var batch = new RemoteCatalogBatch
        {
            AuthoritativeFullRefresh = true,
            Prices = new[]
            {
                new RemoteCatalogPriceWrite
                {
                    EffectiveAt = EffectiveAt,
                    Price = 725,
                    RemotePriceId = "price-authoritative-repair",
                    RemoteProductId = "product-repair",
                    Source = "catalog_pull",
                    Type = "retail"
                }
            }
        };
        var repository = new RemoteCatalogBatchRepository(db.Factory);
        var repaired = await repository.ApplyAsync(batch);
        var retry = await repository.ApplyAsync(batch);

        Assert.AreEqual(1, repaired.PricesApplied);
        Assert.AreEqual(0, repaired.PricesSkipped);
        Assert.AreEqual(1, retry.PricesApplied);
        Assert.AreEqual(0, retry.PricesSkipped);
        using var verify = db.Factory.Open();
        Assert.AreEqual("product-repair", await verify.ExecuteScalarAsync<string>(@"
SELECT remote_product_id
FROM remote_catalog_price_ownership
WHERE remote_price_id = 'price-authoritative-repair';"));
        Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM remote_catalog_price_evidence_quarantine
WHERE evidence_kind = 'history'
  AND remote_price_id = 'price-authoritative-repair'
  AND barcode = 'REPAIR-LEGACY'
  AND authoritative_remote_product_id = 'product-repair';"));
        Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_price_history
WHERE barcode = 'REPAIR-LEGACY'
  AND remote_price_id IS NULL;"));
        Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_price_history
WHERE barcode = 'REPAIR-CURRENT'
  AND remote_price_id = 'price-authoritative-repair';"));
        Assert.AreEqual(2L, await ScalarAsync(verify, "SELECT COUNT(1) FROM product_price_history;"));
    }

    [TestMethod]
    public async Task AuthoritativeFullRefresh_SameOwnerPayloadDriftRemainsFailClosed()
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "product-owned", "PRICE-OWNED");
        var prices = new RemotePriceHistoryRepository(db.Factory);
        Assert.IsTrue((await prices.UpsertOrQueueRemotePriceHistoryAsync(
            "product-owned",
            "price-owned-drift",
            "retail",
            500,
            EffectiveAt,
            "catalog_pull")).Applied);

        var result = await new RemoteCatalogBatchRepository(db.Factory).ApplyAsync(
            new RemoteCatalogBatch
            {
                AuthoritativeFullRefresh = true,
                Prices = new[]
                {
                    new RemoteCatalogPriceWrite
                    {
                        EffectiveAt = EffectiveAt,
                        Price = 501,
                        RemotePriceId = "price-owned-drift",
                        RemoteProductId = "product-owned",
                        Source = "catalog_pull",
                        Type = "retail"
                    }
                }
            });

        Assert.AreEqual(0, result.PricesApplied);
        Assert.AreEqual(1, result.PricesSkipped);
        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM remote_catalog_price_evidence_quarantine
WHERE remote_price_id = 'price-owned-drift';"));
        Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_price_history
WHERE remote_price_id = 'price-owned-drift'
  AND new_price = 500;"));
        Assert.AreEqual("product-owned", await verify.ExecuteScalarAsync<string>(@"
SELECT remote_product_id
FROM remote_catalog_price_ownership
WHERE remote_price_id = 'price-owned-drift';"));
    }

    [TestMethod]
    public async Task AuthoritativeFullRefresh_BlankEffectiveAtRetryReusesOwnedTimestamp()
    {
        const string storedEffectiveAt = "2020-01-02T03:04:05Z";
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "product-blank-time", "PRICE-BLANK-TIME");
        var prices = new RemotePriceHistoryRepository(db.Factory);
        Assert.IsTrue((await prices.UpsertOrQueueRemotePriceHistoryAsync(
            "product-blank-time",
            "price-blank-time",
            "retail",
            615,
            storedEffectiveAt,
            "catalog_pull")).Applied);

        var batch = new RemoteCatalogBatch
        {
            AuthoritativeFullRefresh = true,
            Prices = new[]
            {
                new RemoteCatalogPriceWrite
                {
                    EffectiveAt = string.Empty,
                    Price = 615,
                    RemotePriceId = "price-blank-time",
                    RemoteProductId = "product-blank-time",
                    Source = "catalog_pull",
                    Type = "retail"
                }
            }
        };
        var batches = new RemoteCatalogBatchRepository(db.Factory);
        var firstRetry = await batches.ApplyAsync(batch);
        var secondRetry = await batches.ApplyAsync(batch);

        Assert.AreEqual(1, firstRetry.PricesApplied);
        Assert.AreEqual(0, firstRetry.PricesSkipped);
        Assert.AreEqual(1, secondRetry.PricesApplied);
        Assert.AreEqual(0, secondRetry.PricesSkipped);
        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_price_history
WHERE remote_price_id = 'price-blank-time'
  AND timestamp = '2020-01-02T03:04:05Z';"));
    }

    [TestMethod]
    public async Task ExistingOwnershipConflict_PrevalidationCreatesNoHistoryOrPending()
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "product-owner", "PRICE-OWNER");
        await SeedProductAsync(db.Factory, "product-conflict", "PRICE-CONFLICT");
        var prices = new RemotePriceHistoryRepository(db.Factory);
        Assert.IsTrue((await prices.UpsertOrQueueRemotePriceHistoryAsync(
            "product-owner",
            "price-prevalidated-conflict",
            "retail",
            715,
            EffectiveAt,
            "catalog_pull")).Applied);

        var directConflict = await prices.UpsertOrQueueRemotePriceHistoryAsync(
            "product-conflict",
            "price-prevalidated-conflict",
            "retail",
            715,
            EffectiveAt,
            "catalog_pull");
        var batchConflict = await new RemoteCatalogBatchRepository(db.Factory).ApplyAsync(
            new RemoteCatalogBatch
            {
                Prices = new[]
                {
                    new RemoteCatalogPriceWrite
                    {
                        EffectiveAt = EffectiveAt,
                        Price = 715,
                        RemotePriceId = "price-prevalidated-conflict",
                        RemoteProductId = "product-not-local",
                        Source = "catalog_pull",
                        Type = "retail"
                    }
                }
            });

        AssertSkipped(directConflict);
        Assert.AreEqual(0, batchConflict.PricesApplied);
        Assert.AreEqual(0, batchConflict.PricesQueued);
        Assert.AreEqual(1, batchConflict.PricesSkipped);
        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_price_history
WHERE remote_price_id = 'price-prevalidated-conflict';"));
        Assert.AreEqual(0L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_price_history
WHERE barcode = 'PRICE-CONFLICT'
  AND remote_price_id = 'price-prevalidated-conflict';"));
        Assert.AreEqual(0L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM remote_catalog_pending_prices
WHERE remote_price_id = 'price-prevalidated-conflict';"));
        Assert.AreEqual("product-owner", await verify.ExecuteScalarAsync<string>(@"
SELECT remote_product_id
FROM remote_catalog_price_ownership
WHERE remote_price_id = 'price-prevalidated-conflict';"));
    }

    [TestMethod]
    public async Task InTransactionApply_CallerRollbackLeavesNoRemotePriceEvidence()
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "product-caller-rollback", "PRICE-CALLER-ROLLBACK");

        using (var connection = db.Factory.Open())
        using (var transaction = connection.BeginTransaction())
        {
            var result = await RemotePriceHistoryRepository
                .UpsertOrQueueRemotePriceHistoryInTransactionAsync(
                    connection,
                    transaction,
                    "product-caller-rollback",
                    "price-caller-rollback",
                    "retail",
                    310,
                    EffectiveAt,
                    "catalog_pull");

            Assert.IsTrue(result.Applied);
            Assert.IsFalse(result.Queued);
            transaction.Rollback();
        }

        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_price_history
WHERE remote_price_id = 'price-caller-rollback';"));
        Assert.AreEqual(0L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM remote_catalog_pending_prices
WHERE remote_price_id = 'price-caller-rollback';"));
        Assert.AreEqual(0L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM remote_catalog_price_ownership
WHERE remote_price_id = 'price-caller-rollback';"));
    }

    [TestMethod]
    public async Task RemotePriceApply_OwnershipFailureRollsBackHistoryInsert()
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "product-atomic", "PRICE-ATOMIC");
        using (var trigger = db.Factory.Open())
        {
            await trigger.ExecuteAsync(@"
CREATE TRIGGER fail_remote_price_owner_insert
BEFORE INSERT ON remote_catalog_price_ownership
BEGIN
  SELECT RAISE(ABORT, 'fault_remote_price_owner_insert');
END;");
        }

        await Assert.ThrowsExactlyAsync<SqliteException>(() =>
            new RemotePriceHistoryRepository(db.Factory).UpsertOrQueueRemotePriceHistoryAsync(
                "product-atomic",
                "price-atomic-fault",
                "retail",
                310,
                EffectiveAt,
                "catalog_pull"));

        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_price_history
WHERE remote_price_id = 'price-atomic-fault';"));
        Assert.AreEqual(0L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM remote_catalog_price_ownership
WHERE remote_price_id = 'price-atomic-fault';"));
    }

    [TestMethod]
    public async Task PendingReplay_OwnershipFailureRollsBackHistoryAndPreservesPending()
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "product-pending-atomic", "PENDING-ATOMIC");
        using (var seed = db.Factory.Open())
        {
            await seed.ExecuteAsync(@"
INSERT INTO remote_catalog_pending_prices(
  remote_price_id, remote_product_id, type, price, effective_at, source, created_at)
VALUES(
  'price-pending-atomic', 'product-pending-atomic', 'RETAIL', 410,
  @effectiveAt, 'catalog_pull', @effectiveAt);

CREATE TRIGGER fail_pending_price_owner_insert
BEFORE INSERT ON remote_catalog_price_ownership
BEGIN
  SELECT RAISE(ABORT, 'fault_pending_price_owner_insert');
END;",
                new { effectiveAt = EffectiveAt });
        }

        await Assert.ThrowsExactlyAsync<SqliteException>(() =>
            new RemotePriceHistoryRepository(db.Factory).ApplyPendingRemotePricesAsync());

        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM remote_catalog_pending_prices
WHERE remote_price_id = 'price-pending-atomic';"));
        Assert.AreEqual(0L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM product_price_history
WHERE remote_price_id = 'price-pending-atomic';"));
        Assert.AreEqual(0L, await ScalarAsync(verify, @"
SELECT COUNT(1)
FROM remote_catalog_price_ownership
WHERE remote_price_id = 'price-pending-atomic';"));
    }

    private static RemoteCatalogPriceWrite Price(string remoteProductId, int price)
    {
        return new RemoteCatalogPriceWrite
        {
            RemotePriceId = "price-batch-collision",
            RemoteProductId = remoteProductId,
            Type = "retail",
            Price = price,
            EffectiveAt = EffectiveAt,
            Source = "catalog_pull"
        };
    }

    private static void AssertSkipped(RemotePriceHistoryApplyResult result)
    {
        Assert.IsFalse(result.Applied);
        Assert.IsFalse(result.Queued);
    }

    private static async Task SeedProductAsync(
        SqliteConnectionFactory factory,
        string remoteProductId,
        string barcode)
    {
        using var connection = factory.Open();
        await connection.ExecuteAsync(@"
INSERT INTO products(barcode, name, unitPrice, remote_product_id, is_active)
VALUES(@barcode, @name, 100, @remoteProductId, 1);",
            new
            {
                barcode,
                name = "Product " + barcode,
                remoteProductId
            });
    }

    private static Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        return connection.ExecuteScalarAsync<long>(sql);
    }

    private static async Task<long> ScalarAsync(SqliteConnectionFactory factory, string sql)
    {
        using var connection = factory.Open();
        return await connection.ExecuteScalarAsync<long>(sql);
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
                "win7pos-remote-price-idempotency-tests-" + Guid.NewGuid().ToString("N"));
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
