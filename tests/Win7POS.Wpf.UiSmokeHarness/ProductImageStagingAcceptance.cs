using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dapper;
using Win7POS.Core;
using Win7POS.Core.Images;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Images;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Pos.Online;
using Win7POS.Wpf.Products;
using Win7POS.Wpf.Products.Images;

namespace Win7POS.Wpf.UiSmokeHarness
{
    /// <summary>
    /// Real staging-only acceptance for the TASK-150 synthetic fixture. Raw run
    /// markers, capabilities, credentials and signed URLs stay in memory or in
    /// one CurrentUser-DPAPI state file outside the evidence directory.
    /// </summary>
    internal static class ProductImageStagingAcceptance
    {
        internal const int RestartAfterOfflineQueue = 75;
        internal const int RestartAfterFirstCache = 76;
        internal const int CleanupFenceArmed = 77;
        internal const string AcceptanceMutexName =
            @"Global\Win7POS.ProductImagePhaseBAcceptance.v1";
        internal const string AcceptancePhaseMutexName =
            @"Global\Win7POS.ProductImagePhaseBAcceptance.Phase.v1";
        internal const string AcceptanceRunnerTokenEnvironmentVariable =
            "WIN7POS_PRODUCT_IMAGE_ACCEPTANCE_RUNNER_TOKEN";
        internal const string AcceptanceRunnerHandshakeFileName =
            "product-image-acceptance-runner.dpapi";

        private const string AllowedHost =
            "merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev";
        private const string QaSecretsDirectory = @"C:\ProgramData\Win7POS\QaSecrets";
        private const string FixtureTemplate =
            "asus-product-image-phase-b-fixture-v1";
        private const string StateFileName = "product-image-acceptance-state.dpapi";
        private const string SafeReportName = "product-image-staging-result.json";
        private const string BoundaryPath = "/api/qa/win7pos-product-image";
        private const string StorageOrigin = "https://jpgoimipbothfgkokyvm.supabase.co/";
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);

        internal static async Task<int> RunAsync(
            string sharedProfileName,
            string outputDirectory,
            string phase)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new InvalidOperationException("product_image_acceptance_output_required");
            EnsureIsolatedDataRoot();
            Directory.CreateDirectory(outputDirectory);
            switch ((phase ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "prepare":
                    return await PrepareAsync(sharedProfileName, outputDirectory)
                        .ConfigureAwait(true);
                case "resume":
                    return await ResumeAsync(outputDirectory).ConfigureAwait(true);
                case "cache-restart":
                    return await CacheRestartAsync(outputDirectory)
                        .ConfigureAwait(true);
                case "cleanup":
                    return await CleanupAsync(outputDirectory).ConfigureAwait(true);
                default:
                    throw new InvalidOperationException(
                        "product_image_acceptance_phase_invalid");
            }
        }

        private static async Task<int> PrepareAsync(
            string sharedProfileName,
            string outputDirectory)
        {
            EnsureIsolatedDataRoot();
            Require(!File.Exists(StatePath()),
                "product_image_acceptance_checkpoint_exists");
            var started = DateTimeOffset.UtcNow;
            var qaProfile = LoadQaProfile(sharedProfileName);
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            DbInitializer.EnsureCreated(PosDbOptions.Default());
            PosAdminWebOptions.SaveBaseUrl(qaProfile.BaseUri);
            var sharedStore = new PosTrustedDeviceStore();
            PosTrustedDeviceSession sharedSession;
            using (var sharedHost = new PosOnlineSyncSupervisorHost(factory))
            {
                var sharedBootstrap = await BootstrapAsync(
                    factory,
                    sharedStore,
                    sharedHost,
                    qaProfile.BaseUri,
                    qaProfile.ShopCode,
                    qaProfile.StaffCode,
                    qaProfile.Credential,
                    qaProfile.DeviceIdentifier,
                    qaProfile.DeviceDisplayName).ConfigureAwait(true);
                Require(sharedBootstrap != null && sharedBootstrap.CanOpenPos,
                    "shared_bootstrap_failed");
                Require(sharedStore.TryRead(out sharedSession),
                    "shared_trust_missing");
                await sharedHost.StopAsync().ConfigureAwait(true);
            }

            var runMarker = "ASUSPIB_" + RandomHex(16).ToUpperInvariant();
            var beginRequestId = RequestId("begin");
            BoundaryResponse armed;
            BoundaryResponse provisioned;
            var state = new AcceptanceState
            {
                BaseUrl = qaProfile.BaseUrl,
                BeginRequestId = beginRequestId,
                CacheRoot = CacheRoot(),
                CleanupRequestId = RequestId("cleanup"),
                FenceUntil = CanonicalTimestamp(
                    started.AddHours(2).AddMinutes(21)),
                ResultIssueRequestId = RequestId("result-issue"),
                ResultRequestId = RequestId("result"),
                RunMarker = runMarker,
                StartedAt = CanonicalTimestamp(started),
                Phase = "begin_pending"
            };
            SaveState(state);
            using (var boundary = new BoundaryClient(qaProfile.BaseUri))
            {
                armed = await boundary.PostTrustedAsync(
                    TrustedRequest.Begin(
                        sharedSession,
                        beginRequestId,
                        runMarker),
                    CancellationToken.None).ConfigureAwait(true);
                Require(armed.Ok &&
                        (armed.Code == "armed" || armed.Code == "begin_replayed") &&
                        IsLowerHex64(armed.RunHmac) &&
                        IsLowerHex64(armed.ManifestHmac) &&
                        IsCapability(armed.ProvisionCapability, "provision") &&
                        IsCapability(armed.CleanupCapability, "cleanup") &&
                        IsCapability(armed.ResultCapability, "result"),
                    "boundary_begin_failed");
                var runProfileName =
                    "asus-staging-image-phase-b-" + armed.RunHmac.Substring(0, 24);
                Require(PosTrustedDeviceStore.IsValidProfileName(runProfileName),
                    "run_profile_name_invalid");
                state.CleanupCapability = armed.CleanupCapability;
                state.ManifestHmac = armed.ManifestHmac;
                state.ResultCapability = armed.ResultCapability;
                state.RunHmac = armed.RunHmac;
                state.RunProfileName = runProfileName;
                state.Phase = "armed";
                SaveState(state);
                WriteSafeReport(outputDirectory, state, report =>
                {
                    report.CleanupPending = true;
                });
                provisioned = await boundary.PostCapabilityAsync(
                    CapabilityRequest.Provision(
                        armed,
                        RequestId("provision")),
                    CancellationToken.None).ConfigureAwait(true);
            }
            Uri bootstrapUri = null;
            Require(provisioned.Ok && provisioned.Code == "provisioned" &&
                    provisioned.CredentialsReissued &&
                    provisioned.BootstrapEnvelope != null &&
                    Guid.TryParse(provisioned.ProductId, out _) &&
                    Guid.TryParse(provisioned.ShopId, out _) &&
                    Guid.TryParse(provisioned.StaffId, out _) &&
                    Uri.TryCreate(
                        provisioned.BootstrapEnvelope.BaseUrl,
                        UriKind.Absolute,
                        out bootstrapUri) &&
                    IsAllowedBaseUri(bootstrapUri) &&
                    IsBoundedValue(provisioned.BootstrapEnvelope.ShopCode, 80) &&
                    IsBoundedValue(provisioned.BootstrapEnvelope.StaffCode, 80) &&
                    IsBoundedValue(
                        provisioned.BootstrapEnvelope.DeviceIdentifier,
                        160) &&
                    IsBoundedValue(
                        provisioned.BootstrapEnvelope.StaffCredential,
                        4096),
                "boundary_provision_failed");
            state.ProductId = provisioned.ProductId;
            state.ShopId = provisioned.ShopId;
            state.Phase = "provisioned";
            SaveState(state);
            WriteSafeReport(outputDirectory, state, report =>
            {
                report.CleanupPending = true;
            });

            var runStore = new PosTrustedDeviceStore(state.RunProfileName);
            Require(!runStore.HasStoredState(), "run_profile_already_exists");
            PosTrustedDeviceSession runSession;
            using (var runHost = new PosOnlineSyncSupervisorHost(
                factory,
                runStore,
                new FileLogger("ProductImageStagingRunHost")))
            {
                var runBootstrap = await BootstrapAsync(
                    factory,
                    runStore,
                    runHost,
                    bootstrapUri,
                    provisioned.BootstrapEnvelope.ShopCode,
                    provisioned.BootstrapEnvelope.StaffCode,
                    provisioned.BootstrapEnvelope.StaffCredential,
                    provisioned.BootstrapEnvelope.DeviceIdentifier,
                    "CASSA-ASUSPIB").ConfigureAwait(true);
                Require(runBootstrap != null && runBootstrap.CanOpenPos &&
                        runBootstrap.CatalogCompleted &&
                        runBootstrap.CatalogSaleSafe,
                    "run_bootstrap_catalog_failed");
                Require(runStore.TryRead(out runSession) &&
                        string.Equals(
                            runSession.ShopId,
                            provisioned.ShopId,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            runSession.StaffId,
                            provisioned.StaffId,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            runSession.ShopCode,
                            provisioned.BootstrapEnvelope.ShopCode,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            runSession.StaffCode,
                            provisioned.BootstrapEnvelope.StaffCode,
                            StringComparison.Ordinal),
                    "run_trust_identity_mismatch");
                await runHost.StopAsync().ConfigureAwait(true);
            }

            var product = await FindProductAsync(factory, provisioned.ProductId)
                .ConfigureAwait(true);
            Require(product != null && product.IsActive &&
                    product.UnitPrice >= 0 &&
                    string.IsNullOrWhiteSpace(product.PrimaryImageVersionId),
                "initial_no_image_product_invalid");

            var sourceRoot = Path.Combine(AppPaths.DataDirectory, "qa-sources");
            Directory.CreateDirectory(sourceRoot);
            var pngPath = Path.Combine(sourceRoot, "synthetic-offline.png");
            WriteSyntheticImage(pngPath, true, Color.FromRgb(24, 125, 210));
            var staging = IsolatedStagingStore();
            var workflow = new ProductImageWorkflowService(
                factory,
                staging,
                runStore);
            var queued = await workflow.ChooseOrReplaceAsync(
                product,
                pngPath,
                null,
                CancellationToken.None).ConfigureAwait(true);
            Require(queued != null && queued.PreviewBytes != null &&
                    queued.PreviewBytes.Length > 0 &&
                    queued.State == ProductImageOperationStates.PendingIntent,
                "offline_choose_not_durable");
            var outbox = new ProductImageOperationOutboxRepository(factory);
            var pending = await outbox.GetAsync(queued.OperationId)
                .ConfigureAwait(true);
            Require(pending != null &&
                    pending.State == ProductImageOperationStates.PendingIntent &&
                    File.Exists(Path.Combine(staging.RootPath, pending.StagedMainIdentity)) &&
                    File.Exists(Path.Combine(staging.RootPath, pending.StagedThumbIdentity)),
                "offline_restart_payload_missing");

            var requestedFence = started.AddHours(2).AddMinutes(25);
            BoundaryResponse prearmed;
            using (var boundary = new BoundaryClient(qaProfile.BaseUri))
            {
                prearmed = await boundary.PostTrustedAsync(
                    TrustedRequest.Prearm(
                        sharedSession,
                        RequestId("prearm"),
                        armed,
                        CanonicalTimestamp(requestedFence)),
                    CancellationToken.None).ConfigureAwait(true);
            }
            Require(prearmed.Ok &&
                    (prearmed.Code == "prearmed" ||
                     prearmed.Code == "prearm_replayed") &&
                    !prearmed.RotationRequired &&
                    IsCanonicalTimestamp(prearmed.FenceUntil),
                "cleanup_prearm_failed");

            state.FenceUntil = prearmed.FenceUntil;
            state.FirstOperationId = queued.OperationId;
            state.Phase = "offline_queued";
            SaveState(state);
            WriteSafeReport(outputDirectory, state, report =>
            {
                report.AuthBootstrap = true;
                report.CatalogFullDrain = true;
                report.NoImageInitial = true;
                report.SaleSafe = true;
                report.OfflineChoose = true;
                report.PreprocessPng = true;
                report.LocalPreview = true;
                report.DurableOutbox = true;
                report.RunProfileIsolated = true;
            });
            qaProfile.Clear();
            runMarker = null;
            return RestartAfterOfflineQueue;
        }

