using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class OnlineSyncGenerationRepositoryTests
{
    [TestMethod]
    public async Task Relink_ReleasesClaimsAndRejectsOldGenerationCommits()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory);
        var sales = new SaleRepository(db.Factory);
        var saleId = await InsertSaleAsync(sales);
        var generations = new OnlineSyncGenerationRepository(db.Factory);
        var settings = new SettingsRepository(db.Factory);
        var generationOne = Generation("generation-one");
        var generationTwo = Generation("generation-two");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await generations.ActivateAndRecoverAsync(generationOne, nowMs);
        var pending = (await sales.GetPendingSalesSyncOutboxAsync(1, nowMs + 1)).Single();
        var firstClaim = OnlineSyncAttemptFence.CreateClaimToken();
        Assert.IsTrue(await sales.PrepareSalesSyncAttemptAsync(
            pending.Id,
            pending.ClientBatchId,
            pending.PayloadJson,
            pending.PayloadHash,
            nowMs + 2,
            pending.AttemptCount,
            pending.Status,
            pending.NextRetryAt,
            pending.LeaseObservedAt,
            generationOne,
            firstClaim));

        await generations.ActivateAndRecoverAsync(generationOne, nowMs + 3);
        await AssertClaimAsync(
            db.Factory,
            pending.Id,
            "in_progress",
            1,
            generationOne.GenerationId,
            firstClaim);

        await generations.ActivateAndRecoverAsync(generationTwo, nowMs + 4);
        await AssertClaimAsync(db.Factory, pending.Id, "retry", 0, null, null);
        Assert.IsFalse(await generations.StopIfCurrentAsync(
            generationOne,
            "late_auth_denied",
            nowMs + 5));
        Assert.IsTrue(await generations.IsCurrentAndActiveAsync(generationTwo));
        Assert.IsFalse(await settings.SetStringIfGenerationCurrentAsync(
            "sync.test.generation",
            "old",
            generationOne));
        Assert.IsTrue(await settings.SetStringIfGenerationCurrentAsync(
            "sync.test.generation",
            "current",
            generationTwo));

        var retry = (await sales.GetPendingSalesSyncOutboxAsync(1, nowMs + 6)).Single();
        var secondClaim = OnlineSyncAttemptFence.CreateClaimToken();
        Assert.IsTrue(await sales.PrepareSalesSyncAttemptAsync(
            retry.Id,
            retry.ClientBatchId,
            retry.PayloadJson,
            retry.PayloadHash,
            nowMs + 7,
            retry.AttemptCount,
            retry.Status,
            retry.NextRetryAt,
            retry.LeaseObservedAt,
            generationTwo,
            secondClaim));

        Assert.IsFalse(await sales.MarkSalesSyncAckedAsync(
            retry.Id,
            saleId,
            "server-batch-old",
            "server-sale-old",
            nowMs + 8,
            1,
            new OnlineSyncAttemptFence(generationOne, firstClaim, 1)));
        Assert.IsTrue(await sales.MarkSalesSyncAckedAsync(
            retry.Id,
            saleId,
            "server-batch-current",
            "server-sale-current",
            nowMs + 9,
            1,
            new OnlineSyncAttemptFence(generationTwo, secondClaim, 1)));

        Assert.AreEqual("current", await settings.GetStringAsync("sync.test.generation"));
        await AssertClaimAsync(db.Factory, pending.Id, "acked", 1, null, null);
    }

    [TestMethod]
    public async Task StoppedGeneration_CannotBeReactivated_ButFreshRelinkSurvivesClockRollback()
    {
        using var db = TestDb.Create();
        var generations = new OnlineSyncGenerationRepository(db.Factory);
        var first = Generation("generation-stopped");
        var newer = Generation("generation-newer");

        await generations.ActivateAndRecoverAsync(first, 100);
        Assert.IsTrue(await generations.StopIfCurrentAsync(first, "session_revoked", 101));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            generations.ActivateAndRecoverAsync(first, 102));

        await generations.ActivateAndRecoverAsync(newer, 10_000);
        var afterRestart = Generation("generation-after-clock-rollback");
        var restartPredecessor = await generations.ReadCurrentPredecessorAsync();
        await generations.ActivateAndRecoverAsync(
            afterRestart,
            50,
            restartPredecessor);

        Assert.IsFalse(await generations.IsCurrentAndActiveAsync(newer));
        Assert.IsTrue(await generations.IsCurrentAndActiveAsync(afterRestart));
        using (var connection = db.Factory.Open())
        {
            Assert.AreEqual(10_001L, await connection.ExecuteScalarAsync<long>(@"
SELECT activated_at
FROM pos_sync_session_generation
WHERE singleton_id = 1;"));
        }

        var current = Generation("generation-current");
        var afterRestartPredecessor = await generations
            .ReadCurrentPredecessorAsync();
        await generations.ActivateAndRecoverAsync(
            current,
            51,
            afterRestartPredecessor);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            generations.ActivateAndRecoverAsync(
                Generation("generation-delayed"),
                52,
                afterRestartPredecessor));
        Assert.IsTrue(await generations.IsCurrentAndActiveAsync(current));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            generations.ActivateAndRecoverAsync(
                Generation("generation-expected-empty"),
                53,
                OnlineSyncGenerationPredecessorState.None));

        var activeCurrent = await generations.ReadCurrentPredecessorAsync();
        Assert.IsTrue(activeCurrent.Exists);
        Assert.IsTrue(activeCurrent.Active);
        Assert.IsTrue(await generations.StopIfCurrentAsync(
            current,
            "authorization_epoch_changed",
            54));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            generations.ActivateAndRecoverAsync(
                Generation("generation-after-inactive-predecessor"),
                55,
                activeCurrent));

        using (var emptyDb = TestDb.Create())
        {
            var emptyGenerations = new OnlineSyncGenerationRepository(emptyDb.Factory);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                emptyGenerations.ActivateAndRecoverAsync(
                    Generation("generation-missing-predecessor"),
                    1,
                    activeCurrent));
            var emptyPredecessor = await emptyGenerations
                .ReadCurrentPredecessorAsync();
            Assert.IsFalse(emptyPredecessor.Exists);
            await emptyGenerations.ActivateAndRecoverAsync(
                Generation("generation-first"),
                1,
                emptyPredecessor);
            Assert.IsTrue(await emptyGenerations.IsCurrentAndActiveAsync(
                Generation("generation-first")));
        }
    }

    [TestMethod]
    public async Task MissingGenerationInitializesOnce_ButNeverReplacesDurableState()
    {
        using var db = TestDb.Create();
        var generations = new OnlineSyncGenerationRepository(db.Factory);
        var first = Generation("generation-upgrade");
        var different = Generation("generation-stale-file");

        Assert.IsTrue(await generations.AttachOrInitializeCurrentAsync(first, 100));
        Assert.IsTrue(await generations.AttachOrInitializeCurrentAsync(first, 101));
        Assert.IsFalse(await generations.AttachOrInitializeCurrentAsync(different, 102));
        Assert.IsTrue(await generations.StopIfCurrentAsync(first, "session_revoked", 103));
        Assert.IsFalse(await generations.AttachOrInitializeCurrentAsync(first, 104));
    }

    [TestMethod]
    public async Task LegacyStoredTrust_NormalizedThenDeniedCannotReinitializeAfterRestart()
    {
        using var db = TestDb.Create();
        var generations = new OnlineSyncGenerationRepository(db.Factory);
        var legacyStoredGeneration = Generation("generation-legacy-file");

        var emptyState = await generations.ReadCurrentPredecessorAsync();
        Assert.IsFalse(emptyState.Exists);
        Assert.IsTrue(await generations.AttachOrInitializeCurrentAsync(
            legacyStoredGeneration,
            100));

        var denialScope = await generations.ReadCurrentPredecessorAsync();
        Assert.IsTrue(denialScope.Exists);
        Assert.IsTrue(denialScope.Active);
        Assert.AreEqual(legacyStoredGeneration.Fingerprint, denialScope.Fingerprint);
        Assert.IsTrue(await generations.StopIfCurrentPredecessorAsync(
            denialScope,
            "auth_denied",
            101));

        var afterRestart = new OnlineSyncGenerationRepository(db.Factory);
        Assert.IsFalse(await afterRestart.AttachOrInitializeCurrentAsync(
            legacyStoredGeneration,
            102));
        var tombstone = await afterRestart.ReadCurrentPredecessorAsync();
        Assert.IsTrue(tombstone.Exists);
        Assert.IsFalse(tombstone.Active);
        Assert.AreEqual(legacyStoredGeneration.Fingerprint, tombstone.Fingerprint);
    }

    [TestMethod]
    public async Task ConcurrentAuthenticatedRelinks_FromSamePredecessor_CommitExactlyOne()
    {
        using var db = TestDb.Create();
        var generations = new OnlineSyncGenerationRepository(db.Factory);
        var predecessor = Generation("generation-concurrent-predecessor");
        var candidateOne = Generation("generation-concurrent-one");
        var candidateTwo = Generation("generation-concurrent-two");
        await generations.ActivateAndRecoverAsync(predecessor, 100);
        var expected = await generations.ReadCurrentPredecessorAsync();
        var start = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var first = ActivateAfterSignalAsync(
            generations,
            candidateOne,
            101,
            expected,
            start.Task);
        var second = ActivateAfterSignalAsync(
            generations,
            candidateTwo,
            101,
            expected,
            start.Task);
        start.SetResult(true);
        var results = await Task.WhenAll(first, second);

        Assert.AreEqual(1, results.Count(value => value));
        Assert.AreEqual(
            results[0],
            await generations.IsCurrentAndActiveAsync(candidateOne));
        Assert.AreEqual(
            results[1],
            await generations.IsCurrentAndActiveAsync(candidateTwo));
    }

    [TestMethod]
    public async Task PredecessorScopedStop_TombstonesWithoutTrustedFileState()
    {
        using var db = TestDb.Create();
        var generations = new OnlineSyncGenerationRepository(db.Factory);
        var current = Generation("generation-scoped-denial");
        await generations.ActivateAndRecoverAsync(current, 100);
        var expected = await generations.ReadCurrentPredecessorAsync();
        var different = Generation("generation-different-denial");

        Assert.IsFalse(await generations.StopIfCurrentPredecessorAsync(
            new OnlineSyncGenerationPredecessorState(
                true,
                different.Fingerprint,
                active: true),
            "auth_denied",
            101));
        Assert.IsTrue(await generations.IsCurrentAndActiveAsync(current));

        Assert.IsTrue(await generations.StopIfCurrentPredecessorAsync(
            expected,
            "auth_denied",
            102));
        Assert.IsFalse(await generations.IsCurrentAndActiveAsync(current));
        var stopped = await generations.ReadCurrentPredecessorAsync();
        Assert.IsTrue(stopped.Exists);
        Assert.IsFalse(stopped.Active);
        Assert.AreEqual(expected.Fingerprint, stopped.Fingerprint);
    }

    [TestMethod]
    public async Task RestoreReset_PersistsInactiveTombstoneAndReleasesClaims()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory);
        var sales = new SaleRepository(db.Factory);
        await InsertSaleAsync(sales);
        var generations = new OnlineSyncGenerationRepository(db.Factory);
        var invalidated = Generation("generation-before-restore");
        var replacement = Generation("generation-after-restore");
        await generations.ActivateAndRecoverAsync(invalidated, 100);
        var preRestoreState = await generations.ReadCurrentPredecessorAsync();
        var pending = (await sales.GetPendingSalesSyncOutboxAsync(1, 101)).Single();
        var claim = OnlineSyncAttemptFence.CreateClaimToken();
        Assert.IsTrue(await sales.PrepareSalesSyncAttemptAsync(
            pending.Id,
            pending.ClientBatchId,
            pending.PayloadJson,
            pending.PayloadHash,
            102,
            pending.AttemptCount,
            pending.Status,
            pending.NextRetryAt,
            pending.LeaseObservedAt,
            invalidated,
            claim));

        await generations.ResetForRestoreAsync(
            invalidated,
            invalidated.ShopId,
            invalidated.ShopCode,
            103);

        using (var connection = db.Factory.Open())
        {
            var generation = await connection.QuerySingleAsync<GenerationRow>(@"
SELECT
  generation_id AS GenerationId,
  active AS Active,
  auth_stop_reason AS StopReason
FROM pos_sync_session_generation
WHERE singleton_id = 1;");
            Assert.AreEqual(invalidated.GenerationId, generation.GenerationId);
            Assert.AreEqual(0L, generation.Active);
            Assert.AreEqual("database_restored", generation.StopReason);
        }
        await AssertClaimAsync(db.Factory, pending.Id, "retry", 0, null, null);
        Assert.IsFalse(await generations.IsCurrentAndActiveAsync(invalidated));
        Assert.IsFalse(await generations.AttachOrInitializeCurrentAsync(invalidated, 104));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            generations.ActivateAndRecoverAsync(invalidated, 104));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            generations.ActivateAndRecoverAsync(
                replacement,
                104,
                preRestoreState));
        var postRestoreState = await generations.ReadCurrentPredecessorAsync();
        Assert.IsTrue(postRestoreState.Exists);
        Assert.IsFalse(postRestoreState.Active);
        await generations.ActivateAndRecoverAsync(
            replacement,
            104,
            postRestoreState);
        Assert.IsTrue(await generations.IsCurrentAndActiveAsync(replacement));
    }

    private static async Task<bool> ActivateAfterSignalAsync(
        OnlineSyncGenerationRepository generations,
        OnlineSyncGeneration generation,
        long activatedAt,
        OnlineSyncGenerationPredecessorState expected,
        Task start)
    {
        await start;
        try
        {
            await generations.ActivateAndRecoverAsync(
                generation,
                activatedAt,
                expected);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task AssertClaimAsync(
        SqliteConnectionFactory factory,
        long outboxId,
        string expectedStatus,
        long expectedAttemptCount,
        string? expectedGenerationId,
        string? expectedClaimToken)
    {
        using var connection = factory.Open();
        var row = await connection.QuerySingleAsync<ClaimRow>(@"
SELECT
  status AS Status,
  attempt_count AS AttemptCount,
  claim_generation_id AS GenerationId,
  claim_token AS ClaimToken
FROM sales_sync_outbox
WHERE id = @outboxId;",
            new { outboxId });
        Assert.AreEqual(expectedStatus, row.Status);
        Assert.AreEqual(expectedAttemptCount, row.AttemptCount);
        Assert.AreEqual(expectedGenerationId, row.GenerationId);
        Assert.AreEqual(expectedClaimToken, row.ClaimToken);
    }

    private static OnlineSyncGeneration Generation(string id)
    {
        return new OnlineSyncGeneration(
            id,
            "session-generation-test",
            "device-generation-test",
            "shop-generation-test",
            "SHOP-GENERATION");
    }

    private static async Task<long> InsertSaleAsync(SaleRepository sales)
    {
        return await sales.InsertSaleAsync(
            new Sale
            {
                Code = "GENERATION-SALE",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Kind = (int)SaleKind.Sale,
                Total = 100,
                PaidCash = 100
            },
            new[]
            {
                new SaleLine
                {
                    Barcode = "GENERATION-ITEM",
                    Name = "Generation item",
                    Quantity = 1,
                    UnitPrice = 100
                }
            });
    }

    private static Task SaveShopAsync(SqliteConnectionFactory factory)
    {
        return new ShopOfficialSnapshotRepository(factory).SaveAsync(
            new OfficialShopSnapshot
            {
                ShopCode = "SHOP-GENERATION",
                ShopId = "shop-generation-test",
                ShopName = "Generation shop",
                Source = "test"
            });
    }

    private sealed class ClaimRow
    {
        public long AttemptCount { get; set; }
        public string? ClaimToken { get; set; }
        public string? GenerationId { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    private sealed class GenerationRow
    {
        public long Active { get; set; }
        public string GenerationId { get; set; } = string.Empty;
        public string StopReason { get; set; } = string.Empty;
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

        public SqliteConnectionFactory Factory { get; }
        private string Root { get; }

        public static TestDb Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "Win7POS.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestDb(root);
        }

        public void Dispose()
        {
            SqliteConnectionFactory.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
