using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class ProductImageOperationOutboxRepositoryTests
{
    private const string RemoteProductId = "20000000-0000-4000-8000-000000000150";
    private const string CurrentVersionId = "30000000-0000-4000-8000-000000000150";
    private const string PayloadHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [TestMethod]
    public async Task OfflineReplaceWaitsForRemoteDependencyAndSurvivesRestart()
    {
        using var db = TestDb.Create();
        var productId = await InsertProductAsync(db.Factory, "IMG-OFFLINE-1", null);
        var first = new ProductImageOperationOutboxRepository(db.Factory);

        var queued = await first.EnqueueReplaceAsync(Replace(productId));

        Assert.AreEqual(ProductImageOperationStates.WaitingDependency, queued.State);
        var restarted = new ProductImageOperationOutboxRepository(db.Factory);
        Assert.AreEqual(ProductImageOperationStates.WaitingDependency, (await restarted.GetAsync(queued.OperationId))?.State);
        var released = await restarted.ReleaseDependenciesAsync(
            productId,
            RemoteProductId,
            _ => "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        Assert.AreEqual(1, released);
        var row = await restarted.GetAsync(queued.OperationId);
        Assert.AreEqual(ProductImageOperationStates.PendingIntent, row?.State);
        Assert.AreEqual(RemoteProductId, row?.RemoteProductId);
    }

    [TestMethod]
    public async Task DeleteBeforeRemoteIdentityCancelsOnlyLocalStaging()
    {
        using var db = TestDb.Create();
        var productId = await InsertProductAsync(db.Factory, "IMG-CANCEL-1", null);
        var repository = new ProductImageOperationOutboxRepository(db.Factory);
        var queued = await repository.EnqueueReplaceAsync(Replace(productId));

        var staging = await repository.CancelWaitingForDeletedProductAsync(productId);

        Assert.AreEqual(1, staging.Count);
        Assert.AreEqual("main-opaque.jpg", staging[0].MainIdentity);
        Assert.AreEqual("thumb-opaque.jpg", staging[0].ThumbIdentity);
        var row = await repository.GetAsync(queued.OperationId);
        Assert.AreEqual(ProductImageOperationStates.Completed, row?.State);
        Assert.AreEqual("cancelled_before_remote_identity", row?.CompletionState);
        Assert.IsNull(row?.StagedMainIdentity);
        Assert.IsNull(row?.StagedThumbIdentity);
    }

    [TestMethod]
    public async Task ClaimsAreFencedSingleFlightAndOrderedPerProduct()
    {
        using var db = TestDb.Create();
        var firstProduct = await InsertProductAsync(db.Factory, "IMG-CLAIM-1", RemoteProductId);
        var secondProduct = await InsertProductAsync(
            db.Factory,
            "IMG-CLAIM-2",
            "20000000-0000-4000-8000-000000000151");
        var repository = new ProductImageOperationOutboxRepository(db.Factory);
        var first = await repository.EnqueueReplaceAsync(Replace(firstProduct), Seal);
        var second = await repository.EnqueueRemoveAsync(new ProductImageRemoveEnqueueRequest
        {
            LocalProductId = secondProduct,
            ExpectedCurrentVersionId = CurrentVersionId,
            PayloadHash = PayloadHash
        }, Seal);

        var claimOne = await repository.ClaimNextAsync("generation-a", 1);
        var claimTwo = await repository.ClaimNextAsync("generation-a", 1);

        Assert.IsNotNull(claimOne);
        Assert.IsNotNull(claimTwo);
        Assert.AreNotEqual(claimOne.Operation.LocalProductId, claimTwo.Operation.LocalProductId);
        Assert.IsFalse(await repository.AdvanceAsync(
            new ProductImageOperationClaim
            {
                ClaimGenerationId = "stale-generation",
                ClaimFence = claimOne.ClaimFence,
                Operation = claimOne.Operation
            },
            ProductImageOperationStates.PendingUpload));
        Assert.IsTrue(await repository.AdvanceAsync(
            claimOne,
            ProductImageOperationStates.PendingUpload,
            "40000000-0000-4000-8000-000000000150"));
        Assert.IsTrue(await repository.BlockAsync(claimTwo, "expected_version_conflict"));
        Assert.AreEqual(ProductImageOperationStates.PendingUpload, (await repository.GetAsync(first.OperationId))?.State);
        Assert.AreEqual(ProductImageOperationStates.FailedBlocked, (await repository.GetAsync(second.OperationId))?.State);
    }

    [TestMethod]
    public async Task InterruptedClaimRecoversWithoutChangingOperationIdentity()
    {
        using var db = TestDb.Create();
        var productId = await InsertProductAsync(db.Factory, "IMG-RECOVER-1", RemoteProductId);
        var repository = new ProductImageOperationOutboxRepository(db.Factory);
        var queued = await repository.EnqueueReplaceAsync(Replace(productId), Seal);
        var claim = await repository.ClaimNextAsync("durable-generation", 1);

        Assert.AreEqual(1, await repository.RecoverInterruptedClaimsAsync("durable-generation"));
        var recovered = await repository.GetAsync(queued.OperationId);
        Assert.AreEqual(ProductImageOperationStates.RetryWait, recovered?.State);
        Assert.AreEqual(claim?.Operation.OperationId, recovered?.OperationId);
        Assert.AreEqual(claim?.Operation.PayloadHash, recovered?.PayloadHash);
        var replay = await repository.ClaimNextAsync("durable-generation", 1);
        Assert.AreEqual(ProductImageOperationStates.PendingIntent, replay?.Operation.ResumeState);
    }

    [TestMethod]
    public async Task DueRetryCannotBeStarvedByNewerOperations()
    {
        using var db = TestDb.Create();
        var olderProduct = await InsertProductAsync(db.Factory, "IMG-FAIR-1", RemoteProductId);
        var newerProduct = await InsertProductAsync(
            db.Factory,
            "IMG-FAIR-2",
            "20000000-0000-4000-8000-000000000152");
        var repository = new ProductImageOperationOutboxRepository(db.Factory);
        var older = await repository.EnqueueReplaceAsync(Replace(olderProduct), Seal);
        var firstClaim = await repository.ClaimNextAsync("generation-fair", 1);
        Assert.IsTrue(await repository.RetryAsync(
            firstClaim,
            "retryable_upstream",
            DateTimeOffset.FromUnixTimeMilliseconds(2)));
        await repository.EnqueueReplaceAsync(Replace(newerProduct), Seal);

        var nextClaim = await repository.ClaimNextAsync("generation-fair", 2);

        Assert.AreEqual(older.OperationId, nextClaim?.Operation.OperationId);
    }

    [TestMethod]
    public async Task WaitingDependencyHasBoundedLostSignalWakeup()
    {
        using var db = TestDb.Create();
        var productId = await InsertProductAsync(db.Factory, "IMG-WAKE-1", null);
        var repository = new ProductImageOperationOutboxRepository(db.Factory);
        await repository.EnqueueReplaceAsync(Replace(productId));

        var drain = await repository.GetDrainStateAsync(100);

        Assert.AreEqual(1, drain.Unresolved);
        Assert.AreEqual(0, drain.RemainingDue);
        Assert.AreEqual(1, drain.WaitingDependencies);
        Assert.IsNull(drain.NextRetryAt);
        Assert.AreEqual(5100L, drain.NextWakeAt);
    }

    [TestMethod]
    public async Task DependencyWakeupPrecedesUnrelatedFutureRetry()
    {
        using var db = TestDb.Create();
        var waitingProduct = await InsertProductAsync(db.Factory, "IMG-WAKE-WAITING", null);
        var retryProduct = await InsertProductAsync(
            db.Factory,
            "IMG-WAKE-RETRY",
            "20000000-0000-4000-8000-000000000154");
        var repository = new ProductImageOperationOutboxRepository(db.Factory);
        await repository.EnqueueReplaceAsync(Replace(waitingProduct));
        await repository.EnqueueReplaceAsync(Replace(retryProduct), Seal);
        using (var connection = db.Factory.Open())
        {
            await connection.ExecuteAsync(@"
UPDATE product_image_operation_outbox
SET state = 'retry_wait', next_attempt_at = 600000
WHERE local_product_id = @retryProduct;", new { retryProduct });
        }

        var drain = await repository.GetDrainStateAsync(100);

        Assert.AreEqual(1, drain.WaitingDependencies);
        Assert.AreEqual(600000L, drain.NextRetryAt);
        Assert.AreEqual(5100L, drain.NextWakeAt);
    }

    [TestMethod]
    public async Task RetryBehindWaitingDependencyIsNotAdvertisedAsEligible()
    {
        using var db = TestDb.Create();
        var productId = await InsertProductAsync(db.Factory, "IMG-WAKE-FIFO", null);
        var repository = new ProductImageOperationOutboxRepository(db.Factory);
        await repository.EnqueueReplaceAsync(Replace(productId));
        await repository.EnqueueReplaceAsync(Replace(productId));
        using (var connection = db.Factory.Open())
        {
            await connection.ExecuteAsync(@"
UPDATE product_image_operation_outbox
SET state = 'retry_wait',
    remote_product_id = @remoteProductId,
    next_attempt_at = 600000
WHERE local_product_id = @productId
  AND id = (SELECT MAX(id) FROM product_image_operation_outbox WHERE local_product_id = @productId);",
                new { productId, remoteProductId = RemoteProductId });
        }

        var drain = await repository.GetDrainStateAsync(100);

        Assert.AreEqual(1, drain.WaitingDependencies);
        Assert.IsNull(drain.NextRetryAt);
        Assert.AreEqual(5100L, drain.NextWakeAt);
    }

    [TestMethod]
    public async Task DrainDoesNotCountDueSuccessorBlockedByEarlierRetry()
    {
        using var db = TestDb.Create();
        var productId = await InsertProductAsync(db.Factory, "IMG-DRAIN-1", RemoteProductId);
        var repository = new ProductImageOperationOutboxRepository(db.Factory);
        await repository.EnqueueReplaceAsync(Replace(productId), Seal);
        await repository.EnqueueReplaceAsync(Replace(productId), Seal);
        var first = await repository.ClaimNextAsync("generation-drain", 1);
        Assert.IsTrue(await repository.RetryAsync(
            first,
            "retryable_upstream",
            DateTimeOffset.FromUnixTimeMilliseconds(1000)));

        var drain = await repository.GetDrainStateAsync(100);

        Assert.AreEqual(2, drain.Unresolved);
        Assert.AreEqual(0, drain.RemainingDue);
        Assert.IsFalse(drain.HasImmediateMore);
        Assert.AreEqual(1000L, drain.NextRetryAt);
        Assert.AreEqual(1000L, drain.NextWakeAt);
    }

    [TestMethod]
    public async Task DrainStateExposesEveryUserVisibleQueueState()
    {
        using var db = TestDb.Create();
        var repository = new ProductImageOperationOutboxRepository(db.Factory);
        var pendingProduct = await InsertProductAsync(db.Factory, "IMG-STATUS-PENDING", RemoteProductId);
        var retryProduct = await InsertProductAsync(
            db.Factory,
            "IMG-STATUS-RETRY",
            "20000000-0000-4000-8000-000000000151");
        var progressProduct = await InsertProductAsync(
            db.Factory,
            "IMG-STATUS-PROGRESS",
            "20000000-0000-4000-8000-000000000152");
        var blockedProduct = await InsertProductAsync(
            db.Factory,
            "IMG-STATUS-BLOCKED",
            "20000000-0000-4000-8000-000000000153");
        await repository.EnqueueReplaceAsync(Replace(pendingProduct), Seal);
        await repository.EnqueueReplaceAsync(Replace(retryProduct), Seal);
        await repository.EnqueueReplaceAsync(Replace(progressProduct), Seal);
        await repository.EnqueueReplaceAsync(Replace(blockedProduct), Seal);
        using (var connection = db.Factory.Open())
        {
            await connection.ExecuteAsync(@"
UPDATE product_image_operation_outbox
SET state = CASE local_product_id
    WHEN @retryProduct THEN 'retry_wait'
    WHEN @progressProduct THEN 'in_progress'
    WHEN @blockedProduct THEN 'failed_blocked'
    ELSE state
END,
next_attempt_at = CASE
    WHEN local_product_id = @pendingProduct THEN 0
    WHEN local_product_id = @retryProduct THEN 1000
    ELSE next_attempt_at
END;",
                new { pendingProduct, retryProduct, progressProduct, blockedProduct });
        }

        var drain = await repository.GetDrainStateAsync(100);

        Assert.AreEqual(1, drain.Pending);
        Assert.AreEqual(1, drain.Retry);
        Assert.AreEqual(1, drain.InProgress);
        Assert.AreEqual(1, drain.Blocked);
        Assert.AreEqual(3, drain.Unresolved);
        Assert.AreEqual(1, drain.RemainingDue);
        Assert.AreEqual(1000L, drain.NextRetryAt);
        Assert.AreEqual(1000L, drain.NextWakeAt);
    }

    [TestMethod]
    public async Task DurableSchemaAndRowsCannotContainEphemeralCapabilityFields()
    {
        using var db = TestDb.Create();
        var productId = await InsertProductAsync(db.Factory, "IMG-SECRET-1", RemoteProductId);
        var repository = new ProductImageOperationOutboxRepository(db.Factory);
        await repository.EnqueueReplaceAsync(Replace(productId), Seal);
        using var connection = db.Factory.Open();

        var columns = (await connection.QueryAsync<string>(
            "SELECT name FROM pragma_table_info('product_image_operation_outbox');")).ToArray();
        var forbidden = new[] { "url", "token", "cookie", "authorization", "storage_path", "raw_body" };
        Assert.IsFalse(columns.Any(column => forbidden.Any(term => column.Contains(term, StringComparison.OrdinalIgnoreCase))));
        var material = await connection.ExecuteScalarAsync<string>(@"
SELECT group_concat(COALESCE(operation_id, '') || COALESCE(idempotency_key, '') ||
                    COALESCE(payload_hash, '') || COALESCE(staged_main_identity, '') ||
                    COALESCE(staged_thumb_identity, ''), '')
FROM product_image_operation_outbox;");
        Assert.IsFalse((material ?? string.Empty).Contains("https://", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse((material ?? string.Empty).Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task DependencyRelease_RollsBackEveryRowWhenOnePayloadSealFails()
    {
        using var db = TestDb.Create();
        var productId = await InsertProductAsync(db.Factory, "IMG-ROLLBACK-1", null);
        var repository = new ProductImageOperationOutboxRepository(db.Factory);
        var first = await repository.EnqueueReplaceAsync(Replace(productId));
        var second = await repository.EnqueueReplaceAsync(Replace(productId));
        var calls = 0;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            repository.ReleaseDependenciesAsync(
                productId,
                RemoteProductId,
                _ => ++calls == 1 ? PayloadHash : "invalid"));

        var firstRow = await repository.GetAsync(first.OperationId);
        var secondRow = await repository.GetAsync(second.OperationId);
        Assert.AreEqual(ProductImageOperationStates.WaitingDependency, firstRow.State);
        Assert.AreEqual(ProductImageOperationStates.WaitingDependency, secondRow.State);
        Assert.IsNull(firstRow.RemoteProductId);
        Assert.IsNull(secondRow.RemoteProductId);
    }

    [TestMethod]
    public async Task BlockedRetry_CreatesFreshIdentitiesAndUsesTheirResealedHash()
    {
        using var db = TestDb.Create();
        var productId = await InsertProductAsync(db.Factory, "IMG-RETRY-1", RemoteProductId);
        var repository = new ProductImageOperationOutboxRepository(db.Factory);
        var queued = await repository.EnqueueReplaceAsync(Replace(productId), Seal);
        var claim = await repository.ClaimNextAsync("generation-a", 1);
        Assert.IsTrue(await repository.BlockAsync(claim, "image_conflict"));
        string? resealedOperation = null;
        string? resealedIdempotency = null;
        var resealedHash = "sha256:" + new string('c', 64);

        const string relinkedRemoteProductId = "20000000-0000-4000-8000-000000000199";
        Assert.IsTrue(await repository.RetryBlockedAsNewAsync(
            queued.OperationId,
            relinkedRemoteProductId,
            CurrentVersionId,
            (operationId, idempotencyKey) =>
            {
                resealedOperation = operationId;
                resealedIdempotency = idempotencyKey;
                return resealedHash;
            }));

        var row = await repository.GetLatestForProductAsync(productId);
        Assert.AreEqual(resealedOperation, row.OperationId);
        Assert.AreEqual(resealedIdempotency, row.IdempotencyKey);
        Assert.AreEqual(resealedHash, row.PayloadHash);
        Assert.AreEqual(relinkedRemoteProductId, row.RemoteProductId);
        Assert.AreEqual(ProductImageOperationStates.PendingIntent, row.State);
        Assert.AreEqual(0, row.AttemptCount);
    }

    [TestMethod]
    public async Task SchemaRejectsNonLowercaseOrNonHexDigests()
    {
        using var db = TestDb.Create();
        var productId = await InsertProductAsync(db.Factory, "IMG-CHECK-1", null);
        var repository = new ProductImageOperationOutboxRepository(db.Factory);
        var queued = await repository.EnqueueReplaceAsync(Replace(productId));
        using var connection = db.Factory.Open();

        using (var payloadCommand = connection.CreateCommand())
        {
            payloadCommand.CommandText = @"
UPDATE product_image_operation_outbox
SET payload_hash = 'sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
WHERE operation_id = $operationId;";
            payloadCommand.Parameters.AddWithValue(
                "$operationId",
                queued.OperationId);
            await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                payloadCommand.ExecuteNonQueryAsync());
        }
        using (var digestCommand = connection.CreateCommand())
        {
            digestCommand.CommandText = @"
UPDATE product_image_operation_outbox
SET main_sha256 = 'gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg'
WHERE operation_id = $operationId;";
            digestCommand.Parameters.AddWithValue(
                "$operationId",
                queued.OperationId);
            await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                digestCommand.ExecuteNonQueryAsync());
        }
    }

    [TestMethod]
    public async Task ProductDeactivateAtomicallyCancelsWaitingImagesAndQueuesMutation()
    {
        using var db = TestDb.Create();
        var productId = await InsertProductAsync(db.Factory, "IMG-ATOMIC-1", null);
        var outbox = new ProductImageOperationOutboxRepository(db.Factory);
        var queued = await outbox.EnqueueReplaceAsync(Replace(productId));

        var result = await new ProductRepository(db.Factory).SetLocalArticleActiveAsync(
            productId,
            false,
            ProductWriteOrigin.LocalUserSave);

        Assert.AreEqual(1, result.CancelledProductImages.Count);
        Assert.AreEqual(1, result.Mutations.Count);
        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(
            "SELECT is_active FROM products WHERE id = @productId;",
            new { productId }));
        Assert.AreEqual(
            ProductImageOperationStates.Completed,
            (await outbox.GetAsync(queued.OperationId))?.State);
    }

    [TestMethod]
    public async Task ProductDeactivateFailureRollsBackImageCancellationAndProductState()
    {
        using var db = TestDb.Create();
        var productId = await InsertProductAsync(db.Factory, "IMG-ATOMIC-2", null);
        var outbox = new ProductImageOperationOutboxRepository(db.Factory);
        var queued = await outbox.EnqueueReplaceAsync(Replace(productId));
        using (var fault = db.Factory.Open())
        {
            await fault.ExecuteAsync(@"
CREATE TRIGGER fail_article_mutation_enqueue
BEFORE INSERT ON article_mutation_outbox
BEGIN
  SELECT RAISE(ABORT, 'injected_article_mutation_failure');
END;");
        }

        await Assert.ThrowsExactlyAsync<SqliteException>(() =>
            new ProductRepository(db.Factory).SetLocalArticleActiveAsync(
                productId,
                false,
                ProductWriteOrigin.LocalUserSave));

        using var verify = db.Factory.Open();
        Assert.AreEqual(1L, await verify.ExecuteScalarAsync<long>(
            "SELECT is_active FROM products WHERE id = @productId;",
            new { productId }));
        var row = await outbox.GetAsync(queued.OperationId);
        Assert.AreEqual(ProductImageOperationStates.WaitingDependency, row?.State);
        Assert.AreEqual("main-opaque.jpg", row?.StagedMainIdentity);
    }

    private static ProductImageReplaceEnqueueRequest Replace(long productId) => new()
    {
        LocalProductId = productId,
        ExpectedCurrentVersionId = CurrentVersionId,
        IntendedLocalVersionIdentity = "local-version-001",
        PayloadHash = PayloadHash,
        Main = new ProductImageStagedVariant
        {
            Bytes = 700000,
            Height = 1200,
            Identity = "main-opaque.jpg",
            Sha256 = new string('a', 64),
            Width = 1600
        },
        Thumb = new ProductImageStagedVariant
        {
            Bytes = 80000,
            Height = 288,
            Identity = "thumb-opaque.jpg",
            Sha256 = new string('b', 64),
            Width = 384
        }
    };

    private static string Seal(string operationId, string idempotencyKey) =>
        PayloadHash;

    private static async Task<long> InsertProductAsync(
        SqliteConnectionFactory factory,
        string barcode,
        string? remoteProductId)
    {
        using var connection = factory.Open();
        await connection.ExecuteAsync(@"
INSERT INTO products(barcode, name, unitPrice, remote_product_id, is_active)
VALUES(@barcode, @barcode, 100, @remoteProductId, 1);",
            new { barcode, remoteProductId });
        return await connection.ExecuteScalarAsync<long>(
            "SELECT id FROM products WHERE barcode = @barcode;",
            new { barcode });
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
                "win7pos-image-outbox-" + Guid.NewGuid().ToString("N"));
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
