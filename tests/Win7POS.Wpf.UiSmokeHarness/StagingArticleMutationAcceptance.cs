using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dapper;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos.Dialogs;
using Win7POS.Wpf.Pos.Online;
using Win7POS.Wpf.Products;

namespace Win7POS.Wpf.UiSmokeHarness
{
    /// <summary>
    /// Test-only, bounded public-staging scenario. It mutates only products
    /// created by this run and emits no request payloads or trusted secrets.
    /// </summary>
    internal static class StagingArticleMutationAcceptance
    {
        internal static async Task<StagingArticleMutationAcceptanceResult> RunAsync(
            SqliteConnectionFactory factory,
            PosOnlineSyncSupervisorHost initialHost,
            PosTrustedDeviceSession trustedSession,
            Uri baseUri,
            string runId,
            string outputDirectory,
            CancellationToken cancellationToken,
            bool resumeAfterRestart)
        {
            var result = new StagingArticleMutationAcceptanceResult
            {
                Code = "article_acceptance_not_started",
                HardwareActions = 0,
                StartedAtUtc = UtcNow()
            };
            PosOnlineSyncSupervisorHost activeHost = null;
            var localProductIds = new List<long>();
            var directMutationIds = new List<string>();
            var conflictReceiptMutationIds = new List<string>();
            try
            {
                Require(factory != null, "article_factory_missing");
                Require(initialHost != null, "article_initial_host_missing");
                Require(trustedSession != null, "article_trusted_session_missing");
                Require(baseUri != null, "article_base_uri_missing");
                Require(IsSafeRunId(runId), "article_run_id_invalid");
                Directory.CreateDirectory(outputDirectory);

                var workflow = ProductsWorkflowService.CreateDefault();
                var category = await GetVerifiedReferenceAsync(
                        factory,
                        "categories",
                        "remote_category_id")
                    .ConfigureAwait(true);
                var supplier = await GetVerifiedReferenceAsync(
                        factory,
                        "suppliers",
                        "remote_supplier_id")
                    .ConfigureAwait(true);
                Require(category != null, "verified_category_missing");
                Require(supplier != null, "verified_supplier_missing");

                var barcodeA0 = runId + "-A0";
                var barcodeA1 = runId + "-A1";
                var checkpointPath = Path.Combine(
                    outputDirectory,
                    "article-restart-checkpoint.json");
                ProductDetailsRow created;
                if (!resumeAfterRestart)
                {
                    // The stopped host is the network gate for the offline
                    // section. This process exits after persisting the
                    // checkpoint; the wrapper starts a fresh process to
                    // continue.
                    await initialHost.StopAsync().ConfigureAwait(false);

                    var create = await CreateViewModelAsync(
                            ProductEditMode.New,
                            null,
                            workflow)
                        .ConfigureAwait(true);
                    Populate(
                        create,
                        barcodeA0,
                        runId + " ARTICLE A",
                        1100,
                        500,
                        0,
                        runId + "-ITEM-A0");
                    await SubmitAsync(create).ConfigureAwait(true);

                    created = await workflow
                        .GetByBarcodeDetailsAsync(barcodeA0)
                        .ConfigureAwait(true);
                    Require(
                        created != null,
                        "offline_create_local_row_missing");
                    localProductIds.Add(created.Id);
                    var offlineCreateSnapshot =
                        await ReadOfflineCreateAtomicSnapshotAsync(
                                factory,
                                created.Id)
                            .ConfigureAwait(false);
                    Require(
                        offlineCreateSnapshot != null &&
                        offlineCreateSnapshot.ProductRows == 1 &&
                        offlineCreateSnapshot.CreateOutboxRows == 1 &&
                        !string.IsNullOrWhiteSpace(
                            offlineCreateSnapshot.ProductClientProductId) &&
                        string.Equals(
                            offlineCreateSnapshot.ProductClientProductId,
                            offlineCreateSnapshot.CreateClientProductId,
                            StringComparison.Ordinal),
                        "offline_create_not_atomic");
                    result.OfflineCreateAtomic = true;

                    var dependent = await CreateViewModelAsync(
                            ProductEditMode.Edit,
                            created,
                            workflow)
                        .ConfigureAwait(true);
                    Populate(
                        dependent,
                        barcodeA1,
                        runId + " ARTICLE A EDITED",
                        1100,
                        500,
                        0,
                        runId + "-ITEM-A1");
                    dependent.Name2 = runId + " SECONDARY A";
                    dependent.SelectedCategory =
                        dependent.Categories.First(
                            item => item.Id == category.Id);
                    dependent.SelectedSupplier =
                        dependent.Suppliers.First(
                            item => item.Id == supplier.Id);
                    await SubmitAsync(dependent).ConfigureAwait(true);
                    Require(
                        await CountAsync(
                            factory,
                            @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE local_product_id = @id
  AND state = 'waiting_dependency';",
                            new { id = created.Id }).ConfigureAwait(false) == 1,
                        "dependent_edit_not_waiting");
                    result.DependentEditPersisted = true;

                    await CaptureOfflineQueueUiAsync(
                            factory,
                            outputDirectory)
                        .ConfigureAwait(true);
                    WriteJson(
                        checkpointPath,
                        new ArticleRestartCheckpoint
                        {
                            Barcode = barcodeA1,
                            ClientProductId =
                                offlineCreateSnapshot.ProductClientProductId,
                            LocalProductId = created.Id,
                            RunId = runId
                        });
                    result.RestartRequired = true;
                    result.Code = "article_restart_required";
                    return result;
                }

                var checkpoint = ReadJson<ArticleRestartCheckpoint>(
                    checkpointPath);
                Require(
                    checkpoint != null &&
                    string.Equals(
                        checkpoint.RunId,
                        runId,
                        StringComparison.Ordinal) &&
                    checkpoint.LocalProductId > 0 &&
                    !string.IsNullOrWhiteSpace(
                        checkpoint.ClientProductId) &&
                    string.Equals(
                        checkpoint.Barcode,
                        barcodeA1,
                        StringComparison.Ordinal),
                    "article_restart_checkpoint_invalid");
                created = await workflow.GetDetailsByIdAsync(
                        checkpoint.LocalProductId)
                    .ConfigureAwait(true);
                Require(
                    created != null,
                    "restart_local_product_missing");
                localProductIds.Add(created.Id);
                var restartCreateSnapshot =
                    await ReadOfflineCreateAtomicSnapshotAsync(
                            factory,
                            created.Id)
                        .ConfigureAwait(false);
                Require(
                    restartCreateSnapshot != null &&
                    restartCreateSnapshot.ProductRows == 1 &&
                    restartCreateSnapshot.CreateOutboxRows == 1 &&
                    string.Equals(
                        restartCreateSnapshot.ProductClientProductId,
                        restartCreateSnapshot.CreateClientProductId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        restartCreateSnapshot.ProductClientProductId,
                        checkpoint.ClientProductId,
                        StringComparison.Ordinal) &&
                    await CountAsync(
                            factory,
                            @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE local_product_id = @id
  AND state IN ('pending', 'waiting_dependency');",
                            new { id = created.Id }).ConfigureAwait(false) == 2,
                    "restart_did_not_preserve_dependencies");
                result.OfflineCreateAtomic = true;
                result.DependentEditPersisted = true;
                result.HarnessRestartSurvived = true;
                activeHost = initialHost;
                var articleAReference =
                    await ReadProductReferenceAsync(
                            factory,
                            created.Id)
                        .ConfigureAwait(false);
                Require(
                    articleAReference != null &&
                    !string.IsNullOrWhiteSpace(
                        articleAReference.CategoryRemoteId) &&
                    !string.IsNullOrWhiteSpace(
                        articleAReference.SupplierRemoteId),
                    "restart_product_references_missing");

                await DrainArticlesAsync(
                        factory,
                        activeHost,
                        cancellationToken,
                        allowBlocked: false)
                    .ConfigureAwait(true);
                var identityA = await ReadIdentityAsync(factory, created.Id)
                    .ConfigureAwait(false);
                Require(
                    identityA != null &&
                    string.Equals(
                        identityA.ClientProductId,
                        checkpoint.ClientProductId,
                        StringComparison.Ordinal) &&
                    Guid.TryParse(identityA.RemoteProductId, out _) &&
                    PosArticleMutationIntentPolicy.IsProductRevision(
                        identityA.RemoteBaseRevision),
                    "create_ack_remote_identity_missing");
                result.RemoteIdentityAssigned = true;
                result.DependentSequenceApplied =
                    await CountAsync(
                        factory,
                        @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE local_product_id = @id
  AND state = 'completed'
  AND mutation_kind IN ('product_create', 'product_update');",
                        new { id = created.Id }).ConfigureAwait(false) == 2;
                Require(
                    result.DependentSequenceApplied,
                    "dependent_sequence_not_completed");

                await PullCanonicalAsync(
                        factory,
                        activeHost,
                        trustedSession,
                        baseUri,
                        cancellationToken)
                    .ConfigureAwait(false);
                await RequireCanonicalProductAsync(
                        factory,
                        created.Id,
                        barcodeA1,
                        runId + " ARTICLE A EDITED",
                        runId + " SECONDARY A",
                        runId + "-ITEM-A1",
                        articleAReference.CategoryRemoteId,
                        articleAReference.SupplierRemoteId,
                        1100,
                        500,
                        0,
                        true)
                    .ConfigureAwait(false);
                result.DependentCanonicalReadback = true;

                var articleA = await workflow.GetByBarcodeDetailsAsync(barcodeA1)
                    .ConfigureAwait(true);
                Require(articleA != null, "article_a_readback_missing");
                var plusFive = await CreateViewModelAsync(
                        ProductEditMode.Edit,
                        articleA,
                        workflow)
                    .ConfigureAwait(true);
                Populate(
                    plusFive,
                    articleA.Barcode,
                    articleA.Name,
                    1500,
                    650,
                    5,
                    articleA.ArticleCode);
                plusFive.Name2 = articleA.Name2;
                plusFive.SelectedStockReason = plusFive.StockReasons.First(
                    item => item.Code == "count_correction");
                await SubmitAsync(plusFive).ConfigureAwait(true);
                await DrainArticlesAsync(
                        factory,
                        activeHost,
                        cancellationToken,
                        allowBlocked: false)
                    .ConfigureAwait(true);
                result.RetailPrice = true;
                result.PurchasePrice = true;
                result.StockPlusFive = true;

                articleA = await workflow.GetByBarcodeDetailsAsync(barcodeA1)
                    .ConfigureAwait(true);
                var minusTwo = await CreateViewModelAsync(
                        ProductEditMode.Edit,
                        articleA,
                        workflow)
                    .ConfigureAwait(true);
                Populate(
                    minusTwo,
                    articleA.Barcode,
                    articleA.Name,
                    checked((int)articleA.UnitPrice),
                    articleA.PurchasePrice,
                    3,
                    articleA.ArticleCode);
                minusTwo.Name2 = articleA.Name2;
                minusTwo.SelectedStockReason = minusTwo.StockReasons.First(
                    item => item.Code == "damage");
                await SubmitAsync(minusTwo).ConfigureAwait(true);
                await DrainArticlesAsync(
                        factory,
                        activeHost,
                        cancellationToken,
                        allowBlocked: false)
                    .ConfigureAwait(true);
                result.StockMinusTwo = true;

                var barcodeDuplicate = runId + "-D";
                articleA = await workflow.GetByBarcodeDetailsAsync(barcodeA1)
                    .ConfigureAwait(true);
                var duplicate = await CreateViewModelAsync(
                        ProductEditMode.Duplicate,
                        articleA,
                        workflow)
                    .ConfigureAwait(true);
                Populate(
                    duplicate,
                    barcodeDuplicate,
                    runId + " DUPLICATE",
                    1200,
                    550,
                    0,
                    runId + "-ITEM-D");
                duplicate.SelectedCategory = duplicate.Categories.First(
                    item => item.Id == category.Id);
                duplicate.SelectedSupplier = duplicate.Suppliers.First(
                    item => item.Id == supplier.Id);
                await SubmitAsync(duplicate).ConfigureAwait(true);
                var duplicateRow = await workflow
                    .GetByBarcodeDetailsAsync(barcodeDuplicate)
                    .ConfigureAwait(true);
                Require(duplicateRow != null, "duplicate_local_row_missing");
                localProductIds.Add(duplicateRow.Id);
                await DrainArticlesAsync(
                        factory,
                        activeHost,
                        cancellationToken,
                        allowBlocked: false)
                    .ConfigureAwait(true);
                var duplicateIdentity = await ReadIdentityAsync(
                        factory,
                        duplicateRow.Id)
                    .ConfigureAwait(false);
                Require(
                    duplicateIdentity != null &&
                    Guid.TryParse(
                        duplicateIdentity.RemoteProductId,
                        out _) &&
                    !string.Equals(
                        duplicateIdentity.RemoteProductId,
                        identityA.RemoteProductId,
                        StringComparison.Ordinal) &&
                    !string.Equals(
                        duplicateIdentity.ClientProductId,
                        identityA.ClientProductId,
                        StringComparison.Ordinal),
                    "duplicate_remote_identity_missing");
                result.DuplicateIdentityIndependent = true;
                result.DuplicateProduct = true;

                await workflow.SetProductActiveAsync(duplicateRow.Id, false)
                    .ConfigureAwait(true);
                await DrainArticlesAsync(
                        factory,
                        activeHost,
                        cancellationToken,
                        allowBlocked: false)
                    .ConfigureAwait(true);
                await PullCanonicalAsync(
                        factory,
                        activeHost,
                        trustedSession,
                        baseUri,
                        cancellationToken)
                    .ConfigureAwait(false);
                await RequireCanonicalProductAsync(
                        factory,
                        duplicateRow.Id,
                        barcodeDuplicate,
                        runId + " DUPLICATE",
                        string.Empty,
                        runId + "-ITEM-D",
                        category.RemoteId,
                        supplier.RemoteId,
                        1200,
                        550,
                        0,
                        false)
                    .ConfigureAwait(false);
                await workflow.SetProductActiveAsync(duplicateRow.Id, true)
                    .ConfigureAwait(true);
                await DrainArticlesAsync(
                        factory,
                        activeHost,
                        cancellationToken,
                        allowBlocked: false)
                    .ConfigureAwait(true);
                await PullCanonicalAsync(
                        factory,
                        activeHost,
                        trustedSession,
                        baseUri,
                        cancellationToken)
                    .ConfigureAwait(false);
                await RequireCanonicalProductAsync(
                        factory,
                        duplicateRow.Id,
                        barcodeDuplicate,
                        runId + " DUPLICATE",
                        string.Empty,
                        runId + "-ITEM-D",
                        category.RemoteId,
                        supplier.RemoteId,
                        1200,
                        550,
                        0,
                        true)
                    .ConfigureAwait(false);
                result.LifecycleCanonicalReadback = true;
                result.DeactivateReactivate = true;

                var replaySource = await ReadReplaySourceAsync(factory, created.Id)
                    .ConfigureAwait(false);
                Require(replaySource != null, "replay_source_missing");
                var originalIntent = Rehydrate(replaySource.CanonicalPayloadJson);
                var replayAttempt = RunScopedId(runId, "replay-attempt-2");
                var replayRequest = new PosArticleMutationRequest
                {
                    AttemptToken = replayAttempt,
                    Intent = originalIntent,
                    PayloadHash = replaySource.PayloadHash
                };
                var replayResponse = await SendDirectAsync(
                        baseUri,
                        trustedSession,
                        replayRequest,
                        cancellationToken)
                    .ConfigureAwait(false);
                var replayResult = RequireSingleResult(
                    replayResponse,
                    "same_mutation_replay_transport_failed");
                Require(
                    string.Equals(
                        replayResult.DeliveryStatus,
                        PosArticleMutationStatusPolicy.DuplicateReplay,
                        StringComparison.Ordinal) &&
                    ReplayAckMatches(replayResult.Ack, replaySource),
                    "same_mutation_replay_not_observed");
                result.ReplayAckPreserved = true;
                result.SameMutationReplay = true;

                var mismatchedChanges = originalIntent.Changes.ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.Ordinal);
                mismatchedChanges[PosArticleMutationFields.PrimaryName] =
                    runId + " PAYLOAD MISMATCH";
                var mismatchedIntent = PosArticleMutationIntentPolicy.Rehydrate(
                    originalIntent.BaseRevision,
                    mismatchedChanges,
                    originalIntent.ClientProductId,
                    originalIntent.CreatedAt,
                    originalIntent.FieldMask,
                    originalIntent.IdempotencyKey,
                    originalIntent.LocalSequence,
                    originalIntent.MutationId,
                    originalIntent.MutationKind,
                    originalIntent.OccurredAt,
                    originalIntent.RemoteProductId);
                var mismatchRequest = new PosArticleMutationRequest
                {
                    AttemptToken = RunScopedId(
                        runId,
                        "mismatch-attempt-3"),
                    Intent = mismatchedIntent,
                    PayloadHash = PosArticleMutationPayloadHash.Compute(
                        mismatchedIntent)
                };
                var mismatchResponse = await SendDirectAsync(
                        baseUri,
                        trustedSession,
                        mismatchRequest,
                        cancellationToken)
                    .ConfigureAwait(false);
                var mismatchResult = RequireSingleResult(
                    mismatchResponse,
                    "payload_mismatch_transport_failed");
                Require(
                    string.Equals(
                        mismatchResult.DeliveryStatus,
                        PosArticleMutationStatusPolicy
                            .IdempotencyPayloadMismatch,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        mismatchResult.Ack?.Code,
                        PosArticleMutationStatusPolicy
                            .IdempotencyPayloadMismatch,
                        StringComparison.Ordinal),
                    "different_payload_mismatch_not_observed");
                result.DifferentPayloadMismatch = true;
                conflictReceiptMutationIds.Add(originalIntent.MutationId);

                identityA = await ReadIdentityAsync(factory, created.Id)
                    .ConfigureAwait(false);
                var staleMutationId = RunScopedId(runId, "remote-stale");
                var staleIntent = PosArticleMutationIntentPolicy.Create(
                    identityA.RemoteBaseRevision,
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        [PosArticleMutationFields.PrimaryName] =
                            runId + " REMOTE STALE BASE"
                    },
                    identityA.ClientProductId,
                    DateTimeOffset.UtcNow,
                    new[] { PosArticleMutationFields.PrimaryName },
                    RunScopedId(runId, "remote-stale-idem"),
                    identityA.MaximumLocalSequence + 1,
                    staleMutationId,
                    PosArticleMutationKinds.ProductUpdate,
                    DateTimeOffset.UtcNow,
                    identityA.RemoteProductId);
                directMutationIds.Add(staleMutationId);
                var staleAdvanceResponse = await SendDirectAsync(
                        baseUri,
                        trustedSession,
                        new PosArticleMutationRequest
                        {
                            AttemptToken = RunScopedId(
                                runId,
                                "remote-stale-attempt"),
                            Intent = staleIntent,
                            PayloadHash =
                                PosArticleMutationPayloadHash.Compute(staleIntent)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                var staleAdvanceResult = RequireSingleResult(
                    staleAdvanceResponse,
                    "remote_stale_advance_transport_failed");
                Require(
                    string.Equals(
                        staleAdvanceResult.DeliveryStatus,
                        PosArticleMutationStatusPolicy.Applied,
                        StringComparison.Ordinal),
                    "remote_stale_advance_not_applied");
                await RecordDirectProbeAsync(
                        factory,
                        created.Id,
                        new PosArticleMutationRequest
                        {
                            AttemptToken = staleAdvanceResult.Ack.AttemptToken,
                            Intent = staleIntent,
                            PayloadHash =
                                PosArticleMutationPayloadHash.Compute(staleIntent)
                        },
                        staleAdvanceResult)
                    .ConfigureAwait(false);

                articleA = await workflow.GetByBarcodeDetailsAsync(barcodeA1)
                    .ConfigureAwait(true);
                var conflict = await CreateViewModelAsync(
                        ProductEditMode.Edit,
                        articleA,
                        workflow)
                    .ConfigureAwait(true);
                Populate(
                    conflict,
                    articleA.Barcode,
                    runId + " LOCAL CONFLICT",
                    checked((int)articleA.UnitPrice),
                    articleA.PurchasePrice,
                    articleA.StockQty,
                    articleA.ArticleCode);
                conflict.Name2 = articleA.Name2;
                await SubmitAsync(conflict).ConfigureAwait(true);

                var barcodeB = runId + "-B";
                var unrelated = await CreateViewModelAsync(
                        ProductEditMode.New,
                        null,
                        workflow)
                    .ConfigureAwait(true);
                Populate(
                    unrelated,
                    barcodeB,
                    runId + " ARTICLE B",
                    900,
                    400,
                    0,
                    runId + "-ITEM-B");
                await SubmitAsync(unrelated).ConfigureAwait(true);
                var unrelatedRow = await workflow
                    .GetByBarcodeDetailsAsync(barcodeB)
                    .ConfigureAwait(true);
                Require(unrelatedRow != null, "unrelated_local_row_missing");
                localProductIds.Add(unrelatedRow.Id);

                await DrainArticlesAsync(
                        factory,
                        activeHost,
                        cancellationToken,
                        allowBlocked: true)
                    .ConfigureAwait(true);
                result.StaleConflict =
                    await CountAsync(
                        factory,
                        @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE local_product_id = @id
  AND state = 'failed_blocked'
  AND last_typed_code = 'failed_conflict';",
                        new { id = created.Id }).ConfigureAwait(false) == 1;
                result.UnrelatedProductContinued =
                    Guid.TryParse(
                        (await ReadIdentityAsync(factory, unrelatedRow.Id)
                            .ConfigureAwait(false))?.RemoteProductId,
                        out _);
                Require(result.StaleConflict, "stale_conflict_not_blocked");
                Require(
                    result.UnrelatedProductContinued,
                    "blocked_conflict_starved_unrelated_product");

                var uiVerification = await CaptureArticleUiAsync(
                        factory,
                        workflow,
                        created.Id,
                        outputDirectory)
                    .ConfigureAwait(true);
                result.UiLanguagesVerified =
                    uiVerification.LanguagesVerified;
                result.UiKeyboardNavigationVerified =
                    uiVerification.KeyboardNavigationVerified;
                result.UiControlsUnclipped =
                    uiVerification.ControlsUnclipped;
                result.UiResponsive = uiVerification.Responsive;
                result.UiConflictNonModal =
                    uiVerification.ConflictNonModal;
                Require(
                    result.UiLanguagesVerified &&
                    result.UiKeyboardNavigationVerified &&
                    result.UiControlsUnclipped &&
                    result.UiResponsive &&
                    result.UiConflictNonModal,
                    "article_ui_verification_failed");

                var outboxBeforePull = await CountAsync(
                        factory,
                        "SELECT COUNT(1) FROM article_mutation_outbox;")
                    .ConfigureAwait(false);
                await PullCanonicalAsync(
                        factory,
                        activeHost,
                        trustedSession,
                        baseUri,
                        cancellationToken)
                    .ConfigureAwait(false);
                var outboxAfterPull = await CountAsync(
                        factory,
                        "SELECT COUNT(1) FROM article_mutation_outbox;")
                    .ConfigureAwait(false);
                result.CanonicalPull = true;
                result.ZeroEcho = outboxBeforePull == outboxAfterPull;
                Require(result.ZeroEcho, "canonical_pull_created_echo");

                articleA = await workflow.GetByBarcodeDetailsAsync(barcodeA1)
                    .ConfigureAwait(true);
                var correction = await CreateViewModelAsync(
                        ProductEditMode.Edit,
                        articleA,
                        workflow)
                    .ConfigureAwait(true);
                Populate(
                    correction,
                    articleA.Barcode,
                    runId + " CONFLICT RESOLVED",
                    checked((int)articleA.UnitPrice),
                    articleA.PurchasePrice,
                    articleA.StockQty,
                    articleA.ArticleCode);
                correction.Name2 = articleA.Name2;
                await SubmitAsync(correction).ConfigureAwait(true);
                await DrainArticlesAsync(
                        factory,
                        activeHost,
                        cancellationToken,
                        allowBlocked: false)
                    .ConfigureAwait(true);
                Require(
                    await CountAsync(
                        factory,
                        @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE local_product_id = @id
  AND state = 'completed'
  AND last_typed_code = 'failed_conflict'
  AND resolution_code = 'superseded_by_correction'
  AND superseded_by_mutation_id IS NOT NULL
  AND EXISTS (
    SELECT 1
    FROM article_mutation_outbox correction
    WHERE correction.mutation_id =
            article_mutation_outbox.superseded_by_mutation_id
      AND correction.state = 'completed'
      AND correction.ack_status = 'applied'
      AND correction.ack_terminal = 1
  );",
                        new { id = created.Id }).ConfigureAwait(false) == 1,
                    "intentional_conflict_not_superseded");
                result.ConflictResolved = true;

                var outboxBeforeFinalPull = await CountAsync(
                        factory,
                        "SELECT COUNT(1) FROM article_mutation_outbox;")
                    .ConfigureAwait(false);
                await PullCanonicalAsync(
                        factory,
                        activeHost,
                        trustedSession,
                        baseUri,
                        cancellationToken)
                    .ConfigureAwait(false);
                var outboxAfterFinalPull = await CountAsync(
                        factory,
                        "SELECT COUNT(1) FROM article_mutation_outbox;")
                    .ConfigureAwait(false);
                result.ZeroEcho =
                    result.ZeroEcho &&
                    outboxBeforeFinalPull == outboxAfterFinalPull;
                Require(result.ZeroEcho, "final_canonical_pull_created_echo");
                await RequireCanonicalProductAsync(
                        factory,
                        created.Id,
                        barcodeA1,
                        runId + " CONFLICT RESOLVED",
                        runId + " SECONDARY A",
                        runId + "-ITEM-A1",
                        articleAReference.CategoryRemoteId,
                        articleAReference.SupplierRemoteId,
                        1500,
                        650,
                        3,
                        true)
                    .ConfigureAwait(false);
                result.CanonicalValuesMatch = true;
                result.AckCatalogRevisionMatch = true;

                var counts = await ReadFinalCountsAsync(
                        factory,
                        created.Id)
                    .ConfigureAwait(false);
                result.WaitingDependency = counts.WaitingDependency;
                result.Pending = counts.Pending;
                result.InProgress = counts.InProgress;
                result.RetryWait = counts.RetryWait;
                result.BlockedConflicts = counts.BlockedConflicts;
                result.Completed = counts.Completed;
                result.PriceMutationRows = counts.PriceMutationRows;
                result.PriceHistoryMutationRows =
                    counts.PriceHistoryMutationRows;
                result.PriceHistoryDuplicateGroups =
                    counts.PriceHistoryDuplicateGroups;
                result.StockAdjustmentRows = counts.StockAdjustmentRows;
                result.StockQuantityDelta = counts.StockQuantityDelta;
                result.StockDuplicateGroups = counts.StockDuplicateGroups;
                result.SalesRows = counts.SalesRows;
                Require(
                    counts.WaitingDependency == 0 &&
                    counts.Pending == 0 &&
                    counts.InProgress == 0 &&
                    counts.RetryWait == 0,
                    "unresolved_non_conflict_work_remains");
                Require(
                    counts.BlockedConflicts == 0,
                    "intentional_conflict_count_mismatch");
                Require(
                    counts.PriceMutationRows == 2 &&
                    counts.PriceHistoryMutationRows == 2 &&
                    counts.PriceHistoryDuplicateGroups == 0,
                    "price_history_exactly_once_failed");
                Require(
                    counts.StockAdjustmentRows == 2 &&
                    counts.StockQuantityDelta == 3 &&
                    counts.StockDuplicateGroups == 0,
                    "stock_adjustment_exactly_once_failed");
                Require(counts.SalesRows == 0, "article_mutation_created_sale");
                Require(
                    await CountDistinctRemoteChildIdsAsync(
                            factory,
                            created.Id,
                            "product_retail_price_change",
                            "product_purchase_price_change",
                            "remote_price_history_id")
                        .ConfigureAwait(false) == 2,
                    "remote_price_history_ids_missing");
                Require(
                    await CountDistinctRemoteChildIdsAsync(
                            factory,
                            created.Id,
                            "product_manual_stock_adjustment",
                            null,
                            "remote_stock_movement_id")
                        .ConfigureAwait(false) == 2,
                    "remote_stock_movement_ids_missing");
                result.RemoteChildIdsAssigned = true;

                await CaptureCleanSyncCenterAsync(
                        factory,
                        outputDirectory)
                    .ConfigureAwait(true);
                result.UiScreenshots = Directory.EnumerateFiles(
                        outputDirectory,
                        "article-mutation-*.png",
                        SearchOption.TopDirectoryOnly)
                    .Count();
                Require(
                    result.UiScreenshots >= 11,
                    "article_ui_screenshot_count_incomplete");

                result.Passed = true;
                result.Code = "success";
            }
            catch (Exception ex)
            {
                result.Passed = false;
                result.Code = SafeCode(ex.Message);
                result.ExceptionType = ex.GetType().Name;
            }
            finally
            {
                try
                {
                    Directory.CreateDirectory(outputDirectory);
                    var cleanup = await BuildCleanupManifestAsync(
                            factory,
                            runId,
                            trustedSession?.ShopId,
                            localProductIds,
                            directMutationIds,
                            conflictReceiptMutationIds)
                        .ConfigureAwait(false);
                    if (result.Passed)
                    {
                        ValidateCleanupManifestForPass(cleanup);
                    }
                    WriteJson(
                        Path.Combine(
                            outputDirectory,
                            "CLEANUP-MANIFEST.json"),
                        cleanup);
                    WriteCleanupPrompt(
                        Path.Combine(
                            outputDirectory,
                            "NEXT-CODEX-MAC-FINAL-CLEANUP.md"),
                        cleanup);
                    result.CleanupManifestCreated = true;
                }
                catch (Exception cleanupFailure)
                {
                    result.Passed = false;
                    result.Code = "cleanup_manifest_write_failed";
                    result.ExceptionType = cleanupFailure.GetType().Name;
                }
                result.CompletedAtUtc = UtcNow();
                result.ActiveHost = activeHost;
                await WriteEvidenceAsync(factory, outputDirectory, result)
                    .ConfigureAwait(false);
            }
            return result;
        }

