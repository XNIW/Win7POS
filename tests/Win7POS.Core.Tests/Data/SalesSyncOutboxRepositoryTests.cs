using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class SalesSyncOutboxRepositoryTests
{
    private const long NowMs = 1_767_225_600_000L;

    [TestMethod]
    public async Task SalesSyncOutboxRepository_AndSaleFacade_EnqueueCallerRollbackLeavesNoOutboxMutation()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        await SaveShopAsync(directDb.Factory);
        await SaveShopAsync(facadeDb.Factory);
        var directSaleId = await SeedSaleWithLineAsync(
            directDb.Factory,
            "F4-ROLLBACK",
            clientSaleId: null);
        var facadeSaleId = await SeedSaleWithLineAsync(
            facadeDb.Factory,
            "F4-ROLLBACK",
            clientSaleId: null);
        var direct = new DirectOutboxSurface(directDb.Factory);
        var facade = new FacadeOutboxSurface(facadeDb.Factory);

        await EnqueueAndRollbackAsync(
            directDb.Factory,
            direct,
            directSaleId,
            "f4-rollback-client");
        await EnqueueAndRollbackAsync(
            facadeDb.Factory,
            facade,
            facadeSaleId,
            "  f4-rollback-client  ");

        await AssertNoEnqueueMutationAsync(directDb.Factory, directSaleId);
        await AssertNoEnqueueMutationAsync(facadeDb.Factory, facadeSaleId);
    }

    [TestMethod]
    public async Task SalesSyncOutboxRepository_AndSaleFacade_EnqueueReadsUncommittedCallerDataAndRollbackClearsAllRows()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        await SaveShopAsync(directDb.Factory);
        await SaveShopAsync(facadeDb.Factory);
        var direct = new DirectOutboxSurface(directDb.Factory);
        var facade = new FacadeOutboxSurface(facadeDb.Factory);

        await AssertEnqueueReadsUncommittedCallerDataAndRollsBackAsync(
            directDb.Factory,
            direct,
            "F4-UNCOMMITTED-DIRECT",
            "f4-uncommitted-direct");
        await AssertEnqueueReadsUncommittedCallerDataAndRollsBackAsync(
            facadeDb.Factory,
            facade,
            "F4-UNCOMMITTED-FACADE",
            "  f4-uncommitted-facade  ");
    }

    [TestMethod]
    public async Task SaleFacade_BlankClientSaleIdFallsBackAndPersistsCanonicalBinding()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory);
        var saleId = await SeedSaleWithLineAsync(
            db.Factory,
            "F4-BLANK-FALLBACK",
            clientSaleId: null);
        var facade = new FacadeOutboxSurface(db.Factory);

        await EnqueueAndCommitAsync(db.Factory, facade, saleId, "   ");

        var item = (await facade.GetPendingAsync(1, NowMs)).Single();
        AssertOutboxItemTargetsSale(item, saleId);
        var expectedClientSaleId = "win7pos-sale-" +
            saleId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.AreEqual(expectedClientSaleId, item.ClientSaleId);
        var state = await ReadStateAsync(db.Factory, saleId);
        Assert.AreEqual(expectedClientSaleId, state.ClientSaleId);
        Assert.AreEqual("pending", state.Status);
        Assert.AreEqual("pending", state.SaleSyncStatus);
        using var conn = db.Factory.Open();
        Assert.AreEqual(expectedClientSaleId, await conn.ExecuteScalarAsync<string>(
            "SELECT client_sale_id FROM sales WHERE id = @saleId;",
            new { saleId }));
    }

    [TestMethod]
    public async Task SalesSyncOutboxRepository_AndSaleFacade_KeepEnqueuePayloadHashImmutable()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        await SaveShopAsync(directDb.Factory);
        await SaveShopAsync(facadeDb.Factory);
        const string clientSaleId = "f4-immutable-client";
        var directSaleId = await SeedSaleWithLineAsync(
            directDb.Factory,
            "F4-IMMUTABLE",
            clientSaleId);
        var facadeSaleId = await SeedSaleWithLineAsync(
            facadeDb.Factory,
            "F4-IMMUTABLE",
            clientSaleId);
        var direct = new DirectOutboxSurface(directDb.Factory);
        var facade = new FacadeOutboxSurface(facadeDb.Factory);

        await EnqueueAndCommitAsync(directDb.Factory, direct, directSaleId, clientSaleId);
        await EnqueueAndCommitAsync(
            facadeDb.Factory,
            facade,
            facadeSaleId,
            "  " + clientSaleId + "  ");

        var directBefore = (await direct.GetPendingAsync(1, NowMs)).Single();
        var facadeBefore = (await facade.GetPendingAsync(1, NowMs)).Single();
        AssertOutboxItemTargetsSale(directBefore, directSaleId);
        AssertOutboxItemTargetsSale(facadeBefore, facadeSaleId);
        AssertOutboxItemsEqual(directBefore, facadeBefore, compareLeaseObservedAt: false);
        Assert.AreEqual(
            "F4-IMMUTABLE",
            PosSalesSyncRequestBuilder
                .DeserializeCanonical(directBefore.PayloadJson)
                .Sales
                .Single()
                .SaleNumber);

        await MutatePersistedSaleAsync(directDb.Factory, directSaleId);
        await MutatePersistedSaleAsync(facadeDb.Factory, facadeSaleId);

        var tamperedDirect = CopyForClaim(directBefore, payloadJson: directBefore.PayloadJson + " ");
        var tamperedFacade = CopyForClaim(facadeBefore, payloadJson: facadeBefore.PayloadJson + " ");
        Assert.IsFalse(await direct.PrepareAsync(tamperedDirect, NowMs, expectedAttemptCount: 0));
        Assert.IsFalse(await facade.PrepareAsync(tamperedFacade, NowMs, expectedAttemptCount: 0));
        Assert.IsTrue(await direct.PrepareAsync(directBefore, NowMs, expectedAttemptCount: 0));
        Assert.IsTrue(await facade.PrepareAsync(facadeBefore, NowMs, expectedAttemptCount: 0));

        var directAfter = await ReadStateAsync(directDb.Factory, directSaleId);
        var facadeAfter = await ReadStateAsync(facadeDb.Factory, facadeSaleId);
        AssertOutboxStatesEqual(directAfter, facadeAfter);
        Assert.AreEqual("in_progress", directAfter.Status);
        Assert.AreEqual(1, directAfter.AttemptCount);
        Assert.AreEqual(directBefore.PayloadJson, directAfter.PayloadJson);
        Assert.AreEqual(directBefore.PayloadHash, directAfter.PayloadHash);
        Assert.AreEqual(
            "F4-IMMUTABLE",
            PosSalesSyncRequestBuilder
                .DeserializeCanonical(directAfter.PayloadJson!)
                .Sales
                .Single()
                .SaleNumber);
    }

    [TestMethod]
    public async Task SalesSyncOutboxRepository_AndSaleFacade_BoundPendingTake()
    {
        using var db = TestDb.Create();
        var direct = new DirectOutboxSurface(db.Factory);
        var facade = new FacadeOutboxSurface(db.Factory);

        for (var index = 0; index < 51; index++)
        {
            await SeedOutboxAsync(
                db.Factory,
                "F4-CAP-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                status: "pending",
                nextRetryAt: 0,
                lastAttemptAt: null,
                clientBatchId: "batch-cap-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                payloadJson: "{\"cap\":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}",
                payloadHash: "hash-cap-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var directCapped = await direct.GetPendingAsync(999, NowMs);
        var facadeCapped = await facade.GetPendingAsync(999, NowMs);
        Assert.AreEqual(50, directCapped.Count);
        AssertOutboxSequencesEqual(directCapped, facadeCapped);

        var directMinimum = await direct.GetPendingAsync(0, NowMs);
        var facadeMinimum = await facade.GetPendingAsync(0, NowMs);
        Assert.AreEqual(1, directMinimum.Count);
        AssertOutboxSequencesEqual(directMinimum, facadeMinimum);
        Assert.AreEqual(directCapped[0].Id, directMinimum[0].Id);
    }

    [TestMethod]
    public async Task SalesSyncOutboxRepository_AndSaleFacade_RemoteProductIdsKeepMappedUnmappedAndDuplicateParity()
    {
        using var db = TestDb.Create();
        var direct = new DirectOutboxSurface(db.Factory);
        var facade = new FacadeOutboxSurface(db.Factory);
        var mappedFirst = await SeedProductAsync(db.Factory, "F4-REMOTE-FIRST", "remote-first");
        var unmapped = await SeedProductAsync(db.Factory, "F4-REMOTE-UNMAPPED", null);
        var whitespace = await SeedProductAsync(db.Factory, "F4-REMOTE-WHITESPACE", "   ");
        var mappedSecond = await SeedProductAsync(db.Factory, "F4-REMOTE-SECOND", "remote-second");
        var requested = new[]
        {
            mappedSecond,
            mappedFirst,
            unmapped,
            mappedFirst,
            whitespace,
            0L,
            -1L
        };

        var directIds = await direct.GetRemoteProductIdsAsync(requested);
        var facadeIds = await facade.GetRemoteProductIdsAsync(requested);

        Assert.AreEqual(2, directIds.Count);
        Assert.AreEqual("remote-first", directIds[mappedFirst]);
        Assert.AreEqual("remote-second", directIds[mappedSecond]);
        Assert.IsFalse(directIds.ContainsKey(unmapped));
        Assert.IsFalse(directIds.ContainsKey(whitespace));
        Assert.AreEqual(directIds.Count, facadeIds.Count);
        foreach (var pair in directIds)
        {
            Assert.IsTrue(facadeIds.TryGetValue(pair.Key, out var actual));
            Assert.AreEqual(pair.Value, actual);
        }
    }

    [TestMethod]
    public async Task SalesSyncOutboxRepository_AndSaleFacade_ExposeStaleLease()
    {
        using var db = TestDb.Create();
        var direct = new DirectOutboxSurface(db.Factory);
        var facade = new FacadeOutboxSurface(db.Factory);
        var pendingSaleId = await SeedOutboxAsync(
            db.Factory,
            "F4-LEASE-PENDING",
            status: "pending",
            nextRetryAt: 0,
            lastAttemptAt: null,
            clientBatchId: "batch-lease-pending",
            payloadJson: "{\"lease\":\"pending\"}",
            payloadHash: "hash-lease-pending");
        var staleSaleId = await SeedOutboxAsync(
            db.Factory,
            "F4-LEASE-STALE",
            status: "in_progress",
            nextRetryAt: 0,
            lastAttemptAt: NowMs - SaleRepository.SalesSyncInProgressLeaseMilliseconds - 1,
            clientBatchId: "batch-lease-stale",
            payloadJson: "{\"lease\":\"stale\"}",
            payloadHash: "hash-lease-stale");
        var freshObservedAt = NowMs - SaleRepository.SalesSyncInProgressLeaseMilliseconds + 1;
        var freshSaleId = await SeedOutboxAsync(
            db.Factory,
            "F4-LEASE-FRESH",
            status: "in_progress",
            nextRetryAt: 0,
            lastAttemptAt: freshObservedAt,
            clientBatchId: "batch-lease-fresh",
            payloadJson: "{\"lease\":\"fresh\"}",
            payloadHash: "hash-lease-fresh");

        var directPending = await direct.GetPendingAsync(50, NowMs);
        var facadePending = await facade.GetPendingAsync(50, NowMs);
        AssertOutboxSequencesEqual(directPending, facadePending);
        CollectionAssert.AreEquivalent(
            new[] { pendingSaleId, staleSaleId },
            directPending.Select(item => item.SaleId).ToArray());
        Assert.IsFalse(directPending.Any(item => item.SaleId == freshSaleId));
        Assert.IsTrue(directPending.Any(item => item.Status == "in_progress" && item.SaleId == staleSaleId));

        var directDrain = await direct.GetDrainStateAsync(NowMs);
        var facadeDrain = await facade.GetDrainStateAsync(NowMs);
        Assert.AreEqual(2L, directDrain.RemainingDue);
        Assert.AreEqual(freshObservedAt + SaleRepository.SalesSyncInProgressLeaseMilliseconds, directDrain.NextRetryAt);
        Assert.AreEqual(directDrain.RemainingDue, facadeDrain.RemainingDue);
        Assert.AreEqual(directDrain.NextRetryAt, facadeDrain.NextRetryAt);
    }

    [TestMethod]
    public async Task SalesSyncOutboxRepository_AndSaleFacade_ClaimNullSnapshotsWithCas()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        var directSaleId = await SeedOutboxAsync(
            directDb.Factory,
            "F4-NULL-SNAPSHOT",
            status: "pending",
            nextRetryAt: 0,
            lastAttemptAt: null,
            clientBatchId: null,
            payloadJson: null,
            payloadHash: null);
        var facadeSaleId = await SeedOutboxAsync(
            facadeDb.Factory,
            "F4-NULL-SNAPSHOT",
            status: "pending",
            nextRetryAt: 0,
            lastAttemptAt: null,
            clientBatchId: null,
            payloadJson: null,
            payloadHash: null);
        var direct = new DirectOutboxSurface(directDb.Factory);
        var facade = new FacadeOutboxSurface(facadeDb.Factory);
        var directItem = (await direct.GetPendingAsync(1, NowMs)).Single();
        var facadeItem = (await facade.GetPendingAsync(1, NowMs)).Single();

        AssertOutboxItemTargetsSale(directItem, directSaleId);
        AssertOutboxItemTargetsSale(facadeItem, facadeSaleId);
        AssertOutboxItemsEqual(directItem, facadeItem);
        Assert.IsNull(directItem.ClientBatchId);
        Assert.IsNull(directItem.PayloadJson);
        Assert.IsNull(directItem.PayloadHash);
        Assert.IsFalse(await direct.PrepareAsync(
            CopyForClaim(directItem, clientBatchId: "unexpected"),
            NowMs,
            expectedAttemptCount: 0));
        Assert.IsFalse(await facade.PrepareAsync(
            CopyForClaim(facadeItem, clientBatchId: "unexpected"),
            NowMs,
            expectedAttemptCount: 0));
        Assert.IsTrue(await direct.PrepareAsync(directItem, NowMs, expectedAttemptCount: 0));
        Assert.IsTrue(await facade.PrepareAsync(facadeItem, NowMs, expectedAttemptCount: 0));
        Assert.IsFalse(await direct.PrepareAsync(directItem, NowMs, expectedAttemptCount: 0));
        Assert.IsFalse(await facade.PrepareAsync(facadeItem, NowMs, expectedAttemptCount: 0));

        var directState = await ReadStateAsync(directDb.Factory, directSaleId);
        var facadeState = await ReadStateAsync(facadeDb.Factory, facadeSaleId);
        AssertOutboxStatesEqual(directState, facadeState);
        Assert.AreEqual("in_progress", directState.Status);
        Assert.AreEqual(1, directState.AttemptCount);
        Assert.IsNull(directState.ClientBatchId);
        Assert.IsNull(directState.PayloadJson);
        Assert.IsNull(directState.PayloadHash);
    }

    [TestMethod]
    public async Task SalesSyncOutboxRepository_AndSaleFacade_OriginBlockCasKeepsOutboxAndSaleParity()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        var directSaleId = await SeedOutboxAsync(
            directDb.Factory,
            "F4-ORIGIN-BLOCK",
            status: "pending",
            nextRetryAt: 0,
            lastAttemptAt: null,
            clientBatchId: "batch-origin-block",
            payloadJson: "{\"origin\":true}",
            payloadHash: "hash-origin-block");
        var facadeSaleId = await SeedOutboxAsync(
            facadeDb.Factory,
            "F4-ORIGIN-BLOCK",
            status: "pending",
            nextRetryAt: 0,
            lastAttemptAt: null,
            clientBatchId: "batch-origin-block",
            payloadJson: "{\"origin\":true}",
            payloadHash: "hash-origin-block");
        var direct = new DirectOutboxSurface(directDb.Factory);
        var facade = new FacadeOutboxSurface(facadeDb.Factory);
        var directItem = (await direct.GetPendingAsync(1, NowMs)).Single();
        var facadeItem = (await facade.GetPendingAsync(1, NowMs)).Single();

        AssertOutboxItemTargetsSale(directItem, directSaleId);
        AssertOutboxItemTargetsSale(facadeItem, facadeSaleId);
        Assert.IsFalse(await direct.MarkOriginBlockedAsync(
            directSaleId,
            directItem.Id,
            "f4-origin-blocked",
            NowMs,
            directItem.Status,
            directItem.AttemptCount,
            directItem.LeaseObservedAt));
        Assert.IsFalse(await facade.MarkOriginBlockedAsync(
            facadeSaleId,
            facadeItem.Id,
            "f4-origin-blocked",
            NowMs,
            facadeItem.Status,
            facadeItem.AttemptCount,
            facadeItem.LeaseObservedAt));
        Assert.IsTrue(await direct.MarkOriginBlockedAsync(
            directItem.Id,
            directSaleId,
            "f4-origin-blocked",
            NowMs,
            directItem.Status,
            directItem.AttemptCount,
            directItem.LeaseObservedAt));
        Assert.IsTrue(await facade.MarkOriginBlockedAsync(
            facadeItem.Id,
            facadeSaleId,
            "f4-origin-blocked",
            NowMs,
            facadeItem.Status,
            facadeItem.AttemptCount,
            facadeItem.LeaseObservedAt));
        Assert.IsFalse(await direct.MarkOriginBlockedAsync(
            directItem.Id,
            directSaleId,
            "f4-origin-blocked",
            NowMs,
            directItem.Status,
            directItem.AttemptCount,
            directItem.LeaseObservedAt));
        Assert.IsFalse(await facade.MarkOriginBlockedAsync(
            facadeItem.Id,
            facadeSaleId,
            "f4-origin-blocked",
            NowMs,
            facadeItem.Status,
            facadeItem.AttemptCount,
            facadeItem.LeaseObservedAt));

        var directState = await ReadStateAsync(directDb.Factory, directSaleId);
        var facadeState = await ReadStateAsync(facadeDb.Factory, facadeSaleId);
        AssertOutboxStatesEqual(directState, facadeState);
        Assert.AreEqual("failed_blocked", directState.Status);
        Assert.AreEqual("blocked", directState.SaleSyncStatus);
        Assert.AreEqual("f4-origin-blocked", directState.LastErrorCode);
    }

    [TestMethod]
    public async Task SalesSyncOutboxRepository_AndSaleFacade_KeepCasTransitionParity()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        var directSaleId = await SeedOutboxAsync(
            directDb.Factory,
            "F4-CAS",
            status: "pending",
            nextRetryAt: 0,
            lastAttemptAt: null,
            clientBatchId: "batch-cas",
            payloadJson: "{\"cas\":true}",
            payloadHash: "hash-cas");
        var facadeSaleId = await SeedOutboxAsync(
            facadeDb.Factory,
            "F4-CAS",
            status: "pending",
            nextRetryAt: 0,
            lastAttemptAt: null,
            clientBatchId: "batch-cas",
            payloadJson: "{\"cas\":true}",
            payloadHash: "hash-cas");
        var direct = new DirectOutboxSurface(directDb.Factory);
        var facade = new FacadeOutboxSurface(facadeDb.Factory);

        await DriveCasSequenceAsync(direct, directSaleId);
        await DriveCasSequenceAsync(facade, facadeSaleId);

        var directState = await ReadStateAsync(directDb.Factory, directSaleId);
        var facadeState = await ReadStateAsync(facadeDb.Factory, facadeSaleId);
        AssertOutboxStatesEqual(directState, facadeState);
        Assert.AreEqual("failed_blocked", directState.Status);
        Assert.AreEqual(2, directState.AttemptCount);
        Assert.AreEqual("f4-blocked", directState.LastErrorCode);
        Assert.AreEqual("blocked", directState.SaleSyncStatus);
        Assert.IsTrue(await direct.HasUnresolvedAsync());
        Assert.IsTrue(await facade.HasUnresolvedAsync());
        var directSummary = await direct.GetSummaryAsync();
        var facadeSummary = await facade.GetSummaryAsync();
        Assert.AreEqual(1L, directSummary.Blocked);
        Assert.AreEqual(directSummary.Acked, facadeSummary.Acked);
        Assert.AreEqual(directSummary.Blocked, facadeSummary.Blocked);
        Assert.AreEqual(directSummary.InProgress, facadeSummary.InProgress);
        Assert.AreEqual(directSummary.Pending, facadeSummary.Pending);
        Assert.AreEqual(directSummary.Retry, facadeSummary.Retry);
    }

    [TestMethod]
    public async Task SalesSyncOutboxRepository_AndSaleFacade_FenceGenerationScopedAck()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        var activeGeneration = CreateGeneration("f4-active-generation");
        var staleGeneration = CreateGeneration("f4-stale-generation");
        await new OnlineSyncGenerationRepository(directDb.Factory)
            .ActivateAndRecoverAsync(activeGeneration, NowMs);
        await new OnlineSyncGenerationRepository(facadeDb.Factory)
            .ActivateAndRecoverAsync(activeGeneration, NowMs);
        var directSaleId = await SeedOutboxAsync(
            directDb.Factory,
            "F4-FENCE",
            status: "pending",
            nextRetryAt: 0,
            lastAttemptAt: null,
            clientBatchId: "batch-fence",
            payloadJson: "{\"fence\":true}",
            payloadHash: "hash-fence");
        var facadeSaleId = await SeedOutboxAsync(
            facadeDb.Factory,
            "F4-FENCE",
            status: "pending",
            nextRetryAt: 0,
            lastAttemptAt: null,
            clientBatchId: "batch-fence",
            payloadJson: "{\"fence\":true}",
            payloadHash: "hash-fence");
        var direct = new DirectOutboxSurface(directDb.Factory);
        var facade = new FacadeOutboxSurface(facadeDb.Factory);
        var directItem = (await direct.GetPendingAsync(1, NowMs)).Single();
        var facadeItem = (await facade.GetPendingAsync(1, NowMs)).Single();
        AssertOutboxItemTargetsSale(directItem, directSaleId);
        AssertOutboxItemTargetsSale(facadeItem, facadeSaleId);
        var directClaim = OnlineSyncAttemptFence.CreateClaimToken();
        var facadeClaim = OnlineSyncAttemptFence.CreateClaimToken();

        Assert.IsFalse(await direct.PrepareAsync(
            directItem,
            NowMs,
            expectedAttemptCount: 0,
            staleGeneration,
            directClaim));
        Assert.IsFalse(await facade.PrepareAsync(
            facadeItem,
            NowMs,
            expectedAttemptCount: 0,
            staleGeneration,
            facadeClaim));
        Assert.IsTrue(await direct.PrepareAsync(
            directItem,
            NowMs,
            expectedAttemptCount: 0,
            activeGeneration,
            directClaim));
        Assert.IsTrue(await facade.PrepareAsync(
            facadeItem,
            NowMs,
            expectedAttemptCount: 0,
            activeGeneration,
            facadeClaim));

        Assert.IsFalse(await direct.MarkAckedAsync(
            directItem.Id,
            directSaleId,
            "server-batch",
            "server-sale",
            NowMs + 1,
            expectedAttemptCount: 1,
            new OnlineSyncAttemptFence(activeGeneration, OnlineSyncAttemptFence.CreateClaimToken(), 1)));
        Assert.IsFalse(await facade.MarkAckedAsync(
            facadeItem.Id,
            facadeSaleId,
            "server-batch",
            "server-sale",
            NowMs + 1,
            expectedAttemptCount: 1,
            new OnlineSyncAttemptFence(activeGeneration, OnlineSyncAttemptFence.CreateClaimToken(), 1)));
        Assert.IsTrue(await direct.MarkAckedAsync(
            directItem.Id,
            directSaleId,
            "server-batch",
            "server-sale",
            NowMs + 2,
            expectedAttemptCount: 1,
            new OnlineSyncAttemptFence(activeGeneration, directClaim, 1)));
        Assert.IsTrue(await facade.MarkAckedAsync(
            facadeItem.Id,
            facadeSaleId,
            "server-batch",
            "server-sale",
            NowMs + 2,
            expectedAttemptCount: 1,
            new OnlineSyncAttemptFence(activeGeneration, facadeClaim, 1)));

        var directState = await ReadStateAsync(directDb.Factory, directSaleId);
        var facadeState = await ReadStateAsync(facadeDb.Factory, facadeSaleId);
        AssertOutboxStatesEqual(directState, facadeState);
        Assert.AreEqual("acked", directState.Status);
        Assert.AreEqual("acked", directState.SaleSyncStatus);
        Assert.IsNull(directState.ClaimGenerationId);
        Assert.IsNull(directState.ClaimToken);
    }

    [TestMethod]
    public async Task SalesSyncOutboxRepository_AndSaleFacade_FenceGenerationScopedNonAckTransitions()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        var activeGeneration = CreateGeneration("f4-active-retry-generation");
        await new OnlineSyncGenerationRepository(directDb.Factory)
            .ActivateAndRecoverAsync(activeGeneration, NowMs);
        await new OnlineSyncGenerationRepository(facadeDb.Factory)
            .ActivateAndRecoverAsync(activeGeneration, NowMs);
        var directSaleId = await SeedOutboxAsync(
            directDb.Factory,
            "F4-FENCE-RETRY",
            status: "pending",
            nextRetryAt: 0,
            lastAttemptAt: null,
            clientBatchId: "batch-fence-retry",
            payloadJson: "{\"fenceRetry\":true}",
            payloadHash: "hash-fence-retry");
        var facadeSaleId = await SeedOutboxAsync(
            facadeDb.Factory,
            "F4-FENCE-RETRY",
            status: "pending",
            nextRetryAt: 0,
            lastAttemptAt: null,
            clientBatchId: "batch-fence-retry",
            payloadJson: "{\"fenceRetry\":true}",
            payloadHash: "hash-fence-retry");
        var direct = new DirectOutboxSurface(directDb.Factory);
        var facade = new FacadeOutboxSurface(facadeDb.Factory);

        await DriveFencedRetryAndReleaseAsync(
            direct,
            directSaleId,
            activeGeneration,
            OnlineSyncAttemptFence.CreateClaimToken(),
            OnlineSyncAttemptFence.CreateClaimToken());
        await DriveFencedRetryAndReleaseAsync(
            facade,
            facadeSaleId,
            activeGeneration,
            OnlineSyncAttemptFence.CreateClaimToken(),
            OnlineSyncAttemptFence.CreateClaimToken());

        var directState = await ReadStateAsync(directDb.Factory, directSaleId);
        var facadeState = await ReadStateAsync(facadeDb.Factory, facadeSaleId);
        AssertOutboxStatesEqual(directState, facadeState);
        Assert.AreEqual("failed_blocked", directState.Status);
        Assert.AreEqual(2, directState.AttemptCount);
        Assert.AreEqual("f4-generation-blocked", directState.LastErrorCode);
        Assert.AreEqual("blocked", directState.SaleSyncStatus);
        Assert.IsNull(directState.ClaimGenerationId);
        Assert.IsNull(directState.ClaimToken);
    }

    private static async Task DriveCasSequenceAsync(ISalesOutboxSurface surface, long saleId)
    {
        var initial = (await surface.GetPendingAsync(1, NowMs)).Single();
        AssertOutboxItemTargetsSale(initial, saleId);
        Assert.IsTrue(await surface.PrepareAsync(initial, NowMs, expectedAttemptCount: 0));
        Assert.IsFalse(await surface.PrepareAsync(initial, NowMs, expectedAttemptCount: 0));
        Assert.IsTrue(await surface.MarkRetryAsync(
            initial.Id,
            saleId,
            "f4-retry",
            NowMs + 10,
            NowMs + 1,
            expectedAttemptCount: 1));
        Assert.IsFalse(await surface.MarkRetryAsync(
            initial.Id,
            saleId,
            "f4-retry",
            NowMs + 10,
            NowMs + 1,
            expectedAttemptCount: 1));

        var retry = (await surface.GetPendingAsync(1, NowMs + 10)).Single();
        Assert.AreEqual("retry", retry.Status);
        Assert.AreEqual(1, retry.AttemptCount);
        Assert.IsTrue(await surface.PrepareAsync(retry, NowMs + 10, expectedAttemptCount: 1));
        Assert.IsTrue(await surface.DeferDependencyAsync(
            retry.Id,
            saleId,
            "f4-dependency",
            NowMs + 20,
            NowMs + 11,
            expectedAttemptCount: 2));

        var deferred = (await surface.GetPendingAsync(1, NowMs + 20)).Single();
        Assert.AreEqual("retry", deferred.Status);
        Assert.AreEqual(1, deferred.AttemptCount);
        Assert.IsTrue(await surface.PrepareAsync(deferred, NowMs + 20, expectedAttemptCount: 1));
        Assert.IsTrue(await surface.ReleaseAttemptAsync(
            deferred.Id,
            saleId,
            "f4-cancelled",
            NowMs + 30,
            NowMs + 21,
            expectedAttemptCount: 2));

        var released = (await surface.GetPendingAsync(1, NowMs + 30)).Single();
        Assert.AreEqual("retry", released.Status);
        Assert.AreEqual(1, released.AttemptCount);
        Assert.IsTrue(await surface.PrepareAsync(released, NowMs + 30, expectedAttemptCount: 1));
        Assert.IsTrue(await surface.MarkBlockedAsync(
            released.Id,
            saleId,
            "f4-blocked",
            NowMs + 31,
            expectedAttemptCount: 2));
        Assert.IsFalse(await surface.MarkBlockedAsync(
            released.Id,
            saleId,
            "f4-blocked",
            NowMs + 31,
            expectedAttemptCount: 2));
    }

    private static async Task DriveFencedRetryAndReleaseAsync(
        ISalesOutboxSurface surface,
        long saleId,
        OnlineSyncGeneration activeGeneration,
        string retryClaim,
        string releaseClaim)
    {
        var pending = (await surface.GetPendingAsync(1, NowMs)).Single();
        AssertOutboxItemTargetsSale(pending, saleId);
        Assert.IsTrue(await surface.PrepareAsync(
            pending,
            NowMs,
            expectedAttemptCount: 0,
            activeGeneration,
            retryClaim));
        Assert.IsFalse(await surface.MarkRetryAsync(
            pending.Id,
            saleId,
            "f4-generation-retry",
            NowMs + 10,
            NowMs + 1,
            expectedAttemptCount: 1,
            new OnlineSyncAttemptFence(
                activeGeneration,
                OnlineSyncAttemptFence.CreateClaimToken(),
                1)));
        Assert.IsTrue(await surface.MarkRetryAsync(
            pending.Id,
            saleId,
            "f4-generation-retry",
            NowMs + 10,
            NowMs + 1,
            expectedAttemptCount: 1,
            new OnlineSyncAttemptFence(activeGeneration, retryClaim, 1)));

        var retry = (await surface.GetPendingAsync(1, NowMs + 10)).Single();
        AssertOutboxItemTargetsSale(retry, saleId);
        Assert.AreEqual("retry", retry.Status);
        Assert.AreEqual(1, retry.AttemptCount);
        Assert.IsTrue(await surface.PrepareAsync(
            retry,
            NowMs + 10,
            expectedAttemptCount: 1,
            activeGeneration,
            releaseClaim));
        Assert.IsFalse(await surface.ReleaseAttemptAsync(
            retry.Id,
            saleId,
            "f4-generation-release",
            NowMs + 20,
            NowMs + 11,
            expectedAttemptCount: 2,
            new OnlineSyncAttemptFence(
                activeGeneration,
                OnlineSyncAttemptFence.CreateClaimToken(),
                2)));
        Assert.IsTrue(await surface.ReleaseAttemptAsync(
            retry.Id,
            saleId,
            "f4-generation-release",
            NowMs + 20,
            NowMs + 11,
            expectedAttemptCount: 2,
            new OnlineSyncAttemptFence(activeGeneration, releaseClaim, 2)));

        var released = (await surface.GetPendingAsync(1, NowMs + 20)).Single();
        AssertOutboxItemTargetsSale(released, saleId);
        Assert.AreEqual("retry", released.Status);
        Assert.AreEqual(1, released.AttemptCount);
        var blockClaim = OnlineSyncAttemptFence.CreateClaimToken();
        Assert.IsTrue(await surface.PrepareAsync(
            released,
            NowMs + 20,
            expectedAttemptCount: 1,
            activeGeneration,
            blockClaim));
        Assert.IsFalse(await surface.MarkBlockedAsync(
            released.Id,
            saleId,
            "f4-generation-blocked",
            NowMs + 21,
            expectedAttemptCount: 2,
            new OnlineSyncAttemptFence(
                activeGeneration,
                OnlineSyncAttemptFence.CreateClaimToken(),
                2)));
        Assert.IsTrue(await surface.MarkBlockedAsync(
            released.Id,
            saleId,
            "f4-generation-blocked",
            NowMs + 21,
            expectedAttemptCount: 2,
            new OnlineSyncAttemptFence(activeGeneration, blockClaim, 2)));
    }

    private static async Task EnqueueAndRollbackAsync(
        SqliteConnectionFactory factory,
        ISalesOutboxSurface surface,
        long saleId,
        string clientSaleId)
    {
        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        await surface.EnqueueAsync(conn, tx, saleId, clientSaleId);
        Assert.AreEqual(1L, await ScalarAsync(
            conn,
            "SELECT COUNT(1) FROM sales_sync_outbox WHERE sale_id = @saleId;",
            new { saleId },
            tx));
        Assert.AreEqual(clientSaleId.Trim(), await conn.ExecuteScalarAsync<string>(
            "SELECT client_sale_id FROM sales WHERE id = @saleId;",
            new { saleId },
            tx));
        tx.Rollback();
    }

    private static async Task EnqueueAndCommitAsync(
        SqliteConnectionFactory factory,
        ISalesOutboxSurface surface,
        long saleId,
        string clientSaleId)
    {
        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        await surface.EnqueueAsync(conn, tx, saleId, clientSaleId);
        tx.Commit();
    }

    private static async Task AssertEnqueueReadsUncommittedCallerDataAndRollsBackAsync(
        SqliteConnectionFactory factory,
        ISalesOutboxSurface surface,
        string code,
        string suppliedClientSaleId)
    {
        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        var saleId = await InsertUncommittedSaleWithLineAsync(conn, tx, code);

        await surface.EnqueueAsync(conn, tx, saleId, suppliedClientSaleId);

        var payloadJson = await conn.ExecuteScalarAsync<string>(@"
SELECT payload_json
FROM sales_sync_outbox
WHERE sale_id = @saleId;",
            new { saleId },
            tx);
        var request = PosSalesSyncRequestBuilder.DeserializeCanonical(payloadJson);
        var sale = request.Sales.Single();
        Assert.AreEqual(code, sale.SaleNumber);
        Assert.AreEqual(suppliedClientSaleId.Trim(), sale.ClientSaleId);
        Assert.AreEqual(code + " uncommitted line", sale.Lines.Single().ProductName);
        Assert.AreEqual(code + "-UNCOMMITTED-ITEM", sale.Lines.Single().Barcode);
        Assert.AreEqual(1L, await ScalarAsync(
            conn,
            "SELECT COUNT(1) FROM sales_sync_outbox WHERE sale_id = @saleId;",
            new { saleId },
            tx));

        tx.Rollback();

        Assert.AreEqual(0L, await ScalarAsync(
            conn,
            "SELECT COUNT(1) FROM sales WHERE id = @saleId;",
            new { saleId }));
        Assert.AreEqual(0L, await ScalarAsync(
            conn,
            "SELECT COUNT(1) FROM sale_lines WHERE saleId = @saleId;",
            new { saleId }));
        Assert.AreEqual(0L, await ScalarAsync(
            conn,
            "SELECT COUNT(1) FROM sales_sync_outbox WHERE sale_id = @saleId;",
            new { saleId }));
    }

    private static async Task AssertNoEnqueueMutationAsync(
        SqliteConnectionFactory factory,
        long saleId)
    {
        using var conn = factory.Open();
        Assert.AreEqual(0L, await ScalarAsync(
            conn,
            "SELECT COUNT(1) FROM sales_sync_outbox WHERE sale_id = @saleId;",
            new { saleId }));
        Assert.IsNull(await conn.ExecuteScalarAsync<string?>(
            "SELECT client_sale_id FROM sales WHERE id = @saleId;",
            new { saleId }));
        Assert.AreEqual("pending", await conn.ExecuteScalarAsync<string>(
            "SELECT sync_status FROM sales WHERE id = @saleId;",
            new { saleId }));
    }

    private static async Task<long> SeedSaleWithLineAsync(
        SqliteConnectionFactory factory,
        string code,
        string? clientSaleId)
    {
        using var conn = factory.Open();
        var saleId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(client_sale_id, code, createdAt, kind, total, paidCash, paidCard, change)
VALUES(@clientSaleId, @code, @createdAt, 0, 100, 100, 0, 0);
SELECT last_insert_rowid();",
            new { clientSaleId, code, createdAt = NowMs - 100 });
        await conn.ExecuteAsync(@"
INSERT INTO sale_lines(saleId, productId, barcode, name, quantity, unitPrice, lineTotal)
VALUES(@saleId, NULL, @barcode, @name, 1, 100, 100);",
            new { saleId, barcode = code + "-ITEM", name = code + " line" });
        await ReserveAcknowledgedOutboxIdentityAsync(conn, code + "-reserve");
        return saleId;
    }

    private static async Task<long> InsertUncommittedSaleWithLineAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string code)
    {
        var saleId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(client_sale_id, code, createdAt, kind, total, paidCash, paidCard, change)
VALUES(NULL, @code, @createdAt, 0, 100, 100, 0, 0);
SELECT last_insert_rowid();",
            new { code, createdAt = NowMs - 100 }, tx);
        await conn.ExecuteAsync(@"
INSERT INTO sale_lines(saleId, productId, barcode, name, quantity, unitPrice, lineTotal)
VALUES(@saleId, NULL, @barcode, @name, 1, 100, 100);",
            new
            {
                saleId,
                barcode = code + "-UNCOMMITTED-ITEM",
                name = code + " uncommitted line"
            },
            tx);
        return saleId;
    }

    private static async Task<long> SeedOutboxAsync(
        SqliteConnectionFactory factory,
        string key,
        string status,
        long nextRetryAt,
        long? lastAttemptAt,
        string? clientBatchId,
        string? payloadJson,
        string? payloadHash)
    {
        using var conn = factory.Open();
        var clientSaleId = "f4-client-" + key;
        var saleId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(client_sale_id, code, createdAt, kind, total, paidCash, paidCard, change)
VALUES(@clientSaleId, @code, @createdAt, 0, 100, 100, 0, 0);
SELECT last_insert_rowid();",
            new { clientSaleId, code = key, createdAt = NowMs - 100 });
        await conn.ExecuteAsync(@"
INSERT INTO sale_lines(saleId, productId, barcode, name, quantity, unitPrice, lineTotal)
VALUES(@saleId, NULL, @barcode, @name, 1, 100, 100);",
            new { saleId, barcode = key + "-ITEM", name = key + " line" });
        await ReserveAcknowledgedOutboxIdentityAsync(conn, key + "-reserve");
        await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales_sync_outbox(
  sale_id, client_sale_id, client_batch_id, idempotency_key, schema_version, operation_type,
  origin_shop_id, origin_shop_code, payload_json, payload_hash, status, attempt_count,
  next_retry_at, last_attempt_at, created_at, updated_at)