        private static async Task<int> ResumeAsync(string outputDirectory)
        {
            var state = LoadState();
            Require(state.Phase == "offline_queued", "resume_state_invalid");
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            DbInitializer.EnsureCreated(PosDbOptions.Default());
            PosAdminWebOptions.SaveBaseUrl(new Uri(state.BaseUrl));
            var runStore = new PosTrustedDeviceStore(state.RunProfileName);
            Require(runStore.TryRead(out var session), "resume_trust_missing");
            var outbox = new ProductImageOperationOutboxRepository(factory);
            var before = await outbox.GetAsync(state.FirstOperationId)
                .ConfigureAwait(true);
            Require(before != null &&
                    before.State == ProductImageOperationStates.PendingIntent,
                "restart_outbox_missing");
            var staging = IsolatedStagingStore();
            Require(File.Exists(Path.Combine(staging.RootPath, before.StagedMainIdentity)) &&
                    File.Exists(Path.Combine(staging.RootPath, before.StagedThumbIdentity)),
                "restart_staging_missing");

            var transitions = await DrainReplaceAsync(
                factory,
                staging,
                runStore,
                session,
                state.FirstOperationId,
                simulateResponseLoss: false).ConfigureAwait(true);
            Require(transitions.SequenceEqual(new[]
                {
                    "intent_ready", "upload_complete", "finalize_complete",
                    "cleanup_complete"
                }),
                "first_replace_transition_invalid");
            var product = await PullUntilImageAsync(
                factory,
                runStore,
                session,
                state.ProductId,
                value => !string.IsNullOrWhiteSpace(value.PrimaryImageVersionId),
                TimeSpan.FromMinutes(4)).ConfigureAwait(true);
            Require(product != null && Guid.TryParse(product.PrimaryImageVersionId, out _),
                "first_catalog_image_missing");

            var cached = await DownloadAndCacheAsync(
                state,
                session,
                product,
                outputDirectory,
                "first").ConfigureAwait(true);
            Require(cached.Main != null && cached.Thumb != null,
                "first_cache_failed");
            state.AccountScope = cached.AccountScope;
            state.FirstVersionId = product.PrimaryImageVersionId;
            state.FirstImageUpdatedAt = product.PrimaryImageUpdatedAt;
            state.FirstMain = cached.Main;
            state.FirstThumb = cached.Thumb;
            state.Phase = "first_cache_ready";
            SaveState(state);
            WriteSafeReport(outputDirectory, state, report =>
            {
                report.RestartOutboxSurvived = true;
                report.Intent = true;
                report.UploadMainThumb = true;
                report.Finalize = true;
                report.CatalogDelta = true;
                report.ExactVersionReference = true;
                report.ReadBack = true;
                report.ListThumb = true;
                report.EditorMain = true;
                report.CachePromoted = true;
            });
            return RestartAfterFirstCache;
        }

        private static async Task<int> CacheRestartAsync(string outputDirectory)
        {
            var state = LoadState();
            Require(state.Phase == "first_cache_ready", "cache_restart_state_invalid");
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            DbInitializer.EnsureCreated(PosDbOptions.Default());
            PosAdminWebOptions.SaveBaseUrl(new Uri(state.BaseUrl));
            var runStore = new PosTrustedDeviceStore(state.RunProfileName);
            Require(runStore.TryRead(out var session), "cache_restart_trust_missing");
            Require(await VerifyOfflineCacheAsync(state).ConfigureAwait(true),
                "offline_cache_restart_failed");
            var product = await FindProductAsync(factory, state.ProductId)
                .ConfigureAwait(true);
            Require(product != null && product.PrimaryImageVersionId == state.FirstVersionId,
                "replace_initial_version_changed");
            await CaptureOfflineRestartScreenshotsAsync(
                product,
                outputDirectory,
                state.RunProfileName).ConfigureAwait(true);

            var localOnlyId = await InsertFairnessProductAsync(factory)
                .ConfigureAwait(true);
            var sourceRoot = Path.Combine(AppPaths.DataDirectory, "qa-sources");
            Directory.CreateDirectory(sourceRoot);
            var fairnessPng = Path.Combine(sourceRoot, "synthetic-fairness.png");
            WriteSyntheticImage(fairnessPng, true, Color.FromRgb(90, 45, 170));
            var staging = IsolatedStagingStore();
            var workflow = new ProductImageWorkflowService(factory, staging, runStore);
            var localOnly = await new ProductRepository(factory)
                .GetDetailsByIdAsync(localOnlyId).ConfigureAwait(true);
            var waiting = await workflow.ChooseOrReplaceAsync(
                localOnly,
                fairnessPng,
                null,
                CancellationToken.None).ConfigureAwait(true);
            Require(waiting.State == ProductImageOperationStates.WaitingDependency,
                "fairness_dependency_not_waiting");

            var jpegPath = Path.Combine(sourceRoot, "synthetic-replace.jpg");
            WriteSyntheticImage(jpegPath, false, Color.FromRgb(222, 92, 34));
            var replace = await workflow.ChooseOrReplaceAsync(
                product,
                jpegPath,
                null,
                CancellationToken.None).ConfigureAwait(true);
            Require(replace.State == ProductImageOperationStates.PendingIntent,
                "replace_not_queued");
            Require(await VerifyPromotedVersionAsync(state, state.FirstVersionId)
                    .ConfigureAwait(true),
                "old_cache_not_retained_before_finalize");

            var transitions = await DrainReplaceAsync(
                factory,
                staging,
                runStore,
                session,
                replace.OperationId,
                simulateResponseLoss: true).ConfigureAwait(true);
            Require(transitions.SequenceEqual(new[]
                {
                    "intent_ready", "upload_complete", "finalize_complete",
                    "cleanup_complete"
                }),
                "replacement_transition_invalid");
            product = await PullUntilImageAsync(
                factory,
                runStore,
                session,
                state.ProductId,
                value => !string.IsNullOrWhiteSpace(value.PrimaryImageVersionId) &&
                         value.PrimaryImageVersionId != state.FirstVersionId,
                TimeSpan.FromMinutes(4)).ConfigureAwait(true);
            Require(product != null && product.PrimaryImageVersionId != state.FirstVersionId,
                "replacement_version_unchanged");
            var second = await DownloadAndCacheAsync(
                state,
                session,
                product,
                outputDirectory,
                "replacement").ConfigureAwait(true);
            state.SecondVersionId = product.PrimaryImageVersionId;
            state.SecondImageUpdatedAt = product.PrimaryImageUpdatedAt;
            state.SecondMain = second.Main;
            state.SecondThumb = second.Thumb;
            Require(await VerifyPromotedVersionAsync(state, state.SecondVersionId)
                    .ConfigureAwait(true),
                "replacement_cache_not_promoted");
            Require(await VerifyOldCacheInvalidatedAsync(state)
                    .ConfigureAwait(true),
                "old_cache_not_invalidated_after_promotion");

            using (var transport = new PosProductImageClient(
                new PosAdminWebOptions(new Uri(state.BaseUrl)),
                new Uri(StorageOrigin)))
            {
                var row = await new ProductImageOperationOutboxRepository(factory)
                    .GetAsync(replace.OperationId).ConfigureAwait(true);
                var envelope = Envelope(session);
                var intentReplay = await transport.IntentAsync(
                    IntentRequest(row, envelope),
                    CancellationToken.None).ConfigureAwait(true);
                Require(intentReplay.IsSuccess, "intent_replay_failed");
                var finalizeReplay = await transport.FinalizeAsync(
                    FinalizeRequest(row, envelope),
                    CancellationToken.None).ConfigureAwait(true);
                Require(finalizeReplay.IsSuccess, "finalize_replay_failed");
                var mismatch = await transport.IntentAsync(
                    new PosProductImageIntentRequest(
                        row.OperationId + "-intent",
                        row.IdempotencyKey + "-intent",
                        envelope,
                        row.RemoteProductId,
                        null,
                        Metadata(row, ProductImageVariant.Main),
                        Metadata(row, ProductImageVariant.Thumb)),
                    CancellationToken.None).ConfigureAwait(true);
                Require(!mismatch.IsSuccess &&
                        mismatch.FailureKind ==
                            PosProductImageFailureKind.IdempotencyMismatch,
                    "payload_hash_mismatch_not_rejected");

                var staleRemove = await transport.RemoveAsync(
                    new PosProductImageRemoveRequest(
                        "image-op-stale-" + RandomHex(8),
                        "image-idem-stale-" + RandomHex(8),
                        envelope,
                        state.ProductId,
                        state.FirstVersionId),
                    CancellationToken.None).ConfigureAwait(true);
                Require(!staleRemove.IsSuccess &&
                        staleRemove.FailureKind == PosProductImageFailureKind.Conflict,
                    "stale_remove_not_rejected");

                await VerifyExpiredCapabilitiesAsync(
                    transport,
                    state,
                    session,
                    product,
                    row).ConfigureAwait(true);
            }

            product = await FindProductAsync(factory, state.ProductId)
                .ConfigureAwait(true);
            var remove = await workflow.RemoveAsync(product, CancellationToken.None)
                .ConfigureAwait(true);
            var removeTransitions = await DrainRemoveAsync(
                factory,
                staging,
                runStore,
                session,
                remove.OperationId).ConfigureAwait(true);
            Require(removeTransitions.Count == 2 &&
                    removeTransitions[1] == "cleanup_complete",
                "remove_transition_invalid");
            var removedProduct = await PullUntilImageAsync(
                factory,
                runStore,
                session,
                state.ProductId,
                value => string.IsNullOrWhiteSpace(value.PrimaryImageVersionId) &&
                         !string.IsNullOrWhiteSpace(value.PrimaryImageUpdatedAt),
                TimeSpan.FromMinutes(4)).ConfigureAwait(true);
            Require(removedProduct != null &&
                    string.IsNullOrWhiteSpace(removedProduct.PrimaryImageVersionId),
                "catalog_remove_missing");
            var removeRow = await new ProductImageOperationOutboxRepository(factory)
                .GetAsync(remove.OperationId).ConfigureAwait(true);
            using (var transport = new PosProductImageClient(
                new PosAdminWebOptions(new Uri(state.BaseUrl)),
                new Uri(StorageOrigin)))
            {
                var replay = await transport.RemoveAsync(
                    new PosProductImageRemoveRequest(
                        removeRow.OperationId + "-remove",
                        removeRow.IdempotencyKey + "-remove",
                        Envelope(session),
                        state.ProductId,
                        state.SecondVersionId),
                    CancellationToken.None).ConfigureAwait(true);
                Require(replay.IsSuccess, "remove_replay_failed");
            }
            using (var cache = IsolatedCache(state))
            {
                var purged = await cache.PurgeProductAsync(
                    state.AccountScope,
                    Guid.Parse(state.ShopId),
                    Guid.Parse(state.ProductId),
                    null,
                    CancellationToken.None).ConfigureAwait(true);
                var snapshot = await cache.GetSnapshotAsync().ConfigureAwait(true);
                Require(purged >= 2 && snapshot.EntryCount == 0,
                    "exact_cache_purge_failed");
            }
            var waitingAfter = await new ProductImageOperationOutboxRepository(factory)
                .GetAsync(waiting.OperationId).ConfigureAwait(true);
            Require(waitingAfter != null &&
                    waitingAfter.State ==
                        ProductImageOperationStates.WaitingDependency,
                "unrelated_fairness_not_preserved");
            var persistenceCount = CountForbiddenPersistenceMarkers();
            Require(persistenceCount == 0,
                "signed_url_persistence_detected");
            await CaptureNoImageScreenshotsAsync(
                removedProduct,
                outputDirectory,
                state.RunProfileName).ConfigureAwait(true);
            state.Phase = "cleanup_fence_armed";
            SaveState(state);
            WriteSafeReport(outputDirectory, state, report =>
            {
                report.OfflineCacheRestart = true;
                report.ReplaceJpeg = true;
                report.OldImageRetainedUntilFinalize = true;
                report.VersionChanged = true;
                report.NewCachePromoted = true;
                report.OldCacheInvalidated = true;
                report.IntentReplay = true;
                report.FinalizeReplay = true;
                report.ResponseLossRecovery = true;
                report.ExpiredReadRenewed = true;
                report.ExpiredUploadRejected = true;
                report.PayloadHashMismatchRejected = true;
                report.StaleConflictProtected = true;
                report.UnrelatedFairness = true;
                report.Remove = true;
                report.CatalogNullAfterRemove = true;
                report.CachePurged = true;
                report.RemoveReplay = true;
                report.NoHardwareActions = true;
                report.NoSalesEffects = true;
                report.SignedUrlPersistenceCount = persistenceCount;
                report.CleanupPending = true;
            });
            return CleanupFenceArmed;
        }