        private static async Task<ProductEditViewModel> CreateViewModelAsync(
            ProductEditMode mode,
            ProductDetailsRow source,
            ProductsWorkflowService workflow)
        {
            var viewModel = new ProductEditViewModel(mode, source, workflow);
            viewModel.SetCategories(
                await workflow.GetCategoriesAsync().ConfigureAwait(true));
            viewModel.SetSuppliers(
                await workflow.GetSuppliersAsync().ConfigureAwait(true));
            viewModel.SetSelectionFromSource(source);
            return viewModel;
        }

        private static void Populate(
            ProductEditViewModel viewModel,
            string barcode,
            string name,
            int retail,
            int purchase,
            int stock,
            string itemNumber)
        {
            viewModel.Barcode = barcode;
            viewModel.ProductName = name;
            viewModel.PriceText = retail.ToString(CultureInfo.InvariantCulture);
            viewModel.PurchasePriceText = purchase.ToString(
                CultureInfo.InvariantCulture);
            viewModel.StockText = stock.ToString(CultureInfo.InvariantCulture);
            viewModel.ArticleCode = itemNumber;
        }

        private static async Task SubmitAsync(ProductEditViewModel viewModel)
        {
            var completed = new TaskCompletionSource<bool>();
            Action<bool> handler = null;
            handler = success =>
            {
                viewModel.RequestClose -= handler;
                completed.TrySetResult(success);
            };
            viewModel.RequestClose += handler;
            Require(
                viewModel.ConfirmCommand.CanExecute(null),
                "article_view_model_save_disabled");
            viewModel.ConfirmCommand.Execute(null);
            var finished = await Task.WhenAny(
                    completed.Task,
                    Task.Delay(TimeSpan.FromSeconds(15)))
                .ConfigureAwait(true);
            if (!ReferenceEquals(finished, completed.Task))
            {
                viewModel.RequestClose -= handler;
                throw new TimeoutException("article_view_model_save_timeout");
            }
            Require(
                await completed.Task.ConfigureAwait(true),
                "article_view_model_save_cancelled");
        }