VALUES(
  @saleId, @clientSaleId, @clientBatchId, @idempotencyKey, 'pos-sales-ledger-v2', 'sale',
  'shop-f4', 'SHOP-F4', @payloadJson, @payloadHash, @status, 0,
  @nextRetryAt, @lastAttemptAt, @createdAt, @updatedAt);
SELECT last_insert_rowid();",
            new
            {
                saleId,
                clientSaleId,
                clientBatchId,
                idempotencyKey = BuildOpaqueIdentifier("test-outbox-idempotency", key),
                payloadJson,
                payloadHash,
                status,
                nextRetryAt,
                lastAttemptAt,
                createdAt = NowMs - 100,
                updatedAt = lastAttemptAt ?? NowMs - 100
            });
        return saleId;
    }

    private static string BuildOpaqueIdentifier(string scope, string seed)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Concat(scope, ":", seed));
        return Convert.ToBase64String(sha256.ComputeHash(bytes));
    }

    private static async Task ReserveAcknowledgedOutboxIdentityAsync(
        SqliteConnection conn,
        string key)
    {
        var reserveClientSaleId = "f4-reserve-client-" + key;
        var reserveSaleId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(client_sale_id, code, createdAt, kind, total, paidCash, paidCard, change, sync_status)
VALUES(@reserveClientSaleId, @reserveCode, @createdAt, 0, 0, 0, 0, 0, 'acked');
SELECT last_insert_rowid();",
            new
            {
                reserveClientSaleId,
                reserveCode = "F4-RESERVE-" + key,
                createdAt = NowMs - 101
            });
        await conn.ExecuteAsync(@"
INSERT INTO sales_sync_outbox(
  sale_id, client_sale_id, client_batch_id, idempotency_key, schema_version, operation_type,
  origin_shop_id, origin_shop_code, payload_json, payload_hash, status, attempt_count,
  next_retry_at, created_at, updated_at)
VALUES(
  @reserveSaleId, @reserveClientSaleId, @reserveClientBatchId, @reserveIdempotencyKey,
  'pos-sales-ledger-v2', 'sale', 'shop-f4', 'SHOP-F4', '{}', @reservePayloadHash,
  'acked', 0, 0, @createdAt, @createdAt);",
            new
            {
                reserveSaleId,
                reserveClientSaleId,
                reserveClientBatchId = "f4-reserve-batch-" + key,
                reserveIdempotencyKey = BuildOpaqueIdentifier("test-outbox-reserved-idempotency", key),
                reservePayloadHash = "f4-reserve-hash-" + key,
                createdAt = NowMs - 101
            });
    }

    private static async Task<long> SeedProductAsync(
        SqliteConnectionFactory factory,
        string barcode,
        string? remoteProductId)
    {
        using var conn = factory.Open();
        return await conn.ExecuteScalarAsync<long>(@"
INSERT INTO products(barcode, name, unitPrice, remote_product_id)
VALUES(@barcode, @name, 100, @remoteProductId);
SELECT last_insert_rowid();",
            new
            {
                barcode,
                name = "F4 product " + barcode,
                remoteProductId
            });
    }

    private static async Task MutatePersistedSaleAsync(
        SqliteConnectionFactory factory,
        long saleId)
    {
        using var conn = factory.Open();
        await conn.ExecuteAsync(@"
UPDATE sales SET code = 'F4-MUTATED' WHERE id = @saleId;
UPDATE sale_lines SET name = 'F4 mutated line' WHERE saleId = @saleId;",
            new { saleId });
    }

    private static async Task<OutboxState> ReadStateAsync(
        SqliteConnectionFactory factory,
        long saleId)
    {
        using var conn = factory.Open();
        return await conn.QuerySingleAsync<OutboxState>(@"
SELECT
  outbox.client_sale_id AS ClientSaleId,
  outbox.client_batch_id AS ClientBatchId,
  outbox.idempotency_key AS IdempotencyKey,
  outbox.payload_json AS PayloadJson,
  outbox.payload_hash AS PayloadHash,
  outbox.status AS Status,
  outbox.attempt_count AS AttemptCount,
  outbox.next_retry_at AS NextRetryAt,
  outbox.last_error_code AS LastErrorCode,
  outbox.claim_generation_id AS ClaimGenerationId,
  outbox.claim_token AS ClaimToken,
  sale.sync_status AS SaleSyncStatus
FROM sales_sync_outbox outbox
INNER JOIN sales sale ON sale.id = outbox.sale_id
WHERE outbox.sale_id = @saleId;",
            new { saleId });
    }

    private static SalesSyncOutboxItem CopyForClaim(
        SalesSyncOutboxItem source,
        string? clientBatchId = null,
        string? payloadJson = null)
    {
        return new SalesSyncOutboxItem
        {
            Id = source.Id,
            ClientBatchId = clientBatchId ?? source.ClientBatchId,
            PayloadHash = source.PayloadHash,
            PayloadJson = payloadJson ?? source.PayloadJson,
            Status = source.Status,
            AttemptCount = source.AttemptCount,
            NextRetryAt = source.NextRetryAt,
            LeaseObservedAt = source.LeaseObservedAt
        };
    }

    private static async Task<long> ScalarAsync(
        SqliteConnection conn,
        string sql,
        object? parameters = null,
        SqliteTransaction? tx = null)
    {
        var value = await conn.ExecuteScalarAsync(sql, parameters, tx);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static OnlineSyncGeneration CreateGeneration(string generationId)
    {
        return new OnlineSyncGeneration(
            generationId,
            "f4-session",
            "f4-device",
            "shop-f4",
            "SHOP-F4");
    }

    private static Task SaveShopAsync(SqliteConnectionFactory factory)
    {
        return new ShopOfficialSnapshotRepository(factory).SaveAsync(new OfficialShopSnapshot
        {
            ShopCode = "SHOP-F4",
            ShopId = "shop-f4",
            ShopName = "F4 shop",
            Source = "test"
        });
    }

    private static void AssertOutboxSequencesEqual(
        IReadOnlyList<SalesSyncOutboxItem> expected,
        IReadOnlyList<SalesSyncOutboxItem> actual)
    {
        Assert.AreEqual(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            AssertOutboxItemsEqual(expected[index], actual[index]);
        }
    }

    private static void AssertOutboxItemTargetsSale(
        SalesSyncOutboxItem item,
        long saleId)
    {
        Assert.AreEqual(saleId, item.SaleId);
        Assert.AreNotEqual(saleId, item.Id);
    }

    private static void AssertOutboxItemsEqual(
        SalesSyncOutboxItem expected,
        SalesSyncOutboxItem actual,
        bool compareLeaseObservedAt = true)
    {
        Assert.AreEqual(expected.Id, actual.Id);
        Assert.AreEqual(expected.SaleId, actual.SaleId);
        Assert.AreEqual(expected.ClientSaleId, actual.ClientSaleId);
        Assert.AreEqual(expected.ClientBatchId, actual.ClientBatchId);
        Assert.AreEqual(expected.IdempotencyKey, actual.IdempotencyKey);
        Assert.AreEqual(expected.SchemaVersion, actual.SchemaVersion);
        Assert.AreEqual(expected.OperationType, actual.OperationType);
        Assert.AreEqual(expected.OriginShopId, actual.OriginShopId);
        Assert.AreEqual(expected.OriginShopCode, actual.OriginShopCode);
        Assert.AreEqual(expected.PayloadJson, actual.PayloadJson);
        Assert.AreEqual(expected.PayloadHash, actual.PayloadHash);
        Assert.AreEqual(expected.Status, actual.Status);
        Assert.AreEqual(expected.AttemptCount, actual.AttemptCount);
        if (compareLeaseObservedAt)
        {
            Assert.AreEqual(expected.LeaseObservedAt, actual.LeaseObservedAt);
        }
        Assert.AreEqual(expected.NextRetryAt, actual.NextRetryAt);
        Assert.AreEqual(expected.LastErrorCode, actual.LastErrorCode);
    }

    private static void AssertOutboxStatesEqual(OutboxState expected, OutboxState actual)
    {
        Assert.AreEqual(expected.ClientSaleId, actual.ClientSaleId);
        Assert.AreEqual(expected.ClientBatchId, actual.ClientBatchId);
        Assert.AreEqual(expected.IdempotencyKey, actual.IdempotencyKey);
        Assert.AreEqual(expected.PayloadJson, actual.PayloadJson);
        Assert.AreEqual(expected.PayloadHash, actual.PayloadHash);
        Assert.AreEqual(expected.Status, actual.Status);
        Assert.AreEqual(expected.AttemptCount, actual.AttemptCount);
        Assert.AreEqual(expected.NextRetryAt, actual.NextRetryAt);
        Assert.AreEqual(expected.LastErrorCode, actual.LastErrorCode);
        Assert.AreEqual(expected.ClaimGenerationId, actual.ClaimGenerationId);
        Assert.AreEqual(expected.ClaimToken, actual.ClaimToken);
        Assert.AreEqual(expected.SaleSyncStatus, actual.SaleSyncStatus);
    }

    private interface ISalesOutboxSurface
    {
        Task EnqueueAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long saleId,
            string clientSaleId);

        Task<IReadOnlyList<SalesSyncOutboxItem>> GetPendingAsync(int take, long nowMs);
        Task<SalesSyncOutboxSummary> GetSummaryAsync();
        Task<OutboxDrainState> GetDrainStateAsync(long nowMs);
        Task<bool> HasUnresolvedAsync();
        Task<IReadOnlyDictionary<long, string>> GetRemoteProductIdsAsync(
            IEnumerable<long> productIds);

        Task<bool> PrepareAsync(
            SalesSyncOutboxItem item,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncGeneration? generation = null,
            string? claimToken = null);

        Task<bool> MarkAckedAsync(
            long outboxId,
            long saleId,
            string serverBatchId,
            string serverSaleId,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence? fence = null);

        Task<bool> MarkRetryAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence? fence = null);

        Task<bool> DeferDependencyAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence? fence = null);

        Task<bool> ReleaseAttemptAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence? fence = null);

        Task<bool> MarkBlockedAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence? fence = null);

        Task<bool> MarkOriginBlockedAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nowMs,
            string expectedStatus,
            int expectedAttemptCount,
            long expectedLeaseObservedAt);
    }

    private sealed class DirectOutboxSurface : ISalesOutboxSurface
    {
        private readonly SalesSyncOutboxRepository _repository;

        public DirectOutboxSurface(SqliteConnectionFactory factory)
        {
            _repository = new SalesSyncOutboxRepository(
                factory,
                SaleRepository.SalesSyncInProgressLeaseMilliseconds);
        }

        public Task EnqueueAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long saleId,
            string clientSaleId)
        {
            return _repository.EnqueueAsync(conn, tx, saleId, clientSaleId);
        }

        public Task<IReadOnlyList<SalesSyncOutboxItem>> GetPendingAsync(int take, long nowMs)
        {
            return _repository.GetPendingAsync(take, nowMs);
        }

        public Task<SalesSyncOutboxSummary> GetSummaryAsync()
        {
            return _repository.GetSummaryAsync();
        }

        public Task<OutboxDrainState> GetDrainStateAsync(long nowMs)
        {
            return _repository.GetDrainStateAsync(nowMs);
        }

        public Task<bool> HasUnresolvedAsync()
        {
            return _repository.HasUnresolvedAsync();
        }

        public Task<IReadOnlyDictionary<long, string>> GetRemoteProductIdsAsync(
            IEnumerable<long> productIds)
        {
            return _repository.GetRemoteProductIdsAsync(productIds);
        }

        public Task<bool> PrepareAsync(
            SalesSyncOutboxItem item,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncGeneration? generation = null,
            string? claimToken = null)
        {
            return _repository.PrepareAttemptAsync(
                item.Id,
                item.ClientBatchId,
                item.PayloadJson,
                item.PayloadHash,
                nowMs,
                expectedAttemptCount,
                item.Status,
                item.NextRetryAt,
                item.LeaseObservedAt,
                generation!,
                claimToken!);
        }

        public Task<bool> MarkAckedAsync(
            long outboxId,
            long saleId,
            string serverBatchId,
            string serverSaleId,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence? fence = null)
        {
            return _repository.MarkAckedAsync(
                outboxId,
                saleId,
                serverBatchId,
                serverSaleId,
                nowMs,
                expectedAttemptCount,
                fence!);
        }

        public Task<bool> MarkRetryAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence? fence = null)
        {
            return _repository.MarkRetryAsync(
                outboxId,
                saleId,
                errorCode,
                nextRetryAt,
                nowMs,
                expectedAttemptCount,
                fence!);
        }

        public Task<bool> DeferDependencyAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence? fence = null)
        {
            return _repository.DeferDependencyAsync(
                outboxId,
                saleId,
                errorCode,
                nextRetryAt,
                nowMs,
                expectedAttemptCount,
                fence!);
        }

        public Task<bool> ReleaseAttemptAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence? fence = null)
        {
            return _repository.ReleaseAttemptAsync(
                outboxId,
                saleId,
                errorCode,
                nextRetryAt,
                nowMs,
                expectedAttemptCount,
                fence!);
        }

        public Task<bool> MarkBlockedAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence? fence = null)
        {
            return _repository.MarkBlockedAsync(
                outboxId,
                saleId,
                errorCode,
                nowMs,
                expectedAttemptCount,
                fence!);
        }

        public Task<bool> MarkOriginBlockedAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nowMs,
            string expectedStatus,
            int expectedAttemptCount,
            long expectedLeaseObservedAt)
        {
            return _repository.MarkOriginBlockedAsync(
                outboxId,
                saleId,
                errorCode,
                nowMs,
                expectedStatus,
                expectedAttemptCount,
                expectedLeaseObservedAt);
        }
    }

    private sealed class FacadeOutboxSurface : ISalesOutboxSurface
    {
        private readonly SaleRepository _repository;

        public FacadeOutboxSurface(SqliteConnectionFactory factory)
        {
            _repository = new SaleRepository(factory);
        }

        public Task EnqueueAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long saleId,
            string clientSaleId)
        {
            return _repository.EnqueueSalesSyncOutboxAsync(conn, tx, saleId, clientSaleId);
        }

        public Task<IReadOnlyList<SalesSyncOutboxItem>> GetPendingAsync(int take, long nowMs)
        {
            return _repository.GetPendingSalesSyncOutboxAsync(take, nowMs);
        }

        public Task<SalesSyncOutboxSummary> GetSummaryAsync()
        {
            return _repository.GetSalesSyncOutboxSummaryAsync();
        }

        public Task<OutboxDrainState> GetDrainStateAsync(long nowMs)
        {
            return _repository.GetSalesSyncDrainStateAsync(nowMs);
        }

        public Task<bool> HasUnresolvedAsync()
        {
            return _repository.HasUnresolvedSalesSyncOutboxAsync();
        }

        public Task<IReadOnlyDictionary<long, string>> GetRemoteProductIdsAsync(
            IEnumerable<long> productIds)
        {
            return _repository.GetRemoteProductIdsAsync(productIds);
        }

        public Task<bool> PrepareAsync(
            SalesSyncOutboxItem item,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncGeneration? generation = null,
            string? claimToken = null)
        {
            return _repository.PrepareSalesSyncAttemptAsync(
                item.Id,
                item.ClientBatchId,
                item.PayloadJson,
                item.PayloadHash,
                nowMs,
                expectedAttemptCount,
                item.Status,
                item.NextRetryAt,
                item.LeaseObservedAt,
                generation!,
                claimToken!);
        }

        public Task<bool> MarkAckedAsync(
            long outboxId,
            long saleId,
            string serverBatchId,
            string serverSaleId,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence? fence = null)
        {
            return _repository.MarkSalesSyncAckedAsync(
                outboxId,
                saleId,
                serverBatchId,
                serverSaleId,
                nowMs,
                expectedAttemptCount,
                fence!);
        }

        public Task<bool> MarkRetryAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence? fence = null)
        {
            return _repository.MarkSalesSyncRetryAsync(
                outboxId,
                saleId,
                errorCode,
                nextRetryAt,
                nowMs,
                expectedAttemptCount,
                fence!);
        }

        public Task<bool> DeferDependencyAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence? fence = null)
        {
            return _repository.DeferSalesSyncDependencyAsync(
                outboxId,
                saleId,
                errorCode,
                nextRetryAt,
                nowMs,
                expectedAttemptCount,
                fence!);
        }

        public Task<bool> ReleaseAttemptAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nextRetryAt,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence? fence = null)
        {
            return _repository.ReleaseSalesSyncAttemptAsync(
                outboxId,
                saleId,
                errorCode,
                nextRetryAt,
                nowMs,
                expectedAttemptCount,
                fence!);
        }

        public Task<bool> MarkBlockedAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nowMs,
            int expectedAttemptCount,
            OnlineSyncAttemptFence? fence = null)
        {
            return _repository.MarkSalesSyncBlockedAsync(
                outboxId,
                saleId,
                errorCode,
                nowMs,
                expectedAttemptCount,
                fence!);
        }

        public Task<bool> MarkOriginBlockedAsync(
            long outboxId,
            long saleId,
            string errorCode,
            long nowMs,
            string expectedStatus,
            int expectedAttemptCount,
            long expectedLeaseObservedAt)
        {
            return _repository.MarkSalesSyncOriginBlockedAsync(
                outboxId,
                saleId,
                errorCode,
                nowMs,
                expectedStatus,
                expectedAttemptCount,
                expectedLeaseObservedAt);
        }
    }

    private sealed class OutboxState
    {
        public int AttemptCount { get; set; }
        public string? ClaimGenerationId { get; set; }
        public string? ClaimToken { get; set; }
        public string? ClientBatchId { get; set; }
        public string? ClientSaleId { get; set; }
        public string? IdempotencyKey { get; set; }
        public string? LastErrorCode { get; set; }
        public long NextRetryAt { get; set; }
        public string? PayloadHash { get; set; }
        public string? PayloadJson { get; set; }
        public string? SaleSyncStatus { get; set; }
        public string? Status { get; set; }
    }

    private sealed class TestDb : IDisposable
    {
        private TestDb(string root)
        {
            _root = root;
            var options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));
            Factory = new SqliteConnectionFactory(options);
            DbInitializer.EnsureCreated(options);
        }

        internal SqliteConnectionFactory Factory { get; }
        private readonly string _root;

        internal static TestDb Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "win7pos-sales-sync-outbox-repository-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestDb(root);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, true); } catch { }
        }
    }
}
