using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Wpf.Pos.Online;
using Win7POS.Wpf.Products;

namespace Win7POS.Wpf.UiSmokeHarness
{
    /// <summary>
    /// One bounded local acceptance scenario through the real first-login transport,
    /// bootstrap catalog pull, WPF product view models, durable scheduler lane, and
    /// canonical pull apply. The server is loopback-only and keeps synthetic state.
    /// </summary>
    internal static class ArticleMutationLoopbackHarness
    {
        private const string ShopCode = "ARTICLELOOP";
        private const string ShopId =
            "10000000-0000-4000-8000-000000000245";
        private const string StaffId =
            "20000000-0000-4000-8000-000000000245";
        private const string DeviceId =
            "30000000-0000-4000-8000-000000000245";
        private const string SessionId =
            "40000000-0000-4000-8000-000000000245";
        private const string RemoteSourceId =
            "50000000-0000-4000-8000-000000000245";

        internal static async Task<string> RunAsync(string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);
            var report = new LoopbackReport
            {
                StartedAtUtc = UtcNow()
            };

            try
            {
                var options = PosDbOptions.Default();
                DbInitializer.EnsureCreated(options);
                var factory = new SqliteConnectionFactory(options);
                using (var server = new ArticleLoopbackServer(
                    holdFirstCatalogResponse: true))
                using (var host = new PosOnlineSyncSupervisorHost(factory))
                using (var timeout = new CancellationTokenSource(
                    TimeSpan.FromMinutes(2)))
                {
                    PosAdminWebOptions.SaveBaseUrl(new Uri(server.BaseUrl));
                    var store = new PosTrustedDeviceStore();
                    var markerDirectory = Path.Combine(
                        outputDirectory,
                        "run-consumed-callback-regression");
                    Directory.CreateDirectory(markerDirectory);
                    var markerPath = Path.Combine(
                        markerDirectory,
                        "run-consumed-redacted.json");
                    var bootstrapTask = new PosOnlineBootstrapService(
                            factory,
                            store,
                            host)
                        .BootstrapAsync(
                            new PosAdminWebOptions(new Uri(server.BaseUrl)),
                            new PosFirstLoginRequest
                            {
                                Credential = "synthetic-loopback-credential",
                                ShopCode = ShopCode,
                                StaffCode = "ARTICLEPOS",
                                Device = new PosFirstLoginDevice
                                {
                                    AppVersion = "article-loopback",
                                    DeviceIdentifier =
                                        "article-loopback-device",
                                    DisplayName =
                                        "Article mutation loopback"
                                }
                            },
                            "2468",
                            timeout.Token,
                            requestReachedServerObserved: () =>
                                StagingAcceptanceWpfHarness
                                    .WriteRunConsumedMarkerAtomically(
                                        markerDirectory,
                                        "ASUSART_FINAL_CALLBACK_REGRESSION"));
                    var catalogBlocked = await Task.WhenAny(
                            server.FirstCatalogRequestBlocked,
                            Task.Delay(
                                TimeSpan.FromSeconds(10),
                                timeout.Token))
                        .ConfigureAwait(true);
                    Require(
                        ReferenceEquals(
                            catalogBlocked,
                            server.FirstCatalogRequestBlocked) &&
                        File.Exists(markerPath) &&
                        !bootstrapTask.IsCompleted,
                        "run_consumed_marker_not_durable_before_catalog_return");
                    server.ReleaseCatalogResponse();
                    var bootstrap = await bootstrapTask.ConfigureAwait(true);
                    Require(
                        bootstrap.Success &&
                        bootstrap.FirstLoginSucceeded &&
                        bootstrap.TrustedSessionPersisted &&
                        bootstrap.CatalogCompleted &&
                        bootstrap.CatalogSaleSafe &&
                        bootstrap.CanOpenPos,
                        "bootstrap_not_complete_" + SafeCode(bootstrap.Code));
                    Require(
                        store.TryRead(out var trustedSession),
                        "trusted_session_not_readable");
                    var authority = PosOfflineAuthorizationLeasePolicy.Evaluate(
                        trustedSession,
                        DateTimeOffset.UtcNow);
                    Require(
                        trustedSession.OfflineAuthorizationAttested &&
                        authority.Allowed,
                        "offline_authority_not_valid_" +
                        SafeCode(authority.Code));
                    Require(
                        host.CurrentGeneration != null,
                        "sync_generation_not_active");
                    report.FirstLogin = true;
                    report.FullCatalogPull = true;
                    report.OfflineAuthority = true;

                    var workflow = ProductsWorkflowService.CreateDefault();
                    var replayCreate = await CreateViewModelAsync(
                        ProductEditMode.New,
                        null,
                        workflow).ConfigureAwait(true);
                    Populate(
                        replayCreate,
                        "LOOP-CREATE-REPLAY",
                        "Loopback replay create",
                        800,
                        350,
                        4,
                        "LOOP-REPLAY-ITEM");
                    await SubmitAsync(
                        replayCreate,
                        exerciseOffDispatcher: true).ConfigureAwait(true);
                    report.OffDispatcherSubmitMarshaled = true;

                    var replayOutcome = await TriggerArticlesAsync(
                        host,
                        timeout.Token).ConfigureAwait(true);
                    var replayPending = await CountAsync(
                        factory,
                        @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state IN (
  'waiting_dependency',
  'pending',
  'in_progress',
  'retry_wait');").ConfigureAwait(false);
                    var replayBlocked = await CountAsync(
                        factory,
                        @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state = 'failed_blocked';").ConfigureAwait(false);
                    Require(
                        server.LostResponseCount == 1,
                        "lost_response_retry_not_observed_" +
                        SafeCode(replayOutcome.Code) +
                        "_requests_" +
                        server.ArticleRequestCount.ToString(
                            CultureInfo.InvariantCulture) +
                        "_pending_" +
                        replayPending.ToString(
                            CultureInfo.InvariantCulture) +
                        "_blocked_" +
                        replayBlocked.ToString(
                            CultureInfo.InvariantCulture));
                    await MakeRetriesDueAsync(factory).ConfigureAwait(false);
                    await DrainArticlesAsync(
                        factory,
                        host,
                        timeout.Token).ConfigureAwait(true);
                    Require(
                        server.DuplicateReplayCount == 1,
                        "duplicate_replay_not_observed");
                    report.DuplicateReplay = true;

                    var dependentCreate = await CreateViewModelAsync(
                        ProductEditMode.New,
                        null,
                        workflow).ConfigureAwait(true);
                    Populate(
                        dependentCreate,
                        "LOOP-DEPENDENT",
                        "Loopback dependent create",
                        900,
                        400,
                        6,
                        "LOOP-DEPENDENT-ITEM");
                    await SubmitAsync(dependentCreate).ConfigureAwait(true);
                    var dependentRow = await workflow
                        .GetByBarcodeDetailsAsync("LOOP-DEPENDENT")
                        .ConfigureAwait(true);
                    Require(
                        dependentRow != null &&
                        await CountAsync(
                            factory,
                            @"SELECT COUNT(1)
FROM products
WHERE id = @id
  AND remote_product_id IS NULL;",
                            new { id = dependentRow?.Id ?? 0 })
                        .ConfigureAwait(false) == 1,
                        "dependent_create_remote_identity_assigned_too_early");
                    var dependentEdit = await CreateViewModelAsync(
                        ProductEditMode.Edit,
                        dependentRow,
                        workflow).ConfigureAwait(true);
                    Populate(
                        dependentEdit,
                        "LOOP-DEPENDENT-EDIT",
                        "Loopback dependent edited offline",
                        900,
                        400,
                        6,
                        "LOOP-DEPENDENT-ITEM-EDIT");
                    await SubmitAsync(dependentEdit).ConfigureAwait(true);
                    Require(
                        await CountAsync(
                            factory,
                            @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE local_product_id = @id
  AND state = 'waiting_dependency';",
                            new { id = dependentRow.Id })
                        .ConfigureAwait(false) > 0,
                        "dependent_edit_not_waiting");
                    await DrainArticlesAsync(
                        factory,
                        host,
                        timeout.Token).ConfigureAwait(true);
                    report.DependentEdit = true;

                    var remoteSource = await workflow
                        .GetByBarcodeDetailsAsync("LOOP-REMOTE")
                        .ConfigureAwait(true);
                    Require(
                        remoteSource != null &&
                        await CountAsync(
                            factory,
                            @"SELECT COUNT(1)
FROM products
WHERE id = @id
  AND remote_product_id = @remoteProductId;",
                            new
                            {
                                id = remoteSource?.Id ?? 0,
                                remoteProductId = RemoteSourceId
                            })
                        .ConfigureAwait(false) == 1,
                        "bootstrap_source_missing");
                    var fullEdit = await CreateViewModelAsync(
                        ProductEditMode.Edit,
                        remoteSource,
                        workflow).ConfigureAwait(true);
                    Populate(
                        fullEdit,
                        "LOOP-REMOTE-EDIT",
                        "Loopback remote edited",
                        1400,
                        600,
                        15,
                        "LOOP-REMOTE-ITEM-EDIT");
                    fullEdit.Name2 = "Loopback second name";
                    fullEdit.SelectedStockReason = fullEdit.StockReasons.First(
                        item => item.Code == "found");
                    await SubmitAsync(fullEdit).ConfigureAwait(true);
                    await DrainArticlesAsync(
                        factory,
                        host,
                        timeout.Token).ConfigureAwait(true);
                    report.ProductUpdate = true;
                    report.RetailPrice = true;
                    report.PurchasePrice = true;
                    report.StockPlusFive = true;

                    remoteSource = await workflow
                        .GetByBarcodeDetailsAsync("LOOP-REMOTE-EDIT")
                        .ConfigureAwait(true);
                    var stockMinusTwo = await CreateViewModelAsync(
                        ProductEditMode.Edit,
                        remoteSource,
                        workflow).ConfigureAwait(true);
                    Populate(
                        stockMinusTwo,
                        remoteSource.Barcode,
                        remoteSource.Name,
                        checked((int)remoteSource.UnitPrice),
                        remoteSource.PurchasePrice,
                        13,
                        remoteSource.ArticleCode);
                    stockMinusTwo.Name2 = remoteSource.Name2;
                    stockMinusTwo.SelectedStockReason =
                        stockMinusTwo.StockReasons.First(
                            item => item.Code == "count_correction");
                    await SubmitAsync(stockMinusTwo).ConfigureAwait(true);
                    await DrainArticlesAsync(
                        factory,
                        host,
                        timeout.Token).ConfigureAwait(true);
                    report.StockMinusTwo = true;

                    var duplicateSource = await workflow
                        .GetByBarcodeDetailsAsync("LOOP-DEPENDENT-EDIT")
                        .ConfigureAwait(true);
                    var duplicate = await CreateViewModelAsync(
                        ProductEditMode.Duplicate,
                        duplicateSource,
                        workflow).ConfigureAwait(true);
                    Populate(
                        duplicate,
                        "LOOP-DUPLICATE",
                        "Loopback duplicate",
                        950,
                        425,
                        2,
                        "LOOP-DUPLICATE-ITEM");
                    await SubmitAsync(duplicate).ConfigureAwait(true);
                    await DrainArticlesAsync(
                        factory,
                        host,
                        timeout.Token).ConfigureAwait(true);
                    var duplicateRow = await workflow
                        .GetByBarcodeDetailsAsync("LOOP-DUPLICATE")
                        .ConfigureAwait(true);
                    Require(
                        duplicateRow != null &&
                        await CountAsync(
                            factory,
                            @"SELECT COUNT(1)
FROM products
WHERE id = @id
  AND remote_product_id IS NOT NULL;",
                            new { id = duplicateRow?.Id ?? 0 })
                        .ConfigureAwait(false) == 1,
                        "duplicate_remote_identity_missing");
                    report.DuplicateProduct = true;

                    await workflow.SetProductActiveAsync(
                            duplicateRow.Id,
                            false)
                        .ConfigureAwait(true);
                    await DrainArticlesAsync(
                        factory,
                        host,
                        timeout.Token).ConfigureAwait(true);
                    await workflow.SetProductActiveAsync(
                            duplicateRow.Id,
                            true)
                        .ConfigureAwait(true);
                    await DrainArticlesAsync(
                        factory,
                        host,
                        timeout.Token).ConfigureAwait(true);
                    report.DeactivateReactivate = true;

                    remoteSource = await workflow
                        .GetByBarcodeDetailsAsync("LOOP-REMOTE-EDIT")
                        .ConfigureAwait(true);
                    var conflict = await CreateViewModelAsync(
                        ProductEditMode.Edit,
                        remoteSource,
                        workflow).ConfigureAwait(true);
                    Populate(
                        conflict,
                        remoteSource.Barcode,
                        "LOCAL CONFLICT NAME",
                        checked((int)remoteSource.UnitPrice),
                        remoteSource.PurchasePrice,
                        remoteSource.StockQty,
                        remoteSource.ArticleCode);
                    conflict.Name2 = remoteSource.Name2;
                    await SubmitAsync(conflict).ConfigureAwait(true);
                    server.AdvanceRemoteRevision(RemoteSourceId);
                    var conflictOutcome = await TriggerArticlesAsync(
                        host,
                        timeout.Token).ConfigureAwait(true);
                    Require(
                        !conflictOutcome.Success &&
                        await CountAsync(
                            factory,
                            @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state = 'failed_blocked'
  AND last_typed_code = 'failed_conflict';")
                        .ConfigureAwait(false) == 1,
                        "stale_conflict_not_blocked");
                    report.StaleConflict = true;

                    var beforePullOutbox = await CountAsync(
                        factory,
                        "SELECT COUNT(1) FROM article_mutation_outbox;")
                        .ConfigureAwait(false);
                    var pull = await new PosCatalogPullService(factory)
                        .TryPullCatalogForSupervisorAsync(
                            new PosAdminWebOptions(
                                new Uri(server.BaseUrl)),
                            trustedSession,
                            generation: host.CurrentGeneration,
                            executionContext: null,
                            forceFullRepair: true,
                            bootstrapRun: false,
                            cancellationToken: timeout.Token)
                        .ConfigureAwait(false);
                    Require(
                        pull.Completed &&
                        pull.CatalogSaleSafe,
                        "canonical_pull_failed_" +
                        SafeCode(pull.StatusCode));
                    var afterPullOutbox = await CountAsync(
                        factory,
                        "SELECT COUNT(1) FROM article_mutation_outbox;")
                        .ConfigureAwait(false);
                    Require(
                        beforePullOutbox == afterPullOutbox,
                        "canonical_pull_created_outbound_echo");
                    report.CanonicalPull = true;
                    report.ZeroEcho = true;

                    remoteSource = await workflow
                        .GetByBarcodeDetailsAsync("LOOP-REMOTE-EDIT")
                        .ConfigureAwait(true);
                    var correction = await CreateViewModelAsync(
                        ProductEditMode.Edit,
                        remoteSource,
                        workflow).ConfigureAwait(true);
                    Populate(
                        correction,
                        remoteSource.Barcode,
                        "Loopback conflict resolved",
                        checked((int)remoteSource.UnitPrice),
                        remoteSource.PurchasePrice,
                        remoteSource.StockQty,
                        remoteSource.ArticleCode);
                    correction.Name2 = remoteSource.Name2;
                    await SubmitAsync(correction).ConfigureAwait(true);
                    await DrainArticlesAsync(
                        factory,
                        host,
                        timeout.Token).ConfigureAwait(true);
                    Require(
                        await CountAsync(
                            factory,
                            @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state = 'completed'
  AND last_typed_code = 'failed_conflict'
  AND resolution_code = 'superseded_by_correction'
  AND superseded_by_mutation_id IS NOT NULL;")
                        .ConfigureAwait(false) == 1,
                        "conflict_not_superseded_by_correction");
                    report.ConflictResolved = true;

                    var beforeFinalPullOutbox = await CountAsync(
                        factory,
                        "SELECT COUNT(1) FROM article_mutation_outbox;")
                        .ConfigureAwait(false);
                    var finalPull = await new PosCatalogPullService(factory)
                        .TryPullCatalogForSupervisorAsync(
                            new PosAdminWebOptions(
                                new Uri(server.BaseUrl)),
                            trustedSession,
                            generation: host.CurrentGeneration,
                            executionContext: null,
                            forceFullRepair: true,
                            bootstrapRun: false,
                            cancellationToken: timeout.Token)
                        .ConfigureAwait(false);
                    Require(
                        finalPull.Completed &&
                        finalPull.CatalogSaleSafe,
                        "final_canonical_pull_failed_" +
                        SafeCode(finalPull.StatusCode));
                    Require(
                        beforeFinalPullOutbox ==
                        await CountAsync(
                            factory,
                            "SELECT COUNT(1) FROM article_mutation_outbox;")
                            .ConfigureAwait(false),
                        "final_canonical_pull_created_outbound_echo");

                    await VerifyFinalStateAsync(
                        factory,
                        server,
                        replayCreate.Barcode,
                        dependentEdit.Barcode,
                        duplicate.Barcode)
                        .ConfigureAwait(false);
                    report.PendingWork = 0;
                    report.BlockedConflicts = 0;
                    report.PriceHistoryDuplicates = 0;
                    report.StockMovementDuplicates = 0;
                    report.SalesRows = 0;
                    report.ArticleRequests = server.ArticleRequestCount;
                    report.CatalogRequests = server.CatalogRequestCount;
                    report.Passed = true;
                    report.Code = "success";
                    await host.StopAsync().ConfigureAwait(false);
                }

                return "PASS: firstLogin=True; offlineAuthority=True; " +
                    "fullCatalog=True; create=True; duplicateReplay=True; " +
                    "offDispatcherSubmitMarshaled=True; " +
                    "dependentEdit=True; update=True; retail=True; " +
                    "purchase=True; stock=+5,-2; conflict=True; " +
                    "conflictResolved=True; " +
                    "duplicate=True; deactivate=True; reactivate=True; " +
                    "canonicalPull=True; zeroEcho=True; pending=0; " +
                    "blocked=0; duplicateHistory=0; duplicateMovement=0; " +
                    "sales=0";
            }
            catch (Exception ex)
            {
                report.Passed = false;
                report.Code = SafeCode(ex.Message);
                report.ExceptionType = ex.GetType().Name;
                return "FAIL article mutation loopback: " + report.Code;
            }
            finally
            {
                report.CompletedAtUtc = UtcNow();
                WriteJson(
                    Path.Combine(
                        outputDirectory,
                        "article-mutation-loopback-result.json"),
                    report);
            }
        }

        private static async Task<ProductEditViewModel> CreateViewModelAsync(
            ProductEditMode mode,
            ProductDetailsRow source,
            ProductsWorkflowService workflow)
        {
            var viewModel = new ProductEditViewModel(
                mode,
                source,
                workflow);
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
            viewModel.PriceText = retail.ToString(
                CultureInfo.InvariantCulture);
            viewModel.PurchasePriceText = purchase.ToString(
                CultureInfo.InvariantCulture);
            viewModel.StockText = stock.ToString(
                CultureInfo.InvariantCulture);
            viewModel.ArticleCode = itemNumber;
        }

        private static async Task SubmitAsync(
            ProductEditViewModel viewModel,
            bool exerciseOffDispatcher = false)
        {
            var completed = new TaskCompletionSource<bool>();
            var completionOnDispatcher = false;
            Action<bool> handler = null;
            handler = success =>
            {
                completionOnDispatcher =
                    System.Windows.Application.Current?.Dispatcher
                        .CheckAccess() == true;
                viewModel.RequestClose -= handler;
                completed.TrySetResult(success);
            };
            viewModel.RequestClose += handler;
            Action submit = () =>
            {
                Require(
                    viewModel.ConfirmCommand.CanExecute(null),
                    "view_model_save_disabled");
                viewModel.ConfirmCommand.Execute(null);
            };
            if (exerciseOffDispatcher)
                await Task.Run(submit).ConfigureAwait(true);
            else
                submit();
            var finished = await Task.WhenAny(
                completed.Task,
                Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(true);
            if (!ReferenceEquals(finished, completed.Task))
            {
                viewModel.RequestClose -= handler;
                throw new TimeoutException("view_model_save_timeout");
            }
            Require(
                await completed.Task.ConfigureAwait(true),
                "view_model_save_cancelled");
            if (exerciseOffDispatcher)
            {
                Require(
                    completionOnDispatcher,
                    "view_model_save_not_marshaled_to_dispatcher");
            }
        }

        private static async Task<OnlineSyncLaneOutcome>
            TriggerArticlesAsync(
                PosOnlineSyncSupervisorHost host,
                CancellationToken cancellationToken)
        {
            return await host.TriggerAsync(
                    OnlineSyncLane.ArticleMutationOutbox,
                    OnlineSyncLaneTrigger.Manual,
                    cancellationToken)
                .ConfigureAwait(true);
        }

        private static async Task DrainArticlesAsync(
            SqliteConnectionFactory factory,
            PosOnlineSyncSupervisorHost host,
            CancellationToken cancellationToken)
        {
            for (var pass = 0; pass < 40; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = await CountAsync(
                    factory,
                    @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state IN (
  'waiting_dependency',
  'pending',
  'in_progress',
  'retry_wait');")
                    .ConfigureAwait(false);
                if (remaining == 0)
                    return;

                await MakeRetriesDueAsync(factory).ConfigureAwait(false);
                var outcome = await TriggerArticlesAsync(
                    host,
                    cancellationToken).ConfigureAwait(true);
                Require(
                    !outcome.AuthenticationDenied,
                    "loopback_authentication_denied");
                if (!outcome.Success &&
                    !string.Equals(
                        outcome.Code,
                        "article_mutation_blocked",
                        StringComparison.Ordinal))
                {
                    var retries = await CountAsync(
                        factory,
                        @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state = 'retry_wait';").ConfigureAwait(false);
                    Require(
                        retries > 0,
                        "article_drain_failed_" +
                        SafeCode(outcome.Code));
                }
            }
            throw new InvalidOperationException(
                "article_drain_budget_exhausted");
        }

        private static async Task MakeRetriesDueAsync(
            SqliteConnectionFactory factory)
        {
            using (var connection = factory.Open())
            {
                await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET next_attempt_at = 0
WHERE state = 'retry_wait';").ConfigureAwait(false);
            }
        }

        private static async Task<long> CountAsync(
            SqliteConnectionFactory factory,
            string sql,
            object arguments = null)
        {
            using (var connection = factory.Open())
            {
                return await connection.ExecuteScalarAsync<long>(
                        sql,
                        arguments)
                    .ConfigureAwait(false);
            }
        }

        private static async Task VerifyFinalStateAsync(
            SqliteConnectionFactory factory,
            ArticleLoopbackServer server,
            params string[] createdBarcodes)
        {
            using (var connection = factory.Open())
            {
                Require(
                    await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state IN (
  'waiting_dependency',
  'pending',
  'in_progress',
  'retry_wait');") == 0,
                    "pending_article_work_remains");
                Require(
                    await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state IN (
  'waiting_dependency',
  'pending',
  'in_progress',
  'retry_wait',
  'failed_blocked');") == 0,
                    "unresolved_article_work_remains");
                Require(
                    await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state = 'completed'
  AND last_typed_code = 'failed_conflict'
  AND resolution_code = 'superseded_by_correction'
  AND superseded_by_mutation_id IS NOT NULL;") == 1,
                    "resolved_conflict_evidence_mismatch");
                Require(
                    await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM (
  SELECT remote_price_id
  FROM product_price_history
  WHERE remote_price_id IS NOT NULL
  GROUP BY remote_price_id
  HAVING COUNT(1) > 1
);") == 0,
                    "duplicate_remote_price_history");
                Require(
                    await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM (
  SELECT article_mutation_id
  FROM product_price_history
  WHERE article_mutation_id IS NOT NULL
  GROUP BY article_mutation_id
  HAVING COUNT(1) > 1
);") == 0,
                    "duplicate_local_price_history");
                Require(
                    await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM (
  SELECT mutation_id
  FROM article_manual_stock_adjustments
  GROUP BY mutation_id
  HAVING COUNT(1) > 1
);") == 0,
                    "duplicate_manual_stock_adjustment");
                Require(
                    await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_manual_stock_adjustments adjustment
JOIN products product
  ON product.id = adjustment.local_product_id
WHERE product.remote_product_id = @remoteProductId;",
                        new { remoteProductId = RemoteSourceId }) == 2,
                    "manual_stock_adjustment_count_mismatch");
                Require(
                    await connection.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM sales;") == 0 &&
                    await connection.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM sale_lines;") == 0 &&
                    await connection.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM sales_sync_outbox;") == 0 &&
                    await connection.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM local_stock_movements;") == 0,
                    "article_mutation_created_sale_or_revenue_rows");
                Require(
                    await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_attempts
WHERE completed_at IS NULL;") == 0,
                    "article_attempt_ledger_incomplete");
                foreach (var barcode in createdBarcodes)
                {
                    Require(
                        await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM products
WHERE barcode = @barcode
  AND remote_product_id IS NOT NULL;",
                            new { barcode }) == 1,
                        "remote_identity_missing_" +
                        SafeCode(barcode));
                }

                var kindCounts = (await connection.QueryAsync<KindCount>(@"
SELECT mutation_kind AS Kind,
       COUNT(1) AS Count
FROM article_mutation_outbox
WHERE state = 'completed'
GROUP BY mutation_kind;")).ToDictionary(
                    row => row.Kind,
                    row => row.Count,
                    StringComparer.Ordinal);
                RequireKind(
                    kindCounts,
                    PosArticleMutationKinds.ProductCreate,
                    2);
                RequireKind(
                    kindCounts,
                    PosArticleMutationKinds.ProductDuplicate,
                    1);
                RequireKind(
                    kindCounts,
                    PosArticleMutationKinds.ProductUpdate,
                    2);
                RequireKind(
                    kindCounts,
                    PosArticleMutationKinds.ProductRetailPriceChange,
                    1);
                RequireKind(
                    kindCounts,
                    PosArticleMutationKinds.ProductPurchasePriceChange,
                    1);
                RequireKind(
                    kindCounts,
                    PosArticleMutationKinds.ProductManualStockAdjustment,
                    2);
                RequireKind(
                    kindCounts,
                    PosArticleMutationKinds.ProductDeactivate,
                    1);
                RequireKind(
                    kindCounts,
                    PosArticleMutationKinds.ProductActivate,
                    1);
            }

            Require(
                server.AppliedKinds.Contains(
                    PosArticleMutationKinds.ProductCreate) &&
                server.AppliedKinds.Contains(
                    PosArticleMutationKinds.ProductDuplicate) &&
                server.AppliedKinds.Contains(
                    PosArticleMutationKinds.ProductUpdate) &&
                server.AppliedKinds.Contains(
                    PosArticleMutationKinds.ProductRetailPriceChange) &&
                server.AppliedKinds.Contains(
                    PosArticleMutationKinds.ProductPurchasePriceChange) &&
                server.AppliedKinds.Contains(
                    PosArticleMutationKinds.ProductManualStockAdjustment) &&
                server.AppliedKinds.Contains(
                    PosArticleMutationKinds.ProductDeactivate) &&
                server.AppliedKinds.Contains(
                    PosArticleMutationKinds.ProductActivate),
                "loopback_server_did_not_apply_full_matrix");
        }

        private static void RequireKind(
            IDictionary<string, long> counts,
            string kind,
            long minimum)
        {
            Require(
                counts.TryGetValue(kind, out var count) &&
                count >= minimum,
                "completed_kind_missing_" + SafeCode(kind));
        }

        private static void Require(bool condition, string code)
        {
            if (!condition)
                throw new InvalidOperationException(code);
        }

        private static string UtcNow()
        {
            return DateTimeOffset.UtcNow.ToString(
                "O",
                CultureInfo.InvariantCulture);
        }

        private static string SafeCode(string value)
        {
            var source = (value ?? string.Empty).Trim().ToLowerInvariant();
            var builder = new StringBuilder();
            foreach (var character in source)
            {
                if (builder.Length >= 96)
                    break;
                if ((character >= 'a' && character <= 'z') ||
                    (character >= '0' && character <= '9') ||
                    character == '_' ||
                    character == '-')
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
                ? "loopback_failure"
                : builder.ToString().TrimEnd('_');
        }

        private static void WriteJson(string path, object value)
        {
            var serializer = new DataContractJsonSerializer(
                value.GetType());
            using (var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                serializer.WriteObject(stream, value);
            }
        }

        private sealed class KindCount
        {
            public string Kind { get; set; }
            public long Count { get; set; }
        }

        [DataContract]
        private sealed class LoopbackReport
        {
            [DataMember(Order = 1)]
            public bool Passed { get; set; }
            [DataMember(Order = 2)]
            public string Code { get; set; }
            [DataMember(Order = 3)]
            public string ExceptionType { get; set; }
            [DataMember(Order = 4)]
            public string StartedAtUtc { get; set; }
            [DataMember(Order = 5)]
            public string CompletedAtUtc { get; set; }
            [DataMember(Order = 6)]
            public bool FirstLogin { get; set; }
            [DataMember(Order = 7)]
            public bool OfflineAuthority { get; set; }
            [DataMember(Order = 8)]
            public bool FullCatalogPull { get; set; }
            [DataMember(Order = 9)]
            public bool DuplicateReplay { get; set; }
            [DataMember(Order = 10)]
            public bool DependentEdit { get; set; }
            [DataMember(Order = 11)]
            public bool ProductUpdate { get; set; }
            [DataMember(Order = 12)]
            public bool RetailPrice { get; set; }
            [DataMember(Order = 13)]
            public bool PurchasePrice { get; set; }
            [DataMember(Order = 14)]
            public bool StockPlusFive { get; set; }
            [DataMember(Order = 15)]
            public bool StockMinusTwo { get; set; }
            [DataMember(Order = 16)]
            public bool StaleConflict { get; set; }
            [DataMember(Order = 17)]
            public bool DuplicateProduct { get; set; }
            [DataMember(Order = 18)]
            public bool DeactivateReactivate { get; set; }
            [DataMember(Order = 19)]
            public bool CanonicalPull { get; set; }
            [DataMember(Order = 20)]
            public bool ZeroEcho { get; set; }
            [DataMember(Order = 21)]
            public long PendingWork { get; set; }
            [DataMember(Order = 22)]
            public long BlockedConflicts { get; set; }
            [DataMember(Order = 23)]
            public long PriceHistoryDuplicates { get; set; }
            [DataMember(Order = 24)]
            public long StockMovementDuplicates { get; set; }
            [DataMember(Order = 25)]
            public long SalesRows { get; set; }
            [DataMember(Order = 26)]
            public int ArticleRequests { get; set; }
            [DataMember(Order = 27)]
            public int CatalogRequests { get; set; }
            [DataMember(Order = 28)]
            public bool OffDispatcherSubmitMarshaled { get; set; }
            [DataMember(Order = 29)]
            public bool ConflictResolved { get; set; }
        }

        private sealed class ArticleLoopbackServer : IDisposable
        {
            private const string CategoryId =
                "60000000-0000-4000-8000-000000000245";
            private const string SupplierId =
                "70000000-0000-4000-8000-000000000245";
            private readonly object _gate = new object();
            private readonly CancellationTokenSource _cancellation =
                new CancellationTokenSource();
            private readonly TaskCompletionSource<bool>
                _firstCatalogRequestBlocked =
                    new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool>
                _releaseCatalogResponse =
                    new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TcpListener _listener;
            private readonly Task _serverTask;
            private readonly Dictionary<string, RemoteProduct> _products =
                new Dictionary<string, RemoteProduct>(
                    StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, ReplayRecord> _replays =
                new Dictionary<string, ReplayRecord>(
                    StringComparer.Ordinal);
            private readonly List<RemotePrice> _prices =
                new List<RemotePrice>();
            private readonly HashSet<string> _appliedKinds =
                new HashSet<string>(StringComparer.Ordinal);
            private long _catalogRevision = 40;
            private long _revisionCounter;
            private int _articleRequestCount;
            private int _catalogRequestCount;
            private int _duplicateReplayCount;
            private int _lostResponseCount;
            private bool _lostCreateResponse;
            private readonly bool _holdFirstCatalogResponse;
            private int _catalogResponseHeld;

            internal ArticleLoopbackServer(
                bool holdFirstCatalogResponse = false)
            {
                _holdFirstCatalogResponse =
                    holdFirstCatalogResponse;
                var revision =
                    "2026-07-28T12:00:00.123456Z";
                _products.Add(
                    RemoteSourceId,
                    new RemoteProduct
                    {
                        RemoteProductId = RemoteSourceId,
                        Barcode = "LOOP-REMOTE",
                        ItemNumber = "LOOP-REMOTE-ITEM",
                        PrimaryName = "Loopback remote source",
                        SecondaryName = string.Empty,
                        CategoryId = CategoryId,
                        SupplierId = SupplierId,
                        RetailPrice = 1000,
                        PurchasePrice = 500,
                        StockQuantity = 10,
                        Active = true,
                        Revision = revision
                    });
                _prices.Add(
                    new RemotePrice
                    {
                        EffectiveAt = revision,
                        Price = 1000,
                        PriceId =
                            "80000000-0000-4000-8000-000000000245",
                        ProductId = RemoteSourceId,
                        Source = "catalog_pull",
                        Type = "retail"
                    });
                _prices.Add(
                    new RemotePrice
                    {
                        EffectiveAt = revision,
                        Price = 500,
                        PriceId =
                            "90000000-0000-4000-8000-000000000245",
                        ProductId = RemoteSourceId,
                        Source = "catalog_pull",
                        Type = "purchase"
                    });

                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                var port =
                    ((IPEndPoint)_listener.LocalEndpoint).Port;
                BaseUrl = "http://127.0.0.1:" +
                    port.ToString(CultureInfo.InvariantCulture) + "/";
                _serverTask = Task.Run(
                    () => RunAsync(_cancellation.Token));
            }

            internal string BaseUrl { get; }
            internal Task FirstCatalogRequestBlocked =>
                _firstCatalogRequestBlocked.Task;
            internal int ArticleRequestCount =>
                Volatile.Read(ref _articleRequestCount);
            internal int CatalogRequestCount =>
                Volatile.Read(ref _catalogRequestCount);
            internal int DuplicateReplayCount =>
                Volatile.Read(ref _duplicateReplayCount);
            internal int LostResponseCount =>
                Volatile.Read(ref _lostResponseCount);
            internal ISet<string> AppliedKinds
            {
                get
                {
                    lock (_gate)
                    {
                        return new HashSet<string>(
                            _appliedKinds,
                            StringComparer.Ordinal);
                    }
                }
            }

            internal void AdvanceRemoteRevision(string remoteProductId)
            {
                lock (_gate)
                {
                    Require(
                        _products.TryGetValue(
                            remoteProductId,
                            out var product),
                        "server_remote_product_missing");
                    product.PrimaryName =
                        "SERVER CONCURRENT NAME";
                    product.Revision = NextRevision();
                    _catalogRevision++;
                }
            }

            internal void ReleaseCatalogResponse()
            {
                _releaseCatalogResponse.TrySetResult(true);
            }

            public void Dispose()
            {
                ReleaseCatalogResponse();
                _cancellation.Cancel();
                try
                {
                    _listener.Stop();
                }
                catch
                {
                }
                try
                {
                    _serverTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
                _cancellation.Dispose();
            }

            private async Task RunAsync(
                CancellationToken cancellationToken)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClient client = null;
                    try
                    {
                        client = await _listener
                            .AcceptTcpClientAsync()
                            .ConfigureAwait(false);
                        await HandleClientAsync(
                                client,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        client?.Dispose();
                    }
                    catch (SocketException)
                    {
                        client?.Dispose();
                        if (!cancellationToken.IsCancellationRequested)
                            throw;
                    }
                }
            }

            private async Task HandleClientAsync(
                TcpClient client,
                CancellationToken cancellationToken)
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var request = await ReadRequestAsync(
                            stream,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (request.RequestLine.StartsWith(
                            "POST /api/pos/auth/first-login ",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteResponseAsync(
                            stream,
                            200,
                            Serialize(BuildFirstLoginResponse()),
                            cancellationToken)
                        .ConfigureAwait(false);
                        return;
                    }
                    if (request.RequestLine.StartsWith(
                            "POST /api/pos/catalog/pull ",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(
                            ref _catalogRequestCount);
                        if (_holdFirstCatalogResponse &&
                            Interlocked.CompareExchange(
                                ref _catalogResponseHeld,
                                1,
                                0) == 0)
                        {
                            _firstCatalogRequestBlocked.TrySetResult(true);
                            await _releaseCatalogResponse.Task
                                .ConfigureAwait(false);
                        }
                        await WriteResponseAsync(
                            stream,
                            200,
                            Serialize(BuildCatalogResponse()),
                            cancellationToken)
                        .ConfigureAwait(false);
                        return;
                    }
                    if (request.RequestLine.StartsWith(
                            "POST " +
                            PosArticleMutationContract.EndpointPath +
                            " ",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(
                            ref _articleRequestCount);
                        var envelope =
                            Deserialize<MutationEnvelopeWire>(
                                request.Body);
                        var response =
                            ApplyArticleMutations(
                                envelope,
                                out var loseResponse);
                        if (loseResponse)
                        {
                            Interlocked.Increment(
                                ref _lostResponseCount);
                            await WriteResponseAsync(
                                    stream,
                                    500,
                                    Encoding.UTF8.GetBytes(
                                        "{\"ok\":false,\"code\":\"synthetic_lost_response\"}"),
                                    cancellationToken)
                                .ConfigureAwait(false);
                            return;
                        }
                        await WriteResponseAsync(
                                stream,
                                200,
                                Serialize(response),
                                cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    await WriteResponseAsync(
                            stream,
                            404,
                            Encoding.UTF8.GetBytes(
                                "{\"ok\":false,\"code\":\"not_found\"}"),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            private PosArticleMutationResponse ApplyArticleMutations(
                MutationEnvelopeWire envelope,
                out bool loseResponse)
            {
                loseResponse = false;
                Require(
                    envelope != null &&
                    string.Equals(
                        envelope.SchemaVersion,
                        PosArticleMutationContract.SchemaVersion,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        envelope.ShopId,
                        ShopId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        envelope.ShopDeviceId,
                        DeviceId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        envelope.StaffId,
                        StaffId,
                        StringComparison.Ordinal) &&
                    envelope.StaffCredentialVersion == 7 &&
                    string.Equals(
                        envelope.PosSessionId,
                        SessionId,
                        StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(
                        envelope.DeviceToken) &&
                    !string.IsNullOrWhiteSpace(
                        envelope.SessionToken) &&
                    envelope.Mutations != null &&
                    envelope.Mutations.Length > 0 &&
                    envelope.Mutations.Length <=
                        PosArticleMutationContract.MaximumBatchCount,
                    "server_invalid_article_envelope");

                var results =
                    new List<PosArticleMutationResult>();
                lock (_gate)
                {
                    foreach (var mutation in envelope.Mutations)
                    {
                        results.Add(
                            ApplyOne(
                                mutation,
                                ref loseResponse));
                    }
                }
                var allCompleted = results.All(
                    item =>
                        string.Equals(
                            item.DeliveryStatus,
                            PosArticleMutationStatusPolicy.Applied,
                            StringComparison.Ordinal) ||
                        string.Equals(
                            item.DeliveryStatus,
                            PosArticleMutationStatusPolicy.DuplicateReplay,
                            StringComparison.Ordinal));
                return new PosArticleMutationResponse
                {
                    Ok = allCompleted,
                    Code = "success",
                    SchemaVersion =
                        PosArticleMutationContract.SchemaVersion,
                    ServerTime = UtcNow(),
                    Results = results.ToArray()
                };
            }

            private PosArticleMutationResult ApplyOne(
                MutationWire mutation,
                ref bool loseResponse)
            {
                Require(
                    mutation != null &&
                    PosArticleMutationIntentPolicy.IsSafeId(
                        mutation.MutationId) &&
                    PosArticleMutationIntentPolicy.IsSafeId(
                        mutation.IdempotencyKey) &&
                    PosArticleMutationIntentPolicy.IsSafeId(
                        mutation.AttemptToken) &&
                    !string.IsNullOrWhiteSpace(
                        mutation.PayloadHash),
                    "server_invalid_mutation");

                if (_replays.TryGetValue(
                        mutation.MutationId,
                        out var replay))
                {
                    Require(
                        string.Equals(
                            replay.PayloadHash,
                            mutation.PayloadHash,
                            StringComparison.Ordinal),
                        "server_replay_hash_mismatch");
                    Interlocked.Increment(
                        ref _duplicateReplayCount);
                    return new PosArticleMutationResult
                    {
                        DeliveryStatus =
                            PosArticleMutationStatusPolicy
                                .DuplicateReplay,
                        Ack = CloneAck(replay.Ack)
                    };
                }

                RemoteProduct product;
                if (string.Equals(
                        mutation.MutationKind,
                        PosArticleMutationKinds.ProductCreate,
                        StringComparison.Ordinal))
                {
                    product = new RemoteProduct
                    {
                        RemoteProductId =
                            Guid.NewGuid().ToString("D"),
                        Active = true
                    };
                    ApplyProductFields(product, mutation.Changes);
                    product.Revision = NextRevision();
                    _products.Add(
                        product.RemoteProductId,
                        product);
                }
                else if (string.Equals(
                    mutation.MutationKind,
                    PosArticleMutationKinds.ProductDuplicate,
                    StringComparison.Ordinal))
                {
                    Require(
                        _products.TryGetValue(
                            mutation.RemoteProductId ??
                            string.Empty,
                            out var source),
                        "server_duplicate_source_missing");
                    Require(
                        string.Equals(
                            source.Revision,
                            mutation.BaseRevision,
                            StringComparison.Ordinal),
                        "server_duplicate_base_mismatch");
                    product = source.Clone();
                    product.RemoteProductId =
                        Guid.NewGuid().ToString("D");
                    product.Active = true;
                    ApplyProductFields(product, mutation.Changes);
                    product.Revision = NextRevision();
                    _products.Add(
                        product.RemoteProductId,
                        product);
                }
                else
                {
                    if (!_products.TryGetValue(
                            mutation.RemoteProductId ??
                            string.Empty,
                            out product))
                    {
                        return Failure(
                            mutation,
                            PosArticleMutationStatusPolicy.FailedConflict);
                    }
                    if (!string.Equals(
                        product.Revision,
                        mutation.BaseRevision,
                        StringComparison.Ordinal))
                    {
                        return Failure(
                            mutation,
                            PosArticleMutationStatusPolicy.FailedConflict);
                    }

                    switch (mutation.MutationKind)
                    {
                        case PosArticleMutationKinds.ProductUpdate:
                            ApplyProductFields(
                                product,
                                mutation.Changes);
                            break;
                        case PosArticleMutationKinds
                            .ProductRetailPriceChange:
                            product.RetailPrice = ReadInt(
                                mutation.Changes,
                                PosArticleMutationFields.Price);
                            break;
                        case PosArticleMutationKinds
                            .ProductPurchasePriceChange:
                            product.PurchasePrice = ReadInt(
                                mutation.Changes,
                                PosArticleMutationFields.Price);
                            break;
                        case PosArticleMutationKinds
                            .ProductManualStockAdjustment:
                            product.StockQuantity = checked(
                                product.StockQuantity +
                                ReadInt(
                                    mutation.Changes,
                                    PosArticleMutationFields
                                        .QuantityDelta));
                            break;
                        case PosArticleMutationKinds.ProductDeactivate:
                            product.Active = false;
                            break;
                        case PosArticleMutationKinds.ProductActivate:
                            product.Active = true;
                            break;
                        default:
                            throw new InvalidOperationException(
                                "server_unknown_mutation_kind");
                    }
                    product.Revision = NextRevision();
                }

                _catalogRevision++;
                _appliedKinds.Add(mutation.MutationKind);
                string priceHistoryId = null;
                string stockMovementId = null;
                if (string.Equals(
                        mutation.MutationKind,
                        PosArticleMutationKinds
                            .ProductRetailPriceChange,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        mutation.MutationKind,
                        PosArticleMutationKinds
                            .ProductPurchasePriceChange,
                        StringComparison.Ordinal))
                {
                    priceHistoryId =
                        Guid.NewGuid().ToString("D");
                    _prices.Add(
                        new RemotePrice
                        {
                            EffectiveAt =
                                mutation.OccurredAt,
                            Price = ReadInt(
                                mutation.Changes,
                                PosArticleMutationFields.Price),
                            PriceId = priceHistoryId,
                            ProductId =
                                product.RemoteProductId,
                            Source = "article_mutation",
                            Type = string.Equals(
                                mutation.MutationKind,
                                PosArticleMutationKinds
                                    .ProductRetailPriceChange,
                                StringComparison.Ordinal)
                                ? "retail"
                                : "purchase"
                        });
                }
                if (string.Equals(
                    mutation.MutationKind,
                    PosArticleMutationKinds
                        .ProductManualStockAdjustment,
                    StringComparison.Ordinal))
                {
                    stockMovementId =
                        Guid.NewGuid().ToString("D");
                }

                var ack = new PosArticleMutationAck
                {
                    AttemptToken = mutation.AttemptToken,
                    AuthoritativeRevision = product.Revision,
                    CatalogRevision = _catalogRevision.ToString(
                        CultureInfo.InvariantCulture),
                    Code = PosArticleMutationStatusPolicy.Applied,
                    IdempotencyKey = mutation.IdempotencyKey,
                    MutationId = mutation.MutationId,
                    PayloadHash = mutation.PayloadHash,
                    PriceHistoryId = priceHistoryId,
                    RemoteProductId = product.RemoteProductId,
                    Retryable = false,
                    SchemaVersion =
                        PosArticleMutationContract.SchemaVersion,
                    ServerTimestamp = product.Revision,
                    Status = PosArticleMutationStatusPolicy.Applied,
                    StockMovementId = stockMovementId,
                    Terminal = true
                };
                _replays.Add(
                    mutation.MutationId,
                    new ReplayRecord
                    {
                        Ack = CloneAck(ack),
                        PayloadHash = mutation.PayloadHash
                    });

                if (!_lostCreateResponse &&
                    string.Equals(
                        mutation.MutationKind,
                        PosArticleMutationKinds.ProductCreate,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        product.Barcode,
                        "LOOP-CREATE-REPLAY",
                        StringComparison.Ordinal))
                {
                    _lostCreateResponse = true;
                    loseResponse = true;
                }

                return new PosArticleMutationResult
                {
                    DeliveryStatus =
                        PosArticleMutationStatusPolicy.Applied,
                    Ack = ack
                };
            }

            private PosArticleMutationResult Failure(
                MutationWire mutation,
                string code)
            {
                return new PosArticleMutationResult
                {
                    DeliveryStatus = code,
                    Ack = new PosArticleMutationAck
                    {
                        AttemptToken = mutation.AttemptToken,
                        AuthoritativeRevision = null,
                        CatalogRevision =
                            _catalogRevision.ToString(
                                CultureInfo.InvariantCulture),
                        Code = code,
                        IdempotencyKey = mutation.IdempotencyKey,
                        MutationId = mutation.MutationId,
                        PayloadHash = mutation.PayloadHash,
                        PriceHistoryId = null,
                        RemoteProductId =
                            mutation.RemoteProductId,
                        Retryable = false,
                        SchemaVersion =
                            PosArticleMutationContract.SchemaVersion,
                        ServerTimestamp = NextRevision(),
                        Status = code,
                        StockMovementId = null,
                        Terminal = true
                    }
                };
            }

            private PosFirstLoginResponse BuildFirstLoginResponse()
            {
                var now = DateTimeOffset.UtcNow;
                return new PosFirstLoginResponse
                {
                    Device = new PosTrustedDeviceResponse
                    {
                        ShopDeviceId = DeviceId,
                        Status = "active",
                        Trusted = true
                    },
                    EffectiveOfflineAuthorizationExpiresAt =
                        now.AddHours(4).ToString(
                            "O",
                            CultureInfo.InvariantCulture),
                    Ok = true,
                    Policy = ValidPolicy(),
                    ServerTime = now.ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                    Session = new PosSessionResponse
                    {
                        ExpiresAt = now.AddHours(4).ToString(
                            "O",
                            CultureInfo.InvariantCulture),
                        HeartbeatAfterSeconds = 300,
                        PosSessionId = SessionId,
                        SessionToken =
                            "synthetic-loopback-session-token"
                    },
                    Shop = Shop(),
                    Staff = new PosStaffResponse
                    {
                        CredentialVersion = 7,
                        DisplayName =
                            "Synthetic article operator",
                        RoleKey = "pos_admin",
                        StaffCode = "ARTICLEPOS",
                        StaffId = StaffId
                    },
                    TrustedDeviceToken =
                        "synthetic-loopback-device-token"
                };
            }

            private PosCatalogPullResponse BuildCatalogResponse()
            {
                lock (_gate)
                {
                    var now = UtcNow();
                    var active = _products.Values
                        .Where(item => item.Active)
                        .OrderBy(
                            item => item.RemoteProductId,
                            StringComparer.Ordinal)
                        .ToArray();
                    var inactive = _products.Values
                        .Where(item => !item.Active)
                        .OrderBy(
                            item => item.RemoteProductId,
                            StringComparer.Ordinal)
                        .ToArray();
                    return new PosCatalogPullResponse
                    {
                        Catalog = new PosCatalogPayload
                        {
                            Categories = new[]
                            {
                                new PosCatalogCategoryResponse
                                {
                                    CategoryId = CategoryId,
                                    Name =
                                        "Loopback category",
                                    UpdatedAt = now
                                }
                            },
                            Suppliers = new[]
                            {
                                new PosCatalogSupplierResponse
                                {
                                    Name =
                                        "Loopback supplier",
                                    SupplierId = SupplierId,
                                    UpdatedAt = now
                                }
                            },
                            Products = active
                                .Select(ToCatalogProduct)
                                .ToArray(),
                            Prices = _prices
                                .OrderBy(
                                    item => item.PriceId,
                                    StringComparer.Ordinal)
                                .Select(ToCatalogPrice)
                                .ToArray(),
                            Tombstones =
                                new PosCatalogTombstonesResponse
                                {
                                    Categories =
                                        Array.Empty<
                                            PosCatalogCategoryTombstoneResponse>(),
                                    Suppliers =
                                        Array.Empty<
                                            PosCatalogSupplierTombstoneResponse>(),
                                    Products = inactive
                                        .Select(
                                            item =>
                                                new PosCatalogProductTombstoneResponse
                                                {
                                                    DeletedAt =
                                                        item.Revision,
                                                    ProductId =
                                                        item.RemoteProductId,
                                                    UpdatedAt =
                                                        item.Revision
                                                })
                                        .ToArray()
                                }
                        },
                        CatalogSummary =
                            new PosCatalogSummaryResponse
                            {
                                ActiveProducts =
                                    active.LongLength,
                                Categories = 1,
                                Prices = _prices.Count,
                                Products = _products.Count,
                                Suppliers = 1
                            },
                        CatalogVersion =
                            "article-loopback-v" +
                            _catalogRevision.ToString(
                                CultureInfo.InvariantCulture),
                        Code = "success",
                        GeneratedAt = now,
                        HasMore = false,
                        Ok = true,
                        Policy = ValidPolicy(),
                        SchemaVersion =
                            PosOnlineContract.CatalogPullSchemaVersion,
                        ServerTime = now,
                        Shop = Shop(),
                        SyncCursor =
                            "article-loopback-cursor-" +
                            _catalogRevision.ToString(
                                CultureInfo.InvariantCulture) +
                            "-" +
                            _catalogRequestCount.ToString(
                                CultureInfo.InvariantCulture),
                        SyncMode = "full_refresh"
                    };
                }
            }

            private string NextRevision()
            {
                var ticks = Interlocked.Increment(
                    ref _revisionCounter);
                return DateTimeOffset.UtcNow
                    .AddTicks(ticks)
                    .ToUniversalTime()
                    .ToString(
                        "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
                        CultureInfo.InvariantCulture);
            }

            private static PosShopResponse Shop()
            {
                return new PosShopResponse
                {
                    ShopCode = ShopCode,
                    ShopId = ShopId,
                    ShopName =
                        "Article mutation loopback shop",
                    ShopStatus = "active",
                    Source = "qa_loopback",
                    UpdatedAt = UtcNow()
                };
            }

            private static PosPolicyResponse ValidPolicy()
            {
                return new PosPolicyResponse
                {
                    Capabilities =
                        new PosPolicyCapabilitiesResponse
                        {
                            CatalogPull =
                                PosOnlineContract
                                    .CatalogCapabilityVersion,
                            FiscalDocumentMode =
                                "local_receipt_redacted",
                            LocalReceiptPrinting = true,
                            LocalStaffMirror =
                                "current_staff_only",
                            OfflineSales = true,
                            PaymentMethods = new[]
                            {
                                PosOnlineContract.PaymentCash,
                                PosOnlineContract.PaymentCard,
                                PosOnlineContract.PaymentOther
                            },
                            SalesSync =
                                PosOnlineContract
                                    .SalesSchemaVersion
                        },
                    ContractVersion =
                        PosOnlineContract.PolicyContractVersion,
                    Limitations = new[]
                    {
                        "first_activation_requires_online"
                    },
                    OfflinePolicy =
                        new PosOfflinePolicyResponse
                        {
                            FirstActivationRequiresOnline =
                                true,
                            Mode =
                                "offline_first_after_activation",
                            PendingSalesRetention =
                                "local_outbox_until_server_ack",
                            RevocationEnforcement =
                                "next_online_check"
                        },
                    PaymentPolicy =
                        new PosPaymentPolicyResponse
                        {
                            Currency = "CLP",
                            FallbackMethod =
                                PosOnlineContract.PaymentOther,
                            SupportedMethods = new[]
                            {
                                PosOnlineContract.PaymentCash,
                                PosOnlineContract.PaymentCard,
                                PosOnlineContract.PaymentOther
                            },
                            UnsupportedMethods =
                                new[] { "transfer" }
                        },
                    StaffPolicy =
                        new PosStaffPolicyResponse
                        {
                            CredentialMaterial =
                                "not_synced",
                            MustChangeCredential =
                                "online_required",
                            OfflineMirror =
                                "current_staff_only"
                        },
                    TaxPolicy = new PosTaxPolicyResponse
                    {
                        DefaultTaxClp = 0,
                        FiscalAuthorityIntegration =
                            "not_configured",
                        Status = "not_configured"
                    }
                };
            }

            private static PosCatalogProductResponse ToCatalogProduct(
                RemoteProduct product)
            {
                return new PosCatalogProductResponse
                {
                    Barcode = product.Barcode,
                    CategoryId = product.CategoryId,
                    ItemNumber = product.ItemNumber,
                    ProductId = product.RemoteProductId,
                    ProductName = product.PrimaryName,
                    PurchasePrice = product.PurchasePrice,
                    RetailPrice = product.RetailPrice,
                    SecondProductName =
                        product.SecondaryName,
                    StockQuantity = product.StockQuantity,
                    SupplierId = product.SupplierId,
                    UpdatedAt = product.Revision
                };
            }

            private static PosCatalogPriceResponse ToCatalogPrice(
                RemotePrice price)
            {
                return new PosCatalogPriceResponse
                {
                    EffectiveAt = price.EffectiveAt,
                    Price = price.Price,
                    PriceId = price.PriceId,
                    ProductId = price.ProductId,
                    Source = price.Source,
                    Type = price.Type
                };
            }

            private static void ApplyProductFields(
                RemoteProduct product,
                IDictionary<string, object> changes)
            {
                if (Has(changes, PosArticleMutationFields.Barcode))
                {
                    product.Barcode = ReadString(
                        changes,
                        PosArticleMutationFields.Barcode);
                }
                if (Has(changes, PosArticleMutationFields.ItemNumber))
                {
                    product.ItemNumber = ReadNullableString(
                        changes,
                        PosArticleMutationFields.ItemNumber);
                }
                if (Has(changes, PosArticleMutationFields.PrimaryName))
                {
                    product.PrimaryName = ReadString(
                        changes,
                        PosArticleMutationFields.PrimaryName);
                }
                if (Has(changes, PosArticleMutationFields.SecondaryName))
                {
                    product.SecondaryName = ReadNullableString(
                        changes,
                        PosArticleMutationFields.SecondaryName);
                }
                if (Has(changes, PosArticleMutationFields.CategoryId))
                {
                    product.CategoryId = ReadNullableString(
                        changes,
                        PosArticleMutationFields.CategoryId);
                }
                if (Has(changes, PosArticleMutationFields.SupplierId))
                {
                    product.SupplierId = ReadNullableString(
                        changes,
                        PosArticleMutationFields.SupplierId);
                }
                if (Has(changes, PosArticleMutationFields.RetailPrice))
                {
                    product.RetailPrice = ReadInt(
                        changes,
                        PosArticleMutationFields.RetailPrice);
                }
                if (Has(changes, PosArticleMutationFields.PurchasePrice))
                {
                    product.PurchasePrice = ReadInt(
                        changes,
                        PosArticleMutationFields.PurchasePrice);
                }
                if (Has(changes, PosArticleMutationFields.StockQuantity))
                {
                    product.StockQuantity = ReadInt(
                        changes,
                        PosArticleMutationFields.StockQuantity);
                }
                Require(
                    !string.IsNullOrWhiteSpace(product.Barcode) &&
                    !string.IsNullOrWhiteSpace(product.PrimaryName),
                    "server_product_fields_incomplete");
            }

            private static bool Has(
                IDictionary<string, object> changes,
                string key)
            {
                return changes != null &&
                    changes.ContainsKey(key);
            }

            private static string ReadString(
                IDictionary<string, object> changes,
                string key)
            {
                var value = ReadNullableString(changes, key);
                Require(
                    !string.IsNullOrWhiteSpace(value),
                    "server_missing_string_" + SafeCode(key));
                return value;
            }

            private static string ReadNullableString(
                IDictionary<string, object> changes,
                string key)
            {
                object value = null;
                Require(
                    changes != null &&
                    changes.TryGetValue(key, out value),
                    "server_missing_change_" + SafeCode(key));
                return value == null
                    ? null
                    : Convert.ToString(
                        value,
                        CultureInfo.InvariantCulture);
            }

            private static int ReadInt(
                IDictionary<string, object> changes,
                string key)
            {
                object value = null;
                Require(
                    changes != null &&
                    changes.TryGetValue(key, out value) &&
                    value != null,
                    "server_missing_number_" + SafeCode(key));
                return Convert.ToInt32(
                    value,
                    CultureInfo.InvariantCulture);
            }

            private static PosArticleMutationAck CloneAck(
                PosArticleMutationAck ack)
            {
                return new PosArticleMutationAck
                {
                    AttemptToken = ack.AttemptToken,
                    AuthoritativeRevision =
                        ack.AuthoritativeRevision,
                    CatalogRevision = ack.CatalogRevision,
                    Code = ack.Code,
                    IdempotencyKey = ack.IdempotencyKey,
                    MutationId = ack.MutationId,
                    PayloadHash = ack.PayloadHash,
                    PriceHistoryId = ack.PriceHistoryId,
                    RemoteProductId = ack.RemoteProductId,
                    Retryable = ack.Retryable,
                    SchemaVersion = ack.SchemaVersion,
                    ServerTimestamp = ack.ServerTimestamp,
                    Status = ack.Status,
                    StockMovementId = ack.StockMovementId,
                    Terminal = ack.Terminal
                };
            }
        }

        [DataContract]
        private sealed class MutationEnvelopeWire
        {
            [DataMember(Name = "schemaVersion")]
            public string SchemaVersion { get; set; }
            [DataMember(Name = "appVersion")]
            public string AppVersion { get; set; }
            [DataMember(Name = "shopId")]
            public string ShopId { get; set; }
            [DataMember(Name = "shopDeviceId")]
            public string ShopDeviceId { get; set; }
            [DataMember(Name = "staffId")]
            public string StaffId { get; set; }
            [DataMember(Name = "staffCredentialVersion")]
            public int StaffCredentialVersion { get; set; }
            [DataMember(Name = "posSessionId")]
            public string PosSessionId { get; set; }
            [DataMember(Name = "deviceToken")]
            public string DeviceToken { get; set; }
            [DataMember(Name = "sessionToken")]
            public string SessionToken { get; set; }
            [DataMember(Name = "mutations")]
            public MutationWire[] Mutations { get; set; }
        }

        [DataContract]
        private sealed class MutationWire
        {
            [DataMember(Name = "mutationId")]
            public string MutationId { get; set; }
            [DataMember(Name = "idempotencyKey")]
            public string IdempotencyKey { get; set; }
            [DataMember(Name = "payloadHash")]
            public string PayloadHash { get; set; }
            [DataMember(Name = "attemptToken")]
            public string AttemptToken { get; set; }
            [DataMember(Name = "mutationKind")]
            public string MutationKind { get; set; }
            [DataMember(Name = "clientProductId")]
            public string ClientProductId { get; set; }
            [DataMember(Name = "remoteProductId")]
            public string RemoteProductId { get; set; }
            [DataMember(Name = "baseRevision")]
            public string BaseRevision { get; set; }
            [DataMember(Name = "localSequence")]
            public long LocalSequence { get; set; }
            [DataMember(Name = "fieldMask")]
            public string[] FieldMask { get; set; }
            [DataMember(Name = "changes")]
            public Dictionary<string, object> Changes { get; set; }
            [DataMember(Name = "createdAt")]
            public string CreatedAt { get; set; }
            [DataMember(Name = "occurredAt")]
            public string OccurredAt { get; set; }
        }

        private sealed class RemoteProduct
        {
            public string RemoteProductId { get; set; }
            public string Barcode { get; set; }
            public string ItemNumber { get; set; }
            public string PrimaryName { get; set; }
            public string SecondaryName { get; set; }
            public string CategoryId { get; set; }
            public string SupplierId { get; set; }
            public int RetailPrice { get; set; }
            public int PurchasePrice { get; set; }
            public int StockQuantity { get; set; }
            public bool Active { get; set; }
            public string Revision { get; set; }

            public RemoteProduct Clone()
            {
                return new RemoteProduct
                {
                    RemoteProductId = RemoteProductId,
                    Barcode = Barcode,
                    ItemNumber = ItemNumber,
                    PrimaryName = PrimaryName,
                    SecondaryName = SecondaryName,
                    CategoryId = CategoryId,
                    SupplierId = SupplierId,
                    RetailPrice = RetailPrice,
                    PurchasePrice = PurchasePrice,
                    StockQuantity = StockQuantity,
                    Active = Active,
                    Revision = Revision
                };
            }
        }

        private sealed class RemotePrice
        {
            public string EffectiveAt { get; set; }
            public int Price { get; set; }
            public string PriceId { get; set; }
            public string ProductId { get; set; }
            public string Source { get; set; }
            public string Type { get; set; }
        }

        private sealed class ReplayRecord
        {
            public PosArticleMutationAck Ack { get; set; }
            public string PayloadHash { get; set; }
        }

        private sealed class RequestData
        {
            public string RequestLine { get; set; }
            public byte[] Body { get; set; }
        }

        private static T Deserialize<T>(byte[] bytes)
        {
            var serializer = new DataContractJsonSerializer(
                typeof(T),
                new DataContractJsonSerializerSettings
                {
                    UseSimpleDictionaryFormat = true
                });
            using (var stream = new MemoryStream(
                bytes ?? Array.Empty<byte>()))
            {
                return (T)serializer.ReadObject(stream);
            }
        }

        private static byte[] Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(
                typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return stream.ToArray();
            }
        }

        private static async Task<RequestData> ReadRequestAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            var bytes = new List<byte>();
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(
                        buffer,
                        0,
                        buffer.Length,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                    break;
                bytes.AddRange(buffer.Take(read));
                headerEnd = IndexOfHeaderEnd(bytes);
            }
            Require(headerEnd >= 0, "loopback_http_headers_incomplete");

            var headers = Encoding.ASCII.GetString(
                bytes.Take(headerEnd).ToArray());
            var contentLength = 0;
            foreach (var line in headers.Split(
                new[] { "\r\n" },
                StringSplitOptions.None))
            {
                var separator = line.IndexOf(':');
                if (separator > 0 &&
                    string.Equals(
                        line.Substring(0, separator).Trim(),
                        "Content-Length",
                        StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(
                        line.Substring(separator + 1).Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out contentLength);
                }
            }

            var bodyStart = headerEnd + 4;
            while (bytes.Count - bodyStart < contentLength)
            {
                var read = await stream.ReadAsync(
                        buffer,
                        0,
                        buffer.Length,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                    break;
                bytes.AddRange(buffer.Take(read));
            }
            Require(
                bytes.Count - bodyStart >= contentLength,
                "loopback_http_body_incomplete");
            var requestLine = headers.Split(
                new[] { "\r\n" },
                StringSplitOptions.None)[0];
            return new RequestData
            {
                RequestLine = requestLine,
                Body = bytes
                    .Skip(bodyStart)
                    .Take(contentLength)
                    .ToArray()
            };
        }

        private static int IndexOfHeaderEnd(
            IList<byte> bytes)
        {
            for (var index = 3; index < bytes.Count; index++)
            {
                if (bytes[index - 3] == 13 &&
                    bytes[index - 2] == 10 &&
                    bytes[index - 1] == 13 &&
                    bytes[index] == 10)
                {
                    return index - 3;
                }
            }
            return -1;
        }

        private static async Task WriteResponseAsync(
            NetworkStream stream,
            int statusCode,
            byte[] body,
            CancellationToken cancellationToken)
        {
            var reason = statusCode == 200
                ? "OK"
                : statusCode == 500
                    ? "Internal Server Error"
                    : "Not Found";
            var header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " +
                statusCode.ToString(
                    CultureInfo.InvariantCulture) +
                " " + reason + "\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: " +
                body.Length.ToString(
                    CultureInfo.InvariantCulture) +
                "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(
                    header,
                    0,
                    header.Length,
                    cancellationToken)
                .ConfigureAwait(false);
            await stream.WriteAsync(
                    body,
                    0,
                    body.Length,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