        private static async Task DrainArticlesAsync(
            SqliteConnectionFactory factory,
            PosOnlineSyncSupervisorHost host,
            CancellationToken cancellationToken,
            bool allowBlocked)
        {
            for (var pass = 0; pass < 40; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var retry = await CountAsync(
                        factory,
                        @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state = 'retry_wait';")
                    .ConfigureAwait(false);
                Require(retry == 0, "article_retry_wait_fail_fast");
                var remaining = await CountAsync(
                        factory,
                        @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state IN ('waiting_dependency', 'pending', 'in_progress');")
                    .ConfigureAwait(false);
                if (remaining == 0)
                {
                    var blocked = await CountAsync(
                            factory,
                            @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state = 'failed_blocked';")
                        .ConfigureAwait(false);
                    Require(
                        blocked == 0 || allowBlocked,
                        "article_unexpected_blocked_work");
                    return;
                }

                var outcome = await host.TriggerAsync(
                        OnlineSyncLane.ArticleMutationOutbox,
                        OnlineSyncLaneTrigger.Manual,
                        cancellationToken)
                    .ConfigureAwait(true);
                Require(
                    !outcome.AuthenticationDenied,
                    "article_authentication_denied");
                retry = await CountAsync(
                        factory,
                        @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state = 'retry_wait';")
                    .ConfigureAwait(false);
                Require(retry == 0, "article_transport_retry_disallowed");
                if (!outcome.Success)
                {
                    Require(
                        allowBlocked &&
                        string.Equals(
                            outcome.Code,
                            "article_mutation_blocked",
                            StringComparison.Ordinal),
                        "article_drain_" + SafeCode(outcome.Code));
                }
            }
            throw new InvalidOperationException("article_drain_budget_exhausted");
        }

        private static async Task PullCanonicalAsync(
            SqliteConnectionFactory factory,
            PosOnlineSyncSupervisorHost host,
            PosTrustedDeviceSession trustedSession,
            Uri baseUri,
            CancellationToken cancellationToken)
        {
            var pull = await new PosCatalogPullService(factory)
                .TryPullCatalogForSupervisorAsync(
                    new PosAdminWebOptions(baseUri),
                    trustedSession,
                    host.CurrentGeneration,
                    executionContext: null,
                    forceFullRepair: false,
                    bootstrapRun: false,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Require(
                pull.Completed && pull.CatalogSaleSafe,
                "canonical_pull_" + SafeCode(pull.StatusCode));
        }

        private static async Task<PosOnlineResult<PosArticleMutationResponse>>
            SendDirectAsync(
                Uri baseUri,
                PosTrustedDeviceSession trustedSession,
                PosArticleMutationRequest request,
                CancellationToken cancellationToken)
        {
            using (var client = new PosAdminWebClient(
                new PosAdminWebOptions(baseUri)))
            {
                return await client.ArticleMutationsAsync(
                        BuildEnvelope(trustedSession, request),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private static PosArticleMutationEnvelope BuildEnvelope(
            PosTrustedDeviceSession trustedSession,
            PosArticleMutationRequest request)
        {
            return new PosArticleMutationEnvelope
            {
                AppVersion =
                    StagingAcceptanceWpfHarness.GetProductionAppVersion(),
                ShopId = trustedSession.ShopId,
                ShopDeviceId = trustedSession.ShopDeviceId,
                StaffId = trustedSession.StaffId,
                StaffCredentialVersion =
                    trustedSession.StaffCredentialVersion,
                PosSessionId = trustedSession.PosSessionId,
                DeviceToken = trustedSession.DeviceToken,
                SessionToken = trustedSession.SessionToken,
                Mutations = new[] { request }
            };
        }

        private static PosArticleMutationResult RequireSingleResult(
            PosOnlineResult<PosArticleMutationResponse> response,
            string failureCode)
        {
            Require(
                response != null &&
                response.Success &&
                response.HttpStatus.HasValue &&
                response.HttpStatus.Value >= 200 &&
                response.HttpStatus.Value <= 299 &&
                response.Value?.Results?.Length == 1,
                failureCode + "_" +
                SafeCode(response?.Code) + "_" +
                (response?.HttpStatus?.ToString(
                    CultureInfo.InvariantCulture) ?? "none"));
            return response.Value.Results[0];
        }

        private static async Task<VerifiedReferenceRow>
            GetVerifiedReferenceAsync(
                SqliteConnectionFactory factory,
                string table,
                string remoteColumn)
        {
            var sql = "SELECT id AS Id, name AS Name, " + remoteColumn +
                " AS RemoteId FROM " + table +
                " WHERE " + remoteColumn +
                " IS NOT NULL AND TRIM(" + remoteColumn +
                ") <> '' AND COALESCE(is_active, 1) = 1 ORDER BY id LIMIT 1;";
            using (var connection = factory.Open())
            {
                return await connection
                    .QueryFirstOrDefaultAsync<VerifiedReferenceRow>(sql)
                    .ConfigureAwait(false);
            }
        }

        private static async Task<ProductIdentityRow> ReadIdentityAsync(
            SqliteConnectionFactory factory,
            long localProductId)
        {
            using (var connection = factory.Open())
            {
                return await connection
                    .QueryFirstOrDefaultAsync<ProductIdentityRow>(@"
SELECT product.id AS LocalProductId,
       product.client_product_id AS ClientProductId,
       product.remote_product_id AS RemoteProductId,
       product.remote_base_revision AS RemoteBaseRevision,
       COALESCE(MAX(outbox.local_sequence), 0) AS MaximumLocalSequence
FROM products product
LEFT JOIN article_mutation_outbox outbox
  ON outbox.local_product_id = product.id
WHERE product.id = @localProductId
GROUP BY product.id,
         product.client_product_id,
         product.remote_product_id,
         product.remote_base_revision;",
                        new { localProductId })
                .ConfigureAwait(false);
            }
        }

        private static async Task<OfflineCreateAtomicSnapshot>
            ReadOfflineCreateAtomicSnapshotAsync(
                SqliteConnectionFactory factory,
                long localProductId)
        {
            using (var connection = factory.Open())
            using (var transaction = connection.BeginTransaction())
            {
                var snapshot = await connection
                    .QueryFirstAsync<OfflineCreateAtomicSnapshot>(@"
SELECT
  (SELECT COUNT(1)
   FROM products
   WHERE id = @localProductId
     AND remote_product_id IS NULL) AS ProductRows,
  (SELECT COUNT(1)
   FROM article_mutation_outbox
   WHERE local_product_id = @localProductId
     AND mutation_kind = 'product_create'
     AND state = 'pending') AS CreateOutboxRows,
  (SELECT client_product_id
   FROM products
   WHERE id = @localProductId) AS ProductClientProductId,
  (SELECT client_product_id
   FROM article_mutation_outbox
   WHERE local_product_id = @localProductId
     AND mutation_kind = 'product_create'
   ORDER BY local_sequence
   LIMIT 1) AS CreateClientProductId;",
                        new { localProductId },
                        transaction)
                    .ConfigureAwait(false);
                transaction.Commit();
                return snapshot;
            }
        }

        private static async Task<ProductReferenceRow>
            ReadProductReferenceAsync(
                SqliteConnectionFactory factory,
                long localProductId)
        {
            using (var connection = factory.Open())
            {
                return await connection
                    .QueryFirstOrDefaultAsync<ProductReferenceRow>(@"
SELECT category.remote_category_id AS CategoryRemoteId,
       supplier.remote_supplier_id AS SupplierRemoteId
FROM products product
JOIN product_meta meta
  ON meta.barcode = product.barcode
JOIN categories category
  ON category.id = meta.category_id
JOIN suppliers supplier
  ON supplier.id = meta.supplier_id
WHERE product.id = @localProductId;",
                        new { localProductId })
                    .ConfigureAwait(false);
            }
        }

        private static async Task RequireCanonicalProductAsync(
            SqliteConnectionFactory factory,
            long localProductId,
            string barcode,
            string primaryName,
            string secondaryName,
            string itemNumber,
            string categoryRemoteId,
            string supplierRemoteId,
            long retailPrice,
            int purchasePrice,
            int stockQuantity,
            bool active)
        {
            CanonicalProductRow row;
            using (var connection = factory.Open())
            {
                row = await connection
                    .QueryFirstOrDefaultAsync<CanonicalProductRow>(@"
SELECT product.id AS LocalProductId,
       product.barcode AS LocalBarcode,
       product.name AS LocalPrimaryName,
       COALESCE(meta.name2, '') AS LocalSecondaryName,
       COALESCE(meta.article_code, '') AS LocalItemNumber,
       product.unitPrice AS LocalRetailPrice,
       COALESCE(meta.purchase_price, 0) AS LocalPurchasePrice,
       COALESCE(meta.stock_qty, 0) AS LocalStockQuantity,
       COALESCE(category.remote_category_id, '')
         AS LocalCategoryRemoteId,
       COALESCE(supplier.remote_supplier_id, '')
         AS LocalSupplierRemoteId,
       COALESCE(product.is_active, 1) AS LocalIsActive,
       product.remote_product_id AS RemoteProductId,
       product.remote_base_revision AS RemoteBaseRevision,
       shadow.barcode AS ShadowBarcode,
       shadow.primary_name AS ShadowPrimaryName,
       COALESCE(shadow.secondary_name, '') AS ShadowSecondaryName,
       COALESCE(shadow.item_number, '') AS ShadowItemNumber,
       COALESCE(shadow.category_remote_id, '') AS ShadowCategoryRemoteId,
       COALESCE(shadow.supplier_remote_id, '') AS ShadowSupplierRemoteId,
       shadow.retail_price AS ShadowRetailPrice,
       shadow.purchase_price AS ShadowPurchasePrice,
       shadow.stock_quantity AS ShadowStockQuantity,
       shadow.is_active AS ShadowIsActive,
       shadow.authoritative_revision AS CatalogUpdatedAtRevision,
       (
         SELECT outbox.authoritative_revision
         FROM article_mutation_outbox outbox
         WHERE outbox.local_product_id = product.id
           AND outbox.state = 'completed'
           AND outbox.ack_status IN ('applied', 'duplicate_replay')
         ORDER BY outbox.local_sequence DESC, outbox.id DESC
         LIMIT 1
       ) AS LatestAckAuthoritativeRevision
FROM products product
LEFT JOIN product_meta meta
  ON meta.barcode = product.barcode
LEFT JOIN categories category
  ON category.id = meta.category_id
LEFT JOIN suppliers supplier
  ON supplier.id = meta.supplier_id
JOIN article_product_remote_shadow shadow
  ON shadow.remote_product_id = product.remote_product_id
 AND shadow.local_product_id = product.id
WHERE product.id = @localProductId;",
                        new { localProductId })
                    .ConfigureAwait(false);
            }
            Require(row != null, "canonical_product_shadow_missing");
            Require(
                string.Equals(row.LocalBarcode, barcode, StringComparison.Ordinal) &&
                string.Equals(row.ShadowBarcode, barcode, StringComparison.Ordinal) &&
                string.Equals(
                    row.LocalPrimaryName,
                    primaryName,
                    StringComparison.Ordinal) &&
                string.Equals(
                    row.ShadowPrimaryName,
                    primaryName,
                    StringComparison.Ordinal) &&
                string.Equals(
                    row.LocalSecondaryName,
                    secondaryName ?? string.Empty,
                    StringComparison.Ordinal) &&
                string.Equals(
                    row.ShadowSecondaryName,
                    secondaryName ?? string.Empty,
                    StringComparison.Ordinal) &&
                string.Equals(
                    row.LocalItemNumber,
                    itemNumber ?? string.Empty,
                    StringComparison.Ordinal) &&
                string.Equals(
                    row.ShadowItemNumber,
                    itemNumber ?? string.Empty,
                    StringComparison.Ordinal) &&
                string.Equals(
                    row.LocalCategoryRemoteId,
                    categoryRemoteId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    row.LocalSupplierRemoteId,
                    supplierRemoteId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    row.ShadowCategoryRemoteId,
                    categoryRemoteId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    row.ShadowSupplierRemoteId,
                    supplierRemoteId,
                    StringComparison.Ordinal),
                "canonical_product_fields_mismatch");
            Require(
                row.LocalRetailPrice == retailPrice &&
                row.ShadowRetailPrice == retailPrice &&
                row.LocalPurchasePrice == purchasePrice &&
                row.ShadowPurchasePrice == purchasePrice &&
                row.LocalStockQuantity == stockQuantity &&
                row.ShadowStockQuantity == stockQuantity &&
                row.LocalIsActive == (active ? 1 : 0) &&
                row.ShadowIsActive == (active ? 1 : 0),
                "canonical_product_values_mismatch");
            Require(
                Guid.TryParse(row.RemoteProductId, out _) &&
                PosArticleMutationIntentPolicy.IsProductRevision(
                    row.RemoteBaseRevision) &&
                string.Equals(
                    row.RemoteBaseRevision,
                    row.CatalogUpdatedAtRevision,
                    StringComparison.Ordinal) &&
                string.Equals(
                    row.LatestAckAuthoritativeRevision,
                    row.CatalogUpdatedAtRevision,
                    StringComparison.Ordinal),
                "ack_catalog_revision_mismatch");
        }

        private static async Task<long> CountDistinctRemoteChildIdsAsync(
            SqliteConnectionFactory factory,
            long localProductId,
            string firstMutationKind,
            string secondMutationKind,
            string remoteIdColumn)
        {
            var column = string.Equals(
                remoteIdColumn,
                "remote_price_history_id",
                StringComparison.Ordinal)
                ? "remote_price_history_id"
                : string.Equals(
                    remoteIdColumn,
                    "remote_stock_movement_id",
                    StringComparison.Ordinal)
                    ? "remote_stock_movement_id"
                    : null;
            Require(column != null, "remote_child_column_invalid");
            var sql = "SELECT COUNT(DISTINCT " + column + @")
FROM article_mutation_outbox
WHERE local_product_id = @localProductId
  AND state = 'completed'
  AND ack_status = 'applied'
  AND " + column + @" IS NOT NULL
  AND TRIM(" + column + @") <> ''
  AND (
    mutation_kind = @firstMutationKind
    OR (@secondMutationKind IS NOT NULL
        AND mutation_kind = @secondMutationKind)
  );";
            using (var connection = factory.Open())
            {
                return await connection.ExecuteScalarAsync<long>(
                        sql,
                        new
                        {
                            firstMutationKind,
                            localProductId,
                            secondMutationKind
                        })
                    .ConfigureAwait(false);
            }
        }

        private static bool ReplayAckMatches(
            PosArticleMutationAck ack,
            ReplaySourceRow source)
        {
            return ack != null &&
                source != null &&
                string.Equals(
                    ack.AttemptToken,
                    source.AckAttemptToken,
                    StringComparison.Ordinal) &&
                string.Equals(
                    ack.AuthoritativeRevision,
                    source.AuthoritativeRevision,
                    StringComparison.Ordinal) &&
                string.Equals(
                    ack.CatalogRevision,
                    source.CatalogRevision,
                    StringComparison.Ordinal) &&
                string.Equals(
                    ack.MutationId,
                    source.MutationId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    ack.IdempotencyKey,
                    source.IdempotencyKey,
                    StringComparison.Ordinal) &&
                string.Equals(
                    ack.PayloadHash,
                    source.PayloadHash,
                    StringComparison.Ordinal) &&
                string.Equals(
                    ack.RemoteProductId,
                    source.RemoteAssignedProductId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    ack.Status,
                    source.AckStatus,
                    StringComparison.Ordinal) &&
                string.Equals(
                    ack.Code,
                    source.AckCode,
                    StringComparison.Ordinal) &&
                ack.Terminal == source.AckTerminal &&
                ack.Retryable == source.AckRetryable;
        }

        private static async Task RecordDirectProbeAsync(
            SqliteConnectionFactory factory,
            long localProductId,
            PosArticleMutationRequest request,
            PosArticleMutationResult result)
        {
            Require(
                request?.Intent != null &&
                result?.Ack != null &&
                string.Equals(
                    result.DeliveryStatus,
                    PosArticleMutationStatusPolicy.Applied,
                    StringComparison.Ordinal) &&
                string.Equals(
                    result.Ack.MutationId,
                    request.Intent.MutationId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    result.Ack.IdempotencyKey,
                    request.Intent.IdempotencyKey,
                    StringComparison.Ordinal) &&
                string.Equals(
                    result.Ack.PayloadHash,
                    request.PayloadHash,
                    StringComparison.Ordinal),
                "direct_probe_ack_invalid");
            var completedAt = result.Ack.ServerTimestamp;
            var canonical =
                PosArticleMutationCanonicalWriter.Write(request.Intent);
            using (var connection = factory.Open())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    await connection.ExecuteAsync(@"
INSERT INTO article_mutation_outbox(
  local_product_id,
  mutation_id,
  idempotency_key,
  client_product_id,
  remote_product_id,
  mutation_kind,
  local_sequence,
  base_revision,
  field_mask_json,
  intent_json,
  intent_hash,
  canonical_payload_json,
  payload_hash,
  created_at,
  occurred_at,
  state,
  attempt_count,
  next_attempt_at,
  last_typed_code,
  authoritative_revision,
  catalog_revision,
  remote_price_history_id,
  remote_stock_movement_id,
  remote_assigned_product_id,
  ack_status,
  ack_code,
  ack_attempt_token,
  ack_server_timestamp,
  ack_terminal,
  ack_retryable,
  completed_at,
  updated_at)
VALUES(
  @localProductId,
  @mutationId,
  @idempotencyKey,
  @clientProductId,
  @remoteProductId,
  @mutationKind,
  @localSequence,
  @baseRevision,
  @fieldMaskJson,
  @intentJson,
  @intentHash,
  @canonicalPayloadJson,
  @payloadHash,
  @createdAt,
  @occurredAt,
  'completed',
  1,
  0,
  @lastTypedCode,
  @authoritativeRevision,
  @catalogRevision,
  @remotePriceHistoryId,
  @remoteStockMovementId,
  NULL,
  @ackStatus,
  @ackCode,
  @ackAttemptToken,
  @ackServerTimestamp,
  @ackTerminal,
  @ackRetryable,
  @completedAt,
  @completedAt);",
                        new
                        {
                            localProductId,
                            mutationId = request.Intent.MutationId,
                            idempotencyKey =
                                request.Intent.IdempotencyKey,
                            clientProductId =
                                request.Intent.ClientProductId,
                            remoteProductId =
                                request.Intent.RemoteProductId,
                            mutationKind =
                                request.Intent.MutationKind,
                            localSequence =
                                request.Intent.LocalSequence,
                            baseRevision =
                                request.Intent.BaseRevision,
                            fieldMaskJson = WriteStringArray(
                                request.Intent.FieldMask),
                            intentJson = canonical,
                            intentHash = request.PayloadHash,
                            canonicalPayloadJson = canonical,
                            payloadHash = request.PayloadHash,
                            createdAt = request.Intent.CreatedAt,
                            occurredAt = request.Intent.OccurredAt,
                            lastTypedCode = result.DeliveryStatus,
                            authoritativeRevision =
                                result.Ack.AuthoritativeRevision,
                            catalogRevision =
                                result.Ack.CatalogRevision,
                            remotePriceHistoryId =
                                result.Ack.PriceHistoryId,
                            remoteStockMovementId =
                                result.Ack.StockMovementId,
                            ackStatus = result.Ack.Status,
                            ackCode = result.Ack.Code,
                            ackAttemptToken =
                                result.Ack.AttemptToken,
                            ackServerTimestamp =
                                result.Ack.ServerTimestamp,
                            ackTerminal =
                                result.Ack.Terminal ? 1 : 0,
                            ackRetryable =
                                result.Ack.Retryable ? 1 : 0,
                            completedAt
                        },
                        transaction).ConfigureAwait(false);
                    await connection.ExecuteAsync(@"
INSERT INTO article_mutation_attempts(
  mutation_id,
  attempt_token,
  created_at,
  started_at,
  completed_at,
  outcome)
VALUES(
  @mutationId,
  @attemptToken,
  @createdAt,
  @createdAt,
  @completedAt,
  @outcome);",
                        new
                        {
                            mutationId = request.Intent.MutationId,
                            attemptToken = request.AttemptToken,
                            createdAt = request.Intent.CreatedAt,
                            completedAt,
                            outcome = result.DeliveryStatus
                        },
                        transaction).ConfigureAwait(false);
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        private static string WriteStringArray(
            IReadOnlyList<string> values)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(string[]))
                    .WriteObject(
                        stream,
                        (values ?? Array.Empty<string>()).ToArray());
                return new UTF8Encoding(false, true)
                    .GetString(stream.ToArray());
            }
        }

        private static async Task<ReplaySourceRow> ReadReplaySourceAsync(
            SqliteConnectionFactory factory,
            long localProductId)
        {
            using (var connection = factory.Open())
            {
                return await connection
                    .QueryFirstOrDefaultAsync<ReplaySourceRow>(@"
SELECT canonical_payload_json AS CanonicalPayloadJson,
       payload_hash AS PayloadHash,
       mutation_id AS MutationId,
       idempotency_key AS IdempotencyKey,
       authoritative_revision AS AuthoritativeRevision,
       catalog_revision AS CatalogRevision,
       remote_assigned_product_id AS RemoteAssignedProductId,
       ack_status AS AckStatus,
       ack_code AS AckCode,
       ack_attempt_token AS AckAttemptToken,
       ack_terminal AS AckTerminal,
       ack_retryable AS AckRetryable
FROM article_mutation_outbox
WHERE local_product_id = @localProductId
  AND state = 'completed'
  AND mutation_kind = 'product_create'
ORDER BY local_sequence
LIMIT 1;",
                        new { localProductId })
                    .ConfigureAwait(false);
            }
        }

        private static PosArticleMutationIntent Rehydrate(string canonicalJson)
        {
            var serializer = new DataContractJsonSerializer(
                typeof(PersistedCanonicalIntent));
            PersistedCanonicalIntent persisted;
            using (var stream = new MemoryStream(
                new UTF8Encoding(false, true).GetBytes(
                    canonicalJson ?? string.Empty)))
            {
                persisted = serializer.ReadObject(stream) as
                    PersistedCanonicalIntent;
            }
            Require(persisted != null, "replay_payload_rehydrate_failed");
            return PosArticleMutationIntentPolicy.Rehydrate(
                persisted.BaseRevision,
                RebuildChanges(persisted),
                persisted.ClientProductId,
                persisted.CreatedAt,
                persisted.FieldMask,
                persisted.IdempotencyKey,
                persisted.LocalSequence,
                persisted.MutationId,
                persisted.MutationKind,
                persisted.OccurredAt,
                persisted.RemoteProductId);
        }

        private static IDictionary<string, object> RebuildChanges(
            PersistedCanonicalIntent persisted)
        {
            var changes = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var field in FieldsFor(persisted))
            {
                switch (field)
                {
                    case PosArticleMutationFields.Barcode:
                        changes[field] = persisted.Changes?.Barcode;
                        break;
                    case PosArticleMutationFields.ItemNumber:
                        changes[field] = persisted.Changes?.ItemNumber;
                        break;
                    case PosArticleMutationFields.PrimaryName:
                        changes[field] = persisted.Changes?.PrimaryName;
                        break;
                    case PosArticleMutationFields.SecondaryName:
                        changes[field] = persisted.Changes?.SecondaryName;
                        break;
                    case PosArticleMutationFields.CategoryId:
                        changes[field] = persisted.Changes?.CategoryId;
                        break;
                    case PosArticleMutationFields.SupplierId:
                        changes[field] = persisted.Changes?.SupplierId;
                        break;
                    case PosArticleMutationFields.PurchasePrice:
                        changes[field] = persisted.Changes.PurchasePrice.Value;
                        break;
                    case PosArticleMutationFields.RetailPrice:
                        changes[field] = persisted.Changes.RetailPrice.Value;
                        break;
                    case PosArticleMutationFields.StockQuantity:
                        changes[field] = persisted.Changes.StockQuantity.Value;
                        break;
                    case PosArticleMutationFields.Price:
                        changes[field] = persisted.Changes.Price.Value;
                        break;
                    case PosArticleMutationFields.QuantityDelta:
                        changes[field] = persisted.Changes.QuantityDelta.Value;
                        break;
                    case PosArticleMutationFields.Reason:
                        changes[field] = persisted.Changes?.Reason;
                        break;
                }
            }
            return changes;
        }

        private static IEnumerable<string> FieldsFor(
            PersistedCanonicalIntent persisted)
        {
            if (string.Equals(
                persisted.MutationKind,
                PosArticleMutationKinds.ProductUpdate,
                StringComparison.Ordinal))
            {
                return persisted.FieldMask ?? Array.Empty<string>();
            }
            if (string.Equals(
                    persisted.MutationKind,
                    PosArticleMutationKinds.ProductRetailPriceChange,
                    StringComparison.Ordinal) ||
                string.Equals(
                    persisted.MutationKind,
                    PosArticleMutationKinds.ProductPurchasePriceChange,
                    StringComparison.Ordinal))
            {
                return new[] { PosArticleMutationFields.Price };
            }
            if (string.Equals(
                persisted.MutationKind,
                PosArticleMutationKinds.ProductManualStockAdjustment,
                StringComparison.Ordinal))
            {
                return new[]
                {
                    PosArticleMutationFields.QuantityDelta,
                    PosArticleMutationFields.Reason
                };
            }
            if (string.Equals(
                    persisted.MutationKind,
                    PosArticleMutationKinds.ProductActivate,
                    StringComparison.Ordinal) ||
                string.Equals(
                    persisted.MutationKind,
                    PosArticleMutationKinds.ProductDeactivate,
                    StringComparison.Ordinal))
            {
                return Array.Empty<string>();
            }
            return new[]
            {
                PosArticleMutationFields.Barcode,
                PosArticleMutationFields.ItemNumber,
                PosArticleMutationFields.PrimaryName,
                PosArticleMutationFields.SecondaryName,
                PosArticleMutationFields.CategoryId,
                PosArticleMutationFields.SupplierId,
                PosArticleMutationFields.PurchasePrice,
                PosArticleMutationFields.RetailPrice,
                PosArticleMutationFields.StockQuantity
            }.Where(field => HasValue(persisted.Changes, field));
        }

        private static bool HasValue(PersistedChanges changes, string field)
        {
            if (changes == null)
                return false;
            switch (field)
            {
                case PosArticleMutationFields.Barcode:
                    return changes.Barcode != null;
                case PosArticleMutationFields.ItemNumber:
                    return changes.ItemNumber != null;
                case PosArticleMutationFields.PrimaryName:
                    return changes.PrimaryName != null;
                case PosArticleMutationFields.SecondaryName:
                    return changes.SecondaryName != null;
                case PosArticleMutationFields.CategoryId:
                    return changes.CategoryId != null;
                case PosArticleMutationFields.SupplierId:
                    return changes.SupplierId != null;
                case PosArticleMutationFields.PurchasePrice:
                    return changes.PurchasePrice.HasValue;
                case PosArticleMutationFields.RetailPrice:
                    return changes.RetailPrice.HasValue;
                case PosArticleMutationFields.StockQuantity:
                    return changes.StockQuantity.HasValue;
                default:
                    return false;
            }
        }

        private static async Task<FinalCounts> ReadFinalCountsAsync(
            SqliteConnectionFactory factory,
            long articleALocalId)
        {
            using (var connection = factory.Open())
            {
                var counts = new FinalCounts
                {
                    WaitingDependency = await StateCountAsync(
                        connection,
                        "waiting_dependency"),
                    Pending = await StateCountAsync(connection, "pending"),
                    InProgress = await StateCountAsync(
                        connection,
                        "in_progress"),
                    RetryWait = await StateCountAsync(connection, "retry_wait"),
                    BlockedConflicts =
                        await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state = 'failed_blocked';"),
                    Completed = await StateCountAsync(connection, "completed"),
                    PriceMutationRows =
                        await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_outbox
WHERE local_product_id = @articleALocalId
  AND mutation_kind IN (
    'product_retail_price_change',
    'product_purchase_price_change');",
                            new { articleALocalId }),
                    PriceHistoryMutationRows =
                        await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM product_price_history history
JOIN article_mutation_outbox outbox
  ON outbox.mutation_id = history.article_mutation_id
WHERE outbox.local_product_id = @articleALocalId
  AND outbox.mutation_kind IN (
    'product_retail_price_change',
    'product_purchase_price_change');",
                            new { articleALocalId }),
                    PriceHistoryDuplicateGroups =
                        await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM (
  SELECT article_mutation_id
  FROM product_price_history
  WHERE article_mutation_id IS NOT NULL
  GROUP BY article_mutation_id
  HAVING COUNT(1) > 1
);"),
                    StockAdjustmentRows =
                        await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_manual_stock_adjustments
WHERE local_product_id = @articleALocalId;",
                            new { articleALocalId }),
                    StockQuantityDelta =
                        await connection.ExecuteScalarAsync<long>(@"
SELECT COALESCE(SUM(quantity_delta), 0)
FROM article_manual_stock_adjustments
WHERE local_product_id = @articleALocalId;",
                            new { articleALocalId }),
                    StockDuplicateGroups =
                        await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM (
  SELECT mutation_id
  FROM article_manual_stock_adjustments
  GROUP BY mutation_id
  HAVING COUNT(1) > 1
);")
                };
                counts.SalesRows =
                    await connection.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM sales;") +
                    await connection.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM sale_lines;") +
                    await connection.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM sales_sync_outbox;") +
                    await connection.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM local_stock_movements;");
                return counts;
            }
        }

        private static Task<long> StateCountAsync(
            System.Data.IDbConnection connection,
            string state)
        {
            return connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM article_mutation_outbox " +
                "WHERE state = @state;",
                new { state });
        }

        private static async Task<UiVerificationResult> CaptureArticleUiAsync(
            SqliteConnectionFactory factory,
            ProductsWorkflowService workflow,
            long articleALocalId,
            string outputDirectory)
        {
            var verification = new UiVerificationResult
            {
                ConflictNonModal = true,
                ControlsUnclipped = true,
                KeyboardNavigationVerified = true,
                Responsive = true
            };
            var owner = new Window
            {
                Height = 768,
                ShowInTaskbar = false,
                Title = "Win7POS article staging viewport",
                Width = 1024,
                WindowStartupLocation =
                    WindowStartupLocation.CenterScreen
            };
            try
            {
                owner.Show();
                owner.UpdateLayout();
                Require(
                    Math.Abs(owner.ActualWidth - 1024) <= 1 &&
                    Math.Abs(owner.ActualHeight - 768) <= 1,
                    "article_ui_viewport_not_1024x768");

                var source = await workflow
                    .GetDetailsByIdAsync(articleALocalId)
                    .ConfigureAwait(true);
                await CaptureProductEditorAsync(
                        owner,
                        await CreateViewModelAsync(
                                ProductEditMode.New,
                                null,
                                workflow)
                            .ConfigureAwait(true),
                        Path.Combine(
                            outputDirectory,
                            "article-mutation-create-article-1024x768.png"))
                    .ConfigureAwait(true);
                await CaptureProductEditorAsync(
                        owner,
                        await CreateViewModelAsync(
                                ProductEditMode.Edit,
                                source,
                                workflow)
                            .ConfigureAwait(true),
                        Path.Combine(
                            outputDirectory,
                            "article-mutation-product-editor-1024x768.png"))
                    .ConfigureAwait(true);
                await CaptureProductEditorAsync(
                        owner,
                        await CreateViewModelAsync(
                                ProductEditMode.Duplicate,
                                source,
                                workflow)
                            .ConfigureAwait(true),
                        Path.Combine(
                            outputDirectory,
                            "article-mutation-duplicate-article-1024x768.png"))
                    .ConfigureAwait(true);

                var originalLanguage =
                    PosLocalization.Current.CurrentLanguage;
                try
                {
                    foreach (var language in new[]
                    {
                        "en",
                        "es",
                        "it",
                        "zh-CN"
                    })
                    {
                        PosLocalization.Current.SetLanguage(language);
                        var notice = PosLocalization.Current.Text(
                            "sync.articleConflictNotice");
                        Require(
                            !string.IsNullOrWhiteSpace(notice) &&
                            !string.Equals(
                                notice,
                                "sync.articleConflictNotice",
                                StringComparison.Ordinal),
                            "article_conflict_notice_not_localized_" +
                            language.Replace("-", string.Empty));
                        var syncCenter = new SyncCenterDialog(
                            factory,
                            (_, __, ___) =>
                                Task.FromResult<CatalogSyncRunResult>(null),
                            _ => Task.FromResult(false))
                        {
                            Owner = DialogOwnerHelper.GetSafeOwner(owner)
                        };
                        try
                        {
                            syncCenter.Show();
                            syncCenter.UpdateLayout();
                            await Task.Delay(750).ConfigureAwait(true);
                            Require(
                                syncCenter.ActualWidth <= 1024 &&
                                syncCenter.ActualHeight <= 768,
                                "article_sync_center_clipped_" +
                                language.Replace("-", string.Empty));
                            var interaction =
                                await VerifyWindowInteractionAsync(
                                        syncCenter)
                                    .ConfigureAwait(true);
                            verification.ControlsUnclipped &=
                                interaction.ControlsUnclipped;
                            verification.KeyboardNavigationVerified &=
                                interaction.KeyboardNavigationVerified;
                            verification.Responsive &=
                                interaction.Responsive;
                            var visibleText =
                                EnumerateVisibleText(syncCenter).ToArray();
                            Require(
                                visibleText.Contains(
                                    PosLocalization.Current.Text(
                                        "sync.center.title"),
                                    StringComparer.Ordinal) &&
                                visibleText.Contains(
                                    PosLocalization.Current.Text(
                                        "sync.center.articleChanges"),
                                    StringComparer.Ordinal) &&
                                visibleText.Contains(
                                    PosLocalization.Current.Text(
                                        "common.close"),
                                    StringComparer.Ordinal),
                                "article_sync_center_locale_not_rendered_" +
                                language.Replace("-", string.Empty));
                            var syncViewModel =
                                syncCenter.DataContext as SyncCenterViewModel;
                            Require(
                                syncViewModel?.Snapshot != null &&
                                syncViewModel.Snapshot.ArticleBlocked == 1 &&
                                string.Equals(
                                    syncViewModel.Snapshot
                                        .ArticleLastTypedCode,
                                    "failed_conflict",
                                    StringComparison.Ordinal),
                                "article_conflict_not_visible_in_sync_status");
                            verification.ConflictNonModal &=
                                syncCenter.IsVisible &&
                                owner.IsEnabled &&
                                owner.Dispatcher.CheckAccess();
                            var localizedPath = Path.Combine(
                                outputDirectory,
                                "article-mutation-sync-center-conflict-" +
                                language.Replace("-", string.Empty) +
                                "-1024x768.png");
                            CaptureWindow(syncCenter, localizedPath);
                            if (string.Equals(
                                language,
                                "it",
                                StringComparison.Ordinal))
                            {
                                File.Copy(
                                    localizedPath,
                                    Path.Combine(
                                        outputDirectory,
                                        "article-mutation-sync-center-conflict-1024x768.png"),
                                    overwrite: true);
                            }
                        }
                        finally
                        {
                            syncCenter.Close();
                        }
                    }
                }
                finally
                {
                    PosLocalization.Current.SetLanguage(originalLanguage);
                }
                verification.LanguagesVerified = true;
            }
            finally
            {
                owner.Close();
            }
            return verification;
        }

        private static async Task CaptureProductEditorAsync(
            Window owner,
            ProductEditViewModel editorViewModel,
            string outputPath)
        {
            var editor = new ProductEditDialog(editorViewModel)
            {
                Owner = DialogOwnerHelper.GetSafeOwner(owner)
            };
            WindowSizingHelper.CapMaxHeightToOwner(editor);
            try
            {
                editor.Show();
                editor.UpdateLayout();
                await Task.Delay(250).ConfigureAwait(true);
                Require(
                    editor.ActualWidth <= owner.ActualWidth &&
                    editor.ActualHeight <= owner.ActualHeight,
                    "article_product_editor_outer_bounds_invalid");
                var interaction = await VerifyWindowInteractionAsync(editor)
                    .ConfigureAwait(true);
                Require(
                    interaction.ControlsUnclipped &&
                    interaction.KeyboardNavigationVerified &&
                    interaction.Responsive,
                    "article_product_editor_interaction_failed");
                RedactEditableValuesForEvidence(editor);
                editor.UpdateLayout();
                CaptureWindow(editor, outputPath);
            }
            finally
            {
                editor.Close();
            }
        }

        private static async Task CaptureOfflineQueueUiAsync(
            SqliteConnectionFactory factory,
            string outputDirectory)
        {
            await CaptureSyncCenterSnapshotAsync(
                    factory,
                    outputDirectory,
                    "article-mutation-sync-center-pending-1024x768.png")
                .ConfigureAwait(true);

            var captureToken =
                "qa-ui-capture-" + Guid.NewGuid().ToString("N");
            using (var connection = factory.Open())
            {
                var changed = await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET state = 'in_progress',
    claim_generation_id = @captureToken,
    claim_token = @captureToken,
    updated_at = @updatedAt
WHERE id = (
  SELECT id
  FROM article_mutation_outbox
  WHERE state = 'pending'
  ORDER BY local_sequence ASC
  LIMIT 1
);",
                    new
                    {
                        captureToken,
                        updatedAt = UtcNow()
                    }).ConfigureAwait(false);
                Require(changed == 1, "article_ui_in_progress_fixture_missing");
            }
            try
            {
                await CaptureSyncCenterSnapshotAsync(
                        factory,
                        outputDirectory,
                        "article-mutation-sync-center-in-progress-1024x768.png")
                    .ConfigureAwait(true);
            }
            finally
            {
                using (var connection = factory.Open())
                {
                    await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET state = 'pending',
    claim_generation_id = NULL,
    claim_token = NULL,
    updated_at = @updatedAt
WHERE state = 'in_progress'
  AND claim_generation_id = @captureToken
  AND claim_token = @captureToken;",
                        new
                        {
                            captureToken,
                            updatedAt = UtcNow()
                        }).ConfigureAwait(false);
                }
            }
        }