        private static async Task<int> CleanupAsync(string outputDirectory)
        {
            EnsureIsolatedDataRoot();
            var state = LoadState();
            Require(state.Phase != "terminal_clean", "cleanup_state_invalid");
            var sharedStore = new PosTrustedDeviceStore();
            Require(sharedStore.TryRead(out var sharedSession),
                "shared_trust_missing_for_cleanup");
            if (state.Phase == "begin_pending")
            {
                BoundaryResponse recovered;
                using (var boundary = new BoundaryClient(new Uri(state.BaseUrl)))
                {
                    recovered = await boundary.PostTrustedAsync(
                        TrustedRequest.Begin(
                            sharedSession,
                            state.BeginRequestId,
                            state.RunMarker),
                        CancellationToken.None).ConfigureAwait(true);
                }
                Require(recovered.Ok &&
                        (recovered.Code == "armed" ||
                         recovered.Code == "begin_replayed") &&
                        IsLowerHex64(recovered.RunHmac) &&
                        IsLowerHex64(recovered.ManifestHmac) &&
                        IsCapability(
                            recovered.ProvisionCapability,
                            "provision") &&
                        IsCapability(recovered.CleanupCapability, "cleanup") &&
                        IsCapability(recovered.ResultCapability, "result"),
                    "boundary_begin_recovery_failed");
                state.CleanupCapability = recovered.CleanupCapability;
                state.ManifestHmac = recovered.ManifestHmac;
                state.ResultCapability = recovered.ResultCapability;
                state.RunHmac = recovered.RunHmac;
                state.RunProfileName = "asus-staging-image-phase-b-" +
                    recovered.RunHmac.Substring(0, 24);
                Require(PosTrustedDeviceStore.IsValidProfileName(
                        state.RunProfileName),
                    "recovered_run_profile_name_invalid");
                state.Phase = "armed";
                SaveState(state);
                WriteSafeReport(outputDirectory, state, report =>
                {
                    report.CleanupPending = true;
                });
            }
            var phaseAtCleanup = state.Phase;
            var cleanupAt = ParseRequiredTimestamp(state.FenceUntil)
                .AddSeconds(15);
            if (DateTimeOffset.UtcNow < cleanupAt)
            {
                WriteSafeReport(outputDirectory, state, report =>
                {
                    report.CleanupPending = true;
                });
                return CleanupFenceArmed;
            }
            BoundaryResponse cleanup;
            BoundaryResponse terminal;
            using (var boundary = new BoundaryClient(new Uri(state.BaseUrl)))
            {
                var issued = await boundary.PostTrustedAsync(
                    TrustedRequest.ResultIssue(
                        sharedSession,
                        state.ResultIssueRequestId,
                        state),
                    CancellationToken.None).ConfigureAwait(true);
                Require(issued.Ok && issued.Code == "result_issued" &&
                        IsCapability(issued.ResultCapability, "result"),
                    "boundary_result_issue_failed_" + SafeCode(issued.Code));
                state.ResultCapability = issued.ResultCapability;
                SaveState(state);
                cleanup = await boundary.PostCapabilityAsync(
                    CapabilityRequest.Cleanup(
                        state,
                        state.CleanupRequestId),
                    CancellationToken.None).ConfigureAwait(true);
                Require(cleanup.Ok && cleanup.Code == "cleanup_complete" &&
                        cleanup.Receipt != null,
                    "boundary_cleanup_failed_" + SafeCode(cleanup.Code));
                terminal = await boundary.PostCapabilityAsync(
                    CapabilityRequest.Result(
                        state,
                        state.ResultRequestId),
                    CancellationToken.None).ConfigureAwait(true);
            }
            var productCountValid = phaseAtCleanup == "armed"
                ? terminal?.Receipt?.Counts != null &&
                  terminal.Receipt.Counts.Products >= 0 &&
                  terminal.Receipt.Counts.Products <= 1
                : terminal?.Receipt?.Counts != null &&
                  terminal.Receipt.Counts.Products == 1;
            var minimumImageVersions =
                phaseAtCleanup == "first_cache_ready" ||
                phaseAtCleanup == "cleanup_fence_armed"
                    ? 1
                    : 0;
            Require(terminal.Ok && terminal.Code == "terminal" &&
                    terminal.Receipt != null &&
                    terminal.Receipt.Counts != null &&
                    terminal.Receipt.Counts.StorageObjects == 0 &&
                    terminal.Receipt.Counts.ActiveRunOwnedSessions == 0 &&
                    productCountValid &&
                    terminal.Receipt.Counts.ImageVersions >=
                        minimumImageVersions &&
                    terminal.Receipt.SchemaVersion ==
                        "task-150-win7pos-image-qa-cleanup-v1" &&
                    terminal.Receipt.Code == "cleanup_complete" &&
                    terminal.Receipt.RunHmac == state.RunHmac &&
                    terminal.Receipt.ManifestHmac == state.ManifestHmac &&
                    IsLowerHex64(terminal.Receipt.ReceiptHmac) &&
                    terminal.Receipt.SharedSnapshotUnchanged &&
                    terminal.Receipt.ImmutableAuditPreserved &&
                    terminal.Receipt.CleanupCapabilityRevoked,
                "terminal_cleanup_receipt_invalid");

            var runStore = new PosTrustedDeviceStore(state.RunProfileName);
            runStore.Clear();
            new PosTrustedDeviceStore().Clear();
            state.Phase = "terminal_clean";
            WriteSafeReport(outputDirectory, state, report =>
            {
                report.CleanupComplete = true;
                report.CleanupPending = false;
                report.DbResiduals = 0;
                report.StorageResiduals = 0;
                report.ActiveActorSessionResiduals = 0;
                report.SharedSnapshotUnchanged = true;
                report.ImmutableAuditPreserved = true;
                report.RunProfileRemoved = !runStore.HasStoredState();
                report.Passed = false;
            });
            Require(ScanTextArtifacts(outputDirectory, state),
                "evidence_redaction_failed");
            WriteSafeReport(outputDirectory, state, report =>
            {
                report.Passed = IsFullMatrixComplete(report);
            });
            DeleteState();
            return 0;
        }

