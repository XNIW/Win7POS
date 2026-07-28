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
    public async Task Recovery_PreservesActiveProcessAndImmediatelyRecoversPriorProcess()
    {
        using var db = TestDb.Create();
        await new LocalArticleMutationWriter(db.Factory).CreateAsync(
            NewCreate("ART-GENERATION-FENCE-001", "Generation fence"),
            ProductWriteOrigin.LocalUserSave);
        var outbox = new ArticleMutationOutboxRepository(db.Factory);
        var claim = await outbox.ClaimBatchAsync(
            "generation-reused-across-restart",
            claimOwnerId: "process-active");
        Assert.AreEqual(1, claim.Requests.Count);

        Assert.AreEqual(
            0,
            await outbox.RecoverInterruptedAsync(
                TimeSpan.Zero,
                "process-active"));
        using (var active = db.Factory.Open())
        {
            Assert.AreEqual(
                ArticleMutationOutboxStates.InProgress,
                await active.ExecuteScalarAsync<string>(
                    "SELECT state FROM article_mutation_outbox;"));
            Assert.AreEqual(
                1L,
                await active.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_attempts
WHERE completed_at IS NULL;"));
        }

        Assert.AreEqual(
            1,
            await outbox.RecoverInterruptedAsync(
                TimeSpan.Zero,
                "process-after-restart"));
        using var recovered = db.Factory.Open();
        Assert.AreEqual(
            ArticleMutationOutboxStates.RetryWait,
            await recovered.ExecuteScalarAsync<string>(
                "SELECT state FROM article_mutation_outbox;"));
        Assert.AreEqual(
            1L,
            await recovered.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_attempts
WHERE outcome = 'client_interrupted'
  AND completed_at IS NOT NULL;"));
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
    public async Task RetriedDelivery_ReplaysSealedPayloadAfterCatalogBaseAdvances()
    {
        using var db = TestDb.Create();
        var productId = await SeedRemoteProductAsync(
            db.Factory,
            "ART-REPLAY-BASE-001");
        await new LocalArticleMutationWriter(db.Factory).UpdateAsync(
            NewUpdate(
                productId,
                "ART-REPLAY-BASE-001",
                "Ambiguously delivered"),
            ProductWriteOrigin.LocalUserSave);
        var outbox = new ArticleMutationOutboxRepository(db.Factory);
        var first = await outbox.ClaimBatchAsync("generation-first-delivery");
        var original = first.Requests.Single();

        await outbox.ReleaseClaimForTransportFailureAsync(
            first,
            "transport_response_lost",
            authenticationDenied: false);
        using (var connection = db.Factory.Open())
        {
            await connection.ExecuteAsync(@"
UPDATE products
SET remote_base_revision = @advancedRevision
WHERE id = @productId;
UPDATE article_mutation_outbox
SET next_attempt_at = 0;",
                new
                {
                    advancedRevision = Revision(9),
                    productId
                });
        }

        var replay = await outbox.ClaimBatchAsync(
            "generation-replayed-delivery");
        var replayed = replay.Requests.Single();
        Assert.AreEqual(
            original.Intent.MutationId,
            replayed.Intent.MutationId);
        Assert.AreEqual(original.PayloadHash, replayed.PayloadHash);
        Assert.AreEqual(
            original.Intent.BaseRevision,
            replayed.Intent.BaseRevision);
        using var verify = db.Factory.Open();
        Assert.AreEqual(
            ArticleMutationOutboxStates.InProgress,
            await verify.ExecuteScalarAsync<string>(
                "SELECT state FROM article_mutation_outbox;"));
        Assert.AreEqual(
            2L,
            await verify.ExecuteScalarAsync<long>(
                "SELECT attempt_count FROM article_mutation_outbox;"));
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
    public async Task MissingRemoteReference_WaitsAndMaterializesWhenReferenceGetsRemoteId()
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
        Assert.AreEqual(
            ArticleMutationOutboxStates.WaitingDependency,
            blocked.State);
        Assert.AreEqual("dependency_missing_remote_reference", blocked.Code);
        Assert.IsTrue((await new ArticleMutationOutboxRepository(db.Factory)
            .GetDrainStateAsync()).NextRetryAt.HasValue);

        var categoryId = await connection.ExecuteScalarAsync<int>(@"
SELECT category_id
FROM product_meta
WHERE barcode = 'ART-REF-001';");
        var remoteCategoryId = Guid.NewGuid().ToString("D");
        await connection.ExecuteAsync(@"
UPDATE categories
SET remote_category_id = @remoteCategoryId
WHERE id = @categoryId;
UPDATE article_mutation_outbox
SET next_attempt_at = 0;",
            new { categoryId, remoteCategoryId });

        var claim = await new ArticleMutationOutboxRepository(db.Factory)
            .ClaimBatchAsync("generation-reference-resolved");
        Assert.AreEqual(1, claim.Requests.Count);
        Assert.AreEqual(
            remoteCategoryId,
            claim.Requests.Single().Intent.Changes[
                PosArticleMutationFields.CategoryId]);
    }

    [TestMethod]
    public async Task MissingRemoteReference_BlocksIfRemoteBaseChangesWhileWaiting()
    {
        using var db = TestDb.Create();
        var productId = await SeedRemoteProductAsync(
            db.Factory,
            "ART-REF-CONFLICT-001");
        await new LocalArticleMutationWriter(db.Factory).UpdateAsync(
            new LocalArticleUpdateRequest
            {
                ProductId = productId,
                Barcode = "ART-REF-CONFLICT-001",
                PrimaryName = "Remote product",
                RetailPrice = 150,
                PurchasePrice = 60,
                CategoryName = "Waiting local category",
                StockQuantity = 15,
                StockReason = "count_correction"
            },
            ProductWriteOrigin.LocalUserSave);

        using (var connection = db.Factory.Open())
        {
            var categoryId = await connection.ExecuteScalarAsync<int>(@"
SELECT category_id
FROM product_meta
WHERE barcode = 'ART-REF-CONFLICT-001';");
            await connection.ExecuteAsync(@"
UPDATE categories
SET remote_category_id = @remoteCategoryId
WHERE id = @categoryId;
UPDATE products
SET remote_base_revision = @newRevision
WHERE id = @productId;
UPDATE article_mutation_outbox
SET next_attempt_at = 0;",
                new
                {
                    categoryId,
                    newRevision = Revision(2),
                    productId,
                    remoteCategoryId = Guid.NewGuid().ToString("D")
                });
        }

        var claim = await new ArticleMutationOutboxRepository(db.Factory)
            .ClaimBatchAsync("generation-reference-conflict");

        Assert.AreEqual(0, claim.Requests.Count);
        using var verify = db.Factory.Open();
        var blocked = await verify.QuerySingleAsync<(
            string State,
            string Code,
            string CanonicalPayload,
            string PayloadHash)>(@"
SELECT state AS State,
       last_typed_code AS Code,
       canonical_payload_json AS CanonicalPayload,
       payload_hash AS PayloadHash
FROM article_mutation_outbox;");
        Assert.AreEqual(
            ArticleMutationOutboxStates.FailedBlocked,
            blocked.State);
        Assert.AreEqual("failed_conflict", blocked.Code);
        Assert.IsNull(blocked.CanonicalPayload);
        Assert.IsNull(blocked.PayloadHash);
    }

    [TestMethod]
    public async Task LocalDependencyConflict_ReleasesLaterNonReferenceSuccessor()
    {
        using var db = TestDb.Create();
        var productId = await SeedRemoteProductAsync(
            db.Factory,
            "ART-REF-LOCAL-TERMINAL-001");
        var writer = new LocalArticleMutationWriter(db.Factory);
        var waitingReference = NewUpdate(
            productId,
            "ART-REF-LOCAL-TERMINAL-001",
            "Remote product");
        waitingReference.CategoryName = "Waiting local category";
        await writer.UpdateAsync(
            waitingReference,
            ProductWriteOrigin.LocalUserSave);
        var laterSuccessor = NewUpdate(
            productId,
            "ART-REF-LOCAL-TERMINAL-001",
            "Later safe successor");
        laterSuccessor.CategoryName = "Waiting local category";
        await writer.UpdateAsync(
            laterSuccessor,
            ProductWriteOrigin.LocalUserSave);

        using (var connection = db.Factory.Open())
        {
            CollectionAssert.AreEqual(
                new[]
                {
                    ArticleMutationOutboxStates.WaitingDependency,
                    ArticleMutationOutboxStates.WaitingDependency
                },
                (await connection.QueryAsync<string>(@"
SELECT state
FROM article_mutation_outbox
ORDER BY local_sequence;")).ToArray());
            var categoryId = await connection.ExecuteScalarAsync<int>(@"
SELECT category_id
FROM product_meta
WHERE barcode = 'ART-REF-LOCAL-TERMINAL-001';");
            await connection.ExecuteAsync(@"
UPDATE categories
SET remote_category_id = @remoteCategoryId
WHERE id = @categoryId;
UPDATE products
SET remote_base_revision = @newRevision
WHERE id = @productId;
UPDATE article_mutation_outbox
SET next_attempt_at = 0;",
                new
                {
                    categoryId,
                    newRevision = Revision(2),
                    productId,
                    remoteCategoryId = Guid.NewGuid().ToString("D")
                });
        }

        var claim = await new ArticleMutationOutboxRepository(db.Factory)
            .ClaimBatchAsync("generation-local-terminal-successor");

        using var verify = db.Factory.Open();
        var snapshots = (await verify.QueryAsync<(
            long Sequence,
            string State,
            string Code,
            string BaseRevision)>(@"
SELECT local_sequence AS Sequence,
       state AS State,
       last_typed_code AS Code,
       base_revision AS BaseRevision
FROM article_mutation_outbox
ORDER BY local_sequence;")).ToArray();
        var stateEvidence = string.Join(
            ";",
            snapshots.Select(row =>
                row.Sequence + ":" + row.State + ":" + row.Code + ":" +
                row.BaseRevision));
        Assert.AreEqual(1, claim.Requests.Count, stateEvidence);
        Assert.AreEqual(2L, claim.Requests.Single().Intent.LocalSequence);
        Assert.AreEqual(
            Revision(2),
            claim.Requests.Single().Intent.BaseRevision);
        Assert.AreEqual(
            "Later safe successor",
            claim.Requests.Single().Intent.Changes[
                PosArticleMutationFields.PrimaryName]);
        CollectionAssert.AreEqual(
            new[]
            {
                ArticleMutationOutboxStates.FailedBlocked,
                ArticleMutationOutboxStates.InProgress
            },
            (await verify.QueryAsync<string>(@"
SELECT state
FROM article_mutation_outbox
ORDER BY local_sequence;")).ToArray());
        Assert.AreEqual(
            PosArticleMutationStatusPolicy.FailedConflict,
            await verify.ExecuteScalarAsync<string>(@"
SELECT last_typed_code
FROM article_mutation_outbox
WHERE local_sequence = 1;"));
    }

    [TestMethod]
    public async Task ChainedMissingReference_FreezesPredecessorAckUntilResolution()
    {
        using var happyDb = TestDb.Create();
        var happyWriter = new LocalArticleMutationWriter(happyDb.Factory);
        var happyCreate = await happyWriter.CreateAsync(
            NewCreate("ART-CHAIN-REF-OK-001", "Created locally"),
            ProductWriteOrigin.LocalUserSave);
        var happyUpdate = NewUpdate(
            happyCreate.ProductId,
            "ART-CHAIN-REF-OK-001",
            "Created with category");
        happyUpdate.CategoryName = "Chained local category";
        await happyWriter.UpdateAsync(
            happyUpdate,
            ProductWriteOrigin.LocalUserSave);
        var happyOutbox =
            new ArticleMutationOutboxRepository(happyDb.Factory);
        var happyCreateClaim = await happyOutbox.ClaimBatchAsync(
            "generation-chain-create");
        var happyRemoteId = Guid.NewGuid().ToString("D");
        await ApplySuccessAsync(
            happyOutbox,
            happyCreateClaim,
            happyRemoteId,
            Revision(1),
            "1");
        using (var connection = happyDb.Factory.Open())
        {
            var frozen = await connection.QuerySingleAsync<(
                string RemoteProductId,
                string BaseRevision,
                string State)>(@"
SELECT remote_product_id AS RemoteProductId,
       base_revision AS BaseRevision,
       state AS State
FROM article_mutation_outbox
WHERE local_sequence = 2;");
            Assert.AreEqual(happyRemoteId, frozen.RemoteProductId);
            Assert.AreEqual(Revision(1), frozen.BaseRevision);
            Assert.AreEqual(
                ArticleMutationOutboxStates.WaitingDependency,
                frozen.State);
            await ResolveOnlyCategoryAsync(
                connection,
                "ART-CHAIN-REF-OK-001");
        }
        var happyUpdateClaim = await happyOutbox.ClaimBatchAsync(
            "generation-chain-reference-resolved");
        Assert.AreEqual(
            Revision(1),
            happyUpdateClaim.Requests.Single().Intent.BaseRevision);

        using var conflictDb = TestDb.Create();
        var conflictWriter =
            new LocalArticleMutationWriter(conflictDb.Factory);
        var conflictCreate = await conflictWriter.CreateAsync(
            NewCreate("ART-CHAIN-REF-CONFLICT-001", "Created locally"),
            ProductWriteOrigin.LocalUserSave);
        var conflictUpdate = NewUpdate(
            conflictCreate.ProductId,
            "ART-CHAIN-REF-CONFLICT-001",
            "Created with delayed category");
        conflictUpdate.CategoryName = "Delayed chained category";
        await conflictWriter.UpdateAsync(
            conflictUpdate,
            ProductWriteOrigin.LocalUserSave);
        var conflictOutbox =
            new ArticleMutationOutboxRepository(conflictDb.Factory);
        var conflictCreateClaim = await conflictOutbox.ClaimBatchAsync(
            "generation-chain-conflict-create");
        await ApplySuccessAsync(
            conflictOutbox,
            conflictCreateClaim,
            Guid.NewGuid().ToString("D"),
            Revision(2),
            "2");
        using (var connection = conflictDb.Factory.Open())
        {
            await connection.ExecuteAsync(@"
UPDATE products
SET remote_base_revision = @advancedRevision
WHERE id = @productId;",
                new
                {
                    advancedRevision = Revision(3),
                    productId = conflictCreate.ProductId
                });
            await ResolveOnlyCategoryAsync(
                connection,
                "ART-CHAIN-REF-CONFLICT-001");
        }

        var conflictClaim = await conflictOutbox.ClaimBatchAsync(
            "generation-chain-reference-conflict");
        Assert.AreEqual(0, conflictClaim.Requests.Count);
        using var conflictVerify = conflictDb.Factory.Open();
        Assert.AreEqual(
            ArticleMutationOutboxStates.FailedBlocked,
            await conflictVerify.ExecuteScalarAsync<string>(@"
SELECT state
FROM article_mutation_outbox
WHERE local_sequence = 2;"));
        Assert.AreEqual(
            PosArticleMutationStatusPolicy.FailedConflict,
            await conflictVerify.ExecuteScalarAsync<string>(@"
SELECT last_typed_code
FROM article_mutation_outbox
WHERE local_sequence = 2;"));
    }

    [TestMethod]
    public async Task PendingCreate_MaterializesReferenceAndRejectsCorruptIntentHash()
    {
        using var db = TestDb.Create();
        var create = NewCreate("ART-REF-CREATE-001", "Reference create");
        create.CategoryName = "Local create category";
        await new LocalArticleMutationWriter(db.Factory).CreateAsync(
            create,
            ProductWriteOrigin.LocalUserSave);

        using (var connection = db.Factory.Open())
        {
            Assert.AreEqual(
                ArticleMutationOutboxStates.WaitingDependency,
                await connection.ExecuteScalarAsync<string>(
                    "SELECT state FROM article_mutation_outbox;"));
            var categoryId = await connection.ExecuteScalarAsync<int>(@"
SELECT category_id
FROM product_meta
WHERE barcode = 'ART-REF-CREATE-001';");
            await connection.ExecuteAsync(@"
UPDATE categories
SET remote_category_id = @remoteCategoryId
WHERE id = @categoryId;
UPDATE article_mutation_outbox
SET next_attempt_at = 0;",
                new
                {
                    categoryId,
                    remoteCategoryId = Guid.NewGuid().ToString("D")
                });
        }

        var claim = await new ArticleMutationOutboxRepository(db.Factory)
            .ClaimBatchAsync("generation-create-reference");
        Assert.AreEqual(1, claim.Requests.Count);
        Assert.AreEqual(
            PosArticleMutationKinds.ProductCreate,
            claim.Requests.Single().Intent.MutationKind);
        Assert.IsTrue(
            Guid.TryParse(
                claim.Requests.Single().Intent.Changes[
                    PosArticleMutationFields.CategoryId] as string,
                out _));

        using var corruptDb = TestDb.Create();
        var corruptCreate = NewCreate(
            "ART-REF-CORRUPT-001",
            "Corrupt reference create");
        corruptCreate.CategoryName = "Corrupt local category";
        await new LocalArticleMutationWriter(corruptDb.Factory).CreateAsync(
            corruptCreate,
            ProductWriteOrigin.LocalUserSave);
        using (var connection = corruptDb.Factory.Open())
        {
            await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET intent_hash = 'sha256:0000000000000000000000000000000000000000000000000000000000000000',
    next_attempt_at = 0;");
        }
        var corruptClaim =
            await new ArticleMutationOutboxRepository(corruptDb.Factory)
                .ClaimBatchAsync("generation-corrupt-reference");
        Assert.AreEqual(0, corruptClaim.Requests.Count);
        using var verify = corruptDb.Factory.Open();
        Assert.AreEqual(
            ArticleMutationOutboxStates.FailedBlocked,
            await verify.ExecuteScalarAsync<string>(
                "SELECT state FROM article_mutation_outbox;"));
        Assert.AreEqual(
            "dependency_intent_hash_mismatch",
            await verify.ExecuteScalarAsync<string>(
                "SELECT last_typed_code FROM article_mutation_outbox;"));
    }

    [TestMethod]
    public async Task CatalogPull_UpdatesRemoteShadowWithoutOverwritingPendingLocalEdit()
    {
        using var db = TestDb.Create();
        var productId = await SeedRemoteProductAsync(db.Factory, "ART-OVERLAY-001");
        var remoteReferences = await SeedVerifiedReferencesAsync(db.Factory);
        var localReferences = await SeedVerifiedReferencesAsync(db.Factory);
        using (var connection = db.Factory.Open())
        {
            await connection.ExecuteAsync(@"
UPDATE product_meta
SET category_id = @categoryId,
    category_name = 'Remote category',
    supplier_id = @supplierId,
    supplier_name = 'Remote supplier'
WHERE barcode = 'ART-OVERLAY-001';",
                new
                {
                    categoryId = remoteReferences.CategoryId,
                    supplierId = remoteReferences.SupplierId
                });
        }
        var writer = new LocalArticleMutationWriter(db.Factory);
        var pendingUpdate = NewUpdate(
            productId,
            "ART-OVERLAY-001",
            "Pending local name");
        pendingUpdate.CategoryId = localReferences.CategoryId;
        pendingUpdate.CategoryName = "Remote category";
        pendingUpdate.SupplierId = localReferences.SupplierId;
        pendingUpdate.SupplierName = "Remote supplier";
        await writer.UpdateAsync(
            pendingUpdate,
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
                        RemoteCategoryId =
                            remoteReferences.CategoryRemoteId,
                        RemoteSupplierId =
                            remoteReferences.SupplierRemoteId,
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
            Assert.AreEqual(
                localReferences.CategoryId,
                await connection.ExecuteScalarAsync<int>(@"
SELECT category_id
FROM product_meta
WHERE barcode = 'ART-OVERLAY-001';"));
            Assert.AreEqual(
                localReferences.SupplierId,
                await connection.ExecuteScalarAsync<int>(@"
SELECT supplier_id
FROM product_meta
WHERE barcode = 'ART-OVERLAY-001';"));
        }

        var tombstoneRevision = Revision(10);
        await new RemoteCatalogBatchRepository(db.Factory).ApplyAsync(
            new RemoteCatalogBatch
            {
                ProductTombstones = new[]
                {
                    new RemoteCatalogProductTombstoneWrite
                    {
                        RemoteDeletedAt = tombstoneRevision,
                        RemoteProductId = remoteId,
                        RemoteUpdatedAt = tombstoneRevision
                    }
                }
            },
            CancellationToken.None);
        using (var connection = db.Factory.Open())
        {
            var shadow = await connection.QuerySingleAsync<(
                string PrimaryName,
                long IsActive,
                string Revision)>(@"
SELECT primary_name AS PrimaryName,
       is_active AS IsActive,
       authoritative_revision AS Revision
FROM article_product_remote_shadow
WHERE remote_product_id = @remoteId;",
                new { remoteId });
            Assert.AreEqual("New remote canonical name", shadow.PrimaryName);
            Assert.AreEqual(0L, shadow.IsActive);
            Assert.AreEqual(tombstoneRevision, shadow.Revision);
            Assert.AreEqual(
                "Pending local name",
                await connection.ExecuteScalarAsync<string>(
                    "SELECT name FROM products WHERE id = @productId;",
                    new { productId }));
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
    public async Task SuccessfulCorrection_ResolvesOnlyCoveredBlockedIntent()
    {
        using var coveredDb = TestDb.Create();
        var coveredProductId = await SeedRemoteProductAsync(
            coveredDb.Factory,
            "ART-CORRECTION-001");
        var coveredWriter =
            new LocalArticleMutationWriter(coveredDb.Factory);
        var firstCoveredUpdate = NewUpdate(
            coveredProductId,
            "ART-CORRECTION-001",
            "First rejected name");
        firstCoveredUpdate.ItemNumber = "ITEM-OLD";
        firstCoveredUpdate.SecondaryName = "Old secondary";
        await coveredWriter.UpdateAsync(
            firstCoveredUpdate,
            ProductWriteOrigin.LocalUserSave);
        var coveredOutbox =
            new ArticleMutationOutboxRepository(coveredDb.Factory);
        var rejected = await coveredOutbox.ClaimBatchAsync(
            "generation-correction-rejected");
        await ApplyConflictAsync(
            coveredOutbox,
            rejected,
            Revision(4),
            "4");

        var coveredCorrection = NewUpdate(
            coveredProductId,
            "ART-CORRECTION-001",
            "Corrected name");
        coveredCorrection.ItemNumber = "ITEM-OLD";
        coveredCorrection.SecondaryName = "Old secondary";
        await coveredWriter.UpdateAsync(
            coveredCorrection,
            ProductWriteOrigin.LocalUserSave);
        var correction = await coveredOutbox.ClaimBatchAsync(
            "generation-correction-applied");
        var correctionMutationId =
            correction.Requests.Single().Intent.MutationId;
        await ApplySuccessAsync(
            coveredOutbox,
            correction,
            await ProductRemoteIdAsync(
                coveredDb.Factory,
                coveredProductId),
            Revision(5),
            "5");

        using (var connection = coveredDb.Factory.Open())
        {
            var resolved = await connection.QuerySingleAsync<(
                string State,
                string ResolutionCode,
                string ResolvedAt,
                string SupersededBy)>(@"
SELECT state AS State,
       resolution_code AS ResolutionCode,
       resolved_at AS ResolvedAt,
       superseded_by_mutation_id AS SupersededBy
FROM article_mutation_outbox
WHERE local_sequence = 1;");
            Assert.AreEqual(
                ArticleMutationOutboxStates.Completed,
                resolved.State);
            Assert.AreEqual(
                "superseded_by_correction",
                resolved.ResolutionCode);
            Assert.IsFalse(string.IsNullOrWhiteSpace(resolved.ResolvedAt));
            Assert.AreEqual(
                correctionMutationId,
                resolved.SupersededBy);
            Assert.AreEqual(0L, await coveredOutbox.CountUnresolvedAsync(
                connection));
        }

        var coveredRemoteId = await ProductRemoteIdAsync(
            coveredDb.Factory,
            coveredProductId);
        await new RemoteCatalogBatchRepository(coveredDb.Factory).ApplyAsync(
            new RemoteCatalogBatch
            {
                Products = new[]
                {
                    new RemoteCatalogProductWrite
                    {
                        Barcode = "ART-CORRECTION-001",
                        Name = "Canonical after correction",
                        UnitPrice = 150,
                        PurchasePrice = 60,
                        StockQuantity = 15,
                        RemoteProductId = coveredRemoteId,
                        RemoteUpdatedAt = Revision(6)
                    }
                }
            },
            CancellationToken.None);
        using (var connection = coveredDb.Factory.Open())
        {
            Assert.AreEqual(
                "Canonical after correction",
                await connection.ExecuteScalarAsync<string>(
                    "SELECT name FROM products WHERE id = @id;",
                    new { id = coveredProductId }));
        }

        using var unrelatedDb = TestDb.Create();
        var unrelatedProductId = await SeedRemoteProductAsync(
            unrelatedDb.Factory,
            "ART-CORRECTION-UNRELATED-001");
        var unrelatedWriter =
            new LocalArticleMutationWriter(unrelatedDb.Factory);
        var firstUnrelatedUpdate = NewUpdate(
            unrelatedProductId,
            "ART-CORRECTION-UNRELATED-001",
            "Rejected name");
        firstUnrelatedUpdate.ItemNumber = "ITEM-OLD";
        firstUnrelatedUpdate.SecondaryName = "Old secondary";
        await unrelatedWriter.UpdateAsync(
            firstUnrelatedUpdate,
            ProductWriteOrigin.LocalUserSave);
        var unrelatedOutbox =
            new ArticleMutationOutboxRepository(unrelatedDb.Factory);
        await ApplyConflictAsync(
            unrelatedOutbox,
            await unrelatedOutbox.ClaimBatchAsync(
                "generation-unrelated-rejected"),
            Revision(7),
            "7");
        var itemOnly = NewUpdate(
            unrelatedProductId,
            "ART-CORRECTION-UNRELATED-001",
            "Rejected name");
        itemOnly.ItemNumber = "ITEM-CORRECTION-ONLY";
        itemOnly.SecondaryName = "Old secondary";
        await unrelatedWriter.UpdateAsync(
            itemOnly,
            ProductWriteOrigin.LocalUserSave);
        var unrelatedCorrection = await unrelatedOutbox.ClaimBatchAsync(
            "generation-unrelated-correction");
        CollectionAssert.AreEqual(
            new[] { PosArticleMutationFields.ItemNumber },
            unrelatedCorrection.Requests.Single().Intent.FieldMask.ToArray());
        await ApplySuccessAsync(
            unrelatedOutbox,
            unrelatedCorrection,
            await ProductRemoteIdAsync(
                unrelatedDb.Factory,
                unrelatedProductId),
            Revision(8),
            "8");
        using var unrelatedVerify = unrelatedDb.Factory.Open();
        Assert.AreEqual(
            ArticleMutationOutboxStates.FailedBlocked,
            await unrelatedVerify.ExecuteScalarAsync<string>(@"
SELECT state
FROM article_mutation_outbox
WHERE local_sequence = 1;"));
        Assert.AreEqual(
            1L,
            await unrelatedOutbox.CountUnresolvedAsync(
                unrelatedVerify));
    }

    [TestMethod]
    public async Task TerminalFailure_ReleasesQueuedSuccessorsBeforeCorrection()
    {
        using var db = TestDb.Create();
        var productId = await SeedRemoteProductAsync(
            db.Factory,
            "ART-TERMINAL-SUCCESSOR-001");
        var writer = new LocalArticleMutationWriter(db.Factory);
        await writer.UpdateAsync(
            new LocalArticleUpdateRequest
            {
                ProductId = productId,
                Barcode = "ART-TERMINAL-SUCCESSOR-001",
                PrimaryName = "Rejected full edit",
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
        var productClaim = await outbox.ClaimBatchAsync(
            "generation-terminal-product");
        Assert.AreEqual(
            PosArticleMutationKinds.ProductUpdate,
            productClaim.Requests.Single().Intent.MutationKind);
        await ApplyConflictAsync(
            outbox,
            productClaim,
            Revision(4),
            "4");

        using (var connection = db.Factory.Open())
        {
            CollectionAssert.AreEqual(
                new[]
                {
                    ArticleMutationOutboxStates.FailedBlocked,
                    ArticleMutationOutboxStates.Pending,
                    ArticleMutationOutboxStates.WaitingDependency
                },
                (await connection.QueryAsync<string>(@"
SELECT state
FROM article_mutation_outbox
ORDER BY local_sequence;")).ToArray());
        }

        var priceClaim = await outbox.ClaimBatchAsync(
            "generation-terminal-price");
        Assert.AreEqual(
            PosArticleMutationKinds.ProductPurchasePriceChange,
            priceClaim.Requests.Single().Intent.MutationKind);
        Assert.AreEqual(
            Revision(4),
            priceClaim.Requests.Single().Intent.BaseRevision);
        await ApplySuccessAsync(
            outbox,
            priceClaim,
            await ProductRemoteIdAsync(db.Factory, productId),
            Revision(5),
            "5",
            priceHistoryId: Guid.NewGuid().ToString("D"));

        var stockClaim = await outbox.ClaimBatchAsync(
            "generation-terminal-stock");
        Assert.AreEqual(
            PosArticleMutationKinds.ProductManualStockAdjustment,
            stockClaim.Requests.Single().Intent.MutationKind);
        Assert.AreEqual(
            Revision(5),
            stockClaim.Requests.Single().Intent.BaseRevision);
        await ApplySuccessAsync(
            outbox,
            stockClaim,
            await ProductRemoteIdAsync(db.Factory, productId),
            Revision(6),
            "6",
            stockMovementId: Guid.NewGuid().ToString("D"));

        await writer.UpdateAsync(
            new LocalArticleUpdateRequest
            {
                ProductId = productId,
                Barcode = "ART-TERMINAL-SUCCESSOR-001",
                PrimaryName = "Corrected full edit",
                ItemNumber = "ITEM-OLD",
                SecondaryName = "Old secondary",
                RetailPrice = 150,
                PurchasePrice = 70,
                StockQuantity = 18,
                StockReason = "count_correction",
                OccurredAt = DateTimeOffset.UtcNow
            },
            ProductWriteOrigin.LocalUserSave);
        var correction = await outbox.ClaimBatchAsync(
            "generation-terminal-correction");
        Assert.AreEqual(
            PosArticleMutationKinds.ProductUpdate,
            correction.Requests.Single().Intent.MutationKind);
        await ApplySuccessAsync(
            outbox,
            correction,
            await ProductRemoteIdAsync(db.Factory, productId),
            Revision(7),
            "7");
        Assert.AreEqual(0L, await outbox.CountUnresolvedAsync());
    }

    [TestMethod]
    public async Task ClaimTimeStaleBase_ReleasesQueuedSuccessor()
    {
        using var db = TestDb.Create();
        var productId = await SeedRemoteProductAsync(
            db.Factory,
            "ART-STALE-SUCCESSOR-001");
        await new LocalArticleMutationWriter(db.Factory).UpdateAsync(
            new LocalArticleUpdateRequest
            {
                ProductId = productId,
                Barcode = "ART-STALE-SUCCESSOR-001",
                PrimaryName = "Locally edited",
                ItemNumber = "ITEM-OLD",
                SecondaryName = "Old secondary",
                RetailPrice = 150,
                PurchasePrice = 70,
                StockQuantity = 15,
                StockReason = "count_correction",
                OccurredAt = DateTimeOffset.UtcNow
            },
            ProductWriteOrigin.LocalUserSave);
        using (var connection = db.Factory.Open())
        {
            CollectionAssert.AreEqual(
                new[]
                {
                    ArticleMutationOutboxStates.Pending,
                    ArticleMutationOutboxStates.WaitingDependency
                },
                (await connection.QueryAsync<string>(@"
SELECT state
FROM article_mutation_outbox
ORDER BY local_sequence;")).ToArray());
            await connection.ExecuteAsync(@"
UPDATE products
SET remote_base_revision = @newRevision
WHERE id = @productId;",
                new
                {
                    newRevision = Revision(2),
                    productId
                });
        }

        var outbox = new ArticleMutationOutboxRepository(db.Factory);
        var staleClaim = await outbox.ClaimBatchAsync(
            "generation-stale-preflight");
        Assert.AreEqual(0, staleClaim.Requests.Count);
        using (var afterStale = db.Factory.Open())
        {
            CollectionAssert.AreEqual(
                new[]
                {
                    ArticleMutationOutboxStates.FailedBlocked,
                    ArticleMutationOutboxStates.Pending
                },
                (await afterStale.QueryAsync<string>(@"
SELECT state
FROM article_mutation_outbox
ORDER BY local_sequence;")).ToArray());
            Assert.AreEqual(
                PosArticleMutationStatusPolicy.FailedConflict,
                await afterStale.ExecuteScalarAsync<string>(@"
SELECT last_typed_code
FROM article_mutation_outbox
WHERE local_sequence = 1;"));
        }

        var successorClaim = await outbox.ClaimBatchAsync(
            "generation-stale-successor");
        Assert.AreEqual(
            PosArticleMutationKinds.ProductPurchasePriceChange,
            successorClaim.Requests.Single().Intent.MutationKind);
        Assert.AreEqual(
            Revision(2),
            successorClaim.Requests.Single().Intent.BaseRevision);
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
        var catalogBatch = new RemoteCatalogBatch
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
            },
            Prices = new[]
            {
                new RemoteCatalogPriceWrite
                {
                    EffectiveAt = "2026-07-28 12:00:00",
                    Price = 70,
                    RemotePriceId = priceHistoryId,
                    RemoteProductId = remoteId,
                    Source = "pos_article_mutation_v1",
                    Type = "PURCHASE"
                }
            }
        };

        var pullBeforeAckBatch = new RemoteCatalogBatch
        {
            Prices = new[]
            {
                new RemoteCatalogPriceWrite
                {
                    EffectiveAt = null,
                    Price = 70,
                    RemotePriceId = priceHistoryId,
                    RemoteProductId = remoteId,
                    Source = "pos_article_mutation_v1",
                    Type = "PURCHASE"
                }
            }
        };
        await new RemoteCatalogBatchRepository(db.Factory).ApplyAsync(
            pullBeforeAckBatch,
            CancellationToken.None);
        using (var pulledBeforeAck = db.Factory.Open())
        {
            Assert.AreEqual(
                2L,
                await pulledBeforeAck.ExecuteScalarAsync<long>(
                    "SELECT COUNT(1) FROM product_price_history;"));
            Assert.IsNull(
                await pulledBeforeAck.ExecuteScalarAsync<string>(@"
SELECT remote_price_id
FROM product_price_history
WHERE article_mutation_id IS NOT NULL;"));
        }

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
            catalogBatch,
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
            priceHistoryId,
            await connection.ExecuteScalarAsync<string>(@"
SELECT remote_price_id
FROM product_price_history
WHERE article_mutation_id IS NOT NULL;"));
        Assert.AreEqual(
            "pos_article_mutation_v1",
            await connection.ExecuteScalarAsync<string>(@"
SELECT source
FROM product_price_history
WHERE article_mutation_id IS NOT NULL;"));
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

    [TestMethod]
    public async Task PriceAck_MergesOnlyItsAuthoritativePulledRemoteEvent()
    {
        using var db = TestDb.Create();
        var productId = await SeedRemoteProductAsync(
            db.Factory,
            "ART-PRICE-AMBIGUOUS-001");
        await new LocalArticleMutationWriter(db.Factory).UpdateAsync(
            new LocalArticleUpdateRequest
            {
                ProductId = productId,
                Barcode = "ART-PRICE-AMBIGUOUS-001",
                PrimaryName = "Remote product",
                ItemNumber = "ITEM-OLD",
                SecondaryName = "Old secondary",
                RetailPrice = 150,
                PurchasePrice = 70,
                StockQuantity = 15,
                StockReason = "count_correction",
                OccurredAt = DateTimeOffset.UtcNow
            },
            ProductWriteOrigin.LocalUserSave);
        var outbox = new ArticleMutationOutboxRepository(db.Factory);
        var claim = await outbox.ClaimBatchAsync(
            "generation-price-ambiguous");
        await new LocalArticleMutationWriter(db.Factory).UpdateAsync(
            new LocalArticleUpdateRequest
            {
                ProductId = productId,
                Barcode = "ART-PRICE-AMBIGUOUS-NEW",
                PrimaryName = "Remote product",
                ItemNumber = "ITEM-OLD",
                SecondaryName = "Old secondary",
                RetailPrice = 150,
                PurchasePrice = 70,
                StockQuantity = 15,
                StockReason = "count_correction",
                OccurredAt = DateTimeOffset.UtcNow
            },
            ProductWriteOrigin.LocalUserSave);
        var remoteId = await ProductRemoteIdAsync(db.Factory, productId);
        var unrelatedRemotePriceId = Guid.NewGuid().ToString("D");
        var ackRemotePriceId = Guid.NewGuid().ToString("D");

        await new RemoteCatalogBatchRepository(db.Factory).ApplyAsync(
            new RemoteCatalogBatch
            {
                Prices = new[]
                {
                    new RemoteCatalogPriceWrite
                    {
                        EffectiveAt = "2026-07-28 12:00:01",
                        Price = 70,
                        RemotePriceId = unrelatedRemotePriceId,
                        RemoteProductId = remoteId,
                        Source = "pos_article_mutation_v1",
                        Type = "PURCHASE"
                    },
                    new RemoteCatalogPriceWrite
                    {
                        EffectiveAt = "2026-07-28 12:00:02",
                        Price = 70,
                        RemotePriceId = ackRemotePriceId,
                        RemoteProductId = remoteId,
                        Source = "pos_article_mutation_v1",
                        Type = "PURCHASE"
                    }
                }
            },
            CancellationToken.None);
        using (var beforeAck = db.Factory.Open())
        {
            Assert.AreEqual(
                3L,
                await beforeAck.ExecuteScalarAsync<long>(
                    "SELECT COUNT(1) FROM product_price_history;"));
            Assert.IsNull(
                await beforeAck.ExecuteScalarAsync<string>(@"
SELECT remote_price_id
FROM product_price_history
WHERE article_mutation_id IS NOT NULL;"));
            Assert.AreEqual(
                "ART-PRICE-AMBIGUOUS-001",
                await beforeAck.ExecuteScalarAsync<string>(@"
SELECT barcode
FROM product_price_history
WHERE article_mutation_id IS NOT NULL;"));
            Assert.AreEqual(
                2L,
                await beforeAck.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM product_price_history
WHERE article_mutation_id IS NULL
  AND barcode = 'ART-PRICE-AMBIGUOUS-NEW';"));
        }

        await ApplySuccessAsync(
            outbox,
            claim,
            remoteId,
            Revision(8),
            "8",
            priceHistoryId: ackRemotePriceId);

        using var verify = db.Factory.Open();
        Assert.AreEqual(
            2L,
            await verify.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM product_price_history;"));
        Assert.AreEqual(
            ackRemotePriceId,
            await verify.ExecuteScalarAsync<string>(@"
SELECT remote_price_id
FROM product_price_history
WHERE article_mutation_id IS NOT NULL;"));
        Assert.AreEqual(
            unrelatedRemotePriceId,
            await verify.ExecuteScalarAsync<string>(@"
SELECT remote_price_id
FROM product_price_history
WHERE article_mutation_id IS NULL;"));
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

    private static async Task ApplyConflictAsync(
        ArticleMutationOutboxRepository repository,
        ArticleMutationClaim claim,
        string authoritativeRevision,
        string catalogRevision)
    {
        var request = claim.Requests.Single();
        var result = new PosArticleMutationResult
        {
            DeliveryStatus = PosArticleMutationStatusPolicy.FailedConflict,
            Ack = new PosArticleMutationAck
            {
                AttemptToken = request.AttemptToken,
                AuthoritativeRevision = authoritativeRevision,
                CatalogRevision = catalogRevision,
                Code = PosArticleMutationStatusPolicy.FailedConflict,
                IdempotencyKey = request.Intent.IdempotencyKey,
                MutationId = request.Intent.MutationId,
                PayloadHash = request.PayloadHash,
                RemoteProductId = request.Intent.RemoteProductId,
                Retryable = false,
                SchemaVersion = PosArticleMutationContract.SchemaVersion,
                ServerTimestamp = authoritativeRevision,
                Status = PosArticleMutationStatusPolicy.FailedConflict,
                Terminal = true
            }
        };
        var validation = PosArticleMutationResponseValidator.Validate(
            new PosArticleMutationResponse
            {
                Code = "success",
                Ok = false,
                Results = new[] { result },
                SchemaVersion = PosArticleMutationContract.SchemaVersion,
                ServerTime = authoritativeRevision
            },
            claim.Requests,
            (_, attempt) => string.Equals(
                attempt,
                request.AttemptToken,
                StringComparison.Ordinal));
        Assert.IsTrue(validation.IsValid, validation.Code);
        await repository.ApplyValidatedResponseAsync(claim, validation);
    }

    private static async Task ResolveOnlyCategoryAsync(
        SqliteConnection connection,
        string barcode)
    {
        var categoryId = await connection.ExecuteScalarAsync<int>(@"
SELECT category_id
FROM product_meta
WHERE barcode = @barcode;",
            new { barcode });
        await connection.ExecuteAsync(@"
UPDATE categories
SET remote_category_id = @remoteCategoryId
WHERE id = @categoryId;
UPDATE article_mutation_outbox
SET next_attempt_at = 0
WHERE state = 'waiting_dependency';",
            new
            {
                categoryId,
                remoteCategoryId = Guid.NewGuid().ToString("D")
            });
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
