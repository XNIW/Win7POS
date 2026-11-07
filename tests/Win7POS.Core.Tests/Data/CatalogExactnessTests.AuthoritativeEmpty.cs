using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

public sealed partial class CatalogExactnessTests
{
    [TestMethod]
    public async Task AuthoritativeEmpty_CompleteSnapshotPersistsObservationWithoutPublishingCatalogOrSales()
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);
        var stage = await StageEmptySnapshotAsync(db, "empty-observation");
        var state = new CatalogShopStateRepository(db.Factory);
        var before = await state.EnsureAndLoadCursorAsync(stage.ShopId, stage.ShopCode, stage.Generation);

        Assert.AreEqual(string.Empty, await ConfirmEmptySnapshotAsync(db, stage));

        // Reopening the repository exercises the durable observation, while the
        // last live catalog, cursor and sale-safety gates remain untouched.
        state = new CatalogShopStateRepository(db.Factory);
        Assert.IsTrue(await state.IsAuthoritativelyEmptyAsync(stage.Generation));
        Assert.IsFalse(await state.IsSaleSafeForOfficialShopAsync());
        await AssertPreflightPreservedStateAsync(db, stage, before, "product-1");
        using var conn = db.Factory.Open();
        Assert.AreEqual(2L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM products WHERE is_active = 1;"));
        Assert.AreEqual(0L, await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1) FROM app_settings
WHERE key IN (@safe, @completed) AND TRIM(value) <> '';",
            new { safe = CatalogShopStateRepository.SaleSafeAtKey, completed = CatalogShopStateRepository.InitialCompletedAtKey }));
    }

    [TestMethod]
    public async Task AuthoritativeEmpty_PreviouslySaleSafeCatalogRemainsBlockedAfterObservationIsCleared()
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);
        var stage = await StageEmptySnapshotAsync(db, "empty-was-safe");
        await CatalogExactnessTestFixture.SeedVerifiedAsync(db.Factory, stage.ShopId, stage.ShopCode);
        var state = new CatalogShopStateRepository(db.Factory);
        await state.StoreSaleSafeAsync(stage.ShopId, stage.ShopCode, "2026-11-07T01:00:00Z",
            stage.Fence.ExpectedEpoch, generation: stage.Generation);
        Assert.IsTrue(await state.IsSaleSafeForOfficialShopAsync());

        Assert.AreEqual(string.Empty, await ConfirmEmptySnapshotAsync(db, stage));
        Assert.IsTrue(await state.IsAuthoritativelyEmptyAsync(stage.Generation));
        Assert.IsFalse(await state.IsSaleSafeForOfficialShopAsync());
        await state.ClearAuthoritativeEmptyAsync(stage.Generation);
        Assert.IsFalse(await state.IsAuthoritativelyEmptyAsync(stage.Generation));
        Assert.IsFalse(await state.IsSaleSafeForOfficialShopAsync());
        using var conn = db.Factory.Open();
        Assert.AreEqual(2L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM products WHERE is_active = 1;"));
    }

    [TestMethod]
    public async Task AuthoritativeEmpty_IncompleteNewAttemptInvalidatesPreviousObservation()
    {
        using var db = TestDb.Create();
        var stage = await StageEmptySnapshotAsync(db, "empty-retry");
        Assert.AreEqual(string.Empty, await ConfirmEmptySnapshotAsync(db, stage));
        var incomplete = CompleteContext(0, 0, 0, 0);
        incomplete.Pages = 2;

        var code = await new CatalogFullRefreshReconciler(db.Factory).ConfirmAuthoritativeEmptyAsync(
            stage.FullRunId, Summary(0, 0, 0, 0), incomplete, stage.Fence);

        Assert.AreEqual("catalog_authoritative_stage_incomplete", code);
        Assert.IsFalse(await new CatalogShopStateRepository(db.Factory).IsAuthoritativelyEmptyAsync(stage.Generation));
    }

    [TestMethod]
    public async Task AuthoritativeEmpty_MissingAuthoritativeSummaryIsNotZeroEvidence()
    {
        using var db = TestDb.Create();
        var stage = await StageEmptySnapshotAsync(db, "empty-no-summary");
        var code = await new CatalogFullRefreshReconciler(db.Factory).ConfirmAuthoritativeEmptyAsync(
            stage.FullRunId, null!, CompleteContext(0, 0, 0, 0), stage.Fence);
        Assert.AreEqual("catalog_summary_incomplete", code);
        Assert.IsFalse(await new CatalogShopStateRepository(db.Factory).IsAuthoritativelyEmptyAsync(stage.Generation));
    }

    [TestMethod]
    public async Task AuthoritativeEmpty_MissingRunContextIsRejectedWithoutCreatingObservation()
    {
        using var db = TestDb.Create();
        var stage = await StageEmptySnapshotAsync(db, "empty-no-context");
        var code = await new CatalogFullRefreshReconciler(db.Factory).ConfirmAuthoritativeEmptyAsync(
            stage.FullRunId, Summary(0, 0, 0, 0), null!, stage.Fence);
        Assert.AreEqual("catalog_authoritative_stage_incomplete", code);
        Assert.IsFalse(await new CatalogShopStateRepository(db.Factory).IsAuthoritativelyEmptyAsync(stage.Generation));
    }

    [TestMethod]
    public async Task AuthoritativeEmpty_UnfinishedPageChainIsRejected()
    {
        using var db = TestDb.Create();
        var stage = await StageEmptySnapshotAsync(db, "empty-partial", hasMore: true);
        Assert.AreEqual("catalog_authoritative_stage_incomplete", await ConfirmEmptySnapshotAsync(db, stage));
        Assert.IsFalse(await new CatalogShopStateRepository(db.Factory).IsAuthoritativelyEmptyAsync(stage.Generation));
    }

    [TestMethod]
    public async Task AuthoritativeEmpty_NonzeroAuthoritativeLaneIsRejected()
    {
        using var db = TestDb.Create();
        var stage = await StageEmptySnapshotAsync(db, "empty-nonzero-summary");
        var summary = Summary(0, 1, 0, 0);
        var code = await new CatalogFullRefreshReconciler(db.Factory).ConfirmAuthoritativeEmptyAsync(
            stage.FullRunId, summary, CompleteContext(0, 0, 0, 0), stage.Fence);
        Assert.AreEqual("catalog_authoritative_empty_requires_zero_summary", code);
        Assert.IsFalse(await new CatalogShopStateRepository(db.Factory).IsAuthoritativelyEmptyAsync(stage.Generation));
    }

    [TestMethod]
    public async Task AuthoritativeEmpty_ZeroSummaryDoesNotHideUnexpectedStageRows()
    {
        using var db = TestDb.Create();
        var stage = await CreateAuthoritativeStageAsync(db, "empty-unexpected-lane");
        await new RemoteCatalogBatchRepository(db.Factory).StageAuthoritativePageAsync(
            new RemoteCatalogBatch
            {
                AuthoritativeFullRefresh = true,
                AuthoritativeStagePage = new CatalogAuthoritativeStagePage
                { FullRunId = stage.FullRunId, PageNumber = 1, HasMore = false },
                Categories = new[] { new RemoteCatalogCategoryWrite
                    { RemoteCategoryId = "unexpected-category", Name = "Category" } }
            }, CancellationToken.None, stage.Fence);

        Assert.AreEqual("catalog_category_row_evidence_mismatch", await ConfirmEmptySnapshotAsync(db, stage));
        Assert.IsFalse(await new CatalogShopStateRepository(db.Factory).IsAuthoritativelyEmptyAsync(stage.Generation));
    }

    [TestMethod]
    public async Task AuthoritativeEmpty_PartialResponseContextAndChecksumMismatchAreRejected()
    {
        using var db = TestDb.Create();
        var stage = await StageEmptySnapshotAsync(db, "empty-response-context");
        var context = CompleteContext(0, 0, 0, 0);
        context.HasMore = true;
        var reconciler = new CatalogFullRefreshReconciler(db.Factory);
        Assert.AreEqual("catalog_pagination_not_complete", await reconciler.ConfirmAuthoritativeEmptyAsync(
            stage.FullRunId, Summary(0, 0, 0, 0), context, stage.Fence));

        context = CompleteContext(0, 0, 0, 0);
        context.ActualChecksumAlgorithm = "sha256";
        context.ActualChecksum = new string('b', 64);
        var summary = Summary(0, 0, 0, 0);
        summary.ChecksumAlgorithm = "sha256";
        summary.Checksum = new string('a', 64);
        Assert.AreEqual("catalog_checksum_mismatch", await reconciler.ConfirmAuthoritativeEmptyAsync(
            stage.FullRunId, summary, context, stage.Fence));
        Assert.IsFalse(await new CatalogShopStateRepository(db.Factory).IsAuthoritativelyEmptyAsync(stage.Generation));
    }

    [TestMethod]
    public async Task AuthoritativeEmpty_StoppedOrReplacedGenerationCannotReuseOrWriteObservation()
    {
        using var db = TestDb.Create();
        var stage = await StageEmptySnapshotAsync(db, "empty-generation");
        Assert.AreEqual(string.Empty, await ConfirmEmptySnapshotAsync(db, stage));
        var generations = new OnlineSyncGenerationRepository(db.Factory);
        Assert.IsTrue(await generations.StopIfCurrentAsync(stage.Generation, "session_revoked", 2));
        var state = new CatalogShopStateRepository(db.Factory);
        Assert.IsFalse(await state.IsAuthoritativelyEmptyAsync(stage.Generation));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ConfirmEmptySnapshotAsync(db, stage));

        var replacement = new OnlineSyncGeneration("replacement", "new-session", "new-device", stage.ShopId, stage.ShopCode);
        await generations.ActivateAndRecoverAsync(replacement, 3);
        Assert.IsFalse(await state.IsAuthoritativelyEmptyAsync(replacement));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ConfirmEmptySnapshotAsync(db, stage));
    }

    [TestMethod]
    public async Task AuthoritativeEmpty_OfficialShopChangeDeniesObservationAndConfirmation()
    {
        using var db = TestDb.Create();
        var stage = await StageEmptySnapshotAsync(db, "empty-scope");
        Assert.AreEqual(string.Empty, await ConfirmEmptySnapshotAsync(db, stage));
        await SaveShopAsync(db, "another-shop", "ANOTHER-SHOP");
        Assert.IsFalse(await new CatalogShopStateRepository(db.Factory).IsAuthoritativelyEmptyAsync(stage.Generation));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ConfirmEmptySnapshotAsync(db, stage));
    }

    [TestMethod]
    public async Task AuthoritativeEmpty_NewPullClearsProofAndRejectsStaleGenerationClear()
    {
        using var db = TestDb.Create();
        var stage = await StageEmptySnapshotAsync(db, "empty-clear");
        Assert.AreEqual(string.Empty, await ConfirmEmptySnapshotAsync(db, stage));
        var state = new CatalogShopStateRepository(db.Factory);
        var wrongGeneration = new OnlineSyncGeneration("wrong-generation", "wrong-session", "wrong-device", stage.ShopId, stage.ShopCode);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => state.ClearAuthoritativeEmptyAsync(wrongGeneration));
        Assert.IsTrue(await state.IsAuthoritativelyEmptyAsync(stage.Generation));
        await state.ClearAuthoritativeEmptyAsync(stage.Generation);
        Assert.IsFalse(await state.IsAuthoritativelyEmptyAsync(stage.Generation));
    }

    [TestMethod]
    public async Task AuthoritativeEmpty_RepairAndRestoreReviewClearProof()
    {
        using var db = TestDb.Create();
        var stage = await StageEmptySnapshotAsync(db, "empty-repair");
        Assert.AreEqual(string.Empty, await ConfirmEmptySnapshotAsync(db, stage));
        var state = new CatalogShopStateRepository(db.Factory);
        await state.RequestFullRepairAsync(stage.ShopId, stage.ShopCode, stage.Fence.ExpectedEpoch, stage.Generation);
        Assert.IsFalse(await state.IsAuthoritativelyEmptyAsync(stage.Generation));
        // DbInitializer takes the process-wide maintenance drain. Release this
        // verification connection before initializing the independent restore DB.
        using (var conn = db.Factory.Open())
        {
            Assert.AreEqual(0L, await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM app_settings WHERE key LIKE @prefix;",
                new { prefix = CatalogShopStateRepository.AuthoritativeEmptyPrefix + "%" }));
        }

        using var restoreDb = TestDb.Create();
        var restoreStage = await StageEmptySnapshotAsync(restoreDb, "empty-restore");
        Assert.AreEqual(string.Empty, await ConfirmEmptySnapshotAsync(restoreDb, restoreStage));
        await new OnlineSyncGenerationRepository(restoreDb.Factory).StopIfCurrentAsync(restoreStage.Generation, "restore_review", 4);
        var restoreState = new CatalogShopStateRepository(restoreDb.Factory);
        await restoreState.ResetForRestoreReviewAsync(restoreStage.ShopId, restoreStage.ShopCode);
        Assert.IsFalse(await restoreState.IsAuthoritativelyEmptyAsync(restoreStage.Generation));
        using var restoreConn = restoreDb.Factory.Open();
        Assert.AreEqual(0L, await restoreConn.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM app_settings WHERE key LIKE @prefix;",
            new { prefix = CatalogShopStateRepository.AuthoritativeEmptyPrefix + "%" }));
    }

    private static async Task<AuthoritativeStageContext> StageEmptySnapshotAsync(
        TestDb db, string suffix, bool hasMore = false)
    {
        var stage = await CreateAuthoritativeStageAsync(db, suffix);
        await new RemoteCatalogBatchRepository(db.Factory).StageAuthoritativePageAsync(
            new RemoteCatalogBatch
            {
                AuthoritativeFullRefresh = true,
                AuthoritativeStagePage = new CatalogAuthoritativeStagePage
                { FullRunId = stage.FullRunId, PageNumber = 1, HasMore = hasMore }
            }, CancellationToken.None, stage.Fence);
        return stage;
    }

    private static Task<string> ConfirmEmptySnapshotAsync(TestDb db, AuthoritativeStageContext stage)
    {
        return new CatalogFullRefreshReconciler(db.Factory).ConfirmAuthoritativeEmptyAsync(
            stage.FullRunId, Summary(0, 0, 0, 0), CompleteContext(0, 0, 0, 0), stage.Fence);
    }
}
