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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dapper;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;
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
            CancellationToken cancellationToken)
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

                // The stopped host is the network gate for the offline section.
                await initialHost.StopAsync().ConfigureAwait(false);

                var barcodeA0 = runId + "-A0";
                var barcodeA1 = runId + "-A1";
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

                var created = await workflow.GetByBarcodeDetailsAsync(barcodeA0)
                    .ConfigureAwait(true);
                Require(created != null, "offline_create_local_row_missing");
                localProductIds.Add(created.Id);
                Require(
                    await CountAsync(
                        factory,
                        @"SELECT COUNT(1)
FROM products
WHERE id = @id
  AND remote_product_id IS NULL;",
                        new { id = created.Id }).ConfigureAwait(false) == 1 &&
                    await CountAsync(
                        factory,
                        @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE local_product_id = @id
  AND mutation_kind = 'product_create'
  AND state = 'pending';",
                        new { id = created.Id }).ConfigureAwait(false) == 1,
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
                dependent.SelectedCategory = dependent.Categories.First(
                    item => item.Id == category.Id);
                dependent.SelectedSupplier = dependent.Suppliers.First(
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

                // Dispose and reconstruct the supervisor to prove durable restart.
                initialHost.Dispose();
                activeHost = new PosOnlineSyncSupervisorHost(factory);
                var generation = await activeHost
                    .AttachCurrentTrustAsync(cancellationToken)
                    .ConfigureAwait(false);
                Require(generation != null, "restart_trust_attach_failed");
                Require(
                    await CountAsync(
                        factory,
                        @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE local_product_id = @id
  AND state IN ('pending', 'waiting_dependency');",
                        new { id = created.Id }).ConfigureAwait(false) == 2,
                    "restart_did_not_preserve_dependencies");
                result.HarnessRestartSurvived = true;

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
                Require(
                    Guid.TryParse(
                        (await ReadIdentityAsync(factory, duplicateRow.Id)
                            .ConfigureAwait(false))?.RemoteProductId,
                        out _),
                    "duplicate_remote_identity_missing");
                result.DuplicateProduct = true;

                await workflow.SetProductActiveAsync(duplicateRow.Id, false)
                    .ConfigureAwait(true);
                await DrainArticlesAsync(
                        factory,
                        activeHost,
                        cancellationToken,
                        allowBlocked: false)
                    .ConfigureAwait(true);
                await workflow.SetProductActiveAsync(duplicateRow.Id, true)
                    .ConfigureAwait(true);
                await DrainArticlesAsync(
                        factory,
                        activeHost,
                        cancellationToken,
                        allowBlocked: false)
                    .ConfigureAwait(true);
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
                    string.Equals(
                        replayResult.Ack?.AttemptToken,
                        replaySource.AckAttemptToken,
                        StringComparison.Ordinal),
                    "same_mutation_replay_not_observed");
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
                    counts.BlockedConflicts == 1,
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

                await CaptureArticleUiAsync(
                        factory,
                        workflow,
                        created.Id,
                        outputDirectory)
                    .ConfigureAwait(true);
                result.UiScreenshots = 2;

                var cleanup = await BuildCleanupManifestAsync(
                        factory,
                        runId,
                        localProductIds,
                        directMutationIds)
                    .ConfigureAwait(false);
                WriteJson(
                    Path.Combine(outputDirectory, "CLEANUP-MANIFEST.json"),
                    cleanup);
                WriteCleanupPrompt(
                    Path.Combine(
                        outputDirectory,
                        "NEXT-CODEX-MAC-FINAL-CLEANUP.md"),
                    cleanup);
                result.CleanupManifestCreated = true;
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
                    return;

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
            var sql = "SELECT id AS Id, name AS Name FROM " + table +
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
       ack_attempt_token AS AckAttemptToken
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
WHERE state = 'failed_blocked'
  AND last_typed_code = 'failed_conflict';"),
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

        private static async Task CaptureArticleUiAsync(
            SqliteConnectionFactory factory,
            ProductsWorkflowService workflow,
            long articleALocalId,
            string outputDirectory)
        {
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
                var editorViewModel = await CreateViewModelAsync(
                        ProductEditMode.Edit,
                        source,
                        workflow)
                    .ConfigureAwait(true);
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
                    CaptureWindow(
                        editor,
                        Path.Combine(
                            outputDirectory,
                            "article-mutation-product-editor-1024x768.png"));
                }
                finally
                {
                    editor.Close();
                }

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
                    CaptureWindow(
                        syncCenter,
                        Path.Combine(
                            outputDirectory,
                            "article-mutation-sync-center-conflict-1024x768.png"));
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

        private static async Task<CleanupManifest> BuildCleanupManifestAsync(
            SqliteConnectionFactory factory,
            string runId,
            IReadOnlyList<long> localProductIds,
            IReadOnlyList<string> directMutationIds)
        {
            var products = new List<CleanupProduct>();
            using (var connection = factory.Open())
            {
                foreach (var localProductId in localProductIds.Distinct())
                {
                    var row = await connection
                        .QueryFirstAsync<CleanupProductRow>(@"
SELECT id AS LocalProductId,
       barcode AS Barcode,
       client_product_id AS ClientProductId,
       remote_product_id AS RemoteProductId
FROM products
WHERE id = @localProductId;",
                            new { localProductId })
                        .ConfigureAwait(false);
                    var mutationIds = (await connection.QueryAsync<string>(@"
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
                        MutationIds = mutationIds,
                        RemoteProductId = row.RemoteProductId
                    });
                }
            }
            return new CleanupManifest
            {
                CreatedAtUtc = UtcNow(),
                DirectMutationIds = directMutationIds.ToArray(),
                Products = products.ToArray(),
                RunId = runId,
                Scope = "synthetic_products_only"
            };
        }

        private static void WriteCleanupPrompt(
            string path,
            CleanupManifest cleanup)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# NEXT CODEX MAC FINAL CLEANUP");
            builder.AppendLine();
            builder.AppendLine("Operate in the Admin repository on Mac.");
            builder.AppendLine("Do not deploy a Worker and do not touch production.");
            builder.AppendLine();
            builder.AppendLine("1. Read the merged Win7POS handoff `docs/HANDOFFS/WIN7POS_POS_ARTICLE_SYNC_FINAL_ACCEPTANCE.md`.");
            builder.AppendLine("2. Validate QA run ID `" + cleanup.RunId + "`.");
            builder.AppendLine("3. Remove only the following synthetic staging products and their mutable QA rows; preserve immutable audit:");
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
            builder.AppendLine("4. Use the mutation IDs in `CLEANUP-MANIFEST.json` only as scope validation evidence.");
            builder.AppendLine("5. Verify zero residual mutable rows for these exact IDs/barcodes and no change to any pre-existing article.");
            builder.AppendLine("6. Mark Admin TASK-144, TASK-145, and the related closeout task DONE with `USER_CONFIRMED_CLOSURE`.");
            builder.AppendLine("7. Record immutable cleanup evidence and final zero-residual counts.");
            builder.AppendLine("8. Perform no additional Worker deployment.");
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
                !value.StartsWith("ASUSART_", StringComparison.Ordinal))
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

        private sealed class VerifiedReferenceRow
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        private sealed class ProductIdentityRow
        {
            public string ClientProductId { get; set; }
            public long LocalProductId { get; set; }
            public long MaximumLocalSequence { get; set; }
            public string RemoteBaseRevision { get; set; }
            public string RemoteProductId { get; set; }
        }

        private sealed class ReplaySourceRow
        {
            public string AckAttemptToken { get; set; }
            public string CanonicalPayloadJson { get; set; }
            public string PayloadHash { get; set; }
        }

        private sealed class CleanupProductRow
        {
            public string Barcode { get; set; }
            public string ClientProductId { get; set; }
            public long LocalProductId { get; set; }
            public string RemoteProductId { get; set; }
        }

        private sealed class FinalCounts
        {
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

            [DataMember(Name = "blockedConflicts")]
            public long BlockedConflicts { get; set; }

            [DataMember(Name = "canonicalPull")]
            public bool CanonicalPull { get; set; }

            [DataMember(Name = "cleanupManifestCreated")]
            public bool CleanupManifestCreated { get; set; }

            [DataMember(Name = "code")]
            public string Code { get; set; }

            [DataMember(Name = "completed")]
            public long Completed { get; set; }

            [DataMember(Name = "completedAtUtc")]
            public string CompletedAtUtc { get; set; }

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

            [DataMember(Name = "exceptionType")]
            public string ExceptionType { get; set; }

            [DataMember(Name = "hardwareActions")]
            public int HardwareActions { get; set; }

            [DataMember(Name = "harnessRestartSurvived")]
            public bool HarnessRestartSurvived { get; set; }

            [DataMember(Name = "inProgress")]
            public long InProgress { get; set; }

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

            [DataMember(Name = "retailPrice")]
            public bool RetailPrice { get; set; }

            [DataMember(Name = "retryWait")]
            public long RetryWait { get; set; }

            [DataMember(Name = "salesRows")]
            public long SalesRows { get; set; }

            [DataMember(Name = "sameMutationReplay")]
            public bool SameMutationReplay { get; set; }

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
            [DataMember(Name = "createdAtUtc")]
            public string CreatedAtUtc { get; set; }

            [DataMember(Name = "directMutationIds")]
            public string[] DirectMutationIds { get; set; }

            [DataMember(Name = "products")]
            public CleanupProduct[] Products { get; set; }

            [DataMember(Name = "runId")]
            public string RunId { get; set; }

            [DataMember(Name = "scope")]
            public string Scope { get; set; }
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