        private static async Task CaptureSyncCenterSnapshotAsync(
            SqliteConnectionFactory factory,
            string outputDirectory,
            string fileName)
        {
            var owner = new Window
            {
                Height = 768,
                ShowInTaskbar = false,
                Title = "Win7POS article queue staging viewport",
                Width = 1024,
                WindowStartupLocation =
                    WindowStartupLocation.CenterScreen
            };
            try
            {
                owner.Show();
                owner.UpdateLayout();
                var syncCenter = new SyncCenterDialog(
                    factory,
                    (_, __, ___) =>
                        Task.FromResult<CatalogSyncRunResult>(null),
                    _ => Task.FromResult(false))
                {
                    Owner = DialogOwnerHelper.GetSafeOwner(owner)
                };
                try
                {
                    syncCenter.Show();
                    syncCenter.UpdateLayout();
                    await Task.Delay(750).ConfigureAwait(true);
                    var interaction =
                        await VerifyWindowInteractionAsync(syncCenter)
                            .ConfigureAwait(true);
                    Require(
                        interaction.ControlsUnclipped &&
                        interaction.KeyboardNavigationVerified &&
                        interaction.Responsive,
                        "article_sync_center_snapshot_interaction_failed");
                    CaptureWindow(
                        syncCenter,
                        Path.Combine(outputDirectory, fileName));
                }
                finally
                {
                    syncCenter.Close();
                }
            }
            finally
            {
                owner.Close();
            }
        }

