using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class ArticleMutationOutboxRepositoryTests
{
    [TestMethod]
    public async Task Create_CommitsProductMetaStableIdentityAndSealedOutboxTogether()
    {
        using var db = TestDb.Create();
        var writer = new LocalArticleMutationWriter(db.Factory);

        var result = await writer.CreateAsync(
            NewCreate("ART-LOCAL-001", "Local article"),
            ProductWriteOrigin.LocalUserSave);

        Assert.AreEqual(1, result.Mutations.Count);
        Assert.AreEqual(1L, result.Mutations[0].LocalSequence);
        Assert.AreEqual(ArticleMutationOutboxStates.Pending, result.Mutations[0].State);
        Assert.IsTrue(PosArticleMutationIntentPolicy.IsSafeId(result.ClientProductId));
        using var connection = db.Factory.Open();
        var row = await connection.QuerySingleAsync<OutboxSnapshot>(@"
SELECT o.local_product_id AS LocalProductId,
       o.client_product_id AS ClientProductId,
       o.remote_product_id AS RemoteProductId,
       o.base_revision AS BaseRevision,
       o.state AS State,
       o.canonical_payload_json AS CanonicalPayload,
       o.payload_hash AS PayloadHash,
       o.intent_json AS IntentJson,
       p.remote_product_id AS ProductRemoteProductId,
       p.client_product_id AS ProductClientProductId
FROM article_mutation_outbox o
JOIN products p ON p.id = o.local_product_id
WHERE o.mutation_id = @mutationId;",
            new { mutationId = result.Mutations[0].MutationId });
        Assert.AreEqual(result.ProductId, row.LocalProductId);
        Assert.AreEqual(result.ClientProductId, row.ClientProductId);
        Assert.AreEqual(result.ClientProductId, row.ProductClientProductId);
        Assert.IsNull(row.RemoteProductId);
        Assert.IsNull(row.BaseRevision);
        Assert.IsNull(row.ProductRemoteProductId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(row.CanonicalPayload));
        Assert.AreEqual(result.Mutations[0].PayloadHash, row.PayloadHash);
        Assert.IsFalse(row.IntentJson.Contains("deviceToken", StringComparison.Ordinal));
        Assert.IsFalse(row.IntentJson.Contains("sessionToken", StringComparison.Ordinal));
        Assert.AreEqual(
            1L,
            await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM product_meta WHERE barcode = 'ART-LOCAL-001';"));
        Assert.AreEqual(
            2L,
            await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM product_price_history
WHERE barcode = 'ART-LOCAL-001'
  AND source = 'MANUAL_CREATE'
  AND article_mutation_id IS NULL;"));
    }

    [TestMethod]
    public async Task OutboxInsertFailure_RollsBackProductMetaAndReferences()
    {
        using var db = TestDb.Create();
        using (var connection = db.Factory.Open())
        {
            await connection.ExecuteAsync(@"
CREATE TRIGGER fail_article_outbox
BEFORE INSERT ON article_mutation_outbox
BEGIN
  SELECT RAISE(ABORT, 'synthetic outbox failure');
END;");
        }
        var writer = new LocalArticleMutationWriter(db.Factory);

        await AssertThrowsAsync<SqliteException>(() =>
            writer.CreateAsync(
                new LocalArticleCreateRequest
                {
                    Barcode = "ART-ROLLBACK-001",
                    PrimaryName = "Rollback article",
                    RetailPrice = 100,
                    PurchasePrice = 50,
                    InitialStock = 2,
                    CategoryName = "Rollback category",
                    SupplierName = "Rollback supplier"
                },
                ProductWriteOrigin.LocalUserSave));

        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await ScalarAsync(verify, "SELECT COUNT(1) FROM products;"));
        Assert.AreEqual(0L, await ScalarAsync(verify, "SELECT COUNT(1) FROM product_meta;"));
        Assert.AreEqual(0L, await ScalarAsync(verify, "SELECT COUNT(1) FROM categories WHERE name = 'Rollback category';"));
        Assert.AreEqual(0L, await ScalarAsync(verify, "SELECT COUNT(1) FROM suppliers WHERE name = 'Rollback supplier';"));
        Assert.AreEqual(0L, await ScalarAsync(verify, "SELECT COUNT(1) FROM article_mutation_outbox;"));
    }

    [TestMethod]
    public async Task PendingCreate_MultipleOfflineEditsRemainOrderedAndSealOneAtATime()
    {
        using var db = TestDb.Create();
        var writer = new LocalArticleMutationWriter(db.Factory);
        var outbox = new ArticleMutationOutboxRepository(db.Factory);
        var created = await writer.CreateAsync(
            NewCreate("ART-OFFLINE-001", "Version one"),
            ProductWriteOrigin.LocalUserSave);

        await writer.UpdateAsync(
            NewUpdate(created.ProductId, "ART-OFFLINE-001", "Version two"),
            ProductWriteOrigin.LocalUserSave);
        await writer.UpdateAsync(
            NewUpdate(created.ProductId, "ART-OFFLINE-001", "Version three"),
            ProductWriteOrigin.LocalUserSave);

        using (var connection = db.Factory.Open())
        {
            var states = (await connection.QueryAsync<SequenceState>(@"
SELECT local_sequence AS LocalSequence,
       state AS State,
       canonical_payload_json AS CanonicalPayload,
       payload_hash AS PayloadHash
FROM article_mutation_outbox
ORDER BY local_sequence;")).ToArray();
            CollectionAssert.AreEqual(
                new[] { "pending", "waiting_dependency", "waiting_dependency" },
                states.Select(row => row.State).ToArray());
            Assert.IsNull(states[1].CanonicalPayload);
            Assert.IsNull(states[1].PayloadHash);
            Assert.IsNull(states[2].CanonicalPayload);
        }

        var createClaim = await outbox.ClaimBatchAsync("generation-create");
        Assert.AreEqual(1, createClaim.Requests.Count);
        Assert.AreEqual(1L, createClaim.Requests[0].Intent.LocalSequence);
        await ApplySuccessAsync(
            outbox,
            createClaim,
            Guid.NewGuid().ToString("D"),
            Revision(1),
            "1");

        using (var connection = db.Factory.Open())
        {
            var states = (await connection.QueryAsync<SequenceState>(@"
SELECT local_sequence AS LocalSequence,
       state AS State,
       canonical_payload_json AS CanonicalPayload,
       payload_hash AS PayloadHash
FROM article_mutation_outbox
ORDER BY local_sequence;")).ToArray();
            CollectionAssert.AreEqual(
                new[] { "completed", "pending", "waiting_dependency" },
                states.Select(row => row.State).ToArray());
            Assert.IsFalse(string.IsNullOrWhiteSpace(states[1].CanonicalPayload));
            Assert.IsFalse(string.IsNullOrWhiteSpace(states[1].PayloadHash));
            Assert.IsNull(states[2].CanonicalPayload);
        }

        var secondClaim = await outbox.ClaimBatchAsync("generation-second");
        Assert.AreEqual(2L, secondClaim.Requests.Single().Intent.LocalSequence);
        await ApplySuccessAsync(
            outbox,
            secondClaim,
            createClaim.Requests[0].Intent.RemoteProductId ??
                await ProductRemoteIdAsync(db.Factory, created.ProductId),
            Revision(2),
            "2");
        var thirdClaim = await outbox.ClaimBatchAsync("generation-third");
        Assert.AreEqual(3L, thirdClaim.Requests.Single().Intent.LocalSequence);
        Assert.AreEqual(Revision(2), thirdClaim.Requests.Single().Intent.BaseRevision);
    }

    [TestMethod]
    public async Task FullManualEdit_WritesExactUpdateTwoHistoriesAndOneStockAdjustment()
    {
        using var db = TestDb.Create();
        var productId = await SeedRemoteProductAsync(db.Factory, "ART-EDIT-OLD");
        var references = await SeedVerifiedReferencesAsync(db.Factory);
        var writer = new LocalArticleMutationWriter(db.Factory);

        var result = await writer.UpdateAsync(
            new LocalArticleUpdateRequest
            {
                ProductId = productId,
                Barcode = "ART-EDIT-NEW",
                PrimaryName = "Updated primary",
                SecondaryName = "Updated secondary",
                ItemNumber = "ITEM-NEW",
                RetailPrice = 175,
                PurchasePrice = 80,
                CategoryId = references.CategoryId,
                CategoryName = "Remote category",
                SupplierId = references.SupplierId,
                SupplierName = "Remote supplier",
                StockQuantity = 13,
                StockReason = "damage",
                OccurredAt = DateTimeOffset.UtcNow
            },
            ProductWriteOrigin.LocalUserSave);

        Assert.AreEqual(4, result.Mutations.Count);
        using var connection = db.Factory.Open();
        var rows = (await connection.QueryAsync<MutationShape>(@"
SELECT mutation_kind AS MutationKind,
       local_sequence AS LocalSequence,
       field_mask_json AS FieldMaskJson,
       intent_json AS IntentJson,
       state AS State,
       local_price_history_id AS LocalPriceHistoryId,
       local_stock_adjustment_id AS LocalStockAdjustmentId
FROM article_mutation_outbox
ORDER BY local_sequence;")).ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                PosArticleMutationKinds.ProductUpdate,
                PosArticleMutationKinds.ProductPurchasePriceChange,
                PosArticleMutationKinds.ProductRetailPriceChange,
                PosArticleMutationKinds.ProductManualStockAdjustment
            },
            rows.Select(row => row.MutationKind).ToArray());
        CollectionAssert.AreEqual(
            new[] { "pending", "waiting_dependency", "waiting_dependency", "waiting_dependency" },
            rows.Select(row => row.State).ToArray());
        Assert.AreEqual(
            "[\"barcode\",\"categoryId\",\"itemNumber\",\"primaryName\",\"secondaryName\",\"supplierId\"]",
            rows[0].FieldMaskJson);
        StringAssert.Contains(rows[0].IntentJson, "\"barcode\":\"ART-EDIT-NEW\"");
        StringAssert.Contains(rows[0].IntentJson, references.CategoryRemoteId);
        StringAssert.Contains(rows[0].IntentJson, references.SupplierRemoteId);
        Assert.IsTrue(rows[1].LocalPriceHistoryId.HasValue);
        Assert.IsTrue(rows[2].LocalPriceHistoryId.HasValue);
        Assert.IsTrue(rows[3].LocalStockAdjustmentId.HasValue);
        Assert.AreEqual(2L, await ScalarAsync(connection, "SELECT COUNT(1) FROM product_price_history;"));
        Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT COUNT(1) FROM article_manual_stock_adjustments;"));
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(1) FROM sales;"));
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(1) FROM local_stock_movements;"));
        Assert.AreEqual(
            -2L,
            await connection.ExecuteScalarAsync<long>(
                "SELECT quantity_delta FROM article_manual_stock_adjustments;"));
    }

    [TestMethod]
    public async Task Claim_IsFairOnePerProduct_AndRecoveryKeepsAttemptLedger()
    {
        using var db = TestDb.Create();
        var writer = new LocalArticleMutationWriter(db.Factory);
        await writer.CreateAsync(
            NewCreate("ART-FAIR-001", "Fair one"),
            ProductWriteOrigin.LocalUserSave);
        await writer.CreateAsync(
            NewCreate("ART-FAIR-002", "Fair two"),
            ProductWriteOrigin.LocalUserSave);
        var outbox = new ArticleMutationOutboxRepository(db.Factory);

        var claim = await outbox.ClaimBatchAsync("generation-fair");
        Assert.AreEqual(2, claim.Requests.Count);
        Assert.AreEqual(
            2,
            claim.Requests.Select(item => item.Intent.ClientProductId)
                .Distinct(StringComparer.Ordinal).Count());

        Assert.AreEqual(2, await outbox.RecoverInterruptedAsync());
        using var connection = db.Factory.Open();
        Assert.AreEqual(2L, await ScalarAsync(connection, "SELECT COUNT(1) FROM article_mutation_attempts WHERE outcome = 'client_interrupted';"));
        Assert.AreEqual(2L, await ScalarAsync(connection, "SELECT COUNT(1) FROM article_mutation_outbox WHERE state = 'retry_wait';"));
    }

    [TestMethod]
    public async Task ConcurrentClaims_CanIssueOnlyOneAttemptForAMutation()
    {
        using var db = TestDb.Create();
        await new LocalArticleMutationWriter(db.Factory).CreateAsync(
            NewCreate("ART-SINGLE-FLIGHT-001", "Single flight"),
            ProductWriteOrigin.LocalUserSave);
        var firstRepository = new ArticleMutationOutboxRepository(db.Factory);
        var secondRepository = new ArticleMutationOutboxRepository(db.Factory);

        var claims = await Task.WhenAll(
            firstRepository.ClaimBatchAsync("generation-single-flight"),
            secondRepository.ClaimBatchAsync("generation-single-flight"));

        Assert.AreEqual(
            1,
            claims.Sum(claim => claim.Requests.Count));
        using var connection = db.Factory.Open();
        Assert.AreEqual(
            1L,
            await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM article_mutation_attempts;"));
        Assert.AreEqual(
            1L,
            await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state = 'in_progress';"));
    }

    [TestMethod]
    public async Task StaleAck_AfterClaimRecoveryCannotCompleteMutation()
    {
        using var db = TestDb.Create();
        await new LocalArticleMutationWriter(db.Factory).CreateAsync(
            NewCreate("ART-STALE-ACK-001", "Stale ACK"),
            ProductWriteOrigin.LocalUserSave);
        var outbox = new ArticleMutationOutboxRepository(db.Factory);
        var claim = await outbox.ClaimBatchAsync("generation-stale-ack");
        Assert.AreEqual(1, await outbox.RecoverInterruptedAsync());

        await AssertThrowsAsync<InvalidDataException>(() =>
            ApplySuccessAsync(
                outbox,
                claim,
                Guid.NewGuid().ToString("D"),
                Revision(5),
                "5"));

        using var connection = db.Factory.Open();
        Assert.AreEqual(
            ArticleMutationOutboxStates.RetryWait,
            await connection.ExecuteScalarAsync<string>(
                "SELECT state FROM article_mutation_outbox;"));
        Assert.AreEqual(
            0L,
            await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state = 'completed';"));
    }

    [TestMethod]
    public async Task Duplicate_HasNewLocalClientAndMutationIdentityWithoutCopyingRemoteId()
    {
        using var db = TestDb.Create();
        var sourceId = await SeedRemoteProductAsync(db.Factory, "ART-SOURCE-001");
        var writer = new LocalArticleMutationWriter(db.Factory);

        var duplicate = await writer.CreateAsync(
            new LocalArticleCreateRequest
            {
                Barcode = "ART-DUPLICATE-001",
                PrimaryName = "Duplicated",
                RetailPrice = 150,
                PurchasePrice = 60,
                InitialStock = 15,
                DuplicateSourceProductId = sourceId
            },
            ProductWriteOrigin.LocalUserSave);

        using var connection = db.Factory.Open();
        var identities = (await connection.QueryAsync<ProductIdentity>(@"
SELECT id AS Id,
       client_product_id AS ClientProductId,
       remote_product_id AS RemoteProductId
FROM products
ORDER BY id;")).ToArray();
        Assert.AreEqual(2, identities.Length);
        Assert.AreNotEqual(identities[0].Id, identities[1].Id);
        Assert.AreNotEqual(identities[0].ClientProductId, identities[1].ClientProductId);
        Assert.IsNull(identities[1].RemoteProductId);
        var mutation = await connection.QuerySingleAsync<DuplicateMutation>(@"
SELECT mutation_id AS MutationId,
       mutation_kind AS MutationKind,
       local_sequence AS LocalSequence,
       remote_product_id AS RemoteProductId
FROM article_mutation_outbox
WHERE local_product_id = @productId
  AND local_sequence = 1;",
            new { productId = duplicate.ProductId });
        Assert.AreEqual(PosArticleMutationKinds.ProductDuplicate, mutation.MutationKind);
        Assert.AreEqual(1L, mutation.LocalSequence);
        Assert.AreEqual(identities[0].RemoteProductId, mutation.RemoteProductId);
        Assert.AreEqual(duplicate.Mutations[0].MutationId, mutation.MutationId);
    }

    [TestMethod]
    public async Task MissingRemoteReference_PersistsLocalEditAsVisibleBlockedMutation()
    {
        using var db = TestDb.Create();
        var productId = await SeedRemoteProductAsync(db.Factory, "ART-REF-001");
        var writer = new LocalArticleMutationWriter(db.Factory);

        await writer.UpdateAsync(
            new LocalArticleUpdateRequest
            {
                ProductId = productId,
                Barcode = "ART-REF-001",
                PrimaryName = "Remote product",
                RetailPrice = 150,
                PurchasePrice = 60,
                CategoryName = "Local-only category",
                StockQuantity = 15,
                StockReason = "count_correction"
            },
            ProductWriteOrigin.LocalUserSave);

        using var connection = db.Factory.Open();
        Assert.AreEqual(
            "Local-only category",
            await connection.ExecuteScalarAsync<string>(@"
SELECT category_name FROM product_meta WHERE barcode = 'ART-REF-001';"));
        var blocked = await connection.QuerySingleAsync<(string State, string Code)>(@"
SELECT state AS State, last_typed_code AS Code
FROM article_mutation_outbox;");
        Assert.AreEqual(ArticleMutationOutboxStates.FailedBlocked, blocked.State);
        Assert.AreEqual("dependency_missing_remote_reference", blocked.Code);
    }

    [TestMethod]
    public async Task CatalogPull_UpdatesRemoteShadowWithoutOverwritingPendingLocalEdit()
    {
        using var db = TestDb.Create();
        var productId = await SeedRemoteProductAsync(db.Factory, "ART-OVERLAY-001");
        var writer = new LocalArticleMutationWriter(db.Factory);
        await writer.UpdateAsync(
            NewUpdate(productId, "ART-OVERLAY-001", "Pending local name"),
            ProductWriteOrigin.LocalUserSave);
        var remoteId = await ProductRemoteIdAsync(db.Factory, productId);
        var remoteRevision = Revision(9);

        await new RemoteCatalogBatchRepository(db.Factory).ApplyAsync(
            new RemoteCatalogBatch
            {
                Products = new[]
                {
                    new RemoteCatalogProductWrite
                    {
                        Barcode = "ART-OVERLAY-001",
                        Name = "New remote canonical name",
                        UnitPrice = 999,
                        PurchasePrice = 444,
                        StockQuantity = 77,
                        RemoteProductId = remoteId,
                        RemoteUpdatedAt = remoteRevision
                    }
                }
            },
            CancellationToken.None);

        using (var connection = db.Factory.Open())
        {
            Assert.AreEqual(
                "Pending local name",
                await connection.ExecuteScalarAsync<string>(
                    "SELECT name FROM products WHERE id = @productId;",
                    new { productId }));
            Assert.AreEqual(
                150L,
                await connection.ExecuteScalarAsync<long>(
                    "SELECT unitPrice FROM products WHERE id = @productId;",
                    new { productId }));
            Assert.AreEqual(
                remoteRevision,
                await connection.ExecuteScalarAsync<string>(
                    "SELECT remote_base_revision FROM products WHERE id = @productId;",
                    new { productId }));
            Assert.AreEqual(
                "New remote canonical name",
                await connection.ExecuteScalarAsync<string>(@"
SELECT primary_name
FROM article_product_remote_shadow
WHERE remote_product_id = @remoteId;",
                    new { remoteId }));
        }

        var claim = await new ArticleMutationOutboxRepository(db.Factory)
            .ClaimBatchAsync("generation-stale-pull");
        Assert.AreEqual(0, claim.Requests.Count);
        using var verify = db.Factory.Open();
        Assert.AreEqual(
            ArticleMutationOutboxStates.FailedBlocked,
            await verify.ExecuteScalarAsync<string>(
                "SELECT state FROM article_mutation_outbox;"));
        Assert.AreEqual(
            PosArticleMutationStatusPolicy.FailedConflict,
            await verify.ExecuteScalarAsync<string>(
                "SELECT last_typed_code FROM article_mutation_outbox;"));
    }

    [TestMethod]
    public async Task AuthoritativeFullRefresh_DoesNotDeactivatePendingLocalCreate()
    {
        using var db = TestDb.Create();
        var created = await new LocalArticleMutationWriter(db.Factory).CreateAsync(
            NewCreate("ART-PENDING-FULL-001", "Pending full refresh"),
            ProductWriteOrigin.LocalUserSave);

        await new CatalogFullRefreshReconciler(db.Factory).ReconcileAsync(
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            "2026-07-28T13:00:00.000Z");

        using var connection = db.Factory.Open();
        Assert.AreEqual(
            1L,
            await connection.ExecuteScalarAsync<long>(
                "SELECT is_active FROM products WHERE id = @productId;",
                new { productId = created.ProductId }));
        Assert.AreEqual(
            ArticleMutationOutboxStates.Pending,
            await connection.ExecuteScalarAsync<string>(
                "SELECT state FROM article_mutation_outbox WHERE local_product_id = @productId;",
                new { productId = created.ProductId }));
    }

    [TestMethod]
    public async Task NonActivationAck_DoesNotReactivateAnInactiveProduct()
    {
        using var db = TestDb.Create();
        var productId = await SeedRemoteProductAsync(
            db.Factory,
            "ART-INACTIVE-001");
        var writer = new LocalArticleMutationWriter(db.Factory);
        await writer.UpdateAsync(
            NewUpdate(productId, "ART-INACTIVE-001", "Edited while inactive"),
            ProductWriteOrigin.LocalUserSave);
        using (var connection = db.Factory.Open())
        {
            await connection.ExecuteAsync(@"
UPDATE products
SET is_active = 0,
    remote_deleted_at = '2026-07-28T12:00:00.000Z'
WHERE id = @productId;",
                new { productId });
        }

        var outbox = new ArticleMutationOutboxRepository(db.Factory);
        var claim = await outbox.ClaimBatchAsync("generation-inactive");
        await ApplySuccessAsync(
            outbox,
            claim,
            await ProductRemoteIdAsync(db.Factory, productId),
            Revision(4),
            "4");

        using var verify = db.Factory.Open();
        Assert.AreEqual(
            0L,
            await verify.ExecuteScalarAsync<long>(
                "SELECT is_active FROM products WHERE id = @productId;",
                new { productId }));
        Assert.AreEqual(
            "2026-07-28T12:00:00.000Z",
            await verify.ExecuteScalarAsync<string>(
                "SELECT remote_deleted_at FROM products WHERE id = @productId;",
                new { productId }));
    }

    [TestMethod]
    public async Task PriceAndStockAck_StoreRemoteIdsWithoutDuplicateLocalEffects()
    {
        using var db = TestDb.Create();
        var productId = await SeedRemoteProductAsync(
            db.Factory,
            "ART-ECONOMIC-001");
        await new LocalArticleMutationWriter(db.Factory).UpdateAsync(
            new LocalArticleUpdateRequest
            {
                ProductId = productId,
                Barcode = "ART-ECONOMIC-001",
                PrimaryName = "Remote product",
                ItemNumber = "ITEM-OLD",
                SecondaryName = "Old secondary",
                RetailPrice = 150,
                PurchasePrice = 70,
                StockQuantity = 18,
                StockReason = "count_correction",
                OccurredAt = DateTimeOffset.UtcNow
            },
            ProductWriteOrigin.LocalUserSave);
        var outbox = new ArticleMutationOutboxRepository(db.Factory);
        var remoteId = await ProductRemoteIdAsync(db.Factory, productId);
        var priceHistoryId = Guid.NewGuid().ToString("D");
        var stockMovementId = Guid.NewGuid().ToString("D");

        var priceClaim = await outbox.ClaimBatchAsync(
            "generation-economic-price");
        Assert.AreEqual(
            PosArticleMutationKinds.ProductPurchasePriceChange,
            priceClaim.Requests.Single().Intent.MutationKind);
        await ApplySuccessAsync(
            outbox,
            priceClaim,
            remoteId,
            Revision(6),
            "6",
            priceHistoryId: priceHistoryId);

        var stockClaim = await outbox.ClaimBatchAsync(
            "generation-economic-stock");
        Assert.AreEqual(
            PosArticleMutationKinds.ProductManualStockAdjustment,
            stockClaim.Requests.Single().Intent.MutationKind);
        await ApplySuccessAsync(
            outbox,
            stockClaim,
            remoteId,
            Revision(7),
            "7",
            stockMovementId: stockMovementId);

        await new RemoteCatalogBatchRepository(db.Factory).ApplyAsync(
            new RemoteCatalogBatch
            {
                Products = new[]
                {
                    new RemoteCatalogProductWrite
                    {
                        Barcode = "ART-ECONOMIC-001",
                        Name = "Remote product",
                        UnitPrice = 150,
                        PurchasePrice = 70,
                        StockQuantity = 18,
                        RemoteProductId = remoteId,
                        RemoteUpdatedAt = Revision(7)
                    }
                }
            },
            CancellationToken.None);

        using var connection = db.Factory.Open();
        Assert.AreEqual(
            1L,
            await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM product_price_history;"));
        Assert.AreEqual(
            1L,
            await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM article_manual_stock_adjustments;"));
        Assert.AreEqual(
            priceHistoryId,
            await connection.ExecuteScalarAsync<string>(@"
SELECT remote_price_history_id
FROM article_mutation_outbox
WHERE mutation_kind = 'product_purchase_price_change';"));
        Assert.AreEqual(
            stockMovementId,
            await connection.ExecuteScalarAsync<string>(@"
SELECT remote_stock_movement_id
FROM article_mutation_outbox
WHERE mutation_kind = 'product_manual_stock_adjustment';"));
        Assert.AreEqual(
            2L,
            await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state = 'completed';"));
    }

    private static LocalArticleCreateRequest NewCreate(string barcode, string name)
    {
        return new LocalArticleCreateRequest
        {
            Barcode = barcode,
            PrimaryName = name,
            RetailPrice = 150,
            PurchasePrice = 60,
            InitialStock = 15,
            OccurredAt = DateTimeOffset.UtcNow
        };
    }

    private static LocalArticleUpdateRequest NewUpdate(
        long productId,
        string barcode,
        string name)
    {
        return new LocalArticleUpdateRequest
        {
            ProductId = productId,
            Barcode = barcode,
            PrimaryName = name,
            RetailPrice = 150,
            PurchasePrice = 60,
            StockQuantity = 15,
            StockReason = "count_correction",
            OccurredAt = DateTimeOffset.UtcNow
        };
    }

    private static async Task ApplySuccessAsync(
        ArticleMutationOutboxRepository repository,
        ArticleMutationClaim claim,
        string remoteProductId,
        string authoritativeRevision,
        string catalogRevision,
        string? priceHistoryId = null,
        string? stockMovementId = null)
    {
        var request = claim.Requests.Single();
        var result = new PosArticleMutationResult
        {
            DeliveryStatus = PosArticleMutationStatusPolicy.Applied,
            Ack = new PosArticleMutationAck
            {
                AttemptToken = request.AttemptToken,
                AuthoritativeRevision = authoritativeRevision,
                CatalogRevision = catalogRevision,
                Code = PosArticleMutationStatusPolicy.Applied,
                IdempotencyKey = request.Intent.IdempotencyKey,
                MutationId = request.Intent.MutationId,
                PayloadHash = request.PayloadHash,
                PriceHistoryId = priceHistoryId,
                RemoteProductId = remoteProductId,
                Retryable = false,
                SchemaVersion = PosArticleMutationContract.SchemaVersion,
                ServerTimestamp = authoritativeRevision,
                Status = PosArticleMutationStatusPolicy.Applied,
                StockMovementId = stockMovementId,
                Terminal = true
            }
        };
        var validation = PosArticleMutationResponseValidator.Validate(
            new PosArticleMutationResponse
            {
                Code = "success",
                Ok = true,
                Results = new[] { result },
                SchemaVersion = PosArticleMutationContract.SchemaVersion,
                ServerTime = "2026-07-28T12:00:00.000Z"
            },
            claim.Requests,
            (_, attempt) => string.Equals(
                attempt,
                request.AttemptToken,
                StringComparison.Ordinal));
        Assert.IsTrue(validation.IsValid, validation.Code);
        await repository.ApplyValidatedResponseAsync(claim, validation);
    }

    private static string Revision(int sequence)
    {
        return "2026-07-28T12:00:" +
            sequence.ToString("00", CultureInfo.InvariantCulture) +
            ".123456Z";
    }

    private static async Task<string> ProductRemoteIdAsync(
        SqliteConnectionFactory factory,
        long productId)
    {
        using var connection = factory.Open();
        return await connection.ExecuteScalarAsync<string>(
            "SELECT remote_product_id FROM products WHERE id = @productId;",
            new { productId }) ?? string.Empty;
    }

    private static async Task<long> SeedRemoteProductAsync(
        SqliteConnectionFactory factory,
        string barcode)
    {
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction();
        var productId = await connection.ExecuteScalarAsync<long>(@"
INSERT INTO products(
  barcode,
  name,
  unitPrice,
  is_active,
  remote_product_id,
  client_product_id,
  remote_base_revision)
VALUES(
  @barcode,
  'Remote product',
  150,
  1,
  @remoteProductId,
  @clientProductId,
  @baseRevision);
SELECT last_insert_rowid();",
            new
            {
                barcode,
                remoteProductId = Guid.NewGuid().ToString("D"),
                clientProductId = "client-" + Guid.NewGuid().ToString("N"),
                baseRevision = Revision(0)
            },
            transaction);
        await connection.ExecuteAsync(@"
INSERT INTO product_meta(
  barcode,
  article_code,
  name2,
  purchase_price,
  purchase_old,
  retail_old,
  supplier_id,
  supplier_name,
  category_id,
  category_name,
  stock_qty)
VALUES(@barcode, 'ITEM-OLD', 'Old secondary', 60, 0, 0, NULL, '', NULL, '', 15);",
            new { barcode },
            transaction);
        transaction.Commit();
        return productId;
    }

    private static async Task<VerifiedReferences> SeedVerifiedReferencesAsync(
        SqliteConnectionFactory factory)
    {
        using var connection = factory.Open();
        var categoryRemoteId = Guid.NewGuid().ToString("D");
        var supplierRemoteId = Guid.NewGuid().ToString("D");
        var categoryId = await connection.ExecuteScalarAsync<int>(@"
INSERT INTO categories(name, remote_category_id, is_active)
VALUES('Remote category', @remoteId, 1);
SELECT last_insert_rowid();",
            new { remoteId = categoryRemoteId });
        var supplierId = await connection.ExecuteScalarAsync<int>(@"
INSERT INTO suppliers(name, remote_supplier_id, is_active)
VALUES('Remote supplier', @remoteId, 1);
SELECT last_insert_rowid();",
            new { remoteId = supplierRemoteId });
        return new VerifiedReferences
        {
            CategoryId = categoryId,
            CategoryRemoteId = categoryRemoteId,
            SupplierId = supplierId,
            SupplierRemoteId = supplierRemoteId
        };
    }

    private static async Task<long> ScalarAsync(
        SqliteConnection connection,
        string sql)
    {
        return Convert.ToInt64(
            await connection.ExecuteScalarAsync(sql),
            CultureInfo.InvariantCulture);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        Assert.Fail("Expected " + typeof(TException).Name + ".");
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
                "win7pos-article-outbox-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestDb(root);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    private sealed class OutboxSnapshot
    {
        public long LocalProductId { get; set; }
        public string ClientProductId { get; set; } = string.Empty;
        public string? RemoteProductId { get; set; }
        public string? BaseRevision { get; set; }
        public string State { get; set; } = string.Empty;
        public string CanonicalPayload { get; set; } = string.Empty;
        public string PayloadHash { get; set; } = string.Empty;
        public string IntentJson { get; set; } = string.Empty;
        public string? ProductRemoteProductId { get; set; }
        public string ProductClientProductId { get; set; } = string.Empty;
    }

    private sealed class SequenceState
    {
        public long LocalSequence { get; set; }
        public string State { get; set; } = string.Empty;
        public string? CanonicalPayload { get; set; }
        public string? PayloadHash { get; set; }
    }

    private sealed class MutationShape
    {
        public string MutationKind { get; set; } = string.Empty;
        public long LocalSequence { get; set; }
        public string FieldMaskJson { get; set; } = string.Empty;
        public string IntentJson { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public long? LocalPriceHistoryId { get; set; }
        public long? LocalStockAdjustmentId { get; set; }
    }

    private sealed class ProductIdentity
    {
        public long Id { get; set; }
        public string? ClientProductId { get; set; }
        public string? RemoteProductId { get; set; }
    }

    private sealed class DuplicateMutation
    {
        public string MutationId { get; set; } = string.Empty;
        public string MutationKind { get; set; } = string.Empty;
        public long LocalSequence { get; set; }
        public string RemoteProductId { get; set; } = string.Empty;
    }

    private sealed class VerifiedReferences
    {
        public int CategoryId { get; set; }
        public string CategoryRemoteId { get; set; } = string.Empty;
        public int SupplierId { get; set; }
        public string SupplierRemoteId { get; set; } = string.Empty;
    }
}
