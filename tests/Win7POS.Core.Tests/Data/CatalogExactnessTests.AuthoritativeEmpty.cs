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
    public async Task AuthoritativeEmpty_DeltaCannotRestoreSaleSafeFromPreviousVerifiedSnapshot()
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);
        var stage = await CreateAuthoritativeStageAsync(db, "empty-delta-revival");
        var state = new CatalogShopStateRepository(db.Factory);
        await CatalogExactnessTestFixture.SeedVerifiedAsync(db.Factory, stage.ShopId, stage.ShopCode);
        await state.StoreLastSyncAsync(stage.ShopId, stage.ShopCode,
            "previous-full-cursor", "2026-11-07T01:00:00Z", stage.Fence.ExpectedEpoch,
            "full_refresh", stage.Fence.ExpectedPreviousCursor, stage.Fence.ExpectedPreviousMode,
            stage.Generation);
        stage.Fence.ExpectedPreviousCursor = "previous-full-cursor";
        stage.Fence.ExpectedPreviousMode = "full_refresh";
        await state.StoreSaleSafeAsync(stage.ShopId, stage.ShopCode, "2026-11-07T01:00:00Z",
            stage.Fence.ExpectedEpoch, "previous-full-cursor", "full_refresh", generation: stage.Generation);
        Assert.IsTrue(await state.IsSaleSafeForOfficialShopAsync(), "The retained catalog must start verified and sale-safe.");
        await new RemoteCatalogBatchRepository(db.Factory).StageAuthoritativePageAsync(
            new RemoteCatalogBatch
            {
                AuthoritativeFullRefresh = true,
                AuthoritativeStagePage = new CatalogAuthoritativeStagePage
                { FullRunId = stage.FullRunId, PageNumber = 1, HasMore = false }
            }, CancellationToken.None, stage.Fence);

        Assert.AreEqual(string.Empty, await ConfirmEmptySnapshotAsync(db, stage));
        Assert.IsFalse(await state.IsSaleSafeForOfficialShopAsync());
        Assert.AreEqual(CatalogCompletenessStatus.Verified, (await state.LoadExactnessAsync()).Status,
            "The old exactness evidence is retained, not replaced by a false empty sale-safe snapshot.");
        await state.ClearAuthoritativeEmptyAsync(stage.Generation);
        Assert.IsFalse(await state.IsAuthoritativelyEmptyAsync(stage.Generation));

        // A later terminal delta may advance the cursor, but cannot republish the
        // retained pre-empty snapshot as sale-safe without a new verified full.
        await state.StoreLastSyncAsync(stage.ShopId, stage.ShopCode,
            "delta-cursor-after-empty", "2026-11-07T01:01:00Z", stage.Fence.ExpectedEpoch,
            "delta", "previous-full-cursor", "full_refresh", stage.Generation);
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            state.StoreSaleSafeAsync(stage.ShopId, stage.ShopCode, "2026-11-07T01:01:00Z",
                stage.Fence.ExpectedEpoch, "delta-cursor-after-empty", "delta", generation: stage.Generation));
        StringAssert.Contains(exception.Message, "catalog_full_refresh_required_after_empty");
        Assert.IsFalse(await new CatalogShopStateRepository(db.Factory).IsSaleSafeForOfficialShopAsync());
        using var conn = db.Factory.Open();
        Assert.AreEqual(2L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM products WHERE is_active = 1;"));
    }

    [TestMethod]
    public async Task AuthoritativeEmpty_ManualExactnessAndFullCursorApprovalCannotClearFence()
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);
        var stage = await StageEmptySnapshotAsync(db, "empty-manual-approval");
        Assert.AreEqual(string.Empty, await ConfirmEmptySnapshotAsync(db, stage));
        var state = new CatalogShopStateRepository(db.Factory);
        var audit = await new CatalogFullRefreshReconciler(db.Factory).AuditCurrentAsync();
        var verified = CatalogExactnessVerifier.Evaluate(Summary(2, 1, 1, 0), audit, CompleteContext(2, 1, 1, 0));
        Assert.AreEqual(CatalogCompletenessStatus.Verified, verified.Status);

        await state.StoreExactnessAsync(stage.ShopId, stage.ShopCode, verified,
            stage.Fence.ExpectedEpoch, generation: stage.Generation);
        Assert.IsTrue(await state.RequiresFullRefreshAfterEmptyAsync());
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            state.StorePullCursorAsync(stage.ShopId, stage.ShopCode, "manually-approved-full-cursor",
                "2026-11-07T01:02:00Z", stage.Fence.ExpectedEpoch, "full_refresh",
                authoritativeSnapshotCommitted: true, generation: stage.Generation));
        StringAssert.Contains(exception.Message, CatalogShopStateRepository.FullRefreshRequiredAfterEmptyCode);
        await state.StoreLastSyncAsync(stage.ShopId, stage.ShopCode, "generic-full-label",
            "2026-11-07T01:03:00Z", stage.Fence.ExpectedEpoch, "full_refresh", generation: stage.Generation);
        Assert.IsTrue(await new CatalogShopStateRepository(db.Factory).RequiresFullRefreshAfterEmptyAsync());
        Assert.IsFalse(await state.IsSaleSafeForOfficialShopAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AuthoritativeEmpty_VerifiedNonemptyAtomicFullClearsFenceOnlyOnCommit(bool rejectFinalization)
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);
        var stage = await StageEmptySnapshotAsync(db, "empty-full-commit");
        Assert.AreEqual(string.Empty, await ConfirmEmptySnapshotAsync(db, stage));
        var state = new CatalogShopStateRepository(db.Factory);
        await state.RequestFullRepairAsync(stage.ShopId, stage.ShopCode, stage.Fence.ExpectedEpoch, stage.Generation);
        Assert.IsTrue(await state.RequiresFullRefreshAfterEmptyAsync());
        var binding = await state.EnsureAndLoadCursorAsync(stage.ShopId, stage.ShopCode, stage.Generation);
        stage.Fence.ExpectedEpoch = binding.Epoch;
        stage.Fence.ExpectedPreviousCursor = binding.Cursor;
        stage.Fence.ExpectedPreviousMode = binding.Mode;
        var fullRunId = "nonempty-full-replacement";
        var batch = StageBatch(fullRunId, 1, false, "product-after-empty", "AFTER-EMPTY", "price-after-empty");
        var repository = new RemoteCatalogBatchRepository(db.Factory);
        await repository.StageAuthoritativePageAsync(batch, CancellationToken.None, stage.Fence);
        using (var run = repository.CreateRunContext())
        {
            await run.BeginAtomicFullRefreshAsync();
            batch.ReuseValidatedAuthoritativeStagePage = true;
            await run.ApplyAsync(batch, CancellationToken.None, stage.Fence);
            var exactness = await new CatalogFullRefreshReconciler(db.Factory).ReconcileAndVerifyWithinAtomicApplyAsync(
                run, fullRunId, "2026-11-07T01:04:00Z", Summary(1, 1, 1, 1), CompleteContext(1, 1, 1, 1), stage.Fence);
            Assert.AreEqual(CatalogCompletenessStatus.Verified, exactness.Status);
            Func<Task> commit = () => run.CommitAtomicFullRefreshAsync((connection, transaction) =>
                state.FinalizeVerifiedFullRefreshWithinTransactionAsync(connection, transaction,
                    stage.ShopId, stage.ShopCode, exactness, "full-after-empty", "2026-11-07T01:04:00Z",
                    binding.Epoch, binding.Cursor, binding.Mode, "catalog-version-1",
                    rejectFinalization ? 1 : 0, stage.Generation));
            if (rejectFinalization)
            {
                // This check occurs after the finalizer deletes the fence and
                // writes sale-safe, proving its later failure rolls both back.
                var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(commit);
                StringAssert.Contains(exception.Message, "catalog_import_ack_generation_invalid");
            }
            else
            {
                await commit();
            }
        }

        state = new CatalogShopStateRepository(db.Factory);
        Assert.AreEqual(rejectFinalization, await state.RequiresFullRefreshAfterEmptyAsync());
        Assert.AreEqual(!rejectFinalization, await state.IsSaleSafeForOfficialShopAsync());
        using var verify = db.Factory.Open();
        Assert.AreEqual(rejectFinalization ? 0L : 1L, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM products WHERE remote_product_id = 'product-after-empty' AND is_active = 1;"));
        Assert.AreEqual(rejectFinalization ? 2L : 0L, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM products WHERE remote_product_id IN ('product-1', 'product-2') AND is_active = 1;"));
    }

    [TestMethod]
    public async Task AuthoritativeEmpty_FinalizerRejectsCanonicalZeroAndRetainsFence()
    {
        using var db = TestDb.Create();
        var stage = await StageEmptySnapshotAsync(db, "empty-finalizer-zero");
        Assert.AreEqual(string.Empty, await ConfirmEmptySnapshotAsync(db, stage));
        var state = new CatalogShopStateRepository(db.Factory);
        var audit = await new CatalogFullRefreshReconciler(db.Factory).AuditCurrentAsync();
        var zero = CatalogExactnessVerifier.Evaluate(Summary(0, 0, 0, 0), audit, CompleteContext(0, 0, 0, 0));
        Assert.AreEqual(CatalogCompletenessStatus.Verified, zero.Status);
        using (var conn = db.Factory.Open())
        using (var tx = conn.BeginTransaction())
        {
            var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                state.FinalizeVerifiedFullRefreshWithinTransactionAsync(conn, tx, stage.ShopId, stage.ShopCode,
                    zero, "not-a-nonempty-full", "2026-11-07T01:05:00Z", stage.Fence.ExpectedEpoch,
                    stage.Fence.ExpectedPreviousCursor, stage.Fence.ExpectedPreviousMode, "catalog-version-1", 0, stage.Generation));
            StringAssert.Contains(exception.Message, "catalog_exactness_not_verified");
        }
        Assert.IsTrue(await state.RequiresFullRefreshAfterEmptyAsync());
        Assert.IsFalse(await state.IsSaleSafeForOfficialShopAsync());
    }

    [TestMethod]
    public async Task AuthoritativeEmpty_FenceSurvivesGenerationRestoreAndMalformedValue()
    {
        using var db = TestDb.Create();
        var stage = await StageEmptySnapshotAsync(db, "empty-fence-lifetime");
        Assert.AreEqual(string.Empty, await ConfirmEmptySnapshotAsync(db, stage));
        var state = new CatalogShopStateRepository(db.Factory);
        await new OnlineSyncGenerationRepository(db.Factory).StopIfCurrentAsync(stage.Generation, "restore_review", 2);
        await state.ResetForRestoreReviewAsync(stage.ShopId, stage.ShopCode);
        var replacement = new OnlineSyncGeneration("replacement-generation", "replacement-session", "replacement-device", stage.ShopId, stage.ShopCode);
        await new OnlineSyncGenerationRepository(db.Factory).ActivateAndRecoverAsync(replacement, 3);
        Assert.IsFalse(await state.IsAuthoritativelyEmptyAsync(replacement));
        Assert.IsTrue(await new CatalogShopStateRepository(db.Factory).RequiresFullRefreshAfterEmptyAsync());
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync("UPDATE app_settings SET value = '' WHERE key = @key;",
                new { key = CatalogShopStateRepository.FullRefreshRequiredAfterEmptyKey });
        }
        Assert.IsTrue(await state.RequiresFullRefreshAfterEmptyAsync());
        Assert.AreEqual(CatalogSaleSafetyPolicy.RepairRequiredCode,
            (await state.EvaluateSaleSafetyForOfficialShopAsync()).ReasonCode);
    }

    [TestMethod]
    public async Task AuthoritativeEmpty_IncompleteNewAttemptPreservesPublishedObservation()
    {
        using var db = TestDb.Create();
        var stage = await StageEmptySnapshotAsync(db, "empty-retry");
        Assert.AreEqual(string.Empty, await ConfirmEmptySnapshotAsync(db, stage));
        var incomplete = CompleteContext(0, 0, 0, 0);
        incomplete.Pages = 2;

        var code = await new CatalogFullRefreshReconciler(db.Factory).ConfirmAuthoritativeEmptyAsync(
            stage.FullRunId, Summary(0, 0, 0, 0), incomplete, stage.Fence);

        Assert.AreEqual("catalog_authoritative_stage_incomplete", code);
        Assert.IsTrue(await new CatalogShopStateRepository(db.Factory).IsAuthoritativelyEmptyAsync(stage.Generation));
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

    [TestMethod]
    public async Task PublishedEmpty_ReadDoesNotJoinNetworkBarrier_AndRejectionDoesNotUnpublish()
    {
        using var db=TestDb.Create();var stage=await StageEmptySnapshotAsync(db,"published-read");
        Assert.AreEqual(string.Empty,await ConfirmEmptySnapshotAsync(db,stage));
        var state=new CatalogShopStateRepository(db.Factory);
        Task<PosPublishedCatalogState> read;bool completed;
        using(await new CatalogShopTransitionBarrier(db.Factory).EnterAsync())
        {
            read=Task.Run(()=>state.ReadPublishedSnapshotAsync(stage.Generation));
            completed=await Task.WhenAny(read,Task.Delay(250))==read;
        }
        var published=await read.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(completed,"Published reads must not wait for the network transition lease.");
        Assert.IsTrue(published.AuthoritativelyEmpty);Assert.IsFalse(published.RemoteSaleSafe);
        await state.StoreAttemptExactnessAsync(stage.ShopId,stage.ShopCode,new CatalogExactnessResult
        {Code="synthetic_candidate_rejected",Status=CatalogCompletenessStatus.Mismatch,RepairRequired=true},
            stage.Fence.ExpectedEpoch,stage.Fence.ExpectedPreviousCursor,stage.Fence.ExpectedPreviousMode,stage.Generation);
        published=await state.ReadPublishedSnapshotAsync(stage.Generation);
        Assert.IsTrue(published.AuthoritativelyEmpty);Assert.IsFalse(published.RemoteSaleSafe);
        Assert.IsFalse(published.RecoveryRequired);
    }

    [TestMethod]
    public async Task PublishedNonempty_CandidateFailureDoesNotInvalidateOperationalSnapshot()
    {
        using var db = TestDb.Create();
        await SeedCleanCatalogAsync(db);
        var stage = await CreateAuthoritativeStageAsync(db, "candidate-rejection");
        var state = new CatalogShopStateRepository(db.Factory);
        await CatalogExactnessTestFixture.SeedVerifiedAsync(db.Factory, stage.ShopId, stage.ShopCode);
        await state.StoreSaleSafeAsync(stage.ShopId, stage.ShopCode, "2026-11-07T01:00:00Z",
            stage.Fence.ExpectedEpoch, generation: stage.Generation);
        Assert.IsTrue((await state.ReadPublishedSnapshotAsync(stage.Generation)).RemoteSaleSafe);
        Task<PosPublishedCatalogState> pending;
        bool readBeforeWriterCompleted;
        using (var writer = db.Factory.Open())
        using (var transaction = writer.BeginTransaction(deferred: false))
        {
            await writer.ExecuteAsync("UPDATE products SET name=name || '-candidate';", transaction: transaction);
            pending = Task.Run(() => state.ReadPublishedSnapshotAsync(stage.Generation));
            readBeforeWriterCompleted = await Task.WhenAny(pending, Task.Delay(250)) == pending;
            transaction.Rollback();
        }
        Assert.IsTrue((await pending.WaitAsync(TimeSpan.FromSeconds(3))).RemoteSaleSafe);
        Assert.IsTrue(readBeforeWriterCompleted, "Published readers must not acquire a writer reservation.");
        await state.StoreAttemptExactnessAsync(stage.ShopId, stage.ShopCode, new CatalogExactnessResult
        { Code = "candidate_count_mismatch", Status = CatalogCompletenessStatus.Mismatch, RepairRequired = true },
            stage.Fence.ExpectedEpoch, stage.Fence.ExpectedPreviousCursor, stage.Fence.ExpectedPreviousMode, stage.Generation);
        var published = await state.ReadPublishedSnapshotAsync(stage.Generation);
        Assert.IsTrue(published.RemoteSaleSafe);
        Assert.IsFalse(published.AuthoritativelyEmpty);
        Assert.IsFalse(published.RecoveryRequired);
        Assert.AreEqual(CatalogCompletenessStatus.Verified, (await state.LoadExactnessAsync()).Status);
    }

    private static Task<string> ConfirmEmptySnapshotAsync(TestDb db, AuthoritativeStageContext stage)
    {
        return new CatalogFullRefreshReconciler(db.Factory).ConfirmAuthoritativeEmptyAsync(
            stage.FullRunId, Summary(0, 0, 0, 0), CompleteContext(0, 0, 0, 0), stage.Fence);
    }
}
