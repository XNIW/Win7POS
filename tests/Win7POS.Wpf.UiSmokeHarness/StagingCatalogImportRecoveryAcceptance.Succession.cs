using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Import;
using Win7POS.Data.Online;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static partial class StagingCatalogImportRecoveryAcceptance
    {
        private const int SuccessionRows = 1001;

        private static IEnumerable<IdentityFact> AllOperationFacts(LiveReport report) =>
            (report.OrdinaryParts ?? Array.Empty<IdentityFact>())
                .Concat(report.RecoveryParts ?? Array.Empty<IdentityFact>())
                .Concat(report.FenceParts ?? Array.Empty<IdentityFact>())
                .Concat(report.SuccessionPredecessorParts ?? Array.Empty<IdentityFact>())
                .Concat(new[] { report.Original, report.FenceOriginal, report.SuccessionOriginal, report.SuccessionContributor }.Where(fact => fact != null));

        private static async Task CheckAcceptedContributorSuccessionAsync(SqliteConnectionFactory factory, PosAdminWebOptions options,
            Func<PosTrustedDeviceSession> fresh, OnlineSyncGeneration generation, Func<bool> authorize,
            LiveReport report, List<PosAdminWebClient.CatalogImportQaWireRecord> records, CancellationToken token)
        {
            Require(!(await new CatalogImportOutboxRepository(factory).HasUnresolvedAsync()), "u2_succession_initial_queue_not_clean");
            var prefix = report.Prefix + "S-";
            var root = await SeedUncertainLegacyAsync(factory, prefix, SuccessionRows, token, dense: false);
            report.SuccessionOriginal = Facts(factory, new[] { root }).Single();
            string capturedDocument = null;
            var captureSuccessor = false;
            // Observe the existing durable journal without altering auth or the
            // product. Normal convergence subsequently clears this preparation.
            PosTrustedDeviceSession FreshWithJournalObservation()
            {
                if (captureSuccessor)
                {
                    var saved = ScalarText(factory, "SELECT plan_document_json FROM catalog_import_prepared_plan WHERE original_id=@root", new { root });
                    if (saved != null && CatalogImportRecoveryService.Deserialize<PosCatalogImportRecoveryPlanDocument>(saved).Parts.Length == 0)
                    {
                        Require(capturedDocument == null || capturedDocument == saved, "u2_succession_prepared_membership_changed");
                        capturedDocument = saved;
                    }
                }
                return fresh();
            }
            var service = new CatalogImportRecoveryService(factory, freshSession: FreshWithJournalObservation);
            var draft = await service.PrepareAsync(root, options, fresh(), generation, token);
            Require(draft.ReceiptStatus == "not_found" && !draft.CanCommit, "u2_succession_original_not_found_required");
            draft = await service.RetireAsync(draft, options, fresh(), generation, authorize, token);
            Require(draft.ReceiptStatus == "retired" && draft.CanCommit, "u2_succession_original_retirement_missing");
            var beforeInitialCommit = Economics(factory, prefix);
            var initial = await service.CommitAsync(draft, draft.Rows, authorize, generation, token);
            Require(initial.Errors == 0 && initial.CatalogImportOutboxIds.Count == 2, "u2_succession_two_part_predecessor_missing");
            report.SuccessionPredecessorParts = Facts(factory, initial.CatalogImportOutboxIds);
            await AssertBackupAsync(initial.BackupPath, root, prefix, beforeInitialCommit);
            Require(report.SuccessionPredecessorParts[0].Items == 1000 && report.SuccessionPredecessorParts[1].Items == 1,
                "u2_succession_predecessor_row_binding_invalid");
            var repository = new CatalogImportOutboxRepository(factory);
            var predecessor = await repository.GetRemotePlanAsync(initial.CatalogImportOutboxIds[0]);
            AssertRemoteMembership(predecessor, report.SuccessionPredecessorParts);
            report.SuccessionPredecessorPlanId = predecessor.Document.PlanId;
            var economics = Economics(factory, prefix);
            await SyncOneOwnedAcceptedAsync(factory, options, fresh, generation, report, initial.CatalogImportOutboxIds[0], token);
            Require(records.Any(record => record.PlanId == predecessor.Document.PlanId && record.PartIndex == 0 &&
                record.ServerResponseObserved && record.ResponseStatus == "accepted"), "u2_succession_first_live_acceptance_missing");
            var partial = await service.GetPlanProgressAsync(root, token);
            Require(partial.TotalRows == SuccessionRows && partial.CompletedRows == 1000 && partial.TotalParts == 2 &&
                partial.CompletedParts == 1 && !Resolved(factory, root) && !PlanCompleted(factory, root) &&
                Economics(factory, prefix) == economics, "u2_succession_partial_barrier_or_economic_failure");
            var leaf = await service.PrepareLocalAsync(initial.CatalogImportOutboxIds[1], fresh(), generation, token);
            Require(leaf.RequiresPlanRetirement, "u2_succession_explicit_group_retirement_missing");
            draft = await service.RetireCommittedPlanAsync(leaf, options, fresh(), generation, authorize, token);
            Require(draft.Supersession?.IsSettled == true && draft.Supersession.LocalAlreadyApplied &&
                draft.Supersession.PartReceipts[0].Status == "accepted" && draft.Supersession.PartReceipts[1].Status == "retired" &&
                draft.Supersession.CarryCoverage.Count == 1000 && draft.Supersession.CarryCoverage.All(row => row.Kind == "accepted_plan_part" &&
                    row.ContributorPlanId == predecessor.Document.PlanId && row.ContributorPartIndex == 0) &&
                !Resolved(factory, root) && Economics(factory, prefix) == economics, "u2_succession_settlement_or_carry_invalid");
            report.SuccessionRetiredPartCanonicalHash = predecessor.Receipt.Parts.Single(part => part.Index == 1).PayloadHash;
            using (var client = new PosAdminWebClient(options))
            {
                var late = await client.CatalogImportRecoveryApplyAsync(CatalogImportRecoveryProofTransport.Authenticate(
                    new PosCatalogImportRecoveryApplyRequest { PlanId = predecessor.Document.PlanId, PartIndex = 1 }, fresh()), token);
                Require(!late.Denied && (late.Code == "identity_retired" || late.Code == "original_retired" ||
                    late.Value?.Code == "identity_retired" || late.Value?.Reason == "identity_retired"), "u2_succession_retired_child_late_fence_missing");
                report.SuccessionLateFenceCode = late.Value?.Reason ?? late.Value?.Code ?? late.Code;
            }
            Require(Economics(factory, prefix) == economics, "u2_succession_late_fence_changed_local_economics");
            report.SuccessionLateFencePassed = true;

            // The independent operation is owned by the same real device. Both
            // prices change; quantity remains 1.25, avoiding an invented stock-set
            // assumption or a second quantity delta over the frozen root intent.
            var row = Rows(prefix, SuccessionRows, dense: false).Last();
            using (var connection = factory.Open()) report.SuccessionLocalOnlyLegacyHistoryIds = connection.Query<long>(
                "SELECT id FROM product_price_history WHERE barcode=@barcode AND catalog_import_idempotency_key=@key ORDER BY id",
                new { barcode = row.Barcode, key = report.SuccessionOriginal.IdempotencyKey }).ToArray();
            Require(report.SuccessionLocalOnlyLegacyHistoryIds.Length == 2, "u2_succession_legacy_history_identity_missing");
            AssertLocalOnlyLegacyHistory(factory, report);
            row.RetailPrice = "1300"; row.PurchasePrice = "1000";
            var applier = new SupplierExcelImportApplier(factory);
            var preview = await applier.BuildPreviewAsync(new[] { row }, token);
            var entry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, report.Prefix + "independent-contributor.xlsx", "qa-live");
            Require(preview.CanApply && entry != null, "u2_succession_contributor_preview_invalid");
            var contributor = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = entry }, token);
            Require(contributor.Errors == 0 && contributor.Updated == 1 && contributor.PriceHistoryInserted == 2,
                "u2_succession_contributor_local_delta_invalid");
            report.SuccessionContributor = Facts(factory, contributor.CatalogImportOutboxIds).Single();
            var afterContributor = Economics(factory, prefix);
            report.SuccessionLocalOnlyLegacyHistoryIdsBeforeAck = ReadUnmappedLegacyHistoryIds(factory, report);
            Require(report.SuccessionLocalOnlyLegacyHistoryIdsBeforeAck.SequenceEqual(report.SuccessionLocalOnlyLegacyHistoryIds),
                "u2_succession_local_only_legacy_history_changed_before_ack");
            await SyncOneOwnedAcceptedAsync(factory, options, fresh, generation, report, report.SuccessionContributor.Id, token);
            report.SuccessionLocalOnlyLegacyHistoryIdsAfterAck = ReadUnmappedLegacyHistoryIds(factory, report);
            Require(report.SuccessionLocalOnlyLegacyHistoryIdsAfterAck.SequenceEqual(report.SuccessionLocalOnlyLegacyHistoryIdsBeforeAck),
                "u2_succession_local_only_legacy_history_changed_after_ack");
            AssertLocalOnlyLegacyHistory(factory, report);
            Require(records.Count(record => record.ClientImportId == report.SuccessionContributor.ClientImportId &&
                record.ServerResponseObserved && record.ResponseStatus == "accepted") == 1 && Economics(factory, prefix) == afterContributor,
                "u2_succession_contributor_live_ack_or_economic_once_missing");
            draft.Rows.Last().RetailPrice = "1300"; draft.Rows.Last().PurchasePrice = "1000";
            await service.SaveDraftAsync(draft, draft.Rows, token);
            draft = await service.PrepareAsync(root, options, fresh(), generation, token);
            Require(draft.Contributions.Count == 1 && draft.Contributions[0].ContributorId == report.SuccessionContributor.Id &&
                draft.Contributions[0].PayloadHash == report.SuccessionContributor.PayloadHash && draft.Contributions[0].Receipt.Status == "accepted",
                "u2_succession_independent_authoritative_contribution_missing");
            captureSuccessor = true;
            var completed = await service.CommitAsync(draft, draft.Rows, authorize, generation, token);
            Require(completed.Errors == 0 && completed.RecoveryAlreadyConverged && completed.CatalogImportOutboxIds.Count == 0 &&
                completed.PriceHistoryInserted == 0 && completed.Updated == 0 && completed.Inserted == 0 && capturedDocument != null &&
                Economics(factory, prefix) == afterContributor, "u2_succession_convergence_reapplied_economics");
            Require(System.IO.File.Exists(completed.BackupPath), "u2_succession_convergence_backup_missing");
            var beforeConvergence = new SqliteConnectionFactory(PosDbOptions.ForPath(completed.BackupPath));
            Require((await new Win7POS.Data.Repositories.DbMaintenanceRepository(beforeConvergence).ValidateAsync()).IsValid &&
                Economics(beforeConvergence, prefix) == afterContributor && !Resolved(beforeConvergence, root),
                "u2_succession_convergence_backup_not_before_application");
            report.SuccessionBackupVerified = true;
            var document = CatalogImportRecoveryService.Deserialize<PosCatalogImportRecoveryPlanDocument>(capturedDocument);
            var accepted = document.Coverage.Single(coverage => coverage.Kind == "accepted_contributor");
            var contributed = draft.Contributions[0].Request.Items.Single();
            var carriedBindings = predecessor.Document.Coverage.Where(coverage => coverage.Kind == "child" && coverage.PartIndex == 0)
                .ToDictionary(coverage => coverage.ClientItemId, coverage => coverage.ChildClientItemId, StringComparer.Ordinal);
            Require(document.Mode == "replacement" && document.VerifiedOriginalId == predecessor.Document.VerifiedOriginalId &&
                document.Parts.Length == 0 && document.Coverage.Length == SuccessionRows &&
                document.Coverage.Select(coverage => coverage.ClientItemId).Distinct().Count() == SuccessionRows &&
                document.Coverage.Count(coverage => coverage.Kind == "accepted_plan_part" && coverage.ContributorPlanId == predecessor.Document.PlanId &&
                    coverage.ContributorPartIndex == 0) == 1000 && document.Supersedes?.PlanId == predecessor.Document.PlanId &&
                document.Coverage.Where(coverage => coverage.Kind == "accepted_plan_part").All(coverage =>
                    carriedBindings.TryGetValue(coverage.ClientItemId, out var expectedChild) && coverage.ContributorClientItemId == expectedChild) &&
                document.Supersedes.RetiredChildren.Length == 1 && document.Supersedes.RetiredChildren[0].PartIndex == 1 &&
                document.Supersedes.RetiredChildren[0].CanonicalPayloadHash == report.SuccessionRetiredPartCanonicalHash &&
                accepted.ClientItemId == predecessor.Document.Coverage.Single(coverage => coverage.Kind == "child" && coverage.PartIndex == 1).ClientItemId &&
                accepted.ContributorClientItemId == contributed.ClientItemId &&
                accepted.VerifiedContributorId == CatalogImportRecoveryProofTransport.CreateUploadId(report.ShopId, report.ShopDeviceId,
                    "sha256:" + report.SuccessionContributor.PayloadHash, "original") &&
                CatalogImportOutboxPayloadBuilder.SameIntent(accepted.DesiredItem, contributed), "u2_succession_final_coverage_binding_invalid");
            report.SuccessionSuccessorPlanId = document.PlanId;
            report.SuccessionMembershipSha256 = CatalogImportOutboxPayloadBuilder.Sha256Hex(CatalogImportRecoveryService.Serialize(document.Coverage));
            report.SuccessionVerifiedContributorId = accepted.VerifiedContributorId;
            report.SuccessionContributorClientItemId = accepted.ContributorClientItemId;
            using (var client = new PosAdminWebClient(options))
            {
                // Idempotent registration uses the SAME captured journal bytes;
                // /plan takes an upload handle, not an invented plan lookup flag.
                var upload = CatalogImportRecoveryProofTransport.CreateUploadId(report.ShopId, report.ShopDeviceId,
                    "sha256:" + CatalogImportOutboxPayloadBuilder.Sha256Hex(capturedDocument), "plan");
                var registered = await CatalogImportRecoveryProofTransport.RegisterPlanAsync(client, capturedDocument, upload,
                    document.VerifiedOriginalId, fresh, token, document);
                Require(registered.Success && registered.Value?.Ok == true && registered.Value.Status == "planned" &&
                    registered.Value.ShopId == report.ShopId && registered.Value.ShopDeviceId == report.ShopDeviceId &&
                    registered.Value.PlanId == document.PlanId && registered.Value.VerifiedOriginalId == document.VerifiedOriginalId &&
                    registered.Value.PartCount == 0 && registered.Value.ItemCount == 0 && registered.Value.Parts?.Length == 0 &&
                    registered.Value.ParentStatus == "complete" && CatalogImportRecoveryProofTransport.IsHash(registered.Value.PlanCanonicalHash),
                    "u2_succession_authoritative_complete_registration_missing");
                report.SuccessionPlanCanonicalHash = registered.Value.PlanCanonicalHash;
                var replay = await client.CatalogImportRecoveryApplyAsync(CatalogImportRecoveryProofTransport.Authenticate(
                    new PosCatalogImportRecoveryApplyRequest { PlanId = predecessor.Document.PlanId, PartIndex = 0 }, fresh()), token);
                Require(replay.Success && replay.Value?.Ok == true && replay.Value.Status == "accepted" &&
                    replay.Value.PlanId == predecessor.Document.PlanId && replay.Value.PartIndex == 0 &&
                    CatalogImportRecoveryService.Serialize(replay.Value.Receipt) ==
                    CatalogImportRecoveryService.Serialize(draft.Supersession.PartReceipts[0].Receipt), "u2_succession_accepted_part_idempotent_replay_mismatch");
            }
            Require(records.Count(record => record.PlanId == predecessor.Document.PlanId && record.PartIndex == 0 &&
                record.ServerResponseObserved && record.ResponseStatus == "accepted") == 2 &&
                !records.Any(record => record.PlanId == document.PlanId && record.Route.EndsWith("/apply", StringComparison.Ordinal)) &&
                !records.Any(record => record.Route == "/api/pos/catalog/import-sync" && record.ClientImportId == report.SuccessionOriginal.ClientImportId) &&
                Resolved(factory, root) && Scalar(factory, "SELECT COUNT(*) FROM catalog_import_recovery_supersession WHERE original_id=@root AND resolved_at IS NULL", new { root }) == 0 &&
                !(await repository.HasUnresolvedAsync()) && Economics(factory, prefix) == afterContributor,
                "u2_succession_replay_or_final_barrier_invalid");
            AssertFacts(factory, AllOperationFacts(report));
            AssertSuccessionEconomics(factory, report);
            report.SuccessionAcceptedPlanPartRows = 1000; report.SuccessionAcceptedContributorRows = 1;
            report.SuccessionLegacyOriginalHttpDispatchObserved = false;
            report.AcceptedPlanPartSuccessionPassed = true; report.IndependentAcceptedContributorPassed = true;
            report.SuccessionEconomicOncePassed = true; report.SuccessionCoverageOnlyPassed = true;
        }

        private static async Task SyncOneOwnedAcceptedAsync(SqliteConnectionFactory factory, PosAdminWebOptions options,
            Func<PosTrustedDeviceSession> fresh, OnlineSyncGeneration generation, LiveReport report, long expectedId, CancellationToken token)
        {
            var facts = AllOperationFacts(report).ToArray(); AssertFacts(factory, facts);
            Require(Scalar(factory, "SELECT COUNT(*) FROM catalog_import_outbox WHERE status NOT IN ('acked','recovered') AND id NOT IN @ids",
                new { ids = facts.Select(fact => fact.Id).ToArray() }) == 0, "u2_succession_foreign_queue_detected");
            var synced = await new CatalogImportSyncService(factory).SyncPendingAsync(options, fresh(), 1, generation, null, token);
            Require(synced.Acked == 1 && synced.Attempted == 1 && synced.Blocked == 0 && !synced.AuthenticationDenied &&
                ScalarText(factory, "SELECT status FROM catalog_import_outbox WHERE id=@id", new { id = expectedId }) == "acked",
                "u2_succession_expected_owned_ack_missing");
        }

        private static long[] ReadUnmappedLegacyHistoryIds(SqliteConnectionFactory factory, LiveReport report)
        {
            using (var connection = factory.Open()) return connection.Query<long>(
                "SELECT id FROM product_price_history WHERE id IN @ids AND COALESCE(remote_price_id,'')='' ORDER BY id",
                new { ids = report.SuccessionLocalOnlyLegacyHistoryIds }).ToArray();
        }

        private static void AssertLocalOnlyLegacyHistory(SqliteConnectionFactory factory, LiveReport report)
        {
            using (var connection = factory.Open())
            {
                var unmapped = connection.Query<long>("SELECT id FROM product_price_history WHERE barcode LIKE @prefix AND COALESCE(remote_price_id,'')='' ORDER BY id",
                    new { prefix = report.Prefix + "%" }).ToArray();
                Require(unmapped.SequenceEqual(report.SuccessionLocalOnlyLegacyHistoryIds) && unmapped.Length == 2 &&
                    connection.ExecuteScalar<long>(@"SELECT COUNT(*) FROM product_price_history WHERE id IN @ids AND barcode=@barcode
AND catalog_import_idempotency_key=@key AND old_price IS NULL AND
((LOWER(type)='retail' AND new_price=1200) OR (LOWER(type)='purchase' AND new_price=900))",
                        new { ids = unmapped, barcode = report.Prefix + "S-01000", key = report.SuccessionOriginal.IdempotencyKey }) == 2,
                    "u2_succession_local_only_legacy_history_not_exact");
            }
        }

        private static void AssertSuccessionEconomics(SqliteConnectionFactory factory, LiveReport report)
        {
            var prefix = report.Prefix + "S-";
            using (var connection = factory.Open())
            {
                var products = connection.Query<ProductFact>(@"SELECT p.barcode AS Barcode,p.remote_product_id AS RemoteId,p.unitPrice AS Retail,
m.purchase_price AS Purchase,m.stock_qty AS Stock FROM products p JOIN product_meta m ON m.barcode=p.barcode WHERE p.barcode LIKE @prefix",
                    new { prefix = prefix + "%" }).ToArray();
                Require(products.Length == SuccessionRows && products.All(product => product.Stock == 1.25m && !string.IsNullOrWhiteSpace(product.RemoteId) &&
                    (product.Barcode == prefix + "01000" ? product.Retail == 1300 && product.Purchase == 1000 : product.Retail == 1200 && product.Purchase == 900)) &&
                    products.Select(product => product.RemoteId).Distinct().Count() == SuccessionRows &&
                    Scalar(factory, "SELECT COUNT(*) FROM product_price_history WHERE barcode LIKE @prefix", new { prefix = prefix + "%" }) == SuccessionRows * 2 + 2 &&
                    Scalar(factory, @"SELECT COUNT(*) FROM product_price_history WHERE barcode=@barcode AND
((LOWER(type)='retail' AND old_price=1200 AND new_price=1300) OR (LOWER(type)='purchase' AND old_price=900 AND new_price=1000))",
                        new { barcode = prefix + "01000" }) == 2, "u2_succession_price_stock_history_effects_not_once");
            }
            AssertLocalOnlyLegacyHistory(factory, report);
        }
    }
}