        private static async Task CaptureCleanSyncCenterAsync(
            SqliteConnectionFactory factory,
            string outputDirectory)
        {
            var owner = new Window
            {
                Height = 768,
                ShowInTaskbar = false,
                Title = "Win7POS article clean staging viewport",
                Width = 1024,
                WindowStartupLocation =
                    WindowStartupLocation.CenterScreen
            };
            try
            {
                owner.Show();
                owner.UpdateLayout();
                Require(
                    Math.Abs(owner.ActualWidth - 1024) <= 1 &&
                    Math.Abs(owner.ActualHeight - 768) <= 1,
                    "article_clean_ui_viewport_not_1024x768");
                var syncCenter = new SyncCenterDialog(
                    factory,
                    (_, __, ___) =>
                        Task.FromResult<CatalogSyncRunResult>(null),
                    _ => Task.FromResult(false))
                {
                    Owner = DialogOwnerHelper.GetSafeOwner(owner)
                };
                try
                {
                    syncCenter.Show();
                    syncCenter.UpdateLayout();
                    await Task.Delay(750).ConfigureAwait(true);
                    var interaction =
                        await VerifyWindowInteractionAsync(syncCenter)
                            .ConfigureAwait(true);
                    Require(
                        interaction.ControlsUnclipped &&
                        interaction.KeyboardNavigationVerified &&
                        interaction.Responsive,
                        "article_clean_sync_center_interaction_failed");
                    CaptureWindow(
                        syncCenter,
                        Path.Combine(
                            outputDirectory,
                            "article-mutation-sync-center-clean-1024x768.png"));
                }
                finally
                {
                    syncCenter.Close();
                }
            }
            finally
            {
                owner.Close();
            }
        }

