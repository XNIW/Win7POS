using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.UiSmokeHarness
{
    // This entry point is only a continuation of the canonically gated article
    // runner. It reuses its actual authenticated trust and release-pack DLLs.
    // Local uncertain legacy input is explicitly seeded; every server response
    // is fetched live, including retirement, lost ACK, retry and readback.
    internal static partial class StagingCatalogImportRecoveryAcceptance
    {
        internal const string RunnerTokenVariable = "WIN7POS_U2_ACCEPTANCE_RUNNER_TOKEN";
        internal const int RestartRequiredExitCode = 75;
        private const int RowsPerWorkload = 5000;
        private const string ExpectedDirectory = @"C:\POSData\Win7POSFinalArticleSyncAcceptance";
        private const string ResultName = "catalog-import-recovery-live-result.json";

        internal static async Task<int> RunAsync(string profile, string runId, string output, string phase)
        {
            LiveReport report = null;
            var records = new List<PosAdminWebClient.CatalogImportQaWireRecord>();
            try
            {
                Require(string.Equals(Path.GetFullPath(AppPaths.DataDirectory).TrimEnd('\\'), ExpectedDirectory, StringComparison.OrdinalIgnoreCase), "u2_data_directory_invalid");
                Require(Environment.GetEnvironmentVariable(RunnerTokenVariable) == runId &&
                    System.Text.RegularExpressions.Regex.IsMatch(runId ?? "", "^ASUSART_POST_PR68_[0-9]{8}T[0-9]{9}Z_[A-F0-9]{8}$"), "u2_runner_handshake_missing");
                var article = Read<ArticleReceipt>(Path.Combine(output, "staging-acceptance-result.json"));
                Require(article.Passed && article.RunId == runId && article.Profile == profile && article.LogicalRuns == 1,
                    "u2_canonical_article_receipt_required");
                report = phase == "prepare" ? new LiveReport
                {
                    RunId = runId, Profile = profile, Prefix = "U2-" + CatalogImportOutboxPayloadBuilder.Sha256Hex(runId).Substring(0, 16) + "-",
                    StartedAtUtc = UtcStamp(), InitialProcessId = Process.GetCurrentProcess().Id,
                    Scope = "qa-articles-zero-sales", SalesAllowed = false
                } : Read<LiveReport>(Path.Combine(output, ResultName));
                Require(report.RunId == runId && report.Profile == profile && !report.Passed, "u2_phase_identity_invalid");
                Require(phase == "prepare" || phase == "resume" && report.Code == "u2_draft_restart_required" ||
                    phase == "continue" && report.Code == "u2_response_loss_restart_required", "u2_phase_transition_invalid");
                report.Phase = phase;
                var factory = new SqliteConnectionFactory(PosDbOptions.Default());
                var store = new PosTrustedDeviceStore();
                Require(store.TryRead(out var session) && PosOfflineAuthorizationLeasePolicy.ValidateOnlineReceipt(session, DateTimeOffset.UtcNow).Allowed,
                    "u2_current_online_authentication_required");
                var generation = new OnlineSyncGeneration(session.GenerationId, session.PosSessionId, session.ShopDeviceId,
                    session.ShopId, session.ShopCode, session.StaffId, session.StaffCredentialVersion);
                var generations = new OnlineSyncGenerationRepository(factory);
                Require(await generations.IsCurrentAndActiveAsync(generation), "u2_generation_inactive");
                Require(PosAdminWebOptions.TryLoad(out var options, out _) && options.BaseUri.Scheme == "https" &&
                    options.BaseUri.Host == "merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev", "u2_staging_host_invalid");
                if (phase == "prepare")
                { report.ShopId = session.ShopId; report.ShopDeviceId = session.ShopDeviceId; report.GenerationFingerprint = generation.Fingerprint; }
                Require(report.ShopId == session.ShopId && report.ShopDeviceId == session.ShopDeviceId &&
                    report.GenerationFingerprint == generation.Fingerprint, "u2_shop_or_generation_changed");
                PosTrustedDeviceSession FreshSession()
                {
                    Require(store.TryReadGeneration(generation, out var current, out _) &&
                        PosOfflineAuthorizationLeasePolicy.ValidateOnlineReceipt(current, DateTimeOffset.UtcNow).Allowed,
                        "u2_online_authentication_changed");
                    return current;
                }
                bool Authorize() => store.TryReadGeneration(generation, out var current, out _) &&
                    PosOfflineAuthorizationLeasePolicy.ValidateOnlineReceipt(current, DateTimeOffset.UtcNow).Allowed &&
                    generations.IsCurrentAndActiveAsync(generation).GetAwaiter().GetResult();
                var service = new CatalogImportRecoveryService(factory, freshSession: FreshSession);
                using (var scope = PosAdminWebClient.ObserveCatalogImportForQa(options.BaseUri, session.ShopDeviceId,
                    record => { lock (records) records.Add(record); }))
                using (var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(15)))
                {
                    var token = lifetime.Token;
                    Require(Scalar(factory, "SELECT COUNT(*) FROM sales") == 0, "u2_sales_scope_dirty");
                    if (phase == "prepare")
                    {
                        Require(!(await new CatalogImportOutboxRepository(factory).HasUnresolvedAsync()), "u2_preexisting_import_queue_not_clean");
                        Require(Scalar(factory, "SELECT COUNT(*) FROM products WHERE barcode LIKE @prefix", new { prefix = report.Prefix + "%" }) == 0,
                            "u2_dataset_identity_already_used");
                        var ordinaryRows = Rows(report.Prefix + "O-", RowsPerWorkload);
                        var applier = new SupplierExcelImportApplier(factory);
                        var preview = await applier.BuildPreviewAsync(ordinaryRows, token);
                        Require(preview.CanApply, "u2_ordinary_preview_invalid");
                        var plan = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelPlan(preview, report.Prefix + "ordinary.xlsx", "qa-live");
                        Require(plan.TotalRows == RowsPerWorkload && plan.Entries.Count > 5, "u2_dense_body_partition_not_exercised");
                        var applied = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { InsertNew = true, CatalogImportOutboxPlan = plan }, token);
                        Require(applied.Errors == 0 && applied.CatalogImportOutboxIds.Count == plan.Entries.Count, "u2_ordinary_local_apply_failed");
                        report.OrdinaryParts = Facts(factory, applied.CatalogImportOutboxIds);
                        var ordinaryEconomics = Economics(factory, report.Prefix + "O-");
                        await DrainOwnedAsync(factory, options, FreshSession, generation, service, report.OrdinaryParts[0].Id,
                            report.OrdinaryParts, null, token);
                        Require(Economics(factory, report.Prefix + "O-") == ordinaryEconomics, "u2_ordinary_ack_changed_economics");
                        AssertMappedEconomics(factory, report.Prefix + "O-", RowsPerWorkload);
                        report.OrdinaryPassed = true;
                        report.RootId = await SeedUncertainLegacyAsync(factory, report.Prefix + "R-", RowsPerWorkload, token);
                        report.Original = Facts(factory, new[] { report.RootId }).Single();
                        report.EconomicBeforeRecovery = Economics(factory, report.Prefix + "R-");
                        var draft = await service.PrepareLocalAsync(report.RootId, FreshSession(), generation, token);
                        draft.Rows[0].RetailPrice = "-1";
                        await service.SaveDraftAsync(draft, draft.Rows, token);
                        Require(Economics(factory, report.Prefix + "R-") == report.EconomicBeforeRecovery, "u2_draft_applied_automatically");
                        report.Code = "u2_draft_restart_required";
                        report.SeededLegacyUncertainty = true;
                        report.LegacyOriginalHttpDispatchObserved = false;
                        return RestartRequiredExitCode;
                    }
                    Require(Process.GetCurrentProcess().Id != report.InitialProcessId, "u2_draft_process_restart_missing");
                    AssertFacts(factory, report.OrdinaryParts.Concat(new[] { report.Original }));
                    Require(Economics(factory, report.Prefix + "R-") == report.EconomicBeforeRecovery, "u2_restart_changed_economics");
                    if (phase == "resume")
                    {
                        var draft = await service.PrepareLocalAsync(report.RootId, FreshSession(), generation, token);
                        Require(draft.HasSavedDraft && draft.Rows.Count == RowsPerWorkload && draft.Rows[0].RetailPrice == "-1",
                            "u2_saved_draft_not_restored_offline");
                        var requestsBeforeLocalDraft = records.Count;
                        await service.DiscardDraftAsync(report.RootId, token);
                        draft = await service.PrepareLocalAsync(report.RootId, FreshSession(), generation, token);
                        Require(!draft.HasSavedDraft && draft.Rows[0].RetailPrice == "1200" && records.Count == requestsBeforeLocalDraft,
                            "u2_explicit_discard_or_offline_local_prepare_failed");
                        report.DraftPersistRestartDiscardPassed = true;
                        draft = await service.PrepareAsync(report.RootId, options, FreshSession(), generation, token);
                        Require(draft.ReceiptStatus == "not_found" && !draft.CanCommit, "u2_unknown_original_not_authoritatively_checked");
                        draft = await service.RetireAsync(draft, options, FreshSession(), generation, Authorize, token);
                        Require(draft.ReceiptStatus == "retired" && draft.CanCommit, "u2_original_retirement_failed");
                        report.RetirementPassed = true;
                        draft.Rows[0].RetailPrice = "-1";
                        var invalid = await service.BuildPreviewAsync(draft, draft.Rows, token);
                        Require(!invalid.CanApply, "u2_invalid_draft_preview_accepted");
                        var rejected = await service.CommitAsync(draft, draft.Rows, Authorize, generation, token);
                        Require(rejected.Errors > 0 && Economics(factory, report.Prefix + "R-") == report.EconomicBeforeRecovery,
                            "u2_commit_failed_to_revalidate");
                        draft.Rows[0].RetailPrice = "1200";
                        await service.SaveDraftAsync(draft, draft.Rows, token);
                        Require((await service.BuildPreviewAsync(draft, draft.Rows, token)).CanApply, "u2_valid_draft_preview_rejected");
                        var committed = await service.CommitAsync(draft, draft.Rows, Authorize, generation, token);
                        Require(committed.Errors == 0 && committed.CatalogImportOutboxIds.Count > 5, "u2_recovery_local_commit_failed");
                        await AssertBackupAsync(committed.BackupPath, report.RootId, report.Prefix + "R-", report.EconomicBeforeRecovery);
                        report.BackupVerified = true; report.BackupSha256 = FileHash(committed.BackupPath);
                        report.RecoveryParts = Facts(factory, committed.CatalogImportOutboxIds);
                        Require(report.RecoveryParts.Sum(part => part.Items) == RowsPerWorkload &&
                            report.RecoveryParts.All(part => part.Items >= 1 && part.Items <= CatalogImportPlanBuilder.MaximumRowsPerRequest),
                            "u2_recovery_child_rows_not_bounded");
                        report.LocalCommitRevalidationPassed = true;
                        var remotePlan = await new CatalogImportOutboxRepository(factory).GetRemotePlanAsync(report.RecoveryParts[1].Id);
                        AssertRemoteMembership(remotePlan, report.RecoveryParts);
                        report.RemotePlanId = remotePlan.Document.PlanId;
                        scope.DropOneAcceptedPartResponse(report.RemotePlanId, remotePlan.PartIndex);
                        await DrainOwnedAsync(factory, options, FreshSession, generation, service, report.RootId,
                            report.RecoveryParts, scope, token);
                        Require(scope.AcceptedResponseDropped, "u2_accepted_response_loss_not_observed");
                        var progress = await service.GetPlanProgressAsync(report.RootId, token);
                        Require(progress.CompletedParts == 1 && progress.CompletedRows < RowsPerWorkload && !Resolved(factory, report.RootId),
                            "u2_parent_closed_after_partial_or_lost_ack");
                        report.PersistedCompletedPartsBeforeRestart = progress.CompletedParts;
                        report.ResponseLossProcessId = Process.GetCurrentProcess().Id;
                        report.Code = "u2_response_loss_restart_required";
                        return RestartRequiredExitCode;
                    }
                    Require(Process.GetCurrentProcess().Id != report.ResponseLossProcessId, "u2_lost_response_process_restart_missing");
                    AssertFacts(factory, report.RecoveryParts);
                    AssertRemoteMembership(await new CatalogImportOutboxRepository(factory).GetRemotePlanAsync(report.RecoveryParts[0].Id), report.RecoveryParts);
                    var before = await service.GetPlanProgressAsync(report.RootId, token);
                    Require(before.CompletedParts == report.PersistedCompletedPartsBeforeRestart && !Resolved(factory, report.RootId),
                        "u2_persisted_progress_not_resumed");
                    await DrainOwnedAsync(factory, options, FreshSession, generation, service, report.RootId, report.RecoveryParts, null, token);
                    var lost = report.Wire.Single(item => item.AcceptedResponseDropped);
                    var replay = records.Where(item => item.Route == lost.Route && item.PlanId == lost.PlanId && item.PartIndex == lost.PartIndex &&
                        item.ServerResponseObserved && !item.AcceptedResponseDropped && item.ResponseStatus == "accepted").ToArray();
                    Require(replay.Length == 1 && !string.IsNullOrWhiteSpace(lost.ApplyIdentitySha256) &&
                        replay[0].ApplyIdentitySha256 == lost.ApplyIdentitySha256,
                        "u2_lost_ack_exact_part_replay_not_observed");
                    report.LostReplayCredentialBindingChanged = replay[0].CredentialBindingSha256 != lost.CredentialBindingSha256;
                    Require(report.LostReplayCredentialBindingChanged ||
                        replay[0].BodySha256 == lost.BodySha256 && replay[0].BodyUtf8Bytes == lost.BodyUtf8Bytes,
                        "u2_lost_ack_replay_body_changed_without_credential_rotation");
                    AssertMappedEconomics(factory, report.Prefix + "R-", RowsPerWorkload);
                    Require(Economics(factory, report.Prefix + "R-") == report.EconomicBeforeRecovery && Resolved(factory, report.RootId),
                        "u2_recovery_duplicate_economics_or_unresolved_parent");
                    report.Recovery5000Passed = true;
                    report.LostResponseRetryRestartPassed = true;
                    await CheckSmallLateFenceAsync(factory, options, FreshSession, generation, Authorize, service, report, token);
                    await CheckAcceptedContributorSuccessionAsync(factory, options, FreshSession, generation, Authorize, report, records, token);
                    await CheckCloudReadbackAsync(factory, options, FreshSession, report, token);
                    Require(Scalar(factory, "SELECT COUNT(*) FROM sales") == 0 && !(await new CatalogImportOutboxRepository(factory).HasUnresolvedAsync()),
                        "u2_final_scope_or_queue_not_clean");
                    AssertFacts(factory, AllOperationFacts(report));
                    var dispatchedFacts = report.OrdinaryParts.Concat(report.RecoveryParts).Concat(report.FenceParts)
                        .Concat(report.SuccessionPredecessorParts).Concat(new[] { report.FenceOriginal, report.SuccessionContributor }).ToArray();
                    Require(dispatchedFacts.All(fact => fact.Items >= 1 && fact.Items <= 1000 && fact.SqlNormalizedItemsUpperBoundBytes <= 524288),
                        "u2_dispatched_sql_item_budget_invalid");
                    report.SqlNormalizedItemBudgetsPassed = true;
                    report.LocalSqliteIntegrityPassed = ScalarText(factory, "PRAGMA integrity_check") == "ok";
                    Require(report.LocalSqliteIntegrityPassed, "u2_final_sqlite_integrity_failed");
                    WriteCleanupManifest(factory, output, report);
                    report.Passed = true; report.Code = "success";
                    return 0;
                }
            }
            catch (Exception error)
            {
                if (report == null) report = new LiveReport { RunId = runId, Profile = profile, Phase = phase };
                report.Passed = false;
                report.FailureType = error.GetType().Name;
                report.Code = error is CatalogImportRecoveryException recovery ? recovery.Code :
                    error is QaFailure failure ? failure.Code : error is OperationCanceledException ? "u2_cancelled" : "u2_unexpected_failure";
                return 1;
            }
            finally
            {
                if (report != null)
                {
                    report.CompletedAtUtc = UtcStamp();
                    lock (records) report.Wire.AddRange(records);
                    Write(Path.Combine(output, ResultName), report);
                    Write(Path.Combine(output, "catalog-import-recovery-live-" + phase + "-result.json"), report);
                }
            }
        }

        private static SupplierImportEditableRow[] Rows(string prefix, int count, bool dense = true) => Enumerable.Range(0, count).Select(index =>
            new SupplierImportEditableRow { RowNumber = index + 2, Barcode = prefix + index.ToString("D5", CultureInfo.InvariantCulture),
                ProductName = dense ? "U2界😀" + new string('漢', 232) : "U2 successor " + index,
                SecondProductName = dense ? "U2é" + new string('界', 230) : "U2 succession " + index,
                ItemNumber = prefix + index, PurchasePrice = "900", RetailPrice = "1200", Quantity = "1.25" }).ToArray();

        private static async Task<long> SeedUncertainLegacyAsync(SqliteConnectionFactory factory, string prefix, int count, CancellationToken token, bool dense = true)
        {
            var applier = new SupplierExcelImportApplier(factory);
            var preview = await applier.BuildPreviewAsync(Rows(prefix, count, dense), token);
            Require(preview.CanApply, "u2_legacy_input_invalid");
            var entry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, prefix + "legacy.xlsx", "qa-live");
            var applied = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { InsertNew = true, CatalogImportOutboxEntry = entry }, token);
            Require(applied.Errors == 0, "u2_legacy_input_local_apply_failed");
            // Owned QA seed models an old immutable, uncertain operation. This is
            // not evidence of a live dispatch or a historical server acceptance.
            using (var connection = factory.Open()) using (var tx = connection.BeginTransaction())
            {
                Require(connection.Execute(@"UPDATE catalog_import_outbox SET status='failed_blocked',attempt_count=1,last_error_code='qa_seeded_legacy_uncertainty'
WHERE id=@id AND client_import_id=@ClientImportId AND payload_hash=@PayloadHash AND status='pending';",
                    new { id = applied.CatalogImportOutboxId, entry.ClientImportId, entry.PayloadHash }, tx) == 1, "u2_legacy_seed_identity_mismatch");
                connection.Execute("UPDATE catalog_import_recovery SET delivery_known=0,dispatch_count=1 WHERE original_id=@id", new { id = applied.CatalogImportOutboxId }, tx);
                tx.Commit();
            }
            return applied.CatalogImportOutboxId;
        }

        private static async Task DrainOwnedAsync(SqliteConnectionFactory factory, PosAdminWebOptions options, Func<PosTrustedDeviceSession> fresh,
            OnlineSyncGeneration generation, CatalogImportRecoveryService service, long parent, IdentityFact[] facts,
            PosAdminWebClient.CatalogImportQaObservationScope stopAfterDrop, CancellationToken token)
        {
            var repository = new CatalogImportOutboxRepository(factory);
            while (facts.Any(fact => ScalarText(factory, "SELECT status FROM catalog_import_outbox WHERE id=@id", new { id = fact.Id }) != "acked"))
            {
                token.ThrowIfCancellationRequested(); AssertFacts(factory, facts);
                Require(Scalar(factory, "SELECT COUNT(*) FROM catalog_import_outbox WHERE status NOT IN ('acked','recovered') AND id<>@parent AND id NOT IN @ids",
                    new { parent, ids = facts.Select(fact => fact.Id).ToArray() }) == 0, "u2_foreign_import_queue_detected");
                var state = await repository.GetDrainStateAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (state.RemainingDue == 0 && state.NextRetryAt.HasValue)
                {
                    var wait = state.NextRetryAt.Value - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (wait > 0) await Task.Delay((int)Math.Min(wait, 1000L), token); // Real clock/backoff; no queue or lease reset.
                    continue;
                }
                var result = await new CatalogImportSyncService(factory).SyncPendingAsync(options, fresh(), 1, generation, null, token);
                Require(!result.AuthenticationDenied && result.Blocked == 0, "u2_sync_authentication_or_part_failure");
                var progress = await service.GetPlanProgressAsync(parent, token);
                if (progress.CompletedParts < progress.TotalParts) Require(!PlanCompleted(factory, parent), "u2_parent_closed_before_all_acks");
                if (stopAfterDrop?.AcceptedResponseDropped == true) return;
                if (result.Attempted == 0) await Task.Delay(250, token);
            }
            var final = await service.GetPlanProgressAsync(parent, token);
            Require(final.TotalRows == facts.Sum(fact => fact.Items) && final.CompletedRows == final.TotalRows &&
                final.CompletedParts == final.TotalParts, "u2_plan_membership_incomplete");
        }

        private static async Task CheckSmallLateFenceAsync(SqliteConnectionFactory factory, PosAdminWebOptions options,
            Func<PosTrustedDeviceSession> fresh, OnlineSyncGeneration generation, Func<bool> authorize,
            CatalogImportRecoveryService service, LiveReport report, CancellationToken token)
        {
            var id = await SeedUncertainLegacyAsync(factory, report.Prefix + "F-", 1, token);
            var fact = Facts(factory, new[] { id }).Single();
            report.FenceOriginal = fact;
            var draft = await service.PrepareAsync(id, options, fresh(), generation, token);
            Require(draft.ReceiptStatus == "not_found" && !draft.CanCommit, "u2_fence_original_not_found_required");
            draft = await service.RetireAsync(draft, options, fresh(), generation, authorize, token);
            Require(draft.CanCommit && draft.ReceiptStatus == "retired", "u2_fence_retirement_missing");
            PosCatalogImportRequest original;
            using (var connection = factory.Open()) original = CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(
                connection.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id", new { id }));
            var session = fresh(); original.DeviceToken = session.DeviceToken; original.SessionToken = session.SessionToken;
            original.PosSessionId = session.PosSessionId; original.ShopDeviceId = session.ShopDeviceId; original.ShopCode = session.ShopCode;
            original.PayloadHash = fact.PayloadHash; original.Batch.AttemptCount = 1;
            using (var client = new PosAdminWebClient(options))
            {
                var late = await client.CatalogImportAsync(original, token);
                Require(!late.Denied && (late.Code == "identity_retired" || late.Code == "original_retired" ||
                    late.Value?.Code == "identity_retired" || late.Value?.Items?.Any(item => item.Code == "identity_retired") == true),
                    "u2_late_original_not_rejected_by_retirement_fence");
                report.LateOriginalFenceCode = late.Code;
            }
            var applied = await service.CommitAsync(draft, draft.Rows, authorize, generation, token);
            Require(applied.Errors == 0, "u2_fence_replacement_failed");
            report.FenceParts = Facts(factory, applied.CatalogImportOutboxIds);
            await DrainOwnedAsync(factory, options, fresh, generation, service, id, report.FenceParts, null, token);
            AssertMappedEconomics(factory, report.Prefix + "F-", 1);
            Require(Resolved(factory, id), "u2_fence_original_unresolved");
            report.LateOriginalFencePassed = true;
        }

        private static async Task CheckCloudReadbackAsync(SqliteConnectionFactory factory, PosAdminWebOptions options,
            Func<PosTrustedDeviceSession> fresh, LiveReport report, CancellationToken token)
        {
            Dictionary<string, ProductFact> expected;
            using (var connection = factory.Open()) expected = connection.Query<ProductFact>(@"SELECT p.barcode AS Barcode,p.remote_product_id AS RemoteId,
p.name AS Name,m.name2 AS Name2,p.unitPrice AS Retail,m.purchase_price AS Purchase,m.stock_qty AS Stock
FROM products p JOIN product_meta m ON m.barcode=p.barcode WHERE p.barcode LIKE @prefix", new { prefix = report.Prefix + "%" }).ToDictionary(item => item.RemoteId, StringComparer.Ordinal);
            var products = new Dictionary<string, PosCatalogProductResponse>();
            var prices = new Dictionary<string, PosCatalogPriceResponse>();
            var cursors = new HashSet<string>(StringComparer.Ordinal); string cursor = null;
            using (var client = new PosAdminWebClient(options)) do
            {
                var session = fresh();
                var reply = await client.CatalogPullAsync(new PosCatalogPullRequest { DeviceToken = session.DeviceToken,
                    SessionToken = session.SessionToken, PosSessionId = session.PosSessionId, ShopDeviceId = session.ShopDeviceId,
                    Limit = 1000, SyncCursor = cursor }, token);
                Require(reply.Success && reply.Value?.Catalog != null && reply.Value.Shop?.ShopId == report.ShopId, "u2_cloud_readback_failed");
                report.CloudReadbackPages++;
                foreach (var item in reply.Value.Catalog.Products ?? Array.Empty<PosCatalogProductResponse>())
                    if (expected.ContainsKey(item.ProductId)) { Require(!products.ContainsKey(item.ProductId), "u2_cloud_duplicate_product"); products.Add(item.ProductId, item); }
                foreach (var item in reply.Value.Catalog.Prices ?? Array.Empty<PosCatalogPriceResponse>())
                    if (expected.ContainsKey(item.ProductId)) { Require(!prices.ContainsKey(item.PriceId), "u2_cloud_duplicate_price"); prices.Add(item.PriceId, item); }
                if (!reply.Value.HasMore) break;
                Require(!string.IsNullOrWhiteSpace(reply.Value.SyncCursor) && cursors.Add(reply.Value.SyncCursor), "u2_cloud_cursor_not_advancing");
                cursor = reply.Value.SyncCursor;
            } while (true);
            Require(expected.Count == RowsPerWorkload * 2 + 1 + SuccessionRows && products.Count == expected.Count && prices.Count == expected.Count * 2,
                "u2_cloud_exact_identity_or_history_count_mismatch");
            foreach (var item in expected.Values)
            {
                var cloud = products[item.RemoteId];
                Require(cloud.Barcode == item.Barcode && cloud.ProductName == item.Name && cloud.SecondProductName == item.Name2 &&
                    cloud.RetailPrice == (double)item.Retail && cloud.PurchasePrice == (double)item.Purchase && cloud.StockQuantity == (double)item.Stock,
                    "u2_cloud_product_price_stock_value_mismatch");
            }
            using (var connection = factory.Open())
            {
                AssertLocalOnlyLegacyHistory(factory, report);
                foreach (var price in connection.Query<PriceFact>(@"SELECT h.remote_price_id AS RemoteId,h.type AS Kind,h.new_price AS Amount,p.remote_product_id AS ProductId
FROM product_price_history h JOIN products p ON p.barcode=h.barcode WHERE p.barcode LIKE @prefix AND COALESCE(h.remote_price_id,'')<>''", new { prefix = report.Prefix + "%" }))
                {
                    Require(prices.TryGetValue(price.RemoteId, out var cloud) && cloud.ProductId == price.ProductId &&
                        string.Equals(cloud.Type, price.Kind, StringComparison.OrdinalIgnoreCase) && cloud.Price == (double)price.Amount,
                        "u2_cloud_price_identity_kind_or_value_mismatch");
                }
                Require(Scalar(factory, "SELECT COUNT(*) FROM product_price_history WHERE barcode LIKE @prefix AND COALESCE(remote_price_id,'')<>''",
                    new { prefix = report.Prefix + "%" }) == prices.Count, "u2_cloud_history_membership_not_exact");
            }
            report.CloudCanonicalReadbackPassed = true;
        }

        private static async Task AssertBackupAsync(string path, long root, string prefix, string economics)
        {
            Require(File.Exists(path), "u2_backup_missing");
            var backup = new SqliteConnectionFactory(PosDbOptions.ForPath(path));
            Require((await new DbMaintenanceRepository(backup).ValidateAsync()).IsValid && Economics(backup, prefix) == economics &&
                Scalar(backup, "SELECT COUNT(*) FROM catalog_import_plan WHERE original_id=@root", new { root }) == 0,
                "u2_backup_not_verified_before_application");
        }

        private static IdentityFact[] Facts(SqliteConnectionFactory factory, IEnumerable<long> ids)
        {
            using (var connection = factory.Open()) return ids.Select(id =>
            {
                var fact = connection.QuerySingle<IdentityFact>("SELECT id AS Id,client_import_id AS ClientImportId,idempotency_key AS IdempotencyKey,payload_hash AS PayloadHash FROM catalog_import_outbox WHERE id=@id", new { id });
                var raw = connection.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id", new { id });
                Require(CatalogImportOutboxPayloadBuilder.Sha256Hex(raw) == fact.PayloadHash, "u2_persisted_payload_hash_mismatch");
                var request = CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(raw);
                fact.Items = request.Items.Length;
                fact.SqlNormalizedItemsUpperBoundBytes = CatalogImportSqlItemBudget.MeasureOrdinary(request.Items);
                return fact;
            }).ToArray();
        }
        private static void AssertFacts(SqliteConnectionFactory factory, IEnumerable<IdentityFact> facts)
        {
            var wanted = facts.ToArray(); var actual = Facts(factory, wanted.Select(fact => fact.Id));
            Require(CatalogImportRecoveryService.Serialize(actual) == CatalogImportRecoveryService.Serialize(wanted), "u2_identity_or_persisted_intent_changed");
        }
        private static bool Resolved(SqliteConnectionFactory factory, long root) => Scalar(factory,
            "SELECT COUNT(*) FROM catalog_import_recovery WHERE original_id=@root AND resolved_at IS NOT NULL", new { root }) == 1;
        private static bool PlanCompleted(SqliteConnectionFactory factory, long root) => Scalar(factory,
            "SELECT COUNT(*) FROM catalog_import_plan WHERE (original_id=@root OR plan_id=(SELECT plan_id FROM catalog_import_plan_part WHERE outbox_id=@root)) AND completed_at IS NOT NULL", new { root }) > 0;
        private static void AssertRemoteMembership(CatalogImportSavedRemotePlan remote, IdentityFact[] parts)
        {
            Require(remote != null && remote.Document.Parts.Length == parts.Length, "u2_persisted_remote_membership_missing");
            for (var index = 0; index < parts.Length; index++)
            {
                var planned = remote.Document.Parts[index]; var local = parts[index];
                Require(planned.Index == index && planned.Request.Items.Length == local.Items &&
                    local.Items >= 1 && local.Items <= CatalogImportPlanBuilder.MaximumRowsPerRequest &&
                    planned.Request.Batch.ClientImportId == local.ClientImportId && planned.Request.Batch.IdempotencyKey == local.IdempotencyKey &&
                    planned.Request.PayloadHash == local.PayloadHash, "u2_remote_child_membership_binding_mismatch");
            }
        }
        private static void AssertMappedEconomics(SqliteConnectionFactory factory, string prefix, int rows)
        {
            using (var connection = factory.Open())
            {
                var data = connection.Query<ProductFact>(@"SELECT p.barcode AS Barcode,p.remote_product_id AS RemoteId,p.unitPrice AS Retail,m.purchase_price AS Purchase,m.stock_qty AS Stock
FROM products p JOIN product_meta m ON m.barcode=p.barcode WHERE p.barcode LIKE @prefix ORDER BY p.barcode", new { prefix = prefix + "%" }).ToArray();
                Require(data.Length == rows && data.All(item => !string.IsNullOrWhiteSpace(item.RemoteId) && item.Retail == 1200 && item.Purchase == 900 && item.Stock == 1.25m) &&
                    data.Select(item => item.RemoteId).Distinct().Count() == rows, "u2_local_mapped_economic_values_mismatch");
                var history = connection.Query<PriceFact>("SELECT remote_price_id AS RemoteId,type AS Kind,new_price AS Amount FROM product_price_history WHERE barcode LIKE @prefix", new { prefix = prefix + "%" }).ToArray();
                Require(history.Length == rows * 2 && history.All(item => !string.IsNullOrWhiteSpace(item.RemoteId) &&
                    (item.Kind.Equals("retail", StringComparison.OrdinalIgnoreCase) && item.Amount == 1200 ||
                     item.Kind.Equals("purchase", StringComparison.OrdinalIgnoreCase) && item.Amount == 900)) &&
                    history.Select(item => item.RemoteId).Distinct().Count() == rows * 2, "u2_local_history_identity_kind_or_value_mismatch");
            }
        }
        private static string Economics(SqliteConnectionFactory factory, string prefix)
        {
            using (var connection = factory.Open())
            {
                var products = connection.Query<string>(@"SELECT p.barcode||'|'||p.unitPrice||'|'||m.purchase_price||'|'||m.stock_qty FROM products p
JOIN product_meta m ON m.barcode=p.barcode WHERE p.barcode LIKE @prefix ORDER BY p.barcode", new { prefix = prefix + "%" });
                var history = connection.Query<string>("SELECT barcode||'|'||LOWER(type)||'|'||COALESCE(old_price,'')||'|'||new_price||'|'||id FROM product_price_history WHERE barcode LIKE @prefix ORDER BY id", new { prefix = prefix + "%" });
                return CatalogImportOutboxPayloadBuilder.Sha256Hex(string.Join("\n", products.Concat(history)));
            }
        }
        private static void WriteCleanupManifest(SqliteConnectionFactory factory, string output, LiveReport report)
        {
            using (var connection = factory.Open())
                Write(Path.Combine(output, "U2-CLEANUP-MANIFEST.json"), new CleanupManifest { RunId = report.RunId, ShopId = report.ShopId,
                    ShopDeviceId = report.ShopDeviceId, Prefix = report.Prefix,
                    ProductIds = connection.Query<string>("SELECT remote_product_id FROM products WHERE barcode LIKE @prefix ORDER BY barcode", new { prefix = report.Prefix + "%" }).ToArray(),
                    PriceIds = connection.Query<string>("SELECT remote_price_id FROM product_price_history WHERE barcode LIKE @prefix AND COALESCE(remote_price_id,'')<>'' ORDER BY id", new { prefix = report.Prefix + "%" }).ToArray(),
                    LocalOnlyUnacceptedLegacyHistoryIds = report.SuccessionLocalOnlyLegacyHistoryIds,
                    RecoveryPlanIds = new[] { report.RemotePlanId, report.SuccessionPredecessorPlanId, report.SuccessionSuccessorPlanId },
                    Operations = AllOperationFacts(report).ToArray() });
        }
        private static long Scalar(SqliteConnectionFactory factory, string sql, object args = null) { using (var connection = factory.Open()) return connection.ExecuteScalar<long>(sql, args); }
        private static string ScalarText(SqliteConnectionFactory factory, string sql, object args = null) { using (var connection = factory.Open()) return connection.ExecuteScalar<string>(sql, args); }
        private static T Read<T>(string path) => CatalogImportRecoveryService.Deserialize<T>(File.ReadAllText(path));
        private static void Write<T>(string path, T value) => File.WriteAllText(path, CatalogImportRecoveryService.Serialize(value), new System.Text.UTF8Encoding(false));
        private static string FileHash(string path) { using (var stream = File.OpenRead(path)) using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        private static string UtcStamp() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        private static void Require(bool value, string code) { if (!value) throw new QaFailure(code); }
        private sealed class QaFailure : Exception { internal QaFailure(string code) { Code = code; } internal string Code { get; } }
        private sealed class ProductFact { public string Barcode { get; set; } public string RemoteId { get; set; } public string Name { get; set; } public string Name2 { get; set; } public long Retail { get; set; } public long Purchase { get; set; } public decimal Stock { get; set; } }
        private sealed class PriceFact { public string RemoteId { get; set; } public string ProductId { get; set; } public string Kind { get; set; } public long Amount { get; set; } }
        [DataContract] private sealed class ArticleReceipt { [DataMember(Name = "passed")] public bool Passed { get; set; } [DataMember(Name = "runId")] public string RunId { get; set; } [DataMember(Name = "profile")] public string Profile { get; set; } [DataMember(Name = "logicalRuns")] public int LogicalRuns { get; set; } }
        [DataContract] internal sealed class IdentityFact { [DataMember] public long Id { get; set; } [DataMember] public string ClientImportId { get; set; } [DataMember] public string IdempotencyKey { get; set; } [DataMember] public string PayloadHash { get; set; } [DataMember] public int Items { get; set; } [DataMember] public long SqlNormalizedItemsUpperBoundBytes { get; set; } }
        [DataContract] private sealed class CleanupManifest { [DataMember] public string RunId { get; set; } [DataMember] public string ShopId { get; set; } [DataMember] public string ShopDeviceId { get; set; } [DataMember] public string Prefix { get; set; } [DataMember] public string[] ProductIds { get; set; } [DataMember] public string[] PriceIds { get; set; } [DataMember] public long[] LocalOnlyUnacceptedLegacyHistoryIds { get; set; } [DataMember] public string[] RecoveryPlanIds { get; set; } [DataMember] public IdentityFact[] Operations { get; set; } [DataMember] public bool CleanupExecuted { get; set; } }
        [DataContract] private sealed class LiveReport
        {
            [DataMember(Name = "passed")] public bool Passed { get; set; }
            [DataMember(Name = "code")] public string Code { get; set; }
            [DataMember(Name = "runId")] public string RunId { get; set; }
            [DataMember] public string Profile { get; set; } [DataMember] public string Phase { get; set; }
            [DataMember] public string FailureType { get; set; }
            [DataMember] public string Scope { get; set; } [DataMember] public bool SalesAllowed { get; set; }
            [DataMember] public string Prefix { get; set; } [DataMember] public string ShopId { get; set; } [DataMember] public string ShopDeviceId { get; set; }
            [DataMember] public string GenerationFingerprint { get; set; } [DataMember] public string StartedAtUtc { get; set; } [DataMember] public string CompletedAtUtc { get; set; }
            [DataMember] public int InitialProcessId { get; set; } [DataMember] public int ResponseLossProcessId { get; set; }
            [DataMember] public long RootId { get; set; } [DataMember] public IdentityFact Original { get; set; } [DataMember] public IdentityFact FenceOriginal { get; set; }
            [DataMember] public IdentityFact[] OrdinaryParts { get; set; } [DataMember] public IdentityFact[] RecoveryParts { get; set; } [DataMember] public IdentityFact[] FenceParts { get; set; }
            [DataMember] public string EconomicBeforeRecovery { get; set; } [DataMember] public string RemotePlanId { get; set; }
            [DataMember] public int PersistedCompletedPartsBeforeRestart { get; set; } [DataMember] public bool OrdinaryPassed { get; set; }
            [DataMember] public bool SeededLegacyUncertainty { get; set; } [DataMember] public bool LegacyOriginalHttpDispatchObserved { get; set; }
            [DataMember] public bool DraftPersistRestartDiscardPassed { get; set; } [DataMember] public bool LocalCommitRevalidationPassed { get; set; }
            [DataMember] public bool RetirementPassed { get; set; } [DataMember] public bool BackupVerified { get; set; } [DataMember] public string BackupSha256 { get; set; }
            [DataMember] public bool Recovery5000Passed { get; set; } [DataMember] public bool LostResponseRetryRestartPassed { get; set; }
            [DataMember] public bool LostReplayCredentialBindingChanged { get; set; }
            [DataMember] public bool LateOriginalFencePassed { get; set; } [DataMember] public string LateOriginalFenceCode { get; set; }
            [DataMember] public bool CloudCanonicalReadbackPassed { get; set; } [DataMember] public int CloudReadbackPages { get; set; }
            [DataMember] public bool LocalSqliteIntegrityPassed { get; set; }
            [DataMember] public IdentityFact SuccessionOriginal { get; set; } [DataMember] public IdentityFact[] SuccessionPredecessorParts { get; set; }
            [DataMember] public IdentityFact SuccessionContributor { get; set; }
            [DataMember] public string SuccessionPredecessorPlanId { get; set; } [DataMember] public string SuccessionSuccessorPlanId { get; set; }
            [DataMember] public string SuccessionMembershipSha256 { get; set; } [DataMember] public string SuccessionPlanCanonicalHash { get; set; }
            [DataMember] public string SuccessionVerifiedContributorId { get; set; } [DataMember] public string SuccessionContributorClientItemId { get; set; }
            [DataMember] public string SuccessionRetiredPartCanonicalHash { get; set; } [DataMember] public string SuccessionLateFenceCode { get; set; }
            [DataMember] public long[] SuccessionLocalOnlyLegacyHistoryIds { get; set; }
            [DataMember] public long[] SuccessionLocalOnlyLegacyHistoryIdsBeforeAck { get; set; }
            [DataMember] public long[] SuccessionLocalOnlyLegacyHistoryIdsAfterAck { get; set; }
            [DataMember] public int SuccessionAcceptedPlanPartRows { get; set; } [DataMember] public int SuccessionAcceptedContributorRows { get; set; }
            [DataMember] public bool AcceptedPlanPartSuccessionPassed { get; set; } [DataMember] public bool IndependentAcceptedContributorPassed { get; set; }
            [DataMember] public bool SuccessionEconomicOncePassed { get; set; } [DataMember] public bool SuccessionLateFencePassed { get; set; }
            [DataMember] public bool SuccessionCoverageOnlyPassed { get; set; }
            [DataMember] public bool SuccessionBackupVerified { get; set; } [DataMember] public bool SuccessionLegacyOriginalHttpDispatchObserved { get; set; }
            [DataMember] public bool SqlNormalizedItemBudgetsPassed { get; set; }
            [DataMember] public List<PosAdminWebClient.CatalogImportQaWireRecord> Wire { get; set; } = new List<PosAdminWebClient.CatalogImportQaWireRecord>();
            [DataMember] public string[] NotCovered { get; set; } = new[] { "linked_correction_of_deferred_accepted_row_requires_separate_live_case", "concurrent_independent_devices_and_mobile_not_covered", "operator_dialog_keyboard_focus_languages_use_existing_WPF_U2_suite", "sqlite_process_crash_uses_separate_owned_isolated_regression", "full_59999_rows_and_resource_performance_requires_separate_qualification", "physical_Win7_mobile_and_hardware_not_covered", "QA_cleanup_requires_authorized_Admin_procedure" };
        }
    }
}