        private static async Task<PosOnlineBootstrapResult> BootstrapAsync(
            SqliteConnectionFactory factory,
            PosTrustedDeviceStore store,
            PosOnlineSyncSupervisorHost host,
            Uri baseUri,
            string shopCode,
            string staffCode,
            string credential,
            string deviceIdentifier,
            string displayName)
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12)))
            {
                return await new PosOnlineBootstrapService(factory, store, host)
                    .BootstrapAsync(
                        new PosAdminWebOptions(baseUri),
                        new PosFirstLoginRequest
                        {
                            Credential = credential,
                            ShopCode = shopCode,
                            StaffCode = staffCode,
                            Device = new PosFirstLoginDevice
                            {
                                AppVersion = AppVersion(),
                                DeviceIdentifier = deviceIdentifier,
                                DisplayName = displayName
                            }
                        },
                        credential,
                        timeout.Token).ConfigureAwait(true);
            }
        }

        private static async Task<IReadOnlyList<string>> DrainReplaceAsync(
            SqliteConnectionFactory factory,
            ProductImageStagingStore staging,
            PosTrustedDeviceStore store,
            PosTrustedDeviceSession session,
            string operationId,
            bool simulateResponseLoss)
        {
            var service = new ProductImageSyncService(
                factory,
                staging,
                null,
                null);
            var result = new List<string>();
            var row = await new ProductImageOperationOutboxRepository(factory)
                .GetAsync(operationId).ConfigureAwait(true);
            if (simulateResponseLoss)
            {
                using (var transport = new PosProductImageClient(
                    new PosAdminWebOptions(new Uri(ReadBaseUrl())),
                    new Uri(StorageOrigin)))
                {
                    var ignored = await transport.IntentAsync(
                        IntentRequest(row, Envelope(session)),
                        CancellationToken.None).ConfigureAwait(true);
                    Require(ignored.IsSuccess, "intent_response_loss_setup_failed");
                }
            }
            for (var step = 0; step < 4; step++)
            {
                if (simulateResponseLoss && step == 2)
                {
                    row = await new ProductImageOperationOutboxRepository(factory)
                        .GetAsync(operationId).ConfigureAwait(true);
                    using (var transport = new PosProductImageClient(
                        new PosAdminWebOptions(new Uri(ReadBaseUrl())),
                        new Uri(StorageOrigin)))
                    {
                        var ignored = await transport.FinalizeAsync(
                            FinalizeRequest(row, Envelope(session)),
                            CancellationToken.None).ConfigureAwait(true);
                        Require(ignored.IsSuccess,
                            "finalize_response_loss_setup_failed");
                    }
                }
                var sync = await service.SyncNextAsync(
                    new PosAdminWebOptions(new Uri(ReadBaseUrl())),
                    new Uri(StorageOrigin),
                    session,
                    Context(store, session, OnlineSyncLane.ProductImageOutbox),
                    AppVersion(),
                    CancellationToken.None).ConfigureAwait(true);
                Require(sync.Success, "image_sync_failed_" + SafeCode(sync.Code));
                result.Add(sync.Code);
            }
            return result;
        }

        private static async Task<IReadOnlyList<string>> DrainRemoveAsync(
            SqliteConnectionFactory factory,
            ProductImageStagingStore staging,
            PosTrustedDeviceStore store,
            PosTrustedDeviceSession session,
            string operationId)
        {
            var service = new ProductImageSyncService(factory, staging, null, null);
            var result = new List<string>();
            for (var step = 0; step < 2; step++)
            {
                var sync = await service.SyncNextAsync(
                    new PosAdminWebOptions(new Uri(ReadBaseUrl())),
                    new Uri(StorageOrigin),
                    session,
                    Context(store, session, OnlineSyncLane.ProductImageOutbox),
                    AppVersion(),
                    CancellationToken.None).ConfigureAwait(true);
                Require(sync.Success, "remove_sync_failed_" + SafeCode(sync.Code));
                result.Add(sync.Code);
            }
            var row = await new ProductImageOperationOutboxRepository(factory)
                .GetAsync(operationId).ConfigureAwait(true);
            Require(row != null && row.State == ProductImageOperationStates.Completed,
                "remove_outbox_not_completed");
            return result;
        }

        private static OnlineSyncLaneExecutionContext Context(
            PosTrustedDeviceStore store,
            PosTrustedDeviceSession session,
            OnlineSyncLane lane)
        {
            var generation = Generation(session);
            Require(store.TryReadGeneration(
                    generation,
                    out var current,
                    out var stamp),
                "trusted_generation_missing");
            return new OnlineSyncLaneExecutionContext(
                generation,
                lane,
                new PriorityOnlineRequestGate(2),
                _ => Task.FromResult(true),
                code => Task.FromException(
                    new InvalidOperationException("authentication_stop_" + SafeCode(code))),
                _ => Task.FromResult(new OnlineSyncRequestCredentials(
                    generation,
                    current.DeviceToken,
                    current.SessionToken,
                    stamp)));
        }

        private static OnlineSyncGeneration Generation(PosTrustedDeviceSession session)
        {
            return new OnlineSyncGeneration(
                session.GenerationId,
                session.PosSessionId,
                session.ShopDeviceId,
                session.ShopId,
                session.ShopCode,
                session.StaffId,
                session.StaffCredentialVersion);
        }

        private static async Task<ProductDetailsRow> PullUntilImageAsync(
            SqliteConnectionFactory factory,
            PosTrustedDeviceStore store,
            PosTrustedDeviceSession session,
            string productId,
            Func<ProductDetailsRow, bool> predicate,
            TimeSpan timeout)
        {
            var until = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < until)
            {
                var outcome = await new PosCatalogPullService(
                        factory,
                        store,
                        new FileLogger("ProductImageStagingCatalog"),
                        Generation(session))
                    .TryPullIncrementalCatalogAsync(
                        new PosAdminWebOptions(new Uri(ReadBaseUrl())),
                        session,
                        Generation(session),
                        Context(store, session, OnlineSyncLane.CatalogDelta),
                        CancellationToken.None).ConfigureAwait(true);
                Require(!outcome.AuthDenied, "catalog_auth_denied");
                var product = await FindProductAsync(factory, productId)
                    .ConfigureAwait(true);
                if (product != null && predicate(product)) return product;
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
            }
            return null;
        }

        private static async Task<ProductDetailsRow> FindProductAsync(
            SqliteConnectionFactory factory,
            string remoteProductId)
        {
            var rows = await new ProductRepository(factory).ListAllDetailsAsync()
                .ConfigureAwait(true);
            return rows.FirstOrDefault(row => string.Equals(
                row.RemoteProductId,
                remoteProductId,
                StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<CacheEvidence> DownloadAndCacheAsync(
            AcceptanceState state,
            PosTrustedDeviceSession session,
            ProductDetailsRow product,
            string outputDirectory,
            string label)
        {
            using (var client = new PosProductImageClient(
                new PosAdminWebOptions(new Uri(state.BaseUrl)),
                new Uri(StorageOrigin)))
            {
                var response = await client.ReadUrlsAsync(
                    new PosProductImageReadUrlsRequest(
                        Envelope(session),
                        new[]
                        {
                            new PosProductImageReadRef(
                                state.ProductId,
                                "main",
                                product.PrimaryImageVersionId),
                            new PosProductImageReadRef(
                                state.ProductId,
                                "thumb",
                                product.PrimaryImageVersionId)
                        }),
                    CancellationToken.None).ConfigureAwait(true);
                Require(response.IsSuccess && response.Value.Items.Length == 2,
                    "read_urls_failed");
                var accountScope =
                    ProductImageCacheScopeStore.DeriveAccountScope(
                        response.Value.CacheScope);
                var evidence = new CacheEvidence
                {
                    AccountScope = accountScope
                };
                using (var cache = IsolatedCache(state))
                {
                    var scopeStore = new ProductImageCacheScopeStore(
                        new SqliteConnectionFactory(PosDbOptions.Default()));
                    var binding = await scopeStore.BindWithTransitionAsync(
                        session.StaffId,
                        session.ShopId,
                        response.Value.CacheScope,
                        CancellationToken.None).ConfigureAwait(true);
                    Require(string.Equals(
                            binding.AccountScope,
                            accountScope,
                            StringComparison.Ordinal),
                        "cache_scope_binding_invalid");
                    if (!string.IsNullOrEmpty(binding.PurgeToken))
                    {
                        await cache.PurgeAllAsync(CancellationToken.None)
                            .ConfigureAwait(true);
                        Require(await scopeStore.AcknowledgePurgeAsync(
                                session.StaffId,
                                session.ShopId,
                                accountScope,
                                binding.PurgeToken,
                                CancellationToken.None).ConfigureAwait(true),
                            "cache_scope_purge_ack_failed");
                    }
                    foreach (var item in response.Value.Items)
                    {
                        var variant = item.Variant == "main"
                            ? ProductImageVariant.Main
                            : ProductImageVariant.Thumb;
                        Require(ProductImageIdentity.TryCreate(
                                accountScope,
                                session.ShopId,
                                item.ProductId,
                                item.VersionId,
                                out var identity,
                                out _),
                            "cache_identity_invalid");
                        Require(ProductImageMetadata.TryCreate(
                                variant,
                                item.Metadata.MimeType,
                                item.Metadata.Bytes,
                                item.Metadata.Width,
                                item.Metadata.Height,
                                item.Metadata.Sha256,
                                out var metadata,
                                out _),
                            "cache_metadata_invalid");
                        var reference = new ProductImageReference(
                            identity,
                            variant,
                            metadata,
                            ParseTimestamp(product.PrimaryImageUpdatedAt));
                        await cache.GetOrAddAsync(
                            reference,
                            async token =>
                            {
                                var download = await client.DownloadJpegAsync(
                                    item.SignedUrl,
                                    session.ShopId,
                                    item.ProductId,
                                    item.VersionId,
                                    item.Variant,
                                    item.Metadata,
                                    token).ConfigureAwait(true);
                                if (!download.IsSuccess)
                                    throw new InvalidDataException(download.Code);
                                return new MemoryStream(
                                    download.CopyBytes(),
                                    writable: false);
                            },
                            CancellationToken.None).ConfigureAwait(true);
                        await cache.PromoteVariantAsync(reference)
                            .ConfigureAwait(true);
                        var variantEvidence = new VariantEvidence
                        {
                            Bytes = metadata.ByteSize,
                            Height = metadata.Height,
                            Sha256 = metadata.Sha256,
                            Width = metadata.Width
                        };
                        if (variant == ProductImageVariant.Main)
                        {
                            evidence.Main = variantEvidence;
                        }
                        else
                        {
                            evidence.Thumb = variantEvidence;
                        }
                    }
                }
                await CaptureLoadedUiScreenshotsAsync(
                    product,
                    outputDirectory,
                    label,
                    state.RunProfileName).ConfigureAwait(true);
                return evidence;
            }
        }

        private static async Task<bool> VerifyOfflineCacheAsync(AcceptanceState state)
        {
            using (var cache = IsolatedCache(state))
            {
                return await PromotedEntryAsync(
                           cache,
                           state,
                           state.FirstVersionId,
                           ProductImageVariant.Main,
                           state.FirstMain).ConfigureAwait(true) != null &&
                       await PromotedEntryAsync(
                           cache,
                           state,
                           state.FirstVersionId,
                           ProductImageVariant.Thumb,
                           state.FirstThumb).ConfigureAwait(true) != null;
            }
        }

        private static async Task<bool> VerifyPromotedVersionAsync(
            AcceptanceState state,
            string versionId)
        {
            using (var cache = IsolatedCache(state))
            {
                var main = await cache.GetPromotedForProductAsync(
                    state.AccountScope,
                    Guid.Parse(state.ShopId),
                    Guid.Parse(state.ProductId),
                    ProductImageVariant.Main).ConfigureAwait(true);
                return main != null &&
                    string.Equals(
                        main.Reference.Identity.VersionId.ToString("D"),
                        versionId,
                        StringComparison.OrdinalIgnoreCase);
            }
        }

        private static async Task<bool> VerifyOldCacheInvalidatedAsync(
            AcceptanceState state)
        {
            ProductImageIdentity identity;
            if (!ProductImageIdentity.TryCreate(
                    state.AccountScope,
                    state.ShopId,
                    state.ProductId,
                    state.FirstVersionId,
                    out identity,
                    out _))
            {
                return false;
            }
            using (var cache = IsolatedCache(state))
            {
                var main = await cache.GetPromotedAsync(
                    identity,
                    ProductImageVariant.Main).ConfigureAwait(true);
                var thumb = await cache.GetPromotedAsync(
                    identity,
                    ProductImageVariant.Thumb).ConfigureAwait(true);
                var snapshot = await cache.GetSnapshotAsync().ConfigureAwait(true);
                return main == null && thumb == null && snapshot.EntryCount == 2;
            }
        }

        private static async Task<ProductImageCacheEntry> PromotedEntryAsync(
            ProductImageDiskCache cache,
            AcceptanceState state,
            string versionId,
            ProductImageVariant variant,
            VariantEvidence evidence)
        {
            ProductImageIdentity identity = null;
            Require(evidence != null && ProductImageIdentity.TryCreate(
                    state.AccountScope,
                    state.ShopId,
                    state.ProductId,
                    versionId,
                    out identity,
                    out _),
                "offline_cache_identity_invalid");
            var entry = await cache.GetPromotedAsync(identity, variant)
                .ConfigureAwait(true);
            if (entry == null || entry.ByteSize != evidence.Bytes ||
                entry.Reference.Metadata.ByteSize != evidence.Bytes ||
                entry.Reference.Metadata.Width != evidence.Width ||
                entry.Reference.Metadata.Height != evidence.Height ||
                !string.Equals(
                    entry.Reference.Metadata.Sha256,
                    evidence.Sha256,
                    StringComparison.Ordinal))
            {
                return null;
            }
            var bytes = entry.CopyBytes();
            try
            {
                return string.Equals(
                    ProductImageHash.Sha256Hex(bytes),
                    evidence.Sha256,
                    StringComparison.Ordinal)
                    ? entry
                    : null;
            }
            finally
            {
                Clear(bytes);
            }
        }

        private static async Task VerifyExpiredCapabilitiesAsync(
            PosProductImageClient transport,
            AcceptanceState state,
            PosTrustedDeviceSession session,
            ProductDetailsRow product,
            ProductImageOperationRow sourceRow)
        {
            var envelope = Envelope(session);
            var read = await transport.ReadUrlsAsync(
                new PosProductImageReadUrlsRequest(
                    envelope,
                    new[]
                    {
                        new PosProductImageReadRef(
                            state.ProductId,
                            "thumb",
                            state.SecondVersionId)
                    }),
                CancellationToken.None).ConfigureAwait(true);
            Require(read.IsSuccess && read.Value.Items.Length == 1 &&
                    read.Value.Items[0].Status == "ready",
                "expiry_read_setup_failed");
            var readItem = read.Value.Items[0];
            var expiryIntent = new PosProductImageIntentRequest(
                "image-op-expiry-" + RandomHex(8),
                "image-idem-expiry-" + RandomHex(8),
                envelope,
                state.ProductId,
                state.SecondVersionId,
                Metadata(sourceRow, ProductImageVariant.Main),
                Metadata(sourceRow, ProductImageVariant.Thumb));
            var intent = await transport.IntentAsync(
                expiryIntent,
                CancellationToken.None).ConfigureAwait(true);
            Require(intent.IsSuccess && intent.Value.Status == "upload_required",
                "expiry_upload_setup_failed");
            var waitUntil = new[]
            {
                ParseRequiredTimestamp(readItem.ExpiresAt),
                ParseRequiredTimestamp(intent.Value.ExpiresAt)
            }.Max().AddSeconds(3);
            var delay = waitUntil - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay).ConfigureAwait(true);

            var expiredRead = await transport.DownloadJpegAsync(
                readItem.SignedUrl,
                session.ShopId,
                readItem.ProductId,
                readItem.VersionId,
                readItem.Variant,
                readItem.Metadata,
                CancellationToken.None).ConfigureAwait(true);
            Require(!expiredRead.IsSuccess &&
                    expiredRead.Code == "expired_capability",
                "expired_read_not_rejected");
            var renewed = await transport.ReadUrlsAsync(
                new PosProductImageReadUrlsRequest(
                    envelope,
                    new[]
                    {
                        new PosProductImageReadRef(
                            state.ProductId,
                            "thumb",
                            state.SecondVersionId)
                    }),
                CancellationToken.None).ConfigureAwait(true);
            Require(renewed.IsSuccess &&
                    renewed.Value.Items[0].Status == "ready",
                "expired_read_not_renewed");
            var renewedDownload = await transport.DownloadJpegAsync(
                renewed.Value.Items[0].SignedUrl,
                session.ShopId,
                state.ProductId,
                state.SecondVersionId,
                "thumb",
                renewed.Value.Items[0].Metadata,
                CancellationToken.None).ConfigureAwait(true);
            Require(renewedDownload.IsSuccess, "renewed_read_failed");

            byte[] uploadBytes;
            using (var cache = IsolatedCache(state))
            {
                var promoted = await cache.GetPromotedForProductAsync(
                    state.AccountScope,
                    Guid.Parse(state.ShopId),
                    Guid.Parse(state.ProductId),
                    ProductImageVariant.Main).ConfigureAwait(true);
                Require(promoted != null &&
                        string.Equals(
                            promoted.Reference.Identity.VersionId.ToString("D"),
                            state.SecondVersionId,
                            StringComparison.OrdinalIgnoreCase) &&
                        promoted.ByteSize == sourceRow.MainBytes.Value,
                    "expired_upload_source_missing");
                uploadBytes = promoted.CopyBytes();
            }
            using (var main = new MemoryStream(uploadBytes, writable: false))
            {
                var expiredUpload = await transport.UploadJpegAsync(
                    intent.Value.MainUploadUrl,
                    session.ShopId,
                    state.ProductId,
                    intent.Value.VersionId,
                    "main",
                    main,
                    sourceRow.MainBytes.Value,
                    CancellationToken.None).ConfigureAwait(true);
                Require(!expiredUpload.IsSuccess &&
                        expiredUpload.Code == "expired_capability",
                    "expired_upload_not_rejected");
            }
            Clear(uploadBytes);
        }

        private static PosProductImageIntentRequest IntentRequest(
            ProductImageOperationRow row,
            PosProductImageEnvelope envelope)
        {
            return new PosProductImageIntentRequest(
                row.OperationId + "-intent",
                row.IdempotencyKey + "-intent",
                envelope,
                row.RemoteProductId,
                row.ExpectedCurrentVersionId,
                Metadata(row, ProductImageVariant.Main),
                Metadata(row, ProductImageVariant.Thumb));
        }

        private static PosProductImageFinalizeRequest FinalizeRequest(
            ProductImageOperationRow row,
            PosProductImageEnvelope envelope)
        {
            return new PosProductImageFinalizeRequest(
                row.OperationId + "-finalize",
                row.IdempotencyKey + "-finalize",
                envelope,
                row.RemoteProductId,
                row.ExpectedCurrentVersionId,
                row.ServerVersionId);
        }

        private static PosProductImageUploadMetadata Metadata(
            ProductImageOperationRow row,
            ProductImageVariant variant)
        {
            return variant == ProductImageVariant.Main
                ? new PosProductImageUploadMetadata(
                    row.MainBytes.Value,
                    row.MainHeight.Value,
                    ProductImageContractV1.WireMimeType,
                    row.MainSha256,
                    row.MainWidth.Value)
                : new PosProductImageUploadMetadata(
                    row.ThumbBytes.Value,
                    row.ThumbHeight.Value,
                    ProductImageContractV1.WireMimeType,
                    row.ThumbSha256,
                    row.ThumbWidth.Value);
        }

        private static PosProductImageEnvelope Envelope(
            PosTrustedDeviceSession session)
        {
            return new PosProductImageEnvelope(
                AppVersion(),
                session.ShopId,
                session.ShopDeviceId,
                session.StaffId,
                session.StaffCredentialVersion,
                session.PosSessionId,
                session.DeviceToken,
                session.SessionToken);
        }

        private static async Task<long> InsertFairnessProductAsync(
            SqliteConnectionFactory factory)
        {
            using (var connection = factory.Open())
            {
                await connection.ExecuteAsync(@"
INSERT INTO products(barcode, name, unitPrice, is_active)
VALUES('PIB-LOCAL-FAIRNESS', 'Local fairness sentinel', 1, 1);")
                    .ConfigureAwait(true);
                return await connection.ExecuteScalarAsync<long>(
                    "SELECT id FROM products WHERE barcode = 'PIB-LOCAL-FAIRNESS';")
                    .ConfigureAwait(true);
            }
        }

        private static ProductImageStagingStore IsolatedStagingStore()
        {
            return new ProductImageStagingStore(new ProductImageStagingOptions(
                Path.Combine(AppPaths.DataDirectory, "image-staging")));
        }

        private static ProductImageDiskCache IsolatedCache(AcceptanceState state)
        {
            Require(string.Equals(
                    state.CacheRoot,
                    CacheRoot(),
                    StringComparison.OrdinalIgnoreCase),
                "product_image_cache_root_invalid");
            return new ProductImageDiskCache(new ProductImageCacheOptions(
                state.CacheRoot,
                maximumBytes: 8 * 1024 * 1024,
                maximumEntries: 32,
                maximumConcurrentProducers: 2));
        }

        private static string CacheRoot()
        {
            return Path.Combine(AppPaths.DataDirectory, "image-cache");
        }

        private static void WriteSyntheticImage(
            string path,
            bool png,
            Color color)
        {
            const int width = 960;
            const int height = 720;
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = (byte)Math.Min(255, color.B + x % 31);
                pixels[offset + 1] = (byte)Math.Min(255, color.G + y % 29);
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = 255;
            }
            var bitmap = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                width * 4);
            BitmapEncoder encoder = png
                ? (BitmapEncoder)new PngBitmapEncoder()
                : new JpegBitmapEncoder { QualityLevel = 88 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                encoder.Save(stream);
            }
        }

        private static async Task CaptureLoadedUiScreenshotsAsync(
            ProductDetailsRow product,
            string outputDirectory,
            string label,
            string runProfileName)
        {
            var model = new ProductsViewModel();
            model.Items.Add(product);
            var window = new Window
            {
                Width = 1024,
                Height = 768,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Title = "Win7POS product image staging acceptance",
                Content = new ProductsView { DataContext = model }
            };
            try
            {
                using (ProductImageRuntime.UseTrustedProfileForAcceptance(
                    runProfileName,
                    CacheRoot()))
                {
                    window.Show();
                    ProductImageDisplayViewModel display = null;
                    ProductImageListPresenter loader = null;
                    for (var attempt = 0; attempt < 600; attempt++)
                    {
                        window.UpdateLayout();
                        loader = Descendants<ProductImageListPresenter>(window)
                            .FirstOrDefault();
                        display = Descendants<ProductImagePresenter>(window)
                            .Select(item => item.DataContext)
                            .OfType<ProductImageDisplayViewModel>()
                            .FirstOrDefault();
                        if (loader?.Product != null && display?.IsLoaded == true)
                            break;
                        await Task.Delay(100).ConfigureAwait(true);
                    }
                    Require(loader?.Product != null && display?.IsLoaded == true,
                        "staging_list_runtime_image_not_loaded");
                    CaptureWindow(
                        window,
                        Path.Combine(
                            outputDirectory,
                            "product-image-" + label +
                            "-list-thumb-1024x768.png"));
                    window.Close();
                    await Task.Delay(250).ConfigureAwait(true);
                }
            }
            finally
            {
                if (window.IsVisible) window.Close();
            }

            var editorModel = new ProductEditViewModel(
                ProductEditMode.Edit,
                product,
                ProductsWorkflowService.CreateDefault());
            var dialog = new ProductEditDialog(editorModel);
            try
            {
                using (ProductImageRuntime.UseTrustedProfileForAcceptance(
                    runProfileName,
                    CacheRoot()))
                {
                    dialog.Show();
                    for (var attempt = 0;
                         attempt < 600 && !editorModel.ImageDisplay.IsLoaded;
                         attempt++)
                    {
                        dialog.UpdateLayout();
                        await Task.Delay(100).ConfigureAwait(true);
                    }
                    Require(editorModel.ImageDisplay.IsLoaded &&
                            dialog.ActualWidth <= 1024 &&
                            dialog.ActualHeight <= 768,
                        "staging_editor_runtime_image_not_loaded");
                    CaptureWindow(
                        dialog,
                        Path.Combine(
                            outputDirectory,
                            "product-image-" + label +
                            "-editor-main-1024x768.png"));
                    dialog.Close();
                    await Task.Delay(250).ConfigureAwait(true);
                }
            }
            finally
            {
                if (dialog.IsVisible) dialog.Close();
            }
        }

        private static async Task CaptureOfflineRestartScreenshotsAsync(
            ProductDetailsRow product,
            string outputDirectory,
            string runProfileName)
        {
            var variable = PosAdminWebOptions.BaseUrlEnvironmentVariable;
            var previous = Environment.GetEnvironmentVariable(variable);
            Environment.SetEnvironmentVariable(
                variable,
                "https://127.0.0.1:1/");
            try
            {
                await CaptureLoadedUiScreenshotsAsync(
                    product,
                    outputDirectory,
                    "offline-restart",
                    runProfileName).ConfigureAwait(true);
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, previous);
            }
        }

        private static async Task CaptureNoImageScreenshotsAsync(
            ProductDetailsRow product,
            string outputDirectory,
            string runProfileName)
        {
            var listModel = new ProductsViewModel();
            listModel.Items.Add(product);
            var list = new Window
            {
                Width = 1024,
                Height = 768,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Title = "Win7POS product image removed",
                Content = new ProductsView { DataContext = listModel }
            };
            try
            {
                using (ProductImageRuntime.UseTrustedProfileForAcceptance(
                    runProfileName,
                    CacheRoot()))
                {
                    list.Show();
                    ProductImageDisplayViewModel listDisplay = null;
                    ProductImageListPresenter loader = null;
                    for (var attempt = 0; attempt < 600; attempt++)
                    {
                        list.UpdateLayout();
                        loader = Descendants<ProductImageListPresenter>(list)
                            .FirstOrDefault();
                        listDisplay = Descendants<ProductImagePresenter>(list)
                            .Select(item => item.DataContext)
                            .OfType<ProductImageDisplayViewModel>()
                            .FirstOrDefault();
                        if (loader?.Product != null &&
                            listDisplay?.State == ProductImageDisplayState.NoImage)
                        {
                            break;
                        }
                        await Task.Delay(100).ConfigureAwait(true);
                    }
                    Require(loader?.Product != null &&
                            listDisplay?.State == ProductImageDisplayState.NoImage,
                        "removed_list_runtime_no_image_missing");
                    CaptureWindow(
                        list,
                        Path.Combine(
                            outputDirectory,
                            "product-image-removed-list-no-image-1024x768.png"));
                    list.Close();
                    await Task.Delay(250).ConfigureAwait(true);
                }
            }
            finally
            {
                if (list.IsVisible) list.Close();
            }

            var editorModel = new ProductEditViewModel(
                ProductEditMode.Edit,
                product,
                ProductsWorkflowService.CreateDefault());
            var editor = new ProductEditDialog(editorModel);
            try
            {
                using (ProductImageRuntime.UseTrustedProfileForAcceptance(
                    runProfileName,
                    CacheRoot()))
                {
                    editor.Show();
                    for (var attempt = 0;
                         attempt < 600 &&
                         editorModel.ImageDisplay.State !=
                            ProductImageDisplayState.NoImage;
                         attempt++)
                    {
                        editor.UpdateLayout();
                        await Task.Delay(100).ConfigureAwait(true);
                    }
                    Require(
                        editorModel.ImageDisplay.State ==
                            ProductImageDisplayState.NoImage,
                        "removed_editor_runtime_no_image_missing");
                    CaptureWindow(
                        editor,
                        Path.Combine(
                            outputDirectory,
                            "product-image-removed-editor-no-image-1024x768.png"));
                    editor.Close();
                    await Task.Delay(250).ConfigureAwait(true);
                }
            }
            finally
            {
                if (editor.IsVisible) editor.Close();
            }
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject root)
            where T : DependencyObject
        {
            if (root == null) yield break;
            if (root is T typed) yield return typed;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < count; index++)
            {
                foreach (var child in Descendants<T>(
                    VisualTreeHelper.GetChild(root, index)))
                {
                    yield return child;
                }
            }
        }

        private static void CaptureWindow(Window window, string path)
        {
            try
            {
                window.Show();
                window.UpdateLayout();
                var render = new RenderTargetBitmap(
                    Math.Max(1, (int)Math.Ceiling(window.ActualWidth)),
                    Math.Max(1, (int)Math.Ceiling(window.ActualHeight)),
                    96,
                    96,
                    PixelFormats.Pbgra32);
                render.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(render));
                using (var output = new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    encoder.Save(output);
                }
            }
            finally
            {
                window.Close();
            }
        }

        private static QaProfile LoadQaProfile(string profileName)
        {
            var safe = SafeProfileName(profileName);
            var root = Path.GetFullPath(QaSecretsDirectory);
            var path = Path.GetFullPath(Path.Combine(root, safe + ".dpapi"));
            Require(path.StartsWith(
                    root.TrimEnd(Path.DirectorySeparatorChar) +
                    Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(path) &&
                    HasRestrictedAcl(path),
                "shared_profile_unavailable");
            var encrypted = File.ReadAllBytes(path);
            byte[] plaintext = null;
            try
            {
                plaintext = ProtectedData.Unprotect(
                    encrypted,
                    null,
                    DataProtectionScope.CurrentUser);
                var profile = Deserialize<QaProfile>(plaintext);
                Require(profile != null && profile.IsValid(),
                    "shared_profile_invalid");
                return profile;
            }
            finally
            {
                Clear(encrypted);
                Clear(plaintext);
            }
        }

        private static void SaveState(AcceptanceState state)
        {
            var plaintext = Serialize(state);
            byte[] encrypted = null;
            try
            {
                encrypted = ProtectedData.Protect(
                    plaintext,
                    null,
                    DataProtectionScope.CurrentUser);
                var path = StatePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(temporary, encrypted);
                RestrictAcl(temporary);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                RestrictAcl(path);
            }
            finally
            {
                Clear(plaintext);
                Clear(encrypted);
            }
        }

        private static AcceptanceState LoadState()
        {
            var path = StatePath();
            Require(File.Exists(path) && HasRestrictedAcl(path),
                "acceptance_state_missing");
            var encrypted = File.ReadAllBytes(path);
            byte[] plaintext = null;
            try
            {
                plaintext = ProtectedData.Unprotect(
                    encrypted,
                    null,
                    DataProtectionScope.CurrentUser);
                var state = Deserialize<AcceptanceState>(plaintext);
                Require(state != null && state.IsValid(),
                    "acceptance_state_invalid");
                return state;
            }
            finally
            {
                Clear(encrypted);
                Clear(plaintext);
            }
        }

        private static void DeleteState()
        {
            var path = StatePath();
            if (File.Exists(path)) File.Delete(path);
        }

        private static string StatePath()
        {
            return Path.Combine(AppPaths.DataDirectory, StateFileName);
        }

        internal static void ValidateRunnerHandshake(
            string runnerToken,
            int parentProcessId)
        {
            EnsureIsolatedDataRoot();
            Require(IsLowerHex64(runnerToken),
                "product_image_acceptance_runner_token_invalid");
            var path = Path.Combine(
                AppPaths.DataDirectory,
                AcceptanceRunnerHandshakeFileName);
            var info = new FileInfo(path);
            Require(info.Exists && info.Length > 0 && info.Length <= 4096,
                "product_image_acceptance_runner_handshake_missing");
            var encrypted = File.ReadAllBytes(path);
            byte[] plaintext = null;
            byte[] suppliedToken = null;
            try
            {
                plaintext = ProtectedData.Unprotect(
                    encrypted,
                    null,
                    DataProtectionScope.CurrentUser);
                suppliedToken = ParseLowerHex64(runnerToken);
                Require(plaintext != null && plaintext.Length == 40 &&
                        plaintext[0] == (byte)'P' &&
                        plaintext[1] == (byte)'I' &&
                        plaintext[2] == (byte)'B' &&
                        plaintext[3] == (byte)'1' &&
                        BitConverter.ToInt32(plaintext, 4) == parentProcessId &&
                        FixedTimeEquals(plaintext, 8, suppliedToken),
                    "product_image_acceptance_runner_handshake_invalid");
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException(
                    "product_image_acceptance_runner_handshake_invalid");
            }
            finally
            {
                Clear(encrypted);
                Clear(plaintext);
                Clear(suppliedToken);
            }
        }

        private static byte[] ParseLowerHex64(string value)
        {
            Require(IsLowerHex64(value),
                "product_image_acceptance_runner_token_invalid");
            var bytes = new byte[32];
            for (var index = 0; index < bytes.Length; index++)
            {
                bytes[index] = byte.Parse(
                    value.Substring(index * 2, 2),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture);
            }
            return bytes;
        }

        private static bool FixedTimeEquals(
            byte[] payload,
            int payloadOffset,
            byte[] expected)
        {
            if (payload == null || expected == null ||
                payloadOffset < 0 ||
                payload.Length - payloadOffset < expected.Length)
            {
                return false;
            }
            var difference = 0;
            for (var index = 0; index < expected.Length; index++)
            {
                difference |= payload[payloadOffset + index] ^ expected[index];
            }
            return difference == 0;
        }

        private static void WriteSafeReport(
            string outputDirectory,
            AcceptanceState state,
            Action<SafeReport> apply)
        {
            var path = Path.Combine(outputDirectory, SafeReportName);
            SafeReport report = null;
            if (File.Exists(path))
            {
                try
                {
                    var candidate = Deserialize<SafeReport>(
                        File.ReadAllBytes(path));
                    if (candidate != null &&
                        candidate.SchemaVersion ==
                            "win7pos-product-image-staging-v1" &&
                        candidate.RunHmac == state.RunHmac &&
                        candidate.StartedAt == state.StartedAt)
                    {
                        report = candidate;
                    }
                }
                catch (SerializationException)
                {
                }
            }
            if (report == null)
            {
                report = new SafeReport
                {
                    SchemaVersion = "win7pos-product-image-staging-v1",
                    RunHmac = state.RunHmac,
                    StartedAt = state.StartedAt
                };
            }
            report.ExactMainSha = ReadExactMainSha();
            report.FenceUntil = state.FenceUntil;
            report.Phase = state.Phase;
            report.MaximumPrivateBytes = Math.Max(
                report.MaximumPrivateBytes,
                Process.GetCurrentProcess().PrivateMemorySize64);
            apply(report);
            report.CompletedAt = CanonicalTimestamp(DateTimeOffset.UtcNow);
            var bytes = Serialize(report);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                Clear(bytes);
            }
        }

        private static bool IsFullMatrixComplete(SafeReport report)
        {
            return report != null &&
                report.AuthBootstrap &&
                report.CachePromoted &&
                report.CachePurged &&
                report.CatalogDelta &&
                report.CatalogFullDrain &&
                report.CatalogNullAfterRemove &&
                report.CleanupComplete &&
                !report.CleanupPending &&
                report.DbResiduals == 0 &&
                report.DurableOutbox &&
                report.EditorMain &&
                report.ExactVersionReference &&
                report.ExpiredReadRenewed &&
                report.ExpiredUploadRejected &&
                report.Finalize &&
                report.FinalizeReplay &&
                report.ImmutableAuditPreserved &&
                report.Intent &&
                report.IntentReplay &&
                report.ListThumb &&
                report.LocalPreview &&
                report.NewCachePromoted &&
                report.NoHardwareActions &&
                report.NoImageInitial &&
                report.NoSalesEffects &&
                report.OfflineCacheRestart &&
                report.OfflineChoose &&
                report.OldCacheInvalidated &&
                report.OldImageRetainedUntilFinalize &&
                report.PayloadHashMismatchRejected &&
                report.PreprocessPng &&
                report.ReadBack &&
                report.Remove &&
                report.RemoveReplay &&
                report.ReplaceJpeg &&
                report.ResponseLossRecovery &&
                report.RestartOutboxSurvived &&
                report.RunProfileIsolated &&
                report.RunProfileRemoved &&
                report.SaleSafe &&
                report.SharedSnapshotUnchanged &&
                report.SignedUrlPersistenceCount == 0 &&
                report.StaleConflictProtected &&
                report.StorageResiduals == 0 &&
                report.ActiveActorSessionResiduals == 0 &&
                report.UnrelatedFairness &&
                report.UploadMainThumb &&
                report.VersionChanged;
        }

        private static bool ScanTextArtifacts(
            string outputDirectory,
            AcceptanceState state)
        {
            var forbidden = new[]
            {
                state.CleanupCapability,
                state.ResultCapability,
                state.RunMarker,
                state.BeginRequestId,
                state.CleanupRequestId,
                state.ResultIssueRequestId,
                state.ResultRequestId,
                "task150_provision_",
                "token=",
                "/storage/v1/object/sign/",
                "/storage/v1/object/upload/sign/",
                "deviceToken",
                "sessionToken",
                "runMarker"
            };
            foreach (var root in new[] { outputDirectory, AppPaths.LogsDirectory })
            {
                if (!Directory.Exists(root)) continue;
                foreach (var path in Directory.EnumerateFiles(
                    root,
                    "*",
                    SearchOption.AllDirectories))
                {
                    var extension = Path.GetExtension(path);
                    if (extension != ".txt" && extension != ".json" &&
                        extension != ".log" && extension != ".md")
                    {
                        continue;
                    }
                    if (new FileInfo(path).Length > 4 * 1024 * 1024) return false;
                    var text = File.ReadAllText(path);
                    if (forbidden.Where(value => !string.IsNullOrEmpty(value))
                        .Any(value => text.IndexOf(
                            value,
                            StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static int CountForbiddenPersistenceMarkers()
        {
            var patterns = new[]
            {
                "/storage/v1/object/sign/",
                "/storage/v1/object/upload/sign/",
                "\"signedUrl\"",
                "\"mainUploadUrl\"",
                "\"thumbUploadUrl\"",
                "token="
            }.Select(Encoding.UTF8.GetBytes).ToArray();
            var count = 0;
            foreach (var path in Directory.EnumerateFiles(
                AppPaths.DataDirectory,
                "*",
                SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(path);
                var extension = Path.GetExtension(path);
                if (!string.Equals(extension, ".db", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (ContainsAnyBytes(path, patterns)) count++;
            }
            return count;
        }

        private static bool ContainsAnyBytes(
            string path,
            IReadOnlyList<byte[]> patterns)
        {
            var maximum = patterns.Max(item => item.Length);
            var buffer = new byte[64 * 1024 + maximum];
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                var carry = 0;
                while (true)
                {
                    var read = stream.Read(
                        buffer,
                        carry,
                        buffer.Length - carry);
                    var total = carry + read;
                    foreach (var pattern in patterns)
                    {
                        for (var offset = 0;
                             offset <= total - pattern.Length;
                             offset++)
                        {
                            var matched = true;
                            for (var index = 0; index < pattern.Length; index++)
                            {
                                if (buffer[offset + index] == pattern[index])
                                    continue;
                                matched = false;
                                break;
                            }
                            if (matched) return true;
                        }
                    }
                    if (read == 0) return false;
                    carry = Math.Min(maximum - 1, total);
                    Buffer.BlockCopy(
                        buffer,
                        total - carry,
                        buffer,
                        0,
                        carry);
                }
            }
        }

        private static string ReadExactMainSha()
        {
            var raw = Environment.GetEnvironmentVariable(
                "WIN7POS_ACCEPTANCE_EXACT_MAIN_SHA");
            return IsLowerHex(raw, 40) ? raw : string.Empty;
        }

        private static string ReadBaseUrl()
        {
            Require(PosAdminWebOptions.TryLoad(out var options, out _),
                "admin_base_url_missing");
            return options.BaseUri.AbsoluteUri;
        }

        private static string AppVersion()
        {
            return StagingAcceptanceWpfHarness.GetProductionAppVersion();
        }

        private static void EnsureIsolatedDataRoot()
        {
            var root = Path.GetFullPath(AppPaths.DataDirectory).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            Require(string.Equals(
                    root,
                    @"C:\POSData\Win7POS-QA\ProductImagePhaseBAcceptance",
                    StringComparison.OrdinalIgnoreCase),
                "product_image_data_root_not_isolated");
            var existing = root;
            while (!Directory.Exists(existing))
            {
                Require(!File.Exists(existing),
                    "product_image_data_root_non_directory_ancestor");
                existing = Path.GetDirectoryName(existing);
                Require(!string.IsNullOrWhiteSpace(existing),
                    "product_image_data_root_missing_ancestor");
            }
            for (var current = new DirectoryInfo(existing);
                 current != null;
                 current = current.Parent)
            {
                Require(
                    (current.Attributes & FileAttributes.ReparsePoint) == 0,
                    "product_image_data_root_reparse_point");
            }
            if (Directory.Exists(root))
            {
                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(
                        pending.Pop(),
                        "*",
                        SearchOption.TopDirectoryOnly))
                    {
                        var attributes = File.GetAttributes(entry);
                        Require(
                            (attributes & FileAttributes.ReparsePoint) == 0,
                            "product_image_data_root_reparse_point");
                        if ((attributes & FileAttributes.Directory) != 0)
                            pending.Push(entry);
                    }
                }
            }
        }

        private static bool IsBoundedValue(string value, int maximumCharacters)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                value.Length <= maximumCharacters;
        }

        private static string RequestId(string action)
        {
            return "asus-pib-" + SafeCode(action) + "-" + RandomHex(12);
        }

        private static bool IsAcceptanceRequestId(
            string value,
            string action)
        {
            var prefix = "asus-pib-" + action + "-";
            return value != null &&
                value.StartsWith(prefix, StringComparison.Ordinal) &&
                value.Length == prefix.Length + 24 &&
                value.Substring(prefix.Length).All(IsLowerHexCharacter);
        }

        private static string RandomHex(int bytes)
        {
            var value = new byte[bytes];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(value);
            return string.Concat(value.Select(item => item.ToString("x2")));
        }

        private static string CanonicalTimestamp(DateTimeOffset value)
        {
            return value.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
                CultureInfo.InvariantCulture);
        }

        private static bool IsCanonicalTimestamp(string value)
        {
            return DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _);
        }

        private static bool IsAllowedBaseUri(Uri uri)
        {
            return uri != null &&
                string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase) &&
                uri.IsDefaultPort &&
                string.Equals(
                    uri.Host,
                    AllowedHost,
                    StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath == "/" &&
                string.IsNullOrEmpty(uri.UserInfo) &&
                string.IsNullOrEmpty(uri.Query) &&
                string.IsNullOrEmpty(uri.Fragment);
        }

        private static DateTimeOffset ParseRequiredTimestamp(string value)
        {
            Require(DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed),
                "timestamp_invalid");
            return parsed.ToUniversalTime();
        }

        private static DateTimeOffset? ParseTimestamp(string value)
        {
            return DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed.ToUniversalTime()
                : (DateTimeOffset?)null;
        }

        private static string SafeProfileName(string value)
        {
            var candidate = (value ?? string.Empty).Trim();
            Require(candidate.Length >= 3 && candidate.Length <= 64 &&
                    candidate.All(character =>
                        char.IsLetterOrDigit(character) ||
                        character == '-' || character == '_'),
                "shared_profile_name_invalid");
            return candidate;
        }

        private static string SafeCode(string value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            return normalized.Length > 0 && normalized.Length <= 80 &&
                   normalized.All(character =>
                       (character >= 'a' && character <= 'z') ||
                       (character >= '0' && character <= '9') ||
                       character == '_' || character == '-')
                ? normalized
                : "invalid";
        }

        private static bool IsLowerHex64(string value)
        {
            return IsLowerHex(value, 64);
        }

        private static bool IsLowerHex(string value, int length)
        {
            return value != null && value.Length == length &&
                   value.All(IsLowerHexCharacter);
        }

        private static bool IsLowerHexCharacter(char character)
        {
            return (character >= '0' && character <= '9') ||
                   (character >= 'a' && character <= 'f');
        }

        private static bool IsUpperHexCharacter(char character)
        {
            return (character >= '0' && character <= '9') ||
                   (character >= 'A' && character <= 'F');
        }

        private static bool IsCapability(string value, string kind)
        {
            var prefix = "task150_" + kind + "_";
            return value != null && value.StartsWith(prefix, StringComparison.Ordinal) &&
                   value.Length == prefix.Length + 43 &&
                   value.Substring(prefix.Length).All(character =>
                       char.IsLetterOrDigit(character) ||
                       character == '-' || character == '_');
        }

        private static bool HasRestrictedAcl(string path)
        {
            try
            {
                var acl = File.GetAccessControl(path);
                if (!acl.AreAccessRulesProtected) return false;
                var current = WindowsIdentity.GetCurrent().User;
                var system = new SecurityIdentifier(
                    WellKnownSidType.LocalSystemSid,
                    null);
                var currentAllowed = false;
                var systemAllowed = false;
                foreach (FileSystemAccessRule rule in acl.GetAccessRules(
                    true,
                    true,
                    typeof(SecurityIdentifier)))
                {
                    var sid = rule.IdentityReference as SecurityIdentifier;
                    if (sid == null ||
                        rule.AccessControlType != AccessControlType.Allow ||
                        (rule.FileSystemRights & FileSystemRights.FullControl) !=
                            FileSystemRights.FullControl)
                    {
                        return false;
                    }
                    if (sid.Equals(current)) currentAllowed = true;
                    else if (sid.Equals(system)) systemAllowed = true;
                    else return false;
                }
                return currentAllowed && systemAllowed;
            }
            catch
            {
                return false;
            }
        }

        private static void RestrictAcl(string path)
        {
            var current = WindowsIdentity.GetCurrent().User ??
                throw new InvalidOperationException("current_user_sid_missing");
            var system = new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                null);
            var acl = new FileSecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(
                current,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(
                system,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            File.SetAccessControl(path, acl);
        }

        private static byte[] Serialize<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return stream.ToArray();
            }
        }

        private static T Deserialize<T>(byte[] value) where T : class
        {
            using (var stream = new MemoryStream(value, writable: false))
                return new DataContractJsonSerializer(typeof(T)).ReadObject(stream) as T;
        }

        private static void Clear(byte[] value)
        {
            if (value != null) Array.Clear(value, 0, value.Length);
        }

        private static void Require(bool condition, string code)
        {
            if (!condition) throw new InvalidOperationException(SafeCode(code));
        }

        private sealed class BoundaryClient : IDisposable
        {
            private readonly HttpClient _client;

            internal BoundaryClient(Uri baseUri)
            {
                Require(IsAllowedBaseUri(baseUri), "boundary_base_url_invalid");
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                _client = new HttpClient(new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.None,
                    UseCookies = false
                })
                {
                    BaseAddress = baseUri,
                    Timeout = Timeout.InfiniteTimeSpan
                };
            }

            internal Task<BoundaryResponse> PostTrustedAsync(
                TrustedRequest request,
                CancellationToken cancellationToken)
            {
                return PostAsync(request, cancellationToken);
            }

            internal Task<BoundaryResponse> PostCapabilityAsync(
                CapabilityRequest request,
                CancellationToken cancellationToken)
            {
                return PostAsync(request, cancellationToken);
            }

            private async Task<BoundaryResponse> PostAsync<T>(
                T request,
                CancellationToken cancellationToken)
            {
                var body = Serialize(request);
                try
                {
                    Require(body.Length > 1 && body.Length <= 16 * 1024,
                        "boundary_request_size_invalid");
                    using (var timeout = CancellationTokenSource
                        .CreateLinkedTokenSource(cancellationToken))
                    using (var content = new ByteArrayContent(body))
                    using (var message = new HttpRequestMessage(
                        HttpMethod.Post,
                        BoundaryPath))
                    {
                        timeout.CancelAfter(RequestTimeout);
                        content.Headers.ContentType =
                            new System.Net.Http.Headers.MediaTypeHeaderValue(
                                "application/json");
                        message.Content = content;
                        message.Headers.TryAddWithoutValidation(
                            "Cache-Control",
                            "no-store");
                        using (var response = await _client.SendAsync(
                            message,
                            HttpCompletionOption.ResponseHeadersRead,
                            timeout.Token).ConfigureAwait(false))
                        {
                            Require(response.Headers.CacheControl?.NoStore == true &&
                                    string.Equals(
                                        response.Content.Headers.ContentType?.MediaType,
                                        "application/json",
                                        StringComparison.OrdinalIgnoreCase),
                                "boundary_response_headers_invalid");
                            var bytes = await ReadBoundedAsync(
                                response.Content,
                                64 * 1024,
                                timeout.Token).ConfigureAwait(false);
                            try
                            {
                                var result = Deserialize<BoundaryResponse>(bytes);
                                Require(result != null, "boundary_response_invalid");
                                return result;
                            }
                            finally
                            {
                                Clear(bytes);
                            }
                        }
                    }
                }
                finally
                {
                    Clear(body);
                }
            }

            private static async Task<byte[]> ReadBoundedAsync(
                HttpContent content,
                int maximumBytes,
                CancellationToken cancellationToken)
            {
                if (content.Headers.ContentLength.HasValue)
                {
                    Require(content.Headers.ContentLength.Value > 1 &&
                            content.Headers.ContentLength.Value <= maximumBytes,
                        "boundary_response_size_invalid");
                }
                using (var input = await content.ReadAsStreamAsync()
                    .ConfigureAwait(false))
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[8192];
                    try
                    {
                        while (true)
                        {
                            var read = await input.ReadAsync(
                                buffer,
                                0,
                                buffer.Length,
                                cancellationToken).ConfigureAwait(false);
                            if (read == 0) break;
                            if (output.Length + read > maximumBytes)
                                throw new InvalidDataException(
                                    "boundary_response_size_invalid");
                            output.Write(buffer, 0, read);
                        }
                        Require(output.Length > 1,
                            "boundary_response_size_invalid");
                        return output.ToArray();
                    }
                    finally
                    {
                        Clear(buffer);
                        Clear(output.GetBuffer());
                    }
                }
            }

            public void Dispose()
            {
                _client.Dispose();
            }
        }

        [DataContract]
        private sealed class TrustedRequest
        {
            [DataMember(Name = "action", EmitDefaultValue = false)]
            public string Action { get; set; }
            [DataMember(Name = "appVersion", EmitDefaultValue = false)]
            public string AppVersionValue { get; set; }
            [DataMember(Name = "authoritativeFenceUntil", EmitDefaultValue = false)]
            public string AuthoritativeFenceUntil { get; set; }
            [DataMember(Name = "cleanupCapability", EmitDefaultValue = false)]
            public string CleanupCapability { get; set; }
            [DataMember(Name = "deviceToken", EmitDefaultValue = false)]
            public string DeviceToken { get; set; }
            [DataMember(Name = "manifestHmac", EmitDefaultValue = false)]
            public string ManifestHmac { get; set; }
            [DataMember(Name = "posSessionId", EmitDefaultValue = false)]
            public string PosSessionId { get; set; }
            [DataMember(Name = "requestId", EmitDefaultValue = false)]
            public string RequestIdValue { get; set; }
            [DataMember(Name = "runHmac", EmitDefaultValue = false)]
            public string RunHmac { get; set; }
            [DataMember(Name = "runMarker", EmitDefaultValue = false)]
            public string RunMarker { get; set; }
            [DataMember(Name = "sessionToken", EmitDefaultValue = false)]
            public string SessionToken { get; set; }
            [DataMember(Name = "shopDeviceId", EmitDefaultValue = false)]
            public string ShopDeviceId { get; set; }
            [DataMember(Name = "shopId", EmitDefaultValue = false)]
            public string ShopId { get; set; }
            [DataMember(Name = "staffCredentialVersion")]
            public int StaffCredentialVersion { get; set; }
            [DataMember(Name = "staffId", EmitDefaultValue = false)]
            public string StaffId { get; set; }
            [DataMember(Name = "template", EmitDefaultValue = false)]
            public string Template { get; set; }

            internal static TrustedRequest Begin(
                PosTrustedDeviceSession session,
                string requestId,
                string runMarker)
            {
                var request = FromSession(session, "begin", requestId);
                request.RunMarker = runMarker;
                request.Template = FixtureTemplate;
                return request;
            }

            internal static TrustedRequest Prearm(
                PosTrustedDeviceSession session,
                string requestId,
                BoundaryResponse armed,
                string fenceUntil)
            {
                var request = FromSession(session, "prearm", requestId);
                request.RunHmac = armed.RunHmac;
                request.ManifestHmac = armed.ManifestHmac;
                request.CleanupCapability = armed.CleanupCapability;
                request.AuthoritativeFenceUntil = fenceUntil;
                return request;
            }

            internal static TrustedRequest ResultIssue(
                PosTrustedDeviceSession session,
                string requestId,
                AcceptanceState state)
            {
                var request = FromSession(session, "result_issue", requestId);
                request.RunHmac = state.RunHmac;
                request.ManifestHmac = state.ManifestHmac;
                return request;
            }

            private static TrustedRequest FromSession(
                PosTrustedDeviceSession session,
                string action,
                string requestId)
            {
                return new TrustedRequest
                {
                    Action = action,
                    AppVersionValue = AppVersion(),
                    DeviceToken = session.DeviceToken,
                    PosSessionId = session.PosSessionId,
                    RequestIdValue = requestId,
                    SessionToken = session.SessionToken,
                    ShopDeviceId = session.ShopDeviceId,
                    ShopId = session.ShopId,
                    StaffCredentialVersion = session.StaffCredentialVersion,
                    StaffId = session.StaffId
                };
            }
        }

        [DataContract]
        private sealed class CapabilityRequest
        {
            [DataMember(Name = "action")]
            public string Action { get; set; }
            [DataMember(Name = "cleanupCapability", EmitDefaultValue = false)]
            public string CleanupCapability { get; set; }
            [DataMember(Name = "manifestHmac")]
            public string ManifestHmac { get; set; }
            [DataMember(Name = "provisionCapability", EmitDefaultValue = false)]
            public string ProvisionCapability { get; set; }
            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
            [DataMember(Name = "resultCapability", EmitDefaultValue = false)]
            public string ResultCapability { get; set; }
            [DataMember(Name = "runHmac")]
            public string RunHmac { get; set; }

            internal static CapabilityRequest Provision(
                BoundaryResponse armed,
                string requestId)
            {
                return new CapabilityRequest
                {
                    Action = "provision",
                    ManifestHmac = armed.ManifestHmac,
                    ProvisionCapability = armed.ProvisionCapability,
                    RequestId = requestId,
                    RunHmac = armed.RunHmac
                };
            }

            internal static CapabilityRequest Cleanup(
                AcceptanceState state,
                string requestId)
            {
                return new CapabilityRequest
                {
                    Action = "cleanup",
                    CleanupCapability = state.CleanupCapability,
                    ManifestHmac = state.ManifestHmac,
                    RequestId = requestId,
                    RunHmac = state.RunHmac
                };
            }

            internal static CapabilityRequest Result(
                AcceptanceState state,
                string requestId)
            {
                return new CapabilityRequest
                {
                    Action = "result",
                    ManifestHmac = state.ManifestHmac,
                    RequestId = requestId,
                    ResultCapability = state.ResultCapability,
                    RunHmac = state.RunHmac
                };
            }
        }

        [DataContract]
        private sealed class BoundaryResponse
        {
            [DataMember(Name = "activeExpiresAt")]
            public string ActiveExpiresAt { get; set; }
            [DataMember(Name = "bootstrapEnvelope")]
            public BootstrapEnvelope BootstrapEnvelope { get; set; }
            [DataMember(Name = "cleanupCapability")]
            public string CleanupCapability { get; set; }
            [DataMember(Name = "code")]
            public string Code { get; set; }
            [DataMember(Name = "credentialsReissued")]
            public bool CredentialsReissued { get; set; }
            [DataMember(Name = "fenceUntil")]
            public string FenceUntil { get; set; }
            [DataMember(Name = "manifestHmac")]
            public string ManifestHmac { get; set; }
            [DataMember(Name = "ok")]
            public bool Ok { get; set; }
            [DataMember(Name = "productId")]
            public string ProductId { get; set; }
            [DataMember(Name = "provisionCapability")]
            public string ProvisionCapability { get; set; }
            [DataMember(Name = "receipt")]
            public CleanupReceipt Receipt { get; set; }
            [DataMember(Name = "requiredCoverageUntil")]
            public string RequiredCoverageUntil { get; set; }
            [DataMember(Name = "resultCapability")]
            public string ResultCapability { get; set; }
            [DataMember(Name = "rotationRequired")]
            public bool RotationRequired { get; set; }
            [DataMember(Name = "runHmac")]
            public string RunHmac { get; set; }
            [DataMember(Name = "shopId")]
            public string ShopId { get; set; }
            [DataMember(Name = "staffId")]
            public string StaffId { get; set; }
        }

        [DataContract]
        private sealed class BootstrapEnvelope
        {
            [DataMember(Name = "baseUrl")]
            public string BaseUrl { get; set; }
            [DataMember(Name = "deviceIdentifier")]
            public string DeviceIdentifier { get; set; }
            [DataMember(Name = "shopCode")]
            public string ShopCode { get; set; }
            [DataMember(Name = "staffCode")]
            public string StaffCode { get; set; }
            [DataMember(Name = "staffCredential")]
            public string StaffCredential { get; set; }
        }

        [DataContract]
        private sealed class CleanupReceipt
        {
            [DataMember(Name = "cleanupCapabilityRevoked")]
            public bool CleanupCapabilityRevoked { get; set; }
            [DataMember(Name = "code")]
            public string Code { get; set; }
            [DataMember(Name = "counts")]
            public CleanupCounts Counts { get; set; }
            [DataMember(Name = "immutableAuditPreserved")]
            public bool ImmutableAuditPreserved { get; set; }
            [DataMember(Name = "manifestHmac")]
            public string ManifestHmac { get; set; }
            [DataMember(Name = "receiptHmac")]
            public string ReceiptHmac { get; set; }
            [DataMember(Name = "runHmac")]
            public string RunHmac { get; set; }
            [DataMember(Name = "schemaVersion")]
            public string SchemaVersion { get; set; }
            [DataMember(Name = "sharedSnapshotUnchanged")]
            public bool SharedSnapshotUnchanged { get; set; }
        }

        [DataContract]
        private sealed class CleanupCounts
        {
            [DataMember(Name = "activeRunOwnedSessions")]
            public int ActiveRunOwnedSessions { get; set; }
            [DataMember(Name = "imageVersions")]
            public int ImageVersions { get; set; }
            [DataMember(Name = "products")]
            public int Products { get; set; }
            [DataMember(Name = "storageObjects")]
            public int StorageObjects { get; set; }
        }

        [DataContract]
        private sealed class QaProfile
        {
            [DataMember(Name = "baseUrl")]
            public string BaseUrl { get; set; }
            [DataMember(Name = "credential")]
            public string Credential { get; set; }
            [DataMember(Name = "deviceDisplayName")]
            public string DeviceDisplayName { get; set; }
            [DataMember(Name = "deviceIdentifier")]
            public string DeviceIdentifier { get; set; }
            [DataMember(Name = "expiresAt")]
            public string ExpiresAt { get; set; }
            [DataMember(Name = "profileVersion")]
            public int ProfileVersion { get; set; }
            [DataMember(Name = "shopCode")]
            public string ShopCode { get; set; }
            [DataMember(Name = "staffCode")]
            public string StaffCode { get; set; }

            internal Uri BaseUri => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
                ? uri
                : null;

            internal bool IsValid()
            {
                var uri = BaseUri;
                if (ProfileVersion != 2 || !IsAllowedBaseUri(uri) ||
                    string.IsNullOrWhiteSpace(Credential) ||
                    string.IsNullOrWhiteSpace(ShopCode) ||
                    string.IsNullOrWhiteSpace(StaffCode) ||
                    string.IsNullOrWhiteSpace(DeviceIdentifier) ||
                    string.IsNullOrWhiteSpace(DeviceDisplayName))
                {
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(ExpiresAt) &&
                    (!DateTimeOffset.TryParse(
                        ExpiresAt,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var expiry) ||
                     expiry.ToUniversalTime() <= DateTimeOffset.UtcNow))
                {
                    return false;
                }
                return true;
            }

            internal void Clear()
            {
                BaseUrl = null;
                Credential = null;
                DeviceDisplayName = null;
                DeviceIdentifier = null;
                ExpiresAt = null;
                ShopCode = null;
                StaffCode = null;
            }
        }

        [DataContract]
        private sealed class AcceptanceState
        {
            [DataMember(Name = "accountScope")]
            public string AccountScope { get; set; }
            [DataMember(Name = "baseUrl")]
            public string BaseUrl { get; set; }
            [DataMember(Name = "beginRequestId")]
            public string BeginRequestId { get; set; }
            [DataMember(Name = "cacheRoot")]
            public string CacheRoot { get; set; }
            [DataMember(Name = "cleanupCapability")]
            public string CleanupCapability { get; set; }
            [DataMember(Name = "cleanupRequestId")]
            public string CleanupRequestId { get; set; }
            [DataMember(Name = "fenceUntil")]
            public string FenceUntil { get; set; }
            [DataMember(Name = "firstImageUpdatedAt")]
            public string FirstImageUpdatedAt { get; set; }
            [DataMember(Name = "firstMain")]
            public VariantEvidence FirstMain { get; set; }
            [DataMember(Name = "firstOperationId")]
            public string FirstOperationId { get; set; }
            [DataMember(Name = "firstThumb")]
            public VariantEvidence FirstThumb { get; set; }
            [DataMember(Name = "firstVersionId")]
            public string FirstVersionId { get; set; }
            [DataMember(Name = "manifestHmac")]
            public string ManifestHmac { get; set; }
            [DataMember(Name = "phase")]
            public string Phase { get; set; }
            [DataMember(Name = "productId")]
            public string ProductId { get; set; }
            [DataMember(Name = "resultCapability")]
            public string ResultCapability { get; set; }
            [DataMember(Name = "resultIssueRequestId")]
            public string ResultIssueRequestId { get; set; }
            [DataMember(Name = "resultRequestId")]
            public string ResultRequestId { get; set; }
            [DataMember(Name = "runHmac")]
            public string RunHmac { get; set; }
            [DataMember(Name = "runMarker")]
            public string RunMarker { get; set; }
            [DataMember(Name = "runProfileName")]
            public string RunProfileName { get; set; }
            [DataMember(Name = "secondImageUpdatedAt")]
            public string SecondImageUpdatedAt { get; set; }
            [DataMember(Name = "secondMain")]
            public VariantEvidence SecondMain { get; set; }
            [DataMember(Name = "secondThumb")]
            public VariantEvidence SecondThumb { get; set; }
            [DataMember(Name = "secondVersionId")]
            public string SecondVersionId { get; set; }
            [DataMember(Name = "shopId")]
            public string ShopId { get; set; }
            [DataMember(Name = "startedAt")]
            public string StartedAt { get; set; }

            internal bool IsValid()
            {
                var beginPending = Phase == "begin_pending";
                var validPhase = beginPending || Phase == "armed" ||
                    Phase == "provisioned" ||
                    Phase == "offline_queued" ||
                    Phase == "first_cache_ready" ||
                    Phase == "cleanup_fence_armed";
                var fixtureIdentityValid = beginPending || Phase == "armed"
                    ? string.IsNullOrEmpty(ProductId) &&
                      string.IsNullOrEmpty(ShopId)
                    : Guid.TryParse(ProductId, out _) &&
                      Guid.TryParse(ShopId, out _);
                var recoveredIdentityValid = beginPending
                    ? string.IsNullOrEmpty(RunHmac) &&
                      string.IsNullOrEmpty(ManifestHmac) &&
                      string.IsNullOrEmpty(CleanupCapability) &&
                      string.IsNullOrEmpty(ResultCapability) &&
                      string.IsNullOrEmpty(RunProfileName)
                    : IsLowerHex64(RunHmac) &&
                      IsLowerHex64(ManifestHmac) &&
                      IsCapability(CleanupCapability, "cleanup") &&
                      IsCapability(ResultCapability, "result") &&
                      PosTrustedDeviceStore.IsValidProfileName(RunProfileName);
                return Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) &&
                    IsAllowedBaseUri(uri) &&
                    IsAcceptanceRequestId(BeginRequestId, "begin") &&
                    IsAcceptanceRequestId(CleanupRequestId, "cleanup") &&
                    IsAcceptanceRequestId(
                        ResultIssueRequestId,
                        "result-issue") &&
                    IsAcceptanceRequestId(ResultRequestId, "result") &&
                    RunMarker != null &&
                    RunMarker.StartsWith("ASUSPIB_", StringComparison.Ordinal) &&
                    RunMarker.Length == 40 &&
                    RunMarker.Substring(8).All(IsUpperHexCharacter) &&
                    recoveredIdentityValid &&
                    fixtureIdentityValid &&
                    validPhase &&
                    IsCanonicalTimestamp(FenceUntil) &&
                    IsCanonicalTimestamp(StartedAt) &&
                    string.Equals(
                        Path.GetFullPath(CacheRoot ?? string.Empty).TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar),
                        ProductImageStagingAcceptance.CacheRoot(),
                        StringComparison.OrdinalIgnoreCase);
            }
        }

        [DataContract]
        private sealed class VariantEvidence
        {
            [DataMember(Name = "bytes")]
            public int Bytes { get; set; }
            [DataMember(Name = "height")]
            public int Height { get; set; }
            [DataMember(Name = "sha256")]
            public string Sha256 { get; set; }
            [DataMember(Name = "width")]
            public int Width { get; set; }
        }

        private sealed class CacheEvidence
        {
            public string AccountScope { get; set; }
            public VariantEvidence Main { get; set; }
            public VariantEvidence Thumb { get; set; }
        }

        [DataContract]
        private sealed class SafeReport
        {
            [DataMember(Name = "activeActorSessionResiduals")]
            public int ActiveActorSessionResiduals { get; set; }
            [DataMember(Name = "authBootstrap")]
            public bool AuthBootstrap { get; set; }
            [DataMember(Name = "cachePromoted")]
            public bool CachePromoted { get; set; }
            [DataMember(Name = "cachePurged")]
            public bool CachePurged { get; set; }
            [DataMember(Name = "catalogDelta")]
            public bool CatalogDelta { get; set; }
            [DataMember(Name = "catalogFullDrain")]
            public bool CatalogFullDrain { get; set; }
            [DataMember(Name = "catalogNullAfterRemove")]
            public bool CatalogNullAfterRemove { get; set; }
            [DataMember(Name = "cleanupComplete")]
            public bool CleanupComplete { get; set; }
            [DataMember(Name = "cleanupPending")]
            public bool CleanupPending { get; set; }
            [DataMember(Name = "completedAt")]
            public string CompletedAt { get; set; }
            [DataMember(Name = "dbResiduals")]
            public int DbResiduals { get; set; }
            [DataMember(Name = "durableOutbox")]
            public bool DurableOutbox { get; set; }
            [DataMember(Name = "editorMain")]
            public bool EditorMain { get; set; }
            [DataMember(Name = "exactMainSha")]
            public string ExactMainSha { get; set; }
            [DataMember(Name = "exactVersionReference")]
            public bool ExactVersionReference { get; set; }
            [DataMember(Name = "expiredReadRenewed")]
            public bool ExpiredReadRenewed { get; set; }
            [DataMember(Name = "expiredUploadRejected")]
            public bool ExpiredUploadRejected { get; set; }
            [DataMember(Name = "fenceUntil")]
            public string FenceUntil { get; set; }
            [DataMember(Name = "finalize")]
            public bool Finalize { get; set; }
            [DataMember(Name = "finalizeReplay")]
            public bool FinalizeReplay { get; set; }
            [DataMember(Name = "immutableAuditPreserved")]
            public bool ImmutableAuditPreserved { get; set; }
            [DataMember(Name = "intent")]
            public bool Intent { get; set; }
            [DataMember(Name = "intentReplay")]
            public bool IntentReplay { get; set; }
            [DataMember(Name = "listThumb")]
            public bool ListThumb { get; set; }
            [DataMember(Name = "localPreview")]
            public bool LocalPreview { get; set; }
            [DataMember(Name = "maximumPrivateBytes")]
            public long MaximumPrivateBytes { get; set; }
            [DataMember(Name = "newCachePromoted")]
            public bool NewCachePromoted { get; set; }
            [DataMember(Name = "noHardwareActions")]
            public bool NoHardwareActions { get; set; }
            [DataMember(Name = "noImageInitial")]
            public bool NoImageInitial { get; set; }
            [DataMember(Name = "noSalesEffects")]
            public bool NoSalesEffects { get; set; }
            [DataMember(Name = "offlineCacheRestart")]
            public bool OfflineCacheRestart { get; set; }
            [DataMember(Name = "offlineChoose")]
            public bool OfflineChoose { get; set; }
            [DataMember(Name = "oldCacheInvalidated")]
            public bool OldCacheInvalidated { get; set; }
            [DataMember(Name = "oldImageRetainedUntilFinalize")]
            public bool OldImageRetainedUntilFinalize { get; set; }
            [DataMember(Name = "passed")]
            public bool Passed { get; set; }
            [DataMember(Name = "payloadHashMismatchRejected")]
            public bool PayloadHashMismatchRejected { get; set; }
            [DataMember(Name = "phase")]
            public string Phase { get; set; }
            [DataMember(Name = "preprocessPng")]
            public bool PreprocessPng { get; set; }
            [DataMember(Name = "readBack")]
            public bool ReadBack { get; set; }
            [DataMember(Name = "remove")]
            public bool Remove { get; set; }
            [DataMember(Name = "removeReplay")]
            public bool RemoveReplay { get; set; }
            [DataMember(Name = "replaceJpeg")]
            public bool ReplaceJpeg { get; set; }
            [DataMember(Name = "responseLossRecovery")]
            public bool ResponseLossRecovery { get; set; }
            [DataMember(Name = "restartOutboxSurvived")]
            public bool RestartOutboxSurvived { get; set; }
            [DataMember(Name = "runHmac")]
            public string RunHmac { get; set; }
            [DataMember(Name = "runProfileIsolated")]
            public bool RunProfileIsolated { get; set; }
            [DataMember(Name = "runProfileRemoved")]
            public bool RunProfileRemoved { get; set; }
            [DataMember(Name = "saleSafe")]
            public bool SaleSafe { get; set; }
            [DataMember(Name = "schemaVersion")]
            public string SchemaVersion { get; set; }
            [DataMember(Name = "sharedSnapshotUnchanged")]
            public bool SharedSnapshotUnchanged { get; set; }
            [DataMember(Name = "signedUrlPersistenceCount")]
            public int SignedUrlPersistenceCount { get; set; }
            [DataMember(Name = "staleConflictProtected")]
            public bool StaleConflictProtected { get; set; }
            [DataMember(Name = "startedAt")]
            public string StartedAt { get; set; }
            [DataMember(Name = "storageResiduals")]
            public int StorageResiduals { get; set; }
            [DataMember(Name = "unrelatedFairness")]
            public bool UnrelatedFairness { get; set; }
            [DataMember(Name = "uploadMainThumb")]
            public bool UploadMainThumb { get; set; }
            [DataMember(Name = "versionChanged")]
            public bool VersionChanged { get; set; }
        }
    }
}