        private static async Task<WindowInteractionResult>
            VerifyWindowInteractionAsync(Window window)
        {
            Require(window != null && window.IsVisible, "ui_window_not_visible");
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ApplicationIdle);
            var responsive = window.IsVisible &&
                window.Dispatcher.CheckAccess();
            var focusable = EnumerateVisuals(window)
                .OfType<Control>()
                .Where(control =>
                    control.IsVisible &&
                    control.IsEnabled &&
                    control.Focusable &&
                    KeyboardNavigation.GetIsTabStop(control) &&
                    control.ActualWidth > 0 &&
                    control.ActualHeight > 0)
                .ToArray();
            Require(focusable.Length >= 2, "ui_keyboard_targets_missing");

            var first = focusable[0];
            Require(first.Focus(), "ui_initial_focus_failed");
            await window.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.Input);
            var before = Keyboard.FocusedElement;
            var moved = first.MoveFocus(
                new TraversalRequest(FocusNavigationDirection.Next));
            await window.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.Input);
            var after = Keyboard.FocusedElement;
            var keyboardVerified = moved &&
                before != null &&
                after != null &&
                !ReferenceEquals(before, after);

            var viewport = new Rect(
                0,
                0,
                Math.Max(1, window.ActualWidth),
                Math.Max(1, window.ActualHeight));
            var clippingCandidates = EnumerateVisuals(window)
                .OfType<FrameworkElement>()
                .Where(element =>
                    !ReferenceEquals(element, window) &&
                    element.IsVisible &&
                    element.ActualWidth > 0 &&
                    element.ActualHeight > 0 &&
                    (element is Control ||
                     VisualTreeHelper.GetChildrenCount(element) == 0))
                .Distinct()
                .ToArray();
            Require(
                clippingCandidates.Length >= focusable.Length,
                "ui_clipping_targets_missing");
            var unclipped = true;
            foreach (var element in clippingCandidates)
            {
                Rect bounds;
                try
                {
                    bounds = element.TransformToAncestor(window)
                        .TransformBounds(
                            new Rect(
                                0,
                                0,
                                element.ActualWidth,
                                element.ActualHeight));
                }
                catch (InvalidOperationException)
                {
                    unclipped = false;
                    break;
                }
                var clippedHorizontally =
                    bounds.Left < viewport.Left - 1 ||
                    bounds.Right > viewport.Right + 1;
                var clippedVertically =
                    bounds.Top < viewport.Top - 1 ||
                    bounds.Bottom > viewport.Bottom + 1;
                if ((clippedHorizontally &&
                     !IsClippingAllowedByScrollableAncestor(
                         element,
                         horizontal: true)) ||
                    (clippedVertically &&
                     !IsClippingAllowedByScrollableAncestor(
                         element,
                         horizontal: false)))
                {
                    unclipped = false;
                    break;
                }
            }
            return new WindowInteractionResult
            {
                ControlsUnclipped = unclipped,
                KeyboardNavigationVerified = keyboardVerified,
                Responsive = responsive
            };
        }

        private static bool IsClippingAllowedByScrollableAncestor(
            FrameworkElement element,
            bool horizontal)
        {
            var ancestor = VisualTreeHelper.GetParent(element);
            while (ancestor != null)
            {
                var scrollViewer = ancestor as ScrollViewer;
                if (scrollViewer != null)
                {
                    var scrollable = horizontal
                        ? scrollViewer.HorizontalScrollBarVisibility !=
                          ScrollBarVisibility.Disabled &&
                          scrollViewer.ScrollableWidth > 1
                        : scrollViewer.VerticalScrollBarVisibility !=
                          ScrollBarVisibility.Disabled &&
                          scrollViewer.ScrollableHeight > 1;
                    var elementFits = horizontal
                        ? element.ActualWidth <=
                          scrollViewer.ViewportWidth + 1
                        : element.ActualHeight <=
                          scrollViewer.ViewportHeight + 1;
                    if (scrollable && elementFits)
                    {
                        // Only the axis that can genuinely scroll is exempt,
                        // and only when the element can fit in that viewport.
                        return true;
                    }
                }
                ancestor = VisualTreeHelper.GetParent(ancestor);
            }
            return false;
        }

        internal static async Task<bool>
            RunNonFocusableClippingRegressionAsync()
        {
            var canvas = new Canvas
            {
                Height = 420,
                Width = 380
            };
            var first = new Button
            {
                Content = "First",
                Height = 30,
                Width = 80
            };
            var second = new Button
            {
                Content = "Second",
                Height = 30,
                Width = 80
            };
            var clippedLabel = new TextBlock
            {
                Height = 24,
                Text = "Deliberately clipped non-focusable label",
                Width = 180
            };
            Canvas.SetLeft(first, 10);
            Canvas.SetTop(first, 10);
            Canvas.SetLeft(second, 100);
            Canvas.SetTop(second, 10);
            Canvas.SetLeft(clippedLabel, 350);
            Canvas.SetTop(clippedLabel, 80);
            canvas.Children.Add(first);
            canvas.Children.Add(second);
            canvas.Children.Add(clippedLabel);
            var verticalOnlyScrollViewer = new ScrollViewer
            {
                Content = canvas,
                HorizontalScrollBarVisibility =
                    ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility =
                    ScrollBarVisibility.Auto
            };

            var window = new Window
            {
                Content = verticalOnlyScrollViewer,
                Height = 220,
                ShowInTaskbar = false,
                Title = "Win7POS clipping regression",
                Width = 400,
                WindowStartupLocation =
                    WindowStartupLocation.CenterScreen
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var interaction =
                    await VerifyWindowInteractionAsync(window)
                        .ConfigureAwait(true);
                return !interaction.ControlsUnclipped &&
                    interaction.KeyboardNavigationVerified &&
                    interaction.Responsive;
            }
            finally
            {
                window.Close();
            }
        }

        private static IEnumerable<DependencyObject> EnumerateVisuals(
            DependencyObject root)
        {
            if (root == null)
            {
                yield break;
            }
            yield return root;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < count; index += 1)
            {
                foreach (var child in EnumerateVisuals(
                    VisualTreeHelper.GetChild(root, index)))
                {
                    yield return child;
                }
            }
        }

        private static IEnumerable<string> EnumerateVisibleText(
            DependencyObject root)
        {
            foreach (var item in EnumerateVisuals(root))
            {
                var text = (item as TextBlock)?.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    yield return text;
                }
            }
            var window = root as Window;
            if (!string.IsNullOrWhiteSpace(window?.Title))
            {
                yield return window.Title;
            }
        }

        private static void CaptureWindow(Window window, string path)
        {
            var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
            var bitmap = new RenderTargetBitmap(
                width,
                height,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                encoder.Save(output);
            }
        }

        private static void RedactEditableValuesForEvidence(
            DependencyObject root)
        {
            if (root == null) return;
            var textBox = root as TextBox;
            if (textBox != null)
            {
                textBox.SetCurrentValue(
                    TextBox.TextProperty,
                    "[REDACTED]");
            }
            var comboBox = root as ComboBox;
            if (comboBox != null)
            {
                if (comboBox.IsEditable)
                {
                    comboBox.SetCurrentValue(
                        ComboBox.TextProperty,
                        "[REDACTED]");
                }
                else
                {
                    comboBox.SetCurrentValue(
                        ComboBox.SelectedIndexProperty,
                        -1);
                }
            }
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < count; index += 1)
            {
                RedactEditableValuesForEvidence(
                    VisualTreeHelper.GetChild(root, index));
            }
        }

        private static async Task<CleanupManifest> BuildCleanupManifestAsync(
            SqliteConnectionFactory factory,
            string runId,
            string receiptScopeShopId,
            IReadOnlyList<long> localProductIds,
            IReadOnlyList<string> directMutationIds,
            IReadOnlyList<string> conflictReceiptMutationIds)
        {
            var products = new List<CleanupProduct>();
            var mutations = new List<CleanupMutation>();
            using (var connection = factory.Open())
            {
                var scopedLocalProductIds = new HashSet<long>(
                    localProductIds ?? Array.Empty<long>());
                if (IsSafeRunId(runId))
                {
                    var discovered = await connection.QueryAsync<long>(@"
SELECT id
FROM products
WHERE substr(barcode, 1, length(@barcodePrefix)) = @barcodePrefix;",
                        new { barcodePrefix = runId + "-" })
                        .ConfigureAwait(false);
                    scopedLocalProductIds.UnionWith(discovered);
                }
                foreach (var localProductId in scopedLocalProductIds.OrderBy(
                    value => value))
                {
                    var row = await connection
                        .QueryFirstOrDefaultAsync<CleanupProductRow>(@"
SELECT id AS LocalProductId,
       barcode AS Barcode,
       client_product_id AS ClientProductId,
       remote_product_id AS RemoteProductId
FROM products
WHERE id = @localProductId;",
                            new { localProductId })
                        .ConfigureAwait(false);
                    if (row == null) continue;
                    var productMutationIds =
                        (await connection.QueryAsync<string>(@"
SELECT mutation_id
FROM article_mutation_outbox
WHERE local_product_id = @localProductId
ORDER BY local_sequence;",
                            new { localProductId }))
                        .ToArray();
                    products.Add(new CleanupProduct
                    {
                        Barcode = row.Barcode,
                        ClientProductId = row.ClientProductId,
                        MutationIds = productMutationIds,
                        RemoteProductId = row.RemoteProductId
                    });
                }
                if (scopedLocalProductIds.Count > 0)
                {
                    mutations.AddRange(
                        await connection.QueryAsync<CleanupMutation>(@"
SELECT mutation_id AS MutationId,
       remote_product_id AS RemoteProductId,
       remote_price_history_id AS PriceHistoryId,
       remote_stock_movement_id AS StockMovementId,
       ack_status AS AckStatus,
       ack_code AS AckCode,
       last_typed_code AS LastTypedCode,
       resolution_code AS ResolutionCode,
       superseded_by_mutation_id AS SupersededByMutationId
FROM article_mutation_outbox
WHERE local_product_id IN @localProductIds
ORDER BY local_product_id, local_sequence;",
                            new
                            {
                                localProductIds =
                                    scopedLocalProductIds.ToArray()
                            })
                        .ConfigureAwait(false));
                }
            }
            var mutationIds = mutations
                .Select(item => item.MutationId)
                .Concat(directMutationIds ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var remoteProductIds = products
                .Select(item => item.RemoteProductId)
                .Concat(mutations.Select(item => item.RemoteProductId))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var priceHistoryIds = mutations
                .Select(item => item.PriceHistoryId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var stockMovementIds = mutations
                .Select(item => item.StockMovementId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var conflictReceiptReferences =
                (conflictReceiptMutationIds ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .Select(value => new CleanupReceiptReference
                {
                    MutationId = value,
                    ShopId = receiptScopeShopId,
                    Status = PosArticleMutationStatusPolicy
                        .IdempotencyPayloadMismatch,
                    Table = "pos_article_mutation_conflict_receipts"
                })
                .ToArray();
            var mutationReceiptReferences = mutationIds
                .Select(value => new CleanupReceiptReference
                {
                    MutationId = value,
                    ShopId = receiptScopeShopId,
                    Status = mutations
                        .Where(item => string.Equals(
                            item.MutationId,
                            value,
                            StringComparison.Ordinal))
                        .Select(item => FirstNonEmpty(
                            item.AckStatus,
                            item.LastTypedCode,
                            "unknown"))
                        .FirstOrDefault() ?? "unknown",
                    Table = "pos_article_mutation_receipts"
                })
                .ToArray();
            return new CleanupManifest
            {
                CreatedAtUtc = UtcNow(),
                ClientMutationIds = mutationIds,
                ConflictReceiptReferences = conflictReceiptReferences,
                ExpectedCounts = new CleanupExpectedCounts
                {
                    Categories = 0,
                    ConflictReceipts =
                        conflictReceiptReferences.Length,
                    ManualStockMovements =
                        stockMovementIds.Length,
                    MutationReceipts =
                        mutationReceiptReferences.Length,
                    Prices = priceHistoryIds.Length,
                    Products = remoteProductIds.Length,
                    Shops = 0,
                    Suppliers = 0,
                    SyncEvents = null
                },
                ManualStockMovementIds = stockMovementIds,
                MutationReceiptReferences =
                    mutationReceiptReferences,
                Mutations = mutations.ToArray(),
                PriceHistoryIds = priceHistoryIds,
                Products = products.ToArray(),
                ReceiptScopeShopId = receiptScopeShopId,
                RemoteProductIds = remoteProductIds,
                RunId = runId,
                Scope = "exact_synthetic_ids_only",
                SyntheticCategoryIds = Array.Empty<string>(),
                SyntheticShopIds = Array.Empty<string>(),
                SyntheticSupplierIds = Array.Empty<string>(),
                SyncEventIds = Array.Empty<string>(),
                SyncEventResolution = new CleanupSyncEventResolution
                {
                    ExactManualStockMovementIds =
                        stockMovementIds,
                    ExactPriceHistoryIds = priceHistoryIds,
                    ExactRemoteProductIds = remoteProductIds,
                    RequiredBeforeDelete = true
                }
            };
        }

        private static void ValidateCleanupManifestForPass(
            CleanupManifest cleanup)
        {
            Require(
                cleanup != null &&
                !string.IsNullOrWhiteSpace(cleanup.RunId) &&
                !string.IsNullOrWhiteSpace(cleanup.ReceiptScopeShopId),
                "cleanup_manifest_scope_missing");
            Require(
                cleanup.Products != null &&
                cleanup.Products.Length >= 3 &&
                cleanup.Products.All(item =>
                    item != null &&
                    !string.IsNullOrWhiteSpace(item.Barcode) &&
                    !string.IsNullOrWhiteSpace(item.ClientProductId) &&
                    !string.IsNullOrWhiteSpace(item.RemoteProductId)),
                "cleanup_manifest_product_ids_incomplete");
            Require(
                cleanup.ClientMutationIds != null &&
                cleanup.ClientMutationIds.Length > 0 &&
                cleanup.MutationReceiptReferences != null &&
                cleanup.MutationReceiptReferences.Length ==
                    cleanup.ClientMutationIds.Length &&
                cleanup.MutationReceiptReferences.All(item =>
                    item != null &&
                    string.Equals(
                        item.ShopId,
                        cleanup.ReceiptScopeShopId,
                        StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(item.MutationId) &&
                    !string.Equals(
                        item.Status,
                        "unknown",
                        StringComparison.Ordinal)),
                "cleanup_manifest_mutation_receipts_incomplete");
            Require(
                cleanup.ConflictReceiptReferences != null &&
                cleanup.ConflictReceiptReferences.Length == 1 &&
                cleanup.ConflictReceiptReferences.All(item =>
                    item != null &&
                    string.Equals(
                        item.ShopId,
                        cleanup.ReceiptScopeShopId,
                        StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(item.MutationId)),
                "cleanup_manifest_conflict_receipts_incomplete");
            Require(
                cleanup.PriceHistoryIds != null &&
                cleanup.PriceHistoryIds.Length == 2 &&
                cleanup.PriceHistoryIds.All(value =>
                    !string.IsNullOrWhiteSpace(value)) &&
                cleanup.ManualStockMovementIds != null &&
                cleanup.ManualStockMovementIds.Length == 2 &&
                cleanup.ManualStockMovementIds.All(value =>
                    !string.IsNullOrWhiteSpace(value)),
                "cleanup_manifest_remote_child_ids_incomplete");
        }

        private static void WriteCleanupPrompt(
            string path,
            CleanupManifest cleanup)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# NEXT CODEX MAC FINAL CLEANUP");
            builder.AppendLine();
            builder.AppendLine("Operate in the Admin repository on Mac against public staging only.");
            builder.AppendLine("This prompt is complete: do not ask the user to reconstruct IDs or scope.");
            builder.AppendLine("Do not deploy a Worker, change billing, alter migrations/schema, or touch production.");
            builder.AppendLine();
            builder.AppendLine("1. Repo Sync Check both `XNIW/merchandise-control-admin-web` and `XNIW/Win7POS`; use clean exact-main worktrees and record both SHAs.");
            builder.AppendLine("2. Read Win7POS main handoff `docs/HANDOFFS/WIN7POS_POS_ARTICLE_SYNC_FINAL_ACCEPTANCE.md` and its evidence pointers.");
            builder.AppendLine("3. Load `CLEANUP-MANIFEST.json`; require QA run ID exactly `" + cleanup.RunId + "`, scope `exact_synthetic_ids_only`, and pre-existing receipt scope shop ID exactly `" + cleanup.ReceiptScopeShopId + "`. The scope shop is not a deletion target.");
            builder.AppendLine("4. Resolve every manifest reference on staging before any write. Require all exact remote product, price-history, manual-stock-movement and mutation IDs to belong to receipt scope shop `" + cleanup.ReceiptScopeShopId + "`; resolve receipt UUIDs only by each exact `(shop_id, mutation_id, status)` reference in the manifest.");
            builder.AppendLine("5. Resolve disposable `sync_events` only when their exact entity IDs reference a manifest product, price-history, or manual-stock-movement ID. Record the exact event IDs and count before deletion.");
            builder.AppendLine("6. Prove no pre-existing row is targeted: every product barcode/client identity must match this run, every price/movement must reference a manifest product and mutation, synthetic category/supplier/shop arrays must remain empty unless explicitly populated in the manifest, and sales/revenue targets must be zero.");
            builder.AppendLine("7. Compare resolved counts with `expectedCounts`. `syncEvents=null` means derive the exact count from the manifest entity IDs, freeze those event IDs in cleanup evidence, and then treat that frozen count as mandatory. Any other mismatch is a hard rollback.");
            builder.AppendLine("8. Begin one guarded staging transaction with short lock/statement timeouts and lock every resolved target row. Re-run all identity/count predicates inside the transaction.");
            builder.AppendLine("9. Preserve immutable `audit_logs`. Use only the existing test-fixture receipt cleanup guard; do not disable arbitrary triggers or alter schema.");
            builder.AppendLine("10. Delete only the exact resolved synthetic rows, in dependency order: disposable sync events; conflict receipts; mutation receipts; manual stock movements; price history; products; then synthetic category/supplier/shop/runtime rows only if their exact IDs are non-empty in the manifest.");
            builder.AppendLine("11. Roll back on any row-count mismatch, unexpected foreign key, non-synthetic reference, sale/revenue row, missing ID, extra ID, or baseline drift.");
            builder.AppendLine("12. Before commit, verify zero mutable residuals for every exact manifest ID/reference and verify the non-synthetic catalog baseline is unchanged; then commit once.");
            builder.AppendLine("13. After commit, repeat zero-residual and baseline checks from a fresh read-only transaction. Preserve immutable audit and write bounded redacted cleanup evidence.");
            builder.AppendLine("14. Update Admin TASK-143, TASK-144, TASK-145, TASK-146 and TASK-147 as applicable to `DONE`, `USER_CONFIRMED_CLOSURE`, `P0/P1/P2/P3=0/0/0/0`.");
            builder.AppendLine("15. Create a focused docs-only Admin branch/PR recording the exact cleanup counts, immutable audit preservation, staging-only scope, and `DONE_CROSS_REPO_POS_ARTICLE_SYNC`.");
            builder.AppendLine("16. Wait for all required CI, CodeQL and supply-chain checks; fix any P0/P1/P2, obtain independent review, and merge normally.");
            builder.AppendLine("17. Confirm Admin main equals origin/main, the closeout merge is an ancestor, the worktree is clean, and no Worker deployment occurred.");
            builder.AppendLine("18. Return exactly `DONE_CROSS_REPO_POS_ARTICLE_SYNC` with Admin final SHA, cleanup PR/merge, exact deleted counts and zero-residual proof.");
            builder.AppendLine();
            builder.AppendLine("Exact product scope:");
            foreach (var product in cleanup.Products)
            {
                builder.AppendLine(
                    "   - remoteProductId `" +
                    product.RemoteProductId +
                    "`, clientProductId `" +
                    product.ClientProductId +
                    "`, barcode `" +
                    product.Barcode + "`");
            }
            builder.AppendLine();
            builder.AppendLine("All other exact IDs and receipt references are authoritative only in the adjacent `CLEANUP-MANIFEST.json`; never use a wildcard or run-prefix-only delete.");
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }

        private static async Task WriteEvidenceAsync(
            SqliteConnectionFactory factory,
            string outputDirectory,
            StagingArticleMutationAcceptanceResult result)
        {
            try
            {
                Directory.CreateDirectory(outputDirectory);
                WriteJson(
                    Path.Combine(
                        outputDirectory,
                        "article-mutation-results.json"),
                    result);
                var snapshot = await ReadOutboxEvidenceAsync(factory)
                    .ConfigureAwait(false);
                WriteJson(
                    Path.Combine(outputDirectory, "local-outbox-state.json"),
                    snapshot);
                File.WriteAllText(
                    Path.Combine(
                        outputDirectory,
                        "price-history-counts.txt"),
                    "priceMutationRows=" +
                    result.PriceMutationRows.ToString(
                        CultureInfo.InvariantCulture) +
                    Environment.NewLine +
                    "priceHistoryMutationRows=" +
                    result.PriceHistoryMutationRows.ToString(
                        CultureInfo.InvariantCulture) +
                    Environment.NewLine +
                    "duplicateGroups=" +
                    result.PriceHistoryDuplicateGroups.ToString(
                        CultureInfo.InvariantCulture) +
                    Environment.NewLine,
                    new UTF8Encoding(false));
                File.WriteAllText(
                    Path.Combine(
                        outputDirectory,
                        "stock-movement-counts.txt"),
                    "manualAdjustmentRows=" +
                    result.StockAdjustmentRows.ToString(
                        CultureInfo.InvariantCulture) +
                    Environment.NewLine +
                    "signedDelta=" +
                    result.StockQuantityDelta.ToString(
                        CultureInfo.InvariantCulture) +
                    Environment.NewLine +
                    "duplicateGroups=" +
                    result.StockDuplicateGroups.ToString(
                        CultureInfo.InvariantCulture) +
                    Environment.NewLine +
                    "salesLaneRows=" +
                    result.SalesRows.ToString(
                        CultureInfo.InvariantCulture) +
                    Environment.NewLine,
                    new UTF8Encoding(false));
                File.WriteAllText(
                    Path.Combine(outputDirectory, "no-echo-result.txt"),
                    "canonicalPull=" + result.CanonicalPull +
                    Environment.NewLine +
                    "outboundEchoCreated=" + (!result.ZeroEcho) +
                    Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
                // The primary harness result remains non-zero when evidence I/O
                // itself fails.
                result.Passed = false;
                result.Code = "article_evidence_write_failed";
            }
        }

        private static async Task<OutboxEvidence> ReadOutboxEvidenceAsync(
            SqliteConnectionFactory factory)
        {
            if (factory == null)
                return new OutboxEvidence();
            using (var connection = factory.Open())
            {
                var states = (await connection.QueryAsync<StateEvidence>(@"
SELECT state AS State,
       COUNT(1) AS Count
FROM article_mutation_outbox
GROUP BY state
ORDER BY state;")).ToArray();
                var codes = (await connection.QueryAsync<CodeEvidence>(@"
SELECT COALESCE(last_typed_code, 'none') AS Code,
       COUNT(1) AS Count
FROM article_mutation_outbox
GROUP BY COALESCE(last_typed_code, 'none')
ORDER BY COALESCE(last_typed_code, 'none');")).ToArray();
                return new OutboxEvidence
                {
                    Codes = codes,
                    States = states,
                    RawPayloadIncluded = false
                };
            }
        }

        private static async Task<long> CountAsync(
            SqliteConnectionFactory factory,
            string sql,
            object parameters = null)
        {
            using (var connection = factory.Open())
            {
                return await connection.ExecuteScalarAsync<long>(
                        sql,
                        parameters)
                    .ConfigureAwait(false);
            }
        }

        private static bool IsSafeRunId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length > 64 ||
                !value.StartsWith(
                    "ASUSART_FINAL_",
                    StringComparison.Ordinal))
            {
                return false;
            }
            foreach (var character in value)
            {
                if (!((character >= 'A' && character <= 'Z') ||
                      (character >= '0' && character <= '9') ||
                      character == '_'))
                {
                    return false;
                }
            }
            return true;
        }

        private static string RunScopedId(string runId, string suffix)
        {
            var value = runId.ToLowerInvariant() + "-" + suffix;
            Require(
                PosArticleMutationIntentPolicy.IsSafeId(value),
                "run_scoped_identifier_invalid");
            return value;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return (values ?? Array.Empty<string>())
                .FirstOrDefault(value =>
                    !string.IsNullOrWhiteSpace(value)) ??
                string.Empty;
        }

        private static string SafeCode(string value)
        {
            var source = (value ?? string.Empty).Trim().ToLowerInvariant();
            var builder = new StringBuilder();
            foreach (var character in source)
            {
                if (builder.Length >= 120)
                    break;
                if ((character >= 'a' && character <= 'z') ||
                    (character >= '0' && character <= '9') ||
                    character == '_' ||
                    character == '-' ||
                    character == '.')
                {
                    builder.Append(character);
                }
                else if (builder.Length > 0 &&
                         builder[builder.Length - 1] != '_')
                {
                    builder.Append('_');
                }
            }
            return builder.Length == 0
                ? "article_acceptance_failure"
                : builder.ToString().TrimEnd('_');
        }

        private static string UtcNow()
        {
            return DateTimeOffset.UtcNow.ToString(
                "O",
                CultureInfo.InvariantCulture);
        }

        private static void Require(bool condition, string code)
        {
            if (!condition)
                throw new InvalidOperationException(code);
        }

        private static void WriteJson(string path, object value)
        {
            var serializer = new DataContractJsonSerializer(value.GetType());
            using (var output = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                serializer.WriteObject(output, value);
            }
        }

        private static T ReadJson<T>(string path) where T : class
        {
            Require(
                File.Exists(path) &&
                new FileInfo(path).Length > 0 &&
                new FileInfo(path).Length <= 256 * 1024,
                "article_restart_checkpoint_missing");
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                return serializer.ReadObject(input) as T;
            }
        }

        private sealed class VerifiedReferenceRow
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string RemoteId { get; set; }
        }

        private sealed class CanonicalProductRow
        {
            public string CatalogUpdatedAtRevision { get; set; }
            public string LatestAckAuthoritativeRevision { get; set; }
            public string LocalBarcode { get; set; }
            public string LocalCategoryRemoteId { get; set; }
            public int LocalIsActive { get; set; }
            public string LocalItemNumber { get; set; }
            public long LocalProductId { get; set; }
            public string LocalPrimaryName { get; set; }
            public int LocalPurchasePrice { get; set; }
            public long LocalRetailPrice { get; set; }
            public string LocalSecondaryName { get; set; }
            public int LocalStockQuantity { get; set; }
            public string LocalSupplierRemoteId { get; set; }
            public string RemoteBaseRevision { get; set; }
            public string RemoteProductId { get; set; }
            public string ShadowBarcode { get; set; }
            public string ShadowCategoryRemoteId { get; set; }
            public int ShadowIsActive { get; set; }
            public string ShadowItemNumber { get; set; }
            public string ShadowPrimaryName { get; set; }
            public int ShadowPurchasePrice { get; set; }
            public long ShadowRetailPrice { get; set; }
            public string ShadowSecondaryName { get; set; }
            public int ShadowStockQuantity { get; set; }
            public string ShadowSupplierRemoteId { get; set; }
        }

        private sealed class ProductIdentityRow
        {
            public string ClientProductId { get; set; }
            public long LocalProductId { get; set; }
            public long MaximumLocalSequence { get; set; }
            public string RemoteBaseRevision { get; set; }
            public string RemoteProductId { get; set; }
        }

        private sealed class OfflineCreateAtomicSnapshot
        {
            public string CreateClientProductId { get; set; }
            public long CreateOutboxRows { get; set; }
            public string ProductClientProductId { get; set; }
            public long ProductRows { get; set; }
        }

        private sealed class ReplaySourceRow
        {
            public string AckCode { get; set; }
            public string AckAttemptToken { get; set; }
            public bool AckRetryable { get; set; }
            public string AckStatus { get; set; }
            public bool AckTerminal { get; set; }
            public string AuthoritativeRevision { get; set; }
            public string CanonicalPayloadJson { get; set; }
            public string CatalogRevision { get; set; }
            public string IdempotencyKey { get; set; }
            public string MutationId { get; set; }
            public string PayloadHash { get; set; }
            public string RemoteAssignedProductId { get; set; }
        }

        [DataContract]
        private sealed class ArticleRestartCheckpoint
        {
            [DataMember(Name = "barcode")]
            public string Barcode { get; set; }

            [DataMember(Name = "clientProductId")]
            public string ClientProductId { get; set; }

            [DataMember(Name = "localProductId")]
            public long LocalProductId { get; set; }

            [DataMember(Name = "runId")]
            public string RunId { get; set; }

        }

        private sealed class CleanupProductRow
        {
            public string Barcode { get; set; }
            public string ClientProductId { get; set; }
            public long LocalProductId { get; set; }
            public string RemoteProductId { get; set; }
        }

        private sealed class ProductReferenceRow
        {
            public string CategoryRemoteId { get; set; }
            public string SupplierRemoteId { get; set; }
        }

        [DataContract]
        private sealed class CleanupMutation
        {
            [DataMember(Name = "ackCode")]
            public string AckCode { get; set; }

            [DataMember(Name = "ackStatus")]
            public string AckStatus { get; set; }

            [DataMember(Name = "lastTypedCode")]
            public string LastTypedCode { get; set; }

            [DataMember(Name = "mutationId")]
            public string MutationId { get; set; }

            [DataMember(Name = "priceHistoryId")]
            public string PriceHistoryId { get; set; }

            [DataMember(Name = "remoteProductId")]
            public string RemoteProductId { get; set; }

            [DataMember(Name = "resolutionCode")]
            public string ResolutionCode { get; set; }

            [DataMember(Name = "stockMovementId")]
            public string StockMovementId { get; set; }

            [DataMember(Name = "supersededByMutationId")]
            public string SupersededByMutationId { get; set; }
        }

        private sealed class FinalCounts
        {
            // This is the total unresolved failed_blocked count. The property
            // name is kept for the stable evidence contract.
            public long BlockedConflicts { get; set; }
            public long Completed { get; set; }
            public long InProgress { get; set; }
            public long Pending { get; set; }
            public long PriceHistoryDuplicateGroups { get; set; }
            public long PriceHistoryMutationRows { get; set; }
            public long PriceMutationRows { get; set; }
            public long RetryWait { get; set; }
            public long SalesRows { get; set; }
            public long StockAdjustmentRows { get; set; }
            public long StockDuplicateGroups { get; set; }
            public long StockQuantityDelta { get; set; }
            public long WaitingDependency { get; set; }
        }

        private sealed class UiVerificationResult
        {
            public bool ConflictNonModal { get; set; }
            public bool ControlsUnclipped { get; set; }
            public bool KeyboardNavigationVerified { get; set; }
            public bool LanguagesVerified { get; set; }
            public bool Responsive { get; set; }
        }

        private sealed class WindowInteractionResult
        {
            public bool ControlsUnclipped { get; set; }
            public bool KeyboardNavigationVerified { get; set; }
            public bool Responsive { get; set; }
        }

        [DataContract]
        private sealed class PersistedCanonicalIntent
        {
            [DataMember(Name = "baseRevision")]
            public string BaseRevision { get; set; }

            [DataMember(Name = "changes")]
            public PersistedChanges Changes { get; set; }

            [DataMember(Name = "clientProductId")]
            public string ClientProductId { get; set; }

            [DataMember(Name = "createdAt")]
            public string CreatedAt { get; set; }

            [DataMember(Name = "fieldMask")]
            public string[] FieldMask { get; set; }

            [DataMember(Name = "idempotencyKey")]
            public string IdempotencyKey { get; set; }

            [DataMember(Name = "localSequence")]
            public long LocalSequence { get; set; }

            [DataMember(Name = "mutationId")]
            public string MutationId { get; set; }

            [DataMember(Name = "mutationKind")]
            public string MutationKind { get; set; }

            [DataMember(Name = "occurredAt")]
            public string OccurredAt { get; set; }

            [DataMember(Name = "remoteProductId")]
            public string RemoteProductId { get; set; }
        }

        [DataContract]
        private sealed class PersistedChanges
        {
            [DataMember(Name = "barcode", EmitDefaultValue = false)]
            public string Barcode { get; set; }

            [DataMember(Name = "categoryId", EmitDefaultValue = false)]
            public string CategoryId { get; set; }

            [DataMember(Name = "itemNumber", EmitDefaultValue = false)]
            public string ItemNumber { get; set; }

            [DataMember(Name = "price", EmitDefaultValue = false)]
            public decimal? Price { get; set; }

            [DataMember(Name = "primaryName", EmitDefaultValue = false)]
            public string PrimaryName { get; set; }

            [DataMember(Name = "purchasePrice", EmitDefaultValue = false)]
            public decimal? PurchasePrice { get; set; }

            [DataMember(Name = "quantityDelta", EmitDefaultValue = false)]
            public decimal? QuantityDelta { get; set; }

            [DataMember(Name = "reason", EmitDefaultValue = false)]
            public string Reason { get; set; }

            [DataMember(Name = "retailPrice", EmitDefaultValue = false)]
            public decimal? RetailPrice { get; set; }

            [DataMember(Name = "secondaryName", EmitDefaultValue = false)]
            public string SecondaryName { get; set; }

            [DataMember(Name = "stockQuantity", EmitDefaultValue = false)]
            public decimal? StockQuantity { get; set; }

            [DataMember(Name = "supplierId", EmitDefaultValue = false)]
            public string SupplierId { get; set; }
        }

        [DataContract]
        internal sealed class StagingArticleMutationAcceptanceResult
        {
            public PosOnlineSyncSupervisorHost ActiveHost { get; set; }

            [DataMember(Name = "ackCatalogRevisionMatch")]
            public bool AckCatalogRevisionMatch { get; set; }

            [DataMember(Name = "blockedConflicts")]
            public long BlockedConflicts { get; set; }

            [DataMember(Name = "canonicalPull")]
            public bool CanonicalPull { get; set; }

            [DataMember(Name = "canonicalValuesMatch")]
            public bool CanonicalValuesMatch { get; set; }

            [DataMember(Name = "cleanupManifestCreated")]
            public bool CleanupManifestCreated { get; set; }

            [DataMember(Name = "code")]
            public string Code { get; set; }

            [DataMember(Name = "completed")]
            public long Completed { get; set; }

            [DataMember(Name = "completedAtUtc")]
            public string CompletedAtUtc { get; set; }

            [DataMember(Name = "conflictResolved")]
            public bool ConflictResolved { get; set; }

            [DataMember(Name = "deactivateReactivate")]
            public bool DeactivateReactivate { get; set; }

            [DataMember(Name = "dependentCanonicalReadback")]
            public bool DependentCanonicalReadback { get; set; }

            [DataMember(Name = "dependentEditPersisted")]
            public bool DependentEditPersisted { get; set; }

            [DataMember(Name = "dependentSequenceApplied")]
            public bool DependentSequenceApplied { get; set; }

            [DataMember(Name = "differentPayloadMismatch")]
            public bool DifferentPayloadMismatch { get; set; }

            [DataMember(Name = "duplicateProduct")]
            public bool DuplicateProduct { get; set; }

            [DataMember(Name = "duplicateIdentityIndependent")]
            public bool DuplicateIdentityIndependent { get; set; }

            [DataMember(Name = "exceptionType")]
            public string ExceptionType { get; set; }

            [DataMember(Name = "hardwareActions")]
            public int HardwareActions { get; set; }

            [DataMember(Name = "harnessRestartSurvived")]
            public bool HarnessRestartSurvived { get; set; }

            [DataMember(Name = "inProgress")]
            public long InProgress { get; set; }

            [DataMember(Name = "lifecycleCanonicalReadback")]
            public bool LifecycleCanonicalReadback { get; set; }

            [DataMember(Name = "offlineCreateAtomic")]
            public bool OfflineCreateAtomic { get; set; }

            [DataMember(Name = "passed")]
            public bool Passed { get; set; }

            [DataMember(Name = "pending")]
            public long Pending { get; set; }

            [DataMember(Name = "priceHistoryDuplicateGroups")]
            public long PriceHistoryDuplicateGroups { get; set; }

            [DataMember(Name = "priceHistoryMutationRows")]
            public long PriceHistoryMutationRows { get; set; }

            [DataMember(Name = "priceMutationRows")]
            public long PriceMutationRows { get; set; }

            [DataMember(Name = "purchasePrice")]
            public bool PurchasePrice { get; set; }

            [DataMember(Name = "remoteIdentityAssigned")]
            public bool RemoteIdentityAssigned { get; set; }

            [DataMember(Name = "restartRequired")]
            public bool RestartRequired { get; set; }

            [DataMember(Name = "remoteChildIdsAssigned")]
            public bool RemoteChildIdsAssigned { get; set; }

            [DataMember(Name = "retailPrice")]
            public bool RetailPrice { get; set; }

            [DataMember(Name = "retryWait")]
            public long RetryWait { get; set; }

            [DataMember(Name = "salesRows")]
            public long SalesRows { get; set; }

            [DataMember(Name = "sameMutationReplay")]
            public bool SameMutationReplay { get; set; }

            [DataMember(Name = "replayAckPreserved")]
            public bool ReplayAckPreserved { get; set; }

            [DataMember(Name = "staleConflict")]
            public bool StaleConflict { get; set; }

            [DataMember(Name = "startedAtUtc")]
            public string StartedAtUtc { get; set; }

            [DataMember(Name = "stockAdjustmentRows")]
            public long StockAdjustmentRows { get; set; }

            [DataMember(Name = "stockDuplicateGroups")]
            public long StockDuplicateGroups { get; set; }

            [DataMember(Name = "stockMinusTwo")]
            public bool StockMinusTwo { get; set; }

            [DataMember(Name = "stockPlusFive")]
            public bool StockPlusFive { get; set; }

            [DataMember(Name = "stockQuantityDelta")]
            public long StockQuantityDelta { get; set; }

            [DataMember(Name = "uiScreenshots")]
            public int UiScreenshots { get; set; }

            [DataMember(Name = "uiLanguagesVerified")]
            public bool UiLanguagesVerified { get; set; }

            [DataMember(Name = "uiConflictNonModal")]
            public bool UiConflictNonModal { get; set; }

            [DataMember(Name = "uiControlsUnclipped")]
            public bool UiControlsUnclipped { get; set; }

            [DataMember(Name = "uiKeyboardNavigationVerified")]
            public bool UiKeyboardNavigationVerified { get; set; }

            [DataMember(Name = "uiResponsive")]
            public bool UiResponsive { get; set; }

            [DataMember(Name = "unrelatedProductContinued")]
            public bool UnrelatedProductContinued { get; set; }

            [DataMember(Name = "waitingDependency")]
            public long WaitingDependency { get; set; }

            [DataMember(Name = "zeroEcho")]
            public bool ZeroEcho { get; set; }
        }

        [DataContract]
        private sealed class CleanupManifest
        {
            [DataMember(Name = "clientMutationIds")]
            public string[] ClientMutationIds { get; set; }

            [DataMember(Name = "conflictReceiptReferences")]
            public CleanupReceiptReference[] ConflictReceiptReferences {
                get;
                set;
            }

            [DataMember(Name = "createdAtUtc")]
            public string CreatedAtUtc { get; set; }

            [DataMember(Name = "expectedCounts")]
            public CleanupExpectedCounts ExpectedCounts { get; set; }

            [DataMember(Name = "manualStockMovementIds")]
            public string[] ManualStockMovementIds { get; set; }

            [DataMember(Name = "mutationReceiptReferences")]
            public CleanupReceiptReference[] MutationReceiptReferences {
                get;
                set;
            }

            [DataMember(Name = "mutations")]
            public CleanupMutation[] Mutations { get; set; }

            [DataMember(Name = "priceHistoryIds")]
            public string[] PriceHistoryIds { get; set; }

            [DataMember(Name = "products")]
            public CleanupProduct[] Products { get; set; }

            [DataMember(Name = "remoteProductIds")]
            public string[] RemoteProductIds { get; set; }

            [DataMember(Name = "receiptScopeShopId")]
            public string ReceiptScopeShopId { get; set; }

            [DataMember(Name = "runId")]
            public string RunId { get; set; }

            [DataMember(Name = "scope")]
            public string Scope { get; set; }

            [DataMember(Name = "syntheticCategoryIds")]
            public string[] SyntheticCategoryIds { get; set; }

            [DataMember(Name = "syntheticShopIds")]
            public string[] SyntheticShopIds { get; set; }

            [DataMember(Name = "syntheticSupplierIds")]
            public string[] SyntheticSupplierIds { get; set; }

            [DataMember(Name = "syncEventIds")]
            public string[] SyncEventIds { get; set; }

            [DataMember(Name = "syncEventResolution")]
            public CleanupSyncEventResolution SyncEventResolution {
                get;
                set;
            }
        }

        [DataContract]
        private sealed class CleanupExpectedCounts
        {
            [DataMember(Name = "categories")]
            public int Categories { get; set; }

            [DataMember(Name = "conflictReceipts")]
            public int ConflictReceipts { get; set; }

            [DataMember(Name = "manualStockMovements")]
            public int ManualStockMovements { get; set; }

            [DataMember(Name = "mutationReceipts")]
            public int MutationReceipts { get; set; }

            [DataMember(Name = "prices")]
            public int Prices { get; set; }

            [DataMember(Name = "products")]
            public int Products { get; set; }

            [DataMember(Name = "shops")]
            public int Shops { get; set; }

            [DataMember(Name = "suppliers")]
            public int Suppliers { get; set; }

            [DataMember(Name = "syncEvents")]
            public int? SyncEvents { get; set; }
        }

        [DataContract]
        private sealed class CleanupReceiptReference
        {
            [DataMember(Name = "mutationId")]
            public string MutationId { get; set; }

            [DataMember(Name = "shopId")]
            public string ShopId { get; set; }

            [DataMember(Name = "status")]
            public string Status { get; set; }

            [DataMember(Name = "table")]
            public string Table { get; set; }
        }

        [DataContract]
        private sealed class CleanupSyncEventResolution
        {
            [DataMember(Name = "exactManualStockMovementIds")]
            public string[] ExactManualStockMovementIds { get; set; }

            [DataMember(Name = "exactPriceHistoryIds")]
            public string[] ExactPriceHistoryIds { get; set; }

            [DataMember(Name = "exactRemoteProductIds")]
            public string[] ExactRemoteProductIds { get; set; }

            [DataMember(Name = "requiredBeforeDelete")]
            public bool RequiredBeforeDelete { get; set; }
        }

        [DataContract]
        private sealed class CleanupProduct
        {
            [DataMember(Name = "barcode")]
            public string Barcode { get; set; }

            [DataMember(Name = "clientProductId")]
            public string ClientProductId { get; set; }

            [DataMember(Name = "mutationIds")]
            public string[] MutationIds { get; set; }

            [DataMember(Name = "remoteProductId")]
            public string RemoteProductId { get; set; }
        }

        [DataContract]
        private sealed class OutboxEvidence
        {
            [DataMember(Name = "codes")]
            public CodeEvidence[] Codes { get; set; } = Array.Empty<CodeEvidence>();

            [DataMember(Name = "rawPayloadIncluded")]
            public bool RawPayloadIncluded { get; set; }

            [DataMember(Name = "states")]
            public StateEvidence[] States { get; set; } =
                Array.Empty<StateEvidence>();
        }

        [DataContract]
        private sealed class StateEvidence
        {
            [DataMember(Name = "count")]
            public long Count { get; set; }

            [DataMember(Name = "state")]
            public string State { get; set; }
        }

        [DataContract]
        private sealed class CodeEvidence
        {
            [DataMember(Name = "code")]
            public string Code { get; set; }

            [DataMember(Name = "count")]
            public long Count { get; set; }
        }
    }
}
